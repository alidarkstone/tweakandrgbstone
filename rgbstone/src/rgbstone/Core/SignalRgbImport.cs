using System;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace RgbStone.Core
{
    /// <summary>
    /// One-time, read-only import of the fan/strip LED counts the user already set up in SignalRGB
    /// (HKCU\Software\WhirlwindFX\SignalRgb\devices). SignalRGB itself is not modified.
    /// </summary>
    public static class SignalRgbImport
    {
        static readonly Regex LedCountRx = new Regex("\"LedCount\"\\s*:\\s*(\\d+)");
        static readonly Regex NameRx = new Regex("\"ProductName\"\\s*:\\s*\"([^\"]+)\"");

        /// <summary>Copies LED counts onto zones of matching devices. Returns how many zones were set.</summary>
        public static int Apply(AppSettings settings, System.Collections.Generic.IEnumerable<RgbDevice> devices)
        {
            int n = 0;
            try
            {
                using (var root = Registry.CurrentUser.OpenSubKey(@"Software\WhirlwindFX\SignalRgb\devices"))
                {
                    if (root == null) return 0;
                    foreach (var dev in devices)
                    {
                        if (!dev.Id.StartsWith("hid:")) continue;
                        // rgbstone id "hid:0db0:0076:SERIAL" ~ SignalRGB key "0db0:0076:SERIAL"
                        string[] parts = dev.Id.Split(':');
                        if (parts.Length < 4) continue;
                        string prefix = parts[1] + ":" + parts[2] + ":";
                        string key = root.GetSubKeyNames().FirstOrDefault(k =>
                            k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                            k.Substring(prefix.Length).Replace("-", "&").Equals(parts[3], StringComparison.OrdinalIgnoreCase))
                            ?? root.GetSubKeyNames().FirstOrDefault(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                        if (key == null) continue;
                        using (var dk = root.OpenSubKey(key))
                        {
                            foreach (var zone in dev.Zones.Where(z => z.Resizable))
                            {
                                if (!(dk.GetValue(zone.Name) is string json)) continue;
                                int leds = LedCountRx.Matches(json).Cast<Match>().Sum(m => int.Parse(m.Groups[1].Value));
                                if (!settings.For(dev.Id).ZoneLeds.ContainsKey(zone.Name)) settings.For(dev.Id).ZoneLeds[zone.Name] = leds; // never overwrite the user's own value
                                var names = NameRx.Matches(json).Cast<Match>().Select(m => m.Groups[1].Value).ToList();
                                if (names.Count > 0)
                                    settings.For(dev.Id).ZoneNotes[zone.Name] = string.Join(" + ", names.GroupBy(n => n).Select(g => g.Count() > 1 ? $"{g.Count()} × {g.Key}" : g.Key));
                                n++;
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Error("SignalRGB ayarları okunamadı", ex); }
            if (n > 0) Log.Info($"SignalRGB'den {n} bölgenin LED sayısı aktarıldı.");
            return n;
        }
    }
}
