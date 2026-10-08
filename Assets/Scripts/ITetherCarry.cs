using UnityEngine;

// "손잡이를 잡아서 옮기고, 멀리 치우면 사라지는" 연결선의 공통 인터페이스.
// 데스크탑↔AR 연결선(WindowTether)과 AR창↔AR창 연결선(QuadLink)이 같은 손잡이/포인터 코드를 쓰기 위한 것.
public interface ITetherCarry
{
    void BeginCarry();
    void UpdateCarry(Vector3 worldPoint);
    void EndCarry();
}
