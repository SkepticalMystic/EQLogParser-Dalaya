using System.Collections.Generic;
using System.Linq;
using EQLogParser;

namespace EQLogParserTest
{
  // Unit coverage for the pure SpellPickerFilter used by the trigger spell
  // picker (project-trigger-spell-picker Phases 1-3). No WPF / EQDataStore
  // dependency — spells are hand-built so each filter dimension is pinned in
  // isolation and in combination.
  [TestClass]
  public class SpellPickerFilterTest
  {
    private static SpellData Spell(string name, int classMask = 0, string category = "",
      string landsOnYou = "", string landsOnOther = "", string wearOff = "") => new()
    {
      Name = name,
      ClassMask = (ushort)classMask,
      Category = category,
      LandsOnYou = landsOnYou,
      LandsOnOther = landsOnOther,
      WearOff = wearOff
    };

    private static List<SpellData> Sample() =>
    [
      Spell("Superior Healing", (int)SpellClass.Clr | (int)SpellClass.Pal, "Heals",
        landsOnYou: "You feel much better.", landsOnOther: "is surrounded by a holy light."),
      Spell("Circle of Soothing", (int)SpellClass.Dru, "Heals;Duration Heals",
        landsOnOther: "begins to heal."),
      Spell("Frost Bolt", (int)SpellClass.Wiz, "Direct Damage",
        landsOnOther: "is covered in frost."),
      Spell("Chloroplast", (int)SpellClass.Dru | (int)SpellClass.Rng, "Regen",
        landsOnYou: "Your skin hardens.", wearOff: "Your skin returns to normal.")
    ];

    [TestMethod]
    public void NoFilters_ReturnsAllOrderedByName()
    {
      var result = SpellPickerFilter.Apply(Sample(), null, null, null).ToList();
      Assert.AreEqual(4, result.Count);
      // Ordered by name: Chloroplast, Circle of Soothing, Frost Bolt, Superior Healing
      CollectionAssert.AreEqual(
        new[] { "Chloroplast", "Circle of Soothing", "Frost Bolt", "Superior Healing" },
        result.Select(s => s.Name).ToArray());
    }

    [TestMethod]
    public void ClassFilter_KeepsOnlySpellsThatClassCanCast()
    {
      var result = SpellPickerFilter.Apply(Sample(), null, SpellClass.Dru, null).ToList();
      // Druid: Circle of Soothing + Chloroplast (Dru|Rng). Not Superior Healing (Clr|Pal) or Frost Bolt (Wiz).
      CollectionAssert.AreEqual(
        new[] { "Chloroplast", "Circle of Soothing" },
        result.Select(s => s.Name).ToArray());
    }

    [TestMethod]
    public void ClassFilter_MultiClassSpellMatchesEitherClass()
    {
      // Superior Healing is Clr|Pal — a Paladin filter should still find it.
      var asPal = SpellPickerFilter.Apply(Sample(), null, SpellClass.Pal, null).ToList();
      Assert.AreEqual(1, asPal.Count);
      Assert.AreEqual("Superior Healing", asPal[0].Name);
    }

    [TestMethod]
    public void CategoryFilter_MatchesSemicolonJoinedCategories()
    {
      var result = SpellPickerFilter.Apply(Sample(), null, null, "Heals").ToList();
      // Both "Heals" and "Heals;Duration Heals" should match the "Heals" token.
      CollectionAssert.AreEqual(
        new[] { "Circle of Soothing", "Superior Healing" },
        result.Select(s => s.Name).ToArray());
    }

    [TestMethod]
    public void CategoryFilter_DoesNotPartialMatchToken()
    {
      // "Duration Heals" is a distinct token; filtering on it must not pull plain "Heals".
      var result = SpellPickerFilter.Apply(Sample(), null, null, "Duration Heals").ToList();
      Assert.AreEqual(1, result.Count);
      Assert.AreEqual("Circle of Soothing", result[0].Name);
    }

    [TestMethod]
    public void Search_MatchesAcrossNameAndAllMessageFields()
    {
      // Name match.
      Assert.AreEqual(1, SpellPickerFilter.Apply(Sample(), "frost bolt", null, null).Count());
      // LandsOnOther match ("holy light" only on Superior Healing).
      var byOther = SpellPickerFilter.Apply(Sample(), "holy light", null, null).ToList();
      Assert.AreEqual(1, byOther.Count);
      Assert.AreEqual("Superior Healing", byOther[0].Name);
      // WearOff match ("returns to normal" only on Chloroplast).
      var byWear = SpellPickerFilter.Apply(Sample(), "returns to normal", null, null).ToList();
      Assert.AreEqual(1, byWear.Count);
      Assert.AreEqual("Chloroplast", byWear[0].Name);
    }

    [TestMethod]
    public void Search_IsCaseInsensitive()
    {
      Assert.AreEqual(1, SpellPickerFilter.Apply(Sample(), "CIRCLE", null, null).Count());
    }

    [TestMethod]
    public void CombinedFilters_AllApplyTogether()
    {
      // Druid + Heals category + "soothing" search → just Circle of Soothing.
      var result = SpellPickerFilter.Apply(Sample(), "soothing", SpellClass.Dru, "Heals").ToList();
      Assert.AreEqual(1, result.Count);
      Assert.AreEqual("Circle of Soothing", result[0].Name);
    }

    [TestMethod]
    public void NullSpells_ReturnsEmpty()
    {
      Assert.AreEqual(0, SpellPickerFilter.Apply(null, "x", SpellClass.Dru, "Heals").Count());
    }

    [TestMethod]
    public void WhitespaceSearch_IsIgnored()
    {
      Assert.AreEqual(4, SpellPickerFilter.Apply(Sample(), "   ", null, null).Count());
    }
  }
}
