using System.Collections.Generic;
using UnityEngine;

// 꺼내 놓은 창 쿼드(WindowQuadReceiver)에 자동으로 붙는 컴포넌트.
// 한 손만 주먹으로 잡으면 HandRayPointer가 직접 위치/회전을 옮긴다 (기존 방식, 그대로 유지).
//
// 양손이 동시에 같은 창을 주먹으로 잡으면 이 컴포넌트가 전담한다. 동작 방식:
//   - 주먹을 쥐는 순간, 그 손의 레이가 쿼드에 맞은 지점을 "앵커"로 삼는다(HandRayPointer가
//     raycast hit point를 넘겨줌). 앵커는 쿼드의 로컬 좌표계에 고정되고, 그 앵커까지의
//     거리(손->앵커)도 그 순간 값으로 고정되어 이후 절대 다시 재지 않는다.
//   - 매 프레임, 각 손의 "지금" 레이(시작점+방향)를 그 고정 거리만큼 뻗은 지점을 그 손 앵커의
//     새 목표 위치로 삼는다. 즉 앵커는 손의 레이 위에 항상 같은 깊이로 꽂혀서 따라다닌다.
//   - 두 앵커의 목표 위치를 잇는 축(worldAxis)과, 두 손이 "가리키는 평균 방향"(avgForward)으로
//     그립 프레임을 만든다. avgForward를 쓰는 이유: 앵커 위치 자체가 이미 "손의 방향 x 고정거리"로
//     계산되기 때문에, 회전의 남은 자유도(두 앵커를 잇는 축 둘레의 roll)도 "손의 원본 회전(업 벡터)"
//     같은 별도 신호를 섞으면 서로 어긋나며 흔들린다. 대신 같은 신호(레이 방향)에서 파생된 값을
//     쓰면 오른손을 찌르고 왼손을 당기는 동작(요잉)과, 양손을 같이 위/아래로 꺾는 동작(피칭)이
//     둘 다 일관되게 반영된다 - 종이를 들고 손목을 꺾으면 종이가 그만큼 숙여지는 느낌.
//   - 위치는 창을 앵커 A 한 점에 딱 고정하지 않고, 두 앵커의 중점 기준 + grab 시작 시점에
//     저장해둔 상대 오프셋으로 계산한다. 한쪽 앵커에만 쏠리지 않아서 더 안정적이다.
//   - 두 앵커 사이의 월드 거리 변화 비율로 크기(균일 스케일)를 조절한다: 앵커 사이가 멀어지면
//     쿼드가 커지고, 가까워지면 작아진다.
public class TwoHandGrabResize : MonoBehaviour
{
    [Tooltip("양손 사이를 벌려/좁혀 크기를 바꾸는 기능. 지금은 꼭짓점 핸들이 크기 조절을 담당하므로 꺼둔다. " +
             "(회전/기울이기/이동은 이 값과 무관하게 계속 동작)")]
    public bool allowScale = false;

    public float minScale = 0.1f;
    public float maxScale = 3f;

    public bool IsTwoHanded => grabbingHands.Count >= 2;
    public bool IsHeld => grabbingHands.Count >= 1; // 한손이든 양손이든 누군가 잡고 있으면 true

    private class HandState
    {
        public Vector3 position;       // 손 레이 시작점 (매 프레임 갱신)
        public Quaternion rotation;    // 손의 전체 회전(매 프레임 갱신) - forward만 앵커/프레임 계산에 사용
        public Vector3 anchorLocalPos; // 이 손이 처음 잡았을 때의 쿼드 로컬 좌표 (고정, grab 시점에만 기록)
        public float holdDistance;     // 손 시작점 -> 앵커까지 거리 (고정, grab 시점에만 기록)
    }

    private readonly Dictionary<string, HandState> grabbingHands = new();

    // 두 손이 잡은 순서를 프레임 간에 일관되게 유지하기 위한 키
    private string handIdA;
    private string handIdB;

    private float initialAnchorDistance = 0.01f;
    private Vector3 initialScale;

    // grab 시작 시점의 그립 프레임 기준으로 저장해둔, 창의 상대 위치/회전 오프셋
    private Quaternion rotOffset = Quaternion.identity;
    private Vector3 posOffset;

    public void OnGrabStart(string handId, Vector3 pos, Quaternion rot, Vector3 anchorLocalPos, float holdDistance)
    {
        grabbingHands[handId] = new HandState
        {
            position = pos,
            rotation = rot,
            anchorLocalPos = anchorLocalPos,
            holdDistance = Mathf.Max(holdDistance, 0.01f)
        };
        if (grabbingHands.Count == 2)
            BeginTwoHand();
    }

    public void OnGrabEnd(string handId)
    {
        grabbingHands.Remove(handId);
    }

    // HandRayPointer가 주먹을 쥐고 있는 동안 매 프레임 호출. 앵커(로컬 좌표/고정 거리)는
    // 여기서 갱신하지 않는다 - 오직 손의 현재 위치/회전만 갱신.
    public void UpdateHand(string handId, Vector3 pos, Quaternion rot)
    {
        if (!grabbingHands.TryGetValue(handId, out var st)) return;
        st.position = pos;
        st.rotation = rot;

        if (grabbingHands.Count >= 2)
            ApplyTwoHand();
    }

    void BeginTwoHand()
    {
        var ids = new List<string>(grabbingHands.Keys);
        ids.Sort(); // "L"/"R" 등 - 프레임마다 같은 순서가 나오도록 고정
        handIdA = ids[0];
        handIdB = ids[1];

        var a = grabbingHands[handIdA];
        var b = grabbingHands[handIdB];

        initialScale = transform.localScale;

        Quaternion frameStart = ComputeGripFrame(a, b, out Vector3 targetA, out Vector3 targetB);
        Vector3 centerStart = (targetA + targetB) * 0.5f;

        initialAnchorDistance = Mathf.Max(Vector3.Distance(targetA, targetB), 0.01f);

        rotOffset = Quaternion.Inverse(frameStart) * transform.rotation;
        posOffset = Quaternion.Inverse(frameStart) * (transform.position - centerStart);

        Debug.Log("[TwoHandResize] 양손 잡기 시작, 초기 앵커 거리=" + initialAnchorDistance);
    }

    void ApplyTwoHand()
    {
        if (!grabbingHands.TryGetValue(handIdA, out var a) || !grabbingHands.TryGetValue(handIdB, out var b))
            return;

        Quaternion frameNow = ComputeGripFrame(a, b, out Vector3 targetA, out Vector3 targetB);
        Vector3 centerNow = (targetA + targetB) * 0.5f;

        // === 크기: 두 앵커 사이 월드 거리 변화 비율 ===
        // allowScale이 꺼져 있으면 크기는 건드리지 않는다 (크기 조절은 꼭짓점 핸들 담당).
        // 회전/위치는 아래에서 그대로 처리되므로, 양손으로 잡고 돌리고 기울이는 건 계속 된다.
        if (allowScale)
        {
            float dist = Vector3.Distance(targetA, targetB);
            float factor = dist / initialAnchorDistance;
            Vector3 targetScale = initialScale * factor;
            float clampedX = Mathf.Clamp(targetScale.x, minScale, maxScale);
            float uniformFactor = clampedX / Mathf.Max(initialScale.x, 0.0001f);
            transform.localScale = initialScale * uniformFactor;
        }

        // === 회전 + 위치: grab 시작 시점의 그립 프레임 기준 상대 오프셋을 유지 ===
        transform.rotation = frameNow * rotOffset;
        transform.position = centerNow + frameNow * posOffset;
    }

    // 두 손의 앵커 목표 위치(targetA/targetB)와, 그 둘로 만드는 그립 프레임 회전을 계산한다.
    // right(로컬 X) = 앵커A -> 앵커B, forward(로컬 Z) = 두 손이 가리키는 평균 방향을 right에
    // 수직하게 투영한 것. 같은 "레이 방향" 신호에서 회전을 뽑아내므로, 오른손을 찌르고 왼손을
    // 당기는 동작(요잉)과 양손을 같이 위/아래로 꺾는 동작(피칭)이 서로 어긋나지 않고 일관된다.
    Quaternion ComputeGripFrame(HandState a, HandState b, out Vector3 targetA, out Vector3 targetB)
    {
        Vector3 forwardA = a.rotation * Vector3.forward;
        Vector3 forwardB = b.rotation * Vector3.forward;

        targetA = a.position + forwardA * a.holdDistance;
        targetB = b.position + forwardB * b.holdDistance;

        Vector3 right = targetB - targetA;
        Vector3 avgForward = forwardA + forwardB;

        return BuildFrame(right, avgForward);
    }

    // right 방향을 로컬 X축으로, forwardRef를 (직교화한) 로컬 Z축(정면)으로 갖는 회전을 만든다.
    static Quaternion BuildFrame(Vector3 right, Vector3 forwardRef)
    {
        if (right.sqrMagnitude < 0.0001f) right = Vector3.right;
        right.Normalize();

        Vector3 forward = forwardRef - Vector3.Project(forwardRef, right);
        if (forward.sqrMagnitude < 0.0001f)
        {
            // forwardRef가 right와 거의 평행하면 임의의 안전한 대체 축 사용
            Vector3 fallback = Mathf.Abs(Vector3.Dot(right, Vector3.forward)) < 0.9f ? Vector3.forward : Vector3.up;
            forward = fallback - Vector3.Project(fallback, right);
        }
        forward.Normalize();

        Vector3 upwards = Vector3.Cross(forward, right);
        if (upwards.sqrMagnitude < 0.0001f) upwards = Vector3.up;
        upwards.Normalize();

        return Quaternion.LookRotation(forward, upwards);
    }
}
