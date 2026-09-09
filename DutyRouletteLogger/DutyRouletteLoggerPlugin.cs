using System;
using System.IO;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using DutyRouletteLogger.Data;
using DutyRouletteLogger.Models;
using DutyRouletteLogger.Services;
using DutyRouletteLogger.Windows;

namespace DutyRouletteLogger;

public sealed class DutyRouletteLoggerPlugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IDutyState DutyState { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static ISeStringEvaluator SeStringEvaluator { get; private set; } = null!;

    private const string CommandName = "/dutyroulettelog";
    private const string CommandAlias = "/drl";

    public DutyRouletteLoggerConfiguration Configuration { get; init; }

    public LogService LogService { get; init; }

    public InstanceRepository Repository { get; init; }

    public InstanceTrackerService TrackerService { get; init; }

    public readonly WindowSystem WindowSystem = new("DutyRouletteLogger");

    private DutyRouletteLoggerUI MainUI { get; init; }

    private DutyRouletteLoggerConfigUI ConfigUI { get; init; }

    public DutyRouletteLoggerPlugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as DutyRouletteLoggerConfiguration ?? new DutyRouletteLoggerConfiguration();

        LogService = new LogService(Configuration);

        // The database lives at: %APPDATA%/XIVLauncher/pluginConfigs/DutyRouletteLogger/
        Repository = new InstanceRepository(PluginInterface.ConfigDirectory.FullName);

        TrackerService = new InstanceTrackerService(Configuration, Repository, LogService);

        MainUI = new DutyRouletteLoggerUI(this);
        WindowSystem.AddWindow(MainUI);

        ConfigUI = new DutyRouletteLoggerConfigUI(this);
        WindowSystem.AddWindow(ConfigUI);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens the run history. Use '/dutyroulettelog config' for settings, " +
                          "'/dutyroulettelog stats' or '/dutyroulettelog export' for shortcuts. " +
                          "'/drl' is a shorter alias for this command.",
        });

        // Short alias for CommandName, same handler and subcommands. Hidden from the command
        // list (ShowInHelp = false) so it doesn't show up twice in '/xlhelp'.
        CommandManager.AddHandler(CommandAlias, new CommandInfo(OnCommand)
        {
            HelpMessage = $"Alias for '{CommandName}'.",
            ShowInHelp = false,
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        LogDebug("Plugin initialized successfully");

        // Database initialization is asynchronous; we fire it and handle errors inside it
        // (InstanceRepository.InitializeAsync never lets an exception escape).
        _ = InitializeDatabaseAndMaintenanceAsync();
    }

    private async System.Threading.Tasks.Task InitializeDatabaseAndMaintenanceAsync()
    {
        await Repository.InitializeAsync().ConfigureAwait(false);

        if (Repository.IsAvailable)
        {
            await Repository.PurgeOldRunsAsync(Configuration.HistoryRetentionDays).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        WindowSystem.RemoveAllWindows();
        MainUI.Dispose();
        ConfigUI.Dispose();

        TrackerService.Dispose();
        Repository.Dispose();

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(CommandAlias);
    }

    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim();

        if (trimmed.Equals("stats", StringComparison.OrdinalIgnoreCase))
        {
            _ = PrintQuickStatsAsync();
            return;
        }

        if (trimmed.Equals("export", StringComparison.OrdinalIgnoreCase))
        {
            _ = ExportAndAnnounceAsync();
            return;
        }

        if (trimmed.Equals("config", StringComparison.OrdinalIgnoreCase))
        {
            ToggleConfigUi();
            return;
        }

        ToggleMainUi();
    }

    private async System.Threading.Tasks.Task PrintQuickStatsAsync()
    {
        if (!Repository.IsAvailable)
        {
            ChatGui.PrintError("[DutyRouletteLogger] Database is currently unavailable.");
            return;
        }

        try
        {
            var stats = await Repository.GetStatsAsync(new RunFilter()).ConfigureAwait(false);
            ChatGui.Print($"[DutyRouletteLogger] Total runs: {stats.TotalRuns} " +
                          $"(Completed: {stats.CompletedRuns}, Abandoned: {stats.AbandonedRuns}, In progress: {stats.InProgressRuns})");

            if (stats.MostPlayedInstance is not null)
            {
                ChatGui.Print($"[DutyRouletteLogger] Most played instance: {stats.MostPlayedInstance} ({stats.MostPlayedInstanceCount}x)");
            }

            if (stats.MostUsedQueueType is not null)
            {
                ChatGui.Print($"[DutyRouletteLogger] Most used queue: {stats.MostUsedQueueType} ({stats.MostUsedQueueTypeCount}x)");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DutyRouletteLogger: failed to compute quick stats.");
            ChatGui.PrintError("[DutyRouletteLogger] Failed to compute stats. See /xllog for details.");
        }
    }

    private async System.Threading.Tasks.Task ExportAndAnnounceAsync()
    {
        if (!Repository.IsAvailable)
        {
            ChatGui.PrintError("[DutyRouletteLogger] Database is currently unavailable.");
            return;
        }

        try
        {
            var exportDirectory = Path.Combine(PluginInterface.ConfigDirectory.FullName, "exports");
            var path = await Repository.ExportCsvAsync(exportDirectory, new RunFilter()).ConfigureAwait(false);
            ChatGui.Print($"[DutyRouletteLogger] Exported to: {path}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DutyRouletteLogger: failed to export CSV.");
            ChatGui.PrintError("[DutyRouletteLogger] Failed to export CSV. See /xllog for details.");
        }
    }

    public void ToggleMainUi() => MainUI.Toggle();

    public void ToggleConfigUi() => ConfigUI.Toggle();

    public void LogInfo(string message) => LogService.LogInfo(message);

    public void LogDebug(string message) => LogService.LogDebug(message);

    public void LogWarning(string message) => LogService.LogWarning(message);

    public void LogError(string message) => LogService.LogError(message);
}
