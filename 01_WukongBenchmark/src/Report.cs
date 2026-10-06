using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Management;
using System.Net;
using System.Text;

namespace VkBenchmark
{
    public static class Hardware
    {
        private static List<Dictionary<string, object>> Query(string query, params string[] fields)
        {
            var rows = new List<Dictionary<string, object>>();
            using (var searcher = new ManagementObjectSearcher(query))
            {
                searcher.Options.Timeout = TimeSpan.FromSeconds(10);
                using (var results = searcher.Get())
                    foreach (ManagementObject item in results)
                        using (item)
                        {
                            var row = new Dictionary<string, object>();
                            foreach (string field in fields) row[field] = item[field];
                            rows.Add(row);
                        }
            }
            return rows;
        }
        public static Dictionary<string, object> Collect()
        {
            // Required CPU/GPU/RAM queries fail explicitly if WMI is inaccessible.
            var system = Query("SELECT TotalPhysicalMemory, Manufacturer, Model FROM Win32_ComputerSystem", "TotalPhysicalMemory", "Manufacturer", "Model");
            if (system.Count == 0) throw new InvalidOperationException("WMI did not return system/RAM information.");
            var data = new Dictionary<string, object> {
                { "CPU", Query("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor", "Name", "NumberOfCores", "NumberOfLogicalProcessors") },
                { "GPU", Query("SELECT Name, DriverVersion, VideoProcessor FROM Win32_VideoController", "Name", "DriverVersion", "VideoProcessor") },
                { "RAM", Query("SELECT Capacity, Speed, ConfiguredClockSpeed FROM Win32_PhysicalMemory", "Capacity", "Speed", "ConfiguredClockSpeed") },
                { "System", system },
                { "RAM total GiB", Math.Round(Convert.ToDouble(system[0]["TotalPhysicalMemory"], CultureInfo.InvariantCulture) / 1073741824.0, 2) },
                { "OS", Query("SELECT Caption, Version, BuildNumber FROM Win32_OperatingSystem", "Caption", "Version", "BuildNumber") },
                { "LogicalProcessorsAvailable", Environment.ProcessorCount },
                { "Is64BitOS", Environment.Is64BitOperatingSystem }
            };
            if (((List<Dictionary<string, object>>)data["CPU"]).Count == 0 || ((List<Dictionary<string, object>>)data["GPU"]).Count == 0)
                throw new InvalidOperationException("WMI did not return CPU/GPU information.");
            return data;
        }
    }

    public static class Report
    {
        private static string Escape(object value) { return WebUtility.HtmlEncode(Convert.ToString(value, CultureInfo.InvariantCulture)); }
        public static void Save(string directory, RunReport report)
        {
            JsonFile.Write(Path.Combine(directory, "report.json"), report);
            var html = new StringBuilder("<!doctype html><html lang=\"ru\"><meta charset=\"utf-8\"><title>Wukong Benchmark</title><style>body{font:16px/1.6 system-ui;background:#111827;color:#e5e7eb;margin:40px auto;max-width:1040px;padding:0 24px}table{border-collapse:collapse;width:100%;margin:16px 0}td,th{border:1px solid #4b5563;padding:8px;text-align:left}h1,h2{color:#fbbf24}pre{white-space:pre-wrap;overflow-wrap:anywhere}img{max-width:100%}a{color:#93c5fd}</style><h1>Black Myth: Wukong Benchmark</h1>");
            html.Append("<p>Создан (UTC): ").Append(Escape(report.CreatedUtc)).Append("<br>Статус: ").Append(Escape(report.Status));
            html.Append("<br>Исходные настройки восстановлены: ").Append(report.SettingsRestored ? "да" : "нет").Append("<br>Steam build: ").Append(Escape(report.SteamBuildId)).Append("</p>");
            if (report.Error != null) html.Append("<h2>Ошибка</h2><pre>").Append(Escape(report.Error)).Append("</pre>");
            html.Append("<h2>Компьютер</h2>");
            foreach (var group in report.Hardware)
            {
                html.Append("<h3>").Append(Escape(group.Key)).Append("</h3>");
                var rows = group.Value as List<Dictionary<string, object>>;
                if (rows == null) { html.Append("<p>").Append(Escape(group.Value)).Append("</p>"); continue; }
                foreach (var row in rows)
                {
                    html.Append("<table>");
                    foreach (var field in row) html.Append("<tr><th>").Append(Escape(field.Key)).Append("</th><td>").Append(Escape(field.Value)).Append("</td></tr>");
                    html.Append("</table>");
                }
            }
            foreach (var pass in report.Passes)
            {
                html.Append("<h2>").Append(Escape(pass.Name)).Append("</h2><table><tr><th>Средний FPS</th><th>Минимальный FPS</th><th>Максимальный FPS</th></tr><tr>");
                foreach (double value in new[] { pass.Metrics.AverageFps, pass.Metrics.MinimumFps, pass.Metrics.MaximumFps }) html.Append("<td>").Append(Escape(value)).Append("</td>");
                html.Append("</tr></table><table><tr><th>Настройка</th><th>Проверенное значение</th></tr>");
                foreach (var pair in pass.Settings) html.Append("<tr><td>").Append(Escape(pair.Key)).Append("</td><td>").Append(Escape(pair.Value)).Append("</td></tr>");
                html.Append("</table><p>Начало UTC: ").Append(Escape(pass.StartedUtc)).Append("; результат UTC: ").Append(Escape(pass.FinishedUtc)).Append("</p>");
                string relative = new Uri(Path.GetFullPath(directory).TrimEnd('\\') + "\\").MakeRelativeUri(new Uri(pass.Screenshot)).ToString();
                html.Append("<img alt=\"Исходный экран результата Benchmark Tool\" src=\"").Append(Escape(relative)).Append("\"><details><summary>Распознанный текст</summary><pre>").Append(Escape(pass.OcrText)).Append("</pre></details>");
            }
            html.Append("<p>CPU-проход уменьшает графическую нагрузку, но сам по себе не доказывает упор в CPU. Это FPS сцены Benchmark Tool, а не отдельная синтетическая оценка процессора. Все FPS выше прочитаны с итогового экрана.</p></html>");
            File.WriteAllText(Path.Combine(directory, "report.html"), html.ToString(), new UTF8Encoding(false));
        }
    }
}
