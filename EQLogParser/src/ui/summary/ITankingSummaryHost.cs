using System;

namespace EQLogParser
{
  // Abstracts the globals that TankingSummary reaches into for cross-window coordination.
  // Mirror of IDamageSummaryHost for the tanking view — see that file for the overall pattern.
  //
  // Tanking-specific couplings vs damage:
  //   - MainActions.EventsTankingSelectionChanged (replaces DamageSelectionChanged)
  //   - MainActions.FireTankingSelectionChanged
  //   - HealingStatsManager.Instance is still referenced directly from TankingSummary (not
  //     via this host) because tanking overlays received-healing numbers onto each tank's
  //     stats; embedding an isolated HealingStatsManager is deferred to the Healing phase.
  //     In the raid-damage view this means the tanking tab won't show received-healing data
  //     until HealingStatsManager gets the same DI treatment.
  internal interface ITankingSummaryHost
  {
    event Action<bool> EventsClearedActiveData;
    event Action<string> EventsChartOpened;
    event Action<PlayerStatsSelectionChangedEventArgs> EventsTankingSelectionChanged;
    void FireTankingSelectionChanged(PlayerStatsSelectionChangedEventArgs args);
    void CopyToEqClick(string label);
  }

  // Default host for the main Tanking Summary tab — wires the live singletons.
  internal sealed class MainActionsTankingHost : ITankingSummaryHost
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

    public event Action<PlayerStatsSelectionChangedEventArgs> EventsTankingSelectionChanged
    {
      add => MainActions.EventsTankingSelectionChanged += value;
      remove => MainActions.EventsTankingSelectionChanged -= value;
    }

    public void FireTankingSelectionChanged(PlayerStatsSelectionChangedEventArgs args)
      => MainActions.FireTankingSelectionChanged(args);

    public void CopyToEqClick(string label) => MainActions.CopyToEqClick(label);
  }

  // Host for a TankingSummary embedded inside the Raid Damage window. Routes clear-active-data
  // onto the isolated DataManager so loading a new main log doesn't wipe the raid-damage view,
  // and no-ops the chart/selection/copy paths that only make sense for the "current parse".
  internal sealed class RaidDamageTankingHost : ITankingSummaryHost
  {
    private readonly FightManager _fightManager;

    public RaidDamageTankingHost(FightManager fightManager)
    {
      _fightManager = fightManager;
    }

    public event Action<bool> EventsClearedActiveData
    {
      add => _fightManager.EventsClearedActiveData += value;
      remove => _fightManager.EventsClearedActiveData -= value;
    }

    public event Action<string> EventsChartOpened { add { } remove { } }

    public event Action<PlayerStatsSelectionChangedEventArgs> EventsTankingSelectionChanged { add { } remove { } }

    public void FireTankingSelectionChanged(PlayerStatsSelectionChangedEventArgs args) { }

    public void CopyToEqClick(string label) { }
  }
}
