using UnityEngine;

// 꺼낸 창 쿼드 하나. 자기 ID를 허브에 등록하면, 허브가 그 ID 프레임을 그려준다.
// 데스크탑으로 되돌리는 것(reinsert)은 HandRayPointer가 손으로 잡고 레이가 데스크탑
// 화면에 겹치는 순간을 감지해서 처리한다 (여기서는 등록/해제만 담당).
public class WindowQuadReceiver : MonoBehaviour
{
    public long windowId = 0;

    // 이 창을 꺼낸 데스크탑(모니터) index. 연결선(WindowTether)이 어느 모니터에 붙을지 결정.
    public int sourceMonitorIndex = 0;

    [Tooltip("켜면 창이 항상 수직으로 선다(기울이기·돌리기가 막힌다). " +
             "AR에서 자유롭게 기울이려면 꺼둘 것 — 이게 기본값이다. " +
             "기록에서 기울어져 보이는 건 replay의 '창 수직 보정' 버튼으로 보면 된다")]
    public bool keepUpright = false;

    private bool registered = false;

    // ─────────────────────────────────────────────────────────────────────────
    // 창이 뒤로 자빠지는 것을 여기서 최종적으로 막는다.
    //
    // 기울기는 한 군데서만 들어오는 게 아니다 - 손에 들고 있을 때, 주먹으로 잡아
    // 옮길 때, 양손으로 돌릴 때, 가장자리를 끌 때 각각 회전을 건드린다. 그래서
    // 한 곳만 고치면 다른 경로로 또 기울어진다.
    // LateUpdate는 그 처리들이 다 끝난 뒤에 돌기 때문에, 여기서 한 번 세우면
    // 어떤 경로로 기울어졌든 그 프레임 안에 바로잡힌다.
    // ─────────────────────────────────────────────────────────────────────────
    void Awake()
    {
        // 씬/프리팹에 저장된 옛 값이 덮어쓰지 못하게 코드에서 못 박는다.
        // AR 안에서는 위아래 기울기가 살아 있어야 한다. 기록을 수직으로 보고 싶으면
        // replay의 '창 수직 보정' 버튼을 쓴다 (그쪽은 기본 켜짐).
        keepUpright = false;
    }

    void LateUpdate()
    {
        if (!keepUpright) return;
        Vector3 f = transform.forward;
        f.y = 0f;                                  // 앞뒤로 눕는 각도(pitch)를 버린다
        if (f.sqrMagnitude < 1e-6f) return;        // 정확히 위/아래를 보고 있으면 건너뛴다
        transform.rotation = Quaternion.LookRotation(f.normalized, Vector3.up);
    }

    public void Init(long id)
    {
        windowId = id;
        TryRegister();
    }

    void Start()
    {
        if (windowId != 0) TryRegister();
    }

    void TryRegister()
    {
        if (registered) return;
        if (WindowCaptureHub.Instance == null)
        {
            Debug.LogWarning("[WindowQuad] Hub가 없음");
            return;
        }
        var r = GetComponent<Renderer>();
        WindowCaptureHub.Instance.Register(windowId, r);
        registered = true;

        // 양손으로 잡고 돌리기/기울이기/이동은 그대로 유지 (프리팹 수정 불필요).
        // 다만 양손 간격으로 "크기"를 바꾸는 것만 꺼둔다 - 크기 조절은 꼭짓점 핸들이 담당.
        if (GetComponent<TwoHandGrabResize>() == null)
            gameObject.AddComponent<TwoHandGrabResize>();

        // 가장자리(한 변) 리사이즈 컴포넌트도 자동으로 붙여준다.
        // 네 변에 핸들 막대를 만들고, 포인터가 핸들을 잡아 끌면 그 축만 늘려서 비율을 바꾼다.
        if (GetComponent<EdgeResizer>() == null)
            gameObject.AddComponent<EdgeResizer>();

        // 데스크탑과 이어주는 선은 이제 자동으로 안 붙인다.
        // 사용자가 "데스크탑 꼬집 → 창 꼬집"으로 직접 만든다 (QuadLinkManager).
    }
}
