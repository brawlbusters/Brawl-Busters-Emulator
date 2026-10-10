# Server address and launchers

[Guides](README.md) › Server address and launchers

How a client finds your server, and how to point it somewhere else.

## Two addresses have to agree

| Where | What it is | Who uses it |
|---|---|---|
| The client's server table | Where the client connects first, to log in | The client |
| `PublicAddress` in `config/emulator.json` | The address the server puts into the channel list and uses for the UDP relay | The server |

After login the server sends the client to `PublicAddress` for its channels. If that is not an address the
player can reach, they log in and then lose the connection. For a public server both must be the address players
reach the machine on.

## Ports

| Port | Protocol | Use |
|---|---|---|
| 27100, 27110 | TCP and UDP | Lobby; UDP for hole punching and the client check |
| 27120 | UDP | Relay |
| 27900 | TCP | Chat |
| 27180 | TCP | Notice and store pages |

Open them in the firewall of the server machine. The server listens on every address of the machine.

## The client's server table

The client reads its servers from the `SERVER_ADDR` table inside `Data/xmandb.bus`. Each row is a *server
group* with a lobby list and a chat address. The launcher picks the row with `-ServerGroup`.

Show all rows:

```bash
python tools/set_client_server.py list
```

Point a row at your server:

```bash
python tools/set_client_server.py AS_ID_Internal "203.0.113.5:27100;203.0.113.5:27110" 203.0.113.5:27900
```

The row is rewritten in place, so the new text may not be longer than the old one. The first change keeps the
untouched file as `Data/xmandb.bus.before-server-change`. Players need the changed `xmandb.bus`, or the same
command run on their copy.

This table is left out of the [client check](client-check.md), so changing an address never flags a client.

## Launchers

A launcher is a `.bat` file next to the `bin` folder that starts the client with a server group:

```bat
@echo off
cd /d "%~dp0bin"
start "" "pbclient.exe" -Publisher "SG" -Language "EN" -ServerGroup "AS_ID_Internal"
```

To have one client folder that can reach several servers, give each server its own row and its own launcher.
A typical set:

| Launcher | Group | Goes to |
|---|---|---|
| `play-online.bat` | `AS_ID_Internal` | The public server |
| `play-localhost.bat` | `DEV_Daniel` | `127.0.0.1`, a server on the same PC |

`127.0.0.1` always means "this computer". A launcher for it only works on the machine the server runs on.

## Checking that the server is reachable

On the server machine, in a Command Prompt:

```bash
netstat -ano | findstr "LISTENING" | findstr ":27100 :27110 :27900 :27180"
```

Each line is a port that is open. `0.0.0.0:27100` means every address of the machine. UDP ports never show as
`LISTENING`; list them with:

```bash
netstat -ano -p UDP | findstr ":27100 :27110 :27120"
```

## Where the logs are

| Log | Place |
|---|---|
| Server | `logs/Server-YYYYMMDD.log` in the emulator folder |
| Client | `Documents\Busters\Log\Log_YYYYMMDD.txt` on the player's PC (`Log0_`, `Log1_` ... for more clients at once) |

The client's log names every server packet it rejected, which is the first place to look when a screen
misbehaves.
