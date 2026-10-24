using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using RgbStone.Transport;

namespace RgbStone.Core
{
    /// <summary>
    /// Finds devices through every provider, then runs one render clock and one worker thread per device.
    /// A slow or unplugged device only drops its own frames; the others keep going.
    /// </summary>
    public sealed class Engine : IDisposable
    {
        sealed class Worker
        {
            public RgbDevice Device;
            public Thread Thread;
            public readonly AutoResetEvent Frame = new AutoResetEvent(false);
            public volatile bool Busy;
            public long LastSentTicks;
        }

        readonly AppSettings _settings;
        readonly List<Worker> _workers = new List<Worker>();
        readonly object _sync = new object();
        Thread _clock;
        volatile bool _running;
        PortIo _portIo;
        Piix4Smbus _smbus;
        readonly Stopwatch _time = Stopwatch.StartNew();

        public List<RgbDevice> Devices { get; } = new List<RgbDevice>();
        public List<IDeviceProvider> Providers { get; } = new List<IDeviceProvider>();
        public double MeasuredFps { get; private set; }
        public event Action DevicesChanged;

        public Engine(AppSettings settings)
        {
            _settings = settings;
            LoadProviders();
        }

        void LoadProviders()
        {
            var asms = new List<Assembly> { typeof(Engine).Assembly };
            string pluginDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugins");
            if (Directory.Exists(pluginDir))
                foreach (var dll in Directory.GetFiles(pluginDir, "*.dll"))
                {
                    try { asms.Add(Assembly.LoadFrom(dll)); Log.Info("Eklenti yüklendi: " + Path.GetFileName(dll)); }
                    catch (Exception ex) { Log.Error("Eklenti yüklenemedi: " + dll, ex); }
                }
            foreach (var asm in asms)
            {
                Type[] types;
                try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
                foreach (var t in types.Where(t => typeof(IDeviceProvider).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface))
                {
                    try { Providers.Add((IDeviceProvider)Activator.CreateInstance(t)); }
                    catch (Exception ex) { Log.Error("Sürücü başlatılamadı: " + t.Name, ex); }
                }
            }
            Log.Info("Sürücüler: " + string.Join(", ", Providers.Select(p => p.Name)));
        }

        /// <summary>(Re)scans all hardware. Safe to call while running.</summary>
        readonly object _scanLock = new object();
        static readonly TimeSpan DetectTimeout = TimeSpan.FromSeconds(30);
        volatile bool _inScan;

        public void Scan()
        {
            lock (_scanLock)
            {
                _inScan = true;
                try { ScanCore(); } finally { _inScan = false; }
            }
        }

        void ScanCore()
        {
            Stop();
            foreach (var d in Devices) { try { d.Dispose(); } catch { } }
            Devices.Clear();

            VendorTakeover.Apply(_settings);
            var ctx = new DetectContext { System = SystemInfo.Read() };
            var hid = HidEnumerator.Enumerate();
            ctx.HidDevices = hid;
            Log.Info($"{hid.Count} HID koleksiyonu bulundu: " + string.Join(", ", hid.Select(h => $"{h.VendorId:X4}:{h.ProductId:X4}/{h.UsagePage:X4}").Distinct()));

            ctx.SystemSmbus = null;
            if (_settings.RamControl)
            {
                var others = SmbusUsersRunning();
                if (others.Length > 0)
                    Log.Warn("RAM kontrolü atlandı, SMBus'ı kullanan başka program çalışıyor: " + string.Join(", ", others));
                else
                {
                    if (_portIo == null) _portIo = PortIo.TryOpen();
                    if (_smbus == null) _smbus = Piix4Smbus.TryOpenSystemBus(_portIo);
                    ctx.SystemSmbus = _smbus;
                }
            }

            ctx.Detected = Devices;
            foreach (var p in Providers.OrderBy(p => p is IFallbackProvider ? 1 : 0))
            {
                // A driver that hangs on unfamiliar hardware must not leave the whole scan stuck on "Taranıyor…".
                List<RgbDevice> found = null;
                var t = new Thread(() =>
                {
                    try { found = p.Detect(ctx).ToList(); }
                    catch (Exception ex) { Log.Error("Tarama hatası: " + p.Name, ex); }
                }) { IsBackground = true, Name = "detect " + p.Name };
                t.Start();
                if (!t.Join(DetectTimeout)) { Log.Warn($"Tarama zaman aşımı, atlandı: {p.Name}"); continue; }
                foreach (var d in found ?? new List<RgbDevice>())
                {
                    if (Devices.Any(x => x.Id == d.Id)) { d.Dispose(); continue; }
                    Devices.Add(d);
                    Log.Info($"Cihaz: {d.Name} [{d.Id}] ({p.Name})");
                }
            }

            foreach (var d in Devices)
            {
                try { d.Initialize(); }
                catch (Exception ex) { d.Status = "Başlatılamadı: " + ex.Message; Log.Error(d.Name, ex); }
            }
            Log.Info(Devices.Count == 0 ? "Tarama bitti: desteklenen cihaz bulunamadı." : $"Tarama bitti: {Devices.Count} cihaz.");
            // Takeover is only worth it when rgbstone drives the hardware those services fight over.
            if (_settings.VendorTakeover && !TakeoverNeeded(Devices)) VendorTakeover.Restore(_settings);

            if (!_settings.ImportedSignalRgb || _settings.Devices.Values.All(d => d.ZoneNotes.Count == 0))
            {
                SignalRgbImport.Apply(_settings, Devices);
                _settings.ImportedSignalRgb = true;
                _settings.Save();
            }
            FindUnknown(hid);
            ApplyZoneSettings();
            _ramWaiting = _settings.RamControl && ctx.SystemSmbus == null && SmbusUsersRunning().Length > 0;
            TakeBaseline();
            DevicesChanged?.Invoke();
            Start();
            EnsureWatcher();
        }

        // ---------- devices from RGB brands that no driver claimed ----------

        /// <summary>USB vendor ids of brands that sell RGB hardware.</summary>
        static readonly Dictionary<ushort, string> RgbVendors = new Dictionary<ushort, string>
        {
            [0x0B05] = "ASUS", [0x1B1C] = "Corsair", [0x1532] = "Razer", [0x0951] = "HyperX", [0x03F0] = "HyperX (HP)", [0x1038] = "SteelSeries",
            [0x046D] = "Logitech", [0x1E71] = "NZXT", [0x1462] = "MSI", [0x0DB0] = "MSI", [0x048D] = "Gigabyte / ITE", [0x26CE] = "ASRock",
            [0x0CF2] = "Lian Li", [0x1E7D] = "Roccat", [0x2516] = "Cooler Master", [0x264A] = "Thermaltake", [0x258A] = "Glorious / Sinowealth",
            [0x31E3] = "Wooting", [0x3842] = "EVGA", [0x1044] = "Gigabyte", [0x2F68] = "Hyte", [0x0416] = "Winbond / Nuvoton", [0x1A86] = "QinHeng (LED kontrolcü)",
        };

        public sealed class UnknownDevice { public string Brand, Product, Id; }
        public List<UnknownDevice> UnknownDevices { get; } = new List<UnknownDevice>();

        void FindUnknown(List<HidDeviceInfo> hid)
        {
            UnknownDevices.Clear();
            var claimed = new HashSet<string>(Devices.Select(d => d.Id));
            foreach (var g in hid.Where(h => RgbVendors.ContainsKey(h.VendorId) && h.UsagePage >= 0xFF00).GroupBy(h => (h.VendorId, h.ProductId)))
            {
                string prefix = $"hid:{g.Key.VendorId:x4}:{g.Key.ProductId:x4}";
                if (claimed.Any(id => id.StartsWith(prefix))) continue;
                if (_noLightIds.Contains(prefix)) continue;
                var first = g.First();
                UnknownDevices.Add(new UnknownDevice { Brand = RgbVendors[g.Key.VendorId], Product = first.Product, Id = $"{g.Key.VendorId:X4}:{g.Key.ProductId:X4}" });
            }
            if (UnknownDevices.Count > 0)
                Log.Info("Tanınmayan cihazlar: " + string.Join(", ", UnknownDevices.Select(u => $"{u.Brand} {u.Product} [{u.Id}]")));
        }

        /// <summary>Devices a driver recognised as having no controllable lighting (e.g. a mouse without RGB behind a receiver).</summary>
        static readonly HashSet<string> _noLightIds = new HashSet<string>();
        public static void MarkNoLighting(ushort vid, ushort pid) { lock (_noLightIds) _noLightIds.Add($"hid:{vid:x4}:{pid:x4}"); }

        // ---------- hand-back to vendor software ----------
        //
        // Vendor programs already running when rgbstone scans are taken over (the device is put in direct mode anyway).
        // A vendor program started *later* means the user wants it: its devices are paused while it runs and
        // re-initialized as soon as it exits. Nothing is ever stopped or killed here, so there is no fight and no loop.

        readonly HashSet<int> _baseline = new HashSet<int>();
        Thread _watcher;
        volatile bool _ramWaiting, _rescanning;

        void TakeBaseline()
        {
            lock (_baseline)
            {
                _baseline.Clear();
                foreach (var name in Devices.SelectMany(d => d.ConflictingProcesses).Distinct())
                    foreach (var p in SafeProcesses(name)) _baseline.Add(p.Id);
            }
            foreach (var d in Devices) d.Paused = false;
        }

        static Process[] SafeProcesses(string name)
        {
            try { return Process.GetProcessesByName(name); } catch { return new Process[0]; }
        }

        void EnsureWatcher()
        {
            if (_watcher != null) return;
            _watcher = new Thread(WatchLoop) { IsBackground = true, Name = "rgb vendor watch", Priority = ThreadPriority.BelowNormal };
            _watcher.Start();
        }

        void WatchLoop()
        {
            while (true)
            {
                Thread.Sleep(2000);
                if (_rescanning || _inScan) continue;
                try { WatchOnce(); } catch (Exception ex) { Log.Error("İzleme", ex); }
            }
        }

        void WatchOnce()
        {
            var cache = new Dictionary<string, Process[]>(StringComparer.OrdinalIgnoreCase);
            Process[] Get(string n) { if (!cache.TryGetValue(n, out var p)) cache[n] = p = SafeProcesses(n); return p; }

            List<RgbDevice> devices;
            lock (_sync) devices = Devices.ToList();
            ReassertTakeover(devices);
            foreach (var d in devices)
            {
                if (d is UnavailableDevice) continue;
                List<string> newcomers;
                lock (_baseline)
                    newcomers = d.ConflictingProcesses.Where(n => Get(n).Any(p => !_baseline.Contains(p.Id))).ToList();

                bool ram = d.Kind == DeviceKind.Ram;
                if (newcomers.Count > 0 && !d.Paused)
                {
                    d.Paused = true;
                    d.Status = "Üretici yazılımı kontrolde (" + newcomers[0] + ")";
                    Log.Info($"{d.Name}: {string.Join(", ", newcomers)} açıldı, cihaz ona bırakıldı.");
                    if (ram) _ramHoldUntil = DateTime.Now + RamQuiet;
                }
                else if (ram && newcomers.Count == 0 && !d.Paused && DateTime.Now < _ramHoldUntil)
                {
                    // An MSI helper was just stopped: it may have written the sticks, so pause and repaint after the quiet period.
                    d.Paused = true;
                    d.Status = "SMBus sakinleşmesi bekleniyor";
                }
                else if (newcomers.Count == 0 && d.Paused && !(ram && DateTime.Now < _ramHoldUntil))
                {
                    Log.Info($"{d.Name}: üretici yazılımı kapandı, kontrol geri alınıyor.");
                    try { d.Initialize(); d.Paused = false; }
                    catch (Exception ex) { d.Status = "Geri alınamadı: " + ex.Message; Log.Error(d.Name, ex); }
                }
            }

            // RAM was skipped because another SMBus program was running; scan again once it is gone.
            if (_ramWaiting && _settings.RamControl && SmbusUsersRunning().Length == 0)
            {
                _ramWaiting = false;
                Log.Info("SMBus serbest kaldı, RAM için yeniden taranıyor.");
                _rescanning = true;
                try { Scan(); } finally { _rescanning = false; }
            }
        }

        // ---------- takeover kept while running ----------
        //
        // MSI Center restarts LEDKeeper2 (and sometimes the lighting services) in the background, which put the Arctic
        // header back on MSI's own effect. That is not the user opening MSI Center, so with takeover on the helper is stopped
        // again and the devices re-initialized. The MSI Center window itself is the user's choice: while it is open nothing
        // is stopped and the normal hand-back above applies. If the helper keeps coming back, rgbstone gives up for a while
        // instead of fighting it.

        /// <summary>Vendor windows the user opens on purpose.</summary>
        static readonly string[] VendorApps = { "MSI Center" };
        const int RetakeLimit = 3;
        static readonly TimeSpan RetakeWindow = TimeSpan.FromMinutes(10), RetakeBackoff = TimeSpan.FromMinutes(30);
        readonly Queue<DateTime> _retakes = new Queue<DateTime>();
        DateTime _retakeBlockedUntil;

        static bool TakeoverNeeded(IEnumerable<RgbDevice> devices) =>
            devices.Any(d => !(d is UnavailableDevice) && d.Kind != DeviceKind.Ram && d.ConflictingProcesses.Any(VendorTakeover.IsTarget));

        /// <summary>After an SMBus writer goes away, RAM stays quiet a little longer so its last transfers can finish.</summary>
        static readonly TimeSpan RamQuiet = TimeSpan.FromSeconds(10);
        DateTime _ramHoldUntil;

        void ReassertTakeover(List<RgbDevice> devices)
        {
            if (!_settings.VendorTakeover || DateTime.Now < _retakeBlockedUntil || !TakeoverNeeded(devices)) return;
            if (VendorApps.Any(n => SafeProcesses(n).Length > 0)) return;
            if (!VendorTakeover.TargetsRunning()) return;

            var now = DateTime.Now;
            while (_retakes.Count > 0 && now - _retakes.Peek() > RetakeWindow) _retakes.Dequeue();
            if (_retakes.Count >= RetakeLimit)
            {
                _retakes.Clear();
                _retakeBlockedUntil = now + RetakeBackoff;
                Log.Warn($"MSI yardımcısı sürekli geri açılıyor, {RetakeBackoff.TotalMinutes:0} dakika boyunca cihaz ona bırakılıyor.");
                return;
            }
            _retakes.Enqueue(now);

            // The helper may be on the SMBus right now: stop RAM writes first.
            foreach (var d in devices.Where(d => d.Kind == DeviceKind.Ram)) { d.Paused = true; d.Status = "SMBus sakinleşmesi bekleniyor"; }
            _ramHoldUntil = now + RamQuiet;
            var taken = VendorTakeover.Apply(_settings);
            if (taken.Count == 0) return;
            Log.Info("Arka planda geri açılan üretici yardımcısı tekrar devralındı: " + string.Join(", ", taken));
            foreach (var d in devices.Where(d => !(d is UnavailableDevice) && d.Kind != DeviceKind.Ram && d.ConflictingProcesses.Any(VendorTakeover.IsTarget)))
            {
                bool otherApp;
                lock (_baseline) otherApp = d.ConflictingProcesses.Any(n => !VendorTakeover.IsTarget(n) && SafeProcesses(n).Any(p => !_baseline.Contains(p.Id)));
                if (otherApp) continue; // another vendor program the user opened keeps the device
                try { d.Initialize(); d.Paused = false; }
                catch (Exception ex) { d.Status = "Geri alınamadı: " + ex.Message; Log.Error(d.Name, ex); }
            }
        }

        /// <summary>
        /// Programs known to drive the SMBus directly. Two programs talking on the bus at once can redirect a write
        /// to another chip (DDR5 power management sits on the same bus), so RAM control stays off while any of them runs.
        /// MSI Center's Mystic Light also drives Corsair DDR5 lighting, through LEDKeeper2 as well as its services.
        /// </summary>
        public static readonly string[] SmbusUsers = { "SignalRgb", "iCUE", "OpenRGB", "LightingService", "LightKeeperService", "Mystic_Light_Service", "LEDKeeper2", "HWiNFO64", "HWiNFO32", "AIDA64", "CPUID HWMonitor", "ZenTimings" };

        public static string[] SmbusUsersRunning() =>
            SmbusUsers.Where(p => { try { return Process.GetProcessesByName(p).Length > 0; } catch { return false; } }).ToArray();

        public void ApplyZoneSettings()
        {
            lock (_sync)
                foreach (var d in Devices)
                {
                    var ds = _settings.For(d.Id);
                    foreach (var z in d.Zones.Where(z => z.Resizable))
                    {
                        z.Resize(ds.ZoneLeds.TryGetValue(z.Name, out int n) ? n : z.DefaultLeds);
                        if (z.Detected == null && ds.ZoneNotes.TryGetValue(z.Name, out var note)) z.Detected = note;
                        z.SegmentSize = ds.ZoneSegments.TryGetValue(z.Name, out int seg) ? seg : GuessSegment(z);
                    }
                }
        }

        /// <summary>
        /// "Arctic Liquid Freezer III Pro + 3 × ARCTIC P14" is 4 chained parts; if the LEDs split evenly, each part is one ring.
        /// </summary>
        public static int GuessSegment(RgbZone z)
        {
            if (string.IsNullOrEmpty(z.Detected) || z.LedCount == 0) return 0;
            int parts = 0;
            foreach (var part in z.Detected.Split('+'))
            {
                var m = System.Text.RegularExpressions.Regex.Match(part.Trim(), @"^(\d+)\s*[x×]\s");
                parts += m.Success ? int.Parse(m.Groups[1].Value) : 1;
            }
            return parts > 1 && z.LedCount % parts == 0 ? z.LedCount / parts : 0;
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            foreach (var d in Devices)
            {
                var w = new Worker { Device = d };
                w.Thread = new Thread(() => WorkerLoop(w)) { IsBackground = true, Name = "rgb " + d.Name, Priority = ThreadPriority.AboveNormal };
                _workers.Add(w);
                w.Thread.Start();
            }
            _clock = new Thread(ClockLoop) { IsBackground = true, Name = "rgb clock", Priority = ThreadPriority.AboveNormal };
            _clock.Start();
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            _clock?.Join(1000);
            foreach (var w in _workers) w.Frame.Set();
            foreach (var w in _workers) w.Thread.Join(1500);
            _workers.Clear();
        }

        [System.Runtime.InteropServices.DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
        [System.Runtime.InteropServices.DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

        void ClockLoop()
        {
            timeBeginPeriod(1); // 1 ms sleep resolution for an even frame rate
            try { ClockLoopCore(); } finally { timeEndPeriod(1); }
        }

        void ClockLoopCore()
        {
            int frames = 0;
            var fpsWatch = Stopwatch.StartNew();
            while (_running)
            {
                long start = _time.ElapsedTicks;
                int fps = Math.Max(5, Math.Min(60, _settings.Fps));
                foreach (var w in _workers)
                {
                    if (w.Busy || w.Device.Paused || !_settings.For(w.Device.Id).Enabled) continue;
                    long minTicks = Stopwatch.Frequency / Math.Max(1, Math.Min(fps, w.Device.MaxFps));
                    if (start - w.LastSentTicks < minTicks) continue;
                    w.LastSentTicks = start;
                    w.Busy = true;
                    w.Frame.Set();
                }
                frames++;
                if (fpsWatch.ElapsedMilliseconds >= 1000) { MeasuredFps = frames * 1000.0 / fpsWatch.ElapsedMilliseconds; frames = 0; fpsWatch.Restart(); }

                long frameTicks = Stopwatch.Frequency / fps;
                long sleepMs = (frameTicks - (_time.ElapsedTicks - start)) * 1000 / Stopwatch.Frequency;
                if (sleepMs > 0) Thread.Sleep((int)sleepMs);
            }
        }

        void WorkerLoop(Worker w)
        {
            while (true)
            {
                w.Frame.WaitOne();
                if (!_running) return;
                try
                {
                    Render(w.Device);
                    w.Device.Apply();
                }
                catch (Exception ex)
                {
                    w.Device.Status = "Hata: " + ex.Message;
                    Log.Error(w.Device.Name, ex);
                    Thread.Sleep(1000);
                }
                finally { w.Busy = false; }
            }
        }

        void Render(RgbDevice d)
        {
            var effect = Effect.ById(_settings.Effect);
            var s = _settings.ToEffectSettings();
            double t = _time.Elapsed.TotalSeconds;
            lock (_sync)
                foreach (var z in d.Zones)
                    for (int i = 0; i < z.LedCount; i++)
                        z.Colors[i] = effect.ColorAtLed(i, z.PositionOf(i), t, s).Scale(s.Brightness);
        }

        /// <summary>Paints every enabled device with a fixed color, used on exit and before sleep.</summary>
        public void ShutdownAll(Rgb color)
        {
            Stop();
            foreach (var d in Devices)
            {
                if (!_settings.For(d.Id).Enabled || d.Paused) continue;
                try { lock (_sync) d.Shutdown(color); } catch (Exception ex) { Log.Error(d.Name, ex); }
            }
        }

        public void Dispose()
        {
            Stop();
            foreach (var d in Devices) { try { d.Dispose(); } catch { } }
            _smbus?.Dispose();
            _portIo?.Dispose();
        }
    }
}
