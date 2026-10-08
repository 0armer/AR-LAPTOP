using UnityEngine;
using Meta.XR.MRUtilityKit;

// Space Setup에서 "Screen"으로 라벨링한 물리 모니터 앵커를 찾아,
// 모니터 투명판 쿼드(PCStreamReceiver.targetRenderer)를 그 위에 자동 정렬한다.
//
// - 실행할 때마다 앵커 위치로 붙으므로 수동 배치 불필요
// - 쿼드를 앵커의 자식으로 두므로, 에어링크 재연결/재실행에도 위치가 유지됨
// - 모니터가 여러 개면 screenIndex로 어느 화면에 붙일지 선택 (0 = 첫 번째)
//
// MRUK/앵커를 못 찾으면 아무것도 건드리지 않으므로, 기존 수동 배치 워크플로우도 그대로 동작한다.
public class MonitorScreenAnchor : MonoBehaviour
{
    [Header("정렬 대상 (비우면 PCStreamReceiver.targetRenderer 자동 사용)")]
    [Tooltip("물리 모니터 위에 붙일 투명판 쿼드")]
    public Transform monitorQuad;

    [Header("모니터가 여러 개일 때 몇 번째 Screen에 붙일지 (0부터)")]
    public int screenIndex = 0;

    [Header("쿼드를 모니터 앞으로 살짝 띄우는 오프셋(m). Z-fighting 방지")]
    public float depthOffset = 0.01f;

    [Header("쿼드 앞면이 반대로 보이면 (0,180,0)으로")]
    public Vector3 rotationOffsetEuler = Vector3.zero;

    [Header("PlaneRect 실측 크기로 쿼드 스케일 자동 맞춤")]
    public bool matchScreenSize = true;

    private bool bound = false;

    void Start()
    {
        // 대상 쿼드 자동 탐색
        if (monitorQuad == null && PCStreamReceiver.Instance != null &&
            PCStreamReceiver.Instance.targetRenderer != null)
        {
            monitorQuad = PCStreamReceiver.Instance.targetRenderer.transform;
        }

        if (MRUK.Instance == null)
        {
            Debug.LogWarning("[MonitorScreenAnchor] 씬에 MRUK가 없습니다. 씬에 MRUK를 추가하세요.");
            return;
        }

        // 방 데이터 로딩이 끝나면 BindToScreen 호출
        MRUK.Instance.RegisterSceneLoadedCallback(BindToScreen);
    }

    void BindToScreen()
    {
        if (bound) return;
        if (monitorQuad == null)
        {
            Debug.LogWarning("[MonitorScreenAnchor] 정렬할 모니터 쿼드를 찾지 못했습니다.");
            return;
        }

        var room = MRUK.Instance.GetCurrentRoom();
        if (room == null)
        {
            Debug.LogWarning("[MonitorScreenAnchor] 현재 방을 찾지 못했습니다.");
            return;
        }

        // SCREEN 라벨 앵커만 모음
        int found = 0;
        MRUKAnchor target = null;
        foreach (var anchor in room.Anchors)
        {
            if (!anchor.HasAnyLabel(MRUKAnchor.SceneLabels.SCREEN)) continue;
            if (found == screenIndex) target = anchor;
            found++;
        }

        if (target == null)
        {
            Debug.LogWarning($"[MonitorScreenAnchor] SCREEN 앵커를 찾지 못함 " +
                             $"(발견 {found}개, 요청 index={screenIndex}). " +
                             $"Space Setup에서 모니터를 'Screen'으로 라벨링했는지 확인하세요.");
            return;
        }

        // 앵커의 자식으로 붙여서 위치를 고정
        monitorQuad.SetParent(target.transform, worldPositionStays: false);
        monitorQuad.localPosition = new Vector3(0f, 0f, depthOffset);
        monitorQuad.localRotation = Quaternion.Euler(rotationOffsetEuler);

        if (matchScreenSize && target.PlaneRect.HasValue)
        {
            var size = target.PlaneRect.Value.size; // (width, height) m
            monitorQuad.localScale = new Vector3(size.x, size.y, 1f);
        }

        bound = true;
        Debug.Log($"[MonitorScreenAnchor] SCREEN 앵커 {found}개 중 index={screenIndex}에 " +
                  $"모니터 쿼드를 붙였습니다.");
    }
}
