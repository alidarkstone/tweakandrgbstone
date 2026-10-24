using System;
using System.Collections.Generic;
using System.Linq;
using RgbStone.Core;
using RgbStone.Transport;

namespace RgbStone.Drivers
{
    /// <summary>
    /// ASRock Polychrome USB motherboards (26CE:01A2) in zone mode: eight single-color zones
    /// (two 12V headers, three ARGB headers, PCH, I/O cover, PCB). Zone mode works on every board, unlike per-LED mode,
    /// which depends on the header lengths set in ASRock's own app. Experimental.
    /// </summary>
    public sealed class AsrockPolychromeProvider : IDeviceProvider
    {
        public string Name => "ASRock Polychrome";

        public IEnumerable<RgbDevice> Detect(DetectContext ctx)
        {
            foreach (var g in HidSelect.Physical(HidSelect.ByVidPid(ctx, 0x26CE, new ushort[] { 0x01A2 })))
            {
                var info = g.FirstOrDefault(d => d.Interface <= 0) ?? g.First();
                RgbDevice dev;
                try { dev = new AsrockPolychrome(HidDevice.Open(info), ctx.System.BoardProduct); }
                catch (Exception ex) { Log.Error("ASRock", ex); dev = new UnavailableDevice(HidSelect.Id(info), "ASRock " + ctx.System.BoardProduct, "ASRock", DeviceKind.Motherboard, ex.Message); }
                yield return dev;
            }
        }
    }

    public sealed class AsrockPolychrome : RgbDevice
    {
        static readonly string[] ZoneNames = { "12V RGB 1", "12V RGB 2", "5V ARGB 1", "5V ARGB 2", "PCH", "I/O kapağı", "PCB", "5V ARGB 3" };
        readonly HidDevice _hid;
        Rgb[] _last;

        public override string[] ConflictingProcesses => new[] { "AsrRgbLED", "ASRockRGBLED", "Polychrome" };

        public AsrockPolychrome(HidDevice hid, string board)
        {
            _hid = hid;
            Id = HidSelect.Id(hid.Info);
            Name = "ASRock " + (string.IsNullOrEmpty(board) ? "Polychrome" : board);
            Vendor = "ASRock";
            Kind = DeviceKind.Motherboard;
            MaxFps = 30;
            foreach (var n in ZoneNames) Zones.Add(new RgbZone(n, 1));
        }

        bool Write(params byte[] p)
        {
            var b = new byte[65];
            Array.Copy(p, b, Math.Min(p.Length, 65));
            return _hid.Write(b);
        }

        public override void Initialize()
        {
            Write(0x00, 0x10, 0x00, 0x02, 0x0E, 0xFF, 0xF2, 0x00, 0x6D, 0xFF); // zone (direct) mode
            _last = null;
            Status = "Doğrudan kontrol · deneysel";
        }

        public override void Apply()
        {
            var colors = Zones.Select(z => z.Colors[0]).ToArray();
            if (_last != null && colors.SequenceEqual(_last)) return;
            var p = new List<byte> { 0x00, 0x10, 0x00, 0x07, 0xE2 };
            foreach (var c in colors) { p.Add(c.R); p.Add(c.G); p.Add(c.B); }
            bool ok = Write(p.ToArray());
            if (ok) _last = colors;
            Status = ok ? "Doğrudan kontrol · deneysel" : "Yazma hatası";
        }

        public override void Dispose() => _hid.Dispose();
    }
}
