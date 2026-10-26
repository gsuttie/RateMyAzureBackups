# RateMyAzureBackups

Find every backup vault in an Azure tenant, grade each one **A to F**, and see exactly which change lifts the grade.

RateMyAzureBackups is a read-only Blazor Server app with two separate flows, one per vault product:

| Flow | What it rates |
|---|---|
| **Recovery Services vaults** | Azure VMs, SQL Server and SAP HANA in Azure VMs, Azure Files and MARS protected items. Every item is checked for a healthy protection state and a recent successful recovery point, and every virtual machine in scope that no vault protects is called out. |
| **Backup vaults** | Data Protection vaults with blob storage, managed disk, PostgreSQL, MySQL and AKS backup instances. The last backup of each instance is derived from its recent jobs. |

Each flow is three steps: **Scope** (tenant and subscriptions) → **Grades** (A–F per vault, findings with fixes, policy inventory, Word reports) → **Jobs** (backup job analysis with persistent / transient failure assessment, changes versus the previous period and fix scripts).

It is the sibling of [RateMyWAF](https://github.com/gsuttie/ratemywaf): same layout, theme, wizard and report pipeline, applied to Azure Backup.

## Scoring model

100 points across six weighted categories, mapped to a letter, with hard caps. The model is the same for both vault products.

| Category | Points | What earns it |
|---|---|---|
| Protection health | 30 | Every protected item actively protected — none with protection stopped or in error (pro-rated) |
| Backup freshness | 20 | A successful recovery point within the expected window for every active item (36 h for daily and hourly policies, 8 days for weekly; pro-rated) |
| Redundancy & restore | 20 | Geo-redundant storage 12 (zone-redundant 8, locally redundant 0) · Cross Region Restore 8 |
| Vault security | 15 | Soft delete on 5 · immutable vault locked 5 (unlocked 3) · multi-user authorization 3 · public network access disabled 2 |
| Policy hygiene | 10 | Daily or more frequent schedule 4 · at least 30 days retention 3 · weekly/monthly/yearly retention 3 |
| Monitoring & alerts | 5 | Built-in Azure Monitor alerts for all job failures 3 · diagnostics to Log Analytics 2 |

Grade bands: **A** ≥ 90 · **B** ≥ 75 · **C** ≥ 60 · **D** ≥ 45 · **E** ≥ 30 · **F** below.

Caps: nothing protected → **F** · any item with protection stopped or in error → at most **D** (a workload with no new recovery points is the backup equivalent of an unprotected endpoint) · any last backup failed → at most **C** · soft delete disabled → at most **C** · locally redundant storage → at most **B**.

Settings the scan cannot read (older vault API shapes, missing permissions) score half points and never trigger a cap. Every improvement is listed with the points it gains and the grade it would produce, so the biggest win is always visible.

## Running it

Prerequisites: .NET 10 SDK, Azure CLI, and a signed-in account with Reader access to the subscriptions you want to scan.

```powershell
az login                       # optionally: az login --tenant <tenant-id>
cd src/RateMyAzureBackups
dotnet run
```

Open <http://localhost:5220>. Pick a flow, choose the subscriptions (only those containing that vault product are highlighted), scan, and read the grades.

No Azure access? Click **Try with sample data** on the home page. Both flows then run on a realistic built-in estate that covers every grade band, without calling Azure.

### Authentication

> [!WARNING]
> **RateMyAzureBackups has no login of its own.** Every visitor to the site uses the same Azure credential the app was started with, and one shared token cache serves them all. Run it on your own workstation, or put it behind authentication you control (for example App Service Easy Auth or a reverse proxy). If you host it on App Service with a managed identity and no auth in front, **anyone who can reach the URL can read every subscription that identity has access to.**

The app uses `AzureCliCredential` by default. To run it somewhere without the Azure CLI (App Service, a container with a managed identity), set `Azure:Credential` to `Default` in `appsettings.json` or as the environment variable `Azure__Credential=Default` to use `DefaultAzureCredential`.

The credential needs **Reader** on the subscriptions. Backup Reader is not required: everything comes from Azure Resource Graph and ARM `GET` calls.

### What the scan reads

Everything is read-only, through Azure Resource Graph and ARM `GET` calls:

- Recovery Services vaults and Backup vaults: storage redundancy, Cross Region Restore, soft delete, immutability, multi-user authorization, public network access, built-in job-failure alerts (with ARM fallbacks to `backupstorageconfig` and `backupconfig` for vaults whose Resource Graph shape predates these settings)
- Protected items and backup instances (`RecoveryServicesResources` table): protection state, last backup status and time, source resource, policy
- Backup policies: schedule frequency, daily/weekly/monthly/yearly retention, instant restore snapshots, SQL log backup cadence
- Backup jobs (`RecoveryServicesResources` table, roughly the last month) for the Jobs step and for the last-backup status of Backup vault instances
- Diagnostic settings on every vault (which backup log categories are enabled and where they go)
- Every virtual machine in the scanned subscriptions, to list the ones no Recovery Services vault protects

RateMyAzureBackups never modifies a resource. The fix scripts it generates are PowerShell (`Az.RecoveryServices` / `Az.DataProtection`) you review and run yourself.

## Tests

```powershell
dotnet test
```

The suite covers the rating engine (the perfect vault is the reference case and must keep producing 100/A; every cap and improvement path has a test), JSON parsing of every Azure shape (captured from real Resource Graph output), findings, job classification and change detection, fix scripts, Word/HTML reports, the sample estate, and the Blazor components (bUnit).

### Browser walkthrough

`tests/e2e/walkthrough.mjs` drives the running app in headless Chromium through both flows on sample data (scan, grades, unprotected VMs, findings, policy detail, reports, job analysis, fix scripts, reload, dark theme, mobile) and fails on any browser error:

```powershell
dotnet run --project src/RateMyAzureBackups   # in one terminal
cd tests/e2e; npm install; npx playwright install chromium; npm test
```

Set `PW_EXE` to a Chromium executable to use an existing browser instead of the one Playwright downloads.

## Project layout

```
src/RateMyAzureBackups/
  Components/Pages       Home, RecoveryServicesPage, BackupVaultsPage
  Components/Shared      BackupFlow (wizard), ScopeStep, GradesStep, JobsStep, PolicyDetail, modals, badges
  Services/
    BackupRatingService      The A–F engine (shared by both vault products)
    BackupDiscoveryService   Resource Graph / ARM discovery, vault config, unprotected VMs, diagnostics
    BackupFindingsBuilder    Findings with remediation guidance
    BackupJobAnalysisService Job history, failure classification, changes vs previous period
    BackupReportService      Summary and detailed-findings reports (docx + HTML preview)
    FixScriptService         PowerShell remediation scripts per vault product
    DemoDataService          The sample estate
tests/RateMyAzureBackups.Tests    xUnit + bUnit
tests/e2e                         Playwright walkthrough
```

## License

MIT — see [LICENSE](LICENSE).
