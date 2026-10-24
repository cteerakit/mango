using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mango.Services;

public sealed record AppUpdateResult(string Status, Uri? DownloadUri = null, Uri? ChecksumUri = null);

public sealed record AppInstallerDownload(string Status, string? InstallerPath = null);

public sealed class AppUpdateService
{
    private const string InstallerName = "MangoSetup-x64.exe";
    private const string ChecksumName = "SHA256SUMS.txt";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly HttpClient _client;
    private readonly System.Version _installedVersion;

    public string Version => _installedVersion.ToString();
    public string Status => "Check GitHub for a newer version. Mango can download and install it for you; your settings and notes are kept.";

    public AppUpdateService(HttpClient? client = null, System.Version? installedVersion = null)
    {
        _client = client ?? Client;
        _installedVersion = installedVersion ?? typeof(AppUpdateService).Assembly.GetName().Version ?? new(1, 0, 0, 0);
    }

    public async Task<AppUpdateResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://api.github.com/repos/cteerakit/mango/releases/latest");
            request.Headers.UserAgent.ParseAdd($"Mango/{Version}");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await _client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var release = await response.Content.ReadFromJsonAsync<GitHubRelease>(cancellationToken);
            if (release is null || release.Draft || release.Prerelease ||
                release.Tag is not { } tag || !tag.StartsWith('v') ||
                !System.Version.TryParse(tag[1..], out var latestVersion))
            {
                throw new InvalidDataException("GitHub returned an invalid stable release.");
            }

            if (latestVersion <= _installedVersion)
            {
                return new("Mango is up to date.");
            }

            var download = AssetUri(tag, InstallerName);
            var checksum = AssetUri(tag, ChecksumName);
            if (!HasAsset(release, InstallerName, download) || !HasAsset(release, ChecksumName, checksum))
            {
                return new("A newer release exists, but its x64 setup installer is not available yet. Try again later.");
            }

            return new($"Mango {latestVersion} is available. Install it now, or download the installer to run yourself.", download, checksum);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException)
        {
            Debug.WriteLine($"Update check failed: {ex}");
            return new("Could not check for updates. Check your internet connection and try again.");
        }
    }

    /// <summary>
    /// Downloads the installer for <paramref name="update"/> into a fresh temp folder and verifies it
    /// against the release's SHA256SUMS.txt. The installer is deleted if verification fails.
    /// </summary>
    public async Task<AppInstallerDownload> DownloadInstallerAsync(
        AppUpdateResult update, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (update.DownloadUri is null || update.ChecksumUri is null)
        {
            return new("No update is available to install.");
        }

        var folder = Path.Combine(Path.GetTempPath(), $"Mango-update-{Guid.NewGuid():N}");
        var path = Path.Combine(folder, InstallerName);
        try
        {
            var expected = ParseChecksum(await _client.GetStringAsync(update.ChecksumUri, cancellationToken), InstallerName)
                ?? throw new InvalidDataException("The release checksum file does not list the installer.");

            Directory.CreateDirectory(folder);
            using (var response = await _client.GetAsync(update.DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                var buffer = new byte[81920];
                long written = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    written += read;
                    if (total is > 0)
                    {
                        progress?.Report((double)written / total.Value);
                    }
                }
            }

            string actual;
            await using (var file = File.OpenRead(path))
            {
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken));
            }

            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                Cleanup(folder);
                return new("The downloaded installer did not match its published checksum, so it was not run. Try again later.");
            }

            return new("Update downloaded and verified.", path);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Update download failed: {ex}");
            Cleanup(folder);
            return new("Could not download the update. Check your internet connection and try again.");
        }
    }

    /// <summary>
    /// Starts the verified installer silently. The caller is expected to exit so the installer can
    /// replace the running files; the installer relaunches Mango when it finishes.
    /// </summary>
    public static bool TryStartInstaller(string installerPath)
    {
        try
        {
            var log = Path.Combine(Path.GetDirectoryName(installerPath)!, "install.log");
            var start = new ProcessStartInfo(installerPath)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(installerPath)!,
            };
            foreach (var argument in InstallerArguments(log))
            {
                start.ArgumentList.Add(argument);
            }

            return Process.Start(start) is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Debug.WriteLine($"Could not start installer: {ex}");
            return false;
        }
    }

    internal static string[] InstallerArguments(string logPath) =>
    [
        "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", "/RELAUNCH=1", $"/LOG={logPath}",
    ];

    /// <summary>Finds the SHA-256 for <paramref name="fileName"/> in `sha256sum`-style text ("hash  name").</summary>
    internal static string? ParseChecksum(string text, string fileName)
    {
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var split = line.IndexOfAny([' ', '\t']);
            if (split != 64)
            {
                continue;
            }

            var name = line[split..].TrimStart(' ', '\t', '*');
            var hash = line[..split];
            if (name == fileName && hash.All(Uri.IsHexDigit))
            {
                return hash.ToLowerInvariant();
            }
        }

        return null;
    }

    private static Uri AssetUri(string tag, string name) =>
        new($"https://github.com/cteerakit/mango/releases/download/{tag}/{name}");

    private static bool HasAsset(GitHubRelease release, string name, Uri uri) =>
        release.Assets is not null && release.Assets.Any(asset =>
            asset is not null && asset.Name == name && asset.DownloadUrl == uri.AbsoluteUri);

    private static void Cleanup(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not clean up {folder}: {ex.Message}");
        }
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string? Tag,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("assets")] GitHubAsset[]? Assets);

    private sealed record GitHubAsset(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("browser_download_url")] string? DownloadUrl);
}
