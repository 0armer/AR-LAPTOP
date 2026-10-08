using UnityEngine;

// 양손을 맞대면(박수) 손 트래킹 조작 전체를 껐다 켰다 하는 토글.
//
// 왜 필요한가: 헤드셋을 쓴 채로 다른 일을 할 때도 손 제스처가 계속 인식돼서
// 창이 잡히거나 클릭되는 오조작이 생긴다. 박수 한 번으로 손 조작을 통째로 재워둔다.
//
// 판정 방식: 두 손 사이 거리가 clapDistance보다 가까워지는 "순간"에 토글한다.
// 한 번 토글하면 손을 releaseDistance 이상 벌려야 다시 무장(armed)되므로,
// 손을 맞댄 채로 있어도 계속 껐다 켜졌다 하지 않는다.
//
// 씬에 아무것도 안 넣어도 되도록 실행 시 자동 생성된다.
public class HandInteractionToggle : MonoBehaviour
{
    public static HandInteractionToggle Instance { get; private set; }

    // 손 조작이 켜져 있는지. HandRayPointer가 매 프레임 이 값을 본다.
    public static bool HandsEnabled = true;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (Instance != null) return;
        var go = new GameObject("HandInteractionToggle");
        go.AddComponent<HandInteractionToggle>();
    }

    [Header("두 손이 이만큼(m) 가까워지면 토글")]
    public float clapDistance = 0.09f;

    [Header("다시 토글하려면 이만큼(m) 벌려야 함 (연속 토글 방지)")]
    public float releaseDistance = 0.30f;

    [Header("두 손이 서로 다가오는 속도(m/s)가 이보다 빨라야 박수로 인정")]
    [Tooltip("이게 핵심. 손이 그냥 가까이 '있는' 것만으로는 토글되지 않고, 실제로 짝 부딪히듯 " +
             "빠르게 모아야만 인정된다. 시야에 손이 다시 들어올 때 오작동하는 걸 막아준다.")]
    public float minApproachSpeed = 0.45f;

    [Header("토글 직후 이 시간(초) 동안은 다시 토글되지 않음")]
    public float cooldown = 0.5f;

    [Header("두 손이 안정적으로 보인 뒤 이 시간(초)이 지나야 박수를 인정")]
    [Tooltip("손이 시야 밖으로 나갔다 돌아오는 순간엔 위치가 튀어서 박수로 오인된다. 그걸 막는 안정화 시간.")]
    public float trackingSettleTime = 0.35f;

    [Header("꺼졌을 때 로그 남기기")]
    public bool logToggle = true;

    private bool armed = false;        // 손을 충분히 벌린 걸 확인한 상태 = 다음 박수를 받을 준비됨
    private float lastToggleTime = -10f;
    private float trackedSince = -1f;  // 두 손이 연속으로 잘 보이기 시작한 시각
    private float prevDist = -1f;      // 직전 프레임의 두 손 거리 (다가오는 속도 계산용)

    void Awake() { Instance = this; }

    void Update()
    {
        // 신뢰도 높게 추적 중인 손 두 개 찾기 (막 들어온 손은 위치가 튀므로 제외)
        Vector3 a = Vector3.zero, b = Vector3.zero;
        int found = 0;
        var all = HandRayPointer.All;
        for (int i = 0; i < all.Count && found < 2; i++)
        {
            var p = all[i];
            if (p == null || !p.IsHandConfident) continue;
            if (found == 0) a = p.CurrentHandPos; else b = p.CurrentHandPos;
            found++;
        }

        // 두 손이 다 안 보이면: 추적이 끊긴 것으로 보고 무장을 푼다.
        // 이렇게 해야 손이 시야 밖으로 나갔다 돌아올 때 위치가 튀면서 박수로 오인되는 걸 막는다.
        if (found < 2)
        {
            trackedSince = -1f;
            armed = false;
            prevDist = -1f;
            return;
        }

        // 다시 잡힌 직후엔 잠깐 기다린다 (위치가 안정될 시간). 이 동안은 속도 계산도 하지 않는다.
        if (trackedSince < 0f) { trackedSince = Time.time; prevDist = -1f; }
        if (Time.time - trackedSince < trackingSettleTime) return;

        float dist = Vector3.Distance(a, b);

        // 두 손이 서로 다가오는 속도 (양수 = 가까워지는 중)
        float approachSpeed = 0f;
        if (prevDist >= 0f && Time.deltaTime > 1e-5f)
            approachSpeed = (prevDist - dist) / Time.deltaTime;
        prevDist = dist;

        // 손을 충분히 벌린 걸 "직접 본" 뒤에만 박수를 인정한다
        if (dist > releaseDistance) armed = true;

        // 가깝기만 해선 안 되고, 실제로 빠르게 부딪혀 와야 토글된다.
        // 그래서 손을 모은 채 시야에 들어오거나 무릎 위에 손을 얹어둬도 켜지지 않는다.
        if (armed && dist < clapDistance && approachSpeed >= minApproachSpeed
            && Time.time - lastToggleTime > cooldown)
        {
            HandsEnabled = !HandsEnabled;
            armed = false;
            lastToggleTime = Time.time;
            if (logToggle)
                Debug.Log($"[HandToggle] 손 조작 {(HandsEnabled ? "켜짐" : "꺼짐")} (박수)");
        }
    }

    void OnDestroy() { if (Instance == this) Instance = null; }

    // 씬을 다시 로드해도 켜진 상태로 시작하도록
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetState() { HandsEnabled = true; }
}
