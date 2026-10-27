using RateMyAzureBackups.Models;

namespace RateMyAzureBackups.Services;

/// <summary>
/// Orchestrates one flow's steps against either Azure or the built-in demo estate, so the UI never
/// has to know which one it is talking to.
/// </summary>
public sealed class BackupFlowService
{
    private readonly SessionState _state;
    private readonly BackupDiscoveryService _discovery;
    private readonly BackupJobAnalysisService _jobs;

    public BackupFlowService(SessionState state, BackupDiscoveryService discovery, BackupJobAnalysisService jobs)
    {
        _state = state;
        _discovery = discovery;
        _jobs = jobs;
    }

    public SessionState State => _state;

    public IReadOnlyList<SubscriptionInfo> Subscriptions =>
        _state.DemoMode ? DemoDataService.Subscriptions() : _state.Subscriptions;

    public string TenantName(string tenantId) =>
        _state.DemoMode ? DemoDataService.Tenants()[DemoDataService.TenantId].BestName : _state.TenantName(tenantId);

    public string SubscriptionName(string subscriptionId) =>
        Subscriptions.FirstOrDefault(s => s.SubscriptionId.Equals(subscriptionId, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? subscriptionId;

    public Task LoadSubscriptionsAsync(bool force = false, CancellationToken ct = default) =>
        _state.DemoMode ? Task.CompletedTask : _state.EnsureLoadedAsync(force, ct);

    public void EnterDemoMode()
    {
        _state.DemoMode = true;
        foreach (var flow in _state.Flows.Values)
        {
            flow.Scan = null;
            flow.JobAnalysis = null;
            flow.ScanError = null;
            flow.SelectedSubscriptionIds.Clear();
            flow.SelectedTenantId = DemoDataService.TenantId;
        }
    }

    public void ExitDemoMode()
    {
        _state.DemoMode = false;
        foreach (var flow in _state.Flows.Values)
        {
            flow.Scan = null;
            flow.JobAnalysis = null;
            flow.ScanError = null;
            flow.SelectedSubscriptionIds.Clear();
            flow.SelectedTenantId = null;
        }
    }

    public Task<Dictionary<string, BackupSubscriptionSummary>> GetSummariesAsync(
        IReadOnlyCollection<string> subscriptionIds, string? tenantId, CancellationToken ct = default) =>
        _state.DemoMode
            ? Task.FromResult(DemoDataService.Summaries())
            : _discovery.GetSummariesAsync(subscriptionIds, tenantId, ct);

    public async Task<BackupScanResult> ScanAsync(BackupFlowKind flow, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var fs = _state.For(flow);
        var ids = fs.SelectedSubscriptionIds.ToList();
        BackupScanResult result;

        if (_state.DemoMode)
        {
            progress?.Report("Loading sample estate…");
            await Task.Delay(600, ct);
            result = DemoDataService.Scan(flow, ids);
        }
        else
        {
            // Selections normally live in one tenant, but group defensively.
            var groups = Subscriptions
                .Where(s => fs.SelectedSubscriptionIds.Contains(s.SubscriptionId))
                .GroupBy(s => s.TenantId, StringComparer.OrdinalIgnoreCase)
                .ToList();
            result = new BackupScanResult { Flow = flow };
            foreach (var g in groups)
                result.Merge(await _discovery.ScanAsync(flow, g.Select(s => s.SubscriptionId).ToList(), g.Key, progress, ct));
            result.CompletedAt = DateTime.Now;
        }

        fs.Scan = result;
        fs.JobAnalysis = null;
        fs.ScanError = null;
        return result;
    }

    public string? TenantOf(BackupFlowKind flow, string targetId)
    {
        var fs = _state.For(flow);
        var subId = fs.Scan?.Vaults.FirstOrDefault(v => v.Id.Equals(targetId, StringComparison.OrdinalIgnoreCase))?.SubscriptionId;
        return Subscriptions.FirstOrDefault(s => s.SubscriptionId.Equals(subId, StringComparison.OrdinalIgnoreCase))?.TenantId
               ?? fs.SelectedTenantId;
    }

    public async Task<BackupJobAnalysis> AnalyzeJobsAsync(BackupFlowKind flow, string targetId, string targetName, string timeRange, CancellationToken ct = default)
    {
        var fs = _state.For(flow);
        BackupJobAnalysis analysis;
        if (_state.DemoMode)
        {
            await Task.Delay(700, ct);
            analysis = DemoDataService.JobAnalysis(flow, targetId, targetName, timeRange);
        }
        else
        {
            var subId = fs.Scan?.Vaults.FirstOrDefault(v => v.Id.Equals(targetId, StringComparison.OrdinalIgnoreCase))?.SubscriptionId
                        ?? fs.SelectedSubscriptionIds.FirstOrDefault() ?? "";
            analysis = await _jobs.AnalyzeAsync(flow, targetId, targetName, subId, timeRange, TenantOf(flow, targetId), ct);
        }
        fs.JobAnalysis = analysis;
        return analysis;
    }

    public BackupReportContext ReportContext(BackupFlowKind flow)
    {
        var fs = _state.For(flow);
        var subs = Subscriptions.Where(s => fs.SelectedSubscriptionIds.Contains(s.SubscriptionId))
            .OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        var tenantId = subs.Select(s => s.TenantId).FirstOrDefault(t => !string.IsNullOrEmpty(t)) ?? fs.SelectedTenantId ?? "";
        var tenantName = string.IsNullOrEmpty(tenantId) ? "" : TenantName(tenantId);
        return new BackupReportContext
        {
            Flow = flow,
            CustomerName = tenantName,
            TenantName = tenantName,
            ReportAuthor = Environment.UserName,
            ScanDate = fs.Scan?.CompletedAt ?? DateTime.Now,
            Subscriptions = subs,
            Scan = fs.Scan,
            JobAnalysis = fs.JobAnalysis
        };
    }
}
