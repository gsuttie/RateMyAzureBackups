using System.Text.Json;
using RateMyAzureBackups.Models;
using RateMyAzureBackups.Services;
using Xunit;

namespace RateMyAzureBackups.Tests;

public class BackupJobAnalysisServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 09, 00, 00, TimeSpan.Zero);
    private const string VaultId = "/subscriptions/s/resourceGroups/rg/providers/Microsoft.RecoveryServices/vaults/v";

    private static BackupJobInfo Job(string item, int daysAgo, string status, string code = "", double minutes = 20, string operation = "Backup") => new()
    {
        Id = $"{VaultId}/backupJobs/{Guid.NewGuid()}", VaultId = VaultId, ItemId = $"{VaultId}/backupFabrics/x/{item}", ItemName = item, WorkloadType = "Azure VM",
        Operation = operation, Status = status, StartTime = Now.AddDays(-daysAgo), EndTime = Now.AddDays(-daysAgo).AddMinutes(minutes),
        Duration = TimeSpan.FromMinutes(minutes), ErrorCode = code, ErrorMessage = code == "" ? "" : "Something went wrong."
    };

    private static BackupJobAnalysis Analyse(string range, params BackupJobInfo[] jobs)
    {
        var a = new BackupJobAnalysis { Flow = BackupFlowKind.RecoveryServices, TargetId = VaultId, TargetName = "v", TimeRangeKey = range, TimeRangeLabel = BackupJobAnalysisService.LabelFor(range).ToLowerInvariant() };
        BackupJobAnalysisService.Populate(a, jobs, Now);
        return a;
    }

    [Fact]
    public void Populate_CountsOnlyTheCurrentWindow_AndSplitsHealthyFromActivities()
    {
        var a = Analyse("7d",
            Job("ok", 1, "Completed"), Job("ok", 2, "Completed"), Job("ok", 10, "Failed", "X"),   // failure is in the previous period
            Job("bad", 1, "Failed", "UserErrorGuestAgentStatusUnavailable"), Job("bad", 2, "Failed", "UserErrorGuestAgentStatusUnavailable"), Job("bad", 3, "Completed"),
            Job("warn", 1, "CompletedWithWarnings"), Job("progress", 0, "InProgress"));

        Assert.Equal(7, a.TotalJobs);
        Assert.Equal(3, a.CompletedCount);
        Assert.Equal(2, a.FailedCount);
        Assert.Equal(1, a.WarningCount);
        Assert.Equal(1, a.InProgressCount);
        Assert.Equal(4, a.DistinctItems);
        Assert.Equal(["ok", "progress"], a.HealthyItems);
        Assert.Equal(2, a.Activities.Count);
        Assert.Equal(67, a.SuccessRate); // 3 completed + 1 with warnings out of 6 finished jobs
    }

    [Fact]
    public void Classify_PersistentFailure_WhenLastJobsFailInARow()
    {
        var a = Analyse("7d", Job("vm", 3, "Completed"), Job("vm", 2, "Failed", "UserErrorGuestAgentStatusUnavailable"), Job("vm", 1, "Failed", "UserErrorGuestAgentStatusUnavailable"));
        var act = Assert.Single(a.Activities);
        Assert.Equal("Persistent failure", act.Classification);
        Assert.Equal(2, act.ConsecutiveFailures);
        Assert.Contains("VM agent is not responding", act.Explanation);
        Assert.Contains("(UserErrorGuestAgentStatusUnavailable)", act.Explanation);
        Assert.Contains(act.Recommendations, r => r.Contains("Azure VM agent"));
        Assert.Contains(act.Recommendations, r => r.Contains("on-demand backup"));
        Assert.Equal(2, act.SampleJobIds.Count);
    }

    [Fact]
    public void Classify_TransientFailure_WhenASuccessFollows()
    {
        var a = Analyse("7d", Job("vm", 3, "Failed", "UserErrorVmNotInDesirableState"), Job("vm", 2, "Completed"), Job("vm", 1, "Completed"));
        var act = Assert.Single(a.Activities);
        Assert.Equal("Transient failure", act.Classification);
        Assert.Contains("followed by a successful job", act.Explanation);
        Assert.Contains(act.Recommendations, r => r.Contains("No action is needed"));
    }

    [Fact]
    public void Classify_NeedsReview_ForSingleLatestFailure_AndForWarnings()
    {
        var single = Analyse("7d", Job("vm", 2, "Completed"), Job("vm", 1, "Failed", "UserErrorSomethingUnknown"));
        Assert.Equal("Needs review", single.Activities.Single().Classification);
        Assert.Contains(single.Activities.Single().Recommendations, r => r.Contains("UserError"));

        var warn = Analyse("7d", Job("vm", 1, "CompletedWithWarnings"));
        var act = warn.Activities.Single();
        Assert.Equal("Needs review", act.Classification);
        Assert.Contains("completed with warnings", act.Explanation);
    }

    [Fact]
    public void Classify_UsesAzureRecommendation_WhenCodeIsUnknown()
    {
        var jobs = new[] { Job("vm", 2, "Failed", "WeirdCode"), Job("vm", 1, "Failed", "WeirdCode") };
        foreach (var j in jobs) j.ErrorRecommendation = "Do the thing Azure says.";
        var act = Analyse("7d", jobs).Activities.Single();
        Assert.Contains("Do the thing Azure says.", act.Recommendations);
    }

    [Fact]
    public void Classify_LongJobs_GetADurationRecommendation()
    {
        var act = Analyse("7d", Job("vm", 2, "Failed", "X", minutes: 500), Job("vm", 1, "Failed", "X", minutes: 500)).Activities.Single();
        Assert.Contains(act.Recommendations, r => r.Contains("hours on average"));
    }

    [Fact]
    public void BuildChanges_DetectsNewRecoveredStoppedIncreasedAndSlower()
    {
        var a = Analyse("7d",
            Job("new", 1, "Failed", "X"), Job("new", 9, "Completed"),
            Job("recovered", 1, "Completed"), Job("recovered", 9, "Failed", "X"),
            Job("stopped", 9, "Completed"),
            Job("worse", 1, "Failed", "X"), Job("worse", 2, "Failed", "X"), Job("worse", 9, "Failed", "X"), Job("worse", 10, "Completed"),
            Job("slow", 1, "Completed", minutes: 200), Job("slow", 9, "Completed", minutes: 20));

        var types = a.Changes.ToDictionary(c => c.ItemName, c => c.ChangeType);
        Assert.Equal("New failure", types["new"]);
        Assert.Equal("Recovered", types["recovered"]);
        Assert.Equal("Stopped", types["stopped"]);
        Assert.Equal("Increased", types["worse"]);
        Assert.Equal("Slower", types["slow"]);
        Assert.Equal("New failure", a.Changes[0].ChangeType); // ordered by urgency
    }

    [Fact]
    public void Populate_WarnsWhenJobsStoppedEntirely()
    {
        var a = Analyse("24h", Job("vm", 2, "Completed"));
        Assert.Equal(0, a.TotalJobs);
        Assert.Contains(a.Warnings, w => w.Contains("No jobs ran"));
    }

    [Fact]
    public void BuildQuery_ScopesToVaultAndDoubledWindow()
    {
        var q = BackupJobAnalysisService.BuildQuery(BackupFlowKind.RecoveryServices, VaultId, "7d");
        Assert.Contains("microsoft.recoveryservices/vaults/backupjobs", q);
        Assert.Contains(VaultId.ToLowerInvariant() + "/", q);
        Assert.Contains("ago(14d)", q);
        Assert.Contains("microsoft.dataprotection/backupvaults/backupjobs", BackupJobAnalysisService.BuildQuery(BackupFlowKind.BackupVault, VaultId, "24h"));
    }

    [Fact]
    public void ParseJob_RecoveryServicesShape()
    {
        var row = JsonDocument.Parse($$"""
        {
          "id": "{{VaultId}}/backupJobs/66970b54-b798-4c48-aeb6-3f74d5232833", "name": "66970b54-b798-4c48-aeb6-3f74d5232833",
          "properties": {
            "activityId": "2c99b29a", "backupInstanceId": "{{VaultId}}/backupFabrics/Azure/protectionContainers/c/protectedItems/VM;x;rg;vm1",
            "backupManagementType": "AzureIaasVM", "dataSourceId": "/subscriptions/s/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm1",
            "dataSourceType": "Microsoft.Compute/virtualMachines", "duration": "01:11:58.8941979", "endTime": "2026-10-11T02:28:35.3186256Z",
            "entityFriendlyName": "vm1", "errorDetails": [ { "errorCode": "UserErrorGuestAgentStatusUnavailable", "errorString": "VM agent unreachable", "recommendations": [ "Ensure the VM agent is running." ] } ],
            "isUserTriggered": false, "jobType": "AzureIaaSVMJob", "operation": "Backup", "operationCategory": "Backup", "startTime": "2026-10-11T01:16:36.4244277Z", "status": "Failed"
          }
        }
        """).RootElement;
        var j = BackupJobAnalysisService.ParseJob(row);
        Assert.Equal(VaultId, j.VaultId);
        Assert.Equal("vm1", j.ItemName);
        Assert.Equal("Azure VM", j.WorkloadType);
        Assert.Equal("Backup", j.Operation);
        Assert.True(j.IsFailed);
        Assert.Equal(TimeSpan.Parse("01:11:58.8941979"), j.Duration);
        Assert.Equal("UserErrorGuestAgentStatusUnavailable", j.ErrorCode);
        Assert.Equal("VM agent unreachable", j.ErrorMessage);
        Assert.Equal("Ensure the VM agent is running.", j.ErrorRecommendation);
    }

    [Fact]
    public void ParseJob_BackupVaultShape()
    {
        var row = JsonDocument.Parse("""
        {
          "id": "/subscriptions/s/resourceGroups/rg/providers/Microsoft.DataProtection/backupVaults/bv/backupJobs/1", "name": "1",
          "properties": {
            "backupInstanceFriendlyName": "stblobs", "backupInstanceId": "/subscriptions/s/resourceGroups/rg/providers/Microsoft.DataProtection/backupVaults/bv/backupInstances/stblobs-1",
            "dataSourceType": "Microsoft.Storage/storageAccounts/blobServices", "operation": "Backup", "operationCategory": "Backup", "status": "CompletedWithWarnings",
            "startTime": "2026-10-20T01:00:00Z", "endTime": "2026-10-20T01:30:00Z", "policyName": "BlobDaily",
            "errorDetails": [ { "code": "UserErrorPartialBackup", "message": "Some containers were skipped.", "recommendation": "Review the skipped containers." } ]
          }
        }
        """).RootElement;
        var j = BackupJobAnalysisService.ParseJob(row);
        Assert.Equal("stblobs", j.ItemName);
        Assert.Equal("Blob storage", j.WorkloadType);
        Assert.True(j.IsWarning);
        Assert.Equal(TimeSpan.FromMinutes(30), j.Duration);
        Assert.Equal("BlobDaily", j.PolicyName);
        Assert.Equal("UserErrorPartialBackup", j.ErrorCode);
        Assert.Equal("Review the skipped containers.", j.ErrorRecommendation);
    }

    [Fact]
    public void DemoJobAnalysis_ShowsEveryClassification()
    {
        var scan = DemoDataService.RecoveryServicesEstate();
        var legacy = scan.Vaults.Single(v => v.Name == "rsv-contoso-legacy");
        var a = DemoDataService.JobAnalysis(BackupFlowKind.RecoveryServices, legacy.Id, legacy.Name, "7d");
        Assert.True(a.JobsQueryable);
        Assert.Contains(a.Activities, x => x.Classification == "Persistent failure" && x.ItemName == "vm-fileserver");
        Assert.Contains(a.Activities, x => x.Classification == "Needs review" && x.ItemName == "vm-intranet");

        var apps = scan.Vaults.Single(v => v.Name == "rsv-contoso-apps");
        var b = DemoDataService.JobAnalysis(BackupFlowKind.RecoveryServices, apps.Id, apps.Name, "7d");
        Assert.Contains(b.Activities, x => x.Classification == "Transient failure" && x.ItemName == "vm-batch-01");
        Assert.Contains(b.Changes, c => c.ChangeType == "Recovered" && c.ItemName == "vm-app-01");
        Assert.Contains(b.Changes, c => c.ChangeType == "Slower" && c.ItemName == "vm-batch-01");
    }
}
