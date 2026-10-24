using System;
using System.Collections.Generic;
using System.Linq;
using RgbStone.Core;
using RgbStone.Transport;

namespace RgbStone.Drivers
{
    /// <summary>Gigabyte RGB Fusion 2 USB motherboard controllers (ITE 048D:5702 / 8297). Experimental.</summary>
    public sealed class GigabyteFusionProvider : IDeviceProvider
    {
        public string Name => "Gigabyte RGB Fusion 2";

        public IEnumerable<RgbDevice> Detect(DetectContext ctx)
        {
            foreach (var info in ctx.HidDevices.Where(d => d.VendorId == 0x048D && (d.ProductId == 0x5702 || d.ProductId == 0x8297) && d.Usage == 0x00CC).Take(1))
            {
                RgbDevice dev;
                try { dev = new GigabyteFusion(HidDevice.Open(info), ctx.System.BoardProduct); }
                catch (Exception ex) { Log.Error("Gigabyte", ex); dev = new UnavailableDevice(HidSelect.Id(info), "Gigabyte " + ctx.System.BoardProduct, "Gigabyte", DeviceKind.Motherboard, ex.Message); }
                yield return dev;
            }
        }
    }

    public sealed class GigabyteFusion : RgbDevice
    {
        const byte Cmd = 0xCC, Commit = 0x28, SoftwareMode = 0x32, ArgbCounts = 0x34, ConfigTable = 0x60;
        readonly HidDevice _hid;
        readonly List<(byte id, RgbZone zone, bool header12v)> _onboard = new List<(byte, RgbZone, bool)>();
        readonly List<(byte id, RgbZone zone)> _argb = new List<(byte, RgbZone)>();
        int _countsSent = -1;

        public override string[] ConflictingProcesses => new[] { "RGBFusion", "GCC", "GigabyteControlCenter" };

        public GigabyteFusion(HidDevice hid, string board)
        {
            _hid = hid;
            Id = HidSelect.Id(hid.Info);
            Name = "Gigabyte " + (string.IsNullOrEmpty(board) ? "RGB Fusion" : board);
            Vendor = "Gigabyte";
            Kind = DeviceKind.Motherboard;
            MaxFps = 30;
            string[] names = { "LED 1", "12V başlık 1", "LED 2", "LED 3", "12V başlık 2", "LED 4", "LED 5", "LED 6" };
            for (int i = 0; i < 8; i++)
            {
                var z = new RgbZone(names[i], 1);
                _onboard.Add(((byte)(0x20 + i), z, i == 1 || i == 4));
                Zones.Add(z);
            }
            for (int i = 0; i < 2; i++)
            {
                var z = new RgbZone($"5V ARGB {i + 1}", 0, resizable: true, maxLeds: 120);
                _argb.Add(((byte)(0x58 + i), z));
                Zones.Add(z);
            }
        }

        bool Send(params byte[] p)
        {
            var b = new byte[64];
            Array.Copy(p, b, Math.Min(p.Length, 64));
            return _hid.SetFeature(b);
        }

        public override void Initialize()
        {
            Send(Cmd, ConfigTable);
            var cfg = _hid.GetFeature(Cmd);
            if (cfg != null && cfg.Length > 40)
                Log.Info($"{Name}: yazılım {cfg[4]}.{cfg[5]}.{cfg[6]}.{cfg[7]}, '{new string(cfg.Skip(12).Take(28).Select(c => (char)c).ToArray()).Trim('\0', ' ')}'");
            Send(Cmd, SoftwareMode, 1 | 2);
            _countsSent = -1;
            Status = "Doğrudan kontrol · deneysel";
        }

        static int CountEnum(int n) => n <= 32 ? 0 : n <= 64 ? 1 : n <= 256 ? 2 : n <= 512 ? 3 : 4;

        public override void Apply()
        {
            bool ok = true;
            foreach (var (id, zone, is12v) in _onboard)
            {
                var c = zone.Colors[0];
                var p = new byte[17];
                p[0] = Cmd; p[1] = id; p[2] = (byte)(1 << (id - 0x20)); p[0x0B] = 1; p[0x0C] = 0x5A;
                // onboard LEDs are BGR, 12V headers RGB
                if (is12v) { p[0x0E] = c.R; p[0x0F] = c.G; p[0x10] = c.B; }
                else { p[0x0E] = c.B; p[0x0F] = c.G; p[0x10] = c.R; }
                ok &= Send(p);
                Send(Cmd, Commit, 0xFF);
            }

            int mask = CountEnum(_argb[0].zone.LedCount) | (CountEnum(_argb[1].zone.LedCount) << 4);
            if (mask != _countsSent) { Send(Cmd, ArgbCounts, (byte)mask); _countsSent = mask; }

            foreach (var (id, zone) in _argb)
            {
                var colors = zone.Colors;
                for (int start = 0; start < colors.Length; start += 19)
                {
                    int n = Math.Min(19, colors.Length - start);
                    var p = new byte[5 + n * 3];
                    p[0] = Cmd; p[1] = id; p[2] = (byte)(start * 3); p[3] = (byte)((start * 3) >> 8); p[4] = (byte)(n * 3);
                    for (int i = 0; i < n; i++)
                    {
                        var c = colors[start + i]; // ARGB headers are GRB
                        p[5 + i * 3] = c.G; p[6 + i * 3] = c.R; p[7 + i * 3] = c.B;
                    }
                    ok &= Send(p);
                }
            }
            Status = ok ? "Doğrudan kontrol · deneysel" : "Yazma hatası";
        }

        public override void Dispose() => _hid.Dispose();
    }
}
