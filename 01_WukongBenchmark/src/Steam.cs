using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace VkBenchmark
{
    public sealed class SteamInstallation
    {
        public const string AppId = "3132990";
        public string SteamExe;
        public string GameDirectory;
        public string BuildId;
        public string[] ConfigDirectories
        {
            get { return new[] { Path.Combine(GameDirectory, "b1", "Saved", "Config", "Windows"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "b1", "Saved", "Config", "Windows") }; }
        }
        public static string VdfValue(string text, string key)
        {
            var match = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.Replace("\\\\", "\\") : null;
        }
        public static SteamInstallation Discover(string explicitDirectory)
        {
            var roots = new List<string>();
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                    if (key != null && key.GetValue("SteamPath") != null) roots.Add(key.GetValue("SteamPath").ToString());
            }
            catch (System.Security.SecurityException) { /* The default Steam directory remains discoverable in restricted sessions. */ }
            catch (UnauthorizedAccessException) { }
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
            string steamRoot = roots.FirstOrDefault(p => File.Exists(Path.Combine(p, "steam.exe")));
            if (steamRoot == null) throw new FileNotFoundException("Steam client not found.");
            var libraries = new List<string> { steamRoot };
            string folders = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (File.Exists(folders))
                foreach (Match match in Regex.Matches(File.ReadAllText(folders), "\"path\"\\s*\"([^\"]+)\""))
                    libraries.Add(match.Groups[1].Value.Replace("\\\\", "\\"));
            foreach (var library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string manifest = Path.Combine(library, "steamapps", "appmanifest_" + AppId + ".acf");
                if (!File.Exists(manifest)) continue;
                string content = File.ReadAllText(manifest);
                if (VdfValue(content, "appid") != AppId) continue;
                string name = VdfValue(content, "installdir");
                if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || name == "..")
                    throw new InvalidDataException("Invalid Steam installation directory in manifest.");
                string directory = Path.GetFullPath(Path.Combine(library, "steamapps", "common", name));
                if (explicitDirectory != null && !string.Equals(directory.TrimEnd('\\'), Path.GetFullPath(explicitDirectory).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
                if (!Directory.Exists(Path.Combine(directory, "b1", "Binaries", "Win64")))
                    throw new DirectoryNotFoundException("Benchmark installation is incomplete: " + directory);
                return new SteamInstallation { SteamExe = Path.Combine(steamRoot, "steam.exe"), GameDirectory = directory, BuildId = VdfValue(content, "buildid") };
            }
            throw new FileNotFoundException("Install Black Myth: Wukong Benchmark Tool in Steam (AppID 3132990). It was not found in library manifests.");
        }

        public static void RequireNoGame()
        {
            foreach (var process in Process.GetProcesses())
                using (process)
                    if (process.ProcessName.Equals("b1", StringComparison.OrdinalIgnoreCase) ||
                        process.ProcessName.StartsWith("b1-", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Close the game/benchmark before running this tool. PID " + process.Id);
        }
        public List<Process> OwnedProcesses()
        {
            var matches = new List<Process>();
            string prefix = Path.GetFullPath(GameDirectory).TrimEnd('\\') + "\\";
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (process.ProcessName.StartsWith("b1", StringComparison.OrdinalIgnoreCase) &&
                        process.MainModule.FileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    { matches.Add(process); continue; }
                }
                catch (System.ComponentModel.Win32Exception) { }
                catch (InvalidOperationException) { }
                process.Dispose();
            }
            return matches;
        }
        public Process Launch(CancellationToken token, int timeoutSeconds)
        {
            using (Process.Start(new ProcessStartInfo(SteamExe, "-applaunch " + AppId + " -culture=en") { UseShellExecute = false })) { }
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed.TotalSeconds < timeoutSeconds)
            {
                token.ThrowIfCancellationRequested();
                foreach (var process in OwnedProcesses())
                {
                    if (process.MainWindowHandle != IntPtr.Zero && process.ProcessName.IndexOf("Shipping", StringComparison.OrdinalIgnoreCase) >= 0) return process;
                    process.Dispose();
                }
                Thread.Sleep(500);
            }
            throw new TimeoutException("Steam did not create the benchmark window. Check login, installation and launch dialogs.");
        }
        public void StopOwned()
        {
            foreach (var process in OwnedProcesses())
                using (process)
                {
                    if (process.HasExited) continue;
                    process.CloseMainWindow();
                    if (!process.WaitForExit(5000)) { process.Kill(); if (!process.WaitForExit(10000)) throw new IOException("Benchmark did not exit."); }
                }
            var remaining = OwnedProcesses();
            foreach (var process in remaining) process.Dispose();
            if (remaining.Count != 0) throw new IOException("Benchmark is still running; settings were NOT restored underneath it.");
        }
    }

    public sealed class BackupEntry
    {
        public int Slot { get; set; }
        public string Name { get; set; }
        public string BytesBase64 { get; set; }
        public int Attributes { get; set; }
    }
    public sealed class BackupData
    {
        public string[] Directories { get; set; }
        public List<BackupEntry> Files { get; set; }
    }
    public sealed class ConfigBackup
    {
        private readonly BackupData data;
        private readonly string journal;
        public ConfigBackup(string[] directories, string journal)
        {
            this.journal = journal;
            if (File.Exists(journal)) throw new IOException("Unrecovered settings backup exists: " + journal + ". Use --restore first.");
            data = new BackupData { Directories = directories.Select(Path.GetFullPath).ToArray(), Files = new List<BackupEntry>() };
            for (int slot = 0; slot < data.Directories.Length; slot++)
                if (Directory.Exists(data.Directories[slot]))
                    foreach (string file in Directory.GetFiles(data.Directories[slot], "*.ini"))
                        data.Files.Add(new BackupEntry { Slot = slot, Name = Path.GetFileName(file),
                            BytesBase64 = Convert.ToBase64String(File.ReadAllBytes(file)), Attributes = (int)File.GetAttributes(file) });
            JsonFile.Write(journal, data); // Durable before the game is allowed to modify anything.
        }
        private ConfigBackup(BackupData data, string journal) { this.data = data; this.journal = journal; }
        public static ConfigBackup Load(string journal, string[] expectedDirectories)
        {
            var data = JsonFile.Read<BackupData>(journal);
            if (data == null || data.Directories == null || data.Files == null ||
                !data.Directories.SequenceEqual(expectedDirectories.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("Backup does not belong to this installation.");
            foreach (var entry in data.Files)
                if (entry.Slot < 0 || entry.Slot >= data.Directories.Length || string.IsNullOrEmpty(entry.Name) ||
                    entry.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || entry.Name != Path.GetFileName(entry.Name) ||
                    !entry.Name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Unsafe backup entry.");
            return new ConfigBackup(data, journal);
        }
        public void Restore()
        {
            for (int slot = 0; slot < data.Directories.Length; slot++)
            {
                string directory = data.Directories[slot];
                var entries = data.Files.Where(e => e.Slot == slot).ToList();
                if (Directory.Exists(directory))
                    foreach (string file in Directory.GetFiles(directory, "*.ini"))
                        if (!entries.Any(e => string.Equals(e.Name, Path.GetFileName(file), StringComparison.OrdinalIgnoreCase)))
                        { File.SetAttributes(file, FileAttributes.Normal); File.Delete(file); }
                foreach (var entry in entries)
                {
                    Directory.CreateDirectory(directory);
                    string file = Path.Combine(directory, entry.Name);
                    if (File.Exists(file)) File.SetAttributes(file, FileAttributes.Normal);
                    File.WriteAllBytes(file, Convert.FromBase64String(entry.BytesBase64));
                    File.SetAttributes(file, (FileAttributes)entry.Attributes);
                }
            }
            File.Delete(journal); // A copy is kept inside the run directory for inspection.
        }
    }
}
