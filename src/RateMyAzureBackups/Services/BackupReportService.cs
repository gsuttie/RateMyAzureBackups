using RateMyAzureBackups.Models;
using RateMyAzureBackups.Services.Reports;

namespace RateMyAzureBackups.Services;

/// <summary>Everything the reports need that isn't in the scan / job analysis results themselves.</summary>
public sealed class BackupReportContext
{
    public BackupFlowKind Flow { get; init; }
    public string CustomerName { get; init; } = "";
    public string TenantName { get; init; } = "";
    public string ReportAuthor { get; init; } = "";
    public DateTime ScanDate { get; init; } = DateTime.Now;
    public List<string> SubscriptionLines { get; init; } = new();
    public BackupScanResult? Scan { get; init; }
    public BackupJobAnalysis? JobAnalysis { get; init; }
}

/// <summary>Builds the summary and detailed-findings reports (as a ReportDoc) for one flow.</summary>
public static class BackupReportService
{
    public static byte[] SummaryDocx(BackupReportContext ctx) => DocxReportRenderer.Render(BuildSummary(ctx));
    public static byte[] FindingsDocx(BackupReportContext ctx) => DocxReportRenderer.Render(BuildFindings(ctx));
    public static string SummaryHtml(BackupReportContext ctx) => HtmlReportRenderer.Render(BuildSummary(ctx));
    public static string FindingsHtml(BackupReportContext ctx) => HtmlReportRenderer.Render(BuildFindings(ctx));
    public static string SummaryMarkdown(BackupReportContext ctx) => MarkdownReportRenderer.Render(BuildSummary(ctx));
    public static string FindingsMarkdown(BackupReportContext ctx) => MarkdownReportRenderer.Render(BuildFindings(ctx));

    public static string FileName(BackupReportContext ctx, bool findings, bool markdown = false)
    {
        var scope = ctx.Flow == BackupFlowKind.RecoveryServices ? "RecoveryServices" : "BackupVault";
        var customer = string.Concat((ctx.CustomerName ?? "").Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
        if (string.IsNullOrEmpty(customer)) customer = "Backup";
        return $"{customer}-{scope}-Backup-{(findings ? "Findings" : "Summary")}-{ctx.ScanDate:yyyyMMdd}.{(markdown ? "md" : "docx")}";
    }

    // ── Summary report ───────────────────────────────────────────────────────

    public static ReportDoc BuildSummary(BackupReportContext ctx)
    {
        var product = ctx.Flow.Label();
        var scan = ctx.Scan;
        var ja = ctx.JobAnalysis;
        var vaults = scan?.Vaults ?? [];
        var policies = scan?.Policies ?? [];
        var findings = scan?.Findings ?? [];
        var items = vaults.SelectMany(v => v.Items).ToList();

        int broken = items.Count(i => !i.IsActive);
        int failed = items.Count(i => i.IsActive && i.LastBackupSucceeded == false);
        int lrs = vaults.Count(v => v.IsLocallyRedundant);
        int softDeleteOff = vaults.Count(v => v.SoftDeleteEnabled == false);
        int noImmutability = vaults.Count(v => !v.ImmutabilityLocked);
        int unprotectedVms = scan?.UnprotectedResources.Count ?? 0;

        var doc = new ReportDoc { Title = $"{product}s — Backup Summary Report" };

        doc.H1("1. Document information");
        DocInfo(doc, ctx);

        doc.H1("2. Executive summary");
        if (scan is null)
        {
            doc.P("No scan data was available when this report was generated — run a scan to populate the estate sections.");
        }
        else
        {
            var high = findings.Count(f => f.Severity == "High");
            var medium = findings.Count(f => f.Severity == "Medium");
            var info = findings.Count(f => f.Severity == "Info");
            doc.P($"The scan covered {ctx.SubscriptionLines.Count} subscription(s) and found {vaults.Count} {ctx.Flow.PluralLabel()} " +
                  $"protecting {items.Count} {ctx.Flow.ItemPluralLabel()} with {policies.Count} backup polic{(policies.Count == 1 ? "y" : "ies")}. " +
                  $"It produced {findings.Count} finding(s): {high} High, {medium} Medium and {info} Info.");
            var concerns = new List<string>();
            if (unprotectedVms > 0) concerns.Add($"{unprotectedVms} virtual machine(s) in scope are not backed up at all");
            if (broken > 0) concerns.Add($"{broken} {ctx.Flow.ItemPluralLabel()} have protection stopped or in error");
            if (failed > 0) concerns.Add($"{failed} {ctx.Flow.ItemPluralLabel()} failed their most recent backup");
            if (lrs > 0) concerns.Add($"{lrs} vault(s) use locally redundant storage");
            if (softDeleteOff > 0) concerns.Add($"{softDeleteOff} vault(s) have soft delete disabled");
            if (concerns.Count > 0) doc.P($"The most significant gaps: {string.Join("; ", concerns)}.");
        }
        if (ja is { JobsQueryable: true })
            doc.P($"Backup jobs for {product} '{ja.TargetName}' were analysed over the {ja.TimeRangeLabel}: " +
                  $"{ja.TotalJobs} job(s) across {ja.DistinctItems} item(s) — {ja.CompletedCount} completed, {ja.WarningCount} with warnings, " +
                  $"{ja.FailedCount} failed, {ja.InProgressCount} in progress ({ja.SuccessRate}% success). {Classified(ja)}");
        doc.Note("This report is the output of a read-only scan; no changes were made to any Azure resource.");

        doc.H1($"3. {product} backup rating");
        RatingSection(doc, ctx);

        doc.H1("4. Backup estate overview");
        var rows = new List<string[]>
        {
            new[] { ctx.Flow.PluralLabel(), Count(scan, vaults.Count) },
            new[] { Capitalize(ctx.Flow.ItemPluralLabel()), Count(scan, items.Count) },
            new[] { "Items actively protected", Count(scan, items.Count(i => i.IsActive)) },
            new[] { "Items with protection stopped or in error", Count(scan, broken) },
            new[] { "Items whose last backup failed", Count(scan, failed) },
            new[] { "Backup policies", Count(scan, policies.Count) },
            new[] { "Vaults on locally redundant storage", Count(scan, lrs) },
            new[] { "Vaults without soft delete", Count(scan, softDeleteOff) },
            new[] { "Vaults without locked immutability", Count(scan, noImmutability) },
            new[] { "Total findings", Count(scan, findings.Count) },
        };
        if (ctx.Flow == BackupFlowKind.RecoveryServices)
            rows.Insert(3, new[] { "Virtual machines without backup", scan is null ? "—" : $"{unprotectedVms} of {scan.VirtualMachineCount}" });
        doc.Table(["Metric", "Value"], rows, "No scan data available.");

        doc.H1("5. Findings summary");
        var orderedFindings = findings.OrderBy(f => BackupFindingsBuilder.SeverityOrder(f.Severity)).ToList();
        doc.Table(["Severity", "Category", "Finding", "Resource / scope", "Remediation"],
            orderedFindings.Select(f => new[] { f.Severity, f.Category, f.Message,
                $"{f.ResourceName}\n{f.ResourceId}\nSubscription: {f.SubscriptionId}", f.Fix }).ToList(),
            scan is null ? "No scan data available." : "No findings — the backup estate matches the checks performed.",
            orderedFindings.Select(f => new string?[] { SeverityColor(f.Severity), null, null, null, null }).ToList());

        doc.H1("6. Vault configuration");
        doc.Table(["Vault", "Redundancy", "Cross-region restore", "Soft delete", "Immutability", "MUA", "Job alerts"],
            vaults.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).Select(v => new[]
            {
                v.Name, v.RedundancyLabel, Flag(v.CrossRegionRestore), Fallback(v.SoftDeleteState, "Unknown"),
                Fallback(v.ImmutabilityState, "Unknown"), Flag(v.MultiUserAuthorization), Flag(v.AlertsForAllJobFailures)
            }).ToList(),
            scan is null ? "No scan data available." : $"No {ctx.Flow.PluralLabel()} in scope.");

        doc.H1("7. Policy compliance");
        doc.Table(["Policy", "Vault", "Workload", "Schedule", "Retention", "Items"],
            policies.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(p => new[]
            {
                p.Name, Azure.Json.LastSegment(p.VaultId), p.WorkloadType, Fallback(p.ScheduleSummary, "Unknown"), p.RetentionSummary, p.ProtectedItemCount.ToString()
            }).ToList(),
            scan is null ? "No scan data available." : "No backup policies found.");
        PolicyDetails(doc, scan);

        doc.H1("8. Recommendations");
        var recs = new List<string>();
        foreach (var f in findings.Where(f => f.Severity == "High").Take(5))
            recs.Add($"{f.Message} ({f.ResourceName})");
        if (unprotectedVms > 0)
            recs.Add($"Enable backup on the {unprotectedVms} unprotected virtual machine(s).");
        if (broken > 0)
            recs.Add($"Resume or repair protection for the {broken} item(s) that are stopped or in error so they get new recovery points.");
        if (lrs > 0)
            recs.Add($"Plan a move to geo-redundant vaults for the {lrs} vault(s) on locally redundant storage.");
        if (softDeleteOff > 0)
            recs.Add($"Enable soft delete on the {softDeleteOff} vault(s) where it is off.");
        if (ja is not null)
            recs.AddRange(ja.Activities
                .OrderBy(r => BackupJobAnalysisService.ClassificationOrder(r.Classification))
                .SelectMany(r => r.Recommendations).Distinct().Take(5));
        if (recs.Count == 0)
            recs.Add("No immediate actions required — continue periodic scans and job reviews to maintain the current posture.");
        doc.Bullets(recs.Distinct().Take(10));
        doc.Note("All recommendations are advisory. RateMyAzureBackups does not modify vaults, policies, protected items or diagnostic settings.");

        doc.H1("9. Protected items");
        ProtectedItemDetails(doc, scan, ctx.Flow);
        doc.H1("10. Backup job analysis");
        JobAnalysisDetails(doc, ctx, "10");
        AssessmentSections(doc, scan, 11);
        return doc;
    }

    // ── Detailed findings report ─────────────────────────────────────────────

    public static ReportDoc BuildFindings(BackupReportContext ctx)
    {
        var product = ctx.Flow.Label();
        var scan = ctx.Scan;
        var ja = ctx.JobAnalysis;
        var doc = new ReportDoc { Title = $"{product}s — Detailed Backup Findings" };

        doc.H1("1. Document information");
        DocInfo(doc, ctx);

        doc.H1($"2. {product} backup rating");
        RatingSection(doc, ctx);

        doc.H1("3. Detailed findings");
        if (scan is null)
            doc.P("No scan data was available when this report was generated.");
        else if (scan.Findings.Count == 0)
            doc.P("No findings — the backup estate matches the checks performed.");
        else
            foreach (var group in scan.Findings.GroupBy(f => f.Severity).OrderBy(g => BackupFindingsBuilder.SeverityOrder(g.Key)))
            {
                doc.P($"{group.Key} ({group.Count()})", bold: true, color: SeverityColor(group.Key));
                doc.Bullets(group.Select(f => $"{f.Category}: {f.Message} — {f.ResourceName} ({f.ResourceId}, subscription {f.SubscriptionId}). Fix: {f.Fix}"));
            }

        doc.H1("4. Backup policy inventory");
        PolicyDetails(doc, scan);

        doc.H1("5. Protected items");
        ProtectedItemDetails(doc, scan, ctx.Flow);

        doc.H1("6. Backup job analysis");
        JobAnalysisDetails(doc, ctx, "6");

        AssessmentSections(doc, scan, 7);
        return doc;
    }

    // ── Shared sections ──────────────────────────────────────────────────────

    private static void PolicyDetails(ReportDoc doc, BackupScanResult? scan)
    {
        if (scan is null) doc.P("No scan data available.");
        else if (scan.Policies.Count == 0) doc.P("No backup policies in scope.");
        else
            foreach (var p in scan.Policies.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                doc.P($"{p.Name} ({p.WorkloadType}, vault {Azure.Json.LastSegment(p.VaultId)})", bold: true);
                var settings = new List<string>
                {
                    $"Policy ID: {p.Id}",
                    $"Vault ID: {p.VaultId}",
                    $"Subscription: {p.SubscriptionId}; resource group: {p.ResourceGroup}",
                    $"Policy type: {Fallback(p.PolicyType, "Unknown")}; backup management type: {Fallback(p.BackupManagementType, "Unknown")}",
                    $"Frequency: {Fallback(p.ScheduleFrequency, "Unknown")}",
                    $"Schedule: {Fallback(p.ScheduleSummary, "Unknown")}",
                    $"Retention: {p.RetentionSummary}",
                    $"Azure-reported protected items: {p.ProtectedItemCount}",
                    $"Time zone: {Fallback(p.TimeZone, "Unknown")}"
                };
                settings.Add($"Schedule interval: {Number(p.ScheduleIntervalHours, "hours")}");
                settings.Add($"Instant restore snapshots: {Number(p.InstantRpRetentionDays, "days")}");
                settings.Add($"Log backup frequency: {Number(p.LogBackupFrequencyMinutes, "minutes")}; log retention: {Number(p.LogRetentionDays, "days")}");
                doc.Bullets(settings);
                var linked = scan.AllItems.Where(i => p.Id.Equals(i.PolicyId, StringComparison.OrdinalIgnoreCase)).ToList();
                doc.P($"Matched items: {linked.Count}");
                if (linked.Count > 0) doc.Bullets(linked.Select(i => $"{i.Display} ({i.Id})"));
                else doc.P(p.ProtectedItemCount > 0
                    ? "Azure reports assigned items, but none were matched in this scan."
                    : "No assigned items were reported or matched.");
            }
    }

    private static void ProtectedItemDetails(ReportDoc doc, BackupScanResult? scan, BackupFlowKind flow)
    {
        if (scan is null) doc.P("No scan data available.");
        else
        {
            foreach (var v in scan.Vaults.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
            {
                doc.P($"{v.Name} ({v.KindLabel}, {v.RedundancyLabel}, {v.Location}) — {v.Items.Count} {(v.Items.Count == 1 ? flow.ItemLabel() : flow.ItemPluralLabel())}", bold: true);
                if (v.Items.Count == 0) { doc.P("Nothing is protected by this vault."); continue; }
                foreach (var item in v.Items)
                {
                    doc.P($"{item.Display} ({item.WorkloadType})", bold: true);
                    doc.Table(null,
                    [
                        ["Backup item ID", item.Id],
                        ["Source resource ID", Fallback(item.SourceResourceId, "Unknown")],
                        ["Vault ID", Fallback(item.VaultId, v.Id)],
                        ["Subscription", item.SubscriptionId],
                        ["Backup management / raw type", $"{Fallback(item.BackupManagementType, "Unknown")} / {Fallback(item.RawType, "Unknown")}"],
                        ["Container", Fallback(item.ContainerName, "Unknown")],
                        ["Protection state / health", $"{Fallback(item.ProtectionState, "Unknown")} / {Fallback(item.HealthStatus, "Unknown")}"],
                        ["Last backup status", Fallback(item.LastBackupStatus, "Unknown")],
                        ["Last backup time (UTC)", Timestamp(item.LastBackupTime)],
                        ["Policy / policy ID", $"{Fallback(item.PolicyName, "Unknown")}\n{Fallback(item.PolicyId ?? "", "Unknown")}"],
                        ["Error code", Fallback(item.ErrorCode, "None reported")],
                        ["Error message", Fallback(item.ErrorMessage, "None reported")]
                    ], "", firstColHeader: true);
                }
            }
            if (scan.Vaults.Count == 0) doc.P($"No {flow.PluralLabel()} in scope.");

            if (flow == BackupFlowKind.RecoveryServices)
            {
                doc.H2("Virtual machines without backup");
                if (scan.UnprotectedResources.Count == 0)
                    doc.P($"No unprotected VM candidates returned by the VM check ({scan.VirtualMachineCount} VM(s) counted). Review scan warnings and inventory coverage before concluding protection is complete.");
                else
                    doc.Bullets(scan.UnprotectedResources.Select(r =>
                        $"{r.Name} ({r.Id}; subscription {r.SubscriptionId}; {r.ResourceGroup}, {r.Location}{(string.IsNullOrEmpty(r.PowerState) ? "" : $", {r.PowerState}")})"));
            }
        }
    }

    private static void JobAnalysisDetails(ReportDoc doc, BackupReportContext ctx, string section)
    {
        var ja = ctx.JobAnalysis;
        doc.P("Scope: " + (ja is null
            ? "no job analysis has been run"
            : $"{ctx.Flow.Label()} '{ja.TargetName}' ({ja.TargetId}), {ja.TimeRangeLabel}"));

        doc.H2($"{section}.1 Activity summary");
        if (ja is null) doc.P("No job analysis available — run one from the Jobs step.");
        else if (!ja.JobsQueryable) doc.P($"Backup jobs could not be queried: {ja.QueryError}");
        else
        {
            doc.P($"{ja.TotalJobs} job(s) in the {ja.TimeRangeLabel}: {ja.CompletedCount} completed, {ja.WarningCount} completed with warnings, " +
                  $"{ja.FailedCount} failed, {ja.CancelledCount} cancelled and {ja.InProgressCount} in progress, across {ja.DistinctItems} distinct item(s) ({ja.SuccessRate}% success). {Classified(ja)}");
        }
        if (ja is not null)
            foreach (var w in ja.Warnings) doc.P($"Warning: {w}");

        doc.H2($"{section}.2 Changes versus the previous period");
        if (ja is not { JobsQueryable: true }) doc.P("Not available.");
        else if (ja.Changes.Count == 0) doc.P("No significant changes — the same items ran with similar outcomes in both periods.");
        else doc.Bullets(ja.Changes.Select(c => $"{c.ChangeType}: {c.ItemName} — {c.Description}"));

        doc.H2($"{section}.3 Failure assessment");
        if (ja is not { JobsQueryable: true } || ja.Activities.Count == 0)
            doc.P(ja is { JobsQueryable: true } ? "No failed or warning jobs were recorded in the analysis window." : "Not available.");
        else
            foreach (var r in ja.Activities)
            {
                doc.P($"{r.Classification} — {r.ItemName} ({r.WorkloadType}, {r.Operation.ToLowerInvariant()})", bold: true, color: ClassificationColor(r.Classification));
                doc.P(r.Explanation);
                var evidence = new List<string>
                {
                    $"Item ID: {r.ItemId}",
                    $"Policy: {Fallback(r.PolicyName, "Unknown")}",
                    $"Jobs: {r.Count} total, {r.Failed} failed, {r.Succeeded} succeeded, {r.Warnings} with warnings; consecutive failures: {r.ConsecutiveFailures}",
                    $"Error code: {Fallback(r.ErrorCode, "None reported")}",
                    $"Last run (UTC): {Timestamp(r.LastRunTime)}",
                    $"Average duration: {r.AverageDuration?.ToString() ?? "Unknown"}"
                };
                if (!string.IsNullOrEmpty(r.ErrorMessage)) evidence.Add($"Error: {r.ErrorMessage}");
                if (!string.IsNullOrEmpty(r.AzureRecommendation)) evidence.Add($"Azure recommendation: {r.AzureRecommendation}");
                if (r.LastFailureTime is { } f) evidence.Add($"Last failure: {f:yyyy-MM-dd HH:mm} UTC");
                if (r.LastSuccessTime is { } s) evidence.Add($"Last success: {s:yyyy-MM-dd HH:mm} UTC");
                doc.Bullets(evidence);
                if (r.SampleJobIds.Count > 0)
                {
                    doc.P("Sample job IDs:");
                    doc.Bullets(r.SampleJobIds);
                }
            }

        doc.H2($"{section}.4 Remediation recommendations");
        var withRecs = ja is { JobsQueryable: true } ? ja.Activities.Where(r => r.Recommendations.Count > 0).ToList() : [];
        if (withRecs.Count == 0)
            doc.P(ja is { JobsQueryable: true } ? "No remediation recommendations arose from the analysed window." : "Not available.");
        else
            foreach (var r in withRecs)
            {
                doc.P($"{r.ItemName} ({r.Classification})", bold: true);
                doc.Bullets(r.Recommendations);
            }

        doc.H2($"{section}.5 Healthy items");
        if (ja is not { JobsQueryable: true }) doc.P("Not available.");
        else if (ja.HealthyItems.Count == 0) doc.P("No items with only successful jobs were reported.");
        else doc.Bullets(ja.HealthyItems);
    }

    private static string Timestamp(DateTimeOffset? value) => value is { } t ? $"{t.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC" : "Unknown";
    private static string Number(int? value, string unit) => value is { } n ? $"{n} {unit}" : "Unknown / not applicable";

    private static void AssessmentSections(ReportDoc doc, BackupScanResult? scan, int section)
    {
        doc.H1($"{section++}. Resource inventory and coverage");
        doc.Note(BackupAssessmentService.InventoryScope);
        if (scan is null) { doc.P("No scan data available."); return; }
        if (scan.IsSampleData) doc.Note("Sample estate: inventory, costs and requirements are illustrative, not live Azure data.");
        doc.P($"Resource Graph inventory read for {scan.InventorySubscriptionIds.Distinct(StringComparer.OrdinalIgnoreCase).Count()} " +
              $"of {scan.SubscriptionIds.Distinct(StringComparer.OrdinalIgnoreCase).Count()} selected subscriptions.");
        foreach (var warning in scan.Warnings.Distinct()) doc.P($"Scan warning: {warning}");
        doc.Table(["Resource / ARM ID", "Type / source SKU", "Subscription / group / location", "Environment", "Protection evidence"],
            scan.ResourceInventory.Select(r => new[]
            {
                $"{r.Name}\n{r.Id}", $"{r.ResourceType}\n{Fallback(r.StorageConfiguration, "SKU unknown / not applicable")}",
                $"{r.SubscriptionId}\n{r.ResourceGroup}\n{Fallback(r.Location, "location unknown")}", r.Environment,
                $"{r.Protection}; {r.BackupItemIds.Count} linked item(s)\n{string.Join("\n", r.BackupItemIds)}"
            }).ToList(), "Resource inventory unavailable or empty; protection coverage cannot be inferred.");

        doc.H1($"{section++}. Backup locations and storage");
        doc.Table(["Vault / ARM ID", "Primary backup region", "Storage redundancy", "Cross Region Restore"],
            scan.Vaults.Select(v => new[] { $"{v.Name}\n{v.Id}", Fallback(v.Location, "Unknown"),
                $"{v.RedundancyLabel}\n{string.Join("; ", v.StorageConfigurations)}", Flag(v.CrossRegionRestore) }).ToList(),
            "No vault configuration available.");
        doc.Note("Vault region describes vaulted backup location, not necessarily operational snapshots. Verify secondary region, actual recovery-point locations and storage tiers per workload. Source SKU is not backup redundancy.");
        foreach (var vault in scan.Vaults.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
        {
            doc.P($"{vault.Name} - full vault configuration", bold: true);
            doc.Table(null,
            [
                ["Vault ID", vault.Id],
                ["Vault product / SKU", $"{vault.KindLabel} / {Fallback(vault.SkuName, "Unknown")}"],
                ["Subscription / resource group", $"{vault.SubscriptionId} / {vault.ResourceGroup}"],
                ["Soft delete", $"{Fallback(vault.SoftDeleteState, "Unknown")}; retention: {Number(vault.SoftDeleteRetentionDays, "days")}"],
                ["Immutability", Fallback(vault.ImmutabilityState, "Unknown")],
                ["Multi-user authorization", Flag(vault.MultiUserAuthorization)],
                ["Public network access", Fallback(vault.PublicNetworkAccess, "Unknown")],
                ["Alerts for all job failures", Flag(vault.AlertsForAllJobFailures)],
                ["Azure secure score", Fallback(vault.SecureScore, "Unknown")],
                ["Diagnostics to Log Analytics", Flag(vault.DiagnosticsEnabled)],
                ["Diagnostic destinations", Fallback(string.Join("\n", vault.LogDestinations), "None reported")],
                ["Log Analytics workspace IDs", Fallback(string.Join("\n", vault.LogWorkspaceIds), "None reported")]
            ], "", firstColHeader: true);
        }

        doc.H1($"{section++}. Current backup costs");
        doc.Note(BackupAssessmentService.CostScope);
        doc.Table(["Subscription", "UTC billing period", "Availability"],
            scan.CostCoverage.Select(c => new[] { c.SubscriptionId, $"{c.FromUtc:yyyy-MM-dd} to {c.ToUtc:yyyy-MM-dd}",
                c.Available ? "Billing query complete" : $"Unavailable: {c.Error}" }).ToList(), "Billing data not collected.");
        foreach (var currency in scan.Costs.GroupBy(c => c.Currency))
            doc.P($"{currency.Key} {currency.Sum(c => c.Amount):N2} reported subtotal (available subscriptions only).", bold: true);
        doc.Table(["Subscription", "Billed resource ID", "Meter subcategory", "Actual pre-tax cost"],
            scan.Costs.Select(c => new[] { c.SubscriptionId, Fallback(c.ResourceId, "Unallocated"), c.Meter, $"{c.Currency} {c.Amount:N2}" }).ToList(),
            scan.CostCoverage.Any(c => c.Available)
                ? "No Backup-category charges returned for available subscriptions; this is not proof of zero total backup costs."
                : "Actual costs unavailable; no zero-cost assumption or estimate has been substituted.");

        doc.H1($"{section++}. Recommended policies by resource and environment");
        doc.Note("Proposed baselines, not approved requirements or universal Microsoft mandates. Vault grades use generic checks, not these environment-specific proposals.");
        doc.Table(["Resource / ID", "Environment / workload", "Proposed policy", "Recovery gaps"],
            BackupAssessmentService.Recommendations(scan).Select(r => new[]
            {
                $"{r.ResourceName}\n{r.ResourceId}", $"{r.Environment}\n{r.Workload}", r.Policy, r.RecoveryGap
            }).ToList(), "No resources available for policy recommendations.");

        doc.H1($"{section++}. Documented recovery requirements");
        foreach (var resource in scan.ResourceInventory)
        {
            var r = resource.Recovery;
            doc.P($"{resource.Name} ({resource.Id})", bold: true);
            doc.P($"Owner: {Fallback(r.Owner, "Not documented")}; required RPO: {Hours(r.RpoHours)}; " +
                  $"required RTO: {Hours(r.RtoHours)}; required retention: {(r.RetentionDays is { } d ? $"{d} days" : "Not documented")}.");
            doc.P($"Restore test evidence: {Fallback(r.RestoreTestEvidence, "Not documented")}");
            doc.P($"Dependencies, limitations and compliance notes: {Fallback(r.Notes, "Not documented")}");
        }
        if (scan.ResourceInventory.Count == 0) doc.P("Recovery requirements not documented.");

        doc.H1($"{section++}. Cost optimisation opportunities");
        doc.Bullets(BackupAssessmentService.CostOpportunities(scan));
        doc.H1($"{section}. Recovery and assessment limitations");
        doc.Bullets(BackupAssessmentService.Limitations);
        doc.P($"Microsoft backup best practices: {BackupAssessmentService.BestPracticesUrl}");
        doc.P($"Workload support matrix: {BackupAssessmentService.SupportMatrixUrl}");
    }

    private static string Hours(int? value) => value is { } h ? $"{h} hours" : "Not documented";

    private static void DocInfo(ReportDoc doc, BackupReportContext ctx) =>
        doc.Table(null,
        [
            ["Customer", Fallback(ctx.CustomerName, "Unknown")],
            ["Prepared by", Fallback(ctx.ReportAuthor, "RateMyAzureBackups")],
            ["Scan date", ctx.ScanDate.ToString("yyyy-MM-dd HH:mm")],
            ["Tenant", Fallback(ctx.TenantName, "Unknown")],
            ["Subscriptions in scope", ctx.SubscriptionLines.Count > 0 ? string.Join("\n", ctx.SubscriptionLines) : "None"],
            ["Generated by", $"RateMyAzureBackups — {ctx.Flow.Label()} backup assessment (read-only)"],
        ], "", firstColHeader: true);

    private static void RatingSection(ReportDoc doc, BackupReportContext ctx)
    {
        var ratings = ctx.Scan?.Ratings ?? [];
        var product = ctx.Flow.Label();
        if (ratings.Count == 0)
        {
            doc.P(ctx.Scan is null ? "No scan data available." : $"No {ctx.Flow.PluralLabel()} in scope — no rating produced.");
            doc.Legend();
            return;
        }

        var worst = BackupRatingService.Worst(ratings)!;
        var avg = (int)Math.Round(ratings.Average(r => r.Score));
        doc.Grade(worst.Grade, worst.GradeLabel, ratings.Count == 1
            ? $"{product} '{worst.TargetName}' scores {worst.Score}/100." + (worst.CapReason is null ? "" : $" Grade capped: {worst.CapReason}")
            : $"Set by the weakest {product} '{worst.TargetName}' ({worst.Score}/100). Average score {avg}/100 across {ratings.Count} {ctx.Flow.PluralLabel()}.");

        var rows = new List<string[]>();
        var colors = new List<string?[]>();
        foreach (var r in ratings.OrderBy(r => r.TargetName, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add([r.TargetName, $"{r.Grade} — {r.GradeLabel}", $"{r.Score}/100", r.CapReason ?? r.Improvements.FirstOrDefault()?.Action ?? "No gaps identified."]);
            var c = BackupRatingService.ColorForGrade(r.Grade);
            colors.Add([null, c, c, null]);
        }
        doc.Table([product, "Grade", "Score", "Biggest gap"], rows, "", colors);

        doc.P("Scoring model: Protection health 30 · Backup freshness 20 · Redundancy & restore 20 · Vault security 15 · Policy hygiene 10 · Monitoring & alerts 5. " +
              "Grade bands: A ≥ 90, B ≥ 75, C ≥ 60, D ≥ 45, E ≥ 30, F < 30. Caps: nothing protected = F; items stopped or in error = max D; last backup failed = max C; soft delete off = max C; locally redundant storage = max B.");
        doc.Legend();

        var any = false;
        foreach (var r in ratings.OrderByDescending(r => r.Grade, StringComparer.Ordinal).ThenBy(r => r.TargetName, StringComparer.OrdinalIgnoreCase))
        {
            doc.P($"{r.TargetName} (grade {r.Grade}, {r.Score}/100)", bold: true, color: BackupRatingService.ColorForGrade(r.Grade));
            doc.Table(["Category", "Score", "Evidence"],
                r.Categories.Select(c => new[] { c.Name, $"{c.Earned}/{c.Possible}", string.Join("\n", c.Details) }).ToList(),
                "No category breakdown available.");
            if (r.Improvements.Count == 0) continue;
            any = true;
            doc.Bullets(r.Improvements.Select(i => $"+{i.PointsGained} pts — {i.Action}" +
                (BackupRatingService.WorstGrade(i.ResultingGrade, r.Grade) != i.ResultingGrade ? $" (raises the grade to {i.ResultingGrade})" : "")));
        }
        if (!any) doc.P($"No improvements required — every {product} in scope earns full marks.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string Fallback(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;
    private static string Count(BackupScanResult? scan, int count) => scan is null ? "—" : count.ToString();
    private static string Flag(bool? value) => value switch { true => "Enabled", false => "Disabled", null => "Unknown" };
    private static string Capitalize(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    public static string SeverityColor(string severity) => severity switch
    {
        "High" => "#D32F2F",
        "Medium" => "#F57C00",
        _ => "#1976D2"
    };

    public static string ClassificationColor(string classification) => classification switch
    {
        "Persistent failure" => "#D32F2F",
        "Transient failure" => "#F57C00",
        _ => "#1976D2"
    };

    private static string Classified(BackupJobAnalysis ja)
    {
        if (ja.Activities.Count == 0) return "";
        var parts = ja.Activities.GroupBy(r => r.Classification)
            .OrderBy(g => BackupJobAnalysisService.ClassificationOrder(g.Key))
            .Select(g => $"{g.Count()} item(s) classified '{g.Key}'");
        return $"Assessment: {string.Join(", ", parts)}.";
    }
}
