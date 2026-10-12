using System.Collections.Generic;
using UnityEngine;

// [AR LAPTOP] 손가락으로 직접 누르기 (터치)
//
// 검지 끝이 판(데스크탑 화면) 또는 꺼낸 창 면에 닿으면 실제 마우스 입력으로 보낸다.
//   · 데스크탑 판: 닿는 순간 마우스 누름(down) → 손가락 따라 이동(move) → 떼면 놓음(up)
//                 = 클릭, 드래그(창 옮기기·글자 선택) 다 됨
//   · 꺼낸 창   : 톡 치면 클릭(wclick), 닿은 채로 위아래로 밀면 스크롤(wscroll, 스마트폰처럼)
//
// 판정: 손가락 끝과 면 사이 거리
//   touchOn(1cm) 안으로 들어오면 닿음, touchOff(2cm) 밖으로 나가면 뗌 → 경계에서 깜빡이지 않음
//   면 뒤로 너무 깊이(6cm) 들어가면 실수로 본 것으로 보고 뗌
public class FingerTouch : MonoBehaviour
{
    public static FingerTouch Instance { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (Instance != null) return;
        var go = new GameObject("FingerTouch");
        DontDestroyOnLoad(go);
        go.AddComponent<FingerTouch>();
    }

    [Tooltip("끄면 터치 안 함")]
    public bool touchEnabled = true;
    [Tooltip("손가락 끝이 면에서 이 거리(m) 안으로 오면 닿은 것")]
    public float touchOn = 0.005f;   // [AR LAPTOP] 실제로 닿았을 때 (손가락 끝 뼈는 피부 끝과 거의 같음)
    [Tooltip("이 거리(m) 밖으로 나가면 뗀 것")]
    public float touchOff = 0.015f;
    [Tooltip("면 뒤로 이만큼(m) 넘게 들어가면 뗀 것으로 본다")]
    public float maxPenetration = 0.06f;
    [Tooltip("판 가장자리 바깥 여유 (판 크기 비율)")]
    public float edgeMargin = 0.03f;
    [Tooltip("움직임 전송 빈도(Hz)")]
    public float sendRate = 60f;

    [Tooltip("손가락이 화면 위에 떠 있기만 해도 커서를 따라오게 할지. 손 떨림이 커서에 그대로 들어가서 기본은 끔")]
    public bool hoverCursor = false;
    [Tooltip("hoverCursor를 켰을 때: 화면 위 이 거리(m) 안이면 커서가 따라옴")]
    public float hoverDistance = 0.05f;
    [Tooltip("오른손 검지만 터치에 씀 (왼손은 터치 안 함)")]
    public bool rightHandOnly = false;   // [AR LAPTOP] 양손 다 같은 규칙(가리키기만 터치)

    [Header("손 모양으로 의도 판단")]
    [Tooltip("검지를 펴고 나머지 세 손가락을 접은 '가리키기' 모양일 때만 터치 시작")]
    public bool requirePointing = true;
    [Tooltip("검지가 이만큼 이상 펴져야 가리키기 (1 = 일자, 손가락 마디 길이 대비 끝까지 거리)")]
    public float indexStraightOn = 0.85f;
    [Tooltip("가리키기 유지 중엔 이 아래로 떨어져야 풀림 (떨림 방지)")]
    public float indexStraightOff = 0.75f;
    [Tooltip("중지·약지·새끼가 모두 이보다 덜 펴져야(=접혀야) 가리키기")]
    public float othersCurledOn = 0.85f;
    [Tooltip("가리키기 유지 중엔 셋 중 하나라도 이보다 펴지면 풀림")]
    public float othersCurledOff = 0.92f;
    [Tooltip("엄지·중지·약지·새끼 끝 중 하나라도 판 뒤로 이만큼(m) 넘어가 있으면 '종이를 잡고 있음' → 그 손 터치 꺼짐")]
    public float behindTolerance = 0.015f;
    [Tooltip("노트북(태그)이 움직이는 중이면 터치 꺼짐 (들고 옮기는 중)")]
    public bool blockWhileLaptopMoving = true;

    [Header("검지 끝 표시 구체")]
    [Tooltip("가리키기 모양일 때 검지 끝에 작은 구체를 띄움 (닿으면 빨간색)")]
    public bool showTipSphere = true;
    public float tipSphereSize = 0.012f;
    public Color pointColor = new Color(0.2f, 0.85f, 1f);
    public Color touchColor = Color.red;
    public Color fitColor = Color.yellow;

    [Header("손가락으로 판 면 맞추기")]
    [Tooltip("판이 종이 뒤에 있어서 손가락이 판에 못 닿을 때: 종이를 가리키기로 누르고 이 시간(초) 가만히 있으면 판을 손가락 위치로 옮김")]
    public float autoFitHold = 2f;
    [Tooltip("자동 맞춤은 판이 손가락보다 이 거리(m) 안쪽 뒤에 있을 때만 (허공에서 멈춘 걸 오인하지 않게)")]
    public float autoFitMaxGap = 0.05f;
    [Tooltip("손가락이 이만큼(m) 넘게 움직이면 가만히 있는 시간 처음부터")]
    public float fitStillMove = 0.004f;
    static float fitUntil;   // 서버 'fit' 명령: 이 시각까지 아무 쪽(앞/뒤)이든 0.6초 누르면 맞춤
    public static void BeginFit(float seconds = 8f) { fitUntil = Time.time + seconds; Debug.Log("[Touch] fit: 검지로 종이를 누르고 가만히"); }
    public static bool FitActive => Time.time < fitUntil;
    [Tooltip("데스크탑을 누른 뒤 이만큼(m) 움직여야 드래그 시작. 그 전까진 커서를 안 움직여서 손 떨림으로 클릭이 드래그로 바뀌지 않음")]
    public float dragStartMove = 0.012f;

    [Header("꺼낸 창")]
    [Tooltip("이보다 적게(m) 움직이고 떼면 클릭")]
    public float tapMaxMove = 0.015f;
    [Tooltip("손가락을 이만큼(m) 밀 때마다 휠 한 칸")]
    public float scrollMetersPerNotch = 0.015f;

    class TouchState
    {
        public bool down;
        public Transform target;
        public bool isDesktop;
        public long windowId;
        public int monitorIndex;
        public Vector2 startUV, lastUV;
        public Vector3 startLocal;
        public float lastSend;
        public float scrollAccum;   // 아직 안 보낸 스크롤(m)
        public float lastY;
        public bool scrolled;
        public bool dragging;
        public bool pointing;
        public bool blocked;     // 잡기/이동 중이라 막힘
        public Transform sphere;
        public Renderer sphereR;
        public Vector3 stillPos;
        public float stillSince = -1f;
        public float fitProgress;   // 0~1 (구체 색 표시용)
        public float lastIdx, lastOthers;   // 상태 글자용: 검지/나머지 펴진 정도
        public string diag = "";
    }

    // 다른 스크립트(상태 글자 등)가 읽는 손별 상태
    public string DebugState(HandRayPointer p)
    {
        if (!states.TryGetValue(p, out var st)) return "-";
        string s = st.down ? "TOUCH" : st.blocked ? "HOLD" : st.pointing ? "POINT" : "idle";
        return $"{s} i{st.lastIdx:F2} o{st.lastOthers:F2} {st.diag}";
    }

    readonly Dictionary<HandRayPointer, TouchState> states = new();

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }
    void OnDestroy() { if (Instance == this) Instance = null; }

    void Update()
    {
        var pc = PCStreamReceiver.Instance;
        foreach (var p in HandRayPointer.All)
        {
            if (p == null) continue;
            if (!states.TryGetValue(p, out var st)) { st = new TouchState(); states[p] = st; }

            bool allowed = touchEnabled && p.IsHandTracked && !(rightHandOnly && IsLeft(p));
            Transform tip = allowed ? IndexTip(p) : null;
            st.diag = DiagSkeleton(p) + (allowed ? "" : $" notAllowed(tracked={p.IsHandTracked})")
                    + (tip == null ? " noTip" : "") + (pc == null ? " noPC" : (!pc.IsConnected ? " pcOff" : ""));
            if (tip == null || pc == null || !pc.IsConnected) { Release(st, pc, false); st.pointing = false; st.blocked = false; ShowSphere(p, st, null); continue; }

            // 손 모양 읽기 → 가리키기인지 / 종이를 잡고 있는지 / 노트북을 옮기는 중인지
            var hs = ReadShape(p);
            UpdatePointing(st, hs);
            bool grip = hs.valid && FingersBehindSurface(hs);
            var tagF = AprilTagFollower.Instance;
            bool moving = blockWhileLaptopMoving && tagF != null && tagF.IsMoving;
            st.blocked = grip || moving;

            // 손가락으로 판 면 맞추기 (누르는 중이 아닐 때만)
            if (!st.down && st.pointing && !st.blocked && TryFit(st, tip.position)) { ShowSphere(p, st, tip); continue; }

            if (st.down)
            {
                // 누르는 중엔 손 모양이 조금 무너져도 유지. 단, 잡기/이동이 감지되면 즉시 뗌
                if (st.blocked) Release(st, pc, false);
                else Continue(st, tip.position, pc);
            }
            else if (!st.blocked && (!requirePointing || st.pointing) && !FitActive)
            {
                TryBegin(st, tip.position, pc);
                if (!st.down && hoverCursor) Hover(st, tip.position, pc);
            }
            ShowSphere(p, st, tip);
        }
    }

    // ── 닿기 시작 ───────────────────────────────────────────────────────────
    void TryBegin(TouchState st, Vector3 tip, PCStreamReceiver pc)
    {
        Transform best = null;
        float bestAbs = float.MaxValue;
        Vector3 bestLocal = default;

        // 화면 판(태그가 안 보이면 빠짐) + 꺼낸 창. 목록은 프레임당 한 번만 찾는다.
        foreach (var sf in Surfaces())
            Consider(sf.t, tip, ref best, ref bestAbs, ref bestLocal);

        if (best == null) return;
        float d = SignedDepth(best, tip);
        if (d > touchOn || d < -maxPenetration * 0.5f) return;

        st.down = true;
        st.target = best;
        var w2 = best.GetComponent<WindowQuadReceiver>();
        var m2 = best.GetComponent<MonitorPlane>();
        st.isDesktop = w2 == null;
        st.windowId = w2 != null ? w2.windowId : 0;
        st.monitorIndex = m2 != null ? m2.monitorIndex : 0;
        st.startUV = st.lastUV = ToUV(bestLocal);
        st.startLocal = bestLocal;
        st.lastY = bestLocal.y * best.lossyScale.y;
        st.scrollAccum = 0f;
        st.scrolled = false;
        st.dragging = false;
        st.lastSend = Time.time;

        if (st.isDesktop)
            pc.SendText(string.Format("down,{0:F4},{1:F4},{2}", st.lastUV.x, st.lastUV.y, st.monitorIndex));
    }

    // ── 손가락으로 판 면 맞추기 ─────────────────────────────────────────────
    // 검지로 실제 종이를 누르고 가만히 있으면, 그 손가락 끝 = 진짜 종이 면. 판을 거기로 옮긴다.
    bool TryFit(TouchState st, Vector3 tip)
    {
        st.fitProgress = 0f;
        var tagF = AprilTagFollower.Instance;
        if (tagF == null) return false;
        Transform plane = null;
        foreach (var s in Surfaces()) if (s.window == null && s.t != null) { plane = s.t; break; }
        if (plane == null) { st.stillSince = -1f; return false; }

        Vector3 local = plane.InverseTransformPoint(tip);
        if (Mathf.Abs(local.x) > 0.5f || Mathf.Abs(local.y) > 0.5f) { st.stillSince = -1f; return false; }
        float d = SignedDepth(plane, tip);   // + = 손가락이 판 앞(판이 종이 뒤에 있음)

        bool manual = FitActive && Mathf.Abs(d) < 0.10f;
        bool auto = !FitActive && d > touchOn + 0.001f && d < autoFitMaxGap;
        if (!manual && !auto) { st.stillSince = -1f; return false; }

        if (st.stillSince < 0f || Vector3.Distance(tip, st.stillPos) > fitStillMove)
        {
            st.stillPos = tip; st.stillSince = Time.time;
            return manual;
        }
        float need = manual ? 0.6f : autoFitHold;
        st.fitProgress = Mathf.Clamp01((Time.time - st.stillSince) / need);
        if (st.fitProgress >= 1f)
        {
            tagF.AddSurfaceOffset(d);
            fitUntil = 0f;
            st.stillSince = -1f;
            st.fitProgress = 0f;
        }
        return manual || st.fitProgress > 0.15f;   // 맞추는 중엔 터치 시작 안 함
    }

    // ── 검지 끝 구체: 가리키기면 파랑, 맞추는 중 노랑, 닿으면 빨강 ─────────────
    void ShowSphere(HandRayPointer p, TouchState st, Transform tip)
    {
        bool show = showTipSphere && tip != null && (st.pointing || st.down);
        if (!show) { if (st.sphere != null) st.sphere.gameObject.SetActive(false); return; }
        if (st.sphere == null)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "TipSphere";
            Destroy(go.GetComponent<Collider>());   // 레이를 막지 않게
            st.sphere = go.transform;
            st.sphereR = go.GetComponent<Renderer>();
            var lr = p.GetComponent<LineRenderer>();
            Material m = lr != null && lr.sharedMaterial != null ? new Material(lr.sharedMaterial) : null;
            if (m == null)
            {
                var sh = Shader.Find("Universal Render Pipeline/Unlit");
                if (sh == null) sh = Shader.Find("Sprites/Default");
                if (sh != null) m = new Material(sh);
            }
            if (m != null) st.sphereR.sharedMaterial = m;
            st.sphereR.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }
        st.sphere.gameObject.SetActive(true);
        st.sphere.position = tip.position;
        st.sphere.localScale = Vector3.one * tipSphereSize;
        Color c = st.down ? touchColor : (st.fitProgress > 0.15f || FitActive) ? Color.Lerp(pointColor, fitColor, Mathf.Max(st.fitProgress, 0.5f)) : pointColor;
        var mat = st.sphereR.sharedMaterial;
        if (mat != null)
        {
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", c);
        }
    }

    // ── 화면 위에 떠 있을 때: 커서만 손가락을 따라옴 ─────────────────────────
    void Hover(TouchState st, Vector3 tip, PCStreamReceiver pc)
    {
        if (!DesktopVisible() || Time.time - st.lastSend < 1f / 30f) return;
        foreach (var mp in Surfaces())
        {
            if (mp.window != null) continue;
            Vector3 local = mp.t.InverseTransformPoint(tip);
            if (Mathf.Abs(local.x) > 0.5f || Mathf.Abs(local.y) > 0.5f) continue;
            float d = SignedDepth(mp.t, tip);
            if (d < 0f || d > hoverDistance) continue;
            Vector2 uv = ToUV(local);
            if ((uv - st.lastUV).sqrMagnitude < 1e-6f) return;
            pc.SendText(string.Format("hover,{0:F4},{1:F4},{2}", uv.x, uv.y, mp.monitor));
            st.lastUV = uv;
            st.lastSend = Time.time;
            return;
        }
    }

    // ── 닿은 채로 움직이기 ───────────────────────────────────────────────────
    void Continue(TouchState st, Vector3 tip, PCStreamReceiver pc)
    {
        if (st.target == null || !st.target.gameObject.activeInHierarchy) { Release(st, pc, false); return; }

        Vector3 local = st.target.InverseTransformPoint(tip);
        float d = SignedDepth(st.target, tip);
        bool outside = Mathf.Abs(local.x) > 0.5f + edgeMargin * 2f || Mathf.Abs(local.y) > 0.5f + edgeMargin * 2f;
        if (d > touchOff || d < -maxPenetration || outside || (st.isDesktop && !DesktopVisible())) { Release(st, pc, true); return; }

        Vector2 uv = ToUV(local);

        if (st.isDesktop)
        {
            // 떨림 방지: 일정 거리 움직이기 전까지는 누른 자리 그대로 (클릭이 드래그로 안 바뀜)
            if (!st.dragging)
            {
                Vector3 dm = local - st.startLocal; dm.z = 0f;
                if (Vector3.Scale(dm, st.target.lossyScale).magnitude < dragStartMove) return;
                st.dragging = true;
            }
            if (Time.time - st.lastSend >= 1f / Mathf.Max(1f, sendRate) && (uv - st.lastUV).sqrMagnitude > 1e-7f)
            {
                pc.SendText(string.Format("move,{0:F4},{1:F4},{2}", uv.x, uv.y, st.monitorIndex));
                st.lastSend = Time.time;
                st.lastUV = uv;
            }
            return;
        }

        // 꺼낸 창: 세로로 밀면 스크롤 (스마트폰처럼 내용이 손가락을 따라옴)
        float y = local.y * st.target.lossyScale.y;
        st.scrollAccum += y - st.lastY;
        st.lastY = y;
        while (Mathf.Abs(st.scrollAccum) >= scrollMetersPerNotch)
        {
            int dir = st.scrollAccum > 0f ? 1 : -1;      // 손가락이 위로 → 내용도 위로 → 아래로 스크롤
            st.scrollAccum -= dir * scrollMetersPerNotch;
            pc.SendText(string.Format("wscroll,{0},{1:F4},{2:F4},{3}", st.windowId, uv.x, uv.y, -120 * dir));
            st.scrolled = true;
        }
        st.lastUV = uv;
    }

    // ── 떼기 ────────────────────────────────────────────────────────────────
    void Release(TouchState st, PCStreamReceiver pc, bool normal)
    {
        if (!st.down) return;
        st.down = false;
        if (pc == null) { st.target = null; return; }

        if (st.isDesktop)
        {
            pc.SendText(string.Format("up,{0:F4},{1:F4},{2}", st.lastUV.x, st.lastUV.y, st.monitorIndex));
        }
        else if (normal && !st.scrolled && st.target != null)
        {
            Vector3 d = ToLocal(st.lastUV) - st.startLocal;
            d.z = 0f;
            Vector3 moved = Vector3.Scale(d, st.target.lossyScale);   // 로컬 → 미터
            if (moved.magnitude <= tapMaxMove)
                pc.SendText(string.Format("wclick,{0},{1:F4},{2:F4}", st.windowId, st.startUV.x, st.startUV.y));
        }
        st.target = null;
    }

    // ── 도우미 ──────────────────────────────────────────────────────────────
    public static bool DesktopVisible()
    {
        var tag = AprilTagFollower.Instance;
        return tag == null || !tag.fixedLayout || tag.IsTracking;
    }

    void Consider(Transform t, Vector3 tip, ref Transform best, ref float bestAbs, ref Vector3 bestLocal)
    {
        if (t == null || !t.gameObject.activeInHierarchy) return;
        Vector3 local = t.InverseTransformPoint(tip);
        if (Mathf.Abs(local.x) > 0.5f + edgeMargin || Mathf.Abs(local.y) > 0.5f + edgeMargin) return;
        float a = Mathf.Abs(SignedDepth(t, tip));
        if (a < bestAbs) { bestAbs = a; best = t; bestLocal = local; }
    }

    // 손가락 끝이 면의 "보는 사람 쪽"으로 얼마나 떨어져 있는지 (m). 음수 = 면을 뚫고 들어감
    static float SignedDepth(Transform t, Vector3 tip)
    {
        Vector3 n = t.forward;
        var cam = Camera.main;
        if (cam != null && Vector3.Dot(cam.transform.position - t.position, n) < 0f) n = -n;
        return Vector3.Dot(tip - t.position, n);
    }

    // 쿼드 로컬 → 서버가 쓰는 UV (u = 왼→오, v = 위→아래)
    static Vector2 ToUV(Vector3 local)
        => new Vector2(Mathf.Clamp01(local.x + 0.5f), Mathf.Clamp01(1f - (local.y + 0.5f)));

    static Vector3 ToLocal(Vector2 uv) => new Vector3(uv.x - 0.5f, (1f - uv.y) - 0.5f, 0f);

    // ── 화면 판·꺼낸 창 목록 (프레임마다 한 번만 찾음) ──
    public struct Surface { public Transform t; public WindowQuadReceiver window; public int monitor; }
    static readonly List<Surface> surfaces = new();
    static int surfacesFrame = -1;

    public static List<Surface> Surfaces()
    {
        if (surfacesFrame == Time.frameCount) return surfaces;
        surfacesFrame = Time.frameCount;
        surfaces.Clear();
        if (DesktopVisible())
            foreach (var mp in FindObjectsByType<MonitorPlane>(FindObjectsSortMode.None))
                if (mp.gameObject.activeInHierarchy) surfaces.Add(new Surface { t = mp.transform, monitor = mp.monitorIndex });
        foreach (var w in FindObjectsByType<WindowQuadReceiver>(FindObjectsSortMode.None))
            if (w.gameObject.activeInHierarchy) surfaces.Add(new Surface { t = w.transform, window = w });
        return surfaces;
    }

    // 검지 끝에서 가장 가까운 화면/창 면까지 거리(m). 면 범위(가장자리 여유 포함) 밖이면 무한대.
    public static float NearestSurfaceDistance(Vector3 tip, float edgeMarginM = 0.05f)
    {
        float best = float.PositiveInfinity;
        foreach (var s in Surfaces())
        {
            if (s.t == null) continue;
            Vector3 local = s.t.InverseTransformPoint(tip);
            Vector3 sc = s.t.lossyScale;
            float mx = 0.5f + edgeMarginM / Mathf.Max(Mathf.Abs(sc.x), 1e-4f);
            float my = 0.5f + edgeMarginM / Mathf.Max(Mathf.Abs(sc.y), 1e-4f);
            if (Mathf.Abs(local.x) > mx || Mathf.Abs(local.y) > my) continue;
            float d = Mathf.Abs(SignedDepth(s.t, tip));
            if (d < best) best = d;
        }
        return best;
    }

    static bool IsLeft(HandRayPointer p) => p.IsLeftHand();

    static string DiagSkeleton(HandRayPointer p)
    {
        var sk = p.skeleton != null ? p.skeleton : (p.hand != null ? p.hand.GetComponent<OVRSkeleton>() : null);
        if (sk == null) return "sk=null";
        int n = sk.Bones != null ? sk.Bones.Count : -1;
        return $"sk={sk.GetSkeletonType()} init={sk.IsInitialized} bones={n}";
    }

    // ── 손 모양 ─────────────────────────────────────────────────────────────
    public struct HandShape
    {
        public bool valid;
        public float index, middle, ring, little;   // 펴진 정도 (1 = 일자, 접을수록 작아짐)
        public Vector3 thumbTip, middleTip, ringTip, littleTip;
    }

    static readonly Dictionary<OVRSkeleton.BoneId, Transform> boneMap = new();

    public static HandShape ReadShape(HandRayPointer p)
    {
        var hs = new HandShape();
        var sk = p.skeleton != null ? p.skeleton : (p.hand != null ? p.hand.GetComponent<OVRSkeleton>() : null);
        if (sk == null || !sk.IsInitialized || sk.Bones == null) return hs;
        boneMap.Clear();
        foreach (var b in sk.Bones) if (b != null && b.Transform != null) boneMap[b.Id] = b.Transform;

        var t = sk.GetSkeletonType();
        bool xr = t == OVRSkeleton.SkeletonType.XRHandLeft || t == OVRSkeleton.SkeletonType.XRHandRight;
        if (xr)
        {
            hs.index  = Straight(OVRSkeleton.BoneId.XRHand_IndexProximal,  OVRSkeleton.BoneId.XRHand_IndexIntermediate,  OVRSkeleton.BoneId.XRHand_IndexDistal,  OVRSkeleton.BoneId.XRHand_IndexTip);
            hs.middle = Straight(OVRSkeleton.BoneId.XRHand_MiddleProximal, OVRSkeleton.BoneId.XRHand_MiddleIntermediate, OVRSkeleton.BoneId.XRHand_MiddleDistal, OVRSkeleton.BoneId.XRHand_MiddleTip);
            hs.ring   = Straight(OVRSkeleton.BoneId.XRHand_RingProximal,   OVRSkeleton.BoneId.XRHand_RingIntermediate,   OVRSkeleton.BoneId.XRHand_RingDistal,   OVRSkeleton.BoneId.XRHand_RingTip);
            hs.little = Straight(OVRSkeleton.BoneId.XRHand_LittleProximal, OVRSkeleton.BoneId.XRHand_LittleIntermediate, OVRSkeleton.BoneId.XRHand_LittleDistal, OVRSkeleton.BoneId.XRHand_LittleTip);
            hs.valid = Pos(OVRSkeleton.BoneId.XRHand_ThumbTip, out hs.thumbTip) & Pos(OVRSkeleton.BoneId.XRHand_MiddleTip, out hs.middleTip)
                     & Pos(OVRSkeleton.BoneId.XRHand_RingTip, out hs.ringTip) & Pos(OVRSkeleton.BoneId.XRHand_LittleTip, out hs.littleTip);
        }
        else
        {
            hs.index  = Straight(OVRSkeleton.BoneId.Hand_Index1,  OVRSkeleton.BoneId.Hand_Index2,  OVRSkeleton.BoneId.Hand_Index3,  OVRSkeleton.BoneId.Hand_IndexTip);
            hs.middle = Straight(OVRSkeleton.BoneId.Hand_Middle1, OVRSkeleton.BoneId.Hand_Middle2, OVRSkeleton.BoneId.Hand_Middle3, OVRSkeleton.BoneId.Hand_MiddleTip);
            hs.ring   = Straight(OVRSkeleton.BoneId.Hand_Ring1,   OVRSkeleton.BoneId.Hand_Ring2,   OVRSkeleton.BoneId.Hand_Ring3,   OVRSkeleton.BoneId.Hand_RingTip);
            hs.little = Straight(OVRSkeleton.BoneId.Hand_Pinky1,  OVRSkeleton.BoneId.Hand_Pinky2,  OVRSkeleton.BoneId.Hand_Pinky3,  OVRSkeleton.BoneId.Hand_PinkyTip);
            hs.valid = Pos(OVRSkeleton.BoneId.Hand_ThumbTip, out hs.thumbTip) & Pos(OVRSkeleton.BoneId.Hand_MiddleTip, out hs.middleTip)
                     & Pos(OVRSkeleton.BoneId.Hand_RingTip, out hs.ringTip) & Pos(OVRSkeleton.BoneId.Hand_PinkyTip, out hs.littleTip);
        }
        hs.valid &= hs.index > 0f && hs.middle > 0f;
        return hs;
    }

    static bool Pos(OVRSkeleton.BoneId id, out Vector3 p)
    {
        if (boneMap.TryGetValue(id, out var t)) { p = t.position; return true; }
        p = default; return false;
    }

    // 손가락 첫 마디 → 끝까지 직선거리 ÷ 마디 길이 합. 일자면 1, 접을수록 작아짐 (손 크기와 무관)
    static float Straight(OVRSkeleton.BoneId a, OVRSkeleton.BoneId b, OVRSkeleton.BoneId c, OVRSkeleton.BoneId d)
    {
        if (!Pos(a, out var pa) || !Pos(b, out var pb) || !Pos(c, out var pc) || !Pos(d, out var pd)) return 0f;
        float seg = Vector3.Distance(pa, pb) + Vector3.Distance(pb, pc) + Vector3.Distance(pc, pd);
        return seg > 1e-4f ? Vector3.Distance(pa, pd) / seg : 0f;
    }

    void UpdatePointing(TouchState st, HandShape hs)
    {
        if (!hs.valid) { st.pointing = false; st.lastIdx = 0f; st.lastOthers = 0f; return; }
        float others = Mathf.Max(hs.middle, Mathf.Max(hs.ring, hs.little));
        st.lastIdx = hs.index; st.lastOthers = others;
        st.pointing = st.pointing
            ? hs.index >= indexStraightOff && others <= othersCurledOff
            : hs.index >= indexStraightOn && others <= othersCurledOn;
    }

    // 엄지·중지·약지·새끼 끝이 판 "뒤"에 있으면 = 종이/노트북을 쥐고 있는 것 (누를 땐 손 전체가 판 앞에 있음)
    bool FingersBehindSurface(HandShape hs)
    {
        foreach (var s in Surfaces())
        {
            if (s.t == null || s.window != null) continue;   // 실제 종이가 있는 건 데스크탑 판뿐
            if (Behind(s.t, hs.thumbTip) || Behind(s.t, hs.middleTip) || Behind(s.t, hs.ringTip) || Behind(s.t, hs.littleTip))
                return true;
        }
        return false;
    }

    bool Behind(Transform t, Vector3 p)
    {
        Vector3 local = t.InverseTransformPoint(p);
        Vector3 sc = t.lossyScale;
        float mx = 0.5f + 0.03f / Mathf.Max(Mathf.Abs(sc.x), 1e-4f);
        float my = 0.5f + 0.03f / Mathf.Max(Mathf.Abs(sc.y), 1e-4f);
        if (Mathf.Abs(local.x) > mx || Mathf.Abs(local.y) > my) return false;
        return SignedDepth(t, p) < -behindTolerance;
    }

    // 점에서 화면/창 "사각형"까지의 실제 최단 거리(m). 면 바로 위가 아니어도(옆·아래·비스듬히) 잰다.
    public static float NearestRectDistance(Vector3 point)
    {
        float best = float.PositiveInfinity;
        foreach (var s in Surfaces())
        {
            if (s.t == null) continue;
            Vector3 local = s.t.InverseTransformPoint(point);
            Vector3 onRect = new Vector3(Mathf.Clamp(local.x, -0.5f, 0.5f), Mathf.Clamp(local.y, -0.5f, 0.5f), 0f);
            float d = Vector3.Distance(point, s.t.TransformPoint(onRect));
            if (d < best) best = d;
        }
        return best;
    }

    public static Transform IndexTip(HandRayPointer p)
    {
        var sk = p.skeleton;
        if (sk == null && p.hand != null) sk = p.hand.GetComponent<OVRSkeleton>();
        if (sk == null || sk.Bones == null) return null;
        var t = sk.GetSkeletonType();
        bool xr = t == OVRSkeleton.SkeletonType.XRHandLeft || t == OVRSkeleton.SkeletonType.XRHandRight;
        var id = xr ? OVRSkeleton.BoneId.XRHand_IndexTip : OVRSkeleton.BoneId.Hand_IndexTip;
        foreach (var b in sk.Bones) if (b.Id == id) return b.Transform;
        return null;
    }
}
