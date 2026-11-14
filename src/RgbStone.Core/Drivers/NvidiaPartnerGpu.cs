using System;
using System.Collections.Generic;
using System.Linq;
using RgbStone.Core;
using RgbStone.Transport;

namespace RgbStone.Drivers
{
    /// <summary>PNY / Palit / Gainward NVIDIA cards with the single-zone RGB controller at I2C address 0x49.</summary>
    public sealed class PnyGpuProvider : IDeviceProvider
    {
        public string Name => "PNY / Palit / Gainward GPU";

        // (subVendor, device, subDevice, name)
        static readonly (ushort sv, ushort dev, ushort sd, string name)[] Cards =
        {
            (0x196E, 0x2484, 0x136E, "PNY 3070 XLR8"), (0x196E, 0x2488, 0x138A, "PNY 3070 XLR8"), (0x196E, 0x2482, 0x138C, "PNY 3070 Ti XLR8"),
            (0x196E, 0x2216, 0x138B, "PNY 3080 XLR8"), (0x196E, 0x2206, 0x136B, "PNY 3080 XLR8 REVEL EPIC-X"), (0x196E, 0x220A, 0x1398, "PNY 3080 XLR8 REVEL EPIC-X"),
            (0x196E, 0x2208, 0x1385, "PNY 3080 Ti XLR8 REVEL"), (0x196E, 0x2204, 0x136A, "PNY 3090 XLR8"),
            (0x1569, 0x2486, 0x2486, "Palit 3060 Ti Dual OC"), (0x1569, 0x2484, 0x2484, "Palit 3070 GameRock"), (0x1569, 0x2484, 0xF278, "Palit 3070 GameRock OC"),
            (0x1569, 0x2484, 0xF280, "Palit 3070 JetStream"), (0x1569, 0x2488, 0xF280, "Palit 3070 JetStream"), (0x1569, 0x2488, 0x2488, "Palit 3070 Gaming Pro"),
            (0x1569, 0x2488, 0xF278, "Palit 3070 GameRock"), (0x1569, 0x2482, 0xF278, "Palit 3070 Ti GameRock"), (0x1569, 0x2482, 0x2482, "Palit 3070 Ti Gaming Pro"),
            (0x1569, 0x2216, 0x2216, "Palit 3080 Gaming Pro"), (0x1569, 0x2206, 0x2206, "Palit 3080 Gaming Pro"), (0x1569, 0x220A, 0x220A, "Palit 3080 Gaming Pro"),
            (0x1569, 0x2206, 0xF278, "Palit 3080 GameRock"), (0x1569, 0x2216, 0xF278, "Palit 3080 GameRock LHR"), (0x1569, 0x2208, 0x2208, "Palit 3080 Ti Gaming Pro"),
            (0x1569, 0x2208, 0xF278, "Palit 3080 Ti GameRock OC"), (0x1569, 0x2204, 0xF278, "Palit 3090 GameRock"), (0x1569, 0x2204, 0x2204, "Palit 3090 Gaming Pro"),
            (0x1569, 0x2203, 0xF294, "Palit 3090 Ti GameRock"), (0x1569, 0x2786, 0xF298, "Palit 4070 Gaming Pro OC"), (0x1569, 0x2782, 0xF298, "Palit 4070 Ti Gaming Pro"),
            (0x1569, 0x2782, 0xF294, "Palit 4070 Ti GameRock Classic"), (0x1569, 0x2705, 0xF298, "Palit 4070 Ti Super Gaming Pro"), (0x1569, 0x2704, 0xF296, "Palit 4080 GameRock"),
            (0x1569, 0x2702, 0xF298, "Palit 4080 Super Gaming Pro"), (0x1569, 0x2684, 0xF296, "Palit 4090 GameRock OC"), (0x1569, 0x2D04, 0xF330, "Palit 5060 Ti Dual"),
            (0x1569, 0x2F04, 0xF324, "Palit 5070 Gaming Pro"), (0x1569, 0x2C05, 0xF320, "Palit 5070 Ti GameRock"), (0x1569, 0x2C05, 0xF322, "Palit 5070 Ti Gaming Pro"),
            (0x1569, 0x2C02, 0xF320, "Palit 5080 GameRock"), (0x1569, 0x2C02, 0xF322, "Palit 5080 Gaming Pro"), (0x1569, 0x2B85, 0xF318, "Palit 5090 GameRock"),
            (0x10B0, 0x2484, 0x2484, "Gainward 3070 Phoenix GS"), (0x10B0, 0x2488, 0x2488, "Gainward 3070 Phantom GS"), (0x10B0, 0x2482, 0x2482, "Gainward 3070 Ti Phoenix"),
            (0x10B0, 0x2206, 0x2206, "Gainward 3080 Phoenix"), (0x10B0, 0x2216, 0x2216, "Gainward 3080 Phoenix"), (0x10B0, 0x2204, 0x2204, "Gainward 3090 Phoenix"),
            (0x10B0, 0x2782, 0xF299, "Gainward 4070 Ti Phoenix GS"), (0x10B0, 0x2704, 0xF299, "Gainward 4080 Phoenix GS"), (0x10B0, 0x2704, 0xF297, "Gainward 4080 Phantom GS"),
            (0x10B0, 0x2702, 0xF299, "Gainward 4080 Super Phoenix"), (0x10B0, 0x2684, 0xF297, "Gainward 4090 Phantom GS"), (0x10B0, 0x2F04, 0xF325, "Gainward 5070 Phoenix"),
            (0x10B0, 0x2C05, 0xF323, "Gainward 5070 Ti Phoenix"), (0x10B0, 0x2C02, 0xF323, "Gainward 5080 Phoenix"), (0x10B0, 0x2B85, 0xF319, "Gainward 5090 Phantom GS"),
        };

        public IEnumerable<RgbDevice> Detect(DetectContext ctx)
        {
            foreach (var gpu in NvApi.EnumerateGpus())
            {
                var card = Cards.FirstOrDefault(c => c.sv == gpu.SubVendor && c.dev == gpu.Device && c.sd == gpu.SubDevice);
                if (card.name == null) continue;
                yield return new PnyGpu(gpu, card.name);
            }
        }
    }

    public sealed class PnyGpu : RgbDevice
    {
        const byte Addr = 0x49, RegControl = 0xE0, RegMode = 0x60, RegR = 0x6C, RegG = 0x6D, RegB = 0x6E, RegBrightness = 0x6F;
        readonly NvApi.Gpu _gpu;
        Rgb? _last;
        int _failures;
        DateTime _retryAt = DateTime.MinValue;
        /// <summary>After this many failed writes in a row the LED is left alone for <see cref="FailurePause"/>.</summary>
        const int MaxFailures = 3;
        static readonly TimeSpan FailurePause = TimeSpan.FromMinutes(1);

        public PnyGpu(NvApi.Gpu gpu, string name)
        {
            _gpu = gpu;
            Id = $"nvgpu:{gpu.Device:x4}:{gpu.SubVendor:x4}:{gpu.SubDevice:x4}";
            Name = name;
            Vendor = "NVIDIA";
            Kind = DeviceKind.Gpu;
            // Every write is an I2C transaction through the display driver. At 30 fps a rainbow effect made ~90 of them a
            // second, all the time, also while the card decodes video; a single logo LED does not need more than a few.
            MaxFps = 4;
            Zones.Add(new RgbZone("Logo", 1));
        }

        public override void Initialize()
        {
            bool ok = NvApi.WriteByte(_gpu, Addr, RegControl, 0x00)
                   && NvApi.WriteByte(_gpu, Addr, RegMode, 0x01)
                   && NvApi.WriteByte(_gpu, Addr, RegBrightness, 0x64);
            _last = null;
            _failures = ok ? 0 : _failures + 1;
            Status = ok ? "Doğrudan kontrol" : "I2C yazılamadı";
        }

        public override void Apply()
        {
            var now = DateTime.UtcNow;
            if (now < _retryAt) return;
            var c = Zones[0].Colors[0];
            if (_last == c) return;
            // Only the channels that changed are sent.
            var last = _last;
            bool ok = (last?.R == c.R || NvApi.WriteByte(_gpu, Addr, RegR, c.R))
                   && (last?.G == c.G || NvApi.WriteByte(_gpu, Addr, RegG, c.G))
                   && (last?.B == c.B || NvApi.WriteByte(_gpu, Addr, RegB, c.B));
            if (ok)
            {
                _last = c;
                _failures = 0;
                Status = "Doğrudan kontrol";
                return;
            }
            // A failing bus is not hammered every frame: after a few failures in a row, wait a minute.
            _last = null;
            if (++_failures >= MaxFailures)
            {
                _failures = 0;
                _retryAt = now + FailurePause;
                Status = "I2C yazılamadı, 1 dk bekleniyor";
            }
            else Status = "I2C yazılamadı";
        }
    }
}
