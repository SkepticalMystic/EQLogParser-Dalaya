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
      var classes = DecodeClasses(spell.ClassMask, spell.Level);
      if (!string.IsNullOrEmpty(classes))
      {
        rows.Add(new("Classes", classes));
      }

      var skill = DecodeSkill(spell.Skill);
      if (!string.IsNullOrEmpty(skill))
      {
        rows.Add(new("Skill", skill));
      }

      if (!string.IsNullOrEmpty(spell.Category))
      {
        rows.Add(new("Category", spell.Category.Replace(";", ", ")));
      }

      var target = DecodeTarget(spell.Target);
      if (!string.IsNullOrEmpty(target))
      {
        rows.Add(new("Target", target));
      }

      var resist = DecodeResist(spell.Resist);
      if (!string.IsNullOrEmpty(resist))
      {
        rows.Add(new("Resist", resist));
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

    private static string DecodeClasses(ushort mask, byte level)
    {
      if (mask == 0)
      {
        return "";
      }
      string[] names = ["WAR", "CLR", "PAL", "RNG", "SHD", "DRU", "MNK", "BRD",
                         "ROG", "SHM", "NEC", "WIZ", "MAG", "ENC", "BST", "BER"];
      var parts = new List<string>();
      for (var i = 0; i < names.Length; i++)
      {
        if ((mask & (1 << i)) != 0)
        {
          parts.Add($"{names[i]}/{level}");
        }
      }
      return string.Join("  ", parts);
    }

    private static string DecodeSkill(int skill) => skill switch
    {
      -1 => "",
      0  => "1H Blunt",       1  => "1H Slashing",    2  => "2H Blunt",
      3  => "2H Slashing",    4  => "Abjuration",      5  => "Alteration",
      6  => "Apply Poison",   7  => "Archery",          8  => "Backstab",
      9  => "Bind Wound",     10 => "Bash",             12 => "Brass Instruments",
      13 => "Channeling",     14 => "Conjuration",      18 => "Divination",
      20 => "Double Attack",  21 => "Dragon Punch",     22 => "Dual Wield",
      23 => "Eagle Strike",   24 => "Evocation",        25 => "Feign Death",
      27 => "Flying Kick",    29 => "Hand to Hand",     31 => "Kick",
      32 => "Meditate",       33 => "Mend",             37 => "Pierce",
      39 => "Round Kick",     42 => "Singing",          49 => "Stringed Instruments",
      51 => "Throwing",       52 => "Tiger Claw",       54 => "Wind Instruments",
      57 => "Percussion Instruments", 58 => "Intimidation", 59 => "Berserking",
      60 => "Taunting",
      _ => $"Skill {skill}"
    };

    private static string DecodeTarget(byte target) => target switch
    {
      1  => "AE (LoS)",         2  => "Caster AE",          3  => "Group",
      4  => "PBAE",             5  => "Single",              6  => "Self",
      8  => "Target AE",        14 => "Pet",                 36 => "PBAE (Players)",
      38 => "Pet",              40 => "Nearby Players AE",   41 => "Target Group",
      42 => "Directional AE",   45 => "Ring AE",
      _ => target == 0 ? "" : $"Target {target}"
    };

    private static string DecodeResist(SpellResist resist) => resist switch
    {
      SpellResist.Undefined    => "",
      SpellResist.Reflected    => "Reflected",
      SpellResist.Unresistable => "Unresistable",
      SpellResist.Magic        => "Magic",
      SpellResist.Fire         => "Fire",
      SpellResist.Cold         => "Cold",
      SpellResist.Poison       => "Poison",
      SpellResist.Disease      => "Disease",
      SpellResist.Lowest       => "Chromatic",
      SpellResist.Average      => "Prismatic",
      SpellResist.Physical     => "Physical",
      SpellResist.Corruption   => "Corruption",
      _ => ""
    };

    private static string ResolveSpellRefs(string text) =>
      SpellRefRegex.Replace(text, m =>
        int.TryParse(m.Groups[1].Value, out var id) && EQDataStore.Instance.GetSpellById(id) is { } s
          ? s.Name
          : m.Value);
  }
}
