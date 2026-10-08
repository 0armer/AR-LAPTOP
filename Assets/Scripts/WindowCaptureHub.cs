using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

// 8081에 한 번만 연결해서, 들어오는 "ID + 이미지"를 각 쿼드에 나눠준다.
// 각 WindowQuad는 자기 ID를 여기 등록하고, 그 ID의 프레임만 받는다.
public class WindowCaptureHub : MonoBehaviour
{
    public static WindowCaptureHub Instance { get; private set; }

    [Header("서버 (창 캡처 포트)")]
    [Tooltip("비워두면 PCStreamReceiver의 Server Host를 그대로 따라간다 (APK 배포 시 IP를 한 곳에서만 바꾸면 됨)")]
    public string serverHost = "";
    public int serverPort = 8081;

    public string serverUrl = "ws://localhost:8081/";

    [Header("텍스처 품질")]
    [Tooltip("켜면 멀리서/비스듬히 볼 때 격자무늬(모아레)와 반짝임이 사라진다. " +
             "끄면 아주 가까이서 약간 더 또렷하지만 모아레가 심해진다. 웬만하면 켜둘 것.")]
    public bool useMipmaps = true;
    [Tooltip("비스듬히 볼 때의 선명도. mipmap을 켜도 이 값이 높으면 글자가 흐려지지 않는다. 1~16")]
    [Range(1, 16)] public int anisoLevel = 8;
    [Tooltip("mipmap을 켤 땐 Trilinear가 단계 사이 경계까지 부드럽게 해준다")]
    public FilterMode textureFilter = FilterMode.Trilinear;
    [Tooltip("mipmap 선택을 얼마나 '선명한 쪽'으로 당길지. 0=기본(흐림), -1이면 한 단계 더 선명한 걸 쓴다. " +
             "너무 낮추면(-2 이하) 모아레/반짝임이 다시 생긴다. -0.5 ~ -1 사이가 보통 제일 좋다.")]
    [Range(-3f, 0f)] public float mipMapBias = -0.75f;

    [Header("재연결")]
    [Tooltip("연결이 끊기거나 서버가 아직 안 켜져 있으면 이 간격으로 계속 다시 붙는다")]
    public float retrySeconds = 2f;

    private ClientWebSocket websocket;
    private CancellationTokenSource cts;

    // ID별로 최신 프레임을 담아둠
    private readonly ConcurrentDictionary<long, byte[]> latestFrames = new();
    // ID별로 등록된 쿼드(Renderer)
    private readonly Dictionary<long, Renderer> registered = new();

    // 창마다 텍스처를 2장 두고 번갈아 쓴다(더블 버퍼링).
    // 한 장만 쓰면 GPU가 그 텍스처를 읽는 도중에 새 프레임을 덮어쓰게 되어,
    // 화면이 가로로 어긋나 보이는(롤링 셔터/티어링) 현상이 생긴다.
    // 뒤쪽 버퍼에 디코드한 뒤 머티리얼을 그 버퍼로 교체하면 그런 충돌이 없다.
    private readonly Dictionary<long, Texture2D[]> textures = new();
    private readonly Dictionary<long, int> frontIndex = new();

    void Awake()
    {
        Instance = this;

        // 씬에 저장된 옛 값이 덮어쓰지 못하게 코드에서 못 박는다. 글자 선명도용.
        useMipmaps    = true;
        anisoLevel    = 16;            // 비스듬히 봐도 안 흐려진다
        textureFilter = FilterMode.Trilinear;
        mipMapBias    = -1.25f;        // 한 단계 더 선명한 mip을 쓴다
    }

    void Start()
    {
        // 호스트를 직접 안 적었으면 PCStreamReceiver가 쓰는 PC 주소를 그대로 따라간다
        string host = serverHost;
        if (string.IsNullOrWhiteSpace(host) && PCStreamReceiver.Instance != null)
            host = PCStreamReceiver.Instance.serverHost;
        if (!string.IsNullOrWhiteSpace(host))
            serverUrl = $"ws://{host.Trim()}:{serverPort}/";

        cts = new CancellationTokenSource();
        _ = ConnectLoop();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 끊기면 계속 다시 붙는다.
    //
    // 예전에는 Start에서 딱 한 번만 시도하고 실패하면 끝이었다. 그래서
    //  - 서버보다 앱을 먼저 켜거나
    //  - 실험 도중 서버를 다시 켜면
    // 8080(조작)은 재연결되는데 8081(그림)은 영영 죽은 채로 남았다.
    // → 레이·클릭은 되는데 꺼낸 창이 흰 쿼드로만 보이는 증상.
    // ─────────────────────────────────────────────────────────────────────────
    async Task ConnectLoop()
    {
        int fails = 0;
        while (cts != null && !cts.IsCancellationRequested)
        {
            ClientWebSocket ws = null;
            try
            {
                ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri(serverUrl), cts.Token);
                websocket = ws;
                fails = 0;
                Debug.Log($"[Hub] 8081 연결됨 → {serverUrl}");
                await ReceiveLoop(ws, cts.Token);      // 끊길 때까지 여기서 돈다
                Debug.LogWarning("[Hub] 8081 연결이 끊겼습니다. 다시 붙습니다.");
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                fails++;
                // 처음 몇 번과 그 뒤로는 가끔만 찍는다 (로그 도배 방지)
                if (fails <= 3 || fails % 15 == 0)
                    Debug.LogWarning($"[Hub] 8081 연결 실패({fails}회): {e.Message}  대상={serverUrl}");
            }
            finally
            {
                try { ws?.Dispose(); } catch { }
                if (websocket == ws) websocket = null;
            }

            if (cts == null || cts.IsCancellationRequested) break;
            await Task.Delay(TimeSpan.FromSeconds(Mathf.Max(0.5f, retrySeconds)));
        }
    }

    public bool IsConnected => websocket != null && websocket.State == WebSocketState.Open;

    // 쿼드가 자기 ID로 등록
    public void Register(long id, Renderer renderer)
    {
        registered[id] = renderer;
        // 텍스처 2장(더블 버퍼).
        // mipmap을 켜야 멀리서/비스듬히 볼 때 글자에서 생기는 모아레(격자무늬 일렁임)가 사라진다.
        // 대신 이방성 필터를 높여서 비스듬히 봐도 흐려지지 않게 한다.
        var pair = new Texture2D[2];
        for (int i = 0; i < 2; i++)
        {
            var t = new Texture2D(2, 2, TextureFormat.RGB24, useMipmaps);
            t.filterMode = textureFilter;
            t.anisoLevel = Mathf.Clamp(anisoLevel, 1, 16);
            t.wrapMode = TextureWrapMode.Clamp;
            if (useMipmaps) t.mipMapBias = mipMapBias;   // 더 선명한 mip 단계를 쓰게 당긴다
            pair[i] = t;
        }
        textures[id] = pair;
        frontIndex[id] = 0;
        renderer.material.mainTexture = pair[0];
        Debug.Log($"[Hub] 쿼드 등록 id={id}");
    }

    // 창을 데스크탑으로 되돌릴 때(insert) 등록 해제. 안 해주면 더 이상 아무도 안 보는
    // 텍스처를 계속 디코딩하고, 쓰지도 않는 프레임 데이터가 계속 쌓인다.
    public void Unregister(long id)
    {
        registered.Remove(id);
        latestFrames.TryRemove(id, out _);
        if (textures.TryGetValue(id, out var pair))
        {
            textures.Remove(id);
            frontIndex.Remove(id);
            foreach (var t in pair) if (t != null) Destroy(t);
        }
        Debug.Log($"[Hub] 쿼드 등록 해제 id={id}");
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
                    byte[] payload = messageBuffer.ToArray();
                    if (payload.Length > 8)
                    {
                        long id = BitConverter.ToInt64(payload, 0);
                        byte[] jpeg = new byte[payload.Length - 8];
                        Array.Copy(payload, 8, jpeg, 0, jpeg.Length);
                        latestFrames[id] = jpeg;
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Debug.LogWarning("[Hub] 수신 중 끊김: " + e.Message);
        }
    }

    void Update()
    {
        // 등록된 쿼드마다, 최신 프레임이 있으면 "뒤쪽 버퍼"에 디코드한 뒤 그 버퍼로 갈아끼운다.
        // 지금 화면에 보이는 텍스처는 절대 건드리지 않으므로 가로로 어긋나 보이는 현상이 안 생긴다.
        foreach (var kv in registered)
        {
            long id = kv.Key;
            if (!latestFrames.TryGetValue(id, out var jpeg) || jpeg == null) continue;
            latestFrames[id] = null; // 소비

            if (!textures.TryGetValue(id, out var pair)) continue;
            if (!frontIndex.TryGetValue(id, out int front)) front = 0;

            int back = 1 - front;
            var target = pair[back];
            if (target == null) continue;

            if (target.LoadImage(jpeg))
            {
                var rend = kv.Value;
                if (rend != null) rend.material.mainTexture = target;
                frontIndex[id] = back;
            }
        }
    }

    async void OnDestroy()
    {
        cts?.Cancel();
        if (websocket != null && websocket.State == WebSocketState.Open)
        {
            try { await websocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Destroy", CancellationToken.None); }
            catch { }
        }
        websocket?.Dispose();
    }
}
