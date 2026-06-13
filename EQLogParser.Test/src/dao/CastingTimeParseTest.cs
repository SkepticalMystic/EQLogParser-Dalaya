using EQLogParser;

namespace EQLogParserTest
{
  // Locks the parser-format contract for CastingTimeMs (col 20) and RecastTimeMs
  // (col 21), added in 1.1.3 and emitted by tools/spells/convert_spells.py. These
  // fields are the data prerequisite for the planned cast-window correlator
  // (project_dd_attribution) and the heal-coordination timer bundle
  // (project_trigger_spell_picker Phase 4), so a regression here would silently
  // break those features before they're built.
  //
  // The unit tests exercise ParseCustomSpellData directly with crafted lines so
  // the exact parse + the pre-extension backward-compat fallback are pinned. The
  // integration tests confirm the shipped spells.txt actually carries populated
  // values (catches a converter that wasn't rerun after a Dalaya patch).
  [TestClass]
  public class CastingTimeParseTest
  {
    private EQDataStore _store;

    [TestInitialize]
    public void Setup()
    {
      _store = EQDataStore.Instance;
    }

    // 26-column parser-format line. Cols mirror convert_spells.py convert_line:
    // 0 id, 1 name, 2 level, 3 duration, 4 beneficial, 5 maxhits, 6 target,
    // 7 classmask, 8 damaging, 9 combatskill, 10 resist, 11 songwindow, 12 adps,
    // 13 mgb, 14 rank, 15/16 ambiguity, 17-19 messages, 20 cast, 21 recast,
    // 22 category, 23 skill, 24 recourseid, 25 timerid.
    private const string FullLine =
      "9999^Test Spell^60^0^1^0^5^2^1^0^0^0^0^0^0^1^1^You feel tested.^Someone feels tested.^The test fades.^4500^420000^Heals^24^1234^88";

    [TestMethod]
    public void ParseCustomSpellData_ReadsCastingAndRecastMs()
    {
      var spell = _store.ParseCustomSpellData(FullLine);

      Assert.IsNotNull(spell);
      Assert.AreEqual(4500u, spell.CastingTimeMs);
      Assert.AreEqual(420000u, spell.RecastTimeMs);
      Assert.AreEqual("Heals", spell.Category);
      Assert.AreEqual(24, spell.Skill);
      Assert.AreEqual(1234, spell.RecourseID);
      Assert.AreEqual(88, spell.TimerID);
    }

    [TestMethod]
    public void ParseCustomSpellData_ZeroCastIsInstant()
    {
      // CastingTimeMs == 0 is the documented instant-cast signal the correlator
      // keys off (no "begins casting" line for instant spells).
      var line = FullLine.Replace("^4500^420000^Heals^24^1234^88", "^0^0^^0^0^0");
      var spell = _store.ParseCustomSpellData(line);

      Assert.IsNotNull(spell);
      Assert.AreEqual(0u, spell.CastingTimeMs);
      Assert.AreEqual(0u, spell.RecastTimeMs);
    }

    [TestMethod]
    public void ParseCustomSpellData_PreservesNegativeSkill()
    {
      // Skill col 100 uses -1 as the "no skill" marker. The converter preserves
      // the sign (does not clamp to 0), and the parser reads it as a signed int.
      var line = FullLine.Replace("^Heals^24^1234^88", "^Heals^-1^0^0");
      var spell = _store.ParseCustomSpellData(line);

      Assert.IsNotNull(spell);
      Assert.AreEqual(-1, spell.Skill);
    }

    [TestMethod]
    public void ParseCustomSpellData_PreExtensionLineFallsBackToZero()
    {
      // A 20-column line (indices 0-19) predates the 1.1.3 extension. The parser's
      // data.Length guards must fall back to 0 / empty rather than throwing — this
      // also covers the later cols 23-25 (Skill/RecourseID/TimerID).
      var legacy = string.Join('^',
        "9999", "Test Spell", "60", "0", "1", "0", "5", "2", "1", "0", "0", "0",
        "0", "0", "0", "1", "1", "You feel tested.", "Someone feels tested.", "The test fades.");
      var spell = _store.ParseCustomSpellData(legacy);

      Assert.IsNotNull(spell);
      Assert.AreEqual(0u, spell.CastingTimeMs);
      Assert.AreEqual(0u, spell.RecastTimeMs);
      Assert.AreEqual(string.Empty, spell.Category);
      Assert.AreEqual(0, spell.Skill);
      Assert.AreEqual(0, spell.RecourseID);
      Assert.AreEqual(0, spell.TimerID);
    }

    [TestMethod]
    public void ShippedSpells_SuperiorHealing_HasCastingTime()
    {
      // id 9, a stable cleric heal. Converter emits cast=4500, recast=0. Asserted
      // loosely (> 0) because GetSpellByName resolves to the highest-level rank
      // variant; exactness is pinned by the unit tests above.
      var spell = _store.GetSpellByName("Superior Healing");
      Assert.IsNotNull(spell, "Superior Healing missing from spells.txt");
      Assert.IsTrue(spell.CastingTimeMs > 0,
        "Superior Healing should carry a non-zero CastingTimeMs — was the converter rerun?");
    }

    [TestMethod]
    public void ShippedSpells_CompleteHealing_HasRecastLockout()
    {
      // id 13, Complete Healing — the iconic long-recast cleric heal. Proves the
      // RecastTimeMs column flows through from the shipped data, not just cast time.
      var spell = _store.GetSpellByName("Complete Healing");
      Assert.IsNotNull(spell, "Complete Healing missing from spells.txt");
      Assert.IsTrue(spell.RecastTimeMs > 0,
        "Complete Healing should carry a non-zero RecastTimeMs — was the converter rerun?");
    }

    [TestMethod]
    public void ShippedSpells_CompleteHealing_HasSkill()
    {
      // Complete Healing is an Alteration (skill 5) heal. Proves the Skill column
      // (parser col 23, source col 100) flows through from the shipped data.
      var spell = _store.GetSpellByName("Complete Healing");
      Assert.IsNotNull(spell, "Complete Healing missing from spells.txt");
      Assert.AreEqual(5, spell.Skill,
        "Complete Healing should carry Skill 5 (Alteration) — was the converter rerun?");
    }

    [TestMethod]
    public void ShippedSpells_Lich_HasRecourse()
    {
      // Lich procs a recourse on the caster. Proves the RecourseID column (parser
      // col 24, source col 150) flows through. Cols 23-25 are emitted together,
      // so this also confirms TimerID (col 25) was carried.
      var spell = _store.GetSpellByName("Lich");
      Assert.IsNotNull(spell, "Lich missing from spells.txt");
      Assert.IsTrue(spell.RecourseID > 0,
        "Lich should carry a non-zero RecourseID — was the converter rerun?");
    }
  }
}
