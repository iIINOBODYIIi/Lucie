using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lucie.Services;

/// <summary>
/// Connexion directe à l'appli Discord du PC, via son API locale officielle (RPC).
/// Permet de couper / réactiver le micro, la sourdine et de quitter le salon vocal,
/// sans simuler de raccourcis clavier.
///
/// Discord réserve ce contrôle aux applis autorisées : l'utilisateur crée sa propre
/// application sur le portail développeur (Client ID + Client Secret), puis l'autorise
/// une fois dans Discord. Les jetons sont chiffrés avec le compte Windows (DPAPI).
/// </summary>
public sealed class DiscordService : IDisposable
{
    private const string RedirectUri = "http://localhost";
    private static readonly string[] Scopes = { "rpc", "rpc.voice.read", "rpc.voice.write", "identify" };
    private static string FilePath => Path.Combine(SettingsService.DataDir, "discord.dat");

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pending = new();
    private NamedPipeClientStream? _pipe;
    private CancellationTokenSource? _readCts;
    private bool _authenticated;

    private Stored _stored = new();

    public string ClientId => _stored.ClientId;
    public bool HasCredentials => _stored.ClientId.Length > 0 && _stored.ClientSecret.Length > 0;
    public bool IsAuthorized => !string.IsNullOrEmpty(_stored.RefreshToken);
    public bool IsConnected => _authenticated && _pipe is { IsConnected: true };
    public string? UserName { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Utiliser l'API pour le micro et la sourdine (sinon : raccourcis clavier).</summary>
    public bool Enabled
    {
        get => _stored.Enabled;
        set { _stored.Enabled = value; Save(); }
    }

    public event Action? StatusChanged;

    private sealed class Stored
    {
        public string ClientId { get; set; } = "";
        public string ClientSecret { get; set; } = "";
        public string AccessToken { get; set; } = "";
        public string RefreshToken { get; set; } = "";
        public DateTime ExpiresUtc { get; set; }
        public bool Enabled { get; set; } = true;
        public string UserName { get; set; } = "";
    }

    public DiscordService()
    {
        Load();
        if (_stored.UserName.Length > 0) UserName = _stored.UserName;
    }

    // ───────────────────────── Réglages ─────────────────────────

    public void SetCredentials(string clientId, string clientSecret)
    {
        clientId = clientId.Trim();
        clientSecret = clientSecret.Trim();
        if (clientId != _stored.ClientId)
        {
            // Nouvelle application : les anciens jetons ne valent plus rien.
            _stored.AccessToken = _stored.RefreshToken = "";
            UserName = null;
        }
        _stored.ClientId = clientId;
        if (clientSecret.Length > 0) _stored.ClientSecret = clientSecret;
        Save();
    }

    public void Forget()
    {
        Disconnect();
        _stored = new Stored();
        UserName = null;
        LastError = null;
        try { File.Delete(FilePath); } catch { }
        StatusChanged?.Invoke();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser);
            _stored = JsonSerializer.Deserialize<Stored>(bytes) ?? new Stored();
        }
        catch
        {
            _stored = new Stored();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsService.DataDir);
            var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(_stored), null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(FilePath, bytes);
        }
        catch
        {
            // pas bloquant
        }
    }

    // ───────────────────────── Connexion ─────────────────────────

    /// <summary>
    /// Première connexion : Discord affiche une fenêtre « Autoriser Lucie ? ».
    /// Renvoie true quand tout est prêt.
    /// </summary>
    public async Task<bool> AuthorizeAsync()
    {
        LastError = null;
        if (!HasCredentials) { LastError = "Il manque le Client ID ou le Client Secret."; return false; }

        await _gate.WaitAsync();
        try
        {
            if (!await OpenPipeAsync()) return false;

            var res = await SendAsync("AUTHORIZE", new JsonObject
            {
                ["client_id"] = _stored.ClientId,
                ["scopes"] = new JsonArray(Scopes.Select(s => (JsonNode)s).ToArray())
            }, TimeSpan.FromMinutes(2));   // le temps de cliquer sur « Autoriser » dans Discord

            var code = res["data"]?["code"]?.GetValue<string>();
            if (string.IsNullOrEmpty(code)) { LastError = ErrorText(res) ?? "Autorisation refusée dans Discord."; return false; }

            if (!await ExchangeTokenAsync("authorization_code", code)) return false;
            return await AuthenticateAsync();
        }
        catch (Exception ex)
        {
            LastError = "Discord ne répond pas : " + ex.Message;
            Disconnect();
            return false;
        }
        finally
        {
            _gate.Release();
            StatusChanged?.Invoke();
        }
    }

    /// <summary>Se (re)connecte sans rien demander, si Lucie a déjà été autorisée.</summary>
    public async Task<bool> EnsureConnectedAsync()
    {
        if (IsConnected) return true;
        if (!HasCredentials || !IsAuthorized) return false;

        await _gate.WaitAsync();
        try
        {
            if (IsConnected) return true;
            if (!await OpenPipeAsync()) return false;

            // Jeton périmé (ou presque) : on le renouvelle.
            if (DateTime.UtcNow > _stored.ExpiresUtc.AddMinutes(-5) &&
                !await ExchangeTokenAsync("refresh_token", _stored.RefreshToken))
                return false;

            if (await AuthenticateAsync()) return true;

            // Refusé : un dernier essai avec un jeton tout neuf.
            return await ExchangeTokenAsync("refresh_token", _stored.RefreshToken) && await AuthenticateAsync();
        }
        catch (Exception ex)
        {
            LastError = "Discord ne répond pas : " + ex.Message;
            Disconnect();
            return false;
        }
        finally
        {
            _gate.Release();
            StatusChanged?.Invoke();
        }
    }

    private async Task<bool> AuthenticateAsync()
    {
        var res = await SendAsync("AUTHENTICATE", new JsonObject { ["access_token"] = _stored.AccessToken }, TimeSpan.FromSeconds(10));
        var user = res["data"]?["user"];
        if (user is null)
        {
            LastError = ErrorText(res) ?? "Discord a refusé la connexion.";
            _authenticated = false;
            return false;
        }
        _authenticated = true;
        UserName = user["global_name"]?.GetValue<string>() is { Length: > 0 } g ? g : user["username"]?.GetValue<string>();
        _stored.UserName = UserName ?? "";
        Save();
        LastError = null;
        return true;
    }

    private async Task<bool> ExchangeTokenAsync(string grantType, string value)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = _stored.ClientId,
            ["client_secret"] = _stored.ClientSecret,
            ["grant_type"] = grantType,
            ["redirect_uri"] = RedirectUri,
        };
        form[grantType == "refresh_token" ? "refresh_token" : "code"] = value;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var response = await http.PostAsync("https://discord.com/api/oauth2/token", new FormUrlEncodedContent(form));
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                LastError = body.Contains("invalid_client")
                    ? "Client Secret incorrect (copie-le à nouveau depuis le portail Discord)."
                    : body.Contains("redirect_uri")
                        ? "Ajoute http://localhost dans OAuth2 › Redirects sur le portail Discord, puis enregistre."
                        : $"Discord a refusé la connexion ({(int)response.StatusCode}).";
                if (grantType == "refresh_token") { _stored.AccessToken = _stored.RefreshToken = ""; Save(); }
                return false;
            }

            var json = JsonNode.Parse(body)!;
            _stored.AccessToken = json["access_token"]!.GetValue<string>();
            if (json["refresh_token"]?.GetValue<string>() is { Length: > 0 } refresh) _stored.RefreshToken = refresh;
            _stored.ExpiresUtc = DateTime.UtcNow.AddSeconds(json["expires_in"]?.GetValue<int>() ?? 3600);
            Save();
            return true;
        }
        catch (Exception ex)
        {
            LastError = "Impossible de joindre Discord sur internet : " + ex.Message;
            return false;
        }
    }

    // ───────────────────────── Actions ─────────────────────────

    /// <summary>Coupe (true) ou réactive (false) le micro. null = inverser.</summary>
    public Task<bool> SetMuteAsync(bool? mute) => SetVoiceAsync("mute", mute);

    /// <summary>Active (true) ou enlève (false) la sourdine. null = inverser.</summary>
    public Task<bool> SetDeafAsync(bool? deaf) => SetVoiceAsync("deaf", deaf);

    private async Task<bool> SetVoiceAsync(string key, bool? value)
    {
        if (!await EnsureConnectedAsync()) return false;
        try
        {
            bool target;
            if (value is bool v) target = v;
            else
            {
                var current = await SendAsync("GET_VOICE_SETTINGS", new JsonObject(), TimeSpan.FromSeconds(5));
                target = !(current["data"]?[key]?.GetValue<bool>() ?? false);
            }
            var res = await SendAsync("SET_VOICE_SETTINGS", new JsonObject { [key] = target }, TimeSpan.FromSeconds(5));
            if (res["evt"]?.GetValue<string>() == "ERROR") { LastError = ErrorText(res); return false; }
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Disconnect();
            return false;
        }
    }

    /// <summary>Quitte le salon vocal.</summary>
    public async Task<bool> LeaveVoiceAsync()
    {
        if (!await EnsureConnectedAsync()) return false;
        try
        {
            var res = await SendAsync("SELECT_VOICE_CHANNEL", new JsonObject { ["channel_id"] = null, ["force"] = true }, TimeSpan.FromSeconds(5));
            return res["evt"]?.GetValue<string>() != "ERROR";
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Disconnect();
            return false;
        }
    }

    // ───────────────────────── Tube local (IPC) ─────────────────────────

    private async Task<bool> OpenPipeAsync()
    {
        if (_pipe is { IsConnected: true }) return true;
        Disconnect();

        for (int i = 0; i < 10; i++)
        {
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(400);
            }
            catch
            {
                pipe.Dispose();
                continue;
            }

            _pipe = pipe;
            _readCts = new CancellationTokenSource();
            var ready = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending["__ready"] = ready;
            _ = Task.Run(() => ReadLoopAsync(pipe, _readCts.Token));

            await WriteFrameAsync(0, new JsonObject { ["v"] = 1, ["client_id"] = _stored.ClientId });
            var done = await Task.WhenAny(ready.Task, Task.Delay(5000));
            _pending.TryRemove("__ready", out _);
            if (done == ready.Task && ready.Task.IsCompletedSuccessfully && ready.Task.Result["evt"]?.GetValue<string>() == "READY") return true;

            LastError = "Discord a refusé l'appli (vérifie le Client ID).";
            Disconnect();
            return false;
        }

        LastError = "Discord n'est pas ouvert sur ce PC.";
        return false;
    }

    private async Task<JsonObject> SendAsync(string cmd, JsonObject args, TimeSpan timeout)
    {
        var nonce = Guid.NewGuid().ToString();
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[nonce] = tcs;
        try
        {
            await WriteFrameAsync(1, new JsonObject { ["cmd"] = cmd, ["args"] = args, ["nonce"] = nonce });
            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout));
            if (done != tcs.Task) throw new TimeoutException("pas de réponse de Discord");
            return await tcs.Task;
        }
        finally
        {
            _pending.TryRemove(nonce, out _);
        }
    }

    private async Task WriteFrameAsync(int op, JsonObject payload)
    {
        var pipe = _pipe ?? throw new IOException("Discord n'est pas connecté");
        var body = Encoding.UTF8.GetBytes(payload.ToJsonString());
        var frame = new byte[8 + body.Length];
        BitConverter.GetBytes(op).CopyTo(frame, 0);
        BitConverter.GetBytes(body.Length).CopyTo(frame, 4);
        body.CopyTo(frame, 8);
        await pipe.WriteAsync(frame);
        await pipe.FlushAsync();
    }

    private async Task ReadLoopAsync(NamedPipeClientStream pipe, CancellationToken token)
    {
        var header = new byte[8];
        try
        {
            while (!token.IsCancellationRequested && pipe.IsConnected)
            {
                await pipe.ReadExactlyAsync(header, token);
                int op = BitConverter.ToInt32(header, 0);
                int length = BitConverter.ToInt32(header, 4);
                if (length < 0 || length > 4 * 1024 * 1024) break;
                var body = new byte[length];
                await pipe.ReadExactlyAsync(body, token);

                if (op == 3)        // PING → PONG
                {
                    var pong = new byte[8 + length];
                    BitConverter.GetBytes(4).CopyTo(pong, 0);
                    BitConverter.GetBytes(length).CopyTo(pong, 4);
                    body.CopyTo(pong, 8);
                    await pipe.WriteAsync(pong, token);
                    continue;
                }
                if (op == 2) break; // CLOSE

                if (JsonNode.Parse(body) is not JsonObject msg) continue;
                var nonce = msg["nonce"]?.GetValue<string>();
                if (nonce is not null && _pending.TryGetValue(nonce, out var tcs)) tcs.TrySetResult(msg);
                else if (msg["evt"]?.GetValue<string>() is "READY" or "ERROR" && _pending.TryGetValue("__ready", out var ready))
                    ready.TrySetResult(msg);
            }
        }
        catch
        {
            // Discord fermé ou redémarré
        }

        _authenticated = false;
        foreach (var p in _pending.Values) p.TrySetException(new IOException("Discord s'est déconnecté"));
        StatusChanged?.Invoke();
    }

    private static string? ErrorText(JsonObject msg) =>
        msg["evt"]?.GetValue<string>() == "ERROR" ? "Discord : " + (msg["data"]?["message"]?.GetValue<string>() ?? "erreur") : null;

    public void Disconnect()
    {
        _authenticated = false;
        try { _readCts?.Cancel(); } catch { }
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
        _readCts = null;
    }

    public void Dispose() => Disconnect();
}
