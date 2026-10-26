using RateMyAzureBackups.Models;

namespace RateMyAzureBackups.Services;

/// <summary>
/// A realistic sample estate for each flow so the app can be explored (and tested end to end)
/// without Azure credentials. Every grade band from A to F appears somewhere.
/// </summary>
public static class DemoDataService
{
    public const string TenantId = "00000000-0000-0000-0000-00000000demo";
    public const string SubProd = "11111111-1111-1111-1111-111111111111";
    public const string SubShared = "22222222-2222-2222-2222-222222222222";

    /// <summary>Fixed "now" so the sample estate always produces the same grades.</summary>
    public static readonly DateTimeOffset Now = new(2026, 10, 26, 09, 00, 00, TimeSpan.Zero);

    public static List<SubscriptionInfo> Subscriptions() =>
    [
        new() { SubscriptionId = SubProd, DisplayName = "Contoso Production", State = "Enabled", TenantId = TenantId },
        new() { SubscriptionId = SubShared, DisplayName = "Contoso Shared Services", State = "Enabled", TenantId = TenantId },
    ];

    public static Dictionary<string, TenantInfo> Tenants() => new(StringComparer.OrdinalIgnoreCase)
    {
        [TenantId] = new TenantInfo { TenantId = TenantId, DisplayName = "Contoso Ltd (demo)", DefaultDomain = "contoso.onmicrosoft.com" }
    };

    public static Dictionary<string, BackupSubscriptionSummary> Summaries() => new(StringComparer.OrdinalIgnoreCase)
    {
        [SubProd] = new() { SubscriptionId = SubProd, RecoveryServicesVaultCount = 2, ProtectedItemCount = 7, BackupVaultCount = 1, BackupInstanceCount = 3 },
        [SubShared] = new() { SubscriptionId = SubShared, RecoveryServicesVaultCount = 2, ProtectedItemCount = 3, BackupVaultCount = 1, BackupInstanceCount = 2 },
    };

    private static string Rg(string sub, string rg) => $"/subscriptions/{sub}/resourceGroups/{rg}/providers";

    public static BackupScanResult Scan(BackupFlowKind flow, IReadOnlyCollection<string> subscriptionIds)
    {
        var result = flow == BackupFlowKind.RecoveryServices ? RecoveryServicesEstate() : BackupVaultEstate();
        result.SubscriptionIds = subscriptionIds.ToList();
        var subs = new HashSet<string>(subscriptionIds, StringComparer.OrdinalIgnoreCase);
        result.Vaults = result.Vaults.Where(v => subs.Contains(v.SubscriptionId)).ToList();
        result.Policies = result.Policies.Where(p => subs.Contains(p.SubscriptionId)).ToList();
        result.UnprotectedResources = result.UnprotectedResources.Where(r => subs.Contains(r.SubscriptionId)).ToList();
        BackupDiscoveryService.Finalize(result, Now);
        return result;
    }

    // ── Recovery Services vaults ─────────────────────────────────────────────

    public static BackupScanResult RecoveryServicesEstate()
    {
        var result = new BackupScanResult { Flow = BackupFlowKind.RecoveryServices, VirtualMachineCount = 9 };

        // 1. Production vault, everything right → A
        var prodId = $"{Rg(SubProd, "rg-backup-prod")}/Microsoft.RecoveryServices/vaults/rsv-contoso-prod";
        var prodVmPolicy = $"{prodId}/backupPolicies/EnhancedPolicy-Prod";
        var prodSqlPolicy = $"{prodId}/backupPolicies/SqlHourlyLogs";
        var prodFilesPolicy = $"{prodId}/backupPolicies/DailyFileShares";
        result.Vaults.Add(new BackupVaultInfo
        {
            Id = prodId, Name = "rsv-contoso-prod", ResourceGroup = "rg-backup-prod", SubscriptionId = SubProd, Location = "westeurope",
            Kind = BackupFlowKind.RecoveryServices, SkuName = "RS0",
            StorageRedundancy = "GeoRedundant", CrossRegionRestore = true,
            SoftDeleteState = "AlwaysOn", SoftDeleteRetentionDays = 30, ImmutabilityState = "Locked", MultiUserAuthorization = true,
            PublicNetworkAccess = "Disabled", AlertsForAllJobFailures = true, SecureScore = "Maximum",
            DiagnosticsEnabled = true, LogDestinations = ["Log Analytics: law-contoso-prod"],
            Items =
            [
                Vm(prodId, SubProd, "vm-web-01", "rg-app-prod", prodVmPolicy, "EnhancedPolicy-Prod", Now.AddHours(-5)),
                Vm(prodId, SubProd, "vm-web-02", "rg-app-prod", prodVmPolicy, "EnhancedPolicy-Prod", Now.AddHours(-5)),
                Vm(prodId, SubProd, "vm-sql-01", "rg-data-prod", prodVmPolicy, "EnhancedPolicy-Prod", Now.AddHours(-6)),
                Sql(prodId, SubProd, "vm-sql-01", "rg-data-prod", "ContosoERP", prodSqlPolicy, "SqlHourlyLogs", Now.AddMinutes(-40)),
                Files(prodId, SubProd, "stcontosoprod", "rg-data-prod", "finance-share", prodFilesPolicy, "DailyFileShares", Now.AddHours(-9)),
            ]
        });
        result.Policies.Add(new BackupPolicyInfo
        {
            Id = prodVmPolicy, Name = "EnhancedPolicy-Prod", ResourceGroup = "rg-backup-prod", SubscriptionId = SubProd, VaultId = prodId,
            Kind = BackupFlowKind.RecoveryServices, WorkloadType = "Azure VM", BackupManagementType = "AzureIaasVM", PolicyType = "V2", TimeZone = "UTC",
            ScheduleFrequency = "Hourly", ScheduleIntervalHours = 4, ScheduleSummary = "Every 4 hours in a 12h window",
            DailyRetentionDays = 30, WeeklyRetentionWeeks = 12, MonthlyRetentionMonths = 12, YearlyRetentionYears = 3, InstantRpRetentionDays = 2, ProtectedItemCount = 3
        });
        result.Policies.Add(new BackupPolicyInfo
        {
            Id = prodSqlPolicy, Name = "SqlHourlyLogs", ResourceGroup = "rg-backup-prod", SubscriptionId = SubProd, VaultId = prodId,
            Kind = BackupFlowKind.RecoveryServices, WorkloadType = "SQL in Azure VM", BackupManagementType = "AzureWorkload", PolicyType = "V1", TimeZone = "UTC",
            ScheduleFrequency = "Daily", ScheduleSummary = "Daily at 23:00 UTC", DailyRetentionDays = 35, WeeklyRetentionWeeks = 8,
            LogBackupFrequencyMinutes = 60, LogRetentionDays = 35, ProtectedItemCount = 1
        });
        result.Policies.Add(new BackupPolicyInfo
        {
            Id = prodFilesPolicy, Name = "DailyFileShares", ResourceGroup = "rg-backup-prod", SubscriptionId = SubProd, VaultId = prodId,
            Kind = BackupFlowKind.RecoveryServices, WorkloadType = "Azure Files", BackupManagementType = "AzureStorage", PolicyType = "V1", TimeZone = "UTC",
            ScheduleFrequency = "Daily", ScheduleSummary = "Daily at 00:00 UTC", DailyRetentionDays = 30, MonthlyRetentionMonths = 6, ProtectedItemCount = 1
        });

        // 2. Application vault: zone-redundant, immutability unlocked, no MUA, one stale item → B/C territory
        var appId = $"{Rg(SubProd, "rg-backup-prod")}/Microsoft.RecoveryServices/vaults/rsv-contoso-apps";
        var appPolicy = $"{appId}/backupPolicies/DefaultPolicy";
        result.Vaults.Add(new BackupVaultInfo
        {
            Id = appId, Name = "rsv-contoso-apps", ResourceGroup = "rg-backup-prod", SubscriptionId = SubProd, Location = "westeurope",
            Kind = BackupFlowKind.RecoveryServices, SkuName = "RS0",
            StorageRedundancy = "ZoneRedundant", CrossRegionRestore = false,
            SoftDeleteState = "Enabled", SoftDeleteRetentionDays = 14, ImmutabilityState = "Unlocked", MultiUserAuthorization = false,
            PublicNetworkAccess = "Enabled", AlertsForAllJobFailures = true, SecureScore = "Adequate",
            DiagnosticsEnabled = false, LogDestinations = [],
            Items =
            [
                Vm(appId, SubProd, "vm-app-01", "rg-app-prod", appPolicy, "DefaultPolicy", Now.AddHours(-7)),
                Vm(appId, SubProd, "vm-batch-01", "rg-app-prod", appPolicy, "DefaultPolicy", Now.AddDays(-3), status: "Completed"),
            ]
        });
        result.Policies.Add(new BackupPolicyInfo
        {
            Id = appPolicy, Name = "DefaultPolicy", ResourceGroup = "rg-backup-prod", SubscriptionId = SubProd, VaultId = appId,
            Kind = BackupFlowKind.RecoveryServices, WorkloadType = "Azure VM", BackupManagementType = "AzureIaasVM", PolicyType = "V1", TimeZone = "UTC",
            ScheduleFrequency = "Daily", ScheduleSummary = "Daily at 02:00 UTC", DailyRetentionDays = 30, InstantRpRetentionDays = 2, ProtectedItemCount = 2
        });

        // 3. Legacy shared vault: LRS, soft delete off, one failed backup, one protection error, weekly policy → D
        var legacyId = $"{Rg(SubShared, "rg-shared-backup")}/Microsoft.RecoveryServices/vaults/rsv-contoso-legacy";
        var legacyPolicy = $"{legacyId}/backupPolicies/WeeklyPolicy";
        var legacySqlPolicy = $"{legacyId}/backupPolicies/HourlyLogBackup";
        result.Vaults.Add(new BackupVaultInfo
        {
            Id = legacyId, Name = "rsv-contoso-legacy", ResourceGroup = "rg-shared-backup", SubscriptionId = SubShared, Location = "northeurope",
            Kind = BackupFlowKind.RecoveryServices, SkuName = "RS0",
            StorageRedundancy = "LocallyRedundant", CrossRegionRestore = false,
            SoftDeleteState = "Disabled", ImmutabilityState = "Disabled", MultiUserAuthorization = false,
            PublicNetworkAccess = "Enabled", AlertsForAllJobFailures = false, SecureScore = "Minimum",
            DiagnosticsEnabled = false, LogDestinations = [],
            Items =
            [
                Vm(legacyId, SubShared, "vm-intranet", "rg-shared-apps", legacyPolicy, "WeeklyPolicy", Now.AddDays(-2)),
                Vm(legacyId, SubShared, "vm-fileserver", "rg-shared-apps", legacyPolicy, "WeeklyPolicy", Now.AddDays(-9), status: "Failed",
                    errorCode: "UserErrorGuestAgentStatusUnavailable", errorMessage: "VM agent is unable to communicate with the Azure Backup Service."),
                Sql(legacyId, SubShared, "vm-sql-legacy", "rg-shared-apps", "ReportingDW", legacySqlPolicy, "HourlyLogBackup", Now.AddDays(-20), state: "ProtectionError", status: "Unhealthy",
                    errorCode: "UserErrorSQLNoSysadminMembership", errorMessage: "The Azure Backup plugin service account lacks sysadmin membership."),
            ]
        });
        result.Policies.Add(new BackupPolicyInfo
        {
            Id = legacyPolicy, Name = "WeeklyPolicy", ResourceGroup = "rg-shared-backup", SubscriptionId = SubShared, VaultId = legacyId,
            Kind = BackupFlowKind.RecoveryServices, WorkloadType = "Azure VM", BackupManagementType = "AzureIaasVM", PolicyType = "V1", TimeZone = "UTC",
            ScheduleFrequency = "Weekly", ScheduleSummary = "Weekly on Sunday", WeeklyRetentionWeeks = 2, InstantRpRetentionDays = 2, ProtectedItemCount = 2
        });
        result.Policies.Add(new BackupPolicyInfo
        {
            Id = legacySqlPolicy, Name = "HourlyLogBackup", ResourceGroup = "rg-shared-backup", SubscriptionId = SubShared, VaultId = legacyId,
            Kind = BackupFlowKind.RecoveryServices, WorkloadType = "SQL in Azure VM", BackupManagementType = "AzureWorkload", PolicyType = "V1", TimeZone = "UTC",
            ScheduleFrequency = "Daily", ScheduleSummary = "Daily at 22:00 UTC", DailyRetentionDays = 7, LogBackupFrequencyMinutes = 60, LogRetentionDays = 7, ProtectedItemCount = 1
        });

        // 4. An empty dev vault → F
        var devId = $"{Rg(SubShared, "rg-shared-backup")}/Microsoft.RecoveryServices/vaults/rsv-contoso-dev";
        result.Vaults.Add(new BackupVaultInfo
        {
            Id = devId, Name = "rsv-contoso-dev", ResourceGroup = "rg-shared-backup", SubscriptionId = SubShared, Location = "northeurope",
            Kind = BackupFlowKind.RecoveryServices, SkuName = "RS0",
            StorageRedundancy = "GeoRedundant", CrossRegionRestore = false,
            SoftDeleteState = "Enabled", SoftDeleteRetentionDays = 14, ImmutabilityState = "Disabled", MultiUserAuthorization = false,
            PublicNetworkAccess = "Enabled", AlertsForAllJobFailures = true, DiagnosticsEnabled = false
        });
        result.Policies.Add(new BackupPolicyInfo
        {
            Id = $"{devId}/backupPolicies/DefaultPolicy", Name = "DefaultPolicy", ResourceGroup = "rg-shared-backup", SubscriptionId = SubShared, VaultId = devId,
            Kind = BackupFlowKind.RecoveryServices, WorkloadType = "Azure VM", BackupManagementType = "AzureIaasVM", PolicyType = "V1", TimeZone = "UTC",
            ScheduleFrequency = "Daily", ScheduleSummary = "Daily at 02:00 UTC", DailyRetentionDays = 30, InstantRpRetentionDays = 2, ProtectedItemCount = 0
        });

        // Two VMs in scope that nobody backs up.
        result.UnprotectedResources.Add(new UnprotectedResourceInfo
        {
            Id = $"{Rg(SubProd, "rg-app-prod")}/Microsoft.Compute/virtualMachines/vm-jumpbox", Name = "vm-jumpbox", ResourceGroup = "rg-app-prod",
            SubscriptionId = SubProd, Location = "westeurope", PowerState = "running"
        });
        result.UnprotectedResources.Add(new UnprotectedResourceInfo
        {
            Id = $"{Rg(SubShared, "rg-shared-apps")}/Microsoft.Compute/virtualMachines/vm-dc-02", Name = "vm-dc-02", ResourceGroup = "rg-shared-apps",
            SubscriptionId = SubShared, Location = "northeurope", PowerState = "running"
        });

        return result;
    }

    private static ProtectedItemInfo Vm(string vaultId, string sub, string vmName, string rg, string policyId, string policyName, DateTimeOffset lastBackup,
        string state = "Protected", string status = "Completed", string errorCode = "", string errorMessage = "")
    {
        var container = $"IaasVMContainer;iaasvmcontainerv2;{rg};{vmName}";
        return new ProtectedItemInfo
        {
            Id = $"{vaultId}/backupFabrics/Azure/protectionContainers/{container}/protectedItems/VM;iaasvmcontainerv2;{rg};{vmName}",
            Name = $"VM;iaasvmcontainerv2;{rg};{vmName}", FriendlyName = vmName, VaultId = vaultId, SubscriptionId = sub,
            SourceResourceId = $"{Rg(sub, rg)}/Microsoft.Compute/virtualMachines/{vmName}",
            WorkloadType = "Azure VM", RawType = "Microsoft.Compute/virtualMachines", BackupManagementType = "AzureIaasVM", ContainerName = container,
            ProtectionState = state, HealthStatus = status == "Failed" ? "ActionRequired" : "Passed", LastBackupStatus = status, LastBackupTime = lastBackup,
            PolicyId = policyId, PolicyName = policyName, ErrorCode = errorCode, ErrorMessage = errorMessage
        };
    }

    private static ProtectedItemInfo Sql(string vaultId, string sub, string vmName, string rg, string db, string policyId, string policyName, DateTimeOffset lastBackup,
        string state = "Protected", string status = "Healthy", string errorCode = "", string errorMessage = "")
    {
        var container = $"VMAppContainer;Compute;{rg};{vmName}";
        return new ProtectedItemInfo
        {
            Id = $"{vaultId}/backupFabrics/Azure/protectionContainers/{container}/protectedItems/SQLDataBase;MSSQLSERVER;{db}",
            Name = $"SQLDataBase;MSSQLSERVER;{db}", FriendlyName = $"{db} ({vmName})", VaultId = vaultId, SubscriptionId = sub,
            SourceResourceId = $"{Rg(sub, rg)}/Microsoft.Compute/virtualMachines/{vmName}",
            WorkloadType = "SQL in Azure VM", RawType = "AzureVmWorkloadSQLDatabase", BackupManagementType = "AzureWorkload", ContainerName = container,
            ProtectionState = state, LastBackupStatus = status, LastBackupTime = lastBackup,
            PolicyId = policyId, PolicyName = policyName, ErrorCode = errorCode, ErrorMessage = errorMessage
        };
    }

    private static ProtectedItemInfo Files(string vaultId, string sub, string account, string rg, string share, string policyId, string policyName, DateTimeOffset lastBackup)
    {
        var container = $"StorageContainer;Storage;{rg};{account}";
        return new ProtectedItemInfo
        {
            Id = $"{vaultId}/backupFabrics/Azure/protectionContainers/{container}/protectedItems/AzureFileShare;{share}",
            Name = $"AzureFileShare;{share}", FriendlyName = share, VaultId = vaultId, SubscriptionId = sub,
            SourceResourceId = $"{Rg(sub, rg)}/Microsoft.Storage/storageAccounts/{account}",
            WorkloadType = "Azure Files", RawType = "AzureFileShareProtectedItem", BackupManagementType = "AzureStorage", ContainerName = container,
            ProtectionState = "Protected", HealthStatus = "Healthy", LastBackupStatus = "Completed", LastBackupTime = lastBackup,
            PolicyId = policyId, PolicyName = policyName
        };
    }

    // ── Backup vaults (Data Protection) ──────────────────────────────────────

    public static BackupScanResult BackupVaultEstate()
    {
        var result = new BackupScanResult { Flow = BackupFlowKind.BackupVault };

        // 1. Blob + disk vault, well configured except immutability not locked → B
        var prodId = $"{Rg(SubProd, "rg-backup-prod")}/Microsoft.DataProtection/backupVaults/bv-contoso-prod";
        var blobPolicy = $"{prodId}/backupPolicies/BlobVaultedDaily";
        var diskPolicy = $"{prodId}/backupPolicies/DiskHourly";
        result.Vaults.Add(new BackupVaultInfo
        {
            Id = prodId, Name = "bv-contoso-prod", ResourceGroup = "rg-backup-prod", SubscriptionId = SubProd, Location = "westeurope",
            Kind = BackupFlowKind.BackupVault, SkuName = "Backup vault",
            StorageRedundancy = "GeoRedundant", CrossRegionRestore = true,
            SoftDeleteState = "On", SoftDeleteRetentionDays = 14, ImmutabilityState = "Unlocked", MultiUserAuthorization = true,
            PublicNetworkAccess = "Disabled", AlertsForAllJobFailures = true,
            DiagnosticsEnabled = true, LogDestinations = ["Log Analytics: law-contoso-prod"],
            Items =
            [
                Instance(prodId, SubProd, "stcontosoprod", "Blob storage", "Microsoft.Storage/storageAccounts/blobServices", $"{Rg(SubProd, "rg-data-prod")}/Microsoft.Storage/storageAccounts/stcontosoprod", blobPolicy, "BlobVaultedDaily", Now.AddHours(-8)),
                Instance(prodId, SubProd, "disk-sql-data-01", "Managed disk", "Microsoft.Compute/disks", $"{Rg(SubProd, "rg-data-prod")}/Microsoft.Compute/disks/disk-sql-data-01", diskPolicy, "DiskHourly", Now.AddHours(-3)),
                Instance(prodId, SubProd, "disk-sql-log-01", "Managed disk", "Microsoft.Compute/disks", $"{Rg(SubProd, "rg-data-prod")}/Microsoft.Compute/disks/disk-sql-log-01", diskPolicy, "DiskHourly", Now.AddHours(-3)),
            ]
        });
        result.Policies.Add(new BackupPolicyInfo
        {
            Id = blobPolicy, Name = "BlobVaultedDaily", ResourceGroup = "rg-backup-prod", SubscriptionId = SubProd, VaultId = prodId,
            Kind = BackupFlowKind.BackupVault, WorkloadType = "Blob storage", BackupManagementType = "AzureBackupVault", PolicyType = "BackupPolicy",
            ScheduleFrequency = "Daily", ScheduleSummary = "Daily", DailyRetentionDays = 30, WeeklyRetentionWeeks = 12, ProtectedItemCount = 1
        });
        result.Policies.Add(new BackupPolicyInfo
        {
            Id = diskPolicy, Name = "DiskHourly", ResourceGroup = "rg-backup-prod", SubscriptionId = SubProd, VaultId = prodId,
            Kind = BackupFlowKind.BackupVault, WorkloadType = "Managed disk", BackupManagementType = "AzureBackupVault", PolicyType = "BackupPolicy",
            ScheduleFrequency = "Hourly", ScheduleIntervalHours = 4, ScheduleSummary = "Every 4 hours", DailyRetentionDays = 30, ProtectedItemCount = 2
        });

        // 2. PostgreSQL / AKS vault: LRS, soft delete off, one instance in error, 7-day retention → E
        var sharedId = $"{Rg(SubShared, "rg-shared-backup")}/Microsoft.DataProtection/backupVaults/bv-contoso-shared";
        var pgPolicy = $"{sharedId}/backupPolicies/PostgresWeekly";
        var aksPolicy = $"{sharedId}/backupPolicies/AksDaily";
        result.Vaults.Add(new BackupVaultInfo
        {
            Id = sharedId, Name = "bv-contoso-shared", ResourceGroup = "rg-shared-backup", SubscriptionId = SubShared, Location = "northeurope",
            Kind = BackupFlowKind.BackupVault, SkuName = "Backup vault",
            StorageRedundancy = "LocallyRedundant", CrossRegionRestore = false,
            SoftDeleteState = "Off", ImmutabilityState = "Disabled", MultiUserAuthorization = false,
            PublicNetworkAccess = "Enabled", AlertsForAllJobFailures = false,
            DiagnosticsEnabled = false, LogDestinations = [],
            Items =
            [
                Instance(sharedId, SubShared, "pg-contoso-hr", "PostgreSQL flexible server", "Microsoft.DBforPostgreSQL/flexibleServers", $"{Rg(SubShared, "rg-shared-data")}/Microsoft.DBforPostgreSQL/flexibleServers/pg-contoso-hr", pgPolicy, "PostgresWeekly", Now.AddDays(-5)),
                Instance(sharedId, SubShared, "aks-contoso-shared", "AKS cluster", "Microsoft.ContainerService/managedClusters", $"{Rg(SubShared, "rg-shared-aks")}/Microsoft.ContainerService/managedClusters/aks-contoso-shared", aksPolicy, "AksDaily", Now.AddDays(-4),
                    state: "ProtectionError", status: "Failed", errorCode: "UserErrorMissingPermissionsOnBackupVault", errorMessage: "The Backup vault's identity lacks the Reader role on the snapshot resource group."),
            ]
        });
        result.Policies.Add(new BackupPolicyInfo
        {
            Id = pgPolicy, Name = "PostgresWeekly", ResourceGroup = "rg-shared-backup", SubscriptionId = SubShared, VaultId = sharedId,
            Kind = BackupFlowKind.BackupVault, WorkloadType = "PostgreSQL flexible server", BackupManagementType = "AzureBackupVault", PolicyType = "BackupPolicy",
            ScheduleFrequency = "Weekly", ScheduleSummary = "Weekly", WeeklyRetentionWeeks = 1, ProtectedItemCount = 1
        });
        result.Policies.Add(new BackupPolicyInfo
        {
            Id = aksPolicy, Name = "AksDaily", ResourceGroup = "rg-shared-backup", SubscriptionId = SubShared, VaultId = sharedId,
            Kind = BackupFlowKind.BackupVault, WorkloadType = "AKS cluster", BackupManagementType = "AzureBackupVault", PolicyType = "BackupPolicy",
            ScheduleFrequency = "Daily", ScheduleSummary = "Daily", DailyRetentionDays = 7, ProtectedItemCount = 1
        });

        return result;
    }

    private static ProtectedItemInfo Instance(string vaultId, string sub, string name, string workload, string rawType, string sourceId, string policyId, string policyName,
        DateTimeOffset lastBackup, string state = "ProtectionConfigured", string status = "Completed", string errorCode = "", string errorMessage = "")
        => new()
        {
            Id = $"{vaultId}/backupInstances/{name}",
            Name = name, FriendlyName = name, VaultId = vaultId, SubscriptionId = sub,
            SourceResourceId = sourceId, WorkloadType = workload, RawType = rawType, BackupManagementType = "AzureBackupVault",
            ProtectionState = state, HealthStatus = state, LastBackupStatus = status, LastBackupTime = lastBackup,
            PolicyId = policyId, PolicyName = policyName, ErrorCode = errorCode, ErrorMessage = errorMessage
        };

    // ── Backup jobs ──────────────────────────────────────────────────────────

    /// <summary>Synthetic job history for a vault: daily runs per item, with the failure pattern each sample item is meant to show.</summary>
    public static BackupJobAnalysis JobAnalysis(BackupFlowKind flow, string targetId, string targetName, string timeRange)
    {
        var scan = flow == BackupFlowKind.RecoveryServices ? RecoveryServicesEstate() : BackupVaultEstate();
        var vault = scan.Vaults.FirstOrDefault(v => v.Id.Equals(targetId, StringComparison.OrdinalIgnoreCase)) ?? scan.Vaults[0];
        var jobs = Jobs(vault);
        var analysis = new BackupJobAnalysis
        {
            Flow = flow, TargetId = vault.Id, TargetName = targetName, TimeRangeKey = timeRange,
            TimeRangeLabel = BackupJobAnalysisService.LabelFor(timeRange).ToLowerInvariant()
        };
        BackupJobAnalysisService.Populate(analysis, jobs, Now);
        return analysis;
    }

    /// <summary>60 days of daily backup jobs for every item in the vault, deterministic per item name.</summary>
    public static List<BackupJobInfo> Jobs(BackupVaultInfo vault)
    {
        var jobs = new List<BackupJobInfo>();
        foreach (var item in vault.Items)
        {
            var seed = item.Display.Aggregate(17, (h, c) => h * 31 + c);
            for (var day = 60; day >= 0; day--)
            {
                var start = Now.AddDays(-day).Date.AddHours(2 + Math.Abs(seed) % 3).AddMinutes(Math.Abs(seed * day) % 50);
                if (start > Now) continue;
                var (status, code, message) = Outcome(item, day, seed);
                var minutes = 12 + Math.Abs(seed + day * 7) % 45 + (item.WorkloadType == "Azure VM" ? 20 : 0);
                if (item.Display == "vm-batch-01" && day < 7) minutes += 150; // got slower this week
                jobs.Add(new BackupJobInfo
                {
                    Id = $"{vault.Id}/backupJobs/{Guid.NewGuid()}",
                    VaultId = vault.Id, ItemId = item.Id, ItemName = item.Display, WorkloadType = item.WorkloadType,
                    Operation = "Backup", Status = status, StartTime = start, EndTime = start.AddMinutes(minutes), Duration = TimeSpan.FromMinutes(minutes),
                    ErrorCode = code, ErrorMessage = message, PolicyName = item.PolicyName
                });
            }
        }
        // One restore and one in-progress job for realism.
        if (vault.Items.Count > 0)
        {
            var first = vault.Items[0];
            jobs.Add(new BackupJobInfo { Id = $"{vault.Id}/backupJobs/{Guid.NewGuid()}", VaultId = vault.Id, ItemId = first.Id, ItemName = first.Display, WorkloadType = first.WorkloadType, Operation = "Restore", Status = "Completed", StartTime = Now.AddDays(-2), EndTime = Now.AddDays(-2).AddMinutes(25), Duration = TimeSpan.FromMinutes(25), IsUserTriggered = true });
            jobs.Add(new BackupJobInfo { Id = $"{vault.Id}/backupJobs/{Guid.NewGuid()}", VaultId = vault.Id, ItemId = first.Id, ItemName = first.Display, WorkloadType = first.WorkloadType, Operation = "Backup", Status = "InProgress", StartTime = Now.AddMinutes(-20) });
        }
        return jobs;
    }

    private static (string Status, string Code, string Message) Outcome(ProtectedItemInfo item, int daysAgo, int seed)
    {
        switch (item.Display)
        {
            case "vm-fileserver":   // persistent: failing for the last 9 days
                return daysAgo <= 9 ? ("Failed", "UserErrorGuestAgentStatusUnavailable", "VM agent is unable to communicate with the Azure Backup Service.") : ("Completed", "", "");
            case "vm-batch-01":     // transient: one failure 3 days ago, fine since
                return daysAgo == 3 ? ("Failed", "UserErrorVmNotInDesirableState", "The VM was in a transitional state when the snapshot was requested.") : ("Completed", "", "");
            case "vm-intranet":     // needs review: warnings this week
                return daysAgo <= 2 ? ("CompletedWithWarnings", "", "One or more disks were skipped because they are unsupported.") : ("Completed", "", "");
            case "vm-app-01":       // recovered: failed last period, clean this period
                return daysAgo is >= 8 and <= 11 ? ("Failed", "ExtensionSnapshotFailedNoNetworkConnectivity", "Snapshot operation failed due to no network connectivity.") : ("Completed", "", "");
            case "aks-contoso-shared":
                return daysAgo <= 4 ? ("Failed", "UserErrorMissingPermissionsOnBackupVault", "The Backup vault's identity lacks the Reader role on the snapshot resource group.") : ("Completed", "", "");
            case "pg-contoso-hr":
                return daysAgo % 7 != 2 ? ("Completed", "", "") : (daysAgo == 2 ? ("Failed", "UserErrorBackupOperationInProgress", "Another backup job is in progress for this instance.") : ("Completed", "", ""));
            default:
                if (item.IsErrored) return ("Failed", item.ErrorCode, item.ErrorMessage);
                return ("Completed", "", "");
        }
    }
}
