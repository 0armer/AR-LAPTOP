using UnityEngine;

// 꺼내 놓은 창(WindowQuadReceiver)에 자동으로 붙는 컴포넌트.
// 그 창을 "꺼낸 데스크탑(모니터 판)"과 이어주는 연결선을 그린다.
//
//  - 창이 움직여도 선은 계속 양쪽에 붙어 있다.
//  - 붙는 위치는 창과 모니터의 상대 위치로 자동 결정:
//      창이 모니터 오른쪽에 있으면  → 모니터 오른쪽 변 ↔ 창 왼쪽 변
//      창이 모니터 왼쪽 →            모니터 왼쪽 ↔ 창 오른쪽
//      창이 모니터 위 →              모니터 위 ↔ 창 아래
//      창이 모니터 아래 →            모니터 아래 ↔ 창 위
//  - 선 중앙의 구체를 주먹으로 잡아 멀리(removeDistance 이상) 치우고 놓으면 선이 사라진다.
//
// 모니터 판은 MonitorPlane(sourceMonitorIndex와 같은 index)을 찾아서 쓰고,
// 없으면 PCStreamReceiver.targetRenderer(주 모니터 투명판)로 대체한다.
[DisallowMultipleComponent]
public class WindowTether : MonoBehaviour, ITetherCarry
{
    [Header("화살표 외형 (데스크탑 ——> AR 창)")]
    public Color lineColor = new Color(0.22f, 0.83f, 0.90f, 1f);
    public float lineWidth = 0.006f;
    [Tooltip("화살촉 길이(m)")]
    public float arrowHeadLength = 0.05f;
    [Tooltip("화살촉 밑면 반지름(m)")]
    public float arrowHeadRadius = 0.016f;

    [Header("손잡이 - 얇은 사각형 (보이는 크기)")]
    public float handleSize = 0.03f;
    public float handleDepth = 0.006f;
    [Tooltip("자석: 포인터가 이만큼(m) 근처에 오면 잡힌다")]
    public float grabPadding = 0.05f;
    [Range(0f, 1f)] public float idleAlpha = 0.22f;
    [Range(0f, 1f)] public float hoverAlpha = 1f;
    public float hoverGrow = 1.6f;

    [Header("삭제 판정: 중앙 구체를 이만큼(m) 이상 끌고 가서 놓으면 선 삭제")]
    public float removeDistance = 0.2f;

    private WindowQuadReceiver receiver;
    private Transform monitor;
    private LineRenderer line;
    private Transform handle, handleVis, arrowHead;
    private Renderer handleRend;
    private TetherHandle handleMark;
    private Material mat, handleIdleMat, handleHoverMat;

    private bool carrying = false;
    private Vector3 carryPoint;
    private Vector3 naturalMid;

    void Start()
    {
        receiver = GetComponent<WindowQuadReceiver>();
        ResolveMonitor();
        BuildLine();
        BuildHandle();
    }

    void ResolveMonitor()
    {
        int idx = receiver != null ? receiver.sourceMonitorIndex : 0;
        var planes = FindObjectsByType<MonitorPlane>(FindObjectsSortMode.None);
        foreach (var p in planes)
            if (p.monitorIndex == idx) { monitor = p.transform; break; }
        if (monitor == null && planes.Length > 0) monitor = planes[0].transform;
        if (monitor == null && PCStreamReceiver.Instance != null && PCStreamReceiver.Instance.targetRenderer != null)
            monitor = PCStreamReceiver.Instance.targetRenderer.transform;
    }

    void BuildLine()
    {
        mat = DesktopTetherSpawner.MakeUnlit(lineColor, 1f);                       // 선 자체는 또렷하게
        handleHoverMat = DesktopTetherSpawner.MakeUnlit(lineColor, hoverAlpha);    // 손잡이: 닿았을 때
        handleIdleMat = DesktopTetherSpawner.MakeUnlit(lineColor, idleAlpha);      // 손잡이: 평소
        var go = new GameObject("TetherLine");   // 창의 자식으로 두지 않음 (비균일 스케일 영향 방지)
        line = go.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.numCapVertices = 2;
        line.numCornerVertices = 2;
        line.textureMode = LineTextureMode.Stretch;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;
        line.sharedMaterial = mat;
        line.startWidth = lineWidth;
        line.endWidth = lineWidth;
        line.positionCount = 2;

        arrowHead = ArrowLine.CreateHead(mat, "TetherArrowHead");
    }

    void BuildHandle()
    {
        // 루트: 보이지 않는 "잡히는 범위" 콜라이더만 (넉넉하게 = 자석처럼 잡힘)
        var root = new GameObject("TetherHandle");
        var col = root.AddComponent<BoxCollider>();
        col.size = new Vector3(handleSize + grabPadding, handleSize + grabPadding, handleDepth + grabPadding);
        var th = root.AddComponent<TetherHandle>();
        th.tether = this;

        // 자식: 실제로 보이는 얇은 사각형 (콜라이더 없음)
        var vis = GameObject.CreatePrimitive(PrimitiveType.Cube);
        vis.name = "vis";
        var c = vis.GetComponent<Collider>();
        if (c != null) Destroy(c);
        var r = vis.GetComponent<Renderer>();
        if (r != null)
        {
            r.sharedMaterial = handleIdleMat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }
        vis.transform.SetParent(root.transform, false);
        vis.transform.localScale = new Vector3(handleSize, handleSize, handleDepth);

        handle = root.transform;
        handleVis = vis.transform;
        handleRend = r;
        handleMark = th;
    }

    void LateUpdate()
    {
        if (line == null) return;
        if (monitor == null)
        {
            line.enabled = false;
            if (handle) handle.gameObject.SetActive(false);
            if (arrowHead) arrowHead.gameObject.SetActive(false);
            return;
        }
        if (arrowHead && !arrowHead.gameObject.activeSelf) arrowHead.gameObject.SetActive(true);

        ComputeEndpoints(out Vector3 monPt, out Vector3 winPt);
        naturalMid = (monPt + winPt) * 0.5f;

        // 데스크탑(monPt) ——> AR 창(winPt) 화살표.
        // 몸통은 굵기 일정한 선으로 그리되 화살촉 길이만큼 짧게 끝내고, 그 끝에 원뿔 화살촉을 붙인다.
        line.widthCurve = AnimationCurve.Constant(0f, 1f, lineWidth);
        line.widthMultiplier = 1f;

        // 손잡이를 잡고 있으면 선이 꺾이지 않고, 일자 모양 그대로 통째로 손을 따라 옮겨진다.
        // (그 상태로 멀리 가져가서 놓으면 삭제)
        Vector3 offset = carrying ? (carryPoint - naturalMid) : Vector3.zero;
        Vector3 startPt = monPt + offset;
        Vector3 endPt = winPt + offset;

        Vector3 dir = endPt - startPt;
        float segLen = dir.magnitude;
        float headLen = Mathf.Min(arrowHeadLength, segLen * 0.45f);   // 선이 짧을 땐 화살촉도 줄인다
        Vector3 dirN = segLen > 1e-6f ? dir / segLen : Vector3.forward;

        line.positionCount = 2;
        line.SetPosition(0, startPt);
        line.SetPosition(1, endPt - dirN * headLen);

        ArrowLine.PlaceHead(arrowHead, endPt, dirN, arrowHeadRadius, headLen);

        if (handle)
        {
            handle.position = carrying ? carryPoint : naturalMid;

            // 평소엔 반투명, 포인터가 닿으면 또렷해지고 살짝 커진다
            bool hov = handleMark != null && handleMark.IsHovered;
            if (handleVis != null)
            {
                float s = hov ? hoverGrow : 1f;
                handleVis.localScale = new Vector3(handleSize * s, handleSize * s, handleDepth * (hov ? 1.5f : 1f));
            }
            if (handleRend != null)
            {
                var want = hov ? handleHoverMat : handleIdleMat;
                if (handleRend.sharedMaterial != want) handleRend.sharedMaterial = want;
            }
        }
    }

    // 모니터/창의 상대 위치로 "서로 마주보는 변"의 중점을 각각 고른다.
    void ComputeEndpoints(out Vector3 monPt, out Vector3 winPt)
    {
        Vector3 mc = monitor.position, mr = monitor.right, mu = monitor.up;
        float mw = monitor.localScale.x, mh = monitor.localScale.y;
        Vector3 wc = transform.position, wr = transform.right, wu = transform.up;
        float ww = transform.localScale.x, wh = transform.localScale.y;

        Vector3 delta = wc - mc;
        float dx = Vector3.Dot(delta, mr);   // 모니터 기준 좌우 오프셋
        float dy = Vector3.Dot(delta, mu);   // 모니터 기준 상하 오프셋

        if (Mathf.Abs(dx) >= Mathf.Abs(dy))
        {
            if (dx >= 0f) { monPt = mc + mr * (mw * 0.5f); winPt = wc - wr * (ww * 0.5f); } // 창이 오른쪽
            else          { monPt = mc - mr * (mw * 0.5f); winPt = wc + wr * (ww * 0.5f); } // 창이 왼쪽
        }
        else
        {
            if (dy >= 0f) { monPt = mc + mu * (mh * 0.5f); winPt = wc - wu * (wh * 0.5f); } // 창이 위
            else          { monPt = mc - mu * (mh * 0.5f); winPt = wc + wu * (wh * 0.5f); } // 창이 아래
        }
    }

    // ===== 포인터가 호출: 선 잡아서 치우기 =====
    public void BeginCarry()
    {
        carrying = true; carryPoint = naturalMid;
        if (handleMark != null) handleMark.grabbed = true;
    }
    public void UpdateCarry(Vector3 worldPoint) { carryPoint = worldPoint; }
    public void EndCarry()
    {
        if (handleMark != null) handleMark.grabbed = false;
        // 멀리 치웠으면 삭제, 아니면 제자리로
        if (Vector3.Distance(carryPoint, naturalMid) > removeDistance) { RemoveSelf(); return; }
        carrying = false;
    }

    public void RemoveSelf()
    {
        if (handle != null) Destroy(handle.gameObject);
        if (arrowHead != null) Destroy(arrowHead.gameObject);
        if (line != null) Destroy(line.gameObject);
        Destroy(this);   // 창은 그대로 두고 이 tether 컴포넌트만 제거
    }

    void OnDestroy()
    {
        if (handle != null) Destroy(handle.gameObject);
        if (arrowHead != null) Destroy(arrowHead.gameObject);
        if (line != null) Destroy(line.gameObject);
        if (mat != null) Destroy(mat);
        if (handleIdleMat != null) Destroy(handleIdleMat);
        if (handleHoverMat != null) Destroy(handleHoverMat);
    }
}
