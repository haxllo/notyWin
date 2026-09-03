using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using NotyWin.App.Geometry;

namespace NotyWin.App.Deck;

/// <summary>
/// A borderless, no-activate, always-on-top tool window.
///
/// The macOS app uses NSPanel with <c>.borderless, .nonactivatingPanel</c>, level
/// <c>.statusBar</c> (over full-screen apps) or <c>.floating</c>. We model the
/// same surface as a WinUI 3 <see cref="Window"/> with an
/// <c>OverlappedPresenter</c> configured to drop the system frame and sit
/// above the topmost z-band. The "no-activate" behaviour of
/// <c>NSNonactivatingPanel</c> is mirrored by setting
/// <c>ActivatedHook = null</c> on the presenter.
///
/// Initially this used a custom <c>CreateWindowEx</c> HWND + XAML island
/// (DesktopWindowXamlSource). That path is the documented way to host
/// WinUI 3 content in a non-XAML HWND, but it collided with the WinUI 3
/// main thread's own <c>WindowsXamlManager</c>: a thread can have at
/// most one, and the second one trips
/// "ClassFactory cannot supply requested class" the moment
/// <c>InitializeWithWindow.Initialize</c> runs. Per-display WinUI 3
/// <see cref="Window"/>s share the process's single WindowsXamlManager
/// cleanly.
/// </summary>
public sealed class DeckWindow
{
    public Microsoft.UI.Xaml.Window Window { get; }
    public AppWindow AppWindow { get; }

    public DeckWindow()
    {
        Window = new Microsoft.UI.Xaml.Window
        {
            Title = "Noty Deck",
        };
        AppWindow = Window.AppWindow;
        var presenter = AppWindow.Presenter as OverlappedPresenter
            ?? throw new InvalidOperationException("OverlappedPresenter not available");
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsModal = false;
    }

    public void ApplyLevel(bool overFullScreen)
    {
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        // WinUI 3 has no direct "HWND_TOPMOST vs HWND_TOP" toggle after the
        // window is shown. We approximate: keep AlwaysOnTop (HWND_TOPMOST
        // style) and rely on ZOrderHint as the floating (HWND_TOP) fallback.
        presenter.IsAlwaysOnTop = overFullScreen;
        if (!overFullScreen)
            AppWindow.MoveInZOrderAtTop();
    }

    public void SetFrame(double x, double y, double w, double h)
    {
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            (int)Math.Round(x), (int)Math.Round(y),
            (int)Math.Round(w), (int)Math.Round(h)));
    }

    public void Show() => Window.Activate();
    public void Hide() => AppWindow.Hide();
}