using System.Windows;
using System.Windows.Media;
using NotyWin.App.Geometry;
using NotyWin.App.Models;
using NotyWin.Rendering;
using WpfGeometry = System.Windows.Media.Geometry;
using RenderDeckFrame = NotyWin.Rendering.DeckFrame;

namespace NotyWin.App.Deck;

/// <summary>
/// Pure WPF painter: takes a <see cref="RenderDeckFrame"/> and draws every
/// item via <see cref="DrawingContext"/>. Mirrors the WinUI 3 DeckPainter
/// but uses WPF primitives instead of Win2D.
/// </summary>
public sealed class DeckPainter
{
    private static readonly Color BgMaterial = Color.FromArgb(0x99, 0x00, 0x00, 0x00);
    private static readonly Color EdgeSpineColor = Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF);
    private static readonly Color Secondary = Color.FromArgb(0xBF, 0x80, 0x80, 0x80);
    private static readonly Color PlusFg = Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF);

    private static readonly Brush BgMaterialBrush = new SolidColorBrush(BgMaterial);
    private static readonly Brush SecondaryBrush = new SolidColorBrush(Secondary);
    private static readonly Brush PlusFgBrush = new SolidColorBrush(PlusFg);

    public void Paint(DrawingContext dc, RenderDeckFrame frame, double panelWidth, double panelHeight)
    {
        foreach (var ri in frame.Items)
        {
            switch (ri.Kind)
            {
                case RenderItemKind.Pill: PaintPill(dc, ri); break;
                case RenderItemKind.Tab: PaintTab(dc, ri); break;
                case RenderItemKind.ChipTab: PaintChipTab(dc, ri); break;
                case RenderItemKind.EmptyTab: PaintEmptyTab(dc, ri); break;
                case RenderItemKind.MoreTab: PaintMoreTab(dc, ri); break;
                case RenderItemKind.PlusButton: PaintPlus(dc, ri); break;
                case RenderItemKind.CogButton: PaintCog(dc, ri); break;
                case RenderItemKind.EdgeSpine: PaintSpine(dc, ri); break;
                case RenderItemKind.NotePreview: PaintPreview(dc, ri); break;
                // ExpandedNote is painted by the XAML NoteEditorControl overlay.
            }
        }
    }

    // MARK: Pill

    private static void PaintPill(DrawingContext dc, RenderItem r)
    {
        var rect = new Rect(r.X, r.Y, r.Width, r.Height);
        dc.DrawRoundedRectangle(BgMaterialBrush, null, rect, 6, 6);

        var colors = r.DashColors;
        var overflow = r.PillOverflow;
        var totalDashes = colors?.Count ?? (NotesEmpty(r) ? 1 : 0);
        if (overflow) totalDashes += 1;
        if (totalDashes == 0) totalDashes = 1;

        var dashGap = DeckGeom.DashGap;
        var dashW = DeckGeom.DashWidth;
        var dashH = DeckGeom.DashHeight;
        var pad = DeckGeom.PillPad;
        var totalH = pad * 2 + totalDashes * dashH + (totalDashes - 1) * dashGap;
        var y = r.Y + (r.Height - totalH) / 2;

        if (colors is null || colors.Count == 0)
        {
            var dashRect = new Rect(r.X + (r.Width - dashW) / 2, y, dashW, dashH);
            var brush = new SolidColorBrush(Color.FromArgb(0xCC, Secondary.R, Secondary.G, Secondary.B));
            dc.DrawRoundedRectangle(brush, null, dashRect, 2.5, 2.5);
            return;
        }

        for (var i = 0; i < colors.Count; i++)
        {
            var dashRect = new Rect(r.X + (r.Width - dashW) / 2, y, dashW, dashH);
            dc.DrawRoundedRectangle(BrushFromArgb(colors[i]), null, dashRect, 2.5, 2.5);
            y += dashH + dashGap;
        }
        if (overflow)
        {
            var dashRect = new Rect(r.X + (r.Width - dashW) / 2, y, dashW, dashH);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x80, Secondary.R, Secondary.G, Secondary.B)),
                null, dashRect, 2.5, 2.5);
        }
    }

    private static bool NotesEmpty(RenderItem r) => r.DashColors is { Count: 0 };

    // MARK: Tab

    private static void PaintTab(DrawingContext dc, RenderItem r)
    {
        var note = r.Note!;
        var color = ColorFromArgb(note.Palette.PaperArgb);
        var ink = ColorFromArgb(note.Palette.InkArgb);
        var dash = ColorFromArgb(note.Palette.DashArgb);

        var cx = (float)(r.X + (r.Width + (r.Lifted ? 0 : -DeckGeom.Bleed)) / 2);
        var cy = (float)(r.Y + r.Height / 2);

        // Apply lean rotation + optional lift scale.
        dc.PushTransform(new RotateTransform(
            DeckGeom.Lean(true), cx, cy));
        if (r.Lifted)
            dc.PushTransform(new ScaleTransform(1.04, 1.04, cx, cy));

        // Draw tab shape.
        var geo = TabGeo(r.X, r.Y, r.Width, r.Height, onRight: true);
        dc.DrawGeometry(new SolidColorBrush(color), null, geo);

        // Shadow border.
        var shadowOpacity = r.Lifted ? 0.42 : (r.IsOpen || r.Hovering ? 0.32 : 0.22);
        var shadowPen = new Pen(new SolidColorBrush(Color.FromArgb(
            (byte)(shadowOpacity * 255), 0, 0, 0)), 6);
        dc.DrawGeometry(null, shadowPen, geo);

        // Rotated label.
        var labelFont = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal,
            FontStretches.Normal);
        var label = note.DisplayTitle("Untitled").ToUpperInvariant();
        var labelStrip = Math.Max(20, DeckGeom.PitchMax - DeckGeom.LabelInset);
        var labelSize = DeckGeom.TabSize();

        var formatted = new FormattedText(label,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            labelFont, labelSize,
            new SolidColorBrush(Color.FromArgb(
                (byte)((int)(ink.A * 0.85) & 0xFF), ink.R, ink.G, ink.B)))
        {
            Trimming = TextTrimming.CharacterEllipsis,
            MaxTextWidth = labelStrip,
            MaxTextHeight = DeckGeom.TabWidth,
        };

        var innerCx = r.X + r.Width / 2;
        var innerCy = r.Y + r.Height / 2;
        dc.PushTransform(new RotateTransform(90, innerCx, innerCy));
        dc.DrawText(formatted, new Point(
            r.X + (r.Width - labelStrip + DeckGeom.LabelInset) / 2,
            r.Y + DeckGeom.Bleed / 2));
        dc.Pop(); // Pop 90° rotation.

        if (r.Lifted) dc.Pop(); // Pop lift scale.
        dc.Pop(); // Pop lean rotation.

        // Pin indicator.
        if (r.Pinned)
        {
            dc.DrawEllipse(new SolidColorBrush(dash), null,
                new Point(r.X + r.Width - 9, r.Y + 12), 2.5, 2.5);
        }
    }

    // MARK: ChipTab

    private static void PaintChipTab(DrawingContext dc, RenderItem r)
    {
        var dash = ColorFromArgb(r.Note!.Palette.DashArgb);
        var rect = new Rect(r.X, r.Y, DeckGeom.ChipWidth, DeckGeom.ChipHeight);
        var geo = BuildRoundedRectPath(rect, 0, 7, 7, 0);
        dc.DrawGeometry(new SolidColorBrush(dash), null, geo);
        var shadowOpacity = r.IsOpen ? 0.34 : 0.22;
        var shadowPen = new Pen(new SolidColorBrush(Color.FromArgb(
            (byte)(shadowOpacity * 255), 0, 0, 0)), 5);
        dc.DrawGeometry(null, shadowPen, geo);
    }

    // MARK: EmptyTab

    private static void PaintEmptyTab(DrawingContext dc, RenderItem r)
    {
        var geo = TabGeo(r.X, r.Y, r.Width, r.Height, onRight: true);
        dc.DrawGeometry(BgMaterialBrush, null, geo);

        var labelFont = new Typeface("Segoe UI");
        var formatted = new FormattedText("NEW NOTE",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            labelFont, DeckGeom.TabSize(),
            SecondaryBrush);
        dc.DrawText(formatted, new Point(
            r.X + (r.Width - 80) / 2,
            r.Y + r.Height / 2 - 6));
    }

    // MARK: MoreTab

    private static void PaintMoreTab(DrawingContext dc, RenderItem r)
    {
        var rect = new Rect(r.X, r.Y, r.Width, r.Height);
        dc.DrawRoundedRectangle(BgMaterialBrush, null, rect, 9, 9);

        var labelFont = new Typeface("Segoe UI");
        var formatted = new FormattedText("+" + r.HiddenCount,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            labelFont, 10,
            SecondaryBrush);
        dc.DrawText(formatted, new Point(
            r.X + r.Width / 2 - 10,
            r.Y + r.Height / 2 - 7));
    }

    // MARK: PlusButton / CogButton

    private static void PaintPlus(DrawingContext dc, RenderItem r)
    {
        var bg = new SolidColorBrush(Color.FromArgb(0x99, 0x80, 0x80, 0x80));
        dc.DrawEllipse(bg, null,
            new Point(r.X + r.Width / 2, r.Y + r.Height / 2),
            r.Width / 2, r.Width / 2);

        var labelFont = new Typeface("Segoe UI");
        var formatted = new FormattedText("+",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            labelFont, 11,
            PlusFgBrush);
        dc.DrawText(formatted, new Point(
            r.X + r.Width / 2 - 5,
            r.Y + r.Height / 2 - 7));
    }

    private static void PaintCog(DrawingContext dc, RenderItem r)
    {
        var bg = new SolidColorBrush(Color.FromArgb(0x99, 0x80, 0x80, 0x80));
        dc.DrawEllipse(bg, null,
            new Point(r.X + r.Width / 2, r.Y + r.Height / 2),
            r.Width / 2, r.Width / 2);

        var labelFont = new Typeface("Segoe UI");
        var formatted = new FormattedText("\u2699",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            labelFont, 10,
            PlusFgBrush);
        dc.DrawText(formatted, new Point(
            r.X + r.Width / 2 - 6,
            r.Y + r.Height / 2 - 7));
    }

    // MARK: EdgeSpine

    private static void PaintSpine(DrawingContext dc, RenderItem r)
    {
        var pen = new Pen(new SolidColorBrush(EdgeSpineColor), 1)
        {
            DashStyle = new DashStyle(new double[] { 3, 4 }, 0),
        };
        dc.DrawLine(pen,
            new Point(r.X + r.Width / 2, r.Y),
            new Point(r.X + r.Width / 2, r.Y + r.Height));
    }

    // MARK: NotePreview

    private static void PaintPreview(DrawingContext dc, RenderItem r)
    {
        var n = r.Note!;
        var paper = ColorFromArgb(n.Palette.PaperArgb);
        var ink = ColorFromArgb(n.Palette.InkArgb);
        var rect = new Rect(r.X, r.Y, r.Width, r.Height);
        dc.DrawRoundedRectangle(new SolidColorBrush(paper), null, rect, 8, 8);
        dc.DrawRoundedRectangle(null,
            new Pen(new SolidColorBrush(Color.FromArgb(0x1F, ink.R, ink.G, ink.B)), 1),
            rect, 8, 8);

        var titleFont = new Typeface("Segoe UI");
        var formatted = new FormattedText(n.DisplayTitle("Untitled"),
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            titleFont, 11.5,
            new SolidColorBrush(ink));
        dc.DrawText(formatted, new Point(r.X + 10, r.Y + 9));
    }

    // MARK: Geometry helpers

    private static WpfGeometry TabGeo(double x, double y, double w, double h,
        bool onRight, double radius = 11)
    {
        var rect = new Rect(x, y, w, h);
        return BuildRoundedRectPath(rect,
            onRight ? 0 : radius, onRight ? radius : 0,
            onRight ? radius : 0, onRight ? 0 : radius);
    }

    private static StreamGeometry BuildRoundedRectPath(Rect r,
        double tl, double tr, double br, double bl)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            var x = r.X; var y = r.Y; var w = r.Width; var h = r.Height;
            ctx.BeginFigure(new Point(x + tl, y), false, false);
            ctx.LineTo(new Point(x + w - tr, y), true, true);
            if (tr > 0) ctx.ArcTo(new Point(x + w, y + tr),
                new Size(tr, tr), 0, false, SweepDirection.Clockwise, true, true);
            ctx.LineTo(new Point(x + w, y + h - br), true, true);
            if (br > 0) ctx.ArcTo(new Point(x + w - br, y + h),
                new Size(br, br), 0, false, SweepDirection.Clockwise, true, true);
            ctx.LineTo(new Point(x + bl, y + h), true, true);
            if (bl > 0) ctx.ArcTo(new Point(x, y + h - bl),
                new Size(bl, bl), 0, false, SweepDirection.Clockwise, true, true);
            ctx.LineTo(new Point(x, y + tl), true, true);
            if (tl > 0) ctx.ArcTo(new Point(x + tl, y),
                new Size(tl, tl), 0, false, SweepDirection.Clockwise, true, true);
            ctx.Close();
        }
        geo.Freeze();
        return geo;
    }

    private static Color ColorFromArgb(int argb) => Color.FromArgb(
        (byte)((argb >> 24) & 0xFF),
        (byte)((argb >> 16) & 0xFF),
        (byte)((argb >> 8) & 0xFF),
        (byte)(argb & 0xFF));

    private static SolidColorBrush BrushFromArgb(int argb)
    {
        var c = ColorFromArgb(argb);
        return new SolidColorBrush(c);
    }
}
