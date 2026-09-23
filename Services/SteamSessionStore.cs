using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Generic;

using SeansSteamIdler.Models;

namespace SeansSteamIdler.Services;

public sealed record SteamSession(string Username, string RefreshToken);
public sealed record IdlePreset(string PresetName, List<uint> AppIds);

public static class SteamSessionStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SeansSteamIdler.Session.v1");

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SeansSteamIdler",
        "session.json");

    private static string LegacyFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SeansSteamIdler",
        "session.dat");

    private static string SettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SeansSteamIdler");

    private static string FavoritesPath => Path.Combine(SettingsDirectory, "favorites.json");
    private static string ThemePath => Path.Combine(SettingsDirectory, "theme.json");
    private static string ThemeModePath => Path.Combine(SettingsDirectory, "theme-mode.json");
    private static string PersonaStatusPath => Path.Combine(SettingsDirectory, "persona-status.json");
    private static string PresetsPath => Path.Combine(SettingsDirectory, "presets.json");

    public static List<IdlePreset> LoadPresets()
    {
        try
        {
            if (!File.Exists(PresetsPath)) return new List<IdlePreset>();
            return JsonSerializer.Deserialize<List<IdlePreset>>(File.ReadAllText(PresetsPath)) ?? new List<IdlePreset>();
        }
        catch
        {
            return new List<IdlePreset>();
        }
    }

    public static void SavePresets(IEnumerable<IdlePreset> presets)
    {
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(PresetsPath, JsonSerializer.Serialize(presets));
    }

    public static string? LoadPersonaStatus()
    {
        try
        {
            return File.Exists(PersonaStatusPath)
                ? JsonSerializer.Deserialize<string>(File.ReadAllText(PersonaStatusPath))
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static void SavePersonaStatus(string status)
    {
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(PersonaStatusPath, JsonSerializer.Serialize(status));
    }

    public static HashSet<uint> LoadFavoriteAppIds()
    {
        try
        {
            if (!File.Exists(FavoritesPath)) return new HashSet<uint>();
            return JsonSerializer.Deserialize<HashSet<uint>>(File.ReadAllText(FavoritesPath)) ?? new HashSet<uint>();
        }
        catch
        {
            return new HashSet<uint>();
        }
    }

    public static void SaveFavoriteAppIds(IEnumerable<uint> appIds)
    {
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(FavoritesPath, JsonSerializer.Serialize(appIds));
    }

    public static string? LoadTheme()
    {
        try
        {
            return File.Exists(ThemePath)
                ? JsonSerializer.Deserialize<string>(File.ReadAllText(ThemePath))
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static void SaveTheme(string theme)
    {
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(ThemePath, JsonSerializer.Serialize(theme));
    }

    public static string? LoadThemeMode()
    {
        try
        {
            return File.Exists(ThemeModePath)
                ? JsonSerializer.Deserialize<string>(File.ReadAllText(ThemeModePath))
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static void SaveThemeMode(string mode)
    {
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(ThemeModePath, JsonSerializer.Serialize(mode));
    }

    public static void Save(SteamSession session)
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);

        var plaintext = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(session));
        var protectedBytes = ProtectedData.Protect(
            plaintext,
            Entropy,
            DataProtectionScope.CurrentUser);

        var temporaryPath = FilePath + ".tmp";
        File.WriteAllBytes(temporaryPath, protectedBytes);
        File.Move(temporaryPath, FilePath, true);
    }

    public static bool HasSavedSession()
    {
        return File.Exists(FilePath) || File.Exists(LegacyFilePath);
    }

    public static SteamSession? Load()
    {
        try
        {
            var path = File.Exists(FilePath) ? FilePath : LegacyFilePath;
            if (!File.Exists(path))
                return null;

            var protectedBytes = File.ReadAllBytes(path);
            var plaintext = ProtectedData.Unprotect(
                protectedBytes,
                Entropy,
                DataProtectionScope.CurrentUser);

            return JsonSerializer.Deserialize<SteamSession>(plaintext);
        }
        catch
        {
            Delete();
            return null;
        }
    }

    public static void Delete()
    {
        try
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
            if (File.Exists(LegacyFilePath))
                File.Delete(LegacyFilePath);
        }
        catch
        {
        }
    }
}