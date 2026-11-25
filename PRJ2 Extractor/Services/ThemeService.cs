using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Styling;

namespace PRJ2_Extractor.Services;

/// <summary>What the user picked: follow the operating system, or force one scheme.</summary>
public enum ThemeChoice { System, Light, Dark }

/// <summary>
/// Light / dark colour scheme. Avalonia already follows the operating system's light / dark setting while the app runs
/// (ThemeVariant.Default), so a choice is just the requested theme variant of the application; the palettes are the
/// Light / Dark theme dictionaries of App.axaml. The choice is remembered in settings.json in the application data folder.
/// </summary>
public static class ThemeService
{
    private sealed class Settings
    {
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ThemeChoice Theme { get; set; } = ThemeChoice.System;
    }

    private static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PRJ2 Extractor", "settings.json");

    /// <summary>The user's choice (System when nothing was saved).</summary>
    public static ThemeChoice Choice { get; private set; } = ThemeChoice.System;

    /// <summary>Reads the saved choice and applies it. Call it before the first window is created.</summary>
    public static void Initialize(Application app)
    {
        Choice = Load();
        Apply(app);
    }

    public static void SetChoice(ThemeChoice choice)
    {
        if (Choice == choice) return;
        Choice = choice;
        Save(choice);
        if (Application.Current is { } app) Apply(app);
    }

    private static void Apply(Application app) =>
        app.RequestedThemeVariant = Choice switch
        {
            ThemeChoice.Light => ThemeVariant.Light,
            ThemeChoice.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };

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
}
