using RateMyAzureBackups.Models;

namespace RateMyAzureBackups.Services;

/// <summary>
/// Pure, deterministic A-F rating calculator for one vault's backup posture (Recovery Services vault
/// or Backup vault). The weights follow Microsoft's Azure Backup best practices, most important first:
/// workloads actually being protected, recent successful recovery points, surviving a regional outage,
/// resisting deletion/ransomware, sensible policies, and knowing when jobs fail.
///
/// 100 points across six categories, mapped to grades
/// (A >= 90, B >= 75, C >= 60, D >= 45, E >= 30, F below), with hard caps:
/// nothing protected = F; any item with protection stopped or in error = max D;
/// any last backup failed = max C; soft delete off = max C; locally redundant storage = max B.
/// </summary>
public static class BackupRatingService
{
    // Category weights (sum = 100).
    public const int CoveragePts    = 30;
    public const int FreshnessPts   = 20;
    public const int ResiliencePts  = 20; // redundancy 12 + cross-region restore 8
    public const int SecurityPts    = 15; // soft delete 5 + immutability 5 + MUA 3 + public network off 2
    public const int PolicyPts      = 10; // daily schedule 4 + retention >= 30d 3 + long-term retention 3
    public const int MonitoringPts  = 5;  // job-failure alerts 3 + diagnostics to Log Analytics 2

    public const string CoverageCategory   = "Protection health";
    public const string FreshnessCategory  = "Backup freshness";
    public const string ResilienceCategory = "Redundancy & restore";
    public const string SecurityCategory   = "Vault security";
    public const string PolicyCategory     = "Policy hygiene";
    public const string MonitoringCategory = "Monitoring & alerts";

    /// <summary>Daily schedules get this long before a recovery point counts as stale (allows for jitter and long-running jobs).</summary>
    public static readonly TimeSpan DailyFreshnessWindow = TimeSpan.FromHours(36);
    public static readonly TimeSpan WeeklyFreshnessWindow = TimeSpan.FromDays(8);
    public const int MinimumRetentionDays = 30;

    /// <summary>Computes and stores one rating per vault on the scan result.</summary>
    public static void RateAll(BackupScanResult result, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        result.Ratings = result.Vaults.Select(v => RateVault(v, result.Policies, at)).ToList();
    }

    /// <summary>Grade letter for a raw 0-100 score, before caps.</summary>
    public static string GradeForScore(int score) => score switch
    {
        >= 90 => "A",
        >= 75 => "B",
        >= 60 => "C",
        >= 45 => "D",
        >= 30 => "E",
        _ => "F"
    };

    public static string LabelForGrade(string grade) => grade switch
    {
        "A" => "Excellent",
        "B" => "Good",
        "C" => "Average",
        "D" => "Below Average",
        "E" => "Poor",
        _ => "Unacceptable"
    };

    /// <summary>Traffic-light colour for a grade (hex, with leading '#'). Green to dark red.</summary>
    public static string ColorForGrade(string grade) => grade switch
    {
        "A" => "#388E3C",
        "B" => "#7CB342",
        "C" => "#FBC02D",
        "D" => "#F57C00",
        "E" => "#E64A19",
        _ => "#C62828"
    };

    /// <summary>The A-F legend shown in the app and in the reports.</summary>
    public static readonly (string Grade, string Label, string Description)[] Legend =
    [
        ("A", "EXCELLENT", "Outstanding — exceeds expectations"),
        ("B", "GOOD", "Above average — meets most expectations"),
        ("C", "AVERAGE", "Satisfactory — meets basic expectations"),
        ("D", "BELOW AVERAGE", "Below expectations — needs improvement"),
        ("E", "POOR", "Far below expectations — significant improvement needed"),
        ("F", "UNACCEPTABLE", "Unacceptable — does not meet minimum requirements"),
    ];

    /// <summary>The worse (further down the alphabet) of two grades.</summary>
    public static string WorstGrade(string a, string b) =>
        string.CompareOrdinal(a, b) >= 0 ? a : b;

    /// <summary>Overall grade for a set of ratings: the weakest vault sets the grade.</summary>
    public static BackupRating? Worst(IEnumerable<BackupRating> ratings) =>
        ratings.OrderByDescending(r => r.Grade, StringComparer.Ordinal).ThenBy(r => r.Score).FirstOrDefault();

    /// <summary>Policies the vault's items actually use (falling back to the vault's own policies when items carry no policy ID).</summary>
    public static List<BackupPolicyInfo> PoliciesInUse(BackupVaultInfo vault, IReadOnlyList<BackupPolicyInfo> allPolicies)
    {
        var used = allPolicies
            .Where(p => vault.Items.Any(i => p.Id.Equals(i.PolicyId, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (used.Count > 0) return used;
        var own = allPolicies.Where(p => p.VaultId.Equals(vault.Id, StringComparison.OrdinalIgnoreCase)).ToList();
        var withItems = own.Where(p => p.ProtectedItemCount > 0).ToList();
        return withItems.Count > 0 ? withItems : own;
    }

    /// <summary>How long after the last successful backup an item still counts as fresh, given its policy.</summary>
    public static TimeSpan FreshnessWindow(ProtectedItemInfo item, IReadOnlyList<BackupPolicyInfo> allPolicies)
    {
        var policy = allPolicies.FirstOrDefault(p => p.Id.Equals(item.PolicyId, StringComparison.OrdinalIgnoreCase));
        return policy?.ScheduleFrequency == "Weekly" ? WeeklyFreshnessWindow : DailyFreshnessWindow;
    }

    // ── The engine ───────────────────────────────────────────────────────────

    public static BackupRating RateVault(BackupVaultInfo v, IReadOnlyList<BackupPolicyInfo> allPolicies, DateTimeOffset now)
    {
        var product = v.Kind.Label();
        var unit = v.Kind.ItemLabel();
        var items = v.Items;
        var active = items.Where(i => i.IsActive).ToList();
        var broken = items.Where(i => !i.IsActive).ToList();

        var rating = new BackupRating
        {
            TargetKind = v.Kind,
            TargetId = v.Id,
            TargetName = v.Name,
            SubscriptionId = v.SubscriptionId
        };

        // Caps collected as (key, max grade, reason); improvements may lift one by key.
        var caps = new List<(string Key, string MaxGrade, string Reason)>();
        var improvements = new List<(BackupImprovement Item, string? LiftsCapKey)>();

        void Improve(string action, int points, string? liftsCapKey = null)
        {
            if (points > 0)
                improvements.Add((new BackupImprovement { Action = action, PointsGained = points }, liftsCapKey));
        }

        // ── 1. Protection health (30) ────────────────────────────────────────
        // Every item whose protection is stopped or in error is a workload with no new recovery points,
        // which is the backup equivalent of an unprotected endpoint. Hence the D cap below.
        var coverage = new BackupRatingCategory { Name = CoverageCategory, Possible = CoveragePts };
        if (items.Count == 0)
        {
            coverage.Earned = 0;
            coverage.Details.Add($"This {product} protects nothing — it contains no {v.Kind.ItemPluralLabel()}.");
            caps.Add(("noitems", "F", $"Nothing is protected by this {product}."));
            Improve($"Configure backup for the workloads this vault is meant to protect (or delete '{v.Name}' if it is unused).", CoveragePts, "noitems");
        }
        else
        {
            coverage.Earned = (int)Math.Round(CoveragePts * (double)active.Count / items.Count);
            if (active.Count > 0 && active.Count < items.Count)
                coverage.Earned = Math.Clamp(coverage.Earned, 1, CoveragePts - 1);
            coverage.Details.Add($"{active.Count} of {items.Count} {(items.Count == 1 ? unit : v.Kind.ItemPluralLabel())} are actively protected.");

            var errored = broken.Count(i => i.IsErrored);
            var stopped = broken.Count - errored;
            if (broken.Count > 0)
            {
                var parts = new List<string>();
                if (errored > 0) parts.Add($"{errored} in a protection error state");
                if (stopped > 0) parts.Add($"{stopped} with protection stopped or suspended");
                coverage.Details.Add($"{string.Join(", ", parts)}: {string.Join(", ", broken.Take(4).Select(i => $"'{i.Display}'"))}{(broken.Count > 4 ? $" (+{broken.Count - 4} more)" : "")}.");
            }

            if (active.Count == 0)
                caps.Add(("broken", "F", $"No {unit} of this {product} is actively protected — every one is stopped or in error."));
            else if (broken.Count > 0)
                caps.Add(("broken", "D",
                    $"{broken.Count} {unit}(s) have protection stopped or in error — those workloads get no new recovery points."));

            if (broken.Count > 0)
                Improve($"Resume or repair protection for the {broken.Count} {unit}(s) that are stopped or in error on '{v.Name}' " +
                        "(or remove them if the workload is gone).",
                        CoveragePts - coverage.Earned, "broken");
        }
        rating.Categories.Add(coverage);

        // ── 2. Backup freshness (20) ─────────────────────────────────────────
        var freshness = new BackupRatingCategory { Name = FreshnessCategory, Possible = FreshnessPts };
        if (active.Count > 0)
        {
            var known = active.Where(i => i.LastBackupSucceeded is not null || i.LastBackupTime is not null).ToList();
            if (known.Count == 0)
            {
                freshness.Earned = FreshnessPts / 2;
                freshness.Details.Add(active.All(i => i.IsPending)
                    ? "Initial backups are still pending — half points until the first recovery point exists."
                    : "Last backup status could not be determined — half points awarded.");
            }
            else
            {
                var fresh = known.Where(i => i.IsFresh(now, FreshnessWindow(i, allPolicies))).ToList();
                var failed = known.Where(i => i.LastBackupSucceeded == false).ToList();
                var stale = known.Except(fresh).Except(failed).ToList();

                freshness.Earned = (int)Math.Round(FreshnessPts * (double)fresh.Count / known.Count);
                if (fresh.Count > 0 && fresh.Count < known.Count)
                    freshness.Earned = Math.Clamp(freshness.Earned, 1, FreshnessPts - 1);
                freshness.Details.Add($"{fresh.Count} of {known.Count} active {(known.Count == 1 ? unit : v.Kind.ItemPluralLabel())} have a recent successful recovery point.");

                if (failed.Count > 0)
                {
                    freshness.Details.Add($"Last backup failed for: {string.Join(", ", failed.Take(4).Select(i => $"'{i.Display}'"))}{(failed.Count > 4 ? $" (+{failed.Count - 4} more)" : "")}.");
                    caps.Add(("failed", "C", $"{failed.Count} {unit}(s) failed their most recent backup."));
                }
                if (stale.Count > 0)
                    freshness.Details.Add($"{stale.Count} {unit}(s) have no recovery point within the expected window.");

                if (fresh.Count < known.Count)
                    Improve($"Investigate the {known.Count - fresh.Count} {unit}(s) on '{v.Name}' whose last backup failed or is overdue, and run an on-demand backup once fixed.",
                            FreshnessPts - freshness.Earned, failed.Count > 0 ? "failed" : null);
            }
        }
        else
        {
            freshness.Details.Add($"No active {unit} to assess.");
        }
        rating.Categories.Add(freshness);

        // ── 3. Redundancy & restore (20) ─────────────────────────────────────
        var resilience = new BackupRatingCategory { Name = ResilienceCategory, Possible = ResiliencePts };
        if (v.IsGeoRedundant)
        {
            resilience.Earned += 12;
            resilience.Details.Add($"Vault storage is geo-redundant ({v.RedundancyLabel}).");
        }
        else if (v.IsZoneRedundant)
        {
            resilience.Earned += 8;
            resilience.Details.Add("Vault storage is zone-redundant (survives a zone failure, not a regional one).");
            Improve($"Switch '{v.Name}' to geo-redundant storage so backups survive a regional outage (only possible while the vault has no protected items, otherwise plan a new vault).", 4);
        }
        else if (v.IsLocallyRedundant)
        {
            resilience.Details.Add("Vault storage is locally redundant — a regional outage takes the backups with it.");
            caps.Add(("lrs", "B", "Backups are stored locally redundant (LRS) — a regional outage loses the recovery points along with the workloads."));
            Improve($"Move '{v.Name}' to geo-redundant storage (GRS) — redundancy can only be changed on a vault with no protected items, so this usually means a new vault.", 12, "lrs");
        }
        else
        {
            resilience.Earned += 6;
            resilience.Details.Add("Storage redundancy could not be determined — half points awarded.");
        }

        if (v.CrossRegionRestore == true)
        {
            resilience.Earned += 8;
            resilience.Details.Add("Cross Region Restore is enabled.");
        }
        else if (v.CrossRegionRestore is null && !v.IsLocallyRedundant && !v.IsZoneRedundant)
        {
            resilience.Earned += 4;
            resilience.Details.Add("Cross Region Restore state could not be determined — half points awarded.");
        }
        else
        {
            resilience.Details.Add("Cross Region Restore is not enabled — recovery points cannot be restored from the paired region during an outage.");
            Improve($"Enable Cross Region Restore on '{v.Name}'{(v.IsGeoRedundant ? "" : " (requires geo-redundant storage)")} so restores work from the paired region.", 8);
        }
        rating.Categories.Add(resilience);

        // ── 4. Vault security (15) ───────────────────────────────────────────
        var security = new BackupRatingCategory { Name = SecurityCategory, Possible = SecurityPts };
        switch (v.SoftDeleteEnabled)
        {
            case true:
                security.Earned += 5;
                security.Details.Add(v.SoftDeleteAlwaysOn
                    ? $"Soft delete is always-on{Days(v.SoftDeleteRetentionDays)}."
                    : $"Soft delete is enabled{Days(v.SoftDeleteRetentionDays)}.");
                break;
            case null:
                security.Earned += 2;
                security.Details.Add("Soft delete state could not be determined.");
                break;
            case false:
                security.Details.Add("Soft delete is disabled — deleted backup data is gone immediately.");
                caps.Add(("softdelete", "C", "Soft delete is disabled — an accidental or malicious delete destroys the recovery points immediately."));
                Improve($"Enable soft delete on '{v.Name}' (and consider making it always-on).", 5, "softdelete");
                break;
        }

        if (v.ImmutabilityLocked)
        {
            security.Earned += 5;
            security.Details.Add("Immutable vault is locked.");
        }
        else if (v.ImmutabilityUnlocked)
        {
            security.Earned += 3;
            security.Details.Add("Immutable vault is enabled but not locked — it can still be switched off.");
            Improve($"Lock the immutability setting on '{v.Name}' so it cannot be reverted.", 2);
        }
        else
        {
            security.Details.Add("Immutable vault is not enabled — recovery points can be deleted before their retention ends.");
            Improve($"Enable (and then lock) immutable vault on '{v.Name}' to protect recovery points from early deletion.", 5);
        }

        switch (v.MultiUserAuthorization)
        {
            case true:
                security.Earned += 3;
                security.Details.Add("Multi-user authorization (Resource Guard) protects critical operations.");
                break;
            case null:
                security.Earned += 1;
                security.Details.Add("Multi-user authorization state could not be determined.");
                break;
            default:
                security.Details.Add("Multi-user authorization is not configured.");
                Improve($"Protect '{v.Name}' with multi-user authorization (a Resource Guard) so a single compromised identity cannot disable backups.", 3);
                break;
        }

        if (v.PublicNetworkAccess.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
        {
            security.Earned += 2;
            security.Details.Add("Public network access is disabled.");
        }
        else if (string.IsNullOrEmpty(v.PublicNetworkAccess))
        {
            security.Earned += 1;
            security.Details.Add("Public network access setting could not be determined.");
        }
        else
        {
            security.Details.Add("Public network access is enabled.");
            Improve($"Disable public network access on '{v.Name}' and use private endpoints.", 2);
        }
        rating.Categories.Add(security);

        // ── 5. Policy hygiene (10) ───────────────────────────────────────────
        var policy = new BackupRatingCategory { Name = PolicyCategory, Possible = PolicyPts };
        var used = PoliciesInUse(v, allPolicies);
        if (items.Count == 0)
        {
            policy.Details.Add("No protected item to assess.");
        }
        else if (used.Count == 0)
        {
            policy.Earned = PolicyPts / 2;
            policy.Details.Add("Backup policies could not be read — half points awarded.");
        }
        else
        {
            var scheduleKnown = used.Where(p => !string.IsNullOrEmpty(p.ScheduleFrequency)).ToList();
            var daily = scheduleKnown.Count(p => p.IsDailyOrBetter);
            var schedulePts = scheduleKnown.Count == 0 ? 2 : (int)Math.Round(4.0 * daily / scheduleKnown.Count);
            policy.Earned += schedulePts;
            if (scheduleKnown.Count == 0) policy.Details.Add("Schedule frequency unknown.");
            else if (daily == scheduleKnown.Count) policy.Details.Add($"All {used.Count} polic{(used.Count == 1 ? "y" : "ies")} in use back up daily or more often.");
            else
            {
                var weekly = scheduleKnown.Where(p => !p.IsDailyOrBetter).Select(p => $"'{p.Name}'");
                policy.Details.Add($"Less than daily schedule on {string.Join(", ", weekly)}.");
                Improve("Move weekly policies to a daily (or more frequent) schedule so the recovery point objective is at most a day.", 4 - schedulePts);
            }

            var retentionKnown = used.Where(p => p.ShortTermRetentionDays is not null).ToList();
            var longEnough = retentionKnown.Count(p => p.ShortTermRetentionDays >= MinimumRetentionDays);
            var retentionPts = retentionKnown.Count == 0 ? 1 : (int)Math.Round(3.0 * longEnough / retentionKnown.Count);
            policy.Earned += retentionPts;
            if (retentionKnown.Count == 0) policy.Details.Add("Retention unknown.");
            else if (longEnough == retentionKnown.Count) policy.Details.Add($"Every policy keeps recovery points for at least {MinimumRetentionDays} days.");
            else
            {
                var shortOnes = retentionKnown.Where(p => p.ShortTermRetentionDays < MinimumRetentionDays).Select(p => $"'{p.Name}' ({p.ShortTermRetentionDays}d)");
                policy.Details.Add($"Short retention on {string.Join(", ", shortOnes)}.");
                Improve($"Raise daily retention to at least {MinimumRetentionDays} days on the short-retention policies so slow-burn corruption and ransomware can still be recovered from.", 3 - retentionPts);
            }

            if (used.Any(p => p.HasLongTermRetention))
            {
                policy.Earned += 3;
                policy.Details.Add("Long-term (weekly/monthly/yearly) retention is configured.");
            }
            else
            {
                policy.Details.Add("No weekly, monthly or yearly retention points — only short-term recovery is possible.");
                Improve("Add weekly or monthly retention to at least one policy for long-term recovery and compliance.", 3);
            }
        }
        rating.Categories.Add(policy);

        // ── 6. Monitoring & alerts (5) ───────────────────────────────────────
        var monitoring = new BackupRatingCategory { Name = MonitoringCategory, Possible = MonitoringPts };
        switch (v.AlertsForAllJobFailures)
        {
            case true:
                monitoring.Earned += 3;
                monitoring.Details.Add("Built-in Azure Monitor alerts fire for every job failure.");
                break;
            case null:
                monitoring.Earned += 1;
                monitoring.Details.Add("Alert setting could not be determined.");
                break;
            default:
                monitoring.Details.Add("Built-in job-failure alerts are disabled.");
                Improve($"Enable the built-in Azure Monitor alerts for all job failures on '{v.Name}' (Properties → Monitoring settings).", 3);
                break;
        }
        if (v.LogDestinations.Any(d => d.StartsWith("Log Analytics", StringComparison.OrdinalIgnoreCase)))
        {
            monitoring.Earned += 2;
            monitoring.Details.Add("Backup diagnostics flow to a Log Analytics workspace.");
        }
        else if (v.DiagnosticsEnabled is null)
        {
            monitoring.Earned += 1;
            monitoring.Details.Add("Diagnostic settings could not be read.");
        }
        else
        {
            monitoring.Details.Add("Backup diagnostics do not reach a Log Analytics workspace, so Backup reports cannot be built.");
            Improve($"Send the backup diagnostic logs of '{v.Name}' to a Log Analytics workspace for reporting and long-term job history.", 2);
        }
        rating.Categories.Add(monitoring);

        // ── Score, caps and grade ────────────────────────────────────────────
        rating.Score = rating.Categories.Sum(c => c.Earned);

        var scoreGrade = GradeForScore(rating.Score);
        var finalGrade = caps.Aggregate(scoreGrade, (g, cap) => WorstGrade(g, cap.MaxGrade));

        rating.Grade = finalGrade;
        rating.GradeLabel = LabelForGrade(finalGrade);
        rating.CapReason = finalGrade != scoreGrade
            ? caps.Where(c => c.MaxGrade == finalGrade).Select(c => c.Reason).FirstOrDefault()
            : null;

        // ── Resulting grade per improvement (score + points, minus any lifted cap) ──
        foreach (var (item, liftsCapKey) in improvements)
        {
            var newScore = Math.Min(100, rating.Score + item.PointsGained);
            var remainingCaps = caps.Where(c => c.Key != liftsCapKey);
            item.ResultingGrade = remainingCaps.Aggregate(GradeForScore(newScore), (g, cap) => WorstGrade(g, cap.MaxGrade));
        }
        rating.Improvements = improvements
            .Select(i => i.Item)
            .OrderByDescending(i => i.PointsGained)
            .ToList();

        return rating;
    }

    private static string Days(int? days) => days is { } d ? $" ({d} days)" : "";
}
