// Ported from rgbstone, which was written without nullable annotations.
#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RgbStone.Core;
using WinForms = System.Windows.Forms;
using FxEffect = RgbStone.Core.Effect;

namespace RgbStone.UI
{
    /// <summary>The RGB tab: rgbstone's window, now hosted inside the tweakandrgbstone main window.</summary>
    public partial class RgbView : UserControl
    {
        sealed class NavEntry
        {
            public string Page;          // "effects", "settings", "device", "header"
            public RgbDevice Device;
            public TextBlock Status;
        }

        readonly Engine _engine;
        readonly AppSettings _settings;
        readonly DispatcherTimer _refresh, _animation;
        readonly Stopwatch _clock = Stopwatch.StartNew();
        readonly List<(WriteableBitmap bmp, Func<FxEffect> effect)> _previews = new List<(WriteableBitmap, Func<FxEffect>)>();
        readonly List<Action> _pageRefreshers = new List<Action>();
        bool _scanning;

        static readonly Dictionary<DeviceKind, (string glyph, Color color)> KindIcons = new Dictionary<DeviceKind, (string, Color)>
        {
            [DeviceKind.Motherboard] = ("", Color.FromRgb(0x5E, 0x5C, 0xE6)),
            [DeviceKind.Gpu] = ("", Color.FromRgb(0x30, 0xD1, 0x58)),
            [DeviceKind.Ram] = ("", Color.FromRgb(0xFF, 0x9F, 0x0A)),
            [DeviceKind.Keyboard] = ("", Color.FromRgb(0x0A, 0x84, 0xFF)),
            [DeviceKind.Mouse] = ("", Color.FromRgb(0x0A, 0x84, 0xFF)),
            [DeviceKind.Mousepad] = ("", Color.FromRgb(0xFF, 0x37, 0x5F)),
            [DeviceKind.Headset] = ("", Color.FromRgb(0xBF, 0x5A, 0xF2)),
            [DeviceKind.LedController] = ("", Color.FromRgb(0x64, 0xD2, 0xFF)),
            [DeviceKind.Cooler] = ("", Color.FromRgb(0x64, 0xD2, 0xFF)),
            [DeviceKind.Other] = ("", Color.FromRgb(0x8E, 0x8E, 0x93)),
        };

        static readonly string[] Presets =
        {
            "#FF3B30", "#FF9500", "#FFCC00", "#34C759", "#00C7BE", "#30B0C7",
            "#0A84FF", "#5856D6", "#AF52DE", "#FF2D55", "#FFFFFF", "#000000",
        };

        readonly Web.WebDashboard _dashboard;

        public RgbView(RgbHost host)
        {
            _engine = host.Engine;
            _settings = host.Settings;
            _dashboard = host.Dashboard;
            // changes made from a phone show up in the open page
            _dashboard.SettingsChanged += () => Dispatcher.BeginInvoke((Action)(() =>
            {
                if ((Nav.SelectedItem as ListBoxItem)?.Tag is NavEntry e && e.Page != "settings") ShowSelected();
            }));
            InitializeComponent();

            LogoImage.Source = LoadPng("logo-64.png");
            VersionText.Text = "Sürüm " + Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

            RescanButton.Click += (s, e) => Rescan();
            Nav.SelectionChanged += (s, e) => ShowSelected();
            BuildNav();
            Nav.SelectedIndex = 0;

            _refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _refresh.Tick += (s, e) => RefreshStatus();
            _refresh.Start();

            _animation = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
            _animation.Tick += (s, e) => AnimatePreviews();
            _animation.Start();

            _engine.DevicesChanged += () => Dispatcher.BeginInvoke((Action)(() => { BuildNav(); _scanning = false; RescanButton.IsEnabled = true; RescanText.Text = "Cihazları yeniden tara"; }));
            // hot-plug messages arrive at the main window; it exists (hidden) even when started in the tray
            Loaded += (s, e) => HookDeviceChanges();
        }

        // ---------- hot-plug: a newly connected device is picked up without a manual rescan ----------

        DispatcherTimer _plugDebounce;
        string _hidSignature;

        void HookDeviceChanges()
        {
            if (_plugDebounce != null) return; // Loaded fires again whenever the tab is shown
            var window = Window.GetWindow(this);
            if (window == null) return;
            _hidSignature = HidSignature();
            _plugDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _plugDebounce.Tick += (s, e) =>
            {
                _plugDebounce.Stop();
                string sig = HidSignature();
                if (sig == _hidSignature) return; // something non-HID (a USB stick, a monitor) changed
                _hidSignature = sig;
                Log.Info("Cihaz takıldı/çıkarıldı, yeniden taranıyor.");
                Rescan();
            };
            HwndSource.FromHwnd(new WindowInteropHelper(window).EnsureHandle())?.AddHook((IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                const int WM_DEVICECHANGE = 0x0219, DBT_DEVNODES_CHANGED = 0x0007, DBT_DEVICEARRIVAL = 0x8000, DBT_DEVICEREMOVECOMPLETE = 0x8004;
                if (msg != WM_DEVICECHANGE) return IntPtr.Zero;
                long code = w.ToInt64(); // ToInt32 throws on 64-bit wParam values other messages carry
                if (code == DBT_DEVNODES_CHANGED || code == DBT_DEVICEARRIVAL || code == DBT_DEVICEREMOVECOMPLETE)
                {
                    _plugDebounce.Stop();
                    _plugDebounce.Start();
                }
                return IntPtr.Zero;
            });
        }

        static string HidSignature()
        {
            try
            {
                return string.Join("|", Transport.HidEnumerator.Enumerate()
                    .Select(d => $"{d.VendorId:x4}:{d.ProductId:x4}:{d.Interface}:{d.Collection}").OrderBy(x => x));
            }
            catch { return ""; }
        }

        // ---------- assets / tray ----------

        static BitmapImage LoadPng(string name)
        {
            var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (s == null) return null;
            var img = new BitmapImage();
            img.BeginInit(); img.StreamSource = s; img.CacheOption = BitmapCacheOption.OnLoad; img.EndInit(); img.Freeze();
            return img;
        }

        /// <summary>"Efekt" submenu for the shared notification-area icon.</summary>
        public WinForms.ToolStripMenuItem BuildEffectMenu()
        {
            var fx = new WinForms.ToolStripMenuItem("RGB efekti");
            foreach (var e in FxEffect.All)
            {
                var item = new WinForms.ToolStripMenuItem(e.DisplayName) { Tag = e.Id };
                item.Click += (s, a) => Dispatcher.Invoke(() => SetEffect((string)((WinForms.ToolStripMenuItem)s).Tag));
                fx.DropDownItems.Add(item);
            }
            fx.DropDownOpening += (s, e) => { foreach (WinForms.ToolStripMenuItem i in fx.DropDownItems) i.Checked = (string)i.Tag == _settings.Effect; };
            return fx;
        }

        /// <summary>Stops the timers when the app exits.</summary>
        public void Shutdown()
        {
            _refresh.Stop();
            _animation.Stop();
            _plugDebounce?.Stop();
        }

        Window OwnerWindow => Window.GetWindow(this);

        // ---------- scanning ----------

        public void Rescan()
        {
            if (_scanning) return;
            _scanning = true;
            RescanButton.IsEnabled = false;
            RescanText.Text = "Taranıyor…";
            new Thread(() =>
            {
                try { _engine.Scan(); }
                catch (Exception ex)
                {
                    Log.Error("Tarama", ex);
                    Dispatcher.BeginInvoke((Action)(() => { _scanning = false; RescanButton.IsEnabled = true; RescanText.Text = "Cihazları yeniden tara"; }));
                }
            }) { IsBackground = true }.Start();
        }

        // ---------- sidebar ----------

        void BuildNav()
        {
            string selectedId = (Nav.SelectedItem as ListBoxItem)?.Tag is NavEntry n ? (n.Device?.Id ?? n.Page) : "effects";
            Nav.Items.Clear();
            AddNav(new NavEntry { Page = "effects" }, "Efektler", "", Color.FromRgb(0xFF, 0x2D, 0x55), null);
            AddNav(new NavEntry { Page = "settings" }, "Ayarlar", "", Color.FromRgb(0x8E, 0x8E, 0x93), null);

            Nav.Items.Add(new ListBoxItem
            {
                IsEnabled = false, Focusable = false, Style = (Style)FindResource("SidebarItem"),
                Content = new TextBlock { Text = "Cihazlar", Style = (Style)FindResource("Secondary"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 14, 0, 0) },
                Tag = new NavEntry { Page = "header" },
            });
            if (_engine.Devices.Count == 0)
                Nav.Items.Add(new ListBoxItem
                {
                    IsEnabled = false, Style = (Style)FindResource("SidebarItem"),
                    Content = new TextBlock
                    {
                        Text = _scanning ? "Taranıyor…"
                            : _engine.UnknownDevices.Count > 0 ? $"Desteklenen cihaz bulunamadı. {_engine.UnknownDevices.Count} tanınmayan cihaz Ayarlar'da listeleniyor."
                            : "Desteklenen cihaz bulunamadı.",
                        TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("Secondary"),
                    },
                });
            foreach (var d in _engine.Devices)
            {
                var icon = KindIcons.TryGetValue(d.Kind, out var ic) ? ic : KindIcons[DeviceKind.Other];
                AddNav(new NavEntry { Page = "device", Device = d }, d.Name, icon.glyph, icon.color, d.Status);
            }

            foreach (ListBoxItem it in Nav.Items)
                if (it.Tag is NavEntry e && (e.Device?.Id ?? e.Page) == selectedId) { Nav.SelectedItem = it; break; }
            if (Nav.SelectedItem == null) Nav.SelectedIndex = 0;
            ShowSelected();
        }

        void AddNav(NavEntry entry, string text, string glyph, Color color, string status)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(IconSquare(glyph, color, 26, 13));
            var texts = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock { Text = text });
            if (status != null)
            {
                entry.Status = new TextBlock { Text = status, Style = (Style)FindResource("Secondary"), FontSize = 11 };
                texts.Children.Add(entry.Status);
            }
            Grid.SetColumn(texts, 1);
            g.Children.Add(texts);
            Nav.Items.Add(new ListBoxItem { Content = g, Tag = entry, Style = (Style)FindResource("SidebarItem") });
        }

        static Border IconSquare(string glyph, Color color, double size, double fontSize) => new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.26),
            Background = new LinearGradientBrush(Lighten(color, 0.18), color, 90),
            Child = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = fontSize, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            VerticalAlignment = VerticalAlignment.Center,
        };

        static Color Lighten(Color c, double k) =>
            Color.FromRgb((byte)(c.R + (255 - c.R) * k), (byte)(c.G + (255 - c.G) * k), (byte)(c.B + (255 - c.B) * k));

        void ShowSelected()
        {
            if (!((Nav.SelectedItem as ListBoxItem)?.Tag is NavEntry e) || e.Page == "header") return;
            _previews.Clear();
            _pageRefreshers.Clear();
            switch (e.Page)
            {
                case "effects": Page.Content = EffectsPage(); break;
                case "settings": Page.Content = SettingsPage(); break;
                case "device": Page.Content = DevicePage(e.Device); break;
            }
            ContentScroll.ScrollToTop();
        }

        // ---------- shared page pieces ----------

        T Res<T>(string key) => (T)FindResource(key);

        TextBlock PageTitle(string text) => new TextBlock { Text = text, Style = Res<Style>("LargeTitle") };
        TextBlock Section(string text) => new TextBlock { Text = text, Style = Res<Style>("SectionHeader") };
        TextBlock Footnote(string text, Brush color = null)
        {
            var t = new TextBlock { Text = text, Style = Res<Style>("Footnote") };
            if (color != null) t.Foreground = color;
            return t;
        }

        /// <summary>A rounded card holding rows separated by hairlines.</summary>
        Border Card(params FrameworkElement[] rows)
        {
            var stack = new StackPanel();
            for (int i = 0; i < rows.Length; i++)
            {
                if (i > 0) stack.Children.Add(new Border { Style = Res<Style>("RowSeparator") });
                stack.Children.Add(rows[i]);
            }
            return new Border { Style = Res<Style>("Card"), Child = stack };
        }

        /// <summary>Row: label (and optional detail) on the left, a control on the right.</summary>
        Grid Row(string label, FrameworkElement right, string detail = null)
        {
            var g = new Grid { Style = Res<Style>("Row") };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 10, 12, 10) };
            left.Children.Add(new TextBlock { Text = label });
            if (detail != null) left.Children.Add(new TextBlock { Text = detail, Style = Res<Style>("Secondary"), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Thickness(0, 2, 0, 0) });
            g.Children.Add(left);
            if (right != null) { Grid.SetColumn(right, 1); right.VerticalAlignment = VerticalAlignment.Center; g.Children.Add(right); }
            return g;
        }

        /// <summary>Row with a full-width slider and a live value on the right.</summary>
        Grid SliderRow(string label, double min, double max, double value, Func<double, string> format, Action<double> changed)
        {
            var g = new Grid { Style = Res<Style>("Row") };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            g.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            var slider = new Slider { Minimum = min, Maximum = max, Value = value, Margin = new Thickness(0, 12, 0, 12) };
            var val = new TextBlock { Text = format(value), Style = Res<Style>("Secondary"), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            slider.ValueChanged += (s, e) => { val.Text = format(e.NewValue); changed(e.NewValue); };
            Grid.SetColumn(slider, 1); Grid.SetColumn(val, 2);
            g.Children.Add(slider); g.Children.Add(val);
            return g;
        }

        CheckBox Switch(bool on, Action<CheckBox> changed)
        {
            var c = new CheckBox { Style = Res<Style>("Switch"), IsChecked = on };
            c.Click += (s, e) => changed(c);
            return c;
        }

        static SolidColorBrush BrushOf(string hex)
        {
            var c = Rgb.Parse(hex);
            return new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
        }

        /// <summary>Round swatch; click opens a palette of preset colors plus "custom".</summary>
        Button ColorSwatch(Func<string> get, Action<string> set)
        {
            var b = new Button { Style = Res<Style>("Swatch"), Background = BrushOf(get()) };
            b.Click += (s, e) =>
            {
                var popup = new Popup { PlacementTarget = b, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, HorizontalOffset = -150 };
                var wrap = new WrapPanel { Width = 6 * 34 };
                foreach (var hex in Presets)
                {
                    var p = new Button { Style = Res<Style>("Swatch"), Background = BrushOf(hex), Margin = new Thickness(3), ToolTip = hex };
                    p.Click += (s2, e2) => { set(hex); b.Background = BrushOf(hex); popup.IsOpen = false; };
                    wrap.Children.Add(p);
                }
                var custom = new Button { Content = "Özel renk…", Style = Res<Style>("GrayButton"), Margin = new Thickness(3, 8, 3, 0), Padding = new Thickness(10, 5, 10, 5), HorizontalAlignment = HorizontalAlignment.Stretch };
                custom.Click += (s2, e2) =>
                {
                    popup.IsOpen = false;
                    var cur = Rgb.Parse(get());
                    using (var dlg = new WinForms.ColorDialog { FullOpen = true, Color = System.Drawing.Color.FromArgb(cur.R, cur.G, cur.B) })
                        if (dlg.ShowDialog() == WinForms.DialogResult.OK)
                        {
                            string hex = new Rgb(dlg.Color.R, dlg.Color.G, dlg.Color.B).ToString();
                            set(hex); b.Background = BrushOf(hex);
                        }
                };
                var panel = new StackPanel();
                panel.Children.Add(wrap);
                panel.Children.Add(custom);
                popup.Child = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C)), CornerRadius = new CornerRadius(12), Padding = new Thickness(10), Margin = new Thickness(8),
                    Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.5 },
                    Child = panel,
                };
                popup.IsOpen = true;
            };
            return b;
        }

        Border ConflictBanner(IEnumerable<string> processes)
        {
            var list = processes.ToList();
            if (list.Count == 0) return null;
            var g = new Grid { Margin = new Thickness(14, 12, 14, 12) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(new TextBlock { Text = "", Style = Res<Style>("Icon"), Foreground = Res<Brush>("OrangeBrush"), FontSize = 16, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 10, 0) });
            var t = new TextBlock { TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None };
            t.Inlines.Add(new System.Windows.Documents.Run("Üretici yazılımları da çalışıyor. ") { FontWeight = FontWeights.SemiBold });
            t.Inlines.Add(new System.Windows.Documents.Run(string.Join(", ", list) + ". Program yine de kontrol eder; ışıklarda titreme olursa bu yazılımların aydınlatma özelliğini kapat.") { Foreground = Res<Brush>("SecondaryTextBrush") });
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
            return new Border { CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0x9F, 0x0A)), Child = g, Margin = new Thickness(0, 18, 0, 0) };
        }

        List<string> RunningConflicts(IEnumerable<RgbDevice> devices) =>
            devices.SelectMany(d => d.ConflictingProcesses).Distinct()
                .Where(p => { try { return Process.GetProcessesByName(p).Length > 0; } catch { return false; } }).ToList();

        // ---------- live effect previews ----------

        Image PreviewImage(Func<FxEffect> effect, int width, double height)
        {
            var bmp = new WriteableBitmap(width, 1, 96, 96, PixelFormats.Bgr32, null);
            _previews.Add((bmp, effect));
            var img = new Image { Source = bmp, Stretch = Stretch.Fill, Height = height };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.Linear);
            return img;
        }

        void AnimatePreviews()
        {
            if (!IsVisible || _previews.Count == 0) return;
            double t = _clock.Elapsed.TotalSeconds;
            var s = _settings.ToEffectSettings();
            foreach (var (bmp, effect) in _previews)
            {
                var fx = effect();
                int w = bmp.PixelWidth;
                var px = new int[w];
                for (int i = 0; i < w; i++)
                {
                    var c = fx.ColorAtLed(i * 48 / w, w == 1 ? 0 : (double)i / (w - 1), t, s).Scale(s.Brightness);
                    px[i] = (c.R << 16) | (c.G << 8) | c.B;
                }
                bmp.WritePixels(new Int32Rect(0, 0, w, 1), px, w * 4, 0);
            }
        }

        // ---------- pages ----------

        public void SetEffect(string id)
        {
            _settings.Effect = id;
            _settings.Save();
            if ((Nav.SelectedItem as ListBoxItem)?.Tag is NavEntry e && e.Page == "effects") ShowSelected();
        }

        FrameworkElement EffectsPage()
        {
            var page = new StackPanel();
            page.Children.Add(PageTitle("Efektler"));
            page.Children.Add(new TextBlock { Text = "Seçtiğin efekt tüm cihazlarda aynı anda ve senkron oynar.", Style = Res<Style>("Secondary"), FontSize = 13, Margin = new Thickness(0, 4, 0, 0) });

            var banner = ConflictBanner(RunningConflicts(_engine.Devices));
            if (banner != null) page.Children.Add(banner);

            // big live preview
            var preview = new Border { Style = Res<Style>("Card"), Margin = new Thickness(0, 22, 0, 0), Height = 64, ClipToBounds = true };
            preview.SizeChanged += (s, e) => { double h = Math.Max(56, Math.Min(130, e.NewSize.Width * 0.09)); if (Math.Abs(preview.Height - h) > 1) preview.Height = h; };
            var clip = new Border { CornerRadius = new CornerRadius(12), Background = Brushes.Black };
            var img = PreviewImage(() => FxEffect.ById(_settings.Effect), 240, double.NaN);
            clip.Child = img;
            preview.Child = clip;
            preview.Loaded += (s, e) => clip.Clip = new RectangleGeometry(new Rect(0, 0, preview.ActualWidth, preview.ActualHeight), 12, 12);
            preview.SizeChanged += (s, e) => clip.Clip = new RectangleGeometry(new Rect(0, 0, preview.ActualWidth, preview.ActualHeight), 12, 12);
            page.Children.Add(preview);

            page.Children.Add(Section("Efekt"));
            // Tiles share the row evenly and add/remove columns as the window grows or shrinks.
            var tiles = new UniformGrid { Columns = 4, Margin = new Thickness(0, 0, -12, 0) };
            tiles.SizeChanged += (s, e) =>
            {
                int cols = Math.Max(2, Math.Min(7, (int)((e.NewSize.Width + 12) / 172)));
                if (tiles.Columns != cols) tiles.Columns = cols;
            };
            foreach (var fx in FxEffect.All)
            {
                var stack = new StackPanel();
                var bar = new Border { CornerRadius = new CornerRadius(7), Height = 30, Background = Brushes.Black };
                var fxLocal = fx;
                var tileImg = PreviewImage(() => fxLocal, 48, 30);
                bar.Child = tileImg;
                bar.SizeChanged += (s, e) => tileImg.Clip = new RectangleGeometry(new Rect(0, 0, bar.ActualWidth, bar.ActualHeight), 7, 7);
                stack.Children.Add(bar);
                stack.Children.Add(new TextBlock { Text = fx.DisplayName, Margin = new Thickness(2, 8, 0, 0) });
                var rb = new RadioButton { Style = Res<Style>("EffectTile"), Content = stack, GroupName = "fx", IsChecked = fx.Id == _settings.Effect };
                rb.Checked += (s, e) => { _settings.Effect = fxLocal.Id; _settings.Save(); };
                tiles.Children.Add(rb);
            }
            page.Children.Add(tiles);

            page.Children.Add(Section("Görünüm"));
            page.Children.Add(Card(
                Row("Ana renk", ColorSwatch(() => _settings.Color1, v => { _settings.Color1 = v; _settings.Save(); }), "Sabit, nefes, dalga ve kuyruklu yıldız efektleri"),
                Row("İkinci renk", ColorSwatch(() => _settings.Color2, v => { _settings.Color2 = v; _settings.Save(); }), "İki renk dalgası ve kuyruklu yıldız arka planı"),
                SliderRow("Hız", 0.1, 5, _settings.Speed, v => v.ToString("0.0") + "×", v => { _settings.Speed = Math.Round(v, 1); _settings.Save(); }),
                SliderRow("Parlaklık", 0, 100, _settings.Brightness, v => ((int)v) + "%", v => { _settings.Brightness = (int)v; _settings.Save(); })));
            return page;
        }

        FrameworkElement DevicePage(RgbDevice d)
        {
            var page = new StackPanel();
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var icon = KindIcons.TryGetValue(d.Kind, out var ic) ? ic : KindIcons[DeviceKind.Other];
            head.Children.Add(IconSquare(icon.glyph, icon.color, 56, 26));
            var titles = new StackPanel { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            titles.Children.Add(PageTitle(d.Name));
            var sub = new TextBlock { Style = Res<Style>("Secondary"), FontSize = 13, Margin = new Thickness(0, 2, 0, 0) };
            titles.Children.Add(sub);
            Grid.SetColumn(titles, 1);
            head.Children.Add(titles);
            page.Children.Add(head);

            var banner = ConflictBanner(RunningConflicts(new[] { d }));
            if (banner != null) page.Children.Add(banner);

            var status = new TextBlock { Style = Res<Style>("Secondary"), FontSize = 13, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, MaxWidth = 380, TextAlignment = TextAlignment.Right };
            void refresh()
            {
                status.Text = d.Status;
                sub.Text = $"{d.Vendor} · {d.TotalLeds} LED";
            }
            refresh();
            _pageRefreshers.Add(refresh);

            bool unavailable = d is UnavailableDevice;
            page.Children.Add(Section("Kontrol"));
            var sw = Switch(_settings.For(d.Id).Enabled, c => { _settings.For(d.Id).Enabled = c.IsChecked == true; _settings.Save(); });
            sw.IsEnabled = !unavailable;
            page.Children.Add(Card(
                Row("Bu cihazı program yönetsin", sw, "Kapatırsan program bu cihaza hiçbir şey göndermez."),
                Row("Durum", status)));

            if (d.Zones.Count > 0)
            {
                page.Children.Add(Section("Bölgeler"));
                var rows = new List<FrameworkElement>();
                foreach (var z in d.Zones)
                {
                    FrameworkElement right;
                    if (z.Resizable)
                    {
                        var box = new TextBox { Text = z.LedCount.ToString(), Width = 64 };
                        var zone = z;
                        void commit()
                        {
                            if (!int.TryParse(box.Text, out int n)) n = zone.LedCount;
                            n = Math.Max(0, Math.Min(zone.MaxLeds, n));
                            _settings.For(d.Id).ZoneLeds[zone.Name] = n;
                            _settings.Save();
                            _engine.ApplyZoneSettings();
                            box.Text = zone.LedCount.ToString();
                            refresh();
                        }
                        box.LostFocus += (s, e) => commit();
                        box.KeyDown += (s, e) => { if (e.Key == Key.Enter) commit(); };
                        box.PreviewTextInput += (s, e) => e.Handled = !e.Text.All(char.IsDigit);
                        var seg = new TextBox { Text = zone.SegmentSize.ToString(), Width = 48, Margin = new Thickness(16, 0, 0, 0), ToolTip = "Zincirdeki her parçanın (fan, pompa) LED sayısı. Her parça efekti kendi halkasında oynatır. 0 = tek şerit." };
                        void commitSeg()
                        {
                            if (!int.TryParse(seg.Text, out int n)) n = zone.SegmentSize;
                            _settings.For(d.Id).ZoneSegments[zone.Name] = Math.Max(0, Math.Min(zone.MaxLeds, n));
                            _settings.Save();
                            _engine.ApplyZoneSettings();
                            seg.Text = zone.SegmentSize.ToString();
                        }
                        seg.LostFocus += (s, e) => commitSeg();
                        seg.KeyDown += (s, e) => { if (e.Key == Key.Enter) commitSeg(); };
                        seg.PreviewTextInput += (s, e) => e.Handled = !e.Text.All(char.IsDigit);
                        var p = new StackPanel { Orientation = Orientation.Horizontal };
                        p.Children.Add(box);
                        p.Children.Add(new TextBlock { Text = "LED", Style = Res<Style>("Secondary"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
                        p.Children.Add(seg);
                        p.Children.Add(new TextBlock { Text = "LED / parça", Style = Res<Style>("Secondary"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
                        right = p;
                    }
                    else right = new TextBlock { Text = z.LedCount + " LED", Style = Res<Style>("Secondary"), FontSize = 13 };
                    rows.Add(Row(z.Name, right, z.Detected != null ? "Bağlı: " + z.Detected : z.Resizable ? $"En fazla {z.MaxLeds} LED" : null));
                }
                page.Children.Add(Card(rows.ToArray()));
                if (d.Zones.Any(z => z.Resizable))
                    page.Children.Add(Footnote("Başlığa fan veya şerit bağlıysa toplam LED sayısını yaz. Örnek: 12 LED'li 3 fan ve 12 LED'li bir pompa = 48. Boş başlıklar için 0 bırak. \"LED / parça\" kutusuna 12 yazarsan her fan ve pompa efekti kendi halkasında aynı şekilde oynatır."));
            }
            return page;
        }

        FrameworkElement SettingsPage()
        {
            var page = new StackPanel();
            page.Children.Add(PageTitle("Ayarlar"));

            page.Children.Add(Section("Genel"));
            page.Children.Add(Card(
                Row("Üretici aydınlatma servislerini devral",
                    Switch(_settings.VendorTakeover, c => { _settings.VendorTakeover = c.IsChecked == true; _settings.Save(); Rescan(); }),
                    "MSI Mystic Light servisleri program açıkken bir kez durdurulur, kapanınca geri başlatılır. Fan ve diğer özellikler etkilenmez."),
                Row("Kapanış rengi", ColorSwatch(() => _settings.ShutdownColor, v => { _settings.ShutdownColor = v; _settings.Save(); }), "Program kapanırken cihazlara verilen renk"),
                SliderRow("Akıcılık", 10, 60, _settings.Fps, v => ((int)v) + " FPS", v => { _settings.Fps = (int)v; _settings.Save(); })));
            page.Children.Add(Footnote("Windows ile başlatma GPU sekmesindeki Profillerim kartında. Açınca program oturum açılışında tepside başlar, RGB efekti ve GPU profili birlikte gelir."));

            page.Children.Add(Section("Bellek (RAM) ışıkları"));
            var ram = Switch(_settings.RamControl, c =>
            {
                if (c.IsChecked == true && MessageBox.Show(OwnerWindow,
                        "RAM ışıkları, bellek voltaj yongalarıyla aynı SMBus hattından kontrol edilir. Aynı anda başka bir program (SignalRGB, iCUE, HWiNFO vb.) bu hatta yazarsa bilgisayar kapanıp yeniden başlayabilir.\n\nBu programlar açıkken RGB tarafı RAM'e hiç yazmaz. Yine de açmak istiyor musun?",
                        "tweakandrgbstone", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    c.IsChecked = false;
                    return;
                }
                _settings.RamControl = c.IsChecked == true;
                _settings.Save();
                Rescan();
            });
            page.Children.Add(Card(Row("RAM ışıklarını kontrol et", ram, "Deneysel")));
            var blockers = Engine.SmbusUsersRunning();
            page.Children.Add(Footnote(blockers.Length > 0
                ? "Şu an açık olduğu için RAM'e yazılmıyor: " + string.Join(", ", blockers) + "."
                : "SMBus'ı kullanan başka program açık değil.",
                blockers.Length > 0 ? Res<Brush>("OrangeBrush") : null));

            page.Children.Add(Section("Web panosu"));
            var webInfo = new StackPanel();
            void fillWebInfo()
            {
                webInfo.Children.Clear();
                if (!_dashboard.Running) return;
                var addrs = Web.WebDashboard.LocalAddresses();
                var newPin = new Button { Content = "Yeni PIN", Style = Res<Style>("GrayButton"), Padding = new Thickness(12, 5, 12, 5) };
                newPin.Click += (s, e) => { _settings.WebPin = Web.WebDashboard.NewPin(); _settings.Save(); ShowSelected(); };
                webInfo.Children.Add(new Border { Style = Res<Style>("RowSeparator") });
                webInfo.Children.Add(Row("Adres", new TextBlock { Text = addrs.FirstOrDefault() ?? "ağ bulunamadı", FontSize = 13, Foreground = Res<Brush>("AccentBrush") },
                    addrs.Count > 1 ? "Diğer: " + string.Join(", ", addrs.Skip(1)) : "Telefonun aynı Wi-Fi ağındayken bu adresi aç."));
                webInfo.Children.Add(new Border { Style = Res<Style>("RowSeparator") });
                webInfo.Children.Add(Row("PIN: " + _settings.WebPin, newPin, "Yeni PIN alınca açık oturumlar kapanmaz; uygulama yeniden başlayınca kapanır."));
            }
            var web = Switch(_dashboard.Running, c =>
            {
                bool on = c.IsChecked == true;
                bool ok = on ? _dashboard.Start() : true;
                if (!on) _dashboard.Stop();
                _settings.WebEnabled = on && ok;
                _settings.Save();
                c.IsChecked = _dashboard.Running;
                if (on && !ok) MessageBox.Show(OwnerWindow, "Web panosu başlatılamadı. Ayrıntı rgbstone.log dosyasında.", "tweakandrgbstone");
                fillWebInfo();
            });
            fillWebInfo();
            var webCard = Card(Row("Web panosunu etkinleştir", web, "Telefondan veya başka bir bilgisayardan, yalnızca ev ağından ve PIN ile."));
            ((StackPanel)webCard.Child).Children.Add(webInfo);
            page.Children.Add(webCard);
            page.Children.Add(Footnote("Kapalıyken hiçbir port açılmaz. Açıkken yalnızca özel (ev) ağ profiline izin veren bir güvenlik duvarı kuralı eklenir; kapatınca silinir."));

            page.Children.Add(Section("Tanınmayan cihazlar"));
            var unknown = _engine.UnknownDevices.ToList();
            if (unknown.Count == 0)
                page.Children.Add(Card(Row("Tüm RGB markalı cihazlar tanındı", null)));
            else
            {
                var rows = unknown.Select(u =>
                {
                    var copy = new Button { Content = "Kimliği kopyala", Style = Res<Style>("GrayButton"), Padding = new Thickness(12, 5, 12, 5) };
                    copy.Click += (s, e) => { try { Clipboard.SetText($"{u.Brand} {u.Product} {u.Id}"); copy.Content = "Kopyalandı"; } catch { } };
                    return (FrameworkElement)Row($"{u.Brand} · {u.Product}", copy, "Kimlik " + u.Id);
                }).ToArray();
                page.Children.Add(Card(rows));
                page.Children.Add(Footnote("Bu cihazlar RGB üreten markalardan ama henüz sürücüsü yok (bazılarının ışığı da olmayabilir). Kimliği gönderirsen sürücü eklenebilir; program o zamana kadar bu cihazlara dokunmaz."));
            }

            page.Children.Add(Section("Hakkında"));
            var openFolder = new Button { Content = "Klasörü aç", Style = Res<Style>("GrayButton"), Padding = new Thickness(12, 5, 12, 5) };
            openFolder.Click += (s, e) => { try { Process.Start("explorer.exe", AppSettings.Folder); } catch { } };
            page.Children.Add(Card(
                Row("Sürüm", new TextBlock { Text = Assembly.GetExecutingAssembly().GetName().Version.ToString(3), Style = Res<Style>("Secondary"), FontSize = 13 }),
                Row("İnternet bağlantısı", new TextBlock { Text = "Hiç yok", Style = Res<Style>("Secondary"), FontSize = 13 }, "Telemetri, güncelleme veya uzaktan kod indirme yok."),
                Row("Sürücüler", new TextBlock { Text = _engine.Providers.Count.ToString(), Style = Res<Style>("Secondary"), FontSize = 13 }, string.Join(", ", _engine.Providers.Select(p => p.Name))),
                Row("Ayarlar ve kayıtlar", openFolder, AppSettings.Folder)));
            return page;
        }

        // ---------- periodic refresh ----------

        void RefreshStatus()
        {
            foreach (ListBoxItem it in Nav.Items)
                if (it.Tag is NavEntry e && e.Device != null && e.Status != null && e.Status.Text != e.Device.Status)
                    e.Status.Text = e.Device.Status;
            foreach (var r in _pageRefreshers) r();
        }
    }
}
