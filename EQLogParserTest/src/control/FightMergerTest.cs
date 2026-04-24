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
      // NpcDamageManager.HandleDamage calls AddPlayerTime for EVERY record whose defender is an
      // NPC — hits, misses, dodges, INVULNERABLE blocks, etc. — not just hit-type records. If
      // the merger's PopulateAggregates skips non-hit records for time segments, the merged
      // Fight's DamageSegments end earlier than the live path would, which shifts the "+Pets"
      // aggregate union inside DamageStatsManager and makes the raid-damage DPS differ from
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
