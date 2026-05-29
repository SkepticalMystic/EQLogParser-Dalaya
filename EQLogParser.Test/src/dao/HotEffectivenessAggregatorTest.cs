using System.Collections.Generic;
using EQLogParser;

namespace EQLogParserTest
{
  // Tests for HotEffectivenessAggregator. Uses the real EQDataStore.Instance
  // (which has spells.txt + spell-effects.json loaded from the test output
  // directory's data\ folder) for the ComputeHotTickInfo lookups. Records are
  // synthesized in-memory so the test doesn't depend on log fixtures.
  //
  // See memory project_dot_hot_validation Phase 3.
  [TestClass]
  public class HotEffectivenessAggregatorTest
  {
    private static HealRecord Hot(string healer, string spell, uint total, bool crit = false) => new()
    {
      Type = Labels.Hot,
      Healer = healer,
      Healed = healer,
      SubType = spell,
      Total = total,
      ModifiersMask = crit ? LineModifiersParser.Crit : (short)-1
    };

    private static HealRecord Direct(string healer, string spell, uint total) => new()
    {
      Type = Labels.Heal,
      Healer = healer,
      Healed = healer,
      SubType = spell,
      Total = total,
      ModifiersMask = -1
    };

    [TestMethod]
    public void EmptyInputs_EmptyOutput()
    {
      var rows = HotEffectivenessAggregator.Aggregate(new List<HealRecord>(), "Drucilla", 65, SpellClass.Dru, EQDataStore.Instance);
      Assert.AreEqual(0, rows.Count);
    }

    [TestMethod]
    public void NullInputs_HandledDefensively()
    {
      Assert.AreEqual(0, HotEffectivenessAggregator.Aggregate(null, "X", 65, SpellClass.Dru, EQDataStore.Instance).Count);
      Assert.AreEqual(0, HotEffectivenessAggregator.Aggregate([], null, 65, SpellClass.Dru, EQDataStore.Instance).Count);
      Assert.AreEqual(0, HotEffectivenessAggregator.Aggregate([], "X", 65, SpellClass.Dru, null).Count);
    }

    [TestMethod]
    public void FiltersDirectHealsOut()
    {
      var records = new List<HealRecord>
      {
        Hot("Drucilla", "Circle of Soothing", 300),
        Direct("Drucilla", "Healing", 500),
      };

      var rows = HotEffectivenessAggregator.Aggregate(records, "Drucilla", 65, SpellClass.Dru, EQDataStore.Instance);

      // Only the HoT entry survives.
      Assert.AreEqual(1, rows.Count);
      Assert.AreEqual("Circle of Soothing", rows[0].SpellName);
    }

    [TestMethod]
    public void FiltersOtherHealers()
    {
      var records = new List<HealRecord>
      {
        Hot("Drucilla", "Circle of Soothing", 300),
        Hot("Snowzz", "Circle of Soothing", 300),
      };

      var rows = HotEffectivenessAggregator.Aggregate(records, "Drucilla", 65, SpellClass.Dru, EQDataStore.Instance);

      Assert.AreEqual(1, rows.Count);
      Assert.AreEqual(1, rows[0].TickCount);
    }

    [TestMethod]
    public void AggregatesMultipleTicksOfSameSpell()
    {
      var records = new List<HealRecord>
      {
        Hot("Drucilla", "Circle of Soothing", 302),
        Hot("Drucilla", "Circle of Soothing", 604),
        Hot("Drucilla", "Circle of Soothing", 302),
      };

      var rows = HotEffectivenessAggregator.Aggregate(records, "Drucilla", 65, SpellClass.Dru, EQDataStore.Instance);

      Assert.AreEqual(1, rows.Count);
      var row = rows[0];
      Assert.AreEqual(3, row.TickCount);
      Assert.AreEqual(1208L, row.TotalHealing);
      Assert.AreEqual(1208 / 3.0, row.AverageObserved, 0.01);
      // ExpectedPerTick from spell-effects.json: Circle of Soothing slot 2 SPA 100 Base1=155 Calc=100.
      Assert.AreEqual(155, row.ExpectedPerTick);
      Assert.AreEqual(2, row.TickIntervalSeconds);
      Assert.IsNotNull(row.Ratio);
      Assert.AreEqual((1208 / 3.0) / 155, row.Ratio.Value, 0.01);
    }

    [TestMethod]
    public void UnknownSpell_RowHasNullExpected()
    {
      var records = new List<HealRecord>
      {
        Hot("Drucilla", "Fictitious Spell", 250),
      };

      var rows = HotEffectivenessAggregator.Aggregate(records, "Drucilla", 65, SpellClass.Dru, EQDataStore.Instance);

      Assert.AreEqual(1, rows.Count);
      var row = rows[0];
      Assert.AreEqual(1, row.TickCount);
      Assert.AreEqual(250L, row.TotalHealing);
      Assert.IsNull(row.ExpectedPerTick);
      Assert.IsNull(row.TickIntervalSeconds);
      Assert.IsNull(row.Ratio);
    }

    [TestMethod]
    public void SortedByTotalHealingDescending()
    {
      var records = new List<HealRecord>
      {
        Hot("Drucilla", "Relic: Sihala's Empathy", 200),
        Hot("Drucilla", "Circle of Soothing", 1000),
        Hot("Drucilla", "Circle of Soothing", 1000),
        Hot("Drucilla", "Runic: Spined Resurgence", 500),
      };

      var rows = HotEffectivenessAggregator.Aggregate(records, "Drucilla", 65, SpellClass.Dru, EQDataStore.Instance);

      Assert.AreEqual(3, rows.Count);
      Assert.AreEqual("Circle of Soothing", rows[0].SpellName);
      Assert.AreEqual(2000L, rows[0].TotalHealing);
      Assert.AreEqual("Runic: Spined Resurgence", rows[1].SpellName);
      Assert.AreEqual("Relic: Sihala's Empathy", rows[2].SpellName);
    }

    [TestMethod]
    public void NonDruidCaster_GetsSixSecondInterval()
    {
      var records = new List<HealRecord>
      {
        Hot("Othmar", "Circle of Soothing", 155),
      };

      var rows = HotEffectivenessAggregator.Aggregate(records, "Othmar", 65, SpellClass.Clr, EQDataStore.Instance);

      Assert.AreEqual(1, rows.Count);
      Assert.AreEqual(6, rows[0].TickIntervalSeconds);
    }

    [TestMethod]
    public void TracksMaxHealAndCrits()
    {
      var records = new List<HealRecord>
      {
        Hot("Drucilla", "Circle of Soothing", 302, crit: false),
        Hot("Drucilla", "Circle of Soothing", 604, crit: true),
        Hot("Drucilla", "Circle of Soothing", 302, crit: false),
        Hot("Drucilla", "Circle of Soothing", 800, crit: true),
      };

      var rows = HotEffectivenessAggregator.Aggregate(records, "Drucilla", 65, SpellClass.Dru, EQDataStore.Instance);

      Assert.AreEqual(1, rows.Count);
      var row = rows[0];
      Assert.AreEqual(4, row.TickCount);
      Assert.AreEqual(800u, row.MaxHeal);
      Assert.AreEqual(2, row.CritCount);
      Assert.AreEqual(0.5, row.CritRate, 0.0001);
      // Non-crit avg = (302 + 302) / 2 = 302.
      Assert.IsNotNull(row.NonCritAverage);
      Assert.AreEqual(302.0, row.NonCritAverage.Value, 0.0001);
    }

    [TestMethod]
    public void NoCrits_CritRateZero_NonCritEqualsOverallAvg()
    {
      var records = new List<HealRecord>
      {
        Hot("Drucilla", "Circle of Soothing", 302),
        Hot("Drucilla", "Circle of Soothing", 302),
      };

      var rows = HotEffectivenessAggregator.Aggregate(records, "Drucilla", 65, SpellClass.Dru, EQDataStore.Instance);

      Assert.AreEqual(0, rows[0].CritCount);
      Assert.AreEqual(0.0, rows[0].CritRate, 0.0001);
      Assert.AreEqual(302u, rows[0].MaxHeal);
      Assert.AreEqual(302.0, rows[0].NonCritAverage.Value, 0.0001);
    }

    [TestMethod]
    public void AllCrits_NonCritAverageIsNull()
    {
      var records = new List<HealRecord>
      {
        Hot("Drucilla", "Circle of Soothing", 604, crit: true),
        Hot("Drucilla", "Circle of Soothing", 700, crit: true),
      };

      var rows = HotEffectivenessAggregator.Aggregate(records, "Drucilla", 65, SpellClass.Dru, EQDataStore.Instance);

      Assert.AreEqual(2, rows[0].CritCount);
      Assert.AreEqual(1.0, rows[0].CritRate, 0.0001);
      Assert.IsNull(rows[0].NonCritAverage);
    }
  }
}
