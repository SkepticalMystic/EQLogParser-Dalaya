using Syncfusion.UI.Xaml.Grid;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;

namespace EQLogParser
{
  // Trigger spell picker (project-trigger-spell-picker Phases 1-5). Lets the user
  // browse player-castable spells and either insert one of the spell's log messages
  // into the trigger Pattern field (insert mode) or fully configure the current
  // trigger as a buff/HoT/DoT duration tracker (bundle mode). Launched from
  // PatternEditor's "Spell..." button. Filters mirror the Spell Browser (P5).
  public partial class SpellPickerDialog
  {
    private const string AllClasses    = "All Classes";
    private const string AllCategories = "All";
    private const string AllTypes      = "All";
    private const string AnyEffect     = "Any Effect";
    private const string AnySlot       = "Any Slot";
    private const string NoOverlay     = "No Timer Overlay";

    // Index-aligned with the messageField combo and GetFieldText below.
    private static readonly string[] MessageFieldLabels = ["Lands on You", "Lands on Other", "Wear Off"];

    // Insert mode: the chosen message text, valid when IsOkClicked && BundleResult == null.
    public string SelectedText { get; private set; }
    public bool IsOkClicked { get; private set; }

    // Bundle mode (P4/P5): all fields needed to configure the current trigger.
    // Non-null only when the user clicked Generate in bundle mode.
    public record TriggerBundle(
      string Pattern,
      bool UseRegex,
      string PreviousPattern,
      int TimerType,
      double DurationSeconds,
      string EndEarlyPattern,
      string EndEarlyPattern2,
      string AltTimerName,
      string OverlayId
    );

    public TriggerBundle BundleResult { get; private set; }

    // Category regex mode (P5): only Pattern is returned; caller sets UseRegex = true.
    public record CategoryRegex(string Pattern);
    public CategoryRegex CategoryRegexResult { get; private set; }

    private readonly List<SpellData> _allSpells;
    private readonly bool _enableBundleMode;
    private bool _loaded;
    private bool _resetting;

    private sealed record SpaOption(int Spa)
    {
      public override string ToString() => $"{Spa} — {SpellDecoder.GetSpaName(Spa)}";
    }

    private sealed record OverlayOption(string Id, string Name)
    {
      public override string ToString() => Name;
    }

    public SpellPickerDialog(bool enableBundleMode = false)
    {
      ThemeConfig.SetCurrentTheme(this);
      InitializeComponent();
      Owner = MainActions.GetOwner();

      _enableBundleMode = enableBundleMode;
      _allSpells = [.. EQDataStore.Instance.GetSpellsForPicker()];

      foreach (var label in MessageFieldLabels)
      {
        messageField.Items.Add(label);
      }
      messageField.SelectedIndex = 0;

      classFilter.Items.Add(AllClasses);
      foreach (var className in EQDataStore.Instance.GetClassList())
      {
        classFilter.Items.Add(className);
      }
      SelectDefaultClass();

      categoryFilter.Items.Add(AllCategories);
      foreach (var category in EQDataStore.Instance.GetAllCategories())
      {
        categoryFilter.Items.Add(category);
      }
      categoryFilter.SelectedIndex = 0;

      typeFilter.Items.Add(AllTypes);
      typeFilter.Items.Add("Beneficial");
      typeFilter.Items.Add("Detrimental");
      typeFilter.SelectedIndex = 0;

      hasSpaFilter.Items.Add(AnyEffect);
      foreach (var spa in EQDataStore.Instance.GetUsedSpas())
      {
        hasSpaFilter.Items.Add(new SpaOption(spa));
      }
      hasSpaFilter.SelectedIndex = 0;

      inSlotFilter.Items.Add(AnySlot);
      for (var i = 1; i <= 12; i++)
      {
        inSlotFilter.Items.Add($"Slot {i}");
      }
      inSlotFilter.SelectedIndex = 0;
    }

    private void SelectDefaultClass()
    {
      var playerClass = PlayerRegistry.Instance.GetDefaultPlayerClass(ConfigUtil.PlayerName);
      classFilter.SelectedItem = !string.IsNullOrEmpty(playerClass) && classFilter.Items.Contains(playerClass)
        ? playerClass
        : AllClasses;
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
      _loaded = true;

      if (_enableBundleMode)
      {
        modeToggleRow.Visibility = Visibility.Visible;
      }

      overlayPicker.Items.Add(NoOverlay);
      foreach (var ov in (await TriggerStateDB.Instance.GetAllOverlays())
                           .Where(o => o.OverlayData.IsTimerOverlay))
      {
        overlayPicker.Items.Add(new OverlayOption(ov.Id, ov.Name));
      }
      overlayPicker.SelectedIndex = 0;

      ApplyFilter();
      searchBox.Focus();
    }

    private void FilterChanged(object sender, RoutedEventArgs e)
    {
      if (_loaded && !_resetting)
      {
        ApplyFilter();
      }
    }

    private void ModeChanged(object sender, RoutedEventArgs e)
    {
      if (!_loaded) return;

      var isBundle   = bundleModeRadio.IsChecked == true;
      var isCatRegex = catRegexModeRadio.IsChecked == true;
      var isInsert   = !isBundle && !isCatRegex;

      casterRow.Visibility      = isBundle   ? Visibility.Visible   : Visibility.Collapsed;
      catRegexRow.Visibility    = isCatRegex ? Visibility.Visible   : Visibility.Collapsed;
      insertModeLabel.Visibility = isInsert  ? Visibility.Visible   : Visibility.Collapsed;
      messageField.Visibility   = isInsert   ? Visibility.Visible   : Visibility.Collapsed;
      insertButton.Content      = isInsert   ? "Insert" : "Generate";

      if (isCatRegex)
        UpdateCatRegexHint();
      else
        hintText.Text = string.Empty;
    }

    private void CatTypeChanged(object sender, RoutedEventArgs e)
    {
      if (!_loaded) return;
      var isCast = castRadio.IsChecked == true;
      catCasterLabel.Visibility = isCast ? Visibility.Visible : Visibility.Collapsed;
      catCasterBox.Visibility   = isCast ? Visibility.Visible : Visibility.Collapsed;
      catCasterHint.Visibility  = isCast ? Visibility.Visible : Visibility.Collapsed;
      UpdateCatRegexHint();
    }

    private void UpdateCatRegexHint()
    {
      var spells = dataGrid.ItemsSource as List<SpellData>;
      var count  = spells?.Count ?? 0;
      if (count == 0)
      {
        hintText.Text = "No spells match the current filters.";
        return;
      }
      if (landingRadio.IsChecked == true)
      {
        var withLanding = spells.Count(s => !string.IsNullOrWhiteSpace(s.LandsOnOther));
        hintText.Text = withLanding == 0
          ? "No landing messages found for the filtered spells."
          : $"{withLanding} spell{(withLanding == 1 ? "" : "s")} with landing text (of {count} matching).";
      }
      else
      {
        hintText.Text = $"{count} spell{(count == 1 ? "" : "s")} will be included.";
      }
    }

    private void ResetClick(object sender, RoutedEventArgs e)
    {
      _resetting = true;
      searchBox.Clear();
      levelMin.Clear();
      levelMax.Clear();
      SelectDefaultClass();
      categoryFilter.SelectedIndex = 0;
      typeFilter.SelectedIndex     = 0;
      hasSpaFilter.SelectedIndex   = 0;
      inSlotFilter.SelectedIndex   = 0;
      _resetting = false;
      ApplyFilter();
    }

    private void ApplyFilter()
    {
      var className = classFilter.SelectedItem as string;
      var classFlag = (string.IsNullOrEmpty(className) || className == AllClasses)
        ? (SpellClass?)null
        : EQDataStore.Instance.GetSpellClassByName(className);

      var category = categoryFilter.SelectedItem as string;
      if (category == AllCategories) category = null;

      bool? beneficial = (typeFilter.SelectedItem as string) switch
      {
        "Beneficial"  => true,
        "Detrimental" => false,
        _             => null
      };

      var hasMin = int.TryParse(levelMin.Text, out var minLevel);
      var hasMax = int.TryParse(levelMax.Text, out var maxLevel);

      var spaOption = hasSpaFilter.SelectedItem as SpaOption;
      var slotStr   = inSlotFilter.SelectedItem as string;
      var slotIdx   = slotStr != null && slotStr != AnySlot
        ? int.Parse(slotStr.Split(' ')[1]) - 1
        : -1;

      var query = SpellPickerFilter.Apply(
        _allSpells, searchBox.Text, classFlag, category,
        beneficial,
        hasMin ? minLevel : null,
        hasMax ? maxLevel : null).AsEnumerable();

      if (spaOption != null || slotIdx >= 0)
      {
        query = query.Where(s =>
        {
          var effects = EQDataStore.Instance.GetSpellEffects(s.Id);
          if (effects?.Slots == null) return false;
          if (spaOption != null && slotIdx >= 0)
            return effects.Slots.Any(e => e.Spa == spaOption.Spa && e.Slot == slotIdx);
          if (spaOption != null)
            return effects.Slots.Any(e => e.Spa == spaOption.Spa);
          return effects.Slots.Any(e => e.Slot == slotIdx);
        });
      }

      dataGrid.ItemsSource = query.ToList();
      if (catRegexModeRadio?.IsChecked == true)
        UpdateCatRegexHint();
      else
        hintText.Text = string.Empty;
    }

    private void SpellSelectionChanged(object sender, GridSelectionChangedEventArgs e)
    {
      if (dataGrid.SelectedItem is SpellData spell)
      {
        for (var i = 0; i < MessageFieldLabels.Length; i++)
        {
          if (!string.IsNullOrEmpty(GetFieldText(spell, i)))
          {
            messageField.SelectedIndex = i;
            break;
          }
        }
      }
    }

    private void SpellCellDoubleTapped(object sender, GridCellDoubleTappedEventArgs e) => TryInsert();

    private void InsertClick(object sender, RoutedEventArgs e) => TryInsert();

    private void CancelClick(object sender, RoutedEventArgs e)
    {
      IsOkClicked = false;
      Close();
    }

    private void TryInsert()
    {
      if (catRegexModeRadio.IsChecked == true)
      {
        TryCategoryRegex();
        return;
      }
      if (bundleModeRadio.IsChecked == true)
      {
        TryGenerate();
        return;
      }

      if (dataGrid.SelectedItem is not SpellData spell)
      {
        hintText.Text = "Select a spell first.";
        return;
      }

      var text = GetFieldText(spell, messageField.SelectedIndex);
      if (string.IsNullOrEmpty(text))
      {
        hintText.Text = "That spell has no text for the selected message.";
        return;
      }

      SelectedText = text;
      IsOkClicked = true;
      Close();
    }

    private void TryCategoryRegex()
    {
      var spells = dataGrid.ItemsSource as List<SpellData>;
      if (spells == null || spells.Count == 0)
      {
        hintText.Text = "No spells match the current filters.";
        return;
      }

      string pattern;
      if (castRadio.IsChecked == true)
      {
        var casterName = catCasterBox.Text.Trim();
        var alt = string.Join("|", spells.Select(s => Regex.Escape(s.Name)).Distinct().OrderBy(x => x));
        pattern = string.IsNullOrEmpty(casterName)
          ? $@"{{S1}} begins casting ({alt})\."
          : $@"{Regex.Escape(casterName)} begins casting ({alt})\.";
      }
      else
      {
        var texts = spells
          .Select(s => s.LandsOnOther?.Trim())
          .Where(t => !string.IsNullOrEmpty(t))
          .Select(Regex.Escape)
          .Distinct()
          .OrderBy(x => x)
          .ToList();
        if (texts.Count == 0)
        {
          hintText.Text = "No landing messages found for the filtered spells.";
          return;
        }
        pattern = $"({string.Join("|", texts)})";
      }

      CategoryRegexResult = new CategoryRegex(pattern);
      IsOkClicked = true;
      Close();
    }

    private void TryGenerate()
    {
      if (dataGrid.SelectedItem is not SpellData spell)
      {
        hintText.Text = "Select a spell first.";
        return;
      }

      var casterName = casterBox.Text.Trim();
      var hasName    = !string.IsNullOrEmpty(casterName);

      var castingLine = hasName
        ? $"{casterName} begins casting {spell.Name}."
        : $"begins casting {spell.Name}.";

      var landsOnOther = spell.LandsOnOther?.Trim();
      var hasLanding   = !string.IsNullOrEmpty(landsOnOther);

      string pattern, previousPattern, endEarlyPattern, endEarlyPattern2, altTimerName;
      bool useRegex;

      if (hasLanding)
      {
        // Buff/HoT/DoT tracker: fires when spell lands on target.
        // Prepend {S1} to capture the target name.
        // WearOff is first-person ("Your heartbeat steadies.") and only appears
        // in the parser's own log — not in the log when it wears off a target.
        // Leave EndEarlyPattern empty; the timer handles normal expiry.
        var prefix   = landsOnOther.StartsWith('\'') ? "{S1}" : "{S1} ";
        pattern          = prefix + landsOnOther;
        useRegex         = true;
        previousPattern  = hasName ? castingLine : string.Empty;
        endEarlyPattern  = string.Empty;
        endEarlyPattern2 = string.Empty;
        altTimerName     = $"{spell.Name} - {{S1}}";
      }
      else
      {
        // Fallback: no landing message — use casting line as Pattern.
        pattern          = castingLine;
        useRegex         = false;
        previousPattern  = string.Empty;
        endEarlyPattern  = hasName
          ? $"{casterName}'s {spell.Name} spell is interrupted."
          : string.Empty;
        endEarlyPattern2 = string.Empty;
        altTimerName     = spell.Name;
      }

      // spell.Duration is already in seconds (loaded as raw ticks × 6 from spells.txt).
      var durationSeconds = (double)spell.Duration;
      var overlayId = overlayPicker.SelectedItem is OverlayOption op ? op.Id : null;

      if (durationSeconds == 0)
      {
        hintText.Text = "Warning: spell has no duration — timer will show 0 seconds.";
      }

      BundleResult = new TriggerBundle(
        Pattern:          pattern,
        UseRegex:         useRegex,
        PreviousPattern:  previousPattern,
        TimerType:        1,
        DurationSeconds:  durationSeconds,
        EndEarlyPattern:  endEarlyPattern,
        EndEarlyPattern2: endEarlyPattern2,
        AltTimerName:     altTimerName,
        OverlayId:        overlayId
      );
      IsOkClicked = true;
      Close();
    }

    private static string GetFieldText(SpellData spell, int fieldIndex) => fieldIndex switch
    {
      0 => spell.LandsOnYou,
      1 => spell.LandsOnOther,
      2 => spell.WearOff,
      _ => null
    };
  }
}
