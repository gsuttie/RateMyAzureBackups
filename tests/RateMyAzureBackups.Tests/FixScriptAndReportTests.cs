using System.IO.Compression;
using RateMyAzureBackups.Models;
using RateMyAzureBackups.Services;
using Xunit;

namespace RateMyAzureBackups.Tests;

public class FixScriptTests
{
    private const string VaultId = "/subscriptions/s/resourceGroups/rg/providers/Microsoft.RecoveryServices/vaults/rsv-prod";

    private static BackupJobActivity Persistent() => new()
    {
        ItemName = "vm-fileserver", ItemId = VaultId + "/backupFabrics/Azure/protectionContainers/c/protectedItems/VM;x;rg;vm-fileserver",
        WorkloadType = "Azure VM", Operation = "Backup", Classification = "Persistent failure", ErrorCode = "UserErrorGuestAgentStatusUnavailable", Failed = 3
    };

    private static BackupJobActivity DeletedSource() => new()
    {
        ItemName = "vm-gone", ItemId = VaultId + "/backupFabrics/Azure/protectionContainers/c/protectedItems/VM;x;rg;vm-gone",
        WorkloadType = "Azure VM", Operation = "Backup", Classification = "Persistent failure", ErrorCode = "UserErrorVmNotFoundV2", Failed = 5
    };

    private static BackupJobActivity Transient() => new()
    {
        ItemName = "ContosoERP (vm-sql-01)", ItemId = VaultId + "/backupFabrics/Azure/protectionContainers/c/protectedItems/SQLDataBase;MSSQLSERVER;ContosoERP",
        WorkloadType = "SQL in Azure VM", Operation = "Backup", Classification = "Transient failure", Failed = 1, Succeeded = 6
    };

    [Fact]
    public void RecoveryServices_PersistentScript_RunsAdhocBackupAndListsJobs()
    {
        var (explanation, options) = FixScriptService.Describe(Persistent(), BackupFlowKind.RecoveryServices);
        Assert.Contains("on-demand backup", explanation);
        Assert.Contains(options, o => o.Key == "adhocBackup" && o.Enabled);
        Assert.Contains(options, o => o.Key == "stopProtection" && !o.Enabled);

        var script = FixScriptService.Generate(Persistent(), BackupFlowKind.RecoveryServices, options);
        Assert.Contains("Az.RecoveryServices", script);
        Assert.Contains("$vaultName = \"rsv-prod\"", script);
        Assert.Contains("$itemName = \"vm-fileserver\"", script);
        Assert.Contains("-BackupManagementType AzureVM -WorkloadType AzureVM", script);
        Assert.Contains("Backup-AzRecoveryServicesBackupItem", script);
        Assert.Contains("Get-AzRecoveryServicesBackupJob", script);
        Assert.DoesNotContain("Disable-AzRecoveryServicesBackupProtection", script);
        Assert.Contains("# Last error: UserErrorGuestAgentStatusUnavailable", script);
    }

    [Fact]
    public void RecoveryServices_DeletedSource_OffersStopProtection()
    {
        var (_, options) = FixScriptService.Describe(DeletedSource(), BackupFlowKind.RecoveryServices);
        Assert.Contains(options, o => o.Key == "stopProtection" && o.Enabled);
        Assert.Contains(options, o => o.Key == "adhocBackup" && !o.Enabled);
        var script = FixScriptService.Generate(DeletedSource(), BackupFlowKind.RecoveryServices, options);
        Assert.Contains("Disable-AzRecoveryServicesBackupProtection", script);
        Assert.DoesNotContain("Backup-AzRecoveryServicesBackupItem", script);
    }

    [Fact]
    public void RecoveryServices_SqlWorkload_UsesWorkloadTypes()
    {
        var (_, options) = FixScriptService.Describe(Transient(), BackupFlowKind.RecoveryServices);
        var script = FixScriptService.Generate(Transient(), BackupFlowKind.RecoveryServices, options);
        Assert.Contains("-BackupManagementType AzureWorkload -WorkloadType MSSQL", script);
        Assert.Contains("Get-AzRecoveryServicesBackupRecoveryPoint", script);
    }

    [Fact]
    public void BackupVault_Script_UsesDataProtectionCmdlets()
    {
        var a = new BackupJobActivity
        {
            ItemName = "aks-contoso-shared", ItemId = "/subscriptions/s/resourceGroups/rg/providers/Microsoft.DataProtection/backupVaults/bv-shared/backupInstances/aks-1",
            WorkloadType = "AKS cluster", Operation = "Backup", Classification = "Persistent failure", ErrorCode = "UserErrorMissingPermissionsOnBackupVault", Failed = 4
        };
        var (_, options) = FixScriptService.Describe(a, BackupFlowKind.BackupVault);
        var script = FixScriptService.Generate(a, BackupFlowKind.BackupVault, options);
        Assert.Contains("Az.DataProtection", script);
        Assert.Contains("$vaultName = \"bv-shared\"", script);
        Assert.Contains("Get-AzDataProtectionBackupInstance", script);
        Assert.Contains("Backup-AzDataProtectionBackupInstanceAdhoc", script);
        Assert.Contains("Get-AzDataProtectionJob", script);
    }

    [Fact]
    public void NoOptions_ProducesPlaceholderScript()
    {
        var (_, options) = FixScriptService.Describe(Persistent(), BackupFlowKind.RecoveryServices);
        var none = options.Select(o => o with { Enabled = false });
        var script = FixScriptService.Generate(Persistent(), BackupFlowKind.RecoveryServices, none);
        Assert.Contains("No options selected", script);
        Assert.DoesNotContain("Backup-AzRecoveryServicesBackupItem", script);
    }

    [Fact]
    public void NeedsReview_ShowsItemState()
    {
        var a = new BackupJobActivity { ItemName = "vm-intranet", ItemId = VaultId + "/backupFabrics/x", WorkloadType = "Azure VM", Classification = "Needs review", Warnings = 2 };
        var (_, options) = FixScriptService.Describe(a, BackupFlowKind.RecoveryServices);
        Assert.Contains(options, o => o.Key == "showItem" && o.Enabled);
        Assert.Contains("ProtectionState", FixScriptService.Generate(a, BackupFlowKind.RecoveryServices, options));
    }
}

public class ReportTests
{
    private static BackupReportContext Context(BackupFlowKind flow, bool withJobs)
    {
        var scan = DemoDataService.Scan(flow, [DemoDataService.SubProd, DemoDataService.SubShared]);
        // Prefer a vault with something wrong so the job analysis has activities to report.
        var target = scan.Vaults.FirstOrDefault(v => v.Items.Any(i => !i.IsActive || i.LastBackupSucceeded == false)) ?? scan.Vaults.First(v => v.Items.Count > 0);
        return new BackupReportContext
        {
            Flow = flow,
            CustomerName = "Contoso Ltd (demo)",
            TenantName = "Contoso Ltd (demo)",
            ReportAuthor = "tester",
            ScanDate = new DateTime(2026, 10, 26, 9, 0, 0),
            SubscriptionLines = ["Contoso Production (1111)", "Contoso Shared Services (2222)"],
            Scan = scan,
            JobAnalysis = withJobs ? DemoDataService.JobAnalysis(flow, target.Id, target.Name, "7d") : null
        };
    }

    [Fact]
    public void SummaryHtml_ContainsRatingEstateAndFindings()
    {
        var html = BackupReportService.SummaryHtml(Context(BackupFlowKind.RecoveryServices, withJobs: true));
        Assert.Contains("Backup Summary Report", html);
        Assert.Contains("Recovery Services vault backup rating", html);
        Assert.Contains("rsv-contoso-prod", html);
        Assert.Contains("Virtual machines without backup", html);
        Assert.Contains("Vault configuration", html);
        Assert.Contains("Policy compliance", html);
        Assert.Contains("Recommendations", html);
        Assert.Contains("Backup jobs for Recovery Services vault", html);
        Assert.Contains("UNACCEPTABLE", html); // worst vault is the empty dev vault
    }

    [Fact]
    public void FindingsHtml_ContainsEverySection()
    {
        var html = BackupReportService.FindingsHtml(Context(BackupFlowKind.BackupVault, withJobs: true));
        Assert.Contains("Detailed Backup Findings", html);
        Assert.Contains("Detailed findings", html);
        Assert.Contains("Backup policy inventory", html);
        Assert.Contains("Protected items", html);
        Assert.Contains("bv-contoso-shared", html);
        Assert.Contains("6.3 Failure assessment", html);
        Assert.Contains("6.4 Remediation recommendations", html);
        Assert.DoesNotContain("Virtual machines without backup", html);
    }

    [Fact]
    public void Reports_WorkWithoutJobAnalysis_AndWithoutScan()
    {
        var html = BackupReportService.FindingsHtml(Context(BackupFlowKind.RecoveryServices, withJobs: false));
        Assert.Contains("no job analysis has been run", html);

        var empty = new BackupReportContext { Flow = BackupFlowKind.RecoveryServices };
        Assert.Contains("No scan data", BackupReportService.SummaryHtml(empty));
        Assert.NotEmpty(BackupReportService.SummaryDocx(empty));
    }

    [Fact]
    public void Docx_IsAValidWordPackage_WithTheContent()
    {
        var bytes = BackupReportService.FindingsDocx(Context(BackupFlowKind.RecoveryServices, withJobs: true));
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var doc = zip.GetEntry("word/document.xml");
        Assert.NotNull(doc);
        using var reader = new StreamReader(doc!.Open());
        var xml = reader.ReadToEnd();
        Assert.Contains("Detailed Backup Findings", xml);
        Assert.Contains("rsv-contoso-legacy", xml);
        Assert.Contains("Persistent failure", xml);
    }

    [Fact]
    public void FileName_IsSafeAndDescriptive()
    {
        var ctx = Context(BackupFlowKind.RecoveryServices, withJobs: false);
        Assert.Equal("ContosoLtddemo-RecoveryServices-Backup-Summary-20261026.docx", BackupReportService.FileName(ctx, findings: false));
        Assert.Equal("ContosoLtddemo-BackupVault-Backup-Findings-20261026.docx", BackupReportService.FileName(Context(BackupFlowKind.BackupVault, false), findings: true));
    }
}
