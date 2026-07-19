using log4net;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    // When true, MergeBlocks emits a one-shot diagnostic per cluster that detects
    // dedup-key splits — records that share (Attacker, Defender, Total) but landed in
    // different perRecord buckets due to a metadata difference (ModifiersMask,
    // AttackerOwner, Type/SubType, etc.). Off by default; toggleable from
    // Help → "Log Merge Diagnostics" and persisted under ConfigUtil "LogMergeDiagnostics".
    // MainWindow reads the setting at startup and reflects user toggles back here.
    internal static bool MergeDiagnosticsEnabled;

    // Temporary test hook: when non-null, MergeBlocks appends split sample strings here
    // instead of (or in addition to) log4net output, so integration tests can inspect splits.
    internal static System.Collections.Concurrent.ConcurrentBag<string> DiagnosticSplits;

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
      var damageBlocks = MergeBlocks(cluster, drifts, applyDsFilter: true, static f => f.DamageBlocks, "damage");
      var tankingBlocks = MergeBlocks(cluster, drifts, applyDsFilter: false, static f => f.TankingBlocks, "tanking");

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
      Func<Fight, List<ActionGroup>> blocksSelector,
      string blockKind)
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

      ReconcileUnknownDdSubTypes(perRecord);
      CollapseModifierMaskVariants(perRecord);

      if (MergeDiagnosticsEnabled)
      {
        LogDedupSplitDiagnostics(cluster, perRecord, blockKind);
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

    // Pre-pass for DD attribution: when one source attributed a DD hit to a spell name
    // and another source recorded the same hit as "Unknown" (because it didn't observe
    // the cast line), the two records have different SubTypes and may also have different
    // ModifiersMasks. LooseDamageKey includes SubType literally, so CollapseModifierMaskVariants
    // cannot group them. This pass folds Unknown-SubType DD records into their matching
    // named-SubType counterpart so CollapseModifierMaskVariants can handle any remaining
    // mask differences in one consolidated bucket.
    private static void ReconcileUnknownDdSubTypes(Dictionary<DamageRecord, List<double>[]> perRecord)
    {
      if (perRecord.Count < 2) return;

      var namedTargets = new Dictionary<DdPhysicalKey, (DamageRecord Record, List<double>[] BySource)>();
      var unknowns = new List<(DamageRecord Record, List<double>[] BySource)>();

      foreach (var (record, bySource) in perRecord)
      {
        if (record.Type != Labels.Dd) continue;
        var key = DdPhysicalKey.From(record);
        if (record.SubType == Labels.Unk)
        {
          unknowns.Add((record, bySource));
        }
        else if (!namedTargets.ContainsKey(key))
        {
          namedTargets[key] = (record, bySource);
        }
      }

      if (unknowns.Count == 0 || namedTargets.Count == 0) return;

      foreach (var (unknown, unknownBySource) in unknowns)
      {
        var key = DdPhysicalKey.From(unknown);
        if (!namedTargets.TryGetValue(key, out var target)) continue;
        for (var s = 0; s < target.BySource.Length; s++)
          target.BySource[s].AddRange(unknownBySource[s]);
        perRecord.Remove(unknown);
      }
    }

    // Physical-hit identity for DD records: everything except SubType and ModifiersMask.
    // Used by ReconcileUnknownDdSubTypes to match Unknown-SubType records against their
    // named counterparts from sources that could observe the cast line.
    private readonly record struct DdPhysicalKey(
      string Attacker, string AttackerOwner, string Defender, string DefenderOwner,
      bool AttackerIsSpell, uint Total, uint OverTotal)
    {
      internal static DdPhysicalKey From(DamageRecord r) =>
        new(r.Attacker, r.AttackerOwner, r.Defender, r.DefenderOwner,
            r.AttackerIsSpell, r.Total, r.OverTotal);
    }

    // Identity for "the same physical hit" — everything that should match across sources
    // when dedup is working, omitting ModifiersMask because crit/lucky/twincast announcements
    // are paired to the damage line within a 1-second window and that pairing fails per-
    // source under network jitter / log-line ordering. Same-hit records with divergent
    // masks get collapsed by CollapseModifierMaskVariants below.
    private readonly record struct LooseDamageKey(
      string Attacker, string AttackerOwner, string Defender, string DefenderOwner,
      bool AttackerIsSpell, uint Total, uint OverTotal, string Type, string SubType);

    private static LooseDamageKey GetLooseKey(DamageRecord r) =>
      new(r.Attacker, r.AttackerOwner, r.Defender, r.DefenderOwner,
          r.AttackerIsSpell, r.Total, r.OverTotal, r.Type, r.SubType);

    // Collapse buckets that share a loose key but disagree only on ModifiersMask. The
    // default ModifiersMask of -1 means "no modifier announcement was parsed" — it's a
    // sentinel for missing information, not an explicit "this hit had no modifiers" (which
    // would be 0). When sources disagree, OR all concrete (>= 0) masks together — that's
    // the union of modifier announcements any source saw — and route every observation into
    // a single bucket so the order-pair dedup counts it once instead of once per variant.
    private static void CollapseModifierMaskVariants(Dictionary<DamageRecord, List<double>[]> perRecord)
    {
      if (perRecord.Count < 2) return;

      var groups = new Dictionary<LooseDamageKey, List<DamageRecord>>();
      foreach (var record in perRecord.Keys)
      {
        var key = GetLooseKey(record);
        if (!groups.TryGetValue(key, out var list))
        {
          list = [];
          groups[key] = list;
        }
        list.Add(record);
      }

      foreach (var variants in groups.Values)
      {
        if (variants.Count < 2) continue;

        short unifiedMask = -1;
        foreach (var v in variants)
        {
          var m = v.ModifiersMask;
          if (m >= 0)
          {
            unifiedMask = unifiedMask < 0 ? m : (short)(unifiedMask | m);
          }
        }

        // Pick the primary variant: one that already carries the unified mask if such
        // exists, so we can keep its dictionary key and avoid an allocation. Otherwise
        // the first variant becomes the merge target and we'll swap its key out.
        DamageRecord primary = null;
        foreach (var v in variants)
        {
          if (v.ModifiersMask == unifiedMask)
          {
            primary = v;
            break;
          }
        }
        var needsCloneKey = primary == null;
        primary ??= variants[0];

        var mergedTimes = perRecord[primary];
        foreach (var v in variants)
        {
          if (ReferenceEquals(v, primary)) continue;
          var otherTimes = perRecord[v];
          for (var s = 0; s < mergedTimes.Length; s++)
          {
            mergedTimes[s].AddRange(otherTimes[s]);
          }
          perRecord.Remove(v);
        }

        if (needsCloneKey)
        {
          perRecord.Remove(primary);
          var unified = CloneWithMask(primary, unifiedMask);
          perRecord[unified] = mergedTimes;
        }
      }
    }

    private static DamageRecord CloneWithMask(DamageRecord src, short mask) => new()
    {
      Attacker = src.Attacker,
      AttackerOwner = src.AttackerOwner,
      Defender = src.Defender,
      DefenderOwner = src.DefenderOwner,
      AttackerIsSpell = src.AttackerIsSpell,
      Total = src.Total,
      OverTotal = src.OverTotal,
      Type = src.Type,
      SubType = src.SubType,
      ModifiersMask = mask
    };

    // Detects dedup-key splits: records that share (Attacker, Defender, Total) but were
    // bucketed into separate perRecord entries because another field (ModifiersMask,
    // AttackerOwner, Type, etc.) differs across sources. Each split causes the merger to
    // over-emit. Reports per-cluster excess events + damage and the desync field.
    private static void LogDedupSplitDiagnostics(
      List<(string SourcePlayer, double Offset, Fight Fight)> cluster,
      Dictionary<DamageRecord, List<double>[]> perRecord,
      string blockKind)
    {
      if (perRecord.Count == 0)
      {
        return;
      }

      var fightName = cluster[0].Fight.Name ?? "(unnamed)";
      var sourceList = string.Join(",", cluster.Select(c => c.SourcePlayer));

      // Group by loose key (Attacker, Defender, Total) — what the dedup *should* match.
      var loose = new Dictionary<(string Attacker, string Defender, long Total), List<(DamageRecord Record, int[] Counts)>>();
      foreach (var (record, bySource) in perRecord)
      {
        var counts = new int[bySource.Length];
        for (var s = 0; s < bySource.Length; s++)
        {
          counts[s] = bySource[s].Count;
        }
        var key = (record.Attacker ?? "", record.Defender ?? "", record.Total);
        if (!loose.TryGetValue(key, out var variants))
        {
          variants = [];
          loose[key] = variants;
        }
        variants.Add((record, counts));
      }

      long totalCurrentEmits = 0;
      long totalOptimisticEmits = 0;
      long totalExcessEvents = 0;
      long totalExcessDamage = 0;

      var fieldHistogram = new Dictionary<string, (long Events, long Damage)>();
      var samples = new List<string>();

      foreach (var (key, variants) in loose)
      {
        // Sum of "current" emits across variants: max source count per variant.
        long currentEmits = 0;
        foreach (var v in variants)
        {
          var max = 0;
          for (var s = 0; s < v.Counts.Length; s++)
          {
            if (v.Counts[s] > max) max = v.Counts[s];
          }
          currentEmits += max;
        }

        // Optimistic emits if variants were collapsed: max across sources of (sum of all variant counts in that source).
        long optimisticEmits = 0;
        for (var s = 0; s < cluster.Count; s++)
        {
          long sourceSum = 0;
          foreach (var v in variants)
          {
            sourceSum += v.Counts[s];
          }
          if (sourceSum > optimisticEmits) optimisticEmits = sourceSum;
        }

        totalCurrentEmits += currentEmits;
        totalOptimisticEmits += optimisticEmits;

        if (variants.Count < 2)
        {
          continue;
        }

        var excess = currentEmits - optimisticEmits;
        if (excess <= 0)
        {
          continue;
        }

        totalExcessEvents += excess;
        var excessDamage = excess * key.Total;
        totalExcessDamage += excessDamage;

        var desyncField = IdentifyDesyncFields(variants.Select(v => v.Record).ToList());
        if (!fieldHistogram.TryGetValue(desyncField, out var fh))
        {
          fh = (0, 0);
        }
        fieldHistogram[desyncField] = (fh.Events + excess, fh.Damage + excessDamage);

        if (samples.Count < 15)
        {
          var perSourceCounts = new List<string>(cluster.Count);
          for (var s = 0; s < cluster.Count; s++)
          {
            var perVariant = string.Join("/", variants.Select(v => v.Counts[s].ToString()));
            perSourceCounts.Add($"{cluster[s].SourcePlayer}=[{perVariant}]");
          }
          var variantDescs = string.Join(" || ", variants.Select(v => DescribeRecord(v.Record)));
          samples.Add($"{key.Attacker} -> {key.Defender} for {key.Total} excess={excess} desync={desyncField} " +
                      $"perSource={{{string.Join(", ", perSourceCounts)}}} variants=[{variantDescs}]");
        }
      }

      Log.Info($"[MergeDiag] cluster='{fightName}' kind={blockKind} sources=[{sourceList}] " +
               $"uniqueLooseKeys={loose.Count} currentEmits={totalCurrentEmits} optimisticEmits={totalOptimisticEmits} " +
               $"excessEvents={totalExcessEvents} excessDamage={totalExcessDamage:N0}");

      if (totalExcessEvents == 0)
      {
        return;
      }

      foreach (var (field, value) in fieldHistogram.OrderByDescending(kv => kv.Value.Damage))
      {
        Log.Info($"[MergeDiag]   desyncField={field} excessEvents={value.Events} excessDamage={value.Damage:N0}");
      }

      foreach (var sample in samples)
      {
        Log.Info($"[MergeDiag]   sample: {sample}");
        DiagnosticSplits?.Add($"[{fightName}/{blockKind}] {sample}");
      }
    }

    private static string IdentifyDesyncFields(List<DamageRecord> variants)
    {
      var diffs = new List<string>();
      var first = variants[0];
      if (variants.Any(v => v.AttackerOwner != first.AttackerOwner)) diffs.Add("AttackerOwner");
      if (variants.Any(v => v.DefenderOwner != first.DefenderOwner)) diffs.Add("DefenderOwner");
      if (variants.Any(v => v.AttackerIsSpell != first.AttackerIsSpell)) diffs.Add("AttackerIsSpell");
      if (variants.Any(v => v.OverTotal != first.OverTotal)) diffs.Add("OverTotal");
      if (variants.Any(v => v.Type != first.Type)) diffs.Add("Type");
      if (variants.Any(v => v.SubType != first.SubType)) diffs.Add("SubType");
      if (variants.Any(v => v.ModifiersMask != first.ModifiersMask)) diffs.Add("ModifiersMask");
      return diffs.Count == 0 ? "(none)" : string.Join("+", diffs);
    }

    private static string DescribeRecord(DamageRecord r)
    {
      return $"Owner={r.AttackerOwner ?? "null"} DefOwner={r.DefenderOwner ?? "null"} IsSpell={r.AttackerIsSpell} " +
             $"Type={r.Type ?? "null"} SubType={r.SubType ?? "null"} Mods=0x{r.ModifiersMask:X} OT={r.OverTotal}";
    }

    private static void PopulateAggregates(Fight fight)
    {
      PopulateTankingAggregates(fight);

      if (fight.DamageBlocks.Count == 0)
      {
        return;
      }

      var validator = new DamageValidator(
        AppSettings.IsAssassinateDamageEnabled, AppSettings.IsBaneDamageEnabled, AppSettings.IsDamageShieldDamageEnabled,
        AppSettings.IsFinishingBlowDamageEnabled, AppSettings.IsHeadshotDamageEnabled, AppSettings.IsSlayUndeadDamageEnabled);

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
