using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RgbStone.Core;
using RgbStone.Transport;

namespace RgbStone.Drivers
{
    /// <summary>HID++ 2.0 conversation with one device (wired, or one slot of a LIGHTSPEED receiver).</summary>
    sealed class HidppLink : IDisposable
    {
        public const byte SwId = 0x0A;
        public readonly HidDevice Long;
        public readonly byte Index;

        public HidppLink(HidDevice longEp, byte index) { Long = longEp; Index = index; }

        public bool Send(byte feature, byte function, params byte[] args)
        {
            var p = new byte[20];
            p[0] = 0x11; p[1] = Index; p[2] = feature; p[3] = (byte)((function << 4) | SwId);
            Array.Copy(args, 0, p, 4, Math.Min(args.Length, 16));
            return Long.Write(p, 200);
        }

        /// <summary>Sends and waits for the matching reply. Null on timeout, on an error reply, or when the slot is empty.</summary>
        public byte[] Request(byte feature, byte function, int timeoutMs, params byte[] args)
        {
            Long.FlushInput();
            if (!Send(feature, function, args)) return null;
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < until)
            {
                var r = Long.Read(Math.Max(10, (int)(until - DateTime.UtcNow).TotalMilliseconds));
                if (r == null) return null;
                if (r.Length < 5 || r[1] != Index) continue;
                if (r[0] == 0x10 && r[2] == 0x8F) return null;                       // HID++ 1.0 error: nothing paired / asleep
                if (r[0] != 0x11) continue;
                if (r[2] == 0xFF && r[3] == feature) return null;                     // HID++ 2.0 error
                if (r[2] == feature && r[3] == ((function << 4) | SwId)) return r;
            }
            return null;
        }

        /// <summary>Feature index for a feature page; 0 when the device lacks it, -1 when no device answers.</summary>
        public int FeatureIndex(ushort page, int timeoutMs = 300)
        {
            var r = Request(0x00, 0x0, timeoutMs, (byte)(page >> 8), (byte)page);
            return r == null ? -1 : r[4];
        }

        /// <summary>Marketing name through feature 0x0005 (DeviceName).</summary>
        public string Name()
        {
            int f = FeatureIndex(0x0005);
            if (f <= 0) return null;
            var len = Request((byte)f, 0x0, 300);
            if (len == null) return null;
            var sb = new StringBuilder();
            while (sb.Length < len[4])
            {
                var part = Request((byte)f, 0x1, 300, (byte)sb.Length);
                if (part == null) break;
                for (int i = 4; i < part.Length && sb.Length < len[4]; i++) sb.Append((char)part[i]);
            }
            return sb.ToString().Trim('\0', ' ');
        }

        public void Dispose() => Long.Dispose();
    }

    /// <summary>
    /// Logitech G mice (and other zone-lit devices) with feature 0x8071 (RGB effects, "Hero" generation) or 0x8070 (color LED effects).
    /// Every zone gets one color; zones the device does not have are dropped on the first error.
    /// </summary>
    public sealed class LogitechZoneDevice : RgbDevice
    {
        readonly HidppLink _link;
        readonly byte _f8071, _f8070;
        readonly List<int> _zoneIds = new List<int>();
        Rgb[] _last;

        public override string[] ConflictingProcesses => new[] { "lghub_agent", "lghub" };

        internal LogitechZoneDevice(HidppLink link, string name, byte f8071, byte f8070, string idBase)
        {
            _link = link; _f8071 = f8071; _f8070 = f8070;
            Id = idBase + ":" + link.Index;
            Name = name;
            Vendor = "Logitech";
            Kind = DeviceKind.Mouse;
            MaxFps = 20;
            // Logitech G mice light at most two zones (primary + logo).
            for (int z = 0; z < 2; z++) { _zoneIds.Add(z); Zones.Add(new RgbZone(z == 0 ? "Ana bölge" : "Logo", 1)); }
        }

        public override void Initialize()
        {
            if (_f8071 > 0) _link.Request(_f8071, 0x5, 300, 0x01, 0x03, 0x05);   // SW control, no flash writes
            else _link.Request(_f8070, 0x8, 300, 0x01, 0x01);                    // 8070 SetSWControl

            // Find which zones exist: a zone the device lacks answers with an error.
            for (int i = _zoneIds.Count - 1; i > 0; i--)
                if (!SetZone(_zoneIds[i], Rgb.Black, true)) { _zoneIds.RemoveAt(i); Zones.RemoveAt(i); }
            _last = null;
            Status = "Doğrudan kontrol";
        }

        bool SetZone(int zone, Rgb c, bool waitReply)
        {
            byte[] args = _f8071 > 0
                ? new byte[] { (byte)zone, 0x01, c.R, c.G, c.B, 0x00, 0, 0, 0, 0, 0x01 } // 8071 fn1 SetRgbClusterEffect, fixed, persist=0
                : new byte[] { (byte)zone, 0x01, c.R, c.G, c.B, 0x02 };                // 8070 fn3 SetZoneEffect, fixed, volatile
            byte feature = _f8071 > 0 ? _f8071 : _f8070;
            byte fn = (byte)(_f8071 > 0 ? 0x1 : 0x3);
            return waitReply ? _link.Request(feature, fn, 300, args) != null : _link.Send(feature, fn, args);
        }

        public override void Apply()
        {
            if (_last == null) _last = new Rgb[Zones.Count];
            bool ok = true;
            for (int i = 0; i < Zones.Count; i++)
            {
                var c = Zones[i].Colors[0];
                if (_last[i] == c && _last != null) continue;
                ok &= SetZone(_zoneIds[i], c, false);
                _last[i] = c;
            }
            _link.Long.FlushInput();
            Status = ok ? "Doğrudan kontrol" : "Fare yanıt vermiyor (uyku?)";
        }

        public override void Dispose() => _link.Dispose();
    }
}
