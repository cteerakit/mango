using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mango.Services;

public sealed record AppUpdateResult(string Status, Uri? DownloadUri = null);

public sealed class AppUpdateService
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly HttpClient _client;
    private readonly System.Version _installedVersion;

    public string Version => _installedVersion.ToString();
    public string Status => "Check GitHub for a newer version. Download and run its installer to update; your settings and notes are kept.";

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

            var download = new Uri($"https://github.com/cteerakit/mango/releases/download/{tag}/MangoSetup-x64.exe");
            if (release.Assets is null || !release.Assets.Any(asset =>
                asset is not null && asset.Name == "MangoSetup-x64.exe" && asset.DownloadUrl == download.AbsoluteUri))
            {
                return new("A newer release exists, but its x64 setup installer is not available yet. Try again later.");
            }

            return new($"Mango {latestVersion} is available. Download and run the installer to update.", download);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException)
        {
            System.Diagnostics.Debug.WriteLine($"Update check failed: {ex}");
            return new("Could not check for updates. Check your internet connection and try again.");
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
