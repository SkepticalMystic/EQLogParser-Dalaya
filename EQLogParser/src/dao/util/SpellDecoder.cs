using System.Collections.Generic;

namespace EQLogParser
{
  // Shared decode helpers for spell metadata fields (ClassMask, Skill, Target, Resist, SPA names).
  // Extracted from SpellDetailsPopup so the Spell Browser and other surfaces can reuse
  // them without taking a dependency on the UI layer.
  internal static class SpellDecoder
  {
    // showLevel=true  → "NEC/65  WIZ/65" (popup details view)
    // showLevel=false → "NEC  WIZ"        (browser grid column)
    // classLevels: optional 16-element per-class level array from SpellData.ClassLevels;
    // falls back to the scalar `level` when null (older spells.txt or single-class spells).
    internal static string DecodeClasses(ushort mask, byte level, bool showLevel = true, byte[] classLevels = null)
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
          if (showLevel)
          {
            var classLevel = (classLevels != null && classLevels.Length == 16) ? classLevels[i] : level;
            parts.Add($"{names[i]}/{classLevel}");
          }
          else
          {
            parts.Add(names[i]);
          }
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

    // Short human-readable label for a Spell Affect (SPA) number, used to populate
    // the Spell Browser "Has Effect" dropdown. Covers all SPAs that SpellEffectDecoder
    // handles (cases 0–200) plus a numeric fallback for anything else.
    internal static string GetSpaName(int spa) => spa switch
    {
      0   => "Hit Points (HP)",
      1   => "AC",
      2   => "ATK",
      3   => "Movement Speed",
      4   => "STR",
      5   => "DEX",
      6   => "AGI",
      7   => "STA",
      8   => "INT",
      9   => "WIS",
      10  => "CHA",
      11  => "Melee Haste",
      12  => "Invisibility",
      13  => "See Invisible",
      14  => "Enduring Breath",
      15  => "Current Mana",
      18  => "Pacify",
      19  => "Faction",
      20  => "Blind",
      21  => "Stun",
      22  => "Charm",
      23  => "Fear",
      24  => "Endurance Loss",
      25  => "Bind",
      26  => "Gate",
      27  => "Dispel",
      28  => "Invis to Undead",
      29  => "Invis to Animals",
      30  => "Aggro Radius",
      31  => "Mez",
      32  => "Summon Item",
      33  => "Summon Pet",
      35  => "Disease Counter",
      36  => "Poison Counter",
      40  => "Invulnerability",
      41  => "Destroy Target",
      42  => "Shadowstep",
      44  => "Stacking Marker",
      46  => "Fire Resist",
      47  => "Cold Resist",
      48  => "Poison Resist",
      49  => "Disease Resist",
      50  => "Magic Resist",
      52  => "Sense Undead",
      53  => "Sense Summoned",
      54  => "Sense Animal",
      55  => "Absorb Damage",
      56  => "True North",
      57  => "Levitate",
      58  => "Illusion",
      59  => "Damage Shield",
      61  => "Identify",
      63  => "Memory Blur",
      64  => "Stun and Spin",
      65  => "Infravision",
      66  => "Ultravision",
      67  => "Eye of Zomm",
      68  => "Reclaim Pet",
      69  => "Max Hit Points",
      71  => "Summon Pet (v2)",
      73  => "Bind Sight",
      74  => "Feign Death",
      75  => "Voice Graft",
      76  => "Sentinel",
      77  => "Locate Corpse",
      78  => "Absorb Magical Damage",
      79  => "Current HP (Instant)",
      81  => "Resurrect",
      82  => "Summon Player",
      83  => "Teleport",
      84  => "Gravity Flux",
      85  => "Add Proc",
      86  => "Social Radius",
      87  => "Magnification",
      88  => "Evacuate",
      89  => "Player Size",
      91  => "Summon Corpse",
      92  => "Hate",
      93  => "Stop Rain",
      94  => "Cancel if Combat",
      95  => "Sacrifice",
      96  => "Silence",
      97  => "Max Mana",
      98  => "Melee Haste v2",
      99  => "Root",
      100 => "HP Repeating (HoT/DoT)",
      101 => "HP Bonus (7500)",
      102 => "Fear Immunity",
      103 => "Summon Pet (v3)",
      104 => "Translocate",
      105 => "Inhibit Gate",
      106 => "Summon Warder",
      107 => "Pet Enhancement",
      108 => "Summon Familiar",
      109 => "Summon Item (v2)",
      110 => "Archery Modifier",
      111 => "All Resists",
      112 => "Effective Casting Level",
      113 => "Summon Mount",
      114 => "Agro Modifier",
      115 => "Reset Hunger",
      116 => "Curse Counter",
      117 => "Scramble Chat",
      118 => "Singing Skill",
      119 => "Melee Haste v3 (Over Cap)",
      120 => "Spell Damage Taken",
      121 => "Reverse Damage Shield",
      123 => "Stacking: Group",
      124 => "Damage/Effect Boost",
      125 => "Heal/Crit Boost",
      126 => "Charm/Mez Duration",
      127 => "Casting Time",
      128 => "Spell Duration",
      129 => "Spell Range",
      130 => "Spell/Bash Hate",
      131 => "Reagent Chance",
      132 => "Mana Cost",
      134 => "Limit: Max Level",
      135 => "Limit: Resist",
      136 => "Limit: Target",
      137 => "Limit: Effect",
      138 => "Limit: Type",
      139 => "Limit: Spell",
      140 => "Limit: Min Duration",
      141 => "Limit: Max Duration",
      142 => "Limit: Min Level",
      143 => "Limit: Min Cast Time",
      144 => "Damage Shield Spell",
      145 => "Fly Mode",
      146 => "Electricity Resist",
      147 => "Current HP (Instant v2)",
      148 => "Stacking: Block",
      149 => "Stacking: Overwrite",
      150 => "Death Save",
      151 => "Curse",
      152 => "Swarm Pet",
      153 => "Autocast",
      154 => "Dispel Effect",
      199 => "Hate Transfer",
      200 => "Hate per Tick",
      _   => $"SPA {spa}"
    };
  }
}
