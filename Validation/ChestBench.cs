using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ShapeForge;
using UnityEditor;
using UnityEngine;

// Scores generated shapes against shapes that outfit creators authored by hand for the same avatar.
// For every outfit piece that already has the shape, the authored frame is set aside, the piece is refitted from the
// body, and the result is compared vertex by vertex. Nothing is saved: meshes are copied in memory.
//
// Development only. Put the avatar FBX under Assets/Bench/Body and outfit FBXs made for it under Assets/Bench/Outfits.
// Batch: -executeMethod ChestBench.Run [-benchShape "No bra"] [-benchFilter text] [-benchCsv path] [-benchSolver name]
//
// Per piece:
//   accuracy  1 - mean error / mean authored motion over the region either one moves. 100% matches the creator,
//             0% is no better than leaving the cloth still.
//   error     |generated - authored| in mm: mean, p95, max.
//   pen       vertices outside the rest body that end up more than 0.5 mm inside the moved body, at 50 and 100,
//             for the generated shape and for the creator's own shape.
//   rough     p95 of a vertex's motion minus its neighbors' mean motion, in mm. Measures crumpling.
public static class ChestBench
{
    private const string BodyFolder = "Assets/Bench/Body";
    private const string OutfitFolder = "Assets/Bench/Outfits";
    private const int Panels = 6;

    // 0 blue, .33 green, .66 yellow, 1 red.
    private static Color Heat(float t)
    {
        t = Mathf.Clamp01(t);
        if (t < 1f / 3) return Color.Lerp(new Color(.15f, .25f, .9f), new Color(.15f, .85f, .35f), t * 3);
        if (t < 2f / 3) return Color.Lerp(new Color(.15f, .85f, .35f), new Color(1f, .9f, .2f), t * 3 - 1);
        return Color.Lerp(new Color(1f, .9f, .2f), new Color(1f, .15f, .1f), t * 3 - 2);
    }

    private sealed class Row
    {
        internal string Outfit, Piece;
        internal int Vertices, Region;
        internal float Accuracy, Mean, P95, Max, Rough, RoughAuthored, AuthoredPeak;
        // Gain is generated over authored motion. Cosine is the motion-weighted direction agreement.
        // RestInside is the share of the region that already sits more than 2 mm inside the neutral body, which marks
        // a variant authored for another body state.
        internal float Gain, Cosine, RestInside, ErrorSum, AuthoredSum;
        // Mean world-axis motion over the region (x side, y up, z forward) and mean absolute error per axis, in mm.
        internal Vector3 AuthoredAxes, GeneratedAxes, ErrorAxes;
        internal int Pen50, Pen100, AuthoredPen50, AuthoredPen100;
        internal long Ms;
        internal int Key;
        internal readonly double[] BinAuthored = new double[Bins.Length + 1], BinGenerated = new double[Bins.Length + 1],
            BinError = new double[Bins.Length + 1];
        internal readonly int[] BinCount = new int[Bins.Length + 1];
    }

    // Rest distance from the body in mm. The last bin is cloth farther than every bound or beyond reach.
    private static readonly float[] Bins = { 0, 2, 5, 10, 20, 30 };
    private static string BinName(int b) => b == 0 ? "inside" : b < Bins.Length ? $"{Bins[b - 1]}-{Bins[b]}mm" : $">{Bins[^1]}mm";

    public static void Run()
    {
        string shape = Argument("-benchShape") ?? "No bra";
        string filter = Argument("-benchFilter");
        string csv = Argument("-benchCsv");
        string solver = Argument("-benchSolver");
        if (!string.IsNullOrEmpty(solver)) SelectSolver(solver);

        var report = new StringBuilder($"SHAPEFORGE CHEST BENCH shape \"{shape}\" solver {SolverName()}\n");
        var bodyPath = AssetDatabase.FindAssets("t:GameObject", new[] { BodyFolder }).Select(AssetDatabase.GUIDToAssetPath).First();
        var bodyRoot = (GameObject)UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(bodyPath));
        var body = bodyRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .Where(r => r.sharedMesh && r.sharedMesh.GetBlendShapeIndex(shape) >= 0)
            .OrderByDescending(r => r.sharedMesh.vertexCount).FirstOrDefault();
        if (!body)
        {
            var names = bodyRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh)
                .Select(r => r.name + ": " + string.Join(" | ", Enumerable.Range(0, r.sharedMesh.blendShapeCount).Select(r.sharedMesh.GetBlendShapeName)));
            throw new Exception($"No body renderer has {shape}. Renderers:\n" + string.Join("\n", names));
        }
        var probe = new Probe(body, shape);

        // -benchParams "Name=1,Other=2" sets SolverTuning fields. -benchSweep runs several such sets separated by ';'.
        var sets = (Argument("-benchSweep") ?? Argument("-benchParams") ?? "").Split(';');
        bool verbose = sets.Length == 1;
        foreach (var set in sets)
        {
        ApplyParams(set);
        report.AppendLine($"PARAMS {DescribeParams()}");
        var rows = new List<Row>();
        foreach (var path in AssetDatabase.FindAssets("t:GameObject", new[] { OutfitFolder }).Select(AssetDatabase.GUIDToAssetPath).OrderBy(p => p))
        {
            string outfit = Path.GetFileNameWithoutExtension(path);
            if (!string.IsNullOrEmpty(filter) && outfit.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var root = (GameObject)UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(path));
            try
            {
                var pieces = new List<(SkinnedMeshRenderer Renderer, Vector3[] Authored)>();
                foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    var mesh = renderer.sharedMesh;
                    if (!mesh || mesh.GetBlendShapeIndex(shape) < 0) continue;
                    // Some outfits ship a copy of the body. It is not cloth.
                    if (mesh.vertexCount == body.sharedMesh.vertexCount || GeneratorAssets.IsBodyName(renderer.name)) continue;
                    var authored = ShapeTransfer.ReadSourceDeltas(mesh, shape);
                    if (authored.Max(d => d.magnitude) < 1e-5f) continue;
                    var copy = UnityEngine.Object.Instantiate(mesh);
                    copy.ClearBlendShapes();
                    renderer.sharedMesh = copy;
                    pieces.Add((renderer, authored));
                }
                if (pieces.Count == 0) continue;

                var watch = System.Diagnostics.Stopwatch.StartNew();
                var fit = new LayerFit(body, pieces.Select(p => p.Renderer).ToArray(), shape, Method);
                long ms = watch.ElapsedMilliseconds;
                foreach (var (renderer, authored) in pieces)
                {
                    Vector3[] generated;
                    try { generated = fit.GeneratedMotion(renderer, 100); }
                    catch (Exception error)
                    {
                        report.AppendLine($"  {outfit} / {renderer.name}: failed ({error.Message})");
                        continue;
                    }
                    var row = probe.Score(renderer, authored, generated);
                    row.Outfit = outfit;
                    row.Piece = renderer.name;
                    row.Ms = ms;
                    rows.Add(row);
                    if (verbose) report.AppendLine($"  {outfit} / {row.Piece}: accuracy {row.Accuracy:P1}, gain {row.Gain:F2}, cos {row.Cosine:F2}, rest inside {row.RestInside:P0}, error mean {row.Mean:F2} p95 {row.P95:F2} max {row.Max:F2} mm, " +
                        $"pen 50/100 {row.Pen50}/{row.Pen100} (creator {row.AuthoredPen50}/{row.AuthoredPen100}), rough {row.Rough:F3} (creator {row.RoughAuthored:F3}) mm, " +
                        $"region {row.Region}/{row.Vertices}, creator peak {row.AuthoredPeak:F1} mm, {ms} ms");
                    if (verbose && !string.IsNullOrEmpty(filter))
                        report.AppendLine($"      axes (side, up, forward) creator {row.AuthoredAxes:F1} ours {row.GeneratedAxes:F1} mean |error| {row.ErrorAxes:F1} mm");
                    if (verbose && !string.IsNullOrEmpty(filter))
                        for (int b = 0; b <= Bins.Length; b++)
                            if (row.BinCount[b] > 0)
                                report.AppendLine($"      gap {BinName(b),-8} vertices {row.BinCount[b],6}, authored {row.BinAuthored[b] / row.BinCount[b]:F2} mm, " +
                                    $"generated {row.BinGenerated[b] / row.BinCount[b]:F2} mm, error {row.BinError[b] / row.BinCount[b]:F2} mm");
                }
            }
            catch (Exception error)
            {
                report.AppendLine($"  {outfit}: failed ({error.GetType().Name}: {error.Message})");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        // Identical variant meshes count once. Variants authored for another body state cannot be compared.
        var all = rows;
        rows = rows.GroupBy(r => r.Key).Select(g => g.First()).Where(r => r.RestInside <= .2f).ToList();
        report.AppendLine($"SCORED {rows.Count} of {all.Count} pieces (duplicates and pieces authored for another body state left out)");
        if (rows.Count > 0)
        {
            var accuracy = rows.Select(r => r.Accuracy).OrderBy(a => a).ToList();
            report.AppendLine($"POOLED accuracy {1 - rows.Sum(r => r.ErrorSum) / rows.Sum(r => r.AuthoredSum):P1}, gain {rows.Sum(r => r.Gain * r.AuthoredSum) / rows.Sum(r => r.AuthoredSum):F2}, " +
                $"cos {rows.Sum(r => r.Cosine * r.AuthoredSum) / rows.Sum(r => r.AuthoredSum):F2}");
            for (int b = 0; b <= Bins.Length; b++)
            {
                double a = rows.Sum(r => r.BinAuthored[b]), g = rows.Sum(r => r.BinGenerated[b]), e = rows.Sum(r => r.BinError[b]);
                int n = rows.Sum(r => r.BinCount[b]);
                if (n > 0)
                    report.AppendLine($"  gap {BinName(b),-8} vertices {n,7}, authored {a / n:F2} mm, generated {g / n:F2} mm, error {e / n:F2} mm, accuracy {1 - e / Math.Max(a, 1e-9):P0}");
            }
            if (!verbose)
                report.AppendLine("PIECES " + string.Join(" ", rows.Select(r => $"{r.Piece.Replace(' ', '_')}:{r.Accuracy * 100:F0}")));
            report.AppendLine($"SUMMARY pieces {rows.Count}, accuracy median {accuracy[accuracy.Count / 2]:P1} mean {accuracy.Average():P1} worst {accuracy[0]:P1}, " +
                $"error mean {rows.Average(r => r.Mean):F2} mm, p95 mean {rows.Average(r => r.P95):F2} mm, " +
                $"pen 50/100 {rows.Sum(r => r.Pen50)}/{rows.Sum(r => r.Pen100)} (creator {rows.Sum(r => r.AuthoredPen50)}/{rows.Sum(r => r.AuthoredPen100)}), " +
                $"rough mean {rows.Average(r => r.Rough):F3} (creator {rows.Average(r => r.RoughAuthored):F3}) mm, time {rows.GroupBy(r => r.Outfit).Sum(g => g.First().Ms)} ms");
        }
        if (verbose && !string.IsNullOrEmpty(csv))
        {
            var lines = new List<string> { "outfit,piece,accuracy,mean_mm,p95_mm,max_mm,pen50,pen100,creator_pen50,creator_pen100,rough_mm,creator_rough_mm,region,vertices,creator_peak_mm,ms" };
            lines.AddRange(rows.Select(r => string.Join(",", Quote(r.Outfit), Quote(r.Piece), r.Accuracy.ToString("F4"), r.Mean.ToString("F3"), r.P95.ToString("F3"),
                r.Max.ToString("F3"), r.Pen50, r.Pen100, r.AuthoredPen50, r.AuthoredPen100, r.Rough.ToString("F4"), r.RoughAuthored.ToString("F4"), r.Region, r.Vertices,
                r.AuthoredPeak.ToString("F2"), r.Ms)));
            File.WriteAllLines(csv, lines);
        }
        }
        report.AppendLine("SHAPEFORGE CHEST BENCH DONE");
        Debug.Log(report.ToString());
        UnityEngine.Object.DestroyImmediate(bodyRoot);
    }

    private static readonly Type Tuning = typeof(ShapeTransfer).Assembly.GetType("ShapeForge.SolverTuning");

    internal static void ApplyParams(string set)
    {
        if (string.IsNullOrWhiteSpace(set)) return;
        if (Tuning == null) throw new Exception("This ShapeForge build has no solver settings.");
        foreach (var pair in set.Split(','))
        {
            var parts = pair.Split('=');
            var field = Tuning.GetField(parts[0].Trim()) ?? throw new Exception("Unknown solver setting " + parts[0]);
            field.SetValue(null, Convert.ChangeType(float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), field.FieldType));
        }
    }

    internal static string DescribeParams() => Tuning == null ? "none" :
        string.Join(", ", Tuning.GetFields().Select(f => $"{f.Name}={f.GetValue(null)}"));

    // Renders one outfit at rest, with the creator's shape, with the generated shape, and an error heatmap
    // (blue 0 mm, green 5 mm, red 10 mm or more), from the front and the side.
    // Batch: -executeMethod ChestBench.Render -benchFilter text [-benchShape name] [-benchSolver name] [-benchParams set] [-benchOut folder]
    public static void Render()
    {
        string shape = Argument("-benchShape") ?? "(No bra)Breasts_Small";
        string filter = Argument("-benchFilter") ?? throw new Exception("Pass -benchFilter with part of an outfit name.");
        string folder = Argument("-benchOut") ?? Path.GetFullPath("Renders");
        string solver = Argument("-benchSolver");
        if (!string.IsNullOrEmpty(solver)) SelectSolver(solver);
        ApplyParams(Argument("-benchParams"));
        Directory.CreateDirectory(folder);

        var bodyPath = AssetDatabase.FindAssets("t:GameObject", new[] { BodyFolder }).Select(AssetDatabase.GUIDToAssetPath).First();
        var bodyRoot = (GameObject)UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(bodyPath));
        var body = bodyRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .Where(r => r.sharedMesh && r.sharedMesh.GetBlendShapeIndex(shape) >= 0)
            .OrderByDescending(r => r.sharedMesh.vertexCount).First();
        var shader = Shader.Find("Hidden/ShapeForgeBenchHeat") ?? throw new Exception("BenchHeat.shader is missing.");
        var bodyDelta = ShapeTransfer.ReadSourceDeltas(body.sharedMesh, shape);
        var bodyRest = body.sharedMesh.vertices;

        // Frame the moving part of the body. Its mean normal is the view direction.
        var bodyMatrix = body.transform.localToWorldMatrix;
        float peak = bodyDelta.Max(d => d.magnitude);
        var restNormals = body.sharedMesh.normals;
        var forward = Vector3.zero;
        Bounds? frame = null;
        for (int i = 0; i < bodyRest.Length; i++)
        {
            if (bodyDelta[i].magnitude < peak * .2f) continue;
            var p = bodyMatrix.MultiplyPoint3x4(bodyRest[i]);
            if (frame == null) frame = new Bounds(p, Vector3.zero);
            var b = frame.Value; b.Encapsulate(p); frame = b;
            forward += bodyMatrix.MultiplyVector(restNormals[i]) * bodyDelta[i].magnitude;
        }
        forward = Vector3.ProjectOnPlane(forward, Vector3.up).normalized;
        var center = frame.Value.center;
        float size = Mathf.Max(frame.Value.size.x, frame.Value.size.y, frame.Value.size.z) * 1.1f;

        foreach (var path in AssetDatabase.FindAssets("t:GameObject", new[] { OutfitFolder }).Select(AssetDatabase.GUIDToAssetPath).OrderBy(p => p))
        {
            string outfit = Path.GetFileNameWithoutExtension(path);
            if (outfit.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var root = (GameObject)UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(path));
            var pieces = new List<(SkinnedMeshRenderer Renderer, Vector3[] Authored)>();
            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = renderer.sharedMesh;
                if (!mesh || mesh.GetBlendShapeIndex(shape) < 0 || mesh.vertexCount == body.sharedMesh.vertexCount) continue;
                var authored = ShapeTransfer.ReadSourceDeltas(mesh, shape);
                if (authored.Max(d => d.magnitude) < 1e-5f) continue;
                var copy = UnityEngine.Object.Instantiate(mesh);
                copy.ClearBlendShapes();
                renderer.sharedMesh = copy;
                pieces.Add((renderer, authored));
            }
            if (pieces.Count == 0) { UnityEngine.Object.DestroyImmediate(root); continue; }
            var fit = new LayerFit(body, pieces.Select(p => p.Renderer).ToArray(), shape, Method);

            var scene = new List<(GameObject Go, int Panel)>();
            var bodyGray = new Color(.55f, .55f, .58f);
            // Panels: 0 rest, 1 creator, 2 generated, 3 creator motion and 4 generated motion (0 to 50 mm, drawn at rest),
            // 5 generated minus creator along the view axis (red: ours stays forward, blue: ours goes back, 10 mm full).
            for (int panel = 0; panel < Panels; panel++)
                scene.Add((Solid(bodyMatrix, bodyRest, panel == 0 || panel == 3 || panel == 4 ? null : bodyDelta, body.sharedMesh, _ => bodyGray), panel));
            float mm = 1000;
            foreach (var (renderer, authored) in pieces)
            {
                var generated = fit.GeneratedMotion(renderer, 100);
                var matrix = renderer.transform.localToWorldMatrix;
                var rest = renderer.sharedMesh.vertices;
                var mesh = renderer.sharedMesh;
                scene.Add((Solid(matrix, rest, null, mesh, _ => new Color(.9f, .88f, .82f)), 0));
                scene.Add((Solid(matrix, rest, authored, mesh, _ => new Color(.75f, .85f, 1f)), 1));
                scene.Add((Solid(matrix, rest, generated, mesh, _ => new Color(1f, .8f, .6f)), 2));
                scene.Add((Solid(matrix, rest, null, mesh, i => Heat(matrix.MultiplyVector(authored[i]).magnitude * mm / 50f)), 3));
                scene.Add((Solid(matrix, rest, null, mesh, i => Heat(matrix.MultiplyVector(generated[i]).magnitude * mm / 50f)), 4));
                scene.Add((Solid(matrix, rest, authored, mesh, i =>
                {
                    float t = Mathf.Clamp(Vector3.Dot(matrix.MultiplyVector(generated[i] - authored[i]), forward) * mm / 10f, -1, 1);
                    return t >= 0 ? Color.Lerp(Color.white, new Color(1f, .15f, .1f), t) : Color.Lerp(Color.white, new Color(.15f, .35f, 1f), -t);
                }), 5));
            }
            foreach (var r in root.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
            foreach (var r in bodyRoot.GetComponentsInChildren<Renderer>(true)) r.enabled = false;

            var camera = new GameObject("Bench Camera").AddComponent<Camera>();
            camera.orthographic = true;
            camera.orthographicSize = size * .5f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(.13f, .13f, .15f);
            camera.nearClipPlane = .01f;
            camera.farClipPlane = 10f;
            const int tile = 420;
            var target = new RenderTexture(tile, tile, 24) { antiAliasing = 4 };
            camera.targetTexture = target;
            var sheet = new Texture2D(tile * Panels, tile * 2, TextureFormat.RGB24, false);
            var side = Vector3.Cross(Vector3.up, forward).normalized;
            var views = new[] { forward, side };
            for (int row = 0; row < 2; row++)
                for (int panel = 0; panel < Panels; panel++)
                {
                    foreach (var (go, p) in scene) go.SetActive(p == panel);
                    camera.transform.position = center + views[row] * 3;
                    camera.transform.rotation = Quaternion.LookRotation(-views[row], Vector3.up);
                    camera.Render();
                    RenderTexture.active = target;
                    var shot = new Texture2D(tile, tile, TextureFormat.RGB24, false);
                    shot.ReadPixels(new Rect(0, 0, tile, tile), 0, 0);
                    shot.Apply();
                    sheet.SetPixels(panel * tile, (1 - row) * tile, tile, tile, shot.GetPixels());
                    UnityEngine.Object.DestroyImmediate(shot);
                }
            RenderTexture.active = null;
            sheet.Apply();
            string file = Path.Combine(folder, outfit + ".png");
            File.WriteAllBytes(file, sheet.EncodeToPNG());
            Debug.Log("SHAPEFORGE BENCH RENDER " + file);
            foreach (var (go, _) in scene) UnityEngine.Object.DestroyImmediate(go);
            UnityEngine.Object.DestroyImmediate(camera.gameObject);
            UnityEngine.Object.DestroyImmediate(root);
        }
        UnityEngine.Object.DestroyImmediate(bodyRoot);

        GameObject Solid(Matrix4x4 matrix, Vector3[] rest, Vector3[] delta, Mesh source, Func<int, Color> color)
        {
            var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            var points = new Vector3[rest.Length];
            var colors = new Color[rest.Length];
            for (int i = 0; i < rest.Length; i++)
            {
                points[i] = matrix.MultiplyPoint3x4(rest[i] + (delta != null ? delta[i] : Vector3.zero));
                colors[i] = color(i);
            }
            mesh.vertices = points;
            mesh.colors = colors;
            var triangles = new List<int>();
            for (int part = 0; part < source.subMeshCount; part++)
                if (source.GetTopology(part) == MeshTopology.Triangles) triangles.AddRange(source.GetTriangles(part));
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            var go = new GameObject("Bench Mesh");
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = new Material(shader);
            return go;
        }
    }

    // Lists every shape on the body and the chest shapes on each outfit, with the largest motion in mm.
    public static void Dump()
    {
        var report = new StringBuilder("SHAPEFORGE BENCH SHAPES\n");
        foreach (var path in AssetDatabase.FindAssets("t:GameObject", new[] { BodyFolder, OutfitFolder }).Select(AssetDatabase.GUIDToAssetPath).OrderBy(p => p))
        {
            bool isBody = path.StartsWith(BodyFolder);
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            report.AppendLine(Path.GetFileNameWithoutExtension(path));
            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = renderer.sharedMesh;
                if (!mesh) continue;
                float scale = renderer.transform.lossyScale.x * 1000;
                var shapes = Enumerable.Range(0, mesh.blendShapeCount).Select(mesh.GetBlendShapeName)
                    .Where(n => isBody || n.IndexOf("bra", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("breast", StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(n => $"{n} [{ShapeTransfer.ReadSourceDeltas(mesh, n).Max(d => d.magnitude) * scale:F1}mm x{mesh.GetBlendShapeFrameCount(mesh.GetBlendShapeIndex(n))}]");
                report.AppendLine($"  {renderer.name} ({mesh.vertexCount} v): " + string.Join(" | ", shapes));
            }
        }
        Debug.Log(report.ToString());
    }

    private sealed class Probe
    {
        private readonly SkinnedMeshRenderer body;
        private readonly ShapeTransfer.TriangleSurface rest;
        private readonly ShapeTransfer.TriangleSurface[] moved = new ShapeTransfer.TriangleSurface[2];
        private readonly float scale, reach;

        internal Probe(SkinnedMeshRenderer body, string shape)
        {
            this.body = body;
            var mesh = body.sharedMesh;
            var positions = mesh.vertices;
            var delta = ShapeTransfer.ReadSourceDeltas(mesh, shape);
            rest = new ShapeTransfer.TriangleSurface(mesh, positions);
            for (int k = 0; k < 2; k++)
            {
                float t = (k + 1) * .5f;
                moved[k] = new ShapeTransfer.TriangleSurface(rest, positions.Select((p, i) => p + delta[i] * t).ToArray());
            }
            scale = body.transform.lossyScale.x;
            reach = .03f / Mathf.Max(scale, 1e-6f);
        }

        internal Row Score(SkinnedMeshRenderer piece, Vector3[] authored, Vector3[] generated)
        {
            var toBody = body.transform.worldToLocalMatrix * piece.transform.localToWorldMatrix;
            var vertices = piece.sharedMesh.vertices;
            var points = vertices.Select(v => toBody.MultiplyPoint3x4(v)).ToArray();
            var a = authored.Select(d => toBody.MultiplyVector(d) * scale * 1000).ToArray();
            var g = generated.Select(d => toBody.MultiplyVector(d) * scale * 1000).ToArray();
            float peak = a.Max(d => d.magnitude);
            var row = new Row { Vertices = vertices.Length, AuthoredPeak = peak };

            var errors = new List<float>();
            float authoredSum = 0, generatedSum = 0, cosine = 0;
            int inside = 0;
            unchecked
            {
                int key = vertices.Length;
                for (int i = 0; i < vertices.Length; i += 7) key = key * 31 + a[i].GetHashCode() * 17 + vertices[i].GetHashCode();
                row.Key = key;
            }
            float mm = scale * 1000;
            for (int i = 0; i < vertices.Length; i++)
            {
                if (a[i].magnitude < peak * .05f && g[i].magnitude < peak * .05f) continue;
                float error = (g[i] - a[i]).magnitude;
                errors.Add(error);
                authoredSum += a[i].magnitude;
                generatedSum += g[i].magnitude;
                if (a[i].sqrMagnitude > 0 && g[i].sqrMagnitude > 0)
                    cosine += Vector3.Dot(a[i].normalized, g[i].normalized) * a[i].magnitude;
                int bin = Bins.Length;
                if (rest.FindClosest(points[i], reach, out var face, out _, out var offset))
                {
                    float gap = Vector3.Dot(offset, rest.Normal(face)) * mm;
                    if (gap < -2) inside++;
                    bin = gap < 0 ? 0 : Array.FindIndex(Bins, bound => gap < bound);
                    if (bin < 0) bin = Bins.Length;
                }
                row.BinAuthored[bin] += a[i].magnitude;
                row.BinGenerated[bin] += g[i].magnitude;
                row.BinError[bin] += error;
                row.BinCount[bin]++;
                var world = piece.transform.localToWorldMatrix;
                var aw = world.MultiplyVector(authored[i]) * 1000;
                var gw = world.MultiplyVector(generated[i]) * 1000;
                row.AuthoredAxes += aw;
                row.GeneratedAxes += gw;
                row.ErrorAxes += new Vector3(Mathf.Abs(gw.x - aw.x), Mathf.Abs(gw.y - aw.y), Mathf.Abs(gw.z - aw.z));
            }
            row.Region = errors.Count;
            if (errors.Count > 0)
            {
                row.AuthoredAxes /= errors.Count;
                row.GeneratedAxes /= errors.Count;
                row.ErrorAxes /= errors.Count;
                row.Mean = errors.Average();
                row.ErrorSum = errors.Sum();
                row.AuthoredSum = authoredSum;
                row.Gain = authoredSum > 0 ? generatedSum / authoredSum : 0;
                row.Cosine = authoredSum > 0 ? cosine / authoredSum : 0;
                row.RestInside = inside / (float)errors.Count;
                row.Accuracy = authoredSum > 0 ? 1 - errors.Sum() / authoredSum : 0;
                errors.Sort();
                row.P95 = errors[(int)(errors.Count * .95f)];
                row.Max = errors[^1];
            }

            var weld = ShapeTransfer.SeamPartners(piece.sharedMesh, vertices);
            row.Rough = Roughness(piece.sharedMesh, weld, g, peak);
            row.RoughAuthored = Roughness(piece.sharedMesh, weld, a, peak);
            (row.Pen50, row.Pen100) = Penetrations(points, g);
            (row.AuthoredPen50, row.AuthoredPen100) = Penetrations(points, a);
            return row;
        }

        private (int, int) Penetrations(Vector3[] points, Vector3[] motionMm)
        {
            int half = 0, full = 0;
            float tolerance = .5f / 1000 / scale;
            for (int i = 0; i < points.Length; i++)
            {
                if (!rest.FindClosest(points[i], reach, out var face, out _, out var offset)) continue;
                if (Vector3.Dot(offset, rest.Normal(face)) < 0) continue;
                var motion = motionMm[i] / 1000 / scale;
                for (int k = 0; k < 2; k++)
                {
                    var p = points[i] + motion * ((k + 1) * .5f);
                    if (!moved[k].FindClosest(p, reach, out var f, out _, out var o)) continue;
                    if (Vector3.Dot(o, moved[k].Normal(f)) < -tolerance)
                    {
                        if (k == 0) half++;
                        else full++;
                    }
                }
            }
            return (half, full);
        }

        private static float Roughness(Mesh mesh, int[] weld, Vector3[] motion, float peak)
        {
            var neighbors = new Dictionary<int, HashSet<int>>();
            for (int part = 0; part < mesh.subMeshCount; part++)
            {
                if (mesh.GetTopology(part) != MeshTopology.Triangles) continue;
                var t = mesh.GetTriangles(part);
                for (int k = 0; k < t.Length; k += 3)
                    for (int e = 0; e < 3; e++)
                    {
                        int u = weld[t[k + e]], v = weld[t[k + (e + 1) % 3]];
                        if (u == v) continue;
                        (neighbors.TryGetValue(u, out var su) ? su : neighbors[u] = new HashSet<int>()).Add(v);
                        (neighbors.TryGetValue(v, out var sv) ? sv : neighbors[v] = new HashSet<int>()).Add(u);
                    }
            }
            var values = new List<float>();
            foreach (var (i, set) in neighbors)
            {
                if (motion[i].magnitude < peak * .05f) continue;
                var mean = Vector3.zero;
                foreach (int j in set) mean += motion[j];
                values.Add((motion[i] - mean / set.Count).magnitude);
            }
            if (values.Count == 0) return 0;
            values.Sort();
            return values[(int)(values.Count * .95f)];
        }
    }

    // The bench drives whichever solver ShapeForge exposes. Until a second solver exists there is one choice.
    // -benchSolver takes a TransferMethod name. Legacy means the classic Barycentric transfer, Field means Fabric.
    internal static TransferMethod Method = TransferMethod.Fabric;

    internal static void SelectSolver(string name) =>
        Method = name.Equals("Legacy", StringComparison.OrdinalIgnoreCase) ? TransferMethod.Barycentric :
            name.Equals("Field", StringComparison.OrdinalIgnoreCase) ? TransferMethod.Fabric :
            (TransferMethod)Enum.Parse(typeof(TransferMethod), name, true);

    private static string SolverName() => Method.ToString();

    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    private static string Argument(string name)
    {
        var args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
