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

    // 23-column parser-format line. Cols mirror convert_spells.py convert_line:
    // 0 id, 1 name, 2 level, 3 duration, 4 beneficial, 5 maxhits, 6 target,
    // 7 classmask, 8 damaging, 9 combatskill, 10 resist, 11 songwindow, 12 adps,
    // 13 mgb, 14 rank, 15/16 ambiguity, 17-19 messages, 20 cast, 21 recast, 22 category.
    private const string FullLine =
      "9999^Test Spell^60^0^1^0^5^2^1^0^0^0^0^0^0^1^1^You feel tested.^Someone feels tested.^The test fades.^4500^420000^Heals";

    [TestMethod]
    public void ParseCustomSpellData_ReadsCastingAndRecastMs()
    {
      var spell = _store.ParseCustomSpellData(FullLine);

      Assert.IsNotNull(spell);
      Assert.AreEqual(4500u, spell.CastingTimeMs);
      Assert.AreEqual(420000u, spell.RecastTimeMs);
      Assert.AreEqual("Heals", spell.Category);
    }

    [TestMethod]
    public void ParseCustomSpellData_ZeroCastIsInstant()
    {
      // CastingTimeMs == 0 is the documented instant-cast signal the correlator
      // keys off (no "begins casting" line for instant spells).
      var line = FullLine.Replace("^4500^420000^Heals", "^0^0^");
      var spell = _store.ParseCustomSpellData(line);

      Assert.IsNotNull(spell);
      Assert.AreEqual(0u, spell.CastingTimeMs);
      Assert.AreEqual(0u, spell.RecastTimeMs);
    }

    [TestMethod]
    public void ParseCustomSpellData_PreExtensionLineFallsBackToZero()
    {
      // A 20-column line (indices 0-19) predates the 1.1.3 extension. The parser's
      // data.Length guards must fall back to 0 / empty rather than throwing.
      var legacy = string.Join('^',
        "9999", "Test Spell", "60", "0", "1", "0", "5", "2", "1", "0", "0", "0",
        "0", "0", "0", "1", "1", "You feel tested.", "Someone feels tested.", "The test fades.");
      var spell = _store.ParseCustomSpellData(legacy);

      Assert.IsNotNull(spell);
      Assert.AreEqual(0u, spell.CastingTimeMs);
      Assert.AreEqual(0u, spell.RecastTimeMs);
      Assert.AreEqual(string.Empty, spell.Category);
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
  }
}
