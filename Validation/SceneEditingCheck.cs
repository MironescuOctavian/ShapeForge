using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using MilltinaBreastGenerator;

public static class SceneEditingCheck
{
    public static void Run()
    {
        var body = Create("Body", new[] { Vector3.zero, Vector3.right, Vector3.up });
        var inner = Create("Inner", OutfitVertices(.004f));
        var outer = Create("Outer", OutfitVertices(.006f));
        body.sharedMesh.AddBlendShapeFrame("Fit", 100, Enumerable.Repeat(Vector3.back * .01f, 3).ToArray(), null, null);
        FitPreview.Open(body, new[] { inner, outer }, "Fit");
        var window = EditorWindow.GetWindow<FitPreview>();
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
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
            Set("isolateMesh", true);
            Check((int)Invoke("PickOutfit", ray) == 0, "Isolate must exclude other garments from picking.");
            Set("isolateMesh", false);
            var piece = pieces[1];
            var fields = piece.GetType();
            Invoke("BuildBrushGraph", piece);
            Invoke("BuildOutlineEdges", piece);
            Check(((Array)fields.GetField("OutlineEdges", flags).GetValue(piece)).Length == 3, "A triangle needs three outline edges.");
            fields.GetField("Selection", flags).SetValue(piece, new[] { true, true, false });
            fields.GetField("SelectionCount", flags).SetValue(piece, 2);
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
            Invoke("MoveHistory", Get("brushUndo"), Get("brushRedo"));
            Check(((Vector3[])Invoke("CurrentMotion", piece)).SequenceEqual(start), "Undo must revert the move.");
            window.DiscardChanges();
            Check(inner.sharedMesh.blendShapeCount == 0 && outer.sharedMesh.blendShapeCount == 0,
                "Discard must leave original outfit meshes untouched.");
            Debug.Log("SHAPEFORGE SCENE EDITING CHECKS PASSED");
        }
        finally
        {
            window.Close();
            UnityEngine.Object.DestroyImmediate(body.gameObject);
            UnityEngine.Object.DestroyImmediate(inner.gameObject);
            UnityEngine.Object.DestroyImmediate(outer.gameObject);
        }
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
