using log4net;
using System;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;

namespace EQLogParser
{
  public partial class RaidRecordSetupWindow
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    public RaidRecordSetupWindow()
    {
      ThemeConfig.SetCurrentTheme(this);
      InitializeComponent();
      Owner = MainActions.GetOwner();
      LoadSettings();
    }

    private void LoadSettings()
    {
      var delivery = ConfigUtil.GetRaidRecordDelivery();
      if (delivery == "Discord")
      {
        sendToDiscordRadio.IsChecked = true;
      }
      else
      {
        saveToFileRadio.IsChecked = true;
      }

      webhookUrlBox.Text = ConfigUtil.GetRaidRecordWebhook() ?? string.Empty;
      UpdateWebhookFieldState();
    }

    private void UpdateWebhookFieldState()
    {
      var discordSelected = sendToDiscordRadio.IsChecked == true;
      webhookUrlBox.IsEnabled = discordSelected;
      testButton.IsEnabled = discordSelected;
    }

    private void DeliveryRadioChecked(object sender, RoutedEventArgs e) => UpdateWebhookFieldState();

    private async void TestButtonClick(object sender, RoutedEventArgs e)
    {
      var url = webhookUrlBox.Text.Trim();
      if (string.IsNullOrEmpty(url))
      {
        testStatusText.Text = "Enter a webhook URL first.";
        testStatusText.SetResourceReference(ForegroundProperty, "EQWarnForegroundBrush");
        testStatusText.Visibility = Visibility.Visible;
        return;
      }

      testButton.IsEnabled = false;
      testStatusText.Text = "Sending test message…";
      testStatusText.SetResourceReference(ForegroundProperty, "PrimaryDarken");
      testStatusText.Visibility = Visibility.Visible;

      try
      {
        await MainActions.SendDiscordMessage("EQLogParser raid recording test — connection OK.", url, silent: true);
        testStatusText.Text = "Test message sent successfully.";
        testStatusText.SetResourceReference(ForegroundProperty, "EQGoodForegroundBrush");
      }
      catch (Exception ex)
      {
        Log.Error("Discord webhook test failed", ex);
        testStatusText.Text = "Test failed. Check the webhook URL.";
        testStatusText.SetResourceReference(ForegroundProperty, "EQWarnForegroundBrush");
      }
      finally
      {
        testButton.IsEnabled = true;
      }
    }

    private void OkButtonClick(object sender, RoutedEventArgs e)
    {
      var delivery = sendToDiscordRadio.IsChecked == true ? "Discord" : "File";
      ConfigUtil.SetRaidRecordDelivery(delivery);

      if (delivery == "Discord")
      {
        ConfigUtil.SetRaidRecordWebhook(webhookUrlBox.Text.Trim());
      }

      DialogResult = true;
      Close();
    }

    private void CancelButtonClick(object sender, RoutedEventArgs e)
    {
      DialogResult = false;
      Close();
    }
  }
}
