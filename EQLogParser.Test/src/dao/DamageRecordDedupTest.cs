using EQLogParser;

namespace EQLogParserTest
{
  // Verifies the multi-log dedup tolerance introduced for DD attribution:
  // a named SubType from one log source must deduplicate against an "Unknown"
  // SubType from another source that could not observe the cast line.
  [TestClass]
  public class DamageRecordDedupTest
  {
    private static DamageRecord MakeDd(string subType) => new()
    {
      Attacker = "Ezran",
      Defender = "A mob",
      Type = Labels.Dd,
      SubType = subType,
      Total = 2804,
      ModifiersMask = 0,
    };

    [TestMethod]
    public void NamedAndUnknown_AreEqual()
    {
      var named = MakeDd("Burst of Flames");
      var unknown = MakeDd(Labels.Unk);
      Assert.AreEqual(named, unknown, "Named DD must equal Unknown DD for dedup");
      Assert.AreEqual(unknown, named, "Equality must be symmetric");
    }

    [TestMethod]
    public void NamedAndUnknown_HaveSameHashCode()
    {
      // Hash code must be consistent with Equals for HashSet/Dictionary dedup to work.
      var named = MakeDd("Burst of Flames");
      var unknown = MakeDd(Labels.Unk);
      Assert.AreEqual(named.GetHashCode(), unknown.GetHashCode());
    }

    [TestMethod]
    public void TwoNamed_SameSpell_AreEqual()
    {
      var a = MakeDd("Burst of Flames");
      var b = MakeDd("Burst of Flames");
      Assert.AreEqual(a, b);
    }

    [TestMethod]
    public void TwoNamed_DifferentSpells_AreNotEqual()
    {
      var a = MakeDd("Burst of Flames");
      var b = MakeDd("Force Strike");
      Assert.AreNotEqual(a, b, "Different spell names must not dedup");
    }

    [TestMethod]
    public void TwoUnknown_AreEqual()
    {
      var a = MakeDd(Labels.Unk);
      var b = MakeDd(Labels.Unk);
      Assert.AreEqual(a, b);
    }

    [TestMethod]
    public void NonDdRecord_SubTypeMattersForEquality()
    {
      // The Unknown-wildcard logic must NOT apply to other types (DoT, Melee, etc.).
      var a = new DamageRecord { Attacker = "Ezran", Defender = "Mob", Type = Labels.Dot, SubType = "Pyre", Total = 100 };
      var b = new DamageRecord { Attacker = "Ezran", Defender = "Mob", Type = Labels.Dot, SubType = Labels.Unk, Total = 100 };
      Assert.AreNotEqual(a, b, "SubType wildcard must only apply to DD records");
    }
  }
}
