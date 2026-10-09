"""What is still missing, collected from the logs of a play session.

Two sources:

  logs/missing-packets-*.log   requests the server could not answer (unknown category,
                               unknown request id, request it could not read)
  Documents/Busters/Log/*.txt  the game client's own log: messages from the server it
                               rejected ("server message process failed [Screen/ category/ id]"),
                               its error codes and its internal error lines

Play the game - any mode, any screen - then run this. Every distinct line is shown once
with how often it happened, most frequent first.

    python tools/missing_report.py            today's client logs
    python tools/missing_report.py --all      every client log
"""
import collections
import datetime
import glob
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CLIENT_LOGS = os.path.join(os.path.expanduser("~"), "Documents", "Busters", "Log")
CLIENT_PATTERNS = ("server message process failed", "Error code", "ErrorLog", "Mismatch", "hole punching failed", "Lost connection")


def server_side():
    counts = collections.Counter()
    for path in glob.glob(os.path.join(ROOT, "logs", "missing-packets-*.log")):
        server = os.path.basename(path)[len("missing-packets-"):-len(".log")]
        for line in open(path, encoding="utf-8", errors="replace"):
            text = line.split("] ", 3)[-1].strip()
            text = re.sub(r"(not implemented\)|body|:)\s*[0-9A-F]{6,}.*$", r"\1 <data>", text)
            counts[(server, text)] += 1
    return counts


def client_side(everything):
    counts = collections.Counter()
    today = datetime.date.today().strftime("%Y%m%d")
    for path in glob.glob(os.path.join(CLIENT_LOGS, "Log*.txt")):
        if not everything and today not in os.path.basename(path):
            continue
        for line in open(path, encoding="utf-8", errors="replace"):
            if any(pattern in line for pattern in CLIENT_PATTERNS):
                text = re.sub(r"^\[[^\]]*\]\s*", "", line.strip())
                text = re.sub(r"\d+\.\d+\.\d+\.\d+:\d+", "<address>", text)
                text = re.sub(r"ID: \d+", "ID: <id>", text)
                counts[text] += 1
    return counts


def main():
    everything = "--all" in sys.argv
    server = server_side()
    print("Requests the server could not answer (%d distinct):" % len(server))
    for (name, text), count in server.most_common():
        print("  %5d x  [%s] %s" % (count, name, text))
    if not server:
        print("  (none logged)")

    client = client_side(everything)
    print("\nWhat the game client complained about (%d distinct, %s):" % (len(client), "all logs" if everything else "today"))
    for text, count in client.most_common():
        print("  %5d x  %s" % (count, text))
    if not client:
        print("  (nothing - or no client log for today in %s)" % CLIENT_LOGS)
    return 0


if __name__ == "__main__":
    sys.exit(main())
