using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using RgbStone.Core;

namespace RgbStone.Transport
{
    /// <summary>I2C access to NVIDIA graphics cards through the driver's own nvapi64.dll (no extra driver, no admin needed).</summary>
    public static class NvApi
    {
        public sealed class Gpu
        {
            public IntPtr Handle;
            public ushort Vendor, Device, SubVendor, SubDevice;
            public override string ToString() => $"{Vendor:X4}:{Device:X4} sub {SubVendor:X4}:{SubDevice:X4}";
        }

        [StructLayout(LayoutKind.Sequential)]
        struct NV_I2C_INFO_V3
        {
            public uint version;
            public uint displayMask;
            public byte bIsDDCPort;
            public byte i2cDevAddress;
            public IntPtr pbI2cRegAddress;
            public uint regAddrSize;
            public IntPtr pbData;
            public uint cbSize;
            public uint i2cSpeed;
            public uint i2cSpeedKhz;
            public byte portId;
            public uint bIsPortIdSet;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr QueryInterfaceFn(uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int InitializeFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int EnumGpusFn([Out] IntPtr[] handles, out int count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int PciIdsFn(IntPtr gpu, out uint deviceId, out uint subSystemId, out uint revisionId, out uint extDeviceId);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int I2cFn(IntPtr gpu, ref NV_I2C_INFO_V3 info, ref uint unknown);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadLibrary(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr mod, string name);

        static bool _loaded, _ok;
        static I2cFn _write, _read;
        static readonly object Sync = new object();

        static T Fn<T>(QueryInterfaceFn q, uint id) where T : class
        {
            IntPtr p = q(id);
            return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(p);
        }

        public static List<Gpu> EnumerateGpus()
        {
            var result = new List<Gpu>();
            lock (Sync)
            {
                try
                {
                    IntPtr mod = LoadLibrary("nvapi64.dll");
                    if (mod == IntPtr.Zero) return result;
                    IntPtr qi = GetProcAddress(mod, "nvapi_QueryInterface");
                    if (qi == IntPtr.Zero) return result;
                    var q = Marshal.GetDelegateForFunctionPointer<QueryInterfaceFn>(qi);

                    var init = Fn<InitializeFn>(q, 0x0150E828);
                    var enumGpus = Fn<EnumGpusFn>(q, 0xE5AC921F);
                    var pci = Fn<PciIdsFn>(q, 0x2DDFB66E);
                    _write = Fn<I2cFn>(q, 0x283AC65A);
                    _read = Fn<I2cFn>(q, 0x4D7B0709);
                    if (init == null || enumGpus == null || pci == null || _write == null) return result;
                    if (init() != 0) return result;
                    _ok = true;

                    var handles = new IntPtr[64];
                    if (enumGpus(handles, out int count) != 0) return result;
                    for (int i = 0; i < count; i++)
                    {
                        if (pci(handles[i], out uint dev, out uint sub, out _, out _) != 0) continue;
                        result.Add(new Gpu
                        {
                            Handle = handles[i],
                            Vendor = (ushort)dev, Device = (ushort)(dev >> 16),
                            SubVendor = (ushort)sub, SubDevice = (ushort)(sub >> 16),
                        });
                    }
                }
                catch (Exception ex) { Log.Error("NVAPI", ex); }
                finally { _loaded = true; }
            }
            foreach (var g in result) Log.Info("NVIDIA GPU: " + g);
            return result;
        }

        /// <summary>SMBus-style "write byte data" to a 7-bit address on the GPU's RGB I2C port.</summary>
        public static bool WriteByte(Gpu gpu, byte addr, byte register, byte value)
        {
            if (!_loaded || !_ok) return false;
            IntPtr reg = Marshal.AllocHGlobal(1), data = Marshal.AllocHGlobal(1);
            try
            {
                Marshal.WriteByte(reg, register);
                Marshal.WriteByte(data, value);
                var info = new NV_I2C_INFO_V3
                {
                    version = (uint)(Marshal.SizeOf<NV_I2C_INFO_V3>() | (3 << 16)),
                    displayMask = 0,
                    bIsDDCPort = 0,
                    i2cDevAddress = (byte)(addr << 1),
                    pbI2cRegAddress = reg,
                    regAddrSize = 1,
                    pbData = data,
                    cbSize = 1,
                    i2cSpeed = 0xFFFF,
                    i2cSpeedKhz = 0,
                    portId = 1,
                    bIsPortIdSet = 1,
                };
                uint unknown = 0;
                lock (Sync) return _write(gpu.Handle, ref info, ref unknown) == 0;
            }
            finally { Marshal.FreeHGlobal(reg); Marshal.FreeHGlobal(data); }
        }
    }
}
