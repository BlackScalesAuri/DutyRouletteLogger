using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace DutyRouletteLogger.Windows;

/// <summary>
/// The plugin's settings window: log level, instance notifications, and history retention.
/// Separate from the main window (<see cref="DutyRouletteLoggerUI"/>), which only shows the run
/// history.
/// </summary>
public class DutyRouletteLoggerConfigUI : Window, IDisposable
{
    // Ordered from quietest to noisiest, matching what each level includes (see LogLevel.cs):
    // Warning only shows warnings/errors, Debug adds the plugin's important moves, Info adds
    // everything else on top of that.
    private static readonly (LogLevel Level, string Label)[] LogLevelOptions =
    {
        (LogLevel.Warning, "Warning (default) - warnings and errors only"),
        (LogLevel.Debug, "Debug - + important actions (enter/leave instance, etc.)"),
        (LogLevel.Info, "Info - + everything else (very verbose)"),
    };

    private static readonly string[] LogLevelLabels = Array.ConvertAll(LogLevelOptions, o => o.Label);

    private static readonly (int Days, string Label)[] RetentionOptions =
    {
        (7, "7 days"),
        (30, "30 days"),
        (90, "90 days"),
        (0, "Forever"),
    };

    // Pre-computed once: RetentionOptions is static and never changes, so there's no point
    // rebuilding this string[] with Array.ConvertAll on every ImGui frame.
    private static readonly string[] RetentionLabels = Array.ConvertAll(RetentionOptions, o => o.Label);

    private readonly DutyRouletteLoggerPlugin plugin;
    private readonly DutyRouletteLoggerConfiguration configuration;

    public DutyRouletteLoggerConfigUI(DutyRouletteLoggerPlugin plugin) : base("Settings###DutyRouletteLoggerConfigWindow")
    {
        // No NoCollapse: ImGui's default minimize button (the little arrow next to the
        // title) stays available.
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 380),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        this.plugin = plugin;
        configuration = plugin.Configuration;
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        ImGui.TextUnformatted("Logging");
        ImGui.Separator();
        ImGui.Spacing();

        var enabled = configuration.IsLoggingEnabled;
        if (ImGui.Checkbox("Enable logging", ref enabled))
        {
            configuration.IsLoggingEnabled = enabled;
            configuration.Save();
        }

        ImGui.TextUnformatted("Minimum log level:");
        ImGui.SetNextItemWidth(420);
        var levelIndex = Array.FindIndex(LogLevelOptions, o => o.Level == configuration.MinimumLogLevel);
        if (levelIndex < 0)
        {
            levelIndex = 0; // default: Warning
        }

        if (ImGui.Combo("##DutyRouletteLoggerLevelCombo", ref levelIndex, LogLevelLabels, LogLevelLabels.Length))
        {
            configuration.MinimumLogLevel = LogLevelOptions[levelIndex].Level;
            configuration.Save();
        }

        ImGui.Spacing();
        ImGui.TextWrapped("The plugin's messages go to Dalamud's native log (/xllog), respecting the toggle and level above. Error messages always show up regardless of the level chosen.");

        ImGui.Spacing();
        ImGui.TextUnformatted("Quick test:");
        if (ImGui.Button("LogWarning"))
        {
            plugin.LogWarning("Test message (Warning) generated from the UI.");
        }

        ImGui.SameLine();
        if (ImGui.Button("LogDebug"))
        {
            plugin.LogDebug("Test message (Debug) generated from the UI.");
        }

        ImGui.SameLine();
        if (ImGui.Button("LogInfo"))
        {
            plugin.LogInfo("Test message (Info) generated from the UI.");
        }

        ImGui.SameLine();
        if (ImGui.Button("LogError"))
        {
            plugin.LogError("Test message (Error) generated from the UI.");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Automatic instance tracking");
        ImGui.Separator();
        ImGui.Spacing();

        var trackingEnabled = configuration.EnableInstanceTracking;
        if (ImGui.Checkbox("Enable automatic instance logging", ref trackingEnabled))
        {
            configuration.EnableInstanceTracking = trackingEnabled;
            configuration.Save();
        }

        var notificationsEnabled = configuration.EnableInstanceNotifications;
        if (ImGui.Checkbox("Enable notifications when entering/leaving an instance", ref notificationsEnabled))
        {
            configuration.EnableInstanceNotifications = notificationsEnabled;
            configuration.Save();
        }

        ImGui.TextUnformatted("Keep history for:");
        ImGui.SetNextItemWidth(160);
        var retentionIndex = Array.FindIndex(RetentionOptions, o => o.Days == configuration.HistoryRetentionDays);
        if (retentionIndex < 0)
        {
            retentionIndex = 1; // default: 30 days
        }

        if (ImGui.Combo("##DutyRouletteLoggerRetentionCombo", ref retentionIndex, RetentionLabels, RetentionLabels.Length))
        {
            configuration.HistoryRetentionDays = RetentionOptions[retentionIndex].Days;
            configuration.Save();
        }

        ImGui.Spacing();
        ImGui.TextWrapped(
            "Each run's Queue Type is detected automatically the moment the Duty Finder " +
            "\"pops\", straight from the game's roulette sheet. A direct queue with no " +
            "roulette is recorded as \"Unknown\".");
    }
}
