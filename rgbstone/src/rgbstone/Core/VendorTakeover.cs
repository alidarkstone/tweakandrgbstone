using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.ServiceProcess;

namespace RgbStone.Core
{
    /// <summary>
    /// Optional takeover: while rgbstone runs, the vendor lighting services that fight over the same hardware are stopped,
    /// and they are started again when rgbstone exits. The list of stopped services is saved first, so a crash or power loss
    /// is repaired on the next start.
    /// </summary>
    public static class VendorTakeover
    {
        /// <summary>Lighting-only services. Fan curves, monitoring and other vendor features keep running.</summary>
        static readonly string[] Services = { "LightKeeperService", "Mystic_Light_Service" };
        static readonly string[] Processes = { "LEDKeeper2" };

        /// <summary>Stops the lighting services and helpers that are running. Returns the names it stopped.</summary>
        public static List<string> Apply(AppSettings settings)
        {
            var taken = new List<string>();
            if (!settings.VendorTakeover) { Restore(settings); return taken; }
            foreach (var name in Services)
            {
                try
                {
                    using (var sc = new ServiceController(name))
                    {
                        if (sc.Status != ServiceControllerStatus.Running) continue;
                        if (!settings.StoppedServices.Contains(name)) { settings.StoppedServices.Add(name); settings.Save(); }
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                        Log.Info("Devralındı, servis durduruldu: " + name);
                        taken.Add(name);
                    }
                }
                catch (InvalidOperationException) { } // service not installed
                catch (Exception ex) { Log.Error("Servis durdurulamadı: " + name, ex); }
            }
            foreach (var p in Processes.SelectMany(n => Process.GetProcessesByName(n)))
            {
                try { p.Kill(); p.WaitForExit(5000); Log.Info("Devralındı, işlem kapatıldı: " + p.ProcessName); taken.Add(p.ProcessName); }
                catch (Exception ex) { Log.Error("İşlem kapatılamadı: " + p.ProcessName, ex); }
            }
            return taken;
        }

        /// <summary>
        /// True when a takeover target came back while rgbstone runs. MSI Center relaunches LEDKeeper2 (and can restart the
        /// lighting services) on its own, so a reappearance is not the user opening the vendor app.
        /// </summary>
        public static bool TargetsRunning()
        {
            foreach (var name in Services)
            {
                try { using (var sc = new ServiceController(name)) if (sc.Status == ServiceControllerStatus.Running || sc.Status == ServiceControllerStatus.StartPending) return true; }
                catch { }
            }
            foreach (var name in Processes)
            {
                try { if (Process.GetProcessesByName(name).Length > 0) return true; } catch { }
            }
            return false;
        }

        public static bool IsTarget(string name) =>
            Services.Contains(name, StringComparer.OrdinalIgnoreCase) || Processes.Contains(name, StringComparer.OrdinalIgnoreCase);

        /// <summary>Starts every service rgbstone stopped. LEDKeeper2 is relaunched by Mystic_Light_Service itself.</summary>
        public static void Restore(AppSettings settings)
        {
            if (settings.StoppedServices.Count == 0) return;
            foreach (var name in settings.StoppedServices.ToArray())
            {
                try
                {
                    using (var sc = new ServiceController(name))
                    {
                        if (sc.Status == ServiceControllerStatus.Stopped)
                        {
                            sc.Start();
                            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
                        }
                        Log.Info("Servis geri başlatıldı: " + name);
                    }
                }
                catch (Exception ex) { Log.Error("Servis başlatılamadı: " + name, ex); }
                settings.StoppedServices.Remove(name);
            }
            settings.Save();
        }
    }
}
