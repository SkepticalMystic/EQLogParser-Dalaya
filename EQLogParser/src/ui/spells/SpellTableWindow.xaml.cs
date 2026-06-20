using Syncfusion.UI.Xaml.Grid;
using System.Collections.Generic;
using System.Dynamic;
using System.Globalization;
using System.Linq;
using System.Windows.Controls;

namespace EQLogParser
{
  public partial class SpellTableWindow : UserControl
  {
    public SpellTableWindow()
    {
      InitializeComponent();
    }

    internal void Init(IList<SpellData> spells, bool compareMode)
    {
      if (spells == null || spells.Count == 0) return;

      titleLabel.Content = compareMode
        ? $"Compare Spells — {spells.Count}"
        : $"Spell Table — {spells.Count:N0} spells";
      countLabel.Text = $"{spells.Count:N0} spell{(spells.Count == 1 ? "" : "s")}";

      var (rows, occupiedSlots) = BuildRows(spells, compareMode);
      BuildColumns(occupiedSlots);
      dataGrid.ItemsSource = rows;
    }

    private void BuildColumns(List<int> occupiedSlots)
    {
      dataGrid.Columns.Clear();

      void Add(string mapping, string header, double width) =>
        dataGrid.Columns.Add(new GridTextColumn { MappingName = mapping, HeaderText = header, Width = width });

      Add("Name",     "Name",     220);
      Add("ID",       "ID",        60);
      Add("Classes",  "Classes",  180);
      Add("Skill",    "Skill",    110);
      Add("Cast",     "Cast",      70);
      Add("Recast",   "Recast",    70);
      Add("Duration", "Duration",  85);
      Add("Resist",   "Resist",   100);
      Add("Target",   "Target",   110);

      foreach (var n in occupiedSlots)
        Add($"Slot{n}", $"Slot {n}", 200);
    }

    private static (List<IDictionary<string, object>> rows, List<int> occupiedSlots) BuildRows(
      IList<SpellData> spells, bool compareMode)
    {
      var decoded = spells.ToDictionary(s => s,
        s => ParseSlots(SpellEffectDecoder.Describe(EQDataStore.Instance.GetSpellEffects(s.Id), s.Level, s.Level)));

      var occupiedSlots = compareMode
        ? Enumerable.Range(1, 12).Where(n => decoded.Values.Any(d => d.ContainsKey(n))).ToList()
        : [];

      var rows = new List<IDictionary<string, object>>();
      foreach (var spell in spells)
      {
        var resist = SpellDecoder.DecodeResist(spell.Resist);
        var row = (IDictionary<string, object>)new ExpandoObject();

        row["Name"]     = spell.Name;
        row["ID"]       = spell.Id;
        row["Classes"]  = SpellDecoder.DecodeClasses(spell.ClassMask, spell.Level, showLevel: false);
        row["Skill"]    = SpellDecoder.DecodeSkill(spell.Skill);
        row["Cast"]     = spell.CastingTimeMs > 0
                            ? string.Format(CultureInfo.InvariantCulture, "{0:0.##}s", spell.CastingTimeMs / 1000.0)
                            : "Instant";
        row["Recast"]   = spell.RecastTimeMs > 0
                            ? string.Format(CultureInfo.InvariantCulture, "{0:0.##}s", spell.RecastTimeMs / 1000.0)
                            : "";
        row["Duration"] = spell.Duration > 0 ? $"{spell.Duration}s" : "";
        row["Resist"]   = spell.ResistMod != 0
                            ? $"{resist} ({(spell.ResistMod > 0 ? "+" : "")}{spell.ResistMod})"
                            : resist;
        row["Target"]   = SpellDecoder.DecodeTarget(spell.Target);

        var slotMap = decoded[spell];
        foreach (var n in occupiedSlots)
          row[$"Slot{n}"] = slotMap.TryGetValue(n, out var text) ? text : "";

        rows.Add(row);
      }

      return (rows, occupiedSlots);
    }

    // Parse "Slot N: text" lines (output of SpellEffectDecoder.Describe) into a
    // dict keyed by 1-based slot number.
    private static Dictionary<int, string> ParseSlots(List<string> lines)
    {
      var map = new Dictionary<int, string>();
      foreach (var line in lines)
      {
        var colon = line.IndexOf(": ", System.StringComparison.Ordinal);
        if (colon > 5
            && line.StartsWith("Slot ", System.StringComparison.Ordinal)
            && int.TryParse(line[5..colon], out var n))
        {
          map[n] = line[(colon + 2)..];
        }
      }
      return map;
    }
  }
}
