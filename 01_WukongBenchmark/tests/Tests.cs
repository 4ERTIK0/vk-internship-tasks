using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;

namespace VkBenchmark
{
    public static class Tests
    {
        private static int count;
        private static void Check(bool ok, string name)
        {
            if (!ok) throw new Exception("FAILED: " + name);
            count++; Console.WriteLine("PASS " + name);
        }
        private static void Throws(Action action, string name)
        {
            bool caught = false;
            try { action(); } catch (InvalidDataException) { caught = true; }
            Check(caught, name);
        }
        private static void Add(OcrPage page, string text, int x, int y, int width)
        { page.Words.Add(new Word { Text = text, Box = new Rectangle(x, y, width, 22), Confidence = 99 }); }
        private static OcrPage Result(bool vertical)
        {
            var page = new OcrPage { Width = 1280, Height = 720 };
            string[] labels = { "Average", "Minimum", "Maximum" };
            string[] values = { "60.5", "40", "90" };
            for (int i = 0; i < 3; i++)
            {
                int x = vertical ? 150 + i * 350 : 150;
                int y = vertical ? 200 : 200 + i * 65;
                Add(page, labels[i], x, y, 90); Add(page, "FPS", x + 100, y, 40);
                Add(page, values[i], vertical ? x + 40 : x + 220, vertical ? y - 40 : y, 55);
            }
            return page;
        }
        [STAThread]
        public static int Main(string[] args)
        {
            string temp = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-data-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                var horizontal = ResultParser.Parse(Result(false));
                Check(horizontal.AverageFps == 60.5 && horizontal.MinimumFps == 40 && horizontal.MaximumFps == 90, "FPS horizontal labels");
                Check(ResultParser.Parse(Result(true)).Fingerprint() == horizontal.Fingerprint(), "FPS number above label");
                var comma = Result(false); comma.Words.Single(w => w.Text == "60.5").Text = "60,5";
                Check(ResultParser.Parse(comma).AverageFps == 60.5, "decimal comma");
                Throws(() => ResultParser.Parse(new OcrPage { Width = 1280, Height = 720 }), "empty result rejected");
                var missing = Result(false); missing.Words.RemoveAll(w => w.Text == "Maximum");
                Throws(() => ResultParser.Parse(missing), "incomplete metrics rejected");
                var reversed = Result(false); reversed.Words.Single(w => w.Text == "40").Text = "80";
                Throws(() => ResultParser.Parse(reversed), "inconsistent FPS rejected");
                Throws(() => new Metrics { AverageFps = double.NaN, MinimumFps = 10, MaximumFps = 20 }.Validate(), "NaN rejected");
                Throws(() => new Metrics { AverageFps = 0, MinimumFps = 0, MaximumFps = 0 }.Validate(), "zero FPS rejected");
                var ambiguous = Result(false); Add(ambiguous, "50", 400, 200, 25);
                Throws(() => ResultParser.Parse(ambiguous), "ambiguous result rejected");
                string tsv = "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n" +
                    "5\t1\t1\t1\t1\t1\t100\t100\t80\t22\t99\tAverage\n" +
                    "5\t1\t9\t1\t1\t1\t190\t100\t35\t22\t98\tFPS\n" +
                    "5\t1\t1\t1\t1\t3\t400\t100\t35\t22\t10\t9999\n";
                var parsed = OcrPage.FromTsv(tsv, 1280, 720);
                Check(parsed.Words.Count == 2, "low-confidence noise removed");
                Check(parsed.Find("Average FPS") != null, "cross-block OCR phrase");
                var settings = new OcrPage { Width = 1280, Height = 720 };
                Add(settings, "Super", 100, 100, 60); Add(settings, "Resolution", 170, 100, 105); Add(settings, "Sampling", 285, 100, 90); Add(settings, "TSR", 650, 100, 40);
                Add(settings, "Super", 100, 160, 60); Add(settings, "Resolution", 170, 160, 105); Add(settings, "25", 650, 160, 40);
                Check(settings.Find("Super Resolution").Box.Top == 160, "avoid longer label prefix");
                Check(settings.ValueToRight(settings.Find("Super Resolution")) == "25", "read setting value in same row");
                Check(MenuDriver.ValueMatches("1280x720", "1280 x 720"), "resolution whitespace");
                Check(MenuDriver.ValueMatches("1280 × 720", "1280 x 720"), "resolution multiplication symbol");
                Check(!MenuDriver.ValueMatches("Very High", "High"), "do not accept partial setting value");
                Check(SteamInstallation.VdfValue("\"path\" \"D:\\\\SteamLibrary\"", "path") == @"D:\SteamLibrary", "Steam VDF escaped path");
                Check(SteamInstallation.VdfValue("\"buildid\" \"123\"", "appid") == null, "VDF missing field");
                var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                var gate = new CompletionGate(start);
                Check(!gate.Observe(start.AddSeconds(100), 0), "static menu is not completed benchmark");
                Check(!gate.Observe(start.AddSeconds(101), .2), "motion is not completion");
                Check(!gate.Observe(start.AddSeconds(107), 0), "wait for stable result");
                Check(gate.Observe(start.AddSeconds(110), 0), "stable scene may be OCR checked");
                Check(!gate.Observe(start.AddSeconds(111), .2), "new motion resets stability");
                Check(NativeWindow.Difference(new byte[] { 0, 255 }, new byte[] { 255, 0 }) == 1, "image difference normalized");
                string config = Path.Combine(temp, "Config"); Directory.CreateDirectory(config);
                string original = Path.Combine(config, "GameUserSettings.ini");
                byte[] bytes = new byte[] { 0xff, 0xfe, 65, 0, 13, 0, 10, 0 };
                File.WriteAllBytes(original, bytes); File.SetAttributes(original, FileAttributes.ReadOnly);
                string journal = Path.Combine(temp, "backup.json");
                var backup = new ConfigBackup(new[] { config }, journal);
                File.SetAttributes(original, FileAttributes.Normal); File.WriteAllText(original, "modified");
                File.WriteAllText(Path.Combine(config, "New.ini"), "new"); File.WriteAllText(Path.Combine(config, "Keep.sav"), "save");
                ConfigBackup.Load(journal, new[] { config }).Restore();
                Check(File.ReadAllBytes(original).SequenceEqual(bytes), "restore original bytes and encoding");
                Check((File.GetAttributes(original) & FileAttributes.ReadOnly) != 0, "restore read-only attribute");
                Check(!File.Exists(Path.Combine(config, "New.ini")) && File.Exists(Path.Combine(config, "Keep.sav")), "remove only newly created INI");
                Check(!File.Exists(journal), "recovery journal removed after restoration");
                string absent = Path.Combine(temp, "Absent"); string absentJournal = Path.Combine(temp, "absent.json");
                var absentBackup = new ConfigBackup(new[] { absent }, absentJournal);
                Directory.CreateDirectory(absent); File.WriteAllText(Path.Combine(absent, "Generated.ini"), "x"); absentBackup.Restore();
                Check(!File.Exists(Path.Combine(absent, "Generated.ini")), "first launch without config files");
                JsonFile.Write(journal, new BackupData { Directories = new[] { config }, Files = new System.Collections.Generic.List<BackupEntry> {
                    new BackupEntry { Slot = 0, Name = "../outside.ini", BytesBase64 = "", Attributes = 0 } } });
                Throws(() => ConfigBackup.Load(journal, new[] { config }), "reject backup path traversal");
                Throws(() => ConfigBackup.Load(journal, new[] { absent }), "reject another installation backup");
                JsonFile.Write(Path.Combine(temp, "metrics.json"), horizontal);
                Check(JsonFile.Read<Metrics>(Path.Combine(temp, "metrics.json")).Fingerprint() == horizontal.Fingerprint(), "JSON round trip");
                var report = new RunReport { Status = "test", Hardware = new System.Collections.Generic.Dictionary<string, object> { { "CPU", "<unsafe>" } } };
                Report.Save(temp, report);
                string html = File.ReadAllText(Path.Combine(temp, "report.html"));
                Check(html.Contains("Компьютер") && html.Contains("&lt;unsafe&gt;") && !html.Contains("<unsafe>"), "report UTF-8 and HTML escaping");
                if (args.Length == 1)
                {
                    var fixture = ResultParser.Parse(OcrPage.FromTsv(File.ReadAllText(args[0]), 1280, 720));
                    Check(fixture.AverageFps == 60.5 && fixture.MinimumFps == 40 && fixture.MaximumFps == 90, "real OCR on synthetic image");
                }
                File.SetAttributes(original, FileAttributes.Normal);
                Console.WriteLine("ALL " + count + " CHECKS PASSED. Synthetic inputs; this is not a game runtime test.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
            finally
            {
                // The directory is a GUID-named child created by this test, never supplied by the user.
                string parent = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd('\\') + "\\";
                if (Path.GetFullPath(temp).StartsWith(parent, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (string file in Directory.GetFiles(temp, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                    Directory.Delete(temp, true);
                }
            }
        }
    }
}
