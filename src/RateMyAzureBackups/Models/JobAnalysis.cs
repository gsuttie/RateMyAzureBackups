namespace RateMyAzureBackups.Models;

/// <summary>One backup job as read from Azure Resource Graph (both vault products).</summary>
public sealed class BackupJobInfo
{
    public string Id { get; set; } = string.Empty;
    public string VaultId { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public string WorkloadType { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;

    /// <summary>Completed, Failed, CompletedWithWarnings, InProgress, Cancelled...</summary>
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset? StartTime { get; set; }
    public DateTimeOffset? EndTime { get; set; }
    public TimeSpan? Duration { get; set; }
    public bool IsUserTriggered { get; set; }
    public string ErrorCode { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public string ErrorRecommendation { get; set; } = string.Empty;
    public string PolicyName { get; set; } = string.Empty;

    public bool IsFailed => Status.Equals("Failed", StringComparison.OrdinalIgnoreCase);
    public bool IsWarning => Status.Equals("CompletedWithWarnings", StringComparison.OrdinalIgnoreCase);
    public bool IsCompleted => Status.Equals("Completed", StringComparison.OrdinalIgnoreCase);
    public bool IsInProgress => Status.Equals("InProgress", StringComparison.OrdinalIgnoreCase);
    public bool IsCancelled => Status.StartsWith("Cancel", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Result of analysing the backup jobs of one vault over a time window.</summary>
public sealed class BackupJobAnalysis
{
    public BackupFlowKind Flow { get; set; }
    public string TargetId { get; set; } = string.Empty;
    public string TargetName { get; set; } = string.Empty;
    public string TimeRangeKey { get; set; } = string.Empty;
    public string TimeRangeLabel { get; set; } = string.Empty;

    public bool JobsQueryable { get; set; } = true;
    public string QueryError { get; set; } = string.Empty;

    public int TotalJobs { get; set; }
    public int CompletedCount { get; set; }
    public int FailedCount { get; set; }
    public int WarningCount { get; set; }
    public int InProgressCount { get; set; }
    public int CancelledCount { get; set; }
    public int DistinctItems { get; set; }

    /// <summary>Items that ran only successful jobs (listed for completeness, not as activities).</summary>
    public List<string> HealthyItems { get; set; } = new();

    public List<BackupJobActivity> Activities { get; set; } = new();
    public List<BackupJobChange> Changes { get; set; } = new();
    public List<string> Warnings { get; set; } = new();

    /// <summary>Percentage of finished jobs that completed (with or without warnings).</summary>
    public int SuccessRate
    {
        get
        {
            var finished = CompletedCount + WarningCount + FailedCount;
            return finished == 0 ? 100 : (int)Math.Round(100.0 * (CompletedCount + WarningCount) / finished);
        }
    }
}

/// <summary>Job activity for one item that had at least one failure or warning in the window.</summary>
public sealed class BackupJobActivity
{
    public string ItemName { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public string WorkloadType { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string PolicyName { get; set; } = string.Empty;

    public int Count { get; set; }
    public int Failed { get; set; }
    public int Succeeded { get; set; }
    public int Warnings { get; set; }
    public int ConsecutiveFailures { get; set; }

    public string ErrorCode { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public string AzureRecommendation { get; set; } = string.Empty;
    public DateTimeOffset? LastRunTime { get; set; }
    public DateTimeOffset? LastFailureTime { get; set; }
    public DateTimeOffset? LastSuccessTime { get; set; }
    public TimeSpan? AverageDuration { get; set; }
    public List<string> SampleJobIds { get; set; } = new();

    /// <summary>"Persistent failure", "Transient failure" or "Needs review".</summary>
    public string Classification { get; set; } = "Needs review";
    public string Explanation { get; set; } = string.Empty;
    public List<string> Recommendations { get; set; } = new();
}

public sealed class BackupJobChange
{
    /// <summary>"New failure", "Recovered", "Stopped", "Increased", "Decreased" or "Slower".</summary>
    public string ChangeType { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}
