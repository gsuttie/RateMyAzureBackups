using RateMyAzureBackups.Models;

namespace RateMyAzureBackups.Services;

public static class BackupAssessmentService
{
    public const string BestPracticesUrl = "https://learn.microsoft.com/en-us/azure/backup/guidance-best-practices";
    public const string SupportMatrixUrl = "https://learn.microsoft.com/en-us/azure/backup/backup-support-matrix";
    public const string CostScope = "Actual pre-tax charges in the Backup meter category, across both vault products in the selected subscriptions. " +
        "Not a per-item allocation or full recovery cost: snapshots, storage transactions, native database backups, network, restore and third-party charges may be billed elsewhere. " +
        "Billing data can be delayed; unavailable is not zero. Totals are separated by currency and must not be added across flows.";
    public const string InventoryScope = "Inventory includes resources visible in Azure Resource Graph and discovered backup sources. " +
        "Vault associations cover only this flow and the selected subscriptions. A backup item may cover only part of a resource (for example one share or guest database). " +
        "No vault protection found is a review candidate, not proof of no backup: native, cross-scope and third-party protection is not assessed. " +
        "Not assessed resources need service-specific review; this scan does not certify complete tenant protection.";

    public static readonly string[] Limitations =
    [
        "RPO is the maximum acceptable data loss; RTO is the maximum acceptable recovery time. Both need a business owner, approved retention and a tested recovery runbook.",
        "A schedule is configuration, not an achieved RPO. Failed jobs, backup windows and replication lag affect recovery points. A successful backup is not proof that a restore works.",
        "RTO cannot be inferred from backup metadata. Test application-consistent restores including identities, keys, networking, dependencies, quotas and regional capacity; record timings and evidence.",
        "GRS does not alone guarantee user-initiated cross-region recovery. Validate Cross Region Restore and workload, region, encryption and tier support. ZRS protects against zone failure, not a regional outage.",
        "Operational snapshots and vaulted recovery points have different isolation, retention and restore limits. Archive retrieval can add delay and cost. Validate each workload's support matrix before changing policies.",
        "Recovery requirements entered here are user-declared and are not independently verified. They remain in this session; export a report before reload or rescan. Blank values mean not documented, not zero.",
        InventoryScope,
        CostScope
    ];

    public static string NormalizeEnvironment(string value) => value.Trim().ToLowerInvariant() switch
    {
        "prod" or "production" => "Production",
        "dev" or "development" => "Development",
        "test" or "testing" or "qa" or "uat" or "staging" => "Test",
        "critical" or "mission-critical" => "Critical",
        _ => "Unknown"
    };

    internal static bool IsConfirmedProtection(ProtectedItemInfo item) =>
        item.ProtectionState.ToLowerInvariant() is "protected" or "protectionconfigured" or "backupsavailable";

    public static bool IsBackupCandidate(string type) => type.ToLowerInvariant() is
        "microsoft.compute/virtualmachines" or "microsoft.compute/disks" or "microsoft.storage/storageaccounts"
        or "microsoft.storage/storageaccounts/fileservices/shares" or "microsoft.containerservice/managedclusters"
        or "microsoft.dbforpostgresql/servers" or "microsoft.dbforpostgresql/flexibleservers"
        or "microsoft.dbformysql/flexibleservers" or "microsoft.sql/servers/databases"
        or "microsoft.sql/managedinstances/databases" or "microsoft.documentdb/databaseaccounts"
        or "microsoft.web/sites";

    public static List<ProtectedItemInfo> ItemsFor(BackupScanResult scan, ResourceInventoryInfo resource) =>
        scan.AllItems.Where(i => resource.BackupItemIds.Contains(i.Id, StringComparer.OrdinalIgnoreCase)).ToList();

    public static List<PolicyRecommendation> Recommendations(BackupScanResult scan, IEnumerable<ResourceInventoryInfo>? resources = null) =>
        (resources ?? scan.ResourceInventory).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Select(r =>
        {
            var items = ItemsFor(scan, r);
            var workload = items.Count > 0 ? string.Join(", ", items.Select(i => i.WorkloadType).Distinct()) : r.ResourceType;
            return new PolicyRecommendation(r.Id, r.Name, r.Environment, workload, RecommendPolicy(r, workload), RecoveryGap(scan, r, items));
        }).ToList();

    public static string RecommendPolicy(ResourceInventoryInfo resource, string workload)
    {
        if (!IsBackupCandidate(resource.ResourceType) && resource.BackupItemIds.Count == 0)
            return "Preserve infrastructure as code and configuration; identify stateful dependencies and document service-specific recovery or a justified backup exclusion.";
        var baseline = resource.Environment switch
        {
            "Development" => "Daily backups, 7-14 days retention; document whether the resource can instead be rebuilt.",
            "Test" => "Daily backups, 14-30 days retention; retain representative restore-test data.",
            "Production" => "Daily or more frequent backups, at least 30 days short-term retention; propose 12 weekly and 12 monthly points where supported and required.",
            "Critical" => "Use the most frequent supported backups, at least 30 days short-term retention, and approved long-term retention; design independent regional recovery.",
            _ => "Confirm environment and criticality with the owner before selecting schedule, retention and redundancy."
        };
        var lower = workload.ToLowerInvariant();
        var guidance = new List<string>();
        if (lower.Contains("sql") || lower.Contains("hana") || lower.Contains("postgres") || lower.Contains("mysql"))
            guidance.Add("Use supported database-native/PITR or workload-aware backups; choose log cadence from the approved RPO and verify the PITR window.");
        if (lower.Contains("storage") || lower.Contains("files") || lower.Contains("blob"))
            guidance.Add("Assess shares and blob containers individually; combine supported vaulted/operational backup with soft delete and versioning as appropriate.");
        if (lower.Contains("kubernetes") || lower.Contains("managedclusters") || lower.Contains("aks"))
            guidance.Add("Protect cluster objects and supported persistent volumes; validate backup extension, storage and regional restore prerequisites.");
        if (lower.Contains("disk"))
            guidance.Add("Confirm snapshot coverage and isolation; disk snapshots alone do not prove application-consistent or regional recovery.");
        if (lower.Contains("virtualmachines") || lower.Contains("azure vm"))
            guidance.Add("Use application-consistent VM backup where supported; Enhanced policies can provide more frequent points. Protect guest databases separately when needed.");
        if (guidance.Count == 0)
            guidance.Add("Verify service-native recovery, data export and configuration recovery against the service support matrix.");
        return baseline + " " + string.Join(" ", guidance) + " Validate workload support, owner-approved RPO/RTO and compliance before applying; these are proposed baselines, not Microsoft-mandated retention periods.";
    }

    public static string RecoveryGap(BackupScanResult scan, ResourceInventoryInfo resource, List<ProtectedItemInfo>? items = null)
    {
        items ??= ItemsFor(scan, resource);
        var req = resource.Recovery;
        var gaps = new List<string>();
        if (resource.Environment == "Unknown") gaps.Add("Environment not confirmed");
        if (string.IsNullOrWhiteSpace(req.Owner)) gaps.Add("Recovery owner not documented");
        if (req.RpoHours is null) gaps.Add("RPO not documented");
        if (req.RtoHours is null) gaps.Add("RTO not documented");
        if (req.RetentionDays is null) gaps.Add("Required retention not documented");
        if (string.IsNullOrWhiteSpace(req.RestoreTestEvidence)) gaps.Add("Restore test evidence not documented");
        if (items.Count == 0) gaps.Add("No vault backup evidence in this flow; review native/other protection");
        foreach (var item in items)
        {
            if (!IsConfirmedProtection(item)) gaps.Add($"{item.Display}: protection not confirmed ({item.ProtectionState})");
            if (item.LastBackupSucceeded == false) gaps.Add($"{item.Display}: last backup failed");
            var policy = scan.Policies.FirstOrDefault(p => p.Id.Equals(item.PolicyId, StringComparison.OrdinalIgnoreCase));
            if (policy is null) { gaps.Add($"{item.Display}: policy unavailable"); continue; }
            var cadence = policy.ScheduleFrequency switch { "Daily" => 24, "Weekly" => 168, _ => (int?)null };
            if (req.RpoHours is { } rpo && cadence > rpo && policy.LogBackupFrequencyMinutes is null)
                gaps.Add($"{item.Display}: configured {policy.ScheduleFrequency.ToLowerInvariant()} cadence exceeds {rpo}h RPO");
            if (req.RpoHours is { } logRpo && policy.LogBackupFrequencyMinutes is { } logMinutes && logMinutes > logRpo * 60L)
                gaps.Add($"{item.Display}: configured log backup interval {logMinutes}min exceeds {logRpo}h RPO");
            if (req.RpoHours is { } hourlyRpo && policy.ScheduleFrequency == "Hourly"
                && policy.ScheduleIntervalHours > hourlyRpo && policy.LogBackupFrequencyMinutes is null)
                gaps.Add($"{item.Display}: configured hourly interval exceeds {hourlyRpo}h RPO");
            if (req.RetentionDays is { } retention)
            {
                if (policy.MaxRetentionDays is null) gaps.Add($"{item.Display}: configured retention unknown");
                else if (policy.MaxRetentionDays < retention) gaps.Add($"{item.Display}: maximum configured retention below {retention}d requirement");
            }
        }
        gaps.Add("Achieved RPO/RTO and retention coverage require restore validation");
        return string.Join("; ", gaps.Distinct());
    }

    public static List<string> CostOpportunities(BackupScanResult scan)
    {
        var opportunities = new List<string>();
        foreach (var vault in scan.Vaults)
        {
            var stopped = vault.Items.Count(i => i.IsStopped);
            if (stopped > 0)
                opportunities.Add($"{vault.Name}: review {stopped} stopped item(s) for retained-data charges. Confirm retirement, legal holds and owner approval before deleting recovery points.");
        }
        foreach (var policy in scan.Policies.Where(p => p.MaxRetentionDays > 90))
            opportunities.Add($"{policy.Name}: review long-term retention ({policy.RetentionSummary}) against approved requirements; evaluate archive only for supported workloads and restore-time budgets. Model retrieval and early-deletion charges before changing tiers.");
        if (scan.Policies.Any(p => p.ProtectedItemCount == 0))
            opportunities.Add("Consolidate unused/duplicate policy definitions for manageability; unused policies alone do not incur backup storage charges.");
        opportunities.Add("Right-size retention and backup frequency by approved environment requirements; do not reduce production protection simply to improve cost.");
        opportunities.Add("Review snapshot retention, protected-instance sizing, storage growth and redundant protection with Cost Analysis. Preserve required regional resilience; redundancy changes may require a new vault.");
        opportunities.Add("No savings are quantified: validate usage, prices, billing scope and recovery/compliance impact before claiming or applying savings.");
        return opportunities;
    }
}
