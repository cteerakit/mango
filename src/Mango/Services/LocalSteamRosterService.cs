using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text;
using Mango.Models;

namespace Mango.Services;

public sealed class LocalSteamRosterService(IReadOnlyList<string> dotaPaths)
{
    private const string ResultPrefix = "MANGO_STEAM_ROSTER:";
    private readonly ConcurrentDictionary<string, (string Server, string Library)> _servers = new();

    public async Task<MatchRosterResolution?> GetRosterAsync(MatchSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (!PlayerNotesMatch.IsUsableMatchId(snapshot.MatchId) || SteamIdConverter.ToAccountId(snapshot.SteamId) is null)
        {
            return null;
        }

        if (!_servers.TryGetValue(snapshot.MatchId, out var connection))
        {
            foreach (var dotaPath in dotaPaths)
            {
                var logPath = Path.Combine(dotaPath, "game", "dota", "console.log");
                var libraryPath = Path.Combine(dotaPath, "game", "bin", "win64", "steam_api64.dll");
                if (!File.Exists(logPath) || !File.Exists(libraryPath)) continue;
                try
                {
                    using var log = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    log.Seek(Math.Max(0, log.Length - 1024 * 1024), SeekOrigin.Begin);
                    using var reader = new StreamReader(log);
                    var server = SteamLocalRosterMapper.FindServer(await reader.ReadToEndAsync(cancellationToken), snapshot.MatchId);
                    if (server is null) continue;
                    connection = (server, libraryPath);
                    _servers[snapshot.MatchId] = connection;
                    break;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        if (string.IsNullOrWhiteSpace(connection.Server)) return null;
        var script = Path.Combine(AppContext.BaseDirectory, "Read-SteamRoster.ps1");
        if (!File.Exists(script)) return null;
        var scratch = Path.Combine(Path.GetTempPath(), "Mango-SteamRoster-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(scratch, "steam_appid.txt"), "570", cancellationToken);
            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                WorkingDirectory = scratch,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-ApiLibrary", connection.Library, "-ServerSteamId", connection.Server })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return null;
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw;
            }

            var output = await outputTask;
            await errorTask;
            if (process.ExitCode != 0) return null;
            var result = output.Split('\n').LastOrDefault(line => line.StartsWith(ResultPrefix, StringComparison.Ordinal));
            if (result is null) return null;
            var players = SteamLocalRosterMapper.ReadPlayers(result[ResultPrefix.Length..], connection.Server, snapshot.SteamId);
            return new MatchRosterResolution { Players = players, ServerSteamId = connection.Server, Source = "local-steam" };
        }
        finally
        {
            File.Delete(Path.Combine(scratch, "steam_appid.txt"));
            try { Directory.Delete(scratch); }
            catch (IOException) { }
        }
    }
}
