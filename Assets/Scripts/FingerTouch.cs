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
    public bool rightHandOnly = true;
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
            if (tip == null || pc == null || !pc.IsConnected) { Release(st, pc, false); continue; }

            if (st.down) Continue(st, tip.position, pc);
            else
            {
                TryBegin(st, tip.position, pc);
                if (!st.down && hoverCursor) Hover(st, tip.position, pc);
            }
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
