using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace RgbStone.Core
{
    [DataContract]
    public sealed class DeviceSettings
    {
        [DataMember] public bool Enabled = true;
        /// <summary>LED counts for resizable zones (headers, channels), keyed by zone name.</summary>
        [DataMember] public Dictionary<string, int> ZoneLeds = new Dictionary<string, int>();
        /// <summary>What is plugged into each header, e.g. imported from SignalRGB ("Arctic Liquid Freezer III Pro + 3 × P14").</summary>
        [DataMember] public Dictionary<string, string> ZoneNotes = new Dictionary<string, string>();
        /// <summary>LEDs per chained part (fan, pump) on a header, so every part runs the effect on its own ring. 0 = the whole header is one strip.</summary>
        [DataMember] public Dictionary<string, int> ZoneSegments = new Dictionary<string, int>();
    }

    [DataContract]
    public sealed class AppSettings
    {
        [DataMember] public string Effect = "rainbow";
        [DataMember] public string Color1 = "#009BDE";
        [DataMember] public string Color2 = "#FF008C";
        [DataMember] public double Speed = 1.0;
        [DataMember] public int Brightness = 100;
        [DataMember] public int Fps = 30;
        [DataMember] public string ShutdownColor = "#000000";
        [DataMember] public bool ImportedSignalRgb;
        /// <summary>RAM lighting goes over the system SMBus, shared with voltage/SPD chips. Off unless the user turns it on.</summary>
        [DataMember] public bool RamControl;
        /// <summary>Stop the vendor lighting services while rgbstone runs (restored on exit).</summary>
        [DataMember] public bool VendorTakeover;
        /// <summary>Home-network web dashboard; off by default.</summary>
        [DataMember] public bool WebEnabled;
        [DataMember] public string WebPin;
        /// <summary>Services rgbstone stopped and must start again; persisted so a crash cannot leave them off.</summary>
        [DataMember] public List<string> StoppedServices = new List<string>();
        [DataMember] public Dictionary<string, DeviceSettings> Devices = new Dictionary<string, DeviceSettings>();

        public static string Folder { get; private set; }
        static string FilePath => Path.Combine(Folder, "rgbstone.json");

        /// <summary>Used by tweakandrgbstone: rgbstone.json and rgbstone.log live in the shared data folder.</summary>
        public static void UseFolder(string folder)
        {
            Directory.CreateDirectory(folder);
            Folder = folder;
        }

        /// <summary>Portable: settings live next to the exe; falls back to %APPDATA%\rgbstone when that folder is read-only.</summary>
        public static void ResolveFolder()
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            try
            {
                string probe = Path.Combine(exeDir, ".rgbstone-write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                Folder = exeDir;
            }
            catch
            {
                Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "rgbstone");
                Directory.CreateDirectory(Folder);
            }
        }

        static DataContractJsonSerializer Serializer() =>
            new DataContractJsonSerializer(typeof(AppSettings), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    using (var fs = File.OpenRead(FilePath))
                    {
                        var s = (AppSettings)Serializer().ReadObject(fs);
                        if (s.Devices == null) s.Devices = new Dictionary<string, DeviceSettings>();
                        if (s.StoppedServices == null) s.StoppedServices = new List<string>();
                        foreach (var d in s.Devices.Values)
                        {
                            if (d.ZoneLeds == null) d.ZoneLeds = new Dictionary<string, int>();
                            if (d.ZoneNotes == null) d.ZoneNotes = new Dictionary<string, string>();
                            if (d.ZoneSegments == null) d.ZoneSegments = new Dictionary<string, int>();
                        }
                        return s;
                    }
            }
            catch (Exception ex) { Log.Error("Ayarlar okunamadı, varsayılanlar kullanılıyor", ex); }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                using (var ms = new MemoryStream())
                {
                    using (var w = JsonReaderWriterFactory.CreateJsonWriter(ms, Encoding.UTF8, false, true, "  "))
                        Serializer().WriteObject(w, this);
                    File.WriteAllBytes(FilePath, ms.ToArray());
                }
            }
            catch (Exception ex) { Log.Error("Ayarlar kaydedilemedi", ex); }
        }

        public DeviceSettings For(string deviceId)
        {
            if (!Devices.TryGetValue(deviceId, out var d)) Devices[deviceId] = d = new DeviceSettings();
            return d;
        }

        public EffectSettings ToEffectSettings() => new EffectSettings
        {
            Color1 = Rgb.Parse(Color1),
            Color2 = Rgb.Parse(Color2),
            Speed = Math.Max(0.1, Math.Min(5, Speed)),
            Brightness = Math.Max(0, Math.Min(100, Brightness)) / 100.0,
        };
    }
}
