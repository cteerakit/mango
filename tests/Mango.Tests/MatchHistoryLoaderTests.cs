using System.Collections.Concurrent;
using System.Net;
using Mango.Services;
using Xunit;

namespace Mango.Tests;

public sealed class MatchHistoryLoaderTests
{
    [Fact]
    public async Task LoadAsync_CancellationDiscardsTheBatch()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Mango.Tests", Guid.NewGuid().ToString("N"));
        using var handler = new GatedHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.opendota.com/api/") };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var loader = new MatchHistoryLoader(new OpenDotaService(directory, client));
            var loading = loader.LoadAsync(
                [("first", "456", ""), ("second", "789", "")], "123", "999", cancellation.Token);
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);
        }
        finally
        {
            handler.Release.TrySetResult();
            cancellation.Cancel();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task LoadAsync_FailedRecentMatchesKeepEncounterResults()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Mango.Tests", Guid.NewGuid().ToString("N"));
        using var handler = new GatedHandler(failRecent: true);
        handler.Release.TrySetResult();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.opendota.com/api/") };
        try
        {
            var results = await new MatchHistoryLoader(new OpenDotaService(directory, client)).LoadAsync(
                [("first", "456", "")], "123", "999", CancellationToken.None);
            Assert.Equal("0/0", results["first"].EncounterLabel);
            Assert.Equal("Recent matches unavailable.", results["first"].RecentMatchesLabel);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task LoadAsync_StartsMultiplePlayersAndIndependentLookupsBeforeAnyComplete(int playerCount)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Mango.Tests", Guid.NewGuid().ToString("N"));
        using var handler = new GatedHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.opendota.com/api/") };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var loader = new MatchHistoryLoader(new OpenDotaService(directory, client));
            var requests = Enumerable.Range(456, playerCount)
                .Select(id => (Key: id.ToString(), SteamId: id.ToString(), Hero: "")).ToList();
            var loading = loader.LoadAsync(requests, "123", "999", cancellation.Token);
            try
            {
                await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.False(loading.IsCompleted);
                Assert.Equal(6, handler.RequestCount);
            }
            finally
            {
                handler.Release.TrySetResult();
            }
            var results = await loading;
            Assert.Equal(playerCount, results.Count);
            Assert.All(results.Values, result => Assert.Equal("0/0", result.EncounterLabel));
            Assert.Empty(results["456"].RecentMatches);
        }
        finally
        {
            cancellation.Cancel();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private sealed class GatedHandler(bool failRecent = false) : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<string, byte> _requests = new();
        public int RequestCount => _requests.Count;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (failRecent && uri.AbsolutePath.EndsWith("/recentMatches", StringComparison.Ordinal))
                throw new HttpRequestException("Recent matches unavailable");
            if (uri.AbsolutePath != "/api/heroes")
            {
                _requests.TryAdd(uri.PathAndQuery, 0);
                // Recent matches plus both encounter directions, for each of two players.
                if (_requests.Count == 6) Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
        }
    }
}
