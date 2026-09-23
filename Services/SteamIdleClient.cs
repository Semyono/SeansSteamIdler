    using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;
using SeansSteamIdler.Models;

namespace SeansSteamIdler.Services;

/// <summary>
/// Wraps a raw SteamKit2 connection. Logging in this way and sending a
/// "games played" message is exactly what the Steam client itself does when
/// you double-click a game in your library - it just never actually launches
/// anything, so Steam counts the time as playtime with no game process running.
/// </summary>
public class SteamIdleClient
{
    private enum LoginMode { Password, Qr, SavedSession }

    private readonly SteamClient _steamClient;
    private readonly CallbackManager _callbackManager;
    private readonly SteamUser _steamUser;
    private readonly SteamApps _steamApps;
    private readonly SteamFriends _steamFriends;

    private Thread? _callbackThread;
    private volatile bool _running;
    private LoginMode _mode;
    private string _username = "";
    private string _password = "";
    private string? _authCode;
    private string? _twoFactorCode;
    private SteamSession? _savedSession;
    private string? _pendingRefreshToken;
    private string? _pendingUsername;
    private EPersonaState _preferredPersonaState = EPersonaState.Online;
    private CredentialsAuthenticator? _credentialsAuthenticator;

    public bool IsLoggedOn { get; private set; }
    public HashSet<uint> IdlingAppIds { get; } = new();
    public SteamAccountLibrary AccountLibrary { get; }

    public event Action<string>? Log;
    public event Action? LoggedOn;
    public event Action<string>? LoginFailed;
    public event Action<bool>? SteamGuardRequired; // true = mobile 2FA, false = email code
    public event Action? Disconnected;
    public event Action<string>? PersonaNameChanged;
    public event Action<string>? StatusChanged;
    /// <summary>Fires with a steammobile:// URL to render as a QR code. Re-fires if the code refreshes.</summary>
    public event Action<string>? QrChallengeUrlChanged;

    public void SetPreferredPersonaState(EPersonaState state)
    {
        _preferredPersonaState = state;
        if (IsLoggedOn)
            _steamFriends.SetPersonaState(state);
    }

    public SteamIdleClient()
    {
        _steamClient = new SteamClient();
        _callbackManager = new CallbackManager(_steamClient);
        _steamUser = _steamClient.GetHandler<SteamUser>()!;
        _steamApps = _steamClient.GetHandler<SteamApps>()!;
        _steamFriends = _steamClient.GetHandler<SteamFriends>()!;
        AccountLibrary = new SteamAccountLibrary(_callbackManager, _steamApps);

        _callbackManager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
        _callbackManager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
        _callbackManager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        _callbackManager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
        _callbackManager.Subscribe<SteamFriends.PersonaStateCallback>(OnPersonaState);
    }

    public void ConnectWithPassword(string username, string password)
    {
        _mode = LoginMode.Password;
        _username = username;
        _password = password;
        _authCode = null;
        _twoFactorCode = null;
        StartConnection();
    }

    /// <summary>Starts a QR login. Subscribe to QrChallengeUrlChanged first to display the code.</summary>
    public void ConnectWithQr()
    {
        _mode = LoginMode.Qr;
        _authCode = null;
        _twoFactorCode = null;
        _pendingUsername = null;
        _pendingRefreshToken = null;
        StartConnection();
    }

    public void ConnectWithSavedSession()
    {
        _savedSession = SteamSessionStore.Load();
        if (_savedSession is null || string.IsNullOrWhiteSpace(_savedSession.RefreshToken))
        {
            Log?.Invoke(SteamSessionStore.HasSavedSession()
                ? "The saved Steam session could not be read and has been cleared. Please log in again."
                : "No saved Steam session was found. Please log in with QR code or password.");
            return;
        }

        _mode = LoginMode.SavedSession;
        _username = _savedSession.Username;
        Log?.Invoke("Connecting with the saved Steam session...");
        StartConnection();
    }

    private void StartConnection()
    {
        _running = true;
        _callbackThread = new Thread(() =>
        {
            while (_running)
            {
                _callbackManager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
            }
        })
        { IsBackground = true };
        _callbackThread.Start();

        Log?.Invoke("Connecting to Steam...");
        _steamClient.Connect();
    }

    /// <summary>Call after a SteamGuardRequired event, with the code the user typed in. Password mode only.</summary>
    public void SubmitGuardCode(string code, bool isTwoFactor)
    {
        code = code.Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            Log?.Invoke("Enter the Steam Guard code before submitting it.");
            SteamGuardRequired?.Invoke(isTwoFactor);
            return;
        }

        if (_credentialsAuthenticator is not null)
        {
            _credentialsAuthenticator.SubmitCode(code, isTwoFactor);
            return;
        }

        if (isTwoFactor) _twoFactorCode = code;
        else _authCode = code;

        _steamClient.Connect();
    }

    private async void OnConnected(SteamClient.ConnectedCallback callback)
    {
        if (_mode == LoginMode.Qr)
        {
            Log?.Invoke("Connected. Requesting QR code...");
            try
            {
                var authSession = await _steamClient.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails());
                authSession.ChallengeURLChanged = () => QrChallengeUrlChanged?.Invoke(authSession.ChallengeURL);
                QrChallengeUrlChanged?.Invoke(authSession.ChallengeURL);

                Log?.Invoke("Scan the code with the Steam app, then approve the login.");
                var result = await authSession.PollingWaitForResultAsync();

                var refreshToken = result.RefreshToken;
                if (string.IsNullOrWhiteSpace(refreshToken))
                    throw new InvalidOperationException("Steam QR approval did not return a refresh token.");

                _pendingUsername = result.AccountName;
                _pendingRefreshToken = refreshToken;
                _steamUser.LogOn(new SteamUser.LogOnDetails
                {
                    Username = result.AccountName,
                    AccessToken = refreshToken,
                    ShouldRememberPassword = false
                });
            }
            catch (Exception ex)
            {
                Log?.Invoke($"QR login failed: {ex.Message}");
                LoginFailed?.Invoke(ex.Message);
            }
            return;
        }

        if (_mode == LoginMode.SavedSession && _savedSession is not null)
        {
            _steamUser.LogOn(new SteamUser.LogOnDetails
            {
                Username = _savedSession.Username,
                AccessToken = _savedSession.RefreshToken,
                ShouldRememberPassword = true
            });
            return;
        }

        await LoginWithCredentialsAsync();
    }

    private async Task LoginWithCredentialsAsync()
    {
        Log?.Invoke("Connected. Starting secure Steam authentication...");
        var authenticator = new CredentialsAuthenticator(this);
        _credentialsAuthenticator = authenticator;

        try
        {
            var authSession = await _steamClient.Authentication.BeginAuthSessionViaCredentialsAsync(
                new AuthSessionDetails
                {
                    Username = _username,
                    Password = _password,
                    IsPersistentSession = true,
                    Authenticator = authenticator
                });

            var result = await authSession.PollingWaitForResultAsync();
            var refreshToken = result.RefreshToken;
            if (string.IsNullOrWhiteSpace(refreshToken))
                throw new InvalidOperationException("Steam credential authentication did not return a refresh token.");

            _pendingUsername = result.AccountName ?? _username;
            _pendingRefreshToken = refreshToken;
            _steamUser.LogOn(new SteamUser.LogOnDetails
            {
                Username = _pendingUsername,
                AccessToken = refreshToken,
                ShouldRememberPassword = false
            });
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Secure Steam authentication failed: {ex.Message}");
            LoginFailed?.Invoke(ex.Message);
        }
        finally
        {
            _credentialsAuthenticator = null;
        }
    }

    private sealed class CredentialsAuthenticator : IAuthenticator
    {
        private readonly SteamIdleClient _owner;
        private TaskCompletionSource<string>? _codeSource;
        private bool _codeIsTwoFactor;

        public CredentialsAuthenticator(SteamIdleClient owner)
        {
            _owner = owner;
        }

        public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
        {
            return WaitForCodeAsync(true, previousCodeWasIncorrect
                ? "The Steam Guard mobile code was incorrect. Enter a new code."
                : "Enter the code from your Steam Mobile authenticator.");
        }

        public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
        {
            var destination = string.IsNullOrWhiteSpace(email) ? "your email" : email;
            return WaitForCodeAsync(false, previousCodeWasIncorrect
                ? "The emailed Steam Guard code was incorrect. Enter a new code."
                : $"Steam sent a Guard code to {destination}. Enter it here.");
        }

        public Task<bool> AcceptDeviceConfirmationAsync()
        {
            _owner.Log?.Invoke("Approve the Steam sign-in notification in the Steam Mobile app.");
            _owner.SteamGuardRequired?.Invoke(true);
            return Task.FromResult(true);
        }

        public void SubmitCode(string code, bool isTwoFactor)
        {
            if (_codeSource is null || _codeIsTwoFactor != isTwoFactor)
            {
                _owner.Log?.Invoke("Steam is not currently waiting for that type of Guard code.");
                return;
            }

            _codeSource.TrySetResult(code);
        }

        private async Task<string> WaitForCodeAsync(bool isTwoFactor, string message)
        {
            _codeIsTwoFactor = isTwoFactor;
            _codeSource = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _owner.Log?.Invoke(message);
            _owner.SteamGuardRequired?.Invoke(isTwoFactor);

            try
            {
                return await _codeSource.Task;
            }
            finally
            {
                _codeSource = null;
            }
        }
    }

    private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
    {
        if (callback.Result == EResult.LoggedInElsewhere)
        {
            HandleLoggedInElsewhere();
            LoginFailed?.Invoke(callback.Result.ToString());
            return;
        }

        if (callback.Result == EResult.AccountLogonDenied)
        {
            RequestGuardCode(false, "Steam sent a Steam Guard code to your email.");
            return;
        }

        if (callback.Result == EResult.AccountLoginDeniedNeedTwoFactor)
        {
            RequestGuardCode(true, "Steam requires a code from your mobile authenticator.");
            return;
        }

        if (callback.Result == EResult.TwoFactorCodeMismatch)
        {
            _twoFactorCode = null;
            RequestGuardCode(true, "The Steam Guard code was rejected or expired. Enter a new mobile authenticator code.");
            return;
        }

        if (callback.Result == EResult.InvalidLoginAuthCode)
        {
            _authCode = null;
            RequestGuardCode(false, "The emailed Steam Guard code was rejected or expired. Enter a new code.");
            return;
        }

        if (callback.Result != EResult.OK)
        {
            Log?.Invoke($"Steam login rejected: Result={callback.Result}, ExtendedResult={callback.ExtendedResult}.");
            if (callback.Result == EResult.AccessDenied && _mode == LoginMode.SavedSession)
            {
                SteamSessionStore.Delete();
                _savedSession = null;
                Log?.Invoke("The saved Steam refresh token was denied and has been deleted. Please log in again with QR code or password.");
            }
            if (callback.Result == EResult.InvalidPassword)
                Log?.Invoke("Steam rejected the username or password. Verify both values; an incorrect password is not treated as a Steam Guard challenge.");
            if (_mode == LoginMode.SavedSession && callback.Result != EResult.AccessDenied)
            {
                SteamSessionStore.Delete();
                _savedSession = null;
                Log?.Invoke("The saved Steam session was rejected or expired and has been cleared. Please log in again.");
            }
            LoginFailed?.Invoke($"{callback.Result} ({callback.ExtendedResult})");
            return;
        }

        IsLoggedOn = true;
        _steamFriends.SetPersonaState(_preferredPersonaState);
        if (!string.IsNullOrWhiteSpace(_pendingRefreshToken) &&
            !string.IsNullOrWhiteSpace(_pendingUsername))
        {
            var session = new SteamSession(_pendingUsername, _pendingRefreshToken);
            try
            {
                SteamSessionStore.Save(session);
                Log?.Invoke("Steam session saved for automatic login next time.");
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Steam login succeeded, but the session could not be saved: {ex.Message}");
            }
            _pendingRefreshToken = null;
            _pendingUsername = null;
        }
        Log?.Invoke("Logged on to Steam.");
        if (callback.ClientSteamID is not null)
        {
            var cachedPersonaName = _steamFriends.GetPersonaName();
            if (!string.IsNullOrWhiteSpace(cachedPersonaName))
                PersonaNameChanged?.Invoke(cachedPersonaName);

            _steamFriends.RequestFriendInfo(
                callback.ClientSteamID,
                EClientPersonaStateFlag.PlayerName);
        }
        LoggedOn?.Invoke();

        // Re-send whichever games were already marked for idling (e.g. after a reconnect).
        if (IdlingAppIds.Count > 0)
            BroadcastGamesPlayed();
    }

    private void RequestGuardCode(bool isTwoFactor, string message)
    {
        Log?.Invoke(message);
        SteamGuardRequired?.Invoke(isTwoFactor);
    }

    private void OnPersonaState(SteamFriends.PersonaStateCallback callback)
    {
        if (_steamClient.SteamID is null || callback.FriendID != _steamClient.SteamID)
            return;

        if (callback.Name is not null && callback.Name.Length > 0)
            PersonaNameChanged?.Invoke(callback.Name);
    }

    private void OnLoggedOff(SteamUser.LoggedOffCallback callback)
    {
        IsLoggedOn = false;
        if (callback.Result == EResult.LoggedInElsewhere)
        {
            HandleLoggedInElsewhere();
            return;
        }

        Log?.Invoke($"Logged off: {callback.Result}");
    }

    private void OnDisconnected(SteamClient.DisconnectedCallback callback)
    {
        IsLoggedOn = false;
        Log?.Invoke("Disconnected from Steam.");
        StatusChanged?.Invoke("Disconnected from Steam");
        Disconnected?.Invoke();
    }

    public void StartIdling(uint appId)
    {
        if (appId == 0)
        {
            Log?.Invoke("Skipped an invalid app ID (0).");
            return;
        }

        IdlingAppIds.Add(appId);
        _steamFriends.SetPersonaState(_preferredPersonaState);
        StatusChanged?.Invoke($"Idling {IdlingAppIds.Count} game(s)");
        BroadcastGamesPlayed();
    }

    public void StopIdling(uint appId)
    {
        IdlingAppIds.Remove(appId);
        BroadcastGamesPlayed();
    }

    public void StopAllIdling()
    {
        IdlingAppIds.Clear();
        StatusChanged?.Invoke("Connected to Steam");
        BroadcastGamesPlayed();
    }

    private void BroadcastGamesPlayed()
    {
        if (!IsLoggedOn) return;

        var msg = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayed);
        foreach (var appId in IdlingAppIds.Where(appId => appId != 0))
        {
            msg.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed
            {
                game_id = appId
            });
        }
        _steamClient.Send(msg);

        Log?.Invoke(IdlingAppIds.Count == 0
            ? "Stopped idling."
            : $"Now idling {IdlingAppIds.Count} game(s): {string.Join(", ", IdlingAppIds)}.");
    }

    private void HandleLoggedInElsewhere()
    {
        const string message = "Disconnected: Steam account is active on another Steam Client or device. Close other Steam instances or stop playing on Desktop to idle.";
        IdlingAppIds.Clear();
        Log?.Invoke(message);
        StatusChanged?.Invoke(message);
        Disconnected?.Invoke();
    }

    public void Shutdown()
    {
        _running = false;
        StopAllIdling();
        if (IsLoggedOn) _steamUser.LogOff();
        _steamClient.Disconnect();
    }
}

