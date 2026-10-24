using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using RgbStone.Core;
using RgbStone.Transport;

namespace RgbStone.Drivers
{
    /// <summary>
    /// Corsair iCUE peripherals with the "V2" (Bragi) protocol: K55/K57/K60/K70/K95/K100 keyboards, Dark Core, Harpoon,
    /// Ironclaw, Katar, M55/M65/M75, Scimitar mice, MM700 mousepads, and the Slipstream receivers they pair with.
    /// Software mode, then one block write of the color buffer per frame. Keyboards index LEDs by key code (mostly HID usage).
    /// Protocol and LED counts: OpenRGB. Experimental: no Corsair peripheral on the test PC.
    /// </summary>
    public sealed class CorsairPeripheralV2Provider : IDeviceProvider
    {
        public string Name => "Corsair iCUE klavye / fare (V2)";

        // pid -> (name, kind, buffer size, hardware-style controller)
        internal static readonly Dictionary<ushort, (string name, DeviceKind kind, int leds, bool hw)> Models = new Dictionary<ushort, (string, DeviceKind, int, bool)>
        {
            [0x1B4B] = ("Corsair Dark Core RGB", DeviceKind.Mouse, 12, true), [0x1B7E] = ("Corsair Dark Core RGB Pro", DeviceKind.Mouse, 12, true),
            [0x1B5E] = ("Corsair Harpoon Wireless", DeviceKind.Mouse, 2, false), [0x1B4C] = ("Corsair Ironclaw Wireless", DeviceKind.Mouse, 6, false),
            [0x2B32] = ("Corsair Ironclaw Wireless SE", DeviceKind.Mouse, 6, false), [0x1B93] = ("Corsair Katar Pro", DeviceKind.Mouse, 2, true),
            [0x1BBA] = ("Corsair Katar Pro V2", DeviceKind.Mouse, 2, true), [0x1BAC] = ("Corsair Katar Pro XT", DeviceKind.Mouse, 2, true),
            [0x1BA4] = ("Corsair K55 RGB PRO", DeviceKind.Keyboard, 6, false), [0x1B6E] = ("Corsair K57 RGB", DeviceKind.Keyboard, 137, false),
            [0x1BA0] = ("Corsair K60 RGB PRO", DeviceKind.Keyboard, 123, false), [0x1B8D] = ("Corsair K60 RGB PRO SE", DeviceKind.Keyboard, 123, false),
            [0x1BAD] = ("Corsair K60 RGB PRO Low Profile", DeviceKind.Keyboard, 123, false), [0x1BC7] = ("Corsair K60 RGB PRO TKL", DeviceKind.Keyboard, 123, true),
            [0x1BED] = ("Corsair K60 RGB PRO TKL", DeviceKind.Keyboard, 123, true), [0x1BFD] = ("Corsair K70 Core RGB", DeviceKind.Keyboard, 123, true),
            [0x2B01] = ("Corsair K70 Core RGB TKL", DeviceKind.Keyboard, 123, true), [0x1B73] = ("Corsair K70 RGB TKL", DeviceKind.Keyboard, 129, true),
            [0x1BB9] = ("Corsair K70 RGB TKL Champion", DeviceKind.Keyboard, 129, true), [0x1BC4] = ("Corsair K70 RGB PRO", DeviceKind.Keyboard, 142, true),
            [0x1BB3] = ("Corsair K70 RGB PRO V2", DeviceKind.Keyboard, 142, true), [0x1B89] = ("Corsair K95 RGB Platinum XT", DeviceKind.Keyboard, 156, true),
            [0x1B7D] = ("Corsair K100 RGB", DeviceKind.Keyboard, 193, true), [0x1B7C] = ("Corsair K100 RGB Optical", DeviceKind.Keyboard, 193, true),
            [0x1BC5] = ("Corsair K100 RGB Optical", DeviceKind.Keyboard, 193, true),
            [0x1B70] = ("Corsair M55 RGB PRO", DeviceKind.Mouse, 2, false), [0x1B9E] = ("Corsair M65 RGB Ultra", DeviceKind.Mouse, 3, false),
            [0x1BB5] = ("Corsair M65 RGB Ultra Wireless", DeviceKind.Mouse, 2, true), [0x1BF0] = ("Corsair M75", DeviceKind.Mouse, 2, true),
            [0x1BF1] = ("Corsair M75 Wireless", DeviceKind.Mouse, 2, true), [0x1BE3] = ("Corsair Scimitar Elite RGB", DeviceKind.Mouse, 5, true),
            [0x1B9B] = ("Corsair MM700", DeviceKind.Mousepad, 3, false), [0x1BC9] = ("Corsair MM700 3XL", DeviceKind.Mousepad, 3, false),
        };

        // Slipstream receivers: the paired device answers with its own pid
        static readonly ushort[] Receivers = { 0x2B00, 0x1BDC, 0x1BA6, 0x1B66, 0x1B65 };

        public IEnumerable<RgbDevice> Detect(DetectContext ctx)
        {
            var list = ctx.HidDevices.Where(d => d.VendorId == 0x1B1C && d.Interface == 1 && d.UsagePage == 0xFF42
                                              && (Models.ContainsKey(d.ProductId) || Receivers.Contains(d.ProductId)));
            foreach (var g in HidSelect.Physical(list))
            {
                RgbDevice dev = null;
                try { dev = CorsairPeripheralV2.TryCreate(g.First(), Receivers.Contains(g.First().ProductId)); }
                catch (Exception ex) { Log.Error("Corsair V2", ex); }
                if (dev != null) yield return dev;
            }
        }
    }

    public sealed class CorsairPeripheralV2 : RgbDevice
    {
        const int Size = 65;
        readonly HidDevice _hid;
        readonly byte _write;
        readonly bool _skipReads;
        byte _lightCtrl = 0x22;
        readonly bool _hw;
        readonly int _leds;
        byte[] _last;
        DateTime _lastSent;
        const string Ok = "Doğrudan kontrol · deneysel";

        public override string[] ConflictingProcesses => new[] { "iCUE", "Corsair.Service" };

        CorsairPeripheralV2(HidDevice hid, byte write, bool skipReads, ushort pid, (string name, DeviceKind kind, int leds, bool hw) m)
        {
            _hid = hid; _write = write; _skipReads = skipReads; _hw = m.hw; _leds = m.leds;
            Id = HidSelect.Id(hid.Info) + ":" + pid.ToString("x4");
            Name = m.name; Vendor = "Corsair"; Kind = m.kind;
            MaxFps = 30;
            if (m.kind == DeviceKind.Keyboard && m.leds > 20)
                Zones.Add(new RgbZone("Tuşlar", m.leds, x: Enumerable.Range(0, m.leds).Select(i => KeyX(i)).ToArray()));
            else
                Zones.Add(new RgbZone(m.kind == DeviceKind.Keyboard ? "Bölgeler" : "LED'ler", m.leds));
        }

        /// <summary>Corsair key codes are HID usages, except the modifiers and menu key, which sit at 101-111.</summary>
        static double KeyX(int code)
        {
            switch (code)
            {
                case 101: return HidKeyLayout.X(0x65);
                case 105: return HidKeyLayout.X(0xE0);
                case 106: return HidKeyLayout.X(0xE1);
                case 107: return HidKeyLayout.X(0xE2);
                case 108: return HidKeyLayout.X(0xE3);
                case 109: return HidKeyLayout.X(0xE4);
                case 110: return HidKeyLayout.X(0xE5);
                case 111: return HidKeyLayout.X(0xE6);
                case 122: return HidKeyLayout.X(0xE7);
                default: return code < 0xE0 ? HidKeyLayout.X((byte)code) : 0.5;
            }
        }

        internal static CorsairPeripheralV2 TryCreate(HidDeviceInfo info, bool receiver)
        {
            var hid = HidDevice.Open(info);
            byte write = 0x08;
            bool skip = false;
            if (receiver) write = 0x09;
            if (info.ProductId == 0x1B6E) { write = 0x80; skip = true; } // K57 wired

            uint pid = 0;
            for (int i = 0; i < 8 && (pid == 0 || pid == uint.MaxValue); i++)
            {
                pid = GetAddress(hid, write, 0x12);
                if (pid == 0 || pid == uint.MaxValue) Thread.Sleep(120);
            }
            ushort id = pid == 0 || pid == uint.MaxValue ? info.ProductId : (ushort)pid;
            if (!CorsairPeripheralV2Provider.Models.TryGetValue(id, out var m))
            {
                Log.Info($"Corsair V2 {info.ProductId:X4}: bağlı cihaz {id:X4} listede yok" + (receiver ? " (alıcıya bağlı cihaz kapalı olabilir)" : ""));
                hid.Dispose();
                return null;
            }
            var dev = new CorsairPeripheralV2(hid, write, skip, id, m);
            if (info.ProductId == 0x1B6E) dev._lightCtrl = 0x01;
            return dev;
        }

        static byte[] Packet(byte write, byte cmd, params byte[] args)
        {
            var p = new byte[Size];
            p[1] = write; p[2] = cmd;
            Array.Copy(args, 0, p, 3, Math.Min(args.Length, Size - 3));
            return p;
        }

        static uint GetAddress(HidDevice hid, byte write, byte address)
        {
            hid.FlushInput();
            if (!hid.Write(Packet(write, 0x02, address))) return uint.MaxValue;
            var r = hid.Read(50);
            if (r == null || r.Length < 7) return uint.MaxValue;
            if (r[2] != 0) return uint.MaxValue; // error reply
            return (uint)(r[6] << 24 | r[5] << 16 | r[4] << 8 | r[3]);
        }

        byte[] Send(byte cmd, params byte[] args)
        {
            _hid.Write(Packet(_write, cmd, args));
            return _skipReads ? null : _hid.Read(50);
        }

        public override void Initialize()
        {
            Send(0x01, 0x03, 0x00, 0x02); // render mode: software
            Send(0x02, 0x5F);             // lighting control
            if (_lightCtrl == 0x22 && !_skipReads)
            {
                // older firmware has its lighting endpoint at 0x01
                var r = Send(0x0D, 0x00, _lightCtrl);
                if (r != null && r.Length > 2 && r[2] > 0) { _lightCtrl = 0x01; Send(0x0D, 0x00, _lightCtrl); }
                Send(0x05, 0x01, 0x00);
            }
            _last = null;
            Status = Ok;
        }

        public override void Apply()
        {
            var c = Zones[0].Colors;
            byte[] data;
            if (_hw && _lightCtrl == 0x22)
            {
                data = new byte[2 + _leds * 3];
                data[0] = 0x12; data[1] = 0x00; // direct mode
                for (int i = 0; i < c.Length; i++) { data[2 + i * 3] = c[i].R; data[3 + i * 3] = c[i].G; data[4 + i * 3] = c[i].B; }
            }
            else
            {
                data = new byte[_leds * 3]; // R plane, G plane, B plane
                for (int i = 0; i < c.Length; i++) { data[i] = c[i].R; data[_leds + i] = c[i].G; data[_leds * 2 + i] = c[i].B; }
            }
            bool keepalive = (DateTime.Now - _lastSent).TotalSeconds > 20; // the device drops back to hardware mode after ~30 s
            if (!keepalive && _last != null && _last.SequenceEqual(data)) return;
            bool ok = WriteBlock(data);
            if (ok) { _last = data; _lastSent = DateTime.Now; }
            Status = ok ? Ok : "Yazma hatası";
        }

        bool WriteBlock(byte[] data)
        {
            if (!_skipReads) { while (_hid.Read(3) != null) { } }
            Send(0x0D, 0x00, _lightCtrl);
            var p = new byte[Size];
            p[1] = _write; p[2] = 0x06; p[4] = (byte)data.Length; p[5] = (byte)(data.Length >> 8);
            int first = Math.Min(data.Length, Size - 8);
            Array.Copy(data, 0, p, 8, first);
            bool ok = _hid.Write(p);
            if (!_skipReads && _hid.Read(3) == null) Thread.Sleep(2);
            int index = first;
            while (index < data.Length)
            {
                var q = new byte[Size];
                q[1] = _write; q[2] = 0x07;
                int n = Math.Min(data.Length - index, Size - 4);
                Array.Copy(data, index, q, 4, n);
                ok &= _hid.Write(q);
                Thread.Sleep(2);
                index += n;
            }
            Send(0x05, 0x01, 0x00);
            return ok;
        }

        public override void Shutdown(Rgb color)
        {
            base.Shutdown(color);
            Send(0x01, 0x03, 0x00, 0x01); // back to hardware mode
        }

        public override void Dispose() => _hid.Dispose();
    }
}
