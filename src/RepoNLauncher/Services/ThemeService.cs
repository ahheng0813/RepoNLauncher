using System.Windows.Interop;
using Microsoft.Win32;
using Application = System.Windows.Application;
using ResourceDictionary = System.Windows.ResourceDictionary;
using Window = System.Windows.Window;

namespace RepoNLauncher.Services;

/// <summary>The app's colors, picked in Settings, General. Saved to the config as a number, so new choices go at the end.</summary>
public enum AppTheme
{
    Dark,
    Light,
    /// <summary>Dark or light as Windows' "Choose your app mode" says, changing when it does.</summary>
    MatchWindows,
}

/// <summary>
/// Puts the dark or light palette (Theme\Palette.Dark.xaml or Palette.Light.xaml) under the app's styles. Every color
/// in XAML is a DynamicResource, so open windows recolor as it changes; their title bars are redrawn to match, and
/// <see cref="Changed"/> tells what draws colors in code (the run console, chat replies) to draw them again.
/// </summary>
public static class ThemeService
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static readonly Uri DarkPalette = new("pack://application:,,,/Theme/Palette.Dark.xaml", UriKind.Absolute);
    private static readonly Uri LightPalette = new("pack://application:,,,/Theme/Palette.Light.xaml", UriKind.Absolute);

    private static bool _listening;

    public static AppTheme Choice { get; private set; } = AppTheme.Dark;

    /// <summary>The light palette is showing (picked, or Windows' while matching it).</summary>
    public static bool IsLight { get; private set; }

    /// <summary>Raised on the UI thread once the other palette is showing.</summary>
    public static event EventHandler? Changed;

    public static void Apply(AppTheme choice)
    {
        Choice = Enum.IsDefined(choice) ? choice : AppTheme.Dark;
        if (Choice == AppTheme.MatchWindows && !_listening)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _listening = true;
        }
        Show(Choice switch
        {
            AppTheme.Light => true,
            AppTheme.MatchWindows => WindowsUsesLightTheme(),
            _ => false,
        });
    }

    private static void Show(bool light)
    {
        var merged = Application.Current.Resources.MergedDictionaries;
        var index = merged.ToList().FindIndex(IsPalette);
        var wanted = light ? LightPalette : DarkPalette;
        if (index >= 0 && merged[index].Source is { } shown && shown.OriginalString.EndsWith(FileName(wanted), StringComparison.OrdinalIgnoreCase))
        {
            IsLight = light;
            return;
        }

        var palette = new ResourceDictionary { Source = wanted };
        if (index >= 0) merged[index] = palette;
        else merged.Insert(0, palette);
        IsLight = light;

        foreach (Window window in Application.Current.Windows)
            NativeMethods.UseThemedTitleBar(new WindowInteropHelper(window).Handle);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static bool IsPalette(ResourceDictionary dictionary) =>
        dictionary.Source?.OriginalString is { } source
        && (source.EndsWith(FileName(DarkPalette), StringComparison.OrdinalIgnoreCase)
            || source.EndsWith(FileName(LightPalette), StringComparison.OrdinalIgnoreCase));

    private static string FileName(Uri palette) => palette.Segments[^1];

    // Windows' app mode lives in the registry; with no value there, apps are light.
    private static bool WindowsUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Raised for every kind of setting; reading the one value again is cheap, and Show does nothing when it's the same.
    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) =>
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (Choice == AppTheme.MatchWindows) Show(WindowsUsesLightTheme());
        });
}
