using System;
using System.Collections.Generic;
using System.Globalization;

namespace EQLogParser
{
  // Renders a spell's effect slots as wiki-style text (e.g. "Slot 2: Increase Max Hit Points
  // by 1150"), decoding the SPA + base/max/calc data already loaded from spell-effects.json.
  // Direct port of ngdeao/SoD-winspellparser SpellParser.ParseEffect (the case<spa> switch at
  // SpellParser.cs:1391-1808) plus its FormatCount/FormatPercent/CalcValueRange helpers.
  //
  // Phase 1 scope (see memory project_spell_tooltips):
  //  - SPA cases 0-200, the set SoD itself decodes, are ported faithfully. Dalaya-specific
  //    SPAs above 200 (e.g. 340, 383) and any other unhandled id fall to the same
  //    "Unknown Effect" descriptor SoD emits — honest, never throws.
  //  - Effects that reference SoD's parsed Extra text (teleport zone names, summoned pet/item
  //    names) or its large lookup enums (Items / SpellIllusion / SpellTarget / SpellEffect /
  //    SpellTargetRestrict) are rendered in a simplified numeric form, since we don't carry
  //    Extra and haven't ported those enums. Revisit if a consumer needs the names.
  //  - No UI surface yet; this is the decode layer consumers (tooltips, death recap, the
  //    in-app spell browser) will call.
  internal static class SpellEffectDecoder
  {
    // Render every populated slot of a spell as "Slot N: <text>" lines (N is 1-based to match
    // the wiki / SoD). Empty / no-op slots are skipped. level/minLevel drive formula scaling
    // and the "(Lmin) to (Lmax)" annotation; pass the spell's max and min castable levels for
    // a nominal description, or equal values to show a single level.
    internal static List<string> Describe(SpellEffects effects, int level, int minLevel)
    {
      var lines = new List<string>();
      if (effects?.Slots == null)
      {
        return lines;
      }

      var durationTicks = SpellFormula.CalcDuration(effects.DurationCalc, effects.DurationBase, level);
      // Mirror ComputeHotTickInfo: DurationCalc==0 zeroes the formula but Dalaya carries a real
      // DurationBase, so fall back to it for the "per tick" / range annotations.
      if (durationTicks == 0 && effects.DurationBase > 0)
      {
        durationTicks = effects.DurationBase;
      }

      foreach (var slot in effects.Slots)
      {
        var text = DescribeSlot(slot.Spa, slot.Base1, slot.Base2, slot.Max, slot.Calc, level, minLevel, durationTicks);
        if (!string.IsNullOrEmpty(text))
        {
          lines.Add($"Slot {slot.Slot + 1}: {text}");
        }
      }

      return lines;
    }

    // Describe a single effect slot, or null for an empty / no-op slot. Faithful port of
    // SpellParser.ParseEffect.
    internal static string DescribeSlot(int spa, int base1, int base2, int max, int calc, int level, int minLevel, int durationTicks)
    {
      // type 254 indicates an unused slot
      if (spa == 254)
      {
        return null;
      }

      // unused slots: zero-valued stat/HP placeholders
      if (base1 == 0 && base2 == 0 && max == 0 && (spa == 0 || spa == 1 || spa == 2 || spa == 5 || spa == 6 || spa == 10))
      {
        return null;
      }

      // many SPAs use a scaled value based on caster level
      var value = SpellFormula.CalcValue(calc, base1, max, 1, level);
      var minvalue = SpellFormula.CalcValue(calc, base1, max, 1, minLevel);
      var range = CalcValueRange(calc, base1, max, durationTicks, level);

      // some hp/mana/hate effects repeat each tick of the duration
      var repeating = durationTicks > 0 ? " per tick" : null;

      // some effects are capped at a max level
      var maxlevel = max > 0 ? string.Format(CultureInfo.InvariantCulture, " up to level {0}", max) : null;

      switch (spa)
      {
        case 0:
          if (base2 > 0)
          {
            return FormatCount("Hit Points", value) + repeating + range + " (if target type " + base2 + ")";
          }
          return FormatCount("Hit Points", value, minvalue, level, minLevel) + repeating + range;
        case 1:
          return value > 0
            ? FormatCount("AC", value * 300 / 1000, minvalue * 300 / 1000, level, minLevel)
            : FormatCount("AC", value, minvalue, level, minLevel);
        case 2:
          return FormatCount("ATK", value, minvalue, level, minLevel) + range;
        case 3:
          return FormatPercent("Movement Speed", value, minvalue, minLevel, level);
        case 4:
          return FormatCount("STR", value, minvalue, level, minLevel) + range;
        case 5:
          return FormatCount("DEX", value, minvalue, level, minLevel) + range;
        case 6:
          return FormatCount("AGI", value, minvalue, level, minLevel) + range;
        case 7:
          return FormatCount("STA", value, minvalue, level, minLevel) + range;
        case 8:
          return FormatCount("INT", value, minvalue, level, minLevel) + range;
        case 9:
          return FormatCount("WIS", value, minvalue, level, minLevel) + range;
        case 10:
          return FormatCount("CHA", value, minvalue, level, minLevel) + range;
        case 11:
          // base attack speed is 100: 85 = 15% slow, 130 = 30% haste
          return value > 100
            ? FormatPercent("Melee Haste", value - 100, minvalue - 100, minLevel, level)
            : FormatPercent("Attack Speed", value - 100);
        case 12:
          return base1 > 1 ? $"Invisibility (Enhanced {base1})" : "Invisibility (Unstable)";
        case 13:
          return base1 > 1 ? $"See Invisible (Enhanced {base1})" : "See Invisible";
        case 14:
          return "Enduring Breath";
        case 15:
          return FormatCount("Current Mana", value) + repeating + range;
        case 18:
          return "Pacify";
        case 19:
          return FormatCount("Faction", value);
        case 20:
          return "Blind";
        case 21:
          return string.Format(CultureInfo.InvariantCulture, "Stun for {0:0.##}s", base1 / 1000f) + maxlevel;
        case 22:
          return "Charm" + (base1 > 0 ? $" up to level {base1}" : null);
        case 23:
          return "Fear" + maxlevel;
        case 24:
          return FormatPercent("Endurance Loss", -value);
        case 25:
          return "Bind";
        case 26:
          return base1 > 1 ? "Gate to Secondary Bind" : "Gate";
        case 27:
          return $"Dispel ({value})";
        case 28:
          return "Invisibility to Undead";
        case 29:
          return "Invisibility to Animals";
        case 30:
          return $"Decrease Aggro Radius to {value}" + maxlevel;
        case 31:
          return "Mesmerize" + (base1 > 1 ? $" up to level {base1}" : maxlevel);
        case 32:
          // Summon item — SoD names it via the Items enum; we show the numeric id.
          return max > 1 ? $"Summon x {max}: [Item {base1}]" : $"Summon: [Item {base1}]";
        case 33:
          return "Summon Pet";
        case 35:
          return FormatCount("Disease Counter", value);
        case 36:
          return FormatCount("Poison Counter", value);
        case 40:
          return "Invulnerability";
        case 41:
          return "Destroy's Target" + (base1 > 0 ? $" (Maximum level {base1})" : null);
        case 42:
          return "Shadowstep";
        case 44:
          return $"Stacking: Delayed Heal Marker ({value})";
        case 46:
          return FormatCount("Fire Resist", value, minvalue, level, minLevel);
        case 47:
          return FormatCount("Cold Resist", value, minvalue, level, minLevel);
        case 48:
          return FormatCount("Poison Resist", value, minvalue, level, minLevel);
        case 49:
          return FormatCount("Disease Resist", value, minvalue, level, minLevel);
        case 50:
          return FormatCount("Magic Resist", value, minvalue, level, minLevel);
        case 52:
          return "Sense Undead";
        case 53:
          return "Sense Summoned";
        case 54:
          return "Sense Animal";
        case 55:
          return $"Absorb Damage: {value} points";
        case 56:
          return "True North";
        case 57:
          return "Levitate";
        case 58:
          // Illusion — SoD resolves the form via the SpellIllusion enum; we show the form id.
          return base2 > 0 ? $"Illusion: form {base1} ({max})" : $"Illusion: form {base1}";
        case 59:
          return FormatCount("Damage Shield", -value, -minvalue, level, minLevel);
        case 61:
          return "Identify Item";
        case 63:
          return value + 40 < 100 ? $"Memory Blur ({value + 40}% Chance)" : "Memory Blur";
        case 64:
          return string.Format(CultureInfo.InvariantCulture, "Stun and Spin for {0:0.##}s", base1 / 1000f) + maxlevel;
        case 65:
          return "Infravision";
        case 66:
          return "Ultravision";
        case 67:
          return "Eye of Zomm";
        case 68:
          return "Reclaim Pet";
        case 69:
          return FormatCount("Max Hit Points", value, minvalue, level, minLevel) + range;
        case 71:
          return "Summon Pet";
        case 73:
          return "Bind Sight";
        case 74:
          return value < 100 ? $"Feign Death ({value}% Chance)" : "Feign Death";
        case 75:
          return "Voice Graft";
        case 76:
          return "Sentinel";
        case 77:
          return "Locate Corpse";
        case 78:
          return $"Absorb Magical Damage: {value} hit points";
        case 79:
          // delta hp for heal/nuke, non repeating
          if (base2 > 0)
          {
            return FormatCount("Current Hit Points", value) + range + " (if target type " + base2 + ")";
          }
          return FormatCount("Current Hit Points", value) + range;
        case 81:
          return $"Resurrect with {value}% XP";
        case 82:
          return "Summon Player";
        case 83:
          return "Teleport";
        case 84:
          return "Gravity Flux";
        case 85:
          return base2 != 0
            ? $"Add Proc: [Spell {base1}] with {base2}% Rate Mod"
            : $"Add Proc: [Spell {base1}]";
        case 86:
          return $"Decrease Social Radius to {value}" + maxlevel;
        case 87:
          return FormatPercent("Magnification", value);
        case 88:
          return "Evacuate";
        case 89:
          return FormatPercent("Player Size", base1 - 100);
        case 91:
          return $"Summon Corpse up to level {base1}";
        case 92:
          return FormatCount("Hate", value);
        case 93:
          return "Stop Rain";
        case 94:
          return "Cancel if Combat Initiated";
        case 95:
          return "Sacrifice";
        case 96:
          return "Silence";
        case 97:
          return FormatCount("Max Mana", value);
        case 98:
          return FormatPercent("Melee Haste v2", value - 100);
        case 99:
          return "Root";
        case 100:
          // heal over time
          if (base2 > 0)
          {
            return FormatCount("Current HP", value) + repeating + range + " (if target type " + base2 + ")";
          }
          return FormatCount("Current HP", value) + repeating + range;
        case 101:
          return "Increase Current HP by 7500";
        case 102:
          return "Fear Immunity";
        case 103:
          return "Summon Pet";
        case 104:
          return "Translocate";
        case 105:
          return "Inhibit Gate";
        case 106:
          return "Summon Warder";
        case 107:
          return max switch
          {
            0 => FormatPercent("Maximum Pet Hit Points", base1),
            1 => FormatPercent("Maximum Pet Damage", base1 / 2),
            _ => $"Unknown Pet Enhancement: {max}",
          };
        case 108:
          return "Summon Familiar";
        case 109:
          return $"Summon: [Item {base1}]";
        case 110:
          return calc switch
          {
            200 => FormatPercent("Archery Accuracy", base1),
            400 => FormatPercent("Archery Damage", base1),
            _ => $"Unknown Archery Modifier: {calc}",
          };
        case 111:
          return FormatCount("All Resists", value);
        case 112:
          return FormatCount("Effective Casting Level", value);
        case 113:
          return "Summon Mount";
        case 114:
          return FormatPercent("Agro", value - 100);
        case 115:
          return "Reset Hunger Counter";
        case 116:
          return FormatCount("Curse Counter", value);
        case 117:
          return "Scrambles Chat";
        case 118:
          return FormatCount("Singing Skill", value);
        case 119:
          return FormatPercent("Melee Haste v3 (Above Cap)", value - 100);
        case 120:
          return FormatPercent("Spell Damage Taken", base1);
        case 121:
          return FormatCount("Reverse Damage Shield", -value);
        case 123:
          return $"Stacking: Group {base1}";
        case 124:
          return max switch
          {
            -5 => FormatPercent("Disease Elemental DD and DoT Damage", base1),
            -4 => FormatPercent("Poison Elemental DD and DoT Damage", base1),
            -3 => FormatPercent("Cold Elemental DD and DoT Damage", base1),
            -2 => FormatPercent("Fire Elemental DD and DoT Damage", base1),
            -1 => FormatPercent("Magic Elemental DD and DoT Damage", base1),
            0 => FormatPercent("DD Damage", base1),
            1 => FormatPercent("DD and DoT Crit Rate", base1),
            2 => FormatPercent("Bane DD and DoT Damage", base1),
            3 => FormatPercent("DoT Damage", base1),
            10 => FormatPercent("Slow Percentage", base1),
            _ => $"Unknown Effect Boost: {max}",
          };
        case 125:
          return max switch
          {
            0 => FormatPercent("Strength of Healing Spells", base1),
            1 => FormatPercent("Chance of Critical Heals", base1),
            _ => $"Unknown Healing spell Boost: {max}",
          };
        case 126:
          return FormatCount("Charm Duration", base1) + $" tick{(base1 > 1 ? "s" : "")}" + " and " + FormatPercent("Mez Duration", base1 * 10);
        case 127:
          return FormatPercent("Casting Time", -base1).Replace("Increase", "Slow").Replace("Decrease", "Improve");
        case 128:
          return FormatPercent("Spell Duration", base1);
        case 129:
          return FormatPercent("Spell Range", base1);
        case 130:
          return FormatPercent("Spell and Bash Hate", base1, base2);
        case 131:
          return FormatPercent("Chance of Using Reagent", -base1);
        case 132:
          return FormatPercent("Mana Cost", -value);
        case 134:
          return $"Limit Max Level: {base1} (lose {(base2 == 0 ? 100 : base2)}% per level)";
        case 135:
          return $"Limit Resist: {(base1 >= 0 ? "" : "Exclude ")}{(SpellResist)Math.Abs(base1)}";
        case 136:
          return $"Limit Target: {(base1 >= 0 ? "" : "Exclude ")}type {Math.Abs(base1)}";
        case 137:
          return $"Limit Effect: {(base1 >= 0 ? "" : "Exclude ")}SPA {Math.Abs(base1)}";
        case 138:
          return $"Limit Type: {(base1 == 0 ? "Detrimental" : "Beneficial")}";
        case 139:
          return $"Excludes Spell: {(base1 >= 0 ? "" : "Exclude ")}[Spell {Math.Abs(base1)}]";
        case 140:
          return $"Limit Min Duration: {base1 * 6}s";
        case 141:
          return "Limit Max Duration: 0s";
        case 142:
          return $"Limit Min Level: {base1}";
        case 143:
          return string.Format(CultureInfo.InvariantCulture, "Limit Min Casting Time: {0}s", base1 / 1000f);
        case 144:
          return $"Damage Shield: Causes [Spell {base1}]";
        case 145:
          return "Fly Mode";
        case 146:
          return FormatCount("Electricity Resist", value);
        case 147:
          return FormatCount("Current HP", base1);
        case 148:
          return $"Stacking: Block new spell if slot {calc % 100} is 'SPA {base1}' and < {max}";
        case 149:
          return $"Stacking: Overwrite existing spell if slot {calc % 100} is 'SPA {base1}' and < {max}";
        case 150:
          return $"Death Save ({base1}%)" + (max > 0 ? $" with {max} Heal" : "");
        case 151:
          return base1 switch
          {
            1 => "Melee Automatically Critical Hits",
            2 => "Reduce Spell Damage Coming from Target by 15%",
            3 => "Spells Automatically Critical Hit",
            4 => "Melee Automatically Critical Hits (constructs only)",
            _ => $"Unknown Curse: {base1}",
          };
        case 152:
          return base1 > 1 ? $"Swarm Pet (x{base1})" : "Swarm Pet";
        case 153:
          return $"Autocast: [Spell {base1}]";
        case 154:
          return $"Dispell Effect: [Spell {base1}]";
        case 199:
          return FormatCount("Hate Transfer", base1);
        case 200:
          return FormatCount("Hate", base1) + " per tick";

        // --- Dalaya-specific SPAs (not in SoD-winspellparser) ---
        case 203:
          return "Soulbond";
        case 206:
          return $"Force Aggro ({base1} hate)";
        case 220:
          return base1 == 0 ? "Revive Pet" : "Suspend Pet";
        case 234:
          return FormatCount("Fortification", base1);
        case 289:
          return $"On Expiry: Cast [Spell {base1}]";
        case 322:
          return "Home Gate";
        case 328:
          return $"Delay Death: Survive up to {base1} negative HP";
        case 340:
          return max > 0
            ? $"Add Spell Proc: [Spell {base1}] ({max}% Chance)"
            : $"Add Spell Proc: [Spell {base1}]";
        case 383:
          return max > 0
            ? $"Trigger: Cast [Spell {base1}] when [Spell {max}] is used"
            : $"Trigger: Cast [Spell {base1}]";
        case 460:
          return "Relic: Savior Effect";
        case 461:
          return max > 0
            ? $"Add Proc: [Spell {max}] ({base1}% Chance)"
            : $"Add Proc: ({base1}% Chance)";
        case 463:
          return $"Add Block Proc: [Spell {base1}]";
      }

      return string.Format(CultureInfo.InvariantCulture,
        "Unknown Effect: {0} Base1={1} Base2={2} Max={3} Calc={4} Value={5}", spa, base1, base2, max, calc, value);
    }

    // --- Format helpers, ported from SpellParser.cs (the Spell.Format* / CalcValueRange set) ---

    private static string FormatCount(string name, int value) =>
      string.Format(CultureInfo.InvariantCulture, "{0} {1} by {2}", value < 0 ? "Decrease" : "Increase", name, Math.Abs(value));

    private static string FormatCount(string name, int value, int minvalue, int level, int minlevel)
    {
      if (value != minvalue)
      {
        return string.Format(CultureInfo.InvariantCulture, "{0} {1} by {2} (L{3}) to {4} (L{5})",
          value < 0 ? "Decrease" : "Increase", name, Math.Abs(minvalue), minlevel, Math.Abs(value), level);
      }
      return string.Format(CultureInfo.InvariantCulture, "{0} {1} by {2}", value < 0 ? "Decrease" : "Increase", name, Math.Abs(value));
    }

    private static string FormatPercent(string name, float value) => FormatPercent(name, value, value);

    private static string FormatPercent(string name, float min, float max)
    {
      if (Math.Abs(min) > Math.Abs(max))
      {
        (min, max) = (max, min);
      }
      if (min == 0 && max != 0)
      {
        min = 1;
      }
      if (min == max)
      {
        return string.Format(CultureInfo.InvariantCulture, "{0} {1} by {2}%", max < 0 ? "Decrease" : "Increase", name, Math.Abs(max));
      }
      return string.Format(CultureInfo.InvariantCulture, "{0} {1} by {2}% to {3}%", max < 0 ? "Decrease" : "Increase", name, Math.Abs(min), Math.Abs(max));
    }

    private static string FormatPercent(string name, float min, float max, int level, int minlevel)
    {
      if (Math.Abs(min) > Math.Abs(max))
      {
        (min, max) = (max, min);
      }
      if (min == 0 && max != 0)
      {
        min = 1;
      }
      if (min == max)
      {
        return string.Format(CultureInfo.InvariantCulture, "{0} {1} by {2}%", max < 0 ? "Decrease" : "Increase", name, Math.Abs(max));
      }
      return string.Format(CultureInfo.InvariantCulture, "{0} {1} by {2}% (L{4}) to {3}% (L{5})",
        max < 0 ? "Decrease" : "Increase", name, Math.Abs(min), Math.Abs(max), level, minlevel);
    }

    // Annotation for effects whose per-tick value grows/decays over the duration. Port of
    // SpellParser.CalcValueRange; returns null (no annotation) for non-scaling calc codes.
    private static string CalcValueRange(int calc, int base1, int max, int duration, int level)
    {
      var start = SpellFormula.CalcValue(calc, base1, max, 1, level);
      var finish = Math.Abs(SpellFormula.CalcValue(calc, base1, max, duration, level));
      var type = Math.Abs(start) < Math.Abs(finish) ? "Growing" : "Decaying";

      if (calc == 123)
      {
        return $" (Random: {base1} to {max * (base1 >= 0 ? 1 : -1)})";
      }
      if (calc == 107)
      {
        return $" ({type} to {finish} @ 1/tick)";
      }
      if (calc == 108)
      {
        return $" ({type} to {finish} @ 2/tick)";
      }
      if (calc == 120)
      {
        return $" ({type} to {finish} @ 5/tick)";
      }
      if (calc == 122)
      {
        return $" ({type} to {finish} @ 12/tick)";
      }
      if (calc > 1000 && calc < 2000)
      {
        return $" ({type} to {finish} @ {calc - 1000}/tick)";
      }
      return null;
    }
  }
}
