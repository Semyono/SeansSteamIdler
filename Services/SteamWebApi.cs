using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using SeansSteamIdler.Models;

namespace SeansSteamIdler.Services;
public static class SteamWebApi
{
    private static readonly HttpClient Http = new();

    public static async Task<List<GameEntry>> GetOwnedGamesAsync(string apiKey, string steamId64)
    {
        var url = "https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/" +
                   $"?key={apiKey}&steamid={steamId64}&include_appinfo=1&include_played_free_games=1";

        var json = await Http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(json);

        var result = new List<GameEntry>();

        if (!doc.RootElement.TryGetProperty("response", out var response))
            return result;
        if (!response.TryGetProperty("games", out var games))
            return result; // empty/private profile or no games

        foreach (var game in games.EnumerateArray())
        {
            var minutes = game.TryGetProperty("playtime_forever", out var pt) ? pt.GetDouble() : 0;
            result.Add(new GameEntry
            {
                AppId = game.GetProperty("appid").GetUInt32(),
                Name = game.TryGetProperty("name", out var n) ? n.GetString() ?? "Unknown" : "Unknown",
                PlaytimeForeverHours = Math.Round(minutes / 60.0, 1)
            });
        }

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }
    public static async Task<string?> ResolveVanityUrlAsync(string apiKey, string vanityName)
    {
        var url = "https://api.steampowered.com/ISteamUser/ResolveVanityURL/v1/" +
                   $"?key={apiKey}&vanityurl={Uri.EscapeDataString(vanityName)}";

        var json = await Http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(json);
        var response = doc.RootElement.GetProperty("response");

        if (response.TryGetProperty("success", out var success) && success.GetInt32() == 1)
            return response.GetProperty("steamid").GetString();

        return null;
    }
}
