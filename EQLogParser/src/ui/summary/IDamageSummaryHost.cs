using System;

namespace EQLogParser
{
  // Abstracts the globals that DamageSummary reaches into for cross-window coordination:
  //   - DataManager.EventsClearedActiveData  (clear-on-new-log)
  //   - MainActions.EventsChartOpened         (DPS Trends chart wiring)
  //   - MainActions.EventsDamageSummaryOptionsChanged  (global DS/bane toggle rebuild)
  //   - MainActions.CopyToEqClick             ("Copy Parse to EQ" menu command)
  //   - MainActions.FireDamageSelectionChanged  (shares selected stats with ADPS/spell windows)
  //
  // The default MainActionsHost forwards everything to the live singletons so the main DPS
  // Summary tab behaves identically to before. Embedded hosts (e.g. RaidDamageHost) can
  // redirect subscriptions onto an isolated DataManager and no-op the actions that would
  // otherwise mutate the main parse (copy-to-EQ, selection-change broadcasts).
  internal interface IDamageSummaryHost
  {
    event Action<bool> EventsClearedActiveData;
    event Action<string> EventsChartOpened;
    event Action<string> EventsDamageSummaryOptionsChanged;
    void FireDamageSelectionChanged(PlayerStatsSelectionChangedEventArgs args);
    void CopyToEqClick(string label);
  }

  // Default host: wires the live singletons. Used by the main DPS Summary tab via the
  // parameterless DamageSummary ctor.
  internal sealed class MainActionsHost : IDamageSummaryHost
  {
    public event Action<bool> EventsClearedActiveData
    {
      add => FightManager.Instance.EventsClearedActiveData += value;
      remove => FightManager.Instance.EventsClearedActiveData -= value;
    }

    public event Action<string> EventsChartOpened
    {
      add => MainActions.EventsChartOpened += value;
      remove => MainActions.EventsChartOpened -= value;
    }

    public event Action<string> EventsDamageSummaryOptionsChanged
    {
      add => MainActions.EventsDamageSummaryOptionsChanged += value;
      remove => MainActions.EventsDamageSummaryOptionsChanged -= value;
    }

    public void FireDamageSelectionChanged(PlayerStatsSelectionChangedEventArgs args)
      => MainActions.FireDamageSelectionChanged(args);

    public void CopyToEqClick(string label) => MainActions.CopyToEqClick(label);
  }

  // Host for a DamageSummary embedded inside the Raid Damage window. Routes the
  // clear-active-data event onto the isolated DataManager (so loading a new main log doesn't
  // wipe the raid-damage view) and forwards the global options-changed event so DS/bane
  // filter toggles rebuild both views. The chart-opened event is intentionally no-op — raid
  // damage is not wired into DPS Trends — and the copy/selection-change actions target the
  // main parse, which the embedded view is not.
  internal sealed class RaidDamageHost : IDamageSummaryHost
  {
    private readonly FightManager _fightManager;

    public RaidDamageHost(FightManager fightManager)
    {
      _fightManager = fightManager;
    }

    public event Action<bool> EventsClearedActiveData
    {
      add => _fightManager.EventsClearedActiveData += value;
      remove => _fightManager.EventsClearedActiveData -= value;
    }

    public event Action<string> EventsChartOpened { add { } remove { } }

    public event Action<string> EventsDamageSummaryOptionsChanged
    {
      add => MainActions.EventsDamageSummaryOptionsChanged += value;
      remove => MainActions.EventsDamageSummaryOptionsChanged -= value;
    }

    public void FireDamageSelectionChanged(PlayerStatsSelectionChangedEventArgs args) { }

    public void CopyToEqClick(string label) { }
  }
}
