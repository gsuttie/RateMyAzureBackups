using RateMyAzureBackups.Models;

namespace RateMyAzureBackups.Services;

/// <summary>Everything one flow (Recovery Services vaults or Backup vaults) remembers while the user moves between steps.</summary>
public sealed class FlowState
{
    public BackupFlowKind Flow { get; }
    public FlowState(BackupFlowKind flow) => Flow = flow;

    public HashSet<string> SelectedSubscriptionIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? SelectedTenantId { get; set; }

    public BackupScanResult? Scan { get; set; }
    public string? ScanError { get; set; }
    public BackupJobAnalysis? JobAnalysis { get; set; }

    public bool HasScan => Scan is not null;
}

/// <summary>
/// Per-circuit (scoped) state: subscriptions loaded once, plus independent state for each flow so a
/// user can rate their Recovery Services vaults and Backup vaults side by side without one flow resetting the other.
/// </summary>
public sealed class SessionState
{
    private readonly AzureSubscriptionService _subscriptions;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private bool _loaded;

    public SessionState(AzureSubscriptionService subscriptions)
    {
        _subscriptions = subscriptions;
        Flows = new Dictionary<BackupFlowKind, FlowState>
        {
            [BackupFlowKind.RecoveryServices] = new(BackupFlowKind.RecoveryServices),
            [BackupFlowKind.BackupVault] = new(BackupFlowKind.BackupVault),
        };
    }

    public IReadOnlyDictionary<BackupFlowKind, FlowState> Flows { get; }
    public FlowState For(BackupFlowKind flow) => Flows[flow];

    public List<SubscriptionInfo> Subscriptions { get; private set; } = new();
    public Dictionary<string, TenantInfo> Tenants { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? LoadError { get; private set; }
    public bool IsLoaded => _loaded;

    /// <summary>True when the app runs on built-in sample data instead of Azure.</summary>
    public bool DemoMode { get; set; }

    public async Task EnsureLoadedAsync(bool force = false, CancellationToken ct = default)
    {
        if (_loaded && !force) return;
        await _loadLock.WaitAsync(ct);
        try
        {
            if (_loaded && !force) return;
            LoadError = null;
            if (force) _subscriptions.Reset();
            try
            {
                Subscriptions = await _subscriptions.GetSubscriptionsAsync(ct);
                Tenants = await _subscriptions.GetTenantsAsync(ct);
                if (Subscriptions.Count == 0)
                    LoadError = "No subscriptions are visible to the current credential. Run 'az login' and make sure the account has Reader access.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Subscriptions = new();
                LoadError = "Could not list subscriptions. Run 'az login' (optionally with --tenant <id>) in a terminal, then retry. Details: " + ex.Message;
            }
            _loaded = true;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public string TenantName(string tenantId) =>
        Tenants.TryGetValue(tenantId, out var t) ? t.BestName : tenantId;

    public string SubscriptionName(string subscriptionId) =>
        Subscriptions.FirstOrDefault(s => s.SubscriptionId.Equals(subscriptionId, StringComparison.OrdinalIgnoreCase))?.DisplayName
        ?? subscriptionId;

    public IEnumerable<string> DistinctTenantIds() =>
        Subscriptions.Select(s => s.TenantId).Where(t => !string.IsNullOrEmpty(t)).Distinct(StringComparer.OrdinalIgnoreCase);
}
