using Microsoft.UI.Xaml;

namespace NotyWin.App;

/// <summary>
/// Hidden status window. The actual app UI lives in per-display deck HWNDs
/// (see <see cref="NotyWin.App.Deck.DeckManager"/>). This window exists so
/// the WinUI 3 XAML island has a host and so the developer can see "it's
/// running".
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    public void SetStatus(string displays, int notes)
    {
        DisplaysText.Text = "Displays: " + displays;
        NotesText.Text = "Notes: " + notes;
    }
}