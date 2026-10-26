using System.Text.Json;

namespace RateMyAzureBackups.Services.Azure;

/// <summary>
/// Thin, read-only access to the Azure data planes the app needs. Everything the app knows about a
/// customer's backup estate comes through here, so tests can substitute canned JSON.
/// </summary>
public interface IAzureApi
{
    /// <summary>Runs an Azure Resource Graph query across the given subscriptions, following $skipToken paging.</summary>
    Task<List<JsonElement>> QueryResourceGraphAsync(string query, IReadOnlyCollection<string> subscriptionIds, string? tenantId, CancellationToken ct = default);

    /// <summary>GETs an ARM collection URL and returns the concatenated "value" items across nextLink pages.</summary>
    Task<List<JsonElement>> GetArmListAsync(string url, string? tenantId, CancellationToken ct = default);

    /// <summary>GETs a single ARM resource URL and returns the JSON body.</summary>
    Task<JsonElement> GetArmAsync(string url, string? tenantId, CancellationToken ct = default);
}
