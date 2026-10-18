using System.Net;
using Mango.Services;
using Xunit;

namespace Mango.Tests;

public sealed class StratzServiceTests : IDisposable
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "Mango.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReadsPositionsWithBearerTokenAndCachesThem()
    {
        var calls = 0;
        using var client = Client(async request =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("token", request.Headers.Authorization?.Parameter);
            Assert.Contains("\"id\":9050691699", await request.Content!.ReadAsStringAsync());
            return """
                {"data":{"match":{"players":[
                {"steamAccountId":118993939,"position":"POSITION_2"},
                {"steamAccountId":1088652637,"position":"POSITION_5"},
                {"steamAccountId":0,"position":"POSITION_1"},
                {"steamAccountId":6304677,"position":"UNKNOWN"}]}}}
                """;
        });
        var service = new StratzService(_cache, client);

        var positions = await service.GetMatchPositionsAsync(9050691699, " token ");
        var cached = await service.GetMatchPositionsAsync(9050691699, "token");

        Assert.Equal(2, positions![118993939]);
        Assert.Equal(5, positions[1088652637]);
        Assert.Equal(2, positions.Count);
        Assert.Equal(positions, cached);
        Assert.Equal(1, calls);
        Assert.Contains("STRATZ_API", client.DefaultRequestHeaders.UserAgent.ToString());
    }

    [Fact]
    public async Task ReadsOnlyTheRequestedPlayersPurchasesInTimeOrderAndCachesThem()
    {
        var calls = 0;
        using var client = Client(request =>
        {
            calls++;
            return Task.FromResult<string?>("""
                {"data":{"match":{"players":[
                {"steamAccountId":1,"stats":{"itemPurchases":[{"itemId":9,"time":50}]}},
                {"steamAccountId":2,"stats":{"itemPurchases":[{"itemId":5,"time":900},{"itemId":7,"time":-30}]}}]}}}
                """);
        });
        var service = new StratzService(_cache, client);

        var purchases = await service.GetPlayerPurchasesAsync(90, 2, " token ");
        var cached = await service.GetPlayerPurchasesAsync(90, 2, "token");

        Assert.Equal(new[] { new StratzItemPurchase(7, -30), new StratzItemPurchase(5, 900) }, purchases);
        Assert.Equal(purchases, cached);
        Assert.Equal(1, calls);
        Assert.Null(await service.GetPlayerPurchasesAsync(90, 3, "token"));
    }

    [Theory]
    [InlineData("""{"data":{"match":null}}""")]
    [InlineData("""{"message":"A bearer token is required for a request."}""")]
    [InlineData(null)]
    public async Task MissingMatchOrRejectedTokenReturnsNullWithoutCaching(string? body)
    {
        var calls = 0;
        using var client = Client(_ => { calls++; return Task.FromResult(body); });
        var service = new StratzService(_cache, client);

        Assert.Null(await service.GetMatchPositionsAsync(1, "token"));
        Assert.Null(await service.GetMatchPositionsAsync(1, "token"));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task NoTokenSkipsTheRequest()
    {
        using var client = Client(_ => throw new InvalidOperationException("No request expected."));
        Assert.Null(await new StratzService(_cache, client).GetMatchPositionsAsync(1, " "));
    }

    private static HttpClient Client(Func<HttpRequestMessage, Task<string?>> respond) =>
        new(new Handler(respond)) { BaseAddress = new Uri("https://api.stratz.com/") };

    private sealed class Handler(Func<HttpRequestMessage, Task<string?>> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = await respond(request);
            return new HttpResponseMessage(json is null ? HttpStatusCode.Unauthorized : HttpStatusCode.OK)
            { Content = new StringContent(json ?? "") };
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_cache)) Directory.Delete(_cache, recursive: true);
    }
}
