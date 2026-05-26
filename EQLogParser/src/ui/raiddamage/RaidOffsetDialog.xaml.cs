using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EQLogParser
{
  public partial class RaidOffsetDialog
  {
    public double OffsetSeconds { get; private set; }
    public bool IsOkClicked { get; private set; }

    public RaidOffsetDialog(string sourcePlayer, double initialOffsetSeconds)
    {
      ThemeConfig.SetCurrentTheme(this);
      InitializeComponent();
      Owner = MainActions.GetOwner();
      promptText.Text = $"Set time offset for {sourcePlayer}.";
      offsetTextBox.Text = (initialOffsetSeconds / 3600.0).ToString("0.##", CultureInfo.InvariantCulture);
    }

    private void WindowLoaded(object sender, RoutedEventArgs e)
    {
      offsetTextBox.Focus();
      offsetTextBox.SelectAll();
    }

    private void OffsetTextChanged(object sender, TextChangedEventArgs e)
    {
      errorText.Visibility = Visibility.Collapsed;
    }

    private void OffsetKeyDown(object sender, KeyEventArgs e)
    {
      if (e.Key == Key.Enter)
      {
        TryAccept();
      }
    }

    private void OkClick(object sender, RoutedEventArgs e)
    {
      TryAccept();
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
      IsOkClicked = false;
      Close();
    }

    private void TryAccept()
    {
      var text = offsetTextBox.Text?.Trim();
      if (string.IsNullOrEmpty(text))
      {
        ShowError("Enter a number of hours.");
        return;
      }

      if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours))
      {
        ShowError("Not a valid number.");
        return;
      }

      // Stored as seconds. Snap to nearest 60s — sub-minute precision is meaningless for clock
      // alignment and would clutter the offset display.
      OffsetSeconds = System.Math.Round(hours * 3600.0 / 60.0) * 60.0;
      IsOkClicked = true;
      Close();
    }

    private void ShowError(string message)
    {
      errorText.Text = message;
      errorText.Visibility = Visibility.Visible;
    }
  }
}
