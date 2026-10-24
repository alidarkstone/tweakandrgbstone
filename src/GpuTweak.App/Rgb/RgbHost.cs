// Ported from rgbstone, which was written without nullable annotations.
#nullable disable
using System;
using System.Threading;
using Microsoft.Win32;
using RgbStone.Core;

namespace RgbStone.UI
{
    /// <summary>
    /// The RGB side of tweakandrgbstone (formerly rgbstone's Program.Main): settings, the effect engine,
    /// the optional web dashboard, and what happens on sleep, sign-out and exit.
    /// </summary>
    public sealed class RgbHost : IDisposable
    {
        public AppSettings Settings { get; }
        public Engine Engine { get; }
        public Web.WebDashboard Dashboard { get; }
        bool _disposed;

        public RgbHost(string dataDirectory)
        {
            AppSettings.UseFolder(dataDirectory);
            Log.Init(AppSettings.Folder);
            Log.Info("RGB başlıyor, " + typeof(RgbHost).Assembly.GetName().Version);
            Settings = AppSettings.Load();
            Engine = new Engine(Settings);
            Dashboard = new Web.WebDashboard(Engine, Settings);
            if (Settings.WebEnabled) Dashboard.Start();
            else new Thread(() => Web.WebDashboard.CleanupIfDisabled(Settings)) { IsBackground = true }.Start();

            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionEnding += OnSessionEnding;
        }

        void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Suspend) Engine.ShutdownAll(Rgb.Black);
            else if (e.Mode == PowerModes.Resume)
                new Thread(() => { Thread.Sleep(4000); Engine.Scan(); }) { IsBackground = true }.Start();
        }

        void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            Engine.ShutdownAll(Rgb.Parse(Settings.ShutdownColor));
            VendorTakeover.Restore(Settings);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionEnding -= OnSessionEnding;
            Engine.ShutdownAll(Rgb.Parse(Settings.ShutdownColor));
            VendorTakeover.Restore(Settings);
            Settings.Save();
            Dashboard.Dispose();
            Engine.Dispose();
            Log.Info("RGB kapandı.");
        }

        /// <summary>"tweakandrgbstone.exe --msi-test": short color test on an MSI board that has no driver yet (see MsiMysticLightProvider).</summary>
        public static void MsiLegacyTest(string dataDirectory)
        {
            AppSettings.UseFolder(dataDirectory);
            Log.Init(AppSettings.Folder);
            var settings = AppSettings.Load();
            System.Windows.Forms.MessageBox.Show("Tamam'a basınca anakarta bağlı fanlar 6'şar saniye YEŞİL, KIRMIZI ve MAVİ olacak, sonra eski hâline dönecek.\nFanlara bak, sonra rgbstone.log dosyasını gönder.", "tweakandrgbstone MSI testi");
            bool takeover = settings.VendorTakeover;
            settings.VendorTakeover = true; // MSI's own service would repaint the fans during the test
            try
            {
                VendorTakeover.Apply(settings);
                Thread.Sleep(1000);
                Drivers.MsiMysticLightProvider.LegacyColorTest();
            }
            catch (Exception ex) { Log.Error("MSI test", ex); }
            finally
            {
                VendorTakeover.Restore(settings);
                settings.VendorTakeover = takeover;
                settings.Save();
            }
            System.Windows.Forms.MessageBox.Show("Test bitti. Fanlar hangi renklere döndü? rgbstone.log dosyasını gönder.", "tweakandrgbstone MSI testi");
        }

        /// <summary>Diagnostics: "tweakandrgbstone.exe --scan" detects devices, shows a short color test and writes everything to rgbstone.log.</summary>
        public static void ConsoleScan(string dataDirectory)
        {
            AppSettings.UseFolder(dataDirectory);
            Log.Init(AppSettings.Folder);
            var settings = AppSettings.Load();
            using (var engine = new Engine(settings))
            {
                engine.Scan();
                Thread.Sleep(4000);
                foreach (var d in engine.Devices) Log.Info($"SONUÇ {d.Name}: {d.TotalLeds} LED, {d.Status}");
                Log.Info($"Ölçülen FPS: {engine.MeasuredFps:0.0}");
                engine.Stop();
                VendorTakeover.Restore(settings);
            }
        }
    }
}
