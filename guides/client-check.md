# Client check

[Guides](README.md) › Client check

The client keeps its gameplay tables in `Data/xmandb.bus`: character health and movement, attacks, damage,
weapons, monsters, waves and match rules. The file is only XOR-obfuscated, and public tools unpack and repack it.
A player who edits it gets more health or damage in matches. The client sends no checksum of its own; the
original game left that to third-party anti-cheat software that does not run here.

The client check closes that gap. A small module runs inside the game, hashes the tables and proves the result
to the server.

## How it works

1. The game looks for a library called `LightFX.dll` when it starts (Alienware keyboard lighting) and works the
   same without it. The check module is built under that name, so the game loads it by itself. The executable is
   not changed.
2. Once at start and then every minute, the module hashes every file inside `xmandb.bus` except the server
   address table.
3. It asks the server for a random challenge on the lobby's UDP port and answers with
   `SHA-256(challenge + table digest + salt)`.
4. The server compares that with the digest of its own copy of the file and remembers the result per IP address.
5. At login the server looks up the result for the address the login comes from.

The challenge is new every time, so a recorded answer cannot be replayed.

## The client is not in this repository

The repository holds the emulator only; the game client is a separate download (see the
[main README](../README.md)). Two files therefore have to be brought together by hand:

| File | Comes from | Has to be |
|---|---|---|
| `LightFX.dll` | This repository, [`tools/client_check/`](../tools/client_check) | In the `bin` folder of every player's client. Add it to the client package you hand out, or players copy it in themselves |
| `xmandb.bus` | The client download, `Data` folder | Known to the server, so it can tell which tables are the allowed ones |

The server keeps what it allows in its own folder, [`data/client/`](../data/client):

- `digests.txt` lists allowed digests, one per line. It is part of the repository and holds the digest of the
  released client, so a fresh checkout verifies that client without any client file on the server.
- Every `xmandb.bus` you put into that folder is allowed too (any file name ending in `.bus`). These files stay
  on your machine; they are not committed.

If the folder does not exist, the server falls back to the client the emulator sits in (`../Data/xmandb.bus`).
With nothing to compare with, the start-up log says `No game tables to compare with` and no client can be
verified.

## Modes

Set in `config/emulator.json`:

```json
"AntiCheat": {
  "ClientCheck": "require"
}
```

| Mode | A login without a verified client |
|---|---|
| `off` | Is not looked at |
| `log` (default) | Is allowed and written to the server log |
| `require` | Is refused; the client shows its "client version" error on the login screen |

The server reads the mode at start. Restart it after a change.

## Rolling it out

1. Leave the mode on `log`.
2. Give every player `LightFX.dll` for the `bin` folder of their client, next to `pbclient.exe`. A built copy is
   in [`tools/client_check/`](../tools/client_check); `build.bat` there rebuilds it (Visual Studio 2022 with the
   C++ tools, 32-bit).
3. Watch the log. Each login without the module writes
   `Client check: '<login>' logs in with no client check module (bin/LightFX.dll)`.
4. When those lines stop, switch to `require` and restart.

## Settings

All under `AntiCheat` in the config.

| Setting | Default | Meaning |
|---|---|---|
| `ClientCheck` | `log` | `off`, `log` or `require` |
| `ClientDataFile` | `data/client` | Folder (or one file) with the allowed versions, relative to the emulator folder: every `.bus` in it and the digests in its `digests.txt` |
| `AllowedClientDigests` | empty | More allowed digests (64 hex digits each), in addition to `data/client/digests.txt` |
| `ClientCheckMaxAgeSeconds` | `180` | How old the last proof may be at login |

## Reading the log

| Line | Meaning |
|---|---|
| `[ClientCheck] Game tables of <path>: digest C805A1A4...` | At start: an archive in `data/client` whose tables are allowed |
| `[ClientCheck] Game tables allowed by digest C805A1A4...` | At start: a digest from `digests.txt` or the config |
| `[ClientCheck] Mode 'require': ...` | At start: the active mode |
| `[ClientCheck] <ip>: game tables verified` | That address went from unverified to verified |
| `[ClientCheck] <ip>: the game tables of this client are MODIFIED (or of another version)` | The module answered, the tables are not an allowed version |
| `Client check: '<login>' logs in with ...` | `log` mode: a login that `require` would refuse |
| `Handshake refused: '<login>': ...` | `require` mode: the login was refused, with the reason |

## Troubleshooting

**A player gets the "client version" error.**
Look for the `Handshake refused` line of that login.

- `no client check module (bin/LightFX.dll)`: the file is missing from their `bin` folder, or they logged in
  within the first seconds after starting the game. Have them check the file and try again.
- `MODIFIED game tables`: their `xmandb.bus` is not the server's version. Either it was edited, or they have an
  older client. See [Updating client files](updating-client-files.md).

**My own client is refused after I edited the file.**
The server allows what is in `data/client`, not what your client folder holds. Copy the edited `xmandb.bus`
there (or add its digest to `digests.txt`) and restart. See [Updating client files](updating-client-files.md).

**Nobody is verified, and the start-up log says "No game tables to compare with".**
`data/client` has neither a `.bus` file nor a `digests.txt` with a digest in it. Put one of them there
(`python tools/client_digest.py <path>` prints a digest).

## Limits

- A client is identified by its IP address. Two computers behind one router share a result.
- The module runs on the player's computer. It stops people who edit the file with an unpack tool. Somebody who
  can take the module apart, or who changes the game's memory instead of the file, gets past it.
- Only `xmandb.bus` is covered, not the executable or the other archives.
- It does not replace the [reward limits](reward-limits.md), which work without any module.

## For developers

| Part | Where |
|---|---|
| Server side | [`src/BrawlBusters.Core/Security/ClientCheck.cs`](../src/BrawlBusters.Core/Security/ClientCheck.cs) |
| Module | [`tools/client_check/client_check.c`](../tools/client_check/client_check.c) |
| Test | `python tools/test_client_check.py` |

Datagrams on the lobby's UDP port:

```
"BBIC" 01                 ->   "BBIC" 02 + 16 bytes challenge
"BBIC" 03 + 32 bytes      ->   "BBIC" 04 + 1 (verified) / 0
```

The digest is SHA-256 over name, size and stored bytes of each file in the archive's table order, skipping
files whose name contains `clientconfigdb`.
