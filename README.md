# Duty Ledger

Duty Ledger is a Dalamud plugin for FINAL FANTASY XIV that tracks completed duty history, roulette bonuses, rare loot, and Duty Finder queue times.

## Features

- Tracks duty name, job, duration, and result (Clear / Wipe-Abandon) for every completed instance
- Automatically detects roulette category from `ContentFinderCondition` game data - only prompts you when a duty genuinely belongs to more than one roulette pool
- Records daily-roulette bonus, Adventurer-in-Need bonus, and first-duty-clear bonus separately
- Tracks gil and tomestones earned per run, with a live absolute (2000) cap warning read directly from your inventory
- Rare loot tracker: keeps minions, mounts, orchestrion rolls, and Triple Triad cards obtained during duties; discards ordinary gear and materials automatically
- Queue Time Tracker: measures Duty Finder wait time from queue join to pop, grouped by role and roulette category
- Provides history filtering, per-duty averages, and a weekly activity chart
- Supports compact and full layouts
- Includes a dedicated Settings window with color customization and a low-overhead Eco Mode for large histories

## Installation through Dalamud

The plugin is available after a successful GitHub Actions release.

1. Start FFXIV and enter `/xlsettings`.
2. Open the **Experimental** tab.
3. Find **Custom Plugin Repositories**.
4. Add this URL:

   ```text
   https://raw.githubusercontent.com/Kriss-Hietala/DutyLedger/main/repo.json
   ```

5. Click **Save and Close**.
6. Enter `/xlplugins`.
7. Search for **Duty Ledger**.
8. Click **Install**.
9. Open the plugin with:

   ```text
   /dutyledger
   ```

   or, for the compact live-tracker window:

   ```text
   /dutyledgermini
   ```

## Usage

The main window lists your duty history with sortable/searchable columns (date, duty, job, duration, result, roulette, bonuses, wipes, rewards, loot), plus collapsible **Statistics & Summary**, **Duty Averages**, and **Queue Times** sections. Use **Compact mode** for a small always-on overlay showing the duty or queue currently in progress, and **Settings** for colors, chart size, and tracker toggles.

The tracker saves progress history in the plugin configuration directory. **Export CSV** buttons are available for both the duty log and the queue-time log.

## Roulette and bonus detection

The plugin reads the per-roulette boolean flags (`LevelingRoulette`, `GuildHestRoulette`, `MentorRoulette`, etc.) directly off each duty's `ContentFinderCondition` row. When a duty belongs to exactly one roulette pool, the tag is applied automatically; when it belongs to several at once (common for max-level dungeons), a prompt asks you to pick from only the genuinely possible options. Daily-roulette, Adventurer-in-Need, and first-duty-clear bonuses are detected from distinct, independently-verified chat messages and are never double-counted against each other.

## Development build

Requirements:

- .NET 10 SDK
- Dalamud files required by `Dalamud.NET.Sdk/15.0.0`

Build locally:

```bash
~/.dotnet/dotnet build -c Release --no-logo
```

## Releases

Every push to `main` runs GitHub Actions. The workflow automatically assigns version `1.0.<run-number>.0`, builds the Release configuration, creates `publish.zip`, publishes a GitHub Release, and updates the plugin manifests.

The release archive contains:

```text
DutyLedgerPlugin.dll
DutyLedgerPlugin.deps.json
DutyLedgerPlugin.json
```

## Known limitations

- Roulette category detection is only automatic when a duty is in exactly one roulette pool; ambiguous max-level dungeons still require a quick manual pick from the narrowed-down options.
- Queue time is attributed to whichever duty/roulette actually popped if you queue for multiple at once - there is no way to know which specific selection caused the wait.
- Reward and loot parsing rely on English-client chat text patterns; other client languages need matching regex adjustments in `Plugin.cs`.
- Runs shorter than `Configuration.MinimumDurationSeconds` (default 15s) are discarded to filter out accidental enter/leave events.

## License

MIT - see `LICENSE`.
