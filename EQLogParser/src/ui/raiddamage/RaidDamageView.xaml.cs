using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace EQLogParser
{
  public partial class RaidDamageView : UserControl, IDocumentContent
  {
    private readonly ObservableCollection<RaidDamageSource> _sources = [];
    private readonly ObservableCollection<MergedFightRow> _fights = [];

    // Dedicated isolated context used to run BuildTotalStats for rendering merged-fight DPS.
    // Kept separate from per-source parse contexts so stats computation state doesn't get tangled.
    private readonly ParseContext _statsContext = ParseContext.CreateIsolated();

    // Embedded summary controls bound to the isolated managers. Each owns its own event
    // subscriptions (via ContentLoaded) and renders the full tree grid, context menu, pet
    // rollups, column chooser, etc. — identical to the main DPS / Tanking tabs.
    private readonly DamageSummary _damageSummary;
    private readonly TankingSummary _tankingSummary;

    public RaidDamageView()
    {
      InitializeComponent();
      sourcesGrid.ItemsSource = _sources;
      fightsGrid.ItemsSource = _fights;

      _damageSummary = new DamageSummary(
        _statsContext.DamageStatsBuilder,
        new RaidDamageHost(_statsContext.FightManager),
        "RaidDamageSummaryColumns");
      summaryHost.Content = _damageSummary;

      _tankingSummary = new TankingSummary(
        _statsContext.TankingStatsBuilder,
        _statsContext.HealingStatsBuilder,
        new RaidDamageTankingHost(_statsContext.FightManager),
        "RaidDamageTankingSummaryColumns");
      tankingHost.Content = _tankingSummary;

      UpdateFooterStatus();
      RebuildMergedFights();
    }

    // The DockingManager calls this when the tab is closed. Forwarding to each embedded
    // summary tears down its event subscriptions (EventsGenerationStatus,
    // EventsClearedActiveData via the isolated DataManager, host-routed MainActions events).
    public void HideContent()
    {
      _damageSummary?.HideContent();
      _tankingSummary?.HideContent();
    }

    // Tab selection itself doesn't trigger a rebuild — both managers are populated in
    // parallel on every fight-selection change so switching tabs is instant.
    private void RaidViewTabChanged(object sender, SelectionChangedEventArgs e) { }

    private async void AddSourceClick(object sender, RoutedEventArgs e)
    {
      var dialog = new OpenFileDialog
      {
        Filter = "EQ Log files (eqlog_*.txt)|eqlog_*.txt|Text files (*.txt)|*.txt|All files (*.*)|*.*",
        Title = "Add Log Source"
      };

      if (dialog.ShowDialog() != true)
      {
        return;
      }

      var filePath = dialog.FileName;
      var sourcePlayer = FightMerger.TryParsePlayerNameFromLogFile(filePath);
      if (string.IsNullOrEmpty(sourcePlayer))
      {
        new MessageWindow(
          $"Could not extract a source player name from the filename.\n\nExpected format: eqlog_PlayerName_server.txt\n\nFile: {Path.GetFileName(filePath)}",
          "Raid Damage").ShowDialog();
        return;
      }

      // Block the exact same file path under any source — that's always a double-load.
      if (_sources.Any(s => s.FilePaths.Any(p => string.Equals(p, filePath, StringComparison.OrdinalIgnoreCase))))
      {
        new MessageWindow($"This file is already loaded:\n{Path.GetFileName(filePath)}", "Raid Damage").ShowDialog();
        return;
      }

      // If another file for this same player is already loaded, append to that existing source
      // so its isolated Context sees the union of fights. Users splitting one player's raid
      // night across multiple per-fight clips need this.
      var existing = _sources.FirstOrDefault(s => string.Equals(s.SourcePlayer, sourcePlayer, StringComparison.Ordinal));
      if (existing != null)
      {
        await AppendFileToSource(existing, filePath);
        return;
      }

      var source = new RaidDamageSource
      {
        FilePath = filePath,
        SourcePlayer = sourcePlayer,
        IsSelected = true,
        Context = ParseContext.CreateIsolated()
      };
      source.FilePaths.Add(filePath);
      source.StatusText = "(parsing...)";

      // Tell this isolated PlayerRegistry the source's name so the parser resolves "you/your"
      // to the right player. Without this, every source resolves "your" to the live user
      // (ConfigUtil.PlayerName), which mis-attributes self-cast damage and breaks dedup —
      // identical hits land under different attackers across sources and don't merge.
      source.Context.PlayerRegistry.PlayerName = sourcePlayer;

      source.Context.FightManager.EventsNewFight += fight => source.AddFight(fight);

      _sources.Add(source);
      UpdateFooterStatus();

      try
      {
        await Task.Run(() => ParseFile(source, filePath));
        source.StatusText = FormatSourceStatus(source);
      }
      catch (Exception ex)
      {
        source.StatusText = "(parse failed)";
        new MessageWindow($"Failed to parse {Path.GetFileName(filePath)}:\n{ex.Message}", "Raid Damage").ShowDialog();
      }

      // Auto-detect offsets once we have ≥2 sources. Runs on every add so a newly-loaded log
      // gets aligned against the existing anchor without the user having to think about it.
      // Sources the user has manually overridden could in theory be preserved, but since
      // re-detection picks the largest source as anchor, an explicit Detect click after an
      // override is always available.
      if (_sources.Count(s => s.Fights.Count > 0) >= 2)
      {
        ApplyDetectedOffsets();
      }

      RebuildMergedFights();
      UpdateFooterStatus();
    }

    private void ApplyDetectedOffsets()
    {
      var ready = _sources.Where(s => s.Fights.Count > 0).ToList();
      if (ready.Count < 2)
      {
        return;
      }

      var detected = FightOffsetDetector.DetectAll(
        ready.Select(s => (s.SourcePlayer, (IList<Fight>)s.Fights)));

      foreach (var s in ready)
      {
        if (detected.TryGetValue(s.SourcePlayer, out var offset))
        {
          s.TimeOffsetSeconds = offset;
        }
      }
    }

    private void DetectOffsetsClick(object sender, RoutedEventArgs e)
    {
      ApplyDetectedOffsets();
      RebuildMergedFights();
    }

    private void SetOffsetMenuClick(object sender, RoutedEventArgs e)
    {
      var source = ResolveSourceFromMenu(sender);
      if (source == null)
      {
        return;
      }

      var dialog = new RaidOffsetDialog(source.SourcePlayer, source.TimeOffsetSeconds);
      dialog.ShowDialog();
      if (!dialog.IsOkClicked)
      {
        return;
      }

      source.TimeOffsetSeconds = dialog.OffsetSeconds;
      RebuildMergedFights();
    }

    private void ResetOffsetMenuClick(object sender, RoutedEventArgs e)
    {
      var source = ResolveSourceFromMenu(sender);
      if (source == null)
      {
        return;
      }
      source.TimeOffsetSeconds = 0;
      RebuildMergedFights();
    }

    // Context menu items live inside the row's cell template, so DataContext walks up to the
    // RaidDamageSource for the row the user right-clicked.
    private static RaidDamageSource ResolveSourceFromMenu(object sender)
    {
      if (sender is FrameworkElement fe && fe.DataContext is RaidDamageSource rds)
      {
        return rds;
      }
      return null;
    }

    private static void ParseFile(RaidDamageSource source, string filePath)
    {
      var processor = new LogProcessor(filePath, source.Context);
      using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete);
      using var reader = new StreamReader(fs);
      string line;
      while ((line = reader.ReadLine()) != null)
      {
        if (line.Length < 28)
        {
          continue;
        }
        var dt = DateUtil.ParseStandardDate(line);
        if (dt == DateTime.MinValue)
        {
          continue;
        }
        processor.ProcessSync(line, DateUtil.ToDotNetSeconds(dt));
      }
    }

    // Parse an additional log file into an existing source's Context so the merger sees one
    // logical observer regardless of how many separate clips that player provided. Caller is
    // responsible for ensuring filePath is not already on any source.
    private async Task AppendFileToSource(RaidDamageSource source, string filePath)
    {
      source.FilePaths.Add(filePath);
      source.StatusText = $"(parsing {Path.GetFileName(filePath)}...)";
      UpdateFooterStatus();

      try
      {
        await Task.Run(() => ParseFile(source, filePath));
        source.StatusText = FormatSourceStatus(source);
      }
      catch (Exception ex)
      {
        source.StatusText = "(append failed)";
        new MessageWindow($"Failed to parse {Path.GetFileName(filePath)}:\n{ex.Message}", "Raid Damage").ShowDialog();
        return;
      }

      // Newly-merged fights can shift offset detection — re-run the alignment pass if there
      // are ≥2 sources, same as the first-load path.
      if (_sources.Count(s => s.Fights.Count > 0) >= 2)
      {
        ApplyDetectedOffsets();
      }

      RebuildMergedFights();
      UpdateFooterStatus();
    }

    private static string FormatSourceStatus(RaidDamageSource source)
    {
      var fightAndDmg = $"{source.FightCount} fights, {FormatDamage(source.TotalDamage)}";
      return source.FilePaths.Count > 1
        ? $"({source.FilePaths.Count} files: {fightAndDmg})"
        : $"({fightAndDmg})";
    }

    private void RemoveSourceClick(object sender, RoutedEventArgs e)
    {
      var selected = sourcesGrid.SelectedItems?.Cast<RaidDamageSource>().ToList();
      if (selected == null || selected.Count == 0)
      {
        return;
      }

      foreach (var source in selected)
      {
        _sources.Remove(source);
      }

      RebuildMergedFights();
      UpdateFooterStatus();
    }

    private void SourcesSelectionChanged(object sender, Syncfusion.UI.Xaml.Grid.GridSelectionChangedEventArgs e)
    {
      removeSourceButton.IsEnabled = sourcesGrid.SelectedItems is { Count: > 0 };
    }

    private void SourceCheckboxClick(object sender, RoutedEventArgs e)
    {
      Dispatcher.BeginInvoke(() =>
      {
        RebuildMergedFights();
        UpdateFooterStatus();
      });
    }

    private void FightsSelectionChanged(object sender, Syncfusion.UI.Xaml.Grid.GridSelectionChangedEventArgs e)
    {
      UpdatePerPlayerSummary();
    }

    // Runs the merger and repopulates the middle fights grid. Also refreshes the right pane
    // (with no fights selected, the summary treats "all fights" as the scope).
    private void RebuildMergedFights()
    {
      _fights.Clear();

      var selectedSources = _sources.Where(s => s.IsSelected && s.Fights.Count > 0).ToList();
      if (selectedSources.Count == 0)
      {
        fightsTitle.Text = _sources.Count == 0
          ? "Merged Fights — No Sources Loaded"
          : "Merged Fights — No Sources Selected";
        UpdatePerPlayerSummary();
        return;
      }

      var fightSources = selectedSources
        .Select(s => new FightSource
        {
          SourcePlayer = s.SourcePlayer,
          Fights = s.Fights,
          TimeOffsetSeconds = s.TimeOffsetSeconds
        })
        .ToList();
      var merged = FightMerger.MergeFromSources(fightSources);

      // Match the Fight List's default (Tanking unchecked): only fights with at least one
      // player-damage hit. Otherwise pure-tanking encounters (NPCs hit you, you didn't hit
      // back) inflate the count and don't contribute to a DPS merge anyway.
      foreach (var f in merged.Where(f => f.DamageHits > 0).OrderByDescending(f => f.BeginTime))
      {
        _fights.Add(new MergedFightRow(f));
      }

      fightsTitle.Text = $"Merged Fights — {_fights.Count} ({selectedSources.Count} source{(selectedSources.Count == 1 ? "" : "s")})";
      UpdatePerPlayerSummary();
    }

    // Called whenever fight selection changes, or after a merger rebuild. If no fights are
    // selected, the pane stays blank (matching DPS Summary's "No NPCs Selected" default).
    private void UpdatePerPlayerSummary()
    {
      var selectedFights = fightsGrid.SelectedItems?.Cast<MergedFightRow>().Select(r => r.Fight).ToList();

      if (selectedFights is null or { Count: 0 })
      {
        // BuildTotalStats with no Npcs drives each embedded summary to the "No NPCs" state
        // via the NONPC event path. No need to touch the controls directly.
        Task.Run(() => _statsContext.DamageStatsBuilder.BuildTotalStats(new GenerateStatsOptions()));
        Task.Run(() => _statsContext.TankingStatsBuilder.BuildTotalStats(new GenerateStatsOptions()));
        mergeStatus.Text = "";
        return;
      }

      // Assign unique Ids so DamageStatsManager can rank/group fights. Merged Fight objects
      // start with Id=0 which causes hashing/equality collisions in the stats computation.
      var id = 1;
      foreach (var f in selectedFights) { f.Id = id++; }

      var options = new GenerateStatsOptions();
      options.Npcs.AddRange(selectedFights);
      options.AllRanges = new TimeRange();
      foreach (var f in selectedFights)
      {
        if (!double.IsNaN(f.BeginDamageTime) && !double.IsNaN(f.LastDamageTime))
        {
          options.AllRanges.Add(new TimeSegment(f.BeginDamageTime, f.LastDamageTime));
        }
      }

      // Re-seed from the live PlayerManager. _statsContext is constructed at app startup
      // (docking manager creates the view eagerly), so its PlayerManager snapshot misses any
      // pet→owner mappings that accumulated during the current live session. Without this,
      // DamageStatsManager.UpdatePetMapping fails to find owners and pets show as their own
      // top-level rows instead of rolling into "OwnerName +Pets" aggregates.
      _statsContext.PlayerRegistry.SeedFrom(PlayerRegistry.Instance);

      // Then fold in each contributing source's classification state. Each per-source parse
      // context inferred player classes (and verified players/pets) from its own log during
      // ParseFile, whereas the live Instance only knows what the user observed in their own
      // session — often empty when external raid logs are loaded without a live parse. Seeding
      // the sources after Instance lets each source's own observations win, so the embedded
      // summary's Class column populates the same way the main DPS/Tanking tabs do.
      foreach (var source in _sources.Where(s => s.IsSelected && s.Fights.Count > 0))
      {
        _statsContext.PlayerRegistry.SeedFrom(source.Context.PlayerRegistry);
      }

      // Run on background thread — BuildTotalStats does meaningful work and fires events
      // that route through each embedded summary's subscription. Tanking uses the same fight
      // list but reads fight.TankingBlocks / TankSegments internally, so the options are shared.
      Task.Run(() => _statsContext.DamageStatsBuilder.BuildTotalStats(options));
      Task.Run(() => _statsContext.TankingStatsBuilder.BuildTotalStats(options));

      mergeStatus.Text = $"{selectedFights.Count} fight{(selectedFights.Count == 1 ? "" : "s")} selected";
    }

    private void UpdateFooterStatus()
    {
      var selectedCount = _sources.Count(s => s.IsSelected);
      sourcesStatus.Text = _sources.Count == 0
        ? "No sources. Click Add to load an exported log."
        : $"{_sources.Count} source{(_sources.Count == 1 ? "" : "s")} • {selectedCount} selected";

      detectOffsetsButton.IsEnabled = _sources.Count(s => s.Fights.Count > 0) >= 2;
    }

    private static string FormatDamage(long damage)
    {
      if (damage >= 1_000_000_000) return $"{damage / 1_000_000_000.0:F2}B";
      if (damage >= 1_000_000) return $"{damage / 1_000_000.0:F2}M";
      if (damage >= 1_000) return $"{damage / 1_000.0:F1}K";
      return damage.ToString();
    }
  }

  internal class MergedFightRow
  {
    public Fight Fight { get; }
    public string BeginTimeString => Fight.BeginTimeString;
    public string Name => Fight.Name;
    public long Damage => Fight.DamageTotal;

    public string DamageString
    {
      get
      {
        var d = Damage;
        if (d >= 1_000_000_000) return $"{d / 1_000_000_000.0:F2}B";
        if (d >= 1_000_000) return $"{d / 1_000_000.0:F2}M";
        if (d >= 1_000) return $"{d / 1_000.0:F1}K";
        return d.ToString();
      }
    }

    public string DurationString
    {
      get
      {
        if (double.IsNaN(Fight.BeginDamageTime) || double.IsNaN(Fight.LastDamageTime)) return "";
        var s = (int)Math.Round(Fight.LastDamageTime - Fight.BeginDamageTime);
        if (s < 0) s = 0;
        if (s >= 60) return $"{s / 60}m {s % 60}s";
        return $"{s}s";
      }
    }

    public MergedFightRow(Fight fight)
    {
      Fight = fight;
    }
  }

  internal class RaidDamageSource : INotifyPropertyChanged
  {
    // The first log file added for this player. Additional files appended via AddSource land
    // in FilePaths but keep this as the primary path for display/back-compat.
    public string FilePath { get; set; }
    public string SourcePlayer { get; set; }
    public ParseContext Context { get; set; }
    public List<Fight> Fights { get; } = [];

    // All log files contributing to this source (>=1). Lets one player provide multiple
    // partial logs (e.g. per-fight clips) that get parsed into the same isolated Context so
    // the merger sees them as one logical observer.
    public List<string> FilePaths { get; } = [];

    private bool _isSelected;
    public bool IsSelected
    {
      get => _isSelected;
      set { _isSelected = value; OnPropertyChanged(); }
    }

    private string _statusText = "";
    public string StatusText
    {
      get => _statusText;
      set { _statusText = value; OnPropertyChanged(); }
    }

    // Subtracted from this source's timestamps to align with the merged frame. Set by
    // FightOffsetDetector after parsing or manually by the user via "Set offset..." menu.
    private double _timeOffsetSeconds;
    public double TimeOffsetSeconds
    {
      get => _timeOffsetSeconds;
      set
      {
        if (Math.Abs(_timeOffsetSeconds - value) < 0.5) return;
        _timeOffsetSeconds = value;
        OnPropertyChanged();
        OnPropertyChanged(nameof(OffsetDisplay));
      }
    }

    // Compact display for the source row. Empty for zero offset (the common case — anchor
    // source and any aligned-clock sources). Examples: "+1h", "-30m", "+1h 30m".
    public string OffsetDisplay
    {
      get
      {
        var seconds = (long)Math.Round(_timeOffsetSeconds);
        if (seconds == 0) return "";

        var sign = seconds < 0 ? "-" : "+";
        var abs = Math.Abs(seconds);
        var hours = abs / 3600;
        var minutes = abs % 3600 / 60;

        if (hours > 0 && minutes > 0) return $"{sign}{hours}h {minutes}m";
        if (hours > 0) return $"{sign}{hours}h";
        return $"{sign}{minutes}m";
      }
    }

    public int FightCount => Fights.Count;
    public long TotalDamage => Fights.Sum(f => f.DamageTotal);

    internal void AddFight(Fight fight)
    {
      Fights.Add(fight);
    }

    public event PropertyChangedEventHandler PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string name = null)
    {
      PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
  }

}
