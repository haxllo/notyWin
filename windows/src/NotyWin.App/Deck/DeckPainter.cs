using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using NotyWin.App.Geometry;
using NotyWin.App.Models;
using NotyWin.Rendering;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Text;
using RenderDeckFrame = NotyWin.Rendering.DeckFrame;

namespace NotyWin.App.Deck;

/// <summary>
/// Font weights used in the deck. WinUI 3 projection doesn't expose
/// <c>FontWeights.SemiBold</c> reliably, so use the raw struct.
/// </summary>
internal static class Weight
{
    public static readonly FontWeight SemiBold = new() { Weight = 600 };
    public static readonly FontWeight Bold = new() { Weight = 700 };
}

/// <summary>
/// Pure painter: takes a <see cref="RenderDeckFrame"/> and draws every item
/// via Win2D primitives. Mirrors the SwiftUI layout in
/// Sources/DeckViews.swift 1:1.
/// </summary>
public sealed class DeckPainter
{
    private static readonly Color BgMaterial = Color.FromArgb(0x99, 0x00, 0x00, 0x00);
    private static readonly Color EdgeSpineColor = Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF);
    private static readonly Color Secondary = Color.FromArgb(0xBF, 0x80, 0x80, 0x80);
    private static readonly Color PlusFg = Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF);

    public void Paint(CanvasDrawingSession ds, RenderDeckFrame frame, double panelWidth)
    {
        DeckLog.Write("PAINT", $"Painting {frame.Items.Count} items, panelW={panelWidth}");
        foreach (var ri in frame.Items)
        {
            DeckLog.Write("PAINT", $"  {ri.Kind} ({ri.X},{ri.Y}) {ri.Width}x{ri.Height} note={ri.Note?.Id?[..8]}");
        }
        foreach (var ri in frame.Items)
        {
            switch (ri.Kind)
            {
                case RenderItemKind.Pill: PaintPill(ds, ri); break;
                case RenderItemKind.Tab: PaintTab(ds, ri); break;
                case RenderItemKind.ChipTab: PaintChipTab(ds, ri); break;
                case RenderItemKind.EmptyTab: PaintEmptyTab(ds, ri); break;
                case RenderItemKind.MoreTab: PaintMoreTab(ds, ri); break;
                case RenderItemKind.PlusButton: PaintPlus(ds, ri); break;
                case RenderItemKind.CogButton: PaintCog(ds, ri); break;
                case RenderItemKind.EdgeSpine: PaintSpine(ds, ri); break;
                case RenderItemKind.NotePreview: PaintPreview(ds, ri); break;
                // ExpandedNote is not painted: the open note is a real XAML
                // editor (NoteEditorControl) overlaid on the canvas, since
                // Win2D cannot take keyboard input. The item stays in the frame
                // so its rect is hit-testable (HTCLIENT) and clicks reach it.
            }
        }
    }

    // MARK: Pill

    private static void PaintPill(CanvasDrawingSession ds, RenderItem r)
    {
        var rect = new Rect(r.X, r.Y, r.Width, r.Height);
        // Use a lighter pill background so coloured dashes are visible against it.
        ds.FillRoundedRectangle(rect, 6, 6, Color.FromArgb(0xCC, 0x20, 0x20, 0x20));

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
            // Empty deck — one bright secondary dash, clearly visible.
            var dashRect = new Rect(r.X + (r.Width - dashW) / 2, y, dashW, dashH);
            ds.FillRoundedRectangle(dashRect, 2.5f, 2.5f, Color.FromArgb(0xCC, Secondary.R, Secondary.G, Secondary.B));
            return;
        }

        for (var i = 0; i < colors.Count; i++)
        {
            var dashRect = new Rect(r.X + (r.Width - dashW) / 2, y, dashW, dashH);
            ds.FillRoundedRectangle(dashRect, 2.5f, 2.5f, ColorFromArgb(colors[i]));
            y += dashH + dashGap;
        }
        if (overflow)
        {
            var dashRect = new Rect(r.X + (r.Width - dashW) / 2, y, dashW, dashH);
            ds.FillRoundedRectangle(dashRect, 2.5f, 2.5f, Color.FromArgb(0x99, Secondary.R, Secondary.G, Secondary.B));
        }
    }

    private static bool NotesEmpty(RenderItem r) => r.DashColors is { Count: 0 };

    // MARK: Tab

    private static void PaintTab(CanvasDrawingSession ds, RenderItem r)
    {
        var note = r.Note!;
        var color = ColorFromArgb(note.Palette.PaperArgb);
        var ink = ColorFromArgb(note.Palette.InkArgb);
        var dash = ColorFromArgb(note.Palette.DashArgb);

        // Anchor at the edge that meets the screen so the lean and the lift
        // both feel like they pivot against the screen.
        var cx = (float)(r.X + (r.Width + (r.Lifted ? 0 : -DeckGeom.Bleed)) / 2);
        var cy = (float)(r.Y + r.Height / 2);

        var save = ds.Transform;
        var m = Matrix3x2.CreateTranslation(cx, cy)
              * Matrix3x2.CreateRotation((float)DeckGeom.Lean(true) * (MathF.PI / 180f))
              * Matrix3x2.CreateScale((float)(r.Lifted ? 1.04 : 1.0), (float)(r.Lifted ? 1.04 : 1.0))
              * Matrix3x2.CreateTranslation(-cx, -cy);
        ds.Transform = save * m;

        var geo = TabGeo(r.X, r.Y, r.Width, r.Height, onRight: true);
        ds.FillGeometry(geo, color);

        var shadowOpacity = r.Lifted ? 0.42 : (r.IsOpen || r.Hovering ? 0.32 : 0.22);
        ds.DrawGeometry(geo, Color.FromArgb((byte)(shadowOpacity * 255), 0, 0, 0), 6);

        // Rotated label.
        var labelFont = new CanvasTextFormat
        {
            FontFamily = "Segoe UI",
            FontSize = (float)DeckGeom.TabSize(),
            FontWeight = Weight.SemiBold,
        };
        var label = note.DisplayTitle("Untitled").ToUpperInvariant();
        var labelStrip = Math.Max(20, DeckGeom.PitchMax - DeckGeom.LabelInset);
        var labelX = (float)(r.X + (r.Width - labelStrip + DeckGeom.LabelInset) / 2);
        var labelY = (float)(r.Y + DeckGeom.Bleed / 2);

        var textLayout = new CanvasTextLayout(ds, label, labelFont, (float)labelStrip, (float)DeckGeom.TabWidth)
        {
            TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
        };

        var innerCx = (float)(r.X + r.Width / 2);
        var innerCy = (float)(r.Y + r.Height / 2);
        ds.Transform = save * m * Matrix3x2.CreateRotation(90f, new Vector2(innerCx, innerCy));
        ds.DrawTextLayout(textLayout, labelX, labelY, ink);
        ds.Transform = save;

        if (r.Pinned)
        {
            ds.FillCircle((float)(r.X + r.Width - 9), (float)(r.Y + 12), 2.5f, dash);
        }
    }

    // MARK: ChipTab

    private static void PaintChipTab(CanvasDrawingSession ds, RenderItem r)
    {
        var dash = ColorFromArgb(r.Note!.Palette.DashArgb);
        var geo = CanvasGeometry.CreateRoundedRectangle(null,
            new Rect(r.X, r.Y, DeckGeom.ChipWidth, DeckGeom.ChipHeight), 7, 7);
        ds.FillGeometry(geo, dash);
        var shadowOpacity = r.IsOpen ? 0.34 : 0.22;
        ds.DrawGeometry(geo, Color.FromArgb((byte)(shadowOpacity * 255), 0, 0, 0), 5);
    }

    // MARK: EmptyTab

    private static void PaintEmptyTab(CanvasDrawingSession ds, RenderItem r)
    {
        var geo = TabGeo(r.X, r.Y, r.Width, r.Height, onRight: true);
        ds.FillGeometry(geo, BgMaterial);
        var format = new CanvasTextFormat
        {
            FontFamily = "Segoe UI",
            FontSize = (float)DeckGeom.TabSize(),
            FontWeight = Weight.SemiBold,
        };
        ds.DrawText("NEW NOTE", (float)(r.X + (r.Width - 80) / 2), (float)(r.Y + r.Height / 2 - 6), Secondary, format);
    }

    // MARK: MoreTab

    private static void PaintMoreTab(CanvasDrawingSession ds, RenderItem r)
    {
        var geo = CanvasGeometry.CreateRoundedRectangle(null,
            new Rect(r.X, r.Y, r.Width, r.Height), 9, 9);
        ds.FillGeometry(geo, BgMaterial);
        var format = new CanvasTextFormat
        {
            FontFamily = "Segoe UI",
            FontSize = 10,
            FontWeight = Weight.SemiBold,
        };
        ds.DrawText("+" + r.HiddenCount, (float)(r.X + r.Width / 2 - 10), (float)(r.Y + r.Height / 2 - 7), Secondary, format);
    }

    // MARK: PlusButton / CogButton

    private static void PaintPlus(CanvasDrawingSession ds, RenderItem r)
    {
        var bg = Color.FromArgb(0x99, 0x80, 0x80, 0x80);
        ds.FillCircle((float)(r.X + r.Width / 2), (float)(r.Y + r.Height / 2), (float)(r.Width / 2), bg);
        var format = new CanvasTextFormat
        {
            FontFamily = "Segoe UI",
            FontSize = 11,
            FontWeight = Weight.SemiBold,
        };
        ds.DrawText("+", (float)(r.X + r.Width / 2 - 5), (float)(r.Y + r.Height / 2 - 7), PlusFg, format);
    }

    private static void PaintCog(CanvasDrawingSession ds, RenderItem r)
    {
        var bg = Color.FromArgb(0x99, 0x80, 0x80, 0x80);
        ds.FillCircle((float)(r.X + r.Width / 2), (float)(r.Y + r.Height / 2), (float)(r.Width / 2), bg);
        var format = new CanvasTextFormat
        {
            FontFamily = "Segoe UI",
            FontSize = 10,
            FontWeight = Weight.SemiBold,
        };
        ds.DrawText("\u2699", (float)(r.X + r.Width / 2 - 6), (float)(r.Y + r.Height / 2 - 7), PlusFg, format);
    }

    // MARK: EdgeSpine

    private static void PaintSpine(CanvasDrawingSession ds, RenderItem r)
    {
        var style = new CanvasStrokeStyle
        {
            CustomDashStyle = new[] { 3f, 4f },
        };
        ds.DrawLine((float)(r.X + r.Width / 2), (float)r.Y, (float)(r.X + r.Width / 2), (float)(r.Y + r.Height),
            EdgeSpineColor, 1, style);
    }

    // MARK: NotePreview

    private static void PaintPreview(CanvasDrawingSession ds, RenderItem r)
    {
        var n = r.Note!;
        var paper = ColorFromArgb(n.Palette.PaperArgb);
        var ink = ColorFromArgb(n.Palette.InkArgb);
        ds.FillRoundedRectangle(new Rect(r.X, r.Y, r.Width, r.Height), 8, 8, paper);
        ds.DrawRoundedRectangle(new Rect(r.X, r.Y, r.Width, r.Height), 8, 8,
            Color.FromArgb(0x1F, ink.R, ink.G, ink.B), 1);
        var titleFont = new CanvasTextFormat
        {
            FontFamily = "Segoe UI",
            FontSize = 11.5f,
            FontWeight = Weight.Bold,
        };
        ds.DrawText(n.DisplayTitle("Untitled"), (float)(r.X + 10), (float)(r.Y + 9), ink, titleFont);
    }

    // MARK: Paths

    private static CanvasGeometry TabGeo(double x, double y, double w, double h, bool onRight, double radius = 11)
    {
        var rect = new Rect(x, y, w, h);
        // Rounded only on the outward-facing side (right edge of screen).
        return BuildRoundedRectPath(rect, onRight ? 0 : radius, onRight ? radius : 0, onRight ? radius : 0, onRight ? 0 : radius);
    }

    private static CanvasGeometry BuildRoundedRectPath(Rect r, double tl, double tr, double br, double bl)
    {
        using var builder = new CanvasPathBuilder(null);
        var x = r.X; var y = r.Y; var w = r.Width; var h = r.Height;
        builder.BeginFigure((float)(x + tl), (float)y);
        builder.AddLine((float)(x + w - tr), (float)y);
        if (tr > 0) builder.AddArc(new Vector2((float)(x + w), (float)(y + tr)), (float)tr, (float)tr, 0, CanvasSweepDirection.Clockwise, CanvasArcSize.Small);
        builder.AddLine((float)(x + w), (float)(y + h - br));
        if (br > 0) builder.AddArc(new Vector2((float)(x + w - br), (float)(y + h)), (float)br, (float)br, 0, CanvasSweepDirection.Clockwise, CanvasArcSize.Small);
        builder.AddLine((float)(x + bl), (float)(y + h));
        if (bl > 0) builder.AddArc(new Vector2((float)x, (float)(y + h - bl)), (float)bl, (float)bl, 0, CanvasSweepDirection.Clockwise, CanvasArcSize.Small);
        builder.AddLine((float)x, (float)(y + tl));
        if (tl > 0) builder.AddArc(new Vector2((float)(x + tl), (float)y), (float)tl, (float)tl, 0, CanvasSweepDirection.Clockwise, CanvasArcSize.Small);
        builder.EndFigure(CanvasFigureLoop.Closed);
        return CanvasGeometry.CreatePath(builder);
    }

    private static Color ColorFromArgb(int argb) => Color.FromArgb(
        (byte)((argb >> 24) & 0xFF),
        (byte)((argb >> 16) & 0xFF),
        (byte)((argb >> 8) & 0xFF),
        (byte)(argb & 0xFF));
}
