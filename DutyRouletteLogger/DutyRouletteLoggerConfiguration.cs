using System;
using Dalamud.Configuration;

namespace DutyRouletteLogger;

[Serializable]
public class DutyRouletteLoggerConfiguration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>
    /// If false, no message is sent to the Dalamud log (/xllog), regardless of <see cref="MinimumLogLevel"/>.
    /// </summary>
    public bool IsLoggingEnabled { get; set; } = true;

    /// <summary>
    /// Minimum level that will be sent to the Dalamud log; see <see cref="LogLevel"/> for what
    /// each level includes. Defaults to <see cref="LogLevel.Warning"/>, the quietest setting.
    /// </summary>
    public LogLevel MinimumLogLevel { get; set; } = LogLevel.Warning;

    /// <summary>
    /// If true, Duty Roulette Logger watches IDutyState/ICondition/IPartyList and automatically
    /// records every instance run to the SQLite database.
    /// </summary>
    public bool EnableInstanceTracking { get; set; } = true;

    /// <summary>
    /// If true, shows a chat message when entering/leaving a tracked instance.
    /// </summary>
    public bool EnableInstanceNotifications { get; set; } = false;

    /// <summary>
    /// History retention, in days. 0 (or less) means "keep forever".
    /// Suggested UI values: 7, 30, 90, 0.
    /// </summary>
    public int HistoryRetentionDays { get; set; } = 30;

    /// <summary>
    /// Shortcut to save the configuration through PluginInterface.
    /// </summary>
    public void Save()
    {
        DutyRouletteLoggerPlugin.PluginInterface.SavePluginConfig(this);
    }
}
