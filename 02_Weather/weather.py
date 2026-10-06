#!/usr/bin/env python3
"""Задание VK: текущая погода и статистика по странам. Только stdlib."""
from __future__ import annotations

import argparse
from collections import defaultdict
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from decimal import Decimal, InvalidOperation
from email.utils import parsedate_to_datetime
import hashlib
import json
from pathlib import Path
import socket
import sys
import time
import unicodedata
from urllib.error import HTTPError, URLError
from urllib.parse import quote
from urllib.request import Request, urlopen

SOURCE_URL = "https://gistpad.com/raw/vk-task-14"
ROOT = Path(__file__).resolve().parent
MAX_RESPONSE_BYTES = 5_000_000


@dataclass(frozen=True)
class WeatherData:
    city: str                 # Город из исходного файла
    temperature_c: Decimal    # Текущая температура, НЕ дневной прогноз
    country: str              # Страна из ответа API
    resolved_city: str        # Населённый пункт, выбранный геокодером wttr.in
    fetched_at_utc: str


@dataclass(frozen=True)
class CountryStats:
    country: str
    count: int
    average_c: Decimal
    minimum_c: Decimal
    maximum_c: Decimal


class WeatherError(Exception):
    """Ожидаемая ошибка сети или входных данных."""


def read_cities(path: Path) -> list[str]:
    """Сохраняет порядок первого появления; регистр и лишние пробелы несущественны."""
    text = path.read_text(encoding="utf-8-sig")
    if "<html" in text.lower() or "<!doctype" in text.lower():
        raise WeatherError("Вместо списка городов получена HTML-страница.")
    seen: set[str] = set()
    cities: list[str] = []
    for line in text.splitlines():
        city = " ".join(unicodedata.normalize("NFC", line).split())
        if city and city.casefold() not in seen:
            seen.add(city.casefold())
            cities.append(city)
    if not cities:
        raise WeatherError("Файл не содержит городов.")
    return cities


def first_object(value: object, field: str) -> dict:
    if not isinstance(value, list) or not value or not isinstance(value[0], dict):
        raise WeatherError(f"В ответе API отсутствует {field}[0].")
    return value[0]


def parse_weather(city: str, payload: object, fetched_at: str) -> WeatherData:
    if not isinstance(payload, dict):
        raise WeatherError("Корень ответа API должен быть JSON-объектом.")
    if "error" in payload or (
        isinstance(payload.get("data"), dict) and "error" in payload["data"]
    ):
        raise WeatherError("API вернул ошибку: " + json.dumps(payload, ensure_ascii=False)[:300])
    current = first_object(payload.get("current_condition"), "current_condition")
    area = first_object(payload.get("nearest_area"), "nearest_area")
    country = first_object(area.get("country"), "nearest_area.country").get("value")
    resolved = first_object(area.get("areaName"), "nearest_area.areaName").get("value")
    if not isinstance(country, str) or not country.strip():
        raise WeatherError("API не вернул название страны.")
    if not isinstance(resolved, str) or not resolved.strip():
        raise WeatherError("API не вернул название найденного города.")
    raw_temp = current.get("temp_C")
    if isinstance(raw_temp, bool) or not isinstance(raw_temp, (str, int, float, Decimal)):
        raise WeatherError("API не вернул числовое значение temp_C.")
    try:
        temp = Decimal(str(raw_temp))
    except InvalidOperation as exc:
        raise WeatherError("Некорректная температура temp_C.") from exc
    if not temp.is_finite():
        raise WeatherError("Температура должна быть конечным числом.")
    return WeatherData(city, temp, country.strip(), resolved.strip(), fetched_at)


def group_by_country(records: list[WeatherData]) -> list[CountryStats]:
    groups: dict[str, list[Decimal]] = defaultdict(list)
    for row in records:
        groups[row.country].append(row.temperature_c)
    return [
        CountryStats(country, len(values), sum(values) / len(values), min(values), max(values))
        for country, values in sorted(groups.items(), key=lambda pair: pair[0].casefold())
    ]


def retry_delay(header: str | None, attempt: int) -> float:
    if header:
        try:
            return min(30.0, max(0.0, float(header)))
        except ValueError:
            try:
                return min(30.0, max(0.0, (
                    parsedate_to_datetime(header) - datetime.now(timezone.utc)
                ).total_seconds()))
            except (ValueError, TypeError, OverflowError):
                pass
    return min(2.0 ** attempt, 30.0)


def get_bytes(url: str, timeout: float, attempts: int,
              opener=urlopen, sleeper=time.sleep) -> bytes:
    request = Request(url, headers={"User-Agent": "VK-Weather-Task/1.0", "Accept": "application/json, text/plain"})
    for attempt in range(attempts):
        try:
            with opener(request, timeout=timeout) as response:
                raw = response.read(MAX_RESPONSE_BYTES + 1)
            if len(raw) > MAX_RESPONSE_BYTES:
                raise WeatherError("Ответ сервера превышает 5 МБ.")
            return raw
        except HTTPError as exc:
            retryable = exc.code == 429 or 500 <= exc.code <= 599
            delay = retry_delay(exc.headers.get("Retry-After") if exc.headers else None, attempt)
            exc.close()
            if not retryable or attempt == attempts - 1:
                raise WeatherError(f"HTTP {exc.code}: {url}") from exc
        except (URLError, TimeoutError, socket.timeout, ConnectionError) as exc:
            delay = retry_delay(None, attempt)
            if attempt == attempts - 1:
                raise WeatherError(f"Сеть недоступна или истёк тайм-аут: {exc}") from exc
        sleeper(delay)
    raise WeatherError("Исчерпаны попытки запроса.")


def fetch_weather(city: str, timeout: float, attempts: int, raw_path: Path) -> WeatherData:
    # safe='' кодирует также '/', '?' и '#': название не может изменить маршрут или query.
    url = "https://wttr.in/" + quote(city, safe="") + "?format=j1"
    raw = get_bytes(url, timeout, attempts)
    raw_path.write_bytes(raw)  # Сохраняется именно полученный ответ, включая ошибочный.
    try:
        payload = json.loads(raw.decode("utf-8-sig"), parse_float=Decimal)
    except (UnicodeError, json.JSONDecodeError) as exc:
        raise WeatherError("Сервер вернул не UTF-8 JSON; исходный ответ сохранён.") from exc
    return parse_weather(city, payload, datetime.now(timezone.utc).isoformat())


def signed(value: Decimal) -> str:
    # Округляем только вывод; расчёт среднего использует исходные числа.
    rounded = value.quantize(Decimal("0.01"))
    if rounded == 0:
        return "0"
    number = format(rounded, "f").rstrip("0").rstrip(".")
    return ("+" if rounded > 0 else "") + number


def report_text(records: list[WeatherData], stats: list[CountryStats],
                errors: list[dict], total: int) -> str:
    lines = [f"Успешно: {len(records)}/{total} уникальных городов", ""]
    lines += [f"{row.city}, {row.country} {signed(row.temperature_c)} °C" for row in records]
    lines += ["", "Статистика по странам (только успешные ответы):"]
    lines += [
        f"{s.country} - {s.count} cities, avg: {signed(s.average_c)} °C, "
        f"min: {signed(s.minimum_c)} °C, max: {signed(s.maximum_c)} °C" for s in stats
    ]
    if errors:
        lines += ["", "НЕПОЛНЫЙ РЕЗУЛЬТАТ. Ошибки:"]
        lines += [f"{e['city']}: {e['error']}" for e in errors]
    return "\n".join(lines) + "\n"


def positive(value: str) -> float:
    try:
        result = float(value)
    except ValueError as exc:
        raise argparse.ArgumentTypeError("Ожидается положительное число") from exc
    if not 0 < result < float("inf"):
        raise argparse.ArgumentTypeError("Ожидается конечное положительное число")
    return result


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cities", type=Path, default=ROOT / "cities.txt", help="UTF-8, один город на строку")
    parser.add_argument("--output", type=Path, default=ROOT / "results")
    parser.add_argument("--timeout", type=positive, default=20.0, help="Тайм-аут одного запроса, секунд")
    parser.add_argument("--attempts", type=int, choices=range(1, 6), default=3)
    parser.add_argument("--delay", type=positive, default=1.0, help="Пауза между городами, секунд")
    parser.add_argument("--download", action="store_true", help="Обновить файл из второго источника задания")
    args = parser.parse_args(argv)
    try:
        if args.download:
            downloaded = get_bytes(SOURCE_URL, args.timeout, args.attempts)
            temporary = args.cities.with_suffix(args.cities.suffix + ".download")
            args.cities.parent.mkdir(parents=True, exist_ok=True)
            try:
                temporary.write_bytes(downloaded)
                read_cities(temporary)  # Не заменяем файл страницей ошибки.
                temporary.replace(args.cities)
            finally:
                temporary.unlink(missing_ok=True)
        cities = read_cities(args.cities)
        run_dir = args.output / datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S_%fZ")
        raw_dir = run_dir / "raw"
        raw_dir.mkdir(parents=True)
        records: list[WeatherData] = []
        errors: list[dict] = []
        for index, city in enumerate(cities, start=1):
            print(f"[{index}/{len(cities)}] {city}", file=sys.stderr, flush=True)
            try:
                records.append(fetch_weather(city, args.timeout, args.attempts, raw_dir / f"{index:03d}.json"))
            except WeatherError as exc:
                errors.append({"city": city, "error": str(exc)})
                print(f"  Ошибка: {exc}", file=sys.stderr, flush=True)
            if index < len(cities):
                time.sleep(args.delay)
        stats = group_by_country(records)
        report = report_text(records, stats, errors, len(cities))
        (run_dir / "report.txt").write_text(report, encoding="utf-8-sig")
        result = {
            "complete": not errors, "unique_cities": len(cities),
            "input_sha256": hashlib.sha256(args.cities.read_bytes()).hexdigest(),
            "weather": [asdict(r) for r in records],
            "countries": [asdict(s) for s in stats], "errors": errors,
        }
        # Decimal сохраняется строкой: точность исходных данных не теряется.
        (run_dir / "report.json").write_text(
            json.dumps(result, ensure_ascii=False, indent=2, default=str) + "\n", encoding="utf-8"
        )
        print(report, end="")
        print(f"\nОтчёт: {run_dir}")
        return 0 if not errors else 1
    except (OSError, UnicodeError, WeatherError) as exc:
        print(f"Ошибка: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8")
    try:
        raise SystemExit(main())
    except KeyboardInterrupt:
        print("\nПрервано пользователем.", file=sys.stderr)
        raise SystemExit(130)
