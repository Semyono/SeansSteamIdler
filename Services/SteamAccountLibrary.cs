using System;
using System.Collections.Generic;
using System.Linq;
using SteamKit2;
using SeansSteamIdler.Models;

namespace SeansSteamIdler.Services;

public sealed class SteamAccountLibrary
{
    private const int PicsBatchSize = 75;
    private readonly SteamApps _steamApps;
    private readonly Dictionary<uint, ulong> _packageTokens = new();
    private readonly HashSet<uint> _appIds = new();
    private readonly Dictionary<uint, GameEntry> _games = new();
    private readonly Dictionary<uint, ulong> _appTokens = new();
    private List<SteamApps.PICSRequest[]> _packageBatches = new();
    private List<uint[]> _appTokenBatches = new();
    private List<SteamApps.PICSRequest[]> _appBatches = new();
    private int _packageBatchIndex;
    private int _appTokenBatchIndex;
    private int _appBatchIndex;
    private bool _waitingForPackageTokens;
    private PicsStage _stage;

    private enum PicsStage
    {
        None,
        Packages,
        Apps
    }

    public event Action<IReadOnlyList<GameEntry>>? Loaded;
    public event Action? LoadingStarted;
    public event Action<string>? Log;

    public SteamAccountLibrary(CallbackManager callbackManager, SteamApps steamApps)
    {
        _steamApps = steamApps;
        callbackManager.Subscribe<SteamApps.LicenseListCallback>(OnLicenseList);
        callbackManager.Subscribe<SteamApps.PICSTokensCallback>(OnPicsTokens);
        callbackManager.Subscribe<SteamApps.PICSProductInfoCallback>(OnProductInfo);
    }

    private void OnLicenseList(SteamApps.LicenseListCallback callback)
    {
        LoadingStarted?.Invoke();

        if (callback.Result != EResult.OK)
        {
            Log?.Invoke($"Steam returned the account license list result {callback.Result}.");
            return;
        }

        _packageTokens.Clear();
        _appIds.Clear();
        _games.Clear();
        _appTokens.Clear();
        _packageBatches.Clear();
        _appTokenBatches.Clear();
        _appBatches.Clear();
        _stage = PicsStage.None;
        _waitingForPackageTokens = false;
        var packagesMissingTokens = new List<uint>();
        foreach (var license in callback.LicenseList)
        {
            if (license.AccessToken != 0)
                _packageTokens[license.PackageID] = license.AccessToken;
            else
                packagesMissingTokens.Add(license.PackageID);
        }

        if (_packageTokens.Count == 0 && packagesMissingTokens.Count == 0)
        {
            Loaded?.Invoke(Array.Empty<GameEntry>());
            Log?.Invoke("Steam returned no account licenses.");
            return;
        }

        if (packagesMissingTokens.Count > 0)
        {
            _waitingForPackageTokens = true;
            _steamApps.PICSGetAccessTokens(
                Array.Empty<uint>(),
                packagesMissingTokens.Distinct().ToArray());
            return;
        }

        RequestPackageInfo();
    }

    private void OnProductInfo(SteamApps.PICSProductInfoCallback callback)
    {
        if (_stage == PicsStage.Packages)
        {
            foreach (var package in callback.Packages.Values)
            {
                var appIds = package.KeyValues["appids"];
                foreach (var appId in appIds.Children)
                {
                    if (uint.TryParse(appId.Value, out var parsedAppId) ||
                        uint.TryParse(appId.Name, out parsedAppId))
                        _appIds.Add(parsedAppId);
                }
            }

            if (callback.ResponsePending)
                return;

            if (_packageBatchIndex < _packageBatches.Count)
            {
                RequestNextPackageBatch();
                return;
            }

            if (_appIds.Count == 0)
            {
                Loaded?.Invoke(Array.Empty<GameEntry>());
                Log?.Invoke("Steam returned no app IDs for the account licenses.");
                return;
            }

            Log?.Invoke($"Loading metadata for {_appIds.Count} owned app(s)...");
            _appTokenBatches = _appIds.Chunk(PicsBatchSize).ToList();
            _appTokenBatchIndex = 0;
            RequestNextAppTokenBatch();
            return;
        }

        if (_stage != PicsStage.Apps)
            return;

        foreach (var app in callback.Apps.Values)
        {
            var common = app.KeyValues["common"];
            var type = common["type"].AsString();
            var name = common["name"].AsString();

            if (!string.Equals(type, "game", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(name))
                continue;

            _games[app.ID] = new GameEntry
            {
                AppId = app.ID,
                Name = name
            };
        }

        if (callback.ResponsePending)
            return;

        if (_appBatchIndex < _appBatches.Count)
        {
            RequestNextAppBatch();
            return;
        }

        var result = _games.Values
            .OrderBy(game => game.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Loaded?.Invoke(result);
        Log?.Invoke($"Loaded {result.Count} playable owned game(s).");
    }

    private void OnPicsTokens(SteamApps.PICSTokensCallback callback)
    {
        if (_waitingForPackageTokens)
        {
            _waitingForPackageTokens = false;
            foreach (var package in callback.PackageTokens)
                _packageTokens[package.Key] = package.Value;

            if (_packageTokens.Count == 0)
            {
                Loaded?.Invoke(Array.Empty<GameEntry>());
                Log?.Invoke("Steam did not return package metadata access tokens.");
                return;
            }

            RequestPackageInfo();
            return;
        }

        foreach (var app in callback.AppTokens)
            _appTokens[app.Key] = app.Value;

        if (_appTokenBatchIndex < _appTokenBatches.Count)
        {
            RequestNextAppTokenBatch();
            return;
        }

        RequestAppInfo();
    }

    private void RequestPackageInfo()
    {
        Log?.Invoke($"Resolving {_packageTokens.Count} Steam license(s)...");
        _packageBatches = _packageTokens
            .Select(pair => new SteamApps.PICSRequest(pair.Key, pair.Value))
            .Chunk(PicsBatchSize)
            .ToList();
        _packageBatchIndex = 0;
        _stage = PicsStage.Packages;
        RequestNextPackageBatch();
    }

    private void RequestNextPackageBatch()
    {
        var batch = _packageBatches[_packageBatchIndex++];
        _steamApps.PICSGetProductInfo(
            Array.Empty<SteamApps.PICSRequest>(),
            batch,
            false);
    }

    private void RequestNextAppTokenBatch()
    {
        var batch = _appTokenBatches[_appTokenBatchIndex++];
        _steamApps.PICSGetAccessTokens(batch, Array.Empty<uint>());
    }

    private void RequestAppInfo()
    {
        _appBatches = _appIds
            .Select(appId => new SteamApps.PICSRequest(
                appId,
                _appTokens.TryGetValue(appId, out var token) ? token : 0))
            .Chunk(PicsBatchSize)
            .ToList();
        _appBatchIndex = 0;
        _games.Clear();
        _stage = PicsStage.Apps;
        RequestNextAppBatch();
    }

    private void RequestNextAppBatch()
    {
        var batch = _appBatches[_appBatchIndex++];
        _steamApps.PICSGetProductInfo(
            batch,
            Array.Empty<SteamApps.PICSRequest>(),
            false);
    }
}