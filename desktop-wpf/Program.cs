using System;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace MentoHUST.Desktop
{
    public static class Program
    {
        internal static Stream Resource(string name)
        {
            var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MentoHUST.Desktop." + name);
            if (stream == null) throw new InvalidOperationException("Missing resource: " + name);
            return stream;
        }

        [STAThread]
        public static int Main(string[] args)
        {
            try {
                var app = new Application();
                using (var stream = Resource("Theme.xaml"))
                    app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(stream));
                Window window;
                using (var stream = Resource("MainWindow.xaml")) window = (Window)XamlReader.Load(stream);
                bool animationChecks = args.Length == 2 && args[0] == "--ui-animation-checks";
                bool memoryChecks = args.Length == 2 && (args[0] == "--ui-checks" || animationChecks);
                bool configurationChecks = args.Length == 2 && args[0] == "--ui-config-checks";
                bool engineChecks = args.Length == 2 && args[0] == "--ui-engine-checks";
                LegacyConfigurationStore store = null;
                if (configurationChecks || engineChecks) {
                    Directory.CreateDirectory(args[1]);
                    store = new LegacyConfigurationStore(Path.Combine(args[1], "session-" + Guid.NewGuid().ToString("N"), "Config.ini"));
                    store.Load(); store.Save(SessionDraft.CreatePreview());
                } else if (!memoryChecks && !(args.Length == 1 && args[0] == "--preview"))
                    store = new LegacyConfigurationStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MentoHUST.Wpf", "Config.ini"));
                IStartupRegistration startup = configurationChecks || engineChecks ? (IStartupRegistration)new MemoryStartupRegistration() :
                    store == null ? null : new WindowsStartupRegistration(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName);
                var controller = new DesktopController(window, store, store != null && !configurationChecks, startup);
                if (args.Length == 1 && args[0] == "--startup") controller.LogStartup();
                if (animationChecks) window.Loaded += async delegate { await controller.RunAnimationChecksAsync(args[1]); };
                else if (memoryChecks || configurationChecks)
                    window.Loaded += async delegate { await controller.RunChecksAsync(args[1]); };
                if (engineChecks) window.Loaded += async delegate { await controller.RunEngineChecksAsync(args[1]); };
                app.Run(window);
                return controller.ExitCode;
            } catch (Exception error) {
                if (args.Length == 2 && (args[0] == "--ui-checks" || args[0] == "--ui-config-checks" || args[0] == "--ui-engine-checks" || args[0] == "--ui-animation-checks")) {
                    Directory.CreateDirectory(args[1]); File.WriteAllText(Path.Combine(args[1], "checks.txt"), error.ToString());
                }
                else if (args.Length == 1 && args[0] == "--preview") Console.Error.WriteLine(error);
                else MessageBox.Show(error.Message, "无法打开界面预览", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }
    }

    public sealed class LogEntry
    {
        public string Time { get; private set; }
        public string Message { get; private set; }
        public Brush Marker { get; private set; }
        public string FullText { get { return Time + "   " + Message; } }
        public LogEntry(string message, AuthenticationState? state)
        {
            Time = DateTime.Now.ToString("HH:mm:ss"); Message = message;
            string color = state == AuthenticationState.Connected ? "#18A78D" : state == AuthenticationState.Failed ? "#EC4963" :
                state.HasValue && state != AuthenticationState.Disconnected ? "#398DD8" : "#9BAABC";
            Marker = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); Marker.Freeze();
        }
    }

    public sealed class DesktopController
    {
        private readonly Window window;
        private IAuthenticationClient authentication = new UnavailableAuthenticationClient();
        private ProcessEngineClient nativeClient;
        private readonly bool enableEngine;
        private bool authenticationRunning, authenticationBusy, closing, closeConfirmed;
        private bool handledSuccess;
        private int automaticMinimizeVersion;
        private readonly HotspotAfterAuthentication hotspot = new HotspotAfterAuthentication(new WindowsHotspotActions());
        private AuthenticationState currentState = AuthenticationState.Disconnected;
        private SessionDraft committed = SessionDraft.CreatePreview();
        private SessionDraft draft;
        private LegacyConfigurationStore configuration;
        private readonly IStartupRegistration startup;
        private bool startupAvailable;
        private string startupReadError;
        private bool settingsVisible, transitioning, syncing;
        private readonly BitmapCache homePageCache = new BitmapCache { EnableClearType = true, SnapsToDevicePixels = true };
        private readonly BitmapCache settingsPageCache = new BitmapCache { EnableClearType = true, SnapsToDevicePixels = true };
        private Forms.NotifyIcon tray;
        private System.Drawing.Icon trayIcon;
        public int ExitCode { get; private set; }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
        [DllImport("user32.dll", EntryPoint = "PostMessageW")]
        private static extern bool PostMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);

        private T Get<T>(string name) where T : FrameworkElement { return (T)window.FindName(name); }
        private void Click(string name, RoutedEventHandler action) { Get<Button>(name).Click += action; }
        private void Log(string text, AuthenticationState? state = null) {
            var list = Get<ListBox>("LogList"); list.Items.Insert(0, new LogEntry(text, state));
            while (list.Items.Count > 200) list.Items.RemoveAt(list.Items.Count - 1);
        }

        public DesktopController(Window window, LegacyConfigurationStore configuration, bool useEngine = false, IStartupRegistration startup = null)
        {
            this.window = window;
            this.configuration = configuration;
            this.startup = startup;
            enableEngine = useEngine;
            if (configuration != null) committed = configuration.Load();
            window.Title = configuration == null ? "JNU Campus Network · 界面预览" : "JNU Campus Network";
            using (var stream = Program.Resource("Emblem.png")) {
                var sheet = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var seal = new CroppedBitmap(sheet, new Int32Rect(0, 0, 500, 493)); seal.Freeze();
                Get<Image>("BrandEmblem").Source = seal;
                var emblem = Get<Image>("BrandEmblem");
                emblem.Clip = new EllipseGeometry(new Rect(0, 0, emblem.Width, emblem.Height));
            }
            using (var stream = Program.Resource("AppIcon.ico")) {
                var icon = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                icon.Freeze(); window.Icon = icon;
            }
            using (var stream = Program.Resource("HeaderHD.png")) {
                var reference = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                reference.Freeze(); Get<Image>("ReferenceHeader").Source = reference;
                // Preserve the generated banner's aspect ratio; extend only its blank edge behind the official emblem.
                double height = Get<Image>("ReferenceHeader").Width * reference.PixelHeight / reference.PixelWidth;
                Get<Canvas>("HeaderCanvas").Height = height;
                Get<Image>("ReferenceHeader").Height = Get<Image>("ReferenceHeaderBackdrop").Height = height;
                Canvas.SetTop(Get<Image>("BrandEmblem"), (height - Get<Image>("BrandEmblem").Height) / 2);
                var backdrop = new CroppedBitmap(reference, new Int32Rect(0, 0, 1, reference.PixelHeight));
                backdrop.Freeze(); Get<Image>("ReferenceHeaderBackdrop").Source = backdrop;
            }
            window.SourceInitialized += delegate {
                int caption = 0xFFF5EB, ink = 0x3E2414;
                IntPtr handle = new WindowInteropHelper(window).Handle;
                DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
                DwmSetWindowAttribute(handle, 36, ref ink, sizeof(int));
            };
            BindHome();
            BindAdapters();
            UpdateConfigurationMode();
            Get<Button>("Authenticate").IsEnabled = authentication.IsAvailable;
            Click("Authenticate", async delegate { await ToggleAuthenticationAsync(); });
            Get<ComboBox>("AccountChoice").SelectionChanged += delegate { UpdateAuthenticationControls(); };
            Get<ComboBox>("AdapterChoice").SelectionChanged += delegate { UpdateAuthenticationControls(); };
            if (useEngine) window.Loaded += async delegate { await InitializeEngineAsync(); };
            Click("OpenSettings", delegate { OpenSettings(); });
            Click("Back", delegate { CancelSettings(); });
            Click("Cancel", delegate { CancelSettings(); });
            Click("Apply", delegate { ApplySettings(); });
            Click("AddAccount", delegate { AddAccount(); });
            Click("DeleteAccount", delegate { DeleteAccount(); });
            Click("ClearLog", delegate { Get<ListBox>("LogList").Items.Clear(); });
            Click("RevealPassword", delegate { RevealPassword(); });
            Click("ToTray", delegate { MinimizeToTray(); });
            Click("ImportConfiguration", delegate { ImportConfiguration(); });
            Click("BrowsePackage", delegate { BrowsePackage(); });
            Click("HotspotHelp", delegate { var tip = (ToolTip)Get<Button>("HotspotHelp").ToolTip; tip.PlacementTarget = Get<Button>("HotspotHelp"); tip.IsOpen = !tip.IsOpen; });
            Get<RadioButton>("AccountTab").Checked += delegate { ShowTab(false); };
            Get<RadioButton>("ParameterTab").Checked += delegate { ShowTab(true); };
            Get<Grid>("SettingsTabs").SizeChanged += delegate { UpdateTabSelection(false); };
            Get<Grid>("SettingsTabs").PreviewKeyDown += delegate(object sender, KeyEventArgs e) {
                if (e.Key != Key.Left && e.Key != Key.Right) return;
                if (!Get<RadioButton>("AccountTab").IsKeyboardFocused && !Get<RadioButton>("ParameterTab").IsKeyboardFocused) return;
                var tab = Get<RadioButton>(e.Key == Key.Right ? "ParameterTab" : "AccountTab");
                tab.IsChecked = true; tab.Focus(); e.Handled = true;
            };
            Get<ListBox>("AccountList").SelectionChanged += delegate { PopulateAccount(); };
            RoutedEventHandler synchronize = delegate { SynchronizeAutoRun(); };
            Get<CheckBox>("AccountAutoRun").Checked += synchronize;
            Get<CheckBox>("AccountAutoRun").Unchecked += synchronize;
            Get<CheckBox>("ParameterAutoRun").Checked += delegate { SynchronizeAutoRun(true); };
            Get<CheckBox>("ParameterAutoRun").Unchecked += delegate { SynchronizeAutoRun(true); };
            window.PreviewKeyDown += delegate(object sender, KeyEventArgs e) {
                if (e.Key == Key.Escape && settingsVisible && !transitioning) {
                    string[] combos = { "AccountChoice", "AdapterChoice", "Multicast", "Dhcp" };
                    if (combos.Any(name => Get<ComboBox>(name).IsDropDownOpen)) return;
                    CancelSettings(); e.Handled = true;
                }
            };
            window.Closing += async delegate(object sender, System.ComponentModel.CancelEventArgs e) {
                automaticMinimizeVersion++;
                hotspot.Cancel();
                if (nativeClient == null || closeConfirmed) return;
                e.Cancel = true; if (closing) return; closing = true;
                authenticationBusy = true; UpdateAuthenticationControls();
                try { await hotspot.CancelAndWaitAsync(); await nativeClient.ShutdownAsync(); }
                catch (Exception error) { Log("后台退出：" + error.Message); nativeClient.Dispose(); }
                closeConfirmed = true; window.Close();
            };
            window.Closed += delegate {
                closing = true;
                foreach (string name in new[] { "HomePage", "SettingsPage" }) {
                    ((TranslateTransform)Get<Grid>(name).RenderTransform).BeginAnimation(TranslateTransform.XProperty, null);
                    Get<Grid>(name).CacheMode = null;
                }
                if (nativeClient != null) nativeClient.Dispose();
                if (tray != null) { tray.Visible = false; tray.Dispose(); }
                if (trayIcon != null) trayIcon.Dispose();
            };
            Log(configuration == null ? "界面预览已就绪" : "配置已载入");
            if (!useEngine) Log("预览模式，不会启动网络认证");
        }

        private async Task InitializeEngineAsync()
        {
            if (nativeClient != null || configuration == null) return;
            nativeClient = new ProcessEngineClient(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MentoHUST.Engine.exe"), configuration.FilePath);
            authentication = nativeClient;
            nativeClient.StateChanged += delegate(object sender, AuthenticationEvent update) {
                window.Dispatcher.BeginInvoke(new Action(async delegate {
                    if (closing) return;
                    currentState = update.State;
                    if (update.State != AuthenticationState.Connected) automaticMinimizeVersion++;
                    if (!authentication.IsAvailable || update.State == AuthenticationState.Disconnected) authenticationRunning = false;
                    if (!string.IsNullOrEmpty(update.Message)) Log(update.Message, update.State);
                    UpdateAuthenticationControls();
                    Task minimize = Task.FromResult(0);
                    if (update.State == AuthenticationState.Connected && !handledSuccess) {
                        handledSuccess = true; if (committed.AutoMinimize) minimize = MinimizeAfterSuccessAsync();
                        if (committed.HotspotAfterSuccess) Log("正在准备移动热点：等待认证网卡联网后，结束 8021x.exe 一次并开启热点");
                    }
                    try {
                        string result = await hotspot.HandleStateAsync(update.State);
                        if (!closing && !string.IsNullOrEmpty(result)) Log(result);
                    } catch (OperationCanceledException) { if (!closing) Log("认证后热点操作已取消；已开启的热点可在 Windows 设置中关闭"); }
                    catch (Exception error) { if (!closing) Log("认证后热点操作失败：" + error.Message); }
                    await minimize;
                }));
            };
            Get<TextBlock>("EngineStatus").Text = "正在连接后台";
            try {
                await nativeClient.ConnectAsync(CancellationToken.None);
                if (!closing) Log(authentication.IsAvailable ? "已准备就绪，点击开始认证连接校园网" : "抓包驱动不可用，无法开始认证");
            } catch (Exception error) { if (!closing) Log("后台不可用：" + error.Message); }
            if (!closing) UpdateAuthenticationControls();
        }

        private void UpdateAuthenticationControls()
        {
            bool locked = authenticationBusy || authenticationRunning || closing;
            foreach (string name in new[] { "AccountChoice", "AdapterChoice" }) Get<ComboBox>(name).IsEnabled = !locked;
            Get<Button>("OpenSettings").IsEnabled = Get<Button>("ImportConfiguration").IsEnabled = !locked;
            var account = Get<ComboBox>("AccountChoice").SelectedItem as AccountDraft;
            var adapter = Get<ComboBox>("AdapterChoice").SelectedItem as AdapterInfo;
            Get<Button>("Authenticate").IsEnabled = !authenticationBusy && !closing && authentication.IsAvailable &&
                (authenticationRunning || (account != null && account.PasswordReadable && !string.IsNullOrEmpty(account.SourceSection) && adapter != null && adapter.CaptureAvailable));
            Get<TextBlock>("AuthenticateLabel").Text = authenticationBusy ? "请稍候…" : authenticationRunning ? "断开认证" : "开始认证";
            Get<TextBlock>("EngineStatus").Text = nativeClient == null ? "界面预览" : !authentication.IsAvailable ? "认证服务不可用" :
                currentState == AuthenticationState.Connected ? "校园网已连接" : authenticationRunning ? "认证进行中" : "点击开始认证";
            string[] labels = { "未认证", "寻找服务器", "发送账号", "验证中", "获取 IP", "已认证", "认证失败" };
            Get<TextBlock>("StateText").Text = labels[(int)currentState];
            string color = currentState == AuthenticationState.Connected ? "#19976F" : currentState == AuthenticationState.Disconnected || currentState == AuthenticationState.Failed ? "#ED3652" : "#237CB7";
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            Get<TextBlock>("StateText").Foreground = brush; Get<System.Windows.Shapes.Ellipse>("StateDot").Fill = brush;
            Get<Border>("StateBadge").Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(currentState == AuthenticationState.Connected ? "#E1F5EB" : currentState == AuthenticationState.Disconnected || currentState == AuthenticationState.Failed ? "#FFE7EC" : "#E7F2FF"));
            Get<TextBlock>("ConfigurationStatus").Text = nativeClient == null ? (configuration == null ? "界面预览 · 更改仅保留在本窗口" : "设置保存至 WPF 独立配置副本 · 离线界面检查") :
                "WPF 独立配置 · 认证引擎集成试用 · 本版手动开始认证";
        }

        private async Task ToggleAuthenticationAsync()
        {
            if (authenticationBusy || closing || !authentication.IsAvailable) return;
            automaticMinimizeVersion++;
            bool stopping = authenticationRunning;
            authenticationBusy = true; UpdateAuthenticationControls();
            try {
                if (stopping) {
                    await hotspot.CancelAndWaitAsync();
                    await authentication.StopAsync(CancellationToken.None); authenticationRunning = false; currentState = AuthenticationState.Disconnected;
                    committed = configuration.Load(); BindHome(); // Core may persist ClientType during authentication.
                } else {
                    await hotspot.CancelAndWaitAsync();
                    var account = Get<ComboBox>("AccountChoice").SelectedItem as AccountDraft;
                    var adapter = Get<ComboBox>("AdapterChoice").SelectedItem as AdapterInfo;
                    if (account == null || adapter == null || !adapter.CaptureAvailable || !account.PasswordReadable) throw new InvalidOperationException("请选择有效的已保存账号和抓包网卡。");
                    var selected = committed.Clone(); selected.DefaultAccount = account.SourceSection; selected.DefaultAdapter = adapter.Id;
                    committed = configuration.Save(selected); BindHome(); authenticationRunning = true; handledSuccess = false;
                    hotspot.BeginSession(committed.HotspotAfterSuccess, adapter.Id);
                    await authentication.StartAsync(new AuthenticationRequest { AccountKey = committed.DefaultAccount, AdapterKey = adapter.Id }, CancellationToken.None);
                }
            } catch (Exception error) { hotspot.Cancel(); authenticationRunning = false; currentState = AuthenticationState.Failed; Log(error.Message, AuthenticationState.Failed); }
            finally { authenticationBusy = false; UpdateAuthenticationControls(); }
        }

        private void BindHome()
        {
            var combo = Get<ComboBox>("AccountChoice");
            var selected = combo.SelectedItem as AccountDraft;
            string username = selected == null ? null : selected.Username;
            combo.ItemsSource = committed.Accounts;
            combo.SelectedItem = committed.Accounts.FirstOrDefault(a => a.SourceSection == committed.DefaultAccount) ??
                committed.Accounts.FirstOrDefault(a => a.Username == username);
            if (combo.SelectedIndex < 0 && committed.Accounts.Count != 0) combo.SelectedIndex = 0;
        }

        private void BindAdapters()
        {
            var adapters = AdapterCatalog.Enumerate().ToList();
            if (!string.IsNullOrEmpty(committed.DefaultAdapter) && !adapters.Any(a => a.Id == committed.DefaultAdapter))
                adapters.Insert(0, new AdapterInfo { Id = committed.DefaultAdapter, Name = "已保存的适配器（当前不可用）", CaptureAvailable = false });
            var combo = Get<ComboBox>("AdapterChoice"); combo.ItemsSource = adapters;
            combo.SelectedItem = adapters.FirstOrDefault(a => a.Id == committed.DefaultAdapter);
            if (combo.SelectedIndex < 0) combo.SelectedIndex = adapters.Count > 0 ? 0 : -1;
        }

        private void UpdateConfigurationMode()
        {
            bool persistent = configuration != null;
            startupAvailable = false; startupReadError = null;
            if (persistent && startup != null) {
                try { committed.AutoRun = startup.IsEnabled; startupAvailable = true; }
                catch (Exception error) { startupReadError = error.Message; }
            }
            Get<TextBlock>("ConfigurationStatus").Text = persistent ?
                "设置保存至 WPF 独立配置副本 · 认证功能尚未接入" :
                "界面预览 · 更改仅保留在本窗口 · 认证功能尚未接入";
            foreach (string name in new[] { "AccountAutoRun", "ParameterAutoRun" }) {
                Get<CheckBox>(name).IsEnabled = !persistent || startupAvailable;
                Get<CheckBox>(name).ToolTip = !persistent ? "预览模式仅演示此选项，不写入 Windows 启动项。" : startupAvailable ?
                    "点击确定后生效：登录 Windows 时自动打开当前客户端；取消勾选并保存可关闭。" :
                    "无法读取 Windows 启动设置：" + (startupReadError ?? "当前模式不支持系统启动项。");
            }
        }

        private async void ImportConfiguration()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "导入旧客户端配置为 WPF 副本", Filter = "MentoHUST 配置 (*.ini)|*.ini", FileName = "Config.ini" };
            if (dialog.ShowDialog(window) != true) return;
            try {
                var target = configuration ?? new LegacyConfigurationStore(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MentoHUST.Wpf", "Config.ini"));
                SessionDraft imported = target.Import(dialog.FileName);
                configuration = target; committed = imported; BindHome(); BindAdapters(); UpdateConfigurationMode();
                window.Title = "JNU Campus Network"; Log("旧配置已导入，原文件保持不变");
                if (enableEngine && nativeClient == null) await InitializeEngineAsync(); else UpdateAuthenticationControls();
            } catch (Exception error) { MessageBox.Show(window, error.Message, "导入失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        private void BrowsePackage()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "选择自定义认证数据包", Filter = "所有文件 (*.*)|*.*" };
            if (dialog.ShowDialog(window) == true) Get<TextBox>("PackagePath").Text = dialog.FileName;
        }

        private void OpenSettings()
        {
            if (transitioning || settingsVisible || authenticationRunning || authenticationBusy) return;
            UpdateConfigurationMode();
            draft = committed.Clone();
            Get<ListBox>("AccountList").ItemsSource = draft.Accounts;
            Get<ListBox>("AccountList").SelectedIndex = draft.Accounts.Count == 0 ? -1 : 0;
            syncing = true;
            Get<CheckBox>("AccountAutoRun").IsChecked = Get<CheckBox>("ParameterAutoRun").IsChecked = draft.AutoRun;
            Get<CheckBox>("AutoAuthenticate").IsChecked = draft.AutoAuthenticate;
            Get<CheckBox>("AutoMinimize").IsChecked = draft.AutoMinimize;
            Get<CheckBox>("HotspotAfterSuccess").IsChecked = draft.HotspotAfterSuccess;
            Get<CheckBox>("BindMac").IsChecked = draft.BindMac;
            Get<CheckBox>("UsePackage").IsChecked = draft.UsePackage;
            syncing = false;
            Get<ComboBox>("Multicast").SelectedIndex = draft.Multicast;
            Get<ComboBox>("Dhcp").SelectedIndex = draft.Dhcp;
            Get<TextBox>("Timeout").Text = draft.Timeout.ToString();
            Get<TextBox>("EchoTime").Text = draft.EchoTime.ToString();
            Get<TextBox>("Reconnect").Text = draft.Reconnect.ToString();
            Get<TextBox>("PackagePath").Text = draft.PackagePath;
            Get<TextBlock>("ValidationMessage").Text = "";
            Get<RadioButton>("AccountTab").IsChecked = true;
            ShowTab(false);
            Slide(true);
        }

        private void Slide(bool entering)
        {
            transitioning = true; settingsVisible = entering;
            Grid outgoing = Get<Grid>(entering ? "HomePage" : "SettingsPage");
            Grid incoming = Get<Grid>(entering ? "SettingsPage" : "HomePage");
            incoming.Visibility = Visibility.Visible;
            incoming.IsHitTestVisible = outgoing.IsHitTestVisible = false;
            double width = ((FrameworkElement)window.Content).ActualWidth;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            ease.Freeze();
            var from = (TranslateTransform)outgoing.RenderTransform; var to = (TranslateTransform)incoming.RenderTransform;
            from.BeginAnimation(TranslateTransform.XProperty, null); to.BeginAnimation(TranslateTransform.XProperty, null);
            from.X = to.X = 0;
            Action finish = delegate {
                outgoing.Visibility = Visibility.Collapsed;
                from.BeginAnimation(TranslateTransform.XProperty, null); to.BeginAnimation(TranslateTransform.XProperty, null);
                from.X = to.X = 0;
                outgoing.CacheMode = incoming.CacheMode = null;
                incoming.IsHitTestVisible = outgoing.IsHitTestVisible = true;
                transitioning = false;
                if (entering) Get<Button>("Back").Focus(); else Get<Button>("OpenSettings").Focus();
            };
            if (!SystemParameters.ClientAreaAnimation) { finish(); return; }
            // Cache only while sliding; resting pages render text normally at the current DPI.
            var source = PresentationSource.FromVisual(window);
            double scale = source == null ? 1 : source.CompositionTarget.TransformToDevice.M11;
            homePageCache.RenderAtScale = settingsPageCache.RenderAtScale = scale;
            Get<Grid>("HomePage").CacheMode = homePageCache;
            Get<Grid>("SettingsPage").CacheMode = settingsPageCache;
            var leave = new DoubleAnimation(0, entering ? -width : width, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
            var arrive = new DoubleAnimation(entering ? width : -width, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
            arrive.Completed += delegate { finish(); };
            from.BeginAnimation(TranslateTransform.XProperty, leave);
            to.BeginAnimation(TranslateTransform.XProperty, arrive);
        }

        private void ShowTab(bool parameter)
        {
            Get<Grid>("AccountPage").Visibility = parameter ? Visibility.Collapsed : Visibility.Visible;
            Get<ScrollViewer>("ParameterPage").Visibility = parameter ? Visibility.Visible : Visibility.Collapsed;
            UpdateTabSelection(settingsVisible && !transitioning);
        }

        private void UpdateTabSelection(bool animate)
        {
            var track = Get<Grid>("SettingsTabs");
            double width = track.ColumnDefinitions[0].ActualWidth;
            if (width <= 0) return;
            var shift = (TranslateTransform)Get<Border>("TabSelection").RenderTransform;
            double target = Get<RadioButton>("ParameterTab").IsChecked == true ? width : 0;
            double current = shift.X;
            shift.BeginAnimation(TranslateTransform.XProperty, null);
            shift.X = target;
            if (animate && SystemParameters.ClientAreaAnimation && Math.Abs(current - target) > 0.1) {
                shift.BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(200)) {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop
                    });
            }
        }

        private void PopulateAccount()
        {
            var account = Get<ListBox>("AccountList").SelectedItem as AccountDraft;
            Get<TextBox>("Username").Text = account == null ? "" : account.Username;
            Get<PasswordBox>("Password").Password = account == null ? "" : account.Password;
            Get<TextBox>("VisiblePassword").Text = account == null ? "" : account.Password;
            if (account != null && !account.PasswordReadable) Error("原密码无法解码，保存参数时会保留原值");
        }

        private void RevealPassword()
        {
            var password = Get<PasswordBox>("Password"); var visible = Get<TextBox>("VisiblePassword");
            if (password.Visibility == Visibility.Visible) {
                visible.Text = password.Password; password.Visibility = Visibility.Collapsed; visible.Visibility = Visibility.Visible;
            } else {
                password.Password = visible.Text; visible.Visibility = Visibility.Collapsed; password.Visibility = Visibility.Visible;
            }
        }

        private void AddAccount()
        {
            string username = Get<TextBox>("Username").Text.Trim();
            string password = Get<PasswordBox>("Password").Visibility == Visibility.Visible ? Get<PasswordBox>("Password").Password : Get<TextBox>("VisiblePassword").Text;
            if (username.Length == 0 || password.Length == 0) { Error("请填写账号和密码"); return; }
            if (Encoding.UTF8.GetByteCount(username) > 65 || Encoding.UTF8.GetByteCount(password) > 63) { Error("账号或密码超过认证核心的字节长度限制"); return; }
            try { LegacyCodec.Encode(password); } catch (ArgumentException error) { Error(error.Message); return; }
            var previous = draft.Accounts.FirstOrDefault(a => a.Username == username);
            // IP is no longer editable here. Preserve imported values; new accounts use the legacy default.
            var account = previous == null ? new AccountDraft { Ip = "0.0.0.0" } : previous.Clone();
            account.Username = username; account.Password = password; account.PasswordReadable = true;
            if (previous == null) draft.Accounts.Add(account);
            else { int index = draft.Accounts.IndexOf(previous); draft.Accounts[index] = account; }
            Get<ListBox>("AccountList").SelectedItem = account;
            Get<TextBlock>("ValidationMessage").Text = "";
        }

        private void DeleteAccount()
        {
            var list = Get<ListBox>("AccountList"); var selected = list.SelectedItem as AccountDraft;
            if (selected == null) { Error("请先选择账号"); return; }
            int index = list.SelectedIndex; draft.Accounts.Remove(selected);
            list.SelectedIndex = Math.Min(index, draft.Accounts.Count - 1);
            if (draft.Accounts.Count == 0) PopulateAccount();
        }

        private void SynchronizeAutoRun(bool fromParameter = false)
        {
            if (syncing) return;
            syncing = true;
            Get<CheckBox>(fromParameter ? "AccountAutoRun" : "ParameterAutoRun").IsChecked =
                Get<CheckBox>(fromParameter ? "ParameterAutoRun" : "AccountAutoRun").IsChecked;
            syncing = false;
        }

        private void Error(string message) { Get<TextBlock>("ValidationMessage").Text = message; }

        private void ApplySettings()
        {
            if (transitioning) return;
            int timeout, echo, reconnect;
            if (!int.TryParse(Get<TextBox>("Timeout").Text, out timeout) || timeout < 0 || timeout > 99) { Error("认证超时范围为 0–99 秒"); return; }
            if (!int.TryParse(Get<TextBox>("EchoTime").Text, out echo) || echo < 0 || echo > 999) { Error("心跳间隔范围为 0–999 秒"); return; }
            if (!int.TryParse(Get<TextBox>("Reconnect").Text, out reconnect) || reconnect < 0 || reconnect > 999) { Error("重连间隔范围为 0–999 分"); return; }
            draft.Timeout = timeout; draft.EchoTime = echo; draft.Reconnect = reconnect;
            draft.AutoRun = Get<CheckBox>("AccountAutoRun").IsChecked == true;
            draft.AutoAuthenticate = Get<CheckBox>("AutoAuthenticate").IsChecked == true;
            draft.AutoMinimize = Get<CheckBox>("AutoMinimize").IsChecked == true;
            draft.HotspotAfterSuccess = Get<CheckBox>("HotspotAfterSuccess").IsChecked == true;
            draft.BindMac = Get<CheckBox>("BindMac").IsChecked == true;
            draft.UsePackage = Get<CheckBox>("UsePackage").IsChecked == true;
            draft.Multicast = Get<ComboBox>("Multicast").SelectedIndex;
            draft.Dhcp = Get<ComboBox>("Dhcp").SelectedIndex;
            draft.PackagePath = Get<TextBox>("PackagePath").Text;
            var selectedAccount = Get<ComboBox>("AccountChoice").SelectedItem as AccountDraft;
            if (selectedAccount != null) draft.DefaultAccount = selectedAccount.SourceSection;
            var selectedAdapter = Get<ComboBox>("AdapterChoice").SelectedItem as AdapterInfo;
            if (selectedAdapter != null && !string.IsNullOrEmpty(selectedAdapter.Id)) draft.DefaultAdapter = selectedAdapter.Id;
            try { committed = StartupSettingsPersistence.Save(configuration, draft, startupAvailable ? startup : null); }
            catch (Exception error) { Error("保存失败：" + error.Message); return; }
            BindHome();
            Log(configuration == null ? "更改已应用到本次界面预览" : "账号与参数已保存"); Slide(false);
        }

        internal void LogStartup() { Log("已随 Windows 登录启动"); }

        private void CancelSettings()
        {
            if (transitioning || !settingsVisible) return;
            draft = null; Slide(false);
        }

        private async Task MinimizeAfterSuccessAsync()
        {
            if (!committed.AutoMinimize) return;
            int version = ++automaticMinimizeVersion;
            try {
                // Give the connected status a render opportunity, then keep it visible for half a second.
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                await Task.Delay(500);
                if (version == automaticMinimizeVersion && !closing && authenticationRunning &&
                    currentState == AuthenticationState.Connected && committed.AutoMinimize && window.IsVisible)
                    MinimizeToTray();
            } catch (Exception error) { if (!closing) Log("收起到托盘失败：" + error.Message); }
        }

        private void MinimizeToTray()
        {
            automaticMinimizeVersion++;
            if (tray == null) {
                tray = new Forms.NotifyIcon { Text = "JNU Campus Network" };
                using (var stream = Program.Resource("AppIcon.ico")) {
                    int size = Forms.SystemInformation.SmallIconSize.Width;
                    trayIcon = new System.Drawing.Icon(stream, size, size);
                    tray.Icon = trayIcon;
                }
                tray.DoubleClick += delegate { RestoreFromTray(); };
                tray.ContextMenuStrip = new Forms.ContextMenuStrip();
                tray.ContextMenuStrip.Items.Add("显示窗口", null, delegate { RestoreFromTray(); });
                tray.ContextMenuStrip.Items.Add("退出", null, delegate { window.Close(); });
            }
            tray.Visible = true; window.Hide();
        }

        private void RestoreFromTray() { automaticMinimizeVersion++; window.Show(); window.WindowState = WindowState.Normal; window.Activate(); if (tray != null) tray.Visible = false; }

        private void Capture(string directory, string name, FrameworkElement element = null)
        {
            window.UpdateLayout();
            var source = PresentationSource.FromVisual(window);
            double scale = source.CompositionTarget.TransformToDevice.M11;
            var content = element ?? (FrameworkElement)window.Content;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * scale),
                (int)Math.Ceiling(content.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(directory, name + ".png"))) encoder.Save(file);
        }

        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

        private void CheckFocusWithoutClickOutline(Control control)
        {
            string border = control.BorderBrush == null ? "" : control.BorderBrush.ToString();
            Require(control.Focus(), "Cannot focus " + control.Name);
            window.UpdateLayout();
            Require((control.BorderBrush == null ? "" : control.BorderBrush.ToString()) == border,
                "Programmatic focus changed border: " + control.Name);
            var layer = AdornerLayer.GetAdornerLayer(control);
            Require(layer == null || layer.GetAdorners(control) == null, "Programmatic focus added an outline: " + control.Name);
        }

        private async Task CheckKeyboardFocusNavigationAsync()
        {
            Get<Button>("Back").Focus();
            // Target only this test window so WPF observes native keyboard input without SendInput.
            IntPtr handle = new WindowInteropHelper(window).Handle;
            Require(PostMessage(handle, 0x100, new IntPtr(9), new IntPtr(0x000F0001)) &&
                PostMessage(handle, 0x101, new IntPtr(9), new IntPtr(unchecked((int)0xC00F0001))), "Cannot route test Tab");
            await Task.Delay(100);
            window.UpdateLayout();
            var tab = Get<RadioButton>("AccountTab");
            Require(tab.IsKeyboardFocused, "Tab navigation did not reach the account tab");
            var layer = AdornerLayer.GetAdornerLayer(tab);
            Require(layer != null && layer.GetAdorners(tab) != null, "Keyboard navigation lost its position cue");
            Require(PostMessage(handle, 0x100, new IntPtr(0x27), new IntPtr(0x014D0001)) &&
                PostMessage(handle, 0x101, new IntPtr(0x27), new IntPtr(unchecked((int)0xC14D0001))), "Cannot route Right key");
            await Task.Delay(250);
            Require(Get<RadioButton>("ParameterTab").IsChecked == true && Get<ScrollViewer>("ParameterPage").Visibility == Visibility.Visible,
                "Right key did not select parameter settings");
            Require(PostMessage(handle, 0x100, new IntPtr(0x25), new IntPtr(0x014B0001)) &&
                PostMessage(handle, 0x101, new IntPtr(0x25), new IntPtr(unchecked((int)0xC14B0001))), "Cannot route Left key");
            await Task.Delay(250);
            Require(tab.IsChecked == true && Get<Grid>("AccountPage").Visibility == Visibility.Visible,
                "Left key did not select account settings");
        }

        private async Task CheckCheckboxTransitionAsync(string directory)
        {
            var box = Get<CheckBox>("AutoMinimize"); bool? original = box.IsChecked;
            var fill = (Border)box.Template.FindName("CheckedFill", box);
            var tick = (System.Windows.Shapes.Path)box.Template.FindName("Tick", box);
            box.IsChecked = false; await Task.Delay(220);
            Require(fill.Opacity < 0.01 && tick.Opacity < 0.01, "Unchecked visual did not settle");
            box.IsChecked = true; await Task.Delay(60);
            double intermediateOpacity = fill.Opacity;
            Capture(directory, "checkbox-transition");
            await Task.Delay(180);
            Require(fill.Opacity > 0.99 && tick.Opacity > 0.99, "Checked visual did not settle");
            Capture(directory, "checkbox-checked");
            box.IsChecked = false; await Task.Delay(35);
            box.IsChecked = true; await Task.Delay(35);
            box.IsChecked = false; await Task.Delay(220);
            Require(box.IsChecked == false && fill.Opacity < 0.01 && tick.Opacity < 0.01,
                "Rapid toggles left a stale checked visual");
            Capture(directory, "checkbox-unchecked");
            File.WriteAllText(Path.Combine(directory, "checkbox-animation.txt"),
                "CHECKBOX_TRANSITION_PASS\nChecked and unchecked settle correctly; rapid toggles preserve the last state.\n" +
                "Intermediate fill opacity: " + intermediateOpacity.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "\n");
            box.IsChecked = original; await Task.Delay(180);
        }

        private async Task CheckTabSelectionTransitionAsync(string directory)
        {
            var parameter = Get<RadioButton>("ParameterTab"); var account = Get<RadioButton>("AccountTab");
            var track = Get<Grid>("SettingsTabs");
            var shift = (TranslateTransform)Get<Border>("TabSelection").RenderTransform;
            parameter.IsChecked = true; await Task.Delay(60);
            double intermediate = shift.X, initialWidth = track.ColumnDefinitions[0].ActualWidth;
            Capture(directory, "tabs-moving");
            await Task.Delay(220);
            Require(Math.Abs(shift.X - track.ColumnDefinitions[0].ActualWidth) < 0.6 &&
                Get<ScrollViewer>("ParameterPage").Visibility == Visibility.Visible, "Parameter tab slider did not settle");
            Capture(directory, "tabs-parameter");
            account.IsChecked = true; await Task.Delay(35);
            parameter.IsChecked = true; await Task.Delay(35);
            account.IsChecked = true; await Task.Delay(240);
            Require(Math.Abs(shift.X) < 0.6 && Get<Grid>("AccountPage").Visibility == Visibility.Visible,
                "Rapid tab changes left stale slider or page");
            Capture(directory, "tabs-account");
            double originalWidth = window.Width;
            parameter.IsChecked = true; await Task.Delay(35);
            window.Width = window.MinWidth; await Task.Delay(120);
            Require(Math.Abs(shift.X - track.ColumnDefinitions[0].ActualWidth) < 0.6, "Resize left slider off the selected tab");
            window.Width = originalWidth; await Task.Delay(120);
            Require(Math.Abs(shift.X - track.ColumnDefinitions[0].ActualWidth) < 0.6, "Restored window left slider off the selected tab");
            account.IsChecked = true; await Task.Delay(240);
            File.WriteAllText(Path.Combine(directory, "tab-animation.txt"), "TAB_SLIDE_PASS\n" +
                "Selection and page agree after normal/rapid toggles and resizing during animation.\n" +
                "Intermediate X: " + intermediate.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                " of " + initialWidth.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "\n");
        }

        internal async Task RunAnimationChecksAsync(string directory)
        {
            Directory.CreateDirectory(directory);
            var gaps = new System.Collections.Generic.List<double>();
            TimeSpan? previousFrame = null;
            EventHandler rendering = delegate(object sender, EventArgs e) {
                var frame = (RenderingEventArgs)e;
                if (!transitioning) { previousFrame = null; return; }
                if (previousFrame.HasValue && frame.RenderingTime > previousFrame.Value)
                    gaps.Add((frame.RenderingTime - previousFrame.Value).TotalMilliseconds);
                previousFrame = frame.RenderingTime;
            };
            try {
                await Task.Delay(250);
                int gcStart = GC.CollectionCount(0);
                CompositionTarget.Rendering += rendering;
                var originalHomeTransform = Get<Grid>("HomePage").RenderTransform;
                var originalSettingsTransform = Get<Grid>("SettingsPage").RenderTransform;
                double warmP95 = 0;
                for (int cycle = 0; cycle < 16; cycle++) {
                    OpenSettings();
                    for (int poll = 0; transitioning && poll < 150; poll++) await Task.Delay(10);
                    Require(!transitioning && settingsVisible && Get<Grid>("HomePage").Visibility == Visibility.Collapsed, "Entry animation did not finish cleanly");
                    await Task.Delay(20);
                    CancelSettings();
                    for (int poll = 0; transitioning && poll < 150; poll++) await Task.Delay(10);
                    Require(!transitioning && !settingsVisible && Get<Grid>("SettingsPage").Visibility == Visibility.Collapsed, "Return animation did not finish cleanly");
                    await Task.Delay(20);
                    if (cycle == 3 && gaps.Count > 0) {
                        var warm = gaps.OrderBy(x => x).ToArray(); warmP95 = warm[(int)((warm.Length - 1) * 0.95)];
                    }
                }
                CompositionTarget.Rendering -= rendering;
                var ordered = gaps.OrderBy(x => x).ToArray();
                int animatedTransforms = new[] { Get<Grid>("HomePage"), Get<Grid>("SettingsPage") }.Count(page => page.RenderTransform.HasAnimatedProperties);
                bool reused = ReferenceEquals(originalHomeTransform, Get<Grid>("HomePage").RenderTransform) &&
                    ReferenceEquals(originalSettingsTransform, Get<Grid>("SettingsPage").RenderTransform);
                Require(animatedTransforms == 0 && reused, "Completed switches must release clocks and reuse transforms");
                Require(Get<Grid>("HomePage").IsHitTestVisible && Get<Grid>("SettingsPage").IsHitTestVisible, "Switching must restore input");
                Require(Get<Grid>("HomePage").CacheMode == null && Get<Grid>("SettingsPage").CacheMode == null, "Resting pages must release bitmap caches for crisp text");
                Capture(directory, "home-after-switching");
                File.WriteAllText(Path.Combine(directory, "animation-checks.txt"),
                    "PAGE_SWITCH_CHECKS_PASS: 16 round trips; final page and hit testing restored; no resting bitmap caches; memory-only preview.\n" +
                    "Rendering tier: " + (RenderCapability.Tier >> 16) + "\nFrame samples: " + gaps.Count +
                    "\nMedian frame gap ms: " + (ordered.Length == 0 ? 0 : ordered[ordered.Length / 2]).ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                    "\nP95 frame gap ms: " + (ordered.Length == 0 ? 0 : ordered[(int)((ordered.Length - 1) * 0.95)]).ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                    "\nFirst 4 round trips P95 ms: " + warmP95.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                    "\nAnimated transforms after completion: " + animatedTransforms + "\nTransforms reused: " + reused +
                    "\nGeneration-0 GC count delta: " + (GC.CollectionCount(0) - gcStart) +
                    "\nFrame timing is a local observation, not an FPS guarantee on other machines.\n");
            } catch (Exception error) { ExitCode = 1; File.WriteAllText(Path.Combine(directory, "animation-checks.txt"), error.ToString()); }
            finally { CompositionTarget.Rendering -= rendering; window.Close(); }
        }

        internal async Task RunChecksAsync(string directory)
        {
            Directory.CreateDirectory(directory);
            try {
                await Task.Delay(250); Capture(directory, "home");
                Capture(directory, "header", Get<Grid>("BrandHeader"));
                var originalLogs = Get<ListBox>("LogList").Items.Cast<object>().ToArray();
                Get<Button>("ClearLog").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(80); Capture(directory, "home-empty-log");
                foreach (var entry in originalLogs) Get<ListBox>("LogList").Items.Add(entry);
                Require(!Get<Button>("Authenticate").IsEnabled, "Preview must not authenticate");
                Require(!committed.HotspotAfterSuccess, "Hotspot automation defaults off");
                double originalWidth = window.Width, originalHeight = window.Height;
                window.Width = window.MinWidth; window.Height = window.MinHeight; await Task.Delay(100);
                Require(Get<ListBox>("LogList").ActualHeight >= 40, "Minimum window keeps logs readable");
                Capture(directory, "home-compact"); window.Width = originalWidth; window.Height = originalHeight; await Task.Delay(100);
                var adapterChoice = Get<ComboBox>("AdapterChoice"); var originalAdapters = adapterChoice.ItemsSource; var originalAdapter = adapterChoice.SelectedItem;
                adapterChoice.ItemsSource = new[] { new AdapterInfo { Name = "以太网 (Realtek Gaming 2.5GbE Family Controller) · 长名称显示检查" } };
                adapterChoice.SelectedIndex = 0; await Task.Delay(80); Capture(directory, "home-long-adapter");
                adapterChoice.ItemsSource = originalAdapters; adapterChoice.SelectedItem = originalAdapter;
                var combo = Get<ComboBox>("AccountChoice");
                combo.IsDropDownOpen = true; await Task.Delay(100);
                var popup = (Popup)combo.Template.FindName("PART_Popup", combo);
                Require(popup.IsOpen && popup.Child.RenderSize.Width >= combo.ActualWidth - 2, "Dropdown preserves field width");
                combo.SelectedIndex = 1; combo.IsDropDownOpen = false;
                Require(((AccountDraft)combo.SelectedItem).Username == "test_account", "Native combo selection");
                OpenSettings(); await Task.Delay(250); Capture(directory, "accounts");
                CheckFocusWithoutClickOutline(Get<Button>("Back"));
                CheckFocusWithoutClickOutline(Get<Button>("AddAccount"));
                CheckFocusWithoutClickOutline(Get<Button>("DeleteAccount"));
                CheckFocusWithoutClickOutline(Get<Button>("RevealPassword"));
                CheckFocusWithoutClickOutline(Get<RadioButton>("AccountTab"));
                Capture(directory, "settings-focus");
                await CheckTabSelectionTransitionAsync(directory);
                window.Width = window.MinWidth; window.Height = window.MinHeight; await Task.Delay(80);
                Require(Get<ListBox>("AccountList").ActualHeight >= 40, "Minimum window keeps account management reachable");
                var deleteAction = Get<Button>("DeleteAccount"); var accountPage = Get<Grid>("AccountPage");
                Require(deleteAction.TransformToAncestor(accountPage).Transform(new Point(0, deleteAction.ActualHeight)).Y <= accountPage.ActualHeight + 1,
                    "Minimum window keeps delete action inside the account card");
                Capture(directory, "accounts-compact");
                window.Width = originalWidth; window.Height = originalHeight; await Task.Delay(80);
                var originalAccounts = Get<ListBox>("AccountList").ItemsSource;
                Get<ListBox>("AccountList").ItemsSource = new AccountDraft[0];
                await Task.Delay(80); Capture(directory, "accounts-empty");
                Get<ListBox>("AccountList").ItemsSource = originalAccounts;
                Get<ListBox>("AccountList").SelectedIndex = 0;
                Require(Application.Current.Windows.Count == 1, "Navigation must stay in one window");
                int original = draft.Accounts.Count;
                var memoryStartup = startup as MemoryStartupRegistration;
                if (configuration != null) Require(startupAvailable && Get<CheckBox>("AccountAutoRun").IsEnabled &&
                    Get<CheckBox>("ParameterAutoRun").IsEnabled, "Persistent settings must enable both startup controls");
                byte[] originalFile = configuration == null ? null : File.ReadAllBytes(configuration.FilePath);
                string originalIp = committed.Accounts[0].Ip;
                draft.Accounts[0].Ip = "192.0.2.17";
                Get<PasswordBox>("Password").Password = "dummy"; AddAccount();
                Require(draft.Accounts[0].Ip == "192.0.2.17", "Password update preserves existing hidden IP");
                Get<TextBox>("Username").Text = ""; AddAccount();
                Require(draft.Accounts.Count == original && Get<TextBlock>("ValidationMessage").Text.Length > 0, "Reject empty username");
                Get<TextBox>("Username").Text = "preview_added"; Get<PasswordBox>("Password").Password = ""; AddAccount();
                Require(draft.Accounts.Count == original && Get<TextBlock>("ValidationMessage").Text.Length > 0, "Reject empty password");
                Get<PasswordBox>("Password").Password = "dummy"; AddAccount();
                Require(draft.Accounts.Count == original + 1, "Add draft account");
                Require(draft.Accounts.Last().Ip == "0.0.0.0", "Account/password alone use default IP");
                RevealPassword(); Require(Get<TextBox>("VisiblePassword").Text == "dummy", "Reveal password");
                RevealPassword(); Require(Get<PasswordBox>("Password").Password == "dummy", "Hide password");
                Get<CheckBox>("AccountAutoRun").IsChecked = true;
                Require(Get<CheckBox>("ParameterAutoRun").IsChecked == true, "Synchronize account-to-parameter");
                Get<CheckBox>("ParameterAutoRun").IsChecked = false;
                Require(Get<CheckBox>("AccountAutoRun").IsChecked == false, "Synchronize parameter-to-account");
                Get<RadioButton>("ParameterTab").IsChecked = true; Capture(directory, "parameters");
                Require(((ToolTip)Get<Button>("HotspotHelp").ToolTip).Content is TextBlock, "Hotspot help tooltip has explanatory text");
                Get<Button>("HotspotHelp").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(100); Capture(directory, "hotspot-help", (ToolTip)Get<Button>("HotspotHelp").ToolTip);
                Get<Button>("HotspotHelp").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Get<CheckBox>("HotspotAfterSuccess").IsChecked = true;
                CheckFocusWithoutClickOutline(Get<RadioButton>("ParameterTab"));
                CheckFocusWithoutClickOutline(Get<CheckBox>("AutoMinimize"));
                CheckFocusWithoutClickOutline(Get<ComboBox>("Multicast"));
                await CheckCheckboxTransitionAsync(directory);
                var parameterScroll = Get<ScrollViewer>("ParameterPage");
                var scrollBar = parameterScroll.Template.FindName("PART_VerticalScrollBar", parameterScroll) as ScrollBar;
                Require(scrollBar != null, "Parameter page keeps its scrollbar");
                ScrollBar.LineDownCommand.Execute(null, scrollBar); await Task.Delay(80);
                Require(parameterScroll.VerticalOffset > 0, "Scrollbar commands move parameter contents");
                parameterScroll.ScrollToEnd(); await Task.Delay(80); Capture(directory, "parameters-bottom");
                Require(parameterScroll.VerticalOffset >= parameterScroll.ScrollableHeight - 1, "Data package fields remain reachable");
                parameterScroll.ScrollToTop(); await Task.Delay(80);
                Get<TextBox>("Timeout").Text = "100"; ApplySettings();
                Require(settingsVisible && committed.Accounts.Count == original, "Reject invalid parameter without commit");
                await Task.Delay(80); Capture(directory, "validation");
                window.Width = window.MinWidth; window.Height = window.MinHeight; await Task.Delay(80);
                Capture(directory, "validation-compact");
                window.Width = originalWidth; window.Height = originalHeight; await Task.Delay(80);
                Get<CheckBox>("AccountAutoRun").IsChecked = true;
                CancelSettings(); await Task.Delay(250);
                if (memoryStartup != null) Require(!memoryStartup.IsEnabled && memoryStartup.ApplyCount == 0, "Cancel must not register startup");
                CheckFocusWithoutClickOutline(Get<Button>("OpenSettings")); Capture(directory, "home-return-focus");
                Require(committed.Accounts.Count == original && committed.Timeout == 8, "Cancel rolls back accounts and parameters");
                Require(!committed.HotspotAfterSuccess, "Cancel discards hotspot option");
                Require(committed.Accounts[0].Ip == originalIp, "Cancel restores hidden IP");
                if (configuration != null) Require(File.ReadAllBytes(configuration.FilePath).SequenceEqual(originalFile), "Cancel leaves configuration bytes unchanged");
                OpenSettings(); await Task.Delay(250);
                draft.Accounts[0].Ip = "192.0.2.17";
                Get<PasswordBox>("Password").Password = "dummy"; AddAccount();
                Get<TextBox>("Username").Text = "preview_applied"; Get<PasswordBox>("Password").Password = "dummy"; AddAccount();
                Get<CheckBox>("HotspotAfterSuccess").IsChecked = true;
                Get<CheckBox>("AccountAutoRun").IsChecked = true;
                Get<TextBox>("Timeout").Text = "10"; ApplySettings(); await Task.Delay(250);
                if (memoryStartup != null) Require(memoryStartup.IsEnabled && memoryStartup.ApplyCount == 1 && committed.AutoRun, "Apply must register startup once");
                Require(committed.Accounts.Count == original + 1 && committed.Timeout == 10, "Apply memory draft");
                Require(committed.HotspotAfterSuccess, "Apply preserves hotspot option");
                Require(committed.Accounts[0].Ip == "192.0.2.17" && committed.Accounts.Last().Ip == "0.0.0.0", "Apply preserves existing IP and default for new account");
                if (configuration != null) {
                    var reloaded = new LegacyConfigurationStore(configuration.FilePath).Load();
                    Require(reloaded.Accounts.Count == original + 1 && reloaded.Timeout == 10, "Apply persists legacy configuration");
                    Require(reloaded.HotspotAfterSuccess, "Hotspot option survives configuration reload");
                    Require(reloaded.AutoRun, "Startup option survives configuration reload");
                    Require(reloaded.Accounts[0].Ip == "192.0.2.17" && reloaded.Accounts.Last().Ip == "0.0.0.0", "Hidden IP values survive configuration reload");
                }
                OpenSettings(); await Task.Delay(250); DeleteAccount(); CancelSettings(); await Task.Delay(250);
                Require(committed.Accounts.Count == original + 1, "Cancel rolls back deletion");
                // Test keyboard mode last: its cues legitimately persist while using the keyboard.
                OpenSettings(); await Task.Delay(250);
                await CheckKeyboardFocusNavigationAsync(); Capture(directory, "settings-keyboard-focus");
                CancelSettings(); await Task.Delay(250);
                MinimizeToTray(); Require(!window.IsVisible && tray.Visible, "Tray hides window");
                RestoreFromTray(); Require(window.IsVisible && !tray.Visible, "Tray restores window");
                await CheckDelayedMinimizeAsync();
                File.WriteAllText(Path.Combine(directory, "checks.txt"), "WPF_UI_CHECKS_PASS\nSingle window; combo selection; draft editing; password reveal; validation; option sync; rollback; apply; tray.\nMinimum window; long adapter display; scrollbar commands; data package fields reachable.\n" +
                    "No programmatic click-style outlines; local Tab routing preserves keyboard focus cue.\n" +
                    "Checkbox animation settles correctly after check, uncheck and rapid toggles.\n" +
                    "Tab slider follows normal/rapid selection, window resizing and native arrow-key navigation.\n" +
                    "Account/password-only editing; empty input rejected; existing IP preserved; new account IP defaults to 0.0.0.0.\n" +
                    "Hotspot option defaults off; help tooltip opens; cancel rolls back; apply and reload preserve option. No hotspot action invoked by UI tests.\n" +
                    "Automatic tray delay: visible connected status for at least 500 ms; disconnect and manual restore cancel pending hide; disabled option leaves window visible. Isolated UI state only.\n" +
                    "Startup: account/parameter options enabled and synchronized; cancel has no registration effect; apply and reload use isolated memory registration.\n" +
                    (configuration == null ? "Memory preview: no configuration files changed.\n" : "Configuration mode: temporary config saved and reloaded; cancellation byte-identical.\n") +
                    "No registry settings or authentication packets were written.\n");
            } catch (Exception error) {
                ExitCode = 1; File.WriteAllText(Path.Combine(directory, "checks.txt"), error.ToString());
            } finally { window.Close(); }
        }

        private async Task CheckDelayedMinimizeAsync()
        {
            bool originalAutoMinimize = committed.AutoMinimize, originalRunning = authenticationRunning;
            AuthenticationState originalState = currentState;
            try {
                // Only the dedicated UI check invokes this in-memory state; no engine event or START is sent.
                committed.AutoMinimize = true; authenticationRunning = true; currentState = AuthenticationState.Connected;
                UpdateAuthenticationControls();
                var timer = System.Diagnostics.Stopwatch.StartNew();
                Task pending = MinimizeAfterSuccessAsync();
                await Task.Delay(150);
                Require(window.IsVisible && Get<TextBlock>("StateText").Text == "已认证", "Connected state must be visible before delayed hide");
                await pending;
                Require(timer.ElapsedMilliseconds >= 500 && !window.IsVisible && tray.Visible, "Automatic tray hide must wait at least half a second");
                RestoreFromTray();
                pending = MinimizeAfterSuccessAsync();
                await Task.Delay(150); currentState = AuthenticationState.Disconnected; automaticMinimizeVersion++;
                await pending;
                Require(window.IsVisible && !tray.Visible, "Disconnect must cancel pending automatic hide");
                currentState = AuthenticationState.Connected;
                pending = MinimizeAfterSuccessAsync();
                await Task.Delay(150); RestoreFromTray();
                await pending;
                Require(window.IsVisible && !tray.Visible, "Manual restore must cancel pending automatic hide");
                committed.AutoMinimize = false; await MinimizeAfterSuccessAsync();
                Require(window.IsVisible, "Disabled automatic minimize must leave window visible");
            } finally {
                automaticMinimizeVersion++;
                committed.AutoMinimize = originalAutoMinimize; authenticationRunning = originalRunning; currentState = originalState;
                if (!window.IsVisible) RestoreFromTray();
                UpdateAuthenticationControls();
            }
        }

        internal async Task RunEngineChecksAsync(string directory)
        {
            Directory.CreateDirectory(directory);
            try {
                // Startup handshake and configuration validation only. Never call START in this mode.
                Require(nativeClient != null, "Native backend was not selected");
                await nativeClient.ConnectAsync(CancellationToken.None); await Task.Delay(200);
                var account = Get<ComboBox>("AccountChoice").SelectedItem as AccountDraft;
                var adapter = Get<ComboBox>("AdapterChoice").SelectedItem as AdapterInfo;
                Require(account != null && account.SourceSection == "Account0", "Engine account key not restored");
                await nativeClient.ValidateAsync(new AuthenticationRequest { AccountKey = account.SourceSection,
                    AdapterKey = "\\Device\\NPF_{00000000-0000-0000-0000-000000000001}" }, CancellationToken.None);
                Require(Get<Button>("Authenticate").IsEnabled == (nativeClient.IsAvailable && adapter != null && adapter.CaptureAvailable), "Start button readiness mismatch");
                Require(Get<TextBlock>("StateText").Text == "未认证", "Readiness manufactured connected status");
                Require(Get<Button>("OpenSettings").IsEnabled && Get<ComboBox>("AccountChoice").IsEnabled, "Idle controls remained locked");
                byte[] before = File.ReadAllBytes(configuration.FilePath);
                await nativeClient.StopAsync(CancellationToken.None); await Task.Delay(100);
                Require(Get<TextBlock>("StateText").Text == "未认证" && before.SequenceEqual(File.ReadAllBytes(configuration.FilePath)), "Idle stop changed state/config incorrectly");
                Capture(directory, "home-engine");
                File.WriteAllText(Path.Combine(directory, "checks.txt"), "WPF_ENGINE_UI_CHECKS_PASS\nNative handshake; account key; config validation; capture readiness; idle stop; UI dispatch; normal window close.\nNo START command; no authentication packets; temporary dummy config only.\n");
            } catch (Exception error) { ExitCode = 1; File.WriteAllText(Path.Combine(directory, "checks.txt"), error.ToString()); }
            finally { window.Close(); }
        }
    }
}
