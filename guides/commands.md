# Commands

[Guides](README.md) › Commands

Slash commands for staff, and how to add one.

## Where to type them

- In room chat or lobby chat in the game.
- In the server window. It may do everything, the slash is optional there, and `stop` shuts the server down.

A command answers the caller only, as a message from `#GMMessage`. Text that starts with a slash and is not a
command is sent as ordinary chat.

## Grades

`users.grade` in the database: 0 player, 1 moderator, 2 game master, 3 developer. The `Staff` map in the config
(`"login": "gm"`) sets a grade at login. `/grade` changes it at once; the player is told and gets the rights
without logging in again.

## The commands

| Command | Aliases | Needs | Does |
|---|---|---|---|
| `/help` | `/commands`, `/?` | anyone | Lists the commands the caller may use |
| `/kick <nickname>` | | Moderator | Disconnects a player |
| `/observe <room number>` | `/gmo`, `/gm_observe` | Moderator | Enters a room as an observer |
| `/online` | `/who` | Moderator | Shows who is connected and where |
| `/ban <nickname> <minutes>` | | Game master | Locks an account for a while |
| `/notice <text>` | `/gm` | Game master | Shows a system message to every player |
| `/unban <nickname>` | | Game master | Lifts a ban |
| `/cash <amount> [nickname]` | | Developer | Adds RT; a negative amount takes it away |
| `/exp <amount> [nickname]` | | Developer | Adds experience |
| `/gold <amount> [nickname]` | | Developer | Adds BP |
| `/grade <nickname> <player\|mod\|gm\|dev>` | | Developer | Changes an account's grade |
| `/item <item id> [quantity] [nickname]` | | Developer | Puts an item into an inventory |
| `/level <1-99> [nickname]` | | Developer | Sets a level |
| `/log [channel] [on\|off]` | | Developer | Shows or switches the log channels |

Where a nickname is optional, the command acts on the caller. A higher grade may use everything a lower one may.
Nobody can kick a higher grade or ban an equal or higher one.

## Adding a command

Every command is one class in
[`src/BrawlBusters.Core/Commands/Commands/`](../src/BrawlBusters.Core/Commands/Commands). The server finds the
classes itself at start; there is no list to add the new one to.

```csharp
using BrawlBusters.Core.Data;
using BrawlBusters.Core.Security;

namespace BrawlBusters.Core.Commands;

public sealed class UnmuteCommand : ChatCommand
{
    public override string Name => "unmute";
    public override string[] Aliases => ["um"];
    public override Permission Required => Permission.Kick;
    public override string Usage => "unmute <nickname>";
    public override string Description => "lets a player chat again";

    public override Task ExecuteAsync(CommandContext context, string[] arguments, CancellationToken cancellationToken)
    {
        Account? target = context.Target(Need(arguments, 0));
        if (target is null) return context.Reply("No such player.");

        // ... do the work ...
        return context.Reply($"{target.Nickname} may chat again.");
    }
}
```

What the base class gives you:

| Helper | Use |
|---|---|
| `Need(arguments, i)` | Argument `i`, or the usage line is shown to the caller |
| `NeedNumber(arguments, i)` | The same, as a number |
| `Optional(arguments, i)` | Argument `i` or `null` |
| `RefreshClientAsync(userId, ...)` | Pushes changed BP, RT, exp and level to that player's client |
| `context.Target(nickname)` | The named account, or the caller when no name was given |
| `context.Reply(text)` | Answers the caller |
| `context.Session` | The caller's game connection; `null` on the server console |
| `context.Accounts.Update(id, account => ...)` | Changes an account and saves it |

Rules that keep it working:

- **`Required`** is the permission, not the grade. `Permissions.cs` says which grade holds which permission.
  The registry refuses the command for anybody without it, before `ExecuteAsync` runs.
- **`Name` must be unique.** Two commands with one name stop the server at start with a message naming both.
- **Throw `CommandUsageException`** for wrong arguments; the caller is shown `Usage`.
- `/help` lists commands by required grade, then by name.

A command that only adds to a number (like `/gold`) derives from `CurrencyCommand` instead and is three lines;
see `GoldCommand.cs`.

## Testing

```bash
python tools/test_lobby_commands.py
python tools/test_grade.py
```

`test_lobby_commands.py` types every command in lobby chat, as a player and as a developer. Add a check for a
new command there.
