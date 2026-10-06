"""Независимая проверка сохранённого живого запуска; никаких запросов в сеть."""
from decimal import Decimal
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent
RUN = ROOT / "results" / "20261006T125029_708684Z"
report = json.loads((RUN / "report.json").read_text(encoding="utf-8"))
cities = [line.strip() for line in (ROOT / "cities.txt").read_text(encoding="utf-8-sig").splitlines() if line.strip()]
assert report["input_sha256"] == hashlib.sha256((ROOT / "cities.txt").read_bytes()).hexdigest()
assert report["complete"] is True and not report["errors"]
assert report["unique_cities"] == len(set(city.casefold() for city in cities)) == 8
assert len(report["weather"]) == 8
assert [r["city"] for r in report["weather"]] == cities
groups = {}
for index, row in enumerate(report["weather"], 1):
    raw = json.loads((RUN / "raw" / f"{index:03d}.json").read_text(encoding="utf-8"))
    value = Decimal(raw["current_condition"][0]["temp_C"])
    country = raw["nearest_area"][0]["country"][0]["value"]
    assert Decimal(row["temperature_c"]) == value
    assert row["country"] == country
    assert row["resolved_city"] == raw["nearest_area"][0]["areaName"][0]["value"]
    groups.setdefault(country, []).append(value)
assert len(report["countries"]) == len(groups)
for item in report["countries"]:
    values = groups.pop(item["country"])
    assert item["count"] == len(values)
    assert Decimal(item["average_c"]) == sum(values) / len(values)
    assert Decimal(item["minimum_c"]) == min(values)
    assert Decimal(item["maximum_c"]) == max(values)
assert not groups
print("PASS: all 8 original API responses agree with the report; all 3 country summaries recomputed independently.")
