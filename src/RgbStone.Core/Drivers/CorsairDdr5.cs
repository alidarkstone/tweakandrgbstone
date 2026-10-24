using System;
using System.Collections.Generic;
using System.Linq;
using RgbStone.Core;
using RgbStone.Transport;

namespace RgbStone.Drivers
{
    /// <summary>
    /// Corsair DDR5 RGB memory (Vengeance RGB DDR5 "CMH", Dominator Titanium "CMP") on the system SMBus.
    /// A stick is only touched after its vendor (0x1A) and model (3/4) registers match.
    /// </summary>
    public sealed class CorsairDdr5Provider : IDeviceProvider
    {
        public string Name => "Corsair DDR5 RAM";

        // Only the RGB controller addresses Corsair DDR5 uses on AM5; nothing else on the bus is probed.
        static readonly byte[] Addresses = { 0x18, 0x19, 0x1A, 0x1B };

        public IEnumerable<RgbDevice> Detect(DetectContext ctx)
        {
            var bus = ctx.SystemSmbus;
            if (bus == null) yield break;

            string part = ctx.System.RamPartNumbers.FirstOrDefault(p => p.StartsWith("CMP") || p.StartsWith("CMH")) ?? "";
            if (part.Length == 0) yield break; // no Corsair DDR5 RGB part number, leave the bus alone
            bool titanium = part.StartsWith("CMP");

            int slot = 0;
            foreach (var addr in Addresses)
            {
                int quick = bus.WriteQuick(addr);
                if (quick != 0) { Log.Info($"RAM adres 0x{addr:X2}: yanıt yok ({quick})"); continue; }
                if (!Matches(bus, addr))
                {
                    Log.Warn($"RAM adres 0x{addr:X2}: yanıt veriyor ama Corsair kimliği okunamadı (üretici {bus.ReadByte(addr, 0x43)}, model {bus.ReadByte(addr, 0x44)})");
                    continue;
                }
                Log.Info($"Corsair RAM bulundu: {bus.Name} adres 0x{addr:X2}");
                yield return new CorsairDdr5(bus, addr, ++slot, titanium);
            }
        }

        static bool Matches(Piix4Smbus bus, byte addr)
        {
            bool vendor = false;
            for (int i = 0; i < 5 && !vendor; i++) vendor = bus.ReadByte(addr, 0x43) == 0x1A;
            if (!vendor) return false;
            for (int i = 0; i < 5; i++)
            {
                int m = bus.ReadByte(addr, 0x44);
                if (m == 0x03 || m == 0x04) return true;
            }
            return false;
        }
    }

    public sealed class CorsairDdr5 : RgbDevice
    {
        static readonly byte[] Crc8 = BuildCrcTable();
        readonly Piix4Smbus _bus;
        readonly byte _addr;
        byte[] _last;

        /// <summary>Every program that writes the SMBus: while one runs, RAM writes stop (two writers at once can hit the PMIC).</summary>
        public override string[] ConflictingProcesses => Engine.SmbusUsers;

        public CorsairDdr5(Piix4Smbus bus, byte addr, int slot, bool titanium)
        {
            _bus = bus;
            _addr = addr;
            Id = $"smbus:{bus.Name}:{addr:x2}";
            Name = (titanium ? "Corsair Dominator Titanium" : "Corsair Vengeance RGB DDR5") + " #" + slot;
            Vendor = "Corsair";
            Kind = DeviceKind.Ram;
            MaxFps = 15; // fewer bus transactions; RAM effects still look smooth
            Zones.Add(new RgbZone("LED'ler", titanium ? 11 : 10));
        }

        public override void Initialize() { _last = null; Status = "Doğrudan kontrol"; }

        public override void Apply()
        {
            var c = Zones[0].Colors;
            var p = new byte[35];
            p[0] = 0x0B; // direct RGB command
            for (int i = 0; i < c.Length && i < 11; i++)
            {
                p[1 + i * 3] = c[i].R; p[2 + i * 3] = c[i].G; p[3 + i * 3] = c[i].B;
            }
            byte crc = 0;
            for (int i = 0; i < 34; i++) crc = Crc8[crc ^ p[i]];
            p[34] = crc;
            if (_last != null && _last.SequenceEqual(p)) return;

            bool ok = _bus.WriteBlock(_addr, 0x31, p, 0, 32) == 0;
            System.Threading.Thread.Sleep(1);
            ok &= _bus.WriteBlock(_addr, 0x32, p, 32, 3) == 0;
            if (ok) _last = p;
            Status = ok ? "Doğrudan kontrol" : "SMBus meşgul";
        }

        /// <summary>CRC-8 with polynomial 0x07 (the SMBus PEC table).</summary>
        static byte[] BuildCrcTable()
        {
            var t = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                int c = i;
                for (int b = 0; b < 8; b++) c = (c & 0x80) != 0 ? (c << 1) ^ 0x07 : c << 1;
                t[i] = (byte)c;
            }
            return t;
        }
    }
}
