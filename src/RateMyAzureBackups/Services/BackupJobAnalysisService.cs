using System.Text.Json;
using RateMyAzureBackups.Models;
using RateMyAzureBackups.Services.Azure;
using static RateMyAzureBackups.Services.Azure.Json;

namespace RateMyAzureBackups.Services;

/// <summary>
/// Reads the backup jobs of one vault from Azure Resource Graph, compares the window against the
/// preceding one, classifies every item with failures as persistent, transient or needing review and
/// produces report-only recommendations.
/// </summary>
public sealed class BackupJobAnalysisService
{
    /// <summary>Supported analysis windows: key (valid KQL timespan) to display label. Resource Graph keeps roughly a month of jobs.</summary>
    public static readonly IReadOnlyList<(string Key, string Label)> TimeRanges =
    [
        ("24h", "Last 24 hours"),
        ("3d", "Last 3 days"),
        ("7d", "Last 7 days"),
        ("14d", "Last 14 days"),
        ("30d", "Last 30 days"),
    ];

    private static readonly Dictionary<string, string> DoubledTimeRanges = new()
    {
        ["24h"] = "48h", ["3d"] = "6d", ["7d"] = "14d", ["14d"] = "28d", ["30d"] = "60d"
    };

    internal static TimeSpan Span(string key) => key switch
    {
        "24h" => TimeSpan.FromHours(24), "3d" => TimeSpan.FromDays(3), "7d" => TimeSpan.FromDays(7),
        "14d" => TimeSpan.FromDays(14), _ => TimeSpan.FromDays(30)
    };

    /// <summary>Well-known Azure Backup error codes and what to do about them.</summary>
    internal static readonly IReadOnlyDictionary<string, (string Summary, string Recommendation)> KnownErrors =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["UserErrorGuestAgentStatusUnavailable"] = ("the Azure VM agent is not responding", "Make sure the Azure VM agent is installed, up to date and running inside the VM, then run an on-demand backup. Stopped VMs are backed up crash-consistent without the agent."),
            ["UserErrorVmNotInDesirableState"] = ("the VM was in a transitional state", "Retry once the VM reaches a stable (running or stopped) state; if it recurs, look for stuck operations in the VM's Activity log."),
            ["UserErrorRpCollectionLimitReached"] = ("the restore point collection has reached its limit", "Delete old restore points from the AzureBackupRG resource group (or raise instant restore retention) and retry."),
            ["UserErrorVmProvisioningStateFailed"] = ("the VM provisioning state is Failed", "Fix the VM so its provisioning state is Succeeded (update or redeploy it), then retry the backup."),
            ["ExtensionSnapshotFailedNoNetworkConnectivity"] = ("the backup extension could not reach Azure storage", "Allow outbound HTTPS to the AzureBackup and Storage service tags through the NSG, firewall or proxy, and check DNS from inside the VM."),
            ["ExtensionSnapshotFailedCOM"] = ("the snapshot extension hit a COM+ error inside the VM", "Restart the COM+ System Application and Volume Shadow Copy services in the guest, then retry."),
            ["ExtensionFailedTimeoutVMNetworkUnresponsive"] = ("the VM did not respond over the network", "Check the VM's network and agent health; make sure the VMSnapshot extension is not blocked by a proxy."),
            ["ExtensionFailedVssWriterInBadState"] = ("a VSS writer inside the VM is in a bad state", "Restart the failing VSS writer's service (vssadmin list writers) or the VM, or switch the policy to crash-consistent snapshots."),
            ["ExtensionFailedSnapshotLimitReachedError"] = ("a disk has reached its snapshot limit", "Delete unneeded disk snapshots so new ones can be taken."),
            ["ExtensionOperationFailedForManagedDisks"] = ("the snapshot of a managed disk failed", "Retry; if it persists, check for disk locks or an Azure Policy blocking snapshot creation."),
            ["UserErrorBackupOperationInProgress"] = ("another backup was still running", "Wait for the running job to finish; if jobs regularly overlap, lower the schedule frequency or investigate why they take so long."),
            ["UserErrorBcmDatasourceNotPresent"] = ("the source resource no longer exists", "Stop protection (retain or delete the backup data) so the item stops generating failed jobs."),
            ["UserErrorVmNotFoundV2"] = ("the virtual machine no longer exists", "Stop protection (retain or delete the backup data) for the deleted VM."),
            ["UserErrorFileShareNotFound"] = ("the file share no longer exists", "Stop protection for the deleted share, or recreate it if the deletion was a mistake."),
            ["UserErrorStorageAccountNotFound"] = ("the storage account no longer exists", "Unregister the storage account from the vault or restore it if deleted accidentally."),
            ["UserErrorKeyvaultPermissionsNotConfigured"] = ("Azure Backup cannot read the disk encryption keys", "Grant the Backup Management Service the required Key Vault permissions (Key Vault Crypto Service Encryption User / Secrets User) on the vault holding the keys."),
            ["UserErrorUnsupportedDiskSize"] = ("a disk is larger than the policy supports", "Use an Enhanced policy (which supports 32 TB disks) or exclude the disk from backup."),
            ["UserErrorSQLNoSysadminMembership"] = ("the backup service account lacks sysadmin on SQL Server", "Add NT SERVICE\\AzureWLBackupPluginSvc to the sysadmin role on the instance and re-register the VM."),
            ["UserErrorSQLLogBackupFailed"] = ("the SQL log backup failed", "Make sure the database is in the FULL recovery model and no other tool is taking log backups."),
            ["UserErrorVMInternetConnectivityIssue"] = ("the VM has no outbound connectivity to Azure Backup", "Allow outbound access to the AzureBackup, Storage and AzureActiveDirectory service tags, or use private endpoints."),
            ["UserErrorRequestDisallowedByPolicy"] = ("an Azure Policy blocked the snapshot resource group", "Add an exemption for the AzureBackupRG_* resource groups / restore point collections to the denying policy."),
            ["UserErrorCrpReportedUserError"] = ("the Compute service rejected the snapshot request", "Open the job details for the underlying CRP error; it is usually a policy denial, lock or quota on the snapshot resource group."),
            ["UserErrorSnapshotRGLocked"] = ("the snapshot resource group is locked", "Remove the lock from the AzureBackupRG_* resource group or exclude it from the lock policy."),
            ["UserErrorInsufficientPermissions"] = ("the vault identity lacks permissions on the source", "Grant the vault's managed identity the required role on the source resource (for example Disk Snapshot Contributor or Storage Account Backup Contributor)."),
            ["UserErrorMissingPermissionsOnBackupVault"] = ("the vault identity lacks permissions", "Assign the roles listed in the job error to the Backup vault's system-assigned identity."),
            ["CloudInternalError"] = ("an Azure-side internal error", "Retry the backup; open a support request if the same error repeats over several days."),
        };

    private readonly IAzureApi _azure;
    private readonly ILogger<BackupJobAnalysisService> _logger;

    public BackupJobAnalysisService(IAzureApi azure, ILogger<BackupJobAnalysisService> logger)
    {
        _azure = azure;
        _logger = logger;
    }

    public static string LabelFor(string timeRange) =>
        TimeRanges.FirstOrDefault(t => t.Key == timeRange).Label ?? timeRange;

    public async Task<BackupJobAnalysis> AnalyzeAsync(
        BackupFlowKind flow, string vaultId, string vaultName, string subscriptionId, string timeRange, string? tenantId,
        CancellationToken ct = default)
    {
        if (!DoubledTimeRanges.ContainsKey(timeRange))
            throw new ArgumentException($"Unsupported time range '{timeRange}'.");

        var analysis = new BackupJobAnalysis
        {
            Flow = flow,
            TargetId = vaultId,
            TargetName = vaultName,
            TimeRangeKey = timeRange,
            TimeRangeLabel = LabelFor(timeRange).ToLowerInvariant()
        };

        List<BackupJobInfo> jobs;
        try
        {
            var rows = await _azure.QueryResourceGraphAsync(BuildQuery(flow, vaultId, timeRange), [subscriptionId], tenantId, ct);
            jobs = rows.Select(ParseJob).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Backup job query failed for {Target}.", vaultName);
            analysis.JobsQueryable = false;
            analysis.QueryError = ex.Message;
            return analysis;
        }

        Populate(analysis, jobs, DateTimeOffset.UtcNow);
        return analysis;
    }

    /// <summary>Jobs of one vault over twice the window (current + previous period), newest first.</summary>
    internal static string BuildQuery(BackupFlowKind flow, string vaultId, string timeRange)
    {
        var type = flow == BackupFlowKind.RecoveryServices ? BackupDiscoveryService.TypeRsvJob : BackupDiscoveryService.TypeBackupVaultJob;
        var prefix = vaultId.Replace("'", "''").ToLowerInvariant() + "/";
        return $"RecoveryServicesResources | where type =~ '{type}' | where tolower(id) startswith '{prefix}' " +
               $"| where todatetime(properties.startTime) > ago({DoubledTimeRanges[timeRange]}) | project id, name, properties";
    }

    internal static BackupJobInfo ParseJob(JsonElement row)
    {
        var id = Str(row, "id");
        var props = Obj(row, "properties");
        var job = new BackupJobInfo
        {
            Id = id,
            VaultId = BackupDiscoveryService.VaultIdOf(id),
            ItemId = Str(props, "backupInstanceId"),
            ItemName = FirstNonEmpty(Str(props, "entityFriendlyName"), Str(props, "backupInstanceFriendlyName"), Str(props, "dataSourceName"), LastSegment(Str(props, "dataSourceId"))),
            Operation = FirstNonEmpty(Str(props, "operation"), Str(props, "operationCategory")),
            Status = Str(props, "status"),
            StartTime = BackupDiscoveryService.Date(Str(props, "startTime")),
            EndTime = BackupDiscoveryService.Date(Str(props, "endTime")),
            IsUserTriggered = Bool(props, "isUserTriggered") == true,
            PolicyName = Str(props, "policyName"),
        };
        job.WorkloadType = BackupDiscoveryService.WorkloadLabel(FirstNonEmpty(Str(props, "dataSourceType"), Str(props, "workloadType")), Str(props, "backupManagementType"));
        if (TimeSpan.TryParse(Str(props, "duration"), out var duration)) job.Duration = duration;
        else if (job.StartTime is { } s && job.EndTime is { } e && e > s) job.Duration = e - s;

        // Recovery Services: errorDetails[{errorCode, errorString, recommendations[]}]; Backup vault: errorDetails[{code, message, recommendation}].
        var err = Arr(props, "errorDetails").FirstOrDefault();
        if (err.ValueKind == JsonValueKind.Object)
        {
            job.ErrorCode = FirstNonEmpty(Str(err, "errorCode"), Str(err, "code"));
            job.ErrorMessage = FirstNonEmpty(Str(err, "errorString"), Str(err, "message"));
            job.ErrorRecommendation = FirstNonEmpty(Str(err, "recommendation"),
                Arr(err, "recommendations").Select(r => r.GetString() ?? "").FirstOrDefault(r => r.Length > 0) ?? "");
        }
        if (string.IsNullOrEmpty(job.ItemId)) job.ItemId = job.ItemName;
        return job;
    }

    /// <summary>Pure part of the analysis (testable without Azure): splits the jobs into current/previous window and classifies.</summary>
    internal static void Populate(BackupJobAnalysis analysis, IReadOnlyCollection<BackupJobInfo> allJobs, DateTimeOffset now)
    {
        var window = Span(analysis.TimeRangeKey);
        var since = now - window;
        var current = allJobs.Where(j => j.StartTime is { } s && s >= since).OrderBy(j => j.StartTime).ToList();
        var previous = allJobs.Where(j => j.StartTime is { } s && s < since && s >= since - window).OrderBy(j => j.StartTime).ToList();

        analysis.TotalJobs = current.Count;
        analysis.CompletedCount = current.Count(j => j.IsCompleted);
        analysis.FailedCount = current.Count(j => j.IsFailed);
        analysis.WarningCount = current.Count(j => j.IsWarning);
        analysis.InProgressCount = current.Count(j => j.IsInProgress);
        analysis.CancelledCount = current.Count(j => j.IsCancelled);
        analysis.DistinctItems = current.Select(j => j.ItemId).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        foreach (var group in current.GroupBy(j => (Item: j.ItemId, j.Operation), new ItemOpComparer()))
        {
            var jobs = group.ToList();
            if (!jobs.Any(j => j.IsFailed || j.IsWarning))
            {
                if (!analysis.HealthyItems.Contains(jobs[0].ItemName)) analysis.HealthyItems.Add(jobs[0].ItemName);
                continue;
            }
            analysis.Activities.Add(BuildActivity(jobs));
        }
        analysis.HealthyItems = analysis.HealthyItems
            .Where(n => !analysis.Activities.Any(a => a.ItemName.Equals(n, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

        analysis.Activities = analysis.Activities
            .OrderBy(a => ClassificationOrder(a.Classification))
            .ThenByDescending(a => a.Failed)
            .ThenBy(a => a.ItemName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        analysis.Changes = BuildChanges(current, previous);

        if (current.Count == 0 && previous.Count > 0)
            analysis.Warnings.Add($"No jobs ran in the {analysis.TimeRangeLabel} although {previous.Count} ran in the period before — check that the schedules are still active.");
        if (analysis.InProgressCount > 3)
            analysis.Warnings.Add($"{analysis.InProgressCount} jobs are still in progress; long-running jobs can delay the next scheduled backup.");
    }

    private sealed class ItemOpComparer : IEqualityComparer<(string Item, string Operation)>
    {
        public bool Equals((string Item, string Operation) x, (string Item, string Operation) y) =>
            string.Equals(x.Item, y.Item, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Operation, y.Operation, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Item, string Operation) obj) =>
            HashCode.Combine(obj.Item.ToLowerInvariant(), obj.Operation.ToLowerInvariant());
    }

    internal static BackupJobActivity BuildActivity(List<BackupJobInfo> jobs)
    {
        jobs = jobs.OrderBy(j => j.StartTime).ToList();
        var finished = jobs.Where(j => !j.IsInProgress).ToList();
        var lastFailed = jobs.LastOrDefault(j => j.IsFailed);
        var consecutive = 0;
        for (var i = finished.Count - 1; i >= 0 && finished[i].IsFailed; i--) consecutive++;

        var durations = finished.Where(j => j.Duration is not null).Select(j => j.Duration!.Value).ToList();
        var a = new BackupJobActivity
        {
            ItemName = jobs[0].ItemName,
            ItemId = jobs[0].ItemId,
            WorkloadType = jobs[0].WorkloadType,
            Operation = jobs[0].Operation,
            PolicyName = jobs.Select(j => j.PolicyName).FirstOrDefault(p => !string.IsNullOrEmpty(p)) ?? "",
            Count = jobs.Count,
            Failed = jobs.Count(j => j.IsFailed),
            Succeeded = jobs.Count(j => j.IsCompleted || j.IsWarning),
            Warnings = jobs.Count(j => j.IsWarning),
            ConsecutiveFailures = consecutive,
            ErrorCode = lastFailed?.ErrorCode ?? jobs.LastOrDefault(j => !string.IsNullOrEmpty(j.ErrorCode))?.ErrorCode ?? "",
            ErrorMessage = lastFailed?.ErrorMessage ?? jobs.LastOrDefault(j => !string.IsNullOrEmpty(j.ErrorMessage))?.ErrorMessage ?? "",
            AzureRecommendation = lastFailed?.ErrorRecommendation ?? "",
            LastRunTime = jobs.Max(j => j.StartTime),
            LastFailureTime = lastFailed?.StartTime,
            LastSuccessTime = jobs.Where(j => j.IsCompleted || j.IsWarning).Max(j => j.StartTime),
            AverageDuration = durations.Count > 0 ? TimeSpan.FromTicks((long)durations.Average(d => d.Ticks)) : null,
            SampleJobIds = jobs.Where(j => j.IsFailed).TakeLast(3).Select(j => LastSegment(j.Id)).ToList(),
        };
        Classify(a);
        return a;
    }

    public static int ClassificationOrder(string classification) => classification switch
    {
        "Persistent failure" => 0,
        "Needs review" => 1,
        _ => 2
    };

    internal static void Classify(BackupJobActivity a)
    {
        var reasons = new List<string>();
        var recs = new List<string>();
        var known = KnownErrors.TryGetValue(a.ErrorCode, out var k) ? k : default;
        var cause = known.Summary ?? (string.IsNullOrEmpty(a.ErrorMessage) ? null : Truncate(a.ErrorMessage, 160));

        if (a.Failed == 0)
        {
            a.Classification = "Needs review";
            reasons.Add($"{a.Warnings} of {a.Count} {a.Operation.ToLowerInvariant()} job(s) completed with warnings");
            recs.Add("Open a warning job to read the warning details; common causes are skipped disks, unsupported file systems or a slow guest agent.");
        }
        else if (a.ConsecutiveFailures >= 2 || a.Failed >= 3 || (a.Failed == a.Count && a.Count >= 1 && a.LastSuccessTime is null && a.Failed >= 2))
        {
            a.Classification = "Persistent failure";
            reasons.Add(a.ConsecutiveFailures >= 2
                ? $"the last {a.ConsecutiveFailures} {a.Operation.ToLowerInvariant()} jobs failed in a row"
                : $"{a.Failed} of {a.Count} {a.Operation.ToLowerInvariant()} job(s) failed");
            if (a.LastSuccessTime is null) reasons.Add("no job succeeded in this window, so the item may have no recent recovery point");
            else reasons.Add($"the last success was {a.LastSuccessTime:yyyy-MM-dd HH:mm} UTC");
        }
        else if (a.LastSuccessTime is { } s && a.LastFailureTime is { } f && s > f)
        {
            a.Classification = "Transient failure";
            reasons.Add($"{a.Failed} failure(s) followed by a successful job at {s:yyyy-MM-dd HH:mm} UTC");
        }
        else
        {
            a.Classification = "Needs review";
            reasons.Add(a.Failed == 1 ? "a single failure that is also the most recent job" : $"{a.Failed} of {a.Count} job(s) failed");
        }

        var explanation = string.Join("; ", reasons);
        if (cause is not null) explanation += $" — {(known.Summary is not null ? "cause: " : "error: ")}{cause}";
        if (!string.IsNullOrEmpty(a.ErrorCode)) explanation += $" ({a.ErrorCode})";
        a.Explanation = Capitalize(explanation) + ".";

        if (known.Recommendation is not null) recs.Add(known.Recommendation);
        else if (!string.IsNullOrEmpty(a.AzureRecommendation)) recs.Add(a.AzureRecommendation);
        else if (a.Failed > 0 && a.ErrorCode.StartsWith("UserError", StringComparison.OrdinalIgnoreCase))
            recs.Add("The error code starts with UserError, which means the cause is in the configuration of the source or vault rather than the Azure platform — open the job for the exact recommended action.");
        else if (a.Failed > 0 && a.ErrorCode.StartsWith("Extension", StringComparison.OrdinalIgnoreCase))
            recs.Add("Extension errors come from inside the VM: check the Azure VM agent, the VMSnapshot extension and outbound connectivity.");

        if (a.Classification == "Persistent failure")
            recs.Add("Run an on-demand backup after fixing the cause so the item has a current recovery point, and make sure the built-in job-failure alerts reach someone.");
        else if (a.Classification == "Transient failure")
            recs.Add("No action is needed unless the pattern repeats; if the same transient error recurs daily, treat it as persistent.");
        if (a.AverageDuration is { } d && d > TimeSpan.FromHours(6))
            recs.Add($"Jobs take {d.TotalHours:0.#} hours on average; consider an Enhanced policy or excluding large data disks.");

        a.Recommendations = recs.Distinct().ToList();
    }

    internal static List<BackupJobChange> BuildChanges(List<BackupJobInfo> current, List<BackupJobInfo> previous)
    {
        var changes = new List<BackupJobChange>();
        var cur = current.GroupBy(j => j.ItemId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var prev = previous.GroupBy(j => j.ItemId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var (id, jobs) in cur)
        {
            var name = jobs[0].ItemName;
            var failed = jobs.Count(j => j.IsFailed);
            var ok = jobs.Count(j => j.IsCompleted || j.IsWarning);
            prev.TryGetValue(id, out var before);
            var failedBefore = before?.Count(j => j.IsFailed) ?? 0;

            if (failed > 0 && failedBefore == 0)
                changes.Add(new BackupJobChange { ChangeType = "New failure", ItemName = name, Description = $"{failed} failed job(s) this period, none in the previous one" + (before is null ? " (no jobs at all before)" : "") + "." });
            else if (failed == 0 && failedBefore > 0 && ok > 0)
                changes.Add(new BackupJobChange { ChangeType = "Recovered", ItemName = name, Description = $"{failedBefore} failure(s) in the previous period, all {ok} job(s) succeeded this period." });
            else if (failedBefore > 0 && failed >= failedBefore * 2)
                changes.Add(new BackupJobChange { ChangeType = "Increased", ItemName = name, Description = $"Failures went from {failedBefore} to {failed}." });
            else if (failed > 0 && failed * 2 <= failedBefore)
                changes.Add(new BackupJobChange { ChangeType = "Decreased", ItemName = name, Description = $"Failures went from {failedBefore} to {failed}." });

            if (before is not null)
            {
                var durNow = jobs.Where(j => j.Duration is not null && j.IsCompleted).Select(j => j.Duration!.Value.TotalMinutes).DefaultIfEmpty(0).Average();
                var durBefore = before.Where(j => j.Duration is not null && j.IsCompleted).Select(j => j.Duration!.Value.TotalMinutes).DefaultIfEmpty(0).Average();
                if (durBefore > 0 && durNow > durBefore * 1.5 && durNow - durBefore > 30)
                    changes.Add(new BackupJobChange { ChangeType = "Slower", ItemName = name, Description = $"Successful jobs now take {durNow:0} min on average, up from {durBefore:0} min." });
            }
        }

        foreach (var (id, before) in prev)
            if (!cur.ContainsKey(id))
                changes.Add(new BackupJobChange { ChangeType = "Stopped", ItemName = before[0].ItemName, Description = $"{before.Count} job(s) in the previous period, none in this one — protection may have been stopped or the schedule changed." });

        return changes
            .OrderBy(c => ChangeOrder(c.ChangeType))
            .ThenBy(c => c.ItemName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int ChangeOrder(string type) => type switch
    {
        "New failure" => 0, "Increased" => 1, "Stopped" => 2, "Slower" => 3, "Decreased" => 4, _ => 5
    };

    private static string Capitalize(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];
    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
