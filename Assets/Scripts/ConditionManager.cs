using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

// ─────────────────────────────────────────────────────────────────────────────
// 실험 조건 관리 (2 × 2)
//
//   모니터 1대 / 2대   ×   연결선 있음 / 없음   =  네 조건
//     M1L0  모니터 1대 · 선 없음
//     M1L1  모니터 1대 · 선 있음
//     M2L0  모니터 2대 · 선 없음
//     M2L1  모니터 2대 · 선 있음
//
// 씬을 네 개로 나누지 않는다. 씬 하나에서 스위치 두 개를 껐다 켠다.
// (씬을 나누면 모니터 앵커 캘리브레이션과 서버 연결을 조건마다 다시 해야 하고,
//  코드를 고칠 때마다 네 군데를 똑같이 고쳐야 한다)
//
// 조건을 바꾸면:
//   1. 공중에 떠 있는 창을 전부 데스크탑으로 되돌리고 (다음 조건은 깨끗한 상태에서 시작)
//   2. 모니터 판을 켜고/끄고
//   3. 선 기능(창 ↔ 창 화살표)을 켜고/끄고
//   4. 기록 파일을 새로 연다  → 조건마다 파일이 따로 남는다
//
// PC 서버 콘솔에서:  cond 1 off   /   cond 2 on   …
// ─────────────────────────────────────────────────────────────────────────────
public class ConditionManager : MonoBehaviour
{
    public static ConditionManager Instance { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (Instance != null) return;
        var go = new GameObject("ConditionManager");
        DontDestroyOnLoad(go);
        go.AddComponent<ConditionManager>();
    }

    [Tooltip("쓸 수 있는 모니터 수 (1 또는 2)")]
    public int monitorCount = 2;
    [Tooltip("연결선(화살표) 기능을 쓸 수 있는지")]
    public bool linksEnabled = true;

    // 다른 스크립트가 읽는 값
    public static int MonitorCount = 2;
    public static bool LinksEnabled = true;

    // 기록 파일 이름에 들어가는 조건 코드
    public static string Code => "M" + MonitorCount + "L" + (LinksEnabled ? 1 : 0);
    public static string Describe =>
        $"모니터 {MonitorCount}대 · 선 {(LinksEnabled ? "있음" : "없음")}";

    [Tooltip("헤드셋 안에서 손으로 조건을 바꾸는 패널도 띄울지. 보통은 꺼둔다 " +
             "(에디터 [실험] 메뉴에서 고르는 걸로 충분하고, 참가자가 실수로 누를 수 있음)")]
    public bool showPanelInHeadset = false;

    // 에디터에서 고른 조건을 기억해두는 키
    public const string PrefMons = "ARSYS_COND_MONS";
    public const string PrefLinks = "ARSYS_COND_LINKS";

    void Awake()
    {
        Instance = this;

        // 유니티 에디터에서 Play를 누른 경우: [실험] 메뉴에서 고른 조건으로 시작한다.
        // (빌드된 APK에서는 인스펙터 기본값 → 이후 서버 콘솔의 cond 명령으로 바꾼다)
#if UNITY_EDITOR
        monitorCount = EditorPrefs.GetInt(PrefMons, monitorCount);
        linksEnabled = EditorPrefs.GetBool(PrefLinks, linksEnabled);
#endif
        // [AR LAPTOP] 모니터 1대 · 선 없음 고정. 메뉴/콘솔에서 뭘 골라도 바뀌지 않는다.
        monitorCount = 1;
        linksEnabled = false;

        // 다른 스크립트가 Awake/Start에서 이 값을 읽으므로 여기서 먼저 확정한다
        MonitorCount = Mathf.Clamp(monitorCount, 1, 8);
        LinksEnabled = linksEnabled;
    }

    void Start()
    {
        // 씬 오브젝트를 실제로 켜고 끈다 (창은 건드리지 않고, 기록 파일도 새로 열지 않는다)
        Apply(monitorCount, linksEnabled, false, false);
        if (showPanelInHeadset) ConditionPanel.Show();
        Debug.Log($"[Cond] 시작 조건 — {Code} ({Describe})");
    }

    /// <summary>조건을 적용한다.</summary>
    /// <param name="monitors">쓸 모니터 수 (1 또는 2)</param>
    /// <param name="links">연결선 기능 on/off</param>
    /// <param name="clearWindows">공중의 창을 전부 데스크탑으로 되돌릴지</param>
    /// <param name="newRecording">기록 파일을 새로 열지</param>
    public void Apply(int monitors, bool links, bool clearWindows = true, bool newRecording = true)
    {
        monitors = 1;      // [AR LAPTOP] 고정
        links = false;     // [AR LAPTOP] 고정
        MonitorCount = Mathf.Clamp(monitors, 1, 8);
        LinksEnabled = links;
        monitorCount = MonitorCount;
        linksEnabled = LinksEnabled;

        if (clearWindows) ClearAllWindows();

        ApplyMonitors();
        ApplyLinks();

        Debug.Log($"[Cond] {Code} — {Describe}");

        if (showPanelInHeadset) ConditionPanel.Show(); else ConditionPanel.Hide();

        if (newRecording && SessionRecorder.Instance != null)
            SessionRecorder.Instance.BeginConditionRun();
    }

    // ── 모니터 판 켜고 끄기 ──────────────────────────────────────────────────
    void ApplyMonitors()
    {
        // 꺼둔 판도 찾아야 다시 켤 수 있다 (Include)
        var planes = FindObjectsByType<MonitorPlane>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (var p in planes)
        {
            bool on = p.monitorIndex < MonitorCount;

            // Renderer는 절대 건드리지 않는다.
            // 모니터 판은 "투명 메쉬"로 씬/프리팹에 세팅돼 있고, 여기서 MeshRenderer를
            // 껐다 켜면 조건에 따라 투명 메쉬가 사라지는 문제가 생긴다.
            // 안 쓰는 모니터는 오브젝트 자체를 꺼서 통째로 없는 것으로 만든다.
            if (p.gameObject.activeSelf != on) p.gameObject.SetActive(on);

            // 꺼진 오브젝트 안에서도 상태는 맞춰둔다 (나중에 누가 다시 켜도 안전하게)
            p.enabled = on;
            foreach (var c in p.GetComponentsInChildren<Collider>(true)) c.enabled = on;
        }

        // 배치(cal)에서 요구하는 개수도 조건에 맞춘다.
        // 이게 없으면 c1(모니터 1대)을 골라도 배치 단계에서 2개를 다 그리라고 요구한다.
        if (MonitorHandCalibrator.Instance != null
            && MonitorHandCalibrator.Instance.calibrateCount != MonitorCount)
        {
            MonitorHandCalibrator.Instance.calibrateCount = MonitorCount;
            Debug.Log($"[Cond] 배치 요구 개수도 {MonitorCount}개로 맞춤");
        }

        Debug.Log($"[Cond] 모니터 쿼드 {MonitorCount}개 사용");
    }

    // ── 연결선 기능 켜고 끄기 ────────────────────────────────────────────────
    void ApplyLinks()
    {
        // 이미 그려져 있는 선은 조건이 바뀌면 무조건 정리한다
        foreach (var t in FindObjectsByType<WindowTether>(FindObjectsSortMode.None))
            if (!LinksEnabled) Destroy(t);
        foreach (var l in FindObjectsByType<QuadLink>(FindObjectsSortMode.None))
            if (!LinksEnabled) Destroy(l.gameObject);

        // 창끼리 잇는 제스처 담당
        if (QuadLinkManager.Instance != null)
            QuadLinkManager.Instance.enabled = LinksEnabled;

        // 데스크탑 가장자리의 상호작용 직육면체(손잡이)는 없앴다.
        // 선은 AR 창 ↔ AR 창 꼬집기로만 만든다. 남아 있으면 레이를 막으므로 지운다.
        if (DesktopTetherSpawner.Instance != null)
            Destroy(DesktopTetherSpawner.Instance.gameObject);
    }

    // ── 공중의 창을 전부 데스크탑으로 되돌리기 ───────────────────────────────
    public void ClearAllWindows()
    {
        var pc = PCStreamReceiver.Instance;
        int n = 0;
        foreach (var q in FindObjectsByType<WindowQuadReceiver>(FindObjectsSortMode.None))
        {
            if (pc != null) pc.SendText($"insert,{q.windowId}");
            if (WindowCaptureHub.Instance != null) WindowCaptureHub.Instance.Unregister(q.windowId);
            Destroy(q.gameObject);
            n++;
        }
        foreach (var l in FindObjectsByType<QuadLink>(FindObjectsSortMode.None))
            Destroy(l.gameObject);
        if (n > 0) Debug.Log($"[Cond] 창 {n}개를 데스크탑으로 되돌림");
    }

    void OnDestroy() { if (Instance == this) Instance = null; }
}
