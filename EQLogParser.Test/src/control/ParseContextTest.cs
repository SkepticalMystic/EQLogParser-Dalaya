using System.Collections.Generic;
using System.Linq;
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

      Assert.AreNotSame(a.DataStore, b.DataStore);
      Assert.AreNotSame(a.PlayerRegistry, b.PlayerRegistry);
      Assert.AreNotSame(a.DamageLineParser, b.DamageLineParser);
      Assert.AreNotSame(a.FightManager, b.FightManager);
      Assert.AreNotSame(a.DamageStatsBuilder, b.DamageStatsBuilder);
      Assert.AreNotSame(a.TankingStatsBuilder, b.TankingStatsBuilder);
      Assert.AreNotSame(a.HealingStatsBuilder, b.HealingStatsBuilder);

      // Isolated contexts should also be distinct from the app-wide singletons.
      Assert.AreNotSame(EQDataStore.Instance, a.DataStore);
      Assert.AreNotSame(PlayerRegistry.Instance, a.PlayerRegistry);
      Assert.AreNotSame(DamageLineParser.Instance, a.DamageLineParser);
      Assert.AreNotSame(DamageStatsBuilder.Instance, a.DamageStatsBuilder);
      Assert.AreNotSame(TankingStatsBuilder.Instance, a.TankingStatsBuilder);
      Assert.AreNotSame(HealingStatsBuilder.Instance, a.HealingStatsBuilder);
    }

    [TestMethod]
    public void CreateIsolated_PlayerRegistrySideEffectsStayInContext()
    {
      // Parsing a line that verifies a player name via an isolated context should NOT mark
      // that player as verified in the live PlayerRegistry.
      var ctx = ParseContext.CreateIsolated();
      var probeName = "IsolationProbe_Unique_42";

      Assert.IsFalse(PlayerRegistry.Instance.IsVerifiedPlayer(probeName));
      Assert.IsFalse(ctx.PlayerRegistry.IsVerifiedPlayer(probeName));

      // Directly mutate the isolated PlayerRegistry; we're not round-tripping through a parser
      // here because that would test a different thing — the point is that state mutations
      // via the isolated context don't leak to the live singleton.
      ctx.PlayerRegistry.AddVerifiedPlayer(probeName, 1000.0);

      Assert.IsTrue(ctx.PlayerRegistry.IsVerifiedPlayer(probeName));
      Assert.IsFalse(PlayerRegistry.Instance.IsVerifiedPlayer(probeName),
        "Verification should not leak into the live PlayerRegistry singleton");
    }

    [TestMethod]
    public void CreateIsolated_DamageStatsBuilderEventsStayInContext()
    {
      // Canary for the DamageSummary-reuse refactor: an isolated DamageStatsBuilder's
      // EventsGenerationStatus must not be observed by subscribers of the live singleton's
      // event, and vice versa. If this ever flips, two DamageSummary controls would stomp on
      // each other's state when BuildTotalStats runs on one of them.
      var ctx = ParseContext.CreateIsolated();

      var liveEvents = new List<StatsGenerationEvent>();
      var isolatedEvents = new List<StatsGenerationEvent>();
      void LiveHandler(StatsGenerationEvent e) { liveEvents.Add(e); }
      void IsolatedHandler(StatsGenerationEvent e) { isolatedEvents.Add(e); }

      DamageStatsBuilder.Instance.EventsGenerationStatus += LiveHandler;
      ctx.DamageStatsBuilder.EventsGenerationStatus += IsolatedHandler;
      try
      {
        // BuildTotalStats with an empty fight list exercises the full event path:
        // FireNewStatsEvent -> FireNoDataEvent (STARTED then NONPC). No damage records
        // required to prove event routing.
        ctx.DamageStatsBuilder.BuildTotalStats(new GenerateStatsOptions());

        Assert.IsTrue(isolatedEvents.Count > 0, "Isolated builder should emit generation events");
        Assert.AreEqual(0, liveEvents.Count,
          "Live DamageStatsBuilder must not receive events from an isolated builder");
      }
      finally
      {
        DamageStatsBuilder.Instance.EventsGenerationStatus -= LiveHandler;
        ctx.DamageStatsBuilder.EventsGenerationStatus -= IsolatedHandler;
      }
    }

    [TestMethod]
    public void CreateIsolated_TankingStatsBuilderEventsStayInContext()
    {
      // Same canary as DamageStatsBuilder above, applied to TankingStatsBuilder. An embedded
      // TankingSummary in the Raid Damage window must not trigger the live DPS Summary tab's
      // tanking event handlers (or vice versa).
      var ctx = ParseContext.CreateIsolated();

      var liveEvents = new List<StatsGenerationEvent>();
      var isolatedEvents = new List<StatsGenerationEvent>();
      void LiveHandler(StatsGenerationEvent e) { liveEvents.Add(e); }
      void IsolatedHandler(StatsGenerationEvent e) { isolatedEvents.Add(e); }

      TankingStatsBuilder.Instance.EventsGenerationStatus += LiveHandler;
      ctx.TankingStatsBuilder.EventsGenerationStatus += IsolatedHandler;
      try
      {
        ctx.TankingStatsBuilder.BuildTotalStats(new GenerateStatsOptions());

        Assert.IsTrue(isolatedEvents.Count > 0, "Isolated builder should emit generation events");
        Assert.AreEqual(0, liveEvents.Count,
          "Live TankingStatsBuilder must not receive events from an isolated builder");
      }
      finally
      {
        TankingStatsBuilder.Instance.EventsGenerationStatus -= LiveHandler;
        ctx.TankingStatsBuilder.EventsGenerationStatus -= IsolatedHandler;
      }
    }

    [TestMethod]
    public void CreateIsolated_HealingStatsBuilderEventsStayInContext()
    {
      // Same canary as DamageStatsBuilder/TankingStatsBuilder, applied to HealingStatsBuilder.
      // An embedded HealingSummary in the Raid Damage window must not trigger the live Healing
      // Summary tab's event handlers (or vice versa).
      var ctx = ParseContext.CreateIsolated();

      var liveEvents = new List<StatsGenerationEvent>();
      var isolatedEvents = new List<StatsGenerationEvent>();
      void LiveHandler(StatsGenerationEvent e) { liveEvents.Add(e); }
      void IsolatedHandler(StatsGenerationEvent e) { isolatedEvents.Add(e); }

      HealingStatsBuilder.Instance.EventsGenerationStatus += LiveHandler;
      ctx.HealingStatsBuilder.EventsGenerationStatus += IsolatedHandler;
      try
      {
        ctx.HealingStatsBuilder.BuildTotalStats(new GenerateStatsOptions());

        Assert.IsTrue(isolatedEvents.Count > 0, "Isolated builder should emit generation events");
        Assert.AreEqual(0, liveEvents.Count,
          "Live HealingStatsBuilder must not receive events from an isolated builder");
      }
      finally
      {
        HealingStatsBuilder.Instance.EventsGenerationStatus -= LiveHandler;
        ctx.HealingStatsBuilder.EventsGenerationStatus -= IsolatedHandler;
      }
    }

    [TestMethod]
    public void RaidDamageHost_DoesNotForwardChartOpened()
    {
      // Verifies the Phase 6/7 gotcha: the embedded DamageSummary in the Raid Damage window
      // must NOT participate in the DPS Trends chart flow. MainActions.FireChartOpened firing
      // must not reach a handler registered via RaidDamageHost.EventsChartOpened, because the
      // add/remove accessors are intentionally no-ops. If a future change wires them up, the
      // embedded instance would call FireChartEvent on its isolated builder — harmless today
      // (MainWindow subscribes to the live singleton's EventsUpdateDataPoint, not the
      // isolated one) but the no-op is the contract worth pinning.
      var host = new RaidDamageHost(FightManager.Instance);
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
      // FightManager, not the live DamageLineParser's subscribers.
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

    [TestMethod]
    public void SeedFrom_CopiesPlayerClass_SoIsolatedSummaryCanShowClassColumn()
    {
      // Regression for the Raid Damage Class column. The embedded summary reads GetPlayerClass
      // off its isolated _statsContext PlayerRegistry, which is populated only via SeedFrom.
      // Before the fix, SeedFrom copied verified players/pets/mercs but NOT the class maps, so
      // every merged player showed a blank Class. The main DPS/Tanking tabs read the live
      // registry directly and were unaffected, which is why only Raid Damage was blank.
      var className = EQDataStore.Instance.GetClassList().FirstOrDefault();
      Assert.IsFalse(string.IsNullOrEmpty(className), "Expected at least one class name from EQDataStore");

      var source = ParseContext.CreateIsolated();
      var dest = ParseContext.CreateIsolated();

      source.PlayerRegistry.AddVerifiedPlayer("Rome", 1000.0);
      source.PlayerRegistry.SetActivePlayerClass("Rome", className, 2, 1000.0);

      Assert.AreEqual(string.Empty, dest.PlayerRegistry.GetPlayerClass("Rome", 1000.0),
        "Sanity: dest must not know the class before seeding");

      dest.PlayerRegistry.SeedFrom(source.PlayerRegistry);

      Assert.AreEqual(className, dest.PlayerRegistry.GetPlayerClass("Rome", 1000.0),
        "SeedFrom must copy class data so the Raid Damage isolated stats context can show the Class column");
    }
  }
}
