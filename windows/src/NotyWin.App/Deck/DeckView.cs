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

    public DeckViewModel? ViewModel { get; set; }
    public RevealProgressTracker Reveal { get; } = new();
    public bool OnRightEdge { get; set; } = true;
    public NoteEditorControl Editor => _editor;

    public DeckView()
    {
        _canvas = new CanvasControl
        {
            // Transparent so the XAML host's background doesn't bleed
            // through as a hard rectangle behind the pill.
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
        };
        _canvas.Draw += OnDraw;
        _overlay.Children.Add(_editor);
        _root.Children.Add(_canvas);
        _root.Children.Add(_overlay);
        Content = _root;
    }

    public void Refresh()
    {
        _frame = null;
        _canvas.Invalidate();
    }

    /// <summary>Re-render after a panel size change (DIPs). The canvas fills the
    /// window, so only the tracked size and the cached frame change.</summary>
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
        var frame = GetOrComputeFrame(_panelW, _panelH);
        return HitTest.Test(x, y, frame, _panelW, OnRightEdge);
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

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (ViewModel is null) return;
        // Resize() is authoritative for panel size; only backfill if a draw
        // races ahead of the first relayout.
        if (_panelW <= 0) _panelW = sender.ActualWidth;
        if (_panelH <= 0) _panelH = sender.ActualHeight;
        var now = Environment.TickCount / 1000.0;
        _frame ??= ViewModel.Render(_panelH, _panelW, LabelCacheSingleton.Get(), Reveal, now);
        DeckLog.Write("VIEW", $"OnDraw w={_panelW:F0} h={_panelH:F0} items={_frame.Items.Count} fan={_frame.FanVisible} pill={_frame.PillVisible}");
        _painter.Paint(args.DrawingSession, _frame, _panelW);
    }
}

internal static class LabelCacheSingleton
{
    private static LabelWidthCache? _cache;
    public static LabelWidthCache Get() => _cache ??= new LabelWidthCache(new GdiTextMeasurer());
}
