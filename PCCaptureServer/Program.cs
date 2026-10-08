using System.Drawing;
using System.Globalization;
using System.IO;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;

// 여러 창 꺼내기 + 개별 캡처 + 꺼낸 창 직접 클릭(PostMessage).
// 8080: 제어 / 8081: 창 캡처
class Program
{
    [DllImport("user32.dll")]
    static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, IntPtr dwExtraInfo);
    const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    const uint MOUSEEVENTF_LEFTUP = 0x0004;

    // ─────────────────────────────────────────────────────────────────────────
    // DPI 인식 (고해상도 노트북에서 AR 창이 잘리는 문제)
    //
    // 윈도우 화면 배율이 100%가 아닌 PC(노트북 대부분이 150~200%)에서,
    // DPI를 모르는 프로그램에게 윈도우는 "축소된 가짜 좌표"를 알려준다.
    //   GetWindowRect  → 1440x900 이라고 대답   (거짓말, 실제는 2880x1800)
    //   PrintWindow    → 2880x1800 짜리로 그림  (진짜 크기)
    // 그래서 작은 그릇에 큰 그림을 부어 가장자리가 잘려나간다.
    //
    // 아래 한 줄로 "나는 진짜 픽셀을 다룬다"고 선언하면 둘 다 실제 크기가 된다.
    // 반드시 창이나 그래픽을 만지기 전, Main 맨 처음에 호출해야 한다.
    // ─────────────────────────────────────────────────────────────────────────
    [DllImport("user32.dll")]
    static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    static readonly IntPtr DPI_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

    [DllImport("shcore.dll")]
    static extern int SetProcessDpiAwareness(int value);   // 구형 윈도우용 예비

    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();               // 더 구형 윈도우용 예비

    static void EnableDpiAwareness()
    {
        try { if (SetProcessDpiAwarenessContext(DPI_PER_MONITOR_AWARE_V2)) return; } catch { }
        try { if (SetProcessDpiAwareness(2) == 0) return; } catch { }   // 2 = PerMonitor
        try { SetProcessDPIAware(); } catch { }
    }

    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int nIndex);
    const int SM_CXSCREEN = 0;
    const int SM_CYSCREEN = 1;
    // 모든 모니터를 합친 가상 화면 영역
    const int SM_XVIRTUALSCREEN = 76;
    const int SM_YVIRTUALSCREEN = 77;
    const int SM_CXVIRTUALSCREEN = 78;
    const int SM_CYVIRTUALSCREEN = 79;

    [DllImport("user32.dll")]
    static extern bool BringWindowToTop(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")]
    static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")]
    static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);
    const uint GA_ROOT = 2;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    // ===== 다중 모니터 열거 =====
    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }
    delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);
    [DllImport("user32.dll")]
    static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    class Mon { public int X, Y, W, H; }
    static readonly List<Mon> monitors = new();

    static void EnumerateMonitors()
    {
        monitors.Clear();
        MonitorEnumProc cb = (IntPtr h, IntPtr hdc, ref RECT r, IntPtr d) =>
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(h, ref mi))
            {
                var m = new Mon
                {
                    X = mi.rcMonitor.Left,
                    Y = mi.rcMonitor.Top,
                    W = mi.rcMonitor.Right - mi.rcMonitor.Left,
                    H = mi.rcMonitor.Bottom - mi.rcMonitor.Top
                };
                bool primary = (mi.dwFlags & 1u) != 0;   // MONITORINFOF_PRIMARY
                if (primary) monitors.Insert(0, m); else monitors.Add(m);   // 주 모니터 = index 0
            }
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
        // [AR LAPTOP] 주 모니터 하나만 쓴다 (보조 모니터가 꽂혀 있어도 무시)
        if (monitors.Count > 1) monitors.RemoveRange(1, monitors.Count - 1);
        if (monitors.Count == 0)
            monitors.Add(new Mon { X = 0, Y = 0, W = screenWidth, H = screenHeight });
    }

    // u,v(0~1) + 모니터 index → 가상 데스크탑 절대 픽셀 좌표 (오프셋 포함)
    static void MapToMonitor(int mon, float u, float v, out int x, out int y)
    {
        if (mon < 0 || mon >= monitors.Count) mon = 0;
        var m = monitors[mon];
        x = m.X + (int)(u * m.W);
        y = m.Y + (int)(v * m.H);
    }

    // 접속 시 Unity에 모니터 목록 전송: "monitors,N,x,y,w,h,..."
    static async Task SendMonitorInfo(WebSocket ws)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("monitors,").Append(monitors.Count);
        foreach (var m in monitors)
            sb.Append(',').Append(m.X).Append(',').Append(m.Y).Append(',').Append(m.W).Append(',').Append(m.H);
        var bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
        try { await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, default); } catch { }
    }
    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int X, int Y, int cx, int cy, uint flags);
    const uint SWP_NOMOVE = 0x0002;
    const uint SWP_NOSIZE = 0x0001;
    const uint SWP_NOZORDER = 0x0004;
    const uint SWP_NOACTIVATE = 0x0010;   // 활성화 안 함 (스냅 우회에 도움)
    const uint SWP_ASYNCWINDOWPOS = 0x4000;
    const uint SWP_FRAMECHANGED = 0x0020; // GWL_EXSTYLE 변경을 확실히 반영시킴
    const uint SWP_NOSENDCHANGING = 0x0400; // WM_WINDOWPOSCHANGING 안 보냄 → 앱이 크기 되돌리는 클램프 우회
    static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    const int SW_RESTORE = 9;
    const int SW_SHOWMAXIMIZED = 3;   // GetWindowPlacement.showCmd 가 이 값이면 최대화 상태

    // 창의 "배치"(위치 + 크기 + 최대화/보통/최소화 상태)를 통째로 저장/복원.
    // 서버 시작 전 상태로 되돌리기 위해 첫 조작 순간에 스냅샷을 떠둔다.
    [StructLayout(LayoutKind.Sequential)]
    struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }
    [DllImport("user32.dll")]
    static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);
    [DllImport("user32.dll")]
    static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
    const uint PW_RENDERFULLCONTENT = 2;   // DWM 합성 창(크롬 등)까지 제대로 찍기 위한 플래그

    // 창별 캡처 상태 - 검은 글리치 프레임을 걸러내(플리커 억제) 직전 좋은 화면을 유지하기 위함.
    class CapState { public double lastBlackRatio; public int skipped; public ulong lastHash; public int sameCount; }

    // ── 전송량 줄이기 ─────────────────────────────────────────────────────────
    // 창 하나를 원본 해상도 · 품질 95로 초당 30장 보내면 초당 10~18MB가 된다.
    // USB(adb reverse)로도 벅차고, 퀘스트가 그걸 매 프레임 JPEG 디코드하느라 느려진다.
    //  1) 긴 변을 MaxCaptureSide 로 줄여서 보낸다 (비율은 유지 → 쿼드 모양 안 변함)
    //  2) 품질을 조금 낮춘다
    //  3) 화면이 그대로면 아예 안 보낸다 (정지된 창은 대역폭 0)
    // ──────────────────────────────────────────────────────────────────────────
    // 1초마다 "실제로 몇 장 · 몇 KB 보냈는지"를 찍는다. 흰 쿼드가 나올 때
    // PC가 안 보내는 건지, 퀘스트가 못 받는 건지 바로 구분된다.
    static int statFrames, statSkipped, statClients;
    static long statBytes;
    static long statTick;

    // 보는 게 우선이라 해상도는 거의 안 줄인다. 1920이면 웬만한 창은 원본 그대로 간다.
    // 그래도 느리면 콘솔에서  cap 1440  /  cap 1280  으로 낮출 수 있다 (0 = 축소 안 함).
    static int MaxCaptureSide = 0;      // 0 = 축소 안 함(원본 해상도 그대로)
    static long JpegQualityVar = 95L;
    const int MaxSameSkip = 30;      // 안 바뀌어도 이만큼 지나면 한 번은 보낸다(보험)

    static ulong QuickHash(byte[] d)
    {
        ulong h = 1469598103934665603UL;
        int step = d.Length > 4096 ? d.Length / 2048 : 1;
        for (int i = 0; i < d.Length; i += step) { h ^= d[i]; h *= 1099511628211UL; }
        h ^= (ulong)d.Length; h *= 1099511628211UL;
        return h;
    }
    static readonly Dictionary<IntPtr, CapState> capStates = new();

    // ===== 꺼낸 창을 "화면 안 원래 자리"에 그대로 두되 투명+클릭통과로 숨기기 =====
    [DllImport("user32.dll")]
    static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")]
    static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")]
    static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
    const int GWL_EXSTYLE = -20;
    const int WS_EX_LAYERED = 0x80000;
    const int WS_EX_TRANSPARENT = 0x20;
    const uint LWA_ALPHA = 0x2;

    // 스타일/알파와 (필요하면) z-order를 SetWindowPos 한 번으로 같이 반영한다.
    // insertAfter를 주면 그 z-order로도 같이 바꿈(TOPMOST/NOTOPMOST) - 클릭 순간
    // "스타일 바꾸기"와 "맨 위로 올리기"를 따로 두 번 호출하지 않고 한 번에 처리해서
    // Win32 호출/대기 횟수를 줄인다.
    static void ApplyWindowStyle(IntPtr hwnd, bool transparent, byte alpha, IntPtr? insertAfter = null)
    {
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        exStyle |= WS_EX_LAYERED;
        if (transparent) exStyle |= WS_EX_TRANSPARENT;
        else exStyle &= ~WS_EX_TRANSPARENT;
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
        SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);

        uint flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED;
        IntPtr after = IntPtr.Zero;
        if (insertAfter.HasValue) after = insertAfter.Value;
        else flags |= SWP_NOZORDER;
        SetWindowPos(hwnd, after, 0, 0, 0, 0, flags);
    }

    // 평소 숨김 상태: 완전 투명(알파 0) + 클릭통과(뒤에 있는 걸 클릭한 것처럼 통과됨).
    static void SetWindowHidden(IntPtr hwnd) => ApplyWindowStyle(hwnd, transparent: true, alpha: 0);

    // 원래(보통 창) 상태로 되돌린다 - 다시 보이고 클릭도 정상.
    static void RestoreWindowVisible(IntPtr hwnd)
    {
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        exStyle &= ~WS_EX_LAYERED;
        exStyle &= ~WS_EX_TRANSPARENT;
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }
    // ======================================================

    // ===== 창에 직접 클릭 메시지 보내기 (커서 안 옮김) =====
    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    // 그 좌표에 있는 자식 창(버튼 등) 찾기
    [DllImport("user32.dll")]
    static extern IntPtr ChildWindowFromPoint(IntPtr hWndParent, POINT p);
    [DllImport("user32.dll")]
    static extern bool ScreenToClient(IntPtr hWnd, ref POINT p);

    const uint WM_MOUSEWHEEL = 0x020A;
    const uint MOUSEEVENTF_WHEEL = 0x0800;

    // 휠을 "실제 커서를 옮겨 진짜로 굴릴지"(true, 모든 앱에서 확실 - Word/Office 포함),
    // 아니면 "창에 메시지로만 보낼지"(false, 조용하지만 Office 등 일부 앱이 무시함).
    // Word가 메시지 방식을 무시해서 기본값을 true로 둔다.
    const bool ScrollWithRealCursor = true;

    // 실제 커서 방식일 때: 스크롤하는 동안만 창을 아주 옅게(거의 안 보이게) 띄운다.
    // 알파 0이면 DWM이 마우스 판정에서 아예 제외해버려서 휠이 안 먹는다. 1이면 판정은 되면서 눈엔 거의 안 띈다.
    const byte ScrollAlpha = 1;
    // 이 시간(ms) 동안 스크롤이 없으면 창을 다시 숨기고 커서를 되돌린다.
    // 한 번 쓸어내리는 동안 계속 켰다 껐다 하지 않게 해서 깜빡임을 없앤다.
    const int ScrollIdleMs = 400;

    static IntPtr scrollActiveWindow = IntPtr.Zero;
    static long scrollLastTick = 0;
    static POINT scrollSavedCursor;

    const uint WM_LBUTTONDOWN = 0x0201;
    const uint WM_LBUTTONUP = 0x0202;
    const uint WM_MOUSEMOVE = 0x0200;
    const int MK_LBUTTON = 0x0001;
    // ======================================================

    static int screenWidth = 1920;
    static int screenHeight = 1080;

    // JPEG 인코딩 화질 (0~100). 높일수록 선명하지만 프레임 용량↑. 90~95 권장.
    const long JpegQuality = 95L;
    static readonly ImageCodecInfo JpegCodec = GetJpegCodec();
    static ImageCodecInfo GetJpegCodec()
    {
        foreach (var c in ImageCodecInfo.GetImageEncoders())
            if (c.FormatID == ImageFormat.Jpeg.Guid) return c;
        return null;
    }

    static IntPtr grabbedWindow = IntPtr.Zero;

    // ===== 주먹으로 창 통째로 옮기기 (gdown/gmove/gup) =====
    // 클릭/드래그(down/move/up)와 별개 상태. 타이틀바 안 잡아도 창 몸통 아무데나 주먹 쥐면 통째로 따라온다.
    static IntPtr grabMoveWindow = IntPtr.Zero;
    static int grabMoveStartCursorX = 0, grabMoveStartCursorY = 0;
    static RECT grabMoveStartRect;

    static readonly List<IntPtr> ejectedWindows = new();
    static readonly object ejectLock = new();

    // 서버가 건드린 창들의 "원래 배치"(서버 실행 전 상태). 종료 시 여기로 전부 되돌린다.
    static readonly Dictionary<IntPtr, WINDOWPLACEMENT> originalPlacements = new();
    static readonly object originalLock = new();

    // 어떤 창을 처음 건드리는 순간(잡기/꺼내기) 원래 배치를 한 번만 저장해둔다.
    static void RememberOriginal(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || IsDesktopOrShellWindow(hwnd)) return;
        lock (originalLock)
        {
            if (originalPlacements.ContainsKey(hwnd)) return;
            var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (GetWindowPlacement(hwnd, ref wp))
                originalPlacements[hwnd] = wp;
        }
    }

    static readonly List<WebSocket> windowClients = new();
    static readonly List<WebSocket> controlClients = new();

    static async Task Main()
    {
        // 무엇보다 먼저. 이 줄이 늦으면 이미 만들어진 것들이 가짜 좌표로 굳는다.
        EnableDpiAwareness();

        screenWidth = GetSystemMetrics(SM_CXSCREEN);
        screenHeight = GetSystemMetrics(SM_CYSCREEN);
        EnumerateMonitors();

        // Ctrl+C 또는 종료 시 꺼낸 창들을 다시 보이게 복원
        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true;              // 즉시 종료 막고 마무리 먼저
            RestoreAllWindows();
            Console.WriteLine("창 복원 완료. 종료합니다.");
            Environment.Exit(0);
        };
        AppDomain.CurrentDomain.ProcessExit += (sender, e) => { RestoreAllWindows(); };

        // localhost 전용이면 APK(퀘스트 단독 실행)에서 접속이 안 된다. 가능하면 모든 랜 인터페이스로 연다.
        PrintHelp();

        var controlListener = StartListener(8080, out bool ctrlAll);
        var windowListener = StartListener(8081, out bool winAll);
        PrintConnectionInfo(ctrlAll && winAll);
        Console.WriteLine($"Screen: {screenWidth}x{screenHeight}  (DPI 인식 켜짐)");
        Console.WriteLine($"Monitors ({monitors.Count}):");
        for (int i = 0; i < monitors.Count; i++)
            Console.WriteLine($"  [{i}] {monitors[i].W}x{monitors[i].H} @ ({monitors[i].X},{monitors[i].Y})");

        _ = Task.Run(async () =>
        {
            while (true)
            {
                var ctx = await windowListener.GetContextAsync();
                if (ctx.Request.IsWebSocketRequest)
                {
                    var wsCtx = await ctx.AcceptWebSocketAsync(null);
                    lock (windowClients) windowClients.Add(wsCtx.WebSocket);
                    Console.WriteLine("Window-capture client connected.");
                }
            }
        });

        _ = Task.Run(async () =>
        {
            while (true)
            {
                var ctx = await controlListener.GetContextAsync();
                if (ctx.Request.IsWebSocketRequest)
                {
                    var wsCtx = await ctx.AcceptWebSocketAsync(null);
                    Console.WriteLine("Control client connected.");
                    lock (controlClients) controlClients.Add(wsCtx.WebSocket);
                    await SendMonitorInfo(wsCtx.WebSocket);   // 모니터 목록 먼저 전송
                    _ = Task.Run(() => ReceiveLoop(wsCtx.WebSocket));
                }
            }
        });

        _ = Task.Run(ConsoleLoop);        // 콘솔 명령 (x / cap / q)
        _ = Task.Run(DesktopStreamLoop);  // [AR LAPTOP] 주 모니터 화면 통째로 → 모니터 판에 그림

        while (true)
        {
            // 스크롤이 끝났으면(잠시 입력 없음) 창을 다시 숨기고 커서를 되돌린다
            if (scrollActiveWindow != IntPtr.Zero && Environment.TickCount64 - scrollLastTick > ScrollIdleMs)
                EndScrollSession();

            List<IntPtr> targets;
            lock (ejectLock) targets = new List<IntPtr>(ejectedWindows);
            foreach (var hwnd in targets)
            {
                try { await CaptureAndSend(hwnd); }
                catch (Exception ex) { Console.WriteLine($"Capture error: {ex.Message}"); }
            }
            await Task.Delay(33);
        }
    }

    // 우선 모든 인터페이스(http://+:port/)로 열고, 권한이 없으면 localhost 전용으로 물러난다.
    // 모든 인터페이스로 열려야 퀘스트에 APK로 설치한 앱이 PC의 랜 IP로 붙을 수 있다.
    static HttpListener StartListener(int port, out bool allInterfaces)
    {
        try
        {
            var l = new HttpListener();
            l.Prefixes.Add($"http://+:{port}/");
            l.Start();
            allInterfaces = true;
            return l;
        }
        catch (Exception)
        {
            var l = new HttpListener();
            l.Prefixes.Add($"http://localhost:{port}/");
            l.Start();
            allInterfaces = false;
            return l;
        }
    }

    static void PrintConnectionInfo(bool allInterfaces)
    {
        Console.WriteLine();
        if (allInterfaces)
        {
            Console.WriteLine("서버가 모든 네트워크 인터페이스에서 접속을 받습니다 (APK 사용 가능).");
            Console.WriteLine("유니티(PCStreamReceiver)의 Server Host에 아래 IP 중 하나를 넣으세요:");
            try
            {
                foreach (var ip in System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName()).AddressList)
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        Console.WriteLine($"    {ip}");
            }
            catch { Console.WriteLine("    (IP 조회 실패 - ipconfig로 확인하세요)"); }
        }
        else
        {
            Console.WriteLine("!! localhost 전용으로 열렸습니다 - 퀘스트에 설치한 APK는 접속할 수 없습니다.");
            Console.WriteLine("   해결: 이 서버를 '관리자 권한'으로 실행하거나, 관리자 명령 프롬프트에서 아래를 1회 실행:");
            Console.WriteLine("     netsh http add urlacl url=http://+:8080/ user=EVERYONE");
            Console.WriteLine("     netsh http add urlacl url=http://+:8081/ user=EVERYONE");
            Console.WriteLine("   (USB 연결이라면 대신: adb reverse tcp:8080 tcp:8080 / adb reverse tcp:8081 tcp:8081)");
        }
        Console.WriteLine("방화벽에서 8080/8081 인바운드가 허용되어 있어야 합니다.");
        Console.WriteLine();
    }

    static async Task CaptureAndSend(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out var rect);
        int w = rect.Right - rect.Left;
        int h = rect.Bottom - rect.Top;
        if (w <= 0 || h <= 0) return;

        if (!capStates.TryGetValue(hwnd, out var st)) { st = new CapState(); capStates[hwnd] = st; }

        byte[] jpeg;
        using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb))
        {
            bool ok;
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                ok = PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT);
                g.ReleaseHdc(hdc);
            }

            double blackRatio = ok ? BlackRatio(bmp, w, h) : 1.0;

            // 거의 전부 검게 나왔으면 렌더 플래그를 바꿔 한 번 더 시도한다 (앱에 따라 0이 더 잘 먹음).
            if (ok && blackRatio > 0.95)
            {
                using (var g2 = Graphics.FromImage(bmp))
                {
                    IntPtr hdc2 = g2.GetHdc();
                    PrintWindow(hwnd, hdc2, 0);
                    g2.ReleaseHdc(hdc2);
                }
                blackRatio = BlackRatio(bmp, w, h);
            }

            // 직전 프레임은 멀쩡했는데 갑자기 대부분이 검게 나왔다 = 캡처 글리치.
            // 그 프레임은 보내지 않고 버려서, 유니티 쪽에 직전 좋은 화면이 그대로 남아 깜빡임이 사라진다.
            // 창이 진짜로 어두워진 경우 영원히 멈추지 않도록 연속 스킵 횟수는 제한한다.
            bool glitch = !ok || (blackRatio >= 0.80 && st.lastBlackRatio < 0.60);
            if (glitch && st.skipped < 30) { st.skipped++; return; }

            st.skipped = 0;
            st.lastBlackRatio = blackRatio;

            // 긴 변이 MaxCaptureSide를 넘으면 비율 그대로 줄인다.
            Bitmap send = bmp;
            Bitmap scaled = null;
            int longSide = Math.Max(w, h);
            if (MaxCaptureSide > 0 && longSide > MaxCaptureSide)
            {
                double sc = (double)MaxCaptureSide / longSide;
                int nw = Math.Max(1, (int)Math.Round(w * sc));
                int nh = Math.Max(1, (int)Math.Round(h * sc));
                scaled = new Bitmap(nw, nh, PixelFormat.Format32bppRgb);
                using (var g3 = Graphics.FromImage(scaled))
                {
                    g3.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g3.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g3.DrawImage(bmp, 0, 0, nw, nh);
                }
                send = scaled;
            }

            using var ms = new MemoryStream();
            if (JpegCodec != null)
            {
                using var eps = new EncoderParameters(1);
                eps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, JpegQualityVar);
                send.Save(ms, JpegCodec, eps);
            }
            else send.Save(ms, ImageFormat.Jpeg);
            jpeg = ms.ToArray();
            scaled?.Dispose();

            // 직전과 똑같은 그림이면 보내지 않는다. 정지된 창은 대역폭을 안 쓴다.
            ulong hash = QuickHash(jpeg);
            if (hash == st.lastHash && st.sameCount < MaxSameSkip) { st.sameCount++; statSkipped++; return; }
            st.sameCount = 0;
            st.lastHash = hash;
        }

        long id = hwnd.ToInt64();
        byte[] payload = new byte[8 + jpeg.Length];
        BitConverter.GetBytes(id).CopyTo(payload, 0);
        jpeg.CopyTo(payload, 8);

        List<WebSocket> snapshot;
        lock (windowClients) snapshot = new List<WebSocket>(windowClients);

        statFrames++; statBytes += payload.Length; statClients = snapshot.Count;
        long nowTick = Environment.TickCount64;
        if (nowTick - statTick >= 1000)
        {
            statTick = nowTick;
            Console.WriteLine($"[캡처] {statFrames}장/초 · {statBytes / 1024}KB/초 · 정지스킵 {statSkipped} · 받는곳 {statClients}곳"
                            + (statClients == 0 ? "  ← 퀘스트가 8081에 안 붙어 있습니다 (흰 쿼드 원인)" : ""));
            statFrames = 0; statBytes = 0; statSkipped = 0;
        }

        await BroadcastFrame(payload);
    }

    // 8081로 프레임 한 장을 모든 클라이언트에 보낸다.
    // 창 캡처 루프와 데스크탑 화면 루프가 동시에 보내므로 락을 건다
    // (웹소켓은 SendAsync가 동시에 두 개 걸리면 예외가 난다).
    static readonly SemaphoreSlim frameSendLock = new(1, 1);
    static async Task BroadcastFrame(byte[] payload)
    {
        List<WebSocket> snapshot;
        lock (windowClients) snapshot = new List<WebSocket>(windowClients);

        var dead = new List<WebSocket>();
        await frameSendLock.WaitAsync();
        try
        {
            foreach (var ws in snapshot)
            {
                if (ws.State == WebSocketState.Open)
                {
                    try { await ws.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Binary, true, default); }
                    catch { dead.Add(ws); }
                }
                else dead.Add(ws);
            }
        }
        finally { frameSendLock.Release(); }
        if (dead.Count > 0)
            lock (windowClients) foreach (var d in dead) windowClients.Remove(d);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // [AR LAPTOP] 데스크탑 화면 통째로 보내기
    //
    // 주 모니터(0번) 화면 전체를 찍어서 ID = DesktopFrameId(-1) 로 8081에 보낸다.
    // 유니티의 DesktopScreenView가 이 ID를 등록해서 모니터 판 위에 그린다.
    // 꺼낸 창은 투명(알파 0)이라 이 화면에는 안 찍힌다 → AR로 나간 창은 판에서 사라져 보인다.
    // ─────────────────────────────────────────────────────────────────────────
    const long DesktopFrameId = -1;
    static bool DesktopStreamOn = true;
    static int DesktopMaxSide = 1920;     // 긴 변 최대 픽셀. 느리면 1280
    static int DesktopIntervalMs = 50;    // 20장/초
    static long DesktopJpegQuality = 80L;

    static async Task DesktopStreamLoop()
    {
        ulong lastHash = 0;
        int same = 0;
        int sent = 0;
        long logTick = Environment.TickCount64;
        Console.WriteLine($"[데스크탑 화면] 주 모니터 {(monitors.Count > 0 ? monitors[0].W + "x" + monitors[0].H : "?")} 를 ID -1 로 보냄");
        while (true)
        {
            long t0 = Environment.TickCount64;
            if (t0 - logTick >= 3000)
            {
                int clients;
                lock (windowClients) clients = windowClients.Count;
                Console.WriteLine($"[데스크탑 화면] 3초간 {sent}장 보냄 · 받는곳 {clients}곳"
                                  + (clients == 0 ? "  ← 헤드셋이 8081에 안 붙음 (흰 판 원인). USB면 adb reverse, 무선이면 Server Host IP 확인" : ""));
                sent = 0;
                logTick = t0;
            }
            if (DesktopStreamOn)
            {
                try
                {
                    var m = monitors.Count > 0 ? monitors[0] : new Mon { X = 0, Y = 0, W = screenWidth, H = screenHeight };
                    byte[] jpeg;
                    using (var bmp = new Bitmap(m.W, m.H, PixelFormat.Format32bppRgb))
                    {
                        using (var g = Graphics.FromImage(bmp))
                            g.CopyFromScreen(m.X, m.Y, 0, 0, new Size(m.W, m.H), CopyPixelOperation.SourceCopy);

                        Bitmap send = bmp;
                        Bitmap scaled = null;
                        int longSide = Math.Max(m.W, m.H);
                        if (DesktopMaxSide > 0 && longSide > DesktopMaxSide)
                        {
                            double sc = (double)DesktopMaxSide / longSide;
                            int nw = Math.Max(1, (int)Math.Round(m.W * sc));
                            int nh = Math.Max(1, (int)Math.Round(m.H * sc));
                            scaled = new Bitmap(nw, nh, PixelFormat.Format32bppRgb);
                            using (var g3 = Graphics.FromImage(scaled))
                            {
                                g3.InterpolationMode = InterpolationMode.HighQualityBilinear;
                                g3.DrawImage(bmp, 0, 0, nw, nh);
                            }
                            send = scaled;
                        }

                        using var ms = new MemoryStream();
                        if (JpegCodec != null)
                        {
                            using var eps = new EncoderParameters(1);
                            eps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, DesktopJpegQuality);
                            send.Save(ms, JpegCodec, eps);
                        }
                        else send.Save(ms, ImageFormat.Jpeg);
                        jpeg = ms.ToArray();
                        scaled?.Dispose();
                    }

                    ulong h = QuickHash(jpeg);
                    if (h == lastHash && same < MaxSameSkip) same++;
                    else
                    {
                        same = 0;
                        lastHash = h;
                        byte[] payload = new byte[8 + jpeg.Length];
                        BitConverter.GetBytes(DesktopFrameId).CopyTo(payload, 0);
                        jpeg.CopyTo(payload, 8);
                        await BroadcastFrame(payload);
                        sent++;
                    }
                }
                catch (Exception ex) { Console.WriteLine($"[데스크탑 화면] 캡처 오류: {ex.Message}"); }
            }
            int wait = DesktopIntervalMs - (int)(Environment.TickCount64 - t0);
            await Task.Delay(Math.Max(5, wait));
        }
    }

    // 프레임에서 "거의 검은 픽셀"의 비율(0~1)을 빠르게 샘플링한다.
    // GetPixel은 픽셀마다 락을 잡아서 느리므로 LockBits로 한 번에 읽는다(캡처 루프가 매 프레임 도는 곳).
    static double BlackRatio(Bitmap bmp, int w, int h)
    {
        int stepX = Math.Max(1, w / 48);
        int stepY = Math.Max(1, h / 48);
        int total = 0, black = 0;
        BitmapData data = null;
        try
        {
            data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            for (int y = 0; y < h; y += stepY)
            {
                IntPtr row = IntPtr.Add(data.Scan0, y * data.Stride);
                for (int x = 0; x < w; x += stepX)
                {
                    int px = Marshal.ReadInt32(row, x * 4);
                    int b = px & 0xFF, g = (px >> 8) & 0xFF, r = (px >> 16) & 0xFF;
                    total++;
                    if (r <= 12 && g <= 12 && b <= 12) black++;
                }
            }
        }
        catch { return 0.0; }   // 읽기 실패 시엔 "검지 않다"로 취급해서 프레임을 버리지 않음
        finally { if (data != null) bmp.UnlockBits(data); }
        return total > 0 ? (double)black / total : 0.0;
    }

    static async Task ReceiveLoop(WebSocket ws)
    {
        // 한 메시지가 버퍼보다 크면 여러 조각으로 나뉘어 온다. EndOfMessage가 뜰 때까지
        // 모아 붙여야 긴 메시지(기록 묶음 등)가 중간에서 잘리지 않는다.
        var buffer = new byte[16384];
        var assembled = new MemoryStream();
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), default);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.MessageType != WebSocketMessageType.Text) continue;

                assembled.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;

                string text = Encoding.UTF8.GetString(assembled.ToArray());
                assembled.SetLength(0);
                await HandleMessage(text, ws);
            }
        }
        catch (Exception ex) { Console.WriteLine($"Receive error: {ex.Message}"); }
    }

    static async Task HandleMessage(string msg, WebSocket ws)
    {
        try
        {
            // Unity가 보낸 세션 기록 묶음
            if (msg.StartsWith("srec,")) return;   // [AR LAPTOP] 기록 안 함

            var parts = msg.Split(',');
            string type = parts[0];

            // 꺼낸 창 클릭: "wclick,id,u,v" (커서 방식 - 확실히 먹음)
            if (type == "wclick" && parts.Length == 4)
            {
                long id = long.Parse(parts[1]);
                float wu = float.Parse(parts[2]);
                float wv = float.Parse(parts[3]);
                WindowClick(new IntPtr(id), wu, wv);
                return;
            }

            // 꺼낸 창 스크롤: "wscroll,id,u,v,delta"  (delta: +위로 / -아래로, 120 = 휠 한 칸)
            // 창에 휠 메시지를 직접 보내므로 창을 보이게 만들 필요도, 실제 커서를 뺏을 필요도 없다.
            // 화면 밖으로 나간 부분도 그대로 스크롤된다(커서가 갈 필요가 없으므로).
            if (type == "wscroll" && parts.Length == 5)
            {
                long id = long.Parse(parts[1]);
                float su = float.Parse(parts[2]);
                float sv = float.Parse(parts[3]);
                int delta = int.Parse(parts[4]);
                WindowScroll(new IntPtr(id), su, sv, delta);
                return;
            }

            // AR에서 데스크탑 화면 쪽으로 끌고 와서 다시 넣기: "insert,id"
            if (type == "insert" && parts.Length == 2)
            {
                long id = long.Parse(parts[1]);
                InsertWindow(new IntPtr(id));
                return;
            }

            // 놓인 AR 창을 주먹으로 잡은 채로 데스크탑 화면에 겹쳐서 되돌리기: "reinsert,id,u,v".
            // insert와 다른 점: 창을 되돌리자마자 그 자리에서 곧바로 "주먹으로 통째로 잡은
            // 상태(grabMoveWindow)"로 등록해서, Unity가 손을 떼지 않고 이어서 보내는 gmove가
            // 별도 gdown 없이 바로 먹는다.
            if (type == "reinsert" && parts.Length >= 4)
            {
                long id = long.Parse(parts[1]);
                float ru = float.Parse(parts[2]);
                float rv = float.Parse(parts[3]);
                int rmon = parts.Length >= 5 ? int.Parse(parts[4]) : 0;
                ReinsertAndGrab(new IntPtr(id), ru, rv, rmon);
                return;
            }

            // AR에서 가장자리로 창 크기 변경(한 변 리사이즈): "wresize,id,pxW,pxH"
            // 실제 창을 그 픽셀 크기로 리사이즈 → 앱이 리플로우 → 캡처 루프가 새 크기로 찍어 스트리밍.
            if (type == "wresize" && parts.Length == 4)
            {
                long id = long.Parse(parts[1]);
                int pxW = int.Parse(parts[2]);
                int pxH = int.Parse(parts[3]);
                WindowResize(new IntPtr(id), pxW, pxH);
                return;
            }

            // 헤드셋이 진행자에게 보내는 안내 문구: "msg,내용"
            // (헤드셋 안에서는 OnGUI 디버그 글씨가 안 보이므로 여기로 띄운다)
            if (type == "msg")
            {
                string body = msg.Length > 4 ? msg.Substring(4) : "";
                Console.WriteLine("  [헤드셋] " + body);
                return;
            }

            // 나머지는 모니터 제어: "type,u,v" 또는 "type,u,v,mon" (mon 없으면 0=주 모니터)
            if (parts.Length < 3) return;
            float u = float.Parse(parts[1]);
            float v = float.Parse(parts[2]);
            int mon = parts.Length >= 4 ? int.Parse(parts[3]) : 0;
            MapToMonitor(mon, u, v, out int x, out int y);

            switch (type)
            {
                case "down":
                    POINT p = new POINT { X = x, Y = y };
                    IntPtr downHit = GetAncestor(WindowFromPoint(p), GA_ROOT);
                    // 바탕화면/셸을 짚었으면 "잡은 창 없음" 취급 (eject 대상에서 제외)
                    grabbedWindow = IsDesktopOrShellWindow(downHit) ? IntPtr.Zero : downHit;
                    Console.WriteLine($"DOWN ({x},{y}) 창: {GetTitle(grabbedWindow)}" + (grabbedWindow == IntPtr.Zero && downHit != IntPtr.Zero ? " (바탕화면/셸이라 제외)" : ""));
                    SetCursorPos(x, y);
                    mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
                    break;
                case "move":
                    SetCursorPos(x, y);
                    break;
                case "up":
                    SetCursorPos(x, y);
                    mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
                    break;

                // 주먹으로 창 통째로 옮기기: 실제 클릭/커서 이동 없이 SetWindowPos로 직접 위치만 바꾼다.
                case "gdown":
                    {
                        POINT gp = new POINT { X = x, Y = y };
                        IntPtr gdownHit = GetAncestor(WindowFromPoint(gp), GA_ROOT);
                        // 바탕화면/셸을 주먹으로 잡으려고 한 거면 무시 (그걸 옮기거나 꺼내면 안 됨)
                        grabMoveWindow = IsDesktopOrShellWindow(gdownHit) ? IntPtr.Zero : gdownHit;
                        if (grabMoveWindow != IntPtr.Zero)
                        {
                            RememberOriginal(grabMoveWindow);   // 옮기기 전 원래 위치 기억 (종료 시 복원용)
                            GetWindowRect(grabMoveWindow, out grabMoveStartRect);
                            grabMoveStartCursorX = x;
                            grabMoveStartCursorY = y;
                            Console.WriteLine($"GDOWN 창 통째로 잡음: {GetTitle(grabMoveWindow)}");
                        }
                        else
                        {
                            Console.WriteLine("GDOWN 무시됨 (바탕화면/셸 또는 창 없음)");
                        }
                        break;
                    }
                case "gmove":
                    if (grabMoveWindow != IntPtr.Zero)
                    {
                        int dx = x - grabMoveStartCursorX;
                        int dy = y - grabMoveStartCursorY;
                        int nx = grabMoveStartRect.Left + dx;
                        int ny = grabMoveStartRect.Top + dy;
                        uint mvFlags = SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS;
                        SetWindowPos(grabMoveWindow, IntPtr.Zero, nx, ny, 0, 0, mvFlags);
                    }
                    break;
                case "gup":
                    if (grabMoveWindow != IntPtr.Zero)
                        Console.WriteLine($"GUP 배치 완료: {GetTitle(grabMoveWindow)}");
                    grabMoveWindow = IntPtr.Zero;
                    break;

                case "eject":
                    // 핀치 드래그로 꺼내기: 실제 마우스 버튼을 누르고 있었으니 먼저 떼준다
                    mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
                    await EjectWindow(grabbedWindow, ws);
                    grabbedWindow = IntPtr.Zero;
                    break;

                case "geject":
                    // 주먹으로 창을 통째로 옮기다가 화면 밖으로 끌고 나가 AR로 꺼내기
                    await EjectWindow(grabMoveWindow, ws);
                    grabMoveWindow = IntPtr.Zero;
                    break;
            }
        }
        catch (Exception ex) { Console.WriteLine($"HandleMessage error: {ex.Message} (msg={msg})"); }
    }

    // 창 전체(가로/세로 모두)가 화면 안에 들어오도록 위치만 보정한다. 창이 화면보다
    // 크면 좌상단을 (0,0)에 맞춘다 - 완전히 다 들어오게 할 수는 없지만 최대한 맞춘다.
    static void ClampWindowToScreen(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out var wr);
        int w = wr.Right - wr.Left;
        int h = wr.Bottom - wr.Top;
        if (w <= 0 || h <= 0) return;

        int nx = wr.Left;
        int ny = wr.Top;
        if (nx < 0) nx = 0;
        if (ny < 0) ny = 0;
        if (nx + w > screenWidth) nx = Math.Max(0, screenWidth - w);
        if (ny + h > screenHeight) ny = Math.Max(0, screenHeight - h);

        if (nx != wr.Left || ny != wr.Top)
        {
            uint flags = SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE;
            SetWindowPos(hwnd, IntPtr.Zero, nx, ny, 0, 0, flags);
            Console.WriteLine($"  화면 안으로 위치 보정: ({wr.Left},{wr.Top}) → ({nx},{ny})");
        }
    }

    // eject/geject 공통 처리: 창을 화면 밖으로 보내고 Unity에 spawn 알림
    static async Task EjectWindow(IntPtr hwnd, WebSocket ws)
    {
        if (hwnd == IntPtr.Zero) return;
        if (IsDesktopOrShellWindow(hwnd)) { Console.WriteLine("EJECT 무시됨 (바탕화면/셸)"); return; } // 안전장치(2중 확인)

        bool already;
        lock (ejectLock) already = ejectedWindows.Contains(hwnd);
        if (already) return;

        // 서버 실행 전 상태(위치/크기/최대화 여부)를 기억해둔다 - 종료 시 이걸로 복원.
        // (여기서는 창을 절대 건드리지 않는다 - 그래야 스폰되는 쿼드 비율이 실제 창과 정확히 일치)
        RememberOriginal(hwnd);

        GetWindowRect(hwnd, out var wr);
        int ww = wr.Right - wr.Left;
        int wh = wr.Bottom - wr.Top;
        long gid = hwnd.ToInt64();
        Console.WriteLine($"★ EJECT: \"{GetTitle(hwnd)}\" id={gid} {ww}x{wh}");
        // 창이 모니터 경계 밖으로 삐져나가 있으면(주먹으로 끌다가 가장자리에서 놓은 경우 등)
        // 온전히 화면 안으로 들어오도록 위치만 보정한다 - 잘린 채로 있으면 나중에 그
        // 잘린 부분은 커서 클램핑 때문에 클릭도 안 먹는다.
        ClampWindowToScreen(hwnd);
        // 화면 밖으로 옮기지 않고, 원래(보정된) 자리에 그대로 둔 채 투명+클릭통과로만 숨긴다.
        // (크로미움/일렉트론 앱이 화면 밖 창을 렌더링 안 하는 문제 회피 + 클릭 시 위치 이동 불필요)
        SetWindowHidden(hwnd);
        lock (ejectLock) ejectedWindows.Add(hwnd);

        string spawnMsg = $"spawn,{gid},{ww},{wh}";
        var bytes = Encoding.UTF8.GetBytes(spawnMsg);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, default);
    }

    // AR 쿼드를 데스크탑 화면 쪽으로 끌고 왔을 때: eject의 반대. 이제 창을 옮긴 적이 없으므로
    // 위치 복원은 필요 없고, 투명+클릭통과만 풀어주면 원래 있던 그 자리에서 바로 보인다.
    static void InsertWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        bool wasEjected;
        lock (ejectLock) wasEjected = ejectedWindows.Remove(hwnd);
        if (!wasEjected)
        {
            Console.WriteLine($"INSERT 무시됨 (꺼내진 창 목록에 없음) id={hwnd.ToInt64()}");
            return;
        }

        RestoreWindowVisible(hwnd);
        // 사용자가 지금 막 되돌려놓은 것이니 눈에 띄게 앞으로 가져오고 포커스도 준다.
        BringWindowToTop(hwnd);
        SetForegroundWindow(hwnd);

        Console.WriteLine($"★ INSERT: \"{GetTitle(hwnd)}\" id={hwnd.ToInt64()}");
    }

    // 놓인 AR 창을 손으로 잡은 채로 데스크탑 화면에 겹쳐서 되돌릴 때: InsertWindow처럼
    // 원래 자리로 복원하는 대신, 지금 커서(u,v)가 있는 위치 근처에 띄우고 그 자리에서
    // 곧바로 "gdown 상태"(grabMoveWindow)로 등록한다. 그래서 Unity가 손을 떼지 않고
    // 이어서 보내는 gmove가 별도 gdown 없이 바로 이 창을 커서 따라 움직인다.
    static void ReinsertAndGrab(IntPtr hwnd, float u, float v, int mon)
    {
        if (hwnd == IntPtr.Zero) return;

        bool wasEjected;
        lock (ejectLock) wasEjected = ejectedWindows.Remove(hwnd);
        if (!wasEjected)
        {
            Console.WriteLine($"REINSERT 무시됨 (꺼내진 창 목록에 없음) id={hwnd.ToInt64()}");
            return;
        }

        RestoreWindowVisible(hwnd);

        GetWindowRect(hwnd, out var wr);
        int ww = wr.Right - wr.Left;

        MapToMonitor(mon, u, v, out int cx, out int cy);
        // 커서가 대략 타이틀바 중앙쯤 오도록 배치 - 손으로 쥔 지점 근처에 창이 나타나게.
        int nx = cx - ww / 2;
        int ny = cy - 20;

        uint flags = SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE;
        SetWindowPos(hwnd, IntPtr.Zero, nx, ny, 0, 0, flags);
        System.Threading.Thread.Sleep(20);
        SetWindowPos(hwnd, IntPtr.Zero, nx, ny, 0, 0, flags);
        BringWindowToTop(hwnd);
        SetForegroundWindow(hwnd);

        // 곧바로 gdown 상태로 등록.
        grabMoveWindow = hwnd;
        grabMoveStartCursorX = cx;
        grabMoveStartCursorY = cy;
        GetWindowRect(hwnd, out grabMoveStartRect);

        Console.WriteLine($"★ REINSERT+GRAB: \"{GetTitle(hwnd)}\" id={hwnd.ToInt64()} → ({nx},{ny}), 커서=({cx},{cy})");
    }

    // 꺼낸 창 클릭: 이 환경에서는 알파 0(완전 투명)인 레이어드 창은 WS_EX_TRANSPARENT를
    // 꺼도 실제 마우스 입력 자체가 안 들어온다 (직접 테스트로 확인됨 - DWM이 알파 0인
    // 창은 클릭 대상에서 아예 제외하는 걸로 보임). 그래서 클릭 순간만큼은 알파를 진짜
    // 255로 되돌려 완전히 정상적인 창처럼 만들어야 클릭이 들어온다. 위치는 안 옮기므로
    // 화면에 아주 짧게(수십ms) 비쳤다 사라지는 정도.
    static void WindowClick(IntPtr hwnd, float u, float v)
    {
        EndScrollSession();   // 스크롤 세션이 열려 있으면 먼저 정리 (스타일이 꼬이지 않게)
        GetWindowRect(hwnd, out var rect);
        int w = rect.Right - rect.Left;
        int h = rect.Bottom - rect.Top;
        if (w <= 0 || h <= 0) return;

        int x = rect.Left + (int)(u * w);
        int y = rect.Top + (int)(v * h);

        // === 화면 밖 지점 클릭 보정 ===
        // 창을 모니터보다 크게 늘리면 아래/오른쪽 일부가 화면 밖으로 나가는데, 실제 커서는
        // 화면 안으로 클램프되기 때문에 그 부분은 클릭이 안 먹는다.
        // 그래서 클릭 지점이 화면 밖이면, 그 지점이 화면 안에 들어올 만큼만 창을 잠깐 옮기고
        // 클릭한 뒤 원래 자리로 되돌린다. (AR 쪽 캡처는 위치와 무관하므로 화면엔 티가 안 남)
        int origX = rect.Left, origY = rect.Top;
        bool movedForClick = false;
        {
            var mon = BestMonitorFor(rect);
            const int margin = 6;
            int dx = 0, dy = 0;
            if (x < mon.X + margin) dx = (mon.X + margin) - x;
            else if (x > mon.X + mon.W - margin) dx = (mon.X + mon.W - margin) - x;
            if (y < mon.Y + margin) dy = (mon.Y + margin) - y;
            else if (y > mon.Y + mon.H - margin) dy = (mon.Y + mon.H - margin) - y;

            if (dx != 0 || dy != 0)
            {
                uint mvFlags = SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOSENDCHANGING;
                SetWindowPos(hwnd, IntPtr.Zero, origX + dx, origY + dy, 0, 0, mvFlags);
                System.Threading.Thread.Sleep(10);
                x += dx; y += dy;
                movedForClick = true;
                Console.WriteLine($"  클릭 지점이 화면 밖 → 창을 ({dx},{dy})만큼 잠깐 이동");
            }
        }

        // 클릭 순간: 실제로 보이는 상태(알파 255) + 클릭통과 끔 + 최상단.
        ApplyWindowStyle(hwnd, transparent: false, alpha: 255, HWND_TOPMOST);
        SetForegroundWindow(hwnd);
        System.Threading.Thread.Sleep(20);

        GetCursorPos(out POINT saved);
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(15);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(25);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(15);
        SetCursorPos(saved.X, saved.Y);

        // 클릭 끝: 다시 평소 상태(알파 0 + 클릭통과) + TOPMOST 해제, 한 번에.
        ApplyWindowStyle(hwnd, transparent: true, alpha: 0, HWND_NOTOPMOST);

        // 클릭하려고 옮겼던 창은 원래 자리로 되돌린다
        if (movedForClick)
        {
            uint back = SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOSENDCHANGING;
            SetWindowPos(hwnd, IntPtr.Zero, origX, origY, 0, 0, back);
        }

        Console.WriteLine($"WCLICK(잠깐 활성화) id={hwnd.ToInt64()} 좌표=({x},{y})");
    }

    // 꺼낸 창을 스크롤한다. delta > 0 이면 위로(휠 굴려 올리기), < 0 이면 아래로. 120이 휠 한 칸.
    static void WindowScroll(IntPtr hwnd, float u, float v, int delta)
    {
        if (hwnd == IntPtr.Zero || delta == 0) return;

        GetWindowRect(hwnd, out var rect);
        int w = rect.Right - rect.Left;
        int h = rect.Bottom - rect.Top;
        if (w <= 0 || h <= 0) return;

        int x = rect.Left + (int)(u * w);
        int y = rect.Top + (int)(v * h);

        if (!ScrollWithRealCursor)
        {
            // 그 지점에 있는 실제 자식 컨트롤로 보낸다 (스크롤 영역이 자식 창인 앱이 많음)
            IntPtr target = DeepChildFromPoint(hwnd, x, y);
            if (target == IntPtr.Zero) target = hwnd;

            IntPtr wParam = (IntPtr)((delta & 0xFFFF) << 16);              // HIWORD = 휠 delta
            IntPtr lParam = (IntPtr)(((y & 0xFFFF) << 16) | (x & 0xFFFF)); // 스크린 좌표
            PostMessage(target, WM_MOUSEWHEEL, wParam, lParam);
            return;
        }

        // === 실제 커서로 휠 굴리기 (Word 등 모든 앱에서 동작) ===
        // 커서는 화면 안에만 갈 수 있으므로, 창과 모니터가 겹치는 영역 안으로 좌표를 밀어 넣는다.
        var mon = BestMonitorFor(rect);
        int minX = Math.Max(rect.Left, mon.X) + 2, maxX = Math.Min(rect.Right, mon.X + mon.W) - 2;
        int minY = Math.Max(rect.Top, mon.Y) + 2, maxY = Math.Min(rect.Bottom, mon.Y + mon.H) - 2;
        if (minX > maxX || minY > maxY) return;   // 창이 이 모니터에 하나도 안 걸쳐 있으면 포기
        x = Math.Clamp(x, minX, maxX);
        y = Math.Clamp(y, minY, maxY);

        // 스크롤 "세션" 시작: 창을 아주 옅게 띄우고 맨 위로. 이미 세션 중이면 그대로 이어서 쓴다.
        if (scrollActiveWindow != hwnd)
        {
            EndScrollSession();
            GetCursorPos(out scrollSavedCursor);
            ApplyWindowStyle(hwnd, transparent: false, alpha: ScrollAlpha, HWND_TOPMOST);
            SetForegroundWindow(hwnd);
            System.Threading.Thread.Sleep(15);
            scrollActiveWindow = hwnd;
        }

        scrollLastTick = Environment.TickCount64;
        SetCursorPos(x, y);
        mouse_event(MOUSEEVENTF_WHEEL, 0, 0, (uint)delta, IntPtr.Zero);
    }

    // 스크롤이 멈춘 뒤 창을 다시 숨기고 커서를 원래 자리로 되돌린다.
    static void EndScrollSession()
    {
        if (scrollActiveWindow == IntPtr.Zero) return;
        IntPtr hwnd = scrollActiveWindow;
        scrollActiveWindow = IntPtr.Zero;
        try
        {
            SetCursorPos(scrollSavedCursor.X, scrollSavedCursor.Y);
            ApplyWindowStyle(hwnd, transparent: true, alpha: 0, HWND_NOTOPMOST);
        }
        catch { }
    }

    // 화면 좌표에 있는 가장 안쪽 자식 창을 찾는다.
    // (숨겨진 창이라 WindowFromPoint는 못 쓰므로, 루트에서부터 자식으로 내려간다)
    static IntPtr DeepChildFromPoint(IntPtr root, int screenX, int screenY)
    {
        IntPtr cur = root;
        for (int depth = 0; depth < 8; depth++)
        {
            POINT p = new POINT { X = screenX, Y = screenY };
            if (!ScreenToClient(cur, ref p)) break;
            IntPtr child = ChildWindowFromPoint(cur, p);
            if (child == IntPtr.Zero || child == cur) break;
            cur = child;
        }
        return cur;
    }

    // 창이 가장 많이 걸쳐 있는 모니터를 고른다 (없으면 주 모니터)
    static Mon BestMonitorFor(RECT r)
    {
        Mon best = monitors.Count > 0 ? monitors[0] : new Mon { X = 0, Y = 0, W = screenWidth, H = screenHeight };
        long bestArea = -1;
        foreach (var m in monitors)
        {
            int ix = Math.Max(0, Math.Min(r.Right, m.X + m.W) - Math.Max(r.Left, m.X));
            int iy = Math.Max(0, Math.Min(r.Bottom, m.Y + m.H) - Math.Max(r.Top, m.Y));
            long area = (long)ix * iy;
            if (area > bestArea) { bestArea = area; best = m; }
        }
        return best;
    }

    // 꺼낸 창을 지정 픽셀 크기로 리사이즈. 좌상단 위치는 그대로 두고 오른쪽/아래로 커진다(SWP_NOMOVE).
    // 창이 숨김(투명 alpha 0) 상태여도 SetWindowPos는 먹고, 캡처 루프가 GetWindowRect로 새 크기를 잡아
    // PrintWindow로 찍어 보내므로 "리플로우된 내용"이 그대로 AR로 스트리밍된다.
    // 창을 모니터보다 크게 만들어도 PrintWindow는 창 표면 전체를 렌더링하므로 AR엔 온전히 보인다.
    // (다만 모니터 밖으로 삐져나간 부분은 실제 커서가 못 닿으므로 wclick은 화면 안 영역에서만 확실히 먹는다.)
    static void WindowResize(IntPtr hwnd, int pxW, int pxH)
    {
        if (hwnd == IntPtr.Zero) return;
        if (IsDesktopOrShellWindow(hwnd)) return;
        if (pxW < 100) pxW = 100;
        if (pxH < 100) pxH = 100;

        // 최대화 상태면 먼저 풀어준다. 최대화 창은 SetWindowPos로 크기가 안 먹어서 데스크탑 크기까지만
        // 리플로우되고 그 이상은 텍스처만 늘어난다. 풀면 모니터보다 크게도 실제로 커지며 진짜 리플로우됨.
        // (꺼낼 때가 아니라 "실제로 늘리는 이 순간"에만 풀어서, 스폰 비율은 원래 창과 정확히 맞게 유지)
        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (GetWindowPlacement(hwnd, ref wp) && wp.showCmd == SW_SHOWMAXIMIZED)
            ShowWindow(hwnd, SW_RESTORE);

        // SWP_NOSENDCHANGING: 앱이 WM_WINDOWPOSCHANGING에서 크기를 되돌리는 걸 우회 (크롬/VS 등이 모니터 이상으로도 커지게)
        uint flags = SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOSENDCHANGING | SWP_ASYNCWINDOWPOS;
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, pxW, pxH, flags);
        Console.WriteLine($"WRESIZE id={hwnd.ToInt64()} → {pxW}x{pxH}");
    }

    // 종료 시: 서버가 건드린 모든 창을 "서버 실행 전 상태"로 되돌린다.
    //  1) 꺼낸 창들의 투명+클릭통과(레이어드) 해제
    //  2) 저장해둔 원래 배치(위치 + 크기 + 최대화/보통 상태)로 복원
    static void RestoreAllWindows()
    {
        EndScrollSession();   // 스크롤 중이었으면 커서/스타일 먼저 되돌림

        // 1) 투명/클릭통과 상태 먼저 정상으로
        List<IntPtr> ejected;
        lock (ejectLock) ejected = new List<IntPtr>(ejectedWindows);
        foreach (var hwnd in ejected)
        {
            try { RestoreWindowVisible(hwnd); } catch { }
        }
        lock (ejectLock) ejectedWindows.Clear();

        // 2) 원래 배치로 복원 (크기/위치/최대화 여부 전부)
        List<KeyValuePair<IntPtr, WINDOWPLACEMENT>> snaps;
        lock (originalLock) snaps = new List<KeyValuePair<IntPtr, WINDOWPLACEMENT>>(originalPlacements);
        if (snaps.Count == 0) return;

        Console.WriteLine($"\n창 {snaps.Count}개를 서버 실행 전 상태로 복원 중...");
        foreach (var kv in snaps)
        {
            try
            {
                var wp = kv.Value;
                SetWindowPlacement(kv.Key, ref wp);
                Console.WriteLine($"  복원: \"{GetTitle(kv.Key)}\"");
            }
            catch (Exception ex) { Console.WriteLine($"  복원 실패: {ex.Message}"); }
        }
        lock (originalLock) originalPlacements.Clear();
        Console.WriteLine("복원 완료.");
    }

    static string GetTitle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "(없음)";
        var sb = new StringBuilder(256);
        GetWindowText(hwnd, sb, sb.Capacity);
        string t = sb.ToString();
        return string.IsNullOrEmpty(t) ? "(제목 없음)" : t;
    }

    static string GetClassNameOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    // 바탕화면/셸 자체는 "앱 창"이 아니므로 잡기/꺼내기 대상에서 제외한다.
    // (포인팅이 창 바깥 빈 데스크탑을 가리켰을 때 바탕화면이 통째로 잡히는 것 방지)
    static bool IsDesktopOrShellWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return true;
        string cls = GetClassNameOf(hwnd);
        return cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd"
            || cls == "Shell_SecondaryTrayWnd" || cls == "SysListView32";
    }

    // ═════════════════════════════════════════════════════════════════════════
    // [AR LAPTOP] 콘솔 (기록 기능 없음)
    //   x / clear   공중의 창 전부 데스크탑으로
    //   cap 1920    창 캡처 긴 변 최대 픽셀 (0 = 원본)
    //   q 90        JPEG 품질
    //   ?           도움말
    // ═════════════════════════════════════════════════════════════════════════
    static void PrintHelp()
    {
        Console.WriteLine();
        Console.WriteLine("  ┌─ AR LAPTOP ───────────────────────────────────┐");
        Console.WriteLine("  │  주 모니터 1개를 통째로 복제해서 판에 보낸다     │");
        Console.WriteLine("  │  판 위치는 헤드셋이 AprilTag(ID 0)로 정한다      │");
        Console.WriteLine("  ├───────────────────────────────────────────────┤");
        Console.WriteLine("  │  x        공중의 창 전부 되돌리기               │");
        Console.WriteLine("  │  cap 1280 창 캡처 해상도 낮추기 (0 = 원본)       │");
        Console.WriteLine("  │  q 80     JPEG 품질                             │");
        Console.WriteLine("  │  Ctrl+C   종료 — 꺼낸 창 원래대로 복원          │");
        Console.WriteLine("  └───────────────────────────────────────────────┘");
        Console.WriteLine();
    }

    // 헤드셋(8080)으로 글자 명령 보내기
    static async Task SendToHeadset(string msg)
    {
        List<WebSocket> targets;
        lock (controlClients) targets = new List<WebSocket>(controlClients);
        var bytes = Encoding.UTF8.GetBytes(msg);
        foreach (var ws in targets)
        {
            if (ws == null || ws.State != WebSocketState.Open) continue;
            try { await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, default); }
            catch { }
        }
    }

    static async Task ConsoleLoop()
    {
        while (true)
        {
            string line = Console.ReadLine();
            if (line == null) { await Task.Delay(500); continue; }
            line = line.Trim();
            if (line.Length == 0) continue;

            int sp = line.IndexOf(' ');
            string cmd = (sp < 0 ? line : line.Substring(0, sp)).ToLowerInvariant();
            string arg = sp < 0 ? "" : line.Substring(sp + 1).Trim();

            switch (cmd)
            {
                case "x": case "clear":
                    await SendToHeadset("study,clear,");
                    Console.WriteLine("[콘솔] 공중의 창을 데스크탑으로 되돌림");
                    break;
                case "cap":
                    if (arg.Length > 0 && int.TryParse(arg, out int px) && px >= 0) MaxCaptureSide = px;
                    Console.WriteLine($"[캡처] 최대 긴변 = {(MaxCaptureSide <= 0 ? "원본 그대로" : MaxCaptureSide + "px")} · 품질 = {JpegQualityVar}");
                    break;
                case "q":
                    if (arg.Length > 0 && long.TryParse(arg, out long qv)) JpegQualityVar = Math.Clamp(qv, 40L, 100L);
                    Console.WriteLine($"[캡처] 품질 = {JpegQualityVar}");
                    break;
                default:
                    PrintHelp();
                    break;
            }
        }
    }
}