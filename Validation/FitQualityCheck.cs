using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MilltinaBreastGenerator;
using UnityEngine;

// Scores transfer accuracy on synthetic height-field bodies with garments hovering above them.
// The metric is the change in signed distance to the body: ideally every garment vertex keeps the gap it had at rest.
// Run with -executeMethod FitQualityCheck.Run. Logs one line per case and fails on regressions past the limits below.
public static class FitQualityCheck
{
    private const string Shape = "bump";

    private readonly struct Case
    {
        internal readonly string Name;
        internal readonly float Gap, Peak, Sigma;
        internal readonly float MaxP95, MaxPenetrations;
        internal readonly int BodyCount;

        internal Case(string name, float gap, float peak, float sigma, float maxP95, int maxPenetrations, int bodyCount = 49)
        {
            Name = name; BodyCount = bodyCount; Gap = gap; Peak = peak; Sigma = sigma; MaxP95 = maxP95; MaxPenetrations = maxPenetrations;
        }
    }

    public static void Run()
    {
        // Units are meters. Limits are the regression ceiling for the current solver.
        var cases = new[]
        {
            new Case("tight 2mm gap, 3cm bump", .002f, .03f, .05f, .0013f, 0),
            new Case("loose 15mm gap, 3cm bump", .015f, .03f, .05f, .0020f, 0),
            new Case("loose 15mm gap, 6mm bump", .015f, .006f, .05f, .0008f, 0),
            new Case("steep 3cm bump, 8mm gap", .008f, .03f, .02f, .0043f, 0),
            new Case("steep, fine body mesh", .008f, .03f, .02f, .0045f, 0, 121),
        };
        var report = new StringBuilder("SHAPEFORGE FIT QUALITY\n");
        var failures = new List<string>();
        foreach (var c in cases)
        {
            var (mean, p95, max, penetrations, covered) = Measure(c);
            report.AppendLine($"{c.Name}: error mean {mean * 1000:F2} p95 {p95 * 1000:F2} max {max * 1000:F2} mm, " +
                $"penetrations {penetrations}, followed {covered:P0}");
            if (p95 > c.MaxP95) failures.Add($"{c.Name}: p95 {p95 * 1000:F2} mm exceeds {c.MaxP95 * 1000:F2} mm");
            if (penetrations > c.MaxPenetrations) failures.Add($"{c.Name}: {penetrations} penetrations");
        }
        Debug.Log(report.ToString());
        if (failures.Count > 0) throw new Exception("Fit quality regressed:\n" + string.Join("\n", failures));
        Debug.Log("SHAPEFORGE FIT QUALITY PASSED");
    }

    private static (float Mean, float P95, float Max, int Penetrations, float Covered) Measure(Case c)
    {
        var body = Grid(.6f, c.BodyCount, 0, 0);
        var heights = body.vertices.Select(v => c.Peak * Mathf.Exp(-(v.x * v.x + v.y * v.y) / (2 * c.Sigma * c.Sigma))).ToArray();
        body.AddBlendShapeFrame(Shape, 100, heights.Select(h => Vector3.forward * h).ToArray(), null, null);
        // A different tessellation so garment vertices never coincide with body vertices.
        var cloth = Grid(.5f, 61, c.Gap, .0031f);
        Mesh result = null;
        try
        {
            try { result = ShapeTransfer.Generate(body, cloth, Matrix4x4.identity, out _, Shape); }
            catch (InvalidOperationException)
            {
                // Nothing matched: the garment never moves, so it keeps the full lift as its error.
                return (c.Peak, c.Peak, c.Peak, c.Peak > c.Gap ? 1 : 0, 0);
            }
            var motion = ShapeTransfer.ReadSourceDeltas(result, Shape);
            var points = body.vertices;
            var moved = points.Select((p, i) => p + Vector3.forward * heights[i]).ToArray();
            var surface = new ShapeTransfer.TriangleSurface(body, moved);
            var errors = new List<float>();
            int penetrations = 0, followed = 0, considered = 0;
            var vertices = cloth.vertices;
            for (int i = 0; i < vertices.Length; i++)
            {
                var v = vertices[i];
                // Only judge vertices where the body moved noticeably beneath them.
                float lift = c.Peak * Mathf.Exp(-(v.x * v.x + v.y * v.y) / (2 * c.Sigma * c.Sigma));
                if (lift < c.Peak * .1f) continue;
                considered++;
                var after = v + motion[i];
                if (!surface.FindClosest(after, .2f, out var face, out _, out var offset)) continue;
                float gap = Vector3.Dot(offset, surface.Normal(face));
                errors.Add(Mathf.Abs(gap - c.Gap));
                if (gap < 0) penetrations++;
                if (motion[i].z > lift * .5f) followed++;
            }
            errors.Sort();
            return (errors.Average(), errors[(int)(errors.Count * .95f)], errors[^1], penetrations,
                considered == 0 ? 0 : followed / (float)considered);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(body);
            UnityEngine.Object.DestroyImmediate(cloth);
            if (result) UnityEngine.Object.DestroyImmediate(result);
        }
    }

    // A square height field in the XY plane facing +Z, raised by gap, with optional deterministic jitter in XY.
    private static Mesh Grid(float size, int count, float gap, float jitter)
    {
        var vertices = new Vector3[count * count];
        var triangles = new List<int>();
        for (int y = 0; y < count; y++)
        for (int x = 0; x < count; x++)
        {
            float jx = jitter * Mathf.Sin(x * 12.9898f + y * 78.233f), jy = jitter * Mathf.Sin(x * 39.346f + y * 11.135f);
            vertices[y * count + x] = new Vector3((x / (count - 1f) - .5f) * size + jx, (y / (count - 1f) - .5f) * size + jy, gap);
        }
        for (int y = 0; y < count - 1; y++)
        for (int x = 0; x < count - 1; x++)
        {
            int a = y * count + x, b = a + 1, c = a + count, d = c + 1;
            triangles.AddRange(new[] { a, b, c, b, d, c }); // Counter-clockwise seen from +Z.
        }
        var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32, vertices = vertices };
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        return mesh;
    }
}
