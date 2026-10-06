using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace VkBenchmark
{
    public sealed class Metrics
    {
        public double AverageFps { get; set; }
        public double MinimumFps { get; set; }
        public double MaximumFps { get; set; }
        public void Validate()
        {
            foreach (double value in new[] { AverageFps, MinimumFps, MaximumFps })
                if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0 || value > 10000)
                    throw new InvalidDataException("Invalid FPS value: " + value);
            if (MinimumFps > AverageFps || AverageFps > MaximumFps)
                throw new InvalidDataException("FPS must satisfy minimum <= average <= maximum.");
        }
        public string Fingerprint()
        {
            return string.Join("/", new[] { MinimumFps, AverageFps, MaximumFps }
                .Select(x => x.ToString("R", CultureInfo.InvariantCulture)));
        }
    }

    public sealed class PassResult
    {
        public string Name { get; set; }
        public string StartedUtc { get; set; }
        public string FinishedUtc { get; set; }
        public Dictionary<string, string> Settings { get; set; }
        public Metrics Metrics { get; set; }
        public string Screenshot { get; set; }
        public string OcrText { get; set; }
    }

    public sealed class RunReport
    {
        public string CreatedUtc { get; set; }
        public string Status { get; set; }
        public string Error { get; set; }
        public string SteamBuildId { get; set; }
        public bool SettingsRestored { get; set; }
        public Dictionary<string, object> Hardware { get; set; }
        public List<PassResult> Passes { get; set; }
        public RunReport()
        {
            CreatedUtc = DateTime.UtcNow.ToString("o");
            Status = "running";
            Passes = new List<PassResult>();
        }
    }

    public static class JsonFile
    {
        public static void Write(string path, object value)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = 16777216 };
            File.WriteAllText(path, json.Serialize(value), new UTF8Encoding(false));
        }
        public static T Read<T>(string path)
        {
            return new JavaScriptSerializer { MaxJsonLength = 16777216 }
                .Deserialize<T>(File.ReadAllText(path, Encoding.UTF8));
        }
    }
}
