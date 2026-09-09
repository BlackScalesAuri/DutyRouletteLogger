using System.Collections.Generic;

namespace DutyRouletteLogger.Models;

/// <summary>
/// Aggregated statistics computed over the currently filtered set of runs.
/// </summary>
public class InstanceStats
{
    public int TotalRuns { get; set; }

    public int CompletedRuns { get; set; }

    public int AbandonedRuns { get; set; }

    public int InProgressRuns { get; set; }

    /// <summary>Average duration (seconds) per QueueType, completed runs only.</summary>
    public Dictionary<string, double> AverageDurationSecondsByQueueType { get; set; } = new();

    /// <summary>Name of the instance with the most recorded runs.</summary>
    public string? MostPlayedInstance { get; set; }

    public int MostPlayedInstanceCount { get; set; }

    /// <summary>QueueType with the most recorded runs.</summary>
    public string? MostUsedQueueType { get; set; }

    public int MostUsedQueueTypeCount { get; set; }
}
