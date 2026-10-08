using UnityEngine;

// AR 창 ↔ AR 창을 잇는 화살표.
//
// 한 손으로 첫 창을 꼬집고, 다른 손으로 두 번째 창을 꼬집으면 QuadLinkManager가 이걸 만든다.
// 데스크탑↔AR 연결선과 똑같이:
//   - 창이 움직여도 계속 붙어 있고
//   - 붙는 위치는 두 창의 상대 위치로 자동 결정되며 (오른쪽에 있으면 오른쪽 변 ↔ 왼쪽 변 …)
//   - 중앙 손잡이를 잡아 멀리 치우면 사라진다.
//
// 창에 붙이는 게 아니라 독립 오브젝트로 존재한다 (한 창에 여러 링크가 걸릴 수 있으므로).
public class QuadLink : MonoBehaviour, ITetherCarry
{
    [Header("잇는 두 창")]
    public Transform from;
    public Transform to;

    [Header("화살표 외형 (from ——> to)")]
    public Color lineColor = new Color(0.72f, 0.55f, 0.95f, 1f);   // 보라 - 데스크탑 화살표(청록)와 구분
    public float lineWidth = 0.006f;
    public float arrowHeadLength = 0.05f;
    public float arrowHeadRadius = 0.016f;

    [Header("손잡이")]
    public float handleSize = 0.03f;
    public float handleDepth = 0.006f;
    public float grabPadding = 0.05f;
    [Range(0f, 1f)] public float idleAlpha = 0.22f;
    [Range(0f, 1f)] public float hoverAlpha = 1f;
    public float hoverGrow = 1.6f;

    [Header("이만큼(m) 이상 끌고 가서 놓으면 삭제")]
    public float removeDistance = 0.2f;

    private LineRenderer line;
    private Transform arrowHead, handle, handleVis;
    private Renderer handleRend;
    private TetherHandle handleMark;
    private Material mat, handleIdleMat, handleHoverMat;

    private bool carrying = false;
    private Vector3 carryPoint, naturalMid;

    // 색을 Awake 전에 정해야 해서, 비활성 상태로 만들고 값을 넣은 뒤 켠다.
    // (비활성 오브젝트에서는 AddComponent를 해도 Awake가 아직 안 돈다)
    public static QuadLink Create(Transform from, Transform to, Color color, string name)
    {
        var go = new GameObject(name);
        go.SetActive(false);
        var link = go.AddComponent<QuadLink>();
        link.from = from;
        link.to = to;
        link.lineColor = color;
        go.SetActive(true);          // 여기서 Awake 실행
        return link;
    }

    public bool Connects(Transform a, Transform b)
        => (from == a && to == b) || (from == b && to == a);

    void Start()
    {
        mat = DesktopTetherSpawner.MakeUnlit(lineColor, 1f);
        handleHoverMat = DesktopTetherSpawner.MakeUnlit(lineColor, hoverAlpha);
        handleIdleMat = DesktopTetherSpawner.MakeUnlit(lineColor, idleAlpha);

        var go = new GameObject("QuadLinkLine");
        line = go.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.numCapVertices = 2;
        line.numCornerVertices = 2;
        line.textureMode = LineTextureMode.Stretch;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;
        line.sharedMaterial = mat;
        line.positionCount = 2;

        arrowHead = ArrowLine.CreateHead(mat, "QuadLinkArrowHead");
        BuildHandle();
    }

    void BuildHandle()
    {
        var root = new GameObject("QuadLinkHandle");
        var col = root.AddComponent<BoxCollider>();
        col.size = new Vector3(handleSize + grabPadding, handleSize + grabPadding, handleDepth + grabPadding);
        var th = root.AddComponent<TetherHandle>();
        th.tether = this;

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
        // 창 하나라도 사라지면 링크도 사라진다
        if (from == null || to == null) { Destroy(gameObject); return; }
        if (line == null) return;

        ComputeEndpoints(out Vector3 aPt, out Vector3 bPt);
        naturalMid = (aPt + bPt) * 0.5f;

        // 잡고 있으면 꺾이지 않고 일자 그대로 통째로 따라온다
        Vector3 offset = carrying ? (carryPoint - naturalMid) : Vector3.zero;
        Vector3 startPt = aPt + offset;
        Vector3 endPt = bPt + offset;

        Vector3 dir = endPt - startPt;
        float segLen = dir.magnitude;
        float headLen = Mathf.Min(arrowHeadLength, segLen * 0.45f);
        Vector3 dirN = segLen > 1e-6f ? dir / segLen : Vector3.forward;

        line.widthCurve = AnimationCurve.Constant(0f, 1f, lineWidth);
        line.widthMultiplier = 1f;
        line.positionCount = 2;
        line.SetPosition(0, startPt);
        line.SetPosition(1, endPt - dirN * headLen);

        ArrowLine.PlaceHead(arrowHead, endPt, dirN, arrowHeadRadius, headLen);

        if (handle)
        {
            handle.position = carrying ? carryPoint : naturalMid;
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

    // 두 창의 상대 위치로 "서로 마주보는 변"의 중점을 각각 고른다 (데스크탑 연결선과 같은 규칙)
    void ComputeEndpoints(out Vector3 aPt, out Vector3 bPt)
    {
        Vector3 ac = from.position, ar = from.right, au = from.up;
        float aw = from.localScale.x, ah = from.localScale.y;
        Vector3 bc = to.position, br = to.right, bu = to.up;
        float bw = to.localScale.x, bh = to.localScale.y;

        Vector3 delta = bc - ac;
        float dx = Vector3.Dot(delta, ar);
        float dy = Vector3.Dot(delta, au);

        if (Mathf.Abs(dx) >= Mathf.Abs(dy))
        {
            if (dx >= 0f) { aPt = ac + ar * (aw * 0.5f); bPt = bc - br * (bw * 0.5f); }  // to가 오른쪽
            else          { aPt = ac - ar * (aw * 0.5f); bPt = bc + br * (bw * 0.5f); }  // to가 왼쪽
        }
        else
        {
            if (dy >= 0f) { aPt = ac + au * (ah * 0.5f); bPt = bc - bu * (bh * 0.5f); }  // to가 위
            else          { aPt = ac - au * (ah * 0.5f); bPt = bc + bu * (bh * 0.5f); }  // to가 아래
        }
    }

    // ===== 손잡이로 잡아 치우기 =====
    public void BeginCarry()
    {
        carrying = true; carryPoint = naturalMid;
        if (handleMark != null) handleMark.grabbed = true;
    }
    public void UpdateCarry(Vector3 worldPoint) { carryPoint = worldPoint; }
    public void EndCarry()
    {
        if (handleMark != null) handleMark.grabbed = false;
        if (Vector3.Distance(carryPoint, naturalMid) > removeDistance) { Destroy(gameObject); return; }
        carrying = false;
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
