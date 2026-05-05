using System.Collections.Generic;
using System.Linq;
using EQLogParser;

namespace EQLogParserTest
{
  [TestClass]
  public class FightMergerTest
  {
    [TestMethod]
    public void TryParsePlayerName_StandardEqLogFile()
    {
      Assert.AreEqual("Skeptical", FightMerger.TryParsePlayerNameFromLogFile("eqlog_Skeptical_dalaya.txt"));
      Assert.AreEqual("Skeptical", FightMerger.TryParsePlayerNameFromLogFile(@"C:\Logs\eqlog_Skeptical_dalaya.txt"));
    }

    [TestMethod]
    public void TryParsePlayerName_ReturnsNullForInvalid()
    {
      Assert.IsNull(FightMerger.TryParsePlayerNameFromLogFile(""));
      Assert.IsNull(FightMerger.TryParsePlayerNameFromLogFile(null));
      Assert.IsNull(FightMerger.TryParsePlayerNameFromLogFile("random.txt"));
      Assert.IsNull(FightMerger.TryParsePlayerNameFromLogFile("eqlog_justone.txt"));
    }

    [TestMethod]
    public void Merge_NullOrEmptySources_ReturnsEmpty()
    {
      Assert.AreEqual(0, FightMerger.MergeFromSources(null).Count);
      Assert.AreEqual(0, FightMerger.MergeFromSources(new List<FightSource>()).Count);
    }

    [TestMethod]
    public void Merge_TwoSourcesObserveSameEvent_DedupedToOne()
    {
      var record = MakeDamage("Alice", "Bob", 500, Labels.Dd, "Ice Comet");
      var fightA = MakeFight("Bob", 100, 110, (105, record));
      var fightB = MakeFight("Bob", 100, 110, (105, record));

      var merged = FightMerger.MergeFromSources(new[]
      {
        new FightSource { SourcePlayer = "Alice", Fights = new List<Fight> { fightA } },
        new FightSource { SourcePlayer = "Carol", Fights = new List<Fight> { fightB } }
      });

      Assert.AreEqual(1, merged.Count);
      Assert.AreEqual(1, TotalActions(merged[0]));
      Assert.AreEqual(500, merged[0].DamageTotal);
      Assert.AreEqual(1, merged[0].DamageHits);
    }

    [TestMethod]
    public void Merge_DistinctRecordsAcrossSources_Combined()
    {
      var aliceHit = MakeDamage("Alice", "Bob", 500, Labels.Dd, "Ice Comet");
      var carolHit = MakeDamage("Carol", "Bob", 300, Labels.Dd, "Lava Bolt");
      var fightA = MakeFight("Bob", 100, 110, (105, aliceHit));
      var fightB = MakeFight("Bob", 100, 110, (105, carolHit));

      var merged = FightMerger.MergeFromSources(new[]
      {
        new FightSource { SourcePlayer = "Alice", Fights = new List<Fight> { fightA } },
        new FightSource { SourcePlayer = "Carol", Fights = new List<Fight> { fightB } }
      });

      Assert.AreEqual(1, merged.Count);
      Assert.AreEqual(2, TotalActions(merged[0]));
      Assert.AreEqual(800, merged[0].DamageTotal);
    }

    [TestMethod]
    public void Merge_DsRecord_KeptOnlyFromDsHoldersLog()
    {
      // Parser semantics: for DS records, Attacker = DS holder (PC), Defender = NPC.
      var ds = MakeDamage(attacker: "Tank", defender: "Bob", total: 100, type: Labels.Ds, subType: Labels.Ds);

      var tankFight = MakeFight("Bob", 100, 110, (105, ds));
      var bardFight = MakeFight("Bob", 100, 110, (105, ds));

      var merged = FightMerger.MergeFromSources(new[]
      {
        new FightSource { SourcePlayer = "Tank", Fights = new List<Fight> { tankFight } },
        new FightSource { SourcePlayer = "Bard", Fights = new List<Fight> { bardFight } }
      });

      Assert.AreEqual(1, merged.Count);
      Assert.AreEqual(1, TotalActions(merged[0]));
    }

    [TestMethod]
    public void Merge_DsRecord_DroppedIfNoDsHolderSource()
    {
      var ds = MakeDamage(attacker: "Tank", defender: "Bob", total: 100, type: Labels.Ds, subType: Labels.Ds);
      var bardFight = MakeFight("Bob", 100, 110, (105, ds));

      var merged = FightMerger.MergeFromSources(new[]
      {
        new FightSource { SourcePlayer = "Bard", Fights = new List<Fight> { bardFight } }
      });

      Assert.AreEqual(1, merged.Count);
      Assert.AreEqual(0, TotalActions(merged[0]));
    }

    [TestMethod]
    public void Merge_SameNameNonOverlapping_KeptSeparate()
    {
      var hit1 = MakeDamage("Alice", "Bob", 500, Labels.Dd, "A");
      var hit2 = MakeDamage("Alice", "Bob", 500, Labels.Dd, "B");
      var f1 = MakeFight("Bob", 100, 110, (105, hit1));
      var f2 = MakeFight("Bob", 200, 210, (205, hit2));

      var merged = FightMerger.MergeFromSources(new[]
      {
        new FightSource { SourcePlayer = "Alice", Fights = new List<Fight> { f1, f2 } }
      });

      Assert.AreEqual(2, merged.Count);
    }

    [TestMethod]
    public void Merge_SameNameOverlapping_CollapsedToOne()
    {
      var hit1 = MakeDamage("Alice", "Bob", 500, Labels.Dd, "A");
      var hit2 = MakeDamage("Alice", "Bob", 300, Labels.Dd, "B");
      var f1 = MakeFight("Bob", 100, 110, (105, hit1));
      var f2 = MakeFight("Bob", 108, 115, (112, hit2));

      var merged = FightMerger.MergeFromSources(new[]
      {
        new FightSource { SourcePlayer = "Alice", Fights = new List<Fight> { f1 } },
        new FightSource { SourcePlayer = "Carol", Fights = new List<Fight> { f2 } }
      });

      Assert.AreEqual(1, merged.Count);
      Assert.AreEqual(800, merged[0].DamageTotal);
      Assert.AreEqual(100.0, merged[0].BeginTime);
      Assert.AreEqual(112.0, merged[0].LastTime);
    }

    [TestMethod]
    public void Merge_RepeatedIdenticalHits_BothSourcesSeeAll_TakesMaxCount()
    {
      // Quad-attack: Alice hits Bob for 100 four times at T=105. Both logs observe all 4.
      var hit = MakeDamage("Alice", "Bob", 100, Labels.Melee, "");
      var fA = MakeFight("Bob", 100, 110, (105, hit), (105, hit), (105, hit), (105, hit));
      var fB = MakeFight("Bob", 100, 110, (105, hit), (105, hit), (105, hit), (105, hit));

      var merged = FightMerger.MergeFromSources(new[]
      {
        new FightSource { SourcePlayer = "S1", Fights = new List<Fight> { fA } },
        new FightSource { SourcePlayer = "S2", Fights = new List<Fight> { fB } }
      });

      Assert.AreEqual(1, merged.Count);
      Assert.AreEqual(4, TotalActions(merged[0]));
      Assert.AreEqual(400, merged[0].DamageTotal);
    }

    [TestMethod]
    public void Merge_RepeatedIdenticalHits_AsymmetricCounts_TakesMax()
    {
      var hit = MakeDamage("Alice", "Bob", 100, Labels.Melee, "");
      var fA = MakeFight("Bob", 100, 110, (105, hit), (105, hit));
      var fB = MakeFight("Bob", 100, 110, (105, hit), (105, hit), (105, hit));

      var merged = FightMerger.MergeFromSources(new[]
      {
        new FightSource { SourcePlayer = "S1", Fights = new List<Fight> { fA } },
        new FightSource { SourcePlayer = "S2", Fights = new List<Fight> { fB } }
      });

      Assert.AreEqual(1, merged.Count);
      Assert.AreEqual(3, TotalActions(merged[0]));
      Assert.AreEqual(300, merged[0].DamageTotal);
    }

    [TestMethod]
    public void Merge_PlayerDamageTotals_AggregatedByAttacker()
    {
      var aliceHit1 = MakeDamage("Alice", "Bob", 500, Labels.Dd, "X");
      var aliceHit2 = MakeDamage("Alice", "Bob", 300, Labels.Dd, "Y");
      var carolHit = MakeDamage("Carol", "Bob", 200, Labels.Dd, "Z");
      var fight = MakeFight("Bob", 100, 110, (105, aliceHit1), (106, aliceHit2), (107, carolHit));

      var merged = FightMerger.MergeFromSources(new[]
      {
        new FightSource { SourcePlayer = "Alice", Fights = new List<Fight> { fight } }
      });

      Assert.AreEqual(1, merged.Count);
      var totals = merged[0].PlayerDamageTotals;
      Assert.IsTrue(totals.ContainsKey("Alice"));
      Assert.AreEqual(800, totals["Alice"].Damage);
      Assert.AreEqual(200, totals["Carol"].Damage);
    }

    [TestMethod]
    public void Merge_PetAttackerOwner_RollsUpIntoOwnerKey()
    {
      var petHit = new DamageRecord
      {
        Attacker = "Goblin Servant",
        AttackerOwner = "Alice",
        Defender = "Bob",
        Total = 400,
        Type = Labels.Melee,
        SubType = ""
      };
      var fight = MakeFight("Bob", 100, 110, (105, petHit));

      var merged = FightMerger.MergeFromSources(new[]
      {
        new FightSource { SourcePlayer = "Alice", Fights = new List<Fight> { fight } }
      });

      Assert.AreEqual(1, merged.Count);
      var totals = merged[0].PlayerDamageTotals;
      Assert.IsTrue(totals.ContainsKey("Alice"));
      Assert.AreEqual(400, totals["Alice"].Damage);
      Assert.AreEqual("Alice", totals["Alice"].PetOwner);
    }

    [TestMethod]
    public void Merge_DifferentNames_KeptSeparate()
    {
      var hitA = MakeDamage("Alice", "Bob", 500, Labels.Dd, "A");
      var hitB = MakeDamage("Alice", "Charlie", 300, Labels.Dd, "B");
      var f1 = MakeFight("Bob", 100, 110, (105, hitA));
      var f2 = MakeFight("Charlie", 100, 110, (105, hitB));

      var merged = FightMerger.MergeFromSources(new[]
      {
        new FightSource { SourcePlayer = "Alice", Fights = new List<Fight> { f1, f2 } }
      });

      Assert.AreEqual(2, merged.Count);
    }

    [TestMethod]
    public void Merge_NonHitRecords_AdvanceAttackerTimeSegment()
    {
      // FightManager.HandleDamage calls AddPlayerTime for EVERY record whose defender is an
      // NPC — hits, misses, dodges, INVULNERABLE blocks, etc. — not just hit-type records. If
      // the merger's PopulateAggregates skips non-hit records for time segments, the merged
      // Fight's DamageSegments end earlier than the live path would, which shifts the "+Pets"
      // aggregate union inside DamageStatsBuilder and makes the raid-damage DPS differ from
      // DPS Summary for the same fight. Pin this behavior: a miss at t=118 must extend the
      // attacker's segment past t=105 even though only the t=105 record is a hit.
      var hit = MakeDamage("Alice", "Bob", 100, Labels.Melee, "");
      var miss = MakeDamage("Alice", "Bob", 0, Labels.Miss, "Hits");
      var fight = MakeFight("Bob", 100, 120, (105, hit), (118, miss));

      var merged = FightMerger.MergeFromSources(new[]
      {
        new FightSource { SourcePlayer = "Alice", Fights = new List<Fight> { fight } }
      });

      Assert.AreEqual(1, merged.Count);
      Assert.IsTrue(merged[0].DamageSegments.TryGetValue("Alice", out var segment),
        "Attacker should have a time segment");
      Assert.AreEqual(105.0, segment.BeginTime);
      Assert.AreEqual(118.0, segment.EndTime,
        "Non-hit record at t=118 must extend the segment's EndTime");
    }

    [TestMethod]
    public void Merge_TankingBlocks_AggregatedSeparatelyFromDamage()
    {
      // Tanking records (NPC hits player) live in Fight.TankingBlocks and must flow through
      // the merger onto the merged Fight's TankingBlocks + TankSegments + TankHits + TankTotal +
      // PlayerTankTotals. Without this the Raid Damage window's Tanking tab shows no data.
      var tankHit = MakeDamage("Bob", "Alice", 500, Labels.Melee, "");
      var fight = new Fight { Name = "Bob", BeginTime = 100, LastTime = 120 };
      var ag = new ActionGroup { BeginTime = 110 };
      ag.Actions.Add(tankHit);
      fight.TankingBlocks.Add(ag);

      var merged = FightMerger.MergeFromSources(new[]
      {
        new FightSource { SourcePlayer = "Alice", Fights = new List<Fight> { fight } }
      });

      Assert.AreEqual(1, merged.Count);
      Assert.AreEqual(1, merged[0].TankingBlocks.Count);
      Assert.AreEqual(1, merged[0].TankHits);
      Assert.AreEqual(500, merged[0].TankTotal);
      Assert.AreEqual(110.0, merged[0].BeginTankingTime);
      Assert.AreEqual(110.0, merged[0].LastTankingTime);
      Assert.IsTrue(merged[0].PlayerTankTotals.ContainsKey("Alice"), "Tank totals should be keyed by defender (the tank)");
      Assert.AreEqual(500, merged[0].PlayerTankTotals["Alice"].Damage);
      Assert.IsTrue(merged[0].TankSegments.ContainsKey("Alice"), "Time segments keyed by defender");
    }

    [TestMethod]
    public void Merge_TimeOffsetAlignsCrossSourceFights()
    {
      // Two sources observe the same fight, but their clocks differ by 1 hour. Without an
      // offset, FightMerger.ClusterByNameAndOverlap sees non-overlapping windows and emits two
      // clusters. With offset=3600 applied to the later-clock source, the windows align and
      // the cluster collapses to one merged fight with deduped records.
      var hit = MakeDamage("Alice", "Bob", 500, Labels.Dd, "Ice Comet");
      var fightAnchor = MakeFight("Bob", 100, 110, (105, hit));
      var fightShifted = MakeFight("Bob", 3700, 3710, (3705, hit));

      var merged = FightMerger.MergeFromSources(new[]
      {
        new FightSource { SourcePlayer = "Anchor", Fights = new List<Fight> { fightAnchor } },
        new FightSource { SourcePlayer = "Shifted", Fights = new List<Fight> { fightShifted }, TimeOffsetSeconds = 3600 }
      });

      Assert.AreEqual(1, merged.Count);
      Assert.AreEqual(1, TotalActions(merged[0]));
      Assert.AreEqual(500, merged[0].DamageTotal);
      Assert.AreEqual(100.0, merged[0].BeginTime, "BeginTime should be in the merged frame (anchor's clock)");
      Assert.AreEqual(105.0, merged[0].DamageBlocks[0].BeginTime, "ActionGroup time should be shifted into the merged frame");
    }

    [TestMethod]
    public void Merge_TimeOffset_DistinctRecordsCombined()
    {
      // Same fight observed from two timezones, each source with a unique hit. Offset must
      // align both records under the same merged-frame time so they end up in one fight.
      var aliceHit = MakeDamage("Alice", "Bob", 500, Labels.Dd, "Ice Comet");
      var carolHit = MakeDamage("Carol", "Bob", 300, Labels.Dd, "Lava Bolt");
      var fightAnchor = MakeFight("Bob", 100, 110, (105, aliceHit));
      var fightShifted = MakeFight("Bob", 3700, 3710, (3705, carolHit));

      var merged = FightMerger.MergeFromSources(new[]
      {
        new FightSource { SourcePlayer = "Anchor", Fights = new List<Fight> { fightAnchor } },
        new FightSource { SourcePlayer = "Shifted", Fights = new List<Fight> { fightShifted }, TimeOffsetSeconds = 3600 }
      });

      Assert.AreEqual(1, merged.Count);
      Assert.AreEqual(2, TotalActions(merged[0]));
      Assert.AreEqual(800, merged[0].DamageTotal);
    }

    [TestMethod]
    public void Merge_NegativeTimeOffset_AlignsBackward()
    {
      // The "shifted" source's clock is BEHIND the anchor — applying a negative offset shifts
      // its timestamps forward into the merged frame. Mirrors timezones in the other direction.
      var hit = MakeDamage("Alice", "Bob", 500, Labels.Dd, "Ice Comet");
      var fightAnchor = MakeFight("Bob", 3700, 3710, (3705, hit));
      var fightShifted = MakeFight("Bob", 100, 110, (105, hit));

      var merged = FightMerger.MergeFromSources(new[]
      {
        new FightSource { SourcePlayer = "Anchor", Fights = new List<Fight> { fightAnchor } },
        new FightSource { SourcePlayer = "Shifted", Fights = new List<Fight> { fightShifted }, TimeOffsetSeconds = -3600 }
      });

      Assert.AreEqual(1, merged.Count);
      Assert.AreEqual(1, TotalActions(merged[0]));
      Assert.AreEqual(3700.0, merged[0].BeginTime);
    }

    [TestMethod]
    public void Merge_DriftedSameHit_DedupedWithinTolerance()
    {
      // EQ writes log lines at second granularity from each player's local clock; the same
      // physical hit can land 1-2 seconds apart in two players' logs. Without a tolerance
      // window the dedup keys don't match and damage doubles. Pin the fix: a hit at T=105 in
      // one source and T=106 in the other (1s drift) must collapse to one emit.
      var hit = MakeDamage("Alice", "Bob", 500, Labels.Dd, "Ice Comet");
      var fightA = MakeFight("Bob", 100, 110, (105, hit));
      var fightB = MakeFight("Bob", 100, 110, (106, hit));

      var merged = FightMerger.MergeFromSources(new[]
      {
        new FightSource { SourcePlayer = "Alice", Fights = new List<Fight> { fightA } },
        new FightSource { SourcePlayer = "Carol", Fights = new List<Fight> { fightB } }
      });

      Assert.AreEqual(1, merged.Count);
      Assert.AreEqual(1, TotalActions(merged[0]));
      Assert.AreEqual(500, merged[0].DamageTotal);
    }

    [TestMethod]
    public void Merge_DriftedSameHit_PairedRegardlessOfGap()
    {
      // Order pairing is invariant to drift size: the N-th observation of a record from each
      // source pairs with the N-th from each other source, regardless of how far apart the
      // timestamps are. Real-world Dalaya logs have shown drift up to 40s on a single physical
      // tick by end of fight; any time-bounded approach misses these.
      var hit = MakeDamage("Alice", "Bob", 500, Labels.Dd, "Ice Comet");
      var fightA = MakeFight("Bob", 100, 200, (105, hit));
      var fightB = MakeFight("Bob", 100, 200, (145, hit));  // 40s drift on the same physical hit

      var merged = FightMerger.MergeFromSources(new[]
      {
        new FightSource { SourcePlayer = "Alice", Fights = new List<Fight> { fightA } },
        new FightSource { SourcePlayer = "Carol", Fights = new List<Fight> { fightB } }
      });

      Assert.AreEqual(1, TotalActions(merged[0]));
      Assert.AreEqual(500, merged[0].DamageTotal);
    }

    [TestMethod]
    public void Merge_DotTickSequence_GrowingDriftToHugeOffset()
    {
      // Real Dalaya logs showed drift growing from 0s on the first tick to 40s+ on the last.
      // Order pairing handles this correctly because each iteration consumes one tick from
      // each source — the absolute time gap doesn't matter, only the per-source ordinal does.
      // Pin the regression: 6 ticks per source must produce 6 emits, not (6 + ones that
      // "couldn't pair due to drift").
      var tick = MakeDamage("Alice", "Bob", 1445, Labels.Dot, "Saitha");
      var fightA = MakeFight("Bob", 100, 250,
        (105, tick), (111, tick), (129, tick), (135, tick), (165, tick), (171, tick));
      var fightB = MakeFight("Bob", 100, 250,
        (109, tick), (118, tick), (145, tick), (154, tick), (196, tick), (203, tick));

      var merged = FightMerger.MergeFromSources(new[]
      {
        new FightSource { SourcePlayer = "Alice", Fights = new List<Fight> { fightA } },
        new FightSource { SourcePlayer = "Carol", Fights = new List<Fight> { fightB } }
      });

      Assert.AreEqual(6, TotalActions(merged[0]),
        "Order pairing must produce 6 ticks per source = 6 emits regardless of growing drift up to 32s");
      Assert.AreEqual(6 * 1445, (int)merged[0].DamageTotal);
    }

    [TestMethod]
    public void Merge_DotTickSequence_PairedByOrderNotByTimeBucket()
    {
      // DoT ticks of the same spell on the same target produce identical DamageRecord values
      // at fixed intervals. Per-source clock drift can push the same physical tick into the
      // *next* tick's time bucket, but sequence-pairing aligns the N-th tick from each source
      // regardless of the per-tick drift — so both sources observing 4 ticks emits 4, not 7
      // (which a naive same-time exact-match dedup would produce).
      var tick = MakeDamage("Alice", "Bob", 1000, Labels.Dot, "Archaic");
      var fightA = MakeFight("Bob", 100, 130, (105, tick), (111, tick), (117, tick), (123, tick));
      var fightB = MakeFight("Bob", 100, 130, (109, tick), (115, tick), (121, tick), (127, tick));

      var merged = FightMerger.MergeFromSources(new[]
      {
        new FightSource { SourcePlayer = "Alice", Fights = new List<Fight> { fightA } },
        new FightSource { SourcePlayer = "Carol", Fights = new List<Fight> { fightB } }
      });

      Assert.AreEqual(1, merged.Count);
      Assert.AreEqual(4, TotalActions(merged[0]),
        "Sequence pairing should produce 4 ticks even though per-tick drift (4s) is less than the inter-tick interval (6s)");
      Assert.AreEqual(4000, merged[0].DamageTotal);
    }

    [TestMethod]
    public void Merge_DotTickSequence_GrowingDriftAcrossBurst()
    {
      // Real Dalaya logs show drift that grows during a burst: first tick aligned, later
      // ticks drift up to 6s. Pin that the sequence-pair algorithm still gives the right count
      // (4 ticks per source = 4 emits, not the 8 we'd see if pair-up failed mid-fight).
      var tick = MakeDamage("Alice", "Bob", 1000, Labels.Dot, "Archaic");
      var fightA = MakeFight("Bob", 100, 130, (105, tick), (111, tick), (117, tick), (123, tick));
      var fightB = MakeFight("Bob", 100, 130, (105, tick), (113, tick), (121, tick), (129, tick));

      var merged = FightMerger.MergeFromSources(new[]
      {
        new FightSource { SourcePlayer = "Alice", Fights = new List<Fight> { fightA } },
        new FightSource { SourcePlayer = "Carol", Fights = new List<Fight> { fightB } }
      });

      Assert.AreEqual(4, TotalActions(merged[0]));
      Assert.AreEqual(4000, merged[0].DamageTotal);
    }

    [TestMethod]
    public void Merge_DriftedQuadAttack_MaxPerSourcePreserved()
    {
      // Quad attack: 4 identical hits from one source at T, observed as 4 at T+1 by another.
      // The cluster contains 4+4 entries from two distinct sources but the merged count is
      // the per-source max (4), not the sum (8).
      var hit = MakeDamage("Alice", "Bob", 100, Labels.Melee, "");
      var fA = MakeFight("Bob", 100, 110, (105, hit), (105, hit), (105, hit), (105, hit));
      var fB = MakeFight("Bob", 100, 110, (106, hit), (106, hit), (106, hit), (106, hit));

      var merged = FightMerger.MergeFromSources(new[]
      {
        new FightSource { SourcePlayer = "S1", Fights = new List<Fight> { fA } },
        new FightSource { SourcePlayer = "S2", Fights = new List<Fight> { fB } }
      });

      Assert.AreEqual(4, TotalActions(merged[0]));
      Assert.AreEqual(400, merged[0].DamageTotal);
    }

    [TestMethod]
    public void Merge_DriftedAsymmetricCounts_TakesMaxAcrossSources()
    {
      // Asymmetric drifted: A sees 2 hits at T, B sees 3 at T+1. Within the tolerance cluster,
      // max is 3.
      var hit = MakeDamage("Alice", "Bob", 100, Labels.Melee, "");
      var fA = MakeFight("Bob", 100, 110, (105, hit), (105, hit));
      var fB = MakeFight("Bob", 100, 110, (106, hit), (106, hit), (106, hit));

      var merged = FightMerger.MergeFromSources(new[]
      {
        new FightSource { SourcePlayer = "S1", Fights = new List<Fight> { fA } },
        new FightSource { SourcePlayer = "S2", Fights = new List<Fight> { fB } }
      });

      Assert.AreEqual(3, TotalActions(merged[0]));
      Assert.AreEqual(300, merged[0].DamageTotal);
    }

    [TestMethod]
    public void Merge_DriftedAcrossOffset_FullPipeline()
    {
      // Real-world scenario: clocks 1h apart with per-hit drift. Source A has hit at T=105,
      // Source B has the same hit at T=3706 (offset 3600 + drift 1s). After offset adjustment
      // the cluster collapses these to one emit despite the residual drift.
      var hit = MakeDamage("Alice", "Bob", 500, Labels.Dd, "Ice Comet");
      var fA = MakeFight("Bob", 100, 110, (105, hit));
      var fB = MakeFight("Bob", 3700, 3710, (3706, hit));

      var merged = FightMerger.MergeFromSources(new[]
      {
        new FightSource { SourcePlayer = "Alice", Fights = new List<Fight> { fA } },
        new FightSource { SourcePlayer = "Carol", Fights = new List<Fight> { fB }, TimeOffsetSeconds = 3600 }
      });

      Assert.AreEqual(1, merged.Count);
      Assert.AreEqual(1, TotalActions(merged[0]));
      Assert.AreEqual(500, merged[0].DamageTotal);
    }

    [TestMethod]
    public void Merge_TimeOffset_DoesNotMergeUnrelatedFights()
    {
      // Sanity check: a non-shared fight in the offset source remains separate. The offset
      // shouldn't accidentally collapse genuinely-distinct fights just because their adjusted
      // times happen to land near anchor fights.
      var hitA = MakeDamage("Alice", "Bob", 500, Labels.Dd, "X");
      var hitB = MakeDamage("Carol", "Charlie", 300, Labels.Dd, "Y");
      var fightAnchor = MakeFight("Bob", 100, 110, (105, hitA));
      var fightOther = MakeFight("Charlie", 3700, 3710, (3705, hitB));

      var merged = FightMerger.MergeFromSources(new[]
      {
        new FightSource { SourcePlayer = "Anchor", Fights = new List<Fight> { fightAnchor } },
        new FightSource { SourcePlayer = "Shifted", Fights = new List<Fight> { fightOther }, TimeOffsetSeconds = 3600 }
      });

      Assert.AreEqual(2, merged.Count);
    }

    [TestMethod]
    public void Merge_LoneSourceEvents_GetDriftCorrectionFromPairedRecords()
    {
      // Source B's clock drifts linearly behind A: 5s late at the first shared event, growing
      // to 10s late at the last. Fit gives slope ≈ 1/11 in B's time domain. For records
      // observed by both, order-pair already picks A's earlier time. For a record observed
      // *only* in B (the canonical Bonaparte-melee scenario — pet meleeing right next to the
      // tank, out of the spell-caster's range), the merge has no A-time to use; without drift
      // correction the lone-source emit time is B's drifted value, inflating the fight
      // duration. The drift function fit from shared records pulls those lone events back.
      var sharedRec = MakeDamage("Alice", "Boss", 1000, Labels.Dot, "Shared");
      var loneRec = MakeDamage("Bob", "Boss", 100, Labels.Melee, "");

      var anchor = MakeFight("Boss", 100, 200,
        (100, sharedRec), (110, sharedRec), (120, sharedRec),
        (130, sharedRec), (140, sharedRec), (150, sharedRec));

      // B's lone record at 165 — extrapolating the linear fit (slope 1/11, intercept 5 at
      // t0=105) predicts drift ≈ 5 + (165-105)/11 ≈ 10.5, so corrected emit time ≈ 154.5.
      var target = MakeFight("Boss", 105, 200,
        (105, sharedRec), (116, sharedRec), (127, sharedRec),
        (138, sharedRec), (149, sharedRec), (160, sharedRec),
        (165, loneRec));

      var merged = FightMerger.MergeFromSources(new[]
      {
        new FightSource { SourcePlayer = "A", Fights = new List<Fight> { anchor } },
        new FightSource { SourcePlayer = "B", Fights = new List<Fight> { target } }
      });

      Assert.AreEqual(1, merged.Count);

      var loneBlock = merged[0].DamageBlocks
        .FirstOrDefault(b => b.Actions.OfType<DamageRecord>().Any(r => ReferenceEquals(r, loneRec)));
      Assert.IsNotNull(loneBlock, "Lone-source record must be present in the merged fight");
      Assert.IsTrue(loneBlock.BeginTime < 160,
        $"Drift correction should pull B's lone-source emit time below 160 (was {loneBlock.BeginTime})");
      Assert.IsTrue(loneBlock.BeginTime > 150,
        $"Drift correction shouldn't undershoot real fight time (was {loneBlock.BeginTime})");
    }

    [TestMethod]
    public void Merge_DriftAnchor_PicksFastestClockNotLargestSource()
    {
      // The constant-offset detector picks the largest source as anchor (TimeOffsetSeconds=0
      // for that source). The drift detector must pick *independently* — by which source's
      // clock is fastest — otherwise drift correction pushes events the wrong direction.
      // Setup: Source "Big" has more total events but its clock is consistently slower than
      // Source "Small". The drift correction must pull Big's lone events earlier, not push
      // Small's events later.
      var sharedRec = MakeDamage("Alice", "Boss", 1000, Labels.Dot, "Shared");
      var bigLoneRec = MakeDamage("Bob", "Boss", 200, Labels.Melee, "");

      // Big has 6 shared events plus 5 lone events, all on a slower clock (20s late vs Small).
      var big = MakeFight("Boss", 100, 250,
        (120, sharedRec), (130, sharedRec), (140, sharedRec),
        (150, sharedRec), (160, sharedRec), (170, sharedRec),
        (180, bigLoneRec), (190, bigLoneRec), (200, bigLoneRec),
        (210, bigLoneRec), (220, bigLoneRec));
      // Small has only the 6 shared events on a faster clock.
      var small = MakeFight("Boss", 100, 200,
        (100, sharedRec), (110, sharedRec), (120, sharedRec),
        (130, sharedRec), (140, sharedRec), (150, sharedRec));

      var merged = FightMerger.MergeFromSources(new[]
      {
        new FightSource { SourcePlayer = "Big", Fights = new List<Fight> { big } },
        new FightSource { SourcePlayer = "Small", Fights = new List<Fight> { small } }
      });

      Assert.AreEqual(1, merged.Count);
      // Big's lone events should have been pulled earlier toward Small's frame. The latest
      // emitted block time should be well below 220 (Big's raw last) — closer to 200 (Big's
      // last minus the ~20s constant lag).
      var lastBlockTime = merged[0].DamageBlocks[^1].BeginTime;
      Assert.IsTrue(lastBlockTime < 215,
        $"Drift correction must pull Big's lone events earlier, not leave them at 220+ (was {lastBlockTime})");
    }

    [TestMethod]
    public void Merge_DriftCorrection_DoesNotAffectFastestSourceEvents()
    {
      // The drift anchor is the source with the smallest mean event time (the fastest clock —
      // closest to physical real time). Its events go through the merge unchanged; only
      // slower sources get drift correction. Pin this so the anchor's lone observations don't
      // accidentally get pulled around.
      var rec = MakeDamage("Alice", "Boss", 1000, Labels.Dd, "X");
      // Source A is consistently 10s faster than Source B for shared observations, plus has
      // one lone observation at t=155.
      var fast = MakeFight("Boss", 100, 200,
        (105, rec), (115, rec), (125, rec), (135, rec), (145, rec), (155, rec));
      var slow = MakeFight("Boss", 100, 200,
        (115, rec), (125, rec), (135, rec), (145, rec), (155, rec));

      var merged = FightMerger.MergeFromSources(new[]
      {
        new FightSource { SourcePlayer = "Fast", Fights = new List<Fight> { fast } },
        new FightSource { SourcePlayer = "Slow", Fights = new List<Fight> { slow } }
      });

      Assert.AreEqual(1, merged.Count);
      Assert.AreEqual(6, TotalActions(merged[0]));
      var lastBlockTime = merged[0].DamageBlocks[^1].BeginTime;
      Assert.AreEqual(155, lastBlockTime, 1e-6,
        "Fast source's lone event must stay at its original time — no drift function applies to the anchor");
    }

    // Helpers

    private static DamageRecord MakeDamage(string attacker, string defender, uint total, string type, string subType) => new()
    {
      Attacker = attacker,
      Defender = defender,
      Total = total,
      Type = type,
      SubType = subType
    };

    private static Fight MakeFight(string name, double beginTime, double lastTime, params (double Time, DamageRecord Record)[] records)
    {
      var fight = new Fight
      {
        Name = name,
        BeginTime = beginTime,
        LastTime = lastTime
      };

      foreach (var group in records.GroupBy(r => r.Time).OrderBy(g => g.Key))
      {
        var ag = new ActionGroup { BeginTime = group.Key };
        foreach (var (_, rec) in group)
        {
          ag.Actions.Add(rec);
        }
        fight.DamageBlocks.Add(ag);
      }

      return fight;
    }

    private static int TotalActions(Fight fight) => fight.DamageBlocks.Sum(b => b.Actions.Count);
  }
}
