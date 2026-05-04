namespace EQLogParser
{
  // Bundle of collaborators used by a single parse flow. The Live context uses the app-wide
  // singletons; an isolated context can be constructed via CreateIsolated() for background
  // parsing (e.g. the Raid Damage window loading another player's exported log) without
  // touching the live session's state or events.
  internal class ParseContext
  {
    internal EQDataStore DataStore { get; }
    internal PlayerRegistry PlayerRegistry { get; }
    internal DamageLineParser DamageLineParser { get; }
    internal HealingLineParser HealingLineParser { get; }
    internal CastLineParser CastLineParser { get; }
    internal MiscLineParser MiscLineParser { get; }
    internal PreLineParser PreLineParser { get; }
    internal FightManager FightManager { get; }
    internal DamageStatsBuilder DamageStatsBuilder { get; }
    internal TankingStatsBuilder TankingStatsBuilder { get; }
    internal HealingStatsBuilder HealingStatsBuilder { get; }

    internal ParseContext(
      EQDataStore dataStore,
      PlayerRegistry playerRegistry,
      DamageLineParser damageLineParser,
      HealingLineParser healingLineParser,
      CastLineParser castLineParser,
      MiscLineParser miscLineParser,
      PreLineParser preLineParser,
      FightManager fightManager,
      DamageStatsBuilder damageStatsBuilder,
      TankingStatsBuilder tankingStatsBuilder,
      HealingStatsBuilder healingStatsBuilder)
    {
      DataStore = dataStore;
      PlayerRegistry = playerRegistry;
      DamageLineParser = damageLineParser;
      HealingLineParser = healingLineParser;
      CastLineParser = castLineParser;
      MiscLineParser = miscLineParser;
      PreLineParser = preLineParser;
      FightManager = fightManager;
      DamageStatsBuilder = damageStatsBuilder;
      TankingStatsBuilder = tankingStatsBuilder;
      HealingStatsBuilder = healingStatsBuilder;
    }

    // The live parse context — singletons bundled together. FightManager is instantiated
    // by MainWindow in the live flow and injected here.
    internal static ParseContext Live(FightManager liveFightManager) => new(
      EQDataStore.Instance,
      PlayerRegistry.Instance,
      DamageLineParser.Instance,
      HealingLineParser.Instance,
      CastLineParser.Instance,
      MiscLineParser.Instance,
      PreLineParser.Instance,
      liveFightManager,
      DamageStatsBuilder.Instance,
      TankingStatsBuilder.Instance,
      HealingStatsBuilder.Instance);

    // A fully isolated context. Reference data (spells, classes, pet names) is re-loaded into
    // the new EQDataStore/PlayerRegistry — callers should reuse the returned context rather than
    // recreating for each parse.
    //
    // The isolated PlayerRegistry is seeded from the live singleton so known players/pets aren't
    // misclassified as NPCs when parsing a partial/exported log. Subsequent mutations stay in
    // the isolated context — nothing leaks back to the live PlayerRegistry.
    internal static ParseContext CreateIsolated()
    {
      var ds = new EQDataStore();
      var pr = new PlayerRegistry(autoSave: false);
      pr.SeedFrom(PlayerRegistry.Instance);
      var dlp = new DamageLineParser(ds, pr);
      var hlp = new HealingLineParser(pr);
      var clp = new CastLineParser(ds, pr);
      var mlp = new MiscLineParser(ds, pr);
      var plp = new PreLineParser(pr);
      var fm = new FightManager(ds, pr, dlp);
      // Wire FightManager into the parser after both exist (avoids a construction cycle).
      dlp.FightManager = fm;
      var dsb = new DamageStatsBuilder(ds, pr, fm);
      var tsb = new TankingStatsBuilder(ds, pr, fm);
      var hsb = new HealingStatsBuilder(ds, pr, fm);
      return new ParseContext(ds, pr, dlp, hlp, clp, mlp, plp, fm, dsb, tsb, hsb);
    }
  }
}
