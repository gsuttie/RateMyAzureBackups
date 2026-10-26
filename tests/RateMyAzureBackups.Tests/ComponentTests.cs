using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RateMyAzureBackups.Components.Shared;
using RateMyAzureBackups.Models;
using RateMyAzureBackups.Services;
using RateMyAzureBackups.Services.Azure;
using System.Text.Json;
using Xunit;

namespace RateMyAzureBackups.Tests;

/// <summary>Rendering tests for the UI components, driven by the sample estate (no Azure).</summary>
public class ComponentTests : BunitContext
{
    /// <summary>An IAzureApi that must never be called: the demo mode keeps everything local.</summary>
    private sealed class ThrowingAzureApi : IAzureApi
    {
        public Task<List<JsonElement>> QueryResourceGraphAsync(string query, IReadOnlyCollection<string> subscriptionIds, string? tenantId, CancellationToken ct = default) => throw new InvalidOperationException("Azure must not be called in demo mode");
        public Task<List<JsonElement>> GetArmListAsync(string url, string? tenantId, CancellationToken ct = default) => throw new InvalidOperationException("Azure must not be called in demo mode");
        public Task<JsonElement> GetArmAsync(string url, string? tenantId, CancellationToken ct = default) => throw new InvalidOperationException("Azure must not be called in demo mode");
        public Task<JsonElement> PostArmQueryAsync(string url, object body, string? tenantId, CancellationToken ct = default) => throw new InvalidOperationException("Azure must not be called in demo mode");
    }

    private BackupFlowService Flows { get; }

    public ComponentTests()
    {
        var credential = new global::Azure.Identity.AzureCliCredential();
        Services.AddSingleton<global::Azure.Core.TokenCredential>(credential);
        Services.AddSingleton<IAzureApi, ThrowingAzureApi>();
        Services.AddSingleton(new BackupDiscoveryService(new ThrowingAzureApi(), NullLogger<BackupDiscoveryService>.Instance));
        Services.AddSingleton(new BackupJobAnalysisService(new ThrowingAzureApi(), NullLogger<BackupJobAnalysisService>.Instance));
        Services.AddScoped(_ => new AzureSubscriptionService(credential, NullLogger<AzureSubscriptionService>.Instance));
        Services.AddScoped<SessionState>();
        Services.AddScoped<BackupFlowService>();
        JSInterop.Mode = JSRuntimeMode.Loose;

        Flows = Services.GetRequiredService<BackupFlowService>();
        Flows.EnterDemoMode();
    }

    private async Task ScanAsync(BackupFlowKind flow)
    {
        var fs = Flows.State.For(flow);
        fs.SelectedSubscriptionIds.Add(DemoDataService.SubProd);
        fs.SelectedSubscriptionIds.Add(DemoDataService.SubShared);
        await Flows.ScanAsync(flow);
    }

    [Fact]
    public void AssessmentPanel_SavesValidatedRequirementsAndIncludesBillingAndLimitations()
    {
        var resource = new ResourceInventoryInfo { Id = "/vm", Name = "vm", ResourceType = "microsoft.compute/virtualmachines" };
        var scan = new BackupScanResult { SubscriptionIds = ["s"], ResourceInventory = [resource] };
        var cut = Render<AssessmentPanel>(p => p.Add(x => x.Scan, scan));
        Assert.Contains("Current backup costs", cut.Markup);
        Assert.Contains("Billing data not collected", cut.Markup);
        Assert.Contains("RPO is the maximum acceptable data loss", cut.Markup);
        Assert.Contains("RPO not documented", cut.Markup);
        cut.Find("button").Click();
        cut.Find("#recovery-environment").Change("Production");
        cut.Find("#recovery-owner").Change("Service team");
        cut.Find("#recovery-rpo").Change("4");
        cut.Find("#recovery-rto").Change("-1");
        cut.Find("#recovery-retention").Change("30");
        cut.Find("#recovery-evidence").Change("Ticket 123: restored in 2h");
        cut.Find("#recovery-notes").Change("Keys and DNS required");
        cut.Find("form").Submit();
        Assert.Null(resource.Recovery.RpoHours); // Invalid form must not mutate the stored requirements.
        Assert.NotEmpty(cut.FindAll(".validation-errors li"));
        cut.Find("#recovery-rto").Change("8");
        cut.Find("form").Submit();
        Assert.Empty(cut.FindAll("form"));
        Assert.Equal("Production", resource.Environment);
        Assert.Equal(4, resource.Recovery.RpoHours);
        Assert.Equal(8, resource.Recovery.RtoHours);
        Assert.Equal(30, resource.Recovery.RetentionDays);
        Assert.Equal("Service team", resource.Recovery.Owner);
        Assert.Equal("Ticket 123: restored in 2h", resource.Recovery.RestoreTestEvidence);
        Assert.Equal("Keys and DNS required", resource.Recovery.Notes);
        Assert.Contains("Requirements saved", cut.Find("[role=status]").TextContent);
        Assert.DoesNotContain("RPO not documented", cut.Markup);
    }

    [Fact]
    public void AssessmentPanel_FiltersAndPagesWithoutDroppingInventoryEntries()
    {
        var scan = new BackupScanResult
        {
            ResourceInventory = Enumerable.Range(1, 30).Select(n => new ResourceInventoryInfo { Id = $"/vm{n:00}", Name = $"vm{n:00}" }).ToList()
        };
        var cut = Render<AssessmentPanel>(p => p.Add(x => x.Scan, scan));
        Assert.Equal(25, cut.FindAll(".assessment tbody tr").Count);
        cut.FindAll(".assessment .actions button")[1].Click();
        Assert.Equal(5, cut.FindAll(".assessment tbody tr").Count);
        cut.Find("#inventory-search").Input("vm01");
        Assert.Single(cut.FindAll(".assessment tbody tr"));
        Assert.Contains("vm01", cut.Find(".assessment tbody").TextContent);
        Assert.Contains("Page 1 of 1", cut.Markup);
        cut.Find("#inventory-search").Input("does-not-exist");
        Assert.Contains("No inventory entries match", cut.Markup);
        Assert.Equal(30, scan.ResourceInventory.Count);
    }

    [Fact]
    public void GradeBadge_RendersLetterAndColour()
    {
        var cut = Render<GradeBadge>(p => p.Add(x => x.Grade, "B").Add(x => x.Large, true));
        var span = cut.Find("span.grade-badge");
        Assert.Equal("B", span.TextContent);
        Assert.Contains("lg", span.ClassName);
        Assert.Contains("#7CB342", span.GetAttribute("style"));
        Assert.Contains("Good", span.GetAttribute("title"));
    }

    [Fact]
    public void StepBar_LocksLaterStepsUntilThereIsAScan()
    {
        var locked = Render<StepBar>(p => p.Add(x => x.Route, "recovery-services").Add(x => x.Current, "scope").Add(x => x.HasScan, false));
        Assert.Equal(2, locked.FindAll("li.step.locked").Count);
        Assert.Empty(locked.FindAll("li.step a"));

        var open = Render<StepBar>(p => p.Add(x => x.Route, "recovery-services").Add(x => x.Current, "grades").Add(x => x.HasScan, true).Add(x => x.HasJobTargets, true));
        Assert.Empty(open.FindAll("li.step.locked"));
        Assert.Contains(open.FindAll("li.step a"), a => a.GetAttribute("href") == "/recovery-services/jobs");
        Assert.Single(open.FindAll("li.step.active"));
    }

    [Fact]
    public async Task GradesStep_RecoveryServices_ShowsOverallGradeRatingsFindingsAndPolicies()
    {
        await ScanAsync(BackupFlowKind.RecoveryServices);
        var cut = Render<GradesStep>(p => p.Add(x => x.Flow, BackupFlowKind.RecoveryServices));

        // The weakest vault (the empty dev vault) sets the overall grade.
        Assert.Equal("F", cut.Find(".overall .grade-badge.lg").TextContent);
        Assert.Equal(4, cut.FindAll("article.rating").Count);
        Assert.Contains("rsv-contoso-prod", cut.Markup);
        Assert.Contains("rsv-contoso-legacy", cut.Markup);

        // The weakest vault is expanded by default with its breakdown and improvements.
        var open = cut.Find("article.rating.open");
        Assert.Contains("Score breakdown", open.TextContent);
        Assert.Contains("How to improve", open.TextContent);
        Assert.Equal(6, open.QuerySelectorAll("table.cats tr").Length);

        // Unprotected VMs, findings with fixes, and the policy table.
        Assert.Contains("Virtual machines without backup", cut.Markup);
        Assert.Contains("vm-jumpbox", cut.Markup);
        Assert.Contains("Findings", cut.Markup);
        Assert.NotEmpty(cut.FindAll("ul.findings li"));
        Assert.Equal(7, cut.FindAll("tr.policy-row").Count);
    }

    [Fact]
    public async Task GradesStep_ExpandingFindingShowsFix_AndPolicyRowShowsDetail()
    {
        await ScanAsync(BackupFlowKind.BackupVault);
        var cut = Render<GradesStep>(p => p.Add(x => x.Flow, BackupFlowKind.BackupVault));

        Assert.Equal("E", cut.Find(".overall .grade-badge.lg").TextContent);
        Assert.Contains("ProtectionError", cut.Markup);
        Assert.DoesNotContain("Virtual machines without backup", cut.Markup);

        Assert.Empty(cut.FindAll(".fix"));
        cut.FindAll("ul.findings li button.link")[0].Click();
        Assert.Single(cut.FindAll(".fix"));

        Assert.Empty(cut.FindAll(".policy-detail"));
        cut.FindAll("tr.policy-row")[0].Click();
        var detail = cut.Find(".policy-detail");
        Assert.Contains("Schedule", detail.TextContent);
        Assert.Contains("Items following this policy", detail.TextContent);
    }

    [Fact]
    public async Task JobsStep_RunsDemoAnalysis_AndShowsAssessment()
    {
        await ScanAsync(BackupFlowKind.RecoveryServices);
        var cut = Render<JobsStep>(p => p.Add(x => x.Flow, BackupFlowKind.RecoveryServices));

        Assert.NotEmpty(cut.FindAll("select option"));
        Assert.Empty(cut.FindAll("article.activity"));

        // Pick the legacy vault, which has a persistently failing item and a warning item.
        var legacyId = Flows.State.For(BackupFlowKind.RecoveryServices).Scan!.Vaults.Single(v => v.Name == "rsv-contoso-legacy").Id;
        cut.FindAll("select")[0].Change(legacyId);
        Assert.Contains("job-failure alerts are", cut.Markup);

        await cut.Find("button.btn-primary").ClickAsync(new());
        cut.WaitForState(() => cut.FindAll("article.activity").Count > 0, TimeSpan.FromSeconds(5));

        Assert.Contains("Failure assessment", cut.Markup);
        Assert.Contains("Persistent failure", cut.Markup);
        Assert.Contains("Needs review", cut.Markup);
        Assert.Contains("vm-fileserver", cut.Markup);
        Assert.NotNull(Flows.State.For(BackupFlowKind.RecoveryServices).JobAnalysis);

        // Fix script modal opens with a generated script.
        cut.FindAll("article.activity button.btn")[0].Click();
        Assert.Contains("generated by RateMyAzureBackups", cut.Find("pre.script").TextContent);
    }

    [Fact]
    public async Task ReportModal_DownloadsWordAndMarkdownForBothFlowsAndTabs()
    {
        foreach (var flow in new[] { BackupFlowKind.RecoveryServices, BackupFlowKind.BackupVault })
        {
            await ScanAsync(flow);
            var cut = Render<ReportModal>(p => p.Add(x => x.Flow, flow));
            await cut.InvokeAsync(() => cut.Instance.Open(false));
            foreach (var findings in new[] { false, true })
            {
                if (findings) cut.FindAll(".report-tabs button")[1].Click();
                var buttons = cut.FindAll(".report-downloads button");
                Assert.Equal(2, buttons.Count);
                Assert.Contains("Download .docx", buttons[0].TextContent);
                Assert.Contains("Download .md", buttons[1].TextContent);
                foreach (var markdown in new[] { false, true })
                {
                    await buttons[markdown ? 1 : 0].ClickAsync(new());
                    var call = JSInterop.Invocations.Last(i => i.Identifier == "rmab.download");
                    var ctx = Flows.ReportContext(flow);
                    Assert.Equal(BackupReportService.FileName(ctx, findings, markdown), call.Arguments[0]);
                    var bytes = Convert.FromBase64String(Assert.IsType<string>(call.Arguments[1]));
                    if (markdown)
                    {
                        Assert.Equal("text/markdown;charset=utf-8", call.Arguments[2]);
                        Assert.Equal(findings ? BackupReportService.FindingsMarkdown(ctx) : BackupReportService.SummaryMarkdown(ctx),
                            System.Text.Encoding.UTF8.GetString(bytes));
                    }
                    else
                    {
                        Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document", call.Arguments[2]);
                        Assert.Equal((byte)'P', bytes[0]);
                        Assert.Equal((byte)'K', bytes[1]);
                    }
                    Assert.All(cut.FindAll(".report-downloads button"), button => Assert.False(button.HasAttribute("disabled")));
                }
            }
        }
    }

    [Fact]
    public async Task BackupFlow_FallsBackToScopeWhenThereIsNoScan()
    {
        var cut = Render<BackupFlow>(p => p.Add(x => x.Flow, BackupFlowKind.RecoveryServices).Add(x => x.Step, "grades"));
        Assert.Contains("Choose what to scan", cut.Markup);

        await ScanAsync(BackupFlowKind.RecoveryServices);
        var grades = Render<BackupFlow>(p => p.Add(x => x.Flow, BackupFlowKind.RecoveryServices).Add(x => x.Step, "grades"));
        Assert.Contains("Findings", grades.Markup);
        Assert.DoesNotContain("Choose what to scan", grades.Markup);
    }

    [Fact]
    public void ScopeStep_DemoMode_ListsSampleSubscriptionsAndCounts()
    {
        var cut = Render<ScopeStep>(p => p.Add(x => x.Flow, BackupFlowKind.BackupVault));
        cut.WaitForState(() => cut.FindAll("li.sub-row").Count == 2, TimeSpan.FromSeconds(5));
        cut.WaitForAssertion(() => Assert.Contains("Backup vault", cut.Find(".sub-count").TextContent), TimeSpan.FromSeconds(5));

        Assert.True(cut.Find("button.btn-primary").HasAttribute("disabled"));
        cut.FindAll("li.sub-row input")[0].Change(true);
        Assert.False(cut.Find("button.btn-primary").HasAttribute("disabled"));
        Assert.Contains("Scan 1 subscription", cut.Find("button.btn-primary").TextContent);
    }
}
