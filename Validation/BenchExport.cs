using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ShapeForge;
using UnityEditor;
using UnityEngine;

// Development only. Writes per-vertex features of every bench piece that has the creator's shape, for offline analysis.
// Batch: -executeMethod BenchExport.Run [-benchShape name] [-benchOut folder] [-benchSolver name] [-benchParams set]
//
// pieces.csv: piece, outfit, name, vertices, exported nodes, creator peak (mm), share of the region inside the rest body.
// piece_<n>_nodes.csv: one row per welded vertex near the moving body. World millimeters, y up.
//   p: rest position. n: cloth normal turned away from the body. area: lumped cloth area (mm^2).
//   gap: signed distance to the rest body (nan without a match). bn: body normal at the foot point.
//   s0..s64: body motion at the foot point blurred over the surface by 0, 8, 16, 32 and 64 mm.
//   ray_rest, ray_moved: distance along -n to the rest and moved body (nan when nothing within 100 mm).
//   a: creator motion. g: generated motion from the selected solver.
// piece_<n>_edges.csv: welded node pairs.
public static class BenchExport
{
    private static readonly int[] SurfaceRadii = { 0, 8, 16, 32, 64 };

    public static void Run()
    {
        string shape = Argument("-benchShape") ?? throw new Exception("Pass -benchShape with the body shape to test.");
        string folder = Argument("-benchOut") ?? Path.GetFullPath("Export");
        Directory.CreateDirectory(folder);
        string solver = Argument("-benchSolver");
        if (!string.IsNullOrEmpty(solver)) ReferenceBench.SelectSolver(solver);
        string set = Argument("-benchParams");
        if (!string.IsNullOrEmpty(set))
            foreach (var pair in set.Split(','))
            {
                var parts = pair.Split('=');
                var field = typeof(SolverTuning).GetField(parts[0].Trim());
                field.SetValue(null, Convert.ChangeType(float.Parse(parts[1], CultureInfo.InvariantCulture), field.FieldType));
            }

        var bodyPath = AssetDatabase.FindAssets("t:GameObject", new[] { "Assets/Bench/Body" }).Select(AssetDatabase.GUIDToAssetPath).First();
        var bodyRoot = (GameObject)UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(bodyPath));
        var body = bodyRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .Where(r => r.sharedMesh && r.sharedMesh.GetBlendShapeIndex(shape) >= 0)
            .OrderByDescending(r => r.sharedMesh.vertexCount).First();
        var source = new ShapeTransfer.FitSource(body.sharedMesh, shape);
        var bodyField = new ShapeTransfer.BodyField(source);
        float unit = bodyField.Millimeter;
        var bodyWorld = body.transform.localToWorldMatrix;
        float toMm = body.transform.lossyScale.x * 1000;

        var index = new List<string> { "piece,outfit,name,vertices,nodes,creator_peak_mm,rest_inside" };
        int pieceNumber = 0;
        foreach (var path in AssetDatabase.FindAssets("t:GameObject", new[] { "Assets/Bench/Outfits" }).Select(AssetDatabase.GUIDToAssetPath).OrderBy(p => p))
        {
            string outfit = Path.GetFileNameWithoutExtension(path);
            var root = (GameObject)UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(path));
            try
            {
                var pieces = new List<(SkinnedMeshRenderer Renderer, Vector3[] Authored)>();
                foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    var mesh = renderer.sharedMesh;
                    if (!mesh || mesh.GetBlendShapeIndex(shape) < 0) continue;
                    if (mesh.vertexCount == body.sharedMesh.vertexCount || GeneratorAssets.IsBodyRenderer(renderer)) continue;
                    var authored = ShapeTransfer.ReadSourceDeltas(mesh, shape);
                    if (authored.Max(d => d.magnitude) < 1e-5f) continue;
                    var copy = UnityEngine.Object.Instantiate(mesh);
                    copy.ClearBlendShapes();
                    renderer.sharedMesh = copy;
                    pieces.Add((renderer, authored));
                }
                if (pieces.Count == 0) continue;
                var fit = new LayerFit(body, pieces.Select(p => p.Renderer).ToArray(), shape, ReferenceBench.Method);
                foreach (var (renderer, authored) in pieces)
                {
                    Vector3[] generated;
                    try { generated = fit.GeneratedMotion(renderer, 100); }
                    catch { continue; }
                    var (nodes, inside, peak) = WritePiece(Path.Combine(folder, $"piece_{pieceNumber}"), renderer, authored, generated,
                        body, source, bodyField, unit, bodyWorld, toMm);
                    index.Add(string.Join(",", pieceNumber, Quote(outfit), Quote(renderer.name), renderer.sharedMesh.vertexCount, nodes,
                        peak.ToString("F2", CultureInfo.InvariantCulture), inside.ToString("F4", CultureInfo.InvariantCulture)));
                    pieceNumber++;
                }
            }
            catch (Exception error)
            {
                Debug.LogWarning($"BenchExport skipped {outfit}: {error.Message}");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }
        File.WriteAllLines(Path.Combine(folder, "pieces.csv"), index);
        Debug.Log($"SHAPEFORGE BENCH EXPORT {pieceNumber} pieces to {folder}");
        UnityEngine.Object.DestroyImmediate(bodyRoot);
    }

    private static (int Nodes, float Inside, float Peak) WritePiece(string prefix, SkinnedMeshRenderer piece, Vector3[] authored, Vector3[] generated,
        SkinnedMeshRenderer body, ShapeTransfer.FitSource source, ShapeTransfer.BodyField field, float unit, Matrix4x4 bodyWorld, float toMm)
    {
        var mesh = piece.sharedMesh;
        var vertices = mesh.vertices;
        var toBody = body.transform.worldToLocalMatrix * piece.transform.localToWorldMatrix;
        var weld = ShapeTransfer.SeamPartners(mesh, vertices);
        var points = vertices.Select(v => toBody.MultiplyPoint3x4(v)).ToArray();

        // Area-weighted normals and lumped areas on welded topology, in body space.
        var normals = new Vector3[vertices.Length];
        var area = new float[vertices.Length];
        var edges = new HashSet<(int, int)>();
        for (int part = 0; part < mesh.subMeshCount; part++)
        {
            if (mesh.GetTopology(part) != MeshTopology.Triangles) continue;
            var t = mesh.GetTriangles(part);
            for (int k = 0; k < t.Length; k += 3)
            {
                int a = weld[t[k]], b = weld[t[k + 1]], c = weld[t[k + 2]];
                if (a == b || b == c || a == c) continue;
                var cross = Vector3.Cross(points[b] - points[a], points[c] - points[a]);
                normals[a] += cross; normals[b] += cross; normals[c] += cross;
                float third = cross.magnitude * .5f / 3;
                area[a] += third; area[b] += third; area[c] += third;
                edges.Add(a < b ? (a, b) : (b, a));
                edges.Add(b < c ? (b, c) : (c, b));
                edges.Add(a < c ? (a, c) : (c, a));
            }
        }

        float peak = authored.Max(d => toBody.MultiplyVector(d).magnitude) * toMm;
        var rows = new StringBuilder("node,px,py,pz,nx,ny,nz,area,gap,bnx,bny,bnz");
        foreach (int r in SurfaceRadii) rows.Append($",s{r}x,s{r}y,s{r}z");
        rows.AppendLine(",ray_rest,ray_moved,ax,ay,az,gx,gy,gz");
        var exported = new Dictionary<int, int>();
        int inside = 0, judged = 0;
        float reach = 100 * unit;
        for (int i = 0; i < vertices.Length; i++)
        {
            if (weld[i] != i) continue;
            var p = points[i];
            var a = toBody.MultiplyVector(authored[i]);
            if (!source.QueryRegion.Contains(p) && a.magnitude * toMm < peak * .01f) continue;

            bool found = source.OriginalSurface.FindClosest(p, source.Radius * 2, out var face, out var weights, out var offset);
            var bodyNormal = found ? source.OriginalSurface.Normal(face) : Vector3.zero;
            float gap = found ? Vector3.Dot(offset, bodyNormal) : float.NaN;
            var normal = normals[i].normalized;
            if (found && Vector3.Dot(normal, bodyNormal) < 0) normal = -normal;
            if (found && a.magnitude * toMm >= peak * .05f)
            {
                judged++;
                if (gap * toMm < -2) inside++;
            }

            var line = new StringBuilder();
            line.Append(exported.Count);
            Vec(bodyWorld.MultiplyPoint3x4(p) * 1000);
            Vec(bodyWorld.MultiplyVector(normal).normalized);
            line.Append(',').Append(F(area[i] * toMm * toMm));
            line.Append(',').Append(F(gap * toMm));
            Vec(found ? bodyWorld.MultiplyVector(bodyNormal).normalized : Vector3.zero);
            foreach (int r in SurfaceRadii)
                Motion(found ? field.Sample(face, weights, r * unit).Delta : (Vector3?)null);
            line.Append(',').Append(Ray(source.OriginalSurface));
            line.Append(',').Append(Ray(source.FittedSurface));
            Motion(a);
            Motion(toBody.MultiplyVector(generated[i]));
            exported[i] = exported.Count;
            rows.AppendLine(line.ToString());

            void Vec(Vector3 v) => line.Append(',').Append(F(v.x)).Append(',').Append(F(v.y)).Append(',').Append(F(v.z));
            void Motion(Vector3? v)
            {
                if (v.HasValue) Vec(bodyWorld.MultiplyVector(v.Value) * 1000);
                else line.Append(",nan,nan,nan");
            }
            string Ray(ShapeTransfer.TriangleSurface surface) =>
                normal != Vector3.zero && surface.Raycast(new Ray(p, -normal), reach, 0, out _, out float distance) ? F(distance * toMm) : "nan";
        }
        File.WriteAllText(prefix + "_nodes.csv", rows.ToString());
        var edgeRows = new StringBuilder("a,b\n");
        foreach (var (u, v) in edges)
            if (exported.TryGetValue(u, out int eu) && exported.TryGetValue(v, out int ev))
                edgeRows.Append(eu).Append(',').Append(ev).Append('\n');
        File.WriteAllText(prefix + "_edges.csv", edgeRows.ToString());
        return (exported.Count, judged > 0 ? inside / (float)judged : 0, peak);
    }

    private static string F(float value) => float.IsFinite(value) ? value.ToString("0.###", CultureInfo.InvariantCulture) : "nan";
    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    private static string Argument(string name)
    {
        var args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
