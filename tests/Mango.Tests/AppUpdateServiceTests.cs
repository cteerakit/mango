using System.Net;
using System.Text.Json;
using Mango.Services;
using Xunit;

namespace Mango.Tests;

public sealed class AppUpdateServiceTests
{
    [Fact]
    public async Task NewerReleaseOffersItsX64Installer()
    {
        using var client = CreateClient(Release("1.0.10.0"));
        var service = new AppUpdateService(client, new Version(1, 0, 3, 0));
        var result = await service.CheckForUpdatesAsync();
        Assert.Equal("Mango 1.0.10.0 is available. Download and run the installer to update.", result.Status);
        Assert.Equal("https://github.com/cteerakit/mango/releases/download/v1.0.10.0/MangoSetup-x64.exe", result.DownloadUri?.AbsoluteUri);
    }

    [Theory]
    [InlineData("1.0.3.0")]
    [InlineData("1.0.2.0")]
    public async Task EqualOrOlderReleaseDoesNotOfferAnUpdate(string version)
    {
        using var client = CreateClient(Release(version));
        var result = await new AppUpdateService(client, new Version(1, 0, 3, 0)).CheckForUpdatesAsync();
        Assert.Equal("Mango is up to date.", result.Status);
        Assert.Null(result.DownloadUri);
    }

    [Theory]
    [InlineData("{\"tag_name\":\"v1.0.4.0\",\"assets\":[]}")]
    [InlineData("{\"tag_name\":\"v1.0.4.0\",\"assets\":[null]}")]
    [InlineData("{\"tag_name\":\"v1.0.4.0\",\"assets\":[{\"name\":\"MangoSetup-x64.exe\",\"browser_download_url\":\"https://example.com/malware.exe\"}]}")]
    public async Task MissingOrUnexpectedInstallerIsNotOffered(string json)
    {
        using var client = CreateClient(json);
        var result = await new AppUpdateService(client, new Version(1, 0, 3, 0)).CheckForUpdatesAsync();
        Assert.Contains("installer is not available yet", result.Status);
        Assert.Null(result.DownloadUri);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"tag_name\":\"v1.0.4.0\",\"prerelease\":true}")]
    [InlineData("{\"tag_name\":\"v1.0.4.0\",\"draft\":true}")]
    public async Task InvalidReleaseCanBeRetried(string json)
    {
        using var client = CreateClient(json);
        var result = await new AppUpdateService(client).CheckForUpdatesAsync();
        Assert.Contains("Could not check for updates", result.Status);
        Assert.Null(result.DownloadUri);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task GitHubFailuresCanBeRetried(HttpStatusCode status)
    {
        using var client = CreateClient("{}", status);
        var result = await new AppUpdateService(client).CheckForUpdatesAsync();
        Assert.Contains("Could not check for updates", result.Status);
        Assert.Null(result.DownloadUri);
    }

    [Fact]
    public async Task OfflineCheckDoesNotCrash()
    {
        using var client = new HttpClient(new Handler(_ => throw new HttpRequestException("offline")));
        var result = await new AppUpdateService(client).CheckForUpdatesAsync();
        Assert.Contains("Could not check for updates", result.Status);
    }

    [Fact]
    public async Task CheckUsesGitHubApiAndIdentifiesMango()
    {
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.Equal("https://api.github.com/repos/cteerakit/mango/releases/latest", request.RequestUri?.AbsoluteUri);
            Assert.Equal("Mango/1.0.3.0", request.Headers.UserAgent.ToString());
            return new(HttpStatusCode.OK) { Content = new StringContent(Release("1.0.3.0")) };
        }));
        await new AppUpdateService(client, new Version(1, 0, 3, 0)).CheckForUpdatesAsync();
    }

    private static string Release(string version) => JsonSerializer.Serialize(new
    {
        tag_name = $"v{version}",
        draft = false,
        prerelease = false,
        assets = new[] { new { name = "MangoSetup-x64.exe", browser_download_url = $"https://github.com/cteerakit/mango/releases/download/v{version}/MangoSetup-x64.exe" } },
    });

    private static HttpClient CreateClient(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(new Handler(_ => new(status) { Content = new StringContent(json) }));

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
