using UnityEngine;

// 이 판(투명 상호작용 표면)이 어느 물리 모니터에 대응하는지 나타낸다.
//   monitorIndex = 0  → 주 모니터
//   monitorIndex = 1  → 두 번째 모니터 ...
// 캘리브레이터의 monitorQuads 배열 순서(slot index)와 같게 두면 된다.
//
// SimpleRayPointer가 레이로 이 판을 맞히면 monitorIndex를 읽어
// 서버에 "type,u,v,mon" 형태로 실어 보낸다.
public class MonitorPlane : MonoBehaviour
{
    [Tooltip("0 = 주 모니터, 1 = 두 번째 모니터 ...")]
    public int monitorIndex = 0;
}
