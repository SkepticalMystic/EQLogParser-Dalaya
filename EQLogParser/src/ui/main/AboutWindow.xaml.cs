using System.Windows;
using System.Windows.Navigation;

namespace EQLogParser
{
  public partial class AboutWindow
  {
    public AboutWindow()
    {
      MainActions.SetCurrentTheme(this);
      InitializeComponent();
      Owner = MainActions.GetOwner();
      versionText.Text = $"v{App.Version}";
    }

    private void HyperlinkRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
      MainActions.OpenFileWithDefault(e.Uri.ToString());
      e.Handled = true;
    }

    private void OkClick(object sender, RoutedEventArgs e) => Close();
  }
}
