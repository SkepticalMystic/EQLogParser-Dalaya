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
          .Select(f => (s.SourcePlayer, Fight: f)))
        .ToList();

      if (tagged.Count == 0)
      {
        return merged;
      }

      foreach (var cluster in ClusterByNameAndOverlap(tagged))
      {
        merged.Add(BuildMergedFight(cluster));
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

    private static IEnumerable<List<(string SourcePlayer, Fight Fight)>> ClusterByNameAndOverlap(
      List<(string SourcePlayer, Fight Fight)> tagged)
    {
      foreach (var byName in tagged.GroupBy(t => t.Fight.Name))
      {
        var sorted = byName.OrderBy(t => t.Fight.BeginTime).ToList();
        List<(string, Fight)> current = null;
        var currentMaxLast = double.NegativeInfinity;

        foreach (var item in sorted)
        {
          var last = double.IsNaN(item.Fight.LastTime) ? item.Fight.BeginTime : item.Fight.LastTime;

          if (current == null || item.Fight.BeginTime > currentMaxLast)
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

    private static Fight BuildMergedFight(List<(string SourcePlayer, Fight Fight)> cluster)
    {
      // Per-source occurrence counts keyed on (BeginTime, DamageRecord flyweight).
      // Records with identical fields share a reference thanks to SimpleObjectCache during parse.
      var perSource = new List<Dictionary<(double Time, DamageRecord Record), int>>(cluster.Count);

      foreach (var (sourcePlayer, sourceFight) in cluster)
      {
        var counts = new Dictionary<(double, DamageRecord), int>();
        foreach (var block in sourceFight.DamageBlocks)
        {
          foreach (var action in block.Actions)
          {
            if (action is not DamageRecord record)
            {
              continue;
            }

            // DS records are only visible in the DS holder's own log. The parser stores
            // the DS holder (a PC) as the record's Attacker; Defender is the NPC taking
            // the reflected damage.
            if (record.Type == Labels.Ds && !string.Equals(sourcePlayer, record.Attacker, StringComparison.Ordinal))
            {
              continue;
            }

            var key = (block.BeginTime, record);
            counts[key] = counts.GetValueOrDefault(key) + 1;
          }
        }
        perSource.Add(counts);
      }

      // Multiset union: per key, take the max count across sources.
      var unioned = new Dictionary<(double Time, DamageRecord Record), int>();
      foreach (var counts in perSource)
      {
        foreach (var kv in counts)
        {
          if (!unioned.TryGetValue(kv.Key, out var existing) || kv.Value > existing)
          {
            unioned[kv.Key] = kv.Value;
          }
        }
      }

      // Rebuild ActionGroups, one per distinct BeginTime.
      var damageBlocks = unioned
        .GroupBy(kv => kv.Key.Time)
        .OrderBy(g => g.Key)
        .Select(g =>
        {
          var ag = new ActionGroup { BeginTime = g.Key };
          foreach (var kv in g)
          {
            for (var i = 0; i < kv.Value; i++)
            {
              ag.Actions.Add(kv.Key.Record);
            }
          }
          return ag;
        })
        .ToList();

      var merged = new Fight
      {
        Name = cluster[0].Fight.Name,
        Dead = cluster.Any(c => c.Fight.Dead),
        BeginTime = cluster.Min(c => c.Fight.BeginTime),
        BeginTimeString = cluster[0].Fight.BeginTimeString
      };
      merged.DamageBlocks.AddRange(damageBlocks);

      PopulateAggregates(merged);
      return merged;
    }

    private static void PopulateAggregates(Fight fight)
    {
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
  }
}
