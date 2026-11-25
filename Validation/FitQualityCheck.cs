using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ShapeForge;
using UnityEngine;

// Scores transfer accuracy on synthetic bodies with garments hovering above them.
// Gap error is the change in signed distance to the body: ideally every garment vertex keeps the gap it had at rest.
// Drift compares each vertex with the ideal result, which carries it with its foot point on the smooth body and keeps
// its gap along the deformed normal, so sideways sliding counts too. Roughness is the drift of a vertex minus the mean
// drift of its neighbors. It catches fits that hit the gap by crumpling the fabric.
// Run with -executeMethod FitQualityCheck.Run. Logs one line per case and fails on regressions past the limits below.
public static class FitQualityCheck
{
    private const string Shape = "bump";

    private enum Body { Plane, Cylinder, Cleavage }

    private readonly struct Case
    {
        internal readonly string Name;
        internal readonly Body Kind;
        internal readonly float Gap, Peak, Sigma;
        internal readonly float MaxP95, MaxRoughness, MaxDrift;
        internal readonly int BodyCount;

        internal Case(string name, float gap, float peak, float sigma, float maxP95, float maxRoughness, float maxDrift,
            int bodyCount = 49, Body kind = Body.Plane)
        {
            Name = name; Kind = kind; BodyCount = bodyCount; Gap = gap; Peak = peak; Sigma = sigma;
            MaxP95 = maxP95; MaxRoughness = maxRoughness; MaxDrift = maxDrift;
        }
    }

    // Units are meters. Limits are the regression ceiling for the current solver, about 1.25 times its result: gap error
    // p95, roughness p95 and drift p95. Every case also requires no new penetrations. The solver smooths strongly so real
    // meshes stay smooth, which costs some accuracy on these ideal bodies.
    private static readonly Case[] Cases =
    {
        new Case("tight 2mm gap, 3cm bump", .002f, .03f, .05f, .0013f, .001f, .0016f),
        new Case("loose 15mm gap, 3cm bump", .015f, .03f, .05f, .002f, .00145f, .0025f),
        new Case("loose 15mm gap, 6mm bump", .015f, .006f, .05f, .0007f, .0003f, .0008f),
        new Case("steep 3cm bump, 8mm gap", .008f, .03f, .02f, .0057f, .004f, .0074f),
        new Case("steep, fine body mesh", .008f, .03f, .02f, .0053f, .004f, .0077f, 121),
        new Case("shrink 3cm dip, 8mm gap", .008f, -.03f, .03f, .0038f, .0026f, .0079f),
        new Case("torso 3cm bust, 5mm gap", .005f, .03f, .045f, .0053f, .0028f, .0059f, 0, Body.Cylinder),
        new Case("cleavage 4cm bust, 3mm gap", .003f, .04f, .03f, .0005f, .00053f, .0018f, 0, Body.Cleavage),
    };

    // Fabric, the default, is gated by its own ceiling, also about 1.25 times its result. It does not hold the rest gap
    // of loose cloth exactly: outfits whose creators made the shape by hand show loose fabric hanging and spanning hollows
    // instead, so its loose and steep cases sit above the classic Barycentric transfer on these ideal bodies.
    private static readonly Dictionary<string, (float P95, float Roughness, float Drift)> FabricLimits = new()
    {
        ["tight 2mm gap, 3cm bump"] = (.00045f, .00039f, .0009f),
        ["loose 15mm gap, 3cm bump"] = (.0064f, .0014f, .0074f),
        ["loose 15mm gap, 6mm bump"] = (.0012f, .00029f, .0016f),
        ["steep 3cm bump, 8mm gap"] = (.0085f, .0036f, .0103f),
        ["steep, fine body mesh"] = (.0095f, .0035f, .0097f),
        ["shrink 3cm dip, 8mm gap"] = (.0039f, .0019f, .0079f),
        ["torso 3cm bust, 5mm gap"] = (.0013f, .00026f, .0024f),
        ["cleavage 4cm bust, 3mm gap"] = (.00038f, .00028f, .00083f),
    };

    private static TransferMethod method = TransferMethod.Fabric;

    public static void Run()
    {
        var report = new StringBuilder("SHAPEFORGE FIT QUALITY\n");
        var failures = new List<string>();
        foreach (var each in new[] { TransferMethod.Barycentric, TransferMethod.Fabric })
        {
        method = each;
        report.AppendLine($"[{method}]");
        foreach (var c in Cases)
        {
            var (mean, p95, max, penetrations, covered, roughness, drift) = Measure(c);
            var limit = method == TransferMethod.Fabric ? FabricLimits[c.Name] : (c.MaxP95, c.MaxRoughness, c.MaxDrift);
            report.AppendLine($"{c.Name}: gap error mean {mean * 1000:F2} p95 {p95 * 1000:F2} max {max * 1000:F2} mm, " +
                $"drift p95 {drift * 1000:F2} mm, roughness p95 {roughness * 1000:F3} mm, penetrations {penetrations}, followed {covered:P0}");
            if (p95 > limit.Item1) failures.Add($"{method} {c.Name}: p95 {p95 * 1000:F2} mm exceeds {limit.Item1 * 1000:F2} mm");
            if (drift > limit.Item3) failures.Add($"{method} {c.Name}: drift {drift * 1000:F2} mm exceeds {limit.Item3 * 1000:F2} mm");
            if (roughness > limit.Item2) failures.Add($"{method} {c.Name}: roughness {roughness * 1000:F3} mm exceeds {limit.Item2 * 1000:F3} mm");
            if (penetrations > 0) failures.Add($"{method} {c.Name}: {penetrations} penetrations");
        }
        report.AppendLine(HarnessCrossCheck());
        float stray = WrongSide();
        report.AppendLine($"wrong-side: garment on a stationary front sheet moved {stray * 1000:F2} mm because of a moving back sheet");
        if (stray > .002f) failures.Add($"{method} wrong-side: garment followed the moving surface behind it ({stray * 1000:F2} mm)");
        float bridge = Bridge();
        report.AppendLine($"bridge: a strap over a valley lifts {bridge:P0} of its supporting crowns");
        if (bridge < .6f) failures.Add($"{method} bridge: strap sagged into the valley ({bridge:P0} of the crown lift)");
        var (strain, tilt) = Button();
        report.AppendLine($"button: largest strain {strain:P2}, tilt error {tilt:F1} degrees on a 40 degree flank");
        if (strain > .01f) failures.Add($"{method} button: a compact closed part deformed by {strain:P1}");
        if (tilt > 25) failures.Add($"{method} button: a compact closed part tilted {tilt:F1} degrees away from the body");
        }
        report.AppendLine(Benchmark());
        Debug.Log(report.ToString());
        if (failures.Count > 0) throw new Exception("Fit quality regressed:\n" + string.Join("\n", failures));
        Debug.Log("SHAPEFORGE FIT QUALITY PASSED");
    }

    // Batch entry: the Table for each method, logged together.
    public static void TableAll()
    {
        var report = new StringBuilder("SHAPEFORGE FIT QUALITY TABLE\n");
        method = TransferMethod.Barycentric;
        report.AppendLine("[Barycentric]").Append(Table());
        // -benchSweep "Name=1,Other=2;..." runs Fabric once per set. Sets apply cumulatively.
        var args = Environment.GetCommandLineArgs();
        int at = Array.IndexOf(args, "-benchSweep");
        method = TransferMethod.Fabric;
        foreach (var set in (at >= 0 && at + 1 < args.Length ? args[at + 1] : "").Split(';'))
        {
            ChestBench.ApplyParams(set);
            report.AppendLine($"[Fabric {set}]").Append(Table());
        }
        Debug.Log(report.ToString());
    }

    // One line per case, without limits or the benchmark. Used to compare solver variants quickly.
    public static string Table()
    {
        var report = new StringBuilder();
        float meanP95 = 0, meanRough = 0, meanDrift = 0;
        foreach (var c in Cases)
        {
            var (mean, p95, max, penetrations, covered, roughness, drift) = Measure(c);
            meanP95 += p95 / Cases.Length;
            meanRough += roughness / Cases.Length;
            meanDrift += drift / Cases.Length;
            report.AppendLine($"  {c.Name,-28} mean {mean * 1000,5:F2} p95 {p95 * 1000,5:F2} max {max * 1000,5:F2} rough {roughness * 1000,6:F3} drift {drift * 1000,5:F2} pen {penetrations} follow {covered:P0}");
        }
        var (strain, tilt) = Button();
        report.AppendLine($"  average p95 {meanP95 * 1000:F3} mm, roughness {meanRough * 1000:F3} mm, drift {meanDrift * 1000:F3} mm, wrong-side {WrongSide() * 1000:F2} mm, bridge {Bridge():F2}, button strain {strain:P1} tilt {tilt:F1} deg");
        return report.ToString();
    }

    // A straight strap resting on two raised crowns and bridging the valley between them. The shape raises both crowns
    // by 2 cm and the valley by much less. The strap should lift as a whole, so the result is valley motion divided
    // by the 2 cm crown lift: 1 is ideal, and a strap that sags into the valley scores lower.
    private static float Bridge()
    {
        float Rest(float x, float y) => .04f * (Gauss(x - .06f, y, .03f) + Gauss(x + .06f, y, .03f));
        float Gauss(float x, float y, float sigma) => Mathf.Exp(-(x * x + y * y) / (2 * sigma * sigma));
        var body = Grid(.4f, 81, 0, 0);
        body.vertices = body.vertices.Select(v => new Vector3(v.x, v.y, Rest(v.x, v.y))).ToArray();
        body.RecalculateNormals();
        body.AddBlendShapeFrame(Shape, 100, body.vertices.Select(v => Vector3.forward * (v.z * .5f)).ToArray(), null, null);
        const int segments = 96;
        var strap = new Vector3[(segments + 1) * 2];
        var triangles = new List<int>();
        for (int x = 0; x <= segments; x++)
        {
            float px = (x / (float)segments - .5f) * .24f;
            strap[x * 2] = new Vector3(px, -.005f, .043f);
            strap[x * 2 + 1] = new Vector3(px, .005f, .043f);
            if (x < segments) triangles.AddRange(new[] { x * 2, x * 2 + 2, x * 2 + 1, x * 2 + 1, x * 2 + 2, x * 2 + 3 });
        }
        var cloth = new Mesh { vertices = strap };
        cloth.SetTriangles(triangles, 0);
        cloth.RecalculateNormals();
        Mesh result = null;
        try
        {
            result = ShapeTransfer.Generate(body, cloth, Matrix4x4.identity, out _, Shape, null, method);
            var motion = ShapeTransfer.ReadSourceDeltas(result, Shape);
            int crown = Mathf.RoundToInt((.06f / .24f + .5f) * segments) * 2, valley = segments;
            return motion[valley].z / .02f;
        }
        finally
        {
            foreach (var mesh in new[] { body, cloth, result })
                if (mesh) UnityEngine.Object.DestroyImmediate(mesh);
        }
    }

    // An 8 mm closed cube, like a button, sits 2 mm above the flank of a steep bump where the body tilts by about
    // 40 degrees. It should stay rigid and tilt with the body. Returns its largest edge strain and its tilt error.
    private static (float Strain, float TiltError) Button()
    {
        const float peak = .03f, sigma = .02f, size = .008f, gap = .002f, x0 = .025f;
        var body = Grid(.6f, 121, 0, 0);
        float Height(Vector3 p) => peak * Mathf.Exp(-(p.x * p.x + p.y * p.y) / (2 * sigma * sigma));
        body.AddBlendShapeFrame(Shape, 100, body.vertices.Select(v => Vector3.forward * Height(v)).ToArray(), null, null);
        var corners = new Vector3[8];
        for (int i = 0; i < 8; i++)
            corners[i] = new Vector3(x0 + ((i & 1) - .5f) * size, ((i >> 1 & 1) - .5f) * size, gap + (i >> 2 & 1) * size);
        // Twelve outward triangles of the cube, then a far-away loose triangle so the cube is not the whole mesh.
        var faces = new[] { 0,2,1, 1,2,3, 4,5,6, 5,7,6, 0,1,4, 1,5,4, 2,6,3, 3,6,7, 0,4,2, 2,4,6, 1,3,5, 3,7,5, 8,9,10 };
        var cloth = new Mesh { vertices = corners.Concat(new[] { new Vector3(.25f, .25f, .01f), new Vector3(.26f, .25f, .01f), new Vector3(.25f, .26f, .01f) }).ToArray() };
        cloth.SetTriangles(faces, 0);
        cloth.RecalculateNormals();
        Mesh result = null;
        try
        {
            result = ShapeTransfer.Generate(body, cloth, Matrix4x4.identity, out _, Shape, null, method);
            var motion = ShapeTransfer.ReadSourceDeltas(result, Shape);
            float strain = 0;
            for (int a = 0; a < 8; a++)
                for (int b = a + 1; b < 8; b++)
                {
                    float before = (corners[b] - corners[a]).magnitude;
                    float after = (corners[b] + motion[b] - corners[a] - motion[a]).magnitude;
                    strain = Mathf.Max(strain, Mathf.Abs(after / before - 1));
                }
            var up = (corners[4] + motion[4] - corners[0] - motion[0]).normalized;
            const float e = 1e-4f;
            var foot = new Vector3(x0, 0, 0);
            var normal = new Vector3(-(Height(foot + Vector3.right * e) - Height(foot - Vector3.right * e)) / (2 * e),
                -(Height(foot + Vector3.up * e) - Height(foot - Vector3.up * e)) / (2 * e), 1).normalized;
            return (strain, Vector3.Angle(up, normal));
        }
        finally
        {
            foreach (var mesh in new[] { body, cloth, result })
                if (mesh) UnityEngine.Object.DestroyImmediate(mesh);
        }
    }

    // Runs the real-avatar harness on a synthetic bump so its numbers can be compared with the case table.
    private static string HarnessCrossCheck()
    {
        var body = Grid(.6f, 49, 0, 0);
        var lift = body.vertices.Select(v => Vector3.forward * (.03f * Mathf.Exp(-(v.x * v.x + v.y * v.y) / (2 * .05f * .05f)))).ToArray();
        body.AddBlendShapeFrame(Shape, 100, lift, null, null);
        var cloth = Grid(.5f, 61, .015f, .0031f);
        var bodyRenderer = new GameObject("harness body").AddComponent<SkinnedMeshRenderer>();
        var clothRenderer = new GameObject("harness cloth").AddComponent<SkinnedMeshRenderer>();
        bodyRenderer.sharedMesh = body;
        clothRenderer.sharedMesh = cloth;
        try { return FitMetrics.Measure(bodyRenderer, new[] { clothRenderer }, Shape, method); }
        finally
        {
            UnityEngine.Object.DestroyImmediate(bodyRenderer.gameObject);
            UnityEngine.Object.DestroyImmediate(clothRenderer.gameObject);
            UnityEngine.Object.DestroyImmediate(body);
            UnityEngine.Object.DestroyImmediate(cloth);
        }
    }

    // A closed-looking body: a stationary front sheet facing +Z and a back sheet 1 cm behind it that faces -Z and
    // slides sideways. A garment sunk 6 mm into the front is nearer the back sheet but belongs to the front.
    private static float WrongSide()
    {
        var front = Grid(.6f, 49, 0, 0);
        var back = Grid(.6f, 49, -.01f, 0);
        var backTriangles = back.triangles;
        for (int i = 0; i < backTriangles.Length; i += 3) (backTriangles[i + 1], backTriangles[i + 2]) = (backTriangles[i + 2], backTriangles[i + 1]);
        int count = front.vertexCount;
        var body = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        body.vertices = front.vertices.Concat(back.vertices).ToArray();
        body.triangles = front.triangles.Concat(backTriangles.Select(t => t + count)).ToArray();
        var slide = new Vector3[body.vertexCount];
        for (int i = count; i < slide.Length; i++) slide[i] = Vector3.right * .03f;
        body.AddBlendShapeFrame(Shape, 100, slide, null, null);
        var cloth = Grid(.4f, 41, -.006f, .0021f);
        Mesh result = null;
        try
        {
            try { result = ShapeTransfer.Generate(body, cloth, Matrix4x4.identity, out _, Shape, null, method); }
            catch (InvalidOperationException exception) when (exception.Message.Contains("No outfit vertices matched"))
            {
                // Everything attached to the stationary front sheet, so there is nothing to transfer.
                return 0;
            }
            var motion = ShapeTransfer.ReadSourceDeltas(result, Shape);
            return motion.Max(m => m.magnitude);
        }
        finally
        {
            foreach (var mesh in new[] { front, back, body, cloth, result })
                if (mesh) UnityEngine.Object.DestroyImmediate(mesh);
        }
    }

    // Times dense, two-frame generation. Reported only, because timings depend on the machine.
    private static string Benchmark()
    {
        var body = Grid(.6f, 201, 0, 0);
        var shaped = body.vertices.Select(v => Vector3.forward * (.03f * Mathf.Exp(-(v.x * v.x + v.y * v.y) / (2 * .05f * .05f)))).ToArray();
        body.AddBlendShapeFrame(Shape, 50, shaped.Select(v => v * .4f).ToArray(), null, null);
        body.AddBlendShapeFrame(Shape, 100, shaped, null, null);
        var inner = Grid(.5f, 250, .004f, .0013f);
        var outer = Grid(.5f, 250, .012f, .0011f);
        var bodyRenderer = new GameObject("bench body").AddComponent<SkinnedMeshRenderer>();
        var innerRenderer = new GameObject("bench inner").AddComponent<SkinnedMeshRenderer>();
        var outerRenderer = new GameObject("bench outer").AddComponent<SkinnedMeshRenderer>();
        bodyRenderer.sharedMesh = body;
        innerRenderer.sharedMesh = inner;
        outerRenderer.sharedMesh = outer;
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var single = ShapeTransfer.Generate(body, inner, Matrix4x4.identity, out _, Shape, null, method);
            long generate = watch.ElapsedMilliseconds;
            UnityEngine.Object.DestroyImmediate(single);
            watch.Restart();
            var fit = new LayerFit(bodyRenderer, new[] { innerRenderer, outerRenderer }, Shape, method);
            long layered = watch.ElapsedMilliseconds;
            return $"benchmark ({inner.vertexCount} verts x2, 2 frames): Generate {generate} ms, LayerFit {layered} ms";
        }
        finally
        {
            foreach (var go in new[] { bodyRenderer.gameObject, innerRenderer.gameObject, outerRenderer.gameObject })
                UnityEngine.Object.DestroyImmediate(go);
            foreach (var mesh in new[] { body, inner, outer })
                UnityEngine.Object.DestroyImmediate(mesh);
        }
    }

    private static (float Mean, float P95, float Max, int Penetrations, float Covered, float Roughness, float Drift) Measure(Case c)
    {
        Mesh body, cloth;
        Func<Vector3, Vector3> lift;
        // The foot point on the smooth rest body below each garment vertex, and the smooth rest normal there.
        Vector3[] feet, restNormals;
        if (c.Kind == Body.Cylinder)
        {
            // A torso-like closed tube along Y facing outward, with a bust raised on its +Z side.
            const float radius = .12f;
            var center = new Vector3(0, 0, radius);
            lift = p =>
            {
                var radial = new Vector3(p.x, 0, p.z).normalized;
                return radial * (c.Peak * Mathf.Exp(-(radial * radius + Vector3.up * p.y - center).sqrMagnitude / (2 * c.Sigma * c.Sigma)));
            };
            body = Tube(radius, .5f, 64, 41, 0);
            // A different tessellation, rotated half a step, so garment vertices never coincide with body vertices.
            cloth = Tube(radius + c.Gap, .4f, 90, 57, .5f);
            restNormals = cloth.vertices.Select(p => new Vector3(p.x, 0, p.z).normalized).ToArray();
            feet = cloth.vertices.Select((p, i) => restNormals[i] * radius + Vector3.up * p.y).ToArray();
        }
        else if (c.Kind == Body.Cleavage)
        {
            // Two rest breasts with a valley between them, grown by the shape. The garment is an offset surface that
            // follows the valley, where the nearest body point switches sides.
            float Height(float x, float y) => c.Peak * (Gauss(x - c.Sigma * 2, y) + Gauss(x + c.Sigma * 2, y));
            float Gauss(float x, float y) => Mathf.Exp(-(x * x + y * y) / (2 * c.Sigma * c.Sigma));
            Vector3 Normal(float x, float y)
            {
                const float e = 1e-4f;
                return new Vector3(-(Height(x + e, y) - Height(x - e, y)) / (2 * e), -(Height(x, y + e) - Height(x, y - e)) / (2 * e), 1).normalized;
            }
            lift = p => Vector3.forward * (Height(p.x, p.y) * .5f);
            body = Grid(.4f, 81, 0, .0012f); // Irregular, like a sculpted body.
            body.vertices = body.vertices.Select(v => new Vector3(v.x, v.y, Height(v.x, v.y))).ToArray();
            body.RecalculateNormals();
            cloth = Grid(.3f, 71, 0, .0011f);
            var grid = cloth.vertices;
            restNormals = grid.Select(q => Normal(q.x, q.y)).ToArray();
            feet = grid.Select(q => new Vector3(q.x, q.y, Height(q.x, q.y))).ToArray();
            cloth.vertices = feet.Select((p, i) => p + restNormals[i] * c.Gap).ToArray();
            cloth.RecalculateNormals();
        }
        else
        {
            lift = p => Vector3.forward * (c.Peak * Mathf.Exp(-(p.x * p.x + p.y * p.y) / (2 * c.Sigma * c.Sigma)));
            body = Grid(.6f, c.BodyCount, 0, 0);
            // A different tessellation so garment vertices never coincide with body vertices.
            cloth = Grid(.5f, 61, c.Gap, .0031f);
            restNormals = cloth.vertices.Select(p => Vector3.forward).ToArray();
            feet = cloth.vertices.Select(p => new Vector3(p.x, p.y, 0)).ToArray();
        }
        // The ideal result carries each garment vertex with its foot point on the smooth body and keeps its gap along
        // the deformed smooth normal. Comparing against it measures sideways drift as well as the gap.
        Vector3 IdealMotion(Vector3 v, int index)
        {
            var b = feet[index];
            var n = restNormals[index];
            var t1 = Vector3.Cross(n, Mathf.Abs(n.y) < .9f ? Vector3.up : Vector3.right).normalized;
            var t2 = Vector3.Cross(n, t1);
            const float e = 1e-4f;
            Vector3 Deformed(Vector3 p) => p + lift(p);
            // Scale the tangents up before the cross product: Vector3.normalized returns zero for tiny vectors.
            var normal = Vector3.Cross((Deformed(b + t1 * e) - Deformed(b - t1 * e)) / e, (Deformed(b + t2 * e) - Deformed(b - t2 * e)) / e).normalized;
            if (Vector3.Dot(normal, n) < 0) normal = -normal;
            return Deformed(b) + normal * c.Gap - v;
        }
        var points = body.vertices;
        var bodyMotion = points.Select(lift).ToArray();
        body.AddBlendShapeFrame(Shape, 100, bodyMotion, null, null);
        Mesh result = null;
        try
        {
            try { result = ShapeTransfer.Generate(body, cloth, Matrix4x4.identity, out _, Shape, null, method); }
            catch (InvalidOperationException)
            {
                // Nothing matched: the garment never moves, so it keeps the full lift as its error.
                float peak = Mathf.Abs(c.Peak);
                return (peak, peak, peak, c.Peak > c.Gap ? 1 : 0, 0, 0, peak);
            }
            var motion = ShapeTransfer.ReadSourceDeltas(result, Shape);
            var moved = points.Select((p, i) => p + bodyMotion[i]).ToArray();
            var surface = new ShapeTransfer.TriangleSurface(body, moved);
            var errors = new List<float>();
            var rough = new List<float>();
            var drift = new List<float>();
            var neighbors = Neighbors(cloth);
            int penetrations = 0, followed = 0, considered = 0;
            var vertices = cloth.vertices;
            var ideal = vertices.Select(IdealMotion).ToArray();
            for (int i = 0; i < vertices.Length; i++)
            {
                var v = vertices[i];
                // Only judge vertices where the body moved noticeably beneath them.
                var expected = lift(v);
                if (expected.magnitude < Mathf.Abs(c.Peak) * .1f) continue;
                considered++;
                var after = v + motion[i];
                if (!surface.FindClosest(after, .2f, out var face, out _, out var offset)) continue;
                float gap = Vector3.Dot(offset, surface.Normal(face));
                errors.Add(Mathf.Abs(gap - c.Gap));
                if (gap < 0) penetrations++;
                if (Vector3.Dot(motion[i], expected) > expected.sqrMagnitude * .5f) followed++;
                drift.Add((motion[i] - ideal[i]).magnitude);
                // Roughness of the error field, so real curvature of the ideal motion does not count.
                if (neighbors[i].Count > 0)
                {
                    var mean = Vector3.zero;
                    foreach (int j in neighbors[i]) mean += motion[j] - ideal[j];
                    rough.Add((motion[i] - ideal[i] - mean / neighbors[i].Count).magnitude);
                }
            }
            errors.Sort();
            rough.Sort();
            drift.Sort();
            float P95(List<float> values) => values.Count == 0 ? 0 : values[(int)(values.Count * .95f)];
            return (errors.Average(), P95(errors), errors[^1], penetrations,
                considered == 0 ? 0 : followed / (float)considered, P95(rough), P95(drift));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(body);
            UnityEngine.Object.DestroyImmediate(cloth);
            if (result) UnityEngine.Object.DestroyImmediate(result);
        }
    }

    private static List<int>[] Neighbors(Mesh mesh)
    {
        var result = new List<int>[mesh.vertexCount];
        for (int i = 0; i < result.Length; i++) result[i] = new List<int>();
        var t = mesh.triangles;
        for (int k = 0; k < t.Length; k += 3)
            for (int e = 0; e < 3; e++)
            {
                int a = t[k + e], b = t[k + (e + 1) % 3];
                if (!result[a].Contains(b)) result[a].Add(b);
                if (!result[b].Contains(a)) result[b].Add(a);
            }
        return result;
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

    // An open tube around Y with outward-facing triangles. Its seam column is duplicated, like a UV seam.
    private static Mesh Tube(float radius, float height, int around, int rows, float phase)
    {
        var vertices = new Vector3[(around + 1) * rows];
        var triangles = new List<int>();
        for (int y = 0; y < rows; y++)
        for (int x = 0; x <= around; x++)
        {
            float angle = (x % around + phase) / around * Mathf.PI * 2;
            vertices[y * (around + 1) + x] = new Vector3(Mathf.Sin(angle) * radius, (y / (rows - 1f) - .5f) * height, Mathf.Cos(angle) * radius);
        }
        for (int y = 0; y < rows - 1; y++)
        for (int x = 0; x < around; x++)
        {
            int a = y * (around + 1) + x, b = a + 1, c = a + around + 1, d = c + 1;
            triangles.AddRange(new[] { a, b, c, b, d, c }); // Outward facing.
        }
        var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32, vertices = vertices };
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        return mesh;
    }
}
