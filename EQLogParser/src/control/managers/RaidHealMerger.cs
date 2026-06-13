using System;
using System.Collections.Generic;

namespace EQLogParser
{
  // Cross-source heal aggregation for the Raid Damage window's Healing tab.
  //
  // Unlike combat damage — which FightMerger folds into merged Fight objects that carry their
  // own DamageBlocks/TankingBlocks — heal records are time-indexed in each source's isolated
  // RecordsStore and have no per-fight container. The Healing tab therefore unions each
  // source's heal log here before HealingStatsBuilder slices it by the selected fights' time
  // windows. Kept as a pure static so the offset-alignment + dedup contract is unit-testable
  // without a RecordsStore or UI (mirrors SpellPickerFilter / HotEffectivenessAggregator).
  internal static class RaidHealMerger
  {
    // Yields every source's heals shifted into the merged frame and deduped across sources.
    //
    // Time alignment: subtract each source's offset (FightMerger.AdjustTime's constant-offset
    // term). The per-cluster drift correction FightMerger also applies is intentionally
    // omitted — it's a sub-fight refinement and heals are sliced by whole-fight windows.
    //
    // Dedup: the same real heal observed in two logs collapses via a
    // (rounded time, healer, healed, amount, type, spell) key, so a heal seen by N sources
    // counts once. Heals unique to one source (the common case for cross-healer comparison)
    // all survive. Dedup is global across sources but never collapses two records from the
    // same source (a source's own log doesn't double-record an event), so genuinely distinct
    // heals are preserved.
    //
    // Per-source input order is preserved; callers feed the result into a RecordsStore, which
    // re-sorts by time on insert.
    internal static IEnumerable<(double Time, HealRecord Record)> Merge(
      IEnumerable<(IEnumerable<(double Time, HealRecord Record)> Heals, double Offset)> sources)
    {
      // Keys contributed by EARLIER sources. A heal is dropped only if a prior source already
      // emitted the same key — never against keys from its own source, so a log's own distinct
      // (but identically-keyed) heals all survive.
      var seen = new HashSet<(long, string, string, uint, string, string)>();
      foreach (var (heals, offset) in sources)
      {
        var thisSource = new HashSet<(long, string, string, uint, string, string)>();
        foreach (var (time, record) in heals)
        {
          var adjusted = time - offset;
          var key = ((long)Math.Round(adjusted), record.Healer, record.Healed, record.Total, record.Type, record.SubType);
          if (seen.Contains(key))
          {
            continue;
          }
          thisSource.Add(key);
          yield return (adjusted, record);
        }
        seen.UnionWith(thisSource);
      }
    }
  }
}
