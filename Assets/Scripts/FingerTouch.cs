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
    public float touchOn = 0.010f;
    [Tooltip("이 거리(m) 밖으로 나가면 뗀 것")]
    public float touchOff = 0.020f;
    [Tooltip("면 뒤로 이만큼(m) 넘게 들어가면 뗀 것으로 본다")]
    public float maxPenetration = 0.06f;
    [Tooltip("판 가장자리 바깥 여유 (판 크기 비율)")]
    public float edgeMargin = 0.03f;
    [Tooltip("움직임 전송 빈도(Hz)")]
    public float sendRate = 60f;

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

            Transform tip = touchEnabled && p.IsHandTracked ? IndexTip(p) : null;
            if (tip == null || pc == null || !pc.IsConnected) { Release(st, pc, false); continue; }

            if (st.down) Continue(st, tip.position, pc);
            else TryBegin(st, tip.position, pc);
        }
    }

    // ── 닿기 시작 ───────────────────────────────────────────────────────────
    void TryBegin(TouchState st, Vector3 tip, PCStreamReceiver pc)
    {
        Transform best = null;
        float bestAbs = float.MaxValue;
        Vector3 bestLocal = default;

        if (DesktopVisible())   // 태그가 안 보여서 화면 판이 숨겨졌으면 터치 안 함
            foreach (var mp in FindObjectsByType<MonitorPlane>(FindObjectsSortMode.None))
                Consider(mp.transform, tip, ref best, ref bestAbs, ref bestLocal);
        foreach (var w in FindObjectsByType<WindowQuadReceiver>(FindObjectsSortMode.None))
            Consider(w.transform, tip, ref best, ref bestAbs, ref bestLocal);

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
        st.lastSend = Time.time;

        if (st.isDesktop)
            pc.SendText(string.Format("down,{0:F4},{1:F4},{2}", st.lastUV.x, st.lastUV.y, st.monitorIndex));
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
    static bool DesktopVisible()
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

    static Transform IndexTip(HandRayPointer p)
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
