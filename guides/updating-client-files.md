# Updating client files

[Guides](README.md) › Updating client files

With the [client check](client-check.md) on, a client whose `Data/xmandb.bus` differs from the server's copy
counts as modified. This page is how to change that file on purpose, or ship a new client, without anybody
being flagged or locked out.

## The rule

The server has no built-in "correct" file. At start it reads its own copy (`AntiCheat.ClientDataFile`, default
`../Data/xmandb.bus`) and allows that version, plus every digest listed in `AntiCheat.AllowedClientDigests`.

So a client passes when its tables are **the same as the server's copy, or on the allow-list**.

The client is not part of this repository. "The server's copy" is the `xmandb.bus` of the client folder the
emulator sits in (`Data`, next to `bin`), or whatever file `ClientDataFile` points at. A new client version is
handed to players the same way the client itself is: nothing you commit here updates their files.

## What does not need any of this

- **Changing the server address** with `tools/set_client_server.py`. The address table
  (`clientconfigdb.xml`) is left out of the check. See [Server address and launchers](server-address.md).
- **Changing other files**: the executable, other `.bus` archives, the UI files. Only `xmandb.bus` is checked.
- **Changing the server's own data** under `data/game/`. That is what the server pays rewards and prices from;
  clients never see it.

## Updating the file for everyone

1. Make your change to `xmandb.bus`.
2. Put the new file where the server reads it (`../Data/xmandb.bus` next to the emulator, unless
   `ClientDataFile` says otherwise).
3. Restart the server. The start-up log shows the new digest:
   `[ClientCheck] Game tables of ...xmandb.bus: digest 1A2B3C4D...`
4. Give players the new file.

From step 3 on, players who still have the old file are refused in `require` mode. To avoid that, use a grace
period.

## Grace period: allow the old and the new version

Before you replace the file, take the digest of the version players have now:

```bash
python tools/client_digest.py
```

Add it to the config, then do the update above:

```json
"AntiCheat": {
  "ClientCheck": "require",
  "AllowedClientDigests": [
    "C805A1A446B505CE0000000000000000000000000000000000000000000000FF"
  ]
}
```

Both versions now log in. When the `MODIFIED` lines for the old version have stopped, remove the digest and
restart.

`client_digest.py` takes a path too, for a copy somewhere else:

```bash
python tools/client_digest.py "D:\old-client\Data\xmandb.bus"
```

## Testing a change before players get it

You want to try an edited file against a running server without changing what players are held to.

1. Keep the server's `ClientDataFile` on the released version.
2. Add the digest of your edited file to `AllowedClientDigests` and restart.
3. Play with the edited client. Players on the released file are unaffected.
4. When the change ships, follow "Updating the file for everyone" and take the test digest off the list.

If your client and the server share one `Data` folder, there is a shortcut: edit the file and restart the
server. It then allows exactly your edited version, and nothing else, so only do this on a server that has no
other players.

## Checklist when a player is flagged after an update

1. Did the server restart after the file changed? Compare the digest in the start-up log with
   `python tools/client_digest.py`.
2. Does the player have the new file? Have them send you their `xmandb.bus` and run `client_digest.py` on it.
3. Is their version meant to be allowed? Then its digest belongs in `AllowedClientDigests`.
4. Does their login say `no client check module`? Then the file is fine and `LightFX.dll` is missing from
   their `bin` folder. See [Client check](client-check.md#troubleshooting).

## Keeping the server's data in step

The server does not read `xmandb.bus` for prices or rewards. It uses its own extracted tables in `data/game/`.
If your client update changes the shop catalog, rewards, stages or maps, the server's copies must be updated as
well, or players see one number and get another. The client check does not look at this.
