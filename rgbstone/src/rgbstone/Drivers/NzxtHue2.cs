using System;
using System.Collections.Generic;
using System.Linq;
using RgbStone.Core;
using RgbStone.Transport;

namespace RgbStone.Drivers
{
    /// <summary>NZXT HUE 2 family: Smart Device V2, RGB &amp; Fan Controller, Control Hub (VID 0x1E71).</summary>
    public sealed class NzxtHue2Provider : IDeviceProvider
    {
        public string Name => "NZXT HUE 2 / Control Hub";

        static readonly Dictionary<ushort, string> Names = new Dictionary<ushort, string>
        {
            [0x2001] = "NZXT HUE 2", [0x2002] = "NZXT HUE 2 Ambient", [0x2006] = "NZXT Smart Device V2",
            [0x200D] = "NZXT Smart Device V2", [0x200F] = "NZXT Smart Device V2",
            [0x2009] = "NZXT RGB & Fan Controller", [0x200E] = "NZXT RGB & Fan Controller", [0x2010] = "NZXT RGB & Fan Controller",
            [0x2011] = "NZXT RGB & Fan Controller", [0x2012] = "NZXT RGB & Fan Controller", [0x2019] = "NZXT RGB & Fan Controller",
            [0x201F] = "NZXT RGB & Fan Controller", [0x2020] = "NZXT RGB & Fan Controller", [0x2021] = "NZXT RGB & Fan Controller",
            [0x2022] = "NZXT Control Hub",
        };

        public IEnumerable<RgbDevice> Detect(DetectContext ctx)
        {
            foreach (var g in HidSelect.Physical(HidSelect.ByVidPid(ctx, 0x1E71, Names.Keys)))
            {
                var info = g.FirstOrDefault(d => d.Interface <= 0) ?? g.First();
                RgbDevice dev;
                try { dev = new NzxtHue2(HidDevice.Open(info), Names[info.ProductId]); }
                catch (Exception ex)
                {
                    Log.Error("NZXT", ex);
                    dev = new UnavailableDevice(HidSelect.Id(info), Names[info.ProductId], "NZXT", DeviceKind.LedController, ex.Message);
                }
                yield return dev;
            }
        }
    }

    public sealed class NzxtHue2 : RgbDevice
    {
        readonly HidDevice _hid;
        const int MaxPerChannel = 40;
        byte[] _ledInfo;

        /// <summary>
        /// HUE 2 accessory ids. 0x19 (F120 RGB Core) and 0x1F (F420 RGB Core, three 140 mm fans in one frame) are the H9 Flow RGB+ fans, 9 LEDs per fan,
        /// confirmed on this PC; the others follow NZXT's HUE 2 accessory list.
        /// </summary>
        static readonly Dictionary<byte, (string name, int leds)> Accessories = new Dictionary<byte, (string, int)>
        {
            [0x01] = ("HUE+ LED şerit", 10), [0x02] = ("Aer RGB 120", 8), [0x04] = ("Aer RGB 2 120", 8), [0x05] = ("Aer RGB 2 140", 8),
            [0x06] = ("HUE 2 LED şerit 300 mm", 10), [0x07] = ("HUE 2 LED şerit 250 mm", 8), [0x08] = ("HUE 2 LED şerit 200 mm", 6),
            [0x09] = ("HUE 2 Cable Comb", 14), [0x0B] = ("HUE 2 Underglow 300 mm", 15), [0x0C] = ("HUE 2 Underglow 200 mm", 10),
            [0x0D] = ("F120 RGB", 18), [0x0E] = ("F140 RGB", 18), [0x10] = ("F120 RGB Duo", 20), [0x11] = ("F140 RGB Duo", 20),
            [0x19] = ("F120 RGB Core (120 mm)", 9), [0x1A] = ("F140 RGB Core (140 mm)", 9), [0x1F] = ("F420 RGB Core (3 × 140 mm tek gövde)", 27), [0x1E] = ("F360 RGB Core (3 × 120 mm tek gövde)", 27),
        };

        public override string[] ConflictingProcesses => new[] { "NZXT CAM", "CAM" };

        public NzxtHue2(HidDevice hid, string name)
        {
            _hid = hid;
            Id = HidSelect.Id(hid.Info);
            Name = name;
            Vendor = "NZXT";
            Kind = DeviceKind.LedController;
            MaxFps = 30;
        }

        public override void Initialize()
        {
            _hid.FlushInput();
            _hid.Write(new byte[] { 0x10, 0x01 });   // firmware
            _hid.Write(new byte[] { 0x20, 0x03 });   // LED channel info

            int channels = 0;
            for (int i = 0; i < 20 && channels == 0; i++)
            {
                var r = _hid.Read(100);
                if (r == null) continue;
                if (r[0] == 0x11 && r[1] == 0x01 && r.Length > 0x13) Log.Info($"{Name} yazılım {r[0x11]}.{r[0x12]}.{r[0x13]}");
                if (r[0] == 0x21 && r[1] == 0x03 && r.Length > 14)
                {
                    channels = r[14];
                    _ledInfo = r;
                    Log.Info($"{Name} LED bilgisi: {BitConverter.ToString(r, 0, Math.Min(64, r.Length))}");
                }
            }
            if (channels <= 0 || channels > 8)
            {
                channels = _hid.Info.ProductId == 0x2022 ? 5 : 3;
                Log.Warn($"{Name}: kanal sayısı okunamadı, {channels} varsayıldı.");
            }
            Zones.Clear();
            for (int c = 0; c < channels; c++)
            {
                var z = new RgbZone($"Channel {c + 1}", 0, resizable: true, maxLeds: MaxPerChannel);
                // The hub reports up to 6 accessory ids per channel at offset 0x0F; known ids give the LED count automatically.
                if (_ledInfo != null)
                {
                    var found = new List<string>();
                    for (int slot = 0; slot < 6; slot++)
                    {
                        int at = 0x0F + c * 6 + slot;
                        if (at >= _ledInfo.Length || _ledInfo[at] == 0) continue;
                        if (Accessories.TryGetValue(_ledInfo[at], out var a)) { z.DefaultLeds += a.leds; found.Add(a.name); }
                        else found.Add($"bilinmeyen aksesuar 0x{_ledInfo[at]:X2}");
                    }
                    if (found.Count > 0) z.Detected = string.Join(" + ", found);
                }
                Zones.Add(z);
            }
            Status = "Doğrudan kontrol";
        }

        public override void Apply()
        {
            for (int ch = 0; ch < Zones.Count; ch++)
            {
                var z = Zones[ch];
                if (z.LedCount == 0) continue;
                int n = Math.Min(z.LedCount, 120), sent = 0, pkt = 0;
                while (sent < n)
                {
                    int count = Math.Min(20, n - sent);
                    var p = new byte[4 + count * 3];
                    p[0] = 0x22; p[1] = (byte)(0x10 | pkt); p[2] = (byte)(1 << ch);
                    for (int i = 0; i < count; i++)
                    {
                        var c = z.Colors[sent + i];
                        p[4 + i * 3] = c.G; p[5 + i * 3] = c.R; p[6 + i * 3] = c.B; // GRB order
                    }
                    if (!_hid.Write(p)) { Status = "Yazma hatası"; return; }
                    sent += count; pkt++;
                }
                _hid.Write(new byte[] { 0x22, 0xA0, (byte)(1 << ch), 0x00, 0x01, 0x00, 0x00, 0x28, 0x00, 0x00, 0x80, 0x00, 0x32, 0x00, 0x00, 0x01 });
            }
            _hid.FlushInput(); // fan status reports pile up otherwise
            Status = "Doğrudan kontrol";
        }

        public override void Dispose() => _hid.Dispose();
    }
}
