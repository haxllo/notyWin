using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NotyWin.App.Deck;
using NotyWin.App.Geometry;
using NotyWin.App.Models;

namespace NotyWin.App;

/// <summary>
/// WPF Settings window. Replaces the WinUI 3 SettingsWindow.
/// Three sections: Deck, Notes, Shortcuts.
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly ISettingsStore _settings;
    private readonly DeckManager _manager;
    private SettingsSnapshot _snapshot;
    private readonly StackPanel _content = new() { Margin = new Thickness(20) };

    public SettingsWindow(ISettingsStore settings, DeckManager manager)
    {
        _settings = settings;
        _manager = manager;
        _snapshot = settings.Load();

        Title = "Noty Settings";
        Width = 600;
        Height = 500;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;

        var root = new StackPanel();

        // Tab buttons
        var tabBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10) };
        var deckBtn = new Button { Content = "Deck", Margin = new Thickness(2) };
        var notesBtn = new Button { Content = "Notes", Margin = new Thickness(2) };
        deckBtn.Click += (_, _) => BuildDeckTab();
        notesBtn.Click += (_, _) => BuildNotesTab();
        tabBar.Children.Add(deckBtn);
        tabBar.Children.Add(notesBtn);
        root.Children.Add(tabBar);

        var scroll = new ScrollViewer { Content = _content };
        root.Children.Add(scroll);
        Content = root;

        BuildDeckTab();
    }

    private void BuildDeckTab()
    {
        _content.Children.Clear();

        AddHeader("Deck");
        AddToggleRow("Keep deck open", _snapshot.DeckAlwaysShown, v => UpdateSetting(x => x with { DeckAlwaysShown = v }));
        AddToggleRow("Hide pill", _snapshot.DeckPillHidden, v => UpdateSetting(x => x with { DeckPillHidden = v }));
        AddToggleRow("Hover preview", _snapshot.TabPreview, v => UpdateSetting(x => x with { TabPreview = v }));
        AddToggleRow("Open on hover", _snapshot.OpenOnHover, v => UpdateSetting(x => x with { OpenOnHover = v }));
        AddToggleRow("Show over full-screen", _snapshot.ShowOverFullScreen, v => UpdateSetting(x => x with { ShowOverFullScreen = v }));
        AddToggleRow("Launch at login", _snapshot.LaunchAtLogin, v => UpdateSetting(x => x with { LaunchAtLogin = v }));
    }

    private void BuildNotesTab()
    {
        _content.Children.Clear();

        AddHeader("Notes");
        AddSliderRow("Text size", 10, 30, 0.5, _snapshot.NoteFontSize, v => UpdateSetting(x => x with { NoteFontSize = v }));
        AddToggleRow("Markdown styling", _snapshot.MarkdownStyling, v => UpdateSetting(x => x with { MarkdownStyling = v }));
    }

    private void AddHeader(string text)
    {
        _content.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 12, 0, 8),
        });
    }

    private void AddToggleRow(string label, bool value, Action<bool> onChange)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        row.Children.Add(new TextBlock
        {
            Text = label,
            Width = 160,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var cb = new CheckBox { IsChecked = value };
        cb.Checked += (_, _) => onChange(true);
        cb.Unchecked += (_, _) => onChange(false);
        row.Children.Add(cb);
        _content.Children.Add(row);
    }

    private void AddSliderRow(string label, double min, double max, double step, double value, Action<double> onChange)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        row.Children.Add(new TextBlock
        {
            Text = label,
            Width = 160,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            TickFrequency = step,
            Value = value,
            Width = 200,
        };
        slider.ValueChanged += (_, e) => onChange(e.NewValue);
        row.Children.Add(slider);
        _content.Children.Add(row);
    }

    private void UpdateSetting(Func<SettingsSnapshot, SettingsSnapshot> mutate)
    {
        _snapshot = mutate(_snapshot);
        _settings.Save(_snapshot);
        _manager.OnSettingsChanged();
    }
}
