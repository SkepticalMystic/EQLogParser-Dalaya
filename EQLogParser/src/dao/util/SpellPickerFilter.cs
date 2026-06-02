using System;
using System.Collections.Generic;
using System.Linq;

namespace EQLogParser
{
  // Pure, UI-independent filtering for the trigger SpellPickerDialog. Kept apart
  // from the dialog so the search/class/category logic is unit-testable without
  // spinning up WPF — mirrors the HotEffectivenessAggregator pure-function
  // pattern. Phases 2 (class) and 3 (category) are optional parameters, so each
  // filter dimension is independent. See memory project-trigger-spell-picker.
  internal static class SpellPickerFilter
  {
    // classFlag == null  → no class filter (all classes).
    // category null/empty → no category filter (all categories).
    // searchText matches Name / LandsOnYou / LandsOnOther / WearOff as a
    // case-insensitive substring. Results are ordered by spell name.
    internal static IEnumerable<SpellData> Apply(
      IEnumerable<SpellData> spells, string searchText, SpellClass? classFlag, string category)
    {
      if (spells == null)
      {
        return [];
      }

      var query = spells;

      if (classFlag is { } cls)
      {
        var bit = (int)cls;
        query = query.Where(s => (s.ClassMask & bit) != 0);
      }

      if (!string.IsNullOrEmpty(category))
      {
        query = query.Where(s => !string.IsNullOrEmpty(s.Category) &&
          s.Category.Split(';').Contains(category, StringComparer.OrdinalIgnoreCase));
      }

      if (!string.IsNullOrWhiteSpace(searchText))
      {
        var term = searchText.Trim();
        query = query.Where(s =>
          ContainsCi(s.Name, term) || ContainsCi(s.LandsOnYou, term) ||
          ContainsCi(s.LandsOnOther, term) || ContainsCi(s.WearOff, term));
      }

      return query.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase);
    }

    private static bool ContainsCi(string haystack, string needle) =>
      !string.IsNullOrEmpty(haystack) && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
  }
}
