using System;
using System.Collections.Generic;
using System.Linq;
using RgbStone.Core;
using RgbStone.Transport;

namespace RgbStone.Drivers
{
    /// <summary>
    /// SteelSeries Apex 5/7/9/Pro keyboards (per-key direct report) and Rival / Sensei mice (per-zone colors).
    /// The QcK Prism mousepads have their own driver. Protocol reference: OpenRGB. Experimental: only a QcK on the test PC.
    /// </summary>
    public sealed class SteelSeriesPeripheralProvider : IDeviceProvider
    {
        public string Name => "SteelSeries Apex / Rival / Sensei";

        internal enum Kind { ApexGen1, ApexGen2, ApexGen3, ApexGen3Wireless, Rival100, Rival300, Sensei, Rival600, Rival3 }

        // (pid, interface, kind, name); interface -1 = any vendor collection
        static readonly (ushort pid, int iface, Kind kind, string name)[] Table =
        {
            (0x161C, 1, Kind.ApexGen1, "SteelSeries Apex 5"), (0x1612, 1, Kind.ApexGen1, "SteelSeries Apex 7"), (0x1618, 1, Kind.ApexGen1, "SteelSeries Apex 7 TKL"),
            (0x1610, 1, Kind.ApexGen1, "SteelSeries Apex Pro"), (0x1614, 1, Kind.ApexGen1, "SteelSeries Apex Pro TKL"),
            (0x1634, 1, Kind.ApexGen2, "SteelSeries Apex 9 TKL"), (0x1620, 1, Kind.ApexGen2, "SteelSeries Apex 9 Mini"), (0x1628, 1, Kind.ApexGen2, "SteelSeries Apex Pro TKL (2023)"),
            (0x1642, 1, Kind.ApexGen3, "SteelSeries Apex Pro TKL Gen 3"), (0x1640, 1, Kind.ApexGen3, "SteelSeries Apex Pro Gen 3"),
            (0x1630, 3, Kind.ApexGen3Wireless, "SteelSeries Apex Pro TKL Wireless (2023)"), (0x1632, 3, Kind.ApexGen3Wireless, "SteelSeries Apex Pro TKL Wireless (2023)"),
            (0x1644, 3, Kind.ApexGen3Wireless, "SteelSeries Apex Pro TKL Wireless Gen 3"), (0x1646, 3, Kind.ApexGen3Wireless, "SteelSeries Apex Pro TKL Wireless Gen 3"),
            (0x1702, 0, Kind.Rival100, "SteelSeries Rival 100"), (0x170C, 0, Kind.Rival100, "SteelSeries Rival 100"), (0x1814, 0, Kind.Rival100, "SteelSeries Rival 105"),
            (0x1816, 0, Kind.Rival100, "SteelSeries Rival 106"), (0x1729, 0, Kind.Rival100, "SteelSeries Rival 110"),
            (0x1710, 0, Kind.Rival300, "SteelSeries Rival 300"), (0x1714, 0, Kind.Rival300, "Acer Predator (Rival 300)"), (0x1394, 0, Kind.Rival300, "SteelSeries Rival 300"),
            (0x1716, 0, Kind.Rival300, "SteelSeries Rival 300"), (0x171A, 0, Kind.Rival300, "SteelSeries Rival 300"), (0x1392, 0, Kind.Rival300, "SteelSeries Rival 300"),
            (0x1718, 0, Kind.Rival300, "SteelSeries Rival 300 HP Omen"),
            (0x1720, 0, Kind.Sensei, "SteelSeries Rival 310"), (0x171E, 0, Kind.Sensei, "SteelSeries Rival 310"), (0x1736, 0, Kind.Sensei, "SteelSeries Rival 310 PUBG"),
            (0x1832, 0, Kind.Sensei, "SteelSeries Sensei TEN"), (0x1834, 0, Kind.Sensei, "SteelSeries Sensei TEN"), (0x1722, 0, Kind.Sensei, "SteelSeries Sensei 310"),
            (0x1724, 0, Kind.Rival600, "SteelSeries Rival 600"), (0x172E, 0, Kind.Rival600, "SteelSeries Rival 600"),
            (0x1824, 3, Kind.Rival3, "SteelSeries Rival 3"), (0x184C, 3, Kind.Rival3, "SteelSeries Rival 3"),
        };

        public IEnumerable<RgbDevice> Detect(DetectContext ctx)
        {
            foreach (var t in Table)
            {
                var info = ctx.HidDevices.Where(d => d.VendorId == 0x1038 && d.ProductId == t.pid && d.Interface == t.iface)
                    .OrderByDescending(d => d.UsagePage >= 0xFF00).ThenByDescending(d => Math.Max(d.FeatureLength, d.OutputLength)).FirstOrDefault();
                if (info == null) continue;
                RgbDevice dev = null;
                try { dev = new SteelSeriesPeripheral(HidDevice.Open(info), t.kind, t.name); }
                catch (Exception ex) { Log.Error("SteelSeries", ex); }
                if (dev != null) yield return dev;
            }
        }
    }

    public sealed class SteelSeriesPeripheral : RgbDevice
    {
        static readonly byte[] ApexKeys =
        {
            0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17,
            0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x29, 0x2A, 0x2B,
            0x2C, 0x2D, 0x2E, 0x2F, 0x30, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40,
            0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4A, 0x4B, 0x4C, 0x4D, 0x4E, 0x4F, 0x50, 0x51, 0x52, 0x64, 0xE0,
            0xE1, 0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xF0, 0x31, 0x87, 0x88, 0x89, 0x8A, 0x8B, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58,
            0x59, 0x5A, 0x5B, 0x5C, 0x5D, 0x5E, 0x5F, 0x60, 0x61, 0x62, 0x63, 0xFB,
        };
        static readonly byte[] Rival3Zones = { 0x01, 0x02, 0x03, 0x04 };

        readonly HidDevice _hid;
        readonly SteelSeriesPeripheralProvider.Kind _kind;
        byte _apexPacket;
        Rgb[][] _last;
        const string Ok = "Doğrudan kontrol · deneysel";

        public override string[] ConflictingProcesses => new[] { "SteelSeriesGG", "SteelSeriesEngine", "SteelSeriesPrism", "SteelSeriesGGClient" };

        bool Apex => _kind <= SteelSeriesPeripheralProvider.Kind.ApexGen3Wireless;

        internal SteelSeriesPeripheral(HidDevice hid, SteelSeriesPeripheralProvider.Kind kind, string name)
        {
            _hid = hid; _kind = kind;
            Id = HidSelect.Id(hid.Info);
            Name = name; Vendor = "SteelSeries";
            Kind = Apex ? DeviceKind.Keyboard : DeviceKind.Mouse;
            MaxFps = Apex ? 30 : 15;
            switch (kind)
            {
                case SteelSeriesPeripheralProvider.Kind.Rival100: Zones.Add(new RgbZone("Logo", 1)); break;
                case SteelSeriesPeripheralProvider.Kind.Rival300:
                case SteelSeriesPeripheralProvider.Kind.Sensei: Zones.Add(new RgbZone("Logo", 1)); Zones.Add(new RgbZone("Tekerlek", 1)); break;
                case SteelSeriesPeripheralProvider.Kind.Rival600: Zones.Add(new RgbZone("Fare", 8)); break;
                case SteelSeriesPeripheralProvider.Kind.Rival3: Zones.Add(new RgbZone("Fare", 4)); break;
                default: Zones.Add(new RgbZone("Tuşlar", ApexKeys.Length, x: HidKeyLayout.X(ApexKeys))); break;
            }
        }

        public override void Initialize()
        {
            _apexPacket = _kind == SteelSeriesPeripheralProvider.Kind.ApexGen1 ? (byte)0x3A
                : _kind == SteelSeriesPeripheralProvider.Kind.ApexGen3Wireless ? (byte)0x61 : (byte)0x40;
            if (_kind >= SteelSeriesPeripheralProvider.Kind.ApexGen3 && Apex) _hid.SetFeature(new byte[] { 0x00, 0x4B }); // gen-3 init
            _last = null;
            Status = Ok;
        }

        public override void Apply()
        {
            var now = Zones.Select(z => (Rgb[])z.Colors.Clone()).ToArray();
            if (_last != null && now.Zip(_last, (a, b) => a.SequenceEqual(b)).All(x => x)) return;
            bool ok = true;
            switch (_kind)
            {
                case SteelSeriesPeripheralProvider.Kind.Rival100:
                    ok = Out(0x05, 0x00, now[0][0].R, now[0][0].G, now[0][0].B);
                    break;
                case SteelSeriesPeripheralProvider.Kind.Rival300:
                    for (int z = 0; z < 2; z++) ok &= Out(0x08, (byte)(z + 1), now[z][0].R, now[z][0].G, now[z][0].B);
                    break;
                case SteelSeriesPeripheralProvider.Kind.Sensei:
                    for (int z = 0; z < 2; z++)
                    {
                        var p = new byte[64];
                        var c = now[z][0];
                        p[0x00] = 0x5B; p[0x02] = (byte)z; p[0x13] = 0x01; p[0x1B] = 0x01; // static, one color
                        p[0x1C] = c.R; p[0x1D] = c.G; p[0x1E] = c.B; p[0x1F] = c.R; p[0x20] = c.G; p[0x21] = c.B;
                        ok &= Out(p);
                    }
                    break;
                case SteelSeriesPeripheralProvider.Kind.Rival600:
                    // the Rival 600 takes the packet as is: 0x1C is its report id
                    for (int z = 0; z < 8; z++) ok &= _hid.Write(new byte[] { 0x1C, 0x27, 0x00, (byte)(1 << z), now[0][z].R, now[0][z].G, now[0][z].B });
                    break;
                case SteelSeriesPeripheralProvider.Kind.Rival3:
                    for (int z = 0; z < 4; z++) ok &= Out(0x05, 0x00, Rival3Zones[z], now[0][z].R, now[0][z].G, now[0][z].B, 0x64);
                    break;
                default:
                    ok = SendApex(now[0]);
                    if (!ok && _kind == SteelSeriesPeripheralProvider.Kind.ApexGen1 && _apexPacket == 0x3A)
                    {
                        // Apex Pro TKL with 2023 firmware speaks the newer protocol
                        _apexPacket = 0x40;
                        _hid.SetFeature(new byte[] { 0x00, 0x4B });
                        ok = SendApex(now[0]);
                    }
                    break;
            }
            if (ok) _last = now;
            Status = ok ? Ok : "Yazma hatası";
        }

        bool SendApex(Rgb[] c)
        {
            int max = _hid.Info.FeatureLength > 0 ? _hid.Info.FeatureLength : 643;
            int n = Math.Min(c.Length, (max - 3) / 4);
            var p = new byte[max];
            p[1] = _apexPacket; p[2] = (byte)n;
            for (int i = 0; i < n; i++) { p[3 + i * 4] = ApexKeys[i]; p[4 + i * 4] = c[i].R; p[5 + i * 4] = c[i].G; p[6 + i * 4] = c[i].B; }
            return _hid.SetFeature(p);
        }

        /// <summary>Output report with report id 0 followed by the payload.</summary>
        bool Out(params byte[] payload)
        {
            var p = new byte[payload.Length + 1];
            Array.Copy(payload, 0, p, 1, payload.Length);
            return _hid.Write(p);
        }

        public override void Dispose() => _hid.Dispose();
    }
}
