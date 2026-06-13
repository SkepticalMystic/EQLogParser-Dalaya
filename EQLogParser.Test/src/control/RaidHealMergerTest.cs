using EQLogParser;
using System.Collections.Generic;
using System.Linq;

namespace EQLogParserTest
{
  // Cross-source heal merge contract for the Raid Damage Healing tab. Heal records don't ride
  // on the merged Fight objects (only combat damage does), so RaidHealMerger unions each
  // source's time-indexed heal log, offset-aligned into the merged frame and deduped across
  // sources. These tests pin that contract — the multi-log healing comparison is the whole
  // reason the merge exists, and a regression here would silently double-count or drop heals.
  // See memory project_healing_raid_tab.
  [TestClass]
  public class RaidHealMergerTest
  {
    private static HealRecord Heal(string healer, string healed, uint total, string spell, string type = "Heal") =>
      new() { Healer = healer, Healed = healed, Total = total, SubType = spell, Type = type };

    private static (IEnumerable<(double, HealRecord)>, double) Source(
      double offset, params (double Time, HealRecord Record)[] heals) =>
      (heals.AsEnumerable(), offset);

    [TestMethod]
    public void Merge_DisjointHealers_KeepsAll()
    {
      // The common case: two healers, each log carrying only its own heals. Nothing dedups;
      // both healers' output shows up so they can be compared.
      var a = Source(0, (100.0, Heal("Cleric", "Tank", 500, "Complete Healing")));
      var b = Source(0, (100.0, Heal("Druid", "Tank", 300, "Chloroplast", "Hot")));

      var merged = RaidHealMerger.Merge([a, b]).ToList();

      Assert.AreEqual(2, merged.Count);
      CollectionAssert.AreEquivalent(
        new[] { "Cleric", "Druid" }, merged.Select(m => m.Record.Healer).ToList());
    }

    [TestMethod]
    public void Merge_SameHealObservedByTwoLogs_DedupsToOne()
    {
      // Cleric's heal appears in their own log and (because Dalaya can echo nearby heals) in
      // the Druid's log too. Same time/healer/healed/amount/spell → one record, not two.
      var clericHeal = (100.0, Heal("Cleric", "Tank", 500, "Complete Healing"));
      var a = Source(0, clericHeal);
      var b = Source(0, clericHeal, (105.0, Heal("Druid", "Tank", 300, "Chloroplast", "Hot")));

      var merged = RaidHealMerger.Merge([a, b]).ToList();

      Assert.AreEqual(2, merged.Count, "the shared Cleric heal should collapse to one record");
      Assert.AreEqual(1, merged.Count(m => m.Record.Healer == "Cleric"));
      Assert.AreEqual(1, merged.Count(m => m.Record.Healer == "Druid"));
    }

    [TestMethod]
    public void Merge_AppliesSourceOffsetToMergedFrame()
    {
      // A source 60s ahead of the anchor: its heal at raw t=160 aligns to t=100 (raw - offset),
      // matching how FightMerger shifts that source's fights into the merged frame.
      var anchor = Source(0, (100.0, Heal("Cleric", "Tank", 500, "Complete Healing")));
      var ahead = Source(60, (160.0, Heal("Druid", "Tank", 300, "Chloroplast", "Hot")));

      var merged = RaidHealMerger.Merge([anchor, ahead]).ToList();

      var druid = merged.Single(m => m.Record.Healer == "Druid");
      Assert.AreEqual(100.0, druid.Time, 0.001, "offset source heal should shift into the merged frame");
    }

    [TestMethod]
    public void Merge_OffsetAlignedDuplicate_StillDedups()
    {
      // Two clocks 60s apart both record the same real heal: anchor at t=100, the +60s source
      // at raw t=160. After alignment both land at t=100 → one record.
      var anchor = Source(0, (100.0, Heal("Cleric", "Tank", 500, "Complete Healing")));
      var skewed = Source(60, (160.0, Heal("Cleric", "Tank", 500, "Complete Healing")));

      var merged = RaidHealMerger.Merge([anchor, skewed]).ToList();

      Assert.AreEqual(1, merged.Count, "the same heal seen on two offset clocks should dedup after alignment");
    }

    [TestMethod]
    public void Merge_IdenticalHealsFromSameSource_BothKept()
    {
      // A single log legitimately recording two identical heals at the same second (e.g. a HoT
      // tick coinciding with a direct heal of the same amount/spell) must NOT be collapsed —
      // dedup targets cross-log echoes, not a source's own distinct events.
      var h = Heal("Cleric", "Tank", 500, "Complete Healing");
      var a = Source(0, (100.0, h), (100.0, h));

      var merged = RaidHealMerger.Merge([a]).ToList();

      Assert.AreEqual(2, merged.Count, "a source's own identical-keyed heals should both survive");
    }
  }
}
