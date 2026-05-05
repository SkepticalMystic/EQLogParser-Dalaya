using System.Collections.Generic;
using EQLogParser;

namespace EQLogParserTest
{
  [TestClass]
  public class FightOffsetDetectorTest
  {
    [TestMethod]
    public void Detect_NullArgs_ReturnsZero()
    {
      Assert.AreEqual(0.0, FightOffsetDetector.Detect(null, null));
      Assert.AreEqual(0.0, FightOffsetDetector.Detect(MakeFights(("Boss", 100)), null));
      Assert.AreEqual(0.0, FightOffsetDetector.Detect(null, MakeFights(("Boss", 100))));
    }

    [TestMethod]
    public void Detect_NoSharedNames_ReturnsZero()
    {
      var refFights = MakeFights(("BossA", 100));
      var targetFights = MakeFights(("BossB", 200));
      Assert.AreEqual(0.0, FightOffsetDetector.Detect(refFights, targetFights));
    }

    [TestMethod]
    public void Detect_SingleSharedFight_OneHourOffset()
    {
      // The user's actual scenario: one shared boss fight, target's clock is exactly 1h ahead.
      var refFights = MakeFights(("Taeshlin", 1000));
      var targetFights = MakeFights(("Taeshlin", 4600)); // 1000 + 3600

      var offset = FightOffsetDetector.Detect(refFights, targetFights);
      Assert.AreEqual(3600.0, offset);
    }

    [TestMethod]
    public void Detect_NegativeOffset()
    {
      var refFights = MakeFights(("Boss", 4600));
      var targetFights = MakeFights(("Boss", 1000));

      var offset = FightOffsetDetector.Detect(refFights, targetFights);
      Assert.AreEqual(-3600.0, offset);
    }

    [TestMethod]
    public void Detect_SnapsToNearest15Minutes()
    {
      // Drift inside the bin width should snap to the bin center. A 3580s real delta
      // (~59m 40s) is in the 3600s bin.
      var refFights = MakeFights(("Boss", 1000));
      var targetFights = MakeFights(("Boss", 4580));

      var offset = FightOffsetDetector.Detect(refFights, targetFights);
      Assert.AreEqual(3600.0, offset);
    }

    [TestMethod]
    public void Detect_HalfHourOffset()
    {
      // India / South Australia / Newfoundland-style 30-minute timezone offset.
      var refFights = MakeFights(("Boss", 1000));
      var targetFights = MakeFights(("Boss", 2800));

      var offset = FightOffsetDetector.Detect(refFights, targetFights);
      Assert.AreEqual(1800.0, offset);
    }

    [TestMethod]
    public void Detect_AlignedClocks_ReturnsZero()
    {
      // Same clock across both sources — modal bucket is zero.
      var refFights = MakeFights(("BossA", 100), ("BossB", 200), ("BossC", 300));
      var targetFights = MakeFights(("BossA", 105), ("BossB", 195), ("BossC", 310));

      var offset = FightOffsetDetector.Detect(refFights, targetFights);
      Assert.AreEqual(0.0, offset);
    }

    [TestMethod]
    public void Detect_RepeatedNamesNoiseSuppressed()
    {
      // 10 trash fights named the same in both sources, real offset 1h. The pairwise deltas
      // include 100 candidates (10*10), but with 15-min bucketing and 60-second-spaced trash
      // fights, all of a single target fight's pairings against ref fights fall into the same
      // bin (since 9 * 60s = 540s < 900s bin width). The +3600 bucket gets ~10 hits per
      // target, totaling ~100; noise buckets get fewer.
      var refList = new List<Fight>();
      var targetList = new List<Fight>();
      for (var i = 0; i < 10; i++)
      {
        refList.Add(MakeFight("Trash", 1000 + i * 60));
        targetList.Add(MakeFight("Trash", 4600 + i * 60));
      }

      var offset = FightOffsetDetector.Detect(refList, targetList);
      Assert.AreEqual(3600.0, offset);
    }

    [TestMethod]
    public void Detect_IgnoresInactivityFights()
    {
      // IsInactivity fights are not real combat and shouldn't anchor offset detection.
      var refList = new List<Fight>
      {
        MakeFight("Boss", 1000),
        new() { Name = Fight.Breaktime, BeginTime = 500, IsInactivity = true }
      };
      var targetList = new List<Fight>
      {
        MakeFight("Boss", 4600),
        new() { Name = Fight.Breaktime, BeginTime = 4400, IsInactivity = true }
      };

      var offset = FightOffsetDetector.Detect(refList, targetList);
      Assert.AreEqual(3600.0, offset);
    }

    [TestMethod]
    public void DetectAll_PicksLargestSourceAsAnchor()
    {
      // Largest = most fights. Anchor's offset is always 0.
      var anchor = MakeFights(("BossA", 100), ("BossB", 200), ("BossC", 300));
      var smaller = MakeFights(("BossA", 3700)); // 1h ahead of anchor

      var detected = FightOffsetDetector.DetectAll(new[]
      {
        ("Anchor", (IList<Fight>)anchor),
        ("Smaller", (IList<Fight>)smaller)
      });

      Assert.AreEqual(2, detected.Count);
      Assert.AreEqual(0.0, detected["Anchor"]);
      Assert.AreEqual(3600.0, detected["Smaller"]);
    }

    [TestMethod]
    public void DetectAll_SmallerThenLarger_AnchorStillLargest()
    {
      // Order in input shouldn't matter; the larger source is always the anchor.
      var smaller = MakeFights(("BossA", 3700));
      var anchor = MakeFights(("BossA", 100), ("BossB", 200), ("BossC", 300));

      var detected = FightOffsetDetector.DetectAll(new[]
      {
        ("Smaller", (IList<Fight>)smaller),
        ("Anchor", (IList<Fight>)anchor)
      });

      Assert.AreEqual(0.0, detected["Anchor"]);
      Assert.AreEqual(3600.0, detected["Smaller"]);
    }

    [TestMethod]
    public void DetectAll_EmptyInput_ReturnsEmpty()
    {
      var detected = FightOffsetDetector.DetectAll(new (string, IList<Fight>)[] { });
      Assert.AreEqual(0, detected.Count);
    }

    [TestMethod]
    public void DetectAll_SkipsSourcesWithNoFights()
    {
      var anchor = MakeFights(("BossA", 100), ("BossB", 200));
      var empty = new List<Fight>();

      var detected = FightOffsetDetector.DetectAll(new[]
      {
        ("Anchor", (IList<Fight>)anchor),
        ("Empty", (IList<Fight>)empty)
      });

      Assert.IsTrue(detected.ContainsKey("Anchor"));
      Assert.IsFalse(detected.ContainsKey("Empty"));
    }

    private static Fight MakeFight(string name, double beginTime) => new()
    {
      Name = name,
      BeginTime = beginTime,
      LastTime = beginTime + 10
    };

    private static List<Fight> MakeFights(params (string Name, double Begin)[] entries)
    {
      var list = new List<Fight>();
      foreach (var (name, begin) in entries)
      {
        list.Add(MakeFight(name, begin));
      }
      return list;
    }
  }
}
