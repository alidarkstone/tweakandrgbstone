using System;
using System.Collections.Generic;

namespace RgbStone.Core
{
    public enum DeviceKind { Motherboard, Gpu, Ram, Keyboard, Mouse, Mousepad, Headset, LedController, Cooler, Other }

    /// <summary>A group of LEDs that is addressed together (a header, a channel, a key matrix...).</summary>
    public sealed class RgbZone
    {
        public string Name { get; }
        /// <summary>True when the user sets the LED count (ARGB headers, fan hub channels).</summary>
        public bool Resizable { get; }
        public int MaxLeds { get; }
        public Rgb[] Colors { get; private set; }
        /// <summary>Optional horizontal LED positions in 0..1, so wave effects run across a keyboard instead of along its scan order.</summary>
        public double[] X { get; private set; }

        public RgbZone(string name, int ledCount, bool resizable = false, int maxLeds = 0, double[] x = null)
        {
            Name = name;
            Resizable = resizable;
            MaxLeds = maxLeds > 0 ? maxLeds : ledCount;
            Colors = new Rgb[ledCount];
            X = x;
        }

        public int LedCount => Colors.Length;
        /// <summary>LED count detected from the hardware (e.g. fans reported by a hub); used until the user sets one.</summary>
        public int DefaultLeds { get; set; }
        /// <summary>What was detected on this header, shown to the user (e.g. "3 × F120 RGB Core").</summary>
        public string Detected { get; set; }

        public void Resize(int count)
        {
            if (!Resizable) return;
            count = Math.Max(0, Math.Min(MaxLeds, count));
            if (count == Colors.Length) return;
            var c = new Rgb[count];
            Array.Copy(Colors, c, Math.Min(count, Colors.Length));
            Colors = c;
            X = null;
        }

        public void Fill(Rgb color)
        {
            for (int i = 0; i < Colors.Length; i++) Colors[i] = color;
        }

        /// <summary>LEDs per daisy-chained part (e.g. 12 for a fan ring); 0 means the zone is one continuous strip.</summary>
        public int SegmentSize { get; set; }

        /// <summary>Position of LED i in 0..1 used by the effects. Chained parts each get the full 0..1 range, so a pump and its fans look alike.</summary>
        public double PositionOf(int i)
        {
            if (X != null && i < X.Length) return X[i];
            int n = Colors.Length;
            if (SegmentSize > 1 && SegmentSize < n)
            {
                int start = i / SegmentSize * SegmentSize;
                int len = Math.Min(SegmentSize, n - start);
                return len <= 1 ? 0 : (double)(i - start) / len;
            }
            return n <= 1 ? 0 : (double)i / (n - 1);
        }
    }

    /// <summary>
    /// Base class for every controllable device. A driver (provider) creates these;
    /// the engine fills <see cref="Zones"/> each frame and calls <see cref="Apply"/>.
    /// </summary>
    public abstract class RgbDevice : IDisposable
    {
        /// <summary>Stable id used as the settings key, e.g. "hid:0db0:0076:7E4924092003".</summary>
        public string Id { get; protected set; }
        public string Name { get; protected set; }
        public string Vendor { get; protected set; }
        public DeviceKind Kind { get; protected set; }
        public List<RgbZone> Zones { get; } = new List<RgbZone>();
        /// <summary>Upper bound for frames per second this device can take.</summary>
        public int MaxFps { get; protected set; } = 60;
        /// <summary>Shown in the device list ("OK", or the last error).</summary>
        public string Status { get; set; } = "Hazır";
        /// <summary>Processes of the vendor's own software, shown as a hint when they may fight over the device.</summary>
        public virtual string[] ConflictingProcesses => new string[0];
        /// <summary>True while vendor software that was started after rgbstone owns the device; the engine skips it.</summary>
        public volatile bool Paused;

        /// <summary>Puts the device into direct/software control. Called once before the first frame.</summary>
        public abstract void Initialize();

        /// <summary>Sends the current zone colors to the hardware.</summary>
        public abstract void Apply();

        /// <summary>Called on exit or sleep; default paints every zone with the color.</summary>
        public virtual void Shutdown(Rgb color)
        {
            foreach (var z in Zones) z.Fill(color);
            Apply();
        }

        public virtual void Dispose() { }

        public int TotalLeds
        {
            get { int n = 0; foreach (var z in Zones) n += z.LedCount; return n; }
        }

        public override string ToString() => Name;
    }

    /// <summary>A device that was recognised but cannot be opened right now (e.g. another program holds it exclusively).</summary>
    public sealed class UnavailableDevice : RgbDevice
    {
        public UnavailableDevice(string id, string name, string vendor, DeviceKind kind, string reason)
        {
            Id = id; Name = name; Vendor = vendor; Kind = kind; Status = reason;
        }
        public override void Initialize() { }
        public override void Apply() { }
        public override void Shutdown(Rgb color) { }
    }

    /// <summary>
    /// A driver for a family of devices. Every non-abstract class implementing this interface
    /// (in rgbstone.exe or in a DLL placed in the "plugins" folder) is picked up automatically.
    /// </summary>
    public interface IDeviceProvider
    {
        string Name { get; }
        IEnumerable<RgbDevice> Detect(DetectContext context);
    }

    /// <summary>Shared information handed to providers during a scan.</summary>
    public sealed class DetectContext
    {
        public IReadOnlyList<Transport.HidDeviceInfo> HidDevices { get; set; }
        public Transport.Piix4Smbus SystemSmbus { get; set; }
        public SystemInfo System { get; set; }
        /// <summary>Devices found so far; generic (fallback) providers use it to skip hardware a dedicated driver already owns.</summary>
        public IReadOnlyList<RgbDevice> Detected { get; set; }
    }

    /// <summary>Marks a generic provider (e.g. HID LampArray) that runs after all brand-specific ones.</summary>
    public interface IFallbackProvider { }
}
