# Updating client files

[Guides](README.md) › Updating client files

With the [client check](client-check.md) on, a client whose `Data/xmandb.bus` is not a version the server allows
counts as modified. This page is how to change that file on purpose, or ship a new client, without anybody
being flagged or locked out.

## The rule

The server has no built-in "correct" file. What it allows is in its own folder, `data/client/` inside the
emulator folder:

| In `data/client/` | Effect |
|---|---|
| `digests.txt` | Every digest listed in it is allowed. Part of the repository |
| Any `*.bus` file | Its tables are allowed. Stays on your machine, not committed |

The server reads the folder at start. A client passes when its tables match **one of those versions**.

The client itself is not part of this repository. Nothing you commit here updates a player's files; a new client
version reaches players the same way the client does.

## What does not need any of this

- **Changing the server address** with `tools/set_client_server.py`. The address table
  (`clientconfigdb.xml`) is left out of the check. See [Server address and launchers](server-address.md).
- **Changing other files**: the executable, other `.bus` archives, the UI files. Only `xmandb.bus` is checked.
- **Changing the server's own data** under `data/game/`. That is what the server pays rewards and prices from;
  clients never see it.

## Updating the file for everyone

1. Make your change to the client's `Data/xmandb.bus`.
2. Tell the server about the new version, one of:
   - copy the new file into the emulator's `data/client/` folder, replacing the old one, or
   - add its digest to `data/client/digests.txt`:

     ```bash
     python tools/client_digest.py "..\Data\xmandb.bus"
     ```

3. Restart the server. The start-up log lists what it allows:
   `[ClientCheck] Game tables of ...\data\client\xmandb.bus: digest 1A2B3C4D...`
4. Give players the new file.

If the digest is in `digests.txt` and you commit that file, everybody who runs the emulator from the repository
allows the new version as well.

## Grace period: allow the old and the new version

Players who still have the old file are refused in `require` mode as soon as the server no longer allows it.
To let both in for a while, keep both versions in `data/client/`:

- leave the old digest in `digests.txt` and add the new one below it, or
- keep the old archive in the folder under another name (`xmandb-old.bus`) next to the new one.

```
C805A1A446B505CE5CD8F1B23CB54D5E068C88051969920082845754D5086E79   # client rev.16319
1A2B3C4D00000000000000000000000000000000000000000000000000000000   # rev.16319 + balance patch 1
```

When the `MODIFIED` lines for the old version have stopped, remove the old line or file and restart.

## Testing a change before players get it

You want to try an edited file against a running server without changing what players are held to.

1. Add the digest of your edited file to `data/client/digests.txt`, with a comment saying what it is.
2. Restart the server.
3. Play with the edited client. Players on the released version are unaffected.
4. When the change ships, it is already allowed. If it is dropped, take the line out again.

Do not commit a test digest: everybody who pulls it would allow your test version.

## Checklist when a player is flagged after an update

1. Did the server restart after `data/client/` changed? The start-up log lists every allowed version; compare
   with `python tools/client_digest.py <the file you meant to allow>`.
2. Does the player have the new file? Have them send you their `xmandb.bus` and run `client_digest.py` on it.
3. Is their version meant to be allowed? Then its digest belongs in `digests.txt`.
4. Does their login say `no client check module`? Then the file is fine and `LightFX.dll` is missing from
   their `bin` folder. See [Client check](client-check.md#troubleshooting).

## Keeping the server's data in step

The server does not read `xmandb.bus` for prices or rewards. It uses its own extracted tables in `data/game/`.
If your client update changes the shop catalog, rewards, stages or maps, the server's copies must be updated as
well, or players see one number and get another. The client check does not look at this.
