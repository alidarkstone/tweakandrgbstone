using System;
using System.Collections.Generic;
using System.Linq;
using RgbStone.Core;
using RgbStone.Transport;

namespace RgbStone.Drivers
{
    /// <summary>
    /// Wooting keyboards with V2 (usage page 0x1337) or V2.12+ (0xFF55) firmware: one raw-colors report carrying the full
    /// 6 × 21 key grid in RGB565. Smaller boards ignore the columns they do not have. Protocol reference: OpenRGB.
    /// Experimental: no Wooting keyboard on the test PC.
    /// </summary>
    public sealed class WootingProvider : IDeviceProvider
    {
        public string Name => "Wooting klavyeler";

        public IEnumerable<RgbDevice> Detect(DetectContext ctx)
        {
            var list = ctx.HidDevices.Where(d => (d.VendorId == 0x31E3 || d.VendorId == 0x03EB) && (d.UsagePage == 0x1337 || d.UsagePage == 0xFF55));
            foreach (var g in HidSelect.Physical(list))
            {
                // the 2012+ protocol wins when a board exposes both pages; the old Atmel id (V1 firmware) is not supported
                var info = g.FirstOrDefault(d => d.UsagePage == 0xFF55) ?? g.First();
                if (info.VendorId == 0x03EB) { Log.Warn("Wooting: V1 yazılımı desteklenmiyor, klavye yazılımını güncelle."); continue; }
                RgbDevice dev = null;
                try { dev = new WootingKeyboard(HidDevice.Open(info), info.UsagePage == 0xFF55); }
                catch (Exception ex) { Log.Error("Wooting", ex); }
                if (dev != null) yield return dev;
            }
        }
    }

    public sealed class WootingKeyboard : RgbDevice
    {
        const int Rows = 6, Cols = 21;
        readonly HidDevice _hid;
        readonly bool _v3;
        byte[] _last;
        const string Ok = "Doğrudan kontrol · deneysel";

        public override string[] ConflictingProcesses => new[] { "wootility", "Wootility" };

        internal WootingKeyboard(HidDevice hid, bool v3)
        {
            _hid = hid; _v3 = v3;
            Id = HidSelect.Id(hid.Info);
            string product = string.IsNullOrEmpty(hid.Info.Product) ? "Wooting" : hid.Info.Product;
            Name = product.StartsWith("Wooting", StringComparison.OrdinalIgnoreCase) ? product : "Wooting " + product;
            Vendor = "Wooting";
            Kind = DeviceKind.Keyboard;
            MaxFps = 30;
            var x = Enumerable.Range(0, Rows * Cols).Select(i => (double)(i % Cols) / (Cols - 1)).ToArray();
            Zones.Add(new RgbZone("Tuşlar", Rows * Cols, x: x));
        }

        public override void Initialize()
        {
            _hid.SetFeature(new byte[] { 0x00, 0xD0, 0xDA, 33, 0, 0, 0, 0 }); // color init
            _last = null;
            Status = Ok;
        }

        public override void Apply()
        {
            var c = Zones[0].Colors;
            var p = new byte[_v3 ? 2046 : 257];
            p[0] = (byte)(_v3 ? 0x04 : 0x00); p[1] = (byte)(_v3 ? 0xD1 : 0xD0); p[2] = 0xDA; p[3] = 11; // raw colors
            for (int i = 0; i < c.Length; i++)
            {
                int v = (c[i].R & 0xF8) << 8 | (c[i].G & 0xFC) << 3 | c[i].B >> 3;
                p[4 + i * 2] = (byte)v; p[5 + i * 2] = (byte)(v >> 8);
            }
            if (_last != null && _last.SequenceEqual(p)) return;
            bool ok = _hid.Write(p);
            if (ok) _last = p;
            Status = ok ? Ok : "Yazma hatası";
        }

        public override void Dispose() => _hid.Dispose();
    }
}
