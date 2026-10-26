using RateMyAzureBackups.Models;
using RateMyAzureBackups.Services;
using Xunit;

namespace RateMyAzureBackups.Tests;

public class BackupFindingsBuilderTests
{
    [Fact]
    public void EveryFindingHasAFix_AndIsOrderedBySeverity()
    {
        var rsv = DemoDataService.RecoveryServicesEstate();
        var bv = DemoDataService.BackupVaultEstate();
        var findings = BackupFindingsBuilder.Build(rsv, DemoDataService.Now).Concat(BackupFindingsBuilder.Build(bv, DemoDataService.Now)).ToList();

        Assert.NotEmpty(findings);
        Assert.All(findings, f => Assert.False(string.IsNullOrWhiteSpace(f.Fix)));
        Assert.All(findings, f => Assert.NotEqual(BackupFindingsBuilder.FixFor("nonexistent"), f.Fix));

        var order = BackupFindingsBuilder.Build(rsv, DemoDataService.Now).Select(f => BackupFindingsBuilder.SeverityOrder(f.Severity)).ToList();
        Assert.Equal(order.OrderBy(o => o), order);
    }

    [Fact]
    public void RecoveryServices_ProducesExpectedCategories()
    {
        var findings = BackupFindingsBuilder.Build(DemoDataService.RecoveryServicesEstate(), DemoDataService.Now);
        var categories = findings.Select(f => f.Category).ToHashSet();

        Assert.Contains("Empty vault", categories);            // rsv-contoso-dev
        Assert.Contains("Protection error", categories);       // ReportingDW
        Assert.Contains("Backup failed", categories);          // vm-fileserver
        Assert.Contains("Stale backup", categories);           // vm-batch-01 (3 days, daily policy)
        Assert.Contains("Locally redundant", categories);      // rsv-contoso-legacy
        Assert.Contains("No cross-region restore", categories);
        Assert.Contains("Soft delete disabled", categories);
        Assert.Contains("Immutability off", categories);
        Assert.Contains("No MUA", categories);
        Assert.Contains("Public network access", categories);
        Assert.Contains("Job alerts disabled", categories);
        Assert.Contains("No diagnostics", categories);
        Assert.Contains("Weekly schedule", categories);        // WeeklyPolicy
        Assert.Contains("Short retention", categories);        // WeeklyPolicy 14d, HourlyLogBackup 7d
        Assert.Contains("No long-term retention", categories); // DefaultPolicy
        Assert.Contains("Unused policy", categories);          // dev DefaultPolicy
        Assert.Contains("Unprotected VM", categories);

        Assert.Equal(2, findings.Count(f => f.Category == "Unprotected VM"));
        Assert.All(findings.Where(f => f.Category is "Unprotected VM" or "Backup failed" or "Protection error" or "Locally redundant" or "Soft delete disabled"),
            f => Assert.Equal("High", f.Severity));
        Assert.DoesNotContain(findings, f => f.Category == "Empty vault" && f.ResourceName != "rsv-contoso-dev");
    }

    [Fact]
    public void BackupVault_ProducesExpectedCategories()
    {
        var findings = BackupFindingsBuilder.Build(DemoDataService.BackupVaultEstate(), DemoDataService.Now);
        var categories = findings.Select(f => f.Category).ToHashSet();

        Assert.Contains("Protection error", categories);
        Assert.Contains("Locally redundant", categories);
        Assert.Contains("Soft delete disabled", categories);
        Assert.Contains("Weekly schedule", categories);
        Assert.Contains("Short retention", categories);
        Assert.DoesNotContain("Unprotected VM", categories);
        Assert.Equal("Info", findings.Single(f => f.Category == "Immutability off" && f.ResourceName == "bv-contoso-prod").Severity); // unlocked = info
    }

    [Fact]
    public void PerfectEstate_HasNoFindings()
    {
        const string vaultId = "/subscriptions/s/resourceGroups/rg/providers/Microsoft.RecoveryServices/vaults/v";
        var policyId = vaultId + "/backupPolicies/p";
        var result = new BackupScanResult
        {
            Flow = BackupFlowKind.RecoveryServices,
            Vaults =
            [
                new BackupVaultInfo
                {
                    Id = vaultId, Name = "v", StorageRedundancy = "GeoRedundant", CrossRegionRestore = true, SoftDeleteState = "AlwaysOn",
                    ImmutabilityState = "Locked", MultiUserAuthorization = true, PublicNetworkAccess = "Disabled", AlertsForAllJobFailures = true,
                    DiagnosticsEnabled = true, LogDestinations = ["Log Analytics: law"],
                    Items = [new ProtectedItemInfo { Id = vaultId + "/backupFabrics/x", VaultId = vaultId, FriendlyName = "vm", WorkloadType = "Azure VM", ProtectionState = "Protected", LastBackupStatus = "Completed", LastBackupTime = DemoDataService.Now.AddHours(-2), PolicyId = policyId }]
                }
            ],
            Policies = [new BackupPolicyInfo { Id = policyId, Name = "p", VaultId = vaultId, ScheduleFrequency = "Daily", DailyRetentionDays = 30, WeeklyRetentionWeeks = 4, ProtectedItemCount = 1 }]
        };
        Assert.Empty(BackupFindingsBuilder.Build(result, DemoDataService.Now));
    }

    [Fact]
    public void Age_FormatsHoursThenDays()
    {
        var now = DemoDataService.Now;
        Assert.Equal("5h ago", BackupFindingsBuilder.Age(now, now.AddHours(-5)));
        Assert.Equal("3d ago", BackupFindingsBuilder.Age(now, now.AddDays(-3)));
    }
}
