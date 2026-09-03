using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NotyWin.App.Geometry;
using NotyWin.App.Models;
using NotyWin.Rendering;
using RenderDeckFrame = NotyWin.Rendering.DeckFrame;

namespace NotyWin.App.Deck;

/// <summary>
/// WinUI 3 host control for the deck. Wraps a Win2D <see cref="CanvasControl"/>
/// and repaints whenever the underlying <see cref="DeckFrame"/> changes.
///
/// Hit areas follow the shingled tab layout — hit testing happens in
/// <see cref="HitTest"/> and is panel-local; pointer coordinates are translated
/// from screen to panel-local in the host.
/// </summary>
public sealed class DeckView : UserControl
{
    private readonly CanvasControl _canvas;
    private readonly DeckPainter _painter = new();
    private RenderDeckFrame? _frame;

    public DeckViewModel? ViewModel { get; set; }
    public RevealProgressTracker Reveal { get; } = new();
    public bool OnRightEdge { get; set; } = true;

    public event Action<RenderItem, double, double>? ItemPressed;
    public event Action<double, double>? PointerMovedOnPanel;
    public event Action? PointerEntered;
    public event Action? PointerExited;

    public DeckView()
    {
        _canvas = new CanvasControl();
        _canvas.Draw += OnDraw;
        _canvas.IsHitTestVisible = true;
        _canvas.PointerMoved += (s, e) => OnPointer(e, isEnter: false);
        _canvas.PointerEntered += (s, e) => PointerEntered?.Invoke();
        _canvas.PointerExited += (s, e) => PointerExited?.Invoke();
        _canvas.PointerPressed += (s, e) => OnPointerPressed(e);
        Content = _canvas;
    }

    public void Refresh()
    {
        _frame = null;
        _canvas.Invalidate();
    }

    /// <summary>Re-render after a panel size change.</summary>
    public void Resize(double panelWidth, double panelHeight)
    {
        _canvas.Width = panelWidth;
        _canvas.Height = panelHeight;
        _canvas.Invalidate();
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (ViewModel is null) return;
        var cache = LabelCacheSingleton.Get();
        _frame ??= ViewModel.Render(sender.ActualWidth, sender.ActualHeight, cache, Reveal);
        _painter.Paint(args.DrawingSession, _frame, sender.ActualWidth);
    }

    private void OnPointer(PointerRoutedEventArgs e, bool isEnter)
    {
        if (_frame is null || ViewModel is null) return;
        var p = e.GetCurrentPoint(_canvas).Position;
        var hit = HitTest.Test(p.X, p.Y, _frame, _canvas.ActualWidth, OnRightEdge);
        Reveal.HoverTabId = hit?.Item.Note?.Id;
        _canvas.Invalidate();
        PointerMovedOnPanel?.Invoke(p.X, p.Y);
    }

    private void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (_frame is null) return;
        var p = e.GetCurrentPoint(_canvas).Position;
        var hit = HitTest.Test(p.X, p.Y, _frame, _canvas.ActualWidth, OnRightEdge);
        if (hit is { } h)
            ItemPressed?.Invoke(h.Item, p.X, p.Y);
    }
}

internal static class LabelCacheSingleton
{
    private static LabelWidthCache? _cache;
    public static LabelWidthCache Get() => _cache ??= new LabelWidthCache(new GdiTextMeasurer());
}