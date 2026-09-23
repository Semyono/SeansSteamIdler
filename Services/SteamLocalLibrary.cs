using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SeansSteamIdler.Models;

namespace SeansSteamIdler.Services;

public static class SteamLocalLibrary
{
    public static string? GetSteamPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        return key?.GetValue("SteamPath") as string;
    }

    public static List<string> GetLibraryFolders(string steamPath)
    {
        var folders = new List<string> { steamPath };

        var vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdfPath))
            return folders;

        var text = File.ReadAllText(vdfPath);
        foreach (Match m in Regex.Matches(text, "\"path\"\\s*\"(.*?)\""))
        {
            var path = m.Groups[1].Value.Replace("\\\\", "\\");
            if (!folders.Contains(path))
                folders.Add(path);
        }

        return folders;
    }

    public static List<GameEntry> GetInstalledGames()
    {
        var games = new List<GameEntry>();

        var steamPath = GetSteamPath();
        if (steamPath is null || !Directory.Exists(steamPath))
            return games;

        foreach (var library in GetLibraryFolders(steamPath))
        {
            var appsDir = Path.Combine(library, "steamapps");
            if (!Directory.Exists(appsDir))
                continue;

            foreach (var manifest in Directory.GetFiles(appsDir, "appmanifest_*.acf"))
            {
                try
                {
                    var text = File.ReadAllText(manifest);
                    var appIdMatch = Regex.Match(text, "\"appid\"\\s*\"(\\d+)\"");
                    var nameMatch = Regex.Match(text, "\"name\"\\s*\"(.*?)\"");

                    if (appIdMatch.Success && nameMatch.Success)
                    {
                        games.Add(new GameEntry
                        {
                            AppId = uint.Parse(appIdMatch.Groups[1].Value),
                            Name = nameMatch.Groups[1].Value
                        });
                    }
                }
                catch
                {
                }
            }
        }

        games.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return games;
    }

    public static string? GetLastLoggedInAccountName()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
        return key?.GetValue("ActiveUser") is int id && id != 0
            ? null 
            : null;
    }
}
