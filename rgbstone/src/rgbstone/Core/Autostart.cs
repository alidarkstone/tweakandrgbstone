using System;
using System.Diagnostics;

namespace RgbStone.Core
{
    /// <summary>
    /// Start with Windows through a Task Scheduler logon task with highest privileges,
    /// so the elevated exe starts silently without a UAC prompt.
    /// </summary>
    public static class Autostart
    {
        const string TaskName = "rgbstone";

        public static bool IsEnabled() => Run("/Query /TN " + TaskName) == 0;

        public static bool Enable(string exePath)
        {
            string tr = "\\\"" + exePath + "\\\" --tray";
            int r = Run($"/Create /F /TN {TaskName} /SC ONLOGON /RL HIGHEST /DELAY 0000:10 /TR \"{tr}\"");
            if (r != 0) Log.Error("Otomatik başlatma görevi oluşturulamadı, kod " + r);
            return r == 0;
        }

        public static bool Disable() => Run("/Delete /F /TN " + TaskName) == 0;

        static int Run(string args)
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe", args)
                {
                    CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                };
                using (var p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit(10000);
                    return p.ExitCode;
                }
            }
            catch (Exception ex) { Log.Error("schtasks", ex); return -1; }
        }
    }
}
