using UnityEngine;

// 꺼내 놓은 창(WindowQuadReceiver)에 자동으로 붙는 컴포넌트.
//
// 두 종류의 핸들(얇은 막대/사각형)을 만든다. 역할이 완전히 다르다:
//
//   - 변 중앙 4개(Left/Right/Top/Bottom) = "실제 창"을 리사이즈.
//     그 축만 늘려서 가로세로 비율을 바꾸고, PC로 wresize를 보내 실제 창을 그 크기로 바꾼다.
//     → 앱이 리플로우해서 내용이 더 보인다(문서 줄이 더 나옴).
//
//   - 꼭짓점 4개(TL/TR/BL/BR) = "쿼드(보이는 판)"의 크기만 조절. 양손으로 잡고 키우던 동작의 대체다.
//     반대쪽 꼭짓점을 고정한 채 비율을 유지하며 커지거나 작아진다.
//     PC 창은 전혀 건드리지 않으므로 리플로우도 없고, 그냥 화면이 크게/작게 보일 뿐이다.
//
// 핸들은 눈에 거슬리지 않게 얇게 그리되(보이는 건 자식 visual), 실제로 잡히는 콜라이더는
// grabPadding만큼 넉넉하게 둔다. 그래서 포인터를 근처에 대기만 해도 자석처럼 잡히고,
// 닿는 순간 색이 밝아지며 살짝 두꺼워져서 "붙었다"는 게 눈에 보인다.
[DisallowMultipleComponent]
public class EdgeResizer : MonoBehaviour
{
    public enum Edge { Left, Right, Top, Bottom, TL, TR, BL, BR }

    [Header("픽셀 <-> 미터 (SpawnQuad의 pixelsToMeters와 동일하게 맞출 것)")]
    public float pixelsToMeters = 0.0005f;

    [Header("최소/최대 크기 (미터)")]
    public float minSize = 0.15f;
    public float maxSize = 6f;

    [Header("PC로 실제 창 리사이즈 명령 보내기 (변 중앙 핸들만 해당)")]
    public bool sendToPC = true;
    public float sendRate = 30f;
    public int minPixels = 120;

    [Header("핸들 외형 - 얇은 막대")]
    [Tooltip("막대의 두께(m). 얇을수록 눈에 덜 거슬림")]
    public float handleThickness = 0.012f;
    [Tooltip("막대의 앞뒤 두께(m)")]
    public float handleDepth = 0.006f;
    [Tooltip("변 막대의 길이 = 그 변 길이 × 이 비율")]
    [Range(0.1f, 1f)] public float edgeLengthRatio = 0.3f;
    [Tooltip("변 막대 길이 하한/상한(m)")]
    public float edgeLengthMin = 0.06f;
    public float edgeLengthMax = 0.26f;
    [Tooltip("꼭짓점 사각형의 한 변 길이(m)")]
    public float cornerLength = 0.026f;

    [Header("쿼드 크기에 따라 핸들도 같이 줄고 커짐")]
    [Tooltip("이 크기(m)의 창일 때 위 수치가 그대로 적용된다. 창이 작아지면 핸들도 비례해서 작아짐")]
    public float referenceQuadSize = 0.5f;
    [Tooltip("핸들이 무한정 작아/커지지 않도록 하는 배율 범위")]
    public float minHandleScale = 0.4f;
    public float maxHandleScale = 1.6f;

    [Header("쿼드 바깥으로 얼마나 띄울지(m) - 화면 위에 겹쳐서 가리지 않도록")]
    public float outsideGap = 0.008f;

    [Header("자석: 포인터가 이만큼(m) 근처에 오면 잡힌다 (보이는 것보다 넉넉하게)")]
    public float grabPadding = 0.05f;
    [Tooltip("창이 작아져도 이 이하로는 자석 범위가 줄지 않는다 (작은 창도 잡기 쉽게)")]
    public float grabPaddingMin = 0.03f;

    [Header("평소에는 투명하게, 포인터가 닿으면 또렷해짐")]
    [Range(0f, 1f)] public float idleAlpha = 0f;
    [Tooltip("포인터가 닿았을 때의 불투명도. 1이면 완전 불투명")]
    [Range(0f, 1f)] public float hoverAlpha = 0.75f;

    [Header("강조 시 두께 배율")]
    public float hoverThicken = 1.7f;

    [Header("색")]
    public Color edgeColor = new Color(0.20f, 0.62f, 0.70f, 1f);     // 변 (차분한 청록)
    public Color cornerColor = new Color(0.75f, 0.56f, 0.22f, 1f);   // 꼭짓점 (차분한 주황)
    public Color hoverColor = new Color(0.55f, 0.97f, 1f, 1f);       // 닿았을 때 (밝게)

    private WindowQuadReceiver receiver;
    private readonly Transform[] roots = new Transform[8];        // 콜라이더(잡히는 범위)
    private readonly Transform[] visuals = new Transform[8];      // 실제로 보이는 얇은 막대
    private readonly WindowEdgeHandle[] marks = new WindowEdgeHandle[8];
    private readonly Renderer[] rends = new Renderer[8];
    private Material edgeMat, cornerMat, hoverMat;

    // 리사이즈 진행 상태
    private bool resizing = false;
    private Edge edge;
    private bool isCorner = false;
    private float lastSendTime;

    private float curScale = 1f;                                  // 이번 프레임 핸들 크기 배율(창 크기에 비례)
    private Vector3 fixedEdgeWorld;                               // 변 리사이즈용
    private Vector3 fixedCornerWorld;                             // 꼭짓점 리사이즈용
    private float startW, startH, startDiag, signR, signU;

    public bool IsResizing => resizing;

    void Awake() { receiver = GetComponent<WindowQuadReceiver>(); }
    void Start() { CreateHandles(); }

    static bool IsCornerEdge(Edge e) => e == Edge.TL || e == Edge.TR || e == Edge.BL || e == Edge.BR;

    void CreateHandles()
    {
        // 평소엔 반투명(거의 안 보이게), 닿으면 불투명하게 또렷이
        edgeMat = DesktopTetherSpawner.MakeUnlit(edgeColor, idleAlpha);
        cornerMat = DesktopTetherSpawner.MakeUnlit(cornerColor, idleAlpha);
        hoverMat = DesktopTetherSpawner.MakeUnlit(hoverColor, hoverAlpha);

        for (int i = 0; i < 8; i++)
        {
            var e = (Edge)i;
            bool corner = IsCornerEdge(e);

            // 루트: 보이지 않는 "잡히는 범위" 콜라이더만 (스케일 1 고정 → 콜라이더 size가 곧 월드 크기)
            var root = new GameObject("Handle_" + e);
            var col = root.AddComponent<BoxCollider>();
            var mark = root.AddComponent<WindowEdgeHandle>();
            mark.owner = this;
            mark.edge = e;

            // 자식: 실제로 보이는 얇은 막대 (콜라이더 없음 - 레이는 루트의 넉넉한 콜라이더로만 잡힘)
            var vis = GameObject.CreatePrimitive(PrimitiveType.Cube);
            vis.name = "vis";
            var c = vis.GetComponent<Collider>();
            if (c != null) Destroy(c);
            var r = vis.GetComponent<Renderer>();
            if (r != null)
            {
                r.sharedMaterial = corner ? cornerMat : edgeMat;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
            }
            vis.transform.SetParent(root.transform, false);

            mark.visual = vis.transform;

            roots[i] = root.transform;
            visuals[i] = vis.transform;
            marks[i] = mark;
            rends[i] = r;
            col.center = Vector3.zero;
        }
        LayoutHandles();
    }

    void LateUpdate() { LayoutHandles(); }

    // 창을 손에 들고 있는 동안엔 핸들을 꺼서 레이가 통과하게 한다.
    // (안 그러면 들고 있는 창/핸들이 레이를 막아서 뒤에 있는 데스크탑 판을 못 맞힌다)
    public void SetHandlesActive(bool on)
    {
        for (int i = 0; i < roots.Length; i++)
            if (roots[i] != null && roots[i].gameObject.activeSelf != on)
                roots[i].gameObject.SetActive(on);
    }

    void LayoutHandles()
    {
        if (roots[0] == null) return;

        float w = transform.localScale.x, hgt = transform.localScale.y;
        Vector3 c = transform.position, right = transform.right, up = transform.up;
        Quaternion rot = transform.rotation;

        // 창이 작아지면 핸들도 같이 작아지도록 배율을 구한다 (짧은 변 기준, 위아래로 클램프)
        float refSide = Mathf.Min(w, hgt);
        curScale = Mathf.Clamp(refSide / Mathf.Max(referenceQuadSize, 1e-4f), minHandleScale, maxHandleScale);

        float thick = handleThickness * curScale;
        float depth = handleDepth * curScale;
        float lenMin = edgeLengthMin * curScale;
        float lenMax = edgeLengthMax * curScale;

        float barW = Mathf.Clamp(w * edgeLengthRatio, lenMin, lenMax);   // 위/아래 막대 길이
        float barH = Mathf.Clamp(hgt * edgeLengthRatio, lenMin, lenMax); // 좌/우 막대 길이

        // 핸들이 쿼드 화면 위에 겹치지 않도록, 각자 자기 두께의 절반 + 여유만큼 바깥으로 민다.
        float gap = outsideGap * curScale;
        float outEdge = thick * 0.5f + gap;                     // 변 막대가 밖으로 나가는 거리
        float corner = cornerLength * curScale;
        float outCorner = corner * 0.5f + gap;                  // 꼭짓점 사각형이 밖으로 나가는 거리

        float hw = w * 0.5f, hh = hgt * 0.5f;

        // 변: 얇은 막대 (좌우 = 세로 막대, 위아래 = 가로 막대) — 전부 쿼드 바깥에 놓임
        Set(Edge.Left,   c - right * (hw + outEdge),  rot, new Vector3(thick, barH, depth));
        Set(Edge.Right,  c + right * (hw + outEdge),  rot, new Vector3(thick, barH, depth));
        Set(Edge.Top,    c + up    * (hh + outEdge),  rot, new Vector3(barW, thick, depth));
        Set(Edge.Bottom, c - up    * (hh + outEdge),  rot, new Vector3(barW, thick, depth));

        // 꼭짓점: 작은 얇은 사각형 — 대각선으로 바깥에 놓임
        Vector3 cs = new Vector3(corner, corner, depth);
        Set(Edge.TL, c - right * (hw + outCorner) + up * (hh + outCorner), rot, cs);
        Set(Edge.TR, c + right * (hw + outCorner) + up * (hh + outCorner), rot, cs);
        Set(Edge.BL, c - right * (hw + outCorner) - up * (hh + outCorner), rot, cs);
        Set(Edge.BR, c + right * (hw + outCorner) - up * (hh + outCorner), rot, cs);
    }

    void Set(Edge e, Vector3 pos, Quaternion rot, Vector3 size)
    {
        int i = (int)e;
        var root = roots[i];
        if (root == null) return;

        root.position = pos;
        root.rotation = rot;
        root.localScale = Vector3.one;   // 콜라이더 size를 월드 단위로 쓰기 위해 항상 1

        bool hov = marks[i] != null && marks[i].IsHovered;

        // 보이는 막대: 닿으면 살짝 두꺼워지고 밝아진다 (자석에 붙은 느낌)
        Vector3 vs = size;
        if (hov)
        {
            // 얇은 축(두께)만 키운다 - 길이는 그대로
            if (IsCornerEdge(e)) { vs.x *= 1.5f; vs.y *= 1.5f; }
            else if (size.x < size.y) vs.x *= hoverThicken;   // 세로 막대 → 가로 두께
            else vs.y *= hoverThicken;                        // 가로 막대 → 세로 두께
            vs.z *= 1.5f;
        }
        visuals[i].localScale = vs;

        if (rends[i] != null)
        {
            var want = hov ? hoverMat : (IsCornerEdge(e) ? cornerMat : edgeMat);
            if (rends[i].sharedMaterial != want) rends[i].sharedMaterial = want;
        }

        // 잡히는 범위(콜라이더)는 보이는 것보다 항상 넉넉하게 = 자석 효과.
        // 창이 작아지면 범위도 같이 줄지만, 너무 줄어 잡기 힘들지 않게 하한을 둔다.
        float pad = Mathf.Max(grabPadding * curScale, grabPaddingMin);
        var col = root.GetComponent<BoxCollider>();
        if (col != null)
            col.size = new Vector3(size.x + pad, size.y + pad, size.z + pad);
    }

    // ===== 포인터가 호출하는 리사이즈 API =====
    public void BeginResize(Edge e, Vector3 rayOrigin, Vector3 rayDir)
    {
        resizing = true;
        edge = e;
        isCorner = IsCornerEdge(e);
        if (marks[(int)e] != null) marks[(int)e].grabbed = true;

        float w = transform.localScale.x, hgt = transform.localScale.y;
        Vector3 c = transform.position, right = transform.right, up = transform.up;

        if (!isCorner)
        {
            switch (e)
            {
                case Edge.Right:  fixedEdgeWorld = c - right * (w * 0.5f); break;
                case Edge.Left:   fixedEdgeWorld = c + right * (w * 0.5f); break;
                case Edge.Top:    fixedEdgeWorld = c - up * (hgt * 0.5f); break;
                case Edge.Bottom: fixedEdgeWorld = c + up * (hgt * 0.5f); break;
            }
        }
        else
        {
            startW = w; startH = hgt;
            startDiag = Mathf.Max(Mathf.Sqrt(w * w + hgt * hgt), 1e-4f);
            switch (e)
            {
                case Edge.TR: fixedCornerWorld = c - right * (w * 0.5f) - up * (hgt * 0.5f); signR = +1; signU = +1; break;
                case Edge.TL: fixedCornerWorld = c + right * (w * 0.5f) - up * (hgt * 0.5f); signR = -1; signU = +1; break;
                case Edge.BR: fixedCornerWorld = c - right * (w * 0.5f) + up * (hgt * 0.5f); signR = +1; signU = -1; break;
                case Edge.BL: fixedCornerWorld = c + right * (w * 0.5f) + up * (hgt * 0.5f); signR = -1; signU = -1; break;
            }
        }
    }

    public void UpdateResize(Vector3 rayOrigin, Vector3 rayDir)
    {
        if (!resizing) return;
        if (!RayPlane(rayOrigin, rayDir, transform.position, transform.forward, out Vector3 hitP)) return;

        Vector3 right = transform.right, up = transform.up;

        if (!isCorner)
        {
            bool horizontal = (edge == Edge.Left || edge == Edge.Right);
            Vector3 dir;
            switch (edge)
            {
                case Edge.Right:  dir = right;  break;
                case Edge.Left:   dir = -right; break;
                case Edge.Top:    dir = up;     break;
                default:          dir = -up;    break;
            }
            float newSize = Mathf.Clamp(Vector3.Dot(hitP - fixedEdgeWorld, dir), minSize, maxSize);
            Vector3 s = transform.localScale;
            if (horizontal) s.x = newSize; else s.y = newSize;
            transform.localScale = s;
            transform.position = fixedEdgeWorld + dir * (newSize * 0.5f);
        }
        else
        {
            float curLen = Vector3.Distance(fixedCornerWorld, hitP);
            float factor = curLen / startDiag;
            float factorMin = Mathf.Max(minSize / startW, minSize / startH);
            float factorMax = Mathf.Min(maxSize / startW, maxSize / startH);
            factor = Mathf.Clamp(factor, factorMin, factorMax);

            float newW = startW * factor, newH = startH * factor;
            Vector3 s = transform.localScale; s.x = newW; s.y = newH; transform.localScale = s;
            transform.position = fixedCornerWorld + signR * right * (newW * 0.5f) + signU * up * (newH * 0.5f);
        }

        MaybeSendResize(false);
    }

    public void EndResize()
    {
        if (!resizing) return;
        resizing = false;
        if (marks[(int)edge] != null) marks[(int)edge].grabbed = false;
        MaybeSendResize(true);
    }

    void MaybeSendResize(bool force)
    {
        if (!sendToPC) return;
        // 꼭짓점은 쿼드(보이는 판) 크기만 바꾸는 것 - 실제 PC 창은 건드리지 않는다.
        if (isCorner) return;
        if (!force && Time.time - lastSendTime < 1f / Mathf.Max(sendRate, 1f)) return;
        lastSendTime = Time.time;
        if (receiver == null || PCStreamReceiver.Instance == null) return;

        float ptm = Mathf.Max(pixelsToMeters, 1e-6f);
        int pxW = Mathf.Max(minPixels, Mathf.RoundToInt(transform.localScale.x / ptm));
        int pxH = Mathf.Max(minPixels, Mathf.RoundToInt(transform.localScale.y / ptm));
        PCStreamReceiver.Instance.SendText(string.Format("wresize,{0},{1},{2}", receiver.windowId, pxW, pxH));
    }

    static bool RayPlane(Vector3 ro, Vector3 rd, Vector3 planePoint, Vector3 planeNormal, out Vector3 hit)
    {
        hit = Vector3.zero;
        float denom = Vector3.Dot(rd, planeNormal);
        if (Mathf.Abs(denom) < 1e-6f) return false;
        float t = Vector3.Dot(planePoint - ro, planeNormal) / denom;
        if (t < 0f) return false;
        hit = ro + rd * t;
        return true;
    }

    void OnDestroy()
    {
        for (int i = 0; i < roots.Length; i++)
            if (roots[i] != null) Destroy(roots[i].gameObject);
        if (edgeMat != null) Destroy(edgeMat);
        if (cornerMat != null) Destroy(cornerMat);
        if (hoverMat != null) Destroy(hoverMat);
    }

}
