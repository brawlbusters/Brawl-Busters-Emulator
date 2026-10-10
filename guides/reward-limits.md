# Reward limits

[Guides](README.md) › Reward limits

What the server refuses to take a client's word for, and how to tune it.

## What a client can and cannot influence

Balances, inventory, prices, drop chances and rewards live on the server: in the database and in the server's own
tables under `data/game/`. A client sends "buy catalog item 2024, option 1", never a price. Editing the client's
files changes what that player's screen shows and nothing else.

Two things are different, because the server cannot see them happen:

| Play | Runs on | What the server gets |
|---|---|---|
| Single play stages | The player's computer | "Stage won" |
| Matches | The host player's computer | Kills, assists, waves, stars, scores, end-of-match statistics |

A changed client can report whatever it likes there. The limits below hold those reports against what the server
does know: its own clock and its own list of who is in the match.

## The limits

**Single play**

- A stage can only be started when the stage it requires has been cleared. The tutorial counts as stage 1.
- A win counts only for a stage that was started, and not sooner than `SinglePlayMinSeconds` after it loaded.

**Matches**

- Match events are only taken while a match is running, and only from its host.
- A kill must name players (or bots) of that match, and nobody kills himself.
- Kills, assists, slays and revives of a player are cut down to what the length of the match allows.
- Survival waves come in order, not sooner than `MinSecondsPerWave` after the one before, with at most four
  stars each and no more waves than the rule has.
- The host's end-of-match statistics (My Stats, daily missions) are taken once per match, only for players who
  were in it, and only for a match of at least `MinSecondsForStatistics`.

The payout tables add their own floor: a mode pays nothing for a match shorter than its `time_min`
(90 to 210 seconds depending on the mode).

## Settings

Under `AntiCheat` in `config/emulator.json`. The server reads them at start.

| Setting | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | Switches all limits on this page off or on |
| `SinglePlayMinSeconds` | `10` | Shortest time between loading a stage and a win that counts |
| `SinglePlayRequirePrevious` | `true` | Stages open in order |
| `MaxKillsPerMinute` | `10` | Kills, and assists, per player per minute of match; `0` = no limit |
| `MaxSlaysPerMinute` | `150` | Zombies slain per player per minute; `0` = no limit |
| `MaxRevivesPerMinute` | `6` | Revives per player per minute |
| `Allowance` | `5` | Added to every per-minute limit, so a short match is not judged too strictly |
| `MinSecondsPerWave` | `5` | Shortest time between two cleared waves |
| `MinSecondsForStatistics` | `60` | Shortest match whose end-of-match statistics are used |

A limit for a match is `Allowance + rate x minutes`. With the defaults, a five-minute match allows 55 kills
per player.

The numbers are estimates of generous but possible play, not values from the original game. If a real match is
ever cut down, raise the limit that fired.

## Reading the log

Every refusal is a warning on the `Match` log channel that starts with `Not plausible`:

```
Not plausible: 300 kill(s) reported for player 1194, at most 6 possible in that time - counted as 6
Not plausible: wave 3 reported as cleared - less than 5 s after the wave before; ignored
Not plausible: single play stage 2 reported as won after 0.4 s - not counted
Not plausible: single play stage 7 asked for before stage 6 was cleared - refused
Room 4: host statistics for a match of 12 s - too short, not used
Room 4: match event Kill reported while no match is running - ignored
```

The line names the player or room. Nobody is banned or kicked by a limit; the report is cut down or ignored and
the match goes on. Acting on a repeat offender is up to you (`/kick`, `/ban`, see [Commands](commands.md)).

## What the limits cannot do

A host who plays a match of normal length and reports believable numbers that are not true, such as the wrong
winner, cannot be told apart from an honest one. That includes ranked matches. Closing that would need the
server to run the match itself, which this game does not do.

Edited gameplay tables (more health, more damage) are a separate problem; that is what the
[client check](client-check.md) is for.

## For developers

| Part | Where |
|---|---|
| The limits | [`src/BrawlBusters.Core/Sessions/MatchGuard.cs`](../src/BrawlBusters.Core/Sessions/MatchGuard.cs) |
| Test | `python tools/test_anticheat.py` |

`test_single.py`, `test_results.py` and `test_boss.py` play a whole match in about a second to check packet
layouts. With the limits on, part of their checks fail for exactly that reason. Run them against a server with
`"AntiCheat": { "Enabled": false }`.
