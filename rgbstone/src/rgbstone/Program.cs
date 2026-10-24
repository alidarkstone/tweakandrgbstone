using System;
using System.Linq;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using RgbStone.Core;
using RgbStone.UI;

namespace RgbStone
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            using (var single = new Mutex(true, @"Global\rgbstone.single-instance", out bool first))
            {
                if (!first)
                {
                    // Silently quitting made a new copy look like it ran while the old one (in the tray) kept going.
                    System.Windows.Forms.MessageBox.Show("rgbstone zaten çalışıyor (saatin yanındaki simgelerde).\nYeni sürümü açmak için önce onu sağ tıklayıp Çıkış ile kapat.", "rgbstone");
                    return 0;
                }

                AppSettings.ResolveFolder();
                Log.Init(AppSettings.Folder);
                Log.Info("rgbstone başlıyor, " + typeof(Program).Assembly.GetName().Version);

                var settings = AppSettings.Load();

                if (args.Contains("--scan"))
                    return ConsoleScan(settings);
                if (args.Contains("--msi-test"))
                    return MsiLegacyTest(settings);

                AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Error("Beklenmeyen hata", e.ExceptionObject as Exception);
                System.Windows.Forms.Application.EnableVisualStyles();

                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.DispatcherUnhandledException += (s, e) => { Log.Error("Arayüz", e.Exception); e.Handled = true; };
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/rgbstone;component/UI/Theme.xaml") });

                using (var engine = new Engine(settings))
                using (var dashboard = new Web.WebDashboard(engine, settings))
                {
                    if (settings.WebEnabled) dashboard.Start();
                    else new Thread(() => Web.WebDashboard.CleanupIfDisabled(settings)) { IsBackground = true }.Start();
                    var window = new MainWindow(engine, settings, dashboard);
                    SystemEvents.PowerModeChanged += (s, e) =>
                    {
                        if (e.Mode == PowerModes.Suspend) engine.ShutdownAll(Rgb.Black);
                        else if (e.Mode == PowerModes.Resume)
                            new Thread(() => { Thread.Sleep(4000); engine.Scan(); }) { IsBackground = true }.Start();
                    };
                    SystemEvents.SessionEnding += (s, e) => { engine.ShutdownAll(Rgb.Parse(settings.ShutdownColor)); VendorTakeover.Restore(settings); };

                    if (!args.Contains("--tray")) window.Show();
                    else new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle(); // hot-plug messages need a window handle
                    window.Rescan();
                    app.Run();

                    engine.ShutdownAll(Rgb.Parse(settings.ShutdownColor));
                    VendorTakeover.Restore(settings);
                    settings.Save();
                }
                Log.Info("rgbstone kapandı.");
                return 0;
            }
        }

        /// <summary>"rgbstone.exe --msi-test": short color test on an MSI board that has no driver yet (see MsiMysticLightProvider).</summary>
        static int MsiLegacyTest(AppSettings settings)
        {
            System.Windows.Forms.MessageBox.Show("Tamam'a basınca anakarta bağlı fanlar 6'şar saniye YEŞİL, KIRMIZI ve MAVİ olacak, sonra eski hâline dönecek.\nFanlara bak, sonra rgbstone.log dosyasını gönder.", "rgbstone MSI testi");
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
            System.Windows.Forms.MessageBox.Show("Test bitti. Fanlar hangi renklere döndü? rgbstone.log dosyasını gönder.", "rgbstone MSI testi");
            return 0;
        }

        /// <summary>Diagnostics: "rgbstone.exe --scan" detects devices, shows a short color test and writes everything to rgbstone.log.</summary>
        static int ConsoleScan(AppSettings settings)
        {
            using (var engine = new Engine(settings))
            {
                engine.Scan();
                Thread.Sleep(4000);
                foreach (var d in engine.Devices) Log.Info($"SONUÇ {d.Name}: {d.TotalLeds} LED, {d.Status}");
                Log.Info($"Ölçülen FPS: {engine.MeasuredFps:0.0}");
                engine.Stop();
                VendorTakeover.Restore(settings);
            }
            return 0;
        }
    }
}
