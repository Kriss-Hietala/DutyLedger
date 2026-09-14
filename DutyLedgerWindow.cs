// DutyLedgerWindow.cs
// ImGui window for the DutyLedger plugin: cap warnings, stats, averages per
// duty, queue-time averages, filterable table with bonus/loot badges, CSV
// export, an independently-collapsible daily activity chart, and an
// independently-collapsible roulette-category breakdown chart (ImPlot).
// Both charts use a dynamic Y-axis tick step (1/2/5/10/20/25/50/...) picked
// to keep roughly 4-8 labeled ticks regardless of how tall the tallest bar
// is, instead of always labeling every single integer.
// The duty table supports real click-to-sort on its headers (Date, Duty,
// Job, Duration, Result, Wipes) via ImGui.TableGetSortSpecs() - the "Sort
// by" dropdown is just a shortcut that sets the same underlying sort state,
// so the two controls never fight each other. Columns that show composite
// badges (Roulette/Bonus/Rewards/Loot) are marked NoSort since there's no
// single meaningful ordering for them.
// Averages/chart/queue data are cached and only recomputed when their
// respective list counts change.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Bindings.ImPlot;
using Dalamud.Interface.Windowing;

namespace DutyLedger;

public sealed class DutyLedgerWindow : Window
{
    private readonly record struct AverageRow(
        string Duty, int Runs, int Clears, TimeSpan AvgDuration,
        double AvgGilPerMin, double AvgTomePerMin, int DailyCount, int AiNCount);

    private readonly record struct QueueAverageRow(
        string Role, string RouletteTag, int Count, TimeSpan AvgWait, TimeSpan MinWait, TimeSpan MaxWait);

    private readonly record struct CategoryRow(string Category, int Count);

    /// <summary>"Nice" step sizes tried in order until one keeps the tick count within a readable range for the given max value.</summary>
    private static readonly int[] NiceSteps = [1, 2, 5, 10, 20, 25, 50, 100, 200, 250, 500, 1000, 2000, 2500, 5000];

    // Table column indices, kept in sync with the TableSetupColumn calls in DrawTable().
    private const int ColDate = 0;
    private const int ColDuty = 1;
    private const int ColJob = 2;
    private const int ColDuration = 3;
    private const int ColResult = 4;
    private const int ColWipes = 7;

    private readonly Plugin plugin;
    private readonly Configuration config;

    private string search = "";
    private int sortPresetIndex = 0;
    private int sortColumnIndex = ColDate;
    private bool sortAscending = false;
    private string lastExportPath = "";
    private string lastQueueExportPath = "";

    private int cachedEntryCount = -1;
    private List<AverageRow> cachedAverages = [];
    private double[] cachedDailyCounts = new double[7];
    private List<CategoryRow> cachedCategoryCounts = [];

    private int cachedQueueCount = -1;
    private List<QueueAverageRow> cachedQueueAverages = [];

    private Vector4 ColorClear => this.config.ColorClear.Vector;
    private Vector4 ColorAbandon => this.config.ColorAbandon.Vector;
    private Vector4 ColorAccent => this.config.ColorAccent.Vector;
    private Vector4 ColorGold => this.config.ColorGold.Vector;
    private Vector4 ColorMuted => this.config.ColorMuted.Vector;

    public DutyLedgerWindow(Plugin plugin, Configuration config)
        : base("Duty Ledger###DutyLedger")
    {
        this.plugin = plugin;
        this.config = config;

        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(1000, 620),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        this.RefreshCachesIfNeeded();

        this.DrawCapWarnings();
        this.DrawStatsSection();
        ImGui.Separator();
        this.DrawAveragesSection();
        ImGui.Separator();
        this.DrawQueueTimesSection();
        ImGui.Separator();
        this.DrawToolbar();
        this.DrawTable();
    }

    private void RefreshCachesIfNeeded()
    {
        if (this.config.Entries.Count != this.cachedEntryCount)
        {
            this.cachedEntryCount = this.config.Entries.Count;

            this.cachedAverages = this.config.Entries
                .GroupBy(x => x.DutyName)
                .Select(g => new AverageRow(
                    g.Key,
                    g.Count(),
                    g.Count(x => x.Result == DutyResult.Clear),
                    TimeSpan.FromSeconds(g.Average(x => x.Duration.TotalSeconds)),
                    g.Average(x => x.GilPerMinute),
                    g.Average(x => x.TomestonesPerMinute),
                    g.Count(x => x.DailyRouletteBonus),
                    g.Count(x => x.AdventurerInNeedBonus)))
                .OrderByDescending(x => x.Runs)
                .ToList();

            var dailyCounts = new double[7];
            foreach (var e in this.config.Entries)
                dailyCounts[((int)e.StartedAt.LocalDateTime.DayOfWeek + 6) % 7]++;
            this.cachedDailyCounts = dailyCounts;

            this.cachedCategoryCounts = this.config.Entries
                .GroupBy(x => string.IsNullOrEmpty(x.RouletteTag) ? "Direct queue" : x.RouletteTag)
                .Select(g => new CategoryRow(g.Key, g.Count()))
                .OrderByDescending(x => x.Count)
                .ToList();
        }

        if (this.config.QueueWaits.Count != this.cachedQueueCount)
        {
            this.cachedQueueCount = this.config.QueueWaits.Count;

            this.cachedQueueAverages = this.config.QueueWaits
                .GroupBy(x => (x.Role, RouletteTag: string.IsNullOrEmpty(x.RouletteTag) ? "(not roulette)" : x.RouletteTag))
                .Select(g => new QueueAverageRow(
                    g.Key.Role,
                    g.Key.RouletteTag,
                    g.Count(),
                    TimeSpan.FromSeconds(g.Average(x => x.WaitTime.TotalSeconds)),
                    g.Min(x => x.WaitTime),
                    g.Max(x => x.WaitTime)))
                .OrderBy(x => x.Role).ThenBy(x => x.RouletteTag)
                .ToList();
        }
    }

    private void DrawCapWarnings()
    {
        if (this.config.CappedTomestones.Count == 0)
            return;

        string? toDismiss = null;

        foreach (var (stone, seenAt) in this.config.CappedTomestones)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, this.ColorAbandon);
            ImGui.TextWrapped($"\u26a0 {stone} reached the 2000 cap (seen {seenAt.LocalDateTime:yyyy-MM-dd HH:mm}). Spend some before you lose more!");
            ImGui.PopStyleColor();

            ImGui.SameLine();
            if (ImGui.SmallButton($"Dismiss##{stone}"))
                toDismiss = stone;
        }

        if (toDismiss is not null)
            this.plugin.DismissCappedTomestone(toDismiss);

        ImGui.Separator();
    }

    private void DrawStatsSection()
    {
        if (!ImGui.CollapsingHeader("Statistics & Summary", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var today = DateTimeOffset.Now.Date;
        var todayEntries = this.config.Entries.Where(x => x.StartedAt.LocalDateTime.Date == today).ToList();

        var weekStart = today.AddDays(-((int)today.DayOfWeek + 6) % 7);
        var weekEntries = this.config.Entries.Where(x => x.StartedAt.LocalDateTime.Date >= weekStart).ToList();
        var mostCommonThisWeek = weekEntries
            .GroupBy(x => x.DutyName)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .FirstOrDefault();

        var todayTime = todayEntries.Aggregate(TimeSpan.Zero, (sum, x) => sum + x.Duration);
        var todayGil = todayEntries.Sum(x => x.GainedGil);
        var todayClearRate = todayEntries.Count == 0 ? 0 : 100.0 * todayEntries.Count(x => x.Result == DutyResult.Clear) / todayEntries.Count;
        var todayDaily = todayEntries.Count(x => x.DailyRouletteBonus);
        var todayAiN = todayEntries.Count(x => x.AdventurerInNeedBonus);
        var todayFirst = todayEntries.Count(x => x.FirstDutyBonus);

        ImGui.TextColored(this.ColorAccent, "Today:");
        ImGui.SameLine();
        ImGui.Text($"{todayEntries.Count} duties  \u2022  time: {Format(todayTime)}  \u2022  gil: {todayGil:N0}  \u2022  clear rate: {todayClearRate:0}%%");

        ImGui.TextColored(this.ColorGold, "Bonuses today:");
        ImGui.SameLine();
        ImGui.Text($"daily roulette x{todayDaily}  \u2022  adventurer-in-need x{todayAiN}  \u2022  first-clear x{todayFirst}");

        ImGui.TextColored(this.ColorAccent, "This week:");
        ImGui.SameLine();
        ImGui.TextUnformatted(mostCommonThisWeek is null
            ? "no entries yet"
            : $"{weekEntries.Count} duties  \u2022  most common: {mostCommonThisWeek.Key} ({mostCommonThisWeek.Count()}x)");

        if (!this.config.ShowWeeklyChart || this.config.EcoMode)
            return;

        ImGui.Spacing();

        if (ImGui.CollapsingHeader("Duties per day of the week", ImGuiTreeNodeFlags.DefaultOpen))
            this.DrawDailyChart();

        if (ImGui.CollapsingHeader("Instances by roulette category", ImGuiTreeNodeFlags.DefaultOpen))
            this.DrawCategoryChart();
    }

    /// <summary>Picks a "nice" Y-axis step (1/2/5/10/20/25/50/...) so the tallest bar produces roughly 4-8 labeled ticks instead of one tick per integer.</summary>
    private static int ComputeNiceStep(int maxValue, int targetTicks = 6)
    {
        if (maxValue <= 0)
            return 1;

        var rawStep = (double)maxValue / targetTicks;
        foreach (var step in NiceSteps)
        {
            if (step >= rawStep)
                return step;
        }

        return NiceSteps[^1];
    }

    private static (double[] Ticks, string[] Labels) BuildYAxisTicks(int maxValue)
    {
        var step = ComputeNiceStep(maxValue);
        var topTick = ((maxValue / step) + 1) * step;

        var tickCount = (topTick / step) + 1;
        var ticks = Enumerable.Range(0, tickCount).Select(i => (double)(i * step)).ToArray();
        var labels = ticks.Select(v => v.ToString("0")).ToArray();
        return (ticks, labels);
    }

    /// <summary>Simple total duty count per weekday - one aggregate series, which is fine on its own (this chart is about activity volume, not outcome).</summary>
    private void DrawDailyChart()
    {
        if (this.config.Entries.Count == 0)
        {
            ImGui.TextDisabled("No data yet.");
            return;
        }

        var counts = this.cachedDailyCounts;
        var dayLabels = new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
        var positions = Enumerable.Range(0, 7).Select(i => (double)i).ToArray();

        var maxCount = Math.Max(1, (int)Math.Ceiling(counts.Max()));
        var (yTicks, yLabels) = BuildYAxisTicks(maxCount);
        var yTop = yTicks[^1];

        if (ImPlot.BeginPlot("##daily_duty_chart", new Vector2(-1, this.config.ChartHeight), ImPlotFlags.NoLegend))
        {
            ImPlot.SetupAxes(string.Empty, string.Empty);
            ImPlot.SetupAxisTicks(ImAxis.X1, 0, 6, 7, dayLabels);
            ImPlot.SetupAxisTicks(ImAxis.Y1, 0, yTop, yTicks.Length, yLabels);
            ImPlot.SetupAxisLimits(ImAxis.Y1, 0, yTop, ImPlotCond.Always);

            ImPlot.SetNextFillStyle(this.ColorAccent);
            ImPlot.PlotBars("Duties", ref positions[0], ref counts[0], counts.Length, 0.6);

            ImPlot.EndPlot();
        }
    }

    /// <summary>
    /// Bar per roulette category (plus "Direct queue" for non-roulette runs
    /// and "Ambiguous (untagged)" for skipped multi-candidate duties), sorted
    /// by frequency.
    /// </summary>
    private void DrawCategoryChart()
    {
        if (this.cachedCategoryCounts.Count == 0)
        {
            ImGui.TextDisabled("No data yet.");
            return;
        }

        var positions = Enumerable.Range(0, this.cachedCategoryCounts.Count).Select(i => (double)i).ToArray();
        var counts = this.cachedCategoryCounts.Select(c => (double)c.Count).ToArray();
        var labels = this.cachedCategoryCounts.Select(c => c.Category).ToArray();

        var maxCount = Math.Max(1, (int)Math.Ceiling(counts.Max()));
        var (yTicks, yLabels) = BuildYAxisTicks(maxCount);
        var yTop = yTicks[^1];

        if (ImPlot.BeginPlot("##category_chart", new Vector2(-1, this.config.ChartHeight), ImPlotFlags.NoLegend))
        {
            ImPlot.SetupAxes(string.Empty, string.Empty);
            ImPlot.SetupAxisTicks(ImAxis.X1, 0, this.cachedCategoryCounts.Count - 1, this.cachedCategoryCounts.Count, labels);
            ImPlot.SetupAxisTicks(ImAxis.Y1, 0, yTop, yTicks.Length, yLabels);
            ImPlot.SetupAxisLimits(ImAxis.Y1, 0, yTop, ImPlotCond.Always);

            ImPlot.SetNextFillStyle(this.ColorGold);
            ImPlot.PlotBars("Instances", ref positions[0], ref counts[0], counts.Length, 0.6);

            ImPlot.EndPlot();
        }
    }

    private void DrawAveragesSection()
    {
        if (!ImGui.CollapsingHeader("Duty Averages"))
            return;

        if (this.cachedAverages.Count == 0)
        {
            ImGui.TextDisabled("No completed duties logged yet.");
            return;
        }

        const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp;

        if (!ImGui.BeginTable("##averages", 7, flags))
            return;

        ImGui.TableSetupColumn("Duty", ImGuiTableColumnFlags.WidthStretch, 2);
        ImGui.TableSetupColumn("Runs");
        ImGui.TableSetupColumn("Clear rate");
        ImGui.TableSetupColumn("Avg duration");
        ImGui.TableSetupColumn("Avg gil/min");
        ImGui.TableSetupColumn("Avg tomestones/min");
        ImGui.TableSetupColumn("Bonuses (daily / AiN)");
        ImGui.TableHeadersRow();

        foreach (var g in this.cachedAverages)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(g.Duty);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(g.Runs.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{100.0 * g.Clears / g.Runs:0}%");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Format(g.AvgDuration));

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{g.AvgGilPerMin:N0}");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{g.AvgTomePerMin:N1}");

            ImGui.TableNextColumn();
            ImGui.TextColored(this.ColorGold, $"{g.DailyCount} / {g.AiNCount}");
        }

        ImGui.EndTable();
    }

    private void DrawQueueTimesSection()
    {
        if (!ImGui.CollapsingHeader("Queue Times"))
            return;

        if (!this.config.TrackQueueTimes)
        {
            ImGui.TextDisabled("Queue time tracking is disabled in Settings.");
            return;
        }

        if (this.cachedQueueAverages.Count == 0)
        {
            ImGui.TextDisabled("No completed queue pops logged yet.");
            return;
        }

        ImGui.TextDisabled("Only queues that actually popped are counted - cancelled/withdrawn queues are discarded.");
        ImGui.TextDisabled("If multiple duties/roulettes were queued at once, the wait is attributed to whichever one popped.");
        ImGui.Spacing();

        if (ImGui.Button("Export queue times CSV"))
            this.lastQueueExportPath = this.plugin.ExportQueueTimesCsv();

        if (!string.IsNullOrEmpty(this.lastQueueExportPath))
        {
            ImGui.SameLine();
            ImGui.TextColored(this.ColorMuted, $"Saved to: {this.lastQueueExportPath}");
        }

        const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp;

        if (!ImGui.BeginTable("##queue_averages", 6, flags))
            return;

        ImGui.TableSetupColumn("Role");
        ImGui.TableSetupColumn("Roulette", ImGuiTableColumnFlags.WidthStretch, 2);
        ImGui.TableSetupColumn("Pops");
        ImGui.TableSetupColumn("Avg wait");
        ImGui.TableSetupColumn("Min wait");
        ImGui.TableSetupColumn("Max wait");
        ImGui.TableHeadersRow();

        foreach (var q in this.cachedQueueAverages)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(q.Role);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(q.RouletteTag);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(q.Count.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Format(q.AvgWait));

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Format(q.MinWait));

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Format(q.MaxWait));
        }

        ImGui.EndTable();
    }

    private void DrawToolbar()
    {
        ImGui.SetNextItemWidth(220);
        ImGui.InputTextWithHint("##search", "Search duty or job...", ref this.search, 128);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(150);
        if (ImGui.Combo("Sort by", ref this.sortPresetIndex, new[] { "Newest", "Longest", "Duty name" }, 3))
        {
            (this.sortColumnIndex, this.sortAscending) = this.sortPresetIndex switch
            {
                1 => (ColDuration, false),
                2 => (ColDuty, true),
                _ => (ColDate, false),
            };
        }

        ImGui.SameLine();
        if (ImGui.Button("Export CSV"))
            this.lastExportPath = this.plugin.ExportCsv();

        ImGui.SameLine();
        if (ImGui.Button("Compact mode"))
            this.plugin.ShowCompactWindow();

        ImGui.SameLine();
        if (ImGui.Button("Settings"))
            this.plugin.ShowSettingsWindow();

        if (!string.IsNullOrEmpty(this.lastExportPath))
            ImGui.TextColored(this.ColorMuted, $"Saved to: {this.lastExportPath}");
    }

    /// <summary>Maps a table column index to the DutyEntry field used to order by it. Only called for columns that are NOT marked NoSort.</summary>
    private static IComparable SortKey(DutyEntry x, int columnIndex) => columnIndex switch
    {
        ColDuty => x.DutyName,
        ColJob => x.Job,
        ColDuration => x.Duration,
        ColResult => x.Result,
        ColWipes => x.WipeCount,
        _ => x.StartedAt,
    };

    private void DrawTable()
    {
        var rows = this.config.Entries
            .Where(x => string.IsNullOrWhiteSpace(this.search)
                        || x.DutyName.Contains(this.search, StringComparison.OrdinalIgnoreCase)
                        || x.Job.Contains(this.search, StringComparison.OrdinalIgnoreCase))
            .ToList();

        const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg
            | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Sortable;

        if (!ImGui.BeginTable("##duties", 10, flags, new Vector2(0, -1)))
            return;

        ImGui.TableSetupColumn("Date", ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.PreferSortDescending);
        ImGui.TableSetupColumn("Duty", ImGuiTableColumnFlags.WidthStretch, 2);
        ImGui.TableSetupColumn("Job");
        ImGui.TableSetupColumn("Duration", ImGuiTableColumnFlags.PreferSortDescending);
        ImGui.TableSetupColumn("Result");
        ImGui.TableSetupColumn("Roulette", ImGuiTableColumnFlags.NoSort);
        ImGui.TableSetupColumn("Bonus", ImGuiTableColumnFlags.NoSort);
        ImGui.TableSetupColumn("Wipes", ImGuiTableColumnFlags.PreferSortDescending);
        ImGui.TableSetupColumn("Rewards", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.NoSort, 1.6f);
        ImGui.TableSetupColumn("Loot", ImGuiTableColumnFlags.NoSort);
        ImGui.TableHeadersRow();

        var sortSpecs = ImGui.TableGetSortSpecs();
        if (sortSpecs.SpecsDirty && sortSpecs.SpecsCount > 0)
        {
            var spec = sortSpecs.Specs;
            this.sortColumnIndex = spec.ColumnIndex;
            this.sortAscending = spec.SortDirection == ImGuiSortDirection.Ascending;
            sortSpecs.SpecsDirty = false;
        }

        rows = this.sortAscending
            ? rows.OrderBy(x => SortKey(x, this.sortColumnIndex)).ToList()
            : rows.OrderByDescending(x => SortKey(x, this.sortColumnIndex)).ToList();

        foreach (var x in rows)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(x.StartedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm"));

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(x.DutyName);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(x.Job);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Format(x.Duration));

            ImGui.TableNextColumn();
            var resultColor = x.Result == DutyResult.Clear ? this.ColorClear : this.ColorAbandon;
            ImGui.TextColored(resultColor, x.Result == DutyResult.Clear ? "Clear" : "Wipe/Abandon");

            ImGui.TableNextColumn();
            this.DrawRouletteCell(x);

            ImGui.TableNextColumn();
            this.DrawBonusCell(x);

            ImGui.TableNextColumn();
            if (x.WipeCount > 0)
                ImGui.TextColored(this.ColorAbandon, x.WipeCount.ToString());
            else
                ImGui.TextUnformatted("-");

            ImGui.TableNextColumn();
            this.DrawRewardsCell(x);

            ImGui.TableNextColumn();
            this.DrawLootCell(x);
        }

        ImGui.EndTable();
    }

    private void DrawRouletteCell(DutyEntry x)
    {
        if (string.IsNullOrEmpty(x.RouletteTag))
        {
            ImGui.TextUnformatted("-");
            return;
        }

        ImGui.TextUnformatted(x.RouletteTag);

        if (!x.RouletteTagAuto)
            return;

        ImGui.SameLine();
        ImGui.TextDisabled("(auto)");

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Detected automatically from this duty's roulette membership in the game data - not manually picked.");
    }

    private void DrawBonusCell(DutyEntry x)
    {
        var badges = new List<string>();
        if (x.DailyRouletteBonus) badges.Add("Daily");
        if (x.AdventurerInNeedBonus) badges.Add("AiN");
        if (x.FirstDutyBonus) badges.Add("First");

        if (badges.Count == 0)
        {
            ImGui.TextUnformatted("-");
            return;
        }

        for (var i = 0; i < badges.Count; i++)
        {
            ImGui.TextColored(this.ColorGold, badges[i]);
            if (i < badges.Count - 1)
                ImGui.SameLine();
        }
    }

    private void DrawRewardsCell(DutyEntry x)
    {
        var hasGil = x.GainedGil > 0;
        var hasTomestones = x.GainedTomestones.Count > 0;

        if (!hasGil && !hasTomestones)
        {
            ImGui.TextUnformatted("-");
            return;
        }

        ImGui.BeginGroup();

        if (hasGil)
        {
            ImGui.TextUnformatted($"{x.GainedGil:N0}g");
            if (hasTomestones)
                ImGui.SameLine();
        }

        if (hasTomestones)
        {
            var stones = x.GainedTomestones.ToList();
            for (var i = 0; i < stones.Count; i++)
            {
                this.DrawIconBadge(stones[i].Key, stones[i].Value.ToString());
                if (i < stones.Count - 1)
                    ImGui.SameLine(0, 8);
            }
        }

        ImGui.EndGroup();

        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted($"Gil/min: {x.GilPerMinute:N0}  \u2022  Tomestones/min: {x.TomestonesPerMinute:N1}");
            ImGui.EndTooltip();
        }
    }

    private void DrawLootCell(DutyEntry x)
    {
        if (x.RareLoot.Count == 0)
        {
            ImGui.TextUnformatted("-");
            return;
        }

        for (var i = 0; i < x.RareLoot.Count; i++)
        {
            this.DrawIconBadge(x.RareLoot[i], null);
            if (i < x.RareLoot.Count - 1)
                ImGui.SameLine(0, 6);
        }
    }

    private void DrawIconBadge(string itemName, string? suffixText)
    {
        if (!this.config.EcoMode)
        {
            var icon = this.plugin.GetItemIcon(itemName);
            if (icon is not null && icon.TryGetWrap(out var wrap, out _) && wrap is not null)
            {
                ImGui.Image(wrap.Handle, new Vector2(18, 18));

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(itemName);

                if (suffixText is not null)
                    ImGui.SameLine(0, 3);
            }
        }

        if (suffixText is not null)
            ImGui.TextUnformatted(suffixText);
        else if (this.config.EcoMode)
            ImGui.TextUnformatted(itemName);
    }

    private static string Format(TimeSpan time) => time.TotalHours >= 1
        ? $"{(int)time.TotalHours}h {time.Minutes:D2}m"
        : $"{time.Minutes}m {time.Seconds:D2}s";
}
