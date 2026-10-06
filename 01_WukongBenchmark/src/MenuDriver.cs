using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace VkBenchmark
{
    public sealed class Setting
    {
        public string Tab;
        public string[] Labels;
        public string Value;
        public int MaxSteps;
        public Setting(string tab, string value, int maxSteps, params string[] labels)
        { Tab = tab; Value = value; MaxSteps = maxSteps; Labels = labels; }
    }

    public sealed class MenuDriver
    {
        private readonly NativeWindow window;
        private readonly Tesseract ocr;
        private readonly string directory;
        private readonly CancellationToken token;
        private int sequence;
        public MenuDriver(NativeWindow window, Tesseract ocr, string directory, CancellationToken token)
        { this.window = window; this.ocr = ocr; this.directory = directory; this.token = token; Directory.CreateDirectory(directory); }

        public OcrPage Read(string name)
        {
            token.ThrowIfCancellationRequested();
            using (var image = window.Capture())
            {
                string path = Path.Combine(directory, string.Format("{0:D4}-{1}.png", ++sequence, name));
                image.Save(path, ImageFormat.Png);
                var page = ocr.Read(path, image.Width, image.Height);
                File.WriteAllText(Path.ChangeExtension(path, ".txt"), page.Text);
                return page;
            }
        }
        private void Pause(int milliseconds) { if (token.WaitHandle.WaitOne(milliseconds)) token.ThrowIfCancellationRequested(); window.CheckAbort(); }
        public void WaitForMenu(int seconds)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed.TotalSeconds < seconds)
            {
                OcrPage page = Read("startup");
                if (page.Find("Graphics", "Display", "Run Benchmark", "Start Benchmark", "Benchmark") != null) return;
                var settings = page.Find("Settings");
                if (settings != null) { window.Click(settings); Pause(1000); continue; }
                // No blind Enter: only pass the game's explicit start prompt.
                if (page.Find("Press Any Button", "Press Any Key", "Press Enter") != null) window.Key(Keys.Enter);
                Pause(3000);
            }
            throw new TimeoutException("English benchmark menu not recognized. Check startup screenshots; initial EULA/Steam dialogs may need one-time completion.");
        }
        private void OpenTab(string tab)
        {
            var page = Read("open-" + tab.ToLowerInvariant());
            var target = page.Find(tab);
            if (target == null)
            {
                var settings = page.Find("Settings");
                if (settings != null) { window.Click(settings); Pause(600); page = Read("settings"); target = page.Find(tab); }
            }
            if (target == null) throw new InvalidDataException("Cannot locate tab: " + tab);
            window.Click(target); Pause(400); window.Scroll(25);
        }
        private OcrPage FindRow(string[] labels, out Phrase label)
        {
            window.Scroll(25);
            for (int pageNumber = 0; pageNumber < 12; pageNumber++)
            {
                var page = Read("find-setting");
                label = page.Find(labels);
                if (label != null) return page;
                window.Scroll(-3);
            }
            label = null;
            throw new InvalidDataException("Cannot locate setting: " + labels[0]);
        }
        public static bool ValueMatches(string actual, string expected)
        {
            Func<string, string> normalize = s => Regex.Replace(OcrPage.Normalize(s.Replace('×', 'x')), @"(\d)\s*x\s*(\d)", "$1x$2");
            return normalize(actual) == normalize(expected);
        }
        private string SetChoice(Setting setting, bool verifyOnly)
        {
            Phrase label;
            var page = FindRow(setting.Labels, out label);
            string actual = page.ValueToRight(label);
            if (ValueMatches(actual, setting.Value)) return actual;
            if (verifyOnly) throw new InvalidDataException("Setting not retained after restart: " + setting.Labels[0] + "; wanted " + setting.Value + "; got " + actual);
            window.Click(label);
            // Moving right visits every value, then moving left also handles clamped selectors/sliders.
            foreach (var key in new[] { Keys.Right, Keys.Left })
                for (int step = 0; step < setting.MaxSteps; step++)
                {
                    window.Key(key); Pause(120);
                    page = Read("set-setting");
                    label = page.Find(setting.Labels);
                    if (label == null) throw new InvalidDataException("Setting disappeared during selection: " + setting.Labels[0]);
                    actual = page.ValueToRight(label);
                    if (ValueMatches(actual, setting.Value)) return actual;
                }
            throw new InvalidDataException("Unsupported/unrecognized value: " + setting.Labels[0] + " = " + setting.Value + "; last OCR: " + actual);
        }
        private string SelectMaximumResolution(bool verifyOnly, string expected)
        {
            if (verifyOnly) return SetChoice(new Setting("Display", expected, 40, "Display Resolution", "Resolution"), true);
            Phrase label;
            var page = FindRow(new[] { "Display Resolution", "Resolution" }, out label);
            string best = null;
            long bestPixels = 0;
            window.Click(label);
            foreach (var key in new[] { Keys.Right, Keys.Left })
            {
                var visited = new HashSet<string>();
                for (int step = 0; step < 40; step++)
                {
                    page = Read("resolution"); label = page.Find("Display Resolution", "Resolution");
                    if (label == null) throw new InvalidDataException("Resolution selector disappeared.");
                    string value = page.ValueToRight(label);
                    if (!visited.Add(value)) break;
                    var match = Regex.Match(value, @"^\s*(\d{3,5})\s*[xX×]\s*(\d{3,5})\s*$");
                    if (!match.Success) throw new InvalidDataException("Unreadable resolution: " + value);
                    long pixels = long.Parse(match.Groups[1].Value) * long.Parse(match.Groups[2].Value);
                    if (pixels > bestPixels) { best = value; bestPixels = pixels; }
                    window.Key(key); Pause(100);
                }
            }
            return SetChoice(new Setting("Display", best, 80, "Display Resolution", "Resolution"), false);
        }
        public Dictionary<string, string> Configure(bool cpu, bool rayTracing, Dictionary<string, string> expected)
        {
            bool verifyOnly = expected != null;
            var values = new Dictionary<string, string>();
            OpenTab("Display");
            var display = new[] {
                new Setting("Display", cpu ? "Windowed" : "Fullscreen", 6, "Display Mode"),
                new Setting("Display", "Off", 5, "V-Sync", "VSync", "Vertical Sync"),
                new Setting("Display", "Off", 10, "Framerate Cap", "Frame Rate Cap", "Frame Rate Limit")
            };
            foreach (var setting in display) values[setting.Labels[0]] = SetChoice(setting, verifyOnly);
            values["Display Resolution"] = cpu
                ? SetChoice(new Setting("Display", "1280 x 720", 80, "Display Resolution", "Resolution"), verifyOnly)
                : SelectMaximumResolution(verifyOnly, verifyOnly ? expected["Display Resolution"] : null);
            Apply();
            OpenTab("Graphics");
            var graphics = new List<Setting> {
                new Setting("Graphics", cpu ? "Low" : "Cinematic", 8, "Graphics Preset", "Quality Preset"),
                new Setting("Graphics", "TSR", 8, "Super Resolution Sampling"),
                new Setting("Graphics", cpu ? "25" : "100", 110, "Super Resolution"),
                new Setting("Graphics", "Off", 5, "Frame Generation"),
                new Setting("Graphics", "Off", 6, "Motion Blur"),
                new Setting("Graphics", cpu || !rayTracing ? "Off" : "On", 4, "Full Ray Tracing")
            };
            // Changing individual qualities turns the displayed preset into Custom.
            // On verification, verify all individual settings instead of the transient preset label.
            foreach (var setting in graphics)
            {
                if (verifyOnly && setting.Labels[0] == "Graphics Preset") continue;
                values[setting.Labels[0]] = SetChoice(setting, verifyOnly);
            }
            string[] qualities = { "View Distance Quality", "Anti-Aliasing Quality", "Post-Effects Quality", "Shadow Quality",
                "Texture Quality", "Visual Effect Quality", "Hair Quality", "Vegetation Quality", "Global Illumination Quality", "Reflection Quality" };
            foreach (string quality in qualities)
            {
                string value = cpu ? (quality == "View Distance Quality" ? "Cinematic" : quality == "Vegetation Quality" ? "High" : "Low") : "Cinematic";
                values[quality] = SetChoice(new Setting("Graphics", value, 8, quality), verifyOnly);
            }
            if (!cpu && rayTracing) values["Full Ray Tracing Level"] = SetChoice(new Setting("Graphics", "Very High", 6, "Full Ray Tracing Level"), verifyOnly);
            if (!verifyOnly) Apply();
            values["Preset policy"] = cpu ? "Low + cinematic view distance + high vegetation" : "Cinematic";
            return values;
        }
        private void Apply()
        {
            var page = Read("apply");
            var button = page.Find("Apply Settings", "Apply");
            if (button != null) { window.Click(button); Pause(800); }
            page = Read("confirm");
            if (page.Find("Keep Changes", "Keep These Settings", "Keep Settings") != null)
            { window.Click(page.Find("Keep Changes", "Keep These Settings", "Keep Settings")); Pause(400); }
            else if (page.Find("Confirm") != null && page.Find("Revert") != null)
            { window.Click(page.Find("Confirm")); Pause(400); }
            page = Read("restart-check");
            if (page.Find("Restart", "Restart Now") != null)
            {
                var later = page.Find("Later", "No");
                if (later == null) throw new InvalidDataException("Unrecognized restart confirmation. See screenshot.");
                window.Click(later); Pause(400);
            }
        }
        public PassResult Run(string name, Dictionary<string, string> settings, int timeoutSeconds)
        {
            var page = Read("before-run");
            var run = page.Find("Run Benchmark", "Start Benchmark", "Start Benchmarking", "Benchmark");
            if (run == null) throw new InvalidDataException("Benchmark start control not found.");
            window.Click(run); Pause(600);
            page = Read("run-confirmation");
            if (page.Find("Confirm") != null && page.Find("Cancel") != null) window.Click(page.Find("Confirm"));
            else if (page.Find("Start Benchmark") != null) window.Click(page.Find("Start Benchmark"));
            string start = DateTime.UtcNow.ToString("o");
            var timer = Stopwatch.StartNew();
            var gate = new CompletionGate(DateTime.UtcNow);
            byte[] previous = null;
            DateTime lastOcr = DateTime.MinValue;
            Metrics candidate = null;
            int repeats = 0;
            while (timer.Elapsed.TotalSeconds < timeoutSeconds)
            {
                Pause(1000);
                using (var screenshot = window.Capture())
                {
                    byte[] current = NativeWindow.Signature(screenshot);
                    double delta = previous == null ? 0 : NativeWindow.Difference(previous, current);
                    previous = current;
                    // During the moving scene, only a tiny image signature is evaluated; no OCR process competes with the CPU test.
                    if (!gate.Observe(DateTime.UtcNow, delta) || (DateTime.UtcNow - lastOcr).TotalSeconds < 4) continue;
                    lastOcr = DateTime.UtcNow;
                    string path = Path.Combine(directory, "result.png");
                    screenshot.Save(path, ImageFormat.Png);
                    OcrPage resultPage = ocr.Read(path, screenshot.Width, screenshot.Height);
                    File.WriteAllText(Path.Combine(directory, "result.txt"), resultPage.Text);
                    Metrics metrics;
                    try { metrics = ResultParser.Parse(resultPage); }
                    catch (InvalidDataException) { repeats = 0; candidate = null; continue; }
                    repeats = candidate != null && candidate.Fingerprint() == metrics.Fingerprint() ? repeats + 1 : 1;
                    candidate = metrics;
                    if (repeats >= 2)
                        return new PassResult { Name = name, StartedUtc = start, FinishedUtc = DateTime.UtcNow.ToString("o"),
                            Settings = settings, Metrics = metrics, Screenshot = path, OcrText = resultPage.Text };
                }
            }
            throw new TimeoutException("No stable benchmark result before timeout. No FPS was fabricated. See diagnostic images.");
        }
    }
}
