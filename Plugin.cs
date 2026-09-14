// Plugin.cs
// DutyLedger - a Dalamud plugin (FFXIV) that logs your duty history.
// Target: Dalamud API 15 / "Dalamud.NET.Sdk/15.0.0".

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Chat;
using Dalamud.Game.Command;
using Dalamud.Game.DutyState;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace DutyLedger;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => "Duty Ledger";

    private const string Command = "/dutyledger";
    private const string MiniCommand = "/dutyledgermini";
    private const int TomestoneCap = 2000;

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commands;
    private readonly ICondition condition;
    private readonly IDutyState dutyState;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly IDataManager data;
    private readonly IChatGui chatGui;
    private readonly ITextureProvider textures;
    private readonly IPluginLog log;

    private readonly Configuration config;
    private readonly WindowSystem windows = new("DutyLedger");
    private readonly DutyLedgerWindow window;
    private readonly CompactWindow compactWindow;
    private readonly SettingsWindow settingsWindow;
    private readonly TagPromptWindow tagPromptWindow;

    private ActiveDuty? activeDuty;
    private DateTimeOffset? completionPendingSince;

    private DateTimeOffset? queueStartedAt;
    private string? queueJob;

    private DateTimeOffset lastCapRecheck = DateTimeOffset.MinValue;

    private Dictionary<string, Item>? itemsByName;
    private readonly Dictionary<string, uint> iconIdCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> RareLootCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Minion", "Mount", "Orchestrion Roll", "Triple Triad Card",
    };

    private static readonly Regex DirectGilPattern =
        new(@"You (?:obtain|receive) ([0-9,]+) gil\.", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DailyRouletteBonusPattern =
        new(@"has been awarded for using the duty roulette", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AdventurerInNeedPattern =
        new(@"has been awarded for being an adventurer in need", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FirstDutyBonusPattern =
        new(@"A bonus of [0-9,]+ .*?will be awarded upon completion", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex GilAmountPattern =
        new(@"([0-9,]+)\s*gil", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TomestonePattern =
        new(@"You (?:obtain|receive) ([0-9,]+) (Allagan tomestones? of [^.]+)\.", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex GeneralLootPattern =
        new(@"You obtain (?:[0-9,]+ )?(?:an? )?([^.]+)\.", RegexOptions.Compiled);

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commands,
        ICondition condition,
        IDutyState dutyState,
        IClientState clientState,
        IObjectTable objectTable,
        IDataManager data,
        IChatGui chatGui,
        ITextureProvider textures,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.commands = commands;
        this.condition = condition;
        this.dutyState = dutyState;
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.data = data;
        this.chatGui = chatGui;
        this.textures = textures;
        this.log = log;

        this.config = this.pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        this.window = new DutyLedgerWindow(this, this.config);
        this.compactWindow = new CompactWindow(this, this.config);
        this.settingsWindow = new SettingsWindow(this, this.config);
        this.tagPromptWindow = new TagPromptWindow(this, this.config);
        this.windows.AddWindow(this.window);
        this.windows.AddWindow(this.compactWindow);
        this.windows.AddWindow(this.settingsWindow);
        this.windows.AddWindow(this.tagPromptWindow);

        if (this.config.CompactMode)
            this.compactWindow.IsOpen = true;
        else
            this.window.IsOpen = false;

        this.commands.AddHandler(Command, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "Opens the Duty Ledger history window.",
        });
        this.commands.AddHandler(MiniCommand, new CommandInfo(this.OnMiniCommand)
        {
            HelpMessage = "Opens the compact Duty Ledger live tracker.",
        });

        this.pluginInterface.UiBuilder.Draw += this.DrawUi;
        this.pluginInterface.UiBuilder.OpenMainUi += this.OpenWindow;
        this.pluginInterface.UiBuilder.OpenConfigUi += this.OpenSettings;

        this.dutyState.DutyStarted += this.OnDutyStarted;
        this.dutyState.DutyCompleted += this.OnDutyCompleted;
        this.dutyState.DutyWiped += this.OnDutyWiped;
        this.condition.ConditionChange += this.OnConditionChanged;
        this.clientState.CfPop += this.OnCfPop;

        this.chatGui.ChatMessageUnhandled += this.OnChatMessage;
    }

    public void Dispose()
    {
        this.chatGui.ChatMessageUnhandled -= this.OnChatMessage;
        this.clientState.CfPop -= this.OnCfPop;
        this.condition.ConditionChange -= this.OnConditionChanged;
        this.dutyState.DutyWiped -= this.OnDutyWiped;
        this.dutyState.DutyCompleted -= this.OnDutyCompleted;
        this.dutyState.DutyStarted -= this.OnDutyStarted;

        this.pluginInterface.UiBuilder.OpenConfigUi -= this.OpenSettings;
        this.pluginInterface.UiBuilder.OpenMainUi -= this.OpenWindow;
        this.pluginInterface.UiBuilder.Draw -= this.DrawUi;

        this.commands.RemoveHandler(MiniCommand);
        this.commands.RemoveHandler(Command);
        this.windows.RemoveAllWindows();
    }

    internal void Save() => this.pluginInterface.SavePluginConfig(this.config);

    internal void ShowFullWindow()
    {
        this.compactWindow.IsOpen = false;
        this.window.IsOpen = true;
        this.config.CompactMode = false;
        this.Save();
    }

    internal void ShowCompactWindow()
    {
        this.window.IsOpen = false;
        this.compactWindow.IsOpen = true;
        this.config.CompactMode = true;
        this.Save();
    }

    internal void ShowSettingsWindow() => this.settingsWindow.IsOpen = true;

    internal ActiveDutySnapshot GetActiveSnapshot() => this.activeDuty is { } a
        ? new ActiveDutySnapshot { IsActive = true, DutyName = a.DutyName, Job = a.Job, StartedAt = a.StartedAt, WipeCount = a.WipeCount }
        : default;

    internal QueueSnapshot GetQueueSnapshot() => this.queueStartedAt is { } startedAt
        ? new QueueSnapshot { IsQueuing = true, QueuedAt = startedAt, Job = this.queueJob ?? "Unknown" }
        : default;

    internal void DismissCappedTomestone(string tomestoneName)
    {
        if (this.config.CappedTomestones.Remove(tomestoneName))
            this.Save();
    }

    private Item? FindItemByName(string name)
    {
        this.itemsByName ??= this.data.GetExcelSheet<Item>()
            .Where(i => !string.IsNullOrWhiteSpace(i.Name.ToString()))
            .GroupBy(i => i.Name.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        return this.itemsByName.TryGetValue(name, out var item) ? item : null;
    }

    internal ISharedImmediateTexture? GetItemIcon(string itemName)
    {
        if (!this.iconIdCache.TryGetValue(itemName, out var iconId))
        {
            var item = this.FindItemByName(itemName);

            if (item is null)
            {
                var singular = Regex.Replace(itemName, @"\btomestones\b", "Tomestone", RegexOptions.IgnoreCase);
                item = this.FindItemByName(singular);
            }

            iconId = item?.Icon ?? 0;
            this.iconIdCache[itemName] = iconId;

            if (iconId == 0)
                this.log.Debug($"[DutyLedger] Could not resolve icon for item \"{itemName}\".");
        }

        return iconId == 0 ? null : this.textures.GetFromGameIcon(new GameIconLookup(iconId));
    }

    /// <summary>Escapes a value for CSV: doubles embedded quotes, and flattens embedded line breaks to spaces so a single logical field can never be split across rows by simple/naive CSV readers.</summary>
    private static string Csv(string value) => "\"" + value.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Replace("\"", "\"\"") + "\"";

    internal string ExportCsv()
    {
        var path = Path.Combine(this.pluginInterface.ConfigDirectory.FullName, $"duty-ledger-export-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        var sb = new StringBuilder("StartedAt,EndedAt,Duty,Job,TerritoryId,DurationSeconds,Result,RouletteTag,RouletteTagAuto,WipeCount,GainedGil,Tomestones,DailyRouletteBonus,AdventurerInNeed,FirstDutyBonus,RareLoot\n");

        foreach (var x in this.config.Entries.OrderBy(e => e.StartedAt))
        {
            var tomestonesSummary = string.Join("; ", x.GainedTomestones.Select(t => $"{t.Key}:{t.Value}"));
            var lootSummary = string.Join("; ", x.RareLoot);

            sb.Append(x.StartedAt.ToString("O", CultureInfo.InvariantCulture)).Append(',')
              .Append(x.EndedAt.ToString("O", CultureInfo.InvariantCulture)).Append(',')
              .Append(Csv(x.DutyName)).Append(',')
              .Append(Csv(x.Job)).Append(',')
              .Append(x.TerritoryTypeId).Append(',')
              .Append((long)x.Duration.TotalSeconds).Append(',')
              .Append(x.Result).Append(',')
              .Append(Csv(x.RouletteTag)).Append(',')
              .Append(x.RouletteTagAuto).Append(',')
              .Append(x.WipeCount).Append(',')
              .Append(x.GainedGil).Append(',')
              .Append(Csv(tomestonesSummary)).Append(',')
              .Append(x.DailyRouletteBonus).Append(',')
              .Append(x.AdventurerInNeedBonus).Append(',')
              .Append(x.FirstDutyBonus).Append(',')
              .Append(Csv(lootSummary)).Append('\n');
        }

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        return path;
    }

    internal string ExportQueueTimesCsv()
    {
        var path = Path.Combine(this.pluginInterface.ConfigDirectory.FullName, $"duty-ledger-queue-times-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        var sb = new StringBuilder("QueuedAt,WaitSeconds,Duty,RouletteTag,Job,Role\n");

        foreach (var x in this.config.QueueWaits.OrderBy(e => e.QueuedAt))
        {
            sb.Append(x.QueuedAt.ToString("O", CultureInfo.InvariantCulture)).Append(',')
              .Append((long)x.WaitTime.TotalSeconds).Append(',')
              .Append(Csv(x.DutyName)).Append(',')
              .Append(Csv(x.RouletteTag)).Append(',')
              .Append(Csv(x.Job)).Append(',')
              .Append(Csv(x.Role)).Append('\n');
        }

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        return path;
    }

    private void OnCommand(string command, string args) => this.ShowFullWindow();

    private void OnMiniCommand(string command, string args) => this.ShowCompactWindow();

    private void OpenWindow()
    {
        if (this.config.CompactMode)
            this.compactWindow.IsOpen = true;
        else
            this.window.IsOpen = true;
    }

    private void OpenSettings() => this.ShowSettingsWindow();

    private void DrawUi()
    {
        this.FinishPendingCompletion();
        this.PeriodicCapRecheck();
        this.windows.Draw();
    }

    /// <summary>
    /// Every ~10s, re-verify any tomestone currently flagged as capped against
    /// its LIVE inventory count. This is what lets the warning auto-clear once
    /// you spend some down, since we have no other event that would tell us.
    /// </summary>
    private void PeriodicCapRecheck()
    {
        if (DateTimeOffset.Now - this.lastCapRecheck < TimeSpan.FromSeconds(10))
            return;

        this.lastCapRecheck = DateTimeOffset.Now;

        if (this.config.CappedTomestones.Count == 0)
            return;

        foreach (var name in this.config.CappedTomestones.Keys.ToList())
            this.CheckLiveTomestoneCap(name);
    }

    /// <summary>
    /// Reads the player's ACTUAL current count of a tomestone type via
    /// InventoryManager (FFXIVClientStructs) rather than waiting for a chat
    /// message - the game only prints "cannot receive any more" when a gain
    /// is actively denied, never when you land exactly on the 2000 cap with
    /// nothing left over to deny.
    /// </summary>
    private unsafe void CheckLiveTomestoneCap(string tomestoneName)
    {
        var item = this.FindItemByName(tomestoneName);
        if (item is not { } row)
            return;

        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager is null)
            return;

        var count = inventoryManager->GetInventoryItemCount(row.RowId);

        if (count >= TomestoneCap)
        {
            if (!this.config.CappedTomestones.ContainsKey(tomestoneName))
            {
                this.config.CappedTomestones[tomestoneName] = DateTimeOffset.Now;
                this.Save();
                this.log.Debug($"[DutyLedger] Live check confirms cap: {tomestoneName} = {count}/{TomestoneCap}");
            }
        }
        else if (this.config.CappedTomestones.Remove(tomestoneName))
        {
            this.Save();
            this.log.Debug($"[DutyLedger] {tomestoneName} dropped back below cap ({count}/{TomestoneCap}) - warning cleared.");
        }
    }

    private void OnDutyStarted(IDutyStateEventArgs args)
    {
        if (this.activeDuty is not null)
            return;

        var cfcId = args.ContentFinderCondition.RowId;
        var territoryId = args.TerritoryType.RowId;

        this.activeDuty = new ActiveDuty
        {
            DutyName = this.ResolveDutyName(cfcId, territoryId),
            TerritoryTypeId = territoryId,
            ContentFinderConditionId = cfcId,
            Job = this.ResolveJob(),
            StartedAt = DateTimeOffset.Now,
            CandidateRouletteTags = this.ResolveCandidateRouletteTags(cfcId),
        };

        this.log.Debug($"[DutyLedger] Duty started: {this.activeDuty.DutyName} (CFC {cfcId}, territory {territoryId}), " +
            $"roulette candidates: [{string.Join(", ", this.activeDuty.CandidateRouletteTags)}]");
    }

    private void OnDutyCompleted(IDutyStateEventArgs args)
    {
        if (this.activeDuty is null)
            return;

        this.completionPendingSince = DateTimeOffset.Now;
    }

    private void OnDutyWiped(IDutyStateEventArgs args)
    {
        if (this.activeDuty is not null)
            this.activeDuty.WipeCount++;
    }

    private void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (flag == ConditionFlag.InDutyQueue)
        {
            this.OnQueueConditionChanged(value);
            return;
        }

        if (flag != ConditionFlag.BoundByDuty || value || this.activeDuty is null)
            return;

        if (this.completionPendingSince is not null)
            return;

        this.FinishActive(DutyResult.Abandon);
    }

    private void OnQueueConditionChanged(bool nowInQueue)
    {
        if (!this.config.TrackQueueTimes)
            return;

        if (nowInQueue)
        {
            this.queueStartedAt = DateTimeOffset.Now;
            this.queueJob = this.ResolveJob();
            this.log.Debug("[DutyLedger] Duty Finder queue started.");
        }
        else if (this.queueStartedAt is not null)
        {
            this.log.Debug("[DutyLedger] Duty Finder queue ended without a pop (cancelled/withdrawn) - not logged.");
            this.queueStartedAt = null;
            this.queueJob = null;
        }
    }

    private void OnCfPop(ContentFinderCondition poppedCfc)
    {
        if (!this.config.TrackQueueTimes || this.queueStartedAt is not { } startedAt)
            return;

        var waitTime = DateTimeOffset.Now - startedAt;
        var candidates = this.ResolveCandidateRouletteTags(poppedCfc.RowId);

        var entry = new QueueWaitEntry
        {
            QueuedAt = startedAt,
            WaitTime = waitTime,
            DutyName = poppedCfc.Name.ToString(),
            RouletteTag = candidates.Count > 0 ? candidates[0] : "",
            Job = this.queueJob ?? "Unknown",
            Role = ResolveRole(this.queueJob ?? "Unknown"),
        };

        this.config.QueueWaits.Add(entry);
        this.Save();

        this.log.Debug($"[DutyLedger] Queue popped after {waitTime}: {entry.DutyName} ({entry.Role}, roulette: \"{entry.RouletteTag}\")");

        this.queueStartedAt = null;
        this.queueJob = null;
    }

    private static string ResolveRole(string jobAbbreviation) => jobAbbreviation.ToUpperInvariant() switch
    {
        "PLD" or "WAR" or "DRK" or "GNB" => "Tank",
        "WHM" or "SCH" or "AST" or "SGE" => "Healer",
        "UNKNOWN" => "Unknown",
        _ => "DPS",
    };

    private void FinishPendingCompletion()
    {
        if (this.completionPendingSince is null)
            return;

        if (DateTimeOffset.Now - this.completionPendingSince.Value < TimeSpan.FromSeconds(5))
            return;

        this.completionPendingSince = null;
        this.FinishActive(DutyResult.Clear);
    }

    private void FinishActive(DutyResult result)
    {
        if (this.activeDuty is not { } active)
            return;

        this.activeDuty = null;

        var duration = DateTimeOffset.Now - active.StartedAt;
        if (duration.TotalSeconds < this.config.MinimumDurationSeconds)
            return;

        var entry = new DutyEntry
        {
            StartedAt = active.StartedAt,
            EndedAt = DateTimeOffset.Now,
            DutyName = active.DutyName,
            Job = active.Job,
            TerritoryTypeId = active.TerritoryTypeId,
            Result = result,
            WipeCount = active.WipeCount,
            GainedGil = active.GainedGil,
            GainedTomestones = new(active.GainedTomestones),
            DailyRouletteBonus = active.DailyRouletteBonus,
            AdventurerInNeedBonus = active.AdventurerInNeedBonus,
            FirstDutyBonus = active.FirstDutyBonus,
            RareLoot = new(active.RareLoot),
        };

        this.config.Entries.Add(entry);

        switch (active.CandidateRouletteTags.Count)
        {
            case 0:
                break;

            case 1:
                entry.RouletteTag = active.CandidateRouletteTags[0];
                entry.RouletteTagAuto = true;
                break;

            default:
                this.tagPromptWindow.PendingEntry = entry;
                this.tagPromptWindow.SetCandidates(active.CandidateRouletteTags);
                this.tagPromptWindow.IsOpen = true;
                break;
        }

        this.Save();
    }

    private void OnChatMessage(IChatMessage chatMessage)
    {
        var text = chatMessage.Message.TextValue;

        if (this.activeDuty is null)
            return;

        if (text.Contains("bonus", StringComparison.OrdinalIgnoreCase) || text.Contains("awarded", StringComparison.OrdinalIgnoreCase))
            this.log.Debug($"[DutyLedger] Reward-related chat line: \"{text}\"");

        var directGilMatch = DirectGilPattern.Match(text);
        if (directGilMatch.Success && int.TryParse(directGilMatch.Groups[1].Value.Replace(",", ""), out var directGil))
            this.activeDuty.GainedGil += directGil;

        if (DailyRouletteBonusPattern.IsMatch(text))
        {
            this.activeDuty.DailyRouletteBonus = true;
            this.AddGilFromLine(text);
        }

        if (AdventurerInNeedPattern.IsMatch(text))
        {
            this.activeDuty.AdventurerInNeedBonus = true;
            this.AddGilFromLine(text);
        }

        if (FirstDutyBonusPattern.IsMatch(text))
        {
            this.activeDuty.FirstDutyBonus = true;
            // Same as Daily/AiN above: this announcement line is the only place
            // the reward amount ever appears in chat, so if we don't grab it
            // here it silently never gets counted into GainedGil at all.
            this.AddGilFromLine(text);
            this.log.Debug($"[DutyLedger] First-duty-clear bonus announced: \"{text}\"");
        }

        var tomestoneMatch = TomestonePattern.Match(text);
        if (tomestoneMatch.Success && int.TryParse(tomestoneMatch.Groups[1].Value.Replace(",", ""), out var amount))
        {
            var stoneName = tomestoneMatch.Groups[2].Value.Trim();
            this.activeDuty.GainedTomestones[stoneName] =
                this.activeDuty.GainedTomestones.GetValueOrDefault(stoneName) + amount;

            // Check the LIVE count immediately - this is what catches landing
            // exactly on 2000/2000, which never produces a "cannot receive" line.
            this.CheckLiveTomestoneCap(stoneName);
            return;
        }

        if (this.config.TrackRareLoot)
            this.CheckRareLoot(text);
    }

    private void CheckRareLoot(string text)
    {
        if (this.activeDuty is null)
            return;

        if (text.Contains("gil", StringComparison.OrdinalIgnoreCase))
            return;

        var match = GeneralLootPattern.Match(text);
        if (!match.Success)
            return;

        var itemName = match.Groups[1].Value.Trim();
        var item = this.FindItemByName(itemName);
        if (item is not { } row)
            return;

        var category = row.ItemUICategory.IsValid ? row.ItemUICategory.Value.Name.ToString() : "";
        if (!RareLootCategories.Contains(category))
            return;

        this.activeDuty.RareLoot.Add(itemName);
        this.log.Debug($"[DutyLedger] Rare loot: {itemName} ({category})");
    }

    private void AddGilFromLine(string text)
    {
        if (this.activeDuty is null)
            return;

        foreach (Match m in GilAmountPattern.Matches(text))
        {
            if (int.TryParse(m.Groups[1].Value.Replace(",", ""), out var gil))
                this.activeDuty.GainedGil += gil;
        }
    }

    private string ResolveDutyName(uint cfcId, uint territoryId)
    {
        var cfc = this.data.GetExcelSheet<ContentFinderCondition>().GetRowOrDefault(cfcId);
        if (cfc is { } cfcRow && !string.IsNullOrWhiteSpace(cfcRow.Name.ToString()))
            return cfcRow.Name.ToString();

        var territory = this.data.GetExcelSheet<TerritoryType>().GetRowOrDefault(territoryId);
        if (territory is { } territoryRow && !string.IsNullOrWhiteSpace(territoryRow.PlaceName.Value.Name.ToString()))
            return territoryRow.PlaceName.Value.Name.ToString();

        return $"Unknown duty (CFC {cfcId}, territory {territoryId})";
    }

    private List<string> ResolveCandidateRouletteTags(uint cfcId)
    {
        var candidates = new List<string>();

        var cfc = this.data.GetExcelSheet<ContentFinderCondition>().GetRowOrDefault(cfcId);
        if (cfc is not { } row)
            return candidates;

        if (row.LevelingRoulette) candidates.Add("Leveling");
        if (row.LevelCapRoulette) candidates.Add("Level Cap Dungeons");
        if (row.HighLevelRoulette) candidates.Add("High-level Dungeons");
        if (row.GuildHestRoulette) candidates.Add("Guildhests");
        if (row.ExpertRoulette) candidates.Add("Expert");
        if (row.TrialRoulette) candidates.Add("Trials");
        if (row.MSQRoulette) candidates.Add("Main Scenario");
        if (row.AllianceRoulette) candidates.Add("Alliance Raids");
        if (row.NormalRaidRoulette) candidates.Add("Normal Raids");
        if (row.MentorRoulette) candidates.Add("Mentor");

        return candidates;
    }

    private string ResolveJob()
    {
        var localPlayer = this.objectTable.LocalPlayer;
        if (localPlayer is null)
            return "Unknown";

        var job = this.data.GetExcelSheet<ClassJob>().GetRowOrDefault(localPlayer.ClassJob.RowId);
        return job is { } row && !string.IsNullOrWhiteSpace(row.Abbreviation.ToString())
            ? row.Abbreviation.ToString()
            : "Unknown";
    }
}
