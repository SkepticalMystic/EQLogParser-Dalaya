using System.Collections.Generic;
using System.IO;
using System.Linq;
using EQLogParser;

namespace EQLogParserTest.Integration
{
  // Captured-value baseline tests. Each fixture is a real Dalaya log under
  // Resources/baseline-logs/<scenario>/. First run writes a JSON snapshot per fight; later
  // runs assert against it with tiered tolerances (boss HP ±1%, per-player totals ±1.5%,
  // top-3 ranking exact, top-3 values ±2%, hit counts exact).
  //
  // Regen mechanisms:
  //   * Targeted: delete the specific baseline JSON file under
  //     Resources/baseline-logs/<scenario>/baselines/ and re-run.
  //   * Bulk:     set the environment variable EQLOGPARSER_BASELINE_REGEN=1.
  //
  // Categorize the reason for any regen — intentional improvement / new fixture / Dalaya log
  // format change / test scope expansion — so silent baseline drift never happens.
  //
  // Phase 2: damage view, single-source small fixture (Kateila's Iskkath's Experiment) to
  // exercise the harness end-to-end. Healing/tanking/multi-source baselines land in Phase 3.
  [TestClass]
  public class BaselineCapturedTest
  {
    private const string Scenario = "raid-night-5-10";
    // Threshold for which fights get baselined. Lower → more coverage, more JSON files; higher
    // → focused on real boss/lieutenant fights, less variance noise. 100 hits roughly targets
    // boss-tier engagements while filtering low-value trash mobs (Kaezulian snipers, walking
    // spores, etc.) that pad the baseline set without adding regression signal.
    private const int MinHitsToBaseline = 100;

    [TestInitialize]
    public void Setup()
    {
      ConfigUtil.PlayerName = "Selfie";
    }

    [TestMethod]
    public void Kateila_Experiment_DamageView()
    {
      var fixturePath = BaselineHarness.LocateFixture(Scenario, "Kateila.log");
      if (fixturePath == null)
      {
        Assert.Inconclusive(
          $"Fixture not found at Resources/baseline-logs/{Scenario}/Kateila.log. " +
          "Copy your eqlog_Kateila_dalaya-Experiment.txt there to enable this test. " +
          "See memory `project_baseline_regression_test` for fixture setup.");
        return;
      }

      var fights = BaselineHarness.ParseFixture("Kateila", fixturePath);
      var meaningful = fights
        .Where(f => !f.IsInactivity && f.DamageHits >= MinHitsToBaseline)
        .ToList();

      Assert.IsTrue(meaningful.Count > 0,
        $"Expected at least one fight with >= {MinHitsToBaseline} hits, got {fights.Count} fights total");

      var created = new List<string>();
      foreach (var fight in meaningful)
      {
        var snapshot = FightSnapshotExtractor.Build(fight);
        var key = $"Kateila-{BaselineHarness.SanitizeKey(fight.Name)}";
        var path = BaselineHarness.GetBaselinePath(Scenario, key);
        var wasCreated = BaselineHarness.AssertOrWriteSnapshot(path, snapshot, FightSnapshotExtractor.Compare);
        if (wasCreated)
        {
          created.Add(Path.GetFileName(path));
        }
      }

      if (created.Count > 0)
      {
        // First-run or regen: tests passed by writing baselines. Surface what was written so a
        // CI run that suddenly creates fresh baselines is obvious.
        TestContext?.WriteLine(
          $"Wrote {created.Count} baseline(s): {string.Join(", ", created)}. " +
          $"Re-run to assert against them.");
      }
    }

    [TestMethod]
    public void MultiLog_5Source_RaidDamage_MergedView()
    {
      // The headline integration test: 5 raid logs (Damocles, Drucilla, Ezran, Illi, Kateila
      // with both files attached to her source) parsed independently, then merged via
      // FightMerger.MergeFromSources with auto-detected offsets. Mirrors RaidDamageView's
      // production flow. Per merged fight with ≥ MinHitsToBaseline hits, snapshots the
      // damage view + DS breakdown and asserts against the JSON baseline.
      //
      // What this catches that single-source tests can't:
      //   * Dedup correctness across observers — same physical hit must collapse to one
      //   * DS filtering — DS records survive only from the holder's source
      //   * Self-cast resolution at scale — multiple sources observing each other's casts
      //   * Drift compensation — clocks aren't perfectly synced across machines
      //   * Pet rollup correctness when pet owners are inferred from multiple sources
      var fixtures = new (string Player, string[] Files)[]
      {
        ("Damocles", new[] { "Damocles.log" }),
        ("Drucilla", new[] { "Drucilla.log" }),
        ("Ezran",    new[] { "Ezran.log" }),
        ("Illi",     new[] { "Illi.log" }),
        // Kateila split her raid night across two clips — same source, two log files,
        // matching RaidDamageView.AppendFileToSource's "one player, many clips" pattern.
        ("Kateila",  new[] { "Kateila.log", "Kateila-KK-OG-Skoal.log" })
      };

      // Resolve fixtures up front; if any are missing, skip the whole test with a clear
      // setup message rather than failing on partial data.
      var resolved = new List<(string Player, string[] Paths)>();
      foreach (var (player, files) in fixtures)
      {
        var paths = files.Select(f => BaselineHarness.LocateFixture(Scenario, f)).ToArray();
        if (paths.Any(p => p == null))
        {
          var missing = files.Where((_, i) => paths[i] == null);
          Assert.Inconclusive(
            $"Multi-log fixture incomplete: missing {string.Join(", ", missing)} " +
            $"under Resources/baseline-logs/{Scenario}/. Populate all 5 source logs to enable.");
          return;
        }
        resolved.Add((player, paths!));
      }

      // Parse each source independently. Kateila's two files append into one ParseContext.
      var perSource = resolved
        .Select(s => (s.Player, Fights: BaselineHarness.ParseFixtures(s.Player, s.Paths)))
        .ToList();

      var merged = BaselineHarness.MergeSources(perSource);

      // Whitespace-named fights are legitimate: Mistress Saitha spawns nameless "wand" adds
      // that the raid must burn to drop her shield. Dalaya logs them with the defender field
      // blanked ("Fertlebin  crushes     for 60 points of damage.", "    has taken N damage
      // from Y by Spell.", etc.), so the parser correctly produces Fight objects with
      // whitespace Name. These get baselined like every other fight; the SanitizeKey helper
      // turns whitespace names into "_" for the filename, and the per-name -pull-N suffix
      // disambiguates the two wand-add waves.
      var meaningful = merged
        .Where(f => f.DamageHits >= MinHitsToBaseline)
        .ToList();

      Assert.IsTrue(meaningful.Count >= 3,
        $"Expected ≥ 3 meaningful merged fights from the 4-boss raid (Iskkath + KK + OG + Skoal); got {meaningful.Count}. " +
        $"Total merged: {merged.Count}, total per-source fights: {string.Join(", ", perSource.Select(s => $"{s.Player}={s.Fights.Count}"))}");

      // Same boss can appear multiple times if the raid wiped and re-pulled — each is its own
      // Fight object with a unique BeginTime. Distinguish by per-name ordinal so pulls don't
      // collide on the baseline key. If the pull count later changes (merger now combines two
      // pulls into one), the affected baselines fail to assert and need explicit regen —
      // which is the right behavior, not silent baseline drift.
      var byName = meaningful
        .GroupBy(f => f.Name)
        .ToList();

      var created = new List<string>();
      foreach (var group in byName)
      {
        var pulls = group.OrderBy(f => f.BeginTime).ToList();
        for (var i = 0; i < pulls.Count; i++)
        {
          var fight = pulls[i];
          var snapshot = FightSnapshotExtractor.Build(fight);
          FightSnapshotExtractor.AddDsBreakdown(snapshot, fight);

          var suffix = pulls.Count > 1 ? $"-pull-{i + 1}" : "";
          var key = $"Merged-{BaselineHarness.SanitizeKey(fight.Name)}{suffix}";
          var path = BaselineHarness.GetBaselinePath(Scenario, key);
          if (BaselineHarness.AssertOrWriteSnapshot(path, snapshot, FightSnapshotExtractor.Compare))
          {
            created.Add(Path.GetFileName(path));
          }
        }
      }

      if (created.Count > 0)
      {
        TestContext?.WriteLine(
          $"Wrote {created.Count} merged baseline(s): {string.Join(", ", created)}. " +
          $"Re-run to assert against them.");
      }
    }

    public TestContext? TestContext { get; set; }
  }
}
