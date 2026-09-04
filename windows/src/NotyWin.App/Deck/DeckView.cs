using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NotyWin.App.Geometry;
using NotyWin.App.Models;
using NotyWin.Rendering;
using RenderDeckFrame = NotyWin.Rendering.DeckFrame;

namespace NotyWin.App.Deck;

/// <summary>
/// WinUI 3 host control for the deck. Two layers in a <see cref="Grid"/>:
/// a Win2D <see cref="CanvasControl"/> paints the pill / fan / tabs, and an
/// overlay <see cref="Canvas"/> carries the <see cref="NoteEditorControl"/> at
/// the expanded note's rect (Win2D cannot take keyboard input, so the open note
/// is real XAML). The canvas stretches to fill the window; the panel size is
/// tracked in <c>_panelW/_panelH</c> and is the single source for layout and
/// hit-testing.
///
/// Deck input is not handled here: the HWND is a non-activating topmost popup
/// whose managed pointer events are unreliable, so <see cref="DeckController"/>
/// drives everything from raw window messages and asks this control for hit
/// tests via <see cref="HitAt"/>.
/// </summary>
public sealed class DeckView : UserControl
{
    private readonly Grid _root = new();
    private readonly CanvasControl _canvas;
    private readonly Canvas _overlay = new();
    private readonly NoteEditorControl _editor = new() { Visibility = Visibility.Collapsed };
    private readonly DeckPainter _painter = new();
    private RenderDeckFrame? _frame;
    private double _panelW;
    private double _panelH;
    private DeckViewModel? _viewModel;

    public DeckViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            _viewModel = value;
            _frame = null;
            _canvas.Invalidate();
        }
    }
    public RevealProgressTracker Reveal { get; } = new();
    public bool OnRightEdge { get; set; } = true;
    public NoteEditorControl Editor => _editor;

    public DeckView()
    {
        _canvas = new CanvasControl
        {
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        _canvas.Draw += OnDraw;
        _canvas.SizeChanged += OnCanvasSizeChanged;
        _overlay.Children.Add(_editor);
        _overlay.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        _root.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        _root.Children.Add(_canvas);
        _root.Children.Add(_overlay);
        Content = _root;
    }

    public void Refresh()
    {
        _frame = null;
        _canvas.Invalidate();
    }

    /// <summary>Re-render after a panel size change (DIPs). The host sizes the
    /// window's client surface; this control must stay stretch-aligned so the
    /// XAML layout pass has one authoritative size.</summary>
    public void Resize(double panelWidth, double panelHeight)
    {
        _panelW = panelWidth;
        _panelH = panelHeight;
        _frame = null;
        _canvas.Invalidate();
    }

    /// <summary>The frame from the last paint pass, computed on demand.</summary>
    public RenderDeckFrame GetOrComputeFrame(double width, double height, double now = 0)
    {
        if (_frame is { } f) return f;
        if (ViewModel is null) return EmptyFrame();
        _frame = ViewModel.Render(height, width, LabelCacheSingleton.Get(), Reveal, now);
        return _frame;
    }

    /// <summary>Hit-test a panel-local point (DIPs) against the live frame.</summary>
    public HitTest.HitItem? HitAt(double x, double y)
    {
        if (ViewModel is null) return null;
        var w = _canvas.ActualWidth;
        var h = _canvas.ActualHeight;
        if (w <= 0 || h <= 0) return null;
        var frame = GetOrComputeFrame(w, h);
        return HitTest.Test(x, y, frame, w, OnRightEdge);
    }

    /// <summary>
    /// Show the editor for <paramref name="note"/> at the expanded note's rect,
    /// or hide it (flushing pending edits) when <c>null</c>.
    /// </summary>
    public void SyncEditor(Note? note, bool onRight, double fontSize)
    {
        if (note is null)
        {
            if (_editor.Visibility == Visibility.Visible) _editor.Flush();
            _editor.Visibility = Visibility.Collapsed;
            return;
        }

        var item = GetOrComputeFrame(_panelW, _panelH).Items
            .FirstOrDefault(i => i.Kind == RenderItemKind.ExpandedNote);
        if (item is null)
        {
            _editor.Visibility = Visibility.Collapsed;
            return;
        }

        _editor.OnRight = onRight;
        _editor.BodyFontSize = fontSize;
        if (ViewModel is not null)
            _editor.MarkdownEnabled = ViewModel.Settings().MarkdownStyling;
        _editor.Width = item.Width;
        _editor.Height = item.Height;
        Canvas.SetLeft(_editor, item.X);
        Canvas.SetTop(_editor, item.Y);
        _editor.Visibility = Visibility.Visible;
        _editor.SetNote(note, autofocus: true);
    }

    private static RenderDeckFrame EmptyFrame() => new()
    {
        Items = Array.Empty<RenderItem>(),
        PillVisible = false,
        FanVisible = false,
        ShowExpanded = false,
    };

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _panelW = e.NewSize.Width;
        _panelH = e.NewSize.Height;
        _frame = null;
        _canvas.Invalidate();
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (ViewModel is null) return;
        var w = _panelW;
        var h = _panelH;
        if (w <= 0 || h <= 0)
        {
            w = sender.ActualWidth;
            h = sender.ActualHeight;
            if (w <= 0 || h <= 0) return;
        }
        var now = Environment.TickCount / 1000.0;
        try
        {
            var frame = ViewModel.Render(h, w, LabelCacheSingleton.Get(), Reveal, now);
            _frame = frame;
            _painter.Paint(args.DrawingSession, frame, w);
        }
        catch (Exception ex)
        {
            // Win2D geometry creation can throw COMException if the device is
            // lost or a resource is disposed mid-frame. Log and skip — the
            // next paint pass will retry with a fresh frame.
            DeckLog.Write("VIEW", $"OnDraw EX w={w:F0} h={h:F0}: {ex.Message}");
        }
    }
}

internal static class LabelCacheSingleton
{
    private static LabelWidthCache? _cache;
    public static LabelWidthCache Get() => _cache ??= new LabelWidthCache(new GdiTextMeasurer());
}
