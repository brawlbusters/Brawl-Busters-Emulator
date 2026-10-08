import json
import os
import re
import sys

from bus_tool import read_archive, xor

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ARCHIVE = os.path.join(ROOT, "..", "Data", "xmandb.bus")
OUT_DIR = os.path.join(ROOT, "data", "game")


def load_tables():
    data, _, entries = read_archive(ARCHIVE)
    tables = {}
    for name, offset, size, encrypted in entries:
        payload = data[offset:offset + size]
        tables[os.path.basename(name)] = (xor(payload) if encrypted else payload).decode("utf-8", "replace")
    return tables


def records(xml):
    for block in re.findall(r"<DATA>(.*?)</DATA>", xml, re.S):
        fields = {}
        for key, value in re.findall(r"<([A-Z_0-9]+)>\s*(?:<!\[CDATA\[)?(.*?)(?:\]\]>)?\s*</\1>", block, re.S):
            fields[key] = value.strip()
        yield fields


def int_list(text):
    return [int(part) for part in text.split(";") if part.strip().lstrip("-").isdigit()]


def tables_of(xml):
    result = {}
    for match in re.finditer(r"<([A-Z_0-9]+)>\s*(?=<DATA>)", xml):
        name = match.group(1)
        end = xml.find("</%s>" % name, match.end())
        result[name] = list(records(xml[match.end():end]))
    return result


CAPSULE_PARTS = ["WEAPON", "HELMET", "UPPER", "LOWER", "HAND", "FOOT"]


def main():
    tables = load_tables()

    catalog = {}
    for row in records(tables["catalogdb.xml"]):
        if "ID" not in row or "PRICE_GOLD" not in row:
            continue
        catalog[row["ID"]] = {
            "gold": int_list(row.get("PRICE_GOLD", "")),
            "cash": int_list(row.get("PRICE_CASH", "")),
            "count": int_list(row.get("LIMIT_COUNT", "")),
            "expire": int_list(row.get("LIMIT_EXPIRE", "")),
            "limit": int(row.get("LIMIT_TYPE") or 0),
            "extend": int_list(row.get("LIMIT_EXPIRE_EXTEND", "")),
            "extend_gold": int_list(row.get("PRICE_GOLD_EXTEND", "")),
            "extend_cash": int_list(row.get("PRICE_CASH_EXTEND", "")),
            "options": [int_list(row.get("OPTION_%d" % n, "")) for n in (1, 2, 3, 4)],
        }

    os.makedirs(OUT_DIR, exist_ok=True)
    item_tables = tables_of(tables["itemdb.xml"])
    items = {}
    for name, rows in item_tables.items():
        if name == "FIXED":
            continue
        for row in rows:
            if row.get("ID", "").isdigit() and row.get("TYPE_ITEM", "").isdigit():
                items[row["ID"]] = [int(row["TYPE_ITEM"]), int(row.get("ID_CLASS") or 0), int(row.get("STACK") or 0),
                                    int(row.get("ID_CONVERT_R") or 0), int(row.get("ID_CONVERT_LR") or 0)]

    packages = {
        "packages": {
            row["ID"]: {
                "fixed": int_list(row.get("INDEX_FIXED_ID", "")),
                "prob": int_list(row.get("INDEX_FIXED_PROB", "")),
                "jackpot": int_list(row.get("INDEX_FIXED_JACKPOT", "")),
                "key": int(row.get("ID_KEY") or 0),
            } for row in item_tables.get("PACKAGE", []) if row.get("ID", "").isdigit()
        },
        "fixed": {
            row["ID"]: {
                "item": int(row["ID_ITEM"]),
                "type": int(row["TYPE_ITEM"]),
                "options": [int(row.get("ITEM_OPTION_%d" % n) or 0) for n in (1, 2, 3, 4)],
                "count": int(row.get("ITEM_LIMIT_COUNT") or 1),
                "expire": int(row.get("ITEM_LIMIT_EXPIRE") or -1),
                "state": int(row.get("ITEM_STATE") or 1),
            } for row in item_tables.get("FIXED", []) if row.get("ID", "").isdigit()
        },
    }

    parts = ["WEAPON", "HELMET", "UPPER", "HAND", "LOWER", "FOOT", "SUIT"]
    misc = {}
    for row in item_tables.get("MISC", []):
        if not row.get("ID", "").isdigit():
            continue
        entry = {"type": int(row["TYPE_ITEM"]), "gold": int(row.get("GIVE_GOLD") or 0), "parts": {}}
        for part in parts:
            addon = int_list(row.get("INDEX_%s_ADDON" % part, ""))
            if addon and addon != [0]:
                entry["parts"][part] = {
                    "addon": addon,
                    "success": int_list(row.get("INDEX_%s_PROB_SUCCESS" % part, "")),
                    "maintain": int_list(row.get("INDEX_%s_PROB_MAINTAIN" % part, "")),
                    "decrease": int_list(row.get("INDEX_%s_PROB_DECREASE" % part, "")),
                    "destroy": int_list(row.get("INDEX_%s_PROB_DESTROY" % part, "")),
                }
        misc[row["ID"]] = entry
    resale = {}
    for row in tables_of(tables["catalogdb.xml"]).get("RESALE", []):
        if row.get("ID", "").isdigit():
            resale[row["ID"]] = int(row.get("PRICE") or 0)
    with open(os.path.join(OUT_DIR, "upgrades.json"), "w", encoding="utf-8") as handle:
        json.dump({"misc": misc, "resale": resale}, handle, separators=(",", ":"))
    print("misc items: %d, resale prices: %d" % (len(misc), len(resale)))

    def float_list(text):
        values = []
        for part in text.split(";"):
            try:
                values.append(float(part))
            except ValueError:
                pass
        return values

    result_tables = tables_of(tables["resultdb.xml"])
    payouts = {}
    for name in ("EXP", "GOLD"):
        for row in result_tables.get(name, []):
            if row.get("ID", "").isdigit():
                payouts.setdefault(row["ID"], {})[name.lower()] = {
                    "outcome": int_list(row.get("OUTCOME", "")),
                    "member": float_list(row.get("MEMBER", "")),
                    "time_min": int(row.get("TIME_MIN") or 0),
                }
    bonus = {}
    for row in result_tables.get("BONUS", []):
        if row.get("ID") == "1" and row.get("ID_LEVEL", "").isdigit():
            bonus[row["ID_LEVEL"]] = {
                "rewards": int_list(row.get("INDEX_REWARD_ID", "")),
                "prob": int_list(row.get("INDEX_REWARD_PROB", "")),
                "time_min": int(row.get("TIME_MIN") or 0),
            }
    rewards = {}
    for row in tables_of(tables["rewarddb.xml"]).get("REWARD", []):
        if row.get("ID", "").isdigit():
            rewards[row["ID"]] = [int(row.get("TYPE") or 0), int(row.get("VALUE") or 0)]
    with open(os.path.join(OUT_DIR, "results.json"), "w", encoding="utf-8") as handle:
        json.dump({"payouts": payouts, "bonus": bonus, "rewards": rewards}, handle, separators=(",", ":"))
    print("payout rows: %d, bonus maps: %d, rewards: %d" % (len(payouts), len(bonus), len(rewards)))

    capsule_tables = tables_of(tables["capsuledb.xml"])
    capsules = {
        "machines": {
            row["ID"]: [{
                "index": int_list(row.get(part + "_INDEX_ID", "")),
                "gold": int(row.get(part + "_PRICE_GOLD") or 0),
                "cash": int(row.get(part + "_PRICE_CASH") or 0),
            } for part in CAPSULE_PARTS] for row in capsule_tables.get("MACHINE", [])
        },
        "items": {
            row["ID"]: {
                "items": int_list(row.get("ID_ITEM", "")),
                "prob": int_list(row.get("PROB_ITEM", "")),
                "opt_gold": int_list(row.get("ID_OPT_GOLD", "")),
                "opt_cash": int_list(row.get("ID_OPT_CASH", "")),
            } for row in capsule_tables.get("ITEM", [])
        },
        "options": {
            row["ID"]: {
                "values": [int_list(row.get("INDEX_OPT%d" % n, "")) for n in (1, 2, 3, 4)],
                "prob": [int_list(row.get("INDEX_OPT%d_PROB" % n, "")) for n in (1, 2, 3, 4)],
                "count": int_list(row.get("INDEX_COUNT", "")),
                "count_prob": int_list(row.get("INDEX_COUNT_PROB", "")),
                "expire": int_list(row.get("INDEX_EXPIRE", "")),
                "expire_prob": int_list(row.get("INDEX_EXPIRE_PROB", "")),
                "state": int(row.get("STATE_ITEM") or 1),
            } for row in capsule_tables.get("OPT", [])
        },
    }

    levels = {}
    for row in records(tables["globaldb.xml"]):
        if row.get("ID", "").isdigit() and row.get("EXP", "").isdigit() and "NOTIFY" in row:
            levels[int(row["ID"])] = int(row["EXP"])
    level_exp = [levels[level] for level in sorted(levels)]

    maps, rules = [], []
    for row in records(tables["leveldb.xml"]):
        if not row.get("ID", "").isdigit():
            continue
        if "TYPE_MODE" in row and "LEVEL_NAME" in row:
            maps.append({
                "id": int(row["ID"]),
                "name": row["LEVEL_NAME"],
                "mode": int(row["TYPE_MODE"]),
                "default": row.get("IS_DEFAULT") == "1",
                "released": row.get("IS_RELEASE") == "1",
                "min": int(row.get("USER_MIN") or 1),
                "max": int(row.get("USER_MAX") or 1),
                "random": int_list(row.get("RANDOM_LIST", "")),
                "channels": int_list(row.get("AVAILABLE_CHANNEL", "")),
                "rules": int_list(row.get("ID_RULE_PARAM_LIST", "")),
            })
        elif "TIME_PLAYGAME" in row and "ROUND" in row:
            rules.append({
                "id": int(row["ID"]),
                "default": row.get("IS_DEFAULT") == "1",
                "time": int(row.get("TIME_PLAYGAME") or 0),
                "rounds": int(row.get("ROUND") or 1),
            })

    stages = []
    for row in records(tables["singleplaydb.xml"]):
        if not row.get("ID_SERVER", "").isdigit():
            continue
        stages.append({
            "id": int(row["ID_SERVER"]),
            "requires": int(row.get("CONDITION_PRE") or 0),
            "map": int(row.get("ID_LEVEL") or 0),
            "first_gold": int(row.get("REWARD_01_GOLD") or 0),
            "first_exp": int(row.get("REWARD_01_EXP") or 0),
            "first_item": int(row.get("REWARD_01_ITEM") or 0),
            "repeat_gold": int(row.get("REWARD_02_GOLD") or 0),
            "repeat_exp": int(row.get("REWARD_02_EXP") or 0),
        })

    os.makedirs(OUT_DIR, exist_ok=True)
    with open(os.path.join(OUT_DIR, "singleplay.json"), "w", encoding="utf-8") as handle:
        json.dump(stages, handle, separators=(",", ":"))
    print("single-play stages: %d" % len(stages))
    with open(os.path.join(OUT_DIR, "maps.json"), "w", encoding="utf-8") as handle:
        json.dump({"maps": maps, "rules": rules}, handle, separators=(",", ":"))
    with open(os.path.join(OUT_DIR, "levels.json"), "w", encoding="utf-8") as handle:
        json.dump(level_exp, handle, separators=(",", ":"))
    with open(os.path.join(OUT_DIR, "catalog.json"), "w", encoding="utf-8") as handle:
        json.dump(catalog, handle, separators=(",", ":"))
    with open(os.path.join(OUT_DIR, "items.json"), "w", encoding="utf-8") as handle:
        json.dump(items, handle, separators=(",", ":"))
    with open(os.path.join(OUT_DIR, "packages.json"), "w", encoding="utf-8") as handle:
        json.dump(packages, handle, separators=(",", ":"))
    with open(os.path.join(OUT_DIR, "capsules.json"), "w", encoding="utf-8") as handle:
        json.dump(capsules, handle, separators=(",", ":"))
    print("packages: %d, package contents: %d, capsule machines: %d"
          % (len(packages["packages"]), len(packages["fixed"]), len(capsules["machines"])))

    print("catalog entries: %d, items: %d, levels: %d, maps: %d, rules: %d -> %s"
          % (len(catalog), len(items), len(level_exp), len(maps), len(rules), OUT_DIR))
    return 0


if __name__ == "__main__":
    sys.exit(main())
