using EQLogParser;
using System.Linq;

namespace EQLogParserTest
{
  // Contract for the SPA effect decoder (port of SoD SpellParser.ParseEffect). The unit tests
  // exercise DescribeSlot with crafted slot values so the per-SPA text + the FormatCount/
  // FormatPercent helpers are pinned exactly. The integration test confirms the decoder reads
  // the shipped spell-effects.json end to end. See memory project_spell_tooltips.
  [TestClass]
  public class SpellEffectDecoderTest
  {
    // DescribeSlot(spa, base1, base2, max, calc, level, minLevel, durationTicks)
    private static string Slot(int spa, int base1, int calc = 100, int level = 60, int minLevel = 60,
      int durationTicks = 0, int base2 = 0, int max = 0) =>
      SpellEffectDecoder.DescribeSlot(spa, base1, base2, max, calc, level, minLevel, durationTicks);

    [TestMethod]
    public void FlatStat_IncreaseAndDecrease()
    {
      Assert.AreEqual("Increase ATK by 25", Slot(2, 25));
      Assert.AreEqual("Increase Fire Resist by 40", Slot(46, 40));
      Assert.AreEqual("Decrease Current Mana by 50", Slot(15, -50));
    }

    [TestMethod]
    public void Heal_And_Nuke_CurrentHitPoints()
    {
      Assert.AreEqual("Increase Current Hit Points by 500", Slot(79, 500));
      Assert.AreEqual("Decrease Current Hit Points by 300", Slot(79, -300));
    }

    [TestMethod]
    public void HoT_RepeatsPerTick()
    {
      // SPA 100 with a positive duration → " per tick" annotation.
      Assert.AreEqual("Increase Current HP by 155 per tick", Slot(100, 155, durationTicks: 18));
    }

    [TestMethod]
    public void Haste_And_Slow_AsPercentAroundBase100()
    {
      // Base attack speed is 100: 130 → +30% haste, 85 → -15% (Attack Speed).
      Assert.AreEqual("Increase Melee Haste by 30%", Slot(11, 130));
      Assert.AreEqual("Decrease Attack Speed by 15%", Slot(11, 85));
    }

    [TestMethod]
    public void Stun_FormatsSecondsFromMillis()
    {
      Assert.AreEqual("Stun for 3s", Slot(21, 3000));
    }

    [TestMethod]
    public void NamedNoValueEffects()
    {
      Assert.AreEqual("Root", Slot(99, 1));
      Assert.AreEqual("Mesmerize", Slot(31, 1));
      Assert.AreEqual("Dispel (1)", Slot(27, 1));
    }

    [TestMethod]
    public void LevelScaledValue_ShowsRange()
    {
      // calc 104 = ubase + level*3. base1=10 → L30: 100, L60: 190. value != minvalue → range.
      Assert.AreEqual("Increase STR by 100 (L30) to 190 (L60)", Slot(4, 10, calc: 104, level: 60, minLevel: 30));
    }

    [TestMethod]
    public void EmptyAndNoOpSlots_ReturnNull()
    {
      Assert.IsNull(Slot(254, 999), "SPA 254 is the unused-slot marker");
      Assert.IsNull(Slot(1, 0), "zero-valued AC/stat placeholder slot is a no-op");
    }

    [TestMethod]
    public void UnknownSpa_FallsBackToDescriptor()
    {
      // Dalaya-specific SPAs above 200 (340, 383, ...) aren't in SoD's switch and fall to the
      // same honest descriptor SoD emits — never throws.
      Assert.AreEqual(
        "Unknown Effect: 340 Base1=5 Base2=0 Max=0 Calc=100 Value=5",
        Slot(340, 5));
    }

    [TestMethod]
    public void Describe_ShippedHoT_RendersPerTickSlot()
    {
      // id 4989 Circle of Soothing: slot 2 = SPA 100, Base1=155, Calc=100, durationBase=6.
      // Stable Dalaya HoT — its decoded slot should read as a per-tick Current HP increase.
      var store = EQDataStore.Instance;
      var effects = store.GetSpellEffects("4989");
      Assert.IsNotNull(effects, "Circle of Soothing missing from spell-effects.json — was the converter rerun?");

      var lines = SpellEffectDecoder.Describe(effects, level: 65, minLevel: 65);

      Assert.IsTrue(lines.Count > 0, "expected at least one decoded slot");
      Assert.IsTrue(lines.Any(l => l.Contains("Current HP") && l.Contains("per tick")),
        "Circle of Soothing should decode a per-tick Current HP slot; got: " + string.Join(" | ", lines));
      Assert.IsTrue(lines.All(l => l.StartsWith("Slot ")), "every line should carry its 1-based slot prefix");
    }
  }
}
