using System.Text.Json;
using RateMyAzureBackups.Models;
using RateMyAzureBackups.Services;
using Xunit;

namespace RateMyAzureBackups.Tests;

/// <summary>Parsing of every Azure JSON shape the scan reads, using captures of real Resource Graph / ARM output.</summary>
public class BackupDiscoveryParsingTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private const string Sub = "51f7af08-becd-4f4d-acba-f30d9ea9a085";
    private const string VaultId = "/subscriptions/" + Sub + "/resourceGroups/rg-backup/providers/Microsoft.RecoveryServices/vaults/rsv-prd";

    [Fact]
    public void ParseRecoveryServicesVault_ReadsRedundancySecurityAndMonitoring()
    {
        var row = J($$"""
        {
          "id": "{{VaultId}}", "name": "rsv-prd", "type": "microsoft.recoveryservices/vaults", "location": "westeurope",
          "resourceGroup": "rg-backup", "subscriptionId": "{{Sub}}", "sku": { "name": "RS0", "tier": "Standard" },
          "properties": {
            "publicNetworkAccess": "Disabled",
            "redundancySettings": { "crossRegionRestore": "Enabled", "standardTierStorageRedundancy": "GeoRedundant" },
            "secureScore": "Adequate",
            "securitySettings": {
              "multiUserAuthorization": "Disabled",
              "softDeleteSettings": { "enhancedSecurityState": "Enabled", "softDeleteRetentionPeriodInDays": 14, "softDeleteState": "Enabled" },
              "immutabilitySettings": { "state": "Unlocked" }
            },
            "monitoringSettings": { "azureMonitorAlertSettings": { "alertsForAllJobFailures": "Enabled" } }
          }
        }
        """);
        var v = BackupDiscoveryService.ParseRecoveryServicesVault(row);

        Assert.Equal("rsv-prd", v.Name);
        Assert.Equal(BackupFlowKind.RecoveryServices, v.Kind);
        Assert.Equal("RS0", v.SkuName);
        Assert.Equal("GeoRedundant", v.StorageRedundancy);
        Assert.True(v.IsGeoRedundant);
        Assert.Equal("GRS", v.RedundancyLabel);
        Assert.True(v.CrossRegionRestore);
        Assert.Equal("Enabled", v.SoftDeleteState);
        Assert.True(v.SoftDeleteEnabled);
        Assert.Equal(14, v.SoftDeleteRetentionDays);
        Assert.True(v.ImmutabilityUnlocked);
        Assert.False(v.MultiUserAuthorization);
        Assert.Equal("Disabled", v.PublicNetworkAccess);
        Assert.True(v.AlertsForAllJobFailures);
        Assert.Equal("Adequate", v.SecureScore);
    }

    [Fact]
    public void ParseRecoveryServicesVault_OlderShape_LeavesSettingsUnknown_ThenFallbacksFillThem()
    {
        var v = BackupDiscoveryService.ParseRecoveryServicesVault(J($$"""{ "id": "{{VaultId}}", "name": "old", "subscriptionId": "{{Sub}}", "properties": { "provisioningState": "Succeeded" } }"""));
        Assert.Equal("", v.StorageRedundancy);
        Assert.Null(v.CrossRegionRestore);
        Assert.Null(v.SoftDeleteEnabled);
        Assert.Null(v.MultiUserAuthorization);

        BackupDiscoveryService.ApplyStorageConfig(v, J("""{ "properties": { "storageType": "LocallyRedundant", "crossRegionRestoreFlag": false } }"""));
        BackupDiscoveryService.ApplyVaultConfig(v, J("""{ "properties": { "softDeleteFeatureState": "Disabled" } }"""));
        Assert.True(v.IsLocallyRedundant);
        Assert.False(v.CrossRegionRestore);
        Assert.False(v.SoftDeleteEnabled);
    }

    [Fact]
    public void ParseProtectedItem_Vm_FromResourceGraph()
    {
        var row = J($$"""
        {
          "id": "{{VaultId}}/backupFabrics/Azure/protectionContainers/IaasVMContainer;iaasvmcontainerv2;rg-vm;vm-service/protectedItems/VM;iaasvmcontainerv2;rg-vm;vm-service",
          "name": "VM;iaasvmcontainerv2;rg-vm;vm-service", "subscriptionId": "{{Sub}}",
          "properties": {
            "backupManagementType": "AzureIaasVM", "containerName": "iaasvmcontainerv2;rg-vm;vm-service", "friendlyName": "vm-service",
            "healthStatus": "Passed", "lastBackupStatus": "Completed", "lastBackupTime": "2026-10-25T20:04:23.1952833Z",
            "policyId": "{{VaultId}}/backupPolicies/EnhancedPolicy", "policyName": null, "policyInfo": { "name": "EnhancedPolicy" },
            "protectedItemType": "Microsoft.Compute/virtualMachines", "protectionState": "Protected", "protectionStatus": "Healthy",
            "sourceResourceId": "/subscriptions/{{Sub}}/resourceGroups/rg-vm/providers/Microsoft.Compute/virtualMachines/vm-service",
            "workloadType": "VM"
          }
        }
        """);
        var i = BackupDiscoveryService.ParseProtectedItem(row);

        Assert.Equal(VaultId, i.VaultId);
        Assert.Equal("vm-service", i.Display);
        Assert.Equal("Azure VM", i.WorkloadType);
        Assert.Equal("Protected", i.ProtectionState);
        Assert.True(i.IsActive);
        Assert.True(i.LastBackupSucceeded);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 20, 04, 23, TimeSpan.Zero), i.LastBackupTime!.Value.ToUniversalTime().AddTicks(-i.LastBackupTime.Value.Ticks % TimeSpan.TicksPerSecond));
        Assert.Equal("EnhancedPolicy", i.PolicyName);
        Assert.EndsWith("/virtualMachines/vm-service", i.SourceResourceId);
    }

    [Fact]
    public void ParseProtectedItem_SqlDatabase_StoppedWithServerName()
    {
        var row = J($$"""
        {
          "id": "{{VaultId}}/backupFabrics/Azure/protectionContainers/VMAppContainer;Compute;rg-vm;sqlvm/protectedItems/SQLDataBase;MSSQLSERVER;Octopus",
          "name": "SQLDataBase;MSSQLSERVER;Octopus", "subscriptionId": "{{Sub}}",
          "properties": {
            "backupManagementType": "AzureWorkload", "friendlyName": "Octopus", "parentName": "MSSQLSERVER", "serverName": "sqlvm",
            "lastBackupStatus": "Unhealthy", "protectedItemType": "AzureVmWorkloadSQLDatabase", "protectionState": "ProtectionStopped",
            "workloadType": "SQLDataBase", "sourceResourceId": "/subscriptions/{{Sub}}/resourceGroups/rg-vm/providers/Microsoft.Compute/virtualMachines/sqlvm"
          }
        }
        """);
        var i = BackupDiscoveryService.ParseProtectedItem(row);
        Assert.Equal("SQL in Azure VM", i.WorkloadType);
        Assert.Equal("Octopus (MSSQLSERVER)", i.Display);
        Assert.True(i.IsStopped);
        Assert.False(i.IsActive);
        Assert.False(i.LastBackupSucceeded);
        Assert.Null(i.LastBackupTime);
    }

    [Fact]
    public void ParseProtectedItem_FileShare_UsesSourceStorageAccount()
    {
        var row = J($$"""
        {
          "id": "{{VaultId}}/backupFabrics/Azure/protectionContainers/StorageContainer;Storage;rg;st/protectedItems/AzureFileShare;ABC",
          "name": "AzureFileShare;ABC", "subscriptionId": "{{Sub}}",
          "properties": { "backupManagementType": "AzureStorage", "friendlyName": "kpn-backup", "protectedItemType": "AzureFileShareProtectedItem",
            "protectionState": "Protected", "lastBackupStatus": "Completed", "lastBackupTime": "2026-10-25T20:04:23Z", "workloadType": "AzureFileShare",
            "isScheduledForDeferredDelete": true }
        }
        """);
        var i = BackupDiscoveryService.ParseProtectedItem(row);
        Assert.Equal("Azure Files", i.WorkloadType);
        Assert.Equal("SoftDeleted", i.ProtectionState); // scheduled for deferred delete overrides Protected
        Assert.True(i.IsStopped);
    }

    [Fact]
    public void ParseRecoveryServicesPolicy_HourlyV2WithLongTermRetention()
    {
        var row = J($$"""
        {
          "id": "{{VaultId}}/backupPolicies/EnhancedPolicy", "name": "EnhancedPolicy", "subscriptionId": "{{Sub}}", "resourceGroup": "rg-backup",
          "properties": {
            "backupManagementType": "AzureIaasVM", "instantRpRetentionRangeInDays": 2, "policyType": "V2", "protectedItemsCount": 2, "timeZone": "UTC", "workLoadType": "VM",
            "retentionPolicy": { "retentionPolicyType": "LongTermRetentionPolicy",
              "dailySchedule": { "retentionDuration": { "count": 30, "durationType": "Days" } },
              "weeklySchedule": { "retentionDuration": { "count": 12, "durationType": "Weeks" } },
              "monthlySchedule": null, "yearlySchedule": { "retentionDuration": { "count": 5, "durationType": "Years" } } },
            "schedulePolicy": { "schedulePolicyType": "SimpleSchedulePolicyV2", "scheduleRunFrequency": "Hourly",
              "hourlySchedule": { "interval": 4, "scheduleWindowDuration": 12, "scheduleWindowStartTime": "2023-10-17T08:00:00Z" } }
          }
        }
        """);
        var p = BackupDiscoveryService.ParseRecoveryServicesPolicy(row);

        Assert.Equal(VaultId, p.VaultId);
        Assert.Equal("Azure VM", p.WorkloadType);
        Assert.Equal("Hourly", p.ScheduleFrequency);
        Assert.Equal(4, p.ScheduleIntervalHours);
        Assert.True(p.IsDailyOrBetter);
        Assert.Equal(30, p.DailyRetentionDays);
        Assert.Equal(12, p.WeeklyRetentionWeeks);
        Assert.Null(p.MonthlyRetentionMonths);
        Assert.Equal(5, p.YearlyRetentionYears);
        Assert.True(p.HasLongTermRetention);
        Assert.Equal(2, p.InstantRpRetentionDays);
        Assert.Equal(2, p.ProtectedItemCount);
        Assert.Equal(5 * 365, p.MaxRetentionDays);
    }

    [Fact]
    public void ParseRecoveryServicesPolicy_DailyAndWeeklyAndSqlSubPolicies()
    {
        var daily = BackupDiscoveryService.ParseRecoveryServicesPolicy(J($$"""
        { "id": "{{VaultId}}/backupPolicies/Daily", "name": "Daily", "properties": { "backupManagementType": "AzureIaasVM", "workLoadType": "VM", "timeZone": "W. Europe Standard Time",
          "schedulePolicy": { "scheduleRunFrequency": "Daily", "scheduleRunTimes": ["2023-01-01T02:00:00Z"] },
          "retentionPolicy": { "retentionPolicyType": "LongTermRetentionPolicy", "dailySchedule": { "retentionDuration": { "count": 7, "durationType": "Days" } } } } }
        """));
        Assert.Equal("Daily", daily.ScheduleFrequency);
        Assert.Contains("02:00", daily.ScheduleSummary);
        Assert.Equal(7, daily.ShortTermRetentionDays);
        Assert.False(daily.HasLongTermRetention);

        var weekly = BackupDiscoveryService.ParseRecoveryServicesPolicy(J($$"""
        { "id": "{{VaultId}}/backupPolicies/Weekly", "name": "Weekly", "properties": { "backupManagementType": "AzureIaasVM",
          "schedulePolicy": { "scheduleRunFrequency": "Weekly", "scheduleRunDays": ["Sunday"], "scheduleRunTimes": ["2023-01-01T02:00:00Z"] },
          "retentionPolicy": { "retentionPolicyType": "LongTermRetentionPolicy", "weeklySchedule": { "retentionDuration": { "count": 4, "durationType": "Weeks" } } } } }
        """));
        Assert.Equal("Weekly", weekly.ScheduleFrequency);
        Assert.Equal("Weekly on Sunday", weekly.ScheduleSummary);
        Assert.Null(weekly.DailyRetentionDays);
        Assert.Equal(28, weekly.ShortTermRetentionDays);

        var sql = BackupDiscoveryService.ParseRecoveryServicesPolicy(J($$"""
        { "id": "{{VaultId}}/backupPolicies/Sql", "name": "Sql", "properties": { "backupManagementType": "AzureWorkload", "workLoadType": "SQLDataBase",
          "subProtectionPolicy": [
            { "policyType": "Full", "schedulePolicy": { "scheduleRunFrequency": "Daily", "scheduleRunTimes": ["2023-01-01T23:00:00Z"] },
              "retentionPolicy": { "retentionPolicyType": "LongTermRetentionPolicy", "dailySchedule": { "retentionDuration": { "count": 35, "durationType": "Days" } } } },
            { "policyType": "Log", "schedulePolicy": { "schedulePolicyType": "LogSchedulePolicy", "scheduleFrequencyInMins": 60 },
              "retentionPolicy": { "retentionPolicyType": "SimpleRetentionPolicy", "retentionDuration": { "count": 5, "durationType": "Weeks" } } } ] } }
        """));
        Assert.Equal("SQL in Azure VM", sql.WorkloadType);
        Assert.Equal("Daily", sql.ScheduleFrequency);
        Assert.Equal(35, sql.DailyRetentionDays);
        Assert.Equal(60, sql.LogBackupFrequencyMinutes);
        Assert.Equal(35, sql.LogRetentionDays);
    }

    private const string BvId = "/subscriptions/" + Sub + "/resourceGroups/rg-backup/providers/Microsoft.DataProtection/backupVaults/bv-prd";

    [Fact]
    public void ParseBackupVault_ReadsDataProtectionShape()
    {
        var row = J($$"""
        {
          "id": "{{BvId}}", "name": "bv-prd", "location": "westeurope", "resourceGroup": "rg-backup", "subscriptionId": "{{Sub}}",
          "properties": {
            "storageSettings": [ { "datastoreType": "VaultStore", "type": "LocallyRedundant" } ],
            "securitySettings": { "softDeleteSettings": { "state": "Off", "retentionDurationInDays": 14.0 }, "immutabilitySettings": { "state": "Locked" } },
            "featureSettings": { "crossRegionRestoreSettings": { "state": "Disabled" } },
            "monitoringSettings": { "azureMonitorAlertSettings": { "alertsForAllJobFailures": "Disabled" } },
            "isVaultProtectedByResourceGuard": true, "provisioningState": "Succeeded"
          }
        }
        """);
        var v = BackupDiscoveryService.ParseBackupVault(row);
        Assert.Equal(BackupFlowKind.BackupVault, v.Kind);
        Assert.True(v.IsLocallyRedundant);
        Assert.Equal("LRS", v.RedundancyLabel);
        Assert.False(v.CrossRegionRestore);
        Assert.False(v.SoftDeleteEnabled);
        Assert.Equal(14, v.SoftDeleteRetentionDays);
        Assert.True(v.ImmutabilityLocked);
        Assert.True(v.MultiUserAuthorization);
        Assert.False(v.AlertsForAllJobFailures);
    }

    [Fact]
    public void ParseBackupInstance_ReadsStateAndError()
    {
        var row = J($$"""
        {
          "id": "{{BvId}}/backupInstances/aks-prd-1234", "name": "aks-prd-1234", "subscriptionId": "{{Sub}}",
          "properties": {
            "friendlyName": "aks-prd", "currentProtectionState": "ProtectionError",
            "dataSourceInfo": { "datasourceType": "Microsoft.ContainerService/managedClusters", "resourceID": "/subscriptions/{{Sub}}/resourceGroups/rg/providers/Microsoft.ContainerService/managedClusters/aks-prd", "resourceName": "aks-prd" },
            "policyInfo": { "policyId": "{{BvId}}/backupPolicies/AksDaily" },
            "protectionStatus": { "status": "ProtectionError", "errorDetails": { "code": "UserErrorMissingPermissionsOnBackupVault", "message": "Missing permissions." } }
          }
        }
        """);
        var i = BackupDiscoveryService.ParseBackupInstance(row);
        Assert.Equal(BvId, i.VaultId);
        Assert.Equal("aks-prd", i.Display);
        Assert.Equal("AKS cluster", i.WorkloadType);
        Assert.True(i.IsErrored);
        Assert.Equal("AksDaily", i.PolicyName);
        Assert.Equal("UserErrorMissingPermissionsOnBackupVault", i.ErrorCode);
        Assert.Null(i.LastBackupTime);
    }

    [Fact]
    public void ParseBackupVaultPolicy_ReadsScheduleAndLifecycles()
    {
        var row = J($$"""
        {
          "id": "{{BvId}}/backupPolicies/DiskDaily", "name": "DiskDaily", "subscriptionId": "{{Sub}}",
          "properties": { "objectType": "BackupPolicy", "datasourceTypes": [ "Microsoft.Compute/disks" ],
            "policyRules": [
              { "objectType": "AzureBackupRule", "name": "BackupHourly", "trigger": { "objectType": "ScheduleBasedTriggerContext", "schedule": { "repeatingTimeIntervals": [ "R/2024-01-01T08:00:00+00:00/PT4H" ] } } },
              { "objectType": "AzureRetentionRule", "name": "Default", "isDefault": true, "lifecycles": [ { "deleteAfter": { "objectType": "AbsoluteDeleteOption", "duration": "P30D" }, "sourceDataStore": { "dataStoreType": "OperationalStore" } } ] },
              { "objectType": "AzureRetentionRule", "name": "Weekly", "isDefault": false, "lifecycles": [ { "deleteAfter": { "objectType": "AbsoluteDeleteOption", "duration": "P12W" } } ] }
            ] }
        }
        """);
        var p = BackupDiscoveryService.ParseBackupVaultPolicy(row);
        Assert.Equal("Managed disk", p.WorkloadType);
        Assert.Equal("Hourly", p.ScheduleFrequency);
        Assert.Equal(4, p.ScheduleIntervalHours);
        Assert.Equal(30, p.DailyRetentionDays);
        Assert.Equal(12, p.WeeklyRetentionWeeks);
        Assert.True(p.HasLongTermRetention);

        var blob = BackupDiscoveryService.ParseBackupVaultPolicy(J($$"""
        { "id": "{{BvId}}/backupPolicies/Blob", "name": "Blob", "properties": { "datasourceTypes": [ "Microsoft.Storage/storageAccounts/blobServices" ],
          "policyRules": [ { "objectType": "AzureRetentionRule", "name": "Default", "isDefault": true, "lifecycles": [ { "deleteAfter": { "duration": "P7D" } } ] } ] } }
        """));
        Assert.Equal("Blob storage", blob.WorkloadType);
        Assert.Equal("Continuous", blob.ScheduleFrequency);
        Assert.Equal(7, blob.DailyRetentionDays);
    }

    [Theory]
    [InlineData("P30D", 720)]
    [InlineData("P12W", 12 * 7 * 24)]
    [InlineData("P1M", 720)]
    [InlineData("P2Y", 2 * 365 * 24)]
    [InlineData("PT4H", 4)]
    [InlineData("P1D", 24)]
    [InlineData("P1DT12H", 36)]
    public void IsoDurationHours_ParsesCommonDurations(string duration, double hours) =>
        Assert.Equal(hours, BackupDiscoveryService.IsoDurationHours(duration));

    [Fact]
    public void IsoDurationHours_RejectsGarbage()
    {
        Assert.Null(BackupDiscoveryService.IsoDurationHours(""));
        Assert.Null(BackupDiscoveryService.IsoDurationHours("30 days"));
    }

    [Theory]
    [InlineData("VM", "AzureIaasVM", "Azure VM")]
    [InlineData("Microsoft.Compute/virtualMachines", "", "Azure VM")]
    [InlineData("AzureFileShare", "AzureStorage", "Azure Files")]
    [InlineData("SQLDataBase", "AzureWorkload", "SQL in Azure VM")]
    [InlineData("SAPHanaDatabase", "AzureWorkload", "SAP HANA in Azure VM")]
    [InlineData("Microsoft.Storage/storageAccounts/blobServices", "AzureBackupVault", "Blob storage")]
    [InlineData("Microsoft.Compute/disks", "AzureBackupVault", "Managed disk")]
    [InlineData("Microsoft.DBforPostgreSQL/flexibleServers", "AzureBackupVault", "PostgreSQL flexible server")]
    [InlineData("Microsoft.ContainerService/managedClusters", "AzureBackupVault", "AKS cluster")]
    [InlineData("", "MAB", "MARS agent (files and folders)")]
    public void WorkloadLabel_MapsAzureSpellings(string raw, string bmt, string expected) =>
        Assert.Equal(expected, BackupDiscoveryService.WorkloadLabel(raw, bmt));

    [Fact]
    public void VaultIdOf_StripsChildSegments()
    {
        Assert.Equal(VaultId, BackupDiscoveryService.VaultIdOf(VaultId + "/backupFabrics/Azure/protectionContainers/x/protectedItems/y"));
        Assert.Equal(VaultId, BackupDiscoveryService.VaultIdOf(VaultId + "/backupPolicies/p"));
        Assert.Equal(VaultId, BackupDiscoveryService.VaultIdOf(VaultId + "/backupJobs/1"));
        Assert.Equal(BvId, BackupDiscoveryService.VaultIdOf(BvId + "/backupInstances/i"));
        Assert.Equal(VaultId, BackupDiscoveryService.VaultIdOf(VaultId));
    }

    [Fact]
    public void AttachItems_PutsItemsOnVaults_AndWarnsAboutOrphans()
    {
        var result = new BackupScanResult { Flow = BackupFlowKind.RecoveryServices, Vaults = [new BackupVaultInfo { Id = VaultId, Name = "rsv-prd" }] };
        BackupDiscoveryService.AttachItems(result,
        [
            new ProtectedItemInfo { Id = VaultId + "/backupFabrics/x", VaultId = VaultId, FriendlyName = "b" },
            new ProtectedItemInfo { Id = VaultId + "/backupFabrics/y", VaultId = VaultId.ToUpperInvariant(), FriendlyName = "a" },
            new ProtectedItemInfo { Id = "/other/backupFabrics/z", VaultId = "/other", FriendlyName = "c" },
        ]);
        Assert.Equal(2, result.Vaults[0].Items.Count);
        Assert.Equal(["a", "b"], result.Vaults[0].Items.Select(i => i.Display));
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void ApplyUnprotectedVms_ListsVmsWithoutAProtectedItem()
    {
        var protectedVm = $"/subscriptions/{Sub}/resourceGroups/RG-VM/providers/Microsoft.Compute/virtualMachines/vm-a";
        var result = new BackupScanResult
        {
            Flow = BackupFlowKind.RecoveryServices,
            Vaults = [new BackupVaultInfo { Id = VaultId, Items = [new ProtectedItemInfo { WorkloadType = "Azure VM", SourceResourceId = protectedVm.ToLowerInvariant() }] }]
        };
        BackupDiscoveryService.ApplyUnprotectedVms(result,
        [
            J($$"""{ "id": "{{protectedVm}}", "name": "vm-a", "subscriptionId": "{{Sub}}", "resourceGroup": "rg-vm", "location": "westeurope", "powerState": "PowerState/running" }"""),
            J($$"""{ "id": "/subscriptions/{{Sub}}/resourceGroups/rg-vm/providers/Microsoft.Compute/virtualMachines/vm-b", "name": "vm-b", "subscriptionId": "{{Sub}}", "resourceGroup": "rg-vm", "location": "westeurope", "powerState": "PowerState/deallocated" }"""),
        ]);
        Assert.Equal(2, result.VirtualMachineCount);
        var u = Assert.Single(result.UnprotectedResources);
        Assert.Equal("vm-b", u.Name);
        Assert.Equal("deallocated", u.PowerState);
    }

    [Fact]
    public void ApplyUnprotectedVms_IgnoresVmsAndResourceGroupsNamedRestore()
    {
        var result = new BackupScanResult { Flow = BackupFlowKind.RecoveryServices };
        BackupDiscoveryService.ApplyUnprotectedVms(result,
        [
            J($$"""{ "id": "/subscriptions/{{Sub}}/resourceGroups/rg-vm/providers/Microsoft.Compute/virtualMachines/vm-b", "name": "vm-b", "subscriptionId": "{{Sub}}", "resourceGroup": "rg-vm", "location": "westeurope", "powerState": "PowerState/running" }"""),
            J($$"""{ "id": "/subscriptions/{{Sub}}/resourceGroups/rg-intercept-restore-tests/providers/Microsoft.Compute/virtualMachines/vm-c", "name": "vm-c", "subscriptionId": "{{Sub}}", "resourceGroup": "rg-intercept-restore-tests", "location": "westeurope", "powerState": "PowerState/deallocated" }"""),
            J($$"""{ "id": "/subscriptions/{{Sub}}/resourceGroups/rg-vm/providers/Microsoft.Compute/virtualMachines/Restore-vm-service", "name": "Restore-vm-service", "subscriptionId": "{{Sub}}", "resourceGroup": "rg-vm", "location": "westeurope", "powerState": "PowerState/deallocated" }"""),
        ]);
        Assert.Equal(1, result.VirtualMachineCount);
        var u = Assert.Single(result.UnprotectedResources);
        Assert.Equal("vm-b", u.Name);
    }

    [Fact]
    public void ApplyLastBackups_UsesLatestFinishedBackupJobPerInstance()
    {
        var itemId = BvId + "/backupInstances/i1";
        var result = new BackupScanResult
        {
            Flow = BackupFlowKind.BackupVault,
            Vaults = [new BackupVaultInfo { Id = BvId, Items = [new ProtectedItemInfo { Id = itemId, VaultId = BvId }] }]
        };
        var t0 = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
        BackupDiscoveryService.ApplyLastBackups(result,
        [
            new BackupJobInfo { ItemId = itemId, Operation = "Backup", Status = "Completed", StartTime = t0, EndTime = t0.AddMinutes(10) },
            new BackupJobInfo { ItemId = itemId, Operation = "Backup", Status = "Failed", StartTime = t0.AddDays(1), EndTime = t0.AddDays(1).AddMinutes(2), ErrorCode = "X" },
            new BackupJobInfo { ItemId = itemId, Operation = "Backup", Status = "InProgress", StartTime = t0.AddDays(2) },
            new BackupJobInfo { ItemId = itemId, Operation = "Restore", Status = "Completed", StartTime = t0.AddDays(3) },
        ]);
        var item = result.Vaults[0].Items[0];
        Assert.Equal("Failed", item.LastBackupStatus);
        Assert.Equal(t0.AddDays(1).AddMinutes(2), item.LastBackupTime);
        Assert.Equal("X", item.ErrorCode);
    }

    [Fact]
    public void ParseDiagnosticSettings_DetectsBackupCategoriesAndDestinations()
    {
        var settings = new[]
        {
            J("""
            { "properties": { "workspaceId": "/subscriptions/s/resourceGroups/rg/providers/Microsoft.OperationalInsights/workspaces/law-1",
                "logs": [ { "category": "CoreAzureBackup", "enabled": true }, { "category": "AddonAzureBackupJobs", "enabled": true } ] } }
            """),
            J("""
            { "properties": { "storageAccountId": "/subscriptions/s/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/stlogs",
                "logs": [ { "categoryGroup": "allLogs", "enabled": true } ] } }
            """),
            J("""{ "properties": { "eventHubName": "hub", "logs": [ { "category": "CoreAzureBackup", "enabled": false } ] } }"""),
        };
        var d = BackupDiscoveryService.ParseDiagnosticSettings(settings);
        Assert.True(d.Enabled);
        Assert.Equal(["Log Analytics: law-1", "Storage: stlogs"], d.Destinations);
        Assert.Single(d.WorkspaceIds);

        var none = BackupDiscoveryService.ParseDiagnosticSettings([J("""{ "properties": { "logs": [ { "category": "AzureSiteRecoveryJobs", "enabled": true } ] } }""")]);
        Assert.False(none.Enabled);
        Assert.Empty(none.Destinations);
    }

    [Fact]
    public void Queries_TargetTheRightTypes()
    {
        Assert.Contains("microsoft.recoveryservices/vaults'", BackupDiscoveryService.VaultQuery(BackupFlowKind.RecoveryServices));
        Assert.Contains("microsoft.dataprotection/backupvaults'", BackupDiscoveryService.VaultQuery(BackupFlowKind.BackupVault));
        Assert.StartsWith("RecoveryServicesResources", BackupDiscoveryService.ItemQuery(BackupFlowKind.RecoveryServices));
        Assert.Contains("protecteditems", BackupDiscoveryService.ItemQuery(BackupFlowKind.RecoveryServices));
        Assert.Contains("backupinstances", BackupDiscoveryService.ItemQuery(BackupFlowKind.BackupVault));
        Assert.Contains("backuppolicies", BackupDiscoveryService.PolicyQuery(BackupFlowKind.BackupVault));
    }
}
