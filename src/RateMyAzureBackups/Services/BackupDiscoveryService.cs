using System.Text.Json;
using System.Text.RegularExpressions;
using RateMyAzureBackups.Models;
using RateMyAzureBackups.Services.Azure;
using static RateMyAzureBackups.Services.Azure.Json;

namespace RateMyAzureBackups.Services;

/// <summary>
/// Discovers the backup estate of one flow (Recovery Services vaults or Backup vaults, their protected
/// items / backup instances and policies) via Azure Resource Graph and ARM, resolves vault security and
/// redundancy settings, diagnostic settings and unprotected workloads, produces findings and rates every
/// vault A-F. Strictly read-only: nothing here ever modifies an Azure resource.
/// </summary>
public sealed partial class BackupDiscoveryService
{
    internal const string TypeRsv = "microsoft.recoveryservices/vaults";
    internal const string TypeBackupVault = "microsoft.dataprotection/backupvaults";
    internal const string TypeProtectedItem = "microsoft.recoveryservices/vaults/backupfabrics/protectioncontainers/protecteditems";
    internal const string TypeBackupInstance = "microsoft.dataprotection/backupvaults/backupinstances";
    internal const string TypeRsvPolicy = "microsoft.recoveryservices/vaults/backuppolicies";
    internal const string TypeBackupVaultPolicy = "microsoft.dataprotection/backupvaults/backuppolicies";
    internal const string TypeRsvJob = "microsoft.recoveryservices/vaults/backupjobs";
    internal const string TypeBackupVaultJob = "microsoft.dataprotection/backupvaults/backupjobs";
    internal const string TypeVm = "microsoft.compute/virtualmachines";

    private const string Projection = "| project id, name, type, location, resourceGroup, subscriptionId, sku, properties";

    private static readonly string VaultSummaryQuery =
        $"Resources | where type in~ ('{TypeRsv}','{TypeBackupVault}') | summarize cnt = count() by subscriptionId, type";

    private static readonly string ItemSummaryQuery =
        $"RecoveryServicesResources | where type in~ ('{TypeProtectedItem}','{TypeBackupInstance}') | summarize cnt = count() by subscriptionId, type";

    internal static string VaultQuery(BackupFlowKind flow) =>
        $"Resources | where type =~ '{(flow == BackupFlowKind.RecoveryServices ? TypeRsv : TypeBackupVault)}' {Projection}";

    internal static string ItemQuery(BackupFlowKind flow) =>
        $"RecoveryServicesResources | where type =~ '{(flow == BackupFlowKind.RecoveryServices ? TypeProtectedItem : TypeBackupInstance)}' {Projection}";

    internal static string PolicyQuery(BackupFlowKind flow) =>
        $"RecoveryServicesResources | where type =~ '{(flow == BackupFlowKind.RecoveryServices ? TypeRsvPolicy : TypeBackupVaultPolicy)}' {Projection}";

    /// <summary>Backup-vault instances carry no last-backup info in Resource Graph, so it is derived from the recent Backup jobs.</summary>
    private const string BackupVaultRecentJobsQuery =
        $"RecoveryServicesResources | where type =~ '{TypeBackupVaultJob}' | where todatetime(properties.startTime) > ago(30d) | project id, name, properties";

    private const string VmQuery =
        $"Resources | where type =~ '{TypeVm}' | project id, name, location, resourceGroup, subscriptionId, powerState = tostring(properties.extended.instanceView.powerState.code)";

    private readonly IAzureApi _azure;
    private readonly ILogger<BackupDiscoveryService> _logger;

    public BackupDiscoveryService(IAzureApi azure, ILogger<BackupDiscoveryService> logger)
    {
        _azure = azure;
        _logger = logger;
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>Counts of vaults and protected items per subscription, so the scope step can highlight the relevant ones.</summary>
    public async Task<Dictionary<string, BackupSubscriptionSummary>> GetSummariesAsync(
        IReadOnlyCollection<string> subscriptionIds, string? tenantId, CancellationToken ct = default)
    {
        var result = subscriptionIds.ToDictionary(
            id => id, id => new BackupSubscriptionSummary { SubscriptionId = id }, StringComparer.OrdinalIgnoreCase);
        if (subscriptionIds.Count == 0) return result;

        var vaultTask = _azure.QueryResourceGraphAsync(VaultSummaryQuery, subscriptionIds, tenantId, ct);
        var itemTask = _azure.QueryResourceGraphAsync(ItemSummaryQuery, subscriptionIds, tenantId, ct);
        await Task.WhenAll(vaultTask, itemTask);

        foreach (var row in vaultTask.Result.Concat(itemTask.Result))
        {
            if (!result.TryGetValue(Str(row, "subscriptionId"), out var summary)) continue;
            var cnt = row.TryGetProperty("cnt", out var c) && c.TryGetInt32(out var n) ? n : 0;
            switch (Str(row, "type").ToLowerInvariant())
            {
                case TypeRsv: summary.RecoveryServicesVaultCount += cnt; break;
                case TypeBackupVault: summary.BackupVaultCount += cnt; break;
                case TypeProtectedItem: summary.ProtectedItemCount += cnt; break;
                case TypeBackupInstance: summary.BackupInstanceCount += cnt; break;
            }
        }
        return result;
    }

    /// <summary>Full read-only scan of one flow across the supplied subscriptions (which must share a tenant).</summary>
    public async Task<BackupScanResult> ScanAsync(
        BackupFlowKind flow, IReadOnlyCollection<string> subscriptionIds, string? tenantId,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var result = new BackupScanResult { Flow = flow, SubscriptionIds = subscriptionIds.ToList() };
        if (subscriptionIds.Count == 0) return result;

        progress?.Report("Querying Azure Resource Graph…");
        var vaultTask = _azure.QueryResourceGraphAsync(VaultQuery(flow), subscriptionIds, tenantId, ct);
        var itemTask = _azure.QueryResourceGraphAsync(ItemQuery(flow), subscriptionIds, tenantId, ct);
        var policyTask = _azure.QueryResourceGraphAsync(PolicyQuery(flow), subscriptionIds, tenantId, ct);
        await Task.WhenAll(vaultTask, itemTask, policyTask);

        foreach (var row in vaultTask.Result)
            result.Vaults.Add(flow == BackupFlowKind.RecoveryServices ? ParseRecoveryServicesVault(row) : ParseBackupVault(row));
        foreach (var row in policyTask.Result)
            result.Policies.Add(flow == BackupFlowKind.RecoveryServices ? ParseRecoveryServicesPolicy(row) : ParseBackupVaultPolicy(row));

        var items = itemTask.Result
            .Select(row => flow == BackupFlowKind.RecoveryServices ? ParseProtectedItem(row) : ParseBackupInstance(row))
            .ToList();
        AttachItems(result, items);

        if (flow == BackupFlowKind.RecoveryServices)
        {
            progress?.Report("Reading vault storage and security configuration…");
            await FillMissingVaultConfigAsync(result, tenantId, ct);

            progress?.Report("Looking for virtual machines without backup…");
            await ApplyUnprotectedVmsAsync(result, subscriptionIds, tenantId, ct);
        }
        else
        {
            progress?.Report("Reading recent backup jobs…");
            await ApplyBackupVaultLastBackupsAsync(result, subscriptionIds, tenantId, ct);
        }

        progress?.Report("Reading diagnostic settings…");
        await ApplyDiagnosticsAsync(result, tenantId, ct);

        progress?.Report("Inventorying resources and reading backup costs…");
        await ApplyResourceInventoryAsync(result, subscriptionIds, tenantId, ct);
        await ApplyCostsAsync(result, subscriptionIds, tenantId, ct);

        progress?.Report("Rating…");
        Finalize(result);
        return result;
    }

    /// <summary>Findings + ratings. Split out so demo data and tests can reuse it on an already-populated result.</summary>
    public static void Finalize(BackupScanResult result, DateTimeOffset? now = null)
    {
        result.Findings = BackupFindingsBuilder.Build(result, now);
        BackupRatingService.RateAll(result, now);
        result.CompletedAt = DateTime.Now;
    }

    /// <summary>Puts every item on its vault; items whose vault is not in scope are reported and dropped.</summary>
    internal static void AttachItems(BackupScanResult result, IEnumerable<ProtectedItemInfo> items)
    {
        var byId = result.Vaults.ToDictionary(v => v.Id, v => v, StringComparer.OrdinalIgnoreCase);
        var orphans = 0;
        foreach (var item in items)
        {
            if (byId.TryGetValue(item.VaultId, out var vault)) vault.Items.Add(item);
            else orphans++;
        }
        if (orphans > 0)
            result.Warnings.Add($"{orphans} {result.Flow.ItemPluralLabel()} belong to vaults that were not returned by Resource Graph and were skipped.");
        foreach (var v in result.Vaults)
            v.Items.Sort((a, b) => string.Compare(a.Display, b.Display, StringComparison.OrdinalIgnoreCase));
    }

    // ── Parsing: vaults ──────────────────────────────────────────────────────

    internal static BackupVaultInfo ParseRecoveryServicesVault(JsonElement row)
    {
        var info = new BackupVaultInfo
        {
            Id = Str(row, "id"),
            Name = Str(row, "name"),
            ResourceGroup = Str(row, "resourceGroup"),
            SubscriptionId = Str(row, "subscriptionId"),
            Location = Str(row, "location"),
            Kind = BackupFlowKind.RecoveryServices,
            SkuName = FirstNonEmpty(Str(Obj(row, "sku"), "name"), Str(Obj(row, "sku"), "tier"))
        };

        var props = Obj(row, "properties");
        if (props.ValueKind != JsonValueKind.Object) return info;

        var redundancy = Obj(props, "redundancySettings");
        info.StorageRedundancy = Str(redundancy, "standardTierStorageRedundancy");
        info.CrossRegionRestore = EnabledFlag(Str(redundancy, "crossRegionRestore"));

        var security = Obj(props, "securitySettings");
        var softDelete = Obj(security, "softDeleteSettings");
        info.SoftDeleteState = Str(softDelete, "softDeleteState");
        info.SoftDeleteRetentionDays = Int(softDelete, "softDeleteRetentionPeriodInDays");
        info.ImmutabilityState = Str(Obj(security, "immutabilitySettings"), "state");
        info.MultiUserAuthorization = EnabledFlag(Str(security, "multiUserAuthorization"));

        info.PublicNetworkAccess = Str(props, "publicNetworkAccess");
        info.AlertsForAllJobFailures = EnabledFlag(Str(Obj(Obj(props, "monitoringSettings"), "azureMonitorAlertSettings"), "alertsForAllJobFailures"));
        info.SecureScore = Str(props, "secureScore");
        return info;
    }

    internal static BackupVaultInfo ParseBackupVault(JsonElement row)
    {
        var info = new BackupVaultInfo
        {
            Id = Str(row, "id"),
            Name = Str(row, "name"),
            ResourceGroup = Str(row, "resourceGroup"),
            SubscriptionId = Str(row, "subscriptionId"),
            Location = Str(row, "location"),
            Kind = BackupFlowKind.BackupVault,
            SkuName = "Backup vault"
        };

        var props = Obj(row, "properties");
        if (props.ValueKind != JsonValueKind.Object) return info;

        // storageSettings: [{ datastoreType: "VaultStore", type: "GeoRedundant" | "LocallyRedundant" | "ZoneRedundant" }]
        var vaultStore = Arr(props, "storageSettings").FirstOrDefault(s => Str(s, "datastoreType").Equals("VaultStore", StringComparison.OrdinalIgnoreCase));
        if (vaultStore.ValueKind != JsonValueKind.Object) vaultStore = Arr(props, "storageSettings").FirstOrDefault();
        info.StorageRedundancy = Str(vaultStore, "type");
        info.StorageConfigurations = Arr(props, "storageSettings")
            .Select(s => $"{Str(s, "datastoreType")}: {Str(s, "type")}").ToList();

        var features = Obj(props, "featureSettings");
        info.CrossRegionRestore = EnabledFlag(Str(Obj(features, "crossRegionRestoreSettings"), "state"));

        var security = Obj(props, "securitySettings");
        var softDelete = Obj(security, "softDeleteSettings");
        info.SoftDeleteState = Str(softDelete, "state");
        var retention = softDelete.ValueKind == JsonValueKind.Object && softDelete.TryGetProperty("retentionDurationInDays", out var r) && r.ValueKind == JsonValueKind.Number
            ? (int?)Math.Round(r.GetDouble()) : null;
        info.SoftDeleteRetentionDays = retention;
        info.ImmutabilityState = Str(Obj(security, "immutabilitySettings"), "state");
        info.MultiUserAuthorization = Bool(props, "isVaultProtectedByResourceGuard");

        info.PublicNetworkAccess = Str(props, "publicNetworkAccess");
        info.AlertsForAllJobFailures = EnabledFlag(Str(Obj(Obj(props, "monitoringSettings"), "azureMonitorAlertSettings"), "alertsForAllJobFailures"));
        info.SecureScore = Str(props, "secureScore");
        return info;
    }

    /// <summary>"Enabled"/"On"/"True" → true, "Disabled"/"Off"/"False" → false, anything else → null.</summary>
    internal static bool? EnabledFlag(string value) => value.ToLowerInvariant() switch
    {
        "enabled" or "on" or "true" => true,
        "disabled" or "off" or "false" => false,
        _ => null
    };

    // ── Parsing: items ───────────────────────────────────────────────────────

    internal static ProtectedItemInfo ParseProtectedItem(JsonElement row)
    {
        var id = Str(row, "id");
        var props = Obj(row, "properties");
        var info = new ProtectedItemInfo
        {
            Id = id,
            Name = Str(row, "name"),
            VaultId = VaultIdOf(id),
            SubscriptionId = Str(row, "subscriptionId"),
            FriendlyName = Str(props, "friendlyName"),
            SourceResourceId = FirstNonEmpty(Str(props, "sourceResourceId"), Str(Obj(props, "dataSourceInfo"), "resourceID")),
            RawType = FirstNonEmpty(Str(props, "protectedItemType"), Str(props, "workloadType")),
            BackupManagementType = Str(props, "backupManagementType"),
            ContainerName = Str(props, "containerName"),
            ProtectionState = Str(props, "protectionState"),
            HealthStatus = FirstNonEmpty(Str(props, "healthStatus"), Str(props, "protectionStatus")),
            LastBackupStatus = Str(props, "lastBackupStatus"),
            LastBackupTime = Date(Str(props, "lastBackupTime")),
            PolicyId = NullIfEmpty(FirstNonEmpty(Str(props, "policyId"), Str(Obj(props, "policyInfo"), "policyId"))),
        };
        info.PolicyName = FirstNonEmpty(Str(props, "policyName"), Str(Obj(props, "policyInfo"), "name"),
            info.PolicyId is null ? "" : LastSegment(info.PolicyId));
        info.WorkloadType = WorkloadLabel(FirstNonEmpty(Str(props, "workloadType"), Str(props, "protectedItemType")), info.BackupManagementType);

        if (Bool(props, "isScheduledForDeferredDelete") == true && !info.IsStopped)
            info.ProtectionState = "SoftDeleted";

        // SQL/SAP items: a friendly name like "SQLDataBase;MSSQLSERVER;Octopus" is not friendly at all.
        if (string.IsNullOrEmpty(info.FriendlyName) && info.Name.Contains(';'))
            info.FriendlyName = info.Name.Split(';').Last();
        if (info.Name.Contains(';') && !string.IsNullOrEmpty(info.FriendlyName) && info.BackupManagementType.Equals("AzureWorkload", StringComparison.OrdinalIgnoreCase))
        {
            var server = FirstNonEmpty(Str(props, "parentName"), Str(props, "serverName"));
            if (!string.IsNullOrEmpty(server) && !info.FriendlyName.Contains(server, StringComparison.OrdinalIgnoreCase))
                info.FriendlyName = $"{info.FriendlyName} ({server})";
        }

        var lastError = Obj(Obj(props, "extendedInfo"), "lastBackupErrorDetail");
        info.ErrorCode = Str(lastError, "code");
        info.ErrorMessage = Str(lastError, "message");
        return info;
    }

    internal static ProtectedItemInfo ParseBackupInstance(JsonElement row)
    {
        var id = Str(row, "id");
        var props = Obj(row, "properties");
        var ds = Obj(props, "dataSourceInfo");
        var info = new ProtectedItemInfo
        {
            Id = id,
            Name = Str(row, "name"),
            VaultId = VaultIdOf(id),
            SubscriptionId = Str(row, "subscriptionId"),
            FriendlyName = FirstNonEmpty(Str(props, "friendlyName"), Str(ds, "resourceName")),
            SourceResourceId = Str(ds, "resourceID"),
            RawType = Str(ds, "datasourceType"),
            BackupManagementType = "AzureBackupVault",
            ProtectionState = FirstNonEmpty(Str(props, "currentProtectionState"), Str(Obj(props, "protectionStatus"), "status")),
            PolicyId = NullIfEmpty(Str(Obj(props, "policyInfo"), "policyId")),
        };
        info.PolicyName = FirstNonEmpty(Str(Obj(props, "policyInfo"), "name"), info.PolicyId is null ? "" : LastSegment(info.PolicyId));
        info.WorkloadType = WorkloadLabel(info.RawType, info.BackupManagementType);
        info.HealthStatus = Str(Obj(props, "protectionStatus"), "status");

        var err = Obj(props, "protectionErrorDetails");
        if (err.ValueKind != JsonValueKind.Object) err = Obj(Obj(props, "protectionStatus"), "errorDetails");
        info.ErrorCode = Str(err, "code");
        info.ErrorMessage = Str(err, "message");

        // Backup vault instances carry no last-backup fields; the scan fills them from recent jobs.
        return info;
    }

    /// <summary>The vault that owns a child resource ID (item, instance, policy or job).</summary>
    internal static string VaultIdOf(string id)
    {
        foreach (var marker in new[] { "/backupfabrics/", "/backupinstances/", "/backuppolicies/", "/backupjobs/" })
        {
            var idx = id.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx > 0) return id[..idx];
        }
        return id;
    }

    /// <summary>Human label for the many ways Azure spells a workload type.</summary>
    internal static string WorkloadLabel(string rawType, string backupManagementType)
    {
        var t = rawType.ToLowerInvariant();
        var bmt = backupManagementType.ToLowerInvariant();
        if (t.Contains("classiccompute")) return "Azure VM (classic)";
        if (t is "vm" || t.Contains("virtualmachines") || t.Contains("iaasvm") || bmt == "azureiaasvm") return "Azure VM";
        if (t.Contains("fileshare") || t.Contains("fileservices")) return "Azure Files";
        if (t.Contains("sqldatabase") || t.Contains("sqldatabase")) return "SQL in Azure VM";
        if (t.Contains("saphana")) return "SAP HANA in Azure VM";
        if (t.Contains("sapase")) return "SAP ASE in Azure VM";
        if (t.Contains("blobservices") || (t.Contains("storageaccounts") && bmt == "azurebackupvault")) return "Blob storage";
        if (t.Contains("/disks")) return "Managed disk";
        if (t.Contains("postgresql") && t.Contains("flexible")) return "PostgreSQL flexible server";
        if (t.Contains("postgresql")) return "PostgreSQL";
        if (t.Contains("mysql")) return "MySQL flexible server";
        if (t.Contains("managedclusters")) return "AKS cluster";
        if (bmt == "mab") return "MARS agent (files and folders)";
        if (bmt == "dpm") return "DPM";
        if (bmt == "azurebackupserver") return "Azure Backup Server";
        return string.IsNullOrEmpty(rawType) ? (string.IsNullOrEmpty(backupManagementType) ? "Unknown" : backupManagementType) : rawType;
    }

    internal static DateTimeOffset? Date(string value) =>
        !string.IsNullOrEmpty(value) && DateTimeOffset.TryParse(value, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var d) && d.Year > 1
            ? d : null;

    // ── Parsing: policies ────────────────────────────────────────────────────

    internal static BackupPolicyInfo ParseRecoveryServicesPolicy(JsonElement row)
    {
        var id = Str(row, "id");
        var props = Obj(row, "properties");
        var info = new BackupPolicyInfo
        {
            Id = id,
            Name = Str(row, "name"),
            ResourceGroup = Str(row, "resourceGroup"),
            SubscriptionId = Str(row, "subscriptionId"),
            VaultId = VaultIdOf(id),
            Kind = BackupFlowKind.RecoveryServices,
            BackupManagementType = Str(props, "backupManagementType"),
            PolicyType = Str(props, "policyType"),
            TimeZone = Str(props, "timeZone"),
            InstantRpRetentionDays = Int(props, "instantRpRetentionRangeInDays"),
            ProtectedItemCount = Int(props, "protectedItemsCount") ?? 0,
        };
        info.WorkloadType = WorkloadLabel(Str(props, "workLoadType"), info.BackupManagementType);

        var schedule = Obj(props, "schedulePolicy");
        var retention = Obj(props, "retentionPolicy");

        // SQL / SAP HANA policies nest the real schedule in subProtectionPolicy (Full + Differential + Log).
        foreach (var sub in Arr(props, "subProtectionPolicy"))
        {
            var policyType = Str(sub, "policyType");
            if (policyType.Equals("Full", StringComparison.OrdinalIgnoreCase))
            {
                schedule = Obj(sub, "schedulePolicy");
                retention = Obj(sub, "retentionPolicy");
            }
            else if (policyType.Equals("Log", StringComparison.OrdinalIgnoreCase))
            {
                info.LogBackupFrequencyMinutes = Int(Obj(sub, "schedulePolicy"), "scheduleFrequencyInMins");
                var logRet = Obj(Obj(sub, "retentionPolicy"), "retentionDuration");
                info.LogRetentionDays = DurationDays(Int(logRet, "count"), Str(logRet, "durationType"));
            }
        }

        ApplyRsvSchedule(info, schedule);
        ApplyRsvRetention(info, retention);
        return info;
    }

    private static void ApplyRsvSchedule(BackupPolicyInfo info, JsonElement schedule)
    {
        if (schedule.ValueKind != JsonValueKind.Object) return;
        var frequency = Str(schedule, "scheduleRunFrequency");
        var hourly = Obj(schedule, "hourlySchedule");
        if (frequency.Equals("Hourly", StringComparison.OrdinalIgnoreCase) || hourly.ValueKind == JsonValueKind.Object)
        {
            info.ScheduleFrequency = "Hourly";
            info.ScheduleIntervalHours = Int(hourly, "interval");
            var window = Int(hourly, "scheduleWindowDuration");
            info.ScheduleSummary = $"Every {info.ScheduleIntervalHours ?? 4} hours{(window is { } w ? $" in a {w}h window" : "")}";
        }
        else if (frequency.Equals("Weekly", StringComparison.OrdinalIgnoreCase))
        {
            info.ScheduleFrequency = "Weekly";
            var days = Arr(schedule, "scheduleRunDays").Select(d => d.GetString() ?? "").Where(d => d.Length > 0).ToList();
            info.ScheduleSummary = days.Count > 0 ? $"Weekly on {string.Join(", ", days)}" : "Weekly";
        }
        else if (frequency.Equals("Daily", StringComparison.OrdinalIgnoreCase) || Arr(schedule, "scheduleRunTimes").Any())
        {
            info.ScheduleFrequency = "Daily";
            var time = Arr(schedule, "scheduleRunTimes").Select(t => Date(t.GetString() ?? "")).FirstOrDefault(t => t is not null);
            info.ScheduleSummary = time is { } at ? $"Daily at {at:HH:mm} {info.TimeZone}".Trim() : "Daily";
        }
        else if (!string.IsNullOrEmpty(frequency))
        {
            info.ScheduleFrequency = frequency;
            info.ScheduleSummary = frequency;
        }
    }

    private static void ApplyRsvRetention(BackupPolicyInfo info, JsonElement retention)
    {
        if (retention.ValueKind != JsonValueKind.Object) return;
        if (Str(retention, "retentionPolicyType").Equals("SimpleRetentionPolicy", StringComparison.OrdinalIgnoreCase))
        {
            var simple = Obj(retention, "retentionDuration");
            info.DailyRetentionDays = DurationDays(Int(simple, "count"), Str(simple, "durationType"));
            return;
        }
        info.DailyRetentionDays = Int(Obj(Obj(retention, "dailySchedule"), "retentionDuration"), "count");
        info.WeeklyRetentionWeeks = Int(Obj(Obj(retention, "weeklySchedule"), "retentionDuration"), "count");
        info.MonthlyRetentionMonths = Int(Obj(Obj(retention, "monthlySchedule"), "retentionDuration"), "count");
        info.YearlyRetentionYears = Int(Obj(Obj(retention, "yearlySchedule"), "retentionDuration"), "count");
    }

    internal static int? DurationDays(int? count, string durationType) => count is null ? null : durationType.ToLowerInvariant() switch
    {
        "weeks" => count * 7,
        "months" => count * 30,
        "years" => count * 365,
        _ => count
    };

    internal static BackupPolicyInfo ParseBackupVaultPolicy(JsonElement row)
    {
        var id = Str(row, "id");
        var props = Obj(row, "properties");
        var info = new BackupPolicyInfo
        {
            Id = id,
            Name = Str(row, "name"),
            ResourceGroup = Str(row, "resourceGroup"),
            SubscriptionId = Str(row, "subscriptionId"),
            VaultId = VaultIdOf(id),
            Kind = BackupFlowKind.BackupVault,
            BackupManagementType = "AzureBackupVault",
            PolicyType = Str(props, "objectType"),
        };
        var datasource = Arr(props, "datasourceTypes").Select(d => d.GetString() ?? "").FirstOrDefault(d => d.Length > 0) ?? "";
        info.WorkloadType = WorkloadLabel(datasource, info.BackupManagementType);

        foreach (var rule in Arr(props, "policyRules"))
        {
            var objectType = Str(rule, "objectType");
            if (objectType.Equals("AzureBackupRule", StringComparison.OrdinalIgnoreCase))
            {
                var intervals = Arr(Obj(Obj(rule, "trigger"), "schedule"), "repeatingTimeIntervals").Select(i => i.GetString() ?? "").ToList();
                var period = intervals.Select(i => i.Split('/').Last()).FirstOrDefault(p => p.StartsWith('P'));
                if (period is null && Str(Obj(rule, "trigger"), "objectType").Contains("Adhoc", StringComparison.OrdinalIgnoreCase)) continue;
                if (period is null)
                {
                    // Operational-tier (blob) policies have no schedule: protection is continuous.
                    if (string.IsNullOrEmpty(info.ScheduleFrequency)) { info.ScheduleFrequency = "Continuous"; info.ScheduleSummary = "Continuous (operational tier)"; }
                    continue;
                }
                var hours = IsoDurationHours(period);
                if (hours is { } h && h < 24) { info.ScheduleFrequency = "Hourly"; info.ScheduleIntervalHours = (int)h; info.ScheduleSummary = $"Every {(int)h} hours"; }
                else if (hours is { } d && d <= 24) { info.ScheduleFrequency = "Daily"; info.ScheduleSummary = "Daily"; }
                else if (hours is { } w && w <= 24 * 7) { info.ScheduleFrequency = "Weekly"; info.ScheduleSummary = "Weekly"; }
                else { info.ScheduleFrequency = "Monthly"; info.ScheduleSummary = period; }
            }
            else if (objectType.Equals("AzureRetentionRule", StringComparison.OrdinalIgnoreCase))
            {
                var name = Str(rule, "name");
                var days = Arr(rule, "lifecycles")
                    .Select(l => IsoDurationHours(Str(Obj(l, "deleteAfter"), "duration")))
                    .Where(h => h is not null).Select(h => (int)Math.Round(h!.Value / 24)).DefaultIfEmpty(0).Max();
                if (days == 0) continue;
                if (Bool(rule, "isDefault") == true || name.Equals("Default", StringComparison.OrdinalIgnoreCase) || name.Contains("Daily", StringComparison.OrdinalIgnoreCase))
                    info.DailyRetentionDays = Math.Max(info.DailyRetentionDays ?? 0, days);
                else if (name.Contains("Week", StringComparison.OrdinalIgnoreCase)) info.WeeklyRetentionWeeks = Math.Max(info.WeeklyRetentionWeeks ?? 0, days / 7);
                else if (name.Contains("Month", StringComparison.OrdinalIgnoreCase)) info.MonthlyRetentionMonths = Math.Max(info.MonthlyRetentionMonths ?? 0, days / 30);
                else if (name.Contains("Year", StringComparison.OrdinalIgnoreCase)) info.YearlyRetentionYears = Math.Max(info.YearlyRetentionYears ?? 0, days / 365);
                else info.DailyRetentionDays = Math.Max(info.DailyRetentionDays ?? 0, days);
            }
        }
        if (string.IsNullOrEmpty(info.ScheduleFrequency) && info.WorkloadType == "Blob storage")
        {
            info.ScheduleFrequency = "Continuous";
            info.ScheduleSummary = "Continuous (operational tier)";
        }
        return info;
    }

    private static readonly Regex IsoDuration = new(@"^P(?:(\d+)Y)?(?:(\d+)M)?(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?$", RegexOptions.Compiled);

    /// <summary>Length of an ISO 8601 duration such as P30D, P12W, P1Y or PT4H, in hours (months = 30 days).</summary>
    internal static double? IsoDurationHours(string duration)
    {
        if (string.IsNullOrEmpty(duration)) return null;
        var m = IsoDuration.Match(duration.Trim());
        if (!m.Success) return null;
        double hours = 0;
        if (m.Groups[1].Success) hours += int.Parse(m.Groups[1].Value) * 365 * 24;
        if (m.Groups[2].Success) hours += int.Parse(m.Groups[2].Value) * 30 * 24;
        if (m.Groups[3].Success) hours += int.Parse(m.Groups[3].Value) * 7 * 24;
        if (m.Groups[4].Success) hours += int.Parse(m.Groups[4].Value) * 24;
        if (m.Groups[5].Success) hours += int.Parse(m.Groups[5].Value);
        if (m.Groups[6].Success) hours += int.Parse(m.Groups[6].Value) / 60.0;
        if (m.Groups[7].Success) hours += int.Parse(m.Groups[7].Value) / 3600.0;
        return hours;
    }

    // ── Vault configuration fallbacks (older Recovery Services vaults) ───────

    private async Task FillMissingVaultConfigAsync(BackupScanResult result, string? tenantId, CancellationToken ct)
    {
        var tasks = result.Vaults
            .Where(v => string.IsNullOrEmpty(v.StorageRedundancy) || string.IsNullOrEmpty(v.SoftDeleteState))
            .Select(v => FillVaultConfigAsync(v, tenantId, ct))
            .ToList();
        if (tasks.Count == 0) return;
        var outcomes = await Task.WhenAll(tasks);
        var failures = outcomes.Count(ok => !ok);
        if (failures > 0)
            result.Warnings.Add($"Storage or security configuration could not be read for {failures} vault(s) — those settings are shown as unknown.");
    }

    private async Task<bool> FillVaultConfigAsync(BackupVaultInfo v, string? tenantId, CancellationToken ct)
    {
        var ok = true;
        if (string.IsNullOrEmpty(v.StorageRedundancy))
        {
            try
            {
                var storage = await _azure.GetArmAsync($"{AzureApi.ArmBase}{v.Id}/backupstorageconfig/vaultstorageconfig?api-version=2023-04-01", tenantId, ct);
                ApplyStorageConfig(v, storage);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read storage config for {VaultId}.", v.Id);
                ok = false;
            }
        }
        if (string.IsNullOrEmpty(v.SoftDeleteState))
        {
            try
            {
                var config = await _azure.GetArmAsync($"{AzureApi.ArmBase}{v.Id}/backupconfig/vaultconfig?api-version=2023-04-01", tenantId, ct);
                ApplyVaultConfig(v, config);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read vault config for {VaultId}.", v.Id);
                ok = false;
            }
        }
        return ok;
    }

    internal static void ApplyStorageConfig(BackupVaultInfo v, JsonElement storage)
    {
        var props = Obj(storage, "properties");
        if (string.IsNullOrEmpty(v.StorageRedundancy)) v.StorageRedundancy = FirstNonEmpty(Str(props, "storageType"), Str(props, "storageModelType"));
        v.CrossRegionRestore ??= Bool(props, "crossRegionRestoreFlag");
    }

    internal static void ApplyVaultConfig(BackupVaultInfo v, JsonElement config)
    {
        var props = Obj(config, "properties");
        if (string.IsNullOrEmpty(v.SoftDeleteState)) v.SoftDeleteState = Str(props, "softDeleteFeatureState");
        v.SoftDeleteRetentionDays ??= Int(props, "softDeleteRetentionPeriodInDays");
    }

    // ── Backup vault: last backup from recent jobs ───────────────────────────

    private async Task ApplyBackupVaultLastBackupsAsync(BackupScanResult result, IReadOnlyCollection<string> subscriptionIds, string? tenantId, CancellationToken ct)
    {
        if (result.ItemCount == 0) return;
        try
        {
            var rows = await _azure.QueryResourceGraphAsync(BackupVaultRecentJobsQuery, subscriptionIds, tenantId, ct);
            ApplyLastBackups(result, rows.Select(BackupJobAnalysisService.ParseJob));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read recent Backup vault jobs.");
            result.Warnings.Add("Recent backup jobs could not be read — the last-backup status of backup instances is unknown.");
        }
    }

    /// <summary>Sets each item's last backup status/time from the latest finished Backup job for it.</summary>
    internal static void ApplyLastBackups(BackupScanResult result, IEnumerable<BackupJobInfo> jobs)
    {
        var latest = jobs
            .Where(j => j.Operation.Contains("Backup", StringComparison.OrdinalIgnoreCase) && !j.IsInProgress && !string.IsNullOrEmpty(j.ItemId))
            .GroupBy(j => j.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(j => j.StartTime).First(), StringComparer.OrdinalIgnoreCase);

        foreach (var item in result.AllItems)
        {
            if (!latest.TryGetValue(item.Id, out var job)) continue;
            if (string.IsNullOrEmpty(item.LastBackupStatus)) item.LastBackupStatus = job.Status;
            item.LastBackupTime ??= job.EndTime ?? job.StartTime;
            if (job.IsFailed && string.IsNullOrEmpty(item.ErrorCode)) { item.ErrorCode = job.ErrorCode; item.ErrorMessage = job.ErrorMessage; }
        }
    }

    // ── Unprotected virtual machines ─────────────────────────────────────────

    private async Task ApplyUnprotectedVmsAsync(BackupScanResult result, IReadOnlyCollection<string> subscriptionIds, string? tenantId, CancellationToken ct)
    {
        try
        {
            var rows = await _azure.QueryResourceGraphAsync(VmQuery, subscriptionIds, tenantId, ct);
            ApplyUnprotectedVms(result, rows);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list virtual machines.");
            result.Warnings.Add("Virtual machines could not be listed — unprotected VMs cannot be identified.");
        }
    }

    /// <summary>Name fragments that mark a resource or resource group as out of scope (e.g. restore-test artifacts).</summary>
    internal static readonly string[] IgnoredNameFragments = ["restore"];

    internal static bool IsIgnoredByName(string? name) =>
        !string.IsNullOrEmpty(name) && IgnoredNameFragments.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase));

    internal static void ApplyUnprotectedVms(BackupScanResult result, IEnumerable<JsonElement> vmRows)
    {
        var protectedIds = result.AllItems
            .Where(i => i.WorkloadType.StartsWith("Azure VM", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(i.SourceResourceId))
            .Select(i => i.SourceResourceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var total = 0;
        foreach (var row in vmRows)
        {
            var id = Str(row, "id");
            var name = Str(row, "name");
            var resourceGroup = Str(row, "resourceGroup");
            if (IsIgnoredByName(name) || IsIgnoredByName(resourceGroup)) continue;
            total++;
            if (protectedIds.Contains(id)) continue;
            result.UnprotectedResources.Add(new UnprotectedResourceInfo
            {
                Id = id,
                Name = name,
                ResourceGroup = resourceGroup,
                SubscriptionId = Str(row, "subscriptionId"),
                Location = Str(row, "location"),
                ResourceType = "Virtual machine",
                PowerState = Str(row, "powerState").Replace("PowerState/", "")
            });
        }
        result.VirtualMachineCount = total;
        result.UnprotectedResources.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
    }

    // ── Diagnostic settings (backup logging) ─────────────────────────────────

    private async Task ApplyDiagnosticsAsync(BackupScanResult result, string? tenantId, CancellationToken ct)
    {
        var tasks = result.Vaults.Select(v => ReadDiagnosticsAsync(v, tenantId, ct)).ToList();
        if (tasks.Count == 0) return;

        var outcomes = await Task.WhenAll(tasks);
        var failures = outcomes.Count(ok => !ok);
        if (failures > 0)
            result.Warnings.Add($"Diagnostic settings could not be read for {failures} vault(s) — their logging status is shown as Unknown.");
    }

    private async Task<bool> ReadDiagnosticsAsync(BackupVaultInfo v, string? tenantId, CancellationToken ct)
    {
        try
        {
            var settings = await _azure.GetArmListAsync(
                $"{AzureApi.ArmBase}{v.Id}/providers/Microsoft.Insights/diagnosticSettings?api-version=2021-05-01-preview", tenantId, ct);
            var d = ParseDiagnosticSettings(settings);
            v.DiagnosticsEnabled = d.Enabled;
            v.LogDestinations = d.Destinations;
            v.LogWorkspaceIds = d.WorkspaceIds;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read diagnostic settings for {ResourceId}.", v.Id);
            v.DiagnosticsEnabled = null;
            return false;
        }
    }

    /// <summary>What the diagnostic settings say about a vault's backup logs.</summary>
    internal sealed record DiagnosticsInfo(bool Enabled, List<string> Destinations, List<string> WorkspaceIds);

    internal static DiagnosticsInfo ParseDiagnosticSettings(IEnumerable<JsonElement> settings)
    {
        var enabled = false;
        var destinations = new List<string>();
        var workspaceIds = new List<string>();

        foreach (var setting in settings)
        {
            var props = Obj(setting, "properties");
            if (props.ValueKind != JsonValueKind.Object) continue;

            var backupLogOn = false;
            foreach (var log in Arr(props, "logs"))
            {
                if (Bool(log, "enabled") != true) continue;
                var category = Str(log, "category");
                var group = Str(log, "categoryGroup");
                // CoreAzureBackup, AddonAzureBackupJobs, AddonAzureBackupPolicy, AzureBackupReport...; "allLogs" includes them all.
                if (category.Contains("Backup", StringComparison.OrdinalIgnoreCase) ||
                    group.Equals("allLogs", StringComparison.OrdinalIgnoreCase))
                {
                    backupLogOn = true;
                    break;
                }
            }
            if (!backupLogOn) continue;

            enabled = true;
            var workspaceId = Str(props, "workspaceId");
            if (!string.IsNullOrEmpty(workspaceId) && !workspaceIds.Contains(workspaceId, StringComparer.OrdinalIgnoreCase))
                workspaceIds.Add(workspaceId);
            AddDestination(destinations, "Log Analytics", workspaceId);
            AddDestination(destinations, "Storage", Str(props, "storageAccountId"));
            AddDestination(destinations, "Event Hub", FirstNonEmpty(Str(props, "eventHubName"), Str(props, "eventHubAuthorizationRuleId")));
            AddDestination(destinations, "Partner solution", Str(props, "marketplacePartnerId"));
        }
        return new DiagnosticsInfo(enabled, destinations.Distinct().ToList(), workspaceIds);

        static void AddDestination(List<string> list, string label, string idOrName)
        {
            if (string.IsNullOrEmpty(idOrName)) return;
            list.Add($"{label}: {LastSegment(idOrName)}");
        }
    }
}
