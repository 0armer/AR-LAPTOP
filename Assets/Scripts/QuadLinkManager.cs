using System.Collections.Generic;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// "한 손으로 A를 꼬집고, 다른 손으로 B를 꼬집으면 A ——> B 화살표가 생긴다"
//
// A, B가 될 수 있는 것:
//   · AR 창 (WindowQuadReceiver)
//   · 데스크탑 모니터 판 (MonitorPlane)   ← 데스크탑 꼬집 → 창 꼬집 = 출처 화살표
//
// 방향은 "먼저 꼬집은 쪽 → 나중에 꼬집은 쪽". 반대 순서로 다시 하면 방향이 뒤집힌다.
// 모니터끼리는 잇지 않는다.
//
// 연결하려던 제스처이므로 두 손의 "클릭"은 취소한다
// (창은 클릭 취소, 데스크탑은 PC로 보낸 마우스 down까지 취소).
//
// 씬에 아무것도 안 넣어도 실행 시 자동 생성된다.
// ─────────────────────────────────────────────────────────────────────────────
public class QuadLinkManager : MonoBehaviour
{
    public static QuadLinkManager Instance { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (Instance != null) return;
        var go = new GameObject("QuadLinkManager");
        go.AddComponent<QuadLinkManager>();
    }

    [Tooltip("창 ↔ 창 화살표 색")]
    public Color quadLinkColor = new Color(0.72f, 0.55f, 0.95f, 1f);    // 보라
    [Tooltip("데스크탑 ↔ 창 화살표 색")]
    public Color desktopLinkColor = new Color(0.22f, 0.83f, 0.90f, 1f); // 청록

    private readonly List<QuadLink> links = new();
    private bool handledThisGesture = false;

    void Awake() { Instance = this; }

    // 한 손이 지금 꼬집고 있는 "연결 끝점"
    struct Pick
    {
        public Transform t;
        public float time;
        public bool isMonitor;
        public HandRayPointer p;
    }

    void Update()
    {
        // 조건이 "선 없음"이면 연결 제스처 자체를 받지 않는다
        if (!ConditionManager.LinksEnabled) { handledThisGesture = false; return; }

        var picks = new List<Pick>(2);
        var all = HandRayPointer.All;
        for (int i = 0; i < all.Count; i++)
        {
            var p = all[i];
            if (p == null) continue;

            var w = p.PinchedWindow;
            if (w != null)
            {
                picks.Add(new Pick { t = w.transform, time = p.PinchedAt, isMonitor = false, p = p });
                continue;
            }
            var m = p.PinchedMonitor;
            if (m != null)
                picks.Add(new Pick { t = m, time = p.PinchedMonitorAt, isMonitor = true, p = p });
        }

        // 서로 다른 두 곳을 잡고 있어야 한다
        if (picks.Count < 2 || picks[0].t == picks[1].t)
        {
            handledThisGesture = false;   // 손을 떼면 다음 제스처를 받을 준비
            return;
        }

        var a = picks[0];
        var b = picks[1];

        // 모니터끼리는 잇지 않는다
        if (a.isMonitor && b.isMonitor) { handledThisGesture = false; return; }

        // 먼저 꼬집은 쪽이 앞(화살표 시작점)
        if (b.time < a.time) { var tmp = a; a = b; b = tmp; }

        if (handledThisGesture) return;   // 한 제스처로 링크를 여러 개 만들지 않게
        handledThisGesture = true;

        // 연결하려던 것이므로 클릭/마우스다운은 전부 취소
        for (int i = 0; i < picks.Count; i++)
        {
            if (picks[i].p == null) continue;
            if (picks[i].isMonitor) picks[i].p.CancelDesktopPress();
            else picks[i].p.SuppressPendingClick();
        }

        bool desktopLink = a.isMonitor || b.isMonitor;
        CreateLink(a.t, b.t, desktopLink ? desktopLinkColor : quadLinkColor);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 데스크탑에서 창을 꺼낸 직후 "모니터 → 새 창" 화살표를 자동으로 만든다.
    // 조건이 "선 없음"이면 아무것도 하지 않는다.
    // 사용자가 이 선을 지우고 다시 잇고 싶으면 평소처럼 양손 꼬집기로 만들면 된다.
    // ─────────────────────────────────────────────────────────────────────────
    public void AutoLinkFromMonitor(int monitorIndex, Transform quad)
    {
        if (!ConditionManager.LinksEnabled) return;
        if (quad == null) return;

        Transform mon = null;
        var planes = FindObjectsByType<MonitorPlane>(FindObjectsSortMode.None);
        for (int i = 0; i < planes.Length; i++)
        {
            if (planes[i] == null) continue;
            if (planes[i].monitorIndex != monitorIndex) continue;
            mon = planes[i].transform;
            break;
        }
        if (mon == null)
        {
            Debug.LogWarning($"[QuadLink] 자동 연결 실패: 모니터 {monitorIndex} 판을 못 찾음");
            return;
        }

        CreateLink(mon, quad, desktopLinkColor);
    }

    void CreateLink(Transform from, Transform to, Color color)
    {
        links.RemoveAll(l => l == null);
        foreach (var l in links)
        {
            if (!l.Connects(from, to)) continue;
            if (l.from == from && l.to == to) { Debug.Log("[QuadLink] 이미 같은 방향으로 이어져 있음"); return; }
            // 반대 순서로 다시 꼬집었으면 화살표 방향을 뒤집는다
            l.from = from; l.to = to;
            Debug.Log($"[QuadLink] 방향 뒤집음: {from.name} -> {to.name}");
            return;
        }

        var link = QuadLink.Create(from, to, color, $"QuadLink_{from.name}_to_{to.name}");
        links.Add(link);
        Debug.Log($"[QuadLink] 연결: {from.name} -> {to.name}");
    }

    void OnDestroy() { if (Instance == this) Instance = null; }
}
