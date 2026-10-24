using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace RgbStone.Transport
{
    /// <summary>One HID top-level collection as Windows exposes it.</summary>
    public sealed class HidDeviceInfo
    {
        public string Path;
        public ushort VendorId, ProductId;
        public int Interface = -1;   // USB interface number from "mi_xx", -1 when the device has a single interface
        public int Collection = -1;  // "col0x" index, -1 when there is only one collection
        public ushort UsagePage, Usage;
        public int InputLength, OutputLength, FeatureLength;
        public string Product = "", Serial = "";

        /// <summary>Path without the interface/collection part, identical for all collections of one physical device.</summary>
        public string ParentKey;

        public override string ToString() =>
            $"{VendorId:X4}:{ProductId:X4} if{Interface} col{Collection} up{UsagePage:X4} u{Usage:X4} in{InputLength} out{OutputLength} feat{FeatureLength} '{Product}'";
    }

    public static class HidEnumerator
    {
        public static List<HidDeviceInfo> Enumerate()
        {
            var list = new List<HidDeviceInfo>();
            Native.HidD_GetHidGuid(out Guid guid);
            IntPtr set = Native.SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
            if (set == new IntPtr(-1)) return list;
            try
            {
                var ifData = new Native.SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<Native.SP_DEVICE_INTERFACE_DATA>() };
                for (uint i = 0; Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref ifData); i++)
                {
                    string path = GetPath(set, ref ifData);
                    if (path == null) continue;
                    var info = Describe(path);
                    if (info != null) list.Add(info);
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(set); }
            return list;
        }

        static string GetPath(IntPtr set, ref Native.SP_DEVICE_INTERFACE_DATA ifData)
        {
            Native.SetupDiGetDeviceInterfaceDetail(set, ref ifData, IntPtr.Zero, 0, out int size, IntPtr.Zero);
            if (size <= 0) return null;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W: 8 on x64, 6 on x86
                Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref ifData, buf, size, out _, IntPtr.Zero)) return null;
                return Marshal.PtrToStringUni(buf + 4);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        static readonly Regex MiRx = new Regex(@"mi_([0-9a-f]{2})", RegexOptions.IgnoreCase);
        static readonly Regex ColRx = new Regex(@"col([0-9a-f]{2})", RegexOptions.IgnoreCase);

        static HidDeviceInfo Describe(string path)
        {
            using (var h = Native.CreateFile(path, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) return null;
                var attr = new Native.HIDD_ATTRIBUTES { Size = Marshal.SizeOf<Native.HIDD_ATTRIBUTES>() };
                if (!Native.HidD_GetAttributes(h, ref attr)) return null;
                var info = new HidDeviceInfo { Path = path, VendorId = attr.VendorID, ProductId = attr.ProductID };

                if (Native.HidD_GetPreparsedData(h, out IntPtr pre))
                {
                    try
                    {
                        if (Native.HidP_GetCaps(pre, out var caps) == Native.HIDP_STATUS_SUCCESS)
                        {
                            info.UsagePage = caps.UsagePage;
                            info.Usage = caps.Usage;
                            info.InputLength = caps.InputReportByteLength;
                            info.OutputLength = caps.OutputReportByteLength;
                            info.FeatureLength = caps.FeatureReportByteLength;
                        }
                    }
                    finally { Native.HidD_FreePreparsedData(pre); }
                }

                var sb = new StringBuilder(256);
                if (Native.HidD_GetProductString(h, sb, sb.Capacity * 2)) info.Product = sb.ToString();
                sb.Clear();
                if (Native.HidD_GetSerialNumberString(h, sb, sb.Capacity * 2)) info.Serial = sb.ToString();

                var m = MiRx.Match(path);
                if (m.Success) info.Interface = Convert.ToInt32(m.Groups[1].Value, 16);
                m = ColRx.Match(path);
                if (m.Success) info.Collection = Convert.ToInt32(m.Groups[1].Value, 16);

                // \\?\hid#vid_046d&pid_c547&mi_02&col01#9&2fca44e3&0&0000#{guid}: the instance part after the second '#'
                // differs per interface, so group by vid/pid + the USB parent hinted in the instance id.
                string lower = path.ToLowerInvariant();
                string[] parts = lower.Split('#');
                string inst = parts.Length > 2 ? parts[2] : lower;
                int amp = inst.LastIndexOf('&');
                info.ParentKey = $"{attr.VendorID:x4}:{attr.ProductID:x4}:" + (amp > 0 ? inst.Substring(0, amp) : inst);
                return info;
            }
        }
    }

    /// <summary>An opened HID collection. Reads and writes use overlapped I/O with timeouts so a stuck device never blocks the engine.</summary>
    public sealed class HidDevice : IDisposable
    {
        public HidDeviceInfo Info { get; }
        readonly SafeFileHandle _h;
        readonly object _io = new object();

        HidDevice(HidDeviceInfo info, SafeFileHandle h) { Info = info; _h = h; }

        public static HidDevice Open(HidDeviceInfo info)
        {
            var h = Native.CreateFile(info.Path, Native.GENERIC_READ | Native.GENERIC_WRITE,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (h.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException(err == 32
                    ? "Başka bir program cihazı kilitlemiş (SignalRGB / üretici yazılımı kapalı mı?)"
                    : $"HID açılamadı ({info.VendorId:X4}:{info.ProductId:X4}), hata {err}");
            }
            return new HidDevice(info, h);
        }

        /// <summary>Output report; data[0] is the report id (0 when the device has none). Padded to the device's report size.</summary>
        public bool Write(byte[] data, int timeoutMs = 500)
        {
            var buf = Pad(data, Info.OutputLength);
            lock (_io) return Overlapped(buf, true, timeoutMs) == buf.Length;
        }

        /// <summary>Reads one input report (first byte is the report id). Returns null on timeout.</summary>
        public byte[] Read(int timeoutMs)
        {
            if (Info.InputLength <= 0) return null;
            var buf = new byte[Info.InputLength];
            int n;
            lock (_io) n = Overlapped(buf, false, timeoutMs);
            if (n <= 0) return null;
            if (n == buf.Length) return buf;
            var r = new byte[n];
            Array.Copy(buf, r, n);
            return r;
        }

        /// <summary>Discards queued input reports.</summary>
        public void FlushInput() => Native.HidD_FlushQueue(_h);

        public bool SetFeature(byte[] data)
        {
            var buf = Pad(data, Info.FeatureLength);
            lock (_io) return Native.HidD_SetFeature(_h, buf, buf.Length);
        }

        public byte[] GetFeature(byte reportId)
        {
            var buf = new byte[Math.Max(1, Info.FeatureLength)];
            buf[0] = reportId;
            lock (_io) return Native.HidD_GetFeature(_h, buf, buf.Length) ? buf : null;
        }

        static byte[] Pad(byte[] data, int len)
        {
            if (data.Length >= len) return data;
            var b = new byte[len];
            Array.Copy(data, b, data.Length);
            return b;
        }

        int Overlapped(byte[] buf, bool write, int timeoutMs)
        {
            using (var ev = new ManualResetEvent(false))
            {
                IntPtr ov = Marshal.AllocHGlobal(Marshal.SizeOf<Native.OVERLAPPED>());
                var pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
                try
                {
                    var o = new Native.OVERLAPPED { hEvent = ev.SafeWaitHandle.DangerousGetHandle() };
                    Marshal.StructureToPtr(o, ov, false);
                    IntPtr p = pin.AddrOfPinnedObject();
                    bool ok = write
                        ? Native.WriteFile(_h, p, buf.Length, IntPtr.Zero, ov)
                        : Native.ReadFile(_h, p, buf.Length, IntPtr.Zero, ov);
                    if (!ok && Marshal.GetLastWin32Error() != Native.ERROR_IO_PENDING) return -1;
                    if (!ev.WaitOne(timeoutMs))
                    {
                        Native.CancelIoEx(_h, ov);
                        // Wait for the cancellation so the pinned buffer and OVERLAPPED stay valid until the kernel lets go.
                        Native.GetOverlappedResult(_h, ov, out _, true);
                        return 0;
                    }
                    return Native.GetOverlappedResult(_h, ov, out int done, false) ? done : -1;
                }
                finally
                {
                    pin.Free();
                    Marshal.FreeHGlobal(ov);
                }
            }
        }

        public void Dispose()
        {
            if (_preparsed != IntPtr.Zero) { Native.HidD_FreePreparsedData(_preparsed); _preparsed = IntPtr.Zero; }
            _h.Dispose();
        }

        // ----- report descriptor access (for standard usage pages such as LampArray) -----

        IntPtr _preparsed;

        IntPtr Preparsed
        {
            get
            {
                if (_preparsed == IntPtr.Zero) Native.HidD_GetPreparsedData(_h, out _preparsed);
                return _preparsed;
            }
        }

        public sealed class ValueCap
        {
            public byte ReportId;
            public ushort UsagePage, Usage;
            public int BitSize, ReportCount, DataIndex;
        }

        /// <summary>All value fields of the given report type (0 input, 1 output, 2 feature).</summary>
        public List<ValueCap> ValueCaps(int reportType)
        {
            var list = new List<ValueCap>();
            if (!Native.HidP_GetCaps(Preparsed, out var caps).Equals(Native.HIDP_STATUS_SUCCESS)) return list;
            ushort n = reportType == 0 ? caps.NumberInputValueCaps : reportType == 1 ? caps.NumberOutputValueCaps : caps.NumberFeatureValueCaps;
            if (n == 0) return list;
            var arr = new Native.HIDP_VALUE_CAPS[n];
            if (Native.HidP_GetValueCaps(reportType, arr, ref n, Preparsed) != Native.HIDP_STATUS_SUCCESS) return list;
            for (int i = 0; i < n; i++)
            {
                var c = arr[i];
                if (c.IsRange != 0)
                {
                    for (int u = c.UsageMin; u <= c.UsageMax; u++)
                        list.Add(new ValueCap { ReportId = c.ReportID, UsagePage = c.UsagePage, Usage = (ushort)u, BitSize = c.BitSize, ReportCount = 1, DataIndex = c.DataIndexMin + (u - c.UsageMin) });
                }
                else list.Add(new ValueCap { ReportId = c.ReportID, UsagePage = c.UsagePage, Usage = c.UsageMin, BitSize = c.BitSize, ReportCount = c.ReportCount, DataIndex = c.DataIndexMin });
            }
            return list;
        }

        /// <summary>Feature report buffer with every field at its "no change" default.</summary>
        public byte[] NewFeatureReport(byte reportId)
        {
            var buf = new byte[Info.FeatureLength];
            Native.HidP_InitializeReportForID(2, reportId, Preparsed, buf, buf.Length);
            return buf;
        }

        public bool SetValue(byte[] report, ushort page, ushort usage, uint value) =>
            Native.HidP_SetUsageValue(2, page, 0, usage, value, Preparsed, report, report.Length) == Native.HIDP_STATUS_SUCCESS;

        public int LastStatus;
        public bool SetValueArray(byte[] report, ushort page, ushort usage, byte[] values) =>
            (LastStatus = Native.HidP_SetUsageValueArray(2, page, 0, usage, values, (ushort)values.Length, Preparsed, report, report.Length)) == Native.HIDP_STATUS_SUCCESS;

        public uint? GetValue(byte[] report, ushort page, ushort usage) =>
            Native.HidP_GetUsageValue(2, page, 0, usage, out uint v, Preparsed, report, report.Length) == Native.HIDP_STATUS_SUCCESS ? v : (uint?)null;

        /// <summary>Sets single values by data index; needed when a report repeats the same usage (one field per lamp).</summary>
        public bool SetData(byte[] report, IList<KeyValuePair<int, uint>> values)
        {
            var data = values.Select(v => new Native.HIDP_DATA { DataIndex = (ushort)v.Key, RawValue = v.Value }).ToArray();
            int n = data.Length;
            return Native.HidP_SetData(2, data, ref n, Preparsed, report, report.Length) == Native.HIDP_STATUS_SUCCESS;
        }

        /// <summary>Sends a feature report built with <see cref="NewFeatureReport"/> (already full length).</summary>
        public bool SetFeatureRaw(byte[] report)
        {
            lock (_io) return Native.HidD_SetFeature(_h, report, report.Length);
        }
    }

    static class Native
    {
        public const int DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
        public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
        public const int ERROR_IO_PENDING = 997;
        public const int HIDP_STATUS_SUCCESS = 0x00110000;

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
                NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_VALUE_CAPS
        {
            public ushort UsagePage; public byte ReportID; public byte IsAlias; public ushort BitField; public ushort LinkCollection;
            public ushort LinkUsage; public ushort LinkUsagePage;
            public byte IsRange, IsStringRange, IsDesignatorRange, IsAbsolute, HasNull, Reserved;
            public ushort BitSize, ReportCount;
            public ushort R1, R2, R3, R4, R5;
            public uint UnitsExp, Units;
            public int LogicalMin, LogicalMax, PhysicalMin, PhysicalMax;
            // Range / NotRange union: UsageMin doubles as NotRange.Usage
            public ushort UsageMin, UsageMax, StringMin, StringMax, DesignatorMin, DesignatorMax, DataIndexMin, DataIndexMax;
        }

        [StructLayout(LayoutKind.Sequential)] public struct HIDP_DATA { public ushort DataIndex; public ushort Reserved; public uint RawValue; }
        [DllImport("hid.dll")] public static extern int HidP_SetData(int type, [In, Out] HIDP_DATA[] data, ref int len, IntPtr pre, byte[] report, int reportLen);
        [DllImport("hid.dll")] public static extern int HidP_GetValueCaps(int type, [In, Out] HIDP_VALUE_CAPS[] caps, ref ushort len, IntPtr pre);
        [DllImport("hid.dll")] public static extern int HidP_InitializeReportForID(int type, byte id, IntPtr pre, byte[] report, int len);
        [DllImport("hid.dll")] public static extern int HidP_SetUsageValue(int type, ushort page, ushort link, ushort usage, uint value, IntPtr pre, byte[] report, int len);
        [DllImport("hid.dll")] public static extern int HidP_SetUsageValueArray(int type, ushort page, ushort link, ushort usage, byte[] values, ushort valuesLen, IntPtr pre, byte[] report, int len);
        [DllImport("hid.dll")] public static extern int HidP_GetUsageValue(int type, ushort page, ushort link, ushort usage, out uint value, IntPtr pre, byte[] report, int len);

        [StructLayout(LayoutKind.Sequential)]
        public struct OVERLAPPED { public IntPtr Internal, InternalHigh; public int Offset, OffsetHigh; public IntPtr hEvent; }

        [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid g);
        [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
        [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr p);
        [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr p);
        [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr p, out HIDP_CAPS c);
        [DllImport("hid.dll", CharSet = CharSet.Unicode)] public static extern bool HidD_GetProductString(SafeFileHandle h, StringBuilder b, int len);
        [DllImport("hid.dll", CharSet = CharSet.Unicode)] public static extern bool HidD_GetSerialNumberString(SafeFileHandle h, StringBuilder b, int len);
        [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_SetFeature(SafeFileHandle h, byte[] b, int len);
        [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetFeature(SafeFileHandle h, byte[] b, int len);
        [DllImport("hid.dll")] public static extern bool HidD_FlushQueue(SafeFileHandle h);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid g, string enumerator, IntPtr parent, int flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid g, uint idx, ref SP_DEVICE_INTERFACE_DATA d);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, int size, out int required, IntPtr devInfo);
        [DllImport("setupapi.dll")] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool WriteFile(SafeFileHandle h, IntPtr buf, int len, IntPtr written, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool ReadFile(SafeFileHandle h, IntPtr buf, int len, IntPtr read, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetOverlappedResult(SafeFileHandle h, IntPtr ov, out int n, bool wait);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CancelIoEx(SafeFileHandle h, IntPtr ov);
    }
}
