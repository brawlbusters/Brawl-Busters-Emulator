#!/usr/bin/env bash
# Builds and starts the server on Linux. Uses config/emulator.json (set BRAWLBUSTERS_CONFIG for another file).
# The database (MariaDB) must already be running; the tables are created on the first start.
set -euo pipefail
cd "$(dirname "$0")"

dotnet build BrawlBusters.sln -c Release -nologo -v q
exec dotnet src/BrawlBusters.Server/bin/Release/net9.0/BrawlBusters.Server.dll
