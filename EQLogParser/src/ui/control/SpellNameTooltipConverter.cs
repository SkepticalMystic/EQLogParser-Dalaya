using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace EQLogParser
{
  // IValueConverter for spell-name cells. Returns a lazily-populated ToolTip for rows whose
  // name resolves to a known spell, or DependencyProperty.UnsetValue (no tooltip) for player
  // names, group headers, and other non-spell rows. Pair with ToolTipService.InitialShowDelay
  // on the host element to control the hover delay.
  public class SpellNameTooltipConverter : IValueConverter
  {
    private const string ReceivedPrefix = "Received ";

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
      if (value is not string name) return DependencyProperty.UnsetValue;
      var abbrvName = name.StartsWith(ReceivedPrefix, StringComparison.Ordinal)
        ? name[ReceivedPrefix.Length..] : name;
      if (EQDataStore.Instance.GetSpellByAbbrv(abbrvName) == null)
        return DependencyProperty.UnsetValue;

      var tip = new ToolTip();
      tip.Opened += (s, _) =>
      {
        var t = (ToolTip)s;
        if (t.Content == null)
          t.Content = SpellNameTooltipConverter.BuildContent(name);
      };
      return tip;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
      => throw new NotSupportedException();

    // Builds the tooltip content panel for a given abbreviated spell name. Returns null if
    // the spell isn't in the data store (should not happen if the converter already checked).
    internal static UIElement BuildContent(string spellName)
    {
      if (string.IsNullOrEmpty(spellName)) return null;
      var abbrvName = spellName.StartsWith(ReceivedPrefix, StringComparison.Ordinal)
        ? spellName[ReceivedPrefix.Length..] : spellName;
      var spell = EQDataStore.Instance.GetSpellByAbbrv(abbrvName);
      if (spell == null) return null;

      var effects = EQDataStore.Instance.GetSpellEffects(spell.Id);
      var level = spell.Level > 0 ? spell.Level : 65;
      var lines = effects != null ? SpellEffectDecoder.Describe(effects, level, level) : [];

      var fg = Application.Current.Resources["ContentForeground"] as Brush ?? SystemColors.ControlTextBrush;

      var panel = new StackPanel { Margin = new Thickness(4, 3, 4, 3) };

      var title = new TextBlock
      {
        Text = spell.Name,
        FontWeight = FontWeights.Bold,
        Foreground = fg,
        Margin = new Thickness(0, 0, 0, lines.Count > 0 || !string.IsNullOrEmpty(spell.Description) ? 4 : 0)
      };
      panel.Children.Add(title);

      if (!string.IsNullOrEmpty(spell.Description))
      {
        panel.Children.Add(new TextBlock
        {
          Text = spell.Description,
          Foreground = fg,
          Opacity = 0.75,
          TextWrapping = TextWrapping.Wrap,
          MaxWidth = 360,
          Margin = new Thickness(0, 0, 0, lines.Count > 0 ? 4 : 0)
        });
      }

      if (lines.Count > 0)
      {
        panel.Children.Add(new Separator { Margin = new Thickness(0, 0, 0, 3) });
        foreach (var line in lines)
        {
          panel.Children.Add(new TextBlock { Text = line, Foreground = fg });
        }
      }

      return panel;
    }
  }
}
