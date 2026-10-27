using System.ComponentModel.DataAnnotations;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RateMyAzureBackups.Models;
using RateMyAzureBackups.Services;
using RateMyAzureBackups.Services.Azure;
using Xunit;

namespace RateMyAzureBackups.Tests;

public class BackupAssessmentTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private const string CostPage = """
        {"properties":{"columns":[
          {"name":"Currency"},{"name":"MeterSubCategory"},{"name":"ResourceId"},{"name":"PreTaxCost"}
        ],"rows":[["GBP","Protected Instances","/vault",12.34],["GBP","Backup Storage",null,5.66]],"nextLink":null}}
        """;

    [Fact]
    public void Inventory_DistinguishesEvidenceMissingProtectionAndUnknown_AndIncludesRestoreNames()
    {
        var scan = new BackupScanResult
        {
            Vaults = [new()
            {
                Items =
                [
                    new() { Id = "item1", SourceResourceId = "/VM1", ProtectionState = "Protected", WorkloadType = "Azure VM" },
                    new() { Id = "item2", SourceResourceId = "/stopped", ProtectionState = "ProtectionStopped" },
                    new() { Id = "item3", SourceResourceId = "/unknown", ProtectionState = "" },
                    new() { Id = "guest-item", SourceResourceId = "/guestdb", WorkloadType = "SQL in Azure VM", ProtectionState = "Protected" }
                ]
            }]
        };
        BackupDiscoveryService.ApplyResourceInventory(scan,
        [
            J("""{"id":"/vm1","name":"vm-restore","type":"microsoft.compute/virtualmachines","tags":{"ENVIRONMENT":"PrOd"}}"""),
            J("""{"id":"/stopped","name":"old-vm","type":"microsoft.compute/virtualmachines"}"""),
            J("""{"id":"/unknown","name":"unknown-vm","type":"microsoft.compute/virtualmachines"}"""),
            J("""{"id":"/storage","name":"store","type":"microsoft.storage/storageaccounts","sku":{"name":"Standard_ZRS"}}"""),
            J("""{"id":"/nic","name":"nic-prod","type":"microsoft.network/networkinterfaces"}""")
        ]);
        Assert.Equal(6, scan.ResourceInventory.Count);
        var vm = scan.ResourceInventory.Single(r => r.Id == "/vm1");
        Assert.Equal("Vault backup items found", vm.Protection);
        Assert.Equal("Production", vm.Environment);
        Assert.Equal(["item1"], vm.BackupItemIds);
        Assert.Equal("Protection stopped or in error", scan.ResourceInventory.Single(r => r.Id == "/stopped").Protection);
        Assert.Equal("Protection pending or unknown", scan.ResourceInventory.Single(r => r.Id == "/unknown").Protection);
        var storage = scan.ResourceInventory.Single(r => r.Id == "/storage");
        Assert.Equal("No vault protection found", storage.Protection);
        Assert.Equal("Standard_ZRS", storage.StorageConfiguration);
        var nic = scan.ResourceInventory.Single(r => r.Id == "/nic");
        Assert.Equal("Not assessed", nic.Protection);
        Assert.Equal("Unknown", nic.Environment); // Not inferred from "prod" in the name.
        Assert.Equal("SQL in Azure VM", scan.ResourceInventory.Single(r => r.Id == "/guestdb").ResourceType);
    }

    [Theory]
    [InlineData("dev", "Development")]
    [InlineData("UAT", "Test")]
    [InlineData(" production ", "Production")]
    [InlineData("mission-critical", "Critical")]
    [InlineData("some-custom-environment", "Unknown")]
    public void Environment_NormalizesOnlyExplicitValues(string value, string expected) =>
        Assert.Equal(expected, BackupAssessmentService.NormalizeEnvironment(value));

    [Theory]
    [InlineData("Development", "7-14 days")]
    [InlineData("Test", "14-30 days")]
    [InlineData("Production", "12 weekly")]
    [InlineData("Critical", "most frequent supported")]
    [InlineData("Unknown", "Confirm environment")]
    public void RecommendedPolicies_AreEnvironmentSpecificAndAdvisory(string environment, string expected)
    {
        var resource = new ResourceInventoryInfo { Environment = environment, ResourceType = "microsoft.compute/virtualmachines" };
        var recommendation = BackupAssessmentService.RecommendPolicy(resource, resource.ResourceType);
        Assert.Contains(expected, recommendation);
        Assert.Contains("application-consistent VM backup", recommendation);
        Assert.Contains("not Microsoft-mandated", recommendation);
    }

    [Theory]
    [InlineData("microsoft.sql/servers/databases", "database-native/PITR")]
    [InlineData("microsoft.storage/storageaccounts", "shares and blob containers individually")]
    [InlineData("microsoft.containerservice/managedclusters", "persistent volumes")]
    [InlineData("microsoft.compute/disks", "disk snapshots alone")]
    [InlineData("microsoft.network/networkinterfaces", "infrastructure as code")]
    public void RecommendedPolicies_RespectWorkloadLimits(string type, string expected) =>
        Assert.Contains(expected, BackupAssessmentService.RecommendPolicy(new() { ResourceType = type }, type));

    [Fact]
    public void RecoveryGaps_ReportMissingRequirementsAndConfiguredGapsWithoutClaimingAchievedObjectives()
    {
        var scan = new BackupScanResult
        {
            Vaults = [new() { Items = [new() { Id = "i", Name = "vm", PolicyId = "p", ProtectionState = "Protected" }] }],
            Policies = [new() { Id = "p", ScheduleFrequency = "Daily", DailyRetentionDays = 14 }]
        };
        var resource = new ResourceInventoryInfo { Id = "vm", BackupItemIds = ["i"], Environment = "Production" };
        var undocumented = BackupAssessmentService.RecoveryGap(scan, resource);
        Assert.Contains("RPO not documented", undocumented);
        Assert.Contains("RTO not documented", undocumented);
        Assert.Contains("Recovery owner not documented", undocumented);
        resource.Recovery = new()
        {
            RpoHours = 4, RtoHours = 8, RetentionDays = 30, Owner = "Service team",
            RestoreTestEvidence = "Ticket 123: restore took 2 hours"
        };
        var gaps = BackupAssessmentService.RecoveryGap(scan, resource);
        Assert.Contains("cadence exceeds 4h RPO", gaps);
        Assert.Contains("maximum configured retention below 30d", gaps);
        Assert.Contains("Achieved RPO/RTO and retention coverage require restore validation", gaps);
        Assert.DoesNotContain("RPO not documented", gaps);
        Assert.DoesNotContain("RTO not documented", gaps);
        Assert.DoesNotContain("Recovery owner not documented", gaps);
        scan.Policies[0].LogBackupFrequencyMinutes = 480;
        Assert.Contains("log backup interval 480min exceeds 4h RPO", BackupAssessmentService.RecoveryGap(scan, resource));
    }

    [Fact]
    public void Recommendations_IncludeBothVmAndGuestDatabaseProtection()
    {
        var advice = BackupAssessmentService.RecommendPolicy(
            new() { ResourceType = "microsoft.compute/virtualmachines", Environment = "Production" }, "Azure VM, SQL in Azure VM");
        Assert.Contains("database-native/PITR", advice);
        Assert.Contains("application-consistent VM backup", advice);
    }

    [Fact]
    public void CostOpportunities_DoNotClaimSavingsOrSuggestUnusedPoliciesAreCharged()
    {
        var scan = new BackupScanResult
        {
            Vaults = [new() { Name = "retired", Items = [new() { ProtectionState = "ProtectionStopped" }] }],
            Policies = [new() { Name = "long-term", YearlyRetentionYears = 3, ProtectedItemCount = 0 }]
        };
        var advice = string.Join(" ", BackupAssessmentService.CostOpportunities(scan));
        Assert.Contains("retained-data charges", advice);
        Assert.Contains("legal holds", advice);
        Assert.Contains("archive only for supported workloads", advice);
        Assert.Contains("unused policies alone do not incur", advice);
        Assert.Contains("No savings are quantified", advice);
    }

    [Fact]
    public void Costs_ParseByColumnNameAndPreserveUnallocatedAndNegativeCharges()
    {
        var lines = BackupDiscoveryService.ParseCostPage(J(CostPage), "sub");
        Assert.Equal(18m, lines.Sum(l => l.Amount));
        Assert.Equal("sub", lines[0].SubscriptionId);
        Assert.Equal("GBP", lines[0].Currency);
        Assert.Equal("", lines[1].ResourceId);
        var credit = BackupDiscoveryService.ParseCostPage(J(CostPage.Replace("12.34", "-12.34")), "sub");
        Assert.Equal(-12.34m, credit[0].Amount);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"properties":{"columns":[],"rows":[]}}""")]
    [InlineData("""{"properties":{"columns":[{"name":"PreTaxCost"},{"name":"ResourceId"},{"name":"MeterSubCategory"},{"name":"Currency"}],"rows":[[null,"/v","Storage","GBP"]]}}""")]
    [InlineData("""{"properties":{"columns":[{"name":"PreTaxCost"},{"name":"ResourceId"},{"name":"MeterSubCategory"},{"name":"Currency"}],"rows":[[1,"/v","Storage",""]]}}""")]
    public void Costs_RejectMalformedOrUnknownValues(string json) =>
        Assert.Throws<InvalidOperationException>(() => BackupDiscoveryService.ParseCostPage(J(json), "sub"));

    [Fact]
    public void CostQuery_UsesActualCostExplicitWindowAndBackupCategory()
    {
        var from = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var query = JsonSerializer.SerializeToElement(BackupDiscoveryService.CostQuery(from, from.AddDays(30)));
        Assert.Equal("ActualCost", query.GetProperty("type").GetString());
        Assert.Equal("Custom", query.GetProperty("timeframe").GetString());
        Assert.Equal(from, query.GetProperty("timePeriod").GetProperty("from").GetDateTime());
        var dataset = query.GetProperty("dataset");
        Assert.Equal(2, dataset.GetProperty("grouping").GetArrayLength());
        var filter = dataset.GetProperty("filter").GetProperty("dimensions");
        Assert.Equal("MeterCategory", filter.GetProperty("name").GetString());
        Assert.Equal("Backup", filter.GetProperty("values")[0].GetString());
    }

    [Fact]
    public async Task Scan_FollowsCostPagesAndRecordsUnavailableSubscriptionsWithoutLosingInventory()
    {
        var api = new AssessmentApi
        {
            Query = (query, _) => Task.FromResult(query == BackupDiscoveryService.InventoryQuery
                ? new List<JsonElement> { J("""{"id":"/vm","name":"vm","type":"microsoft.compute/virtualmachines","subscriptionId":"ok"}""") } : []),
            Post = (url, _) =>
            {
                if (url.Contains("/subscriptions/denied/")) throw new InvalidOperationException("403 billing denied");
                return Task.FromResult(J(url.Contains("skiptoken")
                    ? CostPage
                    : CostPage.Replace("\"nextLink\":null", "\"nextLink\":\"https://management.azure.com/cost-query?skiptoken=2\"")));
            }
        };
        var scan = await new BackupDiscoveryService(api, NullLogger<BackupDiscoveryService>.Instance)
            .ScanAsync(BackupFlowKind.BackupVault, ["ok", "denied"], "tenant");
        Assert.Single(scan.ResourceInventory);
        Assert.Equal(["ok", "denied"], scan.InventorySubscriptionIds);
        Assert.Equal(4, scan.Costs.Count);
        Assert.Equal(36m, scan.Costs.Sum(c => c.Amount));
        Assert.True(scan.CostCoverage.Single(c => c.SubscriptionId == "ok").Available);
        Assert.False(scan.CostCoverage.Single(c => c.SubscriptionId == "denied").Available);
        Assert.Contains(scan.Warnings, w => w.Contains("403 billing denied"));
        Assert.Equal(3, api.Posted.Count);
        Assert.All(api.Posted, p => Assert.Equal(api.Posted[0], p));
    }

    [Fact]
    public async Task Scan_FailedSecondCostPageDoesNotPublishPartialAmountsAsComplete()
    {
        var api = new AssessmentApi
        {
            Post = (url, _) => url.Contains("skiptoken")
                ? throw new InvalidOperationException("billing page failed")
                : Task.FromResult(J(CostPage.Replace("\"nextLink\":null", "\"nextLink\":\"https://management.azure.com/cost-query?skiptoken=2\"")))
        };
        var scan = await new BackupDiscoveryService(api, NullLogger<BackupDiscoveryService>.Instance)
            .ScanAsync(BackupFlowKind.BackupVault, ["s"], null);
        Assert.Empty(scan.Costs);
        Assert.False(Assert.Single(scan.CostCoverage).Available);
        Assert.Contains(scan.Warnings, w => w.Contains("billing page failed"));
    }

    [Fact]
    public async Task Scan_InventoryFailureIsExplicitAndCancellationIsNotSwallowed()
    {
        var api = new AssessmentApi
        {
            Query = (query, _) => query == BackupDiscoveryService.InventoryQuery
                ? throw new InvalidOperationException("inventory unavailable") : Task.FromResult(new List<JsonElement>())
        };
        var discovery = new BackupDiscoveryService(api, NullLogger<BackupDiscoveryService>.Instance);
        var scan = await discovery.ScanAsync(BackupFlowKind.BackupVault, ["s"], null);
        Assert.Empty(scan.InventorySubscriptionIds);
        Assert.Contains(scan.Warnings, w => w.Contains("inventory unavailable"));
        Assert.True(Assert.Single(scan.CostCoverage).Available);

        api.Query = (query, ct) => query == BackupDiscoveryService.InventoryQuery
            ? throw new OperationCanceledException(ct) : Task.FromResult(new List<JsonElement>());
        await Assert.ThrowsAsync<OperationCanceledException>(() => discovery.ScanAsync(BackupFlowKind.BackupVault, ["s"], null));
    }

    [Fact]
    public void Merge_PreservesAssessmentScopeAndCurrency()
    {
        var scan = new BackupScanResult();
        scan.Merge(new BackupScanResult
        {
            ResourceInventory = [new() { Id = "r" }], InventorySubscriptionIds = ["s"],
            Costs = [new() { Amount = 10, Currency = "GBP" }, new() { Amount = 20, Currency = "USD" }],
            CostCoverage = [new() { SubscriptionId = "s", Available = false }], IsSampleData = true
        });
        Assert.Single(scan.ResourceInventory);
        Assert.Equal(["s"], scan.InventorySubscriptionIds);
        Assert.Equal(2, scan.Costs.Select(c => c.Currency).Distinct().Count());
        Assert.False(Assert.Single(scan.CostCoverage).Available);
        Assert.True(scan.IsSampleData);
    }

    [Theory]
    [InlineData(BackupFlowKind.RecoveryServices)]
    [InlineData(BackupFlowKind.BackupVault)]
    public void Reports_IncludeAllAssessmentAreasAndUserRequirements(BackupFlowKind flow)
    {
        var scan = DemoDataService.Scan(flow, [DemoDataService.SubProd]);
        scan.ResourceInventory[0].Recovery = new()
        {
            Owner = "Service team", RpoHours = 4, RtoHours = 8, RetentionDays = 35,
            RestoreTestEvidence = "2026-10-25: 2h restore (ticket 123)", Notes = "Private endpoint and keys required"
        };
        var ctx = new BackupReportContext { Flow = flow, Scan = scan };
        foreach (var html in new[] { BackupReportService.SummaryHtml(ctx), BackupReportService.FindingsHtml(ctx) })
        {
            foreach (var heading in new[] { "Resource inventory and coverage", "Backup locations and storage",
                "Current backup costs", "Recommended policies by resource and environment", "Documented recovery requirements",
                "Cost optimisation opportunities", "Recovery and assessment limitations" })
                Assert.Contains(heading, html);
            Assert.Contains("Service team", html);
            Assert.Contains("2h restore (ticket 123)", html);
            Assert.Contains("Private endpoint and keys required", html);
            Assert.Contains("illustrative, not live Azure data", html);
            Assert.Contains("GBP", html);
            Assert.Contains("native, cross-scope and third-party", html);
        }
        Assert.All(scan.ResourceInventory, r => Assert.Equal(DemoDataService.SubProd, r.SubscriptionId));
        Assert.Single(scan.InventorySubscriptionIds);
        foreach (var bytes in new[] { BackupReportService.SummaryDocx(ctx), BackupReportService.FindingsDocx(ctx) })
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
            var xml = reader.ReadToEnd();
            Assert.Contains("Current backup costs", xml);
            Assert.Contains("Documented recovery requirements", xml);
            Assert.Contains("2h restore (ticket 123)", xml);
        }
    }

    [Fact]
    public void RecoveryRequirements_RejectNonPositiveValues()
    {
        var invalid = new RecoveryRequirements { RpoHours = 0, RtoHours = -1, RetentionDays = 0 };
        var errors = new List<ValidationResult>();
        Assert.False(Validator.TryValidateObject(invalid, new ValidationContext(invalid), errors, true));
        Assert.Equal(3, errors.Count);
    }

    [Fact]
    public async Task ArmCostQuery_PostsAuthenticatedJsonWithNoMutationOperation()
    {
        var factory = new QueryClientFactory();
        var api = new AzureApi(new QueryCredential(), factory, NullLogger<AzureApi>.Instance);
        var result = await api.PostArmQueryAsync("https://management.azure.com/subscriptions/s/providers/Microsoft.CostManagement/query?api-version=2025-03-01",
            BackupDiscoveryService.CostQuery(DateTime.UtcNow.Date.AddDays(-30), DateTime.UtcNow.Date), "tenant");
        Assert.Equal("Bearer test-token", factory.Handler.Authorization);
        Assert.Equal(HttpMethod.Post, factory.Handler.Method);
        Assert.Contains("\"type\":\"ActualCost\"", factory.Handler.Body);
        Assert.Equal("application/json", factory.Handler.MediaType);
        Assert.Equal(18m, BackupDiscoveryService.ParseCostPage(result, "s").Sum(c => c.Amount));
    }

    [Theory]
    [InlineData("https://example.com/query")]
    [InlineData("http://management.azure.com/query")]
    [InlineData("https://management.azure.com:444/query")]
    [InlineData("https://user@management.azure.com/query")]
    public async Task ArmCostQuery_RejectsNonManagementPaginationTargets(string url)
    {
        var factory = new QueryClientFactory();
        var api = new AzureApi(new QueryCredential(), factory, NullLogger<AzureApi>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => api.PostArmQueryAsync(url, new { }, null));
        Assert.Null(factory.Handler.Method);
    }

    private sealed class QueryCredential : global::Azure.Core.TokenCredential
    {
        public override global::Azure.Core.AccessToken GetToken(global::Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("test-token", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<global::Azure.Core.AccessToken> GetTokenAsync(global::Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class QueryClientFactory : IHttpClientFactory
    {
        public QueryHandler Handler { get; } = new();
        public HttpClient CreateClient(string name) => new(Handler, disposeHandler: false);
    }

    private sealed class QueryHandler : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string Authorization { get; private set; } = "";
        public string Body { get; private set; } = "";
        public string? MediaType { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Authorization = request.Headers.Authorization?.ToString() ?? "";
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            MediaType = request.Content.Headers.ContentType?.MediaType;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(CostPage) };
        }
    }

    private sealed class AssessmentApi : IAzureApi
    {
        public Func<string, CancellationToken, Task<List<JsonElement>>> Query { get; set; } = (_, _) => Task.FromResult(new List<JsonElement>());
        public Func<string, CancellationToken, Task<JsonElement>> Post { get; set; } = (_, _) => Task.FromResult(J(CostPage));
        public List<string> Posted { get; } = [];
        public Task<List<JsonElement>> QueryResourceGraphAsync(string query, IReadOnlyCollection<string> subscriptionIds, string? tenantId, CancellationToken ct = default) => Query(query, ct);
        public Task<List<JsonElement>> GetArmListAsync(string url, string? tenantId, CancellationToken ct = default) => Task.FromResult(new List<JsonElement>());
        public Task<JsonElement> GetArmAsync(string url, string? tenantId, CancellationToken ct = default) => Task.FromResult(J("{}"));
        public Task<JsonElement> PostArmQueryAsync(string url, object body, string? tenantId, CancellationToken ct = default)
        {
            Posted.Add(JsonSerializer.Serialize(body));
            return Post(url, ct);
        }
    }
}
