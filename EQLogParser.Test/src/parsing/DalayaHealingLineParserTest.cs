using EQLogParser;

namespace EQLogParserTest
{
  [TestClass]
  public class DalayaHealingLineParserTest
  {
    // Dalaya's healing log format differs significantly from live EQ. The base
    // HealingLineParser was built only for live EQ's "<Healer> healed <Target> for N
    // hit points by <Spell>." shape. These tests pin the Dalaya variants:
    //   1. "Your <Spell> healed <Target> for N damage."  (spell in prefix, "damage" not "hit points")
    //   2. "You healed <Target> for N damage."           (direct heal, no spell)
    //   3. "<Healer> has healed you for N points of damage." (observed third-party)
    // Pattern (1) accounts for ~75% of healing events in real Dalaya logs.

    private HealingLineParser _parser;

    [TestInitialize]
    public void Setup()
    {
      ConfigUtil.PlayerName = "Drucilla";
      // Fresh PlayerRegistry instance per test so we control who's verified.
      var registry = new PlayerRegistry(autoSave: false) { PlayerName = "Drucilla" };
      _parser = new HealingLineParser(registry);
    }

    // =============== Pattern 1: "Your <Spell> healed <Target> for N damage." ===============

    [TestMethod]
    public void YourSpell_SingleWordSpell()
    {
      var r = _parser.ParseLine("Your Healing healed Geralt for 500 damage.");
      Assert.IsNotNull(r);
      Assert.AreEqual("Drucilla", r.Healer);
      Assert.AreEqual("Geralt", r.Healed);
      Assert.AreEqual(500u, r.Total);
      Assert.AreEqual("Healing", r.SubType);
      Assert.AreEqual(Labels.Heal, r.Type);
    }

    [TestMethod]
    public void YourSpell_MultiWordSpell()
    {
      var r = _parser.ParseLine("Your Circle of Soothing healed Berenstein for 604 damage.");
      Assert.IsNotNull(r);
      Assert.AreEqual("Drucilla", r.Healer);
      Assert.AreEqual("Berenstein", r.Healed);
      Assert.AreEqual(604u, r.Total);
      Assert.AreEqual("Circle of Soothing", r.SubType);
    }

    [TestMethod]
    public void YourSpell_RunicWithColon()
    {
      // Item/relic procs use "<Category>: <Name>" format with embedded colon.
      var r = _parser.ParseLine("Your Runic: Spined Resurgence healed Geralt for 878 damage.");
      Assert.IsNotNull(r);
      Assert.AreEqual("Drucilla", r.Healer);
      Assert.AreEqual("Geralt", r.Healed);
      Assert.AreEqual(878u, r.Total);
      Assert.AreEqual("Runic: Spined Resurgence", r.SubType);
    }

    [TestMethod]
    public void YourSpell_RelicWithApostrophe()
    {
      var r = _parser.ParseLine("Your Relic: Sihala's Empathy healed Berenstein for 423 damage.");
      Assert.IsNotNull(r);
      Assert.AreEqual("Drucilla", r.Healer);
      Assert.AreEqual("Berenstein", r.Healed);
      Assert.AreEqual(423u, r.Total);
      Assert.AreEqual("Relic: Sihala's Empathy", r.SubType);
    }

    [TestMethod]
    public void YourSpell_PetTargetWithTrailingSpace()
    {
      // Dalaya named pets have a trailing space in the log; preserve it as the
      // pet-detection signal (same convention as DamageLineParser).
      var r = _parser.ParseLine("Your Circle of Soothing healed Bonaparte  for 302 damage.");
      Assert.IsNotNull(r);
      Assert.AreEqual("Drucilla", r.Healer);
      Assert.AreEqual("Bonaparte ", r.Healed);
      Assert.AreEqual(302u, r.Total);
      Assert.AreEqual("Circle of Soothing", r.SubType);
    }

    [TestMethod]
    public void YourSpell_LargeNumber()
    {
      var r = _parser.ParseLine("Your Mega Heal healed Geralt for 99999 damage.");
      Assert.IsNotNull(r);
      Assert.AreEqual(99999u, r.Total);
    }

    // =============== Pattern 2: "You healed <Target> for N damage." (baseline regression) ===============

    [TestMethod]
    public void YouHealed_DirectNoSpell()
    {
      var r = _parser.ParseLine("You healed Illi for 1788 damage.");
      Assert.IsNotNull(r);
      Assert.AreEqual("Drucilla", r.Healer);
      Assert.AreEqual("Illi", r.Healed);
      Assert.AreEqual(1788u, r.Total);
      // No spell name in this format — subType falls back to SelfHeal sentinel.
      Assert.AreEqual(Labels.SelfHeal, r.SubType);
    }

    [TestMethod]
    public void YouHealed_PetTargetWithTrailingSpace()
    {
      var r = _parser.ParseLine("You healed Bonaparte  for 1992 damage.");
      Assert.IsNotNull(r);
      Assert.AreEqual("Drucilla", r.Healer);
      Assert.AreEqual("Bonaparte ", r.Healed);
      Assert.AreEqual(1992u, r.Total);
    }

    // =============== Pattern 3: "<Healer> has healed you for N points of damage." (baseline regression) ===============

    [TestMethod]
    public void HasHealedYou_ThirdPartyObserved()
    {
      var r = _parser.ParseLine("Condray has healed you for 1441 points of damage.");
      Assert.IsNotNull(r);
      Assert.AreEqual("Condray", r.Healer);
      Assert.AreEqual("Drucilla", r.Healed);
      Assert.AreEqual(1441u, r.Total);
    }

    [TestMethod]
    public void HasHealedYou_NpcSource()
    {
      // Mob/clicky/etc. healing the player — still attribute to the named source.
      var r = _parser.ParseLine("Occatio has healed you for 3911 points of damage.");
      Assert.IsNotNull(r);
      Assert.AreEqual("Occatio", r.Healer);
      Assert.AreEqual("Drucilla", r.Healed);
      Assert.AreEqual(3911u, r.Total);
    }

    // =============== Live-EQ regression guards ===============

    [TestMethod]
    public void LiveEq_WardHealsBreaks_StillAttributesToYou()
    {
      // Live EQ: "Your ward heals you as it breaks! You healed Niktaza for N hit points by Healing Ward."
      // The .! branch must still win over the new "Your " fallback so this stays attributed
      // to the player, not to the literal "ward heals you as it breaks! You" spell name.
      var r = _parser.ParseLine("Your ward heals you as it breaks! You healed Niktaza for 8970 (86306) hit points by Healing Ward.");
      Assert.IsNotNull(r);
      Assert.AreEqual("Drucilla", r.Healer);
      Assert.AreEqual("Niktaza", r.Healed);
      Assert.AreEqual(8970u, r.Total);
      Assert.AreEqual(86306u, r.OverTotal);
      Assert.AreEqual("Healing Ward", r.SubType);
    }

    [TestMethod]
    public void LiveEq_ThirdPartyHealedTargetBySpell()
    {
      var r = _parser.ParseLine("Fllint healed Foob for 11820 hit points by Blessing of the Ancients III.");
      Assert.IsNotNull(r);
      Assert.AreEqual("Fllint", r.Healer);
      Assert.AreEqual("Foob", r.Healed);
      Assert.AreEqual(11820u, r.Total);
      Assert.AreEqual("Blessing of the Ancients III", r.SubType);
    }

    // =============== Pattern 4: "<Healer> performs an exceptional heal! (N)" (Phase 2) ===============
    // Exceptional-heal announcements don't produce records of their own — they pair to an
    // observable heal record (same healer + same amount, within 1s) and set the Crit
    // modifier bit. Announcements with no observable companion record (e.g., other-
    // raider crits on targets we can't see) are discarded, matching how damage handles
    // unobservable third-party crits.

    [TestMethod]
    public void Exceptional_ReturnsNullByItself()
    {
      // The announcement alone produces no record — only modifies a paired heal.
      Assert.IsNull(_parser.ParseLine("Condray performs an exceptional heal! (4918)", beginTime: 100.0));
    }

    [TestMethod]
    public void Exceptional_AnnouncementBeforeHeal_AppliesCrit()
    {
      // Announcement arrives first, heal record arrives within window → crit pairs.
      Assert.IsNull(_parser.ParseLine("Drucilla performs an exceptional heal! (1788)", beginTime: 100.0));
      var r = _parser.ParseLine("You healed Illi for 1788 damage.", beginTime: 100.0);
      Assert.IsNotNull(r);
      Assert.AreEqual("Drucilla", r.Healer);
      Assert.AreEqual(1788u, r.Total);
      Assert.IsTrue(LineModifiersParser.IsCrit(r.ModifiersMask));
    }

    [TestMethod]
    public void Exceptional_HealBeforeAnnouncement_AppliesCrit()
    {
      // Heal record stored first, then announcement arrives within window → crit pairs.
      var r = _parser.ParseLine("Condray has healed you for 1441 points of damage.", beginTime: 100.0);
      Assert.IsNotNull(r);
      Assert.IsFalse(LineModifiersParser.IsCrit(r.ModifiersMask));
      _parser.ParseLine("Condray performs an exceptional heal! (1441)", beginTime: 100.0);
      Assert.IsTrue(LineModifiersParser.IsCrit(r.ModifiersMask), "Crit should be applied to the previously stored heal record");
    }

    [TestMethod]
    public void Exceptional_ZeroAmountIgnored()
    {
      // (0) means "no crit on this cast" — should not affect any nearby heal record.
      var r = _parser.ParseLine("Condray has healed you for 1441 points of damage.", beginTime: 100.0);
      _parser.ParseLine("Condray performs an exceptional heal! (0)", beginTime: 100.0);
      Assert.IsFalse(LineModifiersParser.IsCrit(r.ModifiersMask));
    }

    [TestMethod]
    public void Exceptional_AmountMismatch_NoCrit()
    {
      // Different amount within window — must not crit. Off-by-N pairings would be
      // wrong attribution (e.g., a different cast on a different target).
      var r = _parser.ParseLine("Condray has healed you for 790 points of damage.", beginTime: 100.0);
      _parser.ParseLine("Condray performs an exceptional heal! (3252)", beginTime: 100.0);
      Assert.IsFalse(LineModifiersParser.IsCrit(r.ModifiersMask));
    }

    [TestMethod]
    public void Exceptional_HealerMismatch_NoCrit()
    {
      var r = _parser.ParseLine("Condray has healed you for 1441 points of damage.", beginTime: 100.0);
      _parser.ParseLine("Cassian performs an exceptional heal! (1441)", beginTime: 100.0);
      Assert.IsFalse(LineModifiersParser.IsCrit(r.ModifiersMask));
    }

    [TestMethod]
    public void Exceptional_OutsideWindow_NoCrit()
    {
      // Announcement more than 1s after the heal — pairing fails.
      var r = _parser.ParseLine("Condray has healed you for 1441 points of damage.", beginTime: 100.0);
      _parser.ParseLine("Condray performs an exceptional heal! (1441)", beginTime: 102.0);
      Assert.IsFalse(LineModifiersParser.IsCrit(r.ModifiersMask));
    }

    [TestMethod]
    public void Exceptional_UnpairedAnnouncement_DiscardedSilently()
    {
      // Other-raider crit on a target we can't see — no record to attach. Just discard.
      Assert.IsNull(_parser.ParseLine("Puckin performs an exceptional heal! (4573)", beginTime: 100.0));
      // No state changes anything subsequent.
      var r = _parser.ParseLine("You healed Illi for 500 damage.", beginTime: 100.5);
      Assert.IsNotNull(r);
      Assert.IsFalse(LineModifiersParser.IsCrit(r.ModifiersMask));
    }

    [TestMethod]
    public void Exceptional_DrucillaSpellHealCrit_AppliesCrit()
    {
      // Real-log shape: "Drucilla performs..." paired with "Your <Spell> healed X" line.
      Assert.IsNull(_parser.ParseLine("Drucilla performs an exceptional heal! (910)", beginTime: 100.0));
      var r = _parser.ParseLine("Your Runic: Spined Resurgence healed Illi for 910 damage.", beginTime: 100.0);
      Assert.IsNotNull(r);
      Assert.AreEqual("Drucilla", r.Healer);
      Assert.AreEqual("Runic: Spined Resurgence", r.SubType);
      Assert.IsTrue(LineModifiersParser.IsCrit(r.ModifiersMask));
    }

    [TestMethod]
    public void LiveEq_ThirdPartyHealedOverTime()
    {
      var r = _parser.ParseLine("Snowzz healed Malkatar over time for 8211 hit points by Roar of the Lion 6.");
      Assert.IsNotNull(r);
      Assert.AreEqual("Snowzz", r.Healer);
      Assert.AreEqual("Malkatar", r.Healed);
      Assert.AreEqual(8211u, r.Total);
      Assert.AreEqual(Labels.Hot, r.Type);
      Assert.AreEqual("Roar of the Lion 6", r.SubType);
    }
  }
}
