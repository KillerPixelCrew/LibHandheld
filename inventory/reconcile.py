"""Build a source-preserving HC/HHD target view and related tertiary references."""

import csv
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent
PRIORITY = ("hc", "hhd", "opengamepadui")
FEATURES = {
    "tdp-power": "power", "tdpPower": "power", "oem-power": "oem-power",
    "fan": "fan", "fans": "fan", "charge-limit": "charge-limit",
    "chargeLimit": "charge-limit", "charge-bypass": "charge-bypass",
    "chargeBypass": "charge-bypass", "rgb": "rgb", "controller": "controller",
    "source-gamepad": "controller", "oem-buttons": "oem-buttons",
    "oemButtons": "oem-buttons", "imu": "imu", "gyroAccelerometer": "imu",
    "source-imu": "imu", "motion": "imu", "haptics": "haptics",
    "hapticsRumble": "haptics", "haptics-rumble": "haptics", "touchpad": "touchpad",
}
COLUMNS = tuple(dict.fromkeys(FEATURES.values()))


def normalize(value):
    return re.sub(r"[^a-z0-9]", "", value.casefold())


def aliases(device):
    values = [device.get("name", ""), device.get("sourceId", "")]
    for identity in device.get("identities", []):
        kind = identity.get("kind", "").casefold()
        value = identity.get("value")
        if any(part in kind for part in ("product", "board", "model")) and isinstance(value, str):
            values.append(value)
    for variant in device.get("variants", []):
        if isinstance(variant, str):
            values.append(variant)
        elif isinstance(variant, dict):
            values.append(variant.get("name", ""))
            for key, items in variant.get("selectors", {}).items():
                if any(part in key.casefold() for part in ("product", "board", "model")):
                    values.extend(items if isinstance(items, list) else [items])
    return {normalize(value) for value in values if isinstance(value, str)
            and len(value) > 3 and not any(char in value for char in "*?[]")}


def feature_facts(device, feature):
    return [fact for fact in device.get("features", [])
            if FEATURES.get(fact.get("feature")) == feature and fact.get("status") != "unknown"]


def main():
    sources = {name: json.loads((ROOT / "sources" / f"{name}.json").read_text(encoding="utf-8-sig"))
               for name in PRIORITY}
    records = []
    by_id = {}
    for source, inventory in sources.items():
        for device in inventory["devices"]:
            identity = device["sourceId"]
            identity = identity if identity.startswith(source + ":") else source + ":" + identity
            record = {"id": identity, "source": source, "device": device,
                      "tokens": aliases(device)}
            if record["id"] in by_id:
                raise ValueError("Duplicate source identity: " + record["id"])
            records.append(record)
            by_id[record["id"]] = record

    targets = []
    discrepancies = []
    linked_tertiary = set()
    for record in records:
        if record["source"] == "opengamepadui":
            continue
        device = record["device"]
        related = [other for other in records if other["source"] != record["source"]
                   and record["tokens"] & other["tokens"]]
        linked_tertiary.update(other["id"] for other in related if other["source"] == "opengamepadui")
        preferences = {}
        for feature in COLUMNS:
            observations = []
            for candidate in [record, *related]:
                facts = feature_facts(candidate["device"], feature)
                if facts:
                    observations.append({"recordId": candidate["id"], "source": candidate["source"],
                                         "facts": facts})
            chosen = None
            for source in PRIORITY:
                candidates = [item for item in observations if item["source"] == source]
                if not candidates:
                    continue
                if len(candidates) == 1:
                    chosen = {"status": "source-preferred", "recordId": candidates[0]["recordId"],
                              "source": source, "facts": candidates[0]["facts"]}
                else:
                    chosen = {"status": "needs-variant-resolution", "source": source,
                              "candidateRecordIds": [item["recordId"] for item in candidates]}
                break
            preferences[feature] = chosen or {"status": "unknown"}
            statuses = {fact["status"] for item in observations for fact in item["facts"]}
            if len(statuses) > 1 or (chosen and chosen["status"] == "needs-variant-resolution"):
                discrepancies.append({"targetId": record["id"], "feature": feature,
                                      "resolution": preferences[feature], "observations": observations,
                                      "note": "Review platform, selector and variant scope before treating this as a hardware contradiction."})
        targets.append({"id": record["id"], "source": record["source"],
                        "sourceRecordId": device["sourceId"], "name": device["name"],
                        "vendor": device["vendor"], "family": device["family"],
                        "entryKind": device.get("entryKind", "genericFallback" if device["sourceId"] in ("ASUS", "DefaultDevice") else "deviceModel"),
                        "identities": device.get("identities", []), "variants": device.get("variants", []),
                        "preferredFacts": preferences,
                        "relatedRecords": [{"id": other["id"], "source": other["source"],
                                            "sharedTokens": sorted(record["tokens"] & other["tokens"])}
                                           for other in related],
                        "sourceFile": "sources/" + record["source"] + ".json",
                        "implementationStatus": "planned",
                        "associationRule": "Name/selector text overlap is a research link, not proof that selectors or variants are equivalent."})
    tertiary_only = [record["id"] for record in records
                     if record["source"] == "opengamepadui" and record["id"] not in linked_tertiary]
    result = {"schemaVersion": 1, "platform": "Windows-first .NET", "sourcePriority": list(PRIORITY),
              "countUnit": "source-specific target record, not deduplicated physical model",
              "snapshots": {name: inventory["snapshot"] for name, inventory in sources.items()},
              "sourceRecordCounts": {name: len(inventory["devices"]) for name, inventory in sources.items()},
              "targetRecordCount": len(targets), "devices": targets,
              "tertiaryOnlyRecords": tertiary_only,
              "scope": "All HC/HHD source records are retained, including fallbacks and auxiliary support. Tertiary-only records do not expand the target."}
    (ROOT / "devices.json").write_text(json.dumps(result, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    (ROOT / "differences.json").write_text(json.dumps({"sourcePriority": list(PRIORITY),
        "rule": "HC wins established conflicting facts. Unknown HC facts may use HHD, then tertiary evidence. Ambiguous variant associations stay unresolved.",
        "differences": discrepancies}, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    with (ROOT / "devices.csv").open("w", encoding="utf-8-sig", newline="") as output:
        writer = csv.DictWriter(output, fieldnames=["id", "source", "name", "vendor", "family",
                                                     "selectors", *COLUMNS, "relatedRecords", "implementationStatus"])
        writer.writeheader()
        for device in targets:
            row = {key: device[key] for key in ("id", "source", "name", "vendor", "family", "implementationStatus")}
            row["selectors"] = "; ".join(item["kind"] + "=" + str(item["value"]) for item in device["identities"])
            row["relatedRecords"] = "; ".join(item["id"] for item in device["relatedRecords"])
            for feature, selection in device["preferredFacts"].items():
                row[feature] = selection["status"] if selection["status"] != "source-preferred" else (
                    selection["source"] + ":" + "/".join(sorted({fact["status"] for fact in selection["facts"]})))
            writer.writerow(row)
    print(json.dumps({"sourceRecords": result["sourceRecordCounts"], "targetRecords": len(targets),
                      "differences": len(discrepancies), "tertiaryOnlyRecords": len(tertiary_only)}))


if __name__ == "__main__":
    main()
