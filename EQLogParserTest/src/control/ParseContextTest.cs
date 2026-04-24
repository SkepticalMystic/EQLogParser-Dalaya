using System.Collections.Generic;
using EQLogParser;

namespace EQLogParserTest
{
  [TestClass]
  public class ParseContextTest
  {
    [TestMethod]
    public void CreateIsolated_ProducesIndependentInstances()
    {
      var a = ParseContext.CreateIsolated();
      var b = ParseContext.CreateIsolated();

      Assert.AreNotSame(a.DataManager, b.DataManager);
      Assert.AreNotSame(a.PlayerManager, b.PlayerManager);
      Assert.AreNotSame(a.DamageLineParser, b.DamageLineParser);
      Assert.AreNotSame(a.NpcDamageManager, b.NpcDamageManager);
      Assert.AreNotSame(a.DamageStatsManager, b.DamageStatsManager);
      Assert.AreNotSame(a.TankingStatsManager, b.TankingStatsManager);
      Assert.AreNotSame(a.HealingStatsManager, b.HealingStatsManager);

      // Isolated contexts should also be distinct from the app-wide singletons.
      Assert.AreNotSame(DataManager.Instance, a.DataManager);
      Assert.AreNotSame(PlayerManager.Instance, a.PlayerManager);
      Assert.AreNotSame(DamageLineParser.Instance, a.DamageLineParser);
      Assert.AreNotSame(DamageStatsManager.Instance, a.DamageStatsManager);
      Assert.AreNotSame(TankingStatsManager.Instance, a.TankingStatsManager);
      Assert.AreNotSame(HealingStatsManager.Instance, a.HealingStatsManager);
    }

    [TestMethod]
    public void CreateIsolated_PlayerManagerSideEffectsStayInContext()
    {
      // Parsing a line that verifies a player name via an isolated context should NOT mark
      // that player as verified in the Live PlayerManager.
      var ctx = ParseContext.CreateIsolated();
      var probeName = "IsolationProbe_Unique_42";

      Assert.IsFalse(PlayerManager.Instance.IsVerifiedPlayer(probeName));
      Assert.IsFalse(ctx.PlayerManager.IsVerifiedPlayer(probeName));

      // Directly mutate the isolated PlayerManager; we're not round-tripping through a parser
      // here because that would test a different thing — the point is that state mutations
      // via the isolated context don't leak to the Live singleton.
      ctx.PlayerManager.AddVerifiedPlayer(probeName, 1000.0);

      Assert.IsTrue(ctx.PlayerManager.IsVerifiedPlayer(probeName));
      Assert.IsFalse(PlayerManager.Instance.IsVerifiedPlayer(probeName),
        "Verification should not leak into the live PlayerManager singleton");
    }

    [TestMethod]
    public void CreateIsolated_DamageStatsManagerEventsStayInContext()
    {
      // Canary for the DamageSummary-reuse refactor: an isolated DamageStatsManager's
      // EventsGenerationStatus must not be observed by subscribers of the live singleton's
      // event, and vice versa. If this ever flips, two DamageSummary controls would stomp on
      // each other's state when BuildTotalStats runs on one of them.
      var ctx = ParseContext.CreateIsolated();

      var liveEvents = new List<StatsGenerationEvent>();
      var isolatedEvents = new List<StatsGenerationEvent>();
      void LiveHandler(StatsGenerationEvent e) { liveEvents.Add(e); }
      void IsolatedHandler(StatsGenerationEvent e) { isolatedEvents.Add(e); }

      DamageStatsManager.Instance.EventsGenerationStatus += LiveHandler;
      ctx.DamageStatsManager.EventsGenerationStatus += IsolatedHandler;
      try
      {
        // BuildTotalStats with an empty fight list exercises the full event path:
        // FireNewStatsEvent -> FireNoDataEvent (STARTED then NONPC). No damage records
        // required to prove event routing.
        ctx.DamageStatsManager.BuildTotalStats(new GenerateStatsOptions());

        Assert.IsTrue(isolatedEvents.Count > 0, "Isolated manager should emit generation events");
        Assert.AreEqual(0, liveEvents.Count,
          "Live DamageStatsManager must not receive events from an isolated manager");
      }
      finally
      {
        DamageStatsManager.Instance.EventsGenerationStatus -= LiveHandler;
        ctx.DamageStatsManager.EventsGenerationStatus -= IsolatedHandler;
      }
    }

    [TestMethod]
    public void CreateIsolated_TankingStatsManagerEventsStayInContext()
    {
      // Same canary as DamageStatsManager above, applied to TankingStatsManager. An embedded
      // TankingSummary in the Raid Damage window must not trigger the live DPS Summary tab's
      // tanking event handlers (or vice versa).
      var ctx = ParseContext.CreateIsolated();

      var liveEvents = new List<StatsGenerationEvent>();
      var isolatedEvents = new List<StatsGenerationEvent>();
      void LiveHandler(StatsGenerationEvent e) { liveEvents.Add(e); }
      void IsolatedHandler(StatsGenerationEvent e) { isolatedEvents.Add(e); }

      TankingStatsManager.Instance.EventsGenerationStatus += LiveHandler;
      ctx.TankingStatsManager.EventsGenerationStatus += IsolatedHandler;
      try
      {
        ctx.TankingStatsManager.BuildTotalStats(new GenerateStatsOptions());

        Assert.IsTrue(isolatedEvents.Count > 0, "Isolated manager should emit generation events");
        Assert.AreEqual(0, liveEvents.Count,
          "Live TankingStatsManager must not receive events from an isolated manager");
      }
      finally
      {
        TankingStatsManager.Instance.EventsGenerationStatus -= LiveHandler;
        ctx.TankingStatsManager.EventsGenerationStatus -= IsolatedHandler;
      }
    }

    [TestMethod]
    public void CreateIsolated_HealingStatsManagerEventsStayInContext()
    {
      // Same canary as DamageStatsManager/TankingStatsManager, applied to HealingStatsManager.
      // An embedded HealingSummary in the Raid Damage window must not trigger the live Healing
      // Summary tab's event handlers (or vice versa).
      var ctx = ParseContext.CreateIsolated();

      var liveEvents = new List<StatsGenerationEvent>();
      var isolatedEvents = new List<StatsGenerationEvent>();
      void LiveHandler(StatsGenerationEvent e) { liveEvents.Add(e); }
      void IsolatedHandler(StatsGenerationEvent e) { isolatedEvents.Add(e); }

      HealingStatsManager.Instance.EventsGenerationStatus += LiveHandler;
      ctx.HealingStatsManager.EventsGenerationStatus += IsolatedHandler;
      try
      {
        ctx.HealingStatsManager.BuildTotalStats(new GenerateStatsOptions());

        Assert.IsTrue(isolatedEvents.Count > 0, "Isolated manager should emit generation events");
        Assert.AreEqual(0, liveEvents.Count,
          "Live HealingStatsManager must not receive events from an isolated manager");
      }
      finally
      {
        HealingStatsManager.Instance.EventsGenerationStatus -= LiveHandler;
        ctx.HealingStatsManager.EventsGenerationStatus -= IsolatedHandler;
      }
    }

    [TestMethod]
    public void RaidDamageHost_DoesNotForwardChartOpened()
    {
      // Verifies the Phase 6/7 gotcha: the embedded DamageSummary in the Raid Damage window
      // must NOT participate in the DPS Trends chart flow. MainActions.FireChartOpened firing
      // must not reach a handler registered via RaidDamageHost.EventsChartOpened, because the
      // add/remove accessors are intentionally no-ops. If a future change wires them up, the
      // embedded instance would call FireChartEvent on its isolated manager — harmless today
      // (MainWindow subscribes to the live singleton's EventsUpdateDataPoint, not the
      // isolated one) but the no-op is the contract worth pinning.
      var host = new RaidDamageHost(DataManager.Instance);
      var calls = 0;
      void Handler(string _) { calls++; }

      host.EventsChartOpened += Handler;
      try
      {
        MainActions.FireChartOpened("Damage");
        Assert.AreEqual(0, calls, "RaidDamageHost.EventsChartOpened must not forward MainActions.EventsChartOpened");
      }
      finally
      {
        host.EventsChartOpened -= Handler;
      }
    }

    [TestMethod]
    public void CreateIsolated_DamageParserEventsStayInContext()
    {
      // Damage events fired by an isolated DamageLineParser should only reach its isolated
      // NpcDamageManager, not the Live DamageLineParser's subscribers.
      var ctx = ParseContext.CreateIsolated();

      var liveEvents = new List<DamageProcessedEvent>();
      var isolatedEvents = new List<DamageProcessedEvent>();
      void LiveHandler(DamageProcessedEvent e) { liveEvents.Add(e); }
      void IsolatedHandler(DamageProcessedEvent e) { isolatedEvents.Add(e); }

      DamageLineParser.Instance.EventsDamageProcessed += LiveHandler;
      ctx.DamageLineParser.EventsDamageProcessed += IsolatedHandler;
      try
      {
        // Fire a damage line through the isolated parser. The live parser never sees it.
        var line = "Useless crushes an abyssal terror for 9022 points of damage.";
        ctx.DamageLineParser.Process(new LineData
        {
          Action = line,
          BeginTime = 1000.0,
          Split = line.Split(' ')
        });

        Assert.AreEqual(1, isolatedEvents.Count, "Isolated parser should emit the event");
        Assert.AreEqual(0, liveEvents.Count, "Live parser must not receive events from an isolated context");
      }
      finally
      {
        DamageLineParser.Instance.EventsDamageProcessed -= LiveHandler;
        ctx.DamageLineParser.EventsDamageProcessed -= IsolatedHandler;
      }
    }
  }
}
