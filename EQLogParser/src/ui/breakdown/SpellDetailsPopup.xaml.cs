using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Controls;

namespace EQLogParser
{
  public partial class SpellDetailsPopup : UserControl
  {
    // Separates the "Slot N" label from the effect description so the popup can
    // render them in two aligned columns and dim stacking lines independently.
    private record EffectLine(string Label, string Text, bool IsStacking);

    // [Spell N] appears in proc/autocast/dispel effect text when the referenced
    // spell id is encoded numerically. Replace with the spell name when known.
    private static readonly Regex SpellRefRegex = new(@"\[Spell (\d+)\]", RegexOptions.Compiled);

    public SpellDetailsPopup()
    {
      InitializeComponent();
    }

    internal void Init(SpellData spell)
    {
      if (spell == null)
      {
        return;
      }

      titleLabel.Content = spell.Name;
      metaText.Text = BuildMeta(spell);

      var effects = EQDataStore.Instance.GetSpellEffects(spell.Id);
      var lines = SpellEffectDecoder.Describe(effects, spell.Level, spell.Level);

      if (lines.Count > 0)
      {
        effectsList.ItemsSource = lines.Select(line =>
        {
          var sep = line.IndexOf(": ", System.StringComparison.Ordinal);
          var label = sep >= 0 ? line[..sep] : "";
          var text = sep >= 0 ? line[(sep + 2)..] : line;
          text = ResolveSpellRefs(text);
          return new EffectLine(label, text, text.StartsWith("Stacking", System.StringComparison.Ordinal));
        }).ToList();
      }
      else
      {
        effectsList.ItemsSource = new List<EffectLine> { new("", "No decoded effect data for this spell.", false) };
      }
    }

    private static string ResolveSpellRefs(string text) =>
      SpellRefRegex.Replace(text, m =>
        int.TryParse(m.Groups[1].Value, out var id) && EQDataStore.Instance.GetSpellById(id) is { } s
          ? s.Name
          : m.Value);

    private static string BuildMeta(SpellData spell)
    {
      var parts = new List<string>();
      if (spell.Level is > 0 and < 255)
      {
        parts.Add($"Level {spell.Level}");
      }
      if (!string.IsNullOrEmpty(spell.Category))
      {
        parts.Add(spell.Category.Replace(";", ", "));
      }
      if (spell.CastingTimeMs > 0)
      {
        parts.Add(string.Format(CultureInfo.InvariantCulture, "Cast {0:0.##}s", spell.CastingTimeMs / 1000.0));
      }
      if (spell.RecastTimeMs > 0)
      {
        parts.Add(string.Format(CultureInfo.InvariantCulture, "Recast {0:0.##}s", spell.RecastTimeMs / 1000.0));
      }
      if (spell.Duration > 0)
      {
        parts.Add($"Duration {spell.Duration}s");
      }
      return string.Join("  •  ", parts);
    }
  }
}
