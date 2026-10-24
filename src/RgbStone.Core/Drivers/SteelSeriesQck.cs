using System;
using System.Collections.Generic;
using System.Linq;
using RgbStone.Core;
using RgbStone.Transport;

namespace RgbStone.Drivers
{
    /// <summary>SteelSeries QcK Prism mousepads (two zones: top and bottom edge).</summary>
    public sealed class SteelSeriesQckProvider : IDeviceProvider
    {
        public string Name => "SteelSeries QcK Prism";

        static readonly Dictionary<ushort, string> Models = new Dictionary<ushort, string>
        {
            [0x150A] = "Medium", [0x151E] = "XL", [0x3769] = "XL", [0x1520] = "XL", [0x150D] = "XL", [0x1516] = "3XL", [0x151A] = "5XL",
        };

        public IEnumerable<RgbDevice> Detect(DetectContext ctx)
        {
            foreach (var g in HidSelect.Physical(HidSelect.ByVidPid(ctx, 0x1038, Models.Keys)))
            {
                var info = g.FirstOrDefault(d => d.Interface == 0 && d.UsagePage == 0xFFC0 && d.Usage == 0x0001);
                if (info == null) continue;
                RgbDevice dev;
                try { dev = new SteelSeriesQck(HidDevice.Open(info), Models[info.ProductId]); }
                catch (Exception ex)
                {
                    Log.Error("SteelSeries", ex);
                    dev = new UnavailableDevice(HidSelect.Id(info), "SteelSeries QcK Prism " + Models[info.ProductId], "SteelSeries", DeviceKind.Mousepad, ex.Message);
                }
                yield return dev;
            }
        }
    }

    public sealed class SteelSeriesQck : RgbDevice
    {
        readonly HidDevice _hid;

        public override string[] ConflictingProcesses => new[] { "SteelSeriesGG", "SteelSeriesEngine", "SteelSeriesPrism", "SteelSeriesGGClient" };

        public SteelSeriesQck(HidDevice hid, string model)
        {
            _hid = hid;
            Id = HidSelect.Id(hid.Info);
            Name = "SteelSeries QcK Prism " + model;
            Vendor = "SteelSeries";
            Kind = DeviceKind.Mousepad;
            MaxFps = 30;
            Zones.Add(new RgbZone("Üst", 1));
            Zones.Add(new RgbZone("Alt", 1));
        }

        public override void Initialize() => Status = "Doğrudan kontrol";

        public override void Apply()
        {
            var top = Zones[0].Colors[0];
            var bottom = Zones[1].Colors[0];
            var p = new byte[525];
            p[1] = 0x0E; p[3] = 0x02;
            p[5] = top.R; p[6] = top.G; p[7] = top.B; p[8] = 0xFF; p[9] = 0x32; p[10] = 0xC8; p[14] = 0x01;
            p[17] = bottom.R; p[18] = bottom.G; p[19] = bottom.B; p[20] = 0xFF; p[21] = 0x32; p[22] = 0xC8; p[25] = 0x01;
            p[26] = 0x01; p[28] = 0x01;
            bool ok = _hid.SetFeature(p);
            ok &= _hid.Write(new byte[] { 0x00, 0x0D });
            Status = ok ? "Doğrudan kontrol" : "Yazma hatası";
        }

        public override void Dispose() => _hid.Dispose();
    }
}
