using System;

namespace EQLogParser
{
  // Port of ngdeao/SoD-winspellparser/SpellParser.cs CalcValue and CalcDuration,
  // covering the level/tick-scaled value formulas used by EverQuest's spell
  // system. CalcValue computes a per-tick effect base from the spell's base
  // value, formula code, and caster level. CalcDuration computes a spell's
  // duration in 6-second ticks. See memory project_dot_hot_validation Phase 2
  // for context.
  //
  // Source citations (SpellParser.cs):
  //   - CalcDuration: lines 1821-1896
  //   - CalcValue:   lines 1901-2080
  //
  // The C# port is structurally identical to the source to keep behavior
  // verifiable against the wiki and SoD parser output. Formulas not yet observed
  // in Dalaya spell data are still implemented so future spells using them
  // (e.g., from a patch) don't fall to a default.
  internal static class SpellFormula
  {
    // Returns a duration in 6-second ticks (each tick = 6 seconds of game time).
    // Direct port of SpellParser.cs:1821.
    public static int CalcDuration(int calc, int max, int level)
    {
      int value;
      switch (calc)
      {
        case 0:
          value = 0;
          break;
        case 1:
          value = level / 2;
          if (value < 1) value = 1;
          break;
        case 2:
          value = (level / 2) + 5;
          if (value < 6) value = 6;
          break;
        case 3:
          value = level * 30;
          break;
        case 4:
          value = 50;
          break;
        case 5:
          value = 2;
          break;
        case 6:
          value = level / 2;
          break;
        case 7:
          value = level;
          break;
        case 8:
          value = level + 10;
          break;
        case 9:
          value = level * 2 + 10;
          break;
        case 10:
          value = level * 30 + 10;
          break;
        case 11:
          value = (level + 3) * 30;
          break;
        case 12:
          value = level / 2;
          if (value < 1) value = 1;
          break;
        case 13:
          value = level * 3 + 10;
          break;
        case 14:
          value = (level + 2) * 5;
          break;
        case 15:
          value = (level + 10) * 10;
          break;
        case 50:
          value = 72000;
          break;
        case 3600:
          value = 3600;
          break;
        default:
          value = max;
          break;
      }

      if (max > 0 && value > max) value = max;
      return value;
    }

    // Returns the level/tick-scaled value for a spell effect slot. Tick is
    // 1-based; pass 1 for the per-tick base of a periodic effect. Direct port
    // of SpellParser.cs:1901.
    public static int CalcValue(int calc, int base1, int max, int tick, int level)
    {
      if (calc == 0) return base1;

      if (calc == 100 || calc == 200 || calc == 400)
      {
        if (max > 0 && base1 > max) return max;
        return base1;
      }

      var updownsign = 1;
      var ubase = Math.Abs(base1);

      if (max < 1 && max != 0)
      {
        updownsign = -1;
      }

      var value = ubase;
      switch (calc)
      {
        case 100:
          // unreachable (handled above); kept for parity with source switch.
          break;
        case 101: value = updownsign * (ubase + (level / 2)); break;
        case 102: value = updownsign * (ubase + level); break;
        case 103: value = updownsign * (ubase + (level * 2)); break;
        case 104: value = updownsign * (ubase + (level * 3)); break;
        case 105: value = updownsign * (ubase + (level * 4)); break;
        case 107: value = updownsign * (ubase + tick); break;
        case 108: value = updownsign * (ubase + (2 * tick)); break;
        case 109: value = updownsign * (ubase + (level / 4)); break;
        case 110: value = ubase + (level / 6); break;
        case 111:
          if (level > 16) value = updownsign * (ubase + ((level - 16) * 6));
          break;
        case 112:
          if (level > 24) value = updownsign * (ubase + ((level - 24) * 8));
          break;
        case 113:
          if (level > 34) value = updownsign * (ubase + ((level - 34) * 10));
          break;
        case 114:
          if (level > 44) value = updownsign * (ubase + ((level - 44) * 15));
          break;
        case 115:
          if (level > 15) value = updownsign * (ubase + ((level - 15) * 7));
          break;
        case 116:
          if (level > 24) value = ubase + ((level - 24) * 10);
          break;
        case 117:
          if (level > 34) value = ubase + ((level - 34) * 13);
          break;
        case 118:
          if (level > 44) value = ubase + ((level - 44) * 20);
          break;
        case 119: value = ubase + (level / 8); break;
        case 120: value = updownsign * (ubase + (5 * tick)); break;
        case 121: value = ubase + (level / 3); break;
        case 122:
          var tickRemain = tick - 1;
          if (tickRemain < 0) tickRemain = 0;
          value = updownsign * (base1 - (12 * tickRemain));
          break;
        case 123:
          value = (Math.Abs(max) - ubase) / 2;
          break;
        case 124:
          if (level > 50) value = updownsign * (ubase + (level - 50));
          break;
        case 125:
          if (level > 50) value = updownsign * (ubase + ((level - 50) * 2));
          break;
        case 126:
          if (level > 50) value = updownsign * (ubase + ((level - 50) * 3));
          break;
        case 127:
          if (level > 50) value = updownsign * (ubase + ((level - 50) * 4));
          break;
        case 128:
          if (level > 50) value = updownsign * (ubase + ((level - 50) * 5));
          break;
        case 129:
          if (level > 50) value = updownsign * (ubase + ((level - 50) * 10));
          break;
        case 130:
          if (level > 50) value = updownsign * (ubase + ((level - 50) * 15));
          break;
        case 131:
          if (level > 50) value = updownsign * (ubase + ((level - 50) * 20));
          break;
        case 132:
          if (level > 50) value = updownsign * (ubase + ((level - 50) * 25));
          break;
        case 139:
          if (level > 30) value = updownsign * (ubase + ((level - 30) / 2));
          break;
        case 140:
          if (level > 30) value = updownsign * (ubase + (level - 30));
          break;
        case 141:
          if (level > 30) value = updownsign * (ubase + (3 * (level - 30) / 2));
          break;
        case 142:
          if (level > 30) value = updownsign * (ubase + (2 * (level - 60)));
          break;
        case 143:
          value = updownsign * (ubase + (3 * level / 4));
          break;
        default:
          if (calc > 0 && calc < 1000) value = ubase + (level * calc);
          if (calc >= 1000 && calc < 2000) value = updownsign * (ubase - (tick * (calc - 1000)));
          if (calc >= 2000) value = ubase + (level * (calc - 2000));
          break;
      }

      if (max != 0)
      {
        if (updownsign == 1)
        {
          if (value > max) value = max;
        }
        else
        {
          if (value < max) value = max;
        }
      }

      if (base1 < 0 && value > 0) value = -value;
      return value;
    }
  }
}
