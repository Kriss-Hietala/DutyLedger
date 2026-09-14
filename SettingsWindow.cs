// SettingsWindow.cs
// Dedicated configuration window, opened by Dalamud's own "gear" icon in the
// plugin installer (wired to IDalamudPluginInterface.UiBuilder.OpenConfigUi).
// Verbose static explanations live behind small "(?)" hover markers next to
// the relevant control instead of always-visible paragraphs, so scanning
// the window for the setting you actually want is faster.

using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace DutyLedger;

public sealed class SettingsWindow : Window
{
    private readonly Plugin plugin;
    private readonly Configuration config;

    private bool confirmClearRequested;
    private bool confirmClearAcknowledged;

    private bool confirmClearQueueRequested;
    private bool confirmClearQueueAcknowledged;

    public SettingsWindow(Plugin plugin, Configuration config)
        : base("Duty Ledger Settings###DutyLedgerSettings", ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.plugin = plugin;
        this.config = config;
    }

    /// <summary>Small "(?)" marker placed right after a label/widget on the same line; hovering shows the full explanation as a tooltip instead of it always taking up vertical space.</summary>
    private static void HelpMarker(string text)
    {
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 28f);
            ImGui.TextUnformatted(text);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
    }

    public override void Draw()
    {
        ImGui.TextDisabled("General");
        ImGui.Separator();
        this.DrawGeneralSection();

        ImGui.Spacing();
        ImGui.TextDisabled("Chart");
        ImGui.Separator();
        this.DrawChartSection();

        ImGui.Spacing();
        ImGui.TextDisabled("Loot Tracker");
        ImGui.Separator();
        this.DrawLootSection();

        ImGui.Spacing();
        ImGui.TextDisabled("Queue Time Tracker");
        ImGui.Separator();
        this.DrawQueueSection();

        ImGui.Spacing();
        ImGui.TextDisabled("Performance");
        ImGui.Separator();
        this.DrawPerformanceSection();

        ImGui.Spacing();
        ImGui.TextDisabled("Colors");
        ImGui.Separator();
        this.DrawColorSection();

        ImGui.Spacing();
        ImGui.TextDisabled("Data");
        ImGui.Separator();
        this.DrawDataSection();

        ImGui.Spacing();
        ImGui.Separator();
        this.DrawFooter();
    }

    private void DrawGeneralSection()
    {
        var minDuration = this.config.MinimumDurationSeconds;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderInt("Minimum duty duration (s)", ref minDuration, 0, 120))
        {
            this.config.MinimumDurationSeconds = minDuration;
            this.plugin.Save();
        }
        HelpMarker("Runs shorter than this are ignored (filters out accidental enter/leave).");

        var compact = this.config.CompactMode;
        if (ImGui.Checkbox("Start in compact mode", ref compact))
        {
            this.config.CompactMode = compact;
            this.plugin.Save();
        }
    }

    private void DrawChartSection()
    {
        var showChart = this.config.ShowWeeklyChart;
        if (ImGui.Checkbox("Show weekly bar chart", ref showChart))
        {
            this.config.ShowWeeklyChart = showChart;
            this.plugin.Save();
        }
        HelpMarker("Right-click the chart itself for legend/autofit options (built into ImPlot). Disabled automatically while Eco Mode is on (see Performance below).");

        var height = this.config.ChartHeight;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Chart height", ref height, 100f, 400f, "%.0f px"))
        {
            this.config.ChartHeight = height;
            this.plugin.Save();
        }
    }

    private void DrawLootSection()
    {
        var trackLoot = this.config.TrackRareLoot;
        if (ImGui.Checkbox("Track rare loot (minions, mounts, orchestrion rolls, Triple Triad cards)", ref trackLoot))
        {
            this.config.TrackRareLoot = trackLoot;
            this.plugin.Save();
        }
        HelpMarker("Junk gear, materia, and crafting materials are always filtered out - only collectible-category items are kept.");
    }

    private void DrawQueueSection()
    {
        var trackQueue = this.config.TrackQueueTimes;
        if (ImGui.Checkbox("Track Duty Finder queue times", ref trackQueue))
        {
            this.config.TrackQueueTimes = trackQueue;
            this.plugin.Save();
        }
        HelpMarker(
            "Measures the time from joining a Duty Finder queue (ConditionFlag.InDutyQueue) to the " +
            "queue popping (IClientState.CfPop). Cancelled/withdrawn queues are not recorded. If you " +
            "queue for multiple duties/roulettes at once, the wait is attributed to whichever one pops.");

        ImGui.Text($"Logged queue pops: {this.config.QueueWaits.Count}");
        ImGui.SameLine();
        if (ImGui.Button("Export queue times CSV"))
            this.plugin.ExportQueueTimesCsv();

        ImGui.Spacing();
        this.DrawDangerousClear(
            "Clear queue time history",
            $"This will permanently delete all {this.config.QueueWaits.Count} logged queue pops.",
            ref this.confirmClearQueueRequested,
            ref this.confirmClearQueueAcknowledged,
            () =>
            {
                this.config.QueueWaits.Clear();
                this.plugin.Save();
            });
    }

    private void DrawPerformanceSection()
    {
        var eco = this.config.EcoMode;
        if (ImGui.Checkbox("Eco Mode (skip chart, use text instead of icons)", ref eco))
        {
            this.config.EcoMode = eco;
            this.plugin.Save();
        }
        HelpMarker(
            "Duty averages, the weekly chart, and queue-time averages are cached and only recomputed " +
            "when a new duty/queue pop is logged - not on every frame - so for a typical history " +
            "(hundreds of entries) the difference Eco Mode makes is small. It mainly helps if your " +
            "history has grown into the thousands of entries, by skipping the chart and rendering " +
            "item icons as plain text.");

        ImGui.Text($"Currently logged: {this.config.Entries.Count} duties, {this.config.QueueWaits.Count} queue pops.");
        if (this.config.Entries.Count < 1000)
            ImGui.TextColored(this.config.ColorMuted.Vector, "At this size, Eco Mode is unlikely to be noticeable.");
        else
            ImGui.TextColored(this.config.ColorGold.Vector, "Your history is large enough that Eco Mode may help.");
    }

    private void DrawColorSection()
    {
        this.DrawColorPicker("Clear (success)", () => this.config.ColorClear.Vector, v => this.config.ColorClear = new RgbaColor(v.X, v.Y, v.Z, v.W));
        this.DrawColorPicker("Wipe / Abandon", () => this.config.ColorAbandon.Vector, v => this.config.ColorAbandon = new RgbaColor(v.X, v.Y, v.Z, v.W));
        this.DrawColorPicker("Accent (headers, labels)", () => this.config.ColorAccent.Vector, v => this.config.ColorAccent = new RgbaColor(v.X, v.Y, v.Z, v.W));
        this.DrawColorPicker("Gold (bonus badges)", () => this.config.ColorGold.Vector, v => this.config.ColorGold = new RgbaColor(v.X, v.Y, v.Z, v.W));
        this.DrawColorPicker("Muted (disabled text)", () => this.config.ColorMuted.Vector, v => this.config.ColorMuted = new RgbaColor(v.X, v.Y, v.Z, v.W));

        if (ImGui.Button("Reset colors to default"))
        {
            this.config.ResetColorsToDefault();
            this.plugin.Save();
        }
    }

    private void DrawColorPicker(string label, Func<Vector4> get, Action<Vector4> set)
    {
        var color = get();
        if (ImGui.ColorEdit4(label, ref color, ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.NoInputs))
        {
            set(color);
            this.plugin.Save();
        }
    }

    private void DrawDataSection()
    {
        ImGui.Text($"Logged duties: {this.config.Entries.Count}");

        ImGui.SameLine();
        if (ImGui.Button("Export CSV"))
            this.plugin.ExportCsv();

        ImGui.Spacing();

        this.DrawDangerousClear(
            "Clear all duty history",
            $"This will permanently delete all {this.config.Entries.Count} logged duties.",
            ref this.confirmClearRequested,
            ref this.confirmClearAcknowledged,
            () =>
            {
                this.config.Entries.Clear();
                this.plugin.Save();
            });
    }

    /// <summary>
    /// A destructive-action button hardened against accidental clicks:
    /// 1) the button itself is red/danger-colored and separated from normal
    /// controls, 2) clicking it opens a confirmation area rather than
    /// acting immediately, 3) the actual delete button stays disabled
    /// until you tick an explicit "I understand" checkbox. Three
    /// deliberate steps instead of one accidental click.
    /// </summary>
    private void DrawDangerousClear(string buttonLabel, string warningText, ref bool requested, ref bool acknowledged, Action onConfirmed)
    {
        var danger = this.config.ColorAbandon.Vector;

        if (!requested)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, danger * new Vector4(1, 1, 1, 0.35f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, danger * new Vector4(1, 1, 1, 0.55f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, danger);
            if (ImGui.Button(buttonLabel))
            {
                requested = true;
                acknowledged = false;
            }
            ImGui.PopStyleColor(3);
            return;
        }

        ImGui.PushStyleColor(ImGuiCol.Border, danger);
        // This ImGui binding's BeginChild takes a plain "bool border" third
        // parameter (older API shape), not an ImGuiChildFlags enum - that
        // enum simply isn't defined in this binding version.
        ImGui.BeginChild($"##danger_{buttonLabel}", new Vector2(0, 90), true);

        ImGui.TextColored(danger, warningText);
        ImGui.TextColored(danger, "This cannot be undone.");

        ImGui.Checkbox("I understand, delete it", ref acknowledged);

        ImGui.BeginDisabled(!acknowledged);
        ImGui.PushStyleColor(ImGuiCol.Button, danger);
        if (ImGui.Button("Confirm delete"))
        {
            onConfirmed();
            requested = false;
            acknowledged = false;
        }
        ImGui.PopStyleColor();
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            requested = false;
            acknowledged = false;
        }

        ImGui.EndChild();
        ImGui.PopStyleColor();
    }

    /// <summary>Standard "Close" button at the bottom, where users expect one instead of relying only on the title bar X.</summary>
    private void DrawFooter()
    {
        if (ImGui.Button("Close Settings", new Vector2(150, 0)))
            this.IsOpen = false;
    }
}
