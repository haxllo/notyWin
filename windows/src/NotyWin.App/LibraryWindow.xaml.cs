using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NotyWin.App.Deck;
using NotyWin.App.Models;
using Color = Windows.UI.Color;

namespace NotyWin.App;

/// <summary>
/// All Notes / Archive browser. Mirrors Sources/LibraryWindow.swift.
/// Left: search field + segmented All/Archive + list of notes.
/// Right: detail editor (title derived from body line 1, editable body).
/// </summary>
public sealed partial class LibraryWindow : Window
{
    private readonly NoteList _notes;
    private readonly DeckManager _manager;
    private bool _archiveMode;
    private Note? _selected;
    private bool _suppress;

    public LibraryWindow(NoteList notes, DeckManager manager)
    {
        InitializeComponent();
        _notes = notes;
        _manager = manager;
        ModePicker.SelectedIndex = 0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(940, 580));
        ResizeList();
    }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        _archiveMode = ModePicker.SelectedIndex == 1;
        ArchiveBtn.Content = _archiveMode ? "Restore" : "Archive";
        ResizeList();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ResizeList();

    private void ResizeList()
    {
        var q = SearchBox.Text ?? "";
        var src = _archiveMode ? _notes.Archived : _notes.Active;
        var filtered = string.IsNullOrWhiteSpace(q) ? src
            : src.Where(n =>
                (n.Title?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (n.Body?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));

        var items = filtered.Select(n => new NoteRow
        {
            Id = n.Id,
            Title = n.DisplayTitle("Untitled"),
            Subtitle = BuildSubtitle(n),
            ColorBarBrush = new SolidColorBrush(Color.FromArgb(
                (byte)((n.Palette.DashArgb >> 24) & 0xFF), (byte)((n.Palette.DashArgb >> 16) & 0xFF),
                (byte)((n.Palette.DashArgb >> 8) & 0xFF), (byte)(n.Palette.DashArgb & 0xFF))),
            Note = n,
        }).ToList();
        NoteList.ItemsSource = items;
        CountLabel.Text = $"{items.Count} note{(items.Count == 1 ? "" : "s")}";
    }

    private static string BuildSubtitle(Note n)
    {
        var bits = new List<string>();
        var ts = n.Modified;
        bits.Add(TimeAgo(ts));
        var progress = Tasks.Progress(n.Body ?? "");
        if (progress is { Total: > 0 } p)
            bits.Add($"{p.Done}/{p.Total}");
        return string.Join(" · ", bits);
    }

    private static string TimeAgo(DateTime t)
    {
        var span = DateTime.UtcNow - t;
        if (span.TotalSeconds < 60) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
        return t.ToLocalTime().ToString("MMM d");
    }

    private void OnNoteSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        if (NoteList.SelectedItem is not NoteRow row)
        {
            DetailHeader.Visibility = Visibility.Collapsed;
            _selected = null;
            return;
        }
        _selected = row.Note;
        DetailHeader.Visibility = Visibility.Visible;
        DetailColorDot.Fill = row.ColorBarBrush;
        DetailTitle.Text = row.Title;
        DetailMeta.Text = $"Edited {TimeAgo(row.Note.Modified)}";
        _suppress = true;
        DetailEditor.Text = FromWire(row.Note.Body ?? "");
        _suppress = false;
    }

    private void OnDetailTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress || _selected is null) return;
        var text = DetailEditor.Text.Replace("\r\n", "\n");
        _notes.UpdateBody(_selected.Id, text);
    }

    private void OnNewNoteClicked(object sender, RoutedEventArgs e)
    {
        var n = _notes.Create();
        ResizeList();
        SelectById(n.Id);
    }

    private void OnArchiveClicked(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        if (_archiveMode) _notes.SetArchived(_selected.Id, false);
        else _notes.SetArchived(_selected.Id, true);
        ResizeList();
    }

    private void OnDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        _notes.Delete(_selected.Id, TimeSpan.FromSeconds(10));
        ResizeList();
    }

    private void OnExportClicked(object sender, RoutedEventArgs e)
    {
        // Open file picker, write markdown for each note.
        var picker = new Windows.Storage.Pickers.FileSavePicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.SuggestedFileName = "noty-export";
        picker.FileTypeChoices.Add("Markdown", new List<string> { ".md" });
        var file = picker.PickSaveFileAsync().AsTask().Result;
        if (file is null) return;
        var sb = new System.Text.StringBuilder();
        foreach (var n in _notes.Notes)
        {
            var title = n.DisplayTitle("Untitled");
            sb.AppendLine($"# {title}");
            sb.AppendLine();
            sb.AppendLine(n.Body ?? "");
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
        }
        System.IO.File.WriteAllText(file.Path, sb.ToString());
    }

    private void SelectById(string id)
    {
        _suppress = true;
        foreach (var item in NoteList.Items)
        {
            if (item is NoteRow r && r.Note.Id == id)
            {
                NoteList.SelectedItem = item;
                break;
            }
        }
        _suppress = false;
    }

    private static string FromWire(string body) => body.Replace("\r\n", "\n").Replace('\n', '\r');

    public sealed class NoteRow
    {
        public required string Id { get; init; }
        public required string Title { get; init; }
        public required string Subtitle { get; init; }
        public required Brush ColorBarBrush { get; init; }
        public required Note Note { get; init; }
    }
}
