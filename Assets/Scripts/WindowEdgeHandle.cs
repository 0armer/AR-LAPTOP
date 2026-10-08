using UnityEngine;

// 창(WindowQuad)의 리사이즈 핸들 하나. EdgeResizer가 런타임에 만들어 붙이므로 인스펙터에서 만질 일은 없다.
//
// 구조: 이 오브젝트(루트)에는 "잡히는 범위"인 BoxCollider만 있고(눈에 안 보임),
//       실제로 보이는 얇은 막대는 자식(visual)이다.
//       → 보이는 건 얇지만 콜라이더는 넉넉해서, 포인터를 근처에 대기만 해도 자석처럼 잡힌다.
public class WindowEdgeHandle : MonoBehaviour
{
    public EdgeResizer owner;
    public EdgeResizer.Edge edge;

    [HideInInspector] public Transform visual;

    // 지금 이 핸들을 잡고 리사이즈 중인지 (EdgeResizer가 설정)
    [HideInInspector] public bool grabbed = false;

    private int lastHoverFrame = -1000;

    // 포인터 레이가 이 핸들(패딩 포함)에 닿은 프레임마다 호출. 양손/컨트롤러가 각자 불러도 문제없다.
    public void NotifyHover() { lastHoverFrame = Time.frameCount; }

    // 잡혀 있거나, 직전 프레임에 포인터가 닿아 있었으면 강조 상태
    public bool IsHovered => grabbed || (Time.frameCount - lastHoverFrame) <= 1;

    // 레이가 맞은 지점을 이 핸들의 "중심선" 위로 끌어당긴 점 (레이 끝이 착 달라붙어 보이게).
    public Vector3 SnapPoint(Vector3 worldPoint)
    {
        if (visual == null) return transform.position;

        Vector3 center = visual.position;
        Vector3 axis;
        float halfLen;

        switch (edge)
        {
            case EdgeResizer.Edge.Left:
            case EdgeResizer.Edge.Right:
                axis = visual.up; halfLen = visual.localScale.y * 0.5f; break;
            case EdgeResizer.Edge.Top:
            case EdgeResizer.Edge.Bottom:
                axis = visual.right; halfLen = visual.localScale.x * 0.5f; break;
            default:
                return center;   // 꼭짓점은 점 하나로 스냅
        }

        float t = Mathf.Clamp(Vector3.Dot(worldPoint - center, axis), -halfLen, halfLen);
        return center + axis * t;
    }
}
