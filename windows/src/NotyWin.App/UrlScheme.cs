using System.Web;
using Microsoft.Win32;

namespace NotyWin.App;

/// <summary>
/// Registers and dispatches the <c>noty://</c> URL scheme.
/// Supported routes:
///   noty://new?text=…     — create a note with this text
///   noty://capture        — open Quick Capture
///   noty://all            — open All Notes
///   noty://settings       — open Settings
/// </summary>
public sealed class UrlScheme
{
    private const string SchemeName = "noty";
    private const string ProgId = "NotyWin.Url";
    private const string CommandKey = @"Software\Classes\noty\shell\open\command";

    public Action<string>? OnNew { get; set; }
    public Action? OnCapture { get; set; }
    public Action? OnAll { get; set; }
    public Action? OnSettings { get; set; }

    /// <summary>Register the noty:// scheme so the OS routes URLs to this app.</summary>
    public static void Register(string exePath)
    {
        using (var prog = Registry.CurrentUser.CreateSubKey(@"Software\Classes\noty"))
        {
            prog.SetValue("", "URL:Noty Protocol");
            prog.SetValue("URL Protocol", "");
        }
        using (var icon = Registry.CurrentUser.CreateSubKey(@"Software\Classes\noty\DefaultIcon"))
        {
            icon.SetValue("", $"\"{exePath}\",0");
        }
        using (var cmd = Registry.CurrentUser.CreateSubKey(CommandKey))
        {
            cmd.SetValue("", $"\"{exePath}\" \"%1\"");
        }
    }

    public static void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\noty", throwOnMissingSubKey: false);
    }

    /// <summary>Parse and dispatch a noty:// URL passed via command-line.</summary>
    public void Handle(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme != SchemeName) return;
        var host = uri.Host.ToLowerInvariant();
        switch (host)
        {
            case "new":
                var q = HttpUtility.ParseQueryString(uri.Query);
                var text = q["text"] ?? "";
                OnNew?.Invoke(text);
                break;
            case "capture": OnCapture?.Invoke(); break;
            case "all": OnAll?.Invoke(); break;
            case "settings": OnSettings?.Invoke(); break;
        }
    }
}
