using System;
using System.Collections.Generic;
using System.Linq;
using RgbStone.Core;
using RgbStone.Transport;

namespace RgbStone.Drivers
{
    /// <summary>
    /// Any device that implements the standard HID LampArray usage page (0x59), the protocol behind Windows Dynamic Lighting.
    /// Lamp count, positions and report layout are read from the device itself, so no per-model table is needed.
    /// </summary>
    public sealed class HidLampArrayProvider : IDeviceProvider, IFallbackProvider
    {
        public string Name => "HID LampArray (Windows Dinamik Aydınlatma standardı)";

        public IEnumerable<RgbDevice> Detect(DetectContext ctx)
        {
            foreach (var info in ctx.HidDevices.Where(d => d.UsagePage == 0x59 && d.Usage == 0x01 && d.FeatureLength > 0))
            {
                // A vendor's virtual LampArray (e.g. Logitech's "HID VHF Driver") mirrors hardware a dedicated driver already runs.
                string vid = $"hid:{info.VendorId:x4}:";
                if (ctx.Detected.Any(d => d.Id.StartsWith(vid)) && Environment.GetEnvironmentVariable("RGBSTONE_LAMPARRAY_ALL") != "1")
                {
                    Log.Info($"LampArray {info.VendorId:X4}:{info.ProductId:X4} atlandı, bu marka zaten doğrudan sürülüyor.");
                    continue;
                }
                RgbDevice dev;
                try { dev = HidLampArray.TryCreate(info); }
                catch (Exception ex) { Log.Error("LampArray", ex); dev = null; }
                if (dev != null) yield return dev;
            }
        }
    }

    public sealed class HidLampArray : RgbDevice
    {
        const ushort Page = 0x59;
        const ushort LampCount = 0x03, BoundingBoxWidth = 0x04, LampArrayKind = 0x07, MinUpdateInterval = 0x08;
        const ushort LampId = 0x21, PositionX = 0x23, LampAttributesResponse = 0x22;
        const ushort RedChannel = 0x51, GreenChannel = 0x52, BlueChannel = 0x53, IntensityChannel = 0x54, UpdateFlags = 0x55;
        const ushort AutonomousMode = 0x71;

        readonly HidDevice _hid;
        readonly List<HidDevice.ValueCap> _caps;
        byte _attrReport, _requestReport, _responseReport, _multiReport, _controlReport;
        int _perPacket, _idBytes;
        bool _standardLayout;
        readonly Dictionary<ushort, List<HidDevice.ValueCap>> _slots = new Dictionary<ushort, List<HidDevice.ValueCap>>();
        int[] _lampIds;
        Rgb[] _last;

        HidLampArray(HidDevice hid, List<HidDevice.ValueCap> caps)
        {
            _hid = hid;
            _caps = caps;
            Id = HidSelect.Id(hid.Info) + ":lamparray";
            Vendor = string.IsNullOrEmpty(hid.Info.Product) ? $"{hid.Info.VendorId:X4}" : hid.Info.Product;
            Name = hid.Info.Product.Length > 0 ? hid.Info.Product : $"LampArray {hid.Info.VendorId:X4}:{hid.Info.ProductId:X4}";
            Kind = DeviceKind.Other;
            MaxFps = 30;
        }

        byte ReportWith(ushort usage, ushort without = 0) =>
            _caps.Where(c => c.UsagePage == Page && c.Usage == usage)
                 .Select(c => c.ReportId)
                 .FirstOrDefault(id => without == 0 || !_caps.Any(c => c.ReportId == id && c.Usage == without));

        public static HidLampArray TryCreate(HidDeviceInfo info)
        {
            var hid = HidDevice.Open(info);
            var dev = new HidLampArray(hid, hid.ValueCaps(2));
            if (!dev.ReadLayout()) { hid.Dispose(); return null; }
            return dev;
        }

        bool ReadLayout()
        {
            _attrReport = ReportWith(BoundingBoxWidth);
            _responseReport = ReportWith(PositionX);
            _requestReport = ReportWith(LampId, PositionX);
            _multiReport = ReportWith(RedChannel, 0x61); // multi-update has red channels but no LampIdStart
            _controlReport = ReportWith(AutonomousMode);
            if (_attrReport == 0 && _caps.All(c => c.ReportId != 0)) { Log.Warn($"{Name}: LampArray öznitelik raporu yok."); return false; }

            var attr = _hid.GetFeature(_attrReport);
            uint count = attr == null ? 0 : _hid.GetValue(attr, Page, LampCount) ?? 0;
            uint width = attr == null ? 0 : _hid.GetValue(attr, Page, BoundingBoxWidth) ?? 0;
            uint kind = attr == null ? 0 : _hid.GetValue(attr, Page, LampArrayKind) ?? 0;
            if (count == 0 || count > 1024) { Log.Warn($"{Name}: lamba sayısı okunamadı."); return false; }
            Kind = kind switch { 1 => DeviceKind.Keyboard, 2 => DeviceKind.Mouse, 3 => DeviceKind.Other, 4 => DeviceKind.Other, 5 => DeviceKind.Other, 6 => DeviceKind.Other, 7 => DeviceKind.Headset, _ => DeviceKind.Other };

            // per-lamp X positions (micrometers) -> 0..1, so waves run across the device
            var x = new double[count];
            _lampIds = new int[count];
            for (int i = 0; i < count; i++)
            {
                _lampIds[i] = i;
                if (_requestReport == 0 || _responseReport == 0) { x[i] = count <= 1 ? 0 : (double)i / (count - 1); continue; }
                var req = _hid.NewFeatureReport(_requestReport);
                _hid.SetValue(req, Page, LampId, (uint)i);
                _hid.SetFeatureRaw(req);
                var resp = _hid.GetFeature(_responseReport);
                uint px = resp == null ? 0 : _hid.GetValue(resp, Page, PositionX) ?? 0;
                x[i] = width > 0 ? Math.Min(1, (double)px / width) : (double)i / Math.Max(1, count - 1);
            }

            var redCap = _caps.FirstOrDefault(c => c.ReportId == _multiReport && c.Usage == RedChannel);
            var idCap = _caps.FirstOrDefault(c => c.ReportId == _multiReport && c.Usage == LampId);
            if (redCap == null || idCap == null) { Log.Warn($"{Name}: çoklu güncelleme raporu yok."); return false; }
            // Colors are either one array field per channel or one field per lamp slot (same usage repeated, ordered by data index).
            foreach (var u in new[] { RedChannel, GreenChannel, BlueChannel, IntensityChannel })
                _slots[u] = _caps.Where(c => c.ReportId == _multiReport && c.Usage == u).OrderBy(c => c.DataIndex).ToList();
            int colorSlots = _slots[RedChannel].Count == 1 ? redCap.ReportCount : _slots[RedChannel].Count;
            _perPacket = Math.Max(1, Math.Min(colorSlots, idCap.ReportCount));
            // Windows' parser cannot fill the LampId array of some descriptors (HIDP_STATUS_NOT_VALUE_ARRAY). When the report has
            // exactly the size of the layout from the HID LampArray specification, it is packed by hand:
            // [id][LampCount][UpdateFlags][LampId x n][R G B I x n]
            _idBytes = Math.Max(1, idCap.BitSize / 8);
            _standardLayout = _hid.Info.FeatureLength == 3 + _perPacket * _idBytes + 4 * _perPacket;
            Log.Info($"{Name} çoklu rapor alanları: " + string.Join(", ", _caps.Where(c => c.ReportId == _multiReport)
                .Select(c => $"{c.Usage:X2}x{c.ReportCount}/{c.BitSize}b")));

            Zones.Add(new RgbZone("Lambalar", (int)count, x: x));
            Log.Info($"LampArray {Name}: {count} lamba, tür {kind}, paket başına {_perPacket}, raporlar attr {_attrReport} istek {_requestReport} yanıt {_responseReport} çoklu {_multiReport} kontrol {_controlReport}");
            return true;
        }

        public override void Initialize()
        {
            if (_controlReport != 0)
            {
                var r = _hid.NewFeatureReport(_controlReport);
                _hid.SetValue(r, Page, AutonomousMode, 0); // the host drives the lamps from now on
                _hid.SetFeatureRaw(r);
            }
            _last = null;
            Status = "Doğrudan kontrol";
        }

        public override void Apply()
        {
            var colors = Zones[0].Colors;
            var changed = Enumerable.Range(0, colors.Length).Where(i => _last == null || _last[i] != colors[i]).ToList();
            if (changed.Count == 0) return;

            bool ok = true;
            for (int s = 0; s < changed.Count; s += _perPacket)
            {
                var chunk = changed.Skip(s).Take(_perPacket).ToList();
                bool lastPacket = s + _perPacket >= changed.Count;
                if (_standardLayout)
                {
                    var m = new byte[_hid.Info.FeatureLength];
                    m[0] = _multiReport; m[1] = (byte)chunk.Count; m[2] = (byte)(lastPacket ? 1 : 0);
                    int o = 3;
                    for (int k = 0; k < _perPacket; k++, o += _idBytes)
                        if (k < chunk.Count) for (int b = 0; b < _idBytes; b++) m[o + b] = (byte)(_lampIds[chunk[k]] >> (8 * b));
                    for (int k = 0; k < chunk.Count; k++, o += 4)
                    {
                        var c = colors[chunk[k]];
                        m[o] = c.R; m[o + 1] = c.G; m[o + 2] = c.B; m[o + 3] = 255;
                    }
                    bool sentM = _hid.SetFeatureRaw(m);
                    if (!sentM) Warn($"SetFeature hata {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
                    ok &= sentM;
                    continue;
                }
                var r = _hid.NewFeatureReport(_multiReport);
                _hid.SetValue(r, Page, LampCount, (uint)chunk.Count);
                _hid.SetValue(r, Page, UpdateFlags, lastPacket ? 1u : 0u); // 1 = LampUpdateComplete
                if (!Array(r, LampId, chunk.Select(i => _lampIds[i]))) { ok = false; Warn($"lamba kimlikleri yazılamadı, durum 0x{_hid.LastStatus:X8}"); }
                ok &= Channel(r, RedChannel, chunk.Select(i => (int)colors[i].R));
                ok &= Channel(r, GreenChannel, chunk.Select(i => (int)colors[i].G));
                ok &= Channel(r, BlueChannel, chunk.Select(i => (int)colors[i].B));
                Channel(r, IntensityChannel, chunk.Select(i => 255));
                bool sent = _hid.SetFeatureRaw(r);
                if (!sent) Warn($"{Name}: SetFeature hata {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}, rapor {BitConverter.ToString(r, 0, Math.Min(24, r.Length))}");
                ok &= sent;
            }
            if (_last == null) _last = new Rgb[colors.Length];
            System.Array.Copy(colors, _last, colors.Length);
            Status = ok ? "Doğrudan kontrol" : "Yazma hatası";
        }

        bool Array(byte[] report, ushort usage, IEnumerable<int> values)
        {
            var cap = _caps.FirstOrDefault(c => c.ReportId == _multiReport && c.Usage == usage);
            if (cap == null) return false;
            int bytesPer = Math.Max(1, cap.BitSize / 8);
            var v = values.ToList();
            var buf = new byte[cap.ReportCount * bytesPer];
            for (int i = 0; i < v.Count && i < cap.ReportCount; i++)
                for (int b = 0; b < bytesPer; b++) buf[i * bytesPer + b] = (byte)(v[i] >> (8 * b)); // little endian
            return cap.ReportCount > 1 ? _hid.SetValueArray(report, Page, usage, buf) : _hid.SetValue(report, Page, usage, v.Count > 0 ? (uint)v[0] : 0u);
        }

        int _warnings;
        void Warn(string m) { if (_warnings++ < 5) Log.Warn($"{Name}: {m}"); }

        bool Channel(byte[] report, ushort usage, IEnumerable<int> values)
        {
            var slots = _slots.TryGetValue(usage, out var s) ? s : null;
            if (slots == null || slots.Count == 0) return false;
            if (slots.Count == 1) return Array(report, usage, values);
            var v = values.ToList();
            var data = new List<KeyValuePair<int, uint>>();
            for (int i = 0; i < v.Count && i < slots.Count; i++) data.Add(new KeyValuePair<int, uint>(slots[i].DataIndex, (uint)v[i]));
            bool ok = _hid.SetData(report, data);
            if (!ok) Warn($"kanal {usage:X2} yazılamadı");
            return ok;
        }

        public override void Dispose() => _hid.Dispose();
    }
}
