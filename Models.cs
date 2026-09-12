// Models.cs
// Data models and persistent configuration for the DutyLedger plugin.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Serialization;
using Dalamud.Configuration;

namespace DutyLedger;

public enum DutyResult
{
    Clear,
    Abandon,
}

public enum SortMode
{
    Newest,
    Longest,
    Duty,
}

public sealed class ActiveDuty
{
    public DateTimeOffset StartedAt { get; init; }
    public string DutyName { get; init; } = "Unknown";
    public string Job { get; init; } = "Unknown";
    public uint TerritoryTypeId { get; init; }
    public uint ContentFinderConditionId { get; init; }
    public int WipeCount { get; set; }
    public int GainedGil { get; set; }
    public Dictionary<string, int> GainedTomestones { get; set; } = new();
    public bool DailyRouletteBonus { get; set; }
    public bool AdventurerInNeedBonus { get; set; }
    public bool FirstDutyBonus { get; set; }
    public List<string> CandidateRouletteTags { get; init; } = [];
    public List<string> RareLoot { get; set; } = [];
}

/// <summary>Read-only snapshot of the currently active duty, for the compact window.</summary>
public readonly struct ActiveDutySnapshot
{
    // Explicit constructor required (CS8983): a readonly struct with property
    // initializers must declare one, otherwise the default parameterless
    // constructor would not run those initializers.
    public ActiveDutySnapshot()
    {
    }

    public bool IsActive { get; init; }
    public string DutyName { get; init; } = "";
    public string Job { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public int WipeCount { get; init; }
}

/// <summary>Read-only snapshot of an in-progress Duty Finder queue, for the compact window.</summary>
public readonly struct QueueSnapshot
{
    public QueueSnapshot()
    {
    }

    public bool IsQueuing { get; init; }
    public DateTimeOffset QueuedAt { get; init; }
    public string Job { get; init; } = "";
}

public sealed class DutyEntry
{
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; }
    public string DutyName { get; set; } = "Unknown";
    public string Job { get; set; } = "Unknown";
    public uint TerritoryTypeId { get; set; }
    public DutyResult Result { get; set; }
    public string RouletteTag { get; set; } = "";
    public bool RouletteTagAuto { get; set; }
    public int WipeCount { get; set; }
    public int GainedGil { get; set; }
    public Dictionary<string, int> GainedTomestones { get; set; } = new();
    public bool DailyRouletteBonus { get; set; }
    public bool AdventurerInNeedBonus { get; set; }
    public bool FirstDutyBonus { get; set; }
    public List<string> RareLoot { get; set; } = [];

    [JsonIgnore]
    public TimeSpan Duration => this.EndedAt - this.StartedAt;

    [JsonIgnore]
    public int TotalTomestones => this.GainedTomestones.Count == 0 ? 0 : this.GainedTomestones.Values.Sum();

    [JsonIgnore]
    public double GilPerMinute => this.Duration.TotalMinutes > 0 ? this.GainedGil / this.Duration.TotalMinutes : 0;

    [JsonIgnore]
    public double TomestonesPerMinute => this.Duration.TotalMinutes > 0 ? this.TotalTomestones / this.Duration.TotalMinutes : 0;
}

/// <summary>
/// One measured Duty Finder queue: from ConditionFlag.InDutyQueue turning on
/// to IClientState.CfPop firing. Queues that get cancelled/withdrawn before
/// popping are NOT recorded here (see Plugin.cs) - only completed waits.
/// </summary>
public sealed class QueueWaitEntry
{
    public DateTimeOffset QueuedAt { get; set; }
    public TimeSpan WaitTime { get; set; }
    public string DutyName { get; set; } = "";

    /// <summary>Best-guess roulette category the pop belongs to (first candidate from ContentFinderCondition flags) - empty if the duty isn't in any roulette pool, or if multiple were queued and this is ambiguous.</summary>
    public string RouletteTag { get; set; } = "";
    public string Job { get; set; } = "";
    public string Role { get; set; } = "";
}

public struct RgbaColor
{
    public float R { get; set; }
    public float G { get; set; }
    public float B { get; set; }
    public float A { get; set; }

    public RgbaColor(float r, float g, float b, float a)
    {
        this.R = r;
        this.G = g;
        this.B = b;
        this.A = a;
    }

    [JsonIgnore]
    public Vector4 Vector
    {
        get => new(this.R, this.G, this.B, this.A);
        set { this.R = value.X; this.G = value.Y; this.B = value.Z; this.A = value.W; }
    }
}

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public List<DutyEntry> Entries { get; set; } = [];
    public List<QueueWaitEntry> QueueWaits { get; set; } = [];

    public int MinimumDurationSeconds { get; set; } = 15;
    public bool CompactMode { get; set; } = false;

    public float ChartHeight { get; set; } = 180f;
    public bool ShowWeeklyChart { get; set; } = true;

    public RgbaColor ColorClear { get; set; } = new(0.35f, 0.85f, 0.45f, 1f);
    public RgbaColor ColorAbandon { get; set; } = new(0.90f, 0.35f, 0.35f, 1f);
    public RgbaColor ColorAccent { get; set; } = new(0.45f, 0.65f, 1.00f, 1f);
    public RgbaColor ColorGold { get; set; } = new(0.95f, 0.80f, 0.30f, 1f);
    public RgbaColor ColorMuted { get; set; } = new(0.65f, 0.65f, 0.65f, 1f);

    public Dictionary<string, DateTimeOffset> CappedTomestones { get; set; } = new();

    public bool TrackRareLoot { get; set; } = true;
    public bool TrackQueueTimes { get; set; } = true;
    public bool EcoMode { get; set; } = false;

    public void ResetColorsToDefault()
    {
        this.ColorClear = new(0.35f, 0.85f, 0.45f, 1f);
        this.ColorAbandon = new(0.90f, 0.35f, 0.35f, 1f);
        this.ColorAccent = new(0.45f, 0.65f, 1.00f, 1f);
        this.ColorGold = new(0.95f, 0.80f, 0.30f, 1f);
        this.ColorMuted = new(0.65f, 0.65f, 0.65f, 1f);
    }
}
