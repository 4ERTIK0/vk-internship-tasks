using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace VkBenchmark
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            if (args.Contains("--help"))
            {
                Console.WriteLine("WukongRunner [--check] [--restore] [--install DIR] [--ocr tesseract.exe] [--output DIR] [--gpu-rt on|off] [--timeout SECONDS]\nF12 or Ctrl+C cancels a run. Default: two automated passes, ray tracing ON in GPU pass.");
                return 0;
            }
            var options = new Dictionary<string, string>();
            bool check = false, restore = false;
            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "--check") { check = true; continue; }
                    if (args[i] == "--restore") { restore = true; continue; }
                    if (!new[] { "--install", "--ocr", "--output", "--gpu-rt", "--timeout" }.Contains(args[i]) || i + 1 >= args.Length)
                        throw new ArgumentException("Unknown/incomplete option: " + args[i]);
                    options.Add(args[i], args[++i]);
                }
                int timeout = options.ContainsKey("--timeout") ? int.Parse(options["--timeout"]) : 900;
                if (timeout < 60 || timeout > 3600) throw new ArgumentException("--timeout must be 60..3600 seconds.");
                string rt = options.ContainsKey("--gpu-rt") ? options["--gpu-rt"] : "on";
                if (rt != "on" && rt != "off") throw new ArgumentException("--gpu-rt must be on or off.");
                string baseDirectory = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
                string output = Path.GetFullPath(options.ContainsKey("--output") ? options["--output"] : Path.Combine(baseDirectory, "results"));
                // A global mutex prevents two copies, including copies using different output paths.
                bool created;
                using (var mutex = new Mutex(true, "Local\\VkWukongBenchmarkRunner", out created))
                {
                    if (!created) throw new InvalidOperationException("Another runner is active.");
                    var steam = SteamInstallation.Discover(options.ContainsKey("--install") ? options["--install"] : null);
                    string journal = Path.Combine(baseDirectory, "settings-recovery.json");
                    SteamInstallation.RequireNoGame();
                    if (restore)
                    {
                        ConfigBackup.Load(journal, steam.ConfigDirectories).Restore();
                        Console.WriteLine("Original settings restored."); return 0;
                    }
                    using (var cancellation = new CancellationTokenSource())
                    {
                        Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e) { e.Cancel = true; cancellation.Cancel(); };
                        var ocr = new Tesseract(options.ContainsKey("--ocr") ? Path.GetFullPath(options["--ocr"]) : Tesseract.Discover(), cancellation.Token);
                        var hardware = Hardware.Collect();
                        if (check)
                        {
                            Console.WriteLine("Steam installation: " + steam.GameDirectory + "\nBuild: " + steam.BuildId + "\nTesseract executable and WMI: available. GUI/OCR/benchmark execution is not verified by --check.");
                            return 0;
                        }
                        NativeWindow.SetProcessDPIAware();
                        string runDir = Path.Combine(output, DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ"));
                        Directory.CreateDirectory(runDir);
                        var report = new RunReport { Hardware = hardware, SteamBuildId = steam.BuildId };
                        ConfigBackup backup = null;
                        bool mayOwnGame = false;
                        try
                        {
                            backup = new ConfigBackup(steam.ConfigDirectories, journal);
                            File.Copy(journal, Path.Combine(runDir, "settings-before.json"));
                            foreach (bool cpu in new[] { true, false })
                            {
                                string name = cpu ? "CPU" : "GPU";
                                Console.WriteLine(name + ": applying and verifying settings. F12 cancels.");
                                mayOwnGame = true;
                                Dictionary<string, string> requested;
                                using (var process = steam.Launch(cancellation.Token, timeout))
                                {
                                    var driver = new MenuDriver(new NativeWindow(process, cancellation.Token), ocr, Path.Combine(runDir, name, "setup"), cancellation.Token);
                                    driver.WaitForMenu(timeout);
                                    requested = driver.Configure(cpu, rt == "on", null);
                                }
                                // Always restart once: settings requiring engine restart must be active during measurement.
                                steam.StopOwned();
                                using (var process = steam.Launch(cancellation.Token, timeout))
                                {
                                    var driver = new MenuDriver(new NativeWindow(process, cancellation.Token), ocr, Path.Combine(runDir, name), cancellation.Token);
                                    driver.WaitForMenu(timeout);
                                    var verified = driver.Configure(cpu, rt == "on", requested);
                                    Console.WriteLine(name + ": benchmark running; waiting for the result screen.");
                                    var result = driver.Run(name, verified, timeout);
                                    report.Passes.Add(result);
                                    Report.Save(runDir, report);
                                    Console.WriteLine(name + ": average " + result.Metrics.AverageFps + ", minimum " + result.Metrics.MinimumFps + ", maximum " + result.Metrics.MaximumFps + " FPS");
                                }
                                steam.StopOwned();
                            }
                            report.Status = "complete";
                        }
                        catch (Exception ex)
                        {
                            report.Status = ex is OperationCanceledException ? "cancelled" : "failed";
                            report.Error = ex.ToString();
                            Console.Error.WriteLine(ex.Message);
                        }
                        finally
                        {
                            try
                            {
                                if (mayOwnGame) steam.StopOwned();
                                if (backup != null) { backup.Restore(); report.SettingsRestored = true; }
                            }
                            catch (Exception ex)
                            {
                                report.Status = "failed";
                                report.Error += "\nRESTORE FAILED: " + ex;
                                Console.Error.WriteLine("Settings backup retained: " + journal + ". Close the benchmark and run --restore. " + ex.Message);
                            }
                            Report.Save(runDir, report);
                            Console.WriteLine("Report: " + Path.Combine(runDir, "report.html"));
                        }
                        return report.Status == "complete" && report.SettingsRestored ? 0 : 1;
                    }
                }
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 2; }
        }
    }
}
