using System;
using System.Collections.Generic;
using System.Linq;
using RgbStone.Core;
using RgbStone.Transport;

namespace RgbStone.Drivers
{
    /// <summary>
    /// Thermaltake Riing Plus (264A:1FA5-1FB5) and Riing Quad (2260-2270, 232B) fan controllers: five ports, per-LED colors in
    /// G-R-B order. The Quad drops back to its own effect unless it hears from the host every second. Protocol reference:
    /// OpenRGB. Experimental: no Thermaltake controller on the test PC.
    /// </summary>
    public sealed class ThermaltakeRiingProvider : IDeviceProvider
    {
        public string Name => "Thermaltake Riing Plus / Quad";

        public IEnumerable<RgbDevice> Detect(DetectContext ctx)
        {
            foreach (var g in HidSelect.Physical(ctx.HidDevices.Where(d => d.VendorId == 0x264A && IsRiing(d.ProductId))))
            {
                var info = g.OrderByDescending(d => d.OutputLength).First();
                RgbDevice dev = null;
                try { dev = new ThermaltakeRiing(HidDevice.Open(info), IsQuad(info.ProductId)); }
                catch (Exception ex) { Log.Error("Thermaltake", ex); }
                if (dev != null) yield return dev;
            }
        }

        static bool IsQuad(ushort pid) => (pid >= 0x2260 && pid <= 0x2270) || pid == 0x232B;
        static bool IsRiing(ushort pid) => (pid >= 0x1FA5 && pid <= 0x1FB5) || IsQuad(pid);
    }

    public sealed class ThermaltakeRiing : RgbDevice
    {
        readonly HidDevice _hid;
        readonly bool _quad;
        readonly int _packet;
        readonly byte[][] _last = new byte[5][];
        DateTime _keepalive;
        const string Ok = "Doğrudan kontrol · deneysel";

        public override string[] ConflictingProcesses => new[] { "TT RGB Plus", "TTRGBPlus", "ThermaltakeRGBPlus", "TT RGB PLUS 2.0" };

        internal ThermaltakeRiing(HidDevice hid, bool quad)
        {
            _hid = hid; _quad = quad;
            _packet = quad ? 193 : 65;
            Id = HidSelect.Id(hid.Info);
            Name = quad ? "Thermaltake Riing Quad" : "Thermaltake Riing Plus";
            Vendor = "Thermaltake";
            Kind = DeviceKind.LedController;
            MaxFps = 20;
            int max = (_packet - 5) / 3;
            for (int i = 0; i < 5; i++)
                Zones.Add(new RgbZone($"Port {i + 1}", 0, resizable: true, maxLeds: max) { DefaultLeds = quad ? 54 : 12 });
        }

        public override void Initialize()
        {
            var p = new byte[_packet];
            p[1] = 0xFE; p[2] = 0x33; // init
            _hid.Write(p);
            _hid.Read(250);
            for (int i = 0; i < 5; i++) _last[i] = null;
            Status = Ok;
        }

        public override void Apply()
        {
            bool resend = DateTime.Now >= _keepalive;
            bool ok = true;
            for (int port = 0; port < 5; port++)
            {
                var c = Zones[port].Colors;
                if (c.Length == 0) continue;
                var p = new byte[_packet];
                p[1] = 0x32; p[2] = 0x52; p[3] = (byte)(port + 1);
                p[4] = (byte)(_quad ? 0x24 : 0x18); // per-LED direct mode, fastest speed bits = 0
                for (int i = 0; i < c.Length; i++) { p[5 + i * 3] = c[i].G; p[6 + i * 3] = c[i].R; p[7 + i * 3] = c[i].B; }
                if (!resend && _last[port] != null && _last[port].SequenceEqual(p)) continue;
                bool sent = _hid.Write(p);
                if (!_quad) _hid.Read(100); // the Riing Plus answers every packet
                ok &= sent;
                if (sent) _last[port] = p;
            }
            if (resend) _keepalive = DateTime.Now.AddSeconds(1);
            Status = ok ? Ok : "Yazma hatası";
        }

        public override void Dispose() => _hid.Dispose();
    }
}
