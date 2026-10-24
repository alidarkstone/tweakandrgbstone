using System;
using System.Collections.Generic;
using System.Linq;
using RgbStone.Core;
using RgbStone.Transport;

namespace RgbStone.Drivers
{
    /// <summary>
    /// Corsair's classic lighting controllers: Lighting Node Pro/Core, Commander Pro, LS100, LT100, Spec Omega.
    /// Colors are streamed as separate R, G and B planes of up to 50 LEDs, then committed.
    /// </summary>
    public sealed class CorsairLightingNodeProvider : IDeviceProvider
    {
        public string Name => "Corsair Lighting Node / Commander Pro";

        static readonly Dictionary<ushort, (string name, int channels, int max)> Models = new Dictionary<ushort, (string, int, int)>
        {
            [0x0C0B] = ("Corsair Lighting Node Pro", 2, 204),
            [0x0C1A] = ("Corsair Lighting Node Core", 1, 204),
            [0x0C10] = ("Corsair Commander Pro", 2, 204),
            [0x1D00] = ("Corsair Commander Pro", 2, 204),
            [0x0C1E] = ("Corsair LS100", 2, 138),
            [0x0C23] = ("Corsair LT100", 1, 108),
            [0x1D04] = ("Corsair Spec Omega RGB", 1, 204),
        };

        public IEnumerable<RgbDevice> Detect(DetectContext ctx)
        {
            foreach (var g in HidSelect.Physical(HidSelect.ByVidPid(ctx, 0x1B1C, Models.Keys)))
            {
                var info = g.FirstOrDefault(d => d.Interface <= 0 || d.Interface == 2) ?? g.First();
                var m = Models[info.ProductId];
                RgbDevice dev;
                try { dev = new CorsairLightingNode(HidDevice.Open(info), m.name, m.channels, m.max); }
                catch (Exception ex) { Log.Error("Corsair", ex); dev = new UnavailableDevice(HidSelect.Id(info), m.name, "Corsair", DeviceKind.LedController, ex.Message); }
                yield return dev;
            }
        }
    }

    public sealed class CorsairLightingNode : RgbDevice
    {
        const byte Firmware = 0x02, DirectColors = 0x32, Commit = 0x33, Mode = 0x38, Hardware = 1, Software = 2;
        readonly HidDevice _hid;

        public override string[] ConflictingProcesses => new[] { "iCUE", "Corsair.Service" };

        public CorsairLightingNode(HidDevice hid, string name, int channels, int max)
        {
            _hid = hid;
            Id = HidSelect.Id(hid.Info);
            Name = name;
            Vendor = "Corsair";
            Kind = DeviceKind.LedController;
            MaxFps = 30;
            for (int c = 0; c < channels; c++) Zones.Add(new RgbZone($"Channel {c + 1}", 0, resizable: true, maxLeds: max));
        }

        void Send(params byte[] p)
        {
            var b = new byte[65];
            Array.Copy(p, 0, b, 1, Math.Min(p.Length, 64)); // b[0] = report id 0
            _hid.Write(b);
            _hid.Read(5); // the controller answers every packet; drain without waiting
        }

        public override void Initialize()
        {
            _hid.FlushInput();
            Send(Firmware);
            Status = "Doğrudan kontrol · deneysel";
        }

        public override void Apply()
        {
            _hid.FlushInput();
            for (int ch = 0; ch < Zones.Count; ch++)
            {
                var colors = Zones[ch].Colors;
                if (colors.Length == 0) continue;
                Send(Mode, (byte)ch, Software);
                int total = Math.Min(colors.Length, Zones[ch].MaxLeds);
                for (int start = 0; start < total; start += 50)
                {
                    int n = Math.Min(50, total - start);
                    for (byte plane = 0; plane < 3; plane++)
                    {
                        var p = new byte[6 + n];
                        p[0] = DirectColors; p[1] = (byte)ch; p[2] = (byte)start; p[3] = (byte)n; p[4] = plane;
                        for (int i = 0; i < n; i++)
                        {
                            var c = colors[start + i];
                            p[5 + i] = plane == 0 ? c.R : plane == 1 ? c.G : c.B;
                        }
                        Send(p.Take(5 + n).ToArray());
                    }
                }
            }
            Send(Commit, 0xFF);
            Status = "Doğrudan kontrol · deneysel";
        }

        public override void Shutdown(Rgb color)
        {
            base.Shutdown(color);
        }

        public override void Dispose()
        {
            try { for (int ch = 0; ch < Zones.Count; ch++) Send(Mode, (byte)ch, Hardware); } catch { }
            _hid.Dispose();
        }
    }
}
