using System.Collections.Generic;
using UnityEngine;

// [AR LAPTOP] 꺼낸 창을 손으로 직접 잡기
//
// 레이 없이, 손을 창에 갖다 대고 잡으면(엄지-검지 꼬집기 또는 주먹) 창이 손을 따라온다.
// 놓으면 그 자리에 남는다.
//   · 잡기 판정: 엄지-검지 꼬집기(pinch) 또는 다섯 손가락 다 접은 주먹
//               (검지만 편 가리키기는 터치용이라 잡기 아님)
//   · 거리: 손(꼬집는 점/손바닥)이 창 사각형에서 grabDistance 안
//   · 잡는 순간의 "손 → 창" 상대 위치를 기억해서, 손을 움직이고 돌리는 대로 창이 같이 움직인다
//   · 데스크탑 화면 판은 잡히지 않는다 (태그를 따라가야 하므로)
public class HandDirectGrab : MonoBehaviour
{
    public static HandDirectGrab Instance { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (Instance != null) return;
        var go = new GameObject("HandDirectGrab");
        DontDestroyOnLoad(go);
        go.AddComponent<HandDirectGrab>();
    }

    [Tooltip("손이 창에서 이 거리(m) 안이면 잡을 수 있음")]
    public float grabDistance = 0.06f;
    [Tooltip("엄지-검지 꼬집기 강도가 이 값 이상이면 잡기")]
    [Range(0f, 1f)] public float pinchOn = 0.85f;
    [Tooltip("잡은 뒤엔 이 아래로 떨어져야 놓음 (떨림 방지)")]
    [Range(0f, 1f)] public float pinchOff = 0.55f;
    [Tooltip("주먹: 다섯 손가락이 모두 이보다 덜 펴져야(=접혀야) 잡기")]
    public float fistCurledMax = 0.75f;
    [Tooltip("손 떨림을 거르는 정도 (클수록 바로 따라옴)")]
    public float followSharpness = 25f;

    class Grab
    {
        public Transform quad;
        public Vector3 localPos;        // 손 기준 창 위치
        public Quaternion localRot;     // 손 기준 창 회전
        public bool byPinch;
    }

    readonly Dictionary<HandRayPointer, Grab> grabs = new();

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }
    void OnDestroy() { if (Instance == this) Instance = null; }

    // 다른 스크립트(WindowsFollowDesktop 등)가 "지금 손에 잡힌 창인지" 묻는다
    public static bool IsHeld(GameObject q)
    {
        if (Instance == null || q == null) return false;
        foreach (var g in Instance.grabs.Values)
            if (g.quad != null && g.quad.gameObject == q) return true;
        return false;
    }

    public bool IsGrabbing(HandRayPointer p) => grabs.TryGetValue(p, out var g) && g.quad != null;

    void LateUpdate()
    {
        foreach (var p in HandRayPointer.All)
        {
            if (p == null) continue;
            grabs.TryGetValue(p, out var g);

            if (p.hand == null || !p.hand.IsTracked)
            {
                // 손을 잠깐 놓쳐도 바로 놓지 않는다 (창이 툭 떨어지지 않게) — 다시 보이면 이어서
                continue;
            }

            Pose hp = HandPose(p, out Vector3 pinchPoint);
            float pinch = p.hand.GetFingerPinchStrength(OVRHand.HandFinger.Index);
            var hs = FingerTouch.ReadShape(p);
            bool fist = hs.valid && hs.index < fistCurledMax
                        && Mathf.Max(hs.middle, Mathf.Max(hs.ring, hs.little)) < fistCurledMax;

            if (g != null && g.quad != null)
            {
                bool still = g.byPinch ? pinch >= pinchOff : (fist || pinch >= pinchOff);
                if (!still || !g.quad.gameObject.activeInHierarchy)
                {
                    grabs.Remove(p);
                    continue;
                }
                // 손을 따라 이동·회전
                Vector3 tp = hp.position + hp.rotation * g.localPos;
                Quaternion tr = hp.rotation * g.localRot;
                float a = 1f - Mathf.Exp(-followSharpness * Time.deltaTime);
                g.quad.SetPositionAndRotation(Vector3.Lerp(g.quad.position, tp, a), Quaternion.Slerp(g.quad.rotation, tr, a));
                continue;
            }

            // 새로 잡기: 꼬집기 또는 주먹 + 창 가까이
            bool pinchNow = pinch >= pinchOn;
            if (!pinchNow && !fist) continue;
            Vector3 probe = pinchNow ? pinchPoint : hp.position;
            Transform best = null;
            float bestD = grabDistance;
            foreach (var w in FindObjectsByType<WindowQuadReceiver>(FindObjectsSortMode.None))
            {
                if (!w.gameObject.activeInHierarchy || IsHeld(w.gameObject)) continue;
                float d = RectDistance(w.transform, probe);
                if (d < bestD) { bestD = d; best = w.transform; }
            }
            if (best == null) continue;

            Quaternion inv = Quaternion.Inverse(hp.rotation);
            grabs[p] = new Grab
            {
                quad = best,
                localPos = inv * (best.position - hp.position),
                localRot = inv * best.rotation,
                byPinch = pinchNow,
            };
        }
    }

    // 손의 기준 자세: 손바닥 뼈(있으면) 아니면 손 오브젝트. 꼬집는 점 = 엄지 끝과 검지 끝의 중간
    static Pose HandPose(HandRayPointer p, out Vector3 pinchPoint)
    {
        Transform palm = null, thumb = null, index = null;
        var sk = p.skeleton != null ? p.skeleton : p.hand.GetComponent<OVRSkeleton>();
        if (sk != null && sk.IsInitialized && sk.Bones != null)
        {
            var t = sk.GetSkeletonType();
            bool xr = t == OVRSkeleton.SkeletonType.XRHandLeft || t == OVRSkeleton.SkeletonType.XRHandRight;
            var palmId = xr ? OVRSkeleton.BoneId.XRHand_Palm : OVRSkeleton.BoneId.Hand_WristRoot;
            var thumbId = xr ? OVRSkeleton.BoneId.XRHand_ThumbTip : OVRSkeleton.BoneId.Hand_ThumbTip;
            var indexId = xr ? OVRSkeleton.BoneId.XRHand_IndexTip : OVRSkeleton.BoneId.Hand_IndexTip;
            foreach (var b in sk.Bones)
            {
                if (b.Id == palmId) palm = b.Transform;
                else if (b.Id == thumbId) thumb = b.Transform;
                else if (b.Id == indexId) index = b.Transform;
            }
        }
        Transform basis = palm != null ? palm : p.hand.transform;
        pinchPoint = thumb != null && index != null ? (thumb.position + index.position) * 0.5f : basis.position;
        return new Pose(basis.position, basis.rotation);
    }

    static float RectDistance(Transform t, Vector3 point)
    {
        Vector3 local = t.InverseTransformPoint(point);
        Vector3 onRect = new Vector3(Mathf.Clamp(local.x, -0.5f, 0.5f), Mathf.Clamp(local.y, -0.5f, 0.5f), 0f);
        return Vector3.Distance(point, t.TransformPoint(onRect));
    }
}
