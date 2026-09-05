using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NotyWin.App.Geometry;
using NotyWin.App.Models;
using NotyWin.Rendering;
using RenderDeckFrame = NotyWin.Rendering.DeckFrame;

namespace NotyWin.App.Deck;

/// <summary>
/// WPF host control for the deck. Contains a custom <see cref="DeckCanvas"/>
/// that paints via OnRender(DrawingContext) and a Canvas overlay for the
/// NoteEditorControl. Replaces the WinUI 3 CanvasControl-based DeckView.
/// </summary>
public sealed class DeckView : UserControl
{
    private readonly Grid _root = new();
    private readonly DeckCanvas _canvas;
    private readonly Canvas _overlay = new();
    private readonly NoteEditorControl _editor = new() { Visibility = Visibility.Collapsed };
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
            _canvas.InvalidateVisual();
        }
    }

    public RevealProgressTracker Reveal { get; } = new();
    public bool OnRightEdge { get; set; } = true;
    public NoteEditorControl Editor => _editor;

    public DeckView()
    {
        _canvas = new DeckCanvas(this);
        _overlay.Children.Add(_editor);
        _root.Children.Add(_canvas);
        _root.Children.Add(_overlay);
        Content = _root;
    }

    public void Refresh()
    {
        _frame = null;
        _canvas.InvalidateVisual();
    }

    public void Resize(double panelWidth, double panelHeight)
    {
        _panelW = panelWidth;
        _panelH = panelHeight;
        _frame = null;
        _canvas.InvalidateVisual();
    }

    public RenderDeckFrame GetOrComputeFrame(double width, double height, double now = 0)
    {
        if (_frame is { } f) return f;
        if (ViewModel is null) return EmptyFrame();
        _frame = ViewModel.Render(height, width, LabelCacheSingleton.Get(), Reveal, now);
        return _frame;
    }

    public HitTest.HitItem? HitAt(double x, double y)
    {
        if (ViewModel is null) return null;
        var w = _panelW;
        var h = _panelH;
        if (w <= 0 || h <= 0) return null;
        var frame = GetOrComputeFrame(w, h);
        return HitTest.Test(x, y, frame, w, OnRightEdge);
    }

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

    /// <summary>Custom FrameworkElement that paints the deck via OnRender.</summary>
    private sealed class DeckCanvas : FrameworkElement
    {
        private readonly DeckView _owner;
        private readonly DeckPainter _painter = new();

        public DeckCanvas(DeckView owner)
        {
            _owner = owner;
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (_owner.ViewModel is null) return;
            var w = _owner._panelW;
            var h = _owner._panelH;
            if (w <= 0 || h <= 0) return;
            try
            {
                var now = Environment.TickCount / 1000.0;
                var frame = _owner.ViewModel.Render(h, w, LabelCacheSingleton.Get(), _owner.Reveal, now);
                _owner._frame = frame;
                _painter.Paint(dc, frame, w, h);
            }
            catch (Exception ex)
            {
                DeckLog.Write("VIEW", $"OnRender EX: {ex.Message}");
            }
        }
    }
}

internal static class LabelCacheSingleton
{
    private static LabelWidthCache? _cache;
    public static LabelWidthCache Get() => _cache ??= new LabelWidthCache(new WpfTextMeasurer());
}
