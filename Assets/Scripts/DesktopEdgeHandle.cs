using UnityEngine;

// 데스크탑(모니터 판) 네 변에 놓이는 얇은 네모 핸들 하나.
// 여기를 핀치로 잡고 끌면 화살표가 새로 생기고, AR 창까지 이으면 그 창에 연결선이 다시 만들어진다.
// (연결선을 지운 뒤 되살리는 수단)
//
// 구조는 창 핸들과 같다: 루트에는 넉넉한 BoxCollider(자석), 자식에 얇은 네모(visual).
public class DesktopEdgeHandle : MonoBehaviour
{
    public DesktopTetherSpawner owner;
    public int monitorIndex = 0;
    public Transform monitor;              // 이 핸들이 붙어 있는 모니터 판
    public int edge = 0;                   // 0=Left 1=Right 2=Top 3=Bottom

    [HideInInspector] public Transform visual;

    [HideInInspector] public bool grabbed = false;

    private int lastHoverFrame = -1000;

    public void NotifyHover() { lastHoverFrame = Time.frameCount; }
    public bool IsHovered => grabbed || (Time.frameCount - lastHoverFrame) <= 1;

    // 레이가 맞은 지점을 이 막대의 중심선 위로 끌어당긴 점
    public Vector3 SnapPoint(Vector3 worldPoint)
    {
        if (visual == null) return transform.position;
        Vector3 center = visual.position;
        Vector3 axis;
        float halfLen;

        if (edge == 0 || edge == 1) { axis = visual.up; halfLen = visual.localScale.y * 0.5f; }
        else { axis = visual.right; halfLen = visual.localScale.x * 0.5f; }

        float t = Mathf.Clamp(Vector3.Dot(worldPoint - center, axis), -halfLen, halfLen);
        return center + axis * t;
    }
}
