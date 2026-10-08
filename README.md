# Brawl Busters Emulator

A server emulator for Brawl Busters, written in C# (.NET 9).

Brawl Busters was shut down years ago and the official servers are gone. This project
rebuilds the server side so the original client can log in and play again. It is made up
of three servers:

- **AuthServer** - login and account creation
- **MainServer** - lobby, channels, rooms, inventory, shop, chat and buddies
- **CastServer** - matches

The game client is not included in this repository.

Game client download:
http://www.mediafire.com/file/7c27acxte8gze7h/Brawl_busters_client.zip

## Running it

1. Install the [.NET 9 SDK](https://dotnet.microsoft.com/download).
2. Run `start-servers.bat`. It builds the solution and starts the three servers.
3. Point your client at `127.0.0.1` and log in. An account is created the first time you
   log in with a new ID.

Ports and channels are set in `config/emulator.json`.

## Roadmap

The emulator is still early and the code will be refactored. That is the plan, so expect
things to move around.

Accounts and game data are stored in JSON files right now. That keeps testing and
development simple, but it is temporary. The plan is to move to a real database, either
SQLite or MariaDB.

## Learning purposes only

This project exists for education and preservation. It is a way to learn how an online
game's networking and server logic work, and to keep a dead game playable.

- It is not affiliated with, endorsed by, or connected to the original developers or
  publishers of Brawl Busters. All trademarks and game content belong to their owners.
- It is non-commercial. Do not use it to make money, and do not sell access, items or
  accounts on a server running it.
- It is provided as is, with no warranty. You are responsible for how you use it.

If you are a rights holder and have a concern about this repository, open an issue and we
will respond.

## Contributing

The project is open source and anyone can contribute. A lot of the game is still
unfinished, so help is welcome.

To contribute, fork the repository, make your changes on a branch, and open a pull
request. Keep each PR focused on one thing and describe what you changed and how you
tested it.

The scripts in `tools/` are useful when working on the protocol: `capture_proxy.py` records
a session, and the `test_*.py` scripts check the server's replies.

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
