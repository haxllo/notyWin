using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NotyWin.App.Models;
using NotyWin.App.Geometry;
using NotyWin.App.Deck;
using Color = Windows.UI.Color;

namespace NotyWin.App;

/// <summary>
/// The Settings window — builds content programmatically.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private readonly ISettingsStore _settings;
    private readonly DeckManager _manager;
    private SettingsSnapshot _snapshot;

    public SettingsWindow(ISettingsStore settings, DeckManager manager)
    {
        try
        {
            InitializeComponent();
            _settings = settings;
            _manager = manager;
            _snapshot = settings.Load();
            BuildContent();
            StatusText.Text = "Ready";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
        }
    }

    private void BuildContent()
    {
        ContentPanel.Children.Clear();

        ContentPanel.Children.Add(MakeHeader("Deck Settings"));

        // Style
        var stylePicker = new ComboBox { Width = 200 };
        stylePicker.Items.Add("Tabs");
        stylePicker.Items.Add("Compact");
        stylePicker.SelectedIndex = _snapshot.DeckStyle == DeckStyle.Compact ? 1 : 0;
        stylePicker.SelectionChanged += (_, _) => UpdateSetting(x => x with
        {
            DeckStyle = stylePicker.SelectedIndex == 1 ? DeckStyle.Compact : DeckStyle.Tabs
        });
        ContentPanel.Children.Add(MakeRow("Style", stylePicker));

        // Size
        var sizeSlider = new Slider { Minimum = 0.7, Maximum = 1.8, StepFrequency = 0.05,
            Width = 200, Value = _snapshot.DeckScale };
        var sizeValue = new TextBlock { Text = $"{(_snapshot.DeckScale * 100):F0}%", Width = 40,
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x33, 0x33, 0x33)) };
        sizeSlider.ValueChanged += (_, _) =>
        {
            sizeValue.Text = $"{(sizeSlider.Value * 100):F0}%";
            UpdateSetting(x => x with { DeckScale = sizeSlider.Value });
        };
        ContentPanel.Children.Add(MakeRow("Size", MakeH(sizeSlider, sizeValue)));

        // Edge
        var edgePicker = new ComboBox { Width = 200 };
        edgePicker.Items.Add("Left");
        edgePicker.Items.Add("Right");
        edgePicker.SelectedIndex = _snapshot.DeckOnLeftEdge ? 0 : 1;
        edgePicker.SelectionChanged += (_, _) => UpdateSetting(x => x with
        {
            DeckOnLeftEdge = edgePicker.SelectedIndex == 0
        });
        ContentPanel.Children.Add(MakeRow("Edge", edgePicker));

        // Keep deck open
        var keepSwitch = new ToggleSwitch { IsOn = _snapshot.DeckAlwaysShown };
        keepSwitch.Toggled += (_, _) => UpdateSetting(x => x with { DeckAlwaysShown = keepSwitch.IsOn });
        ContentPanel.Children.Add(MakeRow("Keep deck open", keepSwitch));

        // Hover preview
        var hoverSwitch = new ToggleSwitch { IsOn = _snapshot.TabPreview };
        hoverSwitch.Toggled += (_, _) => UpdateSetting(x => x with { TabPreview = hoverSwitch.IsOn });
        ContentPanel.Children.Add(MakeRow("Hover preview", hoverSwitch));

        ContentPanel.Children.Add(MakeHeader("Notes Settings"));

        // Text size
        var textSlider = new Slider { Minimum = 10, Maximum = 30, StepFrequency = 0.5,
            Width = 200, Value = _snapshot.NoteFontSize };
        var textValue = new TextBlock { Text = $"{_snapshot.NoteFontSize:F1}", Width = 40,
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x33, 0x33, 0x33)) };
        textSlider.ValueChanged += (_, _) =>
        {
            textValue.Text = $"{textSlider.Value:F1}";
            UpdateSetting(x => x with { NoteFontSize = textSlider.Value });
        };
        ContentPanel.Children.Add(MakeRow("Text size", MakeH(textSlider, textValue)));

        // Markdown
        var mdSwitch = new ToggleSwitch { IsOn = _snapshot.MarkdownStyling };
        mdSwitch.Toggled += (_, _) => UpdateSetting(x => x with { MarkdownStyling = mdSwitch.IsOn });
        ContentPanel.Children.Add(MakeRow("Markdown styling", mdSwitch));
    }

    private TextBlock MakeHeader(string text) => new()
    {
        Text = text,
        FontSize = 16,
        FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 },
        Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x33, 0x33, 0x33)),
        Margin = new Thickness(0, 12, 0, 4),
    };

    private Grid MakeRow(string label, FrameworkElement content)
    {
        var g = new Grid { ColumnSpacing = 10, Margin = new Thickness(0, 4, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var labelBlock = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x33, 0x33, 0x33)),
        };
        Grid.SetColumn(labelBlock, 0);
        Grid.SetColumn(content, 1);
        g.Children.Add(labelBlock);
        g.Children.Add(content);
        return g;
    }

    private StackPanel MakeH(params FrameworkElement[] items)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var i in items) s.Children.Add(i);
        return s;
    }

    private void UpdateSetting(Func<SettingsSnapshot, SettingsSnapshot> mutate)
    {
        _snapshot = mutate(_snapshot);
        _settings.Save(_snapshot);
        _manager.OnSettingsChanged();
    }
}
