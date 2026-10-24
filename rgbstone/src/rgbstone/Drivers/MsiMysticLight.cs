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
            ProbeLegacy(ctx);
        }

        /// <summary>
        /// Older boards (VID 0x1462, PID = board number, e.g. 7D77) use the 185/162-byte protocol, which has no driver yet.
        /// Read-only: logs the report sizes and the current zone config (feature report 0x52) so a driver can be written
        /// against real data. Nothing is written to the board.
        /// </summary>
        // Legacy 0x52 config, as read from a PRO B650M-A (1462:7D77): report id, then 10-byte zones (effect, RGB, flags,
        // RGB2, flags, pad); JRAINBOW1/2 are 11 bytes (plus LED count) at offsets 31 and 42.
        static readonly int[] LegacyRainbowOffsets = { 31, 42 };

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

        static void ProbeLegacy(DetectContext ctx)
        {
            foreach (var info in ctx.HidDevices.Where(d => d.VendorId == 0x1462))
            {
                try
                {
                    Log.Info($"MSI eski kontrolcü 1462:{info.ProductId:X4} usage {info.UsagePage:X4}/{info.Usage:X4}: feature {info.FeatureLength}, input {info.InputLength}, output {info.OutputLength} bayt (henüz sürücü yok, sadece okundu)");
                    if (info.FeatureLength <= 0) continue;
                    using (var hid = HidDevice.Open(info))
                    {
                        var cfg = hid.GetFeature(0x52);
                        Log.Info(cfg == null ? "MSI eski kontrolcü: 0x52 okunamadı." : "MSI eski kontrolcü 0x52: " + BitConverter.ToString(cfg));
                    }
                }
                catch (Exception ex) { Log.Error("MSI eski kontrolcü", ex); }
            }
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
}
