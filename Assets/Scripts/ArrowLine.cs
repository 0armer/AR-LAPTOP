using System.Collections.Generic;
using UnityEngine;

// "------>" 모양 화살표를 그리기 위한 헬퍼.
//
// 몸통은 굵기가 일정한 LineRenderer로 그리고, 화살촉은 별도의 원뿔 메시로 얹는다.
// (LineRenderer의 width 커브만으로 화살표를 만들면 가운데가 볼록한 마름모처럼 보여서 이렇게 나눴다)
public static class ArrowLine
{
    private static Mesh coneMesh;
    public static Mesh ConeMesh => coneMesh != null ? coneMesh : (coneMesh = BuildCone(14));

    // 화살촉 오브젝트를 하나 만든다 (콜라이더 없음)
    public static Transform CreateHead(Material mat, string name = "ArrowHead")
    {
        var go = new GameObject(name);
        var mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = ConeMesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = mat;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;
        return go.transform;
    }

    // tip = 화살표가 끝나는 지점, dir = 진행 방향.
    // 원뿔 메시는 밑면이 z=0, 꼭짓점이 z=1이므로 밑면을 (tip - dir*length)에 두면 꼭짓점이 tip에 닿는다.
    public static void PlaceHead(Transform head, Vector3 tip, Vector3 dir, float radius, float length)
    {
        if (head == null) return;
        if (dir.sqrMagnitude < 1e-8f) dir = Vector3.forward;
        dir.Normalize();
        head.position = tip - dir * length;
        head.rotation = Quaternion.LookRotation(dir);
        head.localScale = new Vector3(radius * 2f, radius * 2f, length);
    }

    // 밑면 반지름 0.5, 길이 1 (+Z 방향)인 원뿔
    static Mesh BuildCone(int seg)
    {
        seg = Mathf.Max(6, seg);
        var m = new Mesh { name = "ArrowCone" };
        var verts = new List<Vector3>();
        var tris = new List<int>();

        verts.Add(new Vector3(0f, 0f, 1f));          // 0 = 꼭짓점
        int baseStart = verts.Count;
        for (int i = 0; i < seg; i++)
        {
            float a = (float)i / seg * Mathf.PI * 2f;
            verts.Add(new Vector3(Mathf.Cos(a) * 0.5f, Mathf.Sin(a) * 0.5f, 0f));
        }
        int center = verts.Count;
        verts.Add(Vector3.zero);                      // 밑면 중심

        for (int i = 0; i < seg; i++)
        {
            int a = baseStart + i;
            int b = baseStart + (i + 1) % seg;
            tris.Add(0); tris.Add(b); tris.Add(a);            // 옆면
            tris.Add(center); tris.Add(a); tris.Add(b);       // 밑면 뚜껑
        }

        m.SetVertices(verts);
        m.SetTriangles(tris, 0);
        m.RecalculateNormals();
        m.RecalculateBounds();
        return m;
    }
}
