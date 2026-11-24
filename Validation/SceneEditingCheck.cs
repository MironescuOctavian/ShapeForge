using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using ShapeForge;

public static class SceneEditingCheck
{
    public static void Run()
    {
        var body = Create("Body", new[] { Vector3.zero, Vector3.right, Vector3.up });
        var inner = Create("Inner", OutfitVertices(.004f));
        var outer = Create("Outer", OutfitVertices(.006f));
        body.sharedMesh.AddBlendShapeFrame("Fit", 100, Enumerable.Repeat(Vector3.back * .01f, 3).ToArray(), null, null);
        var host = ScriptableObject.CreateInstance<GeneratorWindow>();
        host.StartEdit(body, new[] { inner, outer }, "Fit", TransferMethod.Barycentric);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var sessionField = typeof(GeneratorWindow).GetField("editSession", flags);
        var window = (FitPreview)sessionField.GetValue(host);
        var type = typeof(FitPreview);
        object Get(string name) => type.GetField(name, flags).GetValue(window);
        void Set(string name, object value) => type.GetField(name, flags).SetValue(window, value);
        object Invoke(string name, params object[] args) => type.GetMethod(name, flags).Invoke(window, args);
        void Check(bool passed, string message) { if (!passed) throw new Exception(message); }

        try
        {
            var pieces = (IList)Get("pieces");
            Check(pieces.Count == 3, "The draft did not build. " + Get("error"));
            var ray = new Ray(new Vector3(.205f, .205f, 1), Vector3.back);
            Check((int)Invoke("PickOutfit", ray) == 1, "Inspection must pick the nearest garment.");
            Check((int)Invoke("PickOutfit", new Ray(new Vector3(.205f, .205f, -1), Vector3.forward)) == -1,
                "The body must occlude garment picking.");
            Check((int)Get("selectedMesh") == -1 && ((IEnumerable)Invoke("ScopedCloth")).Cast<object>().SequenceEqual(new[] { pieces[1], pieces[2] }),
                "Edit must start with both cloth pieces in scope.");
            Invoke("SelectMesh", 0);
            Check(((IEnumerable)Invoke("ScopedCloth")).Cast<object>().Single() == pieces[1],
                "Selecting a cloth piece must restrict the edit scope.");
            Set("isolateMesh", true);
            Check((int)Invoke("PickOutfit", ray) == 0, "Isolate must exclude other garments from picking.");
            Invoke("SelectMesh", -1);
            Check(!(bool)Get("isolateMesh") && ((IEnumerable)Invoke("ScopedCloth")).Cast<object>().Count() == 2,
                "All Cloth must restore both pieces and clear isolation.");
            var piece = pieces[1];
            var fields = piece.GetType();
            Invoke("BuildBrushGraph", piece);
            Invoke("BuildOutlineEdges", piece);
            Check(((Array)fields.GetField("OutlineEdges", flags).GetValue(piece)).Length == 3, "A triangle needs three outline edges.");
            Invoke("UpdateSelection", piece, new[] { true, true, false });
            fields.GetField("Protection", flags).SetValue(piece, new[] { false, true, false });
            var start = (Vector3[])Invoke("CurrentMotion", piece);
            var moved = (Vector3[])Invoke("MoveSelectedVertices", piece, start, Vector3.right * .02f);
            Check(Vector3.Distance(moved[0] - start[0], Vector3.right * .02f) < 1e-6f &&
                moved[1] == start[1] && moved[2] == start[2], "Move must preserve protected and unselected vertices.");
            Invoke("CaptureStroke", piece);
            Invoke("ApplyDraft", piece, moved);
            Invoke("CancelStroke");
            var layer = (LayerFit)Get("previewFit");
            Check(layer.Correction(inner, 100) == null && !(bool)Get("unsavedEdits"),
                "Cancel must restore the exact pre-stroke draft, including clean state.");
            Invoke("CaptureStroke", piece);
            Invoke("ApplyDraft", piece, moved);
            Set("strokeMoved", true);
            Invoke("EndStroke", true);
            host.ShowGenerator();
            host.StartEdit(body, new[] { inner }, "Fit", TransferMethod.Barycentric);
            Check(sessionField.GetValue(host) == window && layer.Correction(inner, 100).SequenceEqual(moved),
                "Returning from the generator must preserve the pending corrections and edit session.");
            Invoke("MoveHistory", Get("brushUndo"), Get("brushRedo"));
            Check(((Vector3[])Invoke("CurrentMotion", piece)).SequenceEqual(start) && !window.HasEdits,
                "Undoing the last edit must restore the starting motion and clean state.");
            Invoke("MoveHistory", Get("brushRedo"), Get("brushUndo"));
            Check(window.HasEdits, "Redo must restore the unsaved state.");
            window.DiscardChanges();
            Check(inner.sharedMesh.blendShapeCount == 0 && outer.sharedMesh.blendShapeCount == 0,
                "Discard must leave original outfit meshes untouched.");
            CheckSpan(type, flags);
            Debug.Log("SHAPEFORGE SCENE EDITING CHECKS PASSED");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(host);
            UnityEngine.Object.DestroyImmediate(body.gameObject);
            UnityEngine.Object.DestroyImmediate(inner.gameObject);
            UnityEngine.Object.DestroyImmediate(outer.gameObject);
        }
    }

    private static void CheckSpan(Type preview, BindingFlags flags)
    {
        var pieceType = preview.GetNestedType("Piece", flags);
        var piece = Activator.CreateInstance(pieceType, nonPublic: true);
        var points = new Vector3[20];
        var edges = new List<(int Target, float Length)>[points.Length];
        for (int row = 0; row < 2; row++)
            for (int i = 0; i < 10; i++)
            {
                int index = row * 10 + i;
                float y = Mathf.Sin(i * Mathf.PI / 9) * -.02f;
                points[index] = new Vector3(i * .01f, y, row * .002f);
                edges[index] = new List<(int, float)>();
                if (i > 0) edges[index].Add((index - 1, .01f));
                if (i < 9) edges[index].Add((index + 1, .01f));
            }

        pieceType.GetField("World", flags).SetValue(piece, points);
        pieceType.GetField("BrushWeld", flags).SetValue(piece, Enumerable.Range(0, points.Length).ToArray());
        pieceType.GetField("BrushEdges", flags).SetValue(piece, edges);
        var method = preview.GetMethod("FindSpanOffsets", flags);
        var arguments = new object[] { piece, points[0], points[4], points[9], .006f, (0, 1, 2), (3, 4, 5), (7, 8, 9) };
        var offsets = (Vector3[])method.Invoke(null, arguments);
        if (offsets == null || offsets[4].y <= 0 || offsets[0] != Vector3.zero || offsets[9] != Vector3.zero ||
            offsets.Skip(10).Any(offset => offset != Vector3.zero))
            throw new Exception("Straighten Span must lift the sag, pin both ends and exclude disconnected cloth inside the strip.");

        arguments[6] = (13, 14, 15);
        if (method.Invoke(null, arguments) != null)
            throw new Exception("Straighten Span must require a connected midpoint.");
    }

    private static Vector3[] OutfitVertices(float depth) =>
        new[] { new Vector3(.2f, .2f, depth), new Vector3(.216f, .2f, depth), new Vector3(.2f, .216f, depth) };

    private static SkinnedMeshRenderer Create(string name, Vector3[] vertices)
    {
        var go = new GameObject(name);
        var renderer = go.AddComponent<SkinnedMeshRenderer>();
        var mesh = new Mesh
        {
            vertices = vertices,
            triangles = new[] { 0, 1, 2 },
            bindposes = new[] { Matrix4x4.identity }
        };
        mesh.boneWeights = Enumerable.Repeat(new BoneWeight { weight0 = 1 }, 3).ToArray();
        mesh.RecalculateNormals();
        renderer.bones = new[] { go.transform };
        renderer.sharedMesh = mesh;
        return renderer;
    }
}
