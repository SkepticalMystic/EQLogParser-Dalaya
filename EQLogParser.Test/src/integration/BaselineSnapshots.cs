using System.Collections.Generic;
using System.Linq;
using EQLogParser;

namespace EQLogParserTest.Integration
{
  // Snapshot DTOs serialized to JSON as captured baselines. One section per view (damage,
  // healing, tanking, merged, roster) per the memory `project_baseline_regression_test`.
  // For Phase 2 the damage section is the only one populated; healing/tanking land in
  // Phase 3 once multi-log baselines are captured.
  internal class FightSnapshot
  {
    public string Fight { get; set; } = "";
    public DamageView Damage { get; set; } = new();
    // Populated only for merged-fight snapshots (multi-source). Null on single-source.
    public DsBreakdown? Ds { get; set; }

    public class DsBreakdown
    {
      public int RecordCount { get; set; }
      // Per DS-holder totals after merge — pins both "DS records kept only from holder" and
      // pet/buff ownership correctness for shields.
      public Dictionary<string, long> ByHolder { get; set; } = new();
    }

    public class DamageView
    {
      public long TotalDamage { get; set; }
      public long TotalHits { get; set; }
      public int PlayerCount { get; set; }
      // Top-3 attackers by damage. Rankings are asserted exact (order matters), values asserted
      // within ±2%. Anything below top-3 lives in PlayerTotals — looser rank tolerance there.
      public List<TopEntry> Top3 { get; set; } = new();
      // All player attackers (and pets keyed by their attacker name, or rolled to owner when
      // AttackerOwner is set). Asserted within ±1.5% per entry.
      public Dictionary<string, PlayerTotal> PlayerTotals { get; set; } = new();
    }

    public class TopEntry
    {
      public int Rank { get; set; }
      public string Name { get; set; } = "";
      public long Damage { get; set; }
    }

    public class PlayerTotal
    {
      public long Damage { get; set; }
      public string? PetOwner { get; set; }
    }
  }

  internal static class FightSnapshotExtractor
  {
    // Phase 2: damage view only. Pet rollup uses Fight.PlayerDamageTotals as-is, which already
    // applies AttackerOwner → owner key (HandleDamageProcessed does `attackerOwner ?? attacker`).
    internal static FightSnapshot Build(Fight fight)
    {
      var snapshot = new FightSnapshot
      {
        Fight = fight.Name ?? "",
        Damage = new FightSnapshot.DamageView
        {
          TotalDamage = fight.DamageTotal,
          TotalHits = fight.DamageHits,
          PlayerCount = fight.PlayerDamageTotals.Count
        }
      };

      var ordered = fight.PlayerDamageTotals
        .OrderByDescending(kv => kv.Value.Damage)
        .ThenBy(kv => kv.Key)
        .ToList();

      var rank = 1;
      foreach (var kv in ordered.Take(3))
      {
        snapshot.Damage.Top3.Add(new FightSnapshot.TopEntry
        {
          Rank = rank++,
          Name = kv.Key,
          Damage = kv.Value.Damage
        });
      }

      foreach (var kv in ordered)
      {
        snapshot.Damage.PlayerTotals[kv.Key] = new FightSnapshot.PlayerTotal
        {
          Damage = kv.Value.Damage,
          PetOwner = kv.Value.PetOwner
        };
      }

      return snapshot;
    }

    // Augment a merged-fight snapshot with the DS breakdown. The merger filters DS records to
    // the holder's source via record.Attacker == sourcePlayer; this method scans the merged
    // fight's blocks and tallies by holder.
    internal static void AddDsBreakdown(FightSnapshot snapshot, Fight mergedFight)
    {
      var dsRecords = mergedFight.DamageBlocks
        .SelectMany(b => b.Actions.OfType<DamageRecord>())
        .Where(r => r.Type == Labels.Ds)
        .ToList();

      var byHolder = dsRecords
        .GroupBy(r => r.Attacker)
        .ToDictionary(g => g.Key, g => g.Sum(r => (long)r.Total));

      snapshot.Ds = new FightSnapshot.DsBreakdown
      {
        RecordCount = dsRecords.Count,
        ByHolder = byHolder
      };
    }

    // Compare current vs baseline. Tiered tolerances per `project_baseline_regression_test`:
    //   total damage / boss HP: ±1%
    //   per-player totals:      ±1.5%
    //   top-3 ranking:          exact order
    //   top-3 values:           ±2%
    //   hit counts / player counts: exact
    internal static void Compare(FightSnapshot expected, FightSnapshot actual)
    {
      Assert.AreEqual(expected.Fight, actual.Fight, "Fight name");

      var e = expected.Damage;
      var a = actual.Damage;

      BaselineHarness.AssertWithinPercent(e.TotalDamage, a.TotalDamage, 1.0,
        $"[{expected.Fight}] damage.totalDamage (boss HP)");
      BaselineHarness.AssertExact(e.TotalHits, a.TotalHits,
        $"[{expected.Fight}] damage.totalHits");
      BaselineHarness.AssertExact(e.PlayerCount, a.PlayerCount,
        $"[{expected.Fight}] damage.playerCount");

      BaselineHarness.AssertOrderedNamesMatch(
        e.Top3.Select(t => t.Name).ToList(),
        a.Top3.Select(t => t.Name).ToList(),
        3,
        $"[{expected.Fight}] damage.top3 ranking");

      for (var i = 0; i < e.Top3.Count && i < a.Top3.Count; i++)
      {
        BaselineHarness.AssertWithinPercent(e.Top3[i].Damage, a.Top3[i].Damage, 2.0,
          $"[{expected.Fight}] damage.top3[{i + 1}].damage ({e.Top3[i].Name})");
      }

      // Per-player totals: every baseline entry must exist in current with damage within
      // ±1.5%. Pet ownership must match exactly (changes to PetOwner are usually intentional —
      // pet mapping update — and should regen the baseline rather than soft-pass).
      foreach (var (player, baseline) in e.PlayerTotals)
      {
        Assert.IsTrue(a.PlayerTotals.TryGetValue(player, out var current),
          $"[{expected.Fight}] player '{player}' present in baseline but missing from current run");
        BaselineHarness.AssertWithinPercent(baseline.Damage, current!.Damage, 1.5,
          $"[{expected.Fight}] player '{player}' damage");
        Assert.AreEqual(baseline.PetOwner, current.PetOwner,
          $"[{expected.Fight}] player '{player}' pet owner");
      }

      // New players appearing in current but not baseline → fail. Don't allow silent
      // attribution growth.
      foreach (var player in a.PlayerTotals.Keys)
      {
        Assert.IsTrue(e.PlayerTotals.ContainsKey(player),
          $"[{expected.Fight}] player '{player}' appears in current run but not in baseline — " +
          $"investigate, then regen baseline if intentional");
      }

      // DS breakdown (merged-fight snapshots only). Record counts are exact (a different
      // count means dedup or filter logic regressed). Per-holder totals are ±1.5% — same
      // as per-player damage, since DS is just another damage attribution slot.
      if (expected.Ds != null)
      {
        Assert.IsNotNull(actual.Ds,
          $"[{expected.Fight}] DS section present in baseline but absent in current run");
        BaselineHarness.AssertExact(expected.Ds.RecordCount, actual.Ds!.RecordCount,
          $"[{expected.Fight}] ds.recordCount");

        foreach (var (holder, baselineTotal) in expected.Ds.ByHolder)
        {
          Assert.IsTrue(actual.Ds.ByHolder.TryGetValue(holder, out var currentTotal),
            $"[{expected.Fight}] DS holder '{holder}' present in baseline but missing from current run");
          BaselineHarness.AssertWithinPercent(baselineTotal, currentTotal, 1.5,
            $"[{expected.Fight}] ds.byHolder['{holder}']");
        }
        foreach (var holder in actual.Ds.ByHolder.Keys)
        {
          Assert.IsTrue(expected.Ds.ByHolder.ContainsKey(holder),
            $"[{expected.Fight}] new DS holder '{holder}' in current run — investigate, regen if intentional");
        }
      }
    }
  }
}
