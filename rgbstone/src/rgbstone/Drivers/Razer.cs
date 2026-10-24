using System;
using System.Collections.Generic;
using System.Linq;
using RgbStone.Core;
using RgbStone.Transport;

namespace RgbStone.Drivers
{
    /// <summary>
    /// Razer Chroma devices (VID 0x1532) through the common 90-byte feature report protocol.
    /// Model tables are not needed: every modern Razer device accepts the "static color" matrix effect for all of its LEDs,
    /// so the whole device follows the shared effect clock as one zone. Experimental: no Razer hardware on the test PC.
    /// </summary>
    public sealed class RazerProvider : IDeviceProvider
    {
        public string Name => "Razer Chroma";

        public IEnumerable<RgbDevice> Detect(DetectContext ctx)
        {
            var razer = ctx.HidDevices.Where(d => d.VendorId == 0x1532 && d.FeatureLength >= 91).ToList();
            foreach (var g in razer.GroupBy(d => d.ProductId))
            {
                RgbDevice dev = null;
                foreach (var info in g.OrderBy(d => d.Interface))
                {
                    try { dev = RazerDevice.TryCreate(info); }
                    catch (Exception ex) { Log.Error("Razer", ex); }
                    if (dev != null) break;
                }
                if (dev != null) yield return dev;
            }
        }
    }

    public sealed class RazerDevice : RgbDevice
    {
        readonly HidDevice _hid;
        readonly byte _tx;
        Rgb? _last;

        public override string[] ConflictingProcesses => new[] { "RazerCentralService", "Razer Synapse Service", "Razer Synapse 3", "RzSDKService", "RazerAppEngine" };

        RazerDevice(HidDevice hid, byte tx)
        {
            _hid = hid;
            _tx = tx;
            Id = HidSelect.Id(hid.Info);
            string product = string.IsNullOrEmpty(hid.Info.Product) ? $"Razer {hid.Info.ProductId:X4}" : hid.Info.Product;
            Name = product.StartsWith("Razer", StringComparison.OrdinalIgnoreCase) ? product : "Razer " + product;
            Vendor = "Razer";
            string p = product.ToLowerInvariant();
            Kind = p.Contains("keyboard") || p.Contains("blackwidow") || p.Contains("huntsman") || p.Contains("ornata") || p.Contains("cynosa") || p.Contains("deathstalker") ? DeviceKind.Keyboard
                 : p.Contains("mouse") || p.Contains("deathadder") || p.Contains("basilisk") || p.Contains("viper") || p.Contains("naga") || p.Contains("mamba") || p.Contains("cobra") ? DeviceKind.Mouse
                 : p.Contains("firefly") || p.Contains("goliathus") || p.Contains("strider") ? DeviceKind.Mousepad
                 : p.Contains("kraken") || p.Contains("barracuda") || p.Contains("nari") ? DeviceKind.Headset
                 : DeviceKind.Other;
            MaxFps = 20;
            Zones.Add(new RgbZone("Tüm cihaz", 1));
        }

        internal static RazerDevice TryCreate(HidDeviceInfo info)
        {
            var hid = HidDevice.Open(info);
            foreach (byte tx in new byte[] { 0x1F, 0x3F, 0xFF, 0x2F, 0x9F })
            {
                var reply = Request(hid, tx, 0x00, 0x82, 0x16); // get serial: a valid transaction id answers with status 0x02
                if (reply != null && reply[1] == 0x02)
                {
                    Log.Info($"Razer {info.ProductId:X4} '{info.Product}': işlem kimliği 0x{tx:X2}");
                    return new RazerDevice(hid, tx);
                }
            }
            hid.Dispose();
            return null;
        }

        static byte[] Build(byte tx, byte cls, byte id, byte size, params byte[] args)
        {
            var p = new byte[91];
            p[2] = tx; p[6] = size; p[7] = cls; p[8] = id;
            Array.Copy(args, 0, p, 9, Math.Min(args.Length, 80));
            byte crc = 0;
            for (int i = 3; i < 89; i++) crc ^= p[i];
            p[89] = crc;
            return p;
        }

        static byte[] Request(HidDevice hid, byte tx, byte cls, byte id, byte size, params byte[] args)
        {
            if (!hid.SetFeature(Build(tx, cls, id, size, args))) return null;
            System.Threading.Thread.Sleep(10);
            return hid.GetFeature(0x00);
        }

        public override void Initialize()
        {
            _last = null;
            Status = "Doğrudan kontrol · deneysel";
        }

        public override void Apply()
        {
            var c = Zones[0].Colors[0];
            if (_last == c) return;
            // class 0x0F id 0x02 "set matrix effect": no flash store, all LEDs, static, 1 color, R G B
            bool ok = _hid.SetFeature(Build(_tx, 0x0F, 0x02, 0x09, 0x00, 0x00, 0x01, 0x00, 0x00, 0x01, c.R, c.G, c.B));
            if (ok) _last = c;
            Status = ok ? "Doğrudan kontrol · deneysel" : "Yazma hatası";
        }

        public override void Dispose() => _hid.Dispose();
    }
}
