using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NotyWin.App.Models;
using NotyWin.App.Geometry;
using NotyWin.App.Deck;

namespace NotyWin.App;

/// <summary>
/// The Settings window — three sections (Shortcuts, Deck, Notes).
/// Mirrors Sources/SettingsWindow.swift. Reads/writes the shared
/// <see cref="ISettingsStore"/>; every change is applied immediately.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private readonly ISettingsStore _settings;
    private readonly DeckManager _manager;
    private SettingsSnapshot _snapshot;

    public SettingsWindow(ISettingsStore settings, DeckManager manager)
    {
        InitializeComponent();
        _settings = settings;
        _manager = manager;
        _snapshot = settings.Load();
        BuildShortcutsTab();
        Nav.SelectedItem = Nav.MenuItems[0];
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        Content.Children.Clear();
        switch (tag)
        {
            case "shortcuts": BuildShortcutsTab(); break;
            case "deck": BuildDeckTab(); break;
            case "notes": BuildNotesTab(); break;
        }
    }

    private void BuildShortcutsTab()
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(MakeHeader("Global"));
        stack.Children.Add(MakeShortcutRow("New note", _snapshot.ScNewNote, s => UpdateSetting(x => x with { ScNewNote = s })));
        stack.Children.Add(MakeShortcutRow("All notes", _snapshot.ScAllNotes, s => UpdateSetting(x => x with { ScAllNotes = s })));
        stack.Children.Add(MakeShortcutRow("Archive window", _snapshot.ScArchive, s => UpdateSetting(x => x with { ScArchive = s })));
        stack.Children.Add(MakeShortcutRow("Quick capture", _snapshot.ScCapture, s => UpdateSetting(x => x with { ScCapture = s })));

        stack.Children.Add(MakeHeader("In-note"));
        stack.Children.Add(MakeShortcutRow("Close", _snapshot.ScClose, s => UpdateSetting(x => x with { ScClose = s })));
        stack.Children.Add(MakeShortcutRow("Archive note", _snapshot.ScArchiveNote, s => UpdateSetting(x => x with { ScArchiveNote = s })));
        stack.Children.Add(MakeShortcutRow("Delete", _snapshot.ScDelete, s => UpdateSetting(x => x with { ScDelete = s })));
        stack.Children.Add(MakeShortcutRow("Find", _snapshot.ScFind, s => UpdateSetting(x => x with { ScFind = s })));
        stack.Children.Add(MakeShortcutRow("Toggle task", _snapshot.ScTask, s => UpdateSetting(x => x with { ScTask = s })));
        stack.Children.Add(MakeShortcutRow("Pin", _snapshot.ScPin, s => UpdateSetting(x => x with { ScPin = s })));
        stack.Children.Add(MakeShortcutRow("Cycle color", _snapshot.ScColour, s => UpdateSetting(x => x with { ScColour = s })));
        stack.Children.Add(MakeShortcutRow("Bigger text", _snapshot.ScBigger, s => UpdateSetting(x => x with { ScBigger = s })));
        stack.Children.Add(MakeShortcutRow("Smaller text", _snapshot.ScSmaller, s => UpdateSetting(x => x with { ScSmaller = s })));
        Content.Children.Add(stack);
    }

    private void BuildDeckTab()
    {
        var stack = new StackPanel { Spacing = 10 };

        // Style
        var styleLabel = MakeLabel("Style");
        var stylePicker = new ComboBox { Width = 200 };
        stylePicker.Items.Add("Tabs");
        stylePicker.Items.Add("Compact");
        stylePicker.SelectedIndex = _snapshot.DeckStyle == DeckStyle.Compact ? 1 : 0;
        stylePicker.SelectionChanged += (_, _) => UpdateSetting(x => x with
        {
            DeckStyle = stylePicker.SelectedIndex == 1 ? DeckStyle.Compact : DeckStyle.Tabs
        });
        stack.Children.Add(MakeRow(styleLabel, stylePicker));

        // Size slider
        var sizeLabel = MakeLabel("Size");
        var sizeSlider = new Slider { Minimum = 0.7, Maximum = 1.8, StepFrequency = 0.05,
            Width = 200, Value = _snapshot.DeckScale };
        var sizeValue = new TextBlock { Text = $"{(_snapshot.DeckScale * 100):F0}%", Width = 40 };
        sizeSlider.ValueChanged += (_, _) =>
        {
            sizeValue.Text = $"{(sizeSlider.Value * 100):F0}%";
            UpdateSetting(x => x with { DeckScale = sizeSlider.Value });
        };
        stack.Children.Add(MakeRow(sizeLabel, MakeH(sizeSlider, sizeValue)));

        // Edge
        var edgeLabel = MakeLabel("Edge");
        var edgePicker = new ComboBox { Width = 200 };
        edgePicker.Items.Add("Left");
        edgePicker.Items.Add("Right");
        edgePicker.SelectedIndex = _snapshot.DeckOnLeftEdge ? 0 : 1;
        edgePicker.SelectionChanged += (_, _) => UpdateSetting(x => x with
        {
            DeckOnLeftEdge = edgePicker.SelectedIndex == 0
        });
        stack.Children.Add(MakeRow(edgeLabel, edgePicker));

        // Detection area
        var detectLabel = MakeLabel("Detection area");
        var detectPicker = new ComboBox { Width = 200 };
        detectPicker.Items.Add("Narrow (8pt)");
        detectPicker.Items.Add("Standard (14pt)");
        detectPicker.Items.Add("Wide (28pt)");
        detectPicker.Items.Add("Very Wide (44pt)");
        detectPicker.SelectedIndex = _snapshot.EdgeWidth switch
        {
            8 => 0, 28 => 2, 44 => 3, _ => 1
        };
        detectPicker.SelectionChanged += (_, _) => UpdateSetting(x => x with
        {
            EdgeWidth = detectPicker.SelectedIndex switch { 0 => 8.0, 2 => 28.0, 3 => 44.0, _ => 14.0 }
        });
        stack.Children.Add(MakeRow(detectLabel, detectPicker));

        // Keep deck open
        var keepSwitch = new ToggleSwitch { IsOn = _snapshot.DeckAlwaysShown, OnContent = "On", OffContent = "Off" };
        keepSwitch.Toggled += (_, _) => UpdateSetting(x => x with { DeckAlwaysShown = keepSwitch.IsOn });
        stack.Children.Add(MakeRow(MakeLabel("Keep deck open"), keepSwitch));

        // Hide pill
        var hideSwitch = new ToggleSwitch { IsOn = _snapshot.DeckPillHidden, OnContent = "On", OffContent = "Off" };
        hideSwitch.Toggled += (_, _) => UpdateSetting(x => x with { DeckPillHidden = hideSwitch.IsOn });
        stack.Children.Add(MakeRow(MakeLabel("Hide pill"), hideSwitch));

        // Hover preview
        var hoverSwitch = new ToggleSwitch { IsOn = _snapshot.TabPreview, OnContent = "On", OffContent = "Off" };
        hoverSwitch.Toggled += (_, _) => UpdateSetting(x => x with { TabPreview = hoverSwitch.IsOn });
        stack.Children.Add(MakeRow(MakeLabel("Hover preview"), hoverSwitch));

        // Open on hover
        var openHoverSwitch = new ToggleSwitch { IsOn = _snapshot.OpenOnHover, OnContent = "On", OffContent = "Off" };
        openHoverSwitch.Toggled += (_, _) => UpdateSetting(x => x with { OpenOnHover = openHoverSwitch.IsOn });
        stack.Children.Add(MakeRow(MakeLabel("Open on hover"), openHoverSwitch));

        // Show over full-screen
        var fullscreenSwitch = new ToggleSwitch { IsOn = _snapshot.ShowOverFullScreen, OnContent = "On", OffContent = "Off" };
        fullscreenSwitch.Toggled += (_, _) => UpdateSetting(x => x with { ShowOverFullScreen = fullscreenSwitch.IsOn });
        stack.Children.Add(MakeRow(MakeLabel("Show over full-screen"), fullscreenSwitch));

        Content.Children.Add(stack);
    }

    private void BuildNotesTab()
    {
        var stack = new StackPanel { Spacing = 10 };

        // Font
        var fontLabel = MakeLabel("Font");
        var fontPicker = new ComboBox { Width = 200 };
        var fonts = new[] { "System", "Noteworthy-Light", "Segoe UI", "Georgia", "Consolas" };
        foreach (var f in fonts) fontPicker.Items.Add(f);
        fontPicker.SelectedIndex = Math.Max(0, Array.IndexOf(fonts, _snapshot.NoteFontName));
        fontPicker.SelectionChanged += (_, _) => UpdateSetting(x => x with
        {
            NoteFontName = fontPicker.SelectedItem as string ?? "Segoe UI"
        });
        stack.Children.Add(MakeRow(fontLabel, fontPicker));

        // Text size
        var sizeLabel = MakeLabel("Text size");
        var sizeSlider = new Slider { Minimum = 10, Maximum = 30, StepFrequency = 0.5,
            Width = 200, Value = _snapshot.NoteFontSize };
        var sizeValue = new TextBlock { Text = $"{_snapshot.NoteFontSize:F1}", Width = 40 };
        sizeSlider.ValueChanged += (_, _) =>
        {
            sizeValue.Text = $"{sizeSlider.Value:F1}";
            UpdateSetting(x => x with { NoteFontSize = sizeSlider.Value });
        };
        stack.Children.Add(MakeRow(sizeLabel, MakeH(sizeSlider, sizeValue)));

        // Note size preset
        var noteLabel = MakeLabel("Note size");
        var notePicker = new ComboBox { Width = 200 };
        notePicker.Items.Add("Small (400×320)");
        notePicker.Items.Add("Medium (460×380)");
        notePicker.Items.Add("Large (560×470)");
        notePicker.Items.Add("Huge (680×560)");
        notePicker.SelectedIndex = Math.Clamp(_snapshot.NoteSizeIndex, 0, 3);
        notePicker.SelectionChanged += (_, _) => UpdateSetting(x => x with
        {
            NoteSizeIndex = notePicker.SelectedIndex
        });
        stack.Children.Add(MakeRow(noteLabel, notePicker));

        // Markdown
        var mdSwitch = new ToggleSwitch { IsOn = _snapshot.MarkdownStyling, OnContent = "On", OffContent = "Off" };
        mdSwitch.Toggled += (_, _) => UpdateSetting(x => x with { MarkdownStyling = mdSwitch.IsOn });
        stack.Children.Add(MakeRow(MakeLabel("Markdown styling"), mdSwitch));

        Content.Children.Add(stack);
    }

    // MARK: - Helpers

    private TextBlock MakeHeader(string text) => new()
    {
        Text = text,
        FontSize = 14,
        FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 },
        Margin = new Thickness(0, 8, 0, 4),
    };

    private TextBlock MakeLabel(string text) => new()
    {
        Text = text,
        Width = 140,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private Grid MakeRow(FrameworkElement label, FrameworkElement content)
    {
        var g = new Grid { ColumnSpacing = 10 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(label, 0);
        Grid.SetColumn(content, 1);
        g.Children.Add(label);
        g.Children.Add(content);
        return g;
    }

    private StackPanel MakeH(params FrameworkElement[] items)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var i in items) s.Children.Add(i);
        return s;
    }

    private Grid MakeShortcutRow(string label, Shortcut shortcut, Action<Shortcut> onChange)
    {
        var box = new TextBox
        {
            Text = FormatShortcut(shortcut),
            Width = 160,
            IsReadOnly = true,
        };
        var recBtn = new Button { Content = "Record", Width = 80 };
        bool recording = false;
        recBtn.Click += (_, _) =>
        {
            recording = !recording;
            recBtn.Content = recording ? "Stop" : "Record";
            box.Text = recording ? "Press shortcut..." : FormatShortcut(shortcut);
        };
        box.KeyDown += (s, e) =>
        {
            if (!recording) return;
            // Build shortcut from captured key + modifiers.
            var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            var alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            var win = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.LeftWindows)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down) ||
                Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.RightWindows)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

            var mods = KeyModifiers.None;
            if (shift) mods |= KeyModifiers.Shift;
            if (ctrl) mods |= KeyModifiers.Control;
            if (alt) mods |= KeyModifiers.Alt;
            if (win) mods |= KeyModifiers.Meta;

            var vk = (int)e.Key;
            if (vk == 0) return; // modifier-only
            var newSc = new Shortcut { Modifiers = mods, KeyCode = vk };
            recording = false;
            recBtn.Content = "Record";
            box.Text = FormatShortcut(newSc);
            onChange(newSc);
            e.Handled = true;
        };
        return MakeRow(MakeLabel(label), MakeH(box, recBtn));
    }

    private static string FormatShortcut(Shortcut s)
    {
        var parts = new List<string>();
        if (s.Modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (s.Modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (s.Modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (s.Modifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Win");
        parts.Add(KeyName(s.KeyCode));
        return string.Join("+", parts);
    }

    private static string KeyName(int vk) => vk switch
    {
        0x08 => "Backspace", 0x09 => "Tab", 0x0D => "Enter", 0x1B => "Esc",
        0x20 => "Space", 0x2E => "Del", 0x26 => "Up", 0x28 => "Down",
        0x25 => "Left", 0x27 => "Right",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x70 and <= 0x7B => $"F{vk - 0x6F}",
        0xBB => "+", 0xBD => "-", 0xBE => ".",
        _ => $"VK{vk:X}"
    };

    private void UpdateSetting(Func<SettingsSnapshot, SettingsSnapshot> mutate)
    {
        _snapshot = mutate(_snapshot);
        _settings.Save(_snapshot);
        _manager.OnSettingsChanged();
    }
}
