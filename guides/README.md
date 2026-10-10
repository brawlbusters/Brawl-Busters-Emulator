# Guides

How-to pages for people who run the emulator or work on it. The [main README](../README.md) covers building,
the database and the project layout; these pages cover single tasks.

## Running a server

| Page | What it answers |
|---|---|
| [Client check](client-check.md) | What the client check is, how to switch it on, what players need, what the log lines mean |
| [Updating client files](updating-client-files.md) | How to change `xmandb.bus` (or ship a new client) without players being flagged or locked out |
| [Reward limits](reward-limits.md) | What the server refuses to believe from a client, the settings behind it, how to tune them |
| [Server address and launchers](server-address.md) | Pointing a client at your server, localhost and public addresses, launcher `.bat` files |

## Working on the code

| Page | What it answers |
|---|---|
| [Commands](commands.md) | The slash commands, who may use them, and how to add one |

## Quick answers

- **A player gets a "client version" error at login.** The client check is on `require` and their client has no
  `LightFX.dll`, or their `xmandb.bus` is not the server's version. See [Client check](client-check.md#troubleshooting).
- **I edited `xmandb.bus` and now my own client is refused.** Restart the server, it reads the file at start.
  See [Updating client files](updating-client-files.md).
- **The log says `Not plausible: ...`.** A client reported more than the match length allows. See
  [Reward limits](reward-limits.md#reading-the-log).
