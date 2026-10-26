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

    public static string FileName(BackupReportContext ctx, bool findings)
    {
        var scope = ctx.Flow == BackupFlowKind.RecoveryServices ? "RecoveryServices" : "BackupVault";
        var customer = string.Concat((ctx.CustomerName ?? "").Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
        if (string.IsNullOrEmpty(customer)) customer = "Backup";
        return $"{customer}-{scope}-Backup-{(findings ? "Findings" : "Summary")}-{ctx.ScanDate:yyyyMMdd}.docx";
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
        doc.Table(["Severity", "Category", "Finding", "Resource"],
            findings.OrderBy(f => BackupFindingsBuilder.SeverityOrder(f.Severity)).Select(f => new[] { f.Severity, f.Category, f.Message, f.ResourceName }).ToList(),
            scan is null ? "No scan data available." : "No findings — the backup estate matches the checks performed.",
            findings.Select(f => new string?[] { SeverityColor(f.Severity), null, null, null }).ToList());

        doc.H1("6. Vault configuration");
        doc.Table(["Vault", "Redundancy", "Cross-region restore", "Soft delete", "Immutability", "MUA", "Job alerts"],
            vaults.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).Select(v => new[]
            {
                v.Name, v.RedundancyLabel, Flag(v.CrossRegionRestore), Fallback(v.SoftDeleteState, "Unknown"),
                Fallback(v.ImmutabilityState, "Disabled"), Flag(v.MultiUserAuthorization), Flag(v.AlertsForAllJobFailures)
            }).ToList(),
            scan is null ? "No scan data available." : $"No {ctx.Flow.PluralLabel()} in scope.");

        doc.H1("7. Policy compliance");
        doc.Table(["Policy", "Vault", "Workload", "Schedule", "Retention", "Items"],
            policies.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(p => new[]
            {
                p.Name, Azure.Json.LastSegment(p.VaultId), p.WorkloadType, Fallback(p.ScheduleSummary, "Unknown"), p.RetentionSummary, p.ProtectedItemCount.ToString()
            }).ToList(),
            scan is null ? "No scan data available." : "No backup policies found.");

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
                doc.Bullets(group.Select(f => $"{f.Category}: {f.Message} — {f.ResourceName}. Fix: {f.Fix}"));
            }

        doc.H1("4. Backup policy inventory");
        if (scan is null) doc.P("No scan data available.");
        else if (scan.Policies.Count == 0) doc.P("No backup policies in scope.");
        else
            foreach (var p in scan.Policies.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                doc.P($"{p.Name} ({p.WorkloadType}, vault {Azure.Json.LastSegment(p.VaultId)})", bold: true);
                var settings = new List<string>
                {
                    $"Schedule: {Fallback(p.ScheduleSummary, "Unknown")}",
                    $"Retention: {p.RetentionSummary}",
                    $"Protected items: {p.ProtectedItemCount}"
                };
                if (p.InstantRpRetentionDays is { } rp) settings.Add($"Instant restore snapshots: {rp} day(s)");
                if (p.LogBackupFrequencyMinutes is { } lf) settings.Add($"Log backups every {lf} min{(p.LogRetentionDays is { } lr ? $", kept {lr} days" : "")}");
                if (!string.IsNullOrEmpty(p.TimeZone)) settings.Add($"Time zone: {p.TimeZone}");
                doc.P(string.Join(" • ", settings));
            }

        doc.H1("5. Protected items");
        if (scan is null) doc.P("No scan data available.");
        else
        {
            foreach (var v in scan.Vaults.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
            {
                doc.P($"{v.Name} ({v.KindLabel}, {v.RedundancyLabel}, {v.Location}) — {v.Items.Count} {(v.Items.Count == 1 ? ctx.Flow.ItemLabel() : ctx.Flow.ItemPluralLabel())}", bold: true);
                if (v.Items.Count == 0) { doc.P("Nothing is protected by this vault."); continue; }
                doc.Bullets(v.Items.Select(i =>
                    $"{i.Display} ({i.WorkloadType}) — {Fallback(i.ProtectionState, "state unknown")}, last backup {Fallback(i.LastBackupStatus, "unknown")}" +
                    (i.LastBackupTime is { } t ? $" at {t:yyyy-MM-dd HH:mm} UTC" : "") +
                    (string.IsNullOrEmpty(i.PolicyName) ? "" : $", policy {i.PolicyName}") +
                    (string.IsNullOrEmpty(i.ErrorCode) ? "" : $", error {i.ErrorCode}")));
            }
            if (scan.Vaults.Count == 0) doc.P($"No {ctx.Flow.PluralLabel()} in scope.");

            if (ctx.Flow == BackupFlowKind.RecoveryServices)
            {
                doc.H2("5.1 Virtual machines without backup");
                if (scan.UnprotectedResources.Count == 0)
                    doc.P(scan.VirtualMachineCount == 0 ? "No virtual machines in scope." : $"All {scan.VirtualMachineCount} virtual machine(s) in scope are backed up.");
                else
                    doc.Bullets(scan.UnprotectedResources.Select(r => $"{r.Name} ({r.ResourceGroup}, {r.Location}{(string.IsNullOrEmpty(r.PowerState) ? "" : $", {r.PowerState}")})"));
            }
        }

        doc.H1("6. Backup job analysis");
        doc.P("Scope: " + (ja is null
            ? "no job analysis has been run"
            : $"{product} '{ja.TargetName}', {ja.TimeRangeLabel}"));

        doc.H2("6.1 Activity summary");
        if (ja is null) doc.P("No job analysis available — run one from the Jobs step.");
        else if (!ja.JobsQueryable) doc.P($"Backup jobs could not be queried: {ja.QueryError}");
        else
        {
            doc.P($"{ja.TotalJobs} job(s) in the {ja.TimeRangeLabel}: {ja.CompletedCount} completed, {ja.WarningCount} completed with warnings, " +
                  $"{ja.FailedCount} failed, {ja.CancelledCount} cancelled and {ja.InProgressCount} in progress, across {ja.DistinctItems} distinct item(s) ({ja.SuccessRate}% success). {Classified(ja)}");
            foreach (var w in ja.Warnings) doc.P($"Warning: {w}");
        }

        doc.H2("6.2 Changes versus the previous period");
        if (ja is not { JobsQueryable: true }) doc.P("Not available.");
        else if (ja.Changes.Count == 0) doc.P("No significant changes — the same items ran with similar outcomes in both periods.");
        else doc.Bullets(ja.Changes.Select(c => $"{c.ChangeType}: {c.ItemName} — {c.Description}"));

        doc.H2("6.3 Failure assessment");
        if (ja is not { JobsQueryable: true } || ja.Activities.Count == 0)
            doc.P(ja is { JobsQueryable: true } ? "No failed or warning jobs were recorded in the analysis window." : "Not available.");
        else
            foreach (var r in ja.Activities)
            {
                doc.P($"{r.Classification} — {r.ItemName} ({r.WorkloadType}, {r.Operation.ToLowerInvariant()})", bold: true, color: ClassificationColor(r.Classification));
                doc.P(r.Explanation);
                var evidence = new List<string> { $"Jobs: {r.Count} total, {r.Failed} failed, {r.Succeeded} succeeded, {r.Warnings} with warnings" };
                if (!string.IsNullOrEmpty(r.ErrorMessage)) evidence.Add($"Error: {r.ErrorMessage}");
                if (r.LastFailureTime is { } f) evidence.Add($"Last failure: {f:yyyy-MM-dd HH:mm} UTC");
                if (r.LastSuccessTime is { } s) evidence.Add($"Last success: {s:yyyy-MM-dd HH:mm} UTC");
                doc.Bullets(evidence);
            }

        doc.H2("6.4 Remediation recommendations");
        var withRecs = ja is { JobsQueryable: true } ? ja.Activities.Where(r => r.Recommendations.Count > 0).ToList() : [];
        if (withRecs.Count == 0)
            doc.P(ja is { JobsQueryable: true } ? "No remediation recommendations arose from the analysed window." : "Not available.");
        else
            foreach (var r in withRecs)
            {
                doc.P($"{r.ItemName} ({r.Classification})", bold: true);
                doc.Bullets(r.Recommendations);
            }

        return doc;
    }

    // ── Shared sections ──────────────────────────────────────────────────────

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
            if (r.Improvements.Count == 0) continue;
            any = true;
            doc.P($"{r.TargetName} (grade {r.Grade}, {r.Score}/100)", bold: true, color: BackupRatingService.ColorForGrade(r.Grade));
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
