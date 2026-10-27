using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Logging.Abstractions;
using RateMyAzureBackups.Models;
using RateMyAzureBackups.Services;
using RateMyAzureBackups.Services.Azure;
using Xunit;

namespace RateMyAzureBackups.Tests;

public class AzureQueryResilienceTests
{
    private const string Url = "https://management.azure.com/subscriptions/s/providers/Microsoft.CostManagement/query?api-version=2025-03-01";
    private static readonly object Body = new { type = "ActualCost" };

    [Fact]
    public void RetryDelay_UsesLongestQuotaHeaderAndStandardDate()
    {
        var now = new DateTimeOffset(2026, 10, 26, 16, 0, 0, TimeSpan.Zero);
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
        response.Headers.Add("x-ms-ratelimit-microsoft.costmanagement-qpu-retry-after", "30");
        response.Headers.Add("x-ms-ratelimit-microsoft.costmanagement-entity-retry-after", "45");
        Assert.Equal(TimeSpan.FromSeconds(45), AzureApi.QueryRetryDelay(response, 0, now));
        response.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddMinutes(2));
        Assert.Equal(TimeSpan.FromMinutes(2), AzureApi.QueryRetryDelay(response, 0, now));
    }

    [Fact]
    public void RetryDelay_MissingOrMalformedHeaderUsesExponentialBackoffWithJitter()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("Retry-After", "invalid");
        response.Headers.Add("x-ms-ratelimit-microsoft.costmanagement-qpu-retry-after", "-1");
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var delay = AzureApi.QueryRetryDelay(response, attempt, DateTimeOffset.UtcNow);
            Assert.InRange(delay.TotalSeconds, Math.Pow(2, attempt + 1), Math.Pow(2, attempt + 1) + 1);
        }
    }

    [Fact]
    public async Task Query_Retries429WithSamePayloadThenCachesSuccess()
    {
        var clock = new FastTimeProvider();
        var factory = new Factory(call => call == 1 ? Throttle("3") : Success());
        var api = Api(factory, clock);
        var result = await api.PostArmQueryAsync(Url, Body, "tenant");
        Assert.True(result.GetProperty("ok").GetBoolean());
        await api.PostArmQueryAsync(Url, Body, "tenant");
        Assert.Equal(2, factory.Handler.Calls);
        Assert.All(factory.Handler.Bodies, payload => Assert.Equal(JsonSerializer.Serialize(Body), payload));
        Assert.Contains(TimeSpan.FromSeconds(3), clock.Delays);
        Assert.Equal(1, factory.Handler.MaxConcurrent);
    }

    [Fact]
    public async Task Query_ConcurrentIdenticalRequestsAreCoalesced_AndCacheScopeIsIsolated()
    {
        var clock = new FastTimeProvider();
        var factory = new Factory(_ => Success());
        var api = Api(factory, clock);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => api.PostArmQueryAsync(Url, Body, "tenant")));
        Assert.Equal(1, factory.Handler.Calls);
        await api.PostArmQueryAsync(Url, Body, "other-tenant");
        await api.PostArmQueryAsync(Url.Replace("/s/", "/other/"), Body, "tenant");
        await api.PostArmQueryAsync(Url, new { type = "ActualCost", timeframe = "Custom" }, "tenant");
        await api.PostArmQueryAsync(Url + "&$skiptoken=2", Body, "tenant");
        Assert.Equal(5, factory.Handler.Calls);
        Assert.Equal(1, factory.Handler.MaxConcurrent);
        Assert.All(clock.Delays, delay => Assert.True(delay >= TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Query_CacheExpiresAfterFourHours()
    {
        var clock = new FastTimeProvider();
        var factory = new Factory(_ => Success());
        var api = Api(factory, clock);
        await api.PostArmQueryAsync(Url, Body, null);
        clock.Advance(TimeSpan.FromHours(3));
        await api.PostArmQueryAsync(Url, Body, null);
        Assert.Equal(1, factory.Handler.Calls);
        clock.Advance(TimeSpan.FromHours(1));
        await api.PostArmQueryAsync(Url, Body, null);
        Assert.Equal(2, factory.Handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task Query_DoesNotRetryOrCachePermanentFailures(HttpStatusCode status)
    {
        var factory = new Factory(call => call == 1 ? new HttpResponseMessage(status) { Content = new StringContent("denied") } : Success());
        var api = Api(factory, new FastTimeProvider());
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => api.PostArmQueryAsync(Url, Body, null));
        Assert.Equal(status, error.StatusCode);
        Assert.Equal(1, factory.Handler.Calls);
        await api.PostArmQueryAsync(Url, Body, null);
        Assert.Equal(2, factory.Handler.Calls);
    }

    [Fact]
    public async Task Query_ExhaustsRetriesAndPreservesCooldownForNextRequest()
    {
        var clock = new FastTimeProvider();
        var factory = new Factory(_ => Throttle("1"));
        var api = Api(factory, clock);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => api.PostArmQueryAsync(Url, Body, null));
        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Equal(4, factory.Handler.Calls);
        Assert.Equal(3, clock.Delays.Count);
        factory.Handler.Respond = _ => Success();
        await api.PostArmQueryAsync(Url, Body, null);
        Assert.Equal(4, clock.Delays.Count);
    }

    [Fact]
    public async Task Query_LongAzureCooldownFailsPromptlyWithoutEarlyRetryOrDiscardingCachedSuccess()
    {
        var clock = new FastTimeProvider();
        var factory = new Factory(call => call == 1 ? Success() : Throttle("3600"));
        var api = Api(factory, clock);
        await api.PostArmQueryAsync(Url, Body, null);
        await Assert.ThrowsAsync<HttpRequestException>(() => api.PostArmQueryAsync(Url, new { query = "new" }, null));
        Assert.Equal(2, factory.Handler.Calls);
        var delayCount = clock.Delays.Count;
        await Assert.ThrowsAsync<HttpRequestException>(() => api.PostArmQueryAsync(Url, new { query = "another" }, null));
        Assert.Equal(2, factory.Handler.Calls);
        Assert.Equal(delayCount, clock.Delays.Count);
        await api.PostArmQueryAsync(Url, Body, null);
        Assert.Equal(2, factory.Handler.Calls);
    }

    [Fact]
    public async Task Query_StopsWhenCumulativeRetryWaitWouldExceedTwoMinutes()
    {
        var clock = new FastTimeProvider();
        var factory = new Factory(_ => Throttle("45"));
        var api = Api(factory, clock);
        await Assert.ThrowsAsync<HttpRequestException>(() => api.PostArmQueryAsync(Url, Body, null));
        Assert.Equal(3, factory.Handler.Calls);
        Assert.Equal(2, clock.Delays.Count);
        Assert.Equal(TimeSpan.FromSeconds(90), TimeSpan.FromTicks(clock.Delays.Sum(delay => delay.Ticks)));
    }

    [Fact]
    public async Task Query_CancelledCallerCannotReadCacheOrIssueMoreRequests()
    {
        var factory = new Factory(_ => Success());
        var api = Api(factory, new FastTimeProvider());
        await api.PostArmQueryAsync(Url, Body, null);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.PostArmQueryAsync(Url, Body, null, cts.Token));
        Assert.Equal(1, factory.Handler.Calls);
    }

    [Fact]
    public async Task Query_CancellationDuringBackoffReleasesGate()
    {
        var clock = new FastTimeProvider();
        using var cts = new CancellationTokenSource();
        clock.BeforeDelay = () => cts.Cancel();
        var factory = new Factory(_ => Throttle("1"));
        var api = Api(factory, clock);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.PostArmQueryAsync(Url, Body, null, cts.Token));
        Assert.Equal(1, factory.Handler.Calls);
        clock.BeforeDelay = null;
        factory.Handler.Respond = _ => Success();
        await api.PostArmQueryAsync(Url, Body, null);
        Assert.Equal(2, factory.Handler.Calls);
    }

    [Fact]
    public async Task Scan_ThrottlingWarningDoesNotSuggestPermissionFailureOrReportZeroCosts()
    {
        var scan = await new BackupDiscoveryService(new ThrottledApi(), NullLogger<BackupDiscoveryService>.Instance)
            .ScanAsync(BackupFlowKind.BackupVault, ["s"], null);
        Assert.Empty(scan.Costs);
        Assert.False(Assert.Single(scan.CostCoverage).Available);
        var warning = Assert.Single(scan.Warnings);
        Assert.Contains("Azure throttled", warning);
        Assert.DoesNotContain("Reader access", warning);
        var context = new BackupReportContext { Scan = scan, Flow = BackupFlowKind.BackupVault };
        Assert.Contains("429", BackupReportService.ExtensiveHtml(context));
        Assert.Contains("429", BackupReportService.ExtensiveMarkdown(context));
    }

    private static AzureApi Api(Factory factory, TimeProvider clock) => new(new Credential(), factory, NullLogger<AzureApi>.Instance, clock);
    private static HttpResponseMessage Success() => new(HttpStatusCode.OK) { Content = new StringContent("""{"ok":true}""") };
    private static HttpResponseMessage Throttle(string seconds)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.Add("x-ms-ratelimit-microsoft.costmanagement-qpu-retry-after", seconds);
        return response;
    }

    private sealed class Factory(Func<int, HttpResponseMessage> respond) : IHttpClientFactory
    {
        public Handler Handler { get; } = new(respond);
        public HttpClient CreateClient(string name) => new(Handler, disposeHandler: false);
    }

    private sealed class Handler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public Func<int, HttpResponseMessage> Respond { get; set; } = respond;
        public int Calls { get; private set; }
        public int MaxConcurrent { get; private set; }
        public List<string> Bodies { get; } = [];
        private int _active;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _active++;
            MaxConcurrent = Math.Max(MaxConcurrent, _active);
            try
            {
                Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
                await Task.Yield();
                return Respond(++Calls);
            }
            finally { _active--; }
        }
    }

    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("test-token", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class FastTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public List<TimeSpan> Delays { get; } = [];
        public Action? BeforeDelay { get; set; }
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan time) => _now += time;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Delays.Add(dueTime);
            BeforeDelay?.Invoke();
            return new Timer(s =>
            {
                Advance(dueTime);
                callback(s);
            }, state, TimeSpan.FromMilliseconds(1), Timeout.InfiniteTimeSpan);
        }
    }

    private sealed class ThrottledApi : IAzureApi
    {
        public Task<List<JsonElement>> QueryResourceGraphAsync(string query, IReadOnlyCollection<string> subscriptionIds, string? tenantId, CancellationToken ct = default) =>
            Task.FromResult(new List<JsonElement>());
        public Task<List<JsonElement>> GetArmListAsync(string url, string? tenantId, CancellationToken ct = default) => Task.FromResult(new List<JsonElement>());
        public Task<JsonElement> GetArmAsync(string url, string? tenantId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<JsonElement> PostArmQueryAsync(string url, object body, string? tenantId, CancellationToken ct = default) =>
            throw new HttpRequestException("Azure billing throttled (429)", null, HttpStatusCode.TooManyRequests);
    }
}
