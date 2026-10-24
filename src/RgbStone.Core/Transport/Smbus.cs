using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using RgbStone.Core;

namespace RgbStone.Transport
{
    /// <summary>Raw x86 port I/O through an already installed inpoutx64 kernel driver.</summary>
    public sealed class PortIo : IDisposable
    {
        const uint IOCTL_READ_PORT_UCHAR = 0x9C402004, IOCTL_WRITE_PORT_UCHAR = 0x9C402008;
        readonly SafeFileHandle _h;

        PortIo(SafeFileHandle h) { _h = h; }

        public static PortIo TryOpen()
        {
            foreach (var name in new[] { @"\\.\inpoutx64", @"\\.\inpout32" })
            {
                var h = Native.CreateFile(name, Native.GENERIC_READ | Native.GENERIC_WRITE, 3, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
                if (!h.IsInvalid) { Log.Info("Port I/O sürücüsü: " + name); return new PortIo(h); }
            }
            Log.Warn("Port I/O sürücüsü (inpoutx64) bulunamadı ya da yönetici izni yok; RAM ışıkları kontrol edilemez.");
            return null;
        }

        public byte In(ushort port)
        {
            var i = BitConverter.GetBytes(port);
            var o = new byte[1];
            DeviceIoControl(_h, IOCTL_READ_PORT_UCHAR, i, 2, o, 1, out _, IntPtr.Zero);
            return o[0];
        }

        public void Out(ushort port, byte value)
        {
            var i = new byte[] { (byte)port, (byte)(port >> 8), value };
            DeviceIoControl(_h, IOCTL_WRITE_PORT_UCHAR, i, 3, null, 0, out _, IntPtr.Zero);
        }

        public void Dispose() => _h.Dispose();

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, int inLen, byte[] outBuf, int outLen, out int ret, IntPtr ov);
    }

    /// <summary>
    /// AMD FCH / Intel PIIX4-compatible SMBus host controller (the bus RAM sticks sit on).
    /// Every transaction holds the system-wide SMBus mutex that HWiNFO, OpenRGB, SignalRGB and vendor tools share,
    /// so rgbstone never interleaves with another program's SMBus access.
    /// </summary>
    public sealed class Piix4Smbus : IDisposable
    {
        const int STS = 0, CNT = 2, CMD = 3, ADD = 4, DAT0 = 5, DAT1 = 6, BLKDAT = 7;
        const byte QUICK = 0x00, BYTE_DATA = 0x08, BLOCK_DATA = 0x14;

        readonly PortIo _io;
        readonly ushort _base;
        readonly Mutex _mutex;
        public string Name { get; }

        Piix4Smbus(PortIo io, ushort baseAddr)
        {
            _io = io;
            _base = baseAddr;
            Name = $"PIIX4 0x{baseAddr:X4}";
            _mutex = OpenSharedMutex();
        }

        static Mutex OpenSharedMutex()
        {
            try { return new Mutex(false, @"Global\Access_SMBUS.HTB.Bus"); }
            catch (Exception ex) { Log.Warn("SMBus mutex açılamadı: " + ex.Message); return null; }
        }

        /// <summary>Opens the AMD FCH SMBus (PCI 1022:790B) at its fixed 0x0B00 base. Returns null when unavailable.</summary>
        public static Piix4Smbus TryOpenSystemBus(PortIo io)
        {
            if (io == null) return null;
            if (!HasAmdSmbusController()) { Log.Info("AMD SMBus denetleyicisi yok, RAM taraması atlandı."); return null; }
            return new Piix4Smbus(io, 0x0B00);
        }

        static bool HasAmdSmbusController()
        {
            try
            {
                using (var q = new System.Management.ManagementObjectSearcher("SELECT DeviceID FROM Win32_PnPEntity WHERE DeviceID LIKE 'PCI\\\\VEN_1022&DEV_790B%'"))
                    return q.Get().Count > 0;
            }
            catch { return false; }
        }

        /// <summary>Writes are only ever allowed to the RGB controller range; SPD hubs (0x50-0x57) and PMICs (0x48-0x4F) are never written.</summary>
        public static bool IsWritableAddress(byte addr) => addr >= 0x18 && addr <= 0x1B;

        DateTime _lastOtherCheck;
        bool _otherUserRunning;

        T Locked<T>(Func<T> f, T failed)
        {
            if ((DateTime.UtcNow - _lastOtherCheck).TotalSeconds > 2)
            {
                _lastOtherCheck = DateTime.UtcNow;
                bool was = _otherUserRunning;
                _otherUserRunning = Core.Engine.SmbusUsersRunning().Length > 0;
                if (_otherUserRunning && !was) Log.Warn("SMBus'ı kullanan başka bir program açıldı; RAM'e yazma durduruldu.");
            }
            if (_otherUserRunning) return failed;

            bool owned = false;
            try
            {
                if (_mutex != null)
                {
                    try { owned = _mutex.WaitOne(200); }
                    catch (AbandonedMutexException) { owned = true; }
                    if (!owned) return failed;
                }
                return f();
            }
            finally { if (owned) _mutex.ReleaseMutex(); }
        }

        /// <summary>0 when a device acknowledges the address.</summary>
        public int WriteQuick(byte addr) => Locked(() =>
        {
            if (!IsWritableAddress(addr)) return -1;
            _io.Out(P(ADD), (byte)(addr << 1));
            return Transaction(QUICK);
        }, -1);

        public int ReadByte(byte addr, byte command) => Locked(() =>
        {
            _io.Out(P(ADD), (byte)((addr << 1) | 1));
            _io.Out(P(CMD), command);
            int r = Transaction(BYTE_DATA);
            return r < 0 ? r : _io.In(P(DAT0));
        }, -1);

        public int WriteByte(byte addr, byte command, byte value) => Locked(() =>
        {
            if (!IsWritableAddress(addr)) return -1;
            _io.Out(P(ADD), (byte)(addr << 1));
            _io.Out(P(CMD), command);
            _io.Out(P(DAT0), value);
            if (!AddressStillOurs(addr)) return -16;
            return Transaction(BYTE_DATA);
        }, -1);

        public int WriteBlock(byte addr, byte command, byte[] data, int offset, int length) => Locked(() =>
        {
            if (length < 1 || length > 32 || !IsWritableAddress(addr)) return -1;
            _io.Out(P(ADD), (byte)(addr << 1));
            _io.Out(P(CMD), command);
            if (!AddressStillOurs(addr)) return -16;
            _io.Out(P(DAT0), (byte)length);
            _io.In(P(CNT)); // reading the control register resets the block data pointer
            for (int i = 0; i < length; i++) _io.Out(P(BLKDAT), data[offset + i]);
            return Transaction(BLOCK_DATA);
        }, -1);

        ushort P(int reg) => (ushort)(_base + reg);

        /// <summary>Re-reads the address register right before starting; if another program changed it, the write is dropped.</summary>
        bool AddressStillOurs(byte addr)
        {
            if (_io.In(P(ADD)) == (byte)(addr << 1)) return true;
            Log.Warn($"{Name}: adres kaydı başka bir program tarafından değiştirildi, yazma iptal edildi.");
            return false;
        }

        int Transaction(byte size)
        {
            byte st = _io.In(P(STS));
            if (st != 0)
            {
                _io.Out(P(STS), st);
                if (_io.In(P(STS)) != 0) return -16; // busy
            }
            _io.Out(P(CNT), (byte)(size & 0x1C));
            _io.Out(P(CNT), (byte)((size & 0x1C) | 0x40)); // start

            var sw = Stopwatch.StartNew();
            do
            {
                st = _io.In(P(STS));
                if ((st & 0x01) == 0 && (st & 0x1E) != 0) break;
                if (sw.ElapsedMilliseconds > 50) { Log.Warn($"{Name}: zaman aşımı"); return -110; }
                Thread.SpinWait(200);
            } while (true);

            int result = 0;
            if ((st & 0x10) != 0) result = -5;       // failed
            else if ((st & 0x08) != 0) result = -11; // bus collision
            else if ((st & 0x04) != 0) result = -6;  // no acknowledge
            st = _io.In(P(STS));
            if (st != 0) _io.Out(P(STS), st);
            return result;
        }

        public void Dispose() => _mutex?.Dispose();
    }
}
