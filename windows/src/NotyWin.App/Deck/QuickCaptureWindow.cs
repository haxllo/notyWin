using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
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
/// A small floating capture box summoned by global hotkey or
/// <c>noty://capture</c>. Type, hit Enter, and the text becomes a note in the
/// deck — no editor opened, no focus ceremony. Mirrors
/// <c>QuickCapture</c> in Sources/QuickCapture.swift.
/// </summary>
public sealed class QuickCaptureWindow : IDisposable
{
    private Microsoft.UI.Xaml.Window? _window;
    private AppWindow? _appWindow;
    private nint _hwnd;
    private readonly NoteList _notes;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;
    private readonly TextBox _textBox;
    private bool _disposed;
    private DateTime _shownAt;

    public QuickCaptureWindow(NoteList notes, Microsoft.UI.Dispatching.DispatcherQueue dispatcher)
    {
        _notes = notes;
        _dispatcher = dispatcher;
        _textBox = new TextBox
        {
            PlaceholderText = "Jot a note…",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13.5,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 4, 0, 0),
            Height = 72,
        };
        _textBox.PreviewKeyDown += OnKeyDown;
    }

    /// <summary>Toggle show/hide with 350ms debounce to prevent hotkey autorepeat flapping.</summary>
    public void Toggle()
    {
        if ((DateTime.UtcNow - _shownAt).TotalSeconds < 0.35) return;
        _shownAt = DateTime.UtcNow;
        if (_window is null) Show();
        else Dismiss();
    }

    public void Show()
    {
        if (_window is null) CreateWindow();
        _shownAt = DateTime.UtcNow;
        _textBox.Text = "";
        PositionOnCursorScreen();
        _window!.Activate();
        _dispatcher.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () => _textBox.Focus(FocusState.Programmatic));
    }

    public void Dismiss()
    {
        _window?.Close();
        _window = null;
    }

    private void CreateWindow()
    {
        _window = new Microsoft.UI.Xaml.Window { Title = "Noty Capture" };
        _window.SystemBackdrop = null;
        _appWindow = _window.AppWindow;
        var presenter = (Microsoft.UI.Windowing.OverlappedPresenter)_appWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        var ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64() | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, (IntPtr)ex);

        // Pick a paper colour for the preview — round-robin.
        var idx = _notes.ActiveCount % NoteColor.All.Length;
        var pal = NoteColor.All[idx];

        var root = new Grid { Width = 460, Height = 150, Padding = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Header row
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var dot = new Ellipse
        {
            Width = 8, Height = 8,
            Fill = new SolidColorBrush(Color.FromArgb(
                (byte)((pal.DashArgb >> 24) & 0xFF), (byte)((pal.DashArgb >> 16) & 0xFF),
                (byte)((pal.DashArgb >> 8) & 0xFF), (byte)(pal.DashArgb & 0xFF))),
            VerticalAlignment = VerticalAlignment.Center,
        };
        header.Children.Add(dot);
        var title = new TextBlock
        {
            Text = "Quick Capture",
            FontSize = 11,
            FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 },
            Opacity = 0.55,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromArgb(
                (byte)((pal.InkArgb >> 24) & 0xFF), (byte)((pal.InkArgb >> 16) & 0xFF),
                (byte)((pal.InkArgb >> 8) & 0xFF), (byte)(pal.InkArgb & 0xFF))),
        };
        header.Children.Add(title);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        _textBox.Foreground = new SolidColorBrush(Color.FromArgb(
            (byte)((pal.InkArgb >> 24) & 0xFF), (byte)((pal.InkArgb >> 16) & 0xFF),
            (byte)((pal.InkArgb >> 8) & 0xFF), (byte)(pal.InkArgb & 0xFF)));
        Grid.SetRow(_textBox, 1);
        root.Children.Add(_textBox);

        var border = new Border
        {
            Child = root,
            Background = new SolidColorBrush(Color.FromArgb(
                (byte)((pal.PaperArgb >> 24) & 0xFF), (byte)((pal.PaperArgb >> 16) & 0xFF),
                (byte)((pal.PaperArgb >> 8) & 0xFF), (byte)(pal.PaperArgb & 0xFF))),
            CornerRadius = new CornerRadius(12),
            BorderBrush = new SolidColorBrush(Color.FromArgb(
                (byte)((pal.InkArgb >> 24) & 0xFF), (byte)((pal.InkArgb >> 16) & 0xFF),
                (byte)((pal.InkArgb >> 8) & 0xFF), (byte)(pal.InkArgb & 0xFF))),
            BorderThickness = new Thickness(1),
        };

        _window.Content = border;
    }

    private void PositionOnCursorScreen()
    {
        if (_appWindow is null) return;
        var displays = DisplayEnumerator.Snapshot();
        var (cx, cy) = DeckWindow.CursorPos();
        var display = DisplayEnumerator.DisplayAtPoint(cx, cy, displays);
        if (display is not { } d) return;
        var dpi = (double)GetDpiForWindow(_hwnd) / 96.0;
        // Convert display rect from physical pixels to DIPs.
        var visL = d.VisLeft / dpi;
        var visR = d.VisRight / dpi;
        var visB = d.VisBottom / dpi;
        var visH = d.VisHeight / dpi;
        var w = 460;
        var h = 150;
        var x = (int)((visL + visR) / 2.0 - w / 2.0);
        var y = (int)(visB - h - visH * 0.12);
        _appWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, w, h));
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (e.Key == VirtualKey.Enter && !shift)
        {
            SaveAndDismiss();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            Dismiss();
            e.Handled = true;
        }
    }

    private void SaveAndDismiss()
    {
        var text = _textBox.Text ?? "";
        var trimmed = text.Trim();
        if (trimmed.Length > 0)
            _notes.Create(trimmed);
        Dismiss();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Dismiss();
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);
}
