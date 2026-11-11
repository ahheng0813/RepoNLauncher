using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace RepoNLauncher.Services;

/// <summary>The browser Windows opens links in, as set in its Settings > Apps > Default apps.</summary>
public static class DefaultBrowser
{
    private const uint AssocFIsProtocol = 0x1000;     // ASSOCF_IS_PROTOCOL: https is a scheme, not a file type
    private const uint AssocStrFriendlyAppName = 4;   // ASSOCSTR_FRIENDLYAPPNAME

    /// <summary>Its name, such as Google Chrome or Microsoft Edge; null when Windows doesn't say.</summary>
    public static string? Name
    {
        get
        {
            foreach (var scheme in new[] { "https", "http" })
            {
                uint size = 0;
                AssocQueryString(AssocFIsProtocol, AssocStrFriendlyAppName, scheme, "open", null, ref size);
                if (size == 0) continue;
                var name = new StringBuilder((int)size);
                if (AssocQueryString(AssocFIsProtocol, AssocStrFriendlyAppName, scheme, "open", name, ref size) == 0 && name.Length > 0)
                    return name.ToString();
            }
            return null;
        }
    }

    /// <summary>Default browser (Google Chrome), for a list of where to open a site.</summary>
    public static string Label => Name is { } name ? $"Default browser ({name})" : "Default browser";

    /// <summary>Open in Google Chrome, for a menu.</summary>
    public static string OpenLabel => Name is { } name ? $"Open in {name}" : "Open in your default browser";

    public static void Open(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryString(uint flags, uint str, string assoc, string? extra, StringBuilder? output, ref uint outputSize);
}
