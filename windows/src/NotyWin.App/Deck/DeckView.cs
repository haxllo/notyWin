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
    public event Action<RenderItem>? TabRightClicked;

    public DeckView()
    {
        _canvas = new CanvasControl
        {
            // Transparent so the XAML host's background doesn't bleed
            // through as a hard rectangle behind the pill.
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
        };
        _canvas.Draw += OnDraw;
        _canvas.IsHitTestVisible = true;
        _canvas.PointerMoved += (s, e) => OnPointerMoved(e);
        _canvas.PointerEntered += (s, e) => PointerEntered?.Invoke();
        _canvas.PointerExited += (s, e) => PointerExited?.Invoke();
        _canvas.PointerPressed += (s, e) => OnPointerPressed(e);
        _canvas.RightTapped += (s, e) => OnRightTapped(e);
        Content = _canvas;
    }

    public void Refresh()
    {
        _frame = null;
        Log("Refresh: invalidate called");
        _canvas.Invalidate();
    }

    /// <summary>Re-render after a panel size change.</summary>
    public void Resize(double panelWidth, double panelHeight)
    {
        Log($"Resize: {panelWidth:F0}x{panelHeight:F0}");
        _canvas.Width = panelWidth;
        _canvas.Height = panelHeight;
        _canvas.Invalidate();
    }

    private static void Log(string msg)
    {
        System.IO.File.AppendAllText(
            System.IO.Path.Combine(System.Environment.GetFolderPath(
                System.Environment.SpecialFolder.LocalApplicationData), "Noty", "wndproc.log"),
            $"[{DateTime.UtcNow:O}] VIEW: {msg}\n");
    }

    /// <summary>Compute the current frame for hit-testing without going through the XAML draw path.</summary>
    public RenderDeckFrame GetOrComputeFrame(double width, double height)
    {
        if (_frame is { } f && Math.Abs(f.Items.Count) >= 0) return f;
        if (ViewModel is null) return new RenderDeckFrame
        {
            Items = Array.Empty<RenderItem>(),
            PillVisible = false, FanVisible = false, ShowExpanded = false,
        };
        var cache = LabelCacheSingleton.Get();
        _frame = ViewModel.Render(width, height, cache, Reveal);
        return _frame;
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (ViewModel is null)
        {
            Log("OnDraw: ViewModel=null, skipping");
            return;
        }
        var cache = LabelCacheSingleton.Get();
        _frame ??= ViewModel.Render(sender.ActualWidth, sender.ActualHeight, cache, Reveal);
        Log($"OnDraw: w={sender.ActualWidth:F0} h={sender.ActualHeight:F0} items={_frame.Items.Count} fan={_frame.FanVisible} pill={_frame.PillVisible}");
        _painter.Paint(args.DrawingSession, _frame, sender.ActualWidth);
    }

    private void OnPointerMoved(PointerRoutedEventArgs e)
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

    private void OnRightTapped(RightTappedRoutedEventArgs e)
    {
        if (_frame is null) return;
        var p = e.GetPosition(_canvas);
        var hit = HitTest.Test(p.X, p.Y, _frame, _canvas.ActualWidth, OnRightEdge);
        if (hit is { Item: { Kind: RenderItemKind.Tab or RenderItemKind.ChipTab } } h)
            TabRightClicked?.Invoke(h.Item);
    }
}

internal static class LabelCacheSingleton
{
    private static LabelWidthCache? _cache;
    public static LabelWidthCache Get() => _cache ??= new LabelWidthCache(new GdiTextMeasurer());
}