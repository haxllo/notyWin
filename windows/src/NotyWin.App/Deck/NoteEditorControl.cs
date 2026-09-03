using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using NotyWin.App.Models;
using Windows.UI.Text;
using Color = Windows.UI.Color;

namespace NotyWin.App.Deck;

/// <summary>
/// The open-note editor, overlaid on the deck canvas at the expanded note's
/// rect. Win2D cannot take keyboard input, so the sheet — paper, header,
/// editable body, footer — is real XAML, mirroring <c>NoteEditorView</c> in
/// Sources/NoteEditor.swift. The gutter/tab stays on the canvas behind it, so
/// the note still reads as growing out of the deck.
///
/// Autosave matches macOS: 250 ms after typing stops the body is committed to
/// the <see cref="NoteList"/>; <see cref="Flush"/> commits immediately when the
/// note closes or the deck collapses.
/// </summary>
public sealed class NoteEditorControl : UserControl
{
    public NoteList? Notes { get; set; }

    /// <summary>Asked to collapse the deck (Close / Archive / Delete).</summary>
    public Action? OnRequestCollapse { get; set; }

    /// <summary>Fired after a mutation so the deck tabs repaint.</summary>
    public Action? OnMutated { get; set; }

    public double BodyFontSize { get; set; } = 13.5;

    private Note? _note;
    private bool _onRight = true;
    private DateTime? _savedAt;
    private bool _suppress;
    private DispatcherQueueTimer? _saveTimer;

    private readonly Border _border;
    private readonly TextBlock _title;
    private readonly TextBlock _saved;
    private readonly TextBlock _pinGlyph;
    private readonly Button _pin;
    private readonly TextBox _body;
    private readonly StackPanel _swatches;
    private readonly List<Button> _footerButtons = new();

    private static readonly Color Transparent = Color.FromArgb(0, 0, 0, 0);
    private static readonly FontWeight SemiBold = new() { Weight = 600 };

    public NoteEditorControl()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });

        // Header: title, saved-ago, pin.
        var header = new Grid { Padding = new Thickness(14, 0, 14, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _title = new TextBlock
        {
            FontSize = 12.5,
            FontWeight = SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(_title, 0);
        header.Children.Add(_title);

        _saved = new TextBlock
        {
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        Grid.SetColumn(_saved, 1);
        header.Children.Add(_saved);

        _pinGlyph = new TextBlock
        {
            Text = "\uE718", // Segoe MDL2: Pin
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 12,
            FontWeight = SemiBold,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
        };
        _pin = new Button
        {
            Content = _pinGlyph,
            Background = new SolidColorBrush(Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Width = 26,
            Height = 26,
        };
        _pin.Click += (_, _) =>
        {
            if (_note is null || Notes is null) return;
            Notes.TogglePin(_note.Id);
            RefreshHeader();
            OnMutated?.Invoke();
        };
        Grid.SetColumn(_pin, 2);
        header.Children.Add(_pin);

        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // Body: a borderless, transparent TextBox so the paper shows through.
        _body = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Transparent),
            Padding = new Thickness(15, 8, 15, 8),
            IsSpellCheckEnabled = true,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectionHighlightColor = new SolidColorBrush(Color.FromArgb(0x55, 0x00, 0x00, 0x00)),
        };
        ScrollViewer.SetVerticalScrollBarVisibility(_body, ScrollBarVisibility.Auto);
        foreach (var key in new[]
        {
            "TextControlBackground", "TextControlBackgroundPointerOver",
            "TextControlBackgroundFocused", "TextControlBackgroundDisabled",
            "TextControlBorderBrush", "TextControlBorderBrushPointerOver",
            "TextControlBorderBrushFocused", "TextControlBorderBrushDisabled",
        })
            _body.Resources[key] = new SolidColorBrush(Transparent);
        _body.TextChanged += OnTextChanged;
        Grid.SetRow(_body, 1);
        root.Children.Add(_body);

        // Footer: colour swatches, then Archive / Delete / Close.
        var footer = new Grid { Padding = new Thickness(14, 0, 14, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _swatches = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 3,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(_swatches, 0);
        footer.Children.Add(_swatches);

        _footerButtons.Add(MakeFooterButton("Archive", 2, (_, _) =>
        {
            if (_note is null || Notes is null) return;
            Notes.SetArchived(_note.Id, true);
            OnMutated?.Invoke();
            OnRequestCollapse?.Invoke();
        }));
        _footerButtons.Add(MakeFooterButton("Delete", 3, (_, _) =>
        {
            if (_note is null || Notes is null) return;
            Notes.Delete(_note.Id, TimeSpan.FromSeconds(10));
            OnMutated?.Invoke();
            OnRequestCollapse?.Invoke();
        }));
        _footerButtons.Add(MakeFooterButton("Close", 4, (_, _) => OnRequestCollapse?.Invoke()));
        foreach (var b in _footerButtons) footer.Children.Add(b);

        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        _border = new Border
        {
            Background = new SolidColorBrush(Transparent),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x12, 0, 0, 0)),
            BorderThickness = new Thickness(0.5),
            Child = root,
        };
        Content = _border;
        BuildSwatches();
    }

    public bool OnRight
    {
        get => _onRight;
        set
        {
            _onRight = value;
            _border.CornerRadius = value
                ? new CornerRadius(14, 0, 0, 14)
                : new CornerRadius(0, 14, 14, 0);
        }
    }

    /// <summary>Bind a note. Re-binding the same note keeps the caret and undo
    /// buffer; a different note flushes the outgoing one first.</summary>
    public void SetNote(Note note, bool autofocus)
    {
        if (_note?.Id == note.Id)
        {
            RefreshHeader();
            return;
        }

        Flush();
        _note = note;
        _suppress = true;
        _body.Text = note.Body ?? "";
        _suppress = false;
        _savedAt = note.Modified;
        Restyle();

        if (autofocus)
        {
            _body.SelectionStart = _body.Text?.Length ?? 0;
            _body.Focus(FocusState.Programmatic);
        }
    }

    /// <summary>Commit any pending edit immediately (250 ms debounce bypassed).</summary>
    public void Flush()
    {
        _saveTimer?.Stop();
        Commit();
    }

    private void Commit()
    {
        if (_note is null || Notes is null) return;
        Notes.UpdateBody(_note.Id, _body.Text ?? "");
        _savedAt = DateTime.UtcNow;
        RefreshHeader();
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress || _note is null) return;
        if (_saveTimer is null)
        {
            _saveTimer = DispatcherQueue.CreateTimer();
            _saveTimer.Interval = TimeSpan.FromMilliseconds(250);
            _saveTimer.IsRepeating = false;
            _saveTimer.Tick += (_, _) => { _saveTimer!.Stop(); Commit(); };
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    // MARK: Styling

    private void Restyle()
    {
        if (_note is null) return;
        var ink = FromArgb(_note.Palette.InkArgb);

        _border.Background = new SolidColorBrush(FromArgb(_note.Palette.PaperArgb));
        _body.Foreground = new SolidColorBrush(ink);
        _body.FontSize = BodyFontSize;

        foreach (var b in _footerButtons)
        {
            b.Foreground = new SolidColorBrush(WithAlpha(ink, 0.72));
            b.Background = new SolidColorBrush(WithAlpha(ink, 0.08));
        }

        BuildSwatches();
        RefreshHeader();
    }

    /// <summary>The parts that change while typing or toggling pin — cheap enough
    /// to refresh on every autosave without rebuilding the whole sheet.</summary>
    private void RefreshHeader()
    {
        if (_note is null) return;
        var ink = FromArgb(_note.Palette.InkArgb);
        _title.Text = _note.DisplayTitle("Untitled");
        _title.Foreground = new SolidColorBrush(WithAlpha(ink, 0.92));
        _saved.Text = SavedLabel(_savedAt);
        _saved.Foreground = new SolidColorBrush(WithAlpha(ink, 0.42));
        _pinGlyph.Foreground = new SolidColorBrush(WithAlpha(ink, _note.Pinned ? 0.85 : 0.40));
        _pinGlyph.RenderTransform = new RotateTransform { Angle = _note.Pinned ? 0 : 32 };
    }

    private void BuildSwatches()
    {
        _swatches.Children.Clear();
        if (_note is null) return;
        var ink = FromArgb(_note.Palette.InkArgb);
        for (var i = 0; i < NoteColor.All.Length; i++)
        {
            var idx = i;
            var c = NoteColor.All[i];
            var selected = _note.Color == idx;
            var fill = new Ellipse
            {
                Width = 11,
                Height = 11,
                Fill = new SolidColorBrush(FromArgb(c.DashArgb)),
                Stroke = selected ? new SolidColorBrush(WithAlpha(ink, 0.55)) : null,
                StrokeThickness = selected ? 1.5 : 0,
            };
            var btn = new Button
            {
                Content = fill,
                Background = new SolidColorBrush(Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(2),
                Width = 19,
                Height = 19,
                VerticalAlignment = VerticalAlignment.Center,
            };
            btn.Click += (_, _) =>
            {
                if (_note is null || Notes is null) return;
                Notes.SetColor(_note.Id, idx);
                Restyle();
                OnMutated?.Invoke();
            };
            _swatches.Children.Add(btn);
        }
    }

    private Button MakeFooterButton(string label, int column, RoutedEventHandler onClick)
    {
        var b = new Button
        {
            Content = label,
            FontSize = 10.5,
            Height = 22,
            Padding = new Thickness(9, 0, 9, 0),
            Margin = new Thickness(7, 0, 0, 0),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Center,
        };
        b.Click += onClick;
        Grid.SetColumn(b, column);
        return b;
    }

    private static string SavedLabel(DateTime? t)
    {
        if (t is null) return "Not saved";
        var span = DateTime.UtcNow - t.Value;
        if (span.TotalSeconds < 5) return "Saved just now";
        if (span.TotalSeconds < 60) return $"Saved {(int)span.TotalSeconds}s ago";
        if (span.TotalMinutes < 60) return $"Saved {(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24) return $"Saved {(int)span.TotalHours}h ago";
        return $"Saved {t.Value.ToLocalTime():MMM d}";
    }

    private static Color FromArgb(int argb) => Color.FromArgb(
        (byte)((argb >> 24) & 0xFF), (byte)((argb >> 16) & 0xFF),
        (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF));

    private static Color WithAlpha(Color c, double a) =>
        Color.FromArgb((byte)Math.Clamp((int)Math.Round(a * 255), 0, 255), c.R, c.G, c.B);
}
