using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public class PCStreamReceiver : MonoBehaviour
{
    [Header("Server — PC의 주소")]
    [Tooltip("에디터에서 링크로 실행할 땐 localhost. 퀘스트에 APK로 설치해서 쓸 땐 PC의 랜 IP를 넣어야 한다 " +
             "(예: 192.168.0.12). 서버를 켜면 콘솔에 쓸 수 있는 IP가 표시된다. " +
             "WindowCaptureHub도 이 값을 자동으로 따라간다.")]
    public string serverHost = "localhost";
    public int serverPort = 8080;

    [Tooltip("자동으로 ws://{serverHost}:{serverPort}/ 로 채워진다. 직접 쓰고 싶으면 Server Host를 비워라.")]
    public string serverUrl = "ws://localhost:8080/";

    [Header("재접속")]
    [Tooltip("서버에 못 붙었거나 붙었다가 끊겼을 때 몇 초마다 다시 시도할지. 0 이하면 한 번만 시도한다.")]
    public float retrySeconds = 2f;

    [Header("Target (전체화면 미러링용, 투명판이면 비워도 됨)")]
    public Renderer targetRenderer;

    private ClientWebSocket websocket;
    private Texture2D texture;
    private CancellationTokenSource cts;
    private readonly ConcurrentQueue<byte[]> incomingFrames = new ConcurrentQueue<byte[]>();

    // 서버가 보낸 텍스트 메시지(spawn 응답 등) 대기열
    private readonly ConcurrentQueue<string> incomingText = new ConcurrentQueue<string>();

    // 실험 진행 명령("study,...")은 spawn 대기열과 섞이면 안 되므로 따로 받는다.
    private readonly ConcurrentQueue<string> incomingStudy = new ConcurrentQueue<string>();

    private readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);

    public static PCStreamReceiver Instance { get; private set; }

    // 연결 전에 기록을 보내봐야 경고만 쌓인다 - 보내는 쪽에서 먼저 확인한다.
    public bool IsConnected => websocket != null && websocket.State == WebSocketState.Open;

    void Awake()
    {
        Instance = this;
    }

    async void Start()
    {
        // Server Host가 채워져 있으면 그걸로 URL을 만든다 (APK 배포 시 IP만 바꾸면 됨)
        if (!string.IsNullOrWhiteSpace(serverHost))
            serverUrl = $"ws://{serverHost.Trim()}:{serverPort}/";

        if (targetRenderer == null)
            targetRenderer = GetComponent<Renderer>();

        texture = new Texture2D(2, 2);
        if (targetRenderer != null)
            targetRenderer.material.mainTexture = texture;

        cts = new CancellationTokenSource();
        await ConnectLoop(cts.Token);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 붙을 때까지, 그리고 끊기면 다시 붙을 때까지 계속 시도한다.
    //
    // 예전에는 Start에서 딱 한 번만 접속해봤다. 그래서 서버를 나중에 켜거나,
    // USB가 잠깐 빠지거나, 앱을 서버보다 먼저 켜면 앱을 껐다 다시 켜야 했다.
    // 실험 중에 그러면 참가자를 기다리게 만든다.
    // ─────────────────────────────────────────────────────────────────────────
    async Task ConnectLoop(CancellationToken token)
    {
        int attempt = 0;

        while (!token.IsCancellationRequested)
        {
            attempt++;
            var ws = new ClientWebSocket();
            bool connected = false;

            try
            {
                // 로그를 매번 찍으면 콘솔이 도배되므로 처음과 10번마다만 남긴다
                if (attempt == 1 || attempt % 10 == 0)
                    Debug.Log($"[PCStream] 접속 시도 {attempt}회 → {serverUrl}");

                await ws.ConnectAsync(new Uri(serverUrl), token);

                connected = true;
                attempt = 0;
                websocket = ws;
                Debug.Log("[PCStream] 연결됨 → " + serverUrl);


                // 연결이 끊길 때까지 여기서 머문다
                await Task.Run(() => ReceiveLoop(ws, token), token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                if (attempt == 1 || attempt % 10 == 0)
                    Debug.LogWarning($"[PCStream] 접속 실패: {e.Message}");
            }
            finally
            {
                if (websocket == ws) websocket = null;
                try { ws.Dispose(); } catch { }
            }

            if (connected)
                Debug.LogWarning("[PCStream] 연결이 끊겼습니다 — 다시 붙는 중…");

            if (retrySeconds <= 0f) break;          // 재시도를 꺼두면 한 번만 시도한다
            if (token.IsCancellationRequested) break;
            try { await Task.Delay(TimeSpan.FromSeconds(Mathf.Max(0.5f, retrySeconds)), token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private float lastNotOpenWarn = -99f;

    public async void SendText(string message)
    {
        var ws = websocket;   // 보내는 도중에 재접속으로 바뀔 수 있으므로 붙잡아둔다
        if (ws == null || ws.State != WebSocketState.Open)
        {
            // 끊긴 동안 손을 움직이면 초당 수십 번 들어온다. 로그는 2초에 한 번만.
            if (Time.unscaledTime - lastNotOpenWarn > 2f)
            {
                lastNotOpenWarn = Time.unscaledTime;
                Debug.LogWarning("[PCStream] 아직 서버에 안 붙음 — 보내지 못함: " + message);
            }
            return;
        }
        var bytes = Encoding.UTF8.GetBytes(message);
        await sendLock.WaitAsync();
        try
        {
            await ws.SendAsync(new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text, true, cts.Token);
        }
        catch (Exception e)
        {
            Debug.LogError("[PCStream] Send error: " + e.Message);
        }
        finally
        {
            sendLock.Release();
        }
    }

    // 서버가 보낸 spawn 메시지를 꺼내간다 (SimpleRayPointer가 사용)
    public bool TryGetSpawnMessage(out string msg)
    {
        return incomingText.TryDequeue(out msg);
    }

    async Task ReceiveLoop(ClientWebSocket ws, CancellationToken token)
    {
        var buffer = new byte[1024 * 1024];
        var messageBuffer = new System.IO.MemoryStream();
        try
        {
            while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                messageBuffer.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    messageBuffer.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    incomingFrames.Enqueue(messageBuffer.ToArray());
                }
                else if (result.MessageType == WebSocketMessageType.Text)
                {
                    string txt = Encoding.UTF8.GetString(messageBuffer.ToArray());
                    if (txt.StartsWith("study,")) incomingStudy.Enqueue(txt);
                    else incomingText.Enqueue(txt);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            // 끊김은 예외로 올라온다. 여기서 끝내면 ConnectLoop이 다시 붙여준다.
            Debug.LogWarning("[PCStream] 수신 중단: " + e.Message);
        }
    }

    void Update()
    {
        // [AR LAPTOP] PC 콘솔 명령. 기록 기능은 없고, x(창 전부 되돌리기)만 처리한다
        while (incomingStudy.TryDequeue(out string studyMsg))
        {
            var parts = studyMsg.Split(',');
            if (parts.Length < 2) continue;
            string cmd = parts[1].Trim();
            if (cmd == "clear" && ConditionManager.Instance != null)
                ConditionManager.Instance.ClearAllWindows();
            else if ((cmd == "depth" || cmd == "tag") && parts.Length >= 3 && AprilTagFollower.Instance != null
                     && float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float one))
            {
                if (cmd == "depth") AprilTagFollower.Instance.SetDepthCm(one);
                else AprilTagFollower.Instance.SetTagCm(one);
            }
            else if ((cmd == "off" || cmd == "size") && parts.Length >= 4 && AprilTagFollower.Instance != null
                     && float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float a)
                     && float.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float b))
            {
                if (cmd == "off") AprilTagFollower.Instance.SetCornerOffsetCm(a, b);
                else AprilTagFollower.Instance.SetSizeCm(a, b);
            }
        }

        byte[] latest = null;
        while (incomingFrames.TryDequeue(out var frame))
            latest = frame;
        if (latest != null && texture != null)
            texture.LoadImage(latest);
    }

    async void OnApplicationQuit()
    {
        cts?.Cancel();
        if (websocket != null && websocket.State == WebSocketState.Open)
        {
            try { await websocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Quit", CancellationToken.None); }
            catch { }
        }
        websocket?.Dispose();
    }
}