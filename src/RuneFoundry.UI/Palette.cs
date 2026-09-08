using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace RuneFoundry.UI;

public enum AppTheme
{
    Dark,
    Light,

    /// <summary>Follow whatever Windows is set to, and keep following it.</summary>
    System,
}

/// <summary>
/// The application's colours, in two sets.
///
/// The colour brushes are referenced with DynamicResource throughout the app, so replacing
/// one in the application's resources repaints everything already on screen. Mutating the
/// brushes in place would be neater but does not work: WPF freezes Freezables declared in
/// a compiled ResourceDictionary, and a frozen brush silently refuses the assignment.
///
/// The two checkerboard colours are the exception. They live inside a DrawingBrush, which
/// is frozen as a whole and cannot hold a DynamicResource, so they stay as they are.
///
/// Both sets are a cool grey with a little blue in it, and the accent is a blue that is
/// lightened for the dark set and darkened for the light one so it keeps its contrast
/// against the surface behind it either way.
/// </summary>
public static class Palette
{
    private static readonly (string Key, string Dark, string Light)[] Colours =
    {
        //               dark        light
        ("BgDeep",      "#16181C", "#E9EBEF"),
        ("Bg",          "#1C1F24", "#F3F5F8"),
        ("Panel",       "#22262C", "#FBFCFD"),
        ("PanelRaised", "#2A2F37", "#E8EBF0"),
        ("Border",      "#363C46", "#CDD3DC"),
        ("Accent",      "#6AA6F0", "#1F5FAF"),
        ("AccentDim",   "#40628F", "#7C97BE"),
        ("Text",        "#E6E9EE", "#1A1D22"),
        ("TextDim",     "#9BA3AF", "#5A6270"),
        ("Good",        "#6FBF73", "#2E7D32"),
        ("Warn",        "#E0A030", "#8A5A00"),
        ("Bad",         "#E0685E", "#B3261E"),
        ("Selection",   "#2C3A4E", "#D6E3F5"),
        ("CheckerA",    "#191C21", "#E4E7EC"),
        ("CheckerB",    "#202429", "#EFF1F4"),
        // The primary button is a solid accent chip; its label has to invert with it.
        ("OnAccent",    "#16181C", "#FFFFFF"),
    };

    /// <summary>Whether the palette in force is the dark one.</summary>
    public static bool IsDark { get; private set; } = true;

    /// <summary>Raised after a repaint, for the parts of the window WPF does not own.</summary>
    public static event Action? Changed;

    /// <summary>Repaints the running application. Safe to call at any time.</summary>
    public static void Apply(AppTheme theme)
    {
        var resources = Application.Current?.Resources;
        if (resources is null) return;

        var light = theme switch
        {
            AppTheme.Light => true,
            AppTheme.Dark => false,
            _ => WindowsPrefersLight(),
        };

        IsDark = !light;

        foreach (var (key, dark, pale) in Colours)
        {
            var colour = (Color)ColorConverter.ConvertFromString(light ? pale : dark);
            resources[key] = new SolidColorBrush(colour);
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Keeps a window's title bar in step with the palette.
    ///
    /// The title bar is drawn by Windows, not by WPF, so a dark window otherwise wears a
    /// white caption. Windows will draw it dark on request, through a DWM attribute that
    /// can only be set once the window has a handle — hence the wait for SourceInitialized
    /// on a window that has not been shown yet.
    /// </summary>
    public static void FollowTitleBar(Window window)
    {
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Paint(window);
        else window.SourceInitialized += (_, _) => Paint(window);

        void Follow() => Paint(window);
        Changed += Follow;
        window.Closed += (_, _) => Changed -= Follow;
    }

    private static void Paint(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        var dark = IsDark ? 1 : 0;

        // 20 is the attribute on Windows 10 2004 and later; 19 is what the first builds
        // that had it used. Setting both costs nothing and covers either.
        foreach (var attribute in new[] { 20, 19 })
            DwmSetWindowAttribute(handle, attribute, ref dark, sizeof(int));

        // Nothing repaints the caption on its own, so nudge the window to redraw it.
        if (window.IsVisible)
        {
            const int flags = 0x0002 | 0x0001 | 0x0004 | 0x0020;   // no move/size/z, frame changed
            SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, flags);
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int cx, int cy, uint flags);

    /// <summary>
    /// Windows' own app-theme setting. Absent or unreadable is treated as dark, which is
    /// what this app looked like before the setting existed.
    /// </summary>
    public static bool WindowsPrefersLight()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }

    public static AppTheme Parse(string? name) => name?.ToLowerInvariant() switch
    {
        "light" => AppTheme.Light,
        "dark" => AppTheme.Dark,
        _ => AppTheme.System,
    };

    public static string Name(AppTheme theme) => theme.ToString().ToLowerInvariant();
}
