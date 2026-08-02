using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace OpenLogViewer.App;

/// <summary>
/// Colours the window's title bar to match the theme.
///
/// WPF leaves the title bar to Windows, so it stays in the system's colours
/// while everything below it follows the chosen scheme — a white strip above a
/// dark application. Windows 11 lets an app set the caption, its text and the
/// border directly, which is the only way to make the two agree.
///
/// Every call is best-effort. The caption colours arrived in Windows 11 and the
/// dark-mode flag changed number during Windows 10; on anything older these
/// fail and leave the title bar as it was, which is what a system title bar
/// looked like before anyone asked.
/// </summary>
internal static class TitleBar
{
    /// <summary>Dark title bar. 20 on current builds, 19 on early Windows 10.</summary>
    private const int ImmersiveDarkMode = 20;
    private const int ImmersiveDarkModeLegacy = 19;

    /// <summary>Windows 11 only: exact colours, rather than merely light or dark.</summary>
    private const int BorderColour = 34;
    private const int CaptionColour = 35;
    private const int TextColour = 36;

    private const int Ok = 0;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);

    public static void Apply(Window window, Theme theme)
    {
        nint handle = new WindowInteropHelper(window).Handle;

        // Before the handle exists there is nothing to colour; the caller tries
        // again once the window is sourced.
        if (handle == nint.Zero) return;

        int dark = theme.IsDark ? 1 : 0;
        if (Set(handle, ImmersiveDarkMode, dark) != Ok) Set(handle, ImmersiveDarkModeLegacy, dark);

        // The panel rather than the plot ground: the title bar sits directly
        // above the toolbar, and matching its neighbour is what stops the seam
        // showing.
        Set(handle, CaptionColour, ColorRef(theme.Panel));
        Set(handle, TextColour, ColorRef(theme.Text));
        Set(handle, BorderColour, ColorRef(theme.Line));
    }

    private static int Set(nint window, int attribute, int value) =>
        DwmSetWindowAttribute(window, attribute, ref value, sizeof(int));

    /// <summary>A Win32 COLORREF, which is 0x00BBGGRR rather than the usual order.</summary>
    private static int ColorRef(Color colour) => colour.R | (colour.G << 8) | (colour.B << 16);
}
