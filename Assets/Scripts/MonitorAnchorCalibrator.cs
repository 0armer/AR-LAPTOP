using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

// 비스듬히 놓인 모니터를 컨트롤러로 직접 캘리브레이션해서, 그 위에 쿼드를 배치한다.
//
// [사용법]
//  1. 실행하면(저장된 앵커가 없으면) 캘리브레이션 모드로 대기.
//  2. 컨트롤러 끝을 모니터의 실제 모서리에 대고 A버튼으로 3점 찍기:
//        1번 = 좌하(bottom-left)
//        2번 = 우하(bottom-right)
//        3번 = 좌상(top-left)
//  3. 3점을 찍으면 위치·기울기·크기를 계산해 쿼드를 놓고 Spatial Anchor로 저장.
//  4. 다음 실행부터는 저장된 앵커를 자동으로 불러와 같은 자리에 쿼드가 뜬다.
//  5. B버튼 = 저장 삭제 후 재캘리브레이션.
//
// Space Setup의 축정렬 박스와 달리, 임의 기울기(pitch/roll)를 그대로 잡는다.
public class MonitorAnchorCalibrator : MonoBehaviour
{
    [Header("이 모니터에 붙일 쿼드 (비스듬한 모니터용)")]
    public Transform monitorQuad;

    [Header("모니터 구분용 저장 슬롯 키 (모니터마다 다르게)")]
    public string slotKey = "monitor2";

    [Header("모서리를 찍을 컨트롤러 (끝 위치를 읽음)")]
    public OVRInput.Controller pointerController = OVRInput.Controller.RTouch;
    [Tooltip("컨트롤러 끝 Transform. 비우면 RightControllerAnchor를 이름으로 찾음")]
    public Transform pointerAnchor;

    [Header("버튼")]
    public OVRInput.Button recordButton = OVRInput.Button.One;  // A
    public OVRInput.Button resetButton = OVRInput.Button.Two;   // B

    [Header("배치 미세조정")]
    [Tooltip("쿼드를 모니터 앞으로 띄우는 거리(m)")]
    public float depthOffset = 0.005f;
    [Tooltip("쿼드 앞면이 반대로 보이면 (0,180,0)")]
    public Vector3 rotationOffsetEuler = new Vector3(0f, 180f, 0f);

    private readonly List<Vector3> corners = new();
    private GameObject anchorGo;
    private bool busy = false;
    private bool placed = false;

    async void Start()
    {
        if (pointerAnchor == null)
        {
            var go = GameObject.Find("RightControllerAnchor") ?? GameObject.Find("RightHandAnchor");
            if (go != null) pointerAnchor = go.transform;
        }

        if (monitorQuad == null)
            Debug.LogWarning("[MonitorCalib] monitorQuad가 비어있음. Inspector에서 지정하세요.");

        // 저장된 앵커가 있으면 자동 로드
        if (HasSaved())
            await LoadSavedAsync();
        else
            Debug.Log("[MonitorCalib] 저장된 앵커 없음. A버튼으로 모서리 3점(좌하→우하→좌상)을 찍어 캘리브레이션하세요.");
    }

    void Update()
    {
        OVRInput.Update();

        // 재캘리브레이션
        if (OVRInput.GetDown(resetButton, pointerController))
        {
            ClearSaved();
            corners.Clear();
            placed = false;
            if (anchorGo != null) Destroy(anchorGo);
            Debug.Log("[MonitorCalib] 저장 삭제. 다시 3점을 찍으세요.");
            return;
        }

        if (placed || busy) return;

        // 모서리 기록
        if (OVRInput.GetDown(recordButton, pointerController) && pointerAnchor != null)
        {
            corners.Add(pointerAnchor.position);
            Debug.Log($"[MonitorCalib] 모서리 {corners.Count}/3 기록: {pointerAnchor.position}");

            if (corners.Count == 3)
                _ = CalibrateAndSaveAsync();
        }
    }

    // 3점 → pose/크기 계산 → 쿼드 배치 → Spatial Anchor 저장
    async Task CalibrateAndSaveAsync()
    {
        busy = true;

        Vector3 p1 = corners[0]; // 좌하
        Vector3 p2 = corners[1]; // 우하
        Vector3 p3 = corners[2]; // 좌상

        Vector3 rightVec = p2 - p1;
        Vector3 upVec = p3 - p1;
        float width = rightVec.magnitude;
        float height = upVec.magnitude;

        if (width < 0.01f || height < 0.01f)
        {
            Debug.LogWarning("[MonitorCalib] 모서리 간격이 너무 좁음. 다시 찍으세요.");
            corners.Clear();
            busy = false;
            return;
        }

        Vector3 rightN = rightVec.normalized;
        Vector3 normal = Vector3.Cross(rightN, upVec).normalized; // 화면 법선

        // 법선이 머리 반대로 향하면 뒤집기 (쿼드가 사용자를 보게)
        Vector3 headPos = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
        Vector3 center = p1 + rightVec * 0.5f + upVec * 0.5f;
        if (Camera.main != null && Vector3.Dot(normal, headPos - center) < 0f)
            normal = -normal;

        Vector3 upOrtho = (upVec - Vector3.Dot(upVec, normal) * normal).normalized;
        Quaternion rot = Quaternion.LookRotation(normal, upOrtho);

        // 앵커 생성
        anchorGo = new GameObject($"MonitorAnchor_{slotKey}");
        anchorGo.transform.SetPositionAndRotation(center, rot);
        var anchor = anchorGo.AddComponent<OVRSpatialAnchor>();

        if (!await WaitForCreated(anchor))
        {
            Debug.LogError("[MonitorCalib] 앵커 생성 실패(타임아웃). Anchor Support가 켜져 있는지 확인하세요.");
            busy = false;
            return;
        }

        // 저장
        var list = new List<OVRSpatialAnchor> { anchor };
        var saveResult = await OVRSpatialAnchor.SaveAnchorsAsync(list);
        if (!saveResult.Success)
        {
            Debug.LogWarning($"[MonitorCalib] 앵커 저장 실패: {saveResult.Status}");
        }
        else
        {
            SaveToPrefs(anchor.Uuid, width, height);
            Debug.Log($"[MonitorCalib] 저장 완료 uuid={anchor.Uuid}, size={width:F2}x{height:F2}m");
        }

        AttachQuad(anchorGo.transform, width, height);
        placed = true;
        busy = false;
    }

    // 앵커 자식으로 쿼드 붙이기
    void AttachQuad(Transform anchorT, float width, float height)
    {
        if (monitorQuad == null) return;
        monitorQuad.SetParent(anchorT, worldPositionStays: false);
        monitorQuad.localPosition = new Vector3(0f, 0f, depthOffset);
        monitorQuad.localRotation = Quaternion.Euler(rotationOffsetEuler);
        monitorQuad.localScale = new Vector3(width, height, 1f);
    }

    async Task<bool> WaitForCreated(OVRSpatialAnchor anchor)
    {
        float t0 = Time.time;
        while (anchor && !anchor.Created)
        {
            if (Time.time - t0 > 5f) return false;
            await Task.Yield();
        }
        return anchor && anchor.Created;
    }

    // ---- 저장된 앵커 로드 ----
    async Task LoadSavedAsync()
    {
        busy = true;
        Guid uuid = new Guid(PlayerPrefs.GetString(Key("uuid")));
        float width = PlayerPrefs.GetFloat(Key("w"), 0.5f);
        float height = PlayerPrefs.GetFloat(Key("h"), 0.3f);

        var unbound = new List<OVRSpatialAnchor.UnboundAnchor>();
        var result = await OVRSpatialAnchor.LoadUnboundAnchorsAsync(new[] { uuid }, unbound);
        if (!result.Success || unbound.Count == 0)
        {
            Debug.LogWarning($"[MonitorCalib] 저장된 앵커 로드 실패: {result.Status}. A버튼으로 다시 캘리브레이션하세요.");
            busy = false;
            return;
        }

        var ua = unbound[0];
        if (!ua.Localized && !await ua.LocalizeAsync())
        {
            Debug.LogWarning("[MonitorCalib] 앵커 로컬라이즈 실패.");
            busy = false;
            return;
        }

        ua.TryGetPose(out var pose);
        anchorGo = new GameObject($"MonitorAnchor_{slotKey}");
        anchorGo.transform.SetPositionAndRotation(pose.position, pose.rotation);
        var anchor = anchorGo.AddComponent<OVRSpatialAnchor>();
        ua.BindTo(anchor);

        AttachQuad(anchorGo.transform, width, height);
        placed = true;
        busy = false;
        Debug.Log($"[MonitorCalib] 저장된 앵커로 쿼드 배치 완료 ({slotKey}).");
    }

    // ---- PlayerPrefs 헬퍼 ----
    string Key(string s) => $"{slotKey}_{s}";
    bool HasSaved() => PlayerPrefs.HasKey(Key("uuid")) && !string.IsNullOrEmpty(PlayerPrefs.GetString(Key("uuid")));

    void SaveToPrefs(Guid uuid, float w, float h)
    {
        PlayerPrefs.SetString(Key("uuid"), uuid.ToString());
        PlayerPrefs.SetFloat(Key("w"), w);
        PlayerPrefs.SetFloat(Key("h"), h);
        PlayerPrefs.Save();
    }

    void ClearSaved()
    {
        PlayerPrefs.DeleteKey(Key("uuid"));
        PlayerPrefs.DeleteKey(Key("w"));
        PlayerPrefs.DeleteKey(Key("h"));
        PlayerPrefs.Save();
    }
}
