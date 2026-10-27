using System.Text.Json;
using RateMyAzureBackups.Models;
using RateMyAzureBackups.Services.Azure;
using static RateMyAzureBackups.Services.Azure.Json;

namespace RateMyAzureBackups.Services;

public sealed partial class BackupDiscoveryService
{
    internal const string InventoryQuery =
        "Resources | project id, name, type, location, resourceGroup, subscriptionId, tags, sku";

    private async Task ApplyResourceInventoryAsync(BackupScanResult result, IReadOnlyCollection<string> subscriptions,
        string? tenantId, CancellationToken ct)
    {
        try
        {
            var rows = await _azure.QueryResourceGraphAsync(InventoryQuery, subscriptions, tenantId, ct);
            ApplyResourceInventory(result, rows);
            result.InventorySubscriptionIds.AddRange(subscriptions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not inventory resources.");
            result.Warnings.Add($"Resource inventory unavailable: {ex.Message}. Coverage must not be treated as complete.");
            AddBackupSourcesToInventory(result);
        }
    }

    internal static void ApplyResourceInventory(BackupScanResult result, IEnumerable<JsonElement> rows)
    {
        var bySource = result.AllItems.Where(i => !string.IsNullOrWhiteSpace(i.SourceResourceId))
            .ToLookup(i => i.SourceResourceId.TrimEnd('/'), StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var tags = Obj(row, "tags");
            var environment = tags.ValueKind == JsonValueKind.Object
                ? tags.EnumerateObject().FirstOrDefault(p => p.Name.Equals("environment", StringComparison.OrdinalIgnoreCase)
                    || p.Name.Equals("env", StringComparison.OrdinalIgnoreCase)).Value
                : default;
            var resource = new ResourceInventoryInfo
            {
                Id = Str(row, "id"), Name = Str(row, "name"), ResourceType = Str(row, "type"),
                ResourceGroup = Str(row, "resourceGroup"), SubscriptionId = Str(row, "subscriptionId"),
                Location = Str(row, "location"), StorageConfiguration = Str(Obj(row, "sku"), "name"),
                Environment = BackupAssessmentService.NormalizeEnvironment(
                    environment.ValueKind == JsonValueKind.String ? environment.GetString() ?? "" : "")
            };
            var linked = bySource[resource.Id.TrimEnd('/')].ToList();
            resource.BackupItemIds = linked.Select(i => i.Id).ToList();
            resource.Protection = linked.Count > 0
                ? linked.Any(i => i.IsStopped || i.IsErrored) ? "Protection stopped or in error"
                    : linked.All(i => BackupAssessmentService.IsConfirmedProtection(i)) ? "Vault backup items found"
                    : "Protection pending or unknown"
                : BackupAssessmentService.IsBackupCandidate(resource.ResourceType)
                    ? "No vault protection found" : "Not assessed";
            result.ResourceInventory.Add(resource);
        }
        AddBackupSourcesToInventory(result);
    }

    internal static void AddBackupSourcesToInventory(BackupScanResult result)
    {
        // Guest databases, shares and agent sources are not necessarily Resources table entries.
        var known = result.ResourceInventory.Select(r => r.Id.TrimEnd('/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var group in result.AllItems.GroupBy(
                     i => (string.IsNullOrWhiteSpace(i.SourceResourceId) ? i.Id : i.SourceResourceId).TrimEnd('/'),
                     StringComparer.OrdinalIgnoreCase))
        {
            if (!known.Add(group.Key)) continue;
            var items = group.ToList();
            var item = items[0];
            result.ResourceInventory.Add(new ResourceInventoryInfo
            {
                Id = group.Key, Name = item.Display, ResourceType = item.WorkloadType,
                SubscriptionId = item.SubscriptionId, BackupItemIds = items.Select(i => i.Id).ToList(),
                Protection = items.Any(i => i.IsStopped || i.IsErrored) ? "Protection stopped or in error"
                    : items.All(BackupAssessmentService.IsConfirmedProtection) ? "Vault backup items found" : "Protection pending or unknown"
            });
        }
    }

    private async Task ApplyCostsAsync(BackupScanResult result, IReadOnlyCollection<string> subscriptions,
        string? tenantId, CancellationToken ct)
    {
        // The last 30 complete UTC days; the same scope and window apply to both vault products.
        var today = DateTime.UtcNow.Date;
        var to = today.AddTicks(-1);
        var from = today.AddDays(-30);
        foreach (var sub in subscriptions)
        {
            var coverage = new BackupCostCoverage { SubscriptionId = sub, FromUtc = from, ToUtc = to };
            result.CostCoverage.Add(coverage);
            var lines = new List<BackupCostLine>();
            try
            {
                var body = CostQuery(from, to);
                var next = $"{AzureApi.ArmBase}/subscriptions/{sub}/providers/Microsoft.CostManagement/query?api-version=2025-03-01";
                var seen = new HashSet<string>(StringComparer.Ordinal);
                while (!string.IsNullOrEmpty(next))
                {
                    if (!seen.Add(next)) throw new InvalidOperationException("Cost query returned a repeated pagination link.");
                    var page = await _azure.PostArmQueryAsync(next, body, tenantId, ct);
                    lines.AddRange(ParseCostPage(page, sub));
                    next = Str(Obj(page, "properties"), "nextLink");
                }
                result.Costs.AddRange(lines);
                coverage.Available = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not read backup costs for {SubscriptionId}.", sub);
                coverage.Error = ex.Message;
                var guidance = ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests }
                    ? "Azure throttled the billing query; wait before rescanning. Other backup assessment data is unaffected."
                    : "Check Cost Management Reader access and billing availability.";
                result.Warnings.Add($"Backup costs unavailable for {sub}: {ex.Message}. {guidance}");
            }
        }
    }

    internal static object CostQuery(DateTime from, DateTime to) => new
    {
        type = "ActualCost",
        timeframe = "Custom",
        timePeriod = new { from, to },
        dataset = new
        {
            granularity = "None",
            aggregation = new { totalCost = new { name = "PreTaxCost", function = "Sum" } },
            grouping = new[] { new { type = "Dimension", name = "ResourceId" }, new { type = "Dimension", name = "MeterSubCategory" } },
            filter = new { dimensions = new { name = "MeterCategory", @operator = "In", values = new[] { "Backup" } } }
        }
    };

    internal static List<BackupCostLine> ParseCostPage(JsonElement page, string subscriptionId)
    {
        var props = Obj(page, "properties");
        if (!props.TryGetProperty("columns", out var columns) || columns.ValueKind != JsonValueKind.Array
            || !props.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Cost query response is missing columns or rows.");
        var names = columns.EnumerateArray().Select(c => Str(c, "name")).ToList();
        int Index(string name)
        {
            var index = names.FindIndex(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
            return index >= 0 ? index : throw new InvalidOperationException($"Cost query response is missing {name}.");
        }
        var cost = Index("PreTaxCost");
        var resource = Index("ResourceId");
        var meter = Index("MeterSubCategory");
        var currency = Index("Currency");
        var result = new List<BackupCostLine>();
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != names.Count
                || !row[cost].TryGetDecimal(out var amount) || row[currency].ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(row[currency].GetString())
                || row[resource].ValueKind is not (JsonValueKind.String or JsonValueKind.Null)
                || row[meter].ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw new InvalidOperationException("Cost query returned an invalid amount, currency or row.");
            result.Add(new BackupCostLine
            {
                SubscriptionId = subscriptionId, Amount = amount, Currency = row[currency].GetString()!,
                ResourceId = row[resource].ValueKind == JsonValueKind.String ? row[resource].GetString()! : "",
                Meter = row[meter].ValueKind == JsonValueKind.String ? row[meter].GetString()! : "Unallocated"
            });
        }
        return result;
    }
}
