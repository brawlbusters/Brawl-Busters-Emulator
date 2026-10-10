"""Prints the digest of a client's game tables - the value the client check compares (see guides/client-check.md).

    python tools/client_digest.py                        the client next to the emulator (../Data/xmandb.bus)
    python tools/client_digest.py path/to/xmandb.bus     another copy, e.g. an older client version

Put a digest into AntiCheat.AllowedClientDigests of config/emulator.json to let that version log in as well.
"""
import sys

from test_client_check import ARCHIVE, digest


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else ARCHIVE
    with open(path, "rb") as handle:
        print(digest(handle.read()).hex().upper())
    return 0


if __name__ == "__main__":
    sys.exit(main())
