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

    // [AR LAPTOP] JPEG는 mip 없는 임시 텍스처에 풀고(빠름), 화면에 쓰는 RenderTexture로 GPU 복사한다.
    // mipmap은 GPU가 만든다 → 예전처럼 CPU가 매 프레임 mip을 만들다 헤드셋 화면까지 끊기는 일이 없다.
    // GPU는 명령을 순서대로 처리하므로 복사 도중 화면이 찢어지는(티어링) 일도 없다.
    private readonly Dictionary<long, Texture2D> staging = new();
    private readonly Dictionary<long, RenderTexture> display = new();
    [Tooltip("한 프레임에 풀 수 있는 이미지 수 (많으면 프레임이 끊김)")]
    public int maxDecodesPerFrame = 2;
    readonly List<long> order = new();
    int rr;

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
        if (!staging.ContainsKey(id))
            staging[id] = new Texture2D(2, 2, TextureFormat.RGB24, false);
        renderer.material.mainTexture = display.TryGetValue(id, out var rt) && rt != null ? rt : staging[id];
        Debug.Log($"[Hub] 쿼드 등록 id={id}");
    }

    // 창을 데스크탑으로 되돌릴 때(insert) 등록 해제
    public void Unregister(long id)
    {
        registered.Remove(id);
        latestFrames.TryRemove(id, out _);
        if (staging.TryGetValue(id, out var t)) { staging.Remove(id); if (t != null) Destroy(t); }
        if (display.TryGetValue(id, out var rt)) { display.Remove(id); if (rt != null) { rt.Release(); Destroy(rt); } }
        Debug.Log($"[Hub] 쿼드 등록 해제 id={id}");
    }

    RenderTexture EnsureDisplay(long id, int w, int h)
    {
        if (display.TryGetValue(id, out var rt) && rt != null && rt.width == w && rt.height == h) return rt;
        if (rt != null) { rt.Release(); Destroy(rt); }
        rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
        {
            useMipMap = useMipmaps,
            autoGenerateMips = useMipmaps,
            filterMode = textureFilter,
            anisoLevel = Mathf.Clamp(anisoLevel, 1, 16),
            wrapMode = TextureWrapMode.Clamp,
            mipMapBias = useMipmaps ? mipMapBias : 0f,
            name = "Frame_" + id,
        };
        rt.Create();
        display[id] = rt;
        return rt;
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
        // 최신 프레임만 푼다(밀린 건 버림). 데스크탑(-1)을 먼저, 나머지 창은 돌아가며.
        order.Clear();
        if (registered.ContainsKey(-1)) order.Add(-1);
        int n0 = order.Count;
        foreach (var id in registered.Keys) if (id != -1) order.Add(id);
        int others = order.Count - n0;

        int done = 0;
        for (int k = 0; k < order.Count && done < Mathf.Max(1, maxDecodesPerFrame); k++)
        {
            long id = k < n0 ? order[k] : order[n0 + (rr + k - n0) % Mathf.Max(1, others)];
            if (!latestFrames.TryGetValue(id, out var jpeg) || jpeg == null) continue;
            latestFrames[id] = null; // 소비
            if (!staging.TryGetValue(id, out var tex) || tex == null) continue;

            if (tex.LoadImage(jpeg, false))
            {
                var rt = EnsureDisplay(id, tex.width, tex.height);
                Graphics.Blit(tex, rt);
                var rend = registered[id];
                if (rend != null && rend.material.mainTexture != rt) rend.material.mainTexture = rt;
            }
            done++;
        }
        rr++;
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
