using System.Collections.Generic;
using System.Linq;
using EQLogParser;

namespace EQLogParserTest.Integration
{
  // Full-pipeline regression tests: drive the real DamageLineParser → FightManager → FightMerger
  // chain with in-memory synthetic logs (no fixtures needed). Behavioral invariants from the
  // upstream merge protocol (memory `feedback_upstream_merge`) live here so every developer gets
  // regression coverage without needing local raid log fixtures.
  //
  // Captured-value baselines (boss HP totals, top-N rankings) live in BaselineCapturedTest and
  // require local fixtures under Resources/baseline-logs/ — see memory
  // `project_baseline_regression_test`.
  [TestClass]
  public class BaselineInvariantsTest
  {
    [TestInitialize]
    public void Setup()
    {
      // Each test builds an isolated ParseContext, but the PlayerRegistry fallback chain still
      // reads ConfigUtil.PlayerName when no explicit override is set. Pin it so any fallback
      // path is deterministic.
      ConfigUtil.PlayerName = "Selfie";
    }

    [TestMethod]
    public void Pipeline_NamedPet_AppearsAsSingleAttackerAcrossHitTypes()
    {
      // The integration regression for the named-pet trim sites (see CLAUDE.md trim-site
      // inventory). A pet with trailing-space signal does spell DD + melee + non-melee DD + miss.
      // Without TrimEnd at one of the sites, that branch's records key under "TestPetAlpha "
      // (with trailing space) while other branches use "TestPetAlpha" — producing two rows in
      // PlayerDamageTotals. Pin the aggregate: exactly one key with no trailing-space variant.
      var ctx = ParseContext.CreateIsolated();
      ctx.PlayerRegistry.PlayerName = "Tester";
      ctx.PlayerRegistry.AddVerifiedPlayer("Tester", 1000.0);

      var fights = new List<Fight>();
      ctx.FightManager.EventsNewFight += fights.Add;

      // Spell `byIndex` branch (trim site #3) — also marks the pet as verified via the
      // trailing-space signal, so subsequent melee/DD records get attributed properly.
      Feed(ctx, 1000, "A test boss has taken 500 damage from TestPetAlpha  by Gale of Blades.");
      // Melee branch (trim site #1) — same trailing-space pattern via the verb path.
      Feed(ctx, 1001, "TestPetAlpha  crushes a test boss for 200 points of damage.");
      // Non-melee DD eqemu branch (trim site #4).
      Feed(ctx, 1002, "TestPetAlpha  hit a test boss for 100 points of non-melee damage.");
      // Miss/INVULNERABLE branch (trim site #5) — damage=0 record still attributes via
      // AddPlayerTime, so a "TestPetAlpha " key would still surface here.
      Feed(ctx, 1003, "TestPetAlpha  tries to crush a test boss, but a test boss is INVULNERABLE!");

      Assert.AreEqual(1, fights.Count, "Single boss → single fight");
      var fight = fights[0];

      var petKeys = fight.PlayerDamageTotals.Keys
        .Where(k => k.StartsWith("TestPetAlpha"))
        .ToList();
      CollectionAssert.AreEquivalent(new[] { "TestPetAlpha" }, petKeys,
        $"Named pet must appear under exactly the trimmed name across all hit types. Got: [{string.Join(", ", petKeys.Select(k => $"'{k}'"))}]");

      // Time segments are populated by AddPlayerTime for every record, even misses. A
      // "TestPetAlpha " key in DamageSegments would mean trim site #5 regressed.
      var segmentKeys = fight.DamageSegments.Keys
        .Where(k => k.StartsWith("TestPetAlpha"))
        .ToList();
      CollectionAssert.AreEquivalent(new[] { "TestPetAlpha" }, segmentKeys,
        "Time segments must use the trimmed name (catches miss/INVULNERABLE trim regressions)");
    }

    [TestMethod]
    public void Pipeline_NamedPet_NotAutoAttributedToOwner()
    {
      // The spell `byIndex` branch must NOT auto-attribute the named pet to the source's
      // PlayerName. Earlier code did this and silently corrupted raid logs where another
      // player's named pet appeared (Kateila parsing → her Bonaparte rows would absorb
      // someone else's pet). Real ownership comes from petmapping.txt, ChatDB "My leader is
      // X", or the Pet Owners UI — never from auto-attribution at parse time.
      var ctx = ParseContext.CreateIsolated();
      ctx.PlayerRegistry.PlayerName = "Tester";
      ctx.PlayerRegistry.AddVerifiedPlayer("Tester", 1000.0);

      Feed(ctx, 1000, "A test boss has taken 500 damage from TestPetBravo  by Gale of Blades.");

      Assert.IsTrue(ctx.PlayerRegistry.IsVerifiedPet("TestPetBravo"),
        "Pet should be verified via the trailing-space signal");

      var owner = ctx.PlayerRegistry.GetPlayerFromPet("TestPetBravo");
      Assert.IsTrue(string.IsNullOrEmpty(owner) || owner == Labels.Unassigned,
        $"Spell byIndex branch must not auto-attribute the pet's owner. Got: '{owner}'");
    }

    [TestMethod]
    public void Pipeline_SelfCast_ResolvesPerSource_DedupsAcrossSources()
    {
      // Two sources observe the SAME physical hit from different angles:
      //   Alice's log: "X has taken N damage from your Ice Comet." (self-cast format)
      //   Bob's log:   "X has taken N damage from Alice by Ice Comet." (third-person Dalaya format)
      // For dedup to collapse these to one record, "your" in Alice's log must resolve to
      // "Alice" (her source PlayerName), not to ConfigUtil.PlayerName ("Selfie" here). If a
      // future change reverts the parsers to read ConfigUtil.PlayerName, the keys differ and
      // dedup fails — surfaces as inflated multi-source damage totals.
      var aliceFights = RunSource("Alice", new[]
      {
        (1000.0, "A test boss has taken 500 damage from your Ice Comet.")
      });

      var bobFights = RunSource("Bob", new[]
      {
        (1000.0, "A test boss has taken 500 damage from Alice by Ice Comet.")
      });

      var merged = FightMerger.MergeFromSources(new[]
      {
        new FightSource { SourcePlayer = "Alice", Fights = aliceFights },
        new FightSource { SourcePlayer = "Bob", Fights = bobFights }
      });

      Assert.AreEqual(1, merged.Count, "Same boss → one merged fight");
      var fight = merged[0];

      var damageRecords = fight.DamageBlocks
        .SelectMany(b => b.Actions.OfType<DamageRecord>())
        .ToList();
      Assert.AreEqual(1, damageRecords.Count,
        "Self-cast + third-person observation of the same physical hit must dedup to one record");
      Assert.AreEqual("Alice", damageRecords[0].Attacker,
        "Self-cast 'your' resolved per-source — Alice's log resolves 'your' to Alice, not ConfigUtil.PlayerName");
      Assert.AreEqual(500u, fight.DamageTotal);
    }

    [TestMethod]
    public void Pipeline_DamageShield_KeptOnlyFromHolderSource_AfterMerge()
    {
      // DS damage is logged only in the DS holder's log (the player wearing the buff). The
      // parser pairs the DS proc line with the NPC's *next melee on the holder* within ~1s
      // (the proc itself fires when the NPC attacks a player with DS up). The emitted record
      // is Attacker=holder, Defender=NPC, Type=Ds. The merger then keeps DS records only from
      // the source whose SourcePlayer matches the DS record's Attacker — without that filter,
      // multi-source raids would inflate DS damage by N.
      //
      // Setup: Tank's log has the DS proc + the NPC's melee that pairs it + Tank's offensive
      // hit. Other's log only sees Tank's offensive hit (he can't see Tank's DS or the boss
      // hitting Tank). After merge, exactly one DS record remains, attributed to Tank.
      var tankFights = RunSource("Tank", new[]
      {
        (1000.0, "A test boss was hit by non-melee for 50 points of damage."),
        (1000.0, "A test boss crushes Tank for 300 points of damage."),
        (1001.0, "Tank crushes a test boss for 200 points of damage.")
      });
      var otherFights = RunSource("Other", new[]
      {
        (1001.0, "Tank crushes a test boss for 200 points of damage.")
      });

      var merged = FightMerger.MergeFromSources(new[]
      {
        new FightSource { SourcePlayer = "Tank", Fights = tankFights },
        new FightSource { SourcePlayer = "Other", Fights = otherFights }
      });

      Assert.AreEqual(1, merged.Count);
      var dsRecords = merged[0].DamageBlocks
        .SelectMany(b => b.Actions.OfType<DamageRecord>())
        .Where(r => r.Type == Labels.Ds)
        .ToList();
      Assert.AreEqual(1, dsRecords.Count, "Exactly one DS record after merge (only from the holder's source)");
      Assert.AreEqual("Tank", dsRecords[0].Attacker, "DS record's Attacker = DS holder");
    }

    [TestMethod]
    public void Pipeline_AllFights_HaveNonWhitespaceName()
    {
      // Guards against re-introducing a blanket TrimEnd on attacker/defender inside
      // CreateDamageRecord. The earlier attempt did that and turned legitimate trailing-space
      // pet attackers (the trim-site signal) into whitespace-only fields, manufacturing
      // phantom Fights. Trim happens at each specific attacker-assembly site instead.
      //
      // The synthetic feed below contains no legitimate whitespace defenders, so no Fight
      // should be emitted with a whitespace name. (Real Dalaya logs CAN legitimately produce
      // whitespace-named Fights — e.g., Mistress Saitha's nameless "wand" adds log lines with
      // blanked defender fields — and BaselineCapturedTest baselines those alongside named
      // fights. This invariant is specifically about parser-manufactured phantoms.)
      var ctx = ParseContext.CreateIsolated();
      ctx.PlayerRegistry.PlayerName = "Tester";
      ctx.PlayerRegistry.AddVerifiedPlayer("Tester", 1000.0);

      var fights = new List<Fight>();
      ctx.FightManager.EventsNewFight += fights.Add;

      Feed(ctx, 1000, "Tester crushes a test boss for 100 points of damage.");
      Feed(ctx, 1001, "A test boss has taken 200 damage from TestPetCharlie  by Gale of Blades.");
      Feed(ctx, 1002, "TestPetCharlie  hit a test boss for 50 points of non-melee damage.");
      Feed(ctx, 1003, "Tester hit a test boss for 300 points of magic damage by Ice Comet.");

      Assert.IsTrue(fights.Count > 0, "At least one fight expected from the mixed-line feed");
      foreach (var f in fights)
      {
        Assert.IsFalse(string.IsNullOrWhiteSpace(f.Name),
          $"Phantom Fight produced with empty/whitespace name: '{f.Name}'");
      }
    }

    [TestMethod]
    public void Pipeline_BlankTargetLines_FromRealPlayers_ProduceLegitWhitespaceFight()
    {
      // The positive counterpart to Pipeline_AllFights_HaveNonWhitespaceName. Dalaya logs
      // certain nameless mobs (e.g. Mistress Saitha's "wand" adds) with the target field
      // blanked, so melee lines read "Attacker hits     for N points of damage." with nothing
      // between the verb and "for". The parser correctly joins the empty inter-token slices
      // into a whitespace defender, and FightManager names the fight by that defender —
      // producing a *legitimate* whitespace-named Fight.
      //
      // The distinction from a parser-manufactured phantom is the roster: a real nameless-add
      // engagement carries multiple distinct real player attackers, whereas a phantom (e.g. a
      // blanket TrimEnd turning a trailing-space pet name into a blank field) would carry a
      // degenerate single-attacker roster. This invariant pins that distinction fixture-free;
      // BaselineCapturedTest asserts the same property on the real 5-source raid data, where
      // the two whitespace fights carry 13- and 12-player rosters.
      var ctx = ParseContext.CreateIsolated();
      ctx.PlayerRegistry.PlayerName = "Tester";
      foreach (var p in new[] { "Warone", "Wartwo", "Warthree" })
      {
        ctx.PlayerRegistry.AddVerifiedPlayer(p, 1000.0);
      }

      var fights = new List<Fight>();
      ctx.FightManager.EventsNewFight += fights.Add;

      // Blank-target melee lines — identical spacing so every record resolves to the same
      // whitespace defender and lands in one fight.
      Feed(ctx, 1000, "Warone hits     for 100 points of damage.");
      Feed(ctx, 1000, "Wartwo crushes     for 80 points of damage.");
      Feed(ctx, 1001, "Warthree slashes     for 120 points of damage.");

      var blankFight = fights.SingleOrDefault(f => string.IsNullOrWhiteSpace(f.Name));
      Assert.IsNotNull(blankFight,
        "Blank-target lines from real players must produce a legitimate whitespace-named Fight");

      CollectionAssert.AreEquivalent(
        new[] { "Warone", "Wartwo", "Warthree" },
        blankFight.PlayerDamageTotals.Keys.ToList(),
        "A legitimate nameless-add fight carries a real multi-player roster — not a degenerate " +
        "single-attacker phantom. This is what separates it from a parser-manufactured whitespace fight.");
    }

    // ---- helpers ----

    private static void Feed(ParseContext ctx, double beginTime, string action)
    {
      ctx.DamageLineParser.Process(new LineData
      {
        Action = action,
        BeginTime = beginTime,
        Split = action.Split(' ')
      });
    }

    private static List<Fight> RunSource(string sourcePlayer, IEnumerable<(double Time, string Line)> lines)
    {
      var ctx = ParseContext.CreateIsolated();
      ctx.PlayerRegistry.PlayerName = sourcePlayer;
      ctx.PlayerRegistry.AddVerifiedPlayer(sourcePlayer, 1.0);

      var fights = new List<Fight>();
      ctx.FightManager.EventsNewFight += fights.Add;

      foreach (var (time, line) in lines)
      {
        Feed(ctx, time, line);
      }
      return fights;
    }
  }
}
