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
      // SPAs with no case in the switch emit an honest descriptor and never throw.
      Assert.AreEqual(
        "Unknown Effect: 999 Base1=5 Base2=0 Max=0 Calc=100 Value=5",
        Slot(999, 5));
    }

    [TestMethod]
    public void DalayaSpas_RenderCorrectly()
    {
      // SPA 203 — Soulbond
      Assert.AreEqual("Soulbond", Slot(203, 0));

      // SPA 206 — Force Aggro (taunt), base1 is hate amount
      Assert.AreEqual("Force Aggro (100 hate)", Slot(206, 100));
      Assert.AreEqual("Force Aggro (400 hate)", Slot(206, 400));

      // SPA 220 — Suspend (1) / Revive (0) Pet
      Assert.AreEqual("Suspend Pet", Slot(220, 1));
      Assert.AreEqual("Revive Pet",  Slot(220, 0));

      // SPA 234 — Fortification Enhancement, base1 is the enhancement amount
      Assert.AreEqual("Increase Fortification by 10", Slot(234, 10));

      // SPA 289 — On Expiry: Cast [Spell base1]
      Assert.AreEqual("On Expiry: Cast [Spell 4656]", Slot(289, 4656));

      // SPA 322 — Home Gate
      Assert.AreEqual("Home Gate", Slot(322, 1));

      // SPA 328 — Delay Death
      Assert.AreEqual("Delay Death: Survive up to 600 negative HP",  Slot(328, 600));
      Assert.AreEqual("Delay Death: Survive up to 5000 negative HP", Slot(328, 5000));

      // SPA 340 — Add Spell Proc: [Spell base1] (max% Chance)
      Assert.AreEqual("Add Spell Proc: [Spell 4700] (20% Chance)", Slot(340, 4700, max: 20));
      Assert.AreEqual("Add Spell Proc: [Spell 4700]",              Slot(340, 4700, max: 0));

      // SPA 383 — Trigger: Cast [Spell base1] when [Spell max] is used (max=0 → no trigger annotation)
      Assert.AreEqual("Trigger: Cast [Spell 911] when [Spell 464] is used", Slot(383, 911, max: 464));
      Assert.AreEqual("Trigger: Cast [Spell 1613]",                          Slot(383, 1613, max: 0));

      // SPA 460 — Relic: Savior Effect
      Assert.AreEqual("Relic: Savior Effect", Slot(460, 1));

      // SPA 461 — Add Proc (reversed fields vs 340): base1=%, max=spell
      Assert.AreEqual("Add Proc: [Spell 3295] (40% Chance)", Slot(461, 40, max: 3295));

      // SPA 463 — Add Block Proc: [Spell base1]
      Assert.AreEqual("Add Block Proc: [Spell 7143]", Slot(463, 7143));
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

    [TestMethod]
    public void Describe_ShippedInstantHeal_DecodesSpaZero()
    {
      // Regression guard for the converter's SPA-0 filter. SPA 0 ("Hit Points") is the real
      // effect for instant heals, nukes, and classic DoTs/HoTs — it must NOT be dropped as an
      // empty marker (only all-zero SPA-0 padding is). id 13 Complete Healing is a large SPA-0
      // heal (base1=20000); if extract_effects regresses to filtering all SPA 0, this spell
      // (and every heal/nuke) would decode to nothing.
      var store = EQDataStore.Instance;
      var effects = store.GetSpellEffects("13");
      Assert.IsNotNull(effects, "Complete Healing missing from spell-effects.json — was the converter rerun?");

      var lines = SpellEffectDecoder.Describe(effects, level: 39, minLevel: 39);

      Assert.IsTrue(lines.Any(l => l.Contains("Hit Points")),
        "Complete Healing (SPA 0 heal) should decode a Hit Points line; got: " + string.Join(" | ", lines));
    }
  }
}
