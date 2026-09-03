using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using NotyWin.App.Models;
using NotyWin.Rendering;
using Windows.System;
using Color = Windows.UI.Color;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace NotyWin.App.Deck;

/// <summary>
/// The open-note editor, overlaid on the deck canvas at the expanded note's
/// rect. Win2D cannot take keyboard input, so the sheet — paper, header,
/// editable body, footer — is real XAML, mirroring <c>NoteEditorView</c> in
/// Sources/NoteEditor.swift. The gutter/tab stays on the canvas behind it, so
/// the note still reads as growing out of the deck.
///
/// The body is a <see cref="RichEditBox"/> styled through the Text Object
/// Model by <see cref="EditorStyleEngine"/>: Markdown-as-you-type, task
/// checkboxes, Ctrl+click links, and per-paragraph text direction. Autosave
/// matches macOS — 250 ms after typing stops the body is committed to the
/// <see cref="NoteList"/>; <see cref="Flush"/> commits immediately.
/// </summary>
public sealed class NoteEditorControl : UserControl
{
    private const string BodyFontName = "Segoe UI";

    public NoteList? Notes { get; set; }

    /// <summary>Asked to collapse the deck (Close / Archive / Delete).</summary>
    public Action? OnRequestCollapse { get; set; }

    /// <summary>Fired after a mutation so the deck tabs repaint.</summary>
    public Action? OnMutated { get; set; }

    /// <summary>Asked to detach into a floating note window.</summary>
    public Action<Note>? OnDetachRequested { get; set; }

    public double BodyFontSize
    {
        get => _bodyFontSize;
        set
        {
            if (Math.Abs(_bodyFontSize - value) < 0.01) return;
            _bodyFontSize = value;
            RefreshStyleIfBound();
        }
    }

    /// <summary>Markdown styling toggle — <c>Settings.markdownStyling</c>.</summary>
    public bool MarkdownEnabled
    {
        get => _markdown;
        set
        {
            if (_markdown == value) return;
            _markdown = value;
            RefreshStyleIfBound();
        }
    }

    private double _bodyFontSize = 13.5;
    private bool _markdown = true;
    private Note? _note;
    private bool _onRight = true;
    private DateTime? _savedAt;
    private bool _suppress;
    private bool _composing;
    private bool _deferredFullPass;
    private bool _styleQueued;
    /// <summary>Base body size in TOM points (XAML FontSize is DIPs).</summary>
    private double _basePt = 10.125;
    /// <summary>Plain text with '\r' paragraph marks — offsets match TOM ranges.</summary>
    private string _text = "";
    private string? _appliedToken;
    private DispatcherQueueTimer? _saveTimer;

    private readonly Border _border;
    private readonly RichEditBox _body;
    private readonly Grid _findRow;
    private readonly TextBox _findBox;
    private readonly TextBlock _findCount;
    private readonly List<Button> _footerButtons = new();

    // Assigned once by the builder methods the constructor calls.
    private TextBlock _title = null!;
    private TextBlock _saved = null!;
    private TextBlock _pinGlyph = null!;
    private Button _pin = null!;
    private TextBlock _dirLabel = null!;
    private MenuFlyout _dirMenu = null!;
    private StackPanel _swatches = null!;

    private static readonly Color Transparent = Color.FromArgb(0, 0, 0, 0);
    private static readonly Windows.UI.Text.FontWeight SemiBold = new() { Weight = 600 };

    public NoteEditorControl()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });

        var header = BuildHeader();
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var find = BuildFindBar();
        _findRow = find.Row;
        _findBox = find.Box;
        _findCount = find.Count;
        _findRow.Visibility = Visibility.Collapsed;
        Grid.SetRow(_findRow, 1);
        root.Children.Add(_findRow);

        _body = new RichEditBox
        {
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Transparent),
            IsSpellCheckEnabled = true,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectionHighlightColor = new SolidColorBrush(Color.FromArgb(0x55, 0x00, 0x00, 0x00)),
            FontFamily = new FontFamily(BodyFontName),
            // Mirrors the macOS textContainerInset.
            Margin = new Thickness(15, 8, 15, 8),
        };
        foreach (var key in new[]
        {
            "TextControlBackground", "TextControlBackgroundPointerOver",
            "TextControlBackgroundFocused", "TextControlBackgroundDisabled",
            "TextControlBorderBrush", "TextControlBorderBrushPointerOver",
            "TextControlBorderBrushFocused", "TextControlBorderBrushDisabled",
        })
            _body.Resources[key] = new SolidColorBrush(Transparent);
        // TextChanging is synchronous and the document may not be modified
        // inside it, so content changes are styled from a queued pass.
        _body.TextChanging += OnTextChanging;
        _body.TextCompositionStarted += (_, _) => _composing = true;
        _body.TextCompositionEnded += (_, _) =>
        {
            _composing = false;
            if (_deferredFullPass) ApplyFullStyle();
        };
        _body.PreviewKeyDown += OnBodyPreviewKeyDown;
        _body.PointerPressed += OnBodyPointerPressed;
        Grid.SetRow(_body, 2);
        root.Children.Add(_body);

        var footer = BuildFooter();
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);

        _border = new Border
        {
            Background = new SolidColorBrush(Transparent),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x12, 0, 0, 0)),
            BorderThickness = new Thickness(0.5),
            Child = root,
        };
        Content = _border;
    }

    // MARK: Layout

    private Grid BuildHeader()
    {
        var header = new Grid { Padding = new Thickness(14, 0, 14, 0), ColumnSpacing = 2 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 5; i++)
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _title = new TextBlock
        {
            FontSize = 12.5,
            FontWeight = SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        header.Children.Add(_title);

        _saved = new TextBlock
        {
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
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
        _pin = MakeHeaderButton(_pinGlyph, 24);
        _pin.Click += (_, _) =>
        {
            if (_note is null || Notes is null) return;
            Notes.TogglePin(_note.Id);
            RefreshHeader();
            OnMutated?.Invoke();
        };
        Grid.SetColumn(_pin, 2);
        header.Children.Add(_pin);

        // Text direction (Automatic / LTR / RTL).
        _dirLabel = new TextBlock
        {
            FontSize = 11,
            FontWeight = SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _dirMenu = new MenuFlyout();
        var dir = MakeHeaderButton(_dirLabel, 30);
        dir.Flyout = _dirMenu;
        Grid.SetColumn(dir, 3);
        header.Children.Add(dir);

        // Toggle task checkbox on the caret's line.
        var task = MakeHeaderButton(new TextBlock
        {
            Text = "\u2611",
            FontSize = 13,
            FontWeight = SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        }, 24);
        task.Click += (_, _) => ToggleTaskLine();
        Grid.SetColumn(task, 4);
        header.Children.Add(task);

        var find = MakeHeaderButton(new TextBlock
        {
            Text = "\uE721", // Segoe MDL2: Search
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 12,
            FontWeight = SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        }, 24);
        find.Click += (_, _) => ToggleFind();
        Grid.SetColumn(find, 5);
        header.Children.Add(find);
        return header;
    }

    private (Grid Row, TextBox Box, TextBlock Count) BuildFindBar()
    {
        var row = new Grid
        {
            Height = 28,
            Padding = new Thickness(14, 0, 14, 0),
            ColumnSpacing = 6,
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var glyph = new TextBlock
        {
            Text = "\uE721",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(glyph);

        var box = new TextBox
        {
            FontSize = 12,
            PlaceholderText = "Find",
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Transparent),
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        box.KeyDown += OnFindKeyDown;
        Grid.SetColumn(box, 1);
        row.Children.Add(box);

        var count = new TextBlock
        {
            Text = "—",
            FontSize = 10.5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(count, 2);
        row.Children.Add(count);

        var up = MakeHeaderButton(new TextBlock
        {
            Text = "\uE70E", // ChevronUp
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 9,
            FontWeight = SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        }, 20);
        up.Click += (_, _) => FindNext(forward: false);
        Grid.SetColumn(up, 3);
        row.Children.Add(up);

        var down = MakeHeaderButton(new TextBlock
        {
            Text = "\uE70D", // ChevronDown
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 9,
            FontWeight = SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        }, 20);
        down.Click += (_, _) => FindNext(forward: true);
        Grid.SetColumn(down, 4);
        row.Children.Add(down);

        return (row, box, count);
    }

    private Grid BuildFooter()
    {
        var footer = new Grid { Padding = new Thickness(14, 0, 14, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
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
        _footerButtons.Add(MakeFooterButton("Pop out", 4, (_, _) =>
        {
            if (_note is null) return;
            OnDetachRequested?.Invoke(_note);
        }));
        _footerButtons.Add(MakeFooterButton("Close", 5, (_, _) => OnRequestCollapse?.Invoke()));
        foreach (var b in _footerButtons) footer.Children.Add(b);
        return footer;
    }

    private Button MakeHeaderButton(FrameworkElement content, double width)
    {
        return new Button
        {
            Content = content,
            Background = new SolidColorBrush(Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(2),
            Width = width,
            Height = 24,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    // MARK: Binding

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
            _note = note;
            RefreshHeader();
            if (_appliedToken != StyleToken)
            {
                Restyle();
                ApplyFullStyle();
            }
            return;
        }

        Flush();
        _note = note;
        _suppress = true;
        _body.Document.SetText(TextSetOptions.None, FromWire(note.Body ?? ""));
        _body.Document.GetText(TextGetOptions.None, out var raw);
        _text = raw;
        _suppress = false;
        _savedAt = note.Modified;
        Restyle();
        ApplyFullStyle();

        if (autofocus)
        {
            var end = Math.Max(0, _text.Length);
            _body.Document.Selection.SetRange(end, end);
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
        Notes.UpdateBody(_note.Id, _text.Replace('\r', '\n'));
        _savedAt = DateTime.UtcNow;
        RefreshHeader();
    }

    private void RefreshStyleIfBound()
    {
        if (_note is null) return;
        Restyle();
        ApplyFullStyle();
    }

    private string StyleToken => _note is null
        ? ""
        : $"{_note.Color}|{_bodyFontSize}|{_markdown}|{_note.TextDirection}";

    /// <summary>Line feeds in storage, carriage returns in TOM.</summary>
    private static string FromWire(string body) => body.Replace("\r\n", "\n").Replace('\n', '\r');

    // MARK: Text change → incremental styling + autosave

    private void OnTextChanging(RichEditBox sender, RichEditBoxTextChangingEventArgs e)
    {
        // Formatting-only changes (our own style passes) must not queue work,
        // or the pass would retrigger itself forever.
        if (!e.IsContentChanging || _suppress || _note is null) return;
        if (_styleQueued) return;
        _styleQueued = true;
        DispatcherQueue.TryEnqueue(ProcessPendingStyle);
    }

    private void ProcessPendingStyle()
    {
        _styleQueued = false;
        if (_note is null) return;
        _body.Document.GetText(TextGetOptions.None, out var raw);
        var old = _text;
        _text = raw;
        if (_composing)
        {
            // Attributes, layout, and selection writes are deferred while the
            // input method owns the text, exactly as the macOS coordinator does.
            _deferredFullPass = true;
            ScheduleSave();
            return;
        }
        var edit = DiffEdits(old, raw);
        foreach (var (start, length) in EditorStyleEngine.AffectedLineRanges(raw, new[] { edit }))
            StyleRange(start, length);
        ScheduleSave();
    }

    /// <summary>The single contiguous region that changed between two strings.</summary>
    private static (int Start, int Length) DiffEdits(string oldText, string newText)
    {
        var prefix = 0;
        while (prefix < oldText.Length && prefix < newText.Length &&
               oldText[prefix] == newText[prefix])
            prefix++;
        var suffix = 0;
        while (suffix < oldText.Length - prefix && suffix < newText.Length - prefix &&
               oldText[oldText.Length - 1 - suffix] == newText[newText.Length - 1 - suffix])
            suffix++;
        return (prefix, newText.Length - prefix - suffix);
    }

    private void ScheduleSave()
    {
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

    // MARK: Styling (TOM application of EditorStyleEngine spans)

    private void ApplyFullStyle()
    {
        if (_note is null) return;
        _deferredFullPass = false;
        StyleRange(0, Math.Max(0, _text.Length));
        _appliedToken = StyleToken;
    }

    private static bool IsCompletedTask(string line) => line.Length > 0 && line[0] == Tasks.Done;

    private void StyleRange(int start, int length)
    {
        if (_note is null || length <= 0) return;
        var ink = _note.Palette.InkArgb;
        var spans = EditorStyleEngine.Style(_text, start, length, ink, _basePt, _markdown, IsCompletedTask);

        var f = _body.Document.GetRange(start, start + length).CharacterFormat;
        f.Name = BodyFontName;
        f.Size = (float)_basePt;
        f.ForegroundColor = FromArgb(ink);
        f.Bold = FormatEffect.Off;
        f.Italic = FormatEffect.Off;
        f.Strikethrough = FormatEffect.Off;
        f.Underline = UnderlineType.None;
        f.BackgroundColor = Transparent;

        foreach (var s in spans)
        {
            if (s.Length <= 0) continue;
            var cf = _body.Document.GetRange(s.Start, s.Start + s.Length).CharacterFormat;
            if ((s.Flags & EditorSpanFlags.Bold) != 0) cf.Bold = FormatEffect.On;
            if ((s.Flags & EditorSpanFlags.Italic) != 0) cf.Italic = FormatEffect.On;
            if ((s.Flags & EditorSpanFlags.Strikethrough) != 0) cf.Strikethrough = FormatEffect.On;
            if ((s.Flags & EditorSpanFlags.Underline) != 0) cf.Underline = UnderlineType.Single;
            if ((s.Flags & EditorSpanFlags.CodeBackground) != 0)
                cf.BackgroundColor = FromArgb(EditorStyleEngine.WithAlpha(ink, 0.07));
            if (s.FontName is not null) cf.Name = s.FontName;
            if (s.SizePt > 0) cf.Size = (float)s.SizePt;
            if (s.ForeArgb != 0) cf.ForegroundColor = FromArgb(s.ForeArgb);
        }

        ApplyTextDirection(start, length);
    }

    /// <summary>
    /// Direction per paragraph: fixed directions apply to every paragraph;
    /// <c>Automatic</c> resolves each paragraph independently from its first
    /// strong character, so one note can hold English and Arabic paragraphs.
    /// </summary>
    private void ApplyTextDirection(int start, int length)
    {
        if (_note is null) return;
        var direction = _note.TextDirection;
        var end = start + length;
        var pos = start;
        while (pos < end)
        {
            var (ls, ll) = EditorStyleEngine.LineRangeContaining(_text, pos);
            if (ll <= 0) break;
            var line = _text.Substring(ls, Math.Min(ll, _text.Length - ls));
            var rtl = direction switch
            {
                NoteTextDirection.RightToLeft => true,
                NoteTextDirection.LeftToRight => false,
                _ => EditorStyleEngine.FirstStrongIsRtl(line),
            };
            var para = _body.Document.GetRange(ls, ls + ll).ParagraphFormat;
            para.RightToLeft = rtl ? FormatEffect.On : FormatEffect.Off;
            para.Alignment = rtl ? ParagraphAlignment.Right : ParagraphAlignment.Left;
            pos = ls + ll;
        }
    }

    private void Restyle()
    {
        if (_note is null) return;
        var ink = FromArgb(_note.Palette.InkArgb);

        _border.Background = new SolidColorBrush(FromArgb(_note.Palette.PaperArgb));
        _body.Foreground = new SolidColorBrush(ink);
        _body.FontSize = _bodyFontSize;
        _basePt = _bodyFontSize * 72.0 / 96.0;

        foreach (var b in _footerButtons)
        {
            b.Foreground = new SolidColorBrush(WithAlpha(ink, 0.72));
            b.Background = new SolidColorBrush(WithAlpha(ink, 0.08));
        }
        _findRow.Background = new SolidColorBrush(FromArgb(
            EditorStyleEngine.WithAlpha(_note.Palette.DashArgb, 0.12)));

        BuildSwatches();
        RebuildDirectionMenu();
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
        _dirLabel.Text = _note.TextDirection switch
        {
            NoteTextDirection.LeftToRight => "→",
            NoteTextDirection.RightToLeft => "←",
            _ => "Auto",
        };
        _dirLabel.Foreground = new SolidColorBrush(WithAlpha(ink, 0.5));
        _findCount.Foreground = new SolidColorBrush(WithAlpha(ink, 0.45));
    }

    private void RebuildDirectionMenu()
    {
        if (_note is null) return;
        _dirMenu.Items.Clear();
        foreach (NoteTextDirection option in Enum.GetValues<NoteTextDirection>())
        {
            var opt = option;
            var title = opt switch
            {
                NoteTextDirection.LeftToRight => "Left to Right",
                NoteTextDirection.RightToLeft => "Right to Left",
                _ => "Automatic",
            };
            var item = new MenuFlyoutItem
            {
                Text = (_note.TextDirection == opt ? "✓ " : "") + title,
            };
            item.Click += (_, _) =>
            {
                if (_note is null || Notes is null) return;
                Notes.SetTextDirection(_note.Id, opt);
                OnMutated?.Invoke();
                Restyle();
                ApplyFullStyle();
            };
            _dirMenu.Items.Add(item);
        }
    }

    // MARK: Tasks

    /// <summary>Turn the caret's line into a task, or strip the checkbox off it.</summary>
    private void ToggleTaskLine()
    {
        if (_note is null) return;
        var caret = Math.Min(_body.Document.Selection.StartPosition, _text.Length);
        var (ls, ll) = EditorStyleEngine.LineRangeContaining(_text, caret);
        var line = _text.Substring(ls, Math.Min(ll, _text.Length - ls)).TrimEnd('\r', '\n');
        if (Tasks.IsTask(line))
        {
            var len = ls + 1 < _text.Length && _text[ls + 1] == ' ' ? 2 : 1;
            _body.Document.GetRange(ls, ls + len).Text = "";
        }
        else
        {
            _body.Document.GetRange(ls, ls).Text = Tasks.OpenPrefix;
        }
    }

    /// <summary>Return starts the next task; on an empty one, ends the list.</summary>
    private bool HandleTaskEnter()
    {
        var caret = Math.Min(_body.Document.Selection.StartPosition, _text.Length);
        var (ls, ll) = EditorStyleEngine.LineRangeContaining(_text, caret);
        var line = _text.Substring(ls, Math.Min(ll, _text.Length - ls)).TrimEnd('\r', '\n');
        if (!Tasks.IsTask(line)) return false;

        if (Tasks.Stripped(line).Length == 0)
        {
            var clear = Math.Min(ll, _text.Length - ls);
            _body.Document.GetRange(ls, ls + clear).Text = "";
            return true;
        }
        _body.Document.Selection.Text = "\r" + Tasks.OpenPrefix;
        return true;
    }

    private bool ToggleCheckboxAt(int index)
    {
        var (ls, ll) = EditorStyleEngine.LineRangeContaining(_text, index);
        var lineLen = Math.Min(ll, _text.Length - ls);
        if (lineLen <= 0) return false;
        var first = _text[ls];
        if (first != Tasks.Open && first != Tasks.Done) return false;
        if (index > ls + 1) return false;
        var flipped = first == Tasks.Open ? Tasks.Done.ToString() : Tasks.Open.ToString();
        _body.Document.GetRange(ls, ls + 1).Text = flipped;
        return true;
    }

    // MARK: Pointer — checkbox clicks and Ctrl+click links

    private void OnBodyPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_note is null) return;
        var point = e.GetCurrentPoint(_body);
        int index;
        try
        {
            var range = _body.Document.GetRangeFromPoint(point.Position, PointOptions.ClientCoordinates);
            index = Math.Clamp(range.StartPosition, 0, Math.Max(0, _text.Length - 1));
        }
        catch
        {
            return;
        }

        if (e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control))
        {
            if (OpenLinkAt(index)) e.Handled = true;
            return;
        }
        if (point.Properties.IsLeftButtonPressed && ToggleCheckboxAt(index))
            e.Handled = true;
    }

    /// <summary>Ctrl+click follows a link, like ⌘-click on macOS. Only vetted
    /// http/https/mailto destinations ever open.</summary>
    private bool OpenLinkAt(int index)
    {
        var (ls, ll) = EditorStyleEngine.LineRangeContaining(_text, index);
        var lineLen = Math.Min(ll, _text.Length - ls);
        if (lineLen <= 0) return false;
        var line = _text.Substring(ls, lineLen);
        foreach (var (start, length, url) in EditorStyleEngine.LinksIn(line))
        {
            if (url is null) continue;
            if (index >= ls + start && index < ls + start + length)
            {
                _ = Launcher.LaunchUriAsync(new Uri(url));
                return true;
            }
        }
        return false;
    }

    // MARK: Keyboard

    private void OnBodyPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // WinUI 3's KeyRoutedEventArgs carries no modifiers; read them from the
        // keyboard source instead.
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (e.Key == VirtualKey.Escape)
        {
            if (_findRow.Visibility == Visibility.Visible)
                CloseFind();
            else
                OnRequestCollapse?.Invoke();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Enter && !shift)
        {
            if (HandleTaskEnter()) e.Handled = true;
        }
        else if (ctrl && e.Key == VirtualKey.F)
        {
            ToggleFind();
            e.Handled = true;
        }
        else if (ctrl && e.Key == VirtualKey.T)
        {
            ToggleTaskLine();
            e.Handled = true;
        }
    }

    // MARK: Find bar

    private void ToggleFind()
    {
        if (_findRow.Visibility == Visibility.Visible) CloseFind();
        else
        {
            _findRow.Visibility = Visibility.Visible;
            _findBox.Focus(FocusState.Programmatic);
            Recount();
        }
    }

    private void CloseFind()
    {
        _findRow.Visibility = Visibility.Collapsed;
        _body.Focus(FocusState.Programmatic);
    }

    private void OnFindKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            FindNext(forward: true);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            CloseFind();
            e.Handled = true;
        }
    }

    private void Recount()
    {
        var q = _findBox.Text ?? "";
        var count = 0;
        if (q.Length > 0)
        {
            var idx = 0;
            while ((idx = _text.IndexOf(q, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                count++;
                idx += q.Length;
            }
        }
        _findCount.Text = count == 0 ? "—" : count.ToString();
    }

    private void FindNext(bool forward)
    {
        var q = _findBox.Text ?? "";
        if (q.Length == 0 || _text.Length == 0) return;
        Recount();

        int found;
        if (forward)
        {
            var from = Math.Min(_body.Document.Selection.EndPosition, _text.Length);
            found = _text.IndexOf(q, from, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
                found = _text.IndexOf(q, 0, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            found = FindLastBefore(Math.Min(_body.Document.Selection.StartPosition, _text.Length));
            if (found < 0)
                found = FindLastBefore(_text.Length + q.Length);
        }
        if (found < 0) return;

        var range = _body.Document.GetRange(found, found + q.Length);
        range.ScrollIntoView(PointOptions.Start);
        _body.Document.Selection.SetRange(found, found + q.Length);
    }

    private int FindLastBefore(int limit)
    {
        var q = _findBox.Text;
        for (var i = Math.Min(limit, _text.Length) - q.Length; i >= 0; i--)
        {
            if (_text.AsSpan(i, q.Length).Equals(q, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    // MARK: Footer

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
                ApplyFullStyle();
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
