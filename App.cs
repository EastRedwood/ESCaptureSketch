using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Hardcodet.Wpf.TaskbarNotification;
using SkiaSharp;

using Color = System.Drawing.Color;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;

namespace ESCaptureSketch;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // 이미 떠 있는 인스턴스에 신호만 보내는 경로는 WPF 를 띄우기 전에 처리한다.
        if (args.Contains("--exit")) return SignalRunning(App.ExitEventName) ? 0 : 1;
        // .escs 연결: --register 는 이미 연결돼 있으면 실행 파일 경로를 고치고(빌드 뒤 자동 실행), --unregister 는 지운다.
        if (args.Contains("--register")) { FileType.Refresh(); return 0; }
        if (args.Contains("--unregister")) return FileType.Unregister() ? 0 : 1;

        var app = new App();
        return app.Run();
    }

    internal static bool SignalRunning(string eventName)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(eventName, out var h)) return false;
            using (h) h.Set();
            return true;
        }
        catch { return false; }
    }
}

public sealed class App : System.Windows.Application
{
    internal const string ExitEventName = @"Local\ESCaptureSketch.Exit";
    private const string CaptureEventName = @"Local\ESCaptureSketch.Capture";
    private const string OpenEventName = @"Local\ESCaptureSketch.Open";
    private EventWaitHandle? _openEvent;
    private const int HotkeyIdCapture = 1;
    private const int HotkeyIdPaste = 2;
    private const int HotkeyIdPin = 4;
    private const int HotkeyIdChalk = 3;
    private bool _chalkHotkeyOn;

    private TaskbarIcon? _trayIcon;
    private AppSettings _settings = new();
    private HotkeyManager? _hotkeys;
    private Mutex? _instanceMutex;
    private EventWaitHandle? _exitEvent;
    private EventWaitHandle? _captureEvent;
    private bool _busy;

    protected override void OnStartup(StartupEventArgs e)
    {
        // GPU 가속을 쓰면 첫 창에서 드라이버 메모리가 크게 올라와 창을 닫아도 남는다. (--hw 는 비교용 개발 옵션)
        if (!e.Args.Contains("--hw"))
            RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Write("처리되지 않은 오류: " + ex.Exception);
            Notify(L.T("오류가 생겼습니다: ") + ex.Exception.Message, BalloonIcon.Error);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Write("치명적 오류: " + ex.ExceptionObject);

        _settings = AppSettings.Load();
        L.Apply(_settings.Language);
        Item.SetFont(_settings.TextFont);
        Item.TextAdjust = _settings.TextSizeAdjust;
        EditOptions.ShapeEdit = _settings.ShapeEdit;
        EditOptions.LaserStyle = _settings.LaserStyle;
        GridOptions.Apply(_settings);
        if (!e.Args.Any(a => a.StartsWith("--dump") || a == "--overlaytest")) AutoStart.RefreshPath();
        ThemeService.SetWhiteboardMode(_settings.Whiteboard, _settings.WhiteboardColor);
        ThemeService.Apply(_settings.Theme);

        var dev = DevCommands.TryParse(e.Args);
        if (dev != null)
        {
            // 개발용 실행은 실제 인스턴스·단축키와 겹치지 않게 단일 인스턴스를 건너뛴다.
            _ = dev.RunAsync(this, _settings);
            return;
        }

        // 탐색기에서 파일을 두 번 눌러 연 경우: 인수로 파일 경로가 온다.
        var files = e.Args.Where(a => !a.StartsWith('-') && File.Exists(a)).Select(Path.GetFullPath).ToList();

        if (!TryClaimSingleInstance())
        {
            // 이미 떠 있으면 새로 띄우지 않고, 파일이 있으면 그쪽에서 열게 넘기고 없으면 캡처 화면을 띄우게 한다.
            if (files.Count > 0) SendOpenRequest(files);
            else Program.SignalRunning(CaptureEventName);
            Shutdown();
            return;
        }

        _hotkeys = new HotkeyManager();
        BuildTrayIcon();
        RefreshHotkey();
        ListenForSignals();
        if (_settings.AssociateEscs) FileType.Register();
        foreach (var file in files) OpenRequestedFile(file);

        // 설정 파일이 없는 첫 실행에만 자동 시작을 등록할지 묻는다. 먼저 저장해 두므로 다시 묻지 않는다.
        if (_settings.FirstRun)
        {
            _settings.Save();
            if (!AutoStart.IsEnabled())
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (FirstRunDialog.Ask()) Log.Write($"첫 실행: 자동 시작 등록 {(AutoStart.Set(true) ? "성공" : "실패")}");
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        Log.Write($"시작 {AppInfo.Display}");
        MemoryTrimmer.ScheduleTrim(TimeSpan.FromSeconds(10));
    }

    private bool TryClaimSingleInstance()
    {
        _instanceMutex = new Mutex(true, @"Local\ESCaptureSketch.SingleInstance", out var createdNew);
        if (createdNew) return true;
        _instanceMutex.Dispose();
        _instanceMutex = null;
        return false;
    }

    private static string OpenRequestDir => Path.Combine(AppPaths.DataDir, "open");

    /// <summary>떠 있는 인스턴스에 열 파일을 넘긴다: 요청마다 파일 하나를 남기고 신호를 보낸다 (여러 개를 한꺼번에 열어도 섞이지 않게).</summary>
    private static void SendOpenRequest(List<string> files)
    {
        try
        {
            Directory.CreateDirectory(OpenRequestDir);
            File.WriteAllLines(Path.Combine(OpenRequestDir, Guid.NewGuid().ToString("N") + ".txt"), files);
            User32.AllowSetForegroundWindow(-1);   // 방금 사용자가 실행한 쪽의 "앞으로 나오기" 권한을 넘긴다
            Program.SignalRunning(OpenEventName);
        }
        catch (Exception ex) { Log.Write("열기 요청 전달 실패: " + ex.Message); }
    }

    private void ProcessOpenRequests()
    {
        try
        {
            if (!Directory.Exists(OpenRequestDir)) return;
            foreach (var request in Directory.GetFiles(OpenRequestDir, "*.txt"))
            {
                var lines = File.ReadAllLines(request);
                File.Delete(request);
                foreach (var file in lines.Where(File.Exists)) OpenRequestedFile(file);
            }
        }
        catch (Exception ex) { Log.Write("열기 요청 처리 실패: " + ex.Message); }
    }

    private void OpenRequestedFile(string path)
    {
        if (OpenFileInEditor(path) is { } error) { Notify(L.T("열지 못했습니다: ") + error, BalloonIcon.Warning); return; }
        var window = Windows.OfType<EditorWindow>().LastOrDefault();
        if (window != null) User32.SetForegroundWindow(new System.Windows.Interop.WindowInteropHelper(window).Handle);
        Log.Write("파일 열기: " + Path.GetFileName(path));
    }

    private void ListenForSignals()
    {
        _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        _captureEvent = new EventWaitHandle(false, EventResetMode.AutoReset, CaptureEventName);
        _openEvent = new EventWaitHandle(false, EventResetMode.AutoReset, OpenEventName);

        var thread = new Thread(() =>
        {
            var handles = new WaitHandle[] { _exitEvent, _captureEvent, _openEvent };
            while (true)
            {
                int i;
                try { i = WaitHandle.WaitAny(handles); }
                catch { return; }

                if (i == 0) { Dispatcher.BeginInvoke(new Action(ExitApp)); return; }
                if (i == 2) { Dispatcher.BeginInvoke(new Action(ProcessOpenRequests)); continue; }
                Dispatcher.BeginInvoke(new Action(() => _ = StartCaptureAsync()));
            }
        }) { IsBackground = true, Name = "signals" };
        thread.Start();
    }

    private void RefreshHotkey()
    {
        if (_hotkeys == null) return;
        _hotkeys.Unregister(HotkeyIdChalk);   // 키가 바뀌었을 수 있으니 칠판이 열려 있으면 다시 등록한다
        _chalkHotkeyOn = false;
        UpdateChalkHotkey();
        // 둘 다 먼저 풀고 따로 등록한다: 한쪽 칸을 비워도 다른 쪽은 그대로 먹고, 두 키를 서로 바꿔도 실패하지 않는다.
        _hotkeys.Unregister(HotkeyIdCapture);
        _hotkeys.Unregister(HotkeyIdPaste);
        RegisterGlobal(HotkeyIdCapture, _settings.CaptureHotkey, () => _ = StartCaptureAsync());
        RegisterGlobal(HotkeyIdPaste, _settings.PasteHotkey, PasteFromClipboard);
        _hotkeys.Unregister(HotkeyIdPin);
        RegisterGlobal(HotkeyIdPin, _settings.PinImageHotkey, ShowClipboardImage);
    }

    private void RegisterGlobal(int id, string key, Action action)
    {
        if (string.IsNullOrEmpty(key)) return;   // 비운 키는 등록하지 않는다
        if (!_hotkeys!.Register(id, key, action))
            Notify(L.F("단축키 등록 실패: {0} (다른 프로그램이 이미 사용 중입니다)", Gesture.Normalize(key)), BalloonIcon.Warning);
    }

    /// <summary>칠판이 하나라도 열려 있으면 칠판 클릭 통과 키를 전역 단축키로 등록하고, 모두 닫히면 해제한다.</summary>
    internal void UpdateChalkHotkey(EditorWindow? closing = null)
    {
        if (_hotkeys == null) return;
        var any = Windows.OfType<EditorWindow>().Any(w => w.IsChalkboard && w != closing);
        if (any == _chalkHotkeyOn) return;
        if (!any) { _hotkeys.Unregister(HotkeyIdChalk); _chalkHotkeyOn = false; return; }
        var key = _settings.ChalkPassHotkey;
        if (string.IsNullOrEmpty(key)) return;
        _chalkHotkeyOn = _hotkeys.Register(HotkeyIdChalk, key, () =>
        {
            foreach (var w in Windows.OfType<EditorWindow>().Where(w => w.IsChalkboard).ToList()) w.ToggleChalkPass();
        });
        if (!_chalkHotkeyOn) Notify(L.F("단축키 등록 실패: {0} (다른 프로그램이 이미 사용 중입니다)", key), BalloonIcon.Warning);
    }

    private void BuildTrayIcon()
    {
        _trayIcon = new TaskbarIcon { ToolTipText = L.T("ESCaptureSketch — 캡처하고 바로 스케치"), Icon = MakeTrayIcon() };
        _trayIcon.TrayLeftMouseUp += (_, _) => _ = StartCaptureAsync();
        _trayIcon.TrayRightMouseDown += (_, _) => RebuildMenu();
        RebuildMenu();
    }

    private void RebuildMenu()
    {
        if (_trayIcon == null) return;
        var menu = new ContextMenu();

        menu.Items.Add(new MenuItem { Header = AppInfo.Display, IsEnabled = false });
        menu.Items.Add(new Separator());

        var capture = new MenuItem { Header = L.F("캡처 ({0})", Gesture.Normalize(_settings.CaptureHotkey)) };
        capture.Click += (_, _) => _ = StartCaptureAsync();
        menu.Items.Add(capture);

        var board = new MenuItem { Header = L.T("빈 화이트보드") };
        board.Click += (_, _) => OpenWhiteboard();
        menu.Items.Add(board);

        var paste = new MenuItem { Header = L.F("클립보드 이미지로 편집 ({0})", Gesture.Normalize(_settings.PasteHotkey)) };
        paste.Click += (_, _) =>
        {
            var d = DocumentFromClipboard();
            if (d == null) Notify(L.T("클립보드에 이미지가 없습니다."), BalloonIcon.Info);
            else OpenDocument(d, L.T("ESCaptureSketch — 붙여넣기"));
        };
        menu.Items.Add(paste);

        var pin = new MenuItem { Header = L.F("클립보드 이미지 띄우기 ({0})", Gesture.Normalize(_settings.PinImageHotkey)) };
        pin.Click += (_, _) => ShowClipboardImage();
        menu.Items.Add(pin);

        var cfg = new MenuItem { Header = L.T("설정") };
        cfg.Click += (_, _) => OpenSettings();
        menu.Items.Add(cfg);

        var manual = new MenuItem { Header = L.T("사용 설명서") };
        manual.Click += (_, _) => ManualWindow.ShowSingle();
        menu.Items.Add(manual);

        menu.Items.Add(new Separator());

        var openData = new MenuItem { Header = L.T("설정 폴더 열기") };
        openData.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppPaths.DataDir) { UseShellExecute = true }); }
            catch { }
        };
        menu.Items.Add(openData);

        var exit = new MenuItem { Header = L.T("종료") };
        exit.Click += (_, _) => ExitApp();
        menu.Items.Add(exit);

        _trayIcon.ContextMenu = menu;
    }

    /// <summary>트레이 아이콘: 프로그램 아이콘. 못 읽으면 Windows 기본 아이콘.</summary>
    private static Icon MakeTrayIcon() => AppIcon.LoadTray() ?? SystemIcons.Application;

    // ---- 캡처 흐름 ----

    internal async Task StartCaptureAsync()
    {
        if (_busy) return;
        _busy = true;

        try
        {
            using var shot = ScreenShot.CaptureVirtualScreen();
            var result = await CaptureSession.SelectAsync(shot, _settings.OpenAfterCapture, DelaySeconds);

            // 오버레이에서 체크 상태를 바꿨으면 다음에도 유지한다.
            // 취소해도 체크를 바꿨다면 그 상태를 저장한다.
            if (result.OpenAfter != _settings.OpenAfterCapture)
            {
                _settings.OpenAfterCapture = result.OpenAfter;
                _settings.Save();
            }

            switch (result.Kind)
            {
                case CaptureKind.Cancelled:
                    return;

                case CaptureKind.Whiteboard:
                    OpenWhiteboard();
                    return;

                case CaptureKind.Load:
                    // 대화 상자는 캡처 흐름이 끝난 뒤에 띄운다 (열어 둔 동안에도 캡처 단축키가 먹게).
                    _ = Dispatcher.BeginInvoke(new Action(ChooseFile));
                    return;

                case CaptureKind.Chalkboard:
                    OpenChalkboard(result.Region, shot.CropToSkia(result.Region));
                    return;

                case CaptureKind.Region:
                    if (_settings.SoundOnCapture) ShutterSound.Play(_settings.ShutterVolume);
                    DeliverCapture(shot, result.Region, result.OpenAfter);
                    return;

                case CaptureKind.Delayed:
                {
                    // 모드 바가 닫힌 채 기다렸다가 그때의 화면을 새로 찍는다. 기다리는 동안 다른 창을 자유롭게 다룰 수 있다.
                    await Task.Delay(TimeSpan.FromSeconds(DelaySeconds));
                    using var later = ScreenShot.CaptureVirtualScreen();
                    if (_settings.SoundOnDelayedCapture) ShutterSound.Play(_settings.ShutterVolume);
                    // 시간이 다 된 순간 커서가 있는 모니터 전체.
                    DeliverCapture(later, System.Windows.Forms.Screen.FromPoint(CaptureSession.CursorPosition()).Bounds, result.OpenAfter);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("캡처 실패: " + ex);
            Notify(L.T("캡처 중 문제가 생겼습니다: ") + ex.Message, BalloonIcon.Error);
        }
        finally
        {
            _busy = false;
            MemoryTrimmer.ScheduleTrim(TimeSpan.FromSeconds(10));
        }
    }

    private int DelaySeconds => Math.Clamp(_settings.CaptureDelaySeconds, 1, 30);

    /// <summary>찍은 화면의 region 을 편집창으로 열거나 클립보드에 넣는다.</summary>
    private void DeliverCapture(ScreenShot shot, System.Drawing.Rectangle region, bool openAfter)
    {
        var image = shot.CropToSkia(region);
        if (image == null) return;
        if (openAfter)
        {
            OpenEditor(Document.WithImage(image), "ESCaptureSketch");
            return;
        }

        // 기본: 클립보드에만 넣고 창은 열지 않는다.
        var copied = CopyToClipboard(image);
        image.Dispose();
        // 복사 성공 알림은 설정에서 끌 수 있다. 실패 알림은 항상 보인다.
        if (!copied) Notify(L.T("클립보드가 사용 중이라 복사하지 못했습니다."), BalloonIcon.Warning);
        else if (_settings.NotifyAfterCapture) Notify(L.F("클립보드에 복사했습니다 ({0}×{1})", region.Width, region.Height), BalloonIcon.Info);
    }

    internal void OpenSettings() => SettingsWindow.ShowSingle(_settings, () => { RefreshHotkey(); RebuildMenu(); });

    internal void OpenDocument(Document doc, string title) => OpenEditor(doc, title);

    internal static string OpenFilter => L.T("이미지·ESCS|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.escs|모든 파일|*.*");

    /// <summary>파일(이미지·.escs)을 편집창으로 연다. 실패하면 이유를 돌려준다.</summary>
    internal string? OpenFileInEditor(string path)
    {
        try
        {
            var doc = path.EndsWith(FileFormat.Extension, StringComparison.OrdinalIgnoreCase)
                ? FileFormat.Load(path)
                : FileFormat.FromImageFile(path);
            OpenEditor(doc, "ESCaptureSketch — " + Path.GetFileName(path));
            return null;
        }
        catch (Exception ex)
        {
            Log.Write("열기 실패: " + ex);
            return ex.Message;
        }
    }

    /// <summary>캡처 메뉴의 불러오기: 편집창이 없는 상태에서 파일 고르기 창을 띄운다.</summary>
    private void ChooseFile()
    {
        // 소유 창이 없으면 고르기 창이 다른 프로그램 뒤에 뜰 수 있어, 보이지 않는 맨 앞 창을 잠깐 소유자로 둔다.
        var owner = new Window { Width = 1, Height = 1, WindowStyle = WindowStyle.None, ShowInTaskbar = false, Topmost = true, Opacity = 0, AllowsTransparency = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen };
        try
        {
            owner.Show();
            owner.Activate();
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = OpenFilter, InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) };
            if (dlg.ShowDialog(owner) != true) return;
            if (OpenFileInEditor(dlg.FileName) is { } error) Notify(L.T("열지 못했습니다: ") + error, BalloonIcon.Warning);
        }
        finally { owner.Close(); }
    }

    /// <summary>클립보드의 이미지를 편집용 문서로. 이미지가 없으면 null.</summary>
    internal static Document? DocumentFromClipboard()
    {
        var image = ImageFromClipboard();
        return image == null ? null : Document.WithImage(image);
    }

    /// <summary>잘라내기가 넣는 투명 포함 PNG 의 클립보드 형식 이름. 다른 프로그램이 클립보드를 바꾸면 함께 사라진다.</summary>
    internal const string OwnImageFormat = "ESCaptureSketch.PNG";

    /// <summary>클립보드의 이미지를 SKImage 로. 이미지가 없으면 null.</summary>
    internal static SKImage? ImageFromClipboard()
    {
        try
        {
            // 이 프로그램에서 잘라낸 것은 투명을 살려 읽는다.
            if (System.Windows.Clipboard.ContainsData(OwnImageFormat) && System.Windows.Clipboard.GetData(OwnImageFormat) is MemoryStream own)
            {
                var bytes = own.ToArray();
                using (var codec = SKCodec.Create(new SKMemoryStream(bytes)))
                    if (codec == null || (long)codec.Info.Width * codec.Info.Height > 100_000_000) { Log.Write("클립보드 이미지가 너무 크거나 읽을 수 없어 건너뜀"); return null; }
                using var decoded = SKBitmap.Decode(bytes);
                if (decoded != null) return SKImage.FromBitmap(decoded);
            }

            if (!System.Windows.Clipboard.ContainsImage()) return null;
            var src = System.Windows.Clipboard.GetImage();
            if (src == null) return null;

            if ((long)src.PixelWidth * src.PixelHeight > 100_000_000) { Log.Write("클립보드 이미지가 너무 커서 건너뜀"); return null; }   // 메모리 보호
            var conv = new System.Windows.Media.Imaging.FormatConvertedBitmap(src, System.Windows.Media.PixelFormats.Pbgra32, null, 0);
            var stride = conv.PixelWidth * 4;
            var buf = new byte[stride * conv.PixelHeight];
            conv.CopyPixels(buf, stride, 0);

            var info = new SKImageInfo(conv.PixelWidth, conv.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(buf, System.Runtime.InteropServices.GCHandleType.Pinned);
            try { return SKImage.FromPixelCopy(info, handle.AddrOfPinnedObject(), stride); }
            finally { handle.Free(); }
        }
        catch (Exception ex) { Log.Write("클립보드 이미지 읽기 실패: " + ex.Message); return null; }
    }

    private static bool CopyToClipboard(SKImage image)
    {
        using var bmp = SKBitmap.FromImage(image);
        var source = EditorWindow.ToBitmapSource(bmp);
        for (var i = 0; i < 6; i++)
        {
            try { System.Windows.Clipboard.SetImage(source); return true; }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(60); }
        }
        return false;
    }

    /// <summary>전역 붙여넣기 단축키: 클립보드 이미지를 편집창으로 연다.</summary>
    private void PasteFromClipboard()
    {
        var d = DocumentFromClipboard();
        if (d == null) { Notify(L.T("클립보드에 이미지가 없습니다."), BalloonIcon.Info); return; }
        OpenDocument(d, L.T("ESCaptureSketch — 붙여넣기"));
    }

    /// <summary>클립보드 이미지만 담은 창을 띄운다 (누를 때마다 새 창).</summary>
    private void ShowClipboardImage()
    {
        using var image = ImageFromClipboard();
        if (image == null) { Notify(L.T("클립보드에 이미지가 없습니다."), BalloonIcon.Info); return; }
        using var bmp = SKBitmap.FromImage(image);
        var w = new PinWindow(EditorWindow.ToBitmapSource(bmp), _settings.PinImageBorder);   // 화소를 복사해 두므로 bmp 는 바로 해제해도 된다
        w.Show();
        w.Activate();
    }

    private void OpenWhiteboard() => OpenEditor(new Document { Whiteboard = true }, L.T("ESCaptureSketch — 화이트보드"));

    private void OpenChalkboard(System.Drawing.Rectangle screen, SKImage? backdrop)
    {
        var doc = new Document { Chalkboard = true, Backdrop = backdrop };
        var w = new EditorWindow(doc, _settings, L.T("ESCaptureSketch — 칠판"), screen);
        w.Show();
        w.Activate();
    }

    private void OpenEditor(Document doc, string title)
    {
        var w = new EditorWindow(doc, _settings, title);
        w.Show();
        w.Activate();
    }

    internal void Notify(string message, BalloonIcon icon)
        => _trayIcon?.ShowBalloonTip("ESCaptureSketch", message, icon);

    private void ExitApp()
    {
        foreach (var w in Windows.OfType<EditorWindow>().ToList()) w.Close();
        _hotkeys?.Dispose();
        _trayIcon?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _settings.FlushPending();
        _exitEvent?.Dispose();
        _captureEvent?.Dispose();
        _openEvent?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}

/// <summary>입력 장치 없이 검증하기 위한 개발용 명령. README 에 적어 둔다.</summary>
internal abstract class DevCommands
{
    public abstract Task RunAsync(App app, AppSettings settings);

    public static DevCommands? TryParse(string[] args)
    {
        // 자동 시작 시험은 시험용 항목 이름을 준 경우에만 (이름 없이 실행하면 실제 자동 시작이 켜진다).
        if (args.Contains("--autostarttest") && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ESCAPTURESKETCH_AUTOSTART_NAME"))) return new AutoStartTestCommand();

        var si = Array.IndexOf(args, "--dumpset");
        if (si >= 0 && si + 1 < args.Length) return new SettingsDumpCommand(args[si + 1]);

        var bi = Array.IndexOf(args, "--dumpbg");
        if (bi >= 0 && bi + 1 < args.Length) return new BgCommand(args[bi + 1]);

        var i = Array.IndexOf(args, "--dump");
        if (i >= 0 && i + 1 < args.Length) return new DumpCommand(args[i + 1], window: false);

        i = Array.IndexOf(args, "--dumpwin");
        if (i >= 0 && i + 1 < args.Length) return new DumpCommand(args[i + 1], window: true);

        i = Array.IndexOf(args, "--dumpboard");
        if (i >= 0 && i + 1 < args.Length) return new BoardCommand(args[i + 1]);

        i = Array.IndexOf(args, "--overlaytest");
        if (i >= 0 && i + 1 < args.Length) return new OverlayTestCommand(args[i + 1]);

        return null;
    }

    /// <summary>--autostarttest: 시험용 항목 이름(ESCAPTURESKETCH_AUTOSTART_NAME)으로 자동 시작 켜기/끄기를 확인한다.</summary>
    private sealed class AutoStartTestCommand : DevCommands
    {
        public override Task RunAsync(App app, AppSettings settings)
        {
            var before = AutoStart.IsEnabled();
            var on = AutoStart.Set(true); var afterOn = AutoStart.IsEnabled();
            Log.Write($"[autostart] 이름 {AutoStart.EntryName}, 처음 {before}, 켜기 {on}->{afterOn}");
            // 끄기는 일부러 하지 않는다: 정리용 .bat 으로 지워 보는 시험을 이어서 한다.
            app.Shutdown();
            return Task.CompletedTask;
        }
    }

    /// <summary>--dumpset &lt;png&gt;: 설정 창을 띄워 내용을 PNG 로 저장한다.</summary>
    private sealed class SettingsDumpCommand(string path) : DevCommands
    {
        public override async Task RunAsync(App app, AppSettings settings)
        {
            try
            {
                SettingsWindow.ShowSingle(settings, () => { });
                await Task.Delay(1200);
                var w = app.Windows.OfType<SettingsWindow>().First();
                if (int.TryParse(Environment.GetEnvironmentVariable("ESCAPTURESKETCH_DEBUG_TAB"), out var tab)) { w.SelectTab(tab); await Task.Delay(500); }
                // 개발용: 설정 창이 열린 채 테마를 바꿔 보는 시험 (예: ESCAPTURESKETCH_DEBUG_THEME=Light → Dark → Light)
                if (Environment.GetEnvironmentVariable("ESCAPTURESKETCH_DEBUG_THEME") is { Length: > 0 } themes)
                    foreach (var th in themes.Split(',')) { ThemeService.Apply(th); await Task.Delay(300); }
                var content = (FrameworkElement)w.Content;
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(w.Background, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
                    dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
                }
                rtb.Render(dv);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                using (var fs = File.Create(path)) enc.Save(fs);
                Log.Write("[dumpset] 저장 " + w.RowSummary());
                w.Close();
            }
            catch (Exception ex) { Log.Write("[dumpset] 실패 " + ex); }
            finally { app.Shutdown(); }
        }
    }

    /// <summary>--dumpboard &lt;png&gt;: 기본 모니터에 칠판을 띄우고 선 몇 개를 그린 뒤, 화면에 실제로 보이는 모습(투명 합성 포함)을 PNG 로 저장한다.</summary>
    private sealed class BoardCommand(string path) : DevCommands
    {
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, int rop);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

        public override async Task RunAsync(App app, AppSettings settings)
        {
            try
            {
                var screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
                using var shot = ScreenShot.CaptureVirtualScreen();
                var doc = new Document { Chalkboard = true, Backdrop = shot.CropToSkia(screen) };
                var w = new EditorWindow(doc, settings, "board", screen);
                w.Show();
                await Task.Delay(1200);
                doc.Items.Add(new Item { Kind = ToolKind.Pen, Color = SKColors.Red, Width = 8, Layer = doc.Active, Pts = { new SKPoint(-300, -100), new SKPoint(0, 80), new SKPoint(300, -100) } });
                doc.Items.Add(new Item { Kind = ToolKind.Ellipse, Color = SKColors.Blue, Width = 6, Layer = doc.Active, Pts = { new SKPoint(-200, 100), new SKPoint(200, 220) } });
                doc.RaiseLayersChanged();
                await Task.Delay(800);
                using var bmp = new System.Drawing.Bitmap(screen.Width, screen.Height);
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    var hdcDst = g.GetHdc();
                    var hdcSrc = GetDC(IntPtr.Zero);
                    BitBlt(hdcDst, 0, 0, screen.Width, screen.Height, hdcSrc, screen.Left, screen.Top, 0x00CC0020 | 0x40000000);   // SRCCOPY | CAPTUREBLT: 투명(레이어드) 창도 포함
                    ReleaseDC(IntPtr.Zero, hdcSrc);
                    g.ReleaseHdc(hdcDst);
                }
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                var o1 = doc.BackdropOrigin!();
                w.DebugToggleChalkMenu();
                await Task.Delay(300);
                var o2 = doc.BackdropOrigin!();
                Log.Write($"[dumpboard] 숨기기 전 원점 {o1}, 후 {o2}");
                await Task.Delay(600);
                using (var bmp2 = new System.Drawing.Bitmap(screen.Width, screen.Height))
                {
                    using (var g2 = System.Drawing.Graphics.FromImage(bmp2))
                    {
                        var d2 = g2.GetHdc(); var s2 = GetDC(IntPtr.Zero);
                        BitBlt(d2, 0, 0, screen.Width, screen.Height, s2, screen.Left, screen.Top, 0x00CC0020 | 0x40000000);
                        ReleaseDC(IntPtr.Zero, s2); g2.ReleaseHdc(d2);
                    }
                    bmp2.Save(path + ".hidden.png", System.Drawing.Imaging.ImageFormat.Png);
                }
                using var ex = Renderer.Export(doc);
                if (ex != null) { using var img = SKImage.FromBitmap(ex); using var d = img.Encode(SKEncodedImageFormat.Png, 100); File.WriteAllBytes(path + ".exp.png", d.ToArray()); }
                Log.Write($"[dumpboard] 창 {w.Left},{w.Top} {w.ActualWidth}x{w.ActualHeight} 화면 {screen}, 내보내기 {(ex == null ? "없음" : ex.Width + "x" + ex.Height + " alpha=" + ex.AlphaType + " 모서리=" + ex.GetPixel(0, 0))}");
                w.Close();
            }
            catch (Exception e) { Log.Write("[dumpboard] 실패 " + e); }
            finally { app.Shutdown(); }
        }
    }

    /// <summary>--dumpbg &lt;png&gt;: 800x500 테두리 이미지를 배경으로 편집창을 띄워 배치를 확인한다.</summary>
    private sealed class BgCommand(string path) : DevCommands
    {
        public override async Task RunAsync(App app, AppSettings settings)
        {
            try
            {
                using var bmp = new SKBitmap(800, 500);
                using (var c = new SKCanvas(bmp))
                {
                    c.Clear(SKColors.LightSkyBlue);
                    using var p = new SKPaint { Color = SKColors.Red, Style = SKPaintStyle.Stroke, StrokeWidth = 8 };
                    c.DrawRect(4, 4, 792, 492, p);
                    c.DrawLine(0, 0, 800, 500, p); c.DrawLine(800, 0, 0, 500, p);
                }
                var bgDoc = new Document { Background = SKImage.FromBitmap(bmp) };
                if (Environment.GetEnvironmentVariable("ESCAPTURESKETCH_DEBUG_GRID") == "1")
                {
                    if (int.TryParse(Environment.GetEnvironmentVariable("ESCAPTURESKETCH_DEBUG_GRIDSTYLE"), out var gs)) bgDoc.GridStyle = gs;
                    using var before = Renderer.Export(bgDoc)!;
                    if (Environment.GetEnvironmentVariable("ESCAPTURESKETCH_DEBUG_GRIDRANGE") == "1")
                    {
                        bgDoc.AddGridLayer(default, bgDoc.Background!.Width, bgDoc.Background.Height);
                        using var banded = Renderer.Export(bgDoc)!;
                        using (var img0 = SKImage.FromBitmap(banded)) using (var enc0 = img0.Encode(SKEncodedImageFormat.Png, 100)) File.WriteAllBytes(path + ".band.png", enc0.ToArray());
                        bgDoc.CropTo(new SKRectI(100, 50, 700, 450));
                        using var cut = Renderer.Export(bgDoc)!;
                        var o = bgDoc.Layers.First(l => l.IsGrid);
                        Log.Write($"[grid] 띠 포함 {banded.Width}x{banded.Height}, 자른 뒤 {cut.Width}x{cut.Height}, 격자 원점 {o.OriginX},{o.OriginY}");
                        using (var img1 = SKImage.FromBitmap(cut)) using (var enc1 = img1.Encode(SKEncodedImageFormat.Png, 100)) File.WriteAllBytes(path + ".cut.png", enc1.ToArray());
                        bgDoc.Undo();
                        Log.Write($"[grid] 되돌린 뒤 격자 원점 {o.OriginX},{o.OriginY}");
                        bgDoc.Undo();
                    }
                    bgDoc.AddGridLayer();         // 선택된 층(0번) 바로 위에 격자 층이 생긴다
                    using var after = Renderer.Export(bgDoc)!;
                    using (var img = SKImage.FromBitmap(after)) using (var enc = img.Encode(SKEncodedImageFormat.Png, 100)) File.WriteAllBytes(path + ".exp.png", enc.ToArray());
                    Log.Write($"[grid] 격자 층 {bgDoc.HasGrid}, 층 {bgDoc.Layers.Count}개, 맨 위={bgDoc.Layers[^1].Name}, 내보내기 달라짐(격자가 이미지 위로 와서 보임)={!before.GetPixelSpan().SequenceEqual(after.GetPixelSpan())}");
                    var path3 = path + ".grid.escs";
                    FileFormat.Save(bgDoc, path3);
                    using var re = FileFormat.Load(path3);
                    Log.Write($"[grid] 저장·열기 뒤 격자 층 {re.HasGrid}, 순서 {string.Join(",", re.Layers.Select(l => l.Name))}");
                    File.Delete(path3);
                }
                var w = new EditorWindow(bgDoc, settings, "bg");
                w.Show();
                await Task.Delay(1500);
                w.DumpWindow(path);
                Log.Write("[dumpbg] " + w.CanvasInfo());
                w.Close();
            }
            catch (Exception ex) { Log.Write("[dumpbg] 실패 " + ex); }
            finally { app.Shutdown(); }
        }
    }

    /// <summary>--overlaytest &lt;폴더&gt;: 오버레이를 잠깐 띄워 사각형·창 모드 화면을 저장하고 잘라내기를 확인한 뒤 취소한다.</summary>
    private sealed class OverlayTestCommand(string dir) : DevCommands
    {
        public override async Task RunAsync(App app, AppSettings settings)
        {
            try
            {
                Directory.CreateDirectory(dir);
                using var shot = ScreenShot.CaptureVirtualScreen();
                var windows = WindowFinder.Snapshot();
                Log.Write($"[overlaytest] 가상화면 {shot.Bounds}, 최상위 창 {windows.Count}개, 첫 창 {(windows.Count > 0 ? windows[0].ToString() : "-")}");

                using (var img = shot.CropToSkia(new System.Drawing.Rectangle(shot.Bounds.X, shot.Bounds.Y, 400, 300)))
                using (var data = img!.Encode(SKEncodedImageFormat.Png, 100))
                {
                    File.WriteAllBytes(Path.Combine(dir, "crop.png"), data.ToArray());
                    Log.Write($"[overlaytest] 잘라내기 {img.Width}x{img.Height}");
                }

                var task = CaptureSession.SelectAsync(shot, out var session);
                await Task.Delay(800);
                session.DumpOverlay(Path.Combine(dir, "overlay_rect.png"));
                session.PickMode(CaptureMode.Window);
                await Task.Delay(500);
                session.DumpOverlay(Path.Combine(dir, "overlay_window.png"));
                session.Cancel();
                var result = await task;
                Log.Write($"[overlaytest] 결과 {result.Kind}");

                var t4 = CaptureSession.SelectAsync(shot, out var s4); await Task.Delay(400); s4.PickMode(CaptureMode.Delayed); var r4 = await t4;
                Log.Write($"[overlaytest] 지연 캡처 선택 {r4.Kind}, {ShutterSound.Check()}");

                // 체크 상태가 결과에 담기는지: 켠 채 취소 / 세션 안에서 토글 후 취소 / 전체화면 확정
                var t1 = CaptureSession.SelectAsync(shot, true, out var s1); await Task.Delay(400); s1.Cancel(); var r1 = await t1;
                var t2 = CaptureSession.SelectAsync(shot, false, out var s2); await Task.Delay(400); s2.ToggleOpenAfter(); s2.Cancel(); var r2 = await t2;
                var t3 = CaptureSession.SelectAsync(shot, true, out var s3); await Task.Delay(400); s3.PickMode(CaptureMode.FullScreen); var r3 = await t3;
                Log.Write($"[overlaytest] 켠 채 취소 OpenAfter={r1.OpenAfter}, 토글 후 취소 OpenAfter={r2.OpenAfter}, 전체화면 OpenAfter={r3.OpenAfter}/{r3.Kind}");
            }
            catch (Exception ex) { Log.Write("[overlaytest] 실패 " + ex); }
            finally { app.Shutdown(); }
        }
    }

    /// <summary>
    /// --dump &lt;png&gt;: 합성 스트로크를 가진 화이트보드를 내보내 PNG 로 저장하고 시간을 로그에 남긴다.
    /// --dumpwin &lt;png&gt;: 같은 문서로 편집창을 띄워 창 내용을 PNG 로 저장한다.
    /// </summary>
    private sealed class DumpCommand(string path, bool window) : DevCommands
    {
        public override async Task RunAsync(App app, AppSettings settings)
        {
            try
            {
                var doc = BuildSample(2000);
                if (window && Environment.GetEnvironmentVariable("ESCAPTURESKETCH_DEBUG_IMG") == "1")
                {
                    // 개발용: 800x500 캡처 이미지 문서 (F11 에서 화면 가득 확대되는지 보는 시험)
                    using var bmp = new SKBitmap(800, 500);
                    using (var c = new SKCanvas(bmp))
                    {
                        c.Clear(new SKColor(250, 244, 220));
                        using var p = new SKPaint { Color = SKColors.SteelBlue, Style = SKPaintStyle.Stroke, StrokeWidth = 12 };
                        c.DrawRect(6, 6, 788, 488, p);
                        p.Style = SKPaintStyle.Fill; p.Color = SKColors.OrangeRed; c.DrawCircle(400, 250, 120, p);
                    }
                    doc = Document.WithImage(SKImage.FromBitmap(bmp));
                }

                if (!window)
                {
                    var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    using var bmp = Renderer.Export(doc)!;
                    var ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                    using var image = SKImage.FromBitmap(bmp);
                    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                    File.WriteAllBytes(path, data.ToArray());
                    Log.Write($"[dump] {bmp.Width}x{bmp.Height} 물체 {doc.Items.Count}개 내보내기 {ms:0.0}ms → {path}");

                    // .escs 저장 → 읽기 → 다시 내보내 같은 그림인지 대조한다.
                    var escs = path + ".escs";
                    doc.Layers[1].Name = "스탬프층";
                    doc.Clip = new SKRect(-500, -320, 500, 320);
                    using var clipped = Renderer.Export(doc)!;
                    FileFormat.Save(doc, escs);
                    using var loaded = FileFormat.Load(escs);
                    using var again = Renderer.Export(loaded)!;
                    var same = clipped.Width == again.Width && clipped.Height == again.Height && clipped.GetPixelSpan().SequenceEqual(again.GetPixelSpan());
                    Log.Write($"[roundtrip] 자르기 {clipped.Width}x{clipped.Height}, 레이어 {loaded.Layers.Count}({loaded.Layers[1].Name}), 물체 {loaded.Items.Count}, 같은 그림: {same}, 파일 {new FileInfo(escs).Length}B");
                    doc.Layers[1].Visible = false;
                    using var hidden = Renderer.Export(doc)!;
                    Log.Write($"[layer] 1층 숨김 후 내보내기 {hidden.Width}x{hidden.Height}, 달라짐: {!hidden.GetPixelSpan().SequenceEqual(clipped.GetPixelSpan())}");
                    // 화면 자르기: 0번 층을 영역으로 대체, 위층은 이동, 이전/다음으로 복구
                    doc.Clip = null; doc.Layers[1].Visible = true;
                    var n0 = doc.Items.Count; var l1 = doc.Items.Count(it => it.Layer == doc.Layers[1]);
                    doc.CropTo(new SKRectI(-300, -200, 300, 200));
                    using var cropped = Renderer.Export(doc)!;
                    var n1 = doc.Items.Count; var bgOk = doc.Background is { Width: 600, Height: 400 };
                    doc.Undo();
                    var n2 = doc.Items.Count; var firstX = doc.Items.First(it => it.Layer == doc.Layers[1]).Pts[0].X;
                    doc.Redo();
                    var firstX2 = doc.Items.First(it => it.Layer == doc.Layers[1]).Pts[0].X;
                    Log.Write($"[crop] 자른 뒤 내보내기 {cropped.Width}x{cropped.Height}, 물체 {n0}->{n1}(1층 {l1}), 새 바탕 600x400: {bgOk}, 되돌리면 {n2}개, 1층 첫 마커 x {firstX}->다시 {firstX2}");

                    // 레이어 순서: 겹치는 두 굵은 선, 위층이 위에 그려지는지 / 순서를 바꾸면 뒤집히는지 / 삭제 / 저장 왕복
                    var t = new Document { Whiteboard = true };
                    var la = t.AddLayer(); var lb = t.AddLayer();
                    t.Items.Add(new Item { Kind = ToolKind.Line, Color = SKColors.Red, Width = 40, Layer = la, Pts = { new SKPoint(-100, 0), new SKPoint(100, 0) } });
                    t.Items.Add(new Item { Kind = ToolKind.Line, Color = SKColors.Blue, Width = 40, Layer = lb, Pts = { new SKPoint(-100, 0), new SKPoint(100, 0) } });
                    string Center(Document d)
                    {
                        using var bm = Renderer.Export(d)!;
                        var c = bm.GetPixel(bm.Width / 2, bm.Height / 2);
                        return c.Red > 200 ? "빨강" : c.Blue > 200 ? "파랑" : $"?{c}";
                    }
                    var before = Center(t);                    // 위층(파랑)이 위
                    t.MoveLayer(2, 1);                          // 파랑층을 빨강층 아래로
                    var swapped = Center(t);
                    var path2 = path + ".order.escs";
                    FileFormat.Save(t, path2);
                    using var t2 = FileFormat.Load(path2);
                    var reloaded = Center(t2);
                    t.DeleteLayer(la);
                    var afterDel = Center(t);
                    Log.Write($"[layers] 처음 위={before}, 순서 바꾼 뒤 위={swapped}, 저장·열기 뒤 위={reloaded}, 위층 삭제 뒤={afterDel}, 레이어 {t.Layers.Count}개, 물체 {t.Items.Count}개, 바탕층 삭제 시도={t.DeleteLayer(t.BaseLayer)}");
                    File.Delete(path2);
                }
                else
                {
                    var w = new EditorWindow(doc, settings, "dump");
                    w.Width = 1200; w.Height = 800;
                    if (int.TryParse(Environment.GetEnvironmentVariable("ESCAPTURESKETCH_DEBUG_WIDTH"), out var dw) && dw > 300) w.Width = dw;   // 개발용: 좁은 창에서 도구 줄 배치를 보는 시험
                    w.Show();
                    await Task.Delay(1500);
                    // 개발용: 열린 상태(최대화 여부)를 로그로 남기고, DEBUG_MAX=1 이면 최대화한 채 닫아 저장되는지 본다
                    Log.Write($"[dumpwin] 열렸을 때 창 상태 {w.WindowState}");
                    if (Environment.GetEnvironmentVariable("ESCAPTURESKETCH_DEBUG_MAX") == "1") { w.WindowState = WindowState.Maximized; await Task.Delay(500); }
                    if (Environment.GetEnvironmentVariable("ESCAPTURESKETCH_DEBUG_GRIDADD") == "1") { w.DebugAddGrid(); await Task.Delay(300); }
                    if (Environment.GetEnvironmentVariable("ESCAPTURESKETCH_DEBUG_LASER") == "1") { w.DebugLaserDemo(); await Task.Delay(120); }
                    if (Environment.GetEnvironmentVariable("ESCAPTURESKETCH_DEBUG_PASTE") == "1")
                    {
                        // 개발용: 붙여넣기(그림 물체) 시험 — 한가운데에 놓이는지, 저장·열기 왕복에서 그림이 살아 있는지
                        using var pb = new SKBitmap(300, 200);
                        using (var pc = new SKCanvas(pb)) { pc.Clear(SKColors.LimeGreen); using var pp = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = 8 }; pc.DrawRect(4, 4, 292, 192, pp); }
                        w.DebugAddPicture(SKImage.FromBitmap(pb));
                        await Task.Delay(500);
                        var pi = w.DebugDoc.Items.Last();
                        var ptmp = path + ".paste.escs";
                        FileFormat.Save(w.DebugDoc, ptmp);
                        using var back = FileFormat.Load(ptmp);
                        File.Delete(ptmp);
                        // 크기 조절의 실행 취소: 점을 바꾸고 기록에 올린 뒤 취소/다시 실행으로 점이 돌아오는지
                        var pb0 = pi.Pts.ToArray();
                        pi.Pts[1] = new SKPoint(pi.Pts[1].X + 100, pi.Pts[1].Y + 50);
                        var pb1 = pi.Pts.ToArray();
                        w.DebugDoc.Push(new Command { Edits = { (pi, pb0, pb1) } });
                        w.DebugDoc.Undo(); var undone = pi.Pts[1] == pb0[1];
                        w.DebugDoc.Redo(); var redone = pi.Pts[1] == pb1[1];
                        Log.Write($"[paste] 크기 조절 취소 {undone}, 다시 실행 {redone}, 기록 {w.DebugDoc.HistoryCount}");
                        Log.Write($"[paste] 종류 {pi.Kind}, 위치 {pi.Pts[0].X},{pi.Pts[0].Y}, 크기 {pi.Picture?.Width}x{pi.Picture?.Height}, 저장·열기 뒤 그림 물체 {back.Items.Count(i => i.Kind == ToolKind.Image && i.Picture != null)}개");
                    }
                    if (Environment.GetEnvironmentVariable("ESCAPTURESKETCH_DEBUG_FULL") == "1") { var o0 = w.DebugOrigin(); w.DebugToggleFullView(); await Task.Delay(900); var o1 = w.DebugOrigin(); Log.Write($"[dumpwin] 월드 원점 화면 위치 {o0.X:0.#},{o0.Y:0.#} -> {o1.X:0.#},{o1.Y:0.#}"); Log.Write($"[dumpwin] F11 모드 창 상태 {w.WindowState}, 스타일 {w.WindowStyle}, 위치 {w.Left},{w.Top} 크기 {w.ActualWidth}x{w.ActualHeight}, 화면 {System.Windows.SystemParameters.PrimaryScreenWidth}x{System.Windows.SystemParameters.PrimaryScreenHeight}"); }
                    if (Environment.GetEnvironmentVariable("ESCAPTURESKETCH_DEBUG_DROP") == "1") w.DebugLayerDrop(1, 0);
                    w.DumpWindow(path);
                    Log.Write($"[bench] 전체 재그리기 {w.BenchFull():0.0}ms, 볼펜 구간 한 번 {w.BenchStroke():0.00}ms (1000회 평균)");
                    Log.Write($"[dumpwin] 저장 {path}, 캔버스 마지막 그리기 {w.LastPaintMs:0.0}ms (2000획, 캐시 재생성 포함)");
                    w.Close();
                }
            }
            catch (Exception ex) { Log.Write("[dump] 실패 " + ex); }
            finally { app.Shutdown(); }
        }

        private static Document BuildSample(int count)
        {
            var doc = new Document { Whiteboard = true };
            var rnd = new Random(7);

            for (var n = 0; n < count; n++)
            {
                var item = new Item
                {
                    Layer = doc.Layers[0],
                    Kind = n % 11 == 0 ? ToolKind.Highlighter : ToolKind.Pen,
                    Color = new SKColor((byte)rnd.Next(40, 230), (byte)rnd.Next(40, 230), (byte)rnd.Next(40, 230)),
                    Width = rnd.Next(2, 9),
                };
                var x = rnd.Next(-900, 900);
                var y = rnd.Next(-500, 500);
                var amp = rnd.Next(10, 60);
                for (var k = 0; k < 60; k++)
                    item.Pts.Add(new SKPoint(x + k * 3, y + MathF.Sin(k / 6f) * amp));
                doc.Items.Add(item);
            }

            doc.Items.Add(new Item { Layer = doc.Layers[0], Kind = ToolKind.Rect, Color = SKColors.Crimson, Width = 5, Pts = { new SKPoint(-300, -200), new SKPoint(100, 60) } });
            doc.Items.Add(new Item { Layer = doc.Layers[0], Kind = ToolKind.Ellipse, Color = SKColors.DodgerBlue, Width = 5, Pts = { new SKPoint(-100, -50), new SKPoint(300, 200) } });
            doc.Items.Add(new Item { Layer = doc.Layers[0], Kind = ToolKind.Arrow, Color = SKColors.Black, Width = 6, Pts = { new SKPoint(-400, 250), new SKPoint(200, -250) } });
            doc.Items.Add(new Item { Layer = doc.Layers[0], Kind = ToolKind.Line, Color = SKColors.DarkGreen, Width = 4, Pts = { new SKPoint(-400, -300), new SKPoint(400, 300) } });
            doc.AddLayer();
            doc.Items.Add(new Item { Kind = ToolKind.Stamp, Color = SKColors.Crimson, Width = 4, Stamp = 1, Layer = doc.Layers[1], Pts = { new SKPoint(-350, -250) } });
            doc.Items.Add(new Item { Kind = ToolKind.Stamp, Color = SKColors.RoyalBlue, Width = 4, Stamp = 12, Layer = doc.Layers[1], Pts = { new SKPoint(-250, -250), new SKPoint(-150, -180) } });
            for (var k = 0; k >= -4; k--)
                doc.Items.Add(new Item { Kind = ToolKind.Stamp, Color = SKColors.DarkOrange, Width = 4, Stamp = k, Layer = doc.Layers[1], Pts = { new SKPoint(-350 + (-k) * 70, -120) } });
            doc.Items.Add(new Item { Kind = ToolKind.Text, Color = SKColors.Black, Width = 6, Layer = doc.Layers[1], Text = "한글 text 테스트" + (char)10 + "둘째 줄 ABC", Pts = { new SKPoint(-350, 20) } });
            return doc;
        }
    }
}
