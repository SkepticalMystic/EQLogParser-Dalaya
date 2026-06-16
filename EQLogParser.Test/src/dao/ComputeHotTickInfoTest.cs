using EQLogParser;

namespace EQLogParserTest
{
  // Integration tests for EQDataStore.ComputeHotTickInfo using the real
  // spells.txt + spell-effects.json shipped with the build. Verifies that the
  // sidecar loader, formula port, and Druid 2s-tick override all line up.
  //
  // Spell selections are stable identifiers (id-based) so a Dalaya patch that
  // renames or rebalances values can be caught here as a failing assertion
  // rather than a silent regression. See memory project_dot_hot_validation
  // Phase 2.
  [TestClass]
  public class ComputeHotTickInfoTest
  {
    private EQDataStore _store;

    [TestInitialize]
    public void Setup()
    {
      // Use the production singleton — it has spells.txt and spell-effects.json
      // loaded from the test output directory's data\ folder (copied by the
      // EQLogParser project at build time).
      _store = EQDataStore.Instance;
    }

    private SpellData FindById(string spellId)
    {
      var effects = _store.GetSpellEffects(spellId);
      Assert.IsNotNull(effects, $"Spell {spellId} not found in spell-effects.json — was the converter rerun?");
      // The HotTickInfo method takes a SpellData. We don't have a GetById, but
      // the name lookup with the right level will resolve dual-entry spells.
      var byName = _store.GetSpellByName(effects.Name);
      Assert.IsNotNull(byName, $"Spell '{effects.Name}' not in spells.txt");
      // Synthesize a SpellData with the right Id when name-lookup returns the
      // wrong variant of a dual-entry spell (e.g., Sihala's Empathy 1076 vs 7591).
      return byName.Id == spellId ? byName : new SpellData
      {
        Id = spellId,
        Name = effects.Name,
        Level = byName.Level,
        Duration = byName.Duration,
        IsBeneficial = true
      };
    }

    [TestMethod]
    public void CircleOfSoothing_DruidLevel65_HasHotInfo()
    {
      // id 4989, slot 2: SPA 100, Base1=155, Calc=100 (flat).
      // durationCalc=11, durationBase=6 → CalcDuration(11, 6, 65) = capped at 6 ticks = 36s.
      // Druid → 2s interval → tickCount = 36/2 = 18. totalExpected = 155 * 18 = 2790.
      var spell = FindById("4989");
      var info = _store.ComputeHotTickInfo(spell, casterLevel: 65, casterClass: SpellClass.Dru);

      Assert.IsTrue(info.HasValue, "ComputeHotTickInfo should return a value for Circle of Soothing");
      Assert.AreEqual(155, info.Value.PerTickAmount);
      Assert.AreEqual(2, info.Value.TickIntervalSeconds);
      Assert.AreEqual(18, info.Value.TickCount);
      Assert.AreEqual(2790, info.Value.TotalExpected);
    }

    [TestMethod]
    public void CircleOfSoothing_NonDruidLevel65_Uses6SecondInterval()
    {
      // Same spell, different caster class. Non-Druids get the standard 6s tick.
      // tickCount = 36/6 = 6. totalExpected = 155 * 6 = 930.
      var spell = FindById("4989");
      var info = _store.ComputeHotTickInfo(spell, casterLevel: 65, casterClass: SpellClass.Clr);

      Assert.IsTrue(info.HasValue);
      Assert.AreEqual(155, info.Value.PerTickAmount);
      Assert.AreEqual(6, info.Value.TickIntervalSeconds);
      Assert.AreEqual(6, info.Value.TickCount);
      Assert.AreEqual(930, info.Value.TotalExpected);
    }

    [TestMethod]
    public void RunicSpinedResurgence_DruidLevel65_PerTick225()
    {
      // id 4096, slot 2: SPA 100, Base1=225, Calc=100. durationCalc=11, durationBase=6.
      var spell = FindById("4096");
      var info = _store.ComputeHotTickInfo(spell, casterLevel: 65, casterClass: SpellClass.Dru);

      Assert.IsTrue(info.HasValue);
      Assert.AreEqual(225, info.Value.PerTickAmount);
      Assert.AreEqual(2, info.Value.TickIntervalSeconds);
      Assert.AreEqual(18, info.Value.TickCount);
    }

    [TestMethod]
    public void RelicSihalasEmpathy_AutocastVariant_PerTick200()
    {
      // id 7591 (autocast variant), slot 1: SPA 100, Base1=200, Calc=100.
      // durationCalc=11, durationBase=3 → CalcDuration capped at 3 ticks = 18s.
      // Druid: 18/2 = 9 ticks. totalExpected = 200 * 9 = 1800.
      var spell = FindById("7591");
      var info = _store.ComputeHotTickInfo(spell, casterLevel: 65, casterClass: SpellClass.Dru);

      Assert.IsTrue(info.HasValue);
      Assert.AreEqual(200, info.Value.PerTickAmount);
      Assert.AreEqual(2, info.Value.TickIntervalSeconds);
      Assert.AreEqual(9, info.Value.TickCount);
      Assert.AreEqual(1800, info.Value.TotalExpected);
    }

    [TestMethod]
    public void RacingFlames_DurationCalcZero_FallsBackToDurationBase()
    {
      // id 4479, slot 4: SPA 100, Base1=40, Calc=100 (flat) → perTick 40.
      // durationCalc=0 makes CalcDuration return 0, but durationBase=3 is the real
      // Dalaya-tuned duration. The fallback uses it (3 ticks = 18s); without it the
      // HoT Effectiveness view would report a zero tick count / zero expected heal.
      var spell = FindById("4479");
      var info = _store.ComputeHotTickInfo(spell, casterLevel: 65, casterClass: SpellClass.Clr);

      Assert.IsTrue(info.HasValue, "calc=0 HoT should still compute via DurationBase fallback");
      Assert.AreEqual(40, info.Value.PerTickAmount);
      Assert.AreEqual(6, info.Value.TickIntervalSeconds);
      Assert.AreEqual(3, info.Value.TickCount);
      Assert.AreEqual(120, info.Value.TotalExpected);
    }

    [TestMethod]
    public void DirectHealSpell_ReturnsNull()
    {
      // id 12, "Healing" — instant heal: SPA 0 with a value but durationBase=0.
      // The SPA-0 fallback is guarded by duration > 0, so this must stay null.
      var spell = new SpellData { Id = "12", Name = "Healing", Level = 14 };
      var info = _store.ComputeHotTickInfo(spell, casterLevel: 14, casterClass: SpellClass.Clr);
      Assert.IsFalse(info.HasValue);
    }

    [TestMethod]
    public void Regeneration_ClassicSpa0Regen_HasHotInfo()
    {
      // id 144, "Regeneration" — classic SPA-0 regen: base1=5, calc=100 (flat),
      // durationCalc=99 → default branch → value = durationBase = 102 ticks = 612s.
      // Non-Druid: 6s interval → tickCount=102, totalExpected=510.
      var spell = FindById("144");
      var info = _store.ComputeHotTickInfo(spell, casterLevel: 65, casterClass: SpellClass.Clr);

      Assert.IsTrue(info.HasValue, "Regeneration (SPA 0 regen) should return HotTickInfo");
      Assert.AreEqual(5, info.Value.PerTickAmount);
      Assert.AreEqual(6, info.Value.TickIntervalSeconds);
      Assert.AreEqual(102, info.Value.TickCount);
      Assert.AreEqual(510, info.Value.TotalExpected);
    }

    [TestMethod]
    public void PackChloroplast_ClassicSpa0Regen_HasHotInfo()
    {
      // id 138, "Pack Chloroplast" — SPA 0, base1=10, calc=100, durationBase=102.
      // Same duration/interval math as Regeneration but base=10.
      var spell = FindById("138");
      var info = _store.ComputeHotTickInfo(spell, casterLevel: 65, casterClass: SpellClass.Clr);

      Assert.IsTrue(info.HasValue, "Pack Chloroplast (SPA 0 regen) should return HotTickInfo");
      Assert.AreEqual(10, info.Value.PerTickAmount);
      Assert.AreEqual(102, info.Value.TickCount);
      Assert.AreEqual(1020, info.Value.TotalExpected);
    }

    [TestMethod]
    public void RelicSihalasEmpathy_PlayerCastVariant_ReturnsNull()
    {
      // id 1076 (player-cast). Slot 0 = SPA 79 (instant heal), slot 1 = SPA 153
      // (autocast). No SPA 100 slot → not classified as a HoT here. The actual
      // HoT effect lives on id 7591 (the autocast target).
      var spell = new SpellData { Id = "1076", Name = "Relic: Sihala's Empathy", Level = 65 };
      var info = _store.ComputeHotTickInfo(spell, casterLevel: 65, casterClass: SpellClass.Dru);
      Assert.IsFalse(info.HasValue);
    }

    [TestMethod]
    public void NullSpell_ReturnsNull()
    {
      var info = _store.ComputeHotTickInfo((SpellData)null, casterLevel: 65, casterClass: SpellClass.Dru);
      Assert.IsFalse(info.HasValue);
    }

    // GetHealingSpellByName regression coverage. Old `Damaging < 0` filter never
    // matched Dalaya spells (convert_spells.py only emits 0 or 1); the corrected
    // filter `IsBeneficial && Damaging > 0` should now return entries for known
    // healing spells. HealingValidator uses this to read Target for AoE filtering.

    [TestMethod]
    public void GetHealingSpellByName_KnownHotReturnsEntry()
    {
      var result = _store.GetHealingSpellByName("Circle of Soothing");
      Assert.IsNotNull(result, "Circle of Soothing should be returned (beneficial, with log messages)");
      Assert.IsTrue(result.IsBeneficial);
    }

    [TestMethod]
    public void GetHealingSpellByName_KnownDirectHealReturnsEntry()
    {
      // Duration=0 doesn't disqualify — the filter is just IsBeneficial + Damaging,
      // not "is a HoT specifically." HealingValidator needs Target for any heal.
      var result = _store.GetHealingSpellByName("Relic: Sihala's Empathy");
      Assert.IsNotNull(result);
      Assert.IsTrue(result.IsBeneficial);
    }

    [TestMethod]
    public void GetHealingSpellByName_UnknownSpellReturnsNull()
    {
      var result = _store.GetHealingSpellByName("Definitely Not A Spell");
      Assert.IsNull(result);
    }

    [TestMethod]
    public void NullSpellName_ReturnsNull()
    {
      var info = _store.ComputeHotTickInfo((string)null, casterLevel: 65, casterClass: SpellClass.Dru);
      Assert.IsFalse(info.HasValue);
    }

    [TestMethod]
    public void NameOverload_ResolvesDualEntryAutocastSpell()
    {
      // Name-based overload routes through GetHotSpellByName, which prefers the
      // Duration > 0 entry. For "Relic: Sihala's Empathy" that's id 7591, not the
      // player-cast id 1076 (which has no SPA 100 slot).
      var info = _store.ComputeHotTickInfo("Relic: Sihala's Empathy", casterLevel: 65, casterClass: SpellClass.Dru);
      Assert.IsTrue(info.HasValue, "Name overload should resolve to the autocast variant with SPA 100");
      Assert.AreEqual(200, info.Value.PerTickAmount);
    }
  }
}
