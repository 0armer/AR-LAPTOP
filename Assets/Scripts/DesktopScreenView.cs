using UnityEngine;

// [AR LAPTOP] 모니터 판(투명 쿼드) 위에 PC 화면 전체를 그대로 그린다.
//
// 서버(PCCaptureServer)가 주 모니터 화면을 통째로 찍어 8081로 ID = -1 프레임을 보낸다.
// 여기서는 모니터 판의 자식으로 "화면 쿼드"를 하나 만들고, 그 ID를 WindowCaptureHub에 등록한다.
//
//  · 모니터 판 자체(Renderer·Collider)는 건드리지 않는다. 레이 판정은 원래 판이 그대로 한다.
//  · 화면 쿼드는 판과 같은 메쉬(=같은 UV)를 써서, 레이가 맞힌 UV와 그림 위치가 정확히 일치한다.
//  · 화면 쿼드엔 콜라이더가 없다 → 레이를 막지 않는다.
//  · 꺼낸 창은 PC에서 투명(알파 0)이라 화면에 안 찍힌다 → 판에서 빠져나간 것처럼 보인다.
public class DesktopScreenView : MonoBehaviour
{
    public static DesktopScreenView Instance { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (Instance != null) return;
        var go = new GameObject("DesktopScreenView");
        DontDestroyOnLoad(go);
        go.AddComponent<DesktopScreenView>();
    }

    public const long DesktopFrameId = -1;   // 서버 Program.DesktopFrameId 와 같아야 함

    [Tooltip("화면을 그릴 모니터 판 번호")]
    public int monitorIndex = 0;
    [Tooltip("판보다 이만큼(m) 내 쪽으로 띄워 그린다 (겹쳐서 깜빡이는 것 방지)")]
    public float frontOffset = 0.002f;

    Transform plane;
    Transform screen;
    Renderer screenRenderer;
    bool registered;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (registered && WindowCaptureHub.Instance != null)
            WindowCaptureHub.Instance.Unregister(DesktopFrameId);
    }

    void Update()
    {
        if (plane == null)
        {
            plane = FindPlane();
            if (plane == null) return;
            screen = null;
            registered = false;
        }

        if (screen == null && !Build()) return;

        if (!registered && WindowCaptureHub.Instance != null)
        {
            WindowCaptureHub.Instance.Register(DesktopFrameId, screenRenderer);
            registered = true;
        }

        // 화면 그림이 아직 한 장도 안 왔으면(=흰 판) 이유를 5초마다 알려준다
        if (registered && Time.time - lastDiag > 5f)
        {
            lastDiag = Time.time;
            var t = screenRenderer.material.mainTexture;
            if (t == null || t.width <= 2)
            {
                var hub = WindowCaptureHub.Instance;
                Debug.LogWarning(hub != null && hub.IsConnected
                    ? "[DesktopScreen] 8081은 붙었는데 화면 그림이 안 옴 → PC 서버가 AR LAPTOP 버전인지 확인 ([데스크탑 화면] 로그가 떠야 함)"
                    : $"[DesktopScreen] 8081 연결 안 됨 → 흰 판. USB면 adb reverse tcp:8081 tcp:8081, 무선이면 PCStreamReceiver Server Host를 PC IP로 ({hub?.serverUrl})");
            }
        }
    }
    float lastDiag;

    void LateUpdate()
    {
        if (plane == null || screen == null) return;

        // AprilTag 고정 배치 모드면, 태그를 처음 보기 전까지는 화면을 숨긴다 (엉뚱한 자리에 떠 있지 않게)
        var tag = AprilTagFollower.Instance;
        screenRenderer.enabled = tag == null || !tag.fixedLayout || tag.HasEverSeen;

        // 보는 사람 쪽으로 살짝 띄운다. 판의 z 스케일이 1이 아니라서 로컬 값으로 환산.
        var cam = Camera.main;
        float side = 1f;
        if (cam != null)
            side = Vector3.Dot(plane.forward, cam.transform.position - plane.position) >= 0f ? 1f : -1f;
        float zs = Mathf.Max(1e-4f, Mathf.Abs(plane.lossyScale.z));
        screen.localPosition = new Vector3(0f, 0f, side * frontOffset / zs);
        screen.localRotation = Quaternion.identity;
        screen.localScale = Vector3.one;
    }

    Transform FindPlane()
    {
        foreach (var p in FindObjectsByType<MonitorPlane>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (p.monitorIndex == monitorIndex) return p.transform;
        return null;
    }

    bool Build()
    {
        // 꺼낸 창과 같은 머티리얼(URP Unlit)을 복사해서 쓴다. 손 포인터가 아직 안 켜졌으면 다음 프레임에.
        Material src = null;
        foreach (var p in HandRayPointer.All)
        {
            if (p == null || p.spawnQuadPrefab == null) continue;
            var r = p.spawnQuadPrefab.GetComponent<Renderer>();
            if (r != null) { src = r.sharedMaterial; break; }
        }
        if (src == null && Time.frameCount < 60) return false;

        Material mat;
        if (src != null) mat = new Material(src);
        else
        {
            var sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh == null) { Debug.LogWarning("[DesktopScreen] 머티리얼을 못 만듦"); return false; }
            mat = new Material(sh);
        }
        if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", 0f);   // 앞뒤 어느 쪽에서 봐도 보이게
        mat.name = "DesktopScreenMat";

        var go = new GameObject("DesktopScreen");
        go.transform.SetParent(plane, false);
        go.layer = plane.gameObject.layer;

        var mf = go.AddComponent<MeshFilter>();
        var pmf = plane.GetComponent<MeshFilter>();
        mf.sharedMesh = pmf != null && pmf.sharedMesh != null ? pmf.sharedMesh : BuiltinQuad();

        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = mat;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;

        screen = go.transform;
        screenRenderer = mr;
        Debug.Log($"[DesktopScreen] {monitorIndex}번 판에 데스크탑 화면 쿼드 생성");
        return true;
    }

    static Mesh BuiltinQuad()
    {
        var tmp = GameObject.CreatePrimitive(PrimitiveType.Quad);
        var m = tmp.GetComponent<MeshFilter>().sharedMesh;
        DestroyImmediate(tmp);
        return m;
    }
}
