using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace EQLogParser
{
  public partial class SpellDetailsPopup : UserControl
  {
    private record EffectLine(string Label, string Text, bool IsStacking);
    private record DetailRow(string Label, string Value);

    // [Spell N] appears in proc/autocast/dispel effect text; replace with the spell name when known.
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
      detailsList.ItemsSource = BuildDetails(spell);

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

      var lands = spell.LandsOnYou?.Trim();
      var landsOther = spell.LandsOnOther?.Trim();
      var wearOff = spell.WearOff?.Trim();
      if (!string.IsNullOrEmpty(lands) || !string.IsNullOrEmpty(landsOther) || !string.IsNullOrEmpty(wearOff))
      {
        SetFlavor(landsOnText, lands, "On you: ");
        SetFlavor(landsOnOtherText, landsOther, "On target: ");
        SetFlavor(wearOffText, wearOff, "Wears off: ");
        flavorPanel.Visibility = Visibility.Visible;
      }
      else
      {
        flavorPanel.Visibility = Visibility.Collapsed;
      }
    }

    private static void SetFlavor(System.Windows.Controls.TextBlock block, string text, string prefix)
    {
      if (string.IsNullOrEmpty(text))
      {
        block.Visibility = Visibility.Collapsed;
      }
      else
      {
        block.Text = prefix + text;
        block.Visibility = Visibility.Visible;
      }
    }

    private static List<DetailRow> BuildDetails(SpellData spell)
    {
      var rows = new List<DetailRow>();

      rows.Add(new("ID", spell.Id));

      // ClassMask stores which classes can cast; Level is the minimum across all eligible classes.
      // For single-class spells this is exact. Multi-class spells share the minimum level label.
      var classes = SpellDecoder.DecodeClasses(spell.ClassMask, spell.Level);
      if (!string.IsNullOrEmpty(classes))
      {
        rows.Add(new("Classes", classes));
      }

      var skill = SpellDecoder.DecodeSkill(spell.Skill);
      if (!string.IsNullOrEmpty(skill))
      {
        rows.Add(new("Skill", skill));
      }

      if (!string.IsNullOrEmpty(spell.Category))
      {
        rows.Add(new("Category", spell.Category.Replace(";", ", ")));
      }

      var target = SpellDecoder.DecodeTarget(spell.Target);
      if (!string.IsNullOrEmpty(target))
      {
        rows.Add(new("Target", target));
      }

      var resist = SpellDecoder.DecodeResist(spell.Resist);
      if (!string.IsNullOrEmpty(resist))
      {
        var resistText = spell.ResistMod != 0
          ? $"{resist} ({(spell.ResistMod > 0 ? "+" : "")}{spell.ResistMod})"
          : resist;
        rows.Add(new("Resist", resistText));
      }

      if (spell.Mana > 0)
      {
        rows.Add(new("Mana", spell.Mana.ToString(CultureInfo.InvariantCulture)));
      }

      if (spell.Range > 0)
      {
        rows.Add(new("Range", $"{spell.Range}'"));
      }

      if (spell.CastingTimeMs > 0)
      {
        rows.Add(new("Cast", string.Format(CultureInfo.InvariantCulture, "{0:0.##}s", spell.CastingTimeMs / 1000.0)));
      }

      if (spell.RecastTimeMs > 0)
      {
        rows.Add(new("Recast", string.Format(CultureInfo.InvariantCulture, "{0:0.##}s", spell.RecastTimeMs / 1000.0)));
      }

      if (spell.Duration > 0)
      {
        var ticks = spell.Duration / 6;
        rows.Add(new("Duration", $"{spell.Duration}s ({ticks} {(ticks == 1 ? "tick" : "ticks")})"));
      }

      if (spell.SongWindow)
      {
        rows.Add(new("Type", "Song"));
      }

      if (spell.RecourseID > 0 && EQDataStore.Instance.GetSpellById(spell.RecourseID) is { } recourse)
      {
        rows.Add(new("Recourse", recourse.Name));
      }

      return rows;
    }

    private static string ResolveSpellRefs(string text) =>
      SpellRefRegex.Replace(text, m =>
        int.TryParse(m.Groups[1].Value, out var id) && EQDataStore.Instance.GetSpellById(id) is { } s
          ? s.Name
          : m.Value);
  }
}
