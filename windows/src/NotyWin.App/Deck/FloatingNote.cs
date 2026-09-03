using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using NotyWin.App.Models;
using Windows.System;
using Color = Windows.UI.Color;

namespace NotyWin.App.Deck;

/// <summary>
/// A note detached from the deck by dragging its gutter past 40pt. Becomes a
/// draggable, resizable sticky that auto-tucks back to the edge when idle.
/// Mirrors Sources/FloatingNote.swift.
///
/// One at a time — pulling out a second tucks the first.
/// </summary>
public sealed class FloatingNote : IDisposable
{
    private Microsoft.UI.Xaml.Window? _window;
    private AppWindow? _appWindow;
    private nint _hwnd;
    private readonly NoteList _notes;
    private readonly ISettingsStore _settings;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;
    private readonly NoteEditorControl _editor;
    private readonly Border _border;
    private readonly Grid _root;
    private string? _noteId;
    private bool _disposed;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _idleTimer;
    private DateTime _lastActivity;

    public static FloatingNote? Current { get; private set; }

    public FloatingNote(NoteList notes, ISettingsStore settings, Microsoft.UI.Dispatching.DispatcherQueue dispatcher)
    {
        _notes = notes;
        _settings = settings;
        _dispatcher = dispatcher;

        _editor = new NoteEditorControl();
        _editor.Notes = notes;
        _editor.OnMutated = () => _lastActivity = DateTime.UtcNow;
        _editor.OnRequestCollapse = () => Tuck();

        _border = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
        };

        _root = new Grid();
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.Children.Add(MakeHeader());
        Grid.SetRow(_editor, 1);
        _root.Children.Add(_editor);
        _border.Child = _root;
    }

    private Grid MakeHeader()
    {
        var header = new Grid { Padding = new Thickness(12, 0, 12, 0), Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new Ellipse
        {
            Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        dot.SetBinding(Ellipse.FillProperty, new Microsoft.UI.Xaml.Data.Binding { Path = new PropertyPath("DashBrush") });
        header.Children.Add(dot);

        var title = new TextBlock
        {
            FontSize = 12.5, FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 },
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        title.SetBinding(TextBlock.TextProperty, new Microsoft.UI.Xaml.Data.Binding { Path = new PropertyPath("Title") });
        Grid.SetColumn(title, 1);
        header.Children.Add(title);

        var pinBtn = new Button
        {
            Content = "\uE718", FontFamily = new FontFamily("Segoe MDL2 Assets"),
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)), BorderThickness = new Thickness(0),
            Width = 24, Height = 24, FontSize = 12,
        };
        pinBtn.Click += (_, _) => { if (_noteId is not null) _notes.TogglePin(_noteId); };
        Grid.SetColumn(pinBtn, 2);
        header.Children.Add(pinBtn);

        var closeBtn = new Button
        {
            Content = "\uE894", FontFamily = new FontFamily("Segoe MDL2 Assets"),
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)), BorderThickness = new Thickness(0),
            Width = 24, Height = 24, FontSize = 10,
        };
        closeBtn.Click += (_, _) => Tuck();
        Grid.SetColumn(closeBtn, 3);
        header.Children.Add(closeBtn);

        return header;
    }

    /// <summary>Show this floating note for the given note id. Tucks any other
    /// floating note first (one at a time).</summary>
    public void ShowFor(string noteId)
    {
        Current?.Tuck();
        _noteId = noteId;
        var n = _notes.ById(noteId);
        if (n is null) return;

        if (_window is null) CreateWindow();

        _editor.MarkdownEnabled = _settings.Load().MarkdownStyling;
        _editor.BodyFontSize = _settings.Load().NoteFontSize;
        _editor.SetNote(n, autofocus: false);
        RestyleForNote(n);

        var (cx, cy) = DeckWindow.CursorPos();
        PositionNear(cx, cy);

        _window.Activate();
        Current = this;
        _lastActivity = DateTime.UtcNow;
        StartIdleWatch();
    }

    public void Tuck()
    {
        if (_window is null) return;
        StopIdleWatch();
        _editor.Flush();
        _window.Close();
        Current = null;
        _noteId = null;
        _window = null;
        _appWindow = null;
    }

    private void CreateWindow()
    {
        _window = new Microsoft.UI.Xaml.Window { Title = "Noty — Floating" };
        _window.SystemBackdrop = null;
        _appWindow = _window.AppWindow;
        var presenter = (OverlappedPresenter)_appWindow.Presenter;
        presenter.SetBorderAndTitleBar(true, false);
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = true;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        SetWindowLongPtr(_hwnd, GWL_STYLE, (IntPtr)(WS_POPUP | WS_VISIBLE));
        var ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64() | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, (IntPtr)ex);

        var w = _settings.Load().FloatingNoteWidth;
        var h = _settings.Load().FloatingNoteHeight;
        _appWindow.Resize(new Windows.Graphics.SizeInt32((int)w, (int)h));

        _window.Content = _border;

        _window.Content.KeyDown += OnKeyDown;
    }

    private void RestyleForNote(Note n)
    {
        var paper = Color.FromArgb(
            (byte)((n.Palette.PaperArgb >> 24) & 0xFF), (byte)((n.Palette.PaperArgb >> 16) & 0xFF),
            (byte)((n.Palette.PaperArgb >> 8) & 0xFF), (byte)(n.Palette.PaperArgb & 0xFF));
        var ink = Color.FromArgb(
            (byte)((n.Palette.InkArgb >> 24) & 0xFF), (byte)((n.Palette.InkArgb >> 16) & 0xFF),
            (byte)((n.Palette.InkArgb >> 8) & 0xFF), (byte)(n.Palette.InkArgb & 0xFF));
        _border.Background = new SolidColorBrush(paper);
        _border.BorderBrush = new SolidColorBrush(Color.FromArgb(0x24, ink.R, ink.G, ink.B));
    }

    private void PositionNear(int cx, int cy)
    {
        var displays = DisplayEnumerator.Snapshot();
        var display = DisplayEnumerator.DisplayAtPoint(cx, cy, displays);
        if (display is null) return;
        var dpi = (double)GetDpiForWindow(_hwnd) / 96.0;
        var w = _settings.Load().FloatingNoteWidth;
        var h = _settings.Load().FloatingNoteHeight;
        _appWindow!.MoveAndResize(new Windows.Graphics.RectInt32(
            Math.Max((int)display.Value.FullLeft, cx - 100),
            Math.Max((int)display.Value.FullTop, cy - 50),
            (int)w, (int)h));
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            Tuck();
            e.Handled = true;
        }
    }

    // Idle: tuck if not pinned, not active, no cursor inside.
    private void StartIdleWatch()
    {
        if (_idleTimer is not null) return;
        _idleTimer = _dispatcher.CreateTimer();
        _idleTimer.Interval = TimeSpan.FromSeconds(2);
        _idleTimer.IsRepeating = true;
        _idleTimer.Tick += (_, _) =>
        {
            if (_noteId is null) return;
            var n = _notes.ById(_noteId);
            if (n is null || n.Pinned) return;
            var span = DateTime.UtcNow - _lastActivity;
            if (span.TotalSeconds > 60)
                _dispatcher.TryEnqueue(Tuck);
        };
        _idleTimer.Start();
    }

    private void StopIdleWatch()
    {
        _idleTimer?.Stop();
        _idleTimer = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopIdleWatch();
        if (Current == this) Current = null;
        _window?.Close();
        _window = null;
    }

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);
}
