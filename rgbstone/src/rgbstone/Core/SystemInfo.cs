using System;
using System.Collections.Generic;
using System.Management;

namespace RgbStone.Core
{
    /// <summary>Motherboard and memory info read once from WMI (local only).</summary>
    public sealed class SystemInfo
    {
        public string BoardVendor = "";
        public string BoardProduct = "";
        public List<string> RamPartNumbers = new List<string>();

        public static SystemInfo Read()
        {
            var s = new SystemInfo();
            try
            {
                using (var q = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard"))
                    foreach (ManagementObject o in q.Get())
                    {
                        s.BoardVendor = (o["Manufacturer"] as string ?? "").Trim();
                        s.BoardProduct = (o["Product"] as string ?? "").Trim();
                    }
                using (var q = new ManagementObjectSearcher("SELECT PartNumber FROM Win32_PhysicalMemory"))
                    foreach (ManagementObject o in q.Get())
                        s.RamPartNumbers.Add((o["PartNumber"] as string ?? "").Trim());
            }
            catch (Exception ex) { Log.Error("WMI", ex); }
            Log.Info($"Anakart: {s.BoardVendor} {s.BoardProduct}; RAM: {string.Join(", ", s.RamPartNumbers)}");
            return s;
        }
    }
}
