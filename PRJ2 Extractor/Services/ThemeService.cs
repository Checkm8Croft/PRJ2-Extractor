using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using Microsoft.Win32;

namespace PRJ2_Extractor.Services;

/// <summary>The colour scheme actually shown.</summary>
public enum AppTheme { Light, Dark }

/// <summary>What the user picked: follow Windows, or force one scheme.</summary>
public enum ThemeChoice { System, Light, Dark }

/// <summary>
/// Light / dark colour scheme. A theme is one palette dictionary (Themes/Light.xaml or Themes/Dark.xaml) that replaces the
/// previous one in the application resources, plus the matching Fluent theme for the built-in controls (scroll bars,
/// tool tips...). By default the app follows the Windows "app mode" setting and switches while it is running; the choice
/// is remembered in %APPDATA%\PRJ2 Extractor\settings.json.
/// </summary>
public static class ThemeService
{
    private sealed class Settings
    {
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ThemeChoice Theme { get; set; } = ThemeChoice.System;
    }

    private static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PRJ2 Extractor", "settings.json");

    private static bool _applied;

    /// <summary>The user's choice (System when nothing was saved).</summary>
    public static ThemeChoice Choice { get; private set; } = ThemeChoice.System;

    /// <summary>The scheme in use: the choice itself, or what Windows is set to when the choice is System.</summary>
    public static AppTheme Current { get; private set; } = AppTheme.Light;

    /// <summary>Reads the saved choice, applies it and starts following Windows. Call it before the first window is created.</summary>
    public static void Initialize()
    {
        Choice = Load();
        Apply();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public static void Shutdown() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

    public static void SetChoice(ThemeChoice choice)
    {
        if (Choice == choice) return;
        Choice = choice;
        Save(choice);
        Apply();
    }

    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        // Raised on a different thread, and for many unrelated settings: only re-apply when following Windows.
        if (Choice == ThemeChoice.System)
            Application.Current?.Dispatcher.BeginInvoke(Apply);
    }

    /// <summary>true when Windows is set to the dark app mode (Settings > Personalization > Colors); light when unknown.</summary>
    public static bool SystemUsesDarkTheme()
    {
        try
        {
            return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int light && light == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void Apply()
    {
        var app = Application.Current;
        if (app == null) return;

        var theme = Choice switch
        {
            ThemeChoice.Light => AppTheme.Light,
            ThemeChoice.Dark => AppTheme.Dark,
            _ => SystemUsesDarkTheme() ? AppTheme.Dark : AppTheme.Light
        };
        if (_applied && theme == Current) return; // nothing changed: avoid a needless repaint

        var merged = app.Resources.MergedDictionaries;
        var palette = new ResourceDictionary { Source = new Uri($"Themes/{theme}.xaml", UriKind.Relative) };
        int index = merged.ToList().FindIndex(d => d.Source != null && IsPalette(d.Source));
        if (index >= 0) { merged.RemoveAt(index); merged.Insert(index, palette); }
        else merged.Insert(0, palette);

        app.ThemeMode = theme == AppTheme.Dark ? ThemeMode.Dark : ThemeMode.Light;
        Current = theme;
        _applied = true;
    }

    /// <summary>The saved choice; System when nothing was saved or the file cannot be read.</summary>
    private static ThemeChoice Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath))?.Theme ?? ThemeChoice.System;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // a damaged settings file must never stop the program from starting
        }
        return ThemeChoice.System;
    }

    private static void Save(ThemeChoice choice)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Settings { Theme = choice }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the choice just is not remembered
        }
    }

    private static bool IsPalette(Uri source) =>
        source.OriginalString.EndsWith("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase) ||
        source.OriginalString.EndsWith("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase);
}
