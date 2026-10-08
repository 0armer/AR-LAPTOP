using System.Collections.Generic;
using UnityEngine;

// 데스크탑(모니터 판) 네 변에 얇은 네모 핸들을 만들고, 거기서 화살표를 끌어내 AR 창에 이어주는 관리자.
//
//  - 핸들을 핀치로 잡으면 그 지점에서 화살표 미리보기가 나온다.
//  - 그 상태로 AR 창을 가리키고 손을 놓으면, 그 창에 연결선(WindowTether)이 다시 생긴다.
//    → 연결선을 잡아서 치워 없앤 뒤에도 이렇게 다시 만들 수 있다.
//  - 허공에서 놓으면 그냥 취소된다.
//
// 씬에 아무것도 안 넣어도 되도록, 실행 시 자동으로 하나 생성된다.
public class DesktopTetherSpawner : MonoBehaviour
{
    public static DesktopTetherSpawner Instance { get; private set; }

    // 더 이상 자동으로 만들지 않는다.
    // 데스크탑 가장자리의 상호작용 직육면체(손잡이)를 없앴기 때문.
    // 선은 이제 AR 창 ↔ AR 창 꼬집기(QuadLinkManager)로만 만든다.
    // MakeUnlit은 static이라 인스턴스가 없어도 다른 스크립트가 계속 쓸 수 있다.
    public static void Spawn()
    {
        if (Instance != null) return;
        var go = new GameObject("DesktopTetherSpawner");
        go.AddComponent<DesktopTetherSpawner>();
    }

    [Header("핸들 외형 - 창의 것과 같은 얇은 네모")]
    public float handleThickness = 0.006f;
    public float handleDepth = 0.004f;
    [Range(0.1f, 1f)] public float edgeLengthRatio = 0.3f;
    public float edgeLengthMin = 0.06f;
    public float edgeLengthMax = 0.26f;

    [Header("모니터 판보다 앞으로 살짝 띄우기(m) - 레이가 판보다 먼저 맞도록. 반대면 음수로")]
    public float forwardOffset = 0.02f;

    [Header("자석 범위(m)")]
    public float grabPadding = 0.05f;

    [Header("색 / 투명도 — 평소엔 완전 투명(안 보임), 포인터가 닿을 때만 나타남")]
    public Color idleColor = new Color(0.20f, 0.62f, 0.70f, 1f);
    public Color hoverColor = new Color(0.55f, 0.97f, 1f, 1f);
    [Range(0f, 1f)] public float idleAlpha = 0f;
    [Range(0f, 1f)] public float hoverAlpha = 0.75f;
    public float hoverThicken = 1.7f;

    [Header("끌어낼 때 보이는 화살표")]
    public float lineWidth = 0.006f;
    public float arrowHeadLength = 0.05f;
    public float arrowHeadRadius = 0.016f;

    class MonSet
    {
        public MonitorPlane plane;
        public Transform[] roots = new Transform[4];
        public Transform[] vis = new Transform[4];
        public DesktopEdgeHandle[] marks = new DesktopEdgeHandle[4];
        public Renderer[] rends = new Renderer[4];
    }

    private readonly List<MonSet> sets = new();
    private Material idleMat, hoverMat, arrowMat;
    private float nextScan = 0f;

    // 주먹을 쥐고 있는 동안에는 이 핸들들을 아예 통과시킨다.
    // (창을 주먹으로 잡아 데스크탑 밖으로 꺼낼 때 핸들에 레이가 착 붙어 방해되는 것 방지)
    private int lastPassThroughFrame = -1000;
    public void NotifyPassThrough() { lastPassThroughFrame = Time.frameCount; }
    bool PassThroughNow => (Time.frameCount - lastPassThroughFrame) <= 2;

    // 드래그 상태
    private bool dragging = false;
    private DesktopEdgeHandle dragHandle = null;
    private Vector3 dragStart, dragCurrent;
    private LineRenderer preview;
    private Transform previewHead;

    void Awake()
    {
        Instance = this;
        idleMat = MakeUnlit(idleColor, idleAlpha);
        hoverMat = MakeUnlit(hoverColor, hoverAlpha);
        arrowMat = MakeUnlit(hoverColor, 1f);
    }

    void Update()
    {
        // 모니터 판은 MRUK 앵커 바인딩 이후에 생길 수 있으니 주기적으로 다시 훑는다.
        if (Time.time >= nextScan)
        {
            nextScan = Time.time + 1f;
            Rescan();
        }
    }

    void Rescan()
    {
        var planes = FindObjectsByType<MonitorPlane>(FindObjectsSortMode.None);

        // 사라진 판 정리
        for (int i = sets.Count - 1; i >= 0; i--)
            if (sets[i].plane == null) { DestroySet(sets[i]); sets.RemoveAt(i); }

        foreach (var p in planes)
        {
            bool known = false;
            foreach (var s in sets) if (s.plane == p) { known = true; break; }
            if (!known) sets.Add(BuildSet(p));
        }
    }

    MonSet BuildSet(MonitorPlane plane)
    {
        var set = new MonSet { plane = plane };
        for (int e = 0; e < 4; e++)
        {
            var root = new GameObject($"DesktopHandle_{plane.monitorIndex}_{e}");
            var col = root.AddComponent<BoxCollider>();
            var mark = root.AddComponent<DesktopEdgeHandle>();
            mark.owner = this;
            mark.monitorIndex = plane.monitorIndex;
            mark.monitor = plane.transform;
            mark.edge = e;

            var vis = GameObject.CreatePrimitive(PrimitiveType.Cube);
            vis.name = "vis";
            var c = vis.GetComponent<Collider>();
            if (c != null) Destroy(c);
            var r = vis.GetComponent<Renderer>();
            if (r != null)
            {
                r.sharedMaterial = idleMat;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
            }
            vis.transform.SetParent(root.transform, false);

            mark.visual = vis.transform;
            set.roots[e] = root.transform;
            set.vis[e] = vis.transform;
            set.marks[e] = mark;
            set.rends[e] = r;
            col.center = Vector3.zero;
        }
        return set;
    }

    void DestroySet(MonSet s)
    {
        for (int e = 0; e < 4; e++) if (s.roots[e] != null) Destroy(s.roots[e].gameObject);
    }

    void LateUpdate()
    {
        foreach (var s in sets)
        {
            if (s.plane == null) continue;
            var m = s.plane.transform;
            float w = m.localScale.x, h = m.localScale.y;
            Vector3 c = m.position + m.forward * forwardOffset;
            Vector3 right = m.right, up = m.up;
            Quaternion rot = m.rotation;

            float barW = Mathf.Clamp(w * edgeLengthRatio, edgeLengthMin, edgeLengthMax);
            float barH = Mathf.Clamp(h * edgeLengthRatio, edgeLengthMin, edgeLengthMax);

            Place(s, 0, c - right * (w * 0.5f), rot, new Vector3(handleThickness, barH, handleDepth));
            Place(s, 1, c + right * (w * 0.5f), rot, new Vector3(handleThickness, barH, handleDepth));
            Place(s, 2, c + up * (h * 0.5f),    rot, new Vector3(barW, handleThickness, handleDepth));
            Place(s, 3, c - up * (h * 0.5f),    rot, new Vector3(barW, handleThickness, handleDepth));
        }

        UpdatePreview();
    }

    void Place(MonSet s, int e, Vector3 pos, Quaternion rot, Vector3 size)
    {
        var root = s.roots[e];
        if (root == null) return;
        root.position = pos;
        root.rotation = rot;
        root.localScale = Vector3.one;

        bool hov = s.marks[e] != null && s.marks[e].IsHovered;

        Vector3 vs = size;
        if (hov)
        {
            if (size.x < size.y) vs.x *= hoverThicken; else vs.y *= hoverThicken;
            vs.z *= 1.5f;
        }
        s.vis[e].localScale = vs;

        if (s.rends[e] != null)
        {
            var want = hov ? hoverMat : idleMat;
            if (s.rends[e].sharedMaterial != want) s.rends[e].sharedMaterial = want;
        }

        var col = root.GetComponent<BoxCollider>();
        if (col != null)
        {
            col.size = new Vector3(size.x + grabPadding, size.y + grabPadding, size.z + grabPadding);
            // 주먹을 쥐고 있는 동안엔 콜라이더를 꺼서 레이가 그냥 통과하게 한다
            bool on = !PassThroughNow;
            if (col.enabled != on) col.enabled = on;
        }
    }

    // ===== 포인터가 호출 =====
    public void BeginDrag(DesktopEdgeHandle h, Vector3 point)
    {
        if (h == null) return;
        dragging = true;
        dragHandle = h;
        h.grabbed = true;
        dragStart = point;
        dragCurrent = point;
    }

    public void UpdateDrag(Vector3 point)
    {
        if (!dragging) return;
        dragCurrent = point;
    }

    // 손을 놓았을 때: 가리키던 AR 창이 있으면 그 창에 연결선을 (다시) 만든다.
    public void EndDrag(WindowQuadReceiver target)
    {
        if (!dragging) return;
        dragging = false;
        if (dragHandle != null)
        {
            if (target != null)
            {
                target.sourceMonitorIndex = dragHandle.monitorIndex;   // 이 모니터에 붙도록
                if (target.GetComponent<WindowTether>() == null)
                {
                    target.gameObject.AddComponent<WindowTether>();
                    Debug.Log($"[DesktopTether] 연결선 다시 생성: id={target.windowId}");
                }
                else Debug.Log("[DesktopTether] 이미 연결선이 있는 창");
            }
            dragHandle.grabbed = false;
        }
        dragHandle = null;
        if (preview != null) preview.enabled = false;
        if (previewHead != null) previewHead.gameObject.SetActive(false);
    }

    void UpdatePreview()
    {
        if (!dragging)
        {
            if (preview != null) preview.enabled = false;
            if (previewHead != null) previewHead.gameObject.SetActive(false);
            return;
        }

        if (preview == null)
        {
            var go = new GameObject("TetherPreview");
            preview = go.AddComponent<LineRenderer>();
            preview.useWorldSpace = true;
            preview.numCapVertices = 2;
            preview.numCornerVertices = 2;
            preview.textureMode = LineTextureMode.Stretch;
            preview.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            preview.receiveShadows = false;
            preview.sharedMaterial = arrowMat;
            previewHead = ArrowLine.CreateHead(arrowMat, "TetherPreviewHead");
        }
        preview.enabled = true;
        if (previewHead != null && !previewHead.gameObject.activeSelf) previewHead.gameObject.SetActive(true);

        // 몸통은 화살촉 길이만큼 짧게, 그 끝에 원뿔 화살촉
        Vector3 dir = dragCurrent - dragStart;
        float segLen = dir.magnitude;
        float headLen = Mathf.Min(arrowHeadLength, segLen * 0.45f);
        Vector3 dirN = segLen > 1e-6f ? dir / segLen : Vector3.forward;

        preview.widthCurve = AnimationCurve.Constant(0f, 1f, lineWidth);
        preview.widthMultiplier = 1f;
        preview.positionCount = 2;
        preview.SetPosition(0, dragStart);
        preview.SetPosition(1, dragCurrent - dirN * headLen);

        ArrowLine.PlaceHead(previewHead, dragCurrent, dirN, arrowHeadRadius, headLen);
    }

    void OnDestroy()
    {
        foreach (var s in sets) DestroySet(s);
        if (preview != null) Destroy(preview.gameObject);
        if (previewHead != null) Destroy(previewHead.gameObject);
        if (idleMat != null) Destroy(idleMat);
        if (hoverMat != null) Destroy(hoverMat);
        if (arrowMat != null) Destroy(arrowMat);
        if (Instance == this) Instance = null;
    }

    public static Material MakeUnlit(Color c, float alpha)
    {
        Shader sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null) sh = Shader.Find("Unlit/Color");
        if (sh == null) sh = Shader.Find("Sprites/Default");
        var m = new Material(sh);

        c.a = Mathf.Clamp01(alpha);
        if (alpha < 0.999f)
        {
            // URP Unlit을 반투명으로 전환 (RenderType 태그까지 바꿔야 확실히 반투명으로 그려진다)
            m.SetOverrideTag("RenderType", "Transparent");
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 0f);
            m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_ZWrite", 0f);
            m.SetFloat("_AlphaClip", 0f);
            m.SetFloat("_Cull", 0f);   // 양면 - 원뿔 화살촉이 어느 쪽에서 봐도 보이게
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.DisableKeyword("_ALPHATEST_ON");
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        return m;
    }
}
