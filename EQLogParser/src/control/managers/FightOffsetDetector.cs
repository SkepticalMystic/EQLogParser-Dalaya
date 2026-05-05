using System;
using System.Collections.Generic;
using System.Linq;

namespace EQLogParser
{
  // Detects clock offsets between log sources for the multi-log raid damage view. EQ logs
  // timestamp every line in the player's local clock, so two players in different timezones
  // produce non-overlapping windows for the same fight and FightMerger keeps them separate.
  // Detect() pairs same-name fights across two sources, snaps the per-pair time deltas to
  // 15-minute buckets, and returns the modal bucket as the offset.
  internal static class FightOffsetDetector
  {
    // Real-world clock differences are always whole hours (most timezones), 30 minutes (India,
    // South Australia), or 45 minutes (Nepal, Chatham). 15-minute granularity covers all of
    // those and is coarse enough to collapse repeat-fight noise: at this bin size, a target
    // fight's ambiguous pairings against multiple ref fights of the same name fall into the
    // same bucket as long as repeats are spaced under ~7 minutes apart.
    private const double SnapGranularitySeconds = 900.0;

    // Returns the offset in seconds to subtract from `target`'s timestamps to align them with
    // `reference`. Positive value means target's clock is ahead of reference.
    //
    // Returns 0 when there are no shared-name fights (callers can show "no shared fights" UI
    // and expose manual override). The returned value is always a multiple of 900 seconds.
    internal static double Detect(IList<Fight> reference, IList<Fight> target)
    {
      if (reference == null || target == null)
      {
        return 0;
      }

      var refTimesByName = new Dictionary<string, List<double>>(StringComparer.Ordinal);
      foreach (var f in reference)
      {
        if (f == null || string.IsNullOrEmpty(f.Name) || f.IsInactivity)
        {
          continue;
        }
        if (!refTimesByName.TryGetValue(f.Name, out var list))
        {
          list = new List<double>();
          refTimesByName[f.Name] = list;
        }
        list.Add(f.BeginTime);
      }

      if (refTimesByName.Count == 0)
      {
        return 0;
      }

      var bucketCounts = new Dictionary<double, int>();
      foreach (var f in target)
      {
        if (f == null || string.IsNullOrEmpty(f.Name) || f.IsInactivity)
        {
          continue;
        }
        if (!refTimesByName.TryGetValue(f.Name, out var refTimes))
        {
          continue;
        }

        foreach (var refTime in refTimes)
        {
          var delta = f.BeginTime - refTime;
          var bucket = Math.Round(delta / SnapGranularitySeconds) * SnapGranularitySeconds;
          bucketCounts[bucket] = bucketCounts.GetValueOrDefault(bucket) + 1;
        }
      }

      if (bucketCounts.Count == 0)
      {
        return 0;
      }

      // Tie-break toward smaller absolute offset. Without this, equal-count buckets are
      // ordered nondeterministically; with same-name fights at sub-bin spacing, the real
      // offset bucket and a noise bucket can tie and we'd rather default to "no shift".
      return bucketCounts
        .OrderByDescending(kv => kv.Value)
        .ThenBy(kv => Math.Abs(kv.Key))
        .First().Key;
    }

    // Convenience for the multi-source case: pick the source with the most fights as the
    // anchor (offset 0) and detect each other source's offset against it. Returns a dictionary
    // keyed by source player name. If the reference source can't be resolved (no fights at
    // all), returns an empty map and the caller should leave existing offsets untouched.
    internal static Dictionary<string, double> DetectAll(
      IEnumerable<(string SourcePlayer, IList<Fight> Fights)> sources)
    {
      var result = new Dictionary<string, double>(StringComparer.Ordinal);
      if (sources == null)
      {
        return result;
      }

      var list = sources
        .Where(s => !string.IsNullOrEmpty(s.SourcePlayer) && s.Fights != null && s.Fights.Count > 0)
        .ToList();
      if (list.Count == 0)
      {
        return result;
      }

      // Anchor = source with the most fights. Most-fights is a stronger signal than first-
      // loaded for "this is probably the most complete log" and gives detection the largest
      // possible reference set.
      var anchor = list.OrderByDescending(s => s.Fights.Count).First();
      result[anchor.SourcePlayer] = 0;

      foreach (var s in list)
      {
        if (string.Equals(s.SourcePlayer, anchor.SourcePlayer, StringComparison.Ordinal))
        {
          continue;
        }
        result[s.SourcePlayer] = Detect(anchor.Fights, s.Fights);
      }

      return result;
    }
  }
}
