using System;
using System.Collections.Generic;
using System.Linq;
using RgbStone.Core;
using RgbStone.Transport;

namespace RgbStone.Drivers
{
    /// <summary>
    /// Corsair Commander Core (iCUE Elite AIO pump + 6 fan ports) and Commander Core XT. Endpoint-based protocol: open
    /// endpoint 0x22, write the color block as data type 0x12, close. The controller falls back to its own effect when it
    /// hears nothing for ~10 s, so the last frame is repeated. Protocol reference: OpenRGB.
    /// Experimental: no Commander Core on the test PC.
    /// </summary>
    public sealed class CorsairCommanderCoreProvider : IDeviceProvider
    {
        public string Name => "Corsair Commander Core / XT";

        static readonly ushort[] Pids = { 0x0C1C, 0x0C32, 0x0C1D, 0x0C3C, 0x0C3D, 0x0C3E, 0x0C2A };

        public IEnumerable<RgbDevice> Detect(DetectContext ctx)
        {
            foreach (var g in HidSelect.Physical(HidSelect.ByVidPid(ctx, 0x1B1C, Pids)))
            {
                var info = g.FirstOrDefault(d => d.Interface == 0 && d.UsagePage == 0xFF42) ?? g.FirstOrDefault(d => d.UsagePage == 0xFF42);
                if (info == null) continue;
                RgbDevice dev = null;
                try { dev = CorsairCommanderCore.TryCreate(info); }
                catch (Exception ex) { Log.Error("Corsair Commander Core", ex); }
                if (dev != null) yield return dev;
            }
        }
    }

    public sealed class CorsairCommanderCore : RgbDevice
    {
        const int FanMax = 34;
        readonly HidDevice _hid;
        readonly bool _xt;
        readonly int _packet;
        byte[] _last;
        DateTime _lastSent;
        int _xtLeds = -1;
        const string Ok = "Doğrudan kontrol · deneysel";

        public override string[] ConflictingProcesses => new[] { "iCUE", "Corsair.Service" };

        CorsairCommanderCore(HidDevice hid, bool xt, int packet, int pumpLeds, IList<int> fanLeds)
        {
            _hid = hid; _xt = xt; _packet = packet;
            Id = HidSelect.Id(hid.Info);
            Name = xt ? "Corsair Commander Core XT" : "Corsair Commander Core";
            Vendor = "Corsair";
            Kind = xt ? DeviceKind.LedController : DeviceKind.Cooler;
            MaxFps = 30;
            if (xt) Zones.Add(new RgbZone("RGB başlığı", 0, resizable: true, maxLeds: 204));
            else Zones.Add(new RgbZone("Pompa", pumpLeds));
            for (int i = 0; i < 6; i++)
                Zones.Add(new RgbZone($"Fan portu {i + 1}", 0, resizable: true, maxLeds: FanMax) { DefaultLeds = i + 1 < fanLeds.Count ? Math.Min(FanMax, fanLeds[i + 1]) : 0 });
        }

        internal static CorsairCommanderCore TryCreate(HidDeviceInfo info)
        {
            var hid = HidDevice.Open(info);
            bool xt = info.ProductId == 0x0C2A;
            int packet = info.OutputLength > 0 ? info.OutputLength : 97;
            var version = Command(hid, packet, 0x02, 0x13);
            if (version == null) { hid.Dispose(); Log.Warn("Corsair Commander Core: cevap yok."); return null; }
            Log.Info($"Corsair Commander Core{(xt ? " XT" : "")} yazılım v{version[0]}.{version[1]}.{version[2]}, paket {packet} bayt");

            var counts = ReadEndpoint(hid, packet, 0x20);
            var leds = new List<int>();
            if (counts != null)
                for (int i = 0; i < counts[2] && i * 4 + 6 < counts.Length; i++) leds.Add(counts[i * 4 + 6] << 8 | counts[i * 4 + 5]);
            int pump = leds.Count > 0 && leds[0] > 0 ? leds[0] : 29;
            return new CorsairCommanderCore(hid, xt, packet, pump, leds);
        }

        /// <summary>[0x00, 0x08, cmd0, cmd1, data...] then the reply (report id 0x00); returns the reply from byte 3.</summary>
        static byte[] Command(HidDevice hid, int packet, byte c0, byte c1, byte[] data = null)
        {
            var p = new byte[packet];
            p[1] = 0x08; p[2] = c0; p[3] = c1;
            if (data != null) Array.Copy(data, 0, p, 4, Math.Min(data.Length, packet - 4));
            hid.FlushInput();
            if (!hid.Write(p, 300)) return null;
            for (int i = 0; i < 8; i++)
            {
                var r = hid.Read(300);
                if (r == null) return null;
                if (r[0] == 0x00) return r.Skip(3).ToArray();
            }
            return null;
        }

        static byte[] ReadEndpoint(HidDevice hid, int packet, byte endpoint)
        {
            Command(hid, packet, 0x0D, 0x00, new byte[] { endpoint, 0x00 });
            var res = Command(hid, packet, 0x08, 0x00);
            Command(hid, packet, 0x05, 0x01);
            return res;
        }

        bool WriteEndpoint(byte endpoint, byte type, byte[] data)
        {
            bool ok = Command(_hid, _packet, 0x0D, 0x00, new byte[] { endpoint, 0x00 }) != null;
            int index = 0;
            bool first = true;
            while (index < data.Length || first)
            {
                if (first)
                {
                    int len = Math.Min(data.Length, _packet - 10);
                    var head = new byte[len + 6];
                    int real = data.Length + 2;
                    head[0] = (byte)real; head[1] = (byte)(real >> 8);
                    head[4] = type; head[5] = 0x00;
                    Array.Copy(data, 0, head, 6, len);
                    ok &= Command(_hid, _packet, 0x06, 0x00, head) != null;
                    index += len;
                    first = false;
                }
                else
                {
                    int len = Math.Min(data.Length - index, _packet - 4);
                    var part = new byte[len];
                    Array.Copy(data, index, part, 0, len);
                    ok &= Command(_hid, _packet, 0x07, 0x00, part) != null;
                    index += len;
                }
            }
            ok &= Command(_hid, _packet, 0x05, 0x01) != null;
            return ok;
        }

        void SetFanMode(bool externalPort)
        {
            var b = new byte[15];
            b[0] = 0x07;
            int start = 3, end = 15;
            if (_xt)
            {
                if (externalPort) { b[1] = 0x01; b[2] = 0x01; }
                else { start = 2; end = 14; }
            }
            else { b[1] = 0x01; b[2] = 0x08; }
            for (int i = start; i < end; i += 2) { b[i] = 0x01; b[i + 1] = 0x06; }
            WriteEndpoint(0x1E, 0x0D, b);
        }

        public override void Initialize()
        {
            SetFanMode(_xt && Zones[0].LedCount > 0);
            Command(_hid, _packet, 0x01, 0x03, new byte[] { 0x00, 0x02 }); // software mode
            _xtLeds = -1;
            _last = null;
            Status = Ok;
        }

        public override void Apply()
        {
            if (_xt && Zones[0].LedCount != _xtLeds)
            {
                _xtLeds = Zones[0].LedCount;
                SetFanMode(_xtLeds > 0);
                if (_xtLeds > 0) { var b = new byte[15]; b[0] = 0x07; b[1] = (byte)_xtLeds; WriteEndpoint(0x1D, 0x0C, b); }
                _last = null;
            }

            var data = new List<byte>();
            for (int z = 0; z < Zones.Count; z++)
            {
                var c = Zones[z].Colors;
                foreach (var col in c) { data.Add(col.R); data.Add(col.G); data.Add(col.B); }
                if (z != 0) for (int i = c.Length; i < FanMax; i++) { data.Add(0); data.Add(0); data.Add(0); } // fan ports are fixed 34-LED slots
            }
            var bytes = data.ToArray();
            bool keepalive = (DateTime.Now - _lastSent).TotalSeconds > 5;
            if (!keepalive && _last != null && _last.SequenceEqual(bytes)) return;
            bool ok = WriteEndpoint(0x22, 0x12, bytes);
            if (ok) { _last = bytes; _lastSent = DateTime.Now; }
            Status = ok ? Ok : "Yazma hatası";
        }

        public override void Shutdown(Rgb color)
        {
            base.Shutdown(color);
            Command(_hid, _packet, 0x01, 0x03, new byte[] { 0x00, 0x01 }); // back to hardware mode
        }

        public override void Dispose() => _hid.Dispose();
    }
}
