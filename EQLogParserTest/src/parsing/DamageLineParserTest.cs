using System.Collections.Generic;
using System.Linq;
using EQLogParser;

namespace EQLogParserTest
{
  [TestClass]
  public class DamageLineParserTest
  {
    // These tests target Dalaya behavior (EMU Parsing toggle OFF). Dalaya's log format
    // for spell damage is "X has taken N damage from AttackerName by SpellName." which
    // is the reverse of live EQ; the parser always swaps the two fields when the EMU
    // toggle is off.

    [TestInitialize]
    public void Setup()
    {
      MainWindow.IsEmuParsingEnabled = false;
      ConfigUtil.PlayerName = "Selfie";
      DamageLineParser.Instance.ResetParseState();
    }

    // =============== Melee hits (attacker verb defender) ===============

    [TestMethod]
    public void Melee_YouCrushNpc()
    {
      var r = DamageLineParser.Instance.ParseLine("You crush Ogna, Artisan of War for 20581 points of damage. (Lucky Critical)");
      Assert.IsNotNull(r);
      Assert.AreEqual("Selfie", r.Attacker);
      Assert.AreEqual("Ogna, Artisan of War", r.Defender);
      Assert.AreEqual(20581u, r.Total);
      Assert.AreEqual(Labels.Melee, r.Type);
    }

    [TestMethod]
    public void Melee_ThirdPartyCrushesNpc()
    {
      var r = DamageLineParser.Instance.ParseLine("Useless crushes an abyssal terror for 9022 points of damage.");
      Assert.IsNotNull(r);
      Assert.AreEqual("Useless", r.Attacker);
      Assert.AreEqual("An abyssal terror", r.Defender);  // NPC names have first-letter uppercased
      Assert.AreEqual(9022u, r.Total);
      Assert.AreEqual(Labels.Melee, r.Type);
    }

    [TestMethod]
    public void Melee_NpcClawsPlayer()
    {
      var r = DamageLineParser.Instance.ParseLine("Susarrak the Crusader claws Villette for 27699 points of damage. (Strikethrough Wild Rampage)");
      Assert.IsNotNull(r);
      Assert.AreEqual("Susarrak the Crusader", r.Attacker);
      Assert.AreEqual("Villette", r.Defender);
      Assert.AreEqual(27699u, r.Total);
    }

    [TestMethod]
    public void Melee_StrikethroughCriticalModifier()
    {
      var r = DamageLineParser.Instance.ParseLine("Astralx crushes Sontalak for 126225 points of damage. (Strikethrough Critical)");
      Assert.IsNotNull(r);
      Assert.AreEqual("Astralx", r.Attacker);
      Assert.AreEqual("Sontalak", r.Defender);
      Assert.AreEqual(126225u, r.Total);
      // Modifier mask is non-zero when modifiers parsed.
      Assert.IsTrue(r.ModifiersMask != 0);
    }

    // =============== Spell damage (hit by format) ===============

    [TestMethod]
    public void Spell_YouHitWithSpell()
    {
      var r = DamageLineParser.Instance.ParseLine("You hit a treant for 1633489 points of magic damage by Chromospheric Vortex Rk. II. (Lucky Critical)");
      Assert.IsNotNull(r);
      Assert.AreEqual("Selfie", r.Attacker);
      Assert.AreEqual("A treant", r.Defender);
      Assert.AreEqual(1633489u, r.Total);
      Assert.AreEqual("Chromospheric Vortex Rk. II", r.SubType);
    }

    [TestMethod]
    public void Spell_ThirdPartyHitWithSpell()
    {
      var r = DamageLineParser.Instance.ParseLine("Sonozen hit Jortreva the Crusader for 38948 points of fire damage by Burst of Flames. (Lucky Critical Twincast)");
      Assert.IsNotNull(r);
      Assert.AreEqual("Sonozen", r.Attacker);
      Assert.AreEqual("Jortreva the Crusader", r.Defender);
      Assert.AreEqual(38948u, r.Total);
      Assert.AreEqual("Burst of Flames", r.SubType);
    }

    // =============== Dalaya-specific: "has taken ... from X by Y" (attacker/spell swapped vs live EQ) ===============

    [TestMethod]
    public void DalayaReversed_NpcTakenFromAttackerBySpell()
    {
      // Dalaya writes "from ATTACKER by SPELL" (reverse of live EQ).
      // The parser always swaps when EMU toggle is off: Attacker ends up in the record's Attacker field.
      var r = DamageLineParser.Instance.ParseLine("Dovhesi has taken 173674 damage from Grendish the Crusader by Curse of the Shrine.");
      Assert.IsNotNull(r);
      Assert.AreEqual("Dovhesi", r.Defender);
      Assert.AreEqual("Grendish the Crusader", r.Attacker);
      Assert.AreEqual("Curse of the Shrine", r.SubType);
      Assert.AreEqual(173674u, r.Total);
    }

    [TestMethod]
    public void DalayaReversed_NpcTakenFromPlayerBySpellWithCrit()
    {
      var r = DamageLineParser.Instance.ParseLine("Grendish the Crusader has taken 1003231 damage from Atvar by Pyre of Klraggek Rk. III. (Lucky Critical)");
      Assert.IsNotNull(r);
      Assert.AreEqual("Grendish the Crusader", r.Defender);
      Assert.AreEqual("Atvar", r.Attacker);
      Assert.AreEqual("Pyre of Klraggek Rk. III", r.SubType);
      Assert.AreEqual(1003231u, r.Total);
    }

    // =============== Dalaya-specific: self-damage "from your SpellName" ===============

    [TestMethod]
    public void DalayaSelfDamage_NpcTakenFromYourSpell()
    {
      // "X has taken N damage from your SpellName." — this is self-cast damage in Dalaya.
      // Defender = X (NPC), Attacker should resolve to the player ("Selfie" via ConfigUtil.PlayerName).
      var r = DamageLineParser.Instance.ParseLine("A gnoll has taken 108790 damage from your Mind Coil Rk. II.");
      Assert.IsNotNull(r);
      Assert.AreEqual("A gnoll", r.Defender);
      Assert.AreEqual("Selfie", r.Attacker);
      Assert.AreEqual(108790u, r.Total);
    }

    [TestMethod]
    public void DalayaSelfDamage_ResolvesToInjectedPlayerName()
    {
      // Multi-log Raid Damage view: each source's parser is given the source player's name
      // via PlayerManager.PlayerName. "from your X" must resolve to the source's name, not
      // the live ConfigUtil.PlayerName, otherwise self-cast damage from non-active sources
      // gets attributed to the live user and dedup against third-person observers fails.
      var pm = new PlayerManager(autoSave: false);
      pm.PlayerName = "Ezran";
      var dlp = new EQLogParser.DamageLineParser(new DataManager(), pm);

      var r = dlp.ParseLine("A gnoll has taken 108790 damage from your Mind Coil Rk. II.");
      Assert.IsNotNull(r);
      Assert.AreEqual("A gnoll", r.Defender);
      Assert.AreEqual("Ezran", r.Attacker, "Self-cast attacker must come from injected PlayerManager.PlayerName");
      Assert.AreEqual(108790u, r.Total);
    }

    [TestMethod]
    public void DalayaSelfDamage_FallsBackToConfigUtilWhenNoOverride()
    {
      // When the override isn't set, PlayerManager.PlayerName falls back to ConfigUtil.PlayerName.
      // Pin this so the live tailing flow keeps working — no caller is required to set the
      // override explicitly.
      var pm = new PlayerManager(autoSave: false);
      // No PlayerName override.
      var dlp = new EQLogParser.DamageLineParser(new DataManager(), pm);

      var r = dlp.ParseLine("A gnoll has taken 108790 damage from your Mind Coil Rk. II.");
      Assert.IsNotNull(r);
      Assert.AreEqual("Selfie", r.Attacker, "Default fallback to ConfigUtil.PlayerName (set in TestInitialize)");
    }

    [TestMethod]
    public void TwoIsolatedParsers_DoNotCrossContaminatePlayerName()
    {
      // Two isolated parse contexts with different PlayerName overrides must produce attacker
      // values matching their respective sources. Static state would leak between sources and
      // break the multi-source merge — guard against that regression.
      var pmA = new PlayerManager(autoSave: false) { PlayerName = "PlayerA" };
      var pmB = new PlayerManager(autoSave: false) { PlayerName = "PlayerB" };
      var dlpA = new EQLogParser.DamageLineParser(new DataManager(), pmA);
      var dlpB = new EQLogParser.DamageLineParser(new DataManager(), pmB);

      var rA = dlpA.ParseLine("A gnoll has taken 100 damage from your Test Spell.");
      var rB = dlpB.ParseLine("A gnoll has taken 100 damage from your Test Spell.");

      Assert.AreEqual("PlayerA", rA.Attacker);
      Assert.AreEqual("PlayerB", rB.Attacker);
    }

    // =============== Misses / dodges / parries / blocks / invulnerable ===============

    [TestMethod]
    public void Miss_YouTryButMiss()
    {
      var r = DamageLineParser.Instance.ParseLine("You try to crush a Kar`Zok soldier, but miss! (Riposte Strikethrough)");
      Assert.IsNotNull(r);
      Assert.AreEqual("Selfie", r.Attacker);
      Assert.AreEqual("A Kar`Zok soldier", r.Defender);
      Assert.AreEqual(Labels.Miss, r.Type);
    }

    [TestMethod]
    public void Miss_NpcTriesToPunchYouButYouDodge()
    {
      var r = DamageLineParser.Instance.ParseLine("Test One Hundred Three tries to punch YOU, but YOU dodge!");
      Assert.IsNotNull(r);
      Assert.AreEqual("Test One Hundred Three", r.Attacker);
      Assert.AreEqual("Selfie", r.Defender);
      Assert.AreEqual(Labels.Dodge, r.Type);
    }

    [TestMethod]
    public void Miss_NpcParries()
    {
      var r = DamageLineParser.Instance.ParseLine("Romance tries to bash Vulak`Aerr, but Vulak`Aerr parries!");
      Assert.IsNotNull(r);
      Assert.AreEqual("Romance", r.Attacker);
      Assert.AreEqual("Vulak`Aerr", r.Defender);
      Assert.AreEqual(Labels.Parry, r.Type);
    }

    [TestMethod]
    public void Miss_NpcRiposte()
    {
      var r = DamageLineParser.Instance.ParseLine("A bloodthirsty gnawer tries to bite Vandil, but Vandil ripostes!");
      Assert.IsNotNull(r);
      Assert.AreEqual("A bloodthirsty gnawer", r.Attacker);
      Assert.AreEqual("Vandil", r.Defender);
      Assert.AreEqual(Labels.Riposte, r.Type);
    }

    [TestMethod]
    public void Miss_RiposteWithStrikethroughIsNotAMiss()
    {
      // A riposte SUFFIXED with (Strikethrough) means the attacker's hit struck through the riposte.
      // The parser intentionally does NOT classify this as a miss — the damage landed and will appear
      // on a separate line. Pinning this behavior so the refactor doesn't silently change it.
      var r = DamageLineParser.Instance.ParseLine("Zelnithak tries to hit Fllint, but Fllint ripostes! (Strikethrough)");
      Assert.IsNull(r);
    }

    [TestMethod]
    public void Miss_Invulnerable()
    {
      var r = DamageLineParser.Instance.ParseLine("Tolzol tries to crush Dendritic Golem, but Dendritic Golem is INVULNERABLE!");
      Assert.IsNotNull(r);
      Assert.AreEqual("Tolzol", r.Attacker);
      Assert.AreEqual("Dendritic Golem", r.Defender);
      Assert.AreEqual(Labels.Invulnerable, r.Type);
    }

    [TestMethod]
    public void Miss_NpcBlocks()
    {
      var r = DamageLineParser.Instance.ParseLine("You try to crush a desert madman, but a desert madman blocks!");
      Assert.IsNotNull(r);
      Assert.AreEqual("Selfie", r.Attacker);
      Assert.AreEqual("A desert madman", r.Defender);
      Assert.AreEqual(Labels.Block, r.Type);
    }

    // =============== DoT "taken from SPELL by ATTACKER" (live-EQ style, NOT Dalaya-swapped) ===============
    // Note: On Dalaya (EMU toggle off), the parser always swaps. So if live-EQ format is fed in, fields
    // will be swapped in the output. Keeping a test to pin down current behavior so the refactor doesn't
    // silently change it.

    [TestMethod]
    public void HasTakenSwapBehavior_RecordsSwappedFields()
    {
      var r = DamageLineParser.Instance.ParseLine("You have taken 4852 damage from Nectar of Misery by Commander Gartik.");
      Assert.IsNotNull(r);
      Assert.AreEqual("Selfie", r.Defender);
      // With swap applied, "Nectar of Misery" (between from and by) ends up in Attacker.
      Assert.AreEqual("Nectar of Misery", r.Attacker);
      Assert.AreEqual("Commander Gartik", r.SubType);
      Assert.AreEqual(4852u, r.Total);
    }

    // =============== Dalaya double-space pet attribution ===============

    [TestMethod]
    public void DalayaPet_DoubleSpaceMarksAttackerAsPet()
    {
      // On Dalaya, named pets (Bonaparte, Fireballs, Otto, Weens) are logged with a trailing
      // extra space before "by". When Split(' ') tokenizes this, it produces an empty token
      // that joins back as a trailing space. The parser uses that trailing space to classify
      // the attacker as a verified pet.
      //
      // The parser does NOT auto-attribute ownership to ConfigUtil.PlayerName — doing so
      // corrupted pet-owner mappings in raid logs where other players' pets appear. Ownership
      // is established via petmapping.txt, ChatManager's "My leader is X" detection, or the
      // Pet Owners UI.
      var probePet = "TestProbePet_Unique";
      Assert.IsFalse(PlayerManager.Instance.IsVerifiedPet(probePet));

      var r = DamageLineParser.Instance.ParseLine($"A gnoll has taken 108790 damage from {probePet}  by Gale of Blades.");
      Assert.IsNotNull(r);
      Assert.AreEqual(probePet, r.Attacker);
      Assert.AreEqual("Gale of Blades", r.SubType);
      Assert.AreEqual(108790u, r.Total);

      // Verified pet — yes.
      Assert.IsTrue(PlayerManager.Instance.IsVerifiedPet(probePet));
      // No owner auto-assigned. GetPlayerFromPet returns either null or Labels.Unassigned.
      var owner = PlayerManager.Instance.GetPlayerFromPet(probePet);
      Assert.IsTrue(string.IsNullOrEmpty(owner) || owner == Labels.Unassigned,
        $"Pet should have no auto-attributed owner, got: {owner}");
    }

    [TestMethod]
    public void DalayaPet_NonMeleeDdTrimsTrailingSpace()
    {
      // Dalaya named pet DD spells (e.g. Blood Fountain on Bonaparte's direct-damage proc) log
      // as "<Pet>  hit <NPC> for <N> points of non-melee damage." with a double-space after the
      // pet name. This routes through the old-eqemu DD branch; without TrimEnd on the assembled
      // attacker, the record stores "Bonaparte " while melee/DoT records store "Bonaparte",
      // producing two separate DPS Summary rows for the same pet.
      var r = DamageLineParser.Instance.ParseLine("Bonaparte  hit a high dreadguard for 267 points of non-melee damage.");
      Assert.IsNotNull(r);
      Assert.AreEqual("Bonaparte", r.Attacker);
      Assert.AreEqual("A high dreadguard", r.Defender);
      Assert.AreEqual(267u, r.Total);
      Assert.AreEqual(Labels.Dd, r.Type);
    }

    [TestMethod]
    public void DalayaPet_MissRecordTrimsTrailingSpace()
    {
      // Dalaya pet misses ("Bonaparte  tries to hit NPC, but NPC is INVULNERABLE!") carry a
      // double-space after the pet name. The try/but miss branch emits a damage=0 record with
      // the attacker name verbatim. Even zero-damage records flow into NpcDamageManager's time
      // segments via AddPlayerTime, which populates _playerTimeRanges — so a "Bonaparte "
      // attacker produces a phantom DPS Summary row with only a Sec value and no damage.
      var r = DamageLineParser.Instance.ParseLine("Bonaparte  tries to hit Mistress Saitha, but Mistress Saitha is INVULNERABLE!");
      Assert.IsNotNull(r);
      Assert.AreEqual("Bonaparte", r.Attacker);
      Assert.AreEqual("Mistress Saitha", r.Defender);
      Assert.AreEqual(0u, r.Total);
      Assert.AreEqual(Labels.Invulnerable, r.Type);
    }

    [TestMethod]
    public void DalayaPlayer_NoTrailingSpaceMarksAttackerAsPlayer()
    {
      // Regular player names never have a trailing space. The parser registers them as
      // verified players, not as pets of the current user.
      var r = DamageLineParser.Instance.ParseLine("A gnoll has taken 50000 damage from Raiddmate by Ice Comet.");
      Assert.IsNotNull(r);
      Assert.AreEqual("Raiddmate", r.Attacker);
      Assert.AreEqual("Ice Comet", r.SubType);
      Assert.IsFalse(PlayerManager.Instance.IsVerifiedPet("Raiddmate"));
    }

    // =============== Crit modifier ===============

    [TestMethod]
    public void Crit_StrikethroughCriticalSetsModifierMask()
    {
      var r = DamageLineParser.Instance.ParseLine("Astralx crushes Sontalak for 126225 points of damage. (Strikethrough Critical)");
      Assert.IsNotNull(r);
      Assert.IsTrue(LineModifiersParser.IsCrit(r.ModifiersMask), "Expected Crit flag set by modifier parser");
    }

    [TestMethod]
    public void Crit_LuckyCriticalSetsModifierMask()
    {
      var r = DamageLineParser.Instance.ParseLine("You crush Ogna, Artisan of War for 20581 points of damage. (Lucky Critical)");
      Assert.IsNotNull(r);
      Assert.IsTrue(LineModifiersParser.IsCrit(r.ModifiersMask));
    }

    // =============== Dalaya DS two-line pending pattern (exercises _pendingDs static state) ===============

    [TestMethod]
    public void DalayaDs_TwoLineSequenceEmitsDsRecordViaEvent()
    {
      // Dalaya DS mechanic: the DS proc line appears BEFORE the melee hit.
      // Parser stashes it in _pendingDs, then resolves it when the matching melee line arrives
      // within 1 second, emitting a DS DamageRecord via EventsDamageProcessed.
      var captured = new List<DamageProcessedEvent>();
      void Handler(DamageProcessedEvent e) { captured.Add(e); }
      DamageLineParser.Instance.EventsDamageProcessed += Handler;
      try
      {
        ProcessLine("Mistress Saitha was hit by non-melee for 45 points of damage.", 1000.0);
        ProcessLine("Mistress Saitha crushes Tank for 500 points of damage.", 1000.5);

        var ds = captured.FirstOrDefault(e => e.Record.Type == Labels.Ds);
        Assert.IsNotNull(ds, "Expected a DS event to fire from the two-line sequence");
        // DS record: Attacker = DS holder (the melee target = Tank), Defender = NPC.
        Assert.AreEqual("Tank", ds.Record.Attacker);
        Assert.AreEqual("Mistress Saitha", ds.Record.Defender);
        Assert.AreEqual(45u, ds.Record.Total);
      }
      finally
      {
        DamageLineParser.Instance.EventsDamageProcessed -= Handler;
      }
    }

    [TestMethod]
    public void DalayaDs_PendingDiscardedIfMeleeLineMoreThanOneSecondLater()
    {
      // If the matching melee line arrives more than 1 second after the DS proc, _pendingDs is
      // discarded and no DS event fires. This guards against misattributing DS to unrelated hits.
      var captured = new List<DamageProcessedEvent>();
      void Handler(DamageProcessedEvent e) { captured.Add(e); }
      DamageLineParser.Instance.EventsDamageProcessed += Handler;
      try
      {
        ProcessLine("Mistress Saitha was hit by non-melee for 45 points of damage.", 1000.0);
        ProcessLine("Mistress Saitha crushes Tank for 500 points of damage.", 1002.5);

        Assert.IsFalse(captured.Any(e => e.Record.Type == Labels.Ds),
          "DS record should not have been emitted — pending expired after 1s");
      }
      finally
      {
        DamageLineParser.Instance.EventsDamageProcessed -= Handler;
      }
    }

    private static void ProcessLine(string action, double beginTime)
    {
      DamageLineParser.Instance.Process(new LineData { Action = action, BeginTime = beginTime, Split = action.Split(' ') });
    }

    // =============== Pass-through for unparseable lines ===============

    [TestMethod]
    public void Unparseable_ReturnsNull()
    {
      Assert.IsNull(DamageLineParser.Instance.ParseLine(""));
      Assert.IsNull(DamageLineParser.Instance.ParseLine(null));
      Assert.IsNull(DamageLineParser.Instance.ParseLine("This is not a damage line."));
    }
  }
}
