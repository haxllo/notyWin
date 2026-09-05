using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NotyWin.App.Models;

namespace NotyWin.App;

/// <summary>
/// WPF Library window (All Notes / Archive). Replaces the WinUI 3 LibraryWindow.
/// </summary>
public sealed class LibraryWindow : Window
{
    private readonly NoteList _notes;
    private Note? _selected;
    private bool _suppress;
    private readonly StackPanel _noteList = new();
    private readonly TextBox _searchBox;
    private readonly TextBlock _countLabel;
    private readonly TextBlock _detailTitle;
    private readonly TextBlock _detailMeta;
    private readonly TextBox _detailEditor;
    private readonly Grid _detailHeader;

    public LibraryWindow(NoteList notes, object? _ = null)
    {
        _notes = notes;

        Title = "Noty — Library";
        Width = 940;
        Height = 580;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Sidebar
        var sidebar = new Grid { Background = new SolidColorBrush(Color.FromArgb(0xF3, 0xF3, 0xF3, 0xF3)) };
        sidebar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sidebar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sidebar.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        sidebar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var modePicker = new ComboBox { Margin = new Thickness(12, 38, 12, 8), SelectedIndex = 0 };
        modePicker.Items.Add("All Notes");
        modePicker.Items.Add("Archive");
        Grid.SetRow(modePicker, 0);
        sidebar.Children.Add(modePicker);

        _searchBox = new TextBox { Margin = new Thickness(12, 4, 12, 8) };
        _searchBox.GotFocus += (_, _) => { if (_searchBox.Text == "Search…") _searchBox.Text = ""; };
        _searchBox.LostFocus += (_, _) => { if (string.IsNullOrWhiteSpace(_searchBox.Text)) _searchBox.Text = "Search…"; };
        _searchBox.Text = "Search…";
        _searchBox.TextChanged += (_, _) => RefreshList();
        Grid.SetRow(_searchBox, 1);
        sidebar.Children.Add(_searchBox);

        var scroll = new ScrollViewer { Content = _noteList };
        Grid.SetRow(scroll, 2);
        sidebar.Children.Add(scroll);

        var bottomBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 6, 12, 6) };
        var newBtn = new Button { Content = "+ New Note" };
        newBtn.Click += (_, _) => { _notes.Create(); RefreshList(); };
        _countLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
        bottomBar.Children.Add(newBtn);
        bottomBar.Children.Add(_countLabel);
        Grid.SetRow(bottomBar, 3);
        sidebar.Children.Add(bottomBar);

        Grid.SetColumn(sidebar, 0);
        root.Children.Add(sidebar);

        // Detail pane
        var detail = new Grid();
        detail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        detail.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _detailHeader = new Grid
        {
            Margin = new Thickness(16, 10, 16, 10),
            Visibility = Visibility.Collapsed,
        };
        _detailHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _detailHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _detailHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var colorDot = new Border
        {
            Width = 12, Height = 12,
            CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        Grid.SetColumn(colorDot, 0);
        _detailHeader.Children.Add(colorDot);

        var titleStack = new StackPanel();
        _detailTitle = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold };
        _detailMeta = new TextBlock { FontSize = 10.5, Opacity = 0.6 };
        titleStack.Children.Add(_detailTitle);
        titleStack.Children.Add(_detailMeta);
        Grid.SetColumn(titleStack, 1);
        _detailHeader.Children.Add(titleStack);

        var archiveBtn = new Button { Content = "Archive", Margin = new Thickness(6, 0, 0, 0) };
        archiveBtn.Click += (_, _) => { if (_selected is not null) { _notes.SetArchived(_selected.Id, true); RefreshList(); } };
        Grid.SetColumn(archiveBtn, 2);
        _detailHeader.Children.Add(archiveBtn);

        Grid.SetRow(_detailHeader, 0);
        detail.Children.Add(_detailHeader);

        _detailEditor = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(16, 8, 16, 8),
            FontSize = 13,
        };
        _detailEditor.TextChanged += (_, _) =>
        {
            if (_suppress || _selected is null) return;
            _notes.UpdateBody(_selected.Id, _detailEditor.Text);
        };
        Grid.SetRow(_detailEditor, 1);
        detail.Children.Add(_detailEditor);

        Grid.SetColumn(detail, 1);
        root.Children.Add(detail);

        Content = root;
        RefreshList();
    }

    private void RefreshList()
    {
        var q = _searchBox.Text ?? "";
        var src = _notes.Active;
        var filtered = string.IsNullOrWhiteSpace(q) ? src
            : src.Where(n =>
                (n.Title?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (n.Body?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));

        _noteList.Children.Clear();
        var count = 0;
        foreach (var n in filtered)
        {
            count++;
            var row = BuildNoteRow(n);
            _noteList.Children.Add(row);
        }
        _countLabel.Text = $"{count} note{(count == 1 ? "" : "s")}";
    }

    private FrameworkElement BuildNoteRow(Note n)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 4, 6, 4) };
        var bar = new Border
        {
            Width = 4, Height = 30,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(ColorFromArgb(n.Palette.DashArgb)),
            Margin = new Thickness(0, 0, 8, 0),
        };
        row.Children.Add(bar);
        var title = new TextBlock
        {
            Text = n.DisplayTitle("Untitled"),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(title);

        row.MouseLeftButtonDown += (_, _) => SelectNote(n);
        return row;
    }

    private void SelectNote(Note n)
    {
        _selected = n;
        _detailHeader.Visibility = Visibility.Visible;
        _detailTitle.Text = n.DisplayTitle("Untitled");
        _detailMeta.Text = $"Edited {TimeAgo(n.Modified)}";
        _suppress = true;
        _detailEditor.Text = n.Body ?? "";
        _suppress = false;
    }

    private static string TimeAgo(DateTime t)
    {
        var span = DateTime.UtcNow - t;
        if (span.TotalSeconds < 60) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
        return t.ToLocalTime().ToString("MMM d");
    }

    private static Color ColorFromArgb(int argb) => Color.FromArgb(
        (byte)((argb >> 24) & 0xFF), (byte)((argb >> 16) & 0xFF),
        (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF));
}
