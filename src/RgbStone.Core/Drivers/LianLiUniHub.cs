using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using RgbStone.Core;
using RgbStone.Transport;

namespace RgbStone.Drivers
{
    /// <summary>
    /// Lian Li UNI FAN hubs: SL (0CF2:A100) and SL V2 / AL V2 (A103-A105). Four channels of chained 16-LED fans, colors in
    /// R-B-G order with the "static" effect holding the full color array. Protocol reference: OpenRGB.
    /// Experimental: no Lian Li hub on the test PC.
    /// </summary>
    public sealed class LianLiUniHubProvider : IDeviceProvider
    {
        public string Name => "Lian Li UNI FAN hub";

        public IEnumerable<RgbDevice> Detect(DetectContext ctx)
        {
            foreach (var g in HidSelect.Physical(HidSelect.ByVidPid(ctx, 0x0CF2, new ushort[] { 0xA100, 0xA103, 0xA104, 0xA105 })))
            {
                var info = g.FirstOrDefault(d => d.UsagePage == 0xFF72) ?? g.OrderByDescending(d => d.OutputLength).First();
                RgbDevice dev = null;
                try { dev = new LianLiUniHub(HidDevice.Open(info)); }
                catch (Exception ex) { Log.Error("Lian Li", ex); }
                if (dev != null) yield return dev;
            }
        }
    }

    public sealed class LianLiUniHub : RgbDevice
    {
        const int LedsPerFan = 16;
        readonly HidDevice _hid;
        readonly bool _v2;
        readonly int[] _fans = new int[4];
        readonly byte[][] _last = new byte[4][];
        const string Ok = "Doğrudan kontrol · deneysel";

        public override string[] ConflictingProcesses => new[] { "L-Connect 3", "L-Connect", "LConnect3", "Lian-Li L-Connect" };

        internal LianLiUniHub(HidDevice hid)
        {
            _hid = hid;
            _v2 = hid.Info.ProductId != 0xA100;
            Id = HidSelect.Id(hid.Info);
            Name = hid.Info.ProductId == 0xA100 ? "Lian Li UNI Hub SL" : hid.Info.ProductId == 0xA104 ? "Lian Li UNI Hub AL V2" : "Lian Li UNI Hub SL V2";
            Vendor = "Lian Li";
            Kind = DeviceKind.LedController;
            MaxFps = 10; // three packets per channel with the pauses the hub needs
            int max = (_v2 ? 6 : 4) * LedsPerFan;
            for (int i = 0; i < 4; i++)
                Zones.Add(new RgbZone($"Kanal {i + 1}", 0, resizable: true, maxLeds: max) { DefaultLeds = 3 * LedsPerFan, SegmentSize = LedsPerFan });
        }

        public override void Initialize()
        {
            Array.Clear(_fans, 0, 4);
            for (int i = 0; i < 4; i++) _last[i] = null;
            Status = Ok;
        }

        bool Send(params byte[] head)
        {
            var p = new byte[Math.Max(head.Length, 11)];
            Array.Copy(head, p, head.Length);
            bool ok = _hid.Write(p);
            Thread.Sleep(5);
            return ok;
        }

        public override void Apply()
        {
            bool ok = true;
            for (int ch = 0; ch < 4; ch++)
            {
                var c = Zones[ch].Colors;
                if (c.Length == 0) continue;
                int fans = (c.Length + LedsPerFan - 1) / LedsPerFan;
                if (fans != _fans[ch])
                {
                    ok &= Send(0xE0, 0x10, (byte)(_v2 ? 0x60 : 0x32), (byte)(ch * 0x10 + fans));
                    _fans[ch] = fans;
                    _last[ch] = null;
                }

                var data = new byte[2 + c.Length * 3];
                data[0] = 0xE0; data[1] = (byte)(0x30 + ch);
                for (int i = 0; i < c.Length; i++)
                {
                    // Lian Li caps the summed channel value at 460 to protect the fan LEDs.
                    int sum = c[i].R + c[i].G + c[i].B;
                    double k = sum > 460 ? 460.0 / sum : 1;
                    data[2 + i * 3] = (byte)(c[i].R * k); data[3 + i * 3] = (byte)(c[i].B * k); data[4 + i * 3] = (byte)(c[i].G * k);
                }
                if (_last[ch] != null && _last[ch].SequenceEqual(data)) continue;
                ok &= Send(data);
                ok &= Send(0xE0, (byte)(0x10 + ch), 0x01, 0x00, 0x00, 0x00); // static, speed, direction, brightness 100 %
                if (ok) _last[ch] = data;
            }
            Status = ok ? Ok : "Yazma hatası";
        }

        public override void Dispose() => _hid.Dispose();
    }
}
