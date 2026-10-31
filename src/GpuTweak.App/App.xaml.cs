using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using GpuTweak.App.ViewModels;
using GpuTweak.App.Views;
using GpuTweak.Core;
using GpuTweak.Core.Backends;
using GpuTweak.Core.Localization;
using GpuTweak.Core.Safety;
using GpuTweak.Core.Settings;
using RgbStone.UI;

namespace GpuTweak.App;

public partial class App : Application
{
    private const string Title = "tweakandrgbstone";
    private MainViewModel? _viewModel;
    private RgbHost? _rgb;
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        _singleInstance = new Mutex(true, @"Global\tweakandrgbstone.single-instance", out var first);
        if (!first)
        {
            MessageBox.Show(L.T("tweakandrgbstone zaten çalışıyor (saatin yanındaki simgelerde)."), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // The separate apps would fight this one over the card's settings and the LEDs.
        var legacy = RunningLegacyApps();
        if (legacy.Count > 0)
        {
            MessageBox.Show(
                L.F("Eski program açık: {0}.\n\nİkisi aynı anda ekran kartını ve RGB cihazlarını kontrol etmeye çalışır. Önce eski programı saatin yanındaki simgesine sağ tıklayıp Çıkış ile kapat, sonra tweakandrgbstone'u yeniden aç.", string.Join(", ", legacy)),
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        var dataDirectory = DataPaths.Resolve();

        // Diagnostics from rgbstone; they only touch the RGB devices.
        if (e.Args.Any(a => a.Equals("--scan", StringComparison.OrdinalIgnoreCase)))
        {
            RgbHost.ConsoleScan(dataDirectory);
            Shutdown();
            return;
        }
        if (e.Args.Any(a => a.Equals("--msi-test", StringComparison.OrdinalIgnoreCase)))
        {
            RgbHost.MsiLegacyTest(dataDirectory);
            Shutdown();
            return;
        }

        DataPaths.ImportLegacy(dataDirectory, AppContext.BaseDirectory);
        Directory.CreateDirectory(Path.Combine(dataDirectory, "logos"));
        L.SetLanguage(new SettingsStore(dataDirectory).Load().Language);

        // Crash check must happen before anything touches the card.
        var crash = new CrashGuard(dataDirectory).BeginSession();

        var demo = e.Args.Any(a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase));
        var startup = e.Args.Any(a => a.Equals(Native.StartupTask.StartupArgument, StringComparison.OrdinalIgnoreCase));
        var backend = GpuBackendFactory.Create(demo);
        // At sign-in the graphics driver can still be loading; a hidden question would block the start-up.
        for (int i = 0; backend is null && startup && i < 6; i++)
        {
            Thread.Sleep(5000);
            backend = GpuBackendFactory.Create(demo);
        }
        if (backend is null && startup) backend = new DemoBackend();
        if (backend is null)
        {
            var answer = MessageBox.Show(
                L.T("Desteklenen bir NVIDIA ya da AMD ekran kartı bulunamadı. Ekran kartı sürücüsünün kurulu olduğundan emin ol.\n\n") +
                L.T("Arayüzü denemek için demo modunda açılsın mı?"),
                Title, MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (answer != MessageBoxResult.Yes)
            {
                Shutdown();
                return;
            }
            backend = new DemoBackend();
        }

        if (!startup) OfferToRemoveLegacyStartupTasks(dataDirectory);

        _viewModel = new MainViewModel(backend, dataDirectory, crash);
        _rgb = new RgbHost(dataDirectory);
        var rgbView = new RgbView(_rgb);
        var window = new MainWindow(_viewModel, rgbView);
        MainWindow = window;
        // Started by Windows at sign-in: stay out of the way, the profile is applied in the background.
        if (startup)
            window.StartInTray();
        else
            window.Show();
        rgbView.Rescan();
    }

    /// <summary>Tweakstone and rgbstone processes, also under versioned exe names such as "Tweakstone-v0.12.3".</summary>
    private static List<string> RunningLegacyApps()
    {
        var names = new List<string>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                var name = p.ProcessName;
                if ((name.StartsWith("Tweakstone", StringComparison.OrdinalIgnoreCase) || name.StartsWith("rgbstone", StringComparison.OrdinalIgnoreCase))
                    && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                    names.Add(name);
            }
        }
        return names;
    }

    /// <summary>
    /// Asks once whether the old apps should stop starting with Windows. Only their Task Scheduler entries are removed,
    /// never the programs or their settings.
    /// </summary>
    private static void OfferToRemoveLegacyStartupTasks(string dataDirectory)
    {
        var marker = Path.Combine(dataDirectory, ".legacy-startup-asked");
        if (File.Exists(marker)) return;
        var tasks = Native.StartupTask.LegacyTasks();
        if (tasks.Count == 0) return;
        var answer = MessageBox.Show(
            L.F("Eski programlar hâlâ Windows ile başlıyor: {0}.\n\ntweakandrgbstone ile aynı anda açılırlarsa ekran kartı ve RGB ayarları çakışır. Eski programların Windows ile başlaması kapatılsın mı?\n\n(Eski programlar ve ayarları silinmez.)", string.Join(", ", tasks)),
            Title, MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes) Native.StartupTask.RemoveLegacyTasks();
        File.WriteAllText(marker, answer.ToString());
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _viewModel?.Dispose();
        _rgb?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, L.T("tweakandrgbstone – beklenmeyen hata"), MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
