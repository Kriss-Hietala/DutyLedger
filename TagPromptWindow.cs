// TagPromptWindow.cs
// Standalone roulette-tagging prompt - its own window (not a nested popup) so
// it still shows up while the main window is collapsed or hidden by compact
// mode. Only opened when a duty is a member of MORE THAN ONE roulette pool
// at once (genuinely ambiguous) - unambiguous and non-roulette duties are
// tagged automatically without ever showing this popup. The button list
// shows only the roulette categories that are actually possible for this
// specific duty, not the full list of 10.
//
// "Skip" tags the entry as "Ambiguous (untagged)" (kept distinct from an
// untouched empty RouletteTag, which the stats views show as "Direct
// queue") and closes. "Close" is a separate, purely dismissive action that
// leaves the entry completely untouched - it mirrors exactly what clicking
// the title bar's own (X) already does. Both exist as real in-content
// buttons because the title bar (X) is a raw ImGui draw command that
// gamepad navigation cannot focus, so a pad-only user needs an actual
// focusable widget to get out of this prompt without necessarily having to
// pick a roulette.

using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace DutyLedger;

public sealed class TagPromptWindow : Window
{
    private const string AmbiguousUntagged = "Ambiguous (untagged)";

    private static readonly string[] FallbackTags =
    [
        "Expert", "Level Cap Dungeons", "High-level Dungeons", "Leveling",
        "Trials", "Main Scenario", "Guildhests", "Alliance Raids",
        "Normal Raids", "Mentor",
    ];

    private readonly Plugin plugin;
    private readonly Configuration config;

    private string[] candidateTags = FallbackTags;

    internal DutyEntry? PendingEntry { get; set; }

    public TagPromptWindow(Plugin plugin, Configuration config)
        : base("Tag last duty###DutyLedgerTagPrompt", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse)
    {
        this.plugin = plugin;
        this.config = config;
    }

    /// <summary>Restricts the buttons shown to the given roulette names, instead of the full list of 10.</summary>
    internal void SetCandidates(IReadOnlyList<string> candidates)
    {
        this.candidateTags = candidates.Count > 0 ? [.. candidates] : FallbackTags;
    }

    public override void Draw()
    {
        if (this.PendingEntry is not { } entry)
        {
            this.IsOpen = false;
            return;
        }

        ImGui.TextColored(this.config.ColorAccent.Vector, entry.DutyName);
        ImGui.TextDisabled("This duty is queued through more than one roulette - pick which one it was:");
        ImGui.Spacing();

        this.DrawWrappedTagButtons(entry);

        ImGui.Spacing();
        if (ImGui.Button("Skip (mark as ambiguous)"))
        {
            entry.RouletteTag = AmbiguousUntagged;
            entry.RouletteTagAuto = false;
            this.plugin.Save();
            this.PendingEntry = null;
            this.IsOpen = false;
        }

        ImGui.SameLine();
        if (ImGui.Button("Close"))
            this.IsOpen = false;
    }

    private void DrawWrappedTagButtons(DutyEntry entry)
    {
        var windowVisibleX2 = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;

        for (var i = 0; i < this.candidateTags.Length; i++)
        {
            var tag = this.candidateTags[i];

            if (ImGui.Button(tag))
            {
                entry.RouletteTag = tag;
                entry.RouletteTagAuto = false;
                this.plugin.Save();
                this.PendingEntry = null;
                this.IsOpen = false;
                return;
            }

            if (i + 1 >= this.candidateTags.Length)
                continue;

            var lastButtonX2 = ImGui.GetItemRectMax().X;
            var nextButtonWidth = ImGui.CalcTextSize(this.candidateTags[i + 1]).X + (ImGui.GetStyle().FramePadding.X * 2);
            var nextButtonX2 = lastButtonX2 + ImGui.GetStyle().ItemSpacing.X + nextButtonWidth;

            if (nextButtonX2 < windowVisibleX2)
                ImGui.SameLine();
        }
    }
}
