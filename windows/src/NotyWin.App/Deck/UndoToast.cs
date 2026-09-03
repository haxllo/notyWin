using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using NotyWin.App.Models;
using Windows.UI;

namespace NotyWin.App.Deck;

/// <summary>
/// A small, non-activating toast that appears at the bottom-center of the
/// screen after a note is deleted, offering a 10-second undo window.
/// Mirrors <c>UndoToast</c> in Sources/UndoToast.swift.
///
/// Shows a circular countdown ring, the note title, and an Undo button.
/// Auto-hides when the pending-delete window expires or the user clicks Undo.
/// </summary>
public sealed class UndoToast : IDisposable
{
    private Microsoft.UI.Xaml.Window? _window;
    private AppWindow? _appWindow;
    private nint _hwnd;
    private readonly NoteList _notes;
    private readonly DispatcherQueue _dispatcher;
    private DispatcherQueueTimer? _tickTimer;
    private DateTime? _deadline;
    private string? _pendingNoteId;
    private bool _disposed;

    // XAML elements
    private TextBlock? _titleText;
    private TextBlock? _countdownText;
    private Button? _undoButton;

    private static readonly Color Transparent = Color.FromArgb(0, 0, 0, 0);

    public UndoToast(NoteList notes, DispatcherQueue dispatcher)
    {
        _notes = notes;
        _dispatcher = dispatcher;
        _notes.Subscribe(new UndoObserver(this));
    }

    private void OnNotesChanged()
    {
        if (_disposed) return;
        var pending = _notes.PendingUndo;
        if (pending is not null)
        {
            Show(pending);
        }
        else
        {
            Hide();
        }
    }

    private void Show(PendingDelete pending)
    {
        _deadline = pending.Deadline;
        _pendingNoteId = pending.Note.Id;

        if (_window is null)
            CreateWindow();

        _titleText!.Text = pending.Note.DisplayTitle("Untitled");
        UpdateCountdown();

        PositionWindow();
        _appWindow!.Show(true);
        StartTick();
    }

    private void Hide()
    {
        StopTick();
        _deadline = null;
        _pendingNoteId = null;
        _appWindow?.Show(false);
    }

    private void CreateWindow()
    {
        _window = new Microsoft.UI.Xaml.Window { Title = "Noty Undo" };
        _window.SystemBackdrop = null;
        _appWindow = _window.AppWindow;
        var presenter = (OverlappedPresenter)_appWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        SetWindowLongPtr(_hwnd, GWL_STYLE, (IntPtr)(WS_POPUP | WS_VISIBLE));
        var ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64() | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, (IntPtr)ex);

        var root = new Grid
        {
            Width = 268,
            Height = 44,
            Padding = new Thickness(14, 0, 14, 0),
            ColumnSpacing = 8,
        };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Countdown ring (simplified as text for now).
        _countdownText = new TextBlock
        {
            FontSize = 12,
            FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 },
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 20,
            TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center,
        };
        root.Children.Add(_countdownText);

        _titleText = new TextBlock
        {
            Text = "Note deleted",
            FontSize = 12,
            FontWeight = new Windows.UI.Text.FontWeight { Weight = 500 },
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(_titleText, 1);
        root.Children.Add(_titleText);

        _undoButton = new Button
        {
            Content = "Undo",
            FontSize = 12,
            FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 },
            Background = new SolidColorBrush(Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _undoButton.Click += (_, _) =>
        {
            _notes.UndoDelete();
        };
        Grid.SetColumn(_undoButton, 2);
        root.Children.Add(_undoButton);

        var border = new Border
        {
            Child = root,
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x20, 0x20, 0x20)),
            CornerRadius = new CornerRadius(11),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0.5),
        };

        _window.Content = border;
    }

    private void PositionWindow()
    {
        if (_appWindow is null) return;
        // Center horizontally on the primary screen, 34pt from bottom.
        var dpi = GetDpiForWindow(_hwnd) / 96.0;
        var w = (int)Math.Round(268 / dpi);
        var h = (int)Math.Round(44 / dpi);
        var sw = GetSystemMetrics(SM_CXSCREEN);
        var sh = GetSystemMetrics(SM_CYSCREEN);
        var x = (sw - w) / 2;
        var y = sh - h - (int)Math.Round(34 / dpi);
        _appWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, w, h));
    }

    private void StartTick()
    {
        StopTick();
        _tickTimer = _dispatcher.CreateTimer();
        _tickTimer.Interval = TimeSpan.FromMilliseconds(100);
        _tickTimer.Tick += (_, _) => UpdateCountdown();
        _tickTimer.Start();
    }

    private void StopTick()
    {
        _tickTimer?.Stop();
        _tickTimer = null;
    }

    private void UpdateCountdown()
    {
        if (_deadline is null || _countdownText is null) return;
        var remaining = (_deadline.Value - DateTime.UtcNow).TotalSeconds;
        if (remaining <= 0)
        {
            _countdownText.Text = "0";
            _notes.ClearPendingUndo();
            return;
        }
        _countdownText.Text = ((int)Math.Ceiling(remaining)).ToString();
        // Color the countdown text with the note's dash color.
        if (_pendingNoteId is not null && _notes.ById(_pendingNoteId) is { } note)
        {
            var dash = note.Palette.DashArgb;
            _countdownText.Foreground = new SolidColorBrush(Color.FromArgb(
                (byte)((dash >> 24) & 0xFF), (byte)((dash >> 16) & 0xFF),
                (byte)((dash >> 8) & 0xFF), (byte)(dash & 0xFF)));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopTick();
        _window?.Close();
        _window = null;
    }

    private sealed class UndoObserver : IObserver<NoteList>
    {
        private readonly UndoToast _t;
        public UndoObserver(UndoToast t) { _t = t; }
        public void OnNext(NoteList value) => _t.OnNotesChanged();
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }

    // P/Invoke
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
