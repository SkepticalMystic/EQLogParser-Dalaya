using System.Collections.Generic;
using System.Linq;

namespace EQLogParser
{
  // One row in the HoT Effectiveness table. SpellName is the SubType from the
  // HealRecord (e.g., "Circle of Soothing"). ExpectedPerTick, TickIntervalSeconds,
  // and Ratio are null when EQDataStore has no HoT entry for the spell (procs
  // from items without spell-effect data, future Dalaya spells with new Calc
  // codes the formula port doesn't handle, etc.). See project_dot_hot_validation
  // Phase 3.
  internal class HotEffectivenessRow
  {
    public string SpellName { get; init; }
    public int TickCount { get; init; }
    public long TotalHealing { get; init; }
    public double AverageObserved { get; init; }
    public double? NonCritAverage { get; init; }     // null when zero non-crit ticks
    public uint MaxHeal { get; init; }
    public int CritCount { get; init; }
    public double CritRate { get; init; }            // 0.0 .. 1.0
    public int? ExpectedPerTick { get; init; }       // spell-data base, no modifiers
    public int? TickIntervalSeconds { get; init; }   // 2 for Druid HoTs, 6 otherwise
    public int? ExpectedTotal { get; init; }
    public double? Ratio { get; init; }              // AverageObserved / ExpectedPerTick
  }

  // Pure-function aggregator. Filters a heal-record stream to a single player's
  // HoT ticks, groups by spell, and joins per-spell observation with the
  // spell-data expected-tick computation. UI is a thin presentation layer over
  // this output; tests assert against this output directly.
  internal static class HotEffectivenessAggregator
  {
    public static List<HotEffectivenessRow> Aggregate(
      IEnumerable<HealRecord> records,
      string playerName,
      byte playerLevel,
      SpellClass playerClass,
      EQDataStore dataStore)
    {
      if (records == null || string.IsNullOrEmpty(playerName) || dataStore == null)
      {
        return [];
      }

      // Filter to this player's HoT ticks only. Direct heals stay out — Phase 3
      // is HoT-specific. Healer comparison is ordinal because PlayerRegistry
      // already normalizes "you/your" to the player's name at parse time.
      var bySpell = records
        .Where(r => r != null
                    && string.Equals(r.Type, Labels.Hot, System.StringComparison.Ordinal)
                    && string.Equals(r.Healer, playerName, System.StringComparison.Ordinal)
                    && !string.IsNullOrEmpty(r.SubType))
        .GroupBy(r => r.SubType);

      var rows = new List<HotEffectivenessRow>();
      foreach (var group in bySpell)
      {
        var count = 0;
        long sum = 0;
        uint max = 0;
        var crits = 0;
        long nonCritSum = 0;
        var nonCritCount = 0;
        foreach (var rec in group)
        {
          count++;
          sum += rec.Total;
          if (rec.Total > max) max = rec.Total;
          if (LineModifiersParser.IsCrit(rec.ModifiersMask))
          {
            crits++;
          }
          else
          {
            nonCritSum += rec.Total;
            nonCritCount++;
          }
        }
        if (count == 0) continue;

        var info = dataStore.ComputeHotTickInfo(group.Key, playerLevel, playerClass);
        var average = sum / (double)count;

        rows.Add(new HotEffectivenessRow
        {
          SpellName = group.Key,
          TickCount = count,
          TotalHealing = sum,
          AverageObserved = average,
          NonCritAverage = nonCritCount > 0 ? nonCritSum / (double)nonCritCount : null,
          MaxHeal = max,
          CritCount = crits,
          CritRate = crits / (double)count,
          ExpectedPerTick = info?.PerTickAmount,
          TickIntervalSeconds = info?.TickIntervalSeconds,
          ExpectedTotal = info?.TotalExpected,
          Ratio = info is { PerTickAmount: > 0 } i ? average / i.PerTickAmount : null
        });
      }

      rows.Sort((a, b) => b.TotalHealing.CompareTo(a.TotalHealing));
      return rows;
    }
  }
}
