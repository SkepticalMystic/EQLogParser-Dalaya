using System.Collections.Generic;

namespace EQLogParser
{
  // Shared decode helpers for spell metadata fields (ClassMask, Skill, Target, Resist).
  // Extracted from SpellDetailsPopup so the Spell Browser and other surfaces can reuse
  // them without taking a dependency on the UI layer.
  internal static class SpellDecoder
  {
    // showLevel=true  → "NEC/65  WIZ/65" (popup details view)
    // showLevel=false → "NEC  WIZ"        (browser grid column)
    internal static string DecodeClasses(ushort mask, byte level, bool showLevel = true)
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
          parts.Add(showLevel ? $"{names[i]}/{level}" : names[i]);
        }
      }
      return string.Join("  ", parts);
    }

    internal static string DecodeSkill(int skill) => skill switch
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

    internal static string DecodeTarget(byte target) => target switch
    {
      1  => "AE (LoS)",         2  => "Caster AE",          3  => "Group",
      4  => "PBAE",             5  => "Single",              6  => "Self",
      8  => "Target AE",        14 => "Pet",                 36 => "PBAE (Players)",
      38 => "Pet",              40 => "Nearby Players AE",   41 => "Target Group",
      42 => "Directional AE",   45 => "Ring AE",
      _ => target == 0 ? "" : $"Target {target}"
    };

    internal static string DecodeResist(SpellResist resist) => resist switch
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
  }
}
