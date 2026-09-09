using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using DutyRouletteLogger.Data;
using DutyRouletteLogger.Models;
using DutyRouletteLogger.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace DutyRouletteLogger.Windows;

/// <summary>
/// The plugin's main window: shows only the run history (filters, statistics, and the
/// table). The plugin's settings live in the separate <see cref="DutyRouletteLoggerConfigUI"/> window.
/// </summary>
public class DutyRouletteLoggerUI : Window, IDisposable
{
    private static readonly (int? Days, string Label)[] PeriodOptions =
    {
        (null, "All"),
        (7, "Last 7 days"),
        (30, "Last 30 days"),
    };

    // Pre-computed once: PeriodOptions/StatusOptions are static and never change, so there's
    // no point rebuilding these string[] with Array.ConvertAll on every ImGui frame (Draw()
    // runs ~60x/s while the window is visible).
    private static readonly string[] PeriodLabels = Array.ConvertAll(PeriodOptions, o => o.Label);

    private static readonly (StatusFilter Status, string Label)[] StatusOptions =
    {
        (StatusFilter.All, "All"),
        (StatusFilter.Completed, "Completed"),
        (StatusFilter.Abandoned, "Abandoned"),
        (StatusFilter.InProgress, "In Progress"),
    };

    private static readonly string[] StatusLabels = Array.ConvertAll(StatusOptions, o => o.Label);

    private readonly DutyRouletteLoggerPlugin plugin;

    // --- Run history state ---
    private readonly RunFilter filter = new();
    private List<InstanceRun> cachedRuns = new();
    private InstanceStats cachedStats = new();
    private List<string> queueTypeOptions = new();
    // Labels for the Queue Type combo, recomputed only when queueTypeOptions changes (on
    // every refresh), not every frame — see RefreshAsync.
    private string[] queueTypeFilterLabels = { "All" };
    private bool hasLoadedHistoryOnce;
    private bool isRefreshing;
    private string instanceNameFilterBuffer = string.Empty;
    private int queueTypeFilterIndex;
    private int periodFilterIndex;
    private int statusFilterIndex;
    private string? lastExportMessage;

    // --- Mentor Roulette achievement progress ---
    private uint mentorRouletteCurrent;
    private uint mentorRouletteMax = MentorRouletteInfo.FallbackMax;
    private bool mentorRouletteLoaded;
    private bool mentorRouletteRequestPending;

    public DutyRouletteLoggerUI(DutyRouletteLoggerPlugin plugin) : base("Run History###DutyRouletteLoggerMainWindow")
    {
        // No NoCollapse: ImGui's default minimize button (the little arrow next to the
        // title) stays available.
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(620, 460),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        this.plugin = plugin;

        // Refreshes the table automatically as soon as a new run is recorded, instead of
        // waiting for the user to click "Refresh" or reopen the window.
        this.plugin.Repository.RunInserted += OnRunInserted;
    }

    public void Dispose()
    {
        plugin.Repository.RunInserted -= OnRunInserted;
    }

    // RunInserted can fire on a background thread (see InstanceRepository.RunInserted);
    // TriggerRefresh only flips a flag and kicks off its own async fetch, so it's safe to call
    // from here without touching ImGui state directly.
    private void OnRunInserted(long id) => TriggerRefresh();

    public override void Draw()
    {
        if (!hasLoadedHistoryOnce)
        {
            hasLoadedHistoryOnce = true;
            TriggerRefresh();
        }

        // The achievement progress request is answered asynchronously by the server; we poll
        // for it every frame (cheap: a couple of field reads) instead of blocking the refresh on it.
        PollMentorRouletteProgress();

        DrawHistoryTab();
    }

    // =====================================================================
    // Run History
    // =====================================================================

    private void DrawHistoryTab()
    {
        DrawMentorRouletteProgress();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (!plugin.Repository.IsAvailable)
        {
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f),
                "Database unavailable. Instance events only show up in the Dalamud log " +
                "(/xllog) until the plugin is next loaded — nothing is persisted here.");
            ImGui.Spacing();
        }

        DrawToolbar();
        DrawCollapsingSection("Filters", DrawFilters);
        DrawCollapsingSection("Statistics (respecting the filters above)", DrawStatsPanel);
        ImGui.Separator();
        DrawRunsTable();
    }

    // CollapsingHeader normally paints a gray background even at rest (ImGuiCol.Header); pushed
    // to transparent here so Filters/Statistics read as plain compact section labels. No
    // Spacing() before/after either: the caller chains these back-to-back for a tight layout.
    private static void DrawCollapsingSection(string label, Action drawContent)
    {
        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0, 0, 0, 0));
        var isOpen = ImGui.CollapsingHeader(label, ImGuiTreeNodeFlags.DefaultOpen);
        ImGui.PopStyleColor();

        if (isOpen)
        {
            drawContent();
        }
    }

    // Always visible regardless of the Filters header's collapsed state, so refreshing/exporting
    // never requires expanding the filters first.
    private void DrawToolbar()
    {
        if (ImGui.Button("Refresh"))
        {
            TriggerRefresh();
        }

        ImGui.SameLine();
        if (ImGui.Button("Export CSV"))
        {
            TriggerExport();
        }

        if (lastExportMessage is not null)
        {
            ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), lastExportMessage);
        }
    }

    private void DrawMentorRouletteProgress()
    {
        ImGui.TextUnformatted("Mentor Roulette progress (\"I Hope Mentor Will Notice Me VI\")");

        if (!mentorRouletteLoaded)
        {
            ImGui.TextUnformatted(mentorRouletteRequestPending
                ? "Fetching achievement progress from the server..."
                : "Achievement progress unavailable.");
            return;
        }

        var fraction = mentorRouletteMax > 0
            ? Math.Clamp((float)mentorRouletteCurrent / mentorRouletteMax, 0f, 1f)
            : 0f;
        var overlay = $"{mentorRouletteCurrent}/{mentorRouletteMax} Duty Roulette: Mentor clears";
        ImGui.ProgressBar(fraction, new Vector2(-1, 0), overlay);
    }

    private void DrawFilters()
    {
        var filterChanged = false;

        ImGui.SetNextItemWidth(200);
        if (queueTypeFilterIndex >= queueTypeFilterLabels.Length)
        {
            // The Queue Type list can shrink between an async refresh and the next frame
            // (e.g. after deleting the last run of a given type). Besides resetting the
            // combo's visual index to "All", we also need to clear the actual filter and
            // force a refresh — otherwise the combo shows "All" while the real filter keeps
            // restricting by a Queue Type that no longer exists in the list.
            queueTypeFilterIndex = 0;
            filter.QueueType = null;
            filterChanged = true;
        }

        if (ImGui.Combo("Queue Type##FilterQueueType", ref queueTypeFilterIndex, queueTypeFilterLabels, queueTypeFilterLabels.Length))
        {
            filter.QueueType = queueTypeFilterIndex == 0 ? null : queueTypeOptions[queueTypeFilterIndex - 1];
            filterChanged = true;
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(200);
        if (ImGui.InputTextWithHint("##FilterInstanceName", "Filter by instance...", ref instanceNameFilterBuffer, 100))
        {
            filter.InstanceNameContains = instanceNameFilterBuffer;
            filterChanged = true;
        }

        ImGui.SetNextItemWidth(160);
        if (ImGui.Combo("Period##FilterPeriod", ref periodFilterIndex, PeriodLabels, PeriodLabels.Length))
        {
            filter.PeriodDays = PeriodOptions[periodFilterIndex].Days;
            filterChanged = true;
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(160);
        if (ImGui.Combo("Status##FilterStatus", ref statusFilterIndex, StatusLabels, StatusLabels.Length))
        {
            filter.Status = StatusOptions[statusFilterIndex].Status;
            filterChanged = true;
        }

        if (filterChanged)
        {
            TriggerRefresh();
        }
    }

    private string[] BuildQueueTypeFilterLabels()
    {
        var labels = new string[queueTypeOptions.Count + 1];
        labels[0] = "All";
        for (var i = 0; i < queueTypeOptions.Count; i++)
        {
            labels[i + 1] = queueTypeOptions[i];
        }

        return labels;
    }

    private void DrawStatsPanel()
    {
        ImGui.Indent();

        ImGui.TextUnformatted($"Total runs: {cachedStats.TotalRuns} " +
                               $"(Completed: {cachedStats.CompletedRuns}, Abandoned: {cachedStats.AbandonedRuns}, In progress: {cachedStats.InProgressRuns})");

        if (cachedStats.MostPlayedInstance is not null)
        {
            ImGui.TextUnformatted($"Most played instance: {cachedStats.MostPlayedInstance} ({cachedStats.MostPlayedInstanceCount}x)");
        }

        if (cachedStats.MostUsedQueueType is not null)
        {
            ImGui.TextUnformatted($"Most used queue: {cachedStats.MostUsedQueueType} ({cachedStats.MostUsedQueueTypeCount}x)");
        }

        if (cachedStats.AverageDurationSecondsByQueueType.Count > 0)
        {
            ImGui.TextUnformatted("Average duration per Queue Type (completed runs only):");
            ImGui.Indent();
            foreach (var (queueType, avgSeconds) in cachedStats.AverageDurationSecondsByQueueType)
            {
                var span = TimeSpan.FromSeconds(avgSeconds);
                ImGui.TextUnformatted($"{queueType}: {span.Minutes}m {span.Seconds}s");
            }

            ImGui.Unindent();
        }

        ImGui.Unindent();
    }

    private void DrawRunsTable()
    {
        if (isRefreshing)
        {
            ImGui.TextUnformatted("Loading...");
            return;
        }

        if (cachedRuns.Count == 0)
        {
            ImGui.TextUnformatted("No runs found for the current filters.");
            return;
        }

        // SizingFixedFit: each column starts out sized to the largest content it received
        // (instead of splitting the space evenly among all of them), so the initial width
        // already fits the data; ImGuiTableFlags.Resizable still allows manual resizing on
        // top of that.
        const ImGuiTableFlags tableFlags = ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders |
                                            ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable |
                                            ImGuiTableFlags.SizingFixedFit;

        if (!ImGui.BeginTable("DutyRouletteLoggerRunsTable", 10, tableFlags, new Vector2(0, 320)))
        {
            return;
        }

        ImGui.TableSetupColumn("Queue Type");
        ImGui.TableSetupColumn("Instance");
        ImGui.TableSetupColumn("Type");
        ImGui.TableSetupColumn("Expansion");
        ImGui.TableSetupColumn("Date/Time");
        ImGui.TableSetupColumn("Duration");
        ImGui.TableSetupColumn("Party");
        ImGui.TableSetupColumn("Job");
        ImGui.TableSetupColumn("Status");
        ImGui.TableSetupColumn("Actions");

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        DrawSortableHeader(0, "Queue Type", RunSortColumn.QueueType);
        DrawSortableHeader(1, "Instance", RunSortColumn.InstanceName);
        DrawSortableHeader(2, "Type", RunSortColumn.InstanceType);
        ImGui.TableSetColumnIndex(3);
        ImGui.TextUnformatted("Expansion");
        DrawSortableHeader(4, "Date/Time", RunSortColumn.EnterTimestamp);
        DrawSortableHeader(5, "Duration", RunSortColumn.Duration);
        DrawSortableHeader(6, "Party", RunSortColumn.PartySize);
        DrawSortableHeader(7, "Job", RunSortColumn.Job);
        DrawSortableHeader(8, "Status", RunSortColumn.Status);
        ImGui.TableSetColumnIndex(9);
        ImGui.TextUnformatted("Actions");

        foreach (var run in cachedRuns)
        {
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(run.QueueType);

            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(run.InstanceName);

            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(run.InstanceType);

            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(run.Expansion);

            ImGui.TableSetColumnIndex(4);
            ImGui.TextUnformatted(run.EnterTimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));

            ImGui.TableSetColumnIndex(5);
            ImGui.TextUnformatted(run.FormattedDuration);

            ImGui.TableSetColumnIndex(6);
            ImGui.TextUnformatted(run.PartySize.ToString());

            ImGui.TableSetColumnIndex(7);
            ImGui.TextUnformatted(run.Job);

            ImGui.TableSetColumnIndex(8);
            var color = run.StatusLabel switch
            {
                "Completed" => new Vector4(0.4f, 1f, 0.4f, 1f),
                "Abandoned" => new Vector4(1f, 0.4f, 0.4f, 1f),
                _ => new Vector4(1f, 0.85f, 0.3f, 1f),
            };
            ImGui.TextColored(color, run.StatusLabel);

            ImGui.TableSetColumnIndex(9);
            DrawDeleteButton(run);
        }

        ImGui.EndTable();
    }

    private void DrawDeleteButton(InstanceRun run)
    {
        var popupId = $"Delete this run?##ConfirmDeleteRun{run.Id}";

        if (ImGui.SmallButton($"Delete##DeleteRun{run.Id}"))
        {
            ImGui.OpenPopup(popupId);
        }

        if (ImGui.BeginPopupModal(popupId, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted(
                $"Delete the run for \"{run.InstanceName}\" ({run.EnterTimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm})?");
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "This action cannot be undone.");
            ImGui.Spacing();

            if (ImGui.Button("Delete##ConfirmDelete"))
            {
                ImGui.CloseCurrentPopup();
                TriggerDelete(run.Id);
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel##CancelDelete"))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    private void DrawSortableHeader(int columnIndex, string label, RunSortColumn column)
    {
        ImGui.TableSetColumnIndex(columnIndex);

        var displayLabel = label;
        if (filter.SortColumn == column)
        {
            displayLabel += filter.SortAscending ? " ▲" : " ▼";
        }

        if (ImGui.Selectable($"{displayLabel}##Header{columnIndex}"))
        {
            if (filter.SortColumn == column)
            {
                filter.SortAscending = !filter.SortAscending;
            }
            else
            {
                filter.SortColumn = column;
                filter.SortAscending = false;
            }

            TriggerRefresh();
        }
    }

    // =====================================================================
    // Mentor Roulette achievement progress (native game data, refreshed alongside the history)
    // =====================================================================

    // Achievement.RequestAchievementProgress(id) only queues a request to the server; the
    // response arrives later via ReceiveAchievementProgress and is read back on the next
    // frame(s) through PollMentorRouletteProgress(). Achievement.Instance() can be null before
    // the relevant game systems are up, so both methods guard against that.
    private unsafe void RequestMentorRouletteProgress()
    {
        var achievement = Achievement.Instance();
        if (achievement is null)
        {
            return;
        }

        mentorRouletteRequestPending = true;
        achievement->RequestAchievementProgress(MentorRouletteInfo.AchievementId);
    }

    private unsafe void PollMentorRouletteProgress()
    {
        if (!mentorRouletteRequestPending)
        {
            return;
        }

        var achievement = Achievement.Instance();
        if (achievement is null)
        {
            return;
        }

        if (achievement->ProgressAchievementId != MentorRouletteInfo.AchievementId ||
            achievement->ProgressRequestState != Achievement.AchievementState.Loaded)
        {
            return;
        }

        mentorRouletteCurrent = achievement->ProgressCurrent;
        mentorRouletteMax = achievement->ProgressMax > 0 ? achievement->ProgressMax : MentorRouletteInfo.FallbackMax;
        mentorRouletteLoaded = true;
        mentorRouletteRequestPending = false;
    }

    // =====================================================================
    // Data loading (asynchronous, triggered from Draw())
    // =====================================================================

    private void TriggerRefresh()
    {
        if (isRefreshing)
        {
            return;
        }

        isRefreshing = true;
        RequestMentorRouletteProgress();
        _ = RefreshAsync();
    }

    private async System.Threading.Tasks.Task RefreshAsync()
    {
        try
        {
            if (!plugin.Repository.IsAvailable)
            {
                cachedRuns = new List<InstanceRun>();
                cachedStats = new InstanceStats();
                return;
            }

            queueTypeOptions = await plugin.Repository.GetDistinctQueueTypesAsync().ConfigureAwait(false);
            queueTypeFilterLabels = BuildQueueTypeFilterLabels();
            cachedRuns = await plugin.Repository.GetRunsAsync(filter).ConfigureAwait(false);

            // Computed from the cachedRuns we just fetched, instead of calling
            // Repository.GetStatsAsync(filter) — which would run the same query again from
            // scratch.
            cachedStats = InstanceRepository.ComputeStats(cachedRuns);
        }
        catch (Exception ex)
        {
            DutyRouletteLoggerPlugin.Log.Error(ex, "DutyRouletteLogger: failed to refresh the history tab.");
        }
        finally
        {
            isRefreshing = false;
        }
    }

    private void TriggerDelete(long id)
    {
        _ = DeleteAsync(id);
    }

    private async System.Threading.Tasks.Task DeleteAsync(long id)
    {
        try
        {
            await plugin.Repository.DeleteRunAsync(id).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DutyRouletteLoggerPlugin.Log.Error(ex, "DutyRouletteLogger: failed to delete the run from the UI.");
            return;
        }

        TriggerRefresh();
    }

    private void TriggerExport()
    {
        _ = ExportAsync();
    }

    private async System.Threading.Tasks.Task ExportAsync()
    {
        if (!plugin.Repository.IsAvailable)
        {
            // Without this check, ExportCsvAsync would run anyway (GetRunsAsync returns an
            // empty list when the database is unavailable) and produce an empty CSV without
            // saying why — unlike the '/dutyroulettelog export' chat command, which already checks
            // this beforehand.
            lastExportMessage = "Database is currently unavailable.";
            return;
        }

        try
        {
            var exportDirectory = System.IO.Path.Combine(DutyRouletteLoggerPlugin.PluginInterface.ConfigDirectory.FullName, "exports");
            var path = await plugin.Repository.ExportCsvAsync(exportDirectory, filter).ConfigureAwait(false);
            lastExportMessage = $"Exported to: {path}";
        }
        catch (Exception ex)
        {
            DutyRouletteLoggerPlugin.Log.Error(ex, "DutyRouletteLogger: failed to export CSV from the UI.");
            lastExportMessage = "Failed to export. See /xllog for details.";
        }
    }
}
