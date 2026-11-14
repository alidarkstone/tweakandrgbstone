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
    private const double DesignWidth = 1400, DesignHeight = 1480, GpuLayoutWidth = 1456;
    // Window size the RGB page and the tab switch are designed for; they grow from here, never shrink.
    private const double BaseWidth = 1120, BaseHeight = 800;
    private const double MinGpuScale = 0.6, MaxScale = 1.6;

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
    /// GPU page: follows the window width (vertical scroll when taller than the window).
    /// RGB page and tab switch: grow past their design size, never shrink below it.
    /// </summary>
    private void UpdateUiScale()
    {
        double w = Root.ActualWidth, h = Root.ActualHeight;
        if (w <= 0 || h <= 0) return;

        // Always leave room for the vertical scroll bar so showing it can't change the scale (no flicker).
        var gpu = Math.Clamp((w - SystemParameters.VerticalScrollBarWidth) / GpuLayoutWidth, MinGpuScale, MaxScale);
        GpuScale.ScaleX = GpuScale.ScaleY = gpu;

        var ui = Math.Clamp(Math.Min(w / BaseWidth, h / BaseHeight), 1.0, MaxScale);
        RgbScale.ScaleX = RgbScale.ScaleY = ui;
        TabScale.ScaleX = TabScale.ScaleY = ui;
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
