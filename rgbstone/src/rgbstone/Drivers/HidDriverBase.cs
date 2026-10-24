using System;
using System.Collections.Generic;
using System.Linq;
using RgbStone.Core;
using RgbStone.Transport;

namespace RgbStone.Drivers
{
    /// <summary>Small helpers shared by the USB HID drivers.</summary>
    static class HidSelect
    {
        public static IEnumerable<HidDeviceInfo> ByVidPid(DetectContext ctx, ushort vid, IEnumerable<ushort> pids) =>
            ctx.HidDevices.Where(d => d.VendorId == vid && pids.Contains(d.ProductId));

        /// <summary>One entry per physical device (several collections of one device share the parent key).</summary>
        public static IEnumerable<IGrouping<string, HidDeviceInfo>> Physical(IEnumerable<HidDeviceInfo> list) =>
            list.GroupBy(d => $"{d.VendorId:x4}:{d.ProductId:x4}:{(string.IsNullOrEmpty(d.Serial) ? d.ParentKey : d.Serial)}");

        public static string Id(HidDeviceInfo d) =>
            $"hid:{d.VendorId:x4}:{d.ProductId:x4}:{(string.IsNullOrEmpty(d.Serial) ? d.ParentKey : d.Serial)}";
    }
}
