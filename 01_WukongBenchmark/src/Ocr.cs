using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VkBenchmark
{
    public sealed class Word
    {
        public string Text;
        public Rectangle Box;
        public double Confidence;
    }

    public sealed class Phrase
    {
        public string Text;
        public Rectangle Box;
    }

    public sealed class OcrPage
    {
        public List<Word> Words = new List<Word>();
        public int Width;
        public int Height;
        public string Text { get { return string.Join("\n", Rows().Select(r => string.Join(" ", r.Select(w => w.Text)))); } }
        public static string Normalize(string text)
        {
            return Regex.Replace(text.ToLowerInvariant(), "[^a-z0-9]+", " ").Trim();
        }

        public List<List<Word>> Rows()
        {
            // TSV block IDs are unreliable in sparse menus. Rebuild rows by geometry.
            var rows = new List<List<Word>>();
            foreach (var word in Words.OrderBy(w => w.Box.Top))
            {
                double center = word.Box.Top + word.Box.Height / 2.0;
                var row = rows.FirstOrDefault(r => Math.Abs(center -
                    r.Average(x => x.Box.Top + x.Box.Height / 2.0)) <= Math.Max(7, word.Box.Height * .6));
                if (row == null) { row = new List<Word>(); rows.Add(row); }
                row.Add(word);
            }
            foreach (var row in rows) row.Sort((a, b) => a.Box.Left.CompareTo(b.Box.Left));
            return rows;
        }

        public Phrase Find(params string[] aliases)
        {
            foreach (var alias in aliases)
            {
                string wanted = Normalize(alias);
                foreach (var row in Rows())
                    for (int start = 0; start < row.Count; start++)
                    {
                        string text = "";
                        Rectangle box = row[start].Box;
                        for (int end = start; end < row.Count; end++)
                        {
                            if (end > start && row[end].Box.Left - row[end - 1].Box.Right > Width * .09) break;
                            text = (text + " " + Normalize(row[end].Text)).Trim();
                            box = Rectangle.Union(box, row[end].Box);
                            if (text == wanted)
                            {
                                // Do not confuse "Super Resolution" with "Super Resolution Sampling",
                                // or "Full Ray Tracing" with "Full Ray Tracing Level".
                                bool longerLabel = end + 1 < row.Count &&
                                    row[end + 1].Box.Left - row[end].Box.Right < Math.Max(row[end].Box.Height, Width * .013) &&
                                    Regex.IsMatch(row[end + 1].Text, "^[A-Za-z]+$");
                                if (!longerLabel) return new Phrase { Text = alias, Box = box };
                            }
                            if (text.Length > wanted.Length + 3) break;
                        }
                    }
            }
            return null;
        }

        public string ValueToRight(Phrase label)
        {
            double cy = label.Box.Top + label.Box.Height / 2.0;
            return string.Join(" ", Words.Where(w => w.Box.Left > label.Box.Right + 3 &&
                Math.Abs(w.Box.Top + w.Box.Height / 2.0 - cy) < Math.Max(label.Box.Height, w.Box.Height) * .8)
                .OrderBy(w => w.Box.Left).Select(w => w.Text));
        }

        public static OcrPage FromTsv(string text, int width, int height)
        {
            var page = new OcrPage { Width = width, Height = height };
            foreach (string line in text.Split('\n'))
            {
                var fields = line.TrimEnd('\r').Split('\t');
                if (fields.Length < 12 || fields[0] != "5" || string.IsNullOrWhiteSpace(fields[11])) continue;
                double confidence;
                int x, y, w, h;
                if (!double.TryParse(fields[10], NumberStyles.Float, CultureInfo.InvariantCulture, out confidence) ||
                    !int.TryParse(fields[6], out x) || !int.TryParse(fields[7], out y) ||
                    !int.TryParse(fields[8], out w) || !int.TryParse(fields[9], out h))
                    throw new InvalidDataException("Malformed Tesseract TSV.");
                if (confidence >= 35 && w > 0 && h > 0)
                    page.Words.Add(new Word { Text = fields[11], Box = new Rectangle(x, y, w, h), Confidence = confidence });
            }
            return page;
        }
    }

    public sealed class Tesseract
    {
        private readonly string executable;
        private readonly CancellationToken token;
        public Tesseract(string executable, CancellationToken token)
        {
            this.executable = executable;
            this.token = token;
            if (!File.Exists(executable)) throw new FileNotFoundException("Install Tesseract 5 with English data, or pass --ocr PATH.", executable);
        }

        public OcrPage Read(string imagePath, int width, int height)
        {
            var start = new ProcessStartInfo(executable, Quote(imagePath) + " stdout -l eng --psm 11 tsv")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8
            };
            using (var process = Process.Start(start))
            {
                try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (InvalidOperationException) { }
                Task<string> output = process.StandardOutput.ReadToEndAsync();
                Task<string> error = process.StandardError.ReadToEndAsync();
                try
                {
                    var timer = Stopwatch.StartNew();
                    while (!process.WaitForExit(100))
                    {
                        token.ThrowIfCancellationRequested();
                        if (timer.Elapsed.TotalSeconds > 30) throw new TimeoutException("Tesseract exceeded 30 seconds.");
                    }
                    if (process.ExitCode != 0) throw new InvalidOperationException("Tesseract: " + error.Result);
                    File.WriteAllText(Path.ChangeExtension(imagePath, ".tsv"), output.Result, Encoding.UTF8);
                    return OcrPage.FromTsv(output.Result, width, height);
                }
                finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(); } }
            }
        }

        public static string Quote(string value)
        {
            if (value.Contains("\"") || value.Contains("\r") || value.Contains("\n"))
                throw new ArgumentException("Invalid path argument.");
            return "\"" + value + "\"";
        }

        public static string Discover()
        {
            var candidates = new List<string> {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tesseract-OCR", "tesseract.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Tesseract-OCR", "tesseract.exe")
            };
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                if (!string.IsNullOrWhiteSpace(directory)) candidates.Add(Path.Combine(directory.Trim('"'), "tesseract.exe"));
            return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
        }
    }

    public static class ResultParser
    {
        public static Metrics Parse(OcrPage page)
        {
            var result = new Metrics {
                AverageFps = ReadMetric(page, "Average FPS", "Average Frame Rate", "Average Framerate"),
                MinimumFps = ReadMetric(page, "Minimum FPS", "Minimum Frame Rate", "Min FPS"),
                MaximumFps = ReadMetric(page, "Maximum FPS", "Maximum Frame Rate", "Max FPS")
            };
            result.Validate();
            return result;
        }

        private static double ReadMetric(OcrPage page, params string[] aliases)
        {
            var label = page.Find(aliases);
            if (label == null) throw new InvalidDataException("Result label not found: " + aliases[0]);
            Func<Word, bool> numeric = w => Regex.IsMatch(w.Text, @"^\d+(?:[.,]\d+)?$");
            var right = page.Words.Where(w => numeric(w) && w.Box.Left > label.Box.Right &&
                w.Box.Left - label.Box.Right < page.Width * .18 &&
                Math.Abs(w.Box.Top + w.Box.Height / 2 - label.Box.Top - label.Box.Height / 2) < label.Box.Height)
                .OrderBy(w => w.Box.Left).ToList();
            // Some versions put the large number ABOVE the label rather than on its right.
            var vertical = page.Words.Where(w => numeric(w) &&
                Math.Abs(w.Box.Left + w.Box.Width / 2 - label.Box.Left - label.Box.Width / 2) < Math.Max(label.Box.Width * .6, page.Width * .025) &&
                (w.Box.Bottom <= label.Box.Top && label.Box.Top - w.Box.Bottom < page.Height * .09 ||
                 w.Box.Top >= label.Box.Bottom && w.Box.Top - label.Box.Bottom < page.Height * .065))
                .OrderBy(w => Math.Abs(w.Box.Top - label.Box.Top)).ToList();
            var values = right.Count == 1 ? right : vertical;
            if (values.Count != 1) throw new InvalidDataException("Ambiguous/missing number for " + aliases[0]);
            return double.Parse(values[0].Text.Replace(',', '.'), CultureInfo.InvariantCulture);
        }
    }

    public sealed class CompletionGate
    {
        private readonly DateTime started;
        private DateTime lastMotion;
        public bool SawMotion { get; private set; }
        public CompletionGate(DateTime now) { started = lastMotion = now; }
        public bool Observe(DateTime now, double difference)
        {
            if (difference > .015) { SawMotion = true; lastMotion = now; }
            return SawMotion && (now - started).TotalSeconds >= 20 && (now - lastMotion).TotalSeconds >= 8;
        }
    }
}
