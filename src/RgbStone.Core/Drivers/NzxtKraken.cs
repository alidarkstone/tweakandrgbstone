using System;
using System.Collections.Generic;
using System.Linq;
using RgbStone.Core;
using RgbStone.Transport;

namespace RgbStone.Drivers
{
    /// <summary>
    /// NZXT Kraken X42/X52/X62/X72 and M22 (2nd generation): logo + 8-LED pump ring, set with the "fixed" effect, which
    /// takes one color per LED. Protocol reference: OpenRGB. Experimental: no Kraken on the test PC.
    /// </summary>
    public sealed class NzxtKrakenProvider : IDeviceProvider
    {
        public string Name => "NZXT Kraken X/M (2. nesil)";

        public IEnumerable<RgbDevice> Detect(DetectContext ctx)
        {
            foreach (var g in HidSelect.Physical(HidSelect.ByVidPid(ctx, 0x1E71, new ushort[] { 0x170E, 0x1715 })))
            {
                var info = g.OrderByDescending(d => d.OutputLength).First();
                RgbDevice dev = null;
                try { dev = new NzxtKraken(HidDevice.Open(info)); }
                catch (Exception ex) { Log.Error("NZXT Kraken", ex); }
                if (dev != null) yield return dev;
            }
        }
    }

    public sealed class NzxtKraken : RgbDevice
    {
        readonly HidDevice _hid;
        byte[] _last;

        public override string[] ConflictingProcesses => new[] { "NZXT CAM", "CAM" };

        internal NzxtKraken(HidDevice hid)
        {
            _hid = hid;
            Id = HidSelect.Id(hid.Info);
            Name = hid.Info.ProductId == 0x1715 ? "NZXT Kraken M22" : "NZXT Kraken X";
            Vendor = "NZXT";
            Kind = DeviceKind.Cooler;
            MaxFps = 20;
            Zones.Add(new RgbZone("Logo", 1));
            Zones.Add(new RgbZone("Pompa halkası", 8));
        }

        public override void Initialize() { _last = null; Status = "Doğrudan kontrol · deneysel"; }

        public override void Apply()
        {
            var p = new byte[64];
            p[0] = 0x02; p[1] = 0x4C;
            p[2] = 0x00;   // both channels
            p[3] = 0x00;   // fixed: one color per LED
            p[4] = 0x02;   // normal speed (unused by fixed)
            var logo = Zones[0].Colors[0];
            p[5] = logo.G; p[6] = logo.R; p[7] = logo.B; // the logo LED is wired G-R-B
            var ring = Zones[1].Colors;
            for (int i = 0; i < 8; i++) { p[8 + i * 3] = ring[i].R; p[9 + i * 3] = ring[i].G; p[10 + i * 3] = ring[i].B; }
            if (_last != null && _last.SequenceEqual(p)) return;
            bool ok = _hid.Write(p);
            if (ok) _last = p;
            Status = ok ? "Doğrudan kontrol · deneysel" : "Yazma hatası";
        }

        public override void Dispose() => _hid.Dispose();
    }
}
