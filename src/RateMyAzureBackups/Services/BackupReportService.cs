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
    public List<SubscriptionInfo> Subscriptions { get; init; } = new();
    public BackupScanResult? Scan { get; init; }
    public BackupJobAnalysis? JobAnalysis { get; init; }
}

public enum BackupReportType { Summary, Detailed, Extensive }

/// <summary>Builds concise customer reports and an extensive findings report for one flow.</summary>
public static class BackupReportService
{
    public static byte[] SummaryDocx(BackupReportContext ctx) => DocxReportRenderer.Render(BuildSummary(ctx));
    public static byte[] DetailedDocx(BackupReportContext ctx) => DocxReportRenderer.Render(BuildDetailed(ctx));
    public static byte[] ExtensiveDocx(BackupReportContext ctx) => DocxReportRenderer.Render(BuildFindings(ctx));
    public static string SummaryHtml(BackupReportContext ctx) => HtmlReportRenderer.Render(BuildSummary(ctx));
    public static string DetailedHtml(BackupReportContext ctx) => HtmlReportRenderer.Render(BuildDetailed(ctx));
    public static string ExtensiveHtml(BackupReportContext ctx) => HtmlReportRenderer.Render(BuildFindings(ctx));
    public static string SummaryMarkdown(BackupReportContext ctx) => MarkdownReportRenderer.Render(BuildSummary(ctx));
    public static string DetailedMarkdown(BackupReportContext ctx) => MarkdownReportRenderer.Render(BuildDetailed(ctx));
    public static string ExtensiveMarkdown(BackupReportContext ctx) => MarkdownReportRenderer.Render(BuildFindings(ctx));

    public static byte[] FindingsDocx(BackupReportContext ctx) => ExtensiveDocx(ctx);
    public static string FindingsHtml(BackupReportContext ctx) => ExtensiveHtml(ctx);
    public static string FindingsMarkdown(BackupReportContext ctx) => ExtensiveMarkdown(ctx);

    public static string FileName(BackupReportContext ctx, bool findings, bool markdown = false)
        => FileName(ctx, findings ? BackupReportType.Extensive : BackupReportType.Summary, markdown);

    public static string FileName(BackupReportContext ctx, BackupReportType reportType, bool markdown = false)
    {
        var scope = ctx.Flow == BackupFlowKind.RecoveryServices ? "RecoveryServices" : "BackupVault";
        var customer = string.Concat((ctx.CustomerName ?? "").Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
        if (string.IsNullOrEmpty(customer)) customer = "Backup";
        return $"{customer}-{scope}-Backup-{reportType}-{ctx.ScanDate:yyyyMMdd}.{(markdown ? "md" : "docx")}";
    }

    // ── Summary report ───────────────────────────────────────────────────────

    public static ReportDoc BuildSummary(BackupReportContext ctx)
    {
        var scan = ctx.Scan;
        var findings = scan?.Findings ?? [];
        var vaults = scan?.Vaults ?? [];
        var items = scan?.AllItems.ToList() ?? [];
        var ratings = scan?.Ratings ?? [];
        var orderedFindings = findings
            .OrderBy(f => BackupFindingsBuilder.SeverityOrder(f.Severity))
            .ThenBy(f => f.ResourceName, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();

        var doc = new ReportDoc { Title = $"{ctx.Flow.Label()}s — Backup Summary Report" };
        SubscriptionSection(doc, ctx);
        doc.H1("Executive overview");
        if (scan is null)
        {
            doc.P("No scan data available.");
        }
        else
        {
            var high = findings.Count(f => f.Severity == "High");
            var medium = findings.Count(f => f.Severity == "Medium");
            var info = findings.Count(f => f.Severity == "Info");
            var subscriptionCount = ReportSubscriptions(ctx).Count;
            doc.P($"This read-only assessment covers {subscriptionCount} subscription(s), {vaults.Count} {ctx.Flow.PluralLabel()}, " +
                  $"{items.Count} protected workload(s) and {scan.Policies.Count} backup polic{(scan.Policies.Count == 1 ? "y" : "ies")}. " +
                  $"It identified {findings.Count} issue(s): {high} high, {medium} medium and {info} informational.");

            if (ratings.Count > 0)
            {
                var weakest = BackupRatingService.Worst(ratings)!;
                var average = (int)Math.Round(ratings.Average(r => r.Score));
                doc.Grade(weakest.Grade, weakest.GradeLabel,
                    ratings.Count == 1
                        ? $"{weakest.TargetName} scores {weakest.Score}/100."
                        : $"The overall grade reflects the weakest-rated vault, {weakest.TargetName} ({weakest.Score}/100); average score across {ratings.Count} vault(s) is {average}/100.");
            }

            var overviewRows = new List<string[]>
            {
                new[] { "Vaults in scope", vaults.Count.ToString() },
                new[] { "Protected workloads", items.Count.ToString() },
                new[] { "Protection stopped or in error", items.Count(i => !i.IsActive).ToString() },
                new[] { "Most recent backup failed", items.Count(i => i.IsActive && i.LastBackupSucceeded == false).ToString() },
                new[] { "Backup policies", scan.Policies.Count.ToString() },
            };
            if (ctx.Flow == BackupFlowKind.RecoveryServices)
                overviewRows.Insert(2, new[] { "Virtual machines without backup", $"{scan.UnprotectedResources.Count} of {scan.VirtualMachineCount}" });
            doc.Table(["Coverage check", "Result"], overviewRows, "No scan data available.");

            doc.H2("Priority findings and next steps");
            doc.P($"Showing the {orderedFindings.Count} highest-priority finding(s) of {findings.Count}; start with High severity items.");
            doc.Table(["#", "Priority", "Area", "Finding", "Suggested next step"],
                orderedFindings.Select((f, index) => new[]
                {
                    (index + 1).ToString(), f.Severity, f.ResourceName, f.Message, SummaryAction(f.Category)
                }).ToList(),
                "No findings — the backup estate matches the checks performed.",
                orderedFindings.Select(f => new string?[] { null, SeverityColor(f.Severity), null, null, null }).ToList());
        }
        return doc;
    }

    // ── Detailed quick-reference report ──────────────────────────────────────

    public static ReportDoc BuildDetailed(BackupReportContext ctx)
    {
        var scan = ctx.Scan;
        var product = ctx.Flow.Label();
        var items = scan?.AllItems.ToList() ?? [];
        var findings = scan?.Findings ?? [];
        var doc = new ReportDoc { Title = $"{ctx.Flow.Label()}s — Detailed Backup Overview" };

        SubscriptionSection(doc, ctx);
        doc.H1("1. Assessment overview");
        DocInfo(doc, ctx);
        if (scan is null)
        {
            doc.P("No scan data available.");
            return doc;
        }

        doc.P($"Assessment covers {ReportSubscriptions(ctx).Count} subscription(s), {scan.Vaults.Count} {ctx.Flow.PluralLabel()}, " +
              $"{items.Count} protected workload(s), {scan.Policies.Count} polic{(scan.Policies.Count == 1 ? "y" : "ies")} " +
              $"and {findings.Count} finding(s).");
        AddRatingOverview(doc, scan, product);

        doc.H1("2. Priority findings");
        var prioritized = findings.OrderBy(f => BackupFindingsBuilder.SeverityOrder(f.Severity))
            .ThenBy(f => f.ResourceName, StringComparer.OrdinalIgnoreCase).Take(10).ToList();
        doc.Table(["Priority", "Resource", "Finding", "Next step"],
            prioritized.Select(f => new[] { f.Severity, f.ResourceName, f.Message, SummaryAction(f.Category) }).ToList(),
            "No findings — the backup estate matches the checks performed.",
            prioritized.Select(f => new string?[] { SeverityColor(f.Severity), null, null, null }).ToList());

        doc.H1("3. Backup policy inventory");
        doc.Table(["Policy", "Workload", "Schedule", "Retention", "Protected items"],
            scan.Policies.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .Select(p => new[] { p.Name, p.WorkloadType, Fallback(p.ScheduleSummary, "Unknown"), p.RetentionSummary, p.ProtectedItemCount.ToString() })
                .ToList(), "No backup policies in scope.");
        if (scan.Policies.Count > 10) doc.P($"Showing 10 of {scan.Policies.Count} policies; see the Extensive report for the full inventory.");

        doc.H1("4. Protected item coverage");
        var vaultRows = scan.Vaults.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).Take(10).Select(v => new[]
        {
            v.Name, v.Items.Count.ToString(), v.Items.Count(i => i.IsActive).ToString(),
            v.Items.Count(i => !i.IsActive).ToString(),
            v.Items.Count(i => i.IsActive && i.LastBackupSucceeded == false).ToString()
        }).ToList();
        doc.Table(["Vault", "Items", "Active", "Stopped / error", "Last backup failed"], vaultRows,
            $"No {ctx.Flow.PluralLabel()} in scope.");
        if (scan.Vaults.Count > 10) doc.P($"Showing 10 of {scan.Vaults.Count} vaults; see the Extensive report for item-level detail.");
        if (ctx.Flow == BackupFlowKind.RecoveryServices)
            doc.P($"{scan.UnprotectedResources.Count} of {scan.VirtualMachineCount} virtual machines were identified as not protected by a Recovery Services vault in scope.");

        doc.H1("5. Backup job analysis");
        AddJobOverview(doc, ctx.JobAnalysis);

        doc.H1("6. Resource inventory and coverage");
        var inventoryItems = scan.ResourceInventory.Select(r => BackupAssessmentService.ItemsFor(scan, r)).ToList();
        var inventorySubscriptionCount = scan.InventorySubscriptionIds.Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var selectedSubscriptionCount = scan.SubscriptionIds.Distinct(StringComparer.OrdinalIgnoreCase).Count();
        doc.P($"Inventory data was available for {inventorySubscriptionCount} of {selectedSubscriptionCount} selected subscription(s). " +
              $"{scan.ResourceInventory.Count} resource(s) were reviewed; " +
              $"{inventoryItems.Count(itemsForResource => itemsForResource.Count > 0)} have linked backup item evidence.");
        if (scan.IsSampleData) doc.Note("Inventory and cost figures are illustrative sample data, not live Azure data.");
        doc.Note("Resource inventory and backup associations are limited to the selected subscriptions and this vault flow.");

        doc.H1("7. Backup locations and storage");
        doc.Table(["Vault", "Region", "Redundancy", "Cross-region restore", "Soft delete", "Immutability"],
            scan.Vaults.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).Select(v => new[]
            {
                v.Name, Fallback(v.Location, "Unknown"), v.RedundancyLabel, Flag(v.CrossRegionRestore),
                Fallback(v.SoftDeleteState, "Unknown"), Fallback(v.ImmutabilityState, "Unknown")
            }).ToList(), "No vault configuration available.");

        doc.H1("8. Current backup costs");
        if (scan.CostCoverage.Count == 0)
            doc.P("Billing data was not collected; no cost estimate is included.");
        else
        {
            doc.P($"{scan.CostCoverage.Count(c => c.Available)} of {scan.CostCoverage.Count} subscription billing queries completed. " +
                  "Unavailable billing data is not treated as zero.");
            foreach (var currency in scan.Costs.GroupBy(c => c.Currency))
                doc.P($"{currency.Key} {currency.Sum(c => c.Amount):N2} reported subtotal.", bold: true);
        }

        doc.H1("9. Recommended policies by resource and environment");
        var recommendations = BackupAssessmentService.Recommendations(scan);
        doc.Table(["Resource", "Environment", "Workload", "Policy direction"],
            recommendations.Take(10).Select(r => new[]
            {
                r.ResourceName, r.Environment, r.Workload, FirstSentence(r.Policy)
            }).ToList(), "No inventory resources available for policy recommendations.");
        if (recommendations.Count > 10) doc.P($"Showing 10 of {recommendations.Count} recommendations; see the Extensive report for full guidance.");

        doc.H1("10. Documented recovery requirements");
        var documented = scan.ResourceInventory.Where(r => r.Recovery.RpoHours is not null
            || r.Recovery.RtoHours is not null || r.Recovery.RetentionDays is not null).ToList();
        doc.Table(["Resource", "Owner", "RPO", "RTO", "Retention"],
            documented.Take(10).Select(r => new[]
            {
                r.Name, Fallback(r.Recovery.Owner, "Not documented"), Hours(r.Recovery.RpoHours),
                Hours(r.Recovery.RtoHours), r.Recovery.RetentionDays is { } days ? $"{days} days" : "Not documented"
            }).ToList(), "Recovery targets are not documented in this assessment.");
        if (documented.Count > 10) doc.P($"Showing 10 of {documented.Count} resources with documented recovery targets.");
        doc.P("Restore testing is required to validate that recovery targets can be met.");

        doc.H1("11. Cost optimisation opportunities");
        doc.Bullets(BackupAssessmentService.CostOpportunities(scan).Take(3).Select(FirstSentence));
        doc.P("No savings are quantified; validate costs and recovery requirements before changing protection.");

        doc.H1("12. Recovery and assessment limitations");
        doc.Bullets([
            "A configured backup schedule and successful job do not prove that recovery objectives can be met; test restores.",
            "Inventory and vault associations cover the selected subscriptions and this backup flow; native, cross-scope and third-party protection may not be represented.",
            "See the Extensive report for full assessment scope, caveats, evidence and resource-level detail."
        ]);
        return doc;
    }

    // ── Extensive findings report ────────────────────────────────────────────

    public static ReportDoc BuildFindings(BackupReportContext ctx)
    {
        var product = ctx.Flow.Label();
        var scan = ctx.Scan;
        var ja = ctx.JobAnalysis;
        var doc = new ReportDoc { Title = $"{product}s — Extensive Backup Findings" };

        SubscriptionSection(doc, ctx);
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

    private static void AddRatingOverview(ReportDoc doc, BackupScanResult scan, string product)
    {
        if (scan.Ratings.Count == 0)
        {
            doc.P($"No {product} ratings available.");
            return;
        }

        var worst = BackupRatingService.Worst(scan.Ratings)!;
        doc.Grade(worst.Grade, worst.GradeLabel,
            $"Weakest-rated vault: {worst.TargetName} ({worst.Score}/100).");
        doc.Table([product, "Grade", "Score", "Main improvement"],
            scan.Ratings.OrderBy(r => r.TargetName, StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .Select(r => new[]
                {
                    r.TargetName, $"{r.Grade} — {r.GradeLabel}", $"{r.Score}/100",
                    r.CapReason ?? r.Improvements.FirstOrDefault()?.Action ?? "No priority gap identified."
                }).ToList(), "No vault ratings available.");
        if (scan.Ratings.Count > 10) doc.P($"Showing 10 of {scan.Ratings.Count} vault ratings.");
    }

    private static void AddJobOverview(ReportDoc doc, BackupJobAnalysis? analysis)
    {
        if (analysis is null)
        {
            doc.P("No job analysis has been run.");
            return;
        }
        if (!analysis.JobsQueryable)
        {
            doc.P($"Jobs for '{analysis.TargetName}' could not be queried: {analysis.QueryError}");
            return;
        }

        doc.P($"'{analysis.TargetName}' over {analysis.TimeRangeLabel}: {analysis.TotalJobs} job(s) across {analysis.DistinctItems} item(s), " +
              $"{analysis.CompletedCount} completed, {analysis.WarningCount} with warnings, {analysis.FailedCount} failed " +
              $"({analysis.SuccessRate}% success).");
        if (analysis.Activities.Count > 0)
            doc.Bullets(analysis.Activities.Take(5).Select(activity =>
                $"{activity.Classification}: {activity.ItemName} — {activity.Failed} failed job(s). " +
                FirstSentence(Fallback(activity.Recommendations.FirstOrDefault() ?? activity.AzureRecommendation, "Review recent job history."))));
        if (analysis.Activities.Count > 5) doc.P($"Showing 5 of {analysis.Activities.Count} job activities.");
        else
            doc.P("No failed or warning job activity was reported.");
    }

    private static string FirstSentence(string text)
    {
        var end = text.IndexOf(". ", StringComparison.Ordinal);
        return end < 0 ? text : text[..(end + 1)];
    }

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
            ["Subscriptions in scope", ReportSubscriptions(ctx).Count.ToString()],
            ["Generated by", $"RateMyAzureBackups — {ctx.Flow.Label()} backup assessment (read-only)"],
        ], "", firstColHeader: true);

    private static void SubscriptionSection(ReportDoc doc, BackupReportContext ctx)
    {
        doc.H1("Subscriptions in scope");
        doc.Table(["Subscription name", "Subscription ID"],
            ReportSubscriptions(ctx).Select(subscription => new[]
            {
                Fallback(subscription.DisplayName, "Name not available"),
                Fallback(subscription.SubscriptionId, "ID not available")
            }).ToList(),
            "No subscription information available.");
    }

    private static List<SubscriptionInfo> ReportSubscriptions(BackupReportContext ctx) =>
        ctx.Subscriptions.Count > 0
            ? ctx.Subscriptions
            : ctx.Scan?.SubscriptionIds.Select(id => new SubscriptionInfo
            {
                DisplayName = "Name not available",
                SubscriptionId = id
            }).ToList() ?? [];

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

    private static string SummaryAction(string category) => category switch
    {
        "Empty vault" => "Assign workloads to the vault or retire it if it is no longer needed.",
        "Protection error" => "Investigate the affected items, resolve the reported cause, then run a backup.",
        "Protection stopped" => "Resume protection with an appropriate backup policy.",
        "Backup failed" => "Resolve the latest job failure and confirm a successful backup.",
        "Stale backup" => "Check the schedule and recent jobs, then create a fresh recovery point.",
        "Locally redundant" => "Plan a move to geo-redundant storage for regional resilience.",
        "No cross-region restore" => "Enable Cross Region Restore where regional recovery is required.",
        "Soft delete disabled" => "Enable soft delete to help recover deleted recovery points.",
        "Immutability off" => "Enable and lock immutability to protect recovery points from deletion.",
        "No MUA" => "Enable multi-user authorization for sensitive vault operations.",
        "Public network access" => "Restrict public access and use private connectivity where possible.",
        "Job alerts disabled" => "Enable backup job failure alerts and route them to an owner.",
        "No diagnostics" => "Send backup diagnostics to a Log Analytics workspace.",
        "Unused policy" => "Assign the policy to workloads or remove it if it is obsolete.",
        "Weekly schedule" => "Increase backup frequency to meet the required recovery point objective.",
        "Short retention" => "Extend retention to meet recovery and compliance requirements.",
        "No long-term retention" => "Add long-term retention if required by recovery or compliance needs.",
        "Unprotected VM" => "Enable backup using a suitable vault and policy.",
        _ => "Review this configuration and align it with recovery requirements."
    };

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
