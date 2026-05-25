using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using EQLogParser;

namespace EQLogParserTest.Integration
{
  // Shared infrastructure for the captured-value baseline tests. Locates per-developer
  // fixtures under EQLogParser.Test/Resources/baseline-logs/<scenario>/, parses a fixture
  // through the real pipeline (ParseContext + LogProcessor), and reads/writes snapshot JSON.
  //
  // First-run behavior: when a baseline JSON is missing, the test computes the snapshot and
  // writes it to disk, passing the assertion with a one-line "(baseline created)" note.
  // Bulk regen: set EQLOGPARSER_BASELINE_REGEN=1 to overwrite every baseline on the run.
  // Targeted regen: delete the specific baseline JSON file.
  //
  // See memory `project_baseline_regression_test` for scope and rationale.
  internal static class BaselineHarness
  {
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
      WriteIndented = true,
      PropertyNamingPolicy = JsonNamingPolicy.CamelCase
      // DictionaryKeyPolicy intentionally not set — player names are case-sensitive identifiers,
      // not properties. Lowercasing them breaks round-trip lookups against the live registry.
    };

    private static readonly Lazy<string> FixturesRoot = new(LocateFixturesRoot);

    internal static bool RegenAll =>
      Environment.GetEnvironmentVariable("EQLOGPARSER_BASELINE_REGEN") == "1";

    // Returns the absolute path to a fixture log file. Returns null if missing — caller is
    // expected to Assert.Inconclusive with a clear "set up your fixtures" message.
    internal static string LocateFixture(string scenario, string fileName)
    {
      var path = Path.Combine(FixturesRoot.Value, scenario, fileName);
      return File.Exists(path) ? path : null;
    }

    internal static string GetBaselinePath(string scenario, string key) =>
      Path.Combine(FixturesRoot.Value, scenario, "baselines", $"{key}.json");

    // Sanitize a fight name into a filesystem-safe key (Iskkath's Experiment → IskkathsExperiment).
    internal static string SanitizeKey(string input)
    {
      if (string.IsNullOrEmpty(input)) return "_";
      var cleaned = Regex.Replace(input, @"[^A-Za-z0-9]+", "-").Trim('-');
      return string.IsNullOrEmpty(cleaned) ? "_" : cleaned;
    }

    // Parse a single log file into a fresh, isolated ParseContext. Mirrors the
    // RaidDamageView.ParseFile path that production uses for the multi-log raid window —
    // same LogProcessor.ProcessSync entry point, same source-aware PlayerName injection.
    // Returns the populated list of Fights captured via FightManager.EventsNewFight.
    internal static List<Fight> ParseFixture(string sourcePlayer, string fixturePath) =>
      ParseFixtures(sourcePlayer, new[] { fixturePath });

    // Multi-file source: append all files into one isolated ParseContext, matching
    // RaidDamageView.AppendFileToSource's "one source, many log clips" pattern. Used by the
    // multi-log baseline when a player split their raid night across multiple logs (Kateila's
    // Iskkath's Experiment.txt + KK-OG-Skoal.txt).
    internal static List<Fight> ParseFixtures(string sourcePlayer, IList<string> fixturePaths)
    {
      var ctx = ParseContext.CreateIsolated();
      ctx.PlayerRegistry.PlayerName = sourcePlayer;

      var fights = new List<Fight>();
      ctx.FightManager.EventsNewFight += fights.Add;

      foreach (var fixturePath in fixturePaths)
      {
        var processor = new LogProcessor(fixturePath, ctx);
        using var fs = new FileStream(fixturePath, FileMode.Open, FileAccess.Read,
          FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        string line;
        while ((line = reader.ReadLine()) != null)
        {
          if (line.Length < 28) continue;
          var dt = DateUtil.ParseStandardDate(line);
          if (dt == DateTime.MinValue) continue;
          processor.ProcessSync(line, DateUtil.ToDotNetSeconds(dt));
        }
      }

      return fights;
    }

    // Run the merger over per-source fight lists, applying the same offset auto-detection
    // RaidDamageView does (FightOffsetDetector.DetectAll picks the largest source as anchor).
    // Returns the merged fights for assertion.
    internal static List<Fight> MergeSources(IList<(string SourcePlayer, List<Fight> Fights)> sources)
    {
      var detected = FightOffsetDetector.DetectAll(
        sources.Where(s => s.Fights.Count > 0)
          .Select(s => (s.SourcePlayer, (IList<Fight>)s.Fights)));

      var fightSources = sources.Select(s => new FightSource
      {
        SourcePlayer = s.SourcePlayer,
        Fights = s.Fights,
        TimeOffsetSeconds = detected.TryGetValue(s.SourcePlayer, out var o) ? o : 0
      });

      return FightMerger.MergeFromSources(fightSources);
    }

    // Snapshot pattern: if the baseline JSON exists AND we're not in bulk-regen mode, load and
    // compare. Otherwise, write the current snapshot to disk and pass with a note. Returns
    // true if a new baseline was written (caller can log/notify).
    internal static bool AssertOrWriteSnapshot<T>(string baselinePath, T snapshot, Action<T, T> compare)
      where T : class
    {
      Directory.CreateDirectory(Path.GetDirectoryName(baselinePath)!);

      if (!RegenAll && File.Exists(baselinePath))
      {
        var existing = JsonSerializer.Deserialize<T>(File.ReadAllText(baselinePath), JsonOptions)
                       ?? throw new InvalidOperationException($"Baseline at {baselinePath} deserialized to null");
        compare(existing, snapshot);
        return false;
      }

      File.WriteAllText(baselinePath, JsonSerializer.Serialize(snapshot, JsonOptions));
      return true;
    }

    // Locate the EQLogParser.Test project root by walking up from the assembly directory until
    // we find EQLogParser.Test.csproj. Tests run from bin\x64\Debug\net8.0-...; the project's
    // Resources/ folder is two-plus levels above that.
    private static string LocateFixturesRoot()
    {
      var assemblyDir = Path.GetDirectoryName(typeof(BaselineHarness).Assembly.Location)
                        ?? throw new InvalidOperationException("Assembly location unavailable");
      var dir = new DirectoryInfo(assemblyDir);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "EQLogParser.Test.csproj")))
      {
        dir = dir.Parent;
      }
      if (dir == null)
      {
        throw new InvalidOperationException(
          $"Could not locate EQLogParser.Test.csproj walking up from {assemblyDir}");
      }
      return Path.Combine(dir.FullName, "Resources", "baseline-logs");
    }

    // Tolerance helpers — tiered per memory `project_baseline_regression_test` decisions:
    // boss HP / per-player totals on a percentage band; rankings exact; hit counts exact.

    internal static void AssertWithinPercent(double expected, double actual, double percent, string message)
    {
      if (expected == 0)
      {
        Assert.AreEqual(expected, actual, $"{message} (expected 0, got {actual})");
        return;
      }
      var diff = Math.Abs(expected - actual);
      var ratio = diff / Math.Abs(expected);
      Assert.IsTrue(ratio <= percent / 100.0,
        $"{message} — expected {expected:N0}, got {actual:N0}, diff {diff:N0} ({ratio * 100:F3}% > {percent}%)");
    }

    internal static void AssertExact<T>(T expected, T actual, string message)
    {
      Assert.AreEqual(expected, actual, message);
    }

    internal static void AssertOrderedNamesMatch(IList<string> expected, IList<string> actual, int topN, string message)
    {
      var n = Math.Min(topN, Math.Min(expected.Count, actual.Count));
      for (var i = 0; i < n; i++)
      {
        if (expected[i] != actual[i])
        {
          Assert.Fail($"{message} — rank {i + 1} mismatch: expected '{expected[i]}', got '{actual[i]}'. " +
                      $"Full expected: [{string.Join(", ", expected.Take(topN))}]. " +
                      $"Full actual: [{string.Join(", ", actual.Take(topN))}]");
        }
      }
    }
  }
}
