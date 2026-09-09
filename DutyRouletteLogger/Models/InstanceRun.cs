using System;

namespace DutyRouletteLogger.Models;

/// <summary>
/// Represents one instance run, mirroring the InstanceRuns table.
/// </summary>
public class InstanceRun
{
    public long Id { get; set; }

    /// <summary>
    /// E.g.: "Mentor Roulette", "Leveling Roulette". See the remarks in
    /// <see cref="Services.InstanceTrackerService"/> about why this field can be
    /// "Unknown" for entries that didn't come from a roulette pop.
    /// </summary>
    public string QueueType { get; set; } = "Unknown";

    /// <summary>Instance name, e.g. "Under The Armor".</summary>
    public string InstanceName { get; set; } = string.Empty;

    /// <summary>UTC timestamp of entering the instance.</summary>
    public DateTime EnterTimestampUtc { get; set; }

    /// <summary>UTC timestamp of leaving the instance (null while still inside).</summary>
    public DateTime? ExitTimestampUtc { get; set; }

    /// <summary>Total duration in seconds (computed on exit).</summary>
    public long? RunDurationSeconds { get; set; }

    /// <summary>Party size at the time of entry (4, 8, 24...).</summary>
    public int PartySize { get; set; }

    /// <summary>Whether the run was completed (true) or abandoned/incomplete (false).</summary>
    public bool IsComplete { get; set; }

    /// <summary>Player's role at the time of entry: "Tank", "Healer", "DPS", or "Unknown".</summary>
    public string Role { get; set; } = "Unknown";

    /// <summary>Player's job abbreviation at the time of entry, e.g. "WAR", "SCH", or "Unknown".</summary>
    public string Job { get; set; } = "Unknown";

    /// <summary>
    /// The duty's own category, e.g. "Dungeons", "Guildhest", "Trials", "Leveling", "Normal Raid"
    /// (derived from ContentFinderCondition's ContentType/roulette-eligibility flags — see
    /// <see cref="Services.InstanceTrackerService.ResolveInstanceType"/>), or "Unknown".
    /// </summary>
    public string InstanceType { get; set; } = "Unknown";

    /// <summary>Expansion the duty was released in, e.g. "Heavensward" (from ContentFinderCondition.RequiredExVersion), or "Unknown".</summary>
    public string Expansion { get; set; } = "Unknown";

    /// <summary>Free-form field for notes (e.g. "Wipe detected").</summary>
    public string? Notes { get; set; }

    /// <summary>Convenience: duration formatted as "12m 23s".</summary>
    public string FormattedDuration
    {
        get
        {
            if (RunDurationSeconds is not { } seconds)
            {
                return "-";
            }

            var span = TimeSpan.FromSeconds(seconds);
            return span.Hours > 0
                ? $"{span.Hours}h {span.Minutes}m {span.Seconds}s"
                : $"{span.Minutes}m {span.Seconds}s";
        }
    }

    /// <summary>Convenience: status label for display in the UI.</summary>
    public string StatusLabel => ExitTimestampUtc is null
        ? "In Progress"
        : IsComplete
            ? "Completed"
            : "Abandoned";
}
