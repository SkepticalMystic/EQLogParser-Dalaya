using Syncfusion.UI.Xaml.Grid;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace EQLogParser
{
  // Trigger spell picker (project-trigger-spell-picker Phases 1-3). Lets the user
  // browse player-castable spells, optionally narrow by class (P2) and category
  // (P3), and insert one of the spell's log messages (Lands on You / Lands on
  // Other / Wear Off) into the trigger Pattern field as raw text. Launched from
  // PatternEditor's "Insert Spell..." button. All filtering is delegated to the
  // pure SpellPickerFilter so it stays unit-tested away from this UI.
  public partial class SpellPickerDialog
  {
    private const string AllClasses = "All Classes";
    private const string AllCategories = "All";

    // Index-aligned with the messageField combo and GetFieldText below.
    private static readonly string[] MessageFieldLabels = ["Lands on You", "Lands on Other", "Wear Off"];

    // The chosen message text, valid only when IsOkClicked is true.
    public string SelectedText { get; private set; }
    public bool IsOkClicked { get; private set; }

    private readonly List<SpellData> _allSpells;
    private bool _loaded;

    public SpellPickerDialog()
    {
      ThemeConfig.SetCurrentTheme(this);
      InitializeComponent();
      Owner = MainActions.GetOwner();

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
      // Default to the player's own class when it's known, so the most common
      // case (build a trigger for a spell I can cast) needs no extra clicks.
      var playerClass = PlayerRegistry.Instance.GetDefaultPlayerClass(ConfigUtil.PlayerName);
      classFilter.SelectedItem = !string.IsNullOrEmpty(playerClass) && classFilter.Items.Contains(playerClass)
        ? playerClass
        : AllClasses;
    }

    private void WindowLoaded(object sender, RoutedEventArgs e)
    {
      _loaded = true;
      ApplyFilter();
      searchBox.Focus();
    }

    private void FilterChanged(object sender, RoutedEventArgs e)
    {
      // Combo population during construction fires SelectionChanged before the
      // dialog is ready; ignore until WindowLoaded has run.
      if (_loaded)
      {
        ApplyFilter();
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
      // Auto-select the first non-empty message so a single Insert usually does
      // the right thing without the user touching the message dropdown.
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

    private static string GetFieldText(SpellData spell, int fieldIndex) => fieldIndex switch
    {
      0 => spell.LandsOnYou,
      1 => spell.LandsOnOther,
      2 => spell.WearOff,
      _ => null
    };
  }
}
