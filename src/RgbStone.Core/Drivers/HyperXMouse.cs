using System;
using System.Collections.Generic;
using System.Linq;
using RgbStone.Core;
using RgbStone.Transport;

namespace RgbStone.Drivers
{
    /// <summary>
    /// HyperX Pulsefire mice (Kingston 0x0951 and HP 0x03F0 ids). Direct-color packets per model; protocol reference: OpenRGB.
    /// Experimental: no HyperX mouse on the test PC.
    /// </summary>
    public sealed class HyperXMouseProvider : IDeviceProvider
    {
        public string Name => "HyperX Pulsefire fareler";

        internal enum Model { Haste, FpsPro, Raid, Surge, Dart }

        // (vid, pid, interface, usage page, model, name)
        static readonly (ushort vid, ushort pid, int iface, ushort page, Model model, string name)[] Table =
        {
            (0x0951, 0x16D3, 1, 0xFF01, Model.Surge, "HyperX Pulsefire Surge"),
            (0x03F0, 0x0490, 1, 0xFF01, Model.Surge, "HyperX Pulsefire Surge"),
            (0x0951, 0x16D7, 1, 0xFF01, Model.FpsPro, "HyperX Pulsefire FPS Pro"),
            (0x0951, 0x16DE, 1, 0xFF01, Model.FpsPro, "HyperX Pulsefire Core"),
            (0x03F0, 0x0D8F, 1, 0xFF01, Model.FpsPro, "HyperX Pulsefire Core"),
            (0x0951, 0x16E1, 2, 0xFF00, Model.Dart, "HyperX Pulsefire Dart"),
            (0x03F0, 0x068E, 2, 0xFF00, Model.Dart, "HyperX Pulsefire Dart"),
            (0x0951, 0x16E2, 1, 0xFF13, Model.Dart, "HyperX Pulsefire Dart"),
            (0x03F0, 0x088E, 1, 0xFF13, Model.Dart, "HyperX Pulsefire Dart"),
            (0x0951, 0x16E4, 1, 0xFF01, Model.Raid, "HyperX Pulsefire Raid"),
            (0x0951, 0x1727, 3, 0xFF90, Model.Haste, "HyperX Pulsefire Haste"),
            (0x03F0, 0x0F8F, 3, 0xFF90, Model.Haste, "HyperX Pulsefire Haste"),
        };

        public IEnumerable<RgbDevice> Detect(DetectContext ctx)
        {
            foreach (var t in Table)
            {
                var info = ctx.HidDevices.FirstOrDefault(d => d.VendorId == t.vid && d.ProductId == t.pid && d.Interface == t.iface && d.UsagePage == t.page);
                if (info == null) continue;
                RgbDevice dev = null;
                try { dev = new HyperXMouse(HidDevice.Open(info), t.model, t.name); }
                catch (Exception ex) { Log.Error("HyperX", ex); }
                if (dev != null) yield return dev;
            }
        }
    }

    public sealed class HyperXMouse : RgbDevice
    {
        readonly HidDevice _hid;
        readonly HyperXMouseProvider.Model _model;
        Rgb[] _last;
        const string Ok = "Doğrudan kontrol · deneysel";

        public override string[] ConflictingProcesses => new[] { "NGenuity2", "HyperX NGENUITY", "NGENUITY" };

        internal HyperXMouse(HidDevice hid, HyperXMouseProvider.Model model, string name)
        {
            _hid = hid; _model = model;
            Id = HidSelect.Id(hid.Info);
            Name = name; Vendor = "HyperX"; Kind = DeviceKind.Mouse;
            MaxFps = 25;
            int leds = model == HyperXMouseProvider.Model.Surge ? 33 : model == HyperXMouseProvider.Model.Raid || model == HyperXMouseProvider.Model.Dart ? 2 : 1;
            Zones.Add(new RgbZone("Fare", leds));
        }

        public override void Initialize() { _last = null; Status = Ok; }

        public override void Apply()
        {
            var c = Zones[0].Colors;
            if (_last != null && c.SequenceEqual(_last)) return;
            bool ok;
            switch (_model)
            {
                case HyperXMouseProvider.Model.Haste:
                    ok = _hid.SetFeature(Packet(0x00, 0x04, 0xF2, 0, 0, 0, 0, 0, 0x02));
                    ok &= _hid.SetFeature(Packet(0x00, 0x81, c[0].R, c[0].G, c[0].B, 0, 0, 0, 0x02));
                    break;
                case HyperXMouseProvider.Model.FpsPro:
                    ok = _hid.SetFeature(Packet(0x07, 0x0A, c[0].R, c[0].G, c[0].B, 0, 0, 0, 0xA0));
                    break;
                case HyperXMouseProvider.Model.Raid:
                    ok = _hid.SetFeature(Packet(0x07, 0x0A, c[0].R, c[0].G, c[0].B, c[1].R, c[1].G, c[1].B, 0xA0));
                    break;
                case HyperXMouseProvider.Model.Surge:
                {
                    var p = new byte[264];
                    p[0] = 0x07; p[1] = 0x14; p[3] = 0xA0;
                    for (int i = 0; i < 32; i++) { p[0x08 + i] = c[i].R; p[0x28 + i] = c[i].G; p[0x48 + i] = c[i].B; }
                    p[0x6C] = c[32].R; p[0x6D] = c[32].G; p[0x6E] = c[32].B;
                    ok = _hid.SetFeature(p);
                    break;
                }
                default: // Dart: logo (0x00) and scroll wheel (0x10), static mode, full brightness
                    ok = Dart(0x00, c[0]) & Dart(0x10, c[1]);
                    break;
            }
            if (ok) _last = (Rgb[])c.Clone();
            Status = ok ? Ok : "Yazma hatası";
        }

        bool Dart(byte led, Rgb c)
        {
            var p = new byte[64];
            p[1] = 0xD2; p[2] = led; p[3] = 0x00; p[4] = 0x08;
            p[5] = c.R; p[6] = c.G; p[7] = c.B; p[8] = c.R; p[9] = c.G; p[10] = c.B;
            p[11] = 0x64; p[12] = 0x00;
            return _hid.Write(p);
        }

        static byte[] Packet(params byte[] head)
        {
            var p = new byte[264];
            Array.Copy(head, p, head.Length);
            return p;
        }

        public override void Dispose() => _hid.Dispose();
    }
}
