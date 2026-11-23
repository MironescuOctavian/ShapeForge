using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;

namespace MilltinaBreastGenerator
{
    // Snapshot every selected piece before Generate assigns any meshes to renderers.
    internal sealed class LayerFit
    {
        private readonly List<(float Weight, LayerFrame Frame)> frames = new();
        private readonly string shape;

        internal LayerFit(SkinnedMeshRenderer body, SkinnedMeshRenderer[] renderers, string shape = ShapeTransfer.Shape, TransferMethod method = TransferMethod.Barycentric, Vector3[] reference = null)
        {
            this.shape = shape;
            var weights = ShapeTransfer.FrameWeights(body.sharedMesh, shape);
            foreach (float weight in weights)
                frames.Add((weight, new LayerFrame(body, renderers, shape, method, weight, weights.Count > 1, reference)));
        }

        internal string Key(SkinnedMeshRenderer renderer) =>
            string.Join("|", frames.Select(frame => Hash128.Compute(frame.Frame.Motion(renderer, out _)).ToString()));

        internal Mesh Build(SkinnedMeshRenderer renderer, out string diagnostic, Vector3[] endpoint = null)
        {
            var output = new List<(float, Vector3[])>();
            diagnostic = "";
            foreach (var (weight, frame) in frames)
            {
                var motion = frame.Motion(renderer, out string message);
                if (weight == 100) diagnostic = message;
                output.Add((weight, weight == 100 && endpoint != null ? endpoint : motion));
            }
            return ShapeTransfer.BuildMesh(renderer.sharedMesh, renderer.sharedMesh.vertices, output, shape);
        }
    }

    internal sealed class LayerFrame
    {
        private sealed class Piece
        {
            internal Mesh Source;
            internal readonly List<int[]> TriangleSections = new();
            internal Matrix4x4 ToBody;
            internal float Winding = 1f;
            internal Vector3[] Local, Rest, Motion, Result;
            internal ShapeTransfer.TriangleSurface Surface;
            internal string Message;
            internal Exception Error;
            internal Bounds Range;
            internal bool IsBody;
            internal ShapeTransfer.TriangleSurface[] Fitted;

            internal void Constrain(Vector3 point, Vector3 motion, float radius, ref Vector3 push)
            {
                if (Range.SqrDistance(point) >= radius * radius ||
                    !Surface.FindClosest(point, radius, out var face, out var weights, out var offset))
                    return;
                var a = Rest[face.A];
                var ab = Rest[face.B] - a;
                var ac = Rest[face.C] - a;
                var normal = Vector3.Cross(ab, ac).normalized * Winding;
                float gap = Vector3.Dot(offset, normal);
                // Only an originally outside, near-normal projection establishes layer order.
                if (gap < 0 || (!IsBody && (gap <= radius * .002f || gap < offset.magnitude * .8f)))
                    return;
                var relative = motion - (Motion[face.A] * weights.x + Motion[face.B] * weights.y + Motion[face.C] * weights.z);
                for (int step = 1; step <= 2; step++)
                {
                    float t = step * .5f;
                    var fittedNormal = Vector3.Cross(ab + (Motion[face.B] - Motion[face.A]) * t,
                        ac + (Motion[face.C] - Motion[face.A]) * t).normalized * Winding;
                    if (Vector3.Dot(fittedNormal, normal) < .65f)
                        continue;
                    float height = Vector3.Dot(offset + (relative + push) * t, fittedNormal);
                    var sample = point + (motion + push) * t;
                    if (Fitted[step - 1].FindClosest(sample, radius, out var nearest, out _, out var fittedOffset))
                    {
                        var nearestNormal = Fitted[step - 1].Normal(nearest) * Winding;
                        float nearestGap = Vector3.Dot(fittedOffset, nearestNormal);
                        if ((IsBody || nearestGap < height) && Vector3.Dot(nearestNormal, normal) > .45f)
                        {
                            fittedNormal = nearestNormal;
                            height = nearestGap;
                        }
                    }
                    else if (IsBody)
                        continue;
                    float missing = ((IsBody ? 0f : Mathf.Min(gap, radius * .25f)) - height) / t;
                    if (missing > 0)
                        push += fittedNormal * Mathf.Min(missing, radius);
                }
            }
        }

        private readonly Dictionary<SkinnedMeshRenderer, Piece> pieces = new();
        private readonly List<Piece> ordered = new();
        private readonly float radius;

        private readonly string shape;

        internal LayerFrame(SkinnedMeshRenderer body, SkinnedMeshRenderer[] renderers, string shape, TransferMethod method, float weight, bool allowStationary, Vector3[] reference)
        {
            this.shape = shape;
            var source = new ShapeTransfer.FitSource(body.sharedMesh, shape, method, weight, reference);
            radius = source.Peak * .20f;
            var worldToBody = body.transform.worldToLocalMatrix;
            foreach (var renderer in renderers)
            {
                var p = new Piece { Source = renderer.sharedMesh, ToBody = worldToBody * renderer.transform.localToWorldMatrix };
                p.Winding = Mathf.Sign(p.ToBody.determinant);
                pieces.Add(renderer, p);
                try
                {
                    p.Local = p.Source.vertices;
                    p.Result = ShapeTransfer.Fit(source, p.Source, p.Local, p.ToBody, out p.Message, allowStationary);
                    p.Rest = Array.ConvertAll(p.Local, p.ToBody.MultiplyPoint3x4);
                    p.Motion = Array.ConvertAll(p.Result, p.ToBody.MultiplyVector);
                    for (int section = 0; section < p.Source.subMeshCount; section++)
                        if (p.Source.GetTopology(section) == MeshTopology.Triangles)
                        {
                            var indices = p.Source.GetTriangles(section);
                            if (indices.Length >= 3)
                                p.TriangleSections.Add(indices);
                        }
                    if (renderers.Length > 1 && p.TriangleSections.Count > 0)
                        BuildSurfaces(p);
                    ordered.Add(p);
                }
                catch (Exception error) { p.Error = error; }
            }
            var skin = new Piece
            {
                Source = body.sharedMesh, Rest = source.Positions,
                Motion = source.Deltas, ToBody = Matrix4x4.identity, IsBody = true
            };
            BuildSurfaces(skin);
            foreach (var p in ordered)
                Correct(p, skin);

            void BuildSurfaces(Piece piece)
            {
                piece.Range = new Bounds(piece.Rest[0], Vector3.zero);
                foreach (var point in piece.Rest)
                    piece.Range.Encapsulate(point);
                piece.Surface = new ShapeTransfer.TriangleSurface(piece.Source, piece.Rest);
                piece.Fitted = new ShapeTransfer.TriangleSurface[2];
                for (int frame = 0; frame < piece.Fitted.Length; frame++)
                {
                    float fraction = (frame + 1) * .5f;
                    var points = new Vector3[piece.Rest.Length];
                    for (int i = 0; i < points.Length; i++)
                        points[i] = piece.Rest[i] + piece.Motion[i] * fraction;
                    piece.Fitted[frame] = new ShapeTransfer.TriangleSurface(piece.Surface, points);
                }
            }
        }

        private void Correct(Piece p, Piece skin)
        {
            var pushes = new Dictionary<Vector3, Vector3>();
            var inverse = p.ToBody.inverse;
            foreach (var inner in ordered)
                if (inner != p && inner.Surface != null)
                    SolveAgainst(inner);
            ApplyPushes();
            pushes.Clear();
            SolveAgainst(skin);
            ApplyPushes();

            void SolveAgainst(Piece inner)
            {
                var motionInBody = inner.IsBody ? Array.ConvertAll(p.Result, p.ToBody.MultiplyVector) : p.Motion;
                for (int i = 0; i < p.Rest.Length; i++) Sample(i, i, i);
                foreach (var indices in p.TriangleSections)
                {
                    for (int k = 0; k < indices.Length; k += 3)
                    {
                        int a = indices[k], b = indices[k+1], c = indices[k+2];
                        Sample(a,b,c);
                        Sample(a,b,-1);
                        Sample(b,c,-1);
                        Sample(c,a,-1);
                    }
                }

                void Sample(int a, int b, int c)
                {
                    var point = p.Rest[a] + p.Rest[b];
                    var motion = motionInBody[a] + motionInBody[b];
                    if (c >= 0) { point += p.Rest[c]; motion += motionInBody[c]; }
                    float count = c >= 0 ? 3 : 2;
                    if (inner.IsBody && motion.sqrMagnitude < 1e-14f) return;
                    var push = Vector3.zero;
                    inner.Constrain(point/count, motion/count, radius, ref push);
                    if (push.sqrMagnitude < 1e-14f) return;
                    Store(a); Store(b); if (c >= 0) Store(c);
                    void Store(int i)
                    {
                        if (!pushes.TryGetValue(p.Local[i], out var prior) || push.sqrMagnitude > prior.sqrMagnitude)
                            pushes[p.Local[i]] = push;
                    }
                }
            }
            void ApplyPushes()
            {
                for (int i = 0; i < p.Result.Length; i++)
                {
                    if (!pushes.TryGetValue(p.Local[i], out var push))
                        continue;
                    p.Result[i] += inverse.MultiplyVector(push);
                }
                if (pushes.Count > 0)
                    p.Message += $" Surface clearance adjusted {pushes.Count} seam-welded positions.";
            }
        }

        internal Vector3[] Motion(SkinnedMeshRenderer renderer, out string diagnostic)
        {
            var p = pieces[renderer];
            if (p.Error != null) throw new InvalidOperationException(p.Error.Message, p.Error);
            diagnostic = p.Message;
            return p.Result;
        }
    }
}
