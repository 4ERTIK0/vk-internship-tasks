"""Автономные тесты: реальные HTTP-запросы заменяются контролируемыми ответами."""
import io
import json
from decimal import Decimal
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from urllib.error import HTTPError, URLError

import weather as w


def payload(temp="18", country="Japan", city="Tokyo"):
    return {"current_condition": [{"temp_C": temp}], "nearest_area": [{
        "country": [{"value": country}], "areaName": [{"value": city}]
    }]}


class WeatherTests(unittest.TestCase):
    def test_unicode_bom_dedup_order(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "cities.txt"
            path.write_text("\ufeffTokyo\n tokyo \nNew   York\nNEW YORK\n\nМосква\n", encoding="utf-8")
            self.assertEqual(w.read_cities(path), ["Tokyo", "New York", "Москва"])

    def test_empty_and_html_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "cities.txt"
            for text in (" \n\t", "<!doctype html><html>error</html>"):
                path.write_text(text, encoding="utf-8")
                with self.subTest(text=text), self.assertRaises(w.WeatherError):
                    w.read_cities(path)

    def test_parse_current_temperature(self):
        data = payload("-5.5", "Russia", "Moscow")
        data["weather"] = [{"avgtempC": "30"}]
        row = w.parse_weather("Moscow", data, "2026-01-01T00:00:00Z")
        self.assertEqual(row.temperature_c, Decimal("-5.5"))
        self.assertEqual(row.country, "Russia")

    def test_invalid_payloads(self):
        cases = [None, [], {}, {"data": {"error": [{"msg": "unknown city"}]}},
                 payload("NaN"), payload("Infinity"), payload("hot"), payload(None),
                 payload(True), payload("18", ""), payload("18", "Japan", "")]
        for data in cases:
            with self.subTest(data=data), self.assertRaises(w.WeatherError):
                w.parse_weather("Tokyo", data, "now")

    def test_country_statistics_include_negative_and_zero(self):
        rows = [w.parse_weather(str(i), payload(t, c), "now") for i, (t, c) in enumerate([
            ("-3", "A"), ("0", "A"), ("6", "A"), ("20", "B")])]
        stats = w.group_by_country(rows)
        self.assertEqual(stats[0], w.CountryStats("A", 3, Decimal(1), Decimal(-3), Decimal(6)))
        self.assertEqual(stats[1].count, 1)
        self.assertEqual(w.group_by_country([]), [])

    def test_temperature_format(self):
        for value, expected in [("0", "0"), ("-0.001", "0"), ("18", "+18"),
                                ("20", "+20"), ("-10.5", "-10.5"), ("1.33333", "+1.33")]:
            with self.subTest(value=value):
                self.assertEqual(w.signed(Decimal(value)), expected)

    def test_retry_429_and_503(self):
        for status in (429, 503):
            with self.subTest(status=status):
                responses = [HTTPError("u", status, "error", {"Retry-After": "2"}, None), io.BytesIO(b"{}")] 
                def opener(*args, **kwargs):
                    response = responses.pop(0)
                    if isinstance(response, Exception):
                        raise response
                    return response
                sleeps = []
                self.assertEqual(w.get_bytes("https://example.test", 1, 2, opener, sleeps.append), b"{}")
                self.assertEqual(sleeps, [2])

    def test_no_retry_404(self):
        with patch("weather.time.sleep") as sleeper:
            def opener(*args, **kwargs):
                raise HTTPError("u", 404, "missing", {}, None)
            with self.assertRaises(w.WeatherError):
                w.get_bytes("https://example.test", 1, 3, opener, sleeper)
            sleeper.assert_not_called()

    def test_network_retries_exhausted(self):
        attempts = []
        def opener(*args, **kwargs):
            attempts.append(1)
            raise URLError("offline")
        with self.assertRaises(w.WeatherError):
            w.get_bytes("https://example.test", 1, 3, opener, lambda _: None)
        self.assertEqual(len(attempts), 3)

    def test_url_encoding_and_raw_archive(self):
        with tempfile.TemporaryDirectory() as directory, patch("weather.get_bytes") as get:
            raw = json.dumps(payload()).encode()
            get.return_value = raw
            path = Path(directory) / "raw.json"
            w.fetch_weather("New York/Москва?#", 10, 2, path)
            self.assertIn("New%20York%2F%D0%9C", get.call_args.args[0])
            self.assertTrue(get.call_args.args[0].endswith("%3F%23?format=j1"))
            self.assertEqual(path.read_bytes(), raw)

    def test_invalid_json_preserved(self):
        with tempfile.TemporaryDirectory() as directory, patch("weather.get_bytes", return_value=b"<html>error"):
            path = Path(directory) / "raw.json"
            with self.assertRaises(w.WeatherError):
                w.fetch_weather("Tokyo", 1, 1, path)
            self.assertEqual(path.read_bytes(), b"<html>error")

    def test_partial_run_has_exit_one_and_computed_stats(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "cities.txt").write_text("Tokyo\nTOKYO\nMissing\n", encoding="utf-8")
            def fetch(city, *args):
                if city == "Missing":
                    raise w.WeatherError("offline")
                return w.parse_weather(city, payload(), "now")
            with patch("weather.fetch_weather", side_effect=fetch) as mocked, patch("weather.time.sleep"), \
                    patch("sys.stdout", new=io.StringIO()), patch("sys.stderr", new=io.StringIO()):
                code = w.main(["--cities", str(root / "cities.txt"), "--output", str(root / "out")])
            result = json.loads(next((root / "out").glob("*/report.json")).read_text(encoding="utf-8"))
            self.assertEqual(code, 1)
            self.assertEqual(mocked.call_count, 2)
            self.assertFalse(result["complete"])
            self.assertEqual(result["countries"][0]["average_c"], "18")
            self.assertEqual(result["countries"][0]["count"], 1)

    def test_all_fail_is_explicit(self):
        report = w.report_text([], [], [{"city": "X", "error": "offline"}], 1)
        self.assertIn("0/1", report)
        self.assertIn("НЕПОЛНЫЙ", report)
        self.assertNotIn("avg:", report)

    def test_retry_after_bound(self):
        self.assertEqual(w.retry_delay("99999", 0), 30)
        self.assertEqual(w.retry_delay("-2", 0), 0)
        self.assertEqual(w.retry_delay("invalid", 2), 4)


if __name__ == "__main__":
    unittest.main()
