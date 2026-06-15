using Syncfusion.UI.Xaml.Grid;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace EQLogParser
{
  // Trigger spell picker (project-trigger-spell-picker Phases 1-4). Lets the user
  // browse player-castable spells, optionally narrow by class (P2) and category
  // (P3), and either insert one of the spell's log messages into the trigger Pattern
  // field (insert mode) or fully configure the current trigger as a heal-coordination
  // cast timer (bundle mode, P4). Launched from PatternEditor's "Spell..." button.
  public partial class SpellPickerDialog
  {
    private const string AllClasses = "All Classes";
    private const string AllCategories = "All";
    private const string HealsCategory = "Heals";

    // Index-aligned with the messageField combo and GetFieldText below.
    private static readonly string[] MessageFieldLabels = ["Lands on You", "Lands on Other", "Wear Off"];

    // Insert mode: the chosen message text, valid when IsOkClicked && BundleResult == null.
    public string SelectedText { get; private set; }
    public bool IsOkClicked { get; private set; }

    // Bundle mode (P4): all fields needed to configure the current trigger as a
    // cast timer. Non-null only when the user clicked Generate in bundle mode.
    public record TriggerBundle(
      string Pattern,
      bool EnableTimer,
      double DurationSeconds,
      string EndEarlyPattern,
      string EndEarlyPattern2
    );

    public TriggerBundle BundleResult { get; private set; }

    private readonly List<SpellData> _allSpells;
    private readonly bool _enableBundleMode;
    private bool _loaded;

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
    }

    private void SelectDefaultClass()
    {
      var playerClass = PlayerRegistry.Instance.GetDefaultPlayerClass(ConfigUtil.PlayerName);
      classFilter.SelectedItem = !string.IsNullOrEmpty(playerClass) && classFilter.Items.Contains(playerClass)
        ? playerClass
        : AllClasses;
    }

    private void WindowLoaded(object sender, RoutedEventArgs e)
    {
      _loaded = true;

      if (_enableBundleMode)
      {
        modeToggleRow.Visibility = Visibility.Visible;
      }

      ApplyFilter();
      searchBox.Focus();
    }

    private void FilterChanged(object sender, RoutedEventArgs e)
    {
      if (_loaded)
      {
        ApplyFilter();
      }
    }

    // P4: called when the user switches between Insert and Generate modes.
    private void ModeChanged(object sender, RoutedEventArgs e)
    {
      if (!_loaded) return;

      var isBundle = bundleModeRadio.IsChecked == true;
      casterRow.Visibility = isBundle ? Visibility.Visible : Visibility.Collapsed;
      insertModeLabel.Visibility = isBundle ? Visibility.Collapsed : Visibility.Visible;
      messageField.Visibility = isBundle ? Visibility.Collapsed : Visibility.Visible;
      insertButton.Content = isBundle ? "Generate" : "Insert";
      hintText.Text = string.Empty;

      // Default to Heals when entering bundle mode — the most common use case.
      if (isBundle && categoryFilter.Items.Contains(HealsCategory))
      {
        categoryFilter.SelectedItem = HealsCategory;
      }
    }

    private void ApplyFilter()
    {
      var className = classFilter.SelectedItem as string;
      var classFlag = string.IsNullOrEmpty(className) || className == AllClasses
        ? (SpellClass?)null
        : EQDataStore.Instance.GetSpellClassByName(className);

      var category = categoryFilter.SelectedItem as string;
      if (category == AllCategories)
      {
        category = null;
      }

      dataGrid.ItemsSource = SpellPickerFilter.Apply(_allSpells, searchBox.Text, classFlag, category).ToList();
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

    // P4: build a TriggerBundle from the selected spell and caster name.
    // Pattern: "{caster} begins casting {spell}." (or "begins casting {spell}." for any caster).
    // EndEarlyPattern: spell's LandsOnOther text (heal confirmed — dismiss the timer).
    // EndEarlyPattern2: "{caster}'s {spell} spell is interrupted." (empty when no caster name).
    private void TryGenerate()
    {
      if (dataGrid.SelectedItem is not SpellData spell)
      {
        hintText.Text = "Select a spell first.";
        return;
      }

      var casterName = casterBox.Text.Trim();
      var hasName = !string.IsNullOrEmpty(casterName);

      var pattern = hasName
        ? $"{casterName} begins casting {spell.Name}."
        : $"begins casting {spell.Name}.";

      if (spell.CastingTimeMs == 0)
      {
        hintText.Text = "Warning: spell has no cast time — timer duration set to 0.";
      }
      var duration = spell.CastingTimeMs / 1000.0;

      // EndEarlyPattern2 requires the possessive caster name; leave empty when
      // no name is provided because the EQ log always includes "Name's SpellName
      // spell is interrupted." — there's no generic form without the name.
      var endEarlyPattern2 = hasName
        ? $"{casterName}'s {spell.Name} spell is interrupted."
        : string.Empty;

      BundleResult = new TriggerBundle(
        Pattern: pattern,
        EnableTimer: true,
        DurationSeconds: duration,
        EndEarlyPattern: spell.LandsOnOther ?? string.Empty,
        EndEarlyPattern2: endEarlyPattern2
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
