namespace RateMyAzureBackups.Models;

public sealed class SubscriptionInfo
{
    public string SubscriptionId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
}

public sealed class TenantInfo
{
    public string TenantId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string DefaultDomain { get; set; } = string.Empty;

    public string BestName => string.IsNullOrWhiteSpace(DisplayName) ? TenantId : DisplayName;
}

/// <summary>Which of the two vault-product flows the user is in.</summary>
public enum BackupFlowKind { RecoveryServices, BackupVault }

public static class BackupFlowKindExtensions
{
    public static string Label(this BackupFlowKind kind) => kind == BackupFlowKind.RecoveryServices ? "Recovery Services vault" : "Backup vault";
    public static string PluralLabel(this BackupFlowKind kind) => kind == BackupFlowKind.RecoveryServices ? "Recovery Services vaults" : "Backup vaults";
    public static string Route(this BackupFlowKind kind) => kind == BackupFlowKind.RecoveryServices ? "recovery-services" : "backup-vaults";

    /// <summary>What one protected thing is called in each product.</summary>
    public static string ItemLabel(this BackupFlowKind kind) => kind == BackupFlowKind.RecoveryServices ? "protected item" : "backup instance";
    public static string ItemPluralLabel(this BackupFlowKind kind) => kind == BackupFlowKind.RecoveryServices ? "protected items" : "backup instances";
}

/// <summary>Counts of flow-relevant resources in one subscription (from Azure Resource Graph).</summary>
public sealed class BackupSubscriptionSummary
{
    public string SubscriptionId { get; set; } = string.Empty;
    public int RecoveryServicesVaultCount { get; set; }
    public int BackupVaultCount { get; set; }
    public int ProtectedItemCount { get; set; }
    public int BackupInstanceCount { get; set; }

    public int VaultCount(BackupFlowKind kind) => kind == BackupFlowKind.RecoveryServices ? RecoveryServicesVaultCount : BackupVaultCount;
    public int ItemCount(BackupFlowKind kind) => kind == BackupFlowKind.RecoveryServices ? ProtectedItemCount : BackupInstanceCount;
    public bool IsRelevant(BackupFlowKind kind) => VaultCount(kind) + ItemCount(kind) > 0;
}
