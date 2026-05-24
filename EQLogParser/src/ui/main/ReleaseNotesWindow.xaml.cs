using System;
using System.IO;
using System.Windows;
using System.Windows.Documents;

namespace EQLogParser
{
  public partial class ReleaseNotesWindow
  {
    public ReleaseNotesWindow()
    {
      MainActions.SetCurrentTheme(this);
      InitializeComponent();
      Owner = MainActions.GetOwner();
      LoadReleaseNotes();
    }

    private void LoadReleaseNotes()
    {
      var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "releasenotes.rtf");
      if (!File.Exists(path)) return;

      using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
      var range = new TextRange(rtfBox.Document.ContentStart, rtfBox.Document.ContentEnd);
      range.Load(stream, DataFormats.Rtf);
    }

    private void OkClick(object sender, RoutedEventArgs e) => Close();
  }
}
