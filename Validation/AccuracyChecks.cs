using System;
using System.Linq;
using ShapeForge;
using UnityEngine;
using UnityEditor;

public static class AccuracyChecks
{
    public static void Run()
    {
        var body = new GameObject("body").AddComponent<SkinnedMeshRenderer>();
        var cloth = new GameObject("cloth").AddComponent<SkinnedMeshRenderer>();
        var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
        mesh.bindposes = new[] { Matrix4x4.identity };
        mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 3).ToArray();
        cloth.bones = new[] { cloth.transform };
        body.bones = new[] { body.transform };
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
            var original = cloth.sharedMesh;
            cloth.sharedMesh = result;
            var baked = new Mesh();
            foreach (float weight in new[] { 0f, 25f, 50f, 75f, 100f })
            {
                var actual = ShapeTransfer.ReadSourceDeltas(result, "test", weight);
                float expected = (1 - Mathf.Abs(weight - 50) / 50) * .1f;
                cloth.SetBlendShapeWeight(0, weight);
                cloth.BakeMesh(baked);
                Check(baked.vertices.Select((p,i) => Vector3.Distance(p - mesh.vertices[i], Vector3.right * expected)).Max() < 1e-5f, "Unity baked " + weight);
                Check(actual.All(v => Vector3.Distance(v, Vector3.right * expected) < 1e-5f), method + " " + weight);
            }
            cloth.sharedMesh = original;
            fit.SetCorrection(cloth, 50, middle.Select(v => v * 2).ToArray());
            var editedMiddle = fit.Build(cloth, out _);
            Check(ShapeTransfer.ReadSourceDeltas(editedMiddle, "test", 50).All(v => Vector3.Distance(v, middle[0] * 2) < 1e-5f), "authored knot edit");
            if (method == TransferMethod.Barycentric)
            {
                fit.SetProtection(cloth, new[] { true, false, false });
                var protectedFit = fit.Build(cloth, out _);
                var motion = ShapeTransfer.ReadSourceDeltas(protectedFit, "test", 50);
                Check(motion[0] == Vector3.zero && Vector3.Distance(motion[1], middle[1] * 2) < 1e-5f,
                    "protection masks an edited vertex without changing its neighbor");
                Check(Vector3.Distance(fit.GeneratedMotion(cloth, 50)[0], middle[0]) < 1e-5f,
                    "restore source is independent of later edits and protection");
                fit.SetProtection(cloth, null);
                UnityEngine.Object.DestroyImmediate(protectedFit);
            }
            fit.SetCorrection(cloth, 50, ShapeTransfer.ReadSourceDeltas(result, "test", 50));
            var corrected = fit.Build(cloth, out _, middle);
            Check(ShapeTransfer.ReadSourceDeltas(corrected, "test", 50).SequenceEqual(ShapeTransfer.ReadSourceDeltas(result, "test", 50)), "draft preserves knot");
            UnityEngine.Object.DestroyImmediate(corrected);
            UnityEngine.Object.DestroyImmediate(result);
        }
        // The garment is authored at the second body's offset and the selected endpoint.
        mesh.AddBlendShapeFrame("offset", 100, Enumerable.Repeat(Vector3.forward * 2, 3).ToArray(), null, null);
        mesh.AddBlendShapeFrame("extend", 100, middle, null, null);
        body.SetBlendShapeWeight(1, 100);
        var local = cloth.sharedMesh.vertices;
        cloth.sharedMesh.vertices = local.Select(v => v + Vector3.forward * 2 + middle[0]).ToArray();
        var reverse = new LayerFit(body, new[] { cloth }, "extend", mode: BodyReference.OtherActiveShapesAndTargetAt100);
        var reversed = reverse.Build(cloth, out _);
        Check(ShapeTransfer.ReadSourceDeltas(reversed, "extend").All(v => Vector3.Distance(v, -middle[0]) < 1e-5f), "reverse on an active reference");
        Check(cloth.sharedMesh.vertices.SequenceEqual(reversed.vertices), "authored reverse rest preserved");
        cloth.sharedMesh.vertices = local;

        var close = new Mesh();
        close.vertices = local.Concat(local.Select(v => v + Vector3.forward * .005f)).ToArray();
        close.triangles = new[] { 0,1,2, 5,4,3 };
        close.AddBlendShapeFrame("slide", 100, middle.Concat(new Vector3[3]).ToArray(), null, null);
        var patch = new Mesh { vertices = new[] { new Vector3(.1f,.1f,.0026f), new Vector3(.2f,.1f,0), new Vector3(.1f,.2f,0) }, triangles = new[] { 0,1,2 } };
        var matched = ShapeTransfer.Generate(close, patch, Matrix4x4.identity, out _, "slide");
        Check(ShapeTransfer.ReadSourceDeltas(matched, "slide").All(v => v.x > .095f), "neighbors identify moving surface across a near tie");

        Check(Vector3.Distance(ShapeTransfer.LimitEdgeStrain(Vector3.right, Vector3.right), -.75f * Vector3.right) < 1e-6f, "excess extension");
        Check(ShapeTransfer.LimitEdgeStrain(Vector3.right, Vector3.up - Vector3.right) == Vector3.zero, "rotation retains edge length");

        var seam = new Mesh();
        seam.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.up, Vector3.right, Vector3.one,
            Vector3.zero, Vector3.left, Vector3.down };
        seam.triangles = new[] { 0,1,2, 3,4,5, 6,7,8 };
        var partners = ShapeTransfer.SeamPartners(seam, seam.vertices);
        Check(partners[1] == partners[4] && partners[2] == partners[3] && partners[0] != partners[6], "split seam versus touching panel");

        // The same seam, but one duplicate carries float noise as left behind by an importer.
        var noisy = new Mesh();
        float noise = 3e-7f;
        noisy.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.up + Vector3.one * noise, Vector3.right - Vector3.one * noise, Vector3.one,
            Vector3.zero, Vector3.left, Vector3.down };
        noisy.triangles = new[] { 0,1,2, 3,4,5, 6,7,8 };
        var noisyPartners = ShapeTransfer.SeamPartners(noisy, noisy.vertices);
        Check(noisyPartners[1] == noisyPartners[4] && noisyPartners[2] == noisyPartners[3] && noisyPartners[0] != noisyPartners[6],
            "seam duplicates with float noise still weld");

        var panel = cloth.sharedMesh;
        panel.uv = new[] { Vector2.zero, Vector2.right, Vector2.up };
        panel.tangents = Enumerable.Repeat(new Vector4(1,0,0,-1), 3).ToArray();
        var rotation = Quaternion.AngleAxis(45, Vector3.forward);
        var twist = panel.vertices.Select(v => rotation * v - v).ToArray();
        var turned = ShapeTransfer.BuildMesh(panel, panel.vertices, twist, "turn");
        var tangents = new Vector3[3];
        turned.GetBlendShapeFrameVertices(0, 0, new Vector3[3], null, tangents);
        Check(tangents.All(t => Vector3.Distance(t + Vector3.right, rotation * Vector3.right) < 1e-5f), "UV tangent twist");
        Check(turned.tangents.All(t => t.w == -1), "tangent handedness");
        string folder = AssetDatabase.CreateFolder("Assets", "ShapeForgeCheck-" + Guid.NewGuid().ToString("N"));
        string path = AssetDatabase.GUIDToAssetPath(folder);
        AssetDatabase.CreateAsset(mesh, path + "/body.asset");
        AssetDatabase.CreateAsset(cloth.sharedMesh, path + "/outfit.asset");
        var neutral = cloth.sharedMesh;
        mesh.AddBlendShapeFrame("stationary", 100, new Vector3[mesh.vertexCount], null, null);
        bool rejectedStationary = false;
        try
        {
            ShapeTransfer.Generate(mesh, neutral, Matrix4x4.identity, out _, "stationary");
        }
        catch (InvalidOperationException exception)
        {
            rejectedStationary = exception.Message.Contains("no measurable deformation");
        }
        Check(rejectedStationary, "empty source shape is rejected");

        GeneratorAssets.Generate(cloth, body, null, shape: "test");
        Check(cloth.sharedMesh.bindposes.SequenceEqual(neutral.bindposes), "bind poses preserved");
        Check(cloth.sharedMesh.boneWeights.SequenceEqual(neutral.boneWeights), "bone weights preserved");
        Check(cloth.sharedMesh.uv.SequenceEqual(neutral.uv), "UVs preserved");
        Check(cloth.sharedMesh.normals.SequenceEqual(neutral.normals), "base normals preserved");
        GeneratorAssets.Generate(cloth, body, null, shape: "extend");
        GeneratorAssets.Restore(cloth, GeneratorAssets.GetInfo(cloth.sharedMesh), "extend");
        string savedPath = AssetDatabase.GetAssetPath(cloth.sharedMesh);
        AssetDatabase.ImportAsset(savedPath, ImportAssetOptions.ForceUpdate);
        var saved = AssetDatabase.LoadAssetAtPath<Mesh>(savedPath);
        Check(saved.GetBlendShapeFrameCount(saved.GetBlendShapeIndex("test")) == 2, "selective restore and asset reload");
        Check(ShapeTransfer.ReadSourceDeltas(saved, "test", 50).All(v => Vector3.Distance(v, middle[0]) < 1e-5f), "saved midpoint");
        CheckLayerInterval();
        CheckBrushGeometry();
        Debug.Log("SHAPEFORGE ACCURACY CHECKS PASSED");
    }

    private static void CheckBrushGeometry()
    {
        var cloth = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.one * 10 };
        var turned = Quaternion.Euler(37, 52, -18);
        var translation = new Vector3(3, -4, 2);
        var moved = Array.ConvertAll(cloth, point => turned * point + translation);
        moved[3] = Vector3.zero;
        Check(PreviewBrushGeometry.AlignPatch(cloth, moved, new[] { 1f, .5f, .8f, 0f },
            out var rotation, out var offset), "restore patch alignment");
        for (int i = 0; i < 3; i++)
            Check(Vector3.Distance(rotation * cloth[i] + offset, moved[i]) < 1e-5f,
                "restore keeps the reference in the fitted position and orientation");
        var relax = PreviewBrushGeometry.RelaxShift(Vector3.zero, Vector3.one, Vector3.forward, .2f, .5f);
        Check(relax.magnitude <= .10001f && relax.x > relax.z * 5 && relax.z > 0,
            "relax is bounded and moves mostly along the fabric");
    }

    private static void CheckLayerInterval()
    {
        var vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
        var plane = new Mesh { vertices = vertices, triangles = new[] { 0, 1, 2 } };
        try
        {
            var surface = new ShapeTransfer.TriangleSurface(plane, vertices);
            const System.Reflection.BindingFlags fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var type = typeof(LayerFrame).GetNestedType("Piece", System.Reflection.BindingFlags.NonPublic);
            var floor = Activator.CreateInstance(type, true);
            type.GetField("Rest", fields).SetValue(floor, vertices);
            type.GetField("Range", fields).SetValue(floor, plane.bounds);
            type.GetField("Motion", fields).SetValue(floor, new Vector3[3]);
            type.GetField("Surface", fields).SetValue(floor, surface);
            type.GetField("Fitted", fields).SetValue(floor, new[] { surface, surface });
            type.GetField("IsBody", fields).SetValue(floor, true);
            var constrain = type.GetMethod("Constrain", fields);
            var point = new Vector3(.2f, .2f, .01f);
            var args = new object[] { point, Vector3.back * .03f, Vector3.zero, .10f, Vector3.zero };
            constrain.Invoke(floor, args);
            Check(Vector3.Distance((Vector3)args[4], Vector3.forward * .01f) < 1e-6f,
                "clear endpoints relative to neutral do not hide a crossing from the prior knot");
            args[1] = Vector3.forward * .03f;
            args[4] = Vector3.zero;
            constrain.Invoke(floor, args);
            Check((Vector3)args[4] == Vector3.zero, "clear interval gets no outward correction");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(plane);
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
    }
}
