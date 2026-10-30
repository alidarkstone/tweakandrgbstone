using System;
using System.Collections.Generic;
using System.Linq;
using RgbStone.Core;
using RgbStone.Transport;

namespace RgbStone.Drivers
{
    /// <summary>
    /// Logitech G keyboards speaking HID++ 2.0 with the 0x8071 (RGB effects) and 0x8081 (per-key lighting v2) features,
    /// wired or through a LIGHTSPEED receiver.
    /// </summary>
    public sealed class LogitechHidppProvider : IDeviceProvider
    {
        public string Name => "Logitech HID++ (G klavyeler)";

        internal sealed class Model
        {
            public string Name;
            public byte[] Keys;     // HID++ zone ids, in layout order
            public double[] X;      // horizontal positions in 0..1
        }

        // PRO X TKL key zone ids and their columns (0..16), taken from the keyboard's key matrix.
        static readonly Model ProXTkl = BuildModel("Logitech PRO X TKL",
            new byte[]
            {
                150, 155, 152, 154, 153,
                38, 55, 56, 57, 58, 59, 60, 61, 62, 63, 64, 65, 66, 67, 68, 69,
                50, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 42, 43, 39, 70, 71, 72,
                40, 17, 23, 5, 18, 20, 25, 21, 9, 15, 16, 44, 45, 46, 73, 74, 75,
                54, 1, 19, 4, 6, 7, 8, 10, 11, 12, 48, 49, 37,
                105, 26, 24, 3, 22, 2, 14, 13, 51, 52, 53, 109, 79,
                104, 107, 106, 41, 110, 111, 98, 108, 77, 78, 76,
                97, 47,
            },
            new[]
            {
                3, 10, 11, 12, 13,
                0, 1, 2, 3, 4, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
                0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
                0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
                0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 13,
                0, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15,
                0, 1, 2, 6, 10, 11, 12, 13, 14, 15, 16,
                1, 12,
            }, 16);

        static Model BuildModel(string name, byte[] keys, int[] cols, int maxCol) =>
            new Model { Name = name, Keys = keys, X = cols.Select(c => (double)c / maxCol).ToArray() };

        // Device ids reported by HID++ DeviceInfo (wireless/wired transport ids) and USB PIDs for wired mode.
        static readonly Dictionary<ushort, Model> ByUsbPid = new Dictionary<ushort, Model> { [0xC352] = ProXTkl, [0xC35B] = ProXTkl };
        static readonly ushort[] Receivers = { 0xC547, 0xC539, 0xC53A, 0xC541, 0xC545 };

        public IEnumerable<RgbDevice> Detect(DetectContext ctx)
        {
            var all = ctx.HidDevices.Where(d => d.VendorId == 0x046D && d.UsagePage == 0xFF00).ToList();
            foreach (var g in all.GroupBy(d => d.ParentKey))
            {
                var shortEp = g.FirstOrDefault(d => d.Usage == 0x0001);
                var longEp = g.FirstOrDefault(d => d.Usage == 0x0002);
                if (longEp == null) continue;
                ushort pid = longEp.ProductId;
                bool receiver = Receivers.Contains(pid) || (longEp.Product ?? "").IndexOf("Receiver", StringComparison.OrdinalIgnoreCase) >= 0;
                var found = new List<RgbDevice>();
                try { found = Probe(shortEp, longEp, receiver, pid); }
                catch (Exception ex) { Log.Error("Logitech", ex); }
                foreach (var d in found) yield return d;
            }
        }

        /// <summary>Asks every slot (1-6 on a receiver, 0xFF when wired) what it is and which lighting features it has.</summary>
        static List<RgbDevice> Probe(HidDeviceInfo shortEp, HidDeviceInfo longEp, bool receiver, ushort pid)
        {
            var result = new List<RgbDevice>();
            var slots = receiver ? new byte[] { 1, 2, 3, 4, 5, 6 } : new byte[] { 0xFF };
            foreach (byte index in slots)
            {
                var link = new HidppLink(HidDevice.Open(longEp), index);
                bool keep = false;
                try
                {
                    int f8081 = link.FeatureIndex(0x8081, 250);
                    if (f8081 < 0) { Log.Info($"Logitech {pid:X4} kanal {index}: cevap yok (boş ya da cihaz uykuda)"); continue; }
                    int f8071 = link.FeatureIndex(0x8071);
                    int f8070 = link.FeatureIndex(0x8070);
                    string name = link.Name() ?? $"Logitech {pid:X4}";
                    Log.Info($"Logitech {pid:X4} kanal {index}: '{name}' 8081=#{f8081} 8071=#{f8071} 8070=#{f8070}");

                    if (f8081 > 0)
                    {
                        Model m = ByUsbPid.TryGetValue(pid, out var known) ? known
                            : name.IndexOf("PRO X TKL", StringComparison.OrdinalIgnoreCase) >= 0 ? ProXTkl : null;
                        if (m == null) { Log.Warn($"'{name}': tuş haritası yok, şimdilik atlandı."); continue; }
                        var kb = LogitechKeyboard.TryCreate(shortEp, longEp, index, m);
                        if (kb != null) result.Add(kb);
                    }
                    else if (f8071 > 0 || f8070 > 0)
                    {
                        result.Add(new LogitechZoneDevice(link, name, (byte)Math.Max(0, f8071), (byte)Math.Max(0, f8070), HidSelect.Id(longEp)));
                        keep = true;
                    }
                    else
                    {
                        Log.Info($"'{name}': kontrol edilebilir RGB ışığı yok.");
                        Engine.MarkNoLighting(longEp.VendorId, longEp.ProductId);
                    }
                }
                finally { if (!keep) link.Dispose(); }
            }
            return result;
        }

        public sealed class LogitechKeyboard : RgbDevice
        {
            const byte SwId = 0x0A;
            readonly HidDevice _long;
            readonly HidDevice _short;
            readonly byte _index;
            readonly byte[] _keys;
            byte _f8071, _f8081;
            Rgb[] _last;
            bool _singleValueOk;

            public override string[] ConflictingProcesses => new[] { "lghub_agent", "lghub" };
            public override bool ConflictNeedsWindow => true; // lghub_agent restarts in the background; only the G HUB window is the user

            LogitechKeyboard(HidDevice shortEp, HidDevice longEp, byte index, Model m)
            {
                _short = shortEp; _long = longEp; _index = index; _keys = m.Keys;
                Id = HidSelect.Id(longEp.Info) + ":" + index;
                Name = m.Name;
                Vendor = "Logitech";
                Kind = DeviceKind.Keyboard;
                MaxFps = 25;
                Zones.Add(new RgbZone("Tuşlar", m.Keys.Length, x: m.X));
            }

            internal static LogitechKeyboard TryCreate(HidDeviceInfo shortInfo, HidDeviceInfo longInfo, byte index, Model m)
            {
                var lng = HidDevice.Open(longInfo);
                HidDevice sht = null;
                try { if (shortInfo != null) sht = HidDevice.Open(shortInfo); } catch { }
                var kb = new LogitechKeyboard(sht, lng, index, m);
                kb._f8071 = kb.FeatureIndex(0x8071);
                kb._f8081 = kb.FeatureIndex(0x8081);
                if (kb._f8081 == 0)
                {
                    Log.Info($"Logitech {longInfo.ProductId:X4}/{index}: per-key aydınlatma yok, atlandı.");
                    kb.Dispose();
                    return null;
                }
                Log.Info($"{m.Name}: 8071=#{kb._f8071} 8081=#{kb._f8081}");
                return kb;
            }

            /// <summary>Root feature (index 0) getFeature: page -> feature index on this device. 0 when absent.</summary>
            byte FeatureIndex(ushort page)
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    _long.FlushInput();
                    Send(0x00, 0x0, (byte)(page >> 8), (byte)page);
                    for (int i = 0; i < 10; i++)
                    {
                        var r = _long.Read(150);
                        if (r == null) break;
                        if (r[0] != 0x11 || r[1] != _index) continue;
                        if (r[2] == 0xFF) return 0;                              // error reply
                        if (r[2] == 0x00 && r[3] == ((0x0 << 4) | SwId)) return r[4];
                    }
                }
                return 0;
            }

            /// <summary>Long HID++ message: [0x11, device, feature, function|swid, params...] (20 bytes).</summary>
            bool Send(byte feature, byte function, params byte[] args)
            {
                var p = new byte[20];
                p[0] = 0x11; p[1] = _index; p[2] = feature; p[3] = (byte)((function << 4) | SwId);
                Array.Copy(args, 0, p, 4, Math.Min(args.Length, 16));
                return _long.Write(p, 200);
            }

            public override void Initialize()
            {
                // 8071 fn5 SetSWControl: take over from onboard effects, no flash writes.
                if (_f8071 != 0) Send(_f8071, 0x5, 0x01, 0x03, 0x05);
                _last = null;

                // Probe 8081 fn6 setRgbZonesSingleValue (one color, up to 13 keys per packet).
                _long.FlushInput();
                Send(_f8081, 0x6, 0, 0, 0, _keys[0]);
                _singleValueOk = true;
                for (int i = 0; i < 5; i++)
                {
                    var r = _long.Read(100);
                    if (r == null) break;
                    if (r[0] == 0x11 && r[1] == _index && r[2] == 0xFF && r[3] == _f8081) { _singleValueOk = false; break; }
                }
                Status = "Doğrudan kontrol";
            }

            // A wireless keyboard that slept comes back on its onboard effect, and G HUB / Dynamic Lighting may repaint it
            // right after it reconnects. rgbstone only sends changed keys, so with a static color it would never repaint.
            // On a reconnect notice the keyboard is taken over again (twice, to land after anything else that reacts to it),
            // and every RefreshEvery the whole frame is sent again in case the notice was missed.
            static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(5);
            DateTime _nextRefresh = DateTime.MinValue;
            readonly Queue<DateTime> _retake = new Queue<DateTime>();

            void CheckReconnect()
            {
                if (_short == null) return;
                for (int i = 0; i < 16; i++)
                {
                    var r = _short.Read(0);
                    if (r == null) break;
                    // HID++ 1.0 device connection notice (0x41); bit 6 of the flags set means the link is down.
                    if (r.Length > 4 && r[0] == 0x10 && r[1] == _index && r[2] == 0x41 && (r[4] & 0x40) == 0)
                    {
                        Log.Info(Name + ": klavye yeniden bağlandı (uykudan uyandı), kontrol geri alınıyor.");
                        _retake.Clear();
                        _retake.Enqueue(DateTime.Now.AddSeconds(1));
                        _retake.Enqueue(DateTime.Now.AddSeconds(4));
                    }
                }
            }

            public override void Apply()
            {
                CheckReconnect();
                var now = DateTime.Now;
                if (_retake.Count > 0 && now >= _retake.Peek()) { _retake.Dequeue(); Initialize(); }
                else if (now >= _nextRefresh) { if (_f8071 != 0) Send(_f8071, 0x5, 0x01, 0x03, 0x05); _last = null; }
                if (_last == null) _nextRefresh = now + RefreshEvery;

                var colors = Zones[0].Colors;
                var changed = new List<int>();
                for (int i = 0; i < colors.Length; i++)
                    if (_last == null || !Close(colors[i], _last[i])) changed.Add(i);
                if (changed.Count == 0) return;

                bool ok = true;
                var individual = new List<int>();
                if (_singleValueOk)
                {
                    foreach (var grp in changed.GroupBy(i => colors[i]))
                    {
                        var idx = grp.ToList();
                        if (idx.Count < 3) { individual.AddRange(idx); continue; }
                        for (int s = 0; s < idx.Count; s += 13)
                        {
                            var args = new List<byte> { grp.Key.R, grp.Key.G, grp.Key.B };
                            args.AddRange(idx.Skip(s).Take(13).Select(i => _keys[i]));
                            ok &= Send(_f8081, 0x6, args.ToArray());
                        }
                    }
                }
                else individual = changed;

                for (int s = 0; s < individual.Count; s += 4)
                {
                    var args = new List<byte>();
                    foreach (int i in individual.Skip(s).Take(4))
                        args.AddRange(new[] { _keys[i], colors[i].R, colors[i].G, colors[i].B });
                    ok &= Send(_f8081, 0x1, args.ToArray());
                }
                ok &= Send(_f8081, 0x7); // frame end
                _long.FlushInput();

                if (_last == null) _last = new Rgb[colors.Length];
                Array.Copy(colors, _last, colors.Length);
                Status = ok ? "Doğrudan kontrol" : "Klavye yanıt vermiyor (uyku?)";
            }

            static bool Close(Rgb a, Rgb b) => Math.Abs(a.R - b.R) < 3 && Math.Abs(a.G - b.G) < 3 && Math.Abs(a.B - b.B) < 3;

            public override void Dispose() { _long.Dispose(); _short?.Dispose(); }
        }
    }
}
