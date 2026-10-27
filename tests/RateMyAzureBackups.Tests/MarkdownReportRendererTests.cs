using RateMyAzureBackups.Models;
using RateMyAzureBackups.Services;
using RateMyAzureBackups.Services.Reports;
using Xunit;

namespace RateMyAzureBackups.Tests;

public class MarkdownReportRendererTests
{
    [Fact]
    public void RendersEveryReportBlockAsGitHubMarkdown()
    {
        var doc = new ReportDoc { Title = "Backup report" };
        doc.H1("Overview").H2("Details").P("Important", bold: true)
            .Note("First line\nSecond line")
            .Bullets(["One", "Two\nContinued"])
            .Table(["Resource", "State"], [["vm", "Protected"]], "Empty")
            .Table(null, [["Owner", "Platform team"]], "", firstColHeader: true)
            .Table(["Empty"], [], "No data")
            .Grade("B", "Good", "Score 81/100")
            .Legend();
        var markdown = MarkdownReportRenderer.Render(doc);
        Assert.StartsWith("# Backup report", markdown);
        Assert.Contains("## Overview", markdown);
        Assert.Contains("### Details", markdown);
        Assert.Contains("**Important**", markdown);
        Assert.Contains("> First line\n> Second line", markdown.Replace("\r\n", "\n"));
        Assert.Contains("- Two\n  Continued", markdown.Replace("\r\n", "\n"));
        Assert.Contains("| Resource | State |", markdown);
        Assert.Contains("| --- | --- |", markdown);
        Assert.Contains("| vm | Protected |", markdown);
        Assert.Contains("| Setting | Value |", markdown);
        Assert.Contains("| **Owner** | Platform team |", markdown);
        Assert.Contains("No data", markdown);
        Assert.Contains("**B \\- Good**", markdown);
        Assert.Contains("Score 81/100", markdown);
        foreach (var grade in BackupRatingService.Legend)
            Assert.Contains($"| {grade.Grade} |", markdown);
    }

    [Fact]
    public void EscapesResourceTextAndPreservesMultilineTableCells()
    {
        var doc = new ReportDoc { Title = "# title" };
        doc.Table(["Name", "Details"],
            [["vm|name", "first\r\nsecond\rthird\n<script>alert(1)</script>"],
             ["[link](https://example.com) *bold* `code` ~strike~", @"C:\path & data"]], "");
        var markdown = MarkdownReportRenderer.Render(doc);
        Assert.StartsWith("# \\# title", markdown);
        Assert.Contains(@"vm\|name", markdown);
        Assert.Contains("first<br>second<br>third<br>&lt;script&gt;", markdown);
        Assert.Contains(@"\[link\]\(https://example\.com\) \*bold\* \`code\` \~strike\~", markdown);
        Assert.Contains(@"C:\\path &amp; data", markdown);
        Assert.DoesNotContain("<script>", markdown);
    }

    [Theory]
    [InlineData(BackupFlowKind.RecoveryServices, false)]
    [InlineData(BackupFlowKind.RecoveryServices, true)]
    [InlineData(BackupFlowKind.BackupVault, false)]
    [InlineData(BackupFlowKind.BackupVault, true)]
    public void ExportsFullReportForEachFlowAndReportType(BackupFlowKind flow, bool findings)
    {
        var scan = DemoDataService.Scan(flow, [DemoDataService.SubProd, DemoDataService.SubShared]);
        scan.ResourceInventory.Last().Recovery.Notes = "Final resource recovery notes";
        var ctx = new BackupReportContext { Flow = flow, Scan = scan, CustomerName = "Customer", ScanDate = new DateTime(2026, 10, 26) };
        var markdown = findings ? BackupReportService.FindingsMarkdown(ctx) : BackupReportService.SummaryMarkdown(ctx);
        Assert.StartsWith($"# {flow.Label()}s", markdown);
        Assert.Contains(findings ? "Detailed Backup Findings" : "Backup Summary Report", markdown);
        foreach (var heading in new[] { "Resource inventory and coverage", "Backup locations and storage", "Current backup costs",
            "Recommended policies by resource and environment", "Documented recovery requirements", "Cost optimisation opportunities",
            "Recovery and assessment limitations", "Final resource recovery notes", "GBP" })
            Assert.Contains(heading, markdown);
        Assert.Contains("| Setting | Value |", markdown);
        Assert.Contains("Source resource ID", markdown);
        Assert.EndsWith(findings ? "-Findings-20261026.md" : "-Summary-20261026.md",
            BackupReportService.FileName(ctx, findings, markdown: true));
        Assert.EndsWith(".docx", BackupReportService.FileName(ctx, findings));
    }

    [Fact]
    public void ReportsWithoutScanStillExportMarkdown()
    {
        var ctx = new BackupReportContext();
        Assert.Contains("No scan data", BackupReportService.SummaryMarkdown(ctx));
        Assert.Contains("No scan data", BackupReportService.FindingsMarkdown(ctx));
    }
}
