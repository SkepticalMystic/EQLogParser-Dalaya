using System.Collections.Generic;
using EQLogParser;

namespace EQLogParserTest
{
  [TestClass]
  public class FightDriftDetectorTest
  {
    [TestMethod]
    public void DriftFunction_PredictAndCorrect()
    {
      // drift(t) = 2 + 0.1 * (t - 100)
      // At t=100: drift=2, corrected=98. At t=200: drift=12, corrected=188.
      var d = new DriftFunction(intercept: 2, slope: 0.1, t0: 100);

      Assert.AreEqual(2, d.Predict(100), 1e-9);
      Assert.AreEqual(12, d.Predict(200), 1e-9);
      Assert.AreEqual(98, d.Correct(100), 1e-9);
      Assert.AreEqual(188, d.Correct(200), 1e-9);
    }

    [TestMethod]
    public void Detect_NoOtherSources_ReturnsEmpty()
    {
      var rec = MakeRec("A", "B", 100);
      var cluster = new List<(string, double, Fight)>
      {
        ("Anchor", 0, BuildFight("Boss", 100, 110, (105, rec)))
      };
      var drifts = FightDriftDetector.ComputeClusterDrifts(cluster);
      Assert.AreEqual(0, drifts.Count);
    }

    [TestMethod]
    public void Detect_NoSharedRecords_NoDriftFitForTarget()
    {
      // Both sources contributed events, but none of the records match → no pairs → no fit.
      var anchorFight = BuildFight("Boss", 100, 200,
        (105, MakeRec("Alice", "Boss", 100, type: Labels.Dd, sub: "X")));
      var targetFight = BuildFight("Boss", 100, 200,
        (110, MakeRec("Alice", "Boss", 200, type: Labels.Dd, sub: "Y")));

      var cluster = new List<(string, double, Fight)>
      {
        ("Anchor", 0, anchorFight),
        ("Target", 0, targetFight),
      };

      var drifts = FightDriftDetector.ComputeClusterDrifts(cluster);
      Assert.IsFalse(drifts.ContainsKey("Target"));
    }

    [TestMethod]
    public void Detect_TooFewPairs_NoFit()
    {
      // 4 paired events — below the minimum (5). Should bail out.
      var rec = MakeRec("Alice", "Boss", 100, type: Labels.Dd, sub: "X");
      var anchor = BuildFight("Boss", 100, 200,
        (105, rec), (115, rec), (125, rec), (135, rec));
      var target = BuildFight("Boss", 100, 200,
        (105, rec), (115, rec), (125, rec), (135, rec));

      var cluster = new List<(string, double, Fight)>
      {
        ("Anchor", 0, anchor),
        ("Target", 0, target),
      };

      var drifts = FightDriftDetector.ComputeClusterDrifts(cluster);
      Assert.IsFalse(drifts.ContainsKey("Target"));
    }

    [TestMethod]
    public void Detect_AlignedClocks_FitNearZero()
    {
      // Both sources observe identical times → drift is 0 everywhere → slope and intercept ~0.
      var rec = MakeRec("Alice", "Boss", 100, type: Labels.Dd, sub: "X");
      var times = new[] { 105.0, 115, 125, 135, 145, 155 };

      var anchor = BuildFightFromTimes("Boss", times, rec);
      var target = BuildFightFromTimes("Boss", times, rec);

      var cluster = new List<(string, double, Fight)>
      {
        ("Anchor", 0, anchor),
        ("Target", 0, target),
      };

      var drifts = FightDriftDetector.ComputeClusterDrifts(cluster);
      Assert.IsTrue(drifts.TryGetValue("Target", out var d));
      Assert.AreEqual(0, d.Slope, 1e-6);
      Assert.AreEqual(0, d.Intercept, 1e-6);
    }

    [TestMethod]
    public void Detect_ConstantDrift_RecoversIntercept()
    {
      // Constant drift (target is uniformly 5s late) → slope=0, intercept=5.
      var rec = MakeRec("Alice", "Boss", 100, type: Labels.Dd, sub: "X");
      var anchorTimes = new[] { 105.0, 115, 125, 135, 145, 155 };
      var targetTimes = new[] { 110.0, 120, 130, 140, 150, 160 };

      var anchor = BuildFightFromTimes("Boss", anchorTimes, rec);
      var target = BuildFightFromTimes("Boss", targetTimes, rec);

      var cluster = new List<(string, double, Fight)>
      {
        ("Anchor", 0, anchor),
        ("Target", 0, target),
      };

      var drifts = FightDriftDetector.ComputeClusterDrifts(cluster);
      Assert.IsTrue(drifts.TryGetValue("Target", out var d));
      Assert.AreEqual(0, d.Slope, 1e-6);
      Assert.AreEqual(5, d.Intercept, 1e-6);
    }

    [TestMethod]
    public void Detect_LinearGrowingDrift_RecoversSlope()
    {
      // Real-world scenario: drift grows linearly over the fight. Anchor times every 10s,
      // target's drift grows by 1s per anchor-step (so drift goes 1, 2, 3, 4, 5, 6 across the
      // 6 paired events). Note: the slope is fit in *target-time* domain, so the coefficient
      // is drift_change / target_time_step = 1 / 11 ≈ 0.0909 (target advances 11s per anchor
      // step because the 10s anchor advance plus 1s drift growth).
      var rec = MakeRec("Alice", "Boss", 100, type: Labels.Dd, sub: "X");
      var anchorTimes = new[] { 100.0, 110, 120, 130, 140, 150 };
      var targetTimes = new[] { 101.0, 112, 123, 134, 145, 156 };

      var anchor = BuildFightFromTimes("Boss", anchorTimes, rec);
      var target = BuildFightFromTimes("Boss", targetTimes, rec);

      var cluster = new List<(string, double, Fight)>
      {
        ("Anchor", 0, anchor),
        ("Target", 0, target),
      };

      var drifts = FightDriftDetector.ComputeClusterDrifts(cluster);
      Assert.IsTrue(drifts.TryGetValue("Target", out var d));
      Assert.AreEqual(1.0 / 11.0, d.Slope, 1e-6);
      // What matters in practice is what Predict() returns at the actual target timestamps —
      // those should match the observed drifts of 1s at the start and 6s at the end.
      Assert.AreEqual(1, d.Predict(101), 1e-6);
      Assert.AreEqual(6, d.Predict(156), 1e-6);
    }

    [TestMethod]
    public void Detect_ImplausibleSlope_Rejected()
    {
      // Pathological data: target's first observation aligned, then huge jumps. Slope would be
      // around 1 (i.e. 100% time dilation). Should be rejected.
      var rec = MakeRec("Alice", "Boss", 100, type: Labels.Dd, sub: "X");
      var anchorTimes = new[] { 100.0, 110, 120, 130, 140, 150 };
      var targetTimes = new[] { 100.0, 130, 160, 190, 220, 250 };  // ~3x time dilation

      var anchor = BuildFightFromTimes("Boss", anchorTimes, rec);
      var target = BuildFightFromTimes("Boss", targetTimes, rec);

      var cluster = new List<(string, double, Fight)>
      {
        ("Anchor", 0, anchor),
        ("Target", 0, target),
      };

      var drifts = FightDriftDetector.ComputeClusterDrifts(cluster);
      Assert.IsFalse(drifts.ContainsKey("Target"),
        "Implausibly large slope should be rejected; caller falls back to constant offset");
    }

    [TestMethod]
    public void Detect_RespectsConstantOffset()
    {
      // Cluster carries a non-zero offset on the target source. The drift fit should be
      // computed from offset-adjusted times, so a target whose times equal anchor-times-plus-3600
      // with offset=3600 set should produce zero drift (the offset already accounts for it).
      var rec = MakeRec("Alice", "Boss", 100, type: Labels.Dd, sub: "X");
      var anchorTimes = new[] { 100.0, 110, 120, 130, 140, 150 };
      var targetTimes = new[] { 3700.0, 3710, 3720, 3730, 3740, 3750 };

      var anchor = BuildFightFromTimes("Boss", anchorTimes, rec);
      var target = BuildFightFromTimes("Boss", targetTimes, rec);

      var cluster = new List<(string, double, Fight)>
      {
        ("Anchor", 0, anchor),
        ("Target", 3600, target),  // offset = 3600 maps target into anchor's frame
      };

      var drifts = FightDriftDetector.ComputeClusterDrifts(cluster);
      Assert.IsTrue(drifts.TryGetValue("Target", out var d));
      Assert.AreEqual(0, d.Slope, 1e-6);
      Assert.AreEqual(0, d.Intercept, 1e-6);
    }

    // Helpers

    private static DamageRecord MakeRec(string attacker, string defender, uint total,
      string type = Labels.Dd, string sub = "X") => new()
    {
      Attacker = attacker,
      Defender = defender,
      Total = total,
      Type = type,
      SubType = sub,
    };

    private static Fight BuildFight(string name, double begin, double last,
      params (double At, DamageRecord Rec)[] recs)
    {
      var f = new Fight { Name = name, BeginTime = begin, LastTime = last };
      foreach (var (at, rec) in recs)
      {
        var ag = new ActionGroup { BeginTime = at };
        ag.Actions.Add(rec);
        f.DamageBlocks.Add(ag);
      }
      return f;
    }

    private static Fight BuildFightFromTimes(string name, double[] times, DamageRecord rec)
    {
      var f = new Fight { Name = name, BeginTime = times[0], LastTime = times[^1] };
      foreach (var t in times)
      {
        var ag = new ActionGroup { BeginTime = t };
        ag.Actions.Add(rec);
        f.DamageBlocks.Add(ag);
      }
      return f;
    }
  }
}
