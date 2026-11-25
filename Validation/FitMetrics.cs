using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ShapeForge;
using UnityEditor;
using UnityEngine;

// Scores how well a generated fit keeps each garment vertex at its rest distance from the body.
// Copy this file into an Editor folder of a project that has ShapeForge installed. Nothing is written to disk:
// the fit is solved in memory and only reported.
//
// Menu: Tools/ShapeForge/Validation/Measure Fit Quality. Select an outfit root (or the avatar) in the Hierarchy first.
// Batch: -executeMethod FitMetrics.RunBatch -fitScene Assets/Scenes/Test.unity [-fitOutfit "Object Name"] [-fitShape "Shape"]
//
// Per piece it reports the gap error in millimeters (world scale): mean, 95th percentile and worst, plus the number of
// vertices that were outside the body at rest and are inside it after the fit. Lower is better. Compare runs before
// and after a change with the same shape and the same outfit to see whether a tweak actually helped.
public static class FitMetrics
{
    private const float DefaultReach = .06f;

    [MenuItem("Tools/ShapeForge/Validation/Measure Fit Quality")]
    private static void Menu()
    {
        var root = Selection.activeGameObject;
        if (!root)
        {
            Debug.LogWarning("ShapeForge: select an outfit or avatar root in the Hierarchy first.");
            return;
        }
        Debug.Log(MeasureRoot(root, null));
    }

    public static void RunBatch()
    {
        string scene = Argument("-fitScene"), outfit = Argument("-fitOutfit"), shape = Argument("-fitShape");
        if (string.IsNullOrEmpty(scene))
            throw new Exception("Pass -fitScene <path to a scene containing the avatar>.");
        UnityEditor.SceneManagement.EditorSceneManager.OpenScene(scene);
        GameObject target = null;
        if (!string.IsNullOrEmpty(outfit))
        {
            target = Resources.FindObjectsOfTypeAll<GameObject>().FirstOrDefault(go => go.scene.IsValid() && go.name == outfit);
            if (!target) throw new Exception("No scene object is named " + outfit);
        }
        else
            target = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene().GetRootGameObjects().FirstOrDefault();
        Debug.Log(MeasureRoot(target, shape));
    }

    internal static string MeasureRoot(GameObject root, string shape)
    {
        var avatar = GeneratorAssets.AvatarRoot(root.transform);
        var all = (avatar ? avatar.gameObject : root).GetComponentsInChildren<SkinnedMeshRenderer>(false);
        var body = GeneratorAssets.FindBody(null, avatar);
        // Without an avatar descriptor, fall back to the renderer with the most blendshapes.
        body ??= all.Where(r => r.sharedMesh).OrderByDescending(r => r.sharedMesh.blendShapeCount).FirstOrDefault();
        if (!body) return "ShapeForge fit metrics: no body renderer with blendshapes was found.";
        if (string.IsNullOrEmpty(shape))
            shape = ShapeTransfer.DiscoverShapes(body.sharedMesh).Where(s => !s.Hidden).Select(s => s.Name).FirstOrDefault();
        if (string.IsNullOrEmpty(shape)) return "ShapeForge fit metrics: the body has no usable source shape.";

        var outfits = root.GetComponentsInChildren<SkinnedMeshRenderer>(false)
            .Where(r => r != body && r.sharedMesh && r.sharedMesh.GetBlendShapeIndex(shape) < 0)
            .ToArray();
        if (outfits.Length == 0) return "ShapeForge fit metrics: no outfit renderers without this shape were found under " + root.name + ".";
        return Measure(body, outfits, shape);
    }

    internal static string Measure(SkinnedMeshRenderer body, SkinnedMeshRenderer[] outfits, string shape,
        TransferMethod method = TransferMethod.Fabric)
    {
        var report = new StringBuilder($"ShapeForge fit metrics, shape \"{shape}\", body {body.name}\n");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var fit = new LayerFit(body, outfits, shape, method);
        long solveMs = watch.ElapsedMilliseconds;

        var mesh = body.sharedMesh;
        var rest = mesh.vertices;
        var delta = ShapeTransfer.ReadSourceDeltas(mesh, shape, 100);
        var moved = rest.Select((p, i) => p + delta[i]).ToArray();
        float peak = delta.Max(d => d.magnitude);
        float scale = Mathf.Abs(body.transform.lossyScale.x);
        float reach = Mathf.Max(peak * 1.5f, DefaultReach / Mathf.Max(scale, 1e-6f));
        var restSurface = new ShapeTransfer.TriangleSurface(mesh, rest);
        var movedSurface = new ShapeTransfer.TriangleSurface(restSurface, moved);
        var worldToBody = body.transform.worldToLocalMatrix;

        foreach (var outfit in outfits)
        {
            Vector3[] motion;
            try { motion = fit.GeneratedMotion(outfit, 100); }
            catch (Exception error)
            {
                report.AppendLine($"  {outfit.name}: skipped ({error.Message})");
                continue;
            }
            var toBody = worldToBody * outfit.transform.localToWorldMatrix;
            var vertices = outfit.sharedMesh.vertices;
            var errors = new List<float>();
            int penetrations = 0, unmatched = 0;
            for (int i = 0; i < vertices.Length; i++)
            {
                var point = toBody.MultiplyPoint3x4(vertices[i]);
                if (!restSurface.FindClosest(point, reach, out var face, out var weights, out var offset)) continue;
                var local = delta[face.A] * weights.x + delta[face.B] * weights.y + delta[face.C] * weights.z;
                // Only judge vertices that sit where the body actually moves.
                if (local.magnitude < peak * .05f) continue;
                float restGap = Vector3.Dot(offset, restSurface.Normal(face));
                var after = point + toBody.MultiplyVector(motion[i]);
                if (!movedSurface.FindClosest(after, reach, out var movedFace, out _, out var movedOffset))
                {
                    unmatched++;
                    continue;
                }
                float gap = Vector3.Dot(movedOffset, movedSurface.Normal(movedFace));
                errors.Add(Mathf.Abs(gap - restGap) * scale);
                if (restGap >= 0 && gap < 0) penetrations++;
            }
            if (errors.Count == 0)
            {
                report.AppendLine($"  {outfit.name}: no vertices sit on the moving part of the body");
                continue;
            }
            errors.Sort();
            report.AppendLine($"  {outfit.name}: {errors.Count} vertices judged, gap error mean {errors.Average() * 1000:F2} mm, " +
                $"p95 {errors[(int)(errors.Count * .95f)] * 1000:F2} mm, worst {errors[^1] * 1000:F2} mm, " +
                $"new penetrations {penetrations}, lost track of {unmatched}");
        }
        report.AppendLine($"  solve time {solveMs} ms");
        return report.ToString();
    }

    private static string Argument(string name)
    {
        var args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
