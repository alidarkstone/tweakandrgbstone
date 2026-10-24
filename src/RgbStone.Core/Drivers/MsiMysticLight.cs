using System;
using System.Collections.Generic;
using System.Linq;
using RgbStone.Core;
using RgbStone.Transport;

namespace RgbStone.Drivers
{
    /// <summary>
    /// MSI Mystic Light motherboards with the newer 761-byte protocol (X870/B850/B840 generation, VID 0x0DB0).
    /// Feature report 0x50 configures zones, 0x51 streams per-LED colors for the ARGB headers, the EZ connector and onboard LEDs.
    /// </summary>
    public sealed class MsiMysticLightProvider : IDeviceProvider
    {
        public string Name => "MSI Mystic Light";

        static readonly ushort[] Pids = { 0x0076, 0x0075, 0x0077, 0x0078, 0x0079, 0x007A, 0x007B, 0x007C, 0x007D, 0x007E };

        public IEnumerable<RgbDevice> Detect(DetectContext ctx)
        {
            foreach (var group in HidSelect.Physical(HidSelect.ByVidPid(ctx, 0x0DB0, Pids)))
            {
                var info = group.FirstOrDefault(d => d.FeatureLength >= 700) ?? group.OrderByDescending(d => d.FeatureLength).First();
                RgbDevice dev = null;
                try { dev = MsiMysticLight.TryCreate(info, ctx.System); }
                catch (Exception ex)
                {
                    Log.Error("MSI", ex);
                    dev = new UnavailableDevice(HidSelect.Id(info), "MSI " + ctx.System.BoardProduct, "MSI", DeviceKind.Motherboard, ex.Message);
                }
                if (dev != null) yield return dev;
            }
            foreach (var dev in DetectLegacy(ctx)) yield return dev;
        }

        // Legacy 0x52 config, as read from a PRO B650M-A (1462:7D77): report id, then 10-byte zones (effect, RGB, flags,
        // RGB2, flags, pad); JRAINBOW1/2 are 11 bytes (plus LED count) at offsets 31 and 42.
        internal static readonly int[] LegacyRainbowOffsets = { 31, 42 };

        /// <summary>Legacy board checked on real hardware (static color per ARGB header confirmed).</summary>
        const ushort LegacyVerified = 0x7D77;

        /// <summary>Board ids that OpenRGB lists as safe for the 0x52 protocol (its bricking-risk list is left out).</summary>
        static readonly ushort[] LegacyPids =
        {
            0x1720, 0x7B12, 0x7B16, 0x7B17, 0x7B18, 0x7B50, 0x7B85, 0x7B92, 0x7B93, 0x7C02, 0x7C34, 0x7C35, 0x7C36, 0x7C37, 0x7C56, 0x7C59,
            0x7C60, 0x7C67, 0x7C71, 0x7C73, 0x7C75, 0x7C76, 0x7C77, 0x7C79, 0x7C80, 0x7C81, 0x7C82, 0x7C83, 0x7C84, 0x7C86, 0x7C87, 0x7C90,
            0x7C91, 0x7C92, 0x7C94, 0x7C95, 0x7C98, 0x7D03, 0x7D04, 0x7D06, 0x7D07, 0x7D08, 0x7D09, 0x7D13, 0x7D14, 0x7D15, 0x7D17, 0x7D18,
            0x7D19, 0x7D20, 0x7D25, 0x7D27, 0x7D28, 0x7D29, 0x7D30, 0x7D31, 0x7D32, 0x7D33, 0x7D36, 0x7D37, 0x7D38, 0x7D40, 0x7D41, 0x7D42,
            0x7D43, 0x7D46, 0x7D50, 0x7D51, 0x7D52, 0x7D53, 0x7D54, 0x7D59, 0x7D67, 0x7D69, 0x7D70, 0x7D73, 0x7D74, 0x7D75, 0x7D76, 0x7D77,
            0x7D78, 0x7D86, 0x7D88, 0x7D89, 0x7D90, 0x7D91, 0x7D93, 0x7D96, 0x7D97, 0x7D98, 0x7D99, 0x7E01, 0x7E03, 0x7E06, 0x7E07, 0x7E09,
            0x7E10, 0x7E20, 0x7E30, 0x7E32, 0x7E34, 0x7E59, 0x7E66, 0x7E70, 0x7E76, 0x7E80, 0x7E81, 0x7E86, 0xB926,
        };

        /// <summary>
        /// Where each header's zone record sits in the 0x52 report, per report size (layouts from OpenRGB's packet structs).
        /// Each entry: (display name, offset). Every record starts with effect, RGB, flags, RGB2.
        /// </summary>
        internal static (string name, int offset)[] LegacyLayout(int size)
        {
            if (size == 162)
                return new[] { ("ARGB Header 1", 11), ("12V RGB 1", 1), ("12V RGB 2", 151), ("Kart üstü", 41) };
            if (size == 112)
                return new[] { ("ARGB Header 1", 11), ("12V RGB 1", 1), ("Kart üstü", 41) };
            return new[] { ("ARGB Header 1", 31), ("ARGB Header 2", 42), ("12V RGB 1", 1), ("12V RGB 2", 174), ("Kart üstü", 74) };
        }

        /// <summary>Windows reports the declared feature size; the 761-byte ones (e.g. 7D77) carry the 185-byte layout.</summary>
        static int LegacySize(int featureLength) =>
            featureLength == 162 || featureLength == 163 ? 162 : featureLength == 112 || featureLength == 113 ? 112 : 185;

        /// <summary>
        /// One-off test for boards without a driver ("rgbstone.exe --msi-test"): reads the board's own 0x52 config, sets the two
        /// ARGB headers to static green, red, blue for a few seconds each by changing only their effect and color bytes, then
        /// writes the original config back. Nothing is saved to the board's flash.
        /// </summary>
        public static void LegacyColorTest()
        {
            var info = Transport.HidEnumerator.Enumerate().FirstOrDefault(d => d.VendorId == 0x1462 && d.FeatureLength >= 185);
            if (info == null) { Log.Warn("MSI test: eski MSI kontrolcüsü yok."); return; }
            using (var hid = HidDevice.Open(info))
            {
                var orig = hid.GetFeature(0x52);
                if (orig == null || orig.Length < 185 || orig[0] != 0x52) { Log.Warn("MSI test: 0x52 okunamadı, hiçbir şey yazılmadı."); return; }
                Log.Info("MSI test öncesi 0x52: " + BitConverter.ToString(orig, 0, 185));
                try
                {
                    foreach (var (name, c) in new[] { ("yeşil", new Rgb(0, 255, 0)), ("kırmızı", new Rgb(255, 0, 0)), ("mavi", new Rgb(0, 0, 255)) })
                    {
                        var p = (byte[])orig.Clone();
                        foreach (int o in LegacyRainbowOffsets)
                        {
                            p[o] = 0x01; // static
                            p[o + 1] = c.R; p[o + 2] = c.G; p[o + 3] = c.B;
                            p[o + 5] = c.R; p[o + 6] = c.G; p[o + 7] = c.B;
                        }
                        bool ok = hid.SetFeature(p);
                        var back = hid.GetFeature(0x52);
                        Log.Info($"MSI test {name}: yazma {(ok ? "tamam" : "BAŞARISIZ")}, geri okunan JRAINBOW1 = {(back == null ? "-" : BitConverter.ToString(back, 31, 11))}");
                        System.Threading.Thread.Sleep(6000);
                    }
                }
                finally
                {
                    Log.Info("MSI test: eski ayar geri yazıldı: " + (hid.SetFeature(orig) ? "tamam" : "BAŞARISIZ"));
                }
            }
        }

        /// <summary>
        /// Older boards (VID 0x1462, PID = board number) use the 0x52 zone-config protocol. Verified boards get a driver;
        /// for any other one the report sizes and current config are only read and logged, so a driver can be written
        /// against real data.
        /// </summary>
        static IEnumerable<RgbDevice> DetectLegacy(DetectContext ctx)
        {
            var found = new List<RgbDevice>();
            foreach (var info in ctx.HidDevices.Where(d => d.VendorId == 0x1462 && d.FeatureLength >= 112))
            {
                HidDevice hid = null;
                try
                {
                    hid = HidDevice.Open(info);
                    var cfg = hid.GetFeature(0x52);
                    int size = LegacySize(info.FeatureLength);
                    bool layoutOk = cfg != null && cfg.Length >= size && cfg[0] == 0x52;
                    if (layoutOk && LegacyPids.Contains(info.ProductId))
                    {
                        found.Add(new MsiMysticLightLegacy(hid, ctx.System.BoardProduct, size, info.ProductId == LegacyVerified));
                        hid = null; // owned by the device now
                        continue;
                    }
                    Log.Info($"MSI eski kontrolcü 1462:{info.ProductId:X4}: feature {info.FeatureLength} bayt, henüz sürücü yok (sadece okundu). 0x52: "
                        + (cfg == null ? "okunamadı" : BitConverter.ToString(cfg, 0, Math.Min(cfg.Length, 185))));
                }
                catch (Exception ex) { Log.Error("MSI eski kontrolcü", ex); }
                finally { hid?.Dispose(); }
            }
            return found;
        }
    }

    public sealed class MsiMysticLight : RgbDevice
    {
        sealed class BoardLayout
        {
            public int ArgbHeaders = 3;
            public bool EzConnector;
            /// <summary>Onboard zones in the order their colors are concatenated: (MSI zone name, display name, LED count).</summary>
            public List<Tuple<string, string, int>> Onboard = new List<Tuple<string, string, int>>();
        }

        // Board tables come from the board's own zone layout (LED counts are not reported by the firmware).
        static readonly Dictionary<string, BoardLayout> Boards = new Dictionary<string, BoardLayout>(StringComparer.OrdinalIgnoreCase)
        {
            ["MPG X870E CARBON WIFI"] = new BoardLayout
            {
                ArgbHeaders = 3, EzConnector = true,
                Onboard = { Tuple.Create("JPIPE1", "I/O kalkanı", 10), Tuple.Create("JPIPE3", "Carbon M.2", 6), Tuple.Create("JRGB1", "12V RGB 1", 1) }
            },
            ["MPG X870E CARBON MAX WIFI"] = new BoardLayout
            {
                ArgbHeaders = 3, EzConnector = true,
                Onboard = { Tuple.Create("JPIPE1", "I/O kalkanı", 10), Tuple.Create("JPIPE3", "Carbon M.2", 6), Tuple.Create("JRGB1", "12V RGB 1", 1) }
            },
        };

        static readonly string[] ZoneOrder =
        {
            "JARGB1", "JARGB2", "JARGB3", "JAF", "JPIPE1", "JPIPE2", "JPIPE3", "JPIPE4", "JPIPE5",
            "JRGB1", "JRGB2", "Onboard1", "Onboard2", "Onboard3", "Onboard4", "Onboard5", "Onboard6", "SelectAll"
        };

        readonly HidDevice _hid;
        readonly BoardLayout _layout;
        readonly List<RgbZone> _headers = new List<RgbZone>();
        RgbZone _ez;
        readonly List<RgbZone> _onboard = new List<RgbZone>();

        public override string[] ConflictingProcesses => new[] { "MSI_Central_Service", "Mystic_Light_Service", "LEDKeeper2", "LightKeeperService", "MSI.CentralServer", "MSI Center", "DCv2" };

        MsiMysticLight(HidDevice hid, string board, BoardLayout layout)
        {
            _hid = hid;
            _layout = layout;
            Id = HidSelect.Id(hid.Info);
            Name = "MSI " + board;
            Vendor = "MSI";
            Kind = DeviceKind.Motherboard;
            MaxFps = 40;

            for (int i = 0; i < layout.ArgbHeaders; i++)
            {
                var z = new RgbZone($"ARGB Header {i + 1}", 0, resizable: true, maxLeds: 240);
                _headers.Add(z); Zones.Add(z);
            }
            if (layout.EzConnector) { _ez = new RgbZone("EZ Connector 1", 0, resizable: true, maxLeds: 240); Zones.Add(_ez); }
            foreach (var o in layout.Onboard) { var z = new RgbZone(o.Item2, o.Item3); _onboard.Add(z); Zones.Add(z); }
        }

        public static MsiMysticLight TryCreate(HidDeviceInfo info, SystemInfo sys)
        {
            var hid = HidDevice.Open(info);
            // Boards that accept a 0x51 feature report speak the new protocol.
            if (!hid.SetFeature(new byte[] { 0x51 }))
            {
                Log.Warn($"MSI {info.ProductId:X4}: eski 185 bayt protokol, bu sürümde desteklenmiyor.");
                hid.Dispose();
                return null;
            }

            string board = sys.BoardProduct;
            string key = board;
            int paren = key.IndexOf(" (MS-", StringComparison.Ordinal);
            if (paren > 0) key = key.Substring(0, paren);
            if (!Boards.TryGetValue(key, out var layout))
            {
                Log.Warn($"MSI: '{board}' tablosu yok, yalnızca ARGB başlıkları kullanılacak.");
                layout = new BoardLayout { ArgbHeaders = 3, EzConnector = false };
            }
            return new MsiMysticLight(hid, board, layout);
        }

        public override void Initialize()
        {
            var zones = _hid.GetFeature(0x50);
            if (zones != null)
            {
                for (int z = 0; z < 18; z++)
                {
                    int o = z * 16 + 1;
                    if (o + 15 < zones.Length && zones[o] != 0)
                        Log.Info($"MSI bölge {ZoneOrder[z]}: mod {zones[o]}, {zones[o + 15]} LED");
                }
            }
            _hid.SetFeature(BuildZoneConfig());
            Status = "Doğrudan kontrol";
        }

        byte[] BuildZoneConfig()
        {
            var p = new byte[290];
            p[0] = 0x50;
            var enabled = new HashSet<string>(_layout.Onboard.Select(o => o.Item1));
            for (int z = 0; z < 18; z++)
            {
                int o = z * 16 + 1;
                // ARGB headers, JAF and "select all" are always in direct (0x09) mode; onboard zones only when the board has them.
                bool on = z <= 3 || z == 17 || enabled.Contains(ZoneOrder[z]);
                if (!on) continue;
                p[o + 0] = 0x09;
                p[o + 1] = 0xFF; p[o + 5] = 0xFF;
                p[o + 9] = 0xFF; p[o + 10] = 0xFF; p[o + 11] = 0xFF; p[o + 12] = 0xFF;
                p[o + 13] = 0x03;
                p[o + 14] = (byte)(z <= 3 ? 0x15 : 0x95);
                p[o + 15] = (byte)(z <= 3 ? 0x78 : 0x1E);
            }
            return p;
        }

        public override void Apply()
        {
            for (int h = 0; h < _headers.Count; h++)
                if (_headers[h].LedCount > 0) Send(0x04, (byte)h, _headers[h].Colors);
            if (_ez != null && _ez.LedCount > 0) Send(0x08, 0, _ez.Colors);
            if (_onboard.Count > 0) Send(0x06, 0, _onboard.SelectMany(z => z.Colors).ToArray());
        }

        void Send(byte target, byte index, Rgb[] colors)
        {
            int n = Math.Min(colors.Length, 240);
            var p = new byte[7 + n * 3];
            p[0] = 0x51; p[1] = 0x09; p[2] = target; p[3] = index; p[6] = (byte)n;
            for (int i = 0; i < n; i++)
            {
                p[7 + i * 3] = colors[i].R;
                p[8 + i * 3] = colors[i].G;
                p[9 + i * 3] = colors[i].B;
            }
            if (!_hid.SetFeature(p)) { Status = "Yazma hatası"; return; }
            _hid.GetFeature(0x51); // the board expects the reply to be read back before the next packet
            Status = "Doğrudan kontrol";
        }

        public override void Dispose() => _hid.Dispose();
    }

    /// <summary>
    /// Older MSI boards (0x52 zone config, e.g. PRO B650M-A). The ARGB headers take one static color each, not per-LED colors,
    /// so each header is a single-LED zone. Only the effect and color bytes of those headers are changed; the rest of the
    /// board's own config is written back as read, and nothing is saved to flash.
    /// </summary>
    public sealed class MsiMysticLightLegacy : RgbDevice
    {
        readonly HidDevice _hid;
        readonly int _size;
        readonly int[] _offsets;
        readonly string _ok;
        byte[] _base;
        Rgb[] _last;

        public override string[] ConflictingProcesses => new[] { "MSI_Central_Service", "Mystic_Light_Service", "LEDKeeper2", "LightKeeperService", "MSI.CentralServer", "MSI Center", "DCv2" };

        internal MsiMysticLightLegacy(HidDevice hid, string board, int size, bool verified)
        {
            _hid = hid;
            _size = size;
            Id = HidSelect.Id(hid.Info);
            Name = "MSI " + board;
            Vendor = "MSI";
            Kind = DeviceKind.Motherboard;
            MaxFps = 10; // a whole config report per change; the board does not need more for fans
            var layout = MsiMysticLightProvider.LegacyLayout(size);
            _offsets = layout.Select(l => l.offset).ToArray();
            foreach (var l in layout) Zones.Add(new RgbZone(l.name, 1));
            _ok = "Doğrudan kontrol (başlık başına tek renk)" + (verified ? "" : " · deneysel");
            Log.Info($"MSI eski protokol {hid.Info.ProductId:X4}: {size} bayt düzen" + (verified ? "" : " (bu kartta denenmedi)"));
        }

        public override void Initialize()
        {
            // Read again here: MSI's service may have changed the config since detection.
            var cfg = _hid.GetFeature(0x52);
            if (cfg != null && cfg.Length >= _size && cfg[0] == 0x52) _base = cfg;
            if (_base == null) { Status = "Ayar okunamadı"; return; }
            _base[_size - 1] = 0; // save flag: never write the config to flash
            _last = null;
            Status = _ok;
        }

        public override void Apply()
        {
            if (_base == null) return;
            var colors = Zones.Select(z => z.Colors[0]).ToArray();
            if (_last != null && colors.SequenceEqual(_last)) return;

            var p = (byte[])_base.Clone();
            for (int i = 0; i < colors.Length; i++)
            {
                int o = _offsets[i];
                var c = colors[i];
                p[o] = 0x01; // static
                p[o + 1] = c.R; p[o + 2] = c.G; p[o + 3] = c.B;
                p[o + 5] = c.R; p[o + 6] = c.G; p[o + 7] = c.B;
            }
            if (_hid.SetFeature(p)) { _last = colors; Status = _ok; }
            else Status = "Yazma hatası";
        }

        public override void Dispose() => _hid.Dispose();
    }
}
