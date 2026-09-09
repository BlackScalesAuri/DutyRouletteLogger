namespace DutyRouletteLogger.Models;

public enum StatusFilter
{
    All,
    Completed,
    Abandoned,
    InProgress,
}

public enum RunSortColumn
{
    EnterTimestamp,
    Duration,
    QueueType,
    InstanceName,
    PartySize,
    Status,
    InstanceType,
    Job,
}

/// <summary>
/// Filter and sort options used by the "Run History" window.
/// </summary>
public class RunFilter
{
    /// <summary>Null or empty = all Queue Types.</summary>
    public string? QueueType { get; set; }

    /// <summary>Text filter (contains) over the instance name.</summary>
    public string? InstanceNameContains { get; set; }

    /// <summary>Null = all time; otherwise, last N days.</summary>
    public int? PeriodDays { get; set; }

    public StatusFilter Status { get; set; } = StatusFilter.All;

    public RunSortColumn SortColumn { get; set; } = RunSortColumn.EnterTimestamp;

    public bool SortAscending { get; set; }
}
