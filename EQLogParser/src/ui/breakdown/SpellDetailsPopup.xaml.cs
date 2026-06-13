using System.Collections.Generic;
using System.Globalization;
using System.Windows.Controls;

namespace EQLogParser
{
  // Read-only popout showing a spell's decoded effect slots (via SpellEffectDecoder) plus
  // basic metadata. Opened from a grid's "Spell Details" context-menu item, mirroring how
  // HotEffectivenessTable is surfaced (SyncFusionUtil.OpenWindow). This is the first UI
  // surface for the SPA decoder — see memory project_spell_tooltips.
  public partial class SpellDetailsPopup : UserControl
  {
    public SpellDetailsPopup()
    {
      InitializeComponent();
    }

    // Render the spell's effects. Decoded at the spell's own min castable level (single value;
    // most Dalaya buffs/heals/nukes use a flat formula so level doesn't change the numbers).
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
      effectsList.ItemsSource = lines.Count > 0
        ? lines
        : new List<string> { "No decoded effect data for this spell." };
    }

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
