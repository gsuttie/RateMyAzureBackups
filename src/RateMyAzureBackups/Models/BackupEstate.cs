namespace RateMyAzureBackups.Models;

/// <summary>One protected thing inside a vault: a Recovery Services protected item or a Backup vault backup instance.</summary>
public sealed class ProtectedItemInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public string VaultId { get; set; } = string.Empty;
    public string SubscriptionId { get; set; } = string.Empty;

    /// <summary>ARM ID of the resource that is being backed up (VM, storage account, disk, database...).</summary>
    public string SourceResourceId { get; set; } = string.Empty;

    /// <summary>Human workload label, e.g. "Azure VM", "Azure Files", "SQL in Azure VM", "Blob storage".</summary>
    public string WorkloadType { get; set; } = string.Empty;
    public string RawType { get; set; } = string.Empty;
    public string BackupManagementType { get; set; } = string.Empty;
    public string ContainerName { get; set; } = string.Empty;

    /// <summary>Protected, ProtectionConfigured, ProtectionStopped, ProtectionError, IRPending, SoftDeleted...</summary>
    public string ProtectionState { get; set; } = string.Empty;
    public string HealthStatus { get; set; } = string.Empty;

    /// <summary>Completed, Healthy, Failed, Unhealthy, CompletedWithWarnings, InProgress, IRPending or empty.</summary>
    public string LastBackupStatus { get; set; } = string.Empty;
    public DateTimeOffset? LastBackupTime { get; set; }

    public string? PolicyId { get; set; }
    public string PolicyName { get; set; } = string.Empty;

    public string ErrorCode { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;

    public string Display => string.IsNullOrEmpty(FriendlyName) ? Name : FriendlyName;

    private static readonly string[] StoppedStates =
        ["ProtectionStopped", "ProtectionPaused", "BackupsSuspended", "BackupSchedulesSuspended", "RetentionSchedulesSuspended", "SoftDeleted"];
    private static readonly string[] ErroredStates =
        ["ProtectionError", "ConfiguringProtectionFailed", "Invalid", "NotProtected"];

    public bool IsStopped => StoppedStates.Contains(ProtectionState, StringComparer.OrdinalIgnoreCase);
    public bool IsErrored => ErroredStates.Contains(ProtectionState, StringComparer.OrdinalIgnoreCase);

    /// <summary>Backups are still being taken (protection is neither stopped nor in error).</summary>
    public bool IsActive => !IsStopped && !IsErrored;

    /// <summary>Initial backup not taken yet, or protection being (re)configured.</summary>
    public bool IsPending => ProtectionState is "IRPending" or "ConfiguringProtection" or "UpdatingProtection";

    /// <summary>true = last backup succeeded, false = failed, null = unknown / not run yet.</summary>
    public bool? LastBackupSucceeded => LastBackupStatus.ToLowerInvariant() switch
    {
        "completed" or "healthy" or "completedwithwarnings" or "succeeded" => true,
        "failed" or "unhealthy" => false,
        _ => null
    };

    /// <summary>A recovery point exists from within the freshness window.</summary>
    public bool IsFresh(DateTimeOffset now, TimeSpan window) =>
        LastBackupSucceeded == true && LastBackupTime is { } t && now - t <= window;
}

public sealed class BackupVaultInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ResourceGroup { get; set; } = string.Empty;
    public string SubscriptionId { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public BackupFlowKind Kind { get; set; }
    public string SkuName { get; set; } = string.Empty;

    /// <summary>"GeoRedundant", "ZoneRedundant", "LocallyRedundant" or empty when unknown.</summary>
    public string StorageRedundancy { get; set; } = string.Empty;
    public List<string> StorageConfigurations { get; set; } = new();
    public bool? CrossRegionRestore { get; set; }

    /// <summary>"Enabled", "AlwaysOn", "Disabled" or empty when unknown.</summary>
    public string SoftDeleteState { get; set; } = string.Empty;
    public int? SoftDeleteRetentionDays { get; set; }

    /// <summary>"Locked", "Unlocked", "Disabled" or empty when unknown.</summary>
    public string ImmutabilityState { get; set; } = string.Empty;
    public bool? MultiUserAuthorization { get; set; }
    public string PublicNetworkAccess { get; set; } = string.Empty;
    public bool? AlertsForAllJobFailures { get; set; }
    public string SecureScore { get; set; } = string.Empty;

    public bool? DiagnosticsEnabled { get; set; }
    public List<string> LogDestinations { get; set; } = new();
    public List<string> LogWorkspaceIds { get; set; } = new();

    public List<ProtectedItemInfo> Items { get; set; } = new();

    public bool IsGeoRedundant => StorageRedundancy.StartsWith("Geo", StringComparison.OrdinalIgnoreCase) || StorageRedundancy.StartsWith("ReadAccessGeo", StringComparison.OrdinalIgnoreCase);
    public bool IsZoneRedundant => StorageRedundancy.StartsWith("Zone", StringComparison.OrdinalIgnoreCase);
    public bool IsLocallyRedundant => StorageRedundancy.StartsWith("Local", StringComparison.OrdinalIgnoreCase);

    /// <summary>true = on (Enabled / AlwaysOn / On), false = off, null = unknown.</summary>
    public bool? SoftDeleteEnabled => SoftDeleteState.ToLowerInvariant() switch
    {
        "enabled" or "alwayson" or "on" => true,
        "disabled" or "off" => false,
        _ => null
    };
    public bool SoftDeleteAlwaysOn => SoftDeleteState.Equals("AlwaysOn", StringComparison.OrdinalIgnoreCase);
    public bool ImmutabilityLocked => ImmutabilityState.Equals("Locked", StringComparison.OrdinalIgnoreCase);
    public bool ImmutabilityUnlocked => ImmutabilityState.Equals("Unlocked", StringComparison.OrdinalIgnoreCase);

    public string RedundancyLabel => StorageRedundancy.ToLowerInvariant() switch
    {
        "georedundant" => "GRS",
        "readaccessgeozoneredundant" => "RA-GZRS",
        "zoneredundant" => "ZRS",
        "locallyredundant" => "LRS",
        "" => "unknown",
        _ => StorageRedundancy
    };

    public string KindLabel => Kind.Label();
    public int ActiveItemCount => Items.Count(i => i.IsActive);
}

public sealed class BackupPolicyInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ResourceGroup { get; set; } = string.Empty;
    public string SubscriptionId { get; set; } = string.Empty;
    public string VaultId { get; set; } = string.Empty;
    public BackupFlowKind Kind { get; set; }

    /// <summary>Workload label, e.g. "Azure VM", "SQL in Azure VM", "Blob storage".</summary>
    public string WorkloadType { get; set; } = string.Empty;
    public string BackupManagementType { get; set; } = string.Empty;
    public string PolicyType { get; set; } = string.Empty;
    public string TimeZone { get; set; } = string.Empty;

    /// <summary>"Hourly", "Daily", "Weekly", "Continuous" or empty when unknown.</summary>
    public string ScheduleFrequency { get; set; } = string.Empty;
    public int? ScheduleIntervalHours { get; set; }
    public string ScheduleSummary { get; set; } = string.Empty;

    public int? DailyRetentionDays { get; set; }
    public int? WeeklyRetentionWeeks { get; set; }
    public int? MonthlyRetentionMonths { get; set; }
    public int? YearlyRetentionYears { get; set; }
    public int? InstantRpRetentionDays { get; set; }

    /// <summary>SQL / SAP HANA log backup cadence, when the policy has one.</summary>
    public int? LogBackupFrequencyMinutes { get; set; }
    public int? LogRetentionDays { get; set; }

    public int ProtectedItemCount { get; set; }

    public bool IsDailyOrBetter => ScheduleFrequency is "Daily" or "Hourly" or "Continuous";
    public bool HasLongTermRetention => (WeeklyRetentionWeeks ?? 0) > 0 || (MonthlyRetentionMonths ?? 0) > 0 || (YearlyRetentionYears ?? 0) > 0;

    /// <summary>Shortest-lived recovery points the policy keeps (daily retention, or the weekly one when there is no daily schedule).</summary>
    public int? ShortTermRetentionDays => DailyRetentionDays ?? (WeeklyRetentionWeeks is { } w ? w * 7 : null);

    /// <summary>Longest retention the policy offers, in days.</summary>
    public int? MaxRetentionDays
    {
        get
        {
            var candidates = new List<int>();
            if (DailyRetentionDays is { } d) candidates.Add(d);
            if (WeeklyRetentionWeeks is { } w) candidates.Add(w * 7);
            if (MonthlyRetentionMonths is { } m) candidates.Add(m * 30);
            if (YearlyRetentionYears is { } y) candidates.Add(y * 365);
            return candidates.Count == 0 ? null : candidates.Max();
        }
    }

    public string RetentionSummary
    {
        get
        {
            var parts = new List<string>();
            if (DailyRetentionDays is { } d) parts.Add($"daily {d}d");
            if (WeeklyRetentionWeeks is { } w) parts.Add($"weekly {w}w");
            if (MonthlyRetentionMonths is { } m) parts.Add($"monthly {m}m");
            if (YearlyRetentionYears is { } y) parts.Add($"yearly {y}y");
            return parts.Count == 0 ? "unknown" : string.Join(" · ", parts);
        }
    }
}

/// <summary>A workload in scope that no vault protects (currently virtual machines).</summary>
public sealed class UnprotectedResourceInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ResourceGroup { get; set; } = string.Empty;
    public string SubscriptionId { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string ResourceType { get; set; } = "Virtual machine";
    public string PowerState { get; set; } = string.Empty;
}

public sealed class BackupFinding
{
    /// <summary>"High", "Medium" or "Info".</summary>
    public string Severity { get; set; } = "Info";
    public string Category { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string ResourceName { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public string SubscriptionId { get; set; } = string.Empty;

    /// <summary>Concrete guidance on how to fix the finding.</summary>
    public string Fix { get; set; } = string.Empty;
}

/// <summary>Complete result of a backup estate scan for one flow across one or more subscriptions.</summary>
public sealed class BackupScanResult
{
    public BackupFlowKind Flow { get; set; }
    public List<BackupVaultInfo> Vaults { get; set; } = new();
    public List<BackupPolicyInfo> Policies { get; set; } = new();
    public List<UnprotectedResourceInfo> UnprotectedResources { get; set; } = new();

    /// <summary>Total virtual machines seen in scope (protected or not); only populated for the Recovery Services flow.</summary>
    public int VirtualMachineCount { get; set; }

    public List<BackupFinding> Findings { get; set; } = new();
    public List<BackupRating> Ratings { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public List<string> SubscriptionIds { get; set; } = new();
    public List<ResourceInventoryInfo> ResourceInventory { get; set; } = new();
    public List<string> InventorySubscriptionIds { get; set; } = new();
    public List<BackupCostLine> Costs { get; set; } = new();
    public List<BackupCostCoverage> CostCoverage { get; set; } = new();
    public bool IsSampleData { get; set; }
    public DateTime CompletedAt { get; set; } = DateTime.Now;

    public int VaultCount => Vaults.Count;
    public IEnumerable<ProtectedItemInfo> AllItems => Vaults.SelectMany(v => v.Items);
    public int ItemCount => Vaults.Sum(v => v.Items.Count);

    public void Merge(BackupScanResult other)
    {
        Vaults.AddRange(other.Vaults);
        Policies.AddRange(other.Policies);
        UnprotectedResources.AddRange(other.UnprotectedResources);
        VirtualMachineCount += other.VirtualMachineCount;
        Findings.AddRange(other.Findings);
        Ratings.AddRange(other.Ratings);
        Warnings.AddRange(other.Warnings);
        SubscriptionIds.AddRange(other.SubscriptionIds);
        ResourceInventory.AddRange(other.ResourceInventory);
        InventorySubscriptionIds.AddRange(other.InventorySubscriptionIds);
        Costs.AddRange(other.Costs);
        CostCoverage.AddRange(other.CostCoverage);
        IsSampleData |= other.IsSampleData;
    }
}
