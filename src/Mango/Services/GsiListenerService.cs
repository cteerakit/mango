using System.Net;
using System.Text;
using System.Text.Json;
using Mango.Models.Gsi;

namespace Mango.Services;

public sealed class GsiListenerService : IDisposable
{
    private readonly MatchStore _store;
    private readonly AppSettings _settings;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private DateTimeOffset _lastGsiDumpUtc;

    public event Action<string>? StatusChanged;

    public GsiListenerService(MatchStore store, AppSettings settings)
    {
        _store = store;
        _settings = settings;
    }

    public void Start()
    {
        Stop();

        _cts = new CancellationTokenSource();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{_settings.GsiPort}/");
        _listener.Start();
        StatusChanged?.Invoke($"Listening on 127.0.0.1:{_settings.GsiPort}");

        _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        if (_listener?.IsListening == true)
        {
            _listener.Stop();
        }

        _listener?.Close();
        _listener = null;
        _cts?.Dispose();
        _cts = null;
    }

    private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is { IsListening: true })
        {
            HttpListenerContext? context = null;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(cancellationToken);
                _ = Task.Run(() => HandleRequestAsync(context), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        try
        {
            using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
            var body = await reader.ReadToEndAsync();

            if (!string.IsNullOrWhiteSpace(body))
            {
                var payload = JsonSerializer.Deserialize<GsiPayload>(body);
                if (payload is not null && IsAuthorized(payload))
                {
                    TryDumpGsiMeta(payload);
                    App.DispatcherQueue.TryEnqueue(() => _store.ApplyPayload(payload));
                }
            }

            var buffer = Encoding.UTF8.GetBytes("ok");
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = "text/plain";
            await context.Response.OutputStream.WriteAsync(buffer);
        }
        catch
        {
            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
        }
        finally
        {
            context.Response.Close();
        }
    }

    private void TryDumpGsiMeta(GsiPayload payload)
    {
        if (DateTimeOffset.UtcNow - _lastGsiDumpUtc < TimeSpan.FromSeconds(15))
        {
            return;
        }

        _lastGsiDumpUtc = DateTimeOffset.UtcNow;
        try
        {
            Directory.CreateDirectory(AppSettings.SettingsDirectory);
            var meta = JsonSerializer.Serialize(new
            {
                matchId = payload.Map?.MatchId,
                serverSteamId = payload.Map?.ServerSteamId,
                gameState = payload.Map?.GameState,
                allPlayerCount = payload.AllPlayers?.Count ?? 0,
                localSteamId = payload.Player?.Local?.SteamId,
                localName = payload.Player?.Local?.Name,
                resolvedPlayerCount = _store.Snapshot.MatchId == payload.Map?.MatchId ? _store.Snapshot.Players.Count : 0,
                resolvedServerSteamId = _store.Snapshot.MatchId == payload.Map?.MatchId ? _store.Snapshot.ServerSteamId : null,
                resolvedSource = _store.LastRosterSource,
                normalizedPlayerCount = GsiNormalizer.Normalize(payload).Players.Count,
            });
            File.WriteAllText(Path.Combine(AppSettings.SettingsDirectory, "gsi-last-meta.json"), meta);
        }
        catch
        {
            // Diagnostics only.
        }
    }

    private bool IsAuthorized(GsiPayload payload)
    {
        if (string.IsNullOrWhiteSpace(_settings.GsiToken))
        {
            return true;
        }

        return string.Equals(payload.Auth?.Token, _settings.GsiToken, StringComparison.Ordinal);
    }

    public void Dispose() => Stop();
}
