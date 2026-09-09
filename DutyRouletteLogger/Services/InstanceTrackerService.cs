using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.DutyState;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using DutyRouletteLogger.Data;
using DutyRouletteLogger.Models;

namespace DutyRouletteLogger.Services;

/// <summary>
/// Watches Dalamud's services (IDutyState, ICondition, IPartyList, IPlayerState,
/// IClientState) and automatically records every instance run to the SQLite database.
///
/// QueueType comes from <see cref="ContentsFinder.QueueInfo"/> (a native game struct, via
/// FFXIVClientStructs): as soon as the Duty Finder "pops" (the <c>IClientState.CfPop</c>
/// event), <c>QueuedContentRouletteId</c> holds the Id of the Lumina "ContentRoulette" sheet
/// row that was queued — no need to read or compare any UI text. Id 0 means it didn't come
/// from a roulette (direct queue for a specific duty), in which case the run is recorded with
/// QueueType = "Unknown". Everything else (instance, timestamps, duration, party size, role,
/// completion status, level sync) is also 100% automatic.
///
/// Log levels used throughout this class: the "important moves" ([INSTANCE] ENTER/EXIT/
/// RECOMMENCE) go out as <see cref="LogService.LogDebug"/>, while the noisier queue-detection
/// internals ([QUEUE-TRACE]) go out as <see cref="LogService.LogInfo"/> — see <see cref="LogLevel"/>
/// for why Info is the more verbose of the two.
/// </summary>
public sealed class InstanceTrackerService : IDisposable
{
    private static readonly Dictionary<string, string> JobRoleMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Tanks
        ["GLA"] = "Tank", ["MRD"] = "Tank", ["PLD"] = "Tank", ["WAR"] = "Tank", ["DRK"] = "Tank", ["GNB"] = "Tank",
        // Healers
        ["CNJ"] = "Healer", ["WHM"] = "Healer", ["SCH"] = "Healer", ["AST"] = "Healer", ["SGE"] = "Healer",
        // DPS (melee, physical ranged and caster, all grouped as "DPS")
        ["PGL"] = "DPS", ["LNC"] = "DPS", ["ROG"] = "DPS", ["ARC"] = "DPS", ["THM"] = "DPS", ["ACN"] = "DPS",
        ["MNK"] = "DPS", ["DRG"] = "DPS", ["NIN"] = "DPS", ["SAM"] = "DPS", ["RPR"] = "DPS", ["VPR"] = "DPS",
        ["BRD"] = "DPS", ["MCH"] = "DPS", ["DNC"] = "DPS",
        ["BLM"] = "DPS", ["SMN"] = "DPS", ["RDM"] = "DPS", ["PCT"] = "DPS", ["BLU"] = "DPS",
    };

    private readonly DutyRouletteLoggerConfiguration configuration;
    private readonly InstanceRepository repository;
    private readonly LogService logService;

    private InstanceRun? currentRun;
    private bool nextEntryViaDutyFinder;
    private string? nextEntryQueueType;

    public InstanceTrackerService(
        DutyRouletteLoggerConfiguration configuration,
        InstanceRepository repository,
        LogService logService)
    {
        this.configuration = configuration;
        this.repository = repository;
        this.logService = logService;

        DutyRouletteLoggerPlugin.DutyState.DutyStarted += OnDutyStarted;
        DutyRouletteLoggerPlugin.DutyState.DutyWiped += OnDutyWiped;
        DutyRouletteLoggerPlugin.DutyState.DutyRecommenced += OnDutyRecommenced;
        DutyRouletteLoggerPlugin.DutyState.DutyCompleted += OnDutyCompleted;
        DutyRouletteLoggerPlugin.Condition.ConditionChange += OnConditionChange;
        DutyRouletteLoggerPlugin.ClientState.CfPop += OnCfPop;
    }

    public void Dispose()
    {
        DutyRouletteLoggerPlugin.DutyState.DutyStarted -= OnDutyStarted;
        DutyRouletteLoggerPlugin.DutyState.DutyWiped -= OnDutyWiped;
        DutyRouletteLoggerPlugin.DutyState.DutyRecommenced -= OnDutyRecommenced;
        DutyRouletteLoggerPlugin.DutyState.DutyCompleted -= OnDutyCompleted;
        DutyRouletteLoggerPlugin.Condition.ConditionChange -= OnConditionChange;
        DutyRouletteLoggerPlugin.ClientState.CfPop -= OnCfPop;
    }

    private void OnCfPop(ContentFinderCondition condition)
    {
        // Confirms that the next duty entry came from a Duty Finder "pop" (roulette or
        // normal queue), and not from the player simply walking up to the entrance.
        nextEntryViaDutyFinder = true;

        // Must be read NOW, at the exact moment of the pop: QueuedContentRouletteId is
        // state for the in-progress queue and may get reset as soon as the player actually
        // enters the duty (the same reason nextEntryViaDutyFinder is captured here and not
        // in OnDutyStarted).
        nextEntryQueueType = ResolveQueuedRouletteLabel();
    }

    /// <summary>
    /// Reads <see cref="ContentsFinder.QueueInfo"/> (a native game struct) to find out which
    /// roulette was queued, with no need to read or compare any UI text. Returns null if it
    /// didn't come from a roulette (direct queue) or if the Id didn't match any sheet row.
    /// </summary>
    private unsafe string? ResolveQueuedRouletteLabel()
    {
        try
        {
            var contentsFinder = ContentsFinder.Instance();
            var rouletteId = contentsFinder->QueueInfo.QueuedContentRouletteId;

            logService.LogInfo($"[QUEUE-TRACE] CfPop: ContentsFinder.QueueInfo.QueuedContentRouletteId={rouletteId}.");

            if (rouletteId == 0)
            {
                // 0 = didn't come from a roulette; direct queue for a specific duty.
                return null;
            }

            // Fully qualified so it doesn't collide with
            // FFXIVClientStructs.FFXIV.Client.Game.UI.ContentRoulette (a native struct,
            // unrelated to the Lumina sheet despite sharing the same name).
            var rouletteSheet = DutyRouletteLoggerPlugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ContentRoulette>();
            if (!rouletteSheet.TryGetRow(rouletteId, out var rouletteRow))
            {
                logService.LogWarning($"[QUEUE-TRACE] CfPop: QueuedContentRouletteId={rouletteId} didn't match any row in the ContentRoulette sheet.");
                return null;
            }

            var name = rouletteRow.Name.ToString();
            if (string.IsNullOrWhiteSpace(name))
            {
                logService.LogWarning($"[QUEUE-TRACE] CfPop: ContentRoulette #{rouletteId} has an empty Name.");
                return null;
            }

            logService.LogInfo($"[QUEUE-TRACE] CfPop: roulette #{rouletteId} resolved via the ContentRoulette sheet -> '{name}'.");
            return name;
        }
        catch (Exception ex)
        {
            DutyRouletteLoggerPlugin.Log.Error(ex, "DutyRouletteLogger: failed to read ContentsFinder.QueueInfo.QueuedContentRouletteId.");
            return null;
        }
    }

    private async void OnDutyStarted(IDutyStateEventArgs args)
    {
        try
        {
            if (!configuration.EnableInstanceTracking || currentRun is not null)
            {
                return;
            }

            var cfc = args.ContentFinderCondition.ValueNullable;
            var instanceName = ResolveInstanceName(args);
            var partySize = Math.Max(DutyRouletteLoggerPlugin.PartyList.Length, 1);
            var (job, role) = ResolveCurrentJobAndRole();
            var syncStatus = ResolveSyncStatus();
            var instanceType = cfc is { } cfcForType ? ResolveInstanceType(cfcForType) : "Unknown";
            var expansion = cfc is { } cfcForExpansion ? ResolveExpansion(cfcForExpansion) : "Unknown";
            var viaDutyFinder = nextEntryViaDutyFinder;
            nextEntryViaDutyFinder = false;

            // Captured at the moment of the pop (OnCfPop), via ContentsFinder.QueueInfo — see
            // ResolveQueuedRouletteLabel. Null means a direct queue (no roulette) or an entry
            // that didn't come from any pop at all (e.g. walked up to the duty entrance).
            var capturedQueueLabel = nextEntryQueueType;
            nextEntryQueueType = null;
            var queueType = capturedQueueLabel ?? "Unknown";

            if (capturedQueueLabel is null)
            {
                logService.LogWarning($"[INSTANCE] QueueType came out \"Unknown\" while entering {instanceName} (ViaDutyFinder={viaDutyFinder}). Check the [QUEUE-TRACE] lines right above (Info level) for the QueuedContentRouletteId captured at the pop.");
            }

            var run = new InstanceRun
            {
                QueueType = queueType,
                InstanceName = instanceName,
                EnterTimestampUtc = DateTime.UtcNow,
                PartySize = partySize,
                Role = role,
                Job = job,
                InstanceType = instanceType,
                Expansion = expansion,
                Notes = BuildEntryNotes(viaDutyFinder, syncStatus),
            };

            currentRun = run;

            logService.LogDebug($"[INSTANCE] ENTER: Queue={run.QueueType}, Instance={run.InstanceName}, Type={run.InstanceType}, Expansion={run.Expansion}, Party={run.PartySize}, Job={run.Job}, Sync={syncStatus}");

            if (repository.IsAvailable)
            {
                run.Id = await repository.InsertRunStartAsync(run).ConfigureAwait(false);
            }
            else
            {
                logService.LogWarning("DutyRouletteLogger: database unavailable, this run will not be persisted to history (it only appears here in the log).");
            }

            NotifyIfEnabled($"Entered {run.InstanceName} ({run.QueueType})");
        }
        catch (Exception ex)
        {
            DutyRouletteLoggerPlugin.Log.Error(ex, "DutyRouletteLogger: failed to record instance entry.");
        }
    }

    private void OnDutyWiped(IDutyStateEventArgs args)
    {
        try
        {
            if (currentRun is null)
            {
                return;
            }

            logService.LogWarning($"[INSTANCE] WIPE: Instance={currentRun.InstanceName}");

            if (repository.IsAvailable && currentRun.Id != 0)
            {
                // Not awaited on purpose (we don't want to block the event handler), but it
                // needs its own try/catch: without it, a failure here (e.g. a busy database)
                // becomes an "unobserved task exception" and disappears without a trace.
                _ = AppendWipeNoteAsync(currentRun.Id);
            }
        }
        catch (Exception ex)
        {
            DutyRouletteLoggerPlugin.Log.Error(ex, "DutyRouletteLogger: failed to record wipe.");
        }
    }

    private async System.Threading.Tasks.Task AppendWipeNoteAsync(long id)
    {
        try
        {
            await repository.AppendNoteAsync(id, "Wipe detected").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DutyRouletteLoggerPlugin.Log.Error(ex, "DutyRouletteLogger: failed to append wipe note to history.");
        }
    }

    private void OnDutyRecommenced(IDutyStateEventArgs args)
    {
        if (currentRun is not null)
        {
            logService.LogDebug($"[INSTANCE] RECOMMENCE: Instance={currentRun.InstanceName}");
        }
    }

    private void OnDutyCompleted(IDutyStateEventArgs args)
    {
        // No try/catch needed here: FinishCurrentRunAsync already protects its own body, so
        // this fire-and-forget is safe even without await (see OnConditionChange).
        _ = FinishCurrentRunAsync(isComplete: true);
    }

    private void OnConditionChange(ConditionFlag flag, bool value)
    {
        // BoundByDuty turning false without DutyCompleted having fired first means the
        // player left/abandoned the instance without completing it.
        if (flag != ConditionFlag.BoundByDuty || value || currentRun is null)
        {
            return;
        }

        _ = FinishCurrentRunAsync(isComplete: false);
    }

    private async System.Threading.Tasks.Task FinishCurrentRunAsync(bool isComplete)
    {
        var run = currentRun;
        if (run is null)
        {
            return;
        }

        currentRun = null;

        // The whole body lives inside the try: since this method is often called
        // fire-and-forget (`_ = FinishCurrentRunAsync(...)`, without await), an exception
        // escaping from here would become a silent "unobserved task exception" instead of
        // showing up in the log.
        try
        {
            var exitUtc = DateTime.UtcNow;
            var duration = (long)Math.Max(0, (exitUtc - run.EnterTimestampUtc).TotalSeconds);

            run.ExitTimestampUtc = exitUtc;
            run.RunDurationSeconds = duration;
            run.IsComplete = isComplete;

            logService.LogDebug($"[INSTANCE] EXIT: Instance={run.InstanceName}, Duration={run.FormattedDuration}, Completed={isComplete}");

            if (repository.IsAvailable && run.Id != 0)
            {
                await repository.CompleteRunAsync(run.Id, exitUtc, duration, isComplete).ConfigureAwait(false);
            }

            NotifyIfEnabled(isComplete
                ? $"Completed {run.InstanceName} in {run.FormattedDuration}"
                : $"Left {run.InstanceName} without completing it ({run.FormattedDuration})");

            // Always announced (not gated by EnableInstanceNotifications): this is an explicit,
            // one-off "how close am I" readout, not a routine enter/leave notification.
            if (isComplete && string.Equals(run.QueueType, MentorRouletteInfo.QueueTypeLabel, StringComparison.Ordinal))
            {
                _ = AnnounceMentorRouletteProgressAsync();
            }
        }
        catch (Exception ex)
        {
            DutyRouletteLoggerPlugin.Log.Error(ex, "DutyRouletteLogger: failed to finalize instance record.");
        }
    }

    /// <summary>
    /// Requests the "I Hope Mentor Will Notice Me VI" achievement's progress from the server
    /// (FFXIVClientStructs' <c>Achievement.RequestAchievementProgress</c>) and prints the result to chat once
    /// it arrives. Every native-memory touch is marshaled onto the framework thread via
    /// <see cref="IFramework"/> — unlike the UI's own polling (which already runs on that thread
    /// as part of Dalamud's Draw callback), this method can be triggered from a duty-completion
    /// event whose continuation may otherwise land on a background thread.
    /// </summary>
    private async System.Threading.Tasks.Task AnnounceMentorRouletteProgressAsync()
    {
        try
        {
            var requested = await DutyRouletteLoggerPlugin.Framework.RunOnFrameworkThread(() =>
            {
                unsafe
                {
                    var achievement = FFXIVClientStructs.FFXIV.Client.Game.UI.Achievement.Instance();
                    if (achievement is null)
                    {
                        return false;
                    }

                    achievement->RequestAchievementProgress(MentorRouletteInfo.AchievementId);
                    return true;
                }
            }).ConfigureAwait(false);

            if (!requested)
            {
                logService.LogWarning("DutyRouletteLogger: Achievement.Instance() was null, can't report Mentor Roulette progress.");
                return;
            }

            // The server answers asynchronously; poll a few times (200ms apart, ~5s total)
            // before giving up.
            for (var attempt = 0; attempt < 25; attempt++)
            {
                var (loaded, current, max) = await DutyRouletteLoggerPlugin.Framework.RunOnTick<(bool Loaded, uint Current, uint Max)>(() =>
                {
                    unsafe
                    {
                        var achievement = FFXIVClientStructs.FFXIV.Client.Game.UI.Achievement.Instance();
                        if (achievement is not null &&
                            achievement->ProgressAchievementId == MentorRouletteInfo.AchievementId &&
                            achievement->ProgressRequestState == FFXIVClientStructs.FFXIV.Client.Game.UI.Achievement.AchievementState.Loaded)
                        {
                            return (true, achievement->ProgressCurrent, achievement->ProgressMax);
                        }
                    }

                    return (false, 0u, 0u);
                }, delay: TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);

                if (loaded)
                {
                    var max2 = max > 0 ? max : MentorRouletteInfo.FallbackMax;
                    logService.LogDebug($"[INSTANCE] Mentor Roulette progress: {current}/{max2}");
                    DutyRouletteLoggerPlugin.ChatGui.Print(
                        $"[DutyRouletteLogger] I Hope Mentor Will Notice Me VI {current}/{max2}");
                    return;
                }
            }

            logService.LogWarning("DutyRouletteLogger: timed out waiting for Mentor Roulette achievement progress from the server.");
        }
        catch (Exception ex)
        {
            DutyRouletteLoggerPlugin.Log.Error(ex, "DutyRouletteLogger: failed to fetch/announce Mentor Roulette achievement progress.");
        }
    }

    private void NotifyIfEnabled(string message)
    {
        if (!configuration.EnableInstanceNotifications)
        {
            return;
        }

        try
        {
            DutyRouletteLoggerPlugin.ChatGui.Print($"[DutyRouletteLogger] {message}");
        }
        catch (Exception ex)
        {
            DutyRouletteLoggerPlugin.Log.Error(ex, "DutyRouletteLogger: failed to send chat notification.");
        }
    }

    private static string ResolveInstanceName(IDutyStateEventArgs args)
    {
        var cfc = args.ContentFinderCondition.ValueNullable;
        if (cfc is { } cfcValue)
        {
            // Addon Excel sheet #9781: the same template the native UI uses to build a duty's
            // name from a ContentFinderCondition. Going through it instead of reading
            // cfcValue.Name.ToString() raw avoids odd artifacts in names that have some
            // formatting/macro embedded (cfcValue.RowId is the parameter the template expects).
            try
            {
                var evaluatedName = DutyRouletteLoggerPlugin.SeStringEvaluator
                    .EvaluateFromAddon(9781, [cfcValue.RowId])
                    .ExtractText();

                if (!string.IsNullOrWhiteSpace(evaluatedName))
                {
                    return evaluatedName;
                }
            }
            catch (Exception ex)
            {
                DutyRouletteLoggerPlugin.Log.Error(ex, "DutyRouletteLogger: failed to format the duty name via ISeStringEvaluator, falling back to the raw name.");
            }

            // Fallback: raw name straight from the sheet, in case Addon sheet #9781 fails or
            // doesn't exist in the current game version.
            var rawName = cfcValue.Name.ToString();
            if (!string.IsNullOrWhiteSpace(rawName))
            {
                return rawName;
            }
        }

        // Fallback: not every "duty" has a valid ContentFinderCondition row (e.g. certain
        // special content). In that case we use the territory's name.
        try
        {
            var territoryId = args.TerritoryType.RowId;
            var territorySheet = DutyRouletteLoggerPlugin.DataManager.GetExcelSheet<TerritoryType>();
            if (territorySheet.TryGetRow(territoryId, out var territoryRow))
            {
                return territoryRow.PlaceName.ValueNullable?.Name.ToString() ?? $"Territory #{territoryId}";
            }
        }
        catch
        {
            // Ignored on purpose: worst case, we fall back to the generic label below.
        }

        return "Unknown instance";
    }

    /// <summary>
    /// Reads the player's current job abbreviation (e.g. "WAR") once and maps it to its role
    /// (Tank/Healer/DPS) via <see cref="JobRoleMap"/>, so both values come from a single read.
    /// </summary>
    private static (string Job, string Role) ResolveCurrentJobAndRole()
    {
        try
        {
            var abbreviation = DutyRouletteLoggerPlugin.PlayerState.ClassJob.ValueNullable?.Abbreviation.ToString();
            if (!string.IsNullOrWhiteSpace(abbreviation))
            {
                var role = JobRoleMap.TryGetValue(abbreviation, out var mappedRole) ? mappedRole : "Unknown";
                return (abbreviation, role);
            }
        }
        catch
        {
            // Ignored: if we can't read the current job, just mark both as unknown.
        }

        return ("Unknown", "Unknown");
    }

    /// <summary>
    /// Buckets the duty into one of the categories the user actually cares about (Guildhest,
    /// Trials, Normal Raid, Leveling, Dungeons), derived entirely from real
    /// ContentFinderCondition fields — never from any name/text comparison:
    /// - ContentType (a Lumina sheet reference) already distinguishes Guildhests/Trials/Raids.
    /// - Within ContentType "Dungeons", the LevelingRoulette flag further separates a run
    ///   queued as part of the Leveling roulette from a plain endgame dungeon.
    /// Anything outside those five (Ultimate Raids, Deep Dungeons, PvP, ...) falls back to its
    /// own real ContentType name instead of being forced into the wrong bucket.
    /// </summary>
    private static string ResolveInstanceType(ContentFinderCondition cfc)
    {
        try
        {
            var contentTypeName = cfc.ContentType.ValueNullable?.Name.ToString();
            if (string.IsNullOrWhiteSpace(contentTypeName))
            {
                return "Unknown";
            }

            return contentTypeName switch
            {
                "Guildhests" => "Guildhest",
                "Trials" => "Trials",
                "Raids" => "Normal Raid",
                "Dungeons" => cfc.LevelingRoulette ? "Leveling" : "Dungeons",
                _ => contentTypeName,
            };
        }
        catch (Exception ex)
        {
            DutyRouletteLoggerPlugin.Log.Error(ex, "DutyRouletteLogger: failed to resolve the instance type from ContentFinderCondition.ContentType.");
            return "Unknown";
        }
    }

    /// <summary>
    /// The expansion the duty was released in (e.g. "Heavensward"), from
    /// ContentFinderCondition.RequiredExVersion. The game's own data doesn't go any finer than
    /// this (no exact patch number like "6.3" ships in the client's Excel sheets).
    /// </summary>
    private static string ResolveExpansion(ContentFinderCondition cfc)
    {
        try
        {
            var name = cfc.RequiredExVersion.ValueNullable?.Name.ToString();
            return string.IsNullOrWhiteSpace(name) ? "Unknown" : name;
        }
        catch (Exception ex)
        {
            DutyRouletteLoggerPlugin.Log.Error(ex, "DutyRouletteLogger: failed to resolve the expansion from ContentFinderCondition.RequiredExVersion.");
            return "Unknown";
        }
    }

    /// <summary>
    /// "Synced" if the player's level is synced (lowered) for the duty, "Unsynced" otherwise.
    /// Comes straight from <see cref="Dalamud.Plugin.Services.IPlayerState.IsLevelSynced"/>,
    /// the same data the native UI uses for the sync indicator next to the character's name.
    /// </summary>
    private static string ResolveSyncStatus()
    {
        try
        {
            return DutyRouletteLoggerPlugin.PlayerState.IsLevelSynced ? "Synced" : "Unsynced";
        }
        catch
        {
            // Ignored: if we can't read the sync state, just mark it as unknown.
        }

        return "Unknown";
    }

    private static string BuildEntryNotes(bool viaDutyFinder, string syncStatus)
    {
        var parts = new List<string>();
        if (viaDutyFinder)
        {
            parts.Add("Via Duty Finder");
        }

        parts.Add(syncStatus);

        return string.Join(" | ", parts);
    }
}
