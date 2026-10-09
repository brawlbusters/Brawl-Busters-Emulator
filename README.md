# Brawl Busters server emulator

C# (.NET 9) server emulator for the Brawl Busters client in `../bin` (rev.16319).

Brawl Busters was shut down years ago and the official servers are gone. This project
rebuilds the server side so the original client can log in and play again.

## Showcase

[![Brawl Busters Emulator showcase](https://img.youtube.com/vi/wJ-pmlJMprw/hqdefault.jpg)](https://youtu.be/wJ-pmlJMprw)

The game client is not included in this repository.

Game client download:
http://www.mediafire.com/file/7c27acxte8gze7h/Brawl_busters_client.zip

## Run

1. `start-servers.bat` - starts the local MariaDB if there is one, builds the solution and starts the server.
2. `play-local.bat` - starts the game against it (`-ServerGroup "DEV_Daniel"`, which the client's own data
   maps to 127.0.0.1).
3. On the login screen type any new ID (4-10 characters) and password (4-16); the account is created on first
   login. A ready-made account `test` / `test` exists unless `NewAccounts.SeedTestAccount` is switched off.

Settings live in `config/emulator.json` (written with defaults on first start). Logs go to `logs/`.

## One server

The client only knows two addresses: a list of lobby servers and one chat server (`clientconfigdb`,
`SERVER_ADDR`). Everything therefore runs in a single process, `BrawlBusters.Server`:

| Port | Protocol | Purpose |
|---|---|---|
| 27100, 27110 (`LobbyPorts`) | TCP + UDP | login and the whole game flow; UDP hole punching |
| 27120 (`RelayPort`) | TCP + UDP | address advertised in the channel list; UDP relay for players who cannot reach each other |
| 27900 (`ChatPort`) | TCP | chat, buddies, invitations |

The earlier split into Auth, Main and Cast servers is gone: the client never connected to the last two.

## Database

`Database.Provider` in the config selects where accounts are kept.

* `MariaDb` - tables `users`, `characters`, `inventory`, `equipment`, `buddies`, `singleplay_clears`,
  `match_records`, `user_records`, `user_rewards`, `store_transactions`, `channels` (`database/schema.sql`).
  On start-up the server creates the database and every missing table, and if the database has no accounts yet
  it imports `data/accounts.json` once when that file exists.
* `Json` - one file, `data/accounts.json`. For development without a database server.

`python database/setup-local-mariadb.py` sets up a private MariaDB under `database/server` (unpack the official
winx64 zip there first), bound to 127.0.0.1:3306, with random passwords: root's is saved in
`database/server/root-password.txt`, the emulator's own user `rock` goes into the config. Run it again after a
reboot to start the database; `start-servers.bat` does that for you.

Accounts are held in memory while the server runs and written to the database about once a second, so game
logic never waits for SQL. Two columns are the exception and may be edited by hand at any time:
`users.grade` and `users.banned_until` are read back every two seconds and applied to the player at once.

## Grades, permissions, commands

`users.grade`: 0 player (default), 1 moderator, 2 game master, 3 developer. The `Staff` map in the config
(`"login": "gm"`) sets a grade at login. A change of grade reaches the player immediately, with a system message.

| Grade | May do |
|---|---|
| Moderator | see staff-only channels, enter a full channel, observe matches, `/online`, `/observe`, `/kick` |
| Game master | the above, `/notice`, `/ban`, `/unban` |
| Developer | the above, `/gold`, `/cash`, `/exp`, `/level`, `/item`, `/grade`, `/log` |

Commands are typed in room chat, in a chat-server room, or in the server window (which may do everything; the
slash is optional there, and `stop` shuts the server down). `/help` lists what the caller may use.
A channel with `"StaffOnly": true` (config, or the `channels` table) is only listed for staff.
A channel with `"LobbyPort": 27110` (one of `LobbyPorts`) lives on that port only: a player entering it from another
port is moved there with the client's own server change (sTransServer / sUserRestart). Off unless set.

## Logging

Every log line belongs to a channel: General, Network, Packets, Session, Lobby, Room, Match, Chat, Bots, Database,
Commands. `Logging.Channels` in the config switches each one on or off at start; `/log <channel> on|off`
does it while the server runs (`/log packets off` stops the hex dump of every message). Warnings and errors
are always written. `Logging.ConsoleLevel` and `Logging.FileLevel` set the minimum level per target.
Messages the server has no handler for are also collected in `logs/missing-packets-Server.log`.

## Threads

Sockets are read and written on many threads. Game state (rooms, channels, matches, bots, chat rooms) is only
ever changed by one message at a time: each message is handled while its session holds the `World` gate, and
sending never blocks because every connection has its own outgoing queue. An account can have one live session;
logging in again closes the older connection. The chat server only accepts a login that matches a live game
session from the same address.

## Layout

```
src/
  BrawlBusters.Server/          the executable: start-up, LobbySession, ServerConsole
  BrawlBusters.Core/
    Configuration/              EmulatorSettings (config/emulator.json)
    Logging/                    Log, LogChannel
    Network/                    framing, LZF, PacketReader/Writer, GameConnection, GameServer, HolePunchServer
    Protocol/                   MsgCategory, NetError, packet builders and parsers
    Security/                   password cipher and hasher, Permissions
    Persistence/                AccountRepository (in-memory store), MariaDb and Json backends
    Data/                       Account model, game data loaded from data/game
    Sessions/                   ClientSession, MessageRouter, World, GameFlow, rooms, channels, bots
    Sessions/Handlers/          one handler per client message category
    Chat/                       chat server: ChatHub, buddies, invitations
    Commands/                   slash commands
database/                       schema.sql, setup-local-mariadb.py, server/ (private MariaDB)
tools/                          headless test clients (test_*.py), autopilot, game data export
docs/                           protocol notes decoded from the client
```

## Adding a message

1. Find it in the client (addresses in `docs/PROTOCOL.md`, decoded tables in `docs/PROTOCOL_INVENTORY.md`).
2. Add a builder or parser in `Core/Protocol/Packets`.
3. Add or extend an `IMessageHandler` in `Core/Sessions/Handlers`; put multi-message replies in `GameFlow`.
4. Register the handler in `StandardRouter`, and cover it with a `tools/test_*.py` script.

## Tests

`tools/test_*.py` are headless clients that speak the real protocol. They take the server's ports from the
environment (`BB_LOBBY_PORT`, `BB_CHAT_PORT`, `BB_RELAY_PORT`) so a second server instance can be tested on
other ports: point it at another config with the `BRAWLBUSTERS_CONFIG` environment variable.

## Public server and local play

`config/emulator.json` is the public server: `PublicAddress` <server address>, lobby 27100 / 27110, relay 27120, chat 27900
(open all four for TCP, and 27100 / 27110 / 27120 for UDP as well). `PublicAddress` must be the address players reach
the machine on - it is what the server puts into the channel list and uses for the UDP relay.
`config/emulator.local.json` is the same with 127.0.0.1, for play on this PC: `start-servers-local.bat`.

The client takes its server from the SERVER_ADDR table in `Data/xmandb.bus`, chosen with `-ServerGroup`:

| Launcher | Group | Address |
|---|---|---|
| `play-online.bat` | `AS_ID_Internal` | <server address>:27100;<server address>:27110, chat <server address>:27900 |
| `play-local.bat` | `DEV_Daniel` | 127.0.0.1:27100;127.0.0.1:27110, chat 127.0.0.1:27900 |

Change a row with `python tools/set_client_server.py <group> "<lobby list>" <chat address>` (`list` shows all rows).
The first change keeps the untouched file as `Data/xmandb.bus.before-server-change`. Players need the changed
`Data/xmandb.bus` (or the same command run on their copy).

## Notice and store pages

The client shows two web pages in its built-in browser: the notice panel of the main screen and the home page of the
store. The emulator serves them itself on `WebPort` (27180, 0 = off) from the `web` folder:

| Address | File |
|---|---|
| `http://<PublicAddress>:27180/notice` | `web/notice.html` |
| `http://<PublicAddress>:27180/shop` | `web/shop.html` |

Edit the files while the server runs; pictures and other files put into `web` are served too. The client has the
addresses in its executable - `python tools/set_client_web.py` writes the configured ones into every
`bin/pbclient*.exe` (`show` lists the current ones; the untouched files are kept as `*.before-web-change`). Open the
port for TCP like the others.

## License & Attribution Requirement

If you use this software in any form (public server, private server, modified build, or
derived work), you MUST comply with the [Custom License (BBECL v1.0)](LICENSE).

This includes, at minimum:

- Clearly stating that your server/software is powered by this project.
- Including a visible reference to the official repository:
  https://github.com/brawlbusters/Brawl-Busters-Emulator
- Keeping all license and copyright notices intact.
- Making modifications publicly available if the software is used or distributed.
- Not using it commercially.

Failure to comply with the license terms is a violation of the license.

## Contacts

For support, join our Discord server: https://discord.gg/j3FwbAgzXw

For info, contributions or bug reports related to the emulator, please open an issue on
this repository.
