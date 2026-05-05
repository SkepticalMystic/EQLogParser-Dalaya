using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace EQLogParser
{
  internal class FightSource
  {
    public string SourcePlayer { get; init; }
    public List<Fight> Fights { get; init; } = [];

    // Subtracted from this source's timestamps to align with the merged frame. Set by
    // FightOffsetDetector or manually by the user. Sources with offset 0 anchor the merged
    // frame; non-zero values shift this source's fights into that frame.
    public double TimeOffsetSeconds { get; init; }
  }

  internal static class FightMerger
  {
    private static readonly Regex EqLogFileRegex =
      new(@"^eqlog_([^_]+)_[^.]+\.txt$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static List<Fight> MergeFromSources(IEnumerable<FightSource> sources)
    {
      var merged = new List<Fight>();
      if (sources == null)
      {
        return merged;
      }

      var tagged = sources
        .Where(s => s != null && !string.IsNullOrEmpty(s.SourcePlayer) && s.Fights != null)
        .SelectMany(s => s.Fights.Where(f => f != null && !f.IsInactivity)
          .Select(f => (s.SourcePlayer, Offset: s.TimeOffsetSeconds, Fight: f)))
        .ToList();

      if (tagged.Count == 0)
      {
        return merged;
      }

      foreach (var cluster in ClusterByNameAndOverlap(tagged))
      {
        // Per-cluster drift correction: fits a linear function for each non-anchor source
        // from paired DamageRecord observations, applied on top of the constant offset. This
        // compensates for clock drift that grows during a single fight (real-world up to 40+s
        // over 3 minutes), which a constant offset alone leaves as inflated time windows for
        // events only one source observed. Anchor sources and sources with too few paired
        // events get no entry in the map and fall back to constant-offset only.
        var drifts = FightDriftDetector.ComputeClusterDrifts(cluster);
        merged.Add(BuildMergedFight(cluster, drifts));
      }

      return merged;
    }

    internal static string TryParsePlayerNameFromLogFile(string path)
    {
      if (string.IsNullOrWhiteSpace(path))
      {
        return null;
      }

      var name = Path.GetFileName(path);
      var match = EqLogFileRegex.Match(name);
      return match.Success ? match.Groups[1].Value : null;
    }

    private static IEnumerable<List<(string SourcePlayer, double Offset, Fight Fight)>> ClusterByNameAndOverlap(
      List<(string SourcePlayer, double Offset, Fight Fight)> tagged)
    {
      foreach (var byName in tagged.GroupBy(t => t.Fight.Name))
      {
        var sorted = byName.OrderBy(t => t.Fight.BeginTime - t.Offset).ToList();
        List<(string, double, Fight)> current = null;
        var currentMaxLast = double.NegativeInfinity;

        foreach (var item in sorted)
        {
          var begin = item.Fight.BeginTime - item.Offset;
          var last = double.IsNaN(item.Fight.LastTime) ? begin : item.Fight.LastTime - item.Offset;

          if (current == null || begin > currentMaxLast)
          {
            if (current != null)
            {
              yield return current;
            }
            current = [item];
            currentMaxLast = last;
          }
          else
          {
            current.Add(item);
            currentMaxLast = Math.Max(currentMaxLast, last);
          }
        }

        if (current != null)
        {
          yield return current;
        }
      }
    }

    private static Fight BuildMergedFight(
      List<(string SourcePlayer, double Offset, Fight Fight)> cluster,
      Dictionary<string, DriftFunction> drifts)
    {
      var damageBlocks = MergeBlocks(cluster, drifts, applyDsFilter: true, static f => f.DamageBlocks);
      var tankingBlocks = MergeBlocks(cluster, drifts, applyDsFilter: false, static f => f.TankingBlocks);

      // Adjusted begin time across the cluster — every source's BeginTime is shifted by its
      // offset and per-source drift before comparison, so the merged frame lines up with
      // whichever source(s) have offset 0 (the anchor). BeginTimeString is rederived from the
      // adjusted time so the displayed start matches the merged-frame BeginTime, not whichever
      // source happened to sort first.
      var beginTime = cluster.Min(c => AdjustTime(c.Fight.BeginTime, c.Offset, GetDrift(drifts, c.SourcePlayer)));

      var merged = new Fight
      {
        Name = cluster[0].Fight.Name,
        Dead = cluster.Any(c => c.Fight.Dead),
        BeginTime = beginTime,
        BeginTimeString = DateUtil.FormatDotNetDateSeconds(beginTime)
      };
      merged.DamageBlocks.AddRange(damageBlocks);
      merged.TankingBlocks.AddRange(tankingBlocks);

      PopulateAggregates(merged);
      return merged;
    }

    // Apply offset and (optional) drift correction to a raw source timestamp. Drift is
    // expressed as a linear function of the offset-adjusted time, so we subtract offset first,
    // then subtract predicted drift.
    private static double AdjustTime(double rawTime, double offset, DriftFunction drift)
    {
      var afterOffset = rawTime - offset;
      return drift?.Correct(afterOffset) ?? afterOffset;
    }

    private static DriftFunction GetDrift(Dictionary<string, DriftFunction> drifts, string sourcePlayer)
    {
      return drifts != null && drifts.TryGetValue(sourcePlayer, out var d) ? d : null;
    }

    // Order-pair union across sources on a named block collection. For each unique DamageRecord,
    // pairs the N-th observation from source A with the N-th from source B — every iteration
    // consumes one entry from *every* source that still has observations remaining, then emits a
    // single merged event. Total events emitted per record = max(observations from any source).
    //
    // Why no drift / time bound: real-world Dalaya logs from two players in a 3-minute boss
    // fight show drift growing monotonically — first tick aligned, last tick drifted by 40+
    // seconds. Any fixed time window misses the late-fight ticks and over-counts. Order
    // pairing is invariant to drift: same-record observations within a single fight cluster
    // are almost always the same set of physical events (the merger already restricts to a
    // single name+time-overlap cluster), so the N-th from each source maps to the same event.
    //
    // Trade-off: if two sources independently miss disjoint genuinely-distinct same-record
    // events (e.g. one source missed cast #1, the other missed cast #2 of an identical-damage
    // recast), order pairing will under-count. This is rare — variable damage rolls usually
    // distinguish DoT ticks across casts — and far less impactful than the 2× over-count this
    // replaces.
    //
    // Multiset behavior preserved: quad attacks (4 identical hits at one timestamp from each
    // source) order-pair into 4 emits, not 7. Asymmetric counts (4+3) emit 4. DS filtering is
    // damage-only because the parser stores the DS holder as Attacker.
    private static List<ActionGroup> MergeBlocks(
      List<(string SourcePlayer, double Offset, Fight Fight)> cluster,
      Dictionary<string, DriftFunction> drifts,
      bool applyDsFilter,
      Func<Fight, List<ActionGroup>> blocksSelector)
    {
      var perRecord = new Dictionary<DamageRecord, List<double>[]>();
      // Cache the drift function per source index so we don't dictionary-lookup per-action.
      var driftBySource = new DriftFunction[cluster.Count];
      for (var i = 0; i < cluster.Count; i++)
      {
        driftBySource[i] = GetDrift(drifts, cluster[i].SourcePlayer);
      }

      for (var sourceIdx = 0; sourceIdx < cluster.Count; sourceIdx++)
      {
        var (sourcePlayer, offset, sourceFight) = cluster[sourceIdx];
        var drift = driftBySource[sourceIdx];
        foreach (var block in blocksSelector(sourceFight))
        {
          foreach (var action in block.Actions)
          {
            if (action is not DamageRecord record)
            {
              continue;
            }

            if (applyDsFilter && record.Type == Labels.Ds &&
                !string.Equals(sourcePlayer, record.Attacker, StringComparison.Ordinal))
            {
              continue;
            }

            if (!perRecord.TryGetValue(record, out var bySource))
            {
              bySource = new List<double>[cluster.Count];
              for (var i = 0; i < cluster.Count; i++)
              {
                bySource[i] = [];
              }
              perRecord[record] = bySource;
            }
            bySource[sourceIdx].Add(AdjustTime(block.BeginTime, offset, drift));
          }
        }
      }

      var emitted = new List<(double Time, DamageRecord Record)>();
      var indices = new int[cluster.Count];

      foreach (var (record, bySource) in perRecord)
      {
        for (var s = 0; s < bySource.Length; s++)
        {
          bySource[s].Sort();
          indices[s] = 0;
        }

        while (true)
        {
          // Each iteration represents one physical event: consume the next unconsumed entry
          // from every source that still has observations, take the earliest of those as the
          // emit time. When a source runs out, surplus events from the others continue to
          // emit alone.
          var emitTime = double.PositiveInfinity;
          var anyConsumed = false;
          for (var s = 0; s < bySource.Length; s++)
          {
            if (indices[s] < bySource[s].Count)
            {
              var t = bySource[s][indices[s]];
              if (t < emitTime)
              {
                emitTime = t;
              }
              indices[s]++;
              anyConsumed = true;
            }
          }

          if (!anyConsumed)
          {
            break;
          }

          emitted.Add((emitTime, record));
        }
      }

      return emitted
        .GroupBy(e => e.Time)
        .OrderBy(g => g.Key)
        .Select(g =>
        {
          var ag = new ActionGroup { BeginTime = g.Key };
          foreach (var e in g)
          {
            ag.Actions.Add(e.Record);
          }
          return ag;
        })
        .ToList();
    }

    private static void PopulateAggregates(Fight fight)
    {
      PopulateTankingAggregates(fight);

      if (fight.DamageBlocks.Count == 0)
      {
        return;
      }

      var validator = new DamageValidator();

      foreach (var block in fight.DamageBlocks)
      {
        var beginTime = block.BeginTime;

        if (double.IsNaN(fight.BeginDamageTime))
        {
          fight.BeginDamageTime = beginTime;
        }
        fight.LastDamageTime = beginTime;

        foreach (var action in block.Actions)
        {
          if (action is not DamageRecord record)
          {
            continue;
          }

          // Populate per-player time segments for every DamageRecord, not just HitType ones.
          // NpcDamageManager.HandleDamage calls AddPlayerTime unconditionally for any record
          // whose defender is an NPC — including misses, dodges, absorbs, INVULNERABLE hits.
          // Those non-hit records still advance the attacker's time segment EndTime, which
          // ripples into the "+Pets" aggregate union inside DamageStatsManager. Skipping them
          // here made the raid-damage view's DPS differ from DPS Summary for the same fight.
          StatsUtil.UpdateTimeSegments(fight.DamageSegments, fight.DamageSubSegments,
            StatsUtil.CreateRecordKey(record.Type, record.SubType), record.Attacker, beginTime);

          if (!StatsUtil.IsHitType(record.Type))
          {
            continue;
          }

          fight.DamageHits++;
          fight.DamageTotal += record.Total;

          var attacker = record.AttackerOwner ?? record.Attacker;
          var damage = validator.IsValid(record) ? record.Total : 0L;

          if (fight.PlayerDamageTotals.TryGetValue(attacker, out var total))
          {
            total.Damage += damage;
            total.PetOwner ??= record.AttackerOwner;
            total.UpdateTime = beginTime;
          }
          else
          {
            fight.PlayerDamageTotals[attacker] = new FightTotalDamage
            {
              Damage = damage,
              PetOwner = record.AttackerOwner,
              BeginTime = beginTime,
              UpdateTime = beginTime
            };
          }
        }
      }

      fight.LastTime = fight.DamageBlocks[^1].BeginTime;
    }

    // Mirror of the damage path for tanking: populate time segments keyed by defender (the
    // tank taking damage), plus aggregate hits/total and per-player totals. Matches the
    // live-parse flow in NpcDamageManager where tanking records are kept independently of
    // damage records. No validator filter — all tanking records count.
    private static void PopulateTankingAggregates(Fight fight)
    {
      if (fight.TankingBlocks.Count == 0)
      {
        return;
      }

      foreach (var block in fight.TankingBlocks)
      {
        var beginTime = block.BeginTime;

        if (double.IsNaN(fight.BeginTankingTime))
        {
          fight.BeginTankingTime = beginTime;
        }
        fight.LastTankingTime = beginTime;

        foreach (var action in block.Actions)
        {
          if (action is not DamageRecord record)
          {
            continue;
          }

          StatsUtil.UpdateTimeSegments(fight.TankSegments, fight.TankSubSegments,
            StatsUtil.CreateRecordKey(record.Type, record.SubType), record.Defender, beginTime);

          fight.TankHits++;
          fight.TankTotal += record.Total;

          if (fight.PlayerTankTotals.TryGetValue(record.Defender, out var total))
          {
            total.Damage += record.Total;
            total.UpdateTime = beginTime;
          }
          else
          {
            fight.PlayerTankTotals[record.Defender] = new FightTotalDamage
            {
              Damage = record.Total,
              BeginTime = beginTime,
              UpdateTime = beginTime
            };
          }
        }
      }
    }
  }
}
