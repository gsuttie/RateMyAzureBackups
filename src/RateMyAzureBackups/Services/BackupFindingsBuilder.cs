using RateMyAzureBackups.Models;

namespace RateMyAzureBackups.Services;

/// <summary>Report-only findings for a scan result, each with concrete remediation guidance attached.</summary>
public static class BackupFindingsBuilder
{
    public static readonly IReadOnlyDictionary<string, string> FixByCategory = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Empty vault"] = "Configure backup for the workloads this vault should protect (Vault → Backup → select the workload type and items), or delete the vault if it is no longer needed so it does not give a false sense of coverage.",
        ["Protection error"] = "Open the item in the vault (Backup items → select the item) and read the error details; the usual causes are a stopped VM agent, a deleted source resource or missing permissions. Fix the cause and run Backup now.",
        ["Protection stopped"] = "Resume protection (Backup items → the item → Resume backup, picking a policy) so new recovery points are taken again, or delete the backup data if the workload has been retired deliberately.",
        ["Backup failed"] = "Open the failed job (Vault → Backup jobs) and follow the recommended action for its error code, then trigger Backup now to confirm the fix. Repeated failures mean the workload has no usable recent recovery point.",
        ["Stale backup"] = "Check the item's backup jobs for skipped or stuck runs, confirm the policy schedule is what you expect, and trigger an on-demand backup so a current recovery point exists.",
        ["Locally redundant"] = "Storage redundancy can only be changed while a vault has no protected items. Create a new geo-redundant vault (Properties → Backup Configuration → Geo-redundant) and move the workloads to it so a regional outage does not destroy the backups too.",
        ["No cross-region restore"] = "Enable Cross Region Restore (Vault → Properties → Backup Configuration → Cross Region Restore) so recovery points can be restored in the paired region during a regional outage. The vault must be geo-redundant.",
        ["Soft delete disabled"] = "Enable soft delete (Vault → Properties → Security Settings → Soft Delete) and consider the always-on state, so deleted recovery points are retained for 14+ days and can be undeleted.",
        ["Immutability off"] = "Enable immutable vault (Vault → Properties → Immutability settings) and lock it once validated, so recovery points cannot be deleted before their retention expires — even by an attacker with vault permissions.",
        ["No MUA"] = "Create a Resource Guard in a separate subscription or tenant and protect the vault with multi-user authorization (Vault → Properties → Multi-User Authorization), so critical operations need a second approver.",
        ["Public network access"] = "Disable public network access on the vault and connect through private endpoints (Vault → Networking), limiting the data plane to your virtual networks.",
        ["Weekly schedule"] = "Change the policy schedule to Daily (or Enhanced hourly for VMs) so the recovery point objective is at most one day (Vault → Backup policies → the policy → Modify).",
        ["Short retention"] = "Raise the daily retention to at least 30 days (Vault → Backup policies → the policy → Modify) so slow-burn data corruption or ransomware discovered weeks later can still be recovered from.",
        ["No long-term retention"] = "Add weekly, monthly or yearly retention points to the policy so long-term and compliance restores are possible.",
        ["Unused policy"] = "Delete the policy if nothing uses it (Vault → Backup policies), or assign it to the items that should follow it — an unused policy protects nothing.",
        ["Job alerts disabled"] = "Enable the built-in Azure Monitor alerts for all job failures (Vault → Properties → Monitoring Settings) and route them to an action group, so failed backups are noticed the same day.",
        ["No diagnostics"] = "Add a diagnostic setting on the vault that sends the Azure Backup categories (CoreAzureBackup, AddonAzureBackupJobs, AddonAzureBackupPolicy, ...) to a Log Analytics workspace, so Backup reports and long-term job history are available.",
        ["Unprotected VM"] = "Enable backup on the virtual machine (VM → Backup → choose a vault and policy, or Backup center → Backup) so it has recovery points; unprotected VMs cannot be restored after deletion, corruption or ransomware.",
    };

    public static string FixFor(string category) =>
        FixByCategory.TryGetValue(category, out var fix)
            ? fix
            : "Review this finding in the Azure Portal and adjust the backup configuration accordingly.";

    public static List<BackupFinding> Build(BackupScanResult result, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        var findings = new List<BackupFinding>();

        void Add(string severity, string category, string message, string name, string id, string subId) =>
            findings.Add(new BackupFinding
            {
                Severity = severity, Category = category, Message = message,
                ResourceName = name, ResourceId = id, SubscriptionId = subId, Fix = FixFor(category)
            });

        foreach (var v in result.Vaults)
        {
            var label = $"{v.Kind.Label()} '{v.Name}'";
            var unit = v.Kind.ItemLabel();

            if (v.Items.Count == 0)
                Add("Medium", "Empty vault", $"{label} contains no {v.Kind.ItemPluralLabel()} — it protects nothing.", v.Name, v.Id, v.SubscriptionId);

            var errored = v.Items.Where(i => i.IsErrored).ToList();
            if (errored.Count > 0)
                Add("High", "Protection error",
                    $"{label}: {errored.Count} {unit}(s) are in a protection error state: {Sample(errored.Select(i => i.Display))}.",
                    v.Name, v.Id, v.SubscriptionId);

            var stopped = v.Items.Where(i => i.IsStopped).ToList();
            if (stopped.Count > 0)
                Add("Medium", "Protection stopped",
                    $"{label}: {stopped.Count} {unit}(s) have protection stopped or suspended: {Sample(stopped.Select(i => i.Display))}.",
                    v.Name, v.Id, v.SubscriptionId);

            var failed = v.Items.Where(i => i.IsActive && i.LastBackupSucceeded == false).ToList();
            if (failed.Count > 0)
                Add("High", "Backup failed",
                    $"{label}: the most recent backup failed for {failed.Count} {unit}(s): {Sample(failed.Select(i => i.Display))}.",
                    v.Name, v.Id, v.SubscriptionId);

            var stale = v.Items.Where(i => i.IsActive && !i.IsPending && i.LastBackupSucceeded != false && i.LastBackupTime is { } t
                                           && at - t > BackupRatingService.FreshnessWindow(i, result.Policies)).ToList();
            if (stale.Count > 0)
                Add("Medium", "Stale backup",
                    $"{label}: {stale.Count} {unit}(s) have no recovery point within the expected window: {Sample(stale.Select(i => $"{i.Display} ({Age(at, i.LastBackupTime!.Value)})"))}.",
                    v.Name, v.Id, v.SubscriptionId);

            if (v.IsLocallyRedundant)
                Add("High", "Locally redundant", $"{label} stores its backups locally redundant (LRS) — a regional outage loses them.", v.Name, v.Id, v.SubscriptionId);

            if (v.CrossRegionRestore == false && v.Items.Count > 0)
                Add("Medium", "No cross-region restore", $"{label} does not have Cross Region Restore enabled.", v.Name, v.Id, v.SubscriptionId);

            if (v.SoftDeleteEnabled == false)
                Add("High", "Soft delete disabled", $"{label} has soft delete disabled — deleted recovery points cannot be recovered.", v.Name, v.Id, v.SubscriptionId);

            if (!v.ImmutabilityLocked && !v.ImmutabilityUnlocked)
                Add("Medium", "Immutability off", $"{label} is not an immutable vault — recovery points can be deleted before their retention ends.", v.Name, v.Id, v.SubscriptionId);
            else if (v.ImmutabilityUnlocked)
                Add("Info", "Immutability off", $"{label} has immutability enabled but not locked — the protection can still be switched off.", v.Name, v.Id, v.SubscriptionId);

            if (v.MultiUserAuthorization == false)
                Add("Info", "No MUA", $"{label} is not protected by multi-user authorization.", v.Name, v.Id, v.SubscriptionId);

            if (v.PublicNetworkAccess.Equals("Enabled", StringComparison.OrdinalIgnoreCase))
                Add("Info", "Public network access", $"{label} allows public network access.", v.Name, v.Id, v.SubscriptionId);

            if (v.AlertsForAllJobFailures == false)
                Add("Medium", "Job alerts disabled", $"{label} has the built-in alerts for job failures disabled.", v.Name, v.Id, v.SubscriptionId);

            if (v.DiagnosticsEnabled == false && v.Items.Count > 0)
                Add("Info", "No diagnostics", $"{label} sends no backup diagnostics to a Log Analytics workspace.", v.Name, v.Id, v.SubscriptionId);
        }

        foreach (var p in result.Policies)
        {
            var vault = result.Vaults.FirstOrDefault(v => v.Id.Equals(p.VaultId, StringComparison.OrdinalIgnoreCase));
            var label = $"Policy '{p.Name}'{(vault is null ? "" : $" on '{vault.Name}'")}";
            var inUse = p.ProtectedItemCount > 0 || result.AllItems.Any(i => p.Id.Equals(i.PolicyId, StringComparison.OrdinalIgnoreCase));

            if (!inUse)
            {
                Add("Info", "Unused policy", $"{label} is not assigned to any {p.Kind.ItemLabel()}.", p.Name, p.Id, p.SubscriptionId);
                continue;
            }

            if (p.ScheduleFrequency == "Weekly")
                Add("Medium", "Weekly schedule", $"{label} backs up only weekly ({p.ScheduleSummary}).", p.Name, p.Id, p.SubscriptionId);

            if (p.ShortTermRetentionDays is { } days && days < BackupRatingService.MinimumRetentionDays)
                Add("Medium", "Short retention", $"{label} keeps recovery points for only {days} days.", p.Name, p.Id, p.SubscriptionId);

            if (!p.HasLongTermRetention && p.ShortTermRetentionDays is not null)
                Add("Info", "No long-term retention", $"{label} has no weekly, monthly or yearly retention.", p.Name, p.Id, p.SubscriptionId);
        }

        foreach (var r in result.UnprotectedResources)
            Add("High", "Unprotected VM",
                $"Virtual machine '{r.Name}' ({r.ResourceGroup}) is not backed up by any Recovery Services vault in scope.",
                r.Name, r.Id, r.SubscriptionId);

        return findings
            .OrderBy(f => SeverityOrder(f.Severity))
            .ThenBy(f => f.ResourceName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static int SeverityOrder(string severity) => severity switch { "High" => 0, "Medium" => 1, _ => 2 };

    private static string Sample(IEnumerable<string> names)
    {
        var list = names.ToList();
        var text = string.Join(", ", list.Take(5));
        return list.Count > 5 ? $"{text} (+{list.Count - 5} more)" : text;
    }

    internal static string Age(DateTimeOffset now, DateTimeOffset then)
    {
        var age = now - then;
        if (age.TotalHours < 48) return $"{(int)age.TotalHours}h ago";
        return $"{(int)age.TotalDays}d ago";
    }
}
