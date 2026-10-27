using System.ComponentModel.DataAnnotations;

namespace RateMyAzureBackups.Models;

public sealed class ResourceInventoryInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ResourceType { get; set; } = "";
    public string SubscriptionId { get; set; } = "";
    public string ResourceGroup { get; set; } = "";
    public string Location { get; set; } = "";
    public string StorageConfiguration { get; set; } = "";
    public string Environment { get; set; } = "Unknown";
    public string Protection { get; set; } = "Not assessed";
    public List<string> BackupItemIds { get; set; } = [];
    public RecoveryRequirements Recovery { get; set; } = new();
}

public sealed class RecoveryRequirements
{
    [Range(1, int.MaxValue)]
    public int? RpoHours { get; set; }
    [Range(1, int.MaxValue)]
    public int? RtoHours { get; set; }
    [Range(1, int.MaxValue)]
    public int? RetentionDays { get; set; }
    public string Owner { get; set; } = "";
    public string RestoreTestEvidence { get; set; } = "";
    public string Notes { get; set; } = "";
}

public sealed class BackupCostLine
{
    public string SubscriptionId { get; set; } = "";
    public string ResourceId { get; set; } = "";
    public string Meter { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
}

public sealed class BackupCostCoverage
{
    public string SubscriptionId { get; set; } = "";
    public DateTime FromUtc { get; set; }
    public DateTime ToUtc { get; set; }
    public bool Available { get; set; }
    public string Error { get; set; } = "";
}

public sealed record PolicyRecommendation(string ResourceId, string ResourceName, string Environment,
    string Workload, string Policy, string RecoveryGap);
