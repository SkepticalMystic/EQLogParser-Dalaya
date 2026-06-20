using EQLogParser;

namespace EQLogParserTest
{
  [TestClass]
  public class SpellCastTrackerTest
  {
    private SpellCastTracker _tracker;

    [TestInitialize]
    public void Setup() => _tracker = new SpellCastTracker();

    [TestMethod]
    public void AttributesHitToRecentCast()
    {
      _tracker.Push("Ezran", "Burst of Flames", 2500, 100.0); // 2.5s cast starting at T=100
      var result = _tracker.TryAttributeHit("Ezran", 103.0);   // hit at T=103 (within window)
      Assert.AreEqual("Burst of Flames", result);
    }

    [TestMethod]
    public void ReturnsNullWhenNoMatchingCast()
    {
      var result = _tracker.TryAttributeHit("Ezran", 103.0);
      Assert.IsNull(result);
    }

    [TestMethod]
    public void ReturnsNullForDifferentAttacker()
    {
      _tracker.Push("Ezran", "Burst of Flames", 2500, 100.0);
      var result = _tracker.TryAttributeHit("Owez", 103.0);
      Assert.IsNull(result);
    }

    [TestMethod]
    public void PrunesStaleCastBeforeWindow()
    {
      _tracker.Push("Ezran", "Burst of Flames", 2500, 100.0); // window closes at T=104.5
      var result = _tracker.TryAttributeHit("Ezran", 110.0);  // well past window
      Assert.IsNull(result);
    }

    [TestMethod]
    public void ConsumesCastOnAttribution()
    {
      _tracker.Push("Ezran", "Burst of Flames", 2500, 100.0);
      _tracker.TryAttributeHit("Ezran", 103.0); // consume
      var result = _tracker.TryAttributeHit("Ezran", 103.5); // second hit — no cast left
      Assert.IsNull(result);
    }

    [TestMethod]
    public void FifoOrderAttributesOlderCastFirst()
    {
      _tracker.Push("Ezran", "Spell A", 2000, 100.0); // window [100, 104]
      _tracker.Push("Ezran", "Spell B", 2000, 103.0); // window [103, 107]

      var first = _tracker.TryAttributeHit("Ezran", 103.0);  // matches Spell A (first in queue)
      var second = _tracker.TryAttributeHit("Ezran", 106.0); // now Spell B is front

      Assert.AreEqual("Spell A", first);
      Assert.AreEqual("Spell B", second);
    }

    [TestMethod]
    public void CancelledCastNotAttributed()
    {
      _tracker.Push("Ezran", "Burst of Flames", 2500, 100.0);
      _tracker.Cancel("Ezran", "Burst of Flames");
      var result = _tracker.TryAttributeHit("Ezran", 103.0);
      Assert.IsNull(result);
    }

    [TestMethod]
    public void CancelCancelsOnlyFirstMatchingEntry()
    {
      _tracker.Push("Ezran", "Burst of Flames", 2500, 100.0); // first
      _tracker.Push("Ezran", "Burst of Flames", 2500, 104.0); // second recast
      _tracker.Cancel("Ezran", "Burst of Flames");             // cancels first only

      // First hit should fall through the cancelled entry to the second cast
      var result = _tracker.TryAttributeHit("Ezran", 107.0);
      Assert.AreEqual("Burst of Flames", result, "Second cast should still attribute after first was cancelled");
    }

    [TestMethod]
    public void ClearRemovesAllEntries()
    {
      _tracker.Push("Ezran", "Burst of Flames", 2500, 100.0);
      _tracker.Clear();
      var result = _tracker.TryAttributeHit("Ezran", 103.0);
      Assert.IsNull(result);
    }

    [TestMethod]
    public void InstantCastMatchesOnSameTick()
    {
      _tracker.Push("Ezran", "Harm Touch", 0, 100.0); // CastingTimeMs=0, instant
      var result = _tracker.TryAttributeHit("Ezran", 100.0);
      Assert.AreEqual("Harm Touch", result);
    }

    [TestMethod]
    public void InstantCastMatchesWithinFudgeWindow()
    {
      _tracker.Push("Ezran", "Harm Touch", 0, 100.0);
      var result = _tracker.TryAttributeHit("Ezran", 101.5); // within 2s fudge
      Assert.AreEqual("Harm Touch", result);
    }
  }
}
