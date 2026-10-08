import collections
import glob
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SINCE = sys.argv[1] if len(sys.argv) > 1 else "00:00"

PATTERNS = (
    re.compile(r"\] (?P<cat>c\w+) (?:sub )?(?P<sub>0x[0-9A-Fa-f]{2}|\w+)? ?\(not implemented\):? ?(?P<body>[0-9A-Fa-f. ()\w]*)"),
    re.compile(r"No handler for (?P<cat>\w+) - body (?P<body>[0-9A-Fa-f]*)"),
    re.compile(r"CHAT RECV (?P<cat>\w+) (?P<sub>0x[0-9A-Fa-f]{2}) \(not implemented\) ?(?P<body>[0-9A-Fa-f]*)"),
    re.compile(r"(?P<cat>UDP) sub-type (?P<sub>0x[0-9A-Fa-f]{2}) not handled"),
)


def main():
    found = collections.OrderedDict()
    for path in sorted(glob.glob(os.path.join(ROOT, "logs", "*Server-*.log"))):
        server = os.path.basename(path).split("-")[0]
        with open(path, encoding="utf-8", errors="replace") as handle:
            for line in handle:
                if line[:5] < SINCE:
                    continue
                for pattern in PATTERNS:
                    match = pattern.search(line)
                    if not match:
                        continue
                    groups = match.groupdict()
                    category = groups.get("cat") or "?"
                    sub = groups.get("sub") or ""
                    body = (groups.get("body") or "").strip()
                    if not sub and len(body) >= 2 and category.startswith("c"):
                        sub, body = "0x" + body[:2].upper(), body[2:]
                    key = (server, category, sub)
                    count, example, first = found.get(key, (0, body, line[:8]))
                    found[key] = (count + 1, example, first)
                    break

    if not found:
        print("Nothing unhandled since %s." % SINCE)
        return 0

    print("%-11s %-18s %-6s %5s  %-8s  %s" % ("server", "category", "sub", "count", "first", "example body"))
    for (server, category, sub), (count, example, first) in found.items():
        print("%-11s %-18s %-6s %5d  %-8s  %s" % (server, category, sub, count, first, example[:70]))
    return 0


if __name__ == "__main__":
    sys.exit(main())
