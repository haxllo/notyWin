using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NotyWin.App.Models;
using NotyWin.Rendering;
using Color = System.Windows.Media.Color;

namespace NotyWin.App.Deck;

/// <summary>
/// WPF note editor using RichTextBox (FlowDocument-based).
/// Replaces the WinUI 3 NoteEditorControl that used RichEditBox + TOM.
/// </summary>
public sealed class NoteEditorControl : UserControl
{
    public NoteList? Notes { get; set; }
    public Action? OnRequestCollapse { get; set; }
    public Action? OnMutated { get; set; }
    public Action<Note>? OnDetachRequested { get; set; }

    public double BodyFontSize { get; set; } = 13.5;
    public bool MarkdownEnabled { get; set; } = true;
    public bool OnRight { get; set; } = true;

    private Note? _note;
    private DateTime? _savedAt;
    private bool _suppress;
    private string _text = "";
    private DispatcherTimer? _saveTimer;

    private readonly Border _border;
    private readonly RichTextBox _body;
    private readonly TextBlock _title;
    private readonly TextBlock _saved;

    public NoteEditorControl()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });

        // Header
        var header = new Grid { Margin = new Thickness(14, 0, 14, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _title = new TextBlock
        {
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        header.Children.Add(_title);

        _saved = new TextBlock
        {
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
            Opacity = 0.42,
        };
        Grid.SetColumn(_saved, 1);
        header.Children.Add(_saved);

        var closeBtn = new Button
        {
            Content = "Close",
            FontSize = 10.5,
            Padding = new Thickness(8, 2, 8, 2),
            BorderThickness = new Thickness(0),
        };
        closeBtn.Click += (_, _) => OnRequestCollapse?.Invoke();
        Grid.SetColumn(closeBtn, 3);
        header.Children.Add(closeBtn);

        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // Body
        _body = new RichTextBox
        {
            AcceptsReturn = true,
            AcceptsTab = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FontSize = BodyFontSize,
            Padding = new Thickness(15, 8, 15, 8),
            IsReadOnly = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        _body.TextChanged += OnTextChanged;
        _body.PreviewKeyDown += OnBodyKeyDown;
        Grid.SetRow(_body, 1);
        root.Children.Add(_body);

        // Footer
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(14, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var archiveBtn = new Button { Content = "Archive", FontSize = 10.5, Padding = new Thickness(9, 2, 9, 2), Margin = new Thickness(0, 0, 7, 0) };
        archiveBtn.Click += (_, _) =>
        {
            if (_note is null || Notes is null) return;
            Notes.SetArchived(_note.Id, true);
            OnMutated?.Invoke();
            OnRequestCollapse?.Invoke();
        };
        var deleteBtn = new Button { Content = "Delete", FontSize = 10.5, Padding = new Thickness(9, 2, 9, 2), Margin = new Thickness(0, 0, 7, 0) };
        deleteBtn.Click += (_, _) =>
        {
            if (_note is null || Notes is null) return;
            Notes.Delete(_note.Id, TimeSpan.FromSeconds(10));
            OnMutated?.Invoke();
            OnRequestCollapse?.Invoke();
        };
        var popBtn = new Button { Content = "Pop out", FontSize = 10.5, Padding = new Thickness(9, 2, 9, 2), Margin = new Thickness(0, 0, 7, 0) };
        popBtn.Click += (_, _) => { if (_note is not null) OnDetachRequested?.Invoke(_note); };
        var closeFooterBtn = new Button { Content = "Close", FontSize = 10.5, Padding = new Thickness(9, 2, 9, 2) };
        closeFooterBtn.Click += (_, _) => OnRequestCollapse?.Invoke();
        footer.Children.Add(archiveBtn);
        footer.Children.Add(deleteBtn);
        footer.Children.Add(popBtn);
        footer.Children.Add(closeFooterBtn);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        _border = new Border
        {
            Child = root,
            CornerRadius = new CornerRadius(14, 0, 0, 14),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x12, 0, 0, 0)),
            BorderThickness = new Thickness(0.5),
        };
        Content = _border;
    }

    public void SetNote(Note note, bool autofocus)
    {
        if (_note?.Id == note.Id)
        {
            _note = note;
            RefreshHeader();
            return;
        }

        Flush();
        _note = note;
        _suppress = true;
        _body.Document.Blocks.Clear();
        _body.Document.Blocks.Add(new System.Windows.Documents.Paragraph(
            new System.Windows.Documents.Run(note.Body ?? "")));
        _text = note.Body ?? "";
        _suppress = false;
        _savedAt = note.Modified;
        Restyle();

        if (autofocus)
            _body.Focus();
    }

    public void Flush()
    {
        _saveTimer?.Stop();
        Commit();
    }

    private void Commit()
    {
        if (_note is null || Notes is null) return;
        Notes.UpdateBody(_note.Id, _text);
        _savedAt = DateTime.UtcNow;
        RefreshHeader();
    }

    private void OnTextChanged(object sender, RoutedEventArgs e)
    {
        if (_suppress || _note is null) return;
        // Extract plain text from the FlowDocument.
        var range = new System.Windows.Documents.TextRange(
            _body.Document.ContentStart,
            _body.Document.ContentEnd);
        _text = range.Text.TrimEnd('\r', '\n');
        ScheduleSave();
    }

    private void ScheduleSave()
    {
        if (_saveTimer is null)
        {
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Commit(); };
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void OnBodyKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            OnRequestCollapse?.Invoke();
            e.Handled = true;
        }
        else if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            // Find bar — not implemented yet.
            e.Handled = true;
        }
        else if (e.Key == Key.T && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ToggleTaskLine();
            e.Handled = true;
        }
    }

    private void ToggleTaskLine()
    {
        if (_note is null) return;
        var caret = _body.CaretPosition;
        var line = caret.GetLineStartPosition(0);
        if (line is null) return;
        var lineEnd = line.GetLineStartPosition(1) ?? _body.Document.ContentEnd;
        var lineRange = new System.Windows.Documents.TextRange(line, lineEnd);
        var lineText = lineRange.Text.TrimEnd('\r', '\n');
        if (Tasks.IsTask(lineText))
        {
            var prefix = lineText.Length > 1 && lineText[1] == ' ' ? 2 : 1;
            lineRange.Text = lineText.Substring(prefix);
        }
        else
        {
            lineRange.Text = Tasks.OpenPrefix + lineText;
        }
    }

    private void Restyle()
    {
        if (_note is null) return;
        var paper = ColorFromArgb(_note.Palette.PaperArgb);
        var ink = ColorFromArgb(_note.Palette.InkArgb);
        _border.Background = new SolidColorBrush(paper);
        _body.Foreground = new SolidColorBrush(ink);
        _body.FontSize = BodyFontSize;
        RefreshHeader();
    }

    private void RefreshHeader()
    {
        if (_note is null) return;
        var ink = ColorFromArgb(_note.Palette.InkArgb);
        _title.Text = _note.DisplayTitle("Untitled");
        _title.Foreground = new SolidColorBrush(Color.FromArgb(
            (byte)(0.92 * 255), ink.R, ink.G, ink.B));
        _saved.Text = SavedLabel(_savedAt);
        _saved.Foreground = new SolidColorBrush(Color.FromArgb(
            (byte)(0.42 * 255), ink.R, ink.G, ink.B));
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

    private static Color ColorFromArgb(int argb) => Color.FromArgb(
        (byte)((argb >> 24) & 0xFF), (byte)((argb >> 16) & 0xFF),
        (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF));
}
