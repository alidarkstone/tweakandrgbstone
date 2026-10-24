using System;
using System.Collections.Generic;
using System.Linq;
using RgbStone.Core;
using RgbStone.Transport;

namespace RgbStone.Drivers
{
    /// <summary>
    /// ASUS Aura USB motherboard controllers (0B05:18F3/19AF/1939/1BED/1AA6): onboard LEDs, 12V headers and addressable headers.
    /// Not tested on real hardware yet, so devices report themselves as experimental.
    /// </summary>
    public sealed class AsusAuraProvider : IDeviceProvider
    {
        public string Name => "ASUS Aura anakart";
        static readonly ushort[] Pids = { 0x18F3, 0x19AF, 0x1939, 0x1BED, 0x1AA6 };

        public IEnumerable<RgbDevice> Detect(DetectContext ctx)
        {
            foreach (var g in HidSelect.Physical(HidSelect.ByVidPid(ctx, 0x0B05, Pids)))
            {
                var info = g.FirstOrDefault(d => d.Interface == 2) ?? g.FirstOrDefault(d => d.OutputLength >= 65);
                if (info == null) continue;
                RgbDevice dev;
                try { dev = new AsusAura(HidDevice.Open(info), ctx.System.BoardProduct); }
                catch (Exception ex) { Log.Error("ASUS", ex); dev = new UnavailableDevice(HidSelect.Id(info), "ASUS Aura " + ctx.System.BoardProduct, "ASUS", DeviceKind.Motherboard, ex.Message); }
                yield return dev;
            }
        }
    }

    public sealed class AsusAura : RgbDevice
    {
        const byte Cmd = 0xEC, SetMode = 0x35, Config = 0xB0, Direct = 0x40, Firmware = 0x82;
        const int MaxPerChannel = 120, PerPacket = 20;

        // Boards whose config table is known to be wrong: (mainboard LEDs, ARGB channels, 12V headers)
        static readonly Dictionary<string, (int main, int argb, int rgb12)> Overrides = new Dictionary<string, (int, int, int)>(StringComparer.OrdinalIgnoreCase)
        {
            ["AULA3-AR32-0207"] = (3, 3, 1), ["AULA3-AR32-0213"] = (2, 3, 1), ["AULA3-AR32-0218"] = (5, 3, 1),
            ["ROG CROSSHAIR VIII HERO"] = (8, 2, 2), ["ROG MAXIMUS Z690 EXTREME"] = (7, 3, 1), ["TUF GAMING X570-PRO (WI-FI)"] = (3, 1, 2),
            ["PRIME Z790-A WIFI"] = (4, 3, 1), ["ROG STRIX B650-A GAMING WIFI"] = (3, 3, 1), ["ROG STRIX B650E-F GAMING WIFI"] = (3, 3, 1),
            ["ROG MAXIMUS Z790 APEX ENCORE"] = (2, 3, 1), ["ROG STRIX B760-F GAMING WIFI"] = (2, 3, 1), ["ROG STRIX Z890-E GAMING WIFI"] = (3, 3, 1),
            ["TUF GAMING Z890-PLUS WIFI"] = (1, 3, 0), ["ROG STRIX Z890-A GAMING WIFI"] = (2, 3, 0), ["PRIME Z890-P WIFI"] = (0, 3, 1),
            ["ROG STRIX B850-F GAMING WIFI"] = (2, 3, 2),
        };

        readonly HidDevice _hid;
        readonly string _board;
        RgbZone _main;
        readonly List<RgbZone> _headers12 = new List<RgbZone>();
        readonly List<RgbZone> _argb = new List<RgbZone>();

        public override string[] ConflictingProcesses => new[] { "ArmouryCrate", "ArmouryCrateService", "LightingService", "AuraService" };

        public AsusAura(HidDevice hid, string board)
        {
            _hid = hid;
            _board = board;
            Id = HidSelect.Id(hid.Info);
            Name = "ASUS " + (string.IsNullOrEmpty(board) ? "Aura" : board);
            Vendor = "ASUS";
            Kind = DeviceKind.Motherboard;
            MaxFps = 30;
        }

        byte[] Ask(params byte[] cmd)
        {
            _hid.FlushInput();
            if (!_hid.Write(Pad(cmd))) return null;
            for (int i = 0; i < 5; i++)
            {
                var r = _hid.Read(200);
                if (r == null) return null;
                if (r.Length > 2 && r[0] == Cmd) return r;
            }
            return null;
        }

        static byte[] Pad(byte[] p) { var b = new byte[65]; Array.Copy(p, b, Math.Min(p.Length, 65)); return b; }

        public override void Initialize()
        {
            string model = "";
            var fw = Ask(Cmd, Firmware);
            if (fw != null && fw[1] == 0x02) model = new string(fw.Skip(2).Take(16).Select(b => (char)b).ToArray()).Trim('\0', ' ');

            int main = 0, argb = 0, rgb12 = 0;
            var cfg = Ask(Cmd, Config);
            if (Overrides.TryGetValue(model, out var o) || Overrides.TryGetValue(_board ?? "", out o)) (main, argb, rgb12) = o;
            else if (cfg != null && cfg[1] == 0x30 && cfg.Length > 33)
            {
                argb = cfg[6] == 4 ? 3 : cfg[6];
                if (cfg[33] == 3) { main = cfg[31] > 0 ? cfg[31] - 1 : 0; rgb12 = 1; }
                else { main = Math.Max(0, cfg[31] - cfg[33]); rgb12 = cfg[33]; }
            }
            else { argb = 3; Log.Warn($"{Name}: yapılandırma tablosu okunamadı, 3 ARGB başlık varsayıldı."); }
            Log.Info($"{Name}: model '{model}', anakart {main} LED, ARGB {argb}, 12V {rgb12}");

            Zones.Clear(); _headers12.Clear(); _argb.Clear();
            if (main > 0) { _main = new RgbZone("Anakart", main); Zones.Add(_main); } else _main = null;
            for (int i = 0; i < rgb12; i++) { var z = new RgbZone($"12V RGB {i + 1}", 1); _headers12.Add(z); Zones.Add(z); }
            for (int i = 0; i < argb; i++) { var z = new RgbZone($"Channel {i + 1}", 0, resizable: true, maxLeds: MaxPerChannel); _argb.Add(z); Zones.Add(z); }

            _hid.Write(Pad(new byte[] { Cmd, 0x52, 0x53, 0x00, 0x01 }));          // leave the gen-2 effect engine
            for (int ch = 0; ch <= argb; ch++) _hid.Write(Pad(new byte[] { Cmd, SetMode, (byte)ch, 0x00, 0x00, 0xFF })); // direct mode
            Status = "Doğrudan kontrol · deneysel";
        }

        public override void Apply()
        {
            // Onboard LEDs and 12V headers stream together on direct channel 4.
            var mainColors = (_main?.Colors ?? new Rgb[0]).Concat(_headers12.Select(z => z.Colors[0])).ToArray();
            bool ok = true;
            if (mainColors.Length > 0) ok &= Stream(4, mainColors);
            for (int i = 0; i < _argb.Count; i++)
                if (_argb[i].LedCount > 0) ok &= Stream((byte)i, _argb[i].Colors);
            Status = ok ? "Doğrudan kontrol · deneysel" : "Yazma hatası";
        }

        bool Stream(byte channel, Rgb[] colors)
        {
            int n = Math.Min(colors.Length, MaxPerChannel), sent = 0;
            bool ok = true;
            while (sent < n)
            {
                int count = Math.Min(PerPacket, n - sent);
                bool apply = sent + count >= n;
                var p = new byte[65];
                p[0] = Cmd; p[1] = Direct; p[2] = (byte)(apply ? 0x80 | channel : channel); p[3] = (byte)sent; p[4] = (byte)count;
                for (int i = 0; i < count; i++)
                {
                    var c = colors[sent + i];
                    p[5 + i * 3] = c.R; p[6 + i * 3] = c.G; p[7 + i * 3] = c.B;
                }
                ok &= _hid.Write(p);
                sent += count;
            }
            return ok;
        }

        public override void Dispose() => _hid.Dispose();
    }
}
