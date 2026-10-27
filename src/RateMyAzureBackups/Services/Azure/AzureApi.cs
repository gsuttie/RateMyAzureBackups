using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;

namespace RateMyAzureBackups.Services.Azure;

/// <summary>Live implementation over HTTPS using the configured TokenCredential (az login by default).</summary>
public sealed class AzureApi : IAzureApi
{
    public const string ArmBase = "https://management.azure.com";
    private static readonly string[] ArmScopes = ["https://management.azure.com/.default"];

    private readonly TokenCredential _credential;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AzureApi> _logger;
    private readonly Dictionary<string, AccessToken> _tokenCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private readonly SemaphoreSlim _queryLock = new(1, 1);
    private readonly Dictionary<string, (JsonElement Result, DateTimeOffset Expires)> _queryCache = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private DateTimeOffset _nextQueryAt;
    private static readonly TimeSpan QueryCacheLifetime = TimeSpan.FromHours(4);
    private static readonly TimeSpan QueryWaitBudget = TimeSpan.FromMinutes(2);
    private const int MaxQueryAttempts = 4;
    private const int MaxCachedQueries = 128;

    public AzureApi(TokenCredential credential, IHttpClientFactory httpClientFactory, ILogger<AzureApi> logger, TimeProvider? timeProvider = null)
    {
        _credential = credential;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<List<JsonElement>> QueryResourceGraphAsync(
        string query, IReadOnlyCollection<string> subscriptionIds, string? tenantId, CancellationToken ct = default)
    {
        var rows = new List<JsonElement>();
        var client = _httpClientFactory.CreateClient();
        var token = await GetTokenAsync(tenantId, ArmScopes, ct);
        string? skipToken = null;

        do
        {
            var options = new Dictionary<string, object?> { ["resultFormat"] = "objectArray" };
            if (skipToken is not null) options["$skipToken"] = skipToken;

            var body = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["subscriptions"] = subscriptionIds,
                ["query"] = query,
                ["options"] = options
            });

            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"{ArmBase}/providers/Microsoft.ResourceGraph/resources?api-version=2022-10-01");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Azure Resource Graph query failed ({(int)response.StatusCode}): {Truncate(json, 400)}");

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                foreach (var row in data.EnumerateArray())
                    rows.Add(row.Clone());

            skipToken = doc.RootElement.TryGetProperty("$skipToken", out var st) ? st.GetString() : null;
        } while (!string.IsNullOrEmpty(skipToken));

        return rows;
    }

    public async Task<List<JsonElement>> GetArmListAsync(string url, string? tenantId, CancellationToken ct = default)
    {
        var items = new List<JsonElement>();
        var client = _httpClientFactory.CreateClient();
        var token = await GetTokenAsync(tenantId, ArmScopes, ct);
        var next = url;

        while (!string.IsNullOrEmpty(next))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, next);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await client.SendAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {Truncate(json, 300)}");

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
                foreach (var item in value.EnumerateArray())
                    items.Add(item.Clone());

            next = doc.RootElement.TryGetProperty("nextLink", out var nl) ? nl.GetString() : null;
        }
        return items;
    }

    public async Task<JsonElement> GetArmAsync(string url, string? tenantId, CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient();
        var token = await GetTokenAsync(tenantId, ArmScopes, ct);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {Truncate(json, 300)}");

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private async Task<string> GetTokenAsync(string? tenantId, string[] scopes, CancellationToken ct)
    {
        var key = $"{tenantId}|{scopes[0]}";
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_tokenCache.TryGetValue(key, out var cached) && cached.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5))
                return cached.Token;

            var context = new TokenRequestContext(scopes, tenantId: string.IsNullOrEmpty(tenantId) ? null : tenantId);
            var token = await _credential.GetTokenAsync(context, ct);
            _tokenCache[key] = token;
            return token.Token;
        }

        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Token acquisition failed for tenant {TenantId}.", tenantId);
            throw new InvalidOperationException(
                "Could not acquire an Azure token. Run 'az login' (optionally with --tenant) and try again. " + ex.Message, ex);
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    public async Task<JsonElement> PostArmQueryAsync(string url, object body, string? tenantId, CancellationToken ct = default)
    {
        var uri = new Uri(url);
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("management.azure.com", StringComparison.OrdinalIgnoreCase)
            || uri.Port != 443 || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("ARM query URL must use the Azure management endpoint.");

        var payload = JsonSerializer.Serialize(body);
        var key = JsonSerializer.Serialize(new[] { tenantId ?? "", uri.AbsoluteUri, payload });
        await _queryLock.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            var now = _time.GetUtcNow();
            if (_queryCache.TryGetValue(key, out var cached) && cached.Expires > now)
                return cached.Result;

            var deadline = now + QueryWaitBudget;
            var client = _httpClientFactory.CreateClient();
            for (var attempt = 0; attempt < MaxQueryAttempts; attempt++)
            {
                var delay = _nextQueryAt - _time.GetUtcNow();
                if (delay > TimeSpan.Zero)
                {
                    if (_nextQueryAt > deadline)
                        throw Throttled(delay);
                    await Task.Delay(delay, _time, ct);
                }

                // Recreate the request and refresh credentials on each attempt.
                var token = await GetTokenAsync(tenantId, ArmScopes, ct);
                using var request = new HttpRequestMessage(HttpMethod.Post, uri);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = await client.SendAsync(request, ct);
                _nextQueryAt = _time.GetUtcNow().AddSeconds(1);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var retry = QueryRetryDelay(response, attempt, _time.GetUtcNow());
                    _nextQueryAt = _time.GetUtcNow() + retry;
                    _logger.LogWarning("Azure billing query throttled on attempt {Attempt}; retry after {Delay}.", attempt + 1, retry);
                    if (attempt == MaxQueryAttempts - 1 || _nextQueryAt > deadline)
                        throw Throttled(retry);
                    continue;
                }

                var json = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"ARM query failed ({(int)response.StatusCode}): {Truncate(json, 300)}",
                        null, response.StatusCode);
                using var doc = JsonDocument.Parse(json);
                var result = doc.RootElement.Clone();
                foreach (var expired in _queryCache.Where(entry => entry.Value.Expires <= _time.GetUtcNow()).Select(entry => entry.Key).ToList())
                    _queryCache.Remove(expired);
                if (_queryCache.Count >= MaxCachedQueries)
                    _queryCache.Remove(_queryCache.MinBy(entry => entry.Value.Expires).Key);
                _queryCache[key] = (result, _time.GetUtcNow() + QueryCacheLifetime);
                return result;
            }
            throw new InvalidOperationException("ARM query retry limit reached.");
        }
        finally
        {
            _queryLock.Release();
        }
    }

    internal static TimeSpan QueryRetryDelay(HttpResponseMessage response, int attempt, DateTimeOffset now)
    {
        // Azure may return several independent quota delays; respect the longest.
        var delay = TimeSpan.FromSeconds(1);
        var supplied = false;
        foreach (var header in response.Headers.Where(h => h.Key.Equals("Retry-After", StringComparison.OrdinalIgnoreCase)
                     || h.Key.StartsWith("x-ms-ratelimit-microsoft.costmanagement-", StringComparison.OrdinalIgnoreCase)
                     && h.Key.EndsWith("-retry-after", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var value in header.Value)
            {
                TimeSpan candidate;
                if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
                    candidate = TimeSpan.FromSeconds(Math.Min(seconds, (DateTimeOffset.MaxValue - now).TotalSeconds - 1));
                else if (header.Key.Equals("Retry-After", StringComparison.OrdinalIgnoreCase)
                         && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
                    candidate = date - now;
                else continue;
                supplied = true;
                if (candidate > delay) delay = candidate;
            }
        }
        return supplied ? delay : TimeSpan.FromSeconds(Math.Pow(2, attempt + 1) + Random.Shared.NextDouble());
    }

    private static HttpRequestException Throttled(TimeSpan delay) => new(
        $"Azure Cost Management is rate-limiting billing queries (429). Retry in approximately {Math.Ceiling(delay.TotalSeconds)} seconds. " +
        "Automatic retries are bounded so the backup scan can still complete.", null, HttpStatusCode.TooManyRequests);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
