using UnityEngine;

[RequireComponent(typeof(LineRenderer))]
public class ControllerRayClicker : MonoBehaviour
{
    [Header("레이캐스트")]
    public float maxDistance = 10f;
    public LayerMask hitLayers = ~0;

    [Header("시작점 오프셋")]
    public float forwardOffset = 0.05f;

    [Header("레이저 색")]
    public Color idleColor = Color.white;
    public Color clickColor = Color.green;

    [Header("클릭 시 굵기")]
    public float idleWidth = 0.005f;
    public float clickWidth = 0.015f;

    [Header("트리거 감도")]
    public float triggerThreshold = 0.5f;

    [Header("연결")]
    public PCStreamReceiver sender;
    public Renderer targetQuadRenderer;

    private LineRenderer line;
    private Material lineMat;
    private bool wasPressed = false;

    void Start()
    {
        line = GetComponent<LineRenderer>();
        line.positionCount = 2;
        line.useWorldSpace = true;
        line.startWidth = idleWidth;
        line.endWidth = idleWidth;

        if (line.material != null)
            lineMat = line.material;

        SetColor(idleColor);

        if (sender == null)
            sender = PCStreamReceiver.Instance;

        if (targetQuadRenderer == null && sender != null)
            targetQuadRenderer = sender.targetRenderer;
    }

    void Update()
    {
        OVRInput.Update();

        // InputTester에서 작동 확인된 그 방식 그대로: 오른손 검지 트리거 축값
        float triggerValue = OVRInput.Get(OVRInput.Axis1D.SecondaryIndexTrigger);
        bool pressed = triggerValue >= triggerThreshold;

        // 레이캐스트
        Vector3 direction = transform.forward;
        Vector3 origin = transform.position + direction * forwardOffset;
        Vector3 lineEnd = origin + direction * maxDistance;
        bool hitQuad = false;
        Vector2 uv = Vector2.zero;

        if (Physics.Raycast(origin, direction, out RaycastHit hit, maxDistance, hitLayers))
        {
            lineEnd = hit.point;
            if (targetQuadRenderer != null && hit.collider.GetComponent<Renderer>() == targetQuadRenderer)
            {
                hitQuad = true;
                uv = hit.textureCoord;
            }
        }

        line.SetPosition(0, origin);
        line.SetPosition(1, lineEnd);

        // 눌리는 순간만 잡기
        bool justPressed = pressed && !wasPressed;
        bool justReleased = !pressed && wasPressed;

        if (justPressed)
        {
            Debug.Log($"[RayClicker] 트리거 눌림! 값={triggerValue:F2} hitQuad={hitQuad}");
            SetColor(clickColor);
            SetWidth(clickWidth);
            OVRInput.SetControllerVibration(0.5f, 0.5f, OVRInput.Controller.RTouch);

            if (hitQuad)
            {
                float u = uv.x;
                float v = 1f - uv.y;
                string msg = string.Format("click,{0:F4},{1:F4}", u, v);
                if (sender != null) sender.SendText(msg);
                Debug.Log($"[RayClicker] Quad click UV=({u:F3},{v:F3})");
            }
        }
        else if (justReleased)
        {
            SetColor(idleColor);
            SetWidth(idleWidth);
            OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.RTouch);
        }

        wasPressed = pressed;
    }

    void SetColor(Color c)
    {
        if (line == null) return;
        line.startColor = c;
        line.endColor = c;
        if (lineMat != null)
        {
            if (lineMat.HasProperty("_Color")) lineMat.SetColor("_Color", c);
            if (lineMat.HasProperty("_BaseColor")) lineMat.SetColor("_BaseColor", c);
            if (lineMat.HasProperty("_TintColor")) lineMat.SetColor("_TintColor", c);
            if (lineMat.HasProperty("_EmissionColor")) lineMat.SetColor("_EmissionColor", c);
        }
    }

    void SetWidth(float w)
    {
        if (line == null) return;
        line.startWidth = w;
        line.endWidth = w;
    }
}