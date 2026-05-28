using EQLogParser;

namespace EQLogParserTest
{
  // Unit tests for the SpellFormula port of SoD-winspellparser CalcValue and
  // CalcDuration. Cases mirror the formulas at SpellParser.cs:1821 (duration)
  // and 1901 (value), verified against the SoD parser source. See memory
  // project_dot_hot_validation Phase 2.
  [TestClass]
  public class SpellFormulaTest
  {
    [TestMethod]
    public void CalcValue_Calc0_ReturnsBaseUnchanged()
    {
      Assert.AreEqual(155, SpellFormula.CalcValue(calc: 0, base1: 155, max: 0, tick: 1, level: 65));
      Assert.AreEqual(-200, SpellFormula.CalcValue(calc: 0, base1: -200, max: 0, tick: 1, level: 65));
    }

    [TestMethod]
    public void CalcValue_Calc100_ReturnsBaseFlat()
    {
      // Calc 100 is flat — no level scaling. Used by Circle of Soothing (Base1=155)
      // and Relic: Sihala's Empathy 7591 (Base1=200).
      Assert.AreEqual(155, SpellFormula.CalcValue(calc: 100, base1: 155, max: 0, tick: 1, level: 65));
      Assert.AreEqual(200, SpellFormula.CalcValue(calc: 100, base1: 200, max: 0, tick: 1, level: 1));
    }

    [TestMethod]
    public void CalcValue_Calc100_CapsAtMax()
    {
      Assert.AreEqual(100, SpellFormula.CalcValue(calc: 100, base1: 155, max: 100, tick: 1, level: 65));
    }

    [TestMethod]
    public void CalcValue_LinearLevelScaling()
    {
      // Calc 101: base + level/2. Calc 102: base + level. Calc 105: base + 4*level.
      Assert.AreEqual(50 + 32, SpellFormula.CalcValue(calc: 101, base1: 50, max: 0, tick: 1, level: 65));
      Assert.AreEqual(50 + 65, SpellFormula.CalcValue(calc: 102, base1: 50, max: 0, tick: 1, level: 65));
      Assert.AreEqual(50 + 65 * 4, SpellFormula.CalcValue(calc: 105, base1: 50, max: 0, tick: 1, level: 65));
    }

    [TestMethod]
    public void CalcValue_NegativeBasePreservesSign()
    {
      // base1=-100, calc=102 → -(100 + level) at level 65 = -165.
      Assert.AreEqual(-(100 + 65), SpellFormula.CalcValue(calc: 102, base1: -100, max: 0, tick: 1, level: 65));
    }

    [TestMethod]
    public void CalcValue_LevelGated_BelowThresholdReturnsUbase()
    {
      // Calc 111 gates at level 16. Below threshold, the switch falls through
      // and `value = ubase` (set at the top of CalcValue, since no `if` branch fires).
      Assert.AreEqual(50, SpellFormula.CalcValue(calc: 111, base1: 50, max: 0, tick: 1, level: 16));
      Assert.AreEqual(50 + (20 - 16) * 6, SpellFormula.CalcValue(calc: 111, base1: 50, max: 0, tick: 1, level: 20));
    }

    [TestMethod]
    public void CalcValue_Calc122_DecaysByTick()
    {
      // base - 12*(tick-1). First tick = base, second = base-12, etc.
      Assert.AreEqual(100, SpellFormula.CalcValue(calc: 122, base1: 100, max: 0, tick: 1, level: 65));
      Assert.AreEqual(100 - 12, SpellFormula.CalcValue(calc: 122, base1: 100, max: 0, tick: 2, level: 65));
      Assert.AreEqual(100 - 12 * 4, SpellFormula.CalcValue(calc: 122, base1: 100, max: 0, tick: 5, level: 65));
    }

    [TestMethod]
    public void CalcValue_DefaultPercentile()
    {
      // calc 1..99 → base + level * calc.
      Assert.AreEqual(10 + 65 * 5, SpellFormula.CalcValue(calc: 5, base1: 10, max: 0, tick: 1, level: 65));
    }

    [TestMethod]
    public void CalcDuration_Calc0_ReturnsZero()
    {
      Assert.AreEqual(0, SpellFormula.CalcDuration(calc: 0, max: 0, level: 65));
    }

    [TestMethod]
    public void CalcDuration_Calc11_LevelPlusThreeTimes30CappedByMax()
    {
      // (level+3)*30, capped to max. For Circle of Soothing (durBase=6, level 65):
      // raw = (65+3)*30 = 2040, capped to 6.
      Assert.AreEqual(6, SpellFormula.CalcDuration(calc: 11, max: 6, level: 65));
      // Without cap, returns raw 2040.
      Assert.AreEqual(2040, SpellFormula.CalcDuration(calc: 11, max: 0, level: 65));
    }

    [TestMethod]
    public void CalcDuration_Calc5_AlwaysTwo()
    {
      Assert.AreEqual(2, SpellFormula.CalcDuration(calc: 5, max: 0, level: 65));
      Assert.AreEqual(2, SpellFormula.CalcDuration(calc: 5, max: 0, level: 1));
    }
  }
}
