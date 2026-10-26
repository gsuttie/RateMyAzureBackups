using RateMyAzureBackups.Models;
using RateMyAzureBackups.Services;
using Xunit;

namespace RateMyAzureBackups.Tests;

/// <summary>
/// Scoring edge cases. The "perfect vault" is the reference case and must keep producing 100/A;
/// the other cases prove every cap and every improvement path.
/// </summary>
public class BackupRatingServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 09, 00, 00, TimeSpan.Zero);
    private const string VaultId = "/subscriptions/s1/resourceGroups/rg/providers/Microsoft.RecoveryServices/vaults/rsv1";
    private const string PolicyId = VaultId + "/backupPolicies/Daily30";

    // ── Builders ─────────────────────────────────────────────────────────────

    private static BackupPolicyInfo DailyPolicy(string id = PolicyId, string name = "Daily30", int retentionDays = 30, bool longTerm = true, string frequency = "Daily") => new()
    {
        Id = id, Name = name, VaultId = VaultId, SubscriptionId = "s1", Kind = BackupFlowKind.RecoveryServices, WorkloadType = "Azure VM",
        ScheduleFrequency = frequency, ScheduleSummary = frequency, DailyRetentionDays = frequency == "Weekly" ? null : retentionDays,
        WeeklyRetentionWeeks = frequency == "Weekly" ? retentionDays / 7 : (longTerm ? 12 : null), ProtectedItemCount = 1
    };

    private static ProtectedItemInfo Item(string name, string state = "Protected", string lastStatus = "Completed", TimeSpan? age = null, string policyId = PolicyId) => new()
    {
        Id = $"{VaultId}/backupFabrics/Azure/protectionContainers/c/protectedItems/{name}", Name = name, FriendlyName = name, VaultId = VaultId, SubscriptionId = "s1",
        SourceResourceId = $"/subscriptions/s1/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/{name}",
        WorkloadType = "Azure VM", BackupManagementType = "AzureIaasVM", ProtectionState = state, LastBackupStatus = lastStatus,
        LastBackupTime = Now - (age ?? TimeSpan.FromHours(6)), PolicyId = policyId, PolicyName = "Daily30"
    };

    /// <summary>A vault that earns full marks in every category.</summary>
    private static BackupVaultInfo PerfectVault() => new()
    {
        Id = VaultId, Name = "rsv1", SubscriptionId = "s1", Kind = BackupFlowKind.RecoveryServices, Location = "westeurope",
        StorageRedundancy = "GeoRedundant", CrossRegionRestore = true,
        SoftDeleteState = "AlwaysOn", SoftDeleteRetentionDays = 14, ImmutabilityState = "Locked", MultiUserAuthorization = true,
        PublicNetworkAccess = "Disabled", AlertsForAllJobFailures = true, DiagnosticsEnabled = true, LogDestinations = ["Log Analytics: law"],
        Items = [Item("vm1"), Item("vm2")]
    };

    private static BackupRating Rate(BackupVaultInfo v, params BackupPolicyInfo[] policies) =>
        BackupRatingService.RateVault(v, policies.Length == 0 ? [DailyPolicy()] : policies, Now);

    private static int Earned(BackupRating r, string category) => r.Categories.Single(c => c.Name == category).Earned;

    // ── Reference case ───────────────────────────────────────────────────────

    [Fact]
    public void PerfectVault_Scores100_GradeA_NoImprovements()
    {
        var r = Rate(PerfectVault());
        Assert.Equal(100, r.Score);
        Assert.Equal("A", r.Grade);
        Assert.Null(r.CapReason);
        Assert.Empty(r.Improvements);
        Assert.Equal(6, r.Categories.Count);
        Assert.Equal(100, r.Categories.Sum(c => c.Possible));
    }

    [Fact]
    public void GradeBands_AreCorrect()
    {
        Assert.Equal("A", BackupRatingService.GradeForScore(90));
        Assert.Equal("B", BackupRatingService.GradeForScore(89));
        Assert.Equal("B", BackupRatingService.GradeForScore(75));
        Assert.Equal("C", BackupRatingService.GradeForScore(60));
        Assert.Equal("D", BackupRatingService.GradeForScore(45));
        Assert.Equal("E", BackupRatingService.GradeForScore(30));
        Assert.Equal("F", BackupRatingService.GradeForScore(29));
        Assert.Equal("F", BackupRatingService.WorstGrade("B", "F"));
    }

    // ── Caps ─────────────────────────────────────────────────────────────────

    [Fact]
    public void EmptyVault_IsCappedToF_AndImprovementLiftsIt()
    {
        var v = PerfectVault();
        v.Items.Clear();
        var r = Rate(v);

        Assert.Equal("F", r.Grade);
        Assert.Contains("Nothing is protected", r.CapReason);
        Assert.Equal(0, Earned(r, BackupRatingService.CoverageCategory));
        var fix = r.Improvements.Single(i => i.PointsGained == BackupRatingService.CoveragePts);
        Assert.Contains("Configure backup", fix.Action);
        Assert.NotEqual("F", fix.ResultingGrade);
    }

    [Fact]
    public void ItemInProtectionError_CapsToD_AndProRatesCoverage()
    {
        var v = PerfectVault();
        v.Items.Add(Item("vm3", state: "ProtectionError", lastStatus: "Unhealthy", age: TimeSpan.FromDays(20)));
        var r = Rate(v);

        Assert.Equal("D", r.Grade);
        Assert.Contains("stopped or in error", r.CapReason);
        Assert.Equal(20, Earned(r, BackupRatingService.CoverageCategory)); // 2 of 3
        // The errored item does not also count against freshness.
        Assert.Equal(BackupRatingService.FreshnessPts, Earned(r, BackupRatingService.FreshnessCategory));
        var fix = r.Improvements.Single(i => i.Action.Contains("Resume or repair"));
        Assert.Equal(10, fix.PointsGained);
        Assert.Equal("A", fix.ResultingGrade);
    }

    [Fact]
    public void ProtectionStopped_CountsAsNotActive()
    {
        var v = PerfectVault();
        v.Items[0].ProtectionState = "ProtectionStopped";
        var r = Rate(v);
        Assert.Equal("D", r.Grade);
        Assert.Equal(15, Earned(r, BackupRatingService.CoverageCategory));
    }

    [Fact]
    public void FailedLastBackup_CapsToC_AndIsListed()
    {
        var v = PerfectVault();
        v.Items[1].LastBackupStatus = "Failed";
        var r = Rate(v);

        Assert.Equal("C", r.Grade);
        Assert.Contains("failed their most recent backup", r.CapReason);
        Assert.Equal(10, Earned(r, BackupRatingService.FreshnessCategory));
        var fix = r.Improvements.Single(i => i.Action.Contains("last backup failed or is overdue"));
        Assert.Equal(10, fix.PointsGained);
        Assert.Equal("A", fix.ResultingGrade); // lifts the cap
    }

    [Fact]
    public void StaleBackup_LosesFreshnessPoints_WithoutCap()
    {
        var v = PerfectVault();
        v.Items[1].LastBackupTime = Now.AddDays(-3);
        var r = Rate(v);

        Assert.Null(r.CapReason);
        Assert.Equal(10, Earned(r, BackupRatingService.FreshnessCategory));
        Assert.Equal("A", r.Grade); // 90
    }

    [Fact]
    public void WeeklyPolicy_GetsAWiderFreshnessWindow()
    {
        var weekly = DailyPolicy(id: VaultId + "/backupPolicies/Weekly", name: "Weekly", retentionDays: 28, frequency: "Weekly");
        var v = PerfectVault();
        foreach (var i in v.Items) { i.PolicyId = weekly.Id; i.LastBackupTime = Now.AddDays(-5); }
        var r = BackupRatingService.RateVault(v, [weekly], Now);

        Assert.Equal(BackupRatingService.FreshnessPts, Earned(r, BackupRatingService.FreshnessCategory));
        Assert.Equal(TimeSpan.FromDays(8), BackupRatingService.FreshnessWindow(v.Items[0], [weekly]));
    }

    [Fact]
    public void SoftDeleteDisabled_CapsToC()
    {
        var v = PerfectVault();
        v.SoftDeleteState = "Disabled";
        var r = Rate(v);

        Assert.Equal("C", r.Grade);
        Assert.Contains("Soft delete is disabled", r.CapReason);
        Assert.Equal(10, Earned(r, BackupRatingService.SecurityCategory));
        Assert.Equal("A", r.Improvements.Single(i => i.Action.Contains("Enable soft delete")).ResultingGrade);
    }

    [Fact]
    public void LocallyRedundant_CapsToB_AndNeedsNewVaultForGrs()
    {
        var v = PerfectVault();
        v.StorageRedundancy = "LocallyRedundant";
        v.CrossRegionRestore = false;
        var r = Rate(v);

        Assert.Equal("B", r.Grade);
        Assert.Equal(80, r.Score); // the raw score is already a B, so no cap reason is shown...
        Assert.Null(r.CapReason);
        Assert.Equal(0, Earned(r, BackupRatingService.ResilienceCategory));
        var grs = r.Improvements.Single(i => i.PointsGained == 12);
        Assert.Contains("geo-redundant", grs.Action);
        Assert.Equal("A", grs.ResultingGrade); // ...but moving to GRS lifts the cap as well as the score

        // With everything else perfect but a stale item, the cap is what holds the grade at B.
        v.Items[0].LastBackupTime = Now.AddDays(-3);
        v.CrossRegionRestore = true;
        var capped = Rate(v);
        Assert.Equal(78, capped.Score);
        Assert.Equal("B", capped.Grade);
        v.Items[0].LastBackupTime = Now.AddHours(-1);
        var highScore = Rate(v);
        Assert.Equal(88, highScore.Score);
        Assert.Equal("B", highScore.Grade);
        Assert.Null(highScore.CapReason);
        Assert.Contains("requires geo-redundant storage", r.Improvements.Single(i => i.PointsGained == 8).Action);
    }

    [Fact]
    public void ZoneRedundant_ScoresBetweenLrsAndGrs_NoCap()
    {
        var v = PerfectVault();
        v.StorageRedundancy = "ZoneRedundant";
        v.CrossRegionRestore = false;
        var r = Rate(v);
        Assert.Equal(8, Earned(r, BackupRatingService.ResilienceCategory));
        Assert.Null(r.CapReason);
    }

    [Fact]
    public void MultipleCaps_WorstWins_AndCapReasonMatchesFinalGrade()
    {
        var v = PerfectVault();
        v.StorageRedundancy = "LocallyRedundant";      // B
        v.SoftDeleteState = "Disabled";                 // C
        v.Items.Add(Item("vm3", state: "ProtectionError")); // D
        var r = Rate(v);
        Assert.Equal("D", r.Grade);
        Assert.Contains("stopped or in error", r.CapReason);
    }

    // ── Partial credit and unknowns ──────────────────────────────────────────

    [Fact]
    public void Security_PartialCredit_ForUnlockedImmutability()
    {
        var v = PerfectVault();
        v.ImmutabilityState = "Unlocked";
        v.MultiUserAuthorization = false;
        v.PublicNetworkAccess = "Enabled";
        var r = Rate(v);
        Assert.Equal(8, Earned(r, BackupRatingService.SecurityCategory)); // 5 + 3 + 0 + 0
        Assert.Contains(r.Improvements, i => i.Action.Contains("Lock the immutability") && i.PointsGained == 2);
        Assert.Contains(r.Improvements, i => i.Action.Contains("multi-user authorization") && i.PointsGained == 3);
        Assert.Contains(r.Improvements, i => i.Action.Contains("public network access") && i.PointsGained == 2);
    }

    [Fact]
    public void UnknownSettings_GetHalfPoints_NoCaps()
    {
        var v = PerfectVault();
        v.StorageRedundancy = "";
        v.CrossRegionRestore = null;
        v.SoftDeleteState = "";
        v.MultiUserAuthorization = null;
        v.PublicNetworkAccess = "";
        v.AlertsForAllJobFailures = null;
        v.DiagnosticsEnabled = null;
        v.LogDestinations = [];
        var r = Rate(v);

        Assert.Null(r.CapReason);
        Assert.Equal(10, Earned(r, BackupRatingService.ResilienceCategory));
        Assert.Equal(2 + 5 + 1 + 1, Earned(r, BackupRatingService.SecurityCategory));
        Assert.Equal(2, Earned(r, BackupRatingService.MonitoringCategory));
    }

    [Fact]
    public void PolicyHygiene_PenalisesWeeklyShortAndNoLongTerm()
    {
        var weekly = DailyPolicy(name: "Weekly", retentionDays: 14, longTerm: false, frequency: "Weekly");
        weekly.WeeklyRetentionWeeks = 2;
        var v = PerfectVault();
        foreach (var i in v.Items) i.LastBackupTime = Now.AddDays(-1);
        var r = BackupRatingService.RateVault(v, [weekly], Now);

        // weekly → 0 schedule pts, 14 days → 0 retention pts, weekly retention counts as long-term → 3
        Assert.Equal(3, Earned(r, BackupRatingService.PolicyCategory));
        Assert.Contains(r.Improvements, i => i.Action.Contains("daily (or more frequent) schedule"));
        Assert.Contains(r.Improvements, i => i.Action.Contains("at least 30 days"));
    }

    [Fact]
    public void PolicyHygiene_NoLongTermRetention_IsAnImprovement()
    {
        var r = Rate(PerfectVault(), DailyPolicy(longTerm: false));
        Assert.Equal(7, Earned(r, BackupRatingService.PolicyCategory));
        Assert.Contains(r.Improvements, i => i.Action.Contains("weekly or monthly retention") && i.PointsGained == 3);
    }

    [Fact]
    public void Monitoring_DisabledAlertsAndNoDiagnostics_AreImprovements()
    {
        var v = PerfectVault();
        v.AlertsForAllJobFailures = false;
        v.DiagnosticsEnabled = false;
        v.LogDestinations = [];
        var r = Rate(v);
        Assert.Equal(0, Earned(r, BackupRatingService.MonitoringCategory));
        Assert.Contains(r.Improvements, i => i.Action.Contains("job failures") && i.PointsGained == 3);
        Assert.Contains(r.Improvements, i => i.Action.Contains("Log Analytics") && i.PointsGained == 2);
    }

    [Fact]
    public void PendingInitialBackup_GetsHalfFreshnessPoints()
    {
        var v = PerfectVault();
        foreach (var i in v.Items) { i.ProtectionState = "IRPending"; i.LastBackupStatus = ""; i.LastBackupTime = null; }
        var r = Rate(v);
        Assert.Equal(10, Earned(r, BackupRatingService.FreshnessCategory));
        Assert.Contains("Initial backups are still pending", string.Join(" ", r.Categories.Single(c => c.Name == BackupRatingService.FreshnessCategory).Details));
    }

    [Fact]
    public void Improvements_AreOrderedByPoints_AndSumToTheGap()
    {
        var v = PerfectVault();
        v.StorageRedundancy = "ZoneRedundant"; v.CrossRegionRestore = false;
        v.ImmutabilityState = "Disabled"; v.MultiUserAuthorization = false;
        v.AlertsForAllJobFailures = false;
        var r = Rate(v);

        Assert.Equal(r.Improvements.OrderByDescending(i => i.PointsGained).Select(i => i.Action), r.Improvements.Select(i => i.Action));
        Assert.Equal(100 - r.Score, r.Improvements.Sum(i => i.PointsGained));
    }

    // ── Sample estate ────────────────────────────────────────────────────────

    [Fact]
    public void DemoRecoveryServicesEstate_CoversTheGradeBands()
    {
        var scan = DemoDataService.Scan(BackupFlowKind.RecoveryServices, [DemoDataService.SubProd, DemoDataService.SubShared]);
        var grades = scan.Ratings.ToDictionary(r => r.TargetName, r => r.Grade);

        Assert.Equal("A", grades["rsv-contoso-prod"]);
        Assert.Equal("C", grades["rsv-contoso-apps"]);
        Assert.Equal("E", grades["rsv-contoso-legacy"]);
        Assert.Equal("F", grades["rsv-contoso-dev"]);
        Assert.Equal("F", BackupRatingService.Worst(scan.Ratings)!.Grade);
        Assert.Equal(2, scan.UnprotectedResources.Count);
    }

    [Fact]
    public void DemoBackupVaultEstate_CoversTheGradeBands()
    {
        var scan = DemoDataService.Scan(BackupFlowKind.BackupVault, [DemoDataService.SubProd, DemoDataService.SubShared]);
        var grades = scan.Ratings.ToDictionary(r => r.TargetName, r => r.Grade);

        Assert.Equal("A", grades["bv-contoso-prod"]);
        Assert.Equal("E", grades["bv-contoso-shared"]);
    }

    [Fact]
    public void DemoScan_FiltersBySubscription()
    {
        var scan = DemoDataService.Scan(BackupFlowKind.RecoveryServices, [DemoDataService.SubProd]);
        Assert.All(scan.Vaults, v => Assert.Equal(DemoDataService.SubProd, v.SubscriptionId));
        Assert.All(scan.Policies, p => Assert.Equal(DemoDataService.SubProd, p.SubscriptionId));
        Assert.Single(scan.UnprotectedResources);
        Assert.Equal(2, scan.Vaults.Count);
        Assert.Equal("C", BackupRatingService.Worst(scan.Ratings)!.Grade);
    }
}
