// CompactWindow.cs
// A small, always-available live tracker window: shows the duty currently in
// progress OR the current Duty Finder queue elapsed time, a one-line summary
// of the last completed duty, today's count, and a compact tomestone-cap
// warning if applicable.

using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace DutyLedger;

public sealed class CompactWindow : Window
{
    private readonly Plugin plugin;
    private readonly Configuration config;

    private Vector4 ColorClear => this.config.ColorClear.Vector;
    private Vector4 ColorAbandon => this.config.ColorAbandon.Vector;
    private Vector4 ColorAccent => this.config.ColorAccent.Vector;
    private Vector4 ColorMuted => this.config.ColorMuted.Vector;

    public CompactWindow(Plugin plugin, Configuration config)
        : base(
            "Duty Ledger###DutyLedgerCompact",
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse)
    {
        this.plugin = plugin;
        this.config = config;
    }

    public override void Draw()
    {
        if (this.config.CappedTomestones.Count > 0)
        {
            var names = string.Join(", ", this.config.CappedTomestones.Keys);
            ImGui.TextColored(this.ColorAbandon, $"\u26a0 Capped (2000): {names}");
            ImGui.Separator();
        }

        var queueSnapshot = this.plugin.GetQueueSnapshot();
        var dutySnapshot = this.plugin.GetActiveSnapshot();

        if (queueSnapshot.IsQueuing)
        {
            var elapsed = DateTimeOffset.Now - queueSnapshot.QueuedAt;
            ImGui.TextColored(this.ColorAccent, $"Queuing... ({queueSnapshot.Job})");
            ImGui.Text($"Waiting: {Format(elapsed)}");
        }
        else if (dutySnapshot.IsActive)
        {
            var elapsed = DateTimeOffset.Now - dutySnapshot.StartedAt;

            ImGui.TextColored(this.ColorAccent, dutySnapshot.DutyName);
            ImGui.SameLine();
            ImGui.TextDisabled($"({dutySnapshot.Job})");

            ImGui.Text($"In progress: {Format(elapsed)}");

            if (dutySnapshot.WipeCount > 0)
                ImGui.TextColored(this.ColorAbandon, $"Wipes this run: {dutySnapshot.WipeCount}");
        }
        else
        {
            ImGui.TextColored(this.ColorMuted, "Not in a duty or queue right now.");
        }

        ImGui.Separator();

        var last = this.config.Entries.Count > 0 ? this.config.Entries[^1] : null;
        if (last is not null)
        {
            var color = last.Result == DutyResult.Clear ? this.ColorClear : this.ColorAbandon;
            ImGui.TextDisabled("Last:");
            ImGui.SameLine();
            ImGui.TextColored(color, $"{last.DutyName} ({Format(last.Duration)})");
        }

        var today = DateTimeOffset.Now.Date;
        var todayEntries = this.config.Entries.Where(x => x.StartedAt.LocalDateTime.Date == today).ToList();
        var todayTime = todayEntries.Aggregate(TimeSpan.Zero, (sum, x) => sum + x.Duration);
        ImGui.TextDisabled($"Today: {todayEntries.Count} duties, {Format(todayTime)}");

        ImGui.Separator();

        if (ImGui.SmallButton("Open full log"))
            this.plugin.ShowFullWindow();

        ImGui.SameLine();
        if (ImGui.SmallButton("Settings"))
            this.plugin.ShowSettingsWindow();
    }

    private static string Format(TimeSpan time) => time.TotalHours >= 1
        ? $"{(int)time.TotalHours}h {time.Minutes:D2}m"
        : $"{time.Minutes}m {time.Seconds:D2}s";
}
