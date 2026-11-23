using System;
using System.Linq;
using MilltinaBreastGenerator;
using UnityEngine;

public static class AccuracyChecks
{
    public static void Run()
    {
        var body = new GameObject("body").AddComponent<SkinnedMeshRenderer>();
        var cloth = new GameObject("cloth").AddComponent<SkinnedMeshRenderer>();
        var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
        mesh.RecalculateNormals();
        body.sharedMesh = mesh;
        cloth.sharedMesh = UnityEngine.Object.Instantiate(mesh);
        var middle = Enumerable.Repeat(Vector3.right * .1f, 3).ToArray();
        mesh.AddBlendShapeFrame("test", 50, middle, null, null);
        mesh.AddBlendShapeFrame("test", 100, new Vector3[3], null, null);
        Check(ShapeTransfer.DiscoverShapes(mesh).Any(s => s.Name == "test"), "round-trip shape discovery");
        Check(ShapeTransfer.ShapeOverlap(body, "test")(cloth), "round-trip overlap");
        foreach (TransferMethod method in Enum.GetValues(typeof(TransferMethod)))
        {
            var fit = new LayerFit(body, new[] { cloth }, "test", method);
            var result = fit.Build(cloth, out _);
            Check(result.GetBlendShapeFrameCount(0) == 2, "source knots");
            Check(mesh.vertices.SequenceEqual(result.vertices), "base positions");
            foreach (float weight in new[] { 0f, 25f, 50f, 75f, 100f })
            {
                var actual = ShapeTransfer.ReadSourceDeltas(result, "test", weight);
                float expected = (1 - Mathf.Abs(weight - 50) / 50) * .1f;
                Check(actual.All(v => Vector3.Distance(v, Vector3.right * expected) < 1e-5f), method + " " + weight);
            }
            var corrected = fit.Build(cloth, out _, middle);
            Check(ShapeTransfer.ReadSourceDeltas(corrected, "test", 50).SequenceEqual(ShapeTransfer.ReadSourceDeltas(result, "test", 50)), "draft preserves knot");
            UnityEngine.Object.DestroyImmediate(corrected);
            UnityEngine.Object.DestroyImmediate(result);
        }
        Debug.Log("SHAPEFORGE ACCURACY CHECKS PASSED");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
    }
}
