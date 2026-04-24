namespace EQLogParser
{
  // Bundle of collaborators used by a single parse flow. The Live context uses the app-wide
  // singletons; an isolated context can be constructed via CreateIsolated() for background
  // parsing (e.g. the Raid Damage window loading another player's exported log) without
  // touching the live session's state or events.
  internal class ParseContext
  {
    internal DataManager DataManager { get; }
    internal PlayerManager PlayerManager { get; }
    internal DamageLineParser DamageLineParser { get; }
    internal HealingLineParser HealingLineParser { get; }
    internal CastLineParser CastLineParser { get; }
    internal MiscLineParser MiscLineParser { get; }
    internal PreLineParser PreLineParser { get; }
    internal NpcDamageManager NpcDamageManager { get; }
    internal DamageStatsManager DamageStatsManager { get; }
    internal TankingStatsManager TankingStatsManager { get; }
    internal HealingStatsManager HealingStatsManager { get; }

    internal ParseContext(
      DataManager dataManager,
      PlayerManager playerManager,
      DamageLineParser damageLineParser,
      HealingLineParser healingLineParser,
      CastLineParser castLineParser,
      MiscLineParser miscLineParser,
      PreLineParser preLineParser,
      NpcDamageManager npcDamageManager,
      DamageStatsManager damageStatsManager,
      TankingStatsManager tankingStatsManager,
      HealingStatsManager healingStatsManager)
    {
      DataManager = dataManager;
      PlayerManager = playerManager;
      DamageLineParser = damageLineParser;
      HealingLineParser = healingLineParser;
      CastLineParser = castLineParser;
      MiscLineParser = miscLineParser;
      PreLineParser = preLineParser;
      NpcDamageManager = npcDamageManager;
      DamageStatsManager = damageStatsManager;
      TankingStatsManager = tankingStatsManager;
      HealingStatsManager = healingStatsManager;
    }

    // The live parse context — singletons bundled together. NpcDamageManager is instantiated
    // by MainWindow in the live flow and injected here.
    internal static ParseContext Live(NpcDamageManager liveNpcDamageManager) => new(
      DataManager.Instance,
      PlayerManager.Instance,
      DamageLineParser.Instance,
      HealingLineParser.Instance,
      CastLineParser.Instance,
      MiscLineParser.Instance,
      PreLineParser.Instance,
      liveNpcDamageManager,
      DamageStatsManager.Instance,
      TankingStatsManager.Instance,
      HealingStatsManager.Instance);

    // A fully isolated context. Reference data (spells, classes, pet names) is re-loaded into
    // the new DataManager/PlayerManager — callers should reuse the returned context rather than
    // recreating for each parse.
    //
    // The isolated PlayerManager is seeded from the live singleton so known players/pets aren't
    // misclassified as NPCs when parsing a partial/exported log. Subsequent mutations stay in
    // the isolated context — nothing leaks back to the live PlayerManager.
    internal static ParseContext CreateIsolated()
    {
      var dm = new DataManager();
      var pm = new PlayerManager(autoSave: false);
      pm.SeedFrom(PlayerManager.Instance);
      var dlp = new DamageLineParser(dm, pm);
      var hlp = new HealingLineParser(pm);
      var clp = new CastLineParser(dm, pm);
      var mlp = new MiscLineParser(dm, pm);
      var plp = new PreLineParser(pm);
      var npc = new NpcDamageManager(dm, pm, dlp);
      var dsm = new DamageStatsManager(dm, pm);
      var tsm = new TankingStatsManager(dm, pm);
      var hsm = new HealingStatsManager(dm, pm);
      return new ParseContext(dm, pm, dlp, hlp, clp, mlp, plp, npc, dsm, tsm, hsm);
    }
  }
}
