using UnityEngine;

// 연결선(화살표) 중앙의 잡기용 손잡이에 붙는 표식.
// 포인터가 이걸 주먹으로 잡아 멀리 치우면 owner가 스스로 사라진다.
//
// 데스크탑↔AR 연결선(WindowTether)과 AR창↔AR창 연결선(QuadLink) 양쪽에서 같은 손잡이를 쓴다.
// 평소엔 반투명이고, 포인터가 근처에 오면(넉넉한 콜라이더 = 자석) 또렷해지며 살짝 커진다.
public class TetherHandle : MonoBehaviour
{
    // 런타임에만 연결된다 (인터페이스라 인스펙터 직렬화는 안 됨)
    [System.NonSerialized] public ITetherCarry tether;

    [HideInInspector] public bool grabbed = false;

    private int lastHoverFrame = -1000;

    public void NotifyHover() { lastHoverFrame = Time.frameCount; }
    public bool IsHovered => grabbed || (Time.frameCount - lastHoverFrame) <= 1;
}
