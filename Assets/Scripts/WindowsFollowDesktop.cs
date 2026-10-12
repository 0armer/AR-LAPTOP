using UnityEngine;

// [AR LAPTOP] 모니터 판(데스크탑 쿼드)이 움직이면, 거기서 꺼낸 AR 창들이 판 기준으로 같이 따라간다.
//
// 원리: 매 프레임 판의 이동·회전 변화량(델타)을 구해서, 공중의 창 전부에 똑같이 적용한다.
//   → 판과 창 사이의 상대 위치·각도가 그대로 유지된다 (판이 돌면 창들도 판을 축으로 같이 돈다).
//   → 사용자가 창을 옮겨 두면, 그 옮긴 자리가 새 상대 위치가 된다 (따로 저장할 게 없음).
//
// 지금 손에 들려 있거나(꺼내는 중/주먹으로 잡음/양손) 가장자리로 크기를 바꾸는 중인 창은 건드리지 않는다.
// 손이 위치를 정하고 있는데 여기서 또 옮기면 둘이 싸운다.
//
// 판을 움직이는 쪽(손 캘리브레이션, 나중에 AprilTag 추적)보다 늦게 돌아야 하므로 실행 순서를 맨 뒤로 둔다.
[DefaultExecutionOrder(20000)]
public class WindowsFollowDesktop : MonoBehaviour
{
    public static WindowsFollowDesktop Instance { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (Instance != null) return;
        var go = new GameObject("WindowsFollowDesktop");
        DontDestroyOnLoad(go);
        go.AddComponent<WindowsFollowDesktop>();
    }

    [Tooltip("따라갈 모니터 판 번호")]
    public int monitorIndex = 0;
    [Tooltip("판이 이보다 적게 움직이면 무시(m). 작은 떨림은 모아뒀다가 넘으면 한 번에 적용")]
    public float minMove = 0.0005f;
    [Tooltip("판이 이보다 적게 돌면 무시(도)")]
    public float minAngle = 0.05f;
    [Tooltip("끄면 창이 판을 따라가지 않는다")]
    public bool follow = true;

    Transform plane;
    Vector3 lastPos;
    Quaternion lastRot;
    bool hasLast;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    void OnDestroy() { if (Instance == this) Instance = null; }

    void LateUpdate()
    {
        if (plane == null)
        {
            hasLast = false;
            foreach (var p in FindObjectsByType<MonitorPlane>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (p.monitorIndex == monitorIndex) { plane = p.transform; break; }
            if (plane == null) return;
        }
        if (!plane.gameObject.activeInHierarchy) { hasLast = false; return; }

        Vector3 pos = plane.position;
        Quaternion rot = plane.rotation;
        if (!hasLast || !follow) { lastPos = pos; lastRot = rot; hasLast = true; return; }

        if ((pos - lastPos).sqrMagnitude < minMove * minMove && Quaternion.Angle(rot, lastRot) < minAngle)
            return;   // 아직 안 움직임 (기준은 그대로 둬서 작은 움직임이 쌓이면 나중에 한 번에 반영)

        Quaternion dRot = rot * Quaternion.Inverse(lastRot);
        foreach (var q in FindObjectsByType<WindowQuadReceiver>(FindObjectsSortMode.None))
        {
            if (IsHandled(q.gameObject)) continue;
            var t = q.transform;
            t.SetPositionAndRotation(pos + dRot * (t.position - lastPos), dRot * t.rotation);
        }
        lastPos = pos;
        lastRot = rot;
    }

    static bool IsHandled(GameObject q)
    {
        foreach (var p in HandRayPointer.All)
            if (p != null && p.IsManipulating(q)) return true;
        if (HandDirectGrab.IsHeld(q)) return true;   // 손으로 직접 잡은 창
        var two = q.GetComponent<TwoHandGrabResize>();
        return two != null && two.IsHeld;
    }
}
