using System;
using System.Collections.Generic;
using System.Linq;

namespace EQLogParser
{
  // Detects per-source linear clock drift within a single fight cluster, on top of the bulk
  // constant offset from FightOffsetDetector. Observed real-world drift in two-player Dalaya
  // logs grew from ~0s to 40+s over a single 3-minute boss fight; a constant offset can't
  // correct that without inflating the lone-source-observed events' time window. This detector
  // pairs DamageRecord observations across sources by record-identity + sequence (the same
  // pairing the merger uses) and fits a least-squares line to the drift values.
  internal static class FightDriftDetector
  {
    // Minimum paired events for a fit. Below this the slope estimate is too noisy to trust.
    // Two would technically suffice for a line, but small samples can produce wild slopes when
    // a single outlier dominates; 5 strikes a reasonable balance for typical raid fights.
    private const int MinPairsForFit = 5;

    // Clamp the fit's slope to a sensible range. A slope of 0.5 means the source's clock runs
    // 50% slower than the anchor's, which is implausible — almost certainly a bad fit. Reject
    // and fall back to constant-offset only.
    private const double MaxAbsSlope = 0.5;

    // Returns a per-source drift function map keyed by source player name. Anchors (sources
    // already offset to the merged frame) are not included since their drift is zero by
    // definition. Sources with too few paired events vs the anchor are also omitted —
    // FightMerger applies no drift correction in that case (constant offset only).
    internal static Dictionary<string, DriftFunction> ComputeClusterDrifts(
      List<(string SourcePlayer, double Offset, Fight Fight)> cluster)
    {
      var result = new Dictionary<string, DriftFunction>(StringComparer.Ordinal);

      // Group cluster entries by source. Multiple fights from the same source can land in one
      // cluster (e.g. two consecutive Boss attempts that overlap in adjusted time); each
      // contributes its own damage records to the pool of pairs.
      var bySource = new Dictionary<string, List<(double Offset, Fight Fight)>>(StringComparer.Ordinal);
      foreach (var (sp, off, f) in cluster)
      {
        if (!bySource.TryGetValue(sp, out var list))
        {
          list = [];
          bySource[sp] = list;
        }
        list.Add((off, f));
      }

      if (bySource.Count < 2)
      {
        return result;
      }

      // Drift anchor = the source with the *fastest* clock (smallest mean offset-adjusted
      // timestamp). Network lag only delays observations, so the source whose events appear
      // earliest in the merged frame is closest to physical real time. Drift correction pulls
      // slower sources back into this frame.
      //
      // Important: this anchor is for *drift only* — it can differ from the constant-offset
      // anchor that the offset detector picked (largest source). Using the wrong anchor
      // pushes correction the wrong direction (extending lone-source events instead of
      // pulling them back).
      string anchor = null;
      var minMeanTime = double.PositiveInfinity;
      foreach (var (sp, entries) in bySource)
      {
        double sum = 0;
        var count = 0;
        foreach (var (offset, fight) in entries)
        {
          foreach (var block in fight.DamageBlocks)
          {
            sum += block.BeginTime - offset;
            count++;
          }
        }
        if (count == 0) continue;
        var mean = sum / count;
        if (mean < minMeanTime)
        {
          minMeanTime = mean;
          anchor = sp;
        }
      }
      if (anchor == null)
      {
        return result;
      }

      var anchorRecords = CollectRecordTimes(bySource[anchor]);

      foreach (var (sp, entries) in bySource)
      {
        if (string.Equals(sp, anchor, StringComparison.Ordinal))
        {
          continue;
        }
        var targetRecords = CollectRecordTimes(entries);
        var drift = FitDrift(anchorRecords, targetRecords);
        if (drift != null)
        {
          result[sp] = drift;
        }
      }

      return result;
    }

    // Pairs damage records by identity + sequence (the same shape the merger uses) and runs
    // ordinary least squares: drift_i = a + b * (target_t_i - t0). Returns null if too few
    // pairs or if the fit produces an implausible slope.
    private static DriftFunction FitDrift(
      Dictionary<DamageRecord, List<double>> anchorRecords,
      Dictionary<DamageRecord, List<double>> targetRecords)
    {
      var pairs = new List<(double Anchor, double Target)>();
      foreach (var (record, anchorTimes) in anchorRecords)
      {
        if (!targetRecords.TryGetValue(record, out var targetTimes))
        {
          continue;
        }
        anchorTimes.Sort();
        targetTimes.Sort();
        var n = Math.Min(anchorTimes.Count, targetTimes.Count);
        for (var i = 0; i < n; i++)
        {
          pairs.Add((anchorTimes[i], targetTimes[i]));
        }
      }

      if (pairs.Count < MinPairsForFit)
      {
        return null;
      }

      // Regress in target_t domain so Predict() can be applied directly to a target timestamp
      // (including lone-source ones not present in the pair set). t0 = earliest target time
      // keeps numerical magnitudes small (Unix-epoch seconds * tiny slope would otherwise lose
      // precision).
      var t0 = pairs.Min(p => p.Target);

      double sumX = 0, sumY = 0;
      foreach (var (a, t) in pairs)
      {
        sumX += t - t0;
        sumY += t - a;
      }
      var n2 = pairs.Count;
      var meanX = sumX / n2;
      var meanY = sumY / n2;

      double num = 0, den = 0;
      foreach (var (a, t) in pairs)
      {
        var dx = (t - t0) - meanX;
        var dy = (t - a) - meanY;
        num += dx * dy;
        den += dx * dx;
      }

      if (den == 0)
      {
        return null;
      }

      var slope = num / den;
      var intercept = meanY - slope * meanX;

      if (Math.Abs(slope) > MaxAbsSlope)
      {
        return null;
      }

      return new DriftFunction(intercept, slope, t0);
    }

    private static Dictionary<DamageRecord, List<double>> CollectRecordTimes(
      List<(double Offset, Fight Fight)> entries)
    {
      var result = new Dictionary<DamageRecord, List<double>>();
      foreach (var (offset, fight) in entries)
      {
        if (fight == null)
        {
          continue;
        }
        foreach (var block in fight.DamageBlocks)
        {
          foreach (var action in block.Actions)
          {
            if (action is not DamageRecord record)
            {
              continue;
            }
            if (!result.TryGetValue(record, out var list))
            {
              list = [];
              result[record] = list;
            }
            list.Add(block.BeginTime - offset);
          }
        }
      }
      return result;
    }
  }
}
