using System.Net;
using System.Security.Cryptography;
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
        Assert.Equal("Mango 1.0.10.0 is available. Install it now, or download the installer to run yourself.", result.Status);
        Assert.Equal("https://github.com/cteerakit/mango/releases/download/v1.0.10.0/MangoSetup-x64.exe", result.DownloadUri?.AbsoluteUri);
        Assert.Equal("https://github.com/cteerakit/mango/releases/download/v1.0.10.0/SHA256SUMS.txt", result.ChecksumUri?.AbsoluteUri);
    }

    [Fact]
    public async Task ReleaseWithoutChecksumFileIsNotOffered()
    {
        const string json = "{\"tag_name\":\"v1.0.4.0\",\"assets\":[{\"name\":\"MangoSetup-x64.exe\",\"browser_download_url\":\"https://github.com/cteerakit/mango/releases/download/v1.0.4.0/MangoSetup-x64.exe\"}]}";
        using var client = CreateClient(json);
        var result = await new AppUpdateService(client, new Version(1, 0, 3, 0)).CheckForUpdatesAsync();
        Assert.Contains("installer is not available yet", result.Status);
        Assert.Null(result.DownloadUri);
    }

    [Fact]
    public async Task VerifiedInstallerIsDownloaded()
    {
        var installer = "installer bytes"u8.ToArray();
        using var client = DownloadClient(installer, Convert.ToHexStringLower(SHA256.HashData(installer)));
        var service = new AppUpdateService(client, new Version(1, 0, 3, 0));
        var update = await service.CheckForUpdatesAsync();
        var reported = new List<double>();
        var download = await service.DownloadInstallerAsync(update, new Progress<double>(reported.Add));
        try
        {
            Assert.NotNull(download.InstallerPath);
            Assert.Equal(installer, await File.ReadAllBytesAsync(download.InstallerPath));
        }
        finally
        {
            if (download.InstallerPath is not null)
            {
                Directory.Delete(Path.GetDirectoryName(download.InstallerPath)!, recursive: true);
            }
        }
    }

    [Fact]
    public async Task InstallerWithWrongChecksumIsDeleted()
    {
        using var client = DownloadClient("installer bytes"u8.ToArray(), new string('0', 64));
        var service = new AppUpdateService(client, new Version(1, 0, 3, 0));
        var update = await service.CheckForUpdatesAsync();
        var before = Directory.GetDirectories(Path.GetTempPath(), "Mango-update-*").Length;
        var download = await service.DownloadInstallerAsync(update);
        Assert.Null(download.InstallerPath);
        Assert.Contains("did not match", download.Status);
        Assert.Equal(before, Directory.GetDirectories(Path.GetTempPath(), "Mango-update-*").Length);
    }

    [Fact]
    public async Task FailedDownloadCanBeRetried()
    {
        using var client = DownloadClient("x"u8.ToArray(), new string('0', 64), failDownload: true);
        var service = new AppUpdateService(client, new Version(1, 0, 3, 0));
        var update = await service.CheckForUpdatesAsync();
        var download = await service.DownloadInstallerAsync(update);
        Assert.Null(download.InstallerPath);
        Assert.Contains("Could not download the update", download.Status);
    }

    [Fact]
    public async Task DownloadWithoutAnUpdateDoesNothing()
    {
        var download = await new AppUpdateService(CreateClient("{}")).DownloadInstallerAsync(new("Mango is up to date."));
        Assert.Null(download.InstallerPath);
    }

    [Theory]
    [InlineData("abc  MangoSetup-x64.exe", null)]
    [InlineData("", null)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF  other.exe", null)]
    [InlineData("zzzz456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  MangoSetup-x64.exe", null)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  MangoSetup-x64.exe", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("other\r\n0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF *MangoSetup-x64.exe\r\n", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void ChecksumFileIsParsed(string text, string? expected) =>
        Assert.Equal(expected, AppUpdateService.ParseChecksum(text, "MangoSetup-x64.exe"));

    [Fact]
    public void InstallerRunsSilentlyAndRelaunchesMango()
    {
        var arguments = AppUpdateService.InstallerArguments(@"C:\temp\install.log");
        Assert.Contains("/VERYSILENT", arguments);
        Assert.Contains("/RELAUNCH=1", arguments);
        Assert.Contains(@"/LOG=C:\temp\install.log", arguments);
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
        assets = new[]
        {
            new { name = "MangoSetup-x64.exe", browser_download_url = $"https://github.com/cteerakit/mango/releases/download/v{version}/MangoSetup-x64.exe" },
            new { name = "SHA256SUMS.txt", browser_download_url = $"https://github.com/cteerakit/mango/releases/download/v{version}/SHA256SUMS.txt" },
        },
    });

    private static HttpClient DownloadClient(byte[] installer, string checksum, bool failDownload = false) =>
        new(new Handler(request => request.RequestUri!.AbsoluteUri switch
        {
            var uri when uri.EndsWith("/releases/latest") => new(HttpStatusCode.OK) { Content = new StringContent(Release("1.0.4.0")) },
            var uri when uri.EndsWith("/SHA256SUMS.txt") => new(HttpStatusCode.OK) { Content = new StringContent($"{checksum}  MangoSetup-x64.exe\n") },
            _ when failDownload => new(HttpStatusCode.NotFound),
            _ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(installer) },
        }));

    private static HttpClient CreateClient(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(new Handler(_ => new(status) { Content = new StringContent(json) }));

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
