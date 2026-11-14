using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GpuTweak.App.Native;
using GpuTweak.App.ViewModels;
using RgbStone.UI;

namespace GpuTweak.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly OverlayWindow _overlay;
    private GlobalHotKey? _hotKey, _artifactHotKey;
    private readonly TrayIcon _tray;
    private readonly RgbView _rgb;
    private bool _exiting;

    // GPU layout design size (GpuContent in MainWindow.xaml, plus its margins); used for the opening size and the UI scale.
    private const double DesignWidth = 1400, DesignHeight = 1480;
    // Window size the RGB page and the tab switch are designed for; they grow from here, never shrink.
    private const double BaseWidth = 1120, BaseHeight = 800;
    private const double MinGpuScale = 0.6, MaxScale = 2.0;
    // Width range (unscaled) the GPU layout may stretch to; below the design width the boxes would get cramped.
    private const double MinLayoutWidth = DesignWidth, MaxLayoutWidth = 2600;

    public MainWindow(MainViewModel viewModel, RgbView rgb)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _rgb = rgb;
        RgbPage.Content = rgb;
        _overlay = new OverlayWindow(viewModel.Overlay);
        _tray = new TrayIcon(ShowFromTray, ExitApp, rgb.BuildEffectMenu());
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.TrayText)) _tray.SetText(viewModel.TrayText);
        };

        FitToScreen();
        Root.SizeChanged += (_, _) => UpdateUiScale();
        GpuPage.SizeChanged += (_, _) => FitGpuPage();
        // Showing or hiding the live panel changes the page height: fit again once the change is laid out.
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsMonitorVisible))
                Dispatcher.BeginInvoke(FitGpuPage, System.Windows.Threading.DispatcherPriority.Loaded);
        };

        foreach (RadioButton corner in CornerPicker.Children)
            corner.IsChecked = (string)corner.Tag == viewModel.Overlay.Corner.ToString();

        SourceInitialized += (_, _) =>
        {
            if (WindowEffects.ApplyMica(this))
            {
                Background = Brushes.Transparent;
                Root.Background = (Brush)FindResource("WindowTintBrush");
            }
            _hotKey = new GlobalHotKey(this, GlobalHotKey.ModControl | GlobalHotKey.ModShift, (uint)KeyInterop.VirtualKeyFromKey(Key.O),
                () => viewModel.Overlay.Toggle());
            // Report an artifact without leaving the game.
            _artifactHotKey = new GlobalHotKey(this, GlobalHotKey.ModControl | GlobalHotKey.ModShift, (uint)KeyInterop.VirtualKeyFromKey(Key.A),
                () => { if (viewModel.ReportArtifactCommand.CanExecute(null)) viewModel.ReportArtifactCommand.Execute(null); }, id: 0x4755);
        };
        Loaded += (_, _) => _overlay.Show();
        // X hides to the notification area; the app really exits only from the tray menu.
        Closing += (_, e) =>
        {
            if (_exiting) return;
            e.Cancel = true;
            Hide();
            _tray.ShowHiddenHint();
        };
        Closed += (_, _) =>
        {
            _rgb.Shutdown();
            _tray.Dispose();
            _hotKey?.Dispose();
            _artifactHotKey?.Dispose();
            _overlay.Close();
        };
    }

    /// <summary>Started at Windows sign-in: run in the notification area without showing the window.</summary>
    public void StartInTray()
    {
        // Show once so the window handle exists (hotkeys) and the overlay starts, then hide straight away.
        WindowState = WindowState.Minimized;
        ShowInTaskbar = false;
        Show();
        Hide();
        _overlay.Show();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApp()
    {
        _exiting = true;
        Close();
    }

    /// <summary>
    /// Opens the window at most 90% of the screen with the layout's proportions. After that the
    /// content scales with the window, whatever size the user drags it to.
    /// </summary>
    private void FitToScreen()
    {
        var area = SystemParameters.WorkArea;
        // The GPU page scrolls when it doesn't fit, so open as large as the screen comfortably allows.
        Width = Math.Min(DesignWidth, area.Width * 0.9);
        Height = Math.Min(DesignHeight, area.Height * 0.9);
    }

    /// <summary>
    /// Scales the UI with the window so text stays readable on a large or maximized window.
    /// GPU page: the boxes widen to the window's shape, then the largest text size is picked at which the
    /// whole page still fits without scrolling (it only scrolls when even the smallest size doesn't fit).
    /// RGB page and tab switch: grow past their design size, never shrink below it.
    /// </summary>
    private void UpdateUiScale()
    {
        double w = Root.ActualWidth, h = Root.ActualHeight;
        if (w <= 0 || h <= 0) return;

        var ui = Math.Clamp(Math.Min(w / BaseWidth, h / BaseHeight), 1.0, MaxScale);
        RgbScale.ScaleX = RgbScale.ScaleY = ui;
        TabScale.ScaleX = TabScale.ScaleY = ui;

        FitGpuPage();
    }

    private void FitGpuPage()
    {
        double w = GpuPage.ActualWidth, h = GpuPage.ActualHeight;
        if (w <= 0 || h <= 0) return;
        var m = GpuContent.Margin;
        double availW = w - m.Left - m.Right, availH = h - m.Top - m.Bottom;

        // Layout width (unscaled) the boxes get at a given scale: as wide as the window allows, within limits.
        double LayoutWidth(double scale) => Math.Clamp(availW / scale, MinLayoutWidth, MaxLayoutWidth);
        double ContentHeight(double layoutWidth)
        {
            GpuContent.Width = layoutWidth;
            GpuContent.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return GpuContent.DesiredSize.Height - m.Top - m.Bottom; // DesiredSize includes the (unscaled) margin
        }

        GpuScale.ScaleX = GpuScale.ScaleY = 1; // measure unscaled
        bool Fits(double scale)
        {
            var lw = LayoutWidth(scale);
            return lw * scale <= availW + 0.5 && ContentHeight(lw) * scale <= availH + 0.5;
        }

        double lo = MinGpuScale, hi = MaxScale;
        if (Fits(hi)) lo = hi;
        else if (!Fits(lo)) hi = lo; // too small to fit even at the smallest text: keep that size and scroll
        else
            for (var i = 0; i < 14; i++)
            {
                var mid = (lo + hi) / 2;
                if (Fits(mid)) lo = mid; else hi = mid;
            }

        var scaleFinal = lo;
        var scrolls = !Fits(scaleFinal);
        // When it scrolls, leave room for the scroll bar.
        if (scrolls) availW -= SystemParameters.VerticalScrollBarWidth;
        GpuContent.Width = LayoutWidth(scaleFinal);
        GpuScale.ScaleX = GpuScale.ScaleY = scaleFinal;
    }

    /// <summary>GPU / RGB switch at the top of the window.</summary>
    private void OnTabChecked(object sender, RoutedEventArgs e)
    {
        if (GpuPage is null || RgbPage is null) return; // fires while the XAML is still loading
        var rgb = RgbTab.IsChecked == true;
        GpuPage.Visibility = rgb ? Visibility.Collapsed : Visibility.Visible;
        RgbPage.Visibility = rgb ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCornerChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string corner }) _viewModel.Overlay.SetCorner(corner);
    }
}
