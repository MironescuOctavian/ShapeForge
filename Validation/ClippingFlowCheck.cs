using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using MilltinaBreastGenerator;

public static class ClippingFlowCheck
{
    public static void Run()
    {
        var body = Create("Body", new[] { Vector3.zero, Vector3.right, Vector3.up });
        var cloth = Create("Test cloth", new[] { new Vector3(.20f,.20f,.004f),
            new Vector3(.216f,.20f,.004f), new Vector3(.20f,.216f,.004f) });
        body.sharedMesh.AddBlendShapeFrame("Fit", 100, Enumerable.Repeat(Vector3.forward * .01f, 3).ToArray(), null, null);
        var window = new FitPreview();
        window.Start(body, new[] { cloth }, "Fit");
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(FitPreview);
        object Field(string name) => type.GetField(name, flags).GetValue(window);
        void Call(string name, params object[] args) => type.GetMethod(name, flags).Invoke(window, args);
        var pieces = (IList)Field("pieces");
        if (pieces.Count != 2) throw new Exception("Preview did not build");
        var piece = pieces[1];
        var view = (SkinnedMeshRenderer)piece.GetType().GetField("Renderer", flags).GetValue(piece);
        var layer = (LayerFit)Field("previewFit");
        var prior = new Vector3[3];
        Call("ApplyDraft", piece, prior);
        Call("CheckClipping");
        for (int i = 0; i < 1000 && Field("clippingWork") != null; i++) Call("AdvanceClipping");
        if (((IList)Field("crossings")).Count == 0)
            throw new Exception("Known intersection was not found. " + Field("status"));

        type.GetField("activePatch", flags).SetValue(window, 0);
        var baseline = view.sharedMesh;
        Call("BeginPatchPreview");
        var firstPreview = view.sharedMesh;
        if (baseline == firstPreview || !layer.Correction(cloth, 100).SequenceEqual(prior) ||
            !(ShapeTransfer.ReadSourceDeltas(firstPreview, "Fit").Max(x => x.z) > 0))
            throw new Exception("Preview must move the intersecting fabric without committing");
        Call("CancelPatchPreview", true);
        if (view.sharedMesh != baseline || !layer.Correction(cloth, 100).SequenceEqual(prior))
            throw new Exception("Cancel must restore the exact preview mesh and prior draft");

        Call("BeginPatchPreview");
        Call("ApplyPatchPreview");
        if (layer.Correction(cloth, 100).Max(x => x.z) <= 0)
            throw new Exception("Apply did not commit correction");
        Call("MoveHistory", Field("brushUndo"), Field("brushRedo"));
        if (!layer.Correction(cloth, 100).SequenceEqual(prior))
            throw new Exception("Undo did not restore prior draft");
        window.Release();
        Debug.Log("CLIPPING PREVIEW CANCEL APPLY UNDO PASSED");
    }

    private static SkinnedMeshRenderer Create(string name, Vector3[] positions)
    {
        var renderer = new GameObject(name).AddComponent<SkinnedMeshRenderer>();
        var mesh = new Mesh { vertices = positions, triangles = new[] { 0, 1, 2 },
            bindposes = new[] { Matrix4x4.identity } };
        mesh.boneWeights = Enumerable.Repeat(new BoneWeight { weight0 = 1 }, 3).ToArray();
        mesh.RecalculateNormals();
        renderer.bones = new[] { renderer.transform };
        renderer.sharedMesh = mesh;
        return renderer;
    }
}
