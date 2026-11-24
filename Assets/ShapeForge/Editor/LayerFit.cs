using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;

namespace ShapeForge
{
    // Snapshot every selected piece before Generate assigns any meshes to renderers.
    internal sealed class LayerFit
    {
        private readonly List<(float Weight, LayerFrame Frame)> frames = new();
        private readonly string shape;
        private readonly Dictionary<SkinnedMeshRenderer, int[]> welds = new();

        private readonly Dictionary<(SkinnedMeshRenderer, float), Vector3[]> edits = new();
        private readonly Dictionary<SkinnedMeshRenderer, bool[]> protection = new();
        internal readonly bool Reverse;
        internal float[] Weights => frames.Select(frame => frame.Weight).ToArray();

        internal Vector3[] Correction(SkinnedMeshRenderer renderer, float weight) =>
            edits.TryGetValue((renderer, weight), out var motion) ? motion : null;

        internal void SetCorrection(SkinnedMeshRenderer renderer, float weight, Vector3[] motion)
        {
            if (motion == null) edits.Remove((renderer, weight));
            else edits[(renderer, weight)] = motion;
        }

        internal void SetProtection(SkinnedMeshRenderer renderer, bool[] mask)
        {
            if (mask == null) protection.Remove(renderer);
            else protection[renderer] = mask;
        }

        private Vector3[] EditedMotion(SkinnedMeshRenderer renderer, float weight, LayerFrame frame, out string message)
        {
            var original = frame.Motion(renderer, out message);
            var motion = edits.TryGetValue((renderer, weight), out var edit) ? edit : original;
            if (!protection.TryGetValue(renderer, out var mask)) return motion;
            var protectedMotion = (Vector3[])motion.Clone();
            for (int i = 0; i < mask.Length; i++)
                if (mask[i]) protectedMotion[i] = Vector3.zero;
            return protectedMotion;
        }

        internal LayerFit(SkinnedMeshRenderer body, SkinnedMeshRenderer[] renderers, string shape, TransferMethod method = TransferMethod.Barycentric, BodyReference mode = BodyReference.Neutral)
        {
            this.shape = shape;
            Reverse = mode == BodyReference.OtherActiveShapesAndTargetAt100;
            var reference = ShapeTransfer.ReferencePositions(body, shape, mode);
            var weights = ShapeTransfer.FrameWeights(body.sharedMesh, shape, Reverse);
            LayerFrame prior = null;
            foreach (float weight in weights)
            {
                var frame = new LayerFrame(body, renderers, shape, method, weight, weights.Count > 1, reference, Reverse, prior, welds);
                frames.Add((weight, frame));
                prior = frame;
            }
        }

        internal Vector3[] GeneratedMotion(SkinnedMeshRenderer renderer, float weight)
        {
            foreach (var frame in frames)
                if (frame.Weight == weight) return frame.Frame.Motion(renderer, out _);
            throw new InvalidOperationException("Select a generated frame.");
        }

        internal string Key(SkinnedMeshRenderer renderer) =>
            (Reverse ? "reverse:" : "") + string.Join("|", frames.Select(frame => Hash128.Compute(EditedMotion(renderer, frame.Weight, frame.Frame, out _)).ToString()));

        internal Mesh Build(SkinnedMeshRenderer renderer, out string diagnostic, Vector3[] endpoint = null, bool calculateShading = true)
        {
            var output = new List<(float, Vector3[])>();
            diagnostic = "";
            foreach (var (weight, frame) in frames)
            {
                var motion = EditedMotion(renderer, weight, frame, out string message);
                if (weight == 100) diagnostic = message;
                output.Add((weight, weight == 100 && endpoint != null ? endpoint : motion));
            }
            return ShapeTransfer.BuildMesh(renderer.sharedMesh, renderer.sharedMesh.vertices, output, shape, calculateShading);
        }
    }

    internal sealed class LayerFrame
    {
        private sealed class Piece
        {
            internal Mesh Source;
            internal readonly List<int[]> TriangleSections = new();
            internal readonly HashSet<(int A, int B)> Edges = new();
            internal Matrix4x4 ToBody;
            internal float Winding = 1f;
            internal Vector3[] Local, Rest, Motion, Result, Previous, Clearance;
            internal int[] Weld;
            internal ShapeTransfer.TriangleSurface Surface;
            internal string Message;
            internal Exception Error;
            internal Bounds Range;
            internal bool IsBody;
            internal ShapeTransfer.TriangleSurface[] Fitted;

            internal void Constrain(Vector3 point, Vector3 previous, Vector3 motion, float radius, ref Vector3 push)
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
                // Sample the actual piecewise-linear interval from the prior authored frame.
                for (int step = 1; step <= 2; step++)
                {
                    float t = step * .5f;
                    var aMotion = At(face.A, t);
                    var bMotion = At(face.B, t);
                    var cMotion = At(face.C, t);
                    var fittedNormal = Vector3.Cross(ab + bMotion - aMotion, ac + cMotion - aMotion).normalized * Winding;
                    if (Vector3.Dot(fittedNormal, normal) < .65f)
                        continue;
                    var sampleMotion = Vector3.LerpUnclamped(previous, motion + push, t);
                    var relative = sampleMotion - (aMotion * weights.x + bMotion * weights.y + cMotion * weights.z);
                    float height = Vector3.Dot(offset + relative, fittedNormal);
                    var sample = point + sampleMotion;
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

            internal Vector3 At(int i, float t) => Previous == null ? Motion[i] * t : Vector3.LerpUnclamped(Previous[i], Motion[i], t);
        }

        private readonly Dictionary<SkinnedMeshRenderer, Piece> pieces = new();
        private readonly List<Piece> ordered = new();
        private readonly float radius;
        private readonly Vector3[] bodyMotion;
        private readonly float bodyPeak;

        internal LayerFrame(SkinnedMeshRenderer body, SkinnedMeshRenderer[] renderers, string shape, TransferMethod method, float weight, bool allowStationary, Vector3[] reference, bool reverse, LayerFrame prior, Dictionary<SkinnedMeshRenderer, int[]> welds)
        {
            var source = new ShapeTransfer.FitSource(body.sharedMesh, shape, method, weight, reference, reverse);
            bodyMotion = source.Deltas;
            bodyPeak = source.Peak;
            radius = Mathf.Max(bodyPeak, prior?.bodyPeak ?? 0) * .20f;
            var worldToBody = body.transform.worldToLocalMatrix;
            foreach (var renderer in renderers)
            {
                var p = new Piece { Source = renderer.sharedMesh, ToBody = worldToBody * renderer.transform.localToWorldMatrix };
                p.Winding = Mathf.Sign(p.ToBody.determinant);
                pieces.Add(renderer, p);
                try
                {
                    p.Local = p.Source.vertices;
                    if (!welds.TryGetValue(renderer, out p.Weld))
                        welds[renderer] = p.Weld = ShapeTransfer.SeamPartners(p.Source, p.Local);
                    p.Result = ShapeTransfer.Fit(source, p.Source, p.Local, p.ToBody, out p.Message, allowStationary, p.Weld);
                    p.Clearance = new Vector3[p.Result.Length];
                    if (prior != null && prior.pieces.TryGetValue(renderer, out var earlier) && earlier.Error == null)
                        p.Previous = Array.ConvertAll(earlier.Result, p.ToBody.MultiplyVector);
                    p.Rest = Array.ConvertAll(p.Local, p.ToBody.MultiplyPoint3x4);
                    p.Motion = Array.ConvertAll(p.Result, p.ToBody.MultiplyVector);
                    for (int section = 0; section < p.Source.subMeshCount; section++)
                        if (p.Source.GetTopology(section) == MeshTopology.Triangles)
                        {
                            var indices = p.Source.GetTriangles(section);
                            if (indices.Length >= 3)
                            {
                                p.TriangleSections.Add(indices);
                                for (int k = 0; k < indices.Length; k += 3)
                                {
                                    Add(indices[k], indices[k+1]);
                                    Add(indices[k+1], indices[k+2]);
                                    Add(indices[k+2], indices[k]);
                                }
                                void Add(int a, int b) => p.Edges.Add(a < b ? (a,b) : (b,a));
                            }
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
                Motion = bodyMotion, Previous = prior?.bodyMotion, ToBody = Matrix4x4.identity, IsBody = true
            };
            BuildSurfaces(skin);
            // Read a common snapshot for every piece, then publish accepted movement together.
            for (int pass = 0; pass < (ordered.Count > 1 ? 3 : 1); pass++)
            {
                foreach (var p in ordered)
                    Correct(p, skin);
                if (ordered.Count < 2) break;
                bool changed = false;
                foreach (var p in ordered)
                {
                    if (p.Fitted == null) continue;
                    float peakChange = 0;
                    for (int i = 0; i < p.Motion.Length; i++)
                    {
                        var motion = p.ToBody.MultiplyVector(p.Result[i]);
                        peakChange = Mathf.Max(peakChange, (motion - p.Motion[i]).sqrMagnitude);
                        p.Motion[i] = motion;
                    }
                    if (peakChange <= radius * radius * 1e-8f) continue;
                    changed = true;
                    if (pass == 2)
                    {
                        p.Message += " Layer corrections reached the pass limit. Inspect remaining contacts in Preview.";
                        continue;
                    }
                    for (int step = 0; step < p.Fitted.Length; step++)
                    {
                        var points = new Vector3[p.Rest.Length];
                        float t = (step + 1) * .5f;
                        for (int i = 0; i < points.Length; i++) points[i] = p.Rest[i] + p.At(i, t);
                        p.Fitted[step].Refit(points);
                    }
                }
                if (!changed) break;
            }

            // Preview keeps only solved offsets. Release the search trees and temporary topology.
            foreach (var p in ordered)
            {
                p.Fitted = null;
                p.Surface = null;
                p.Rest = p.Motion = p.Local = p.Previous = p.Clearance = null;
                p.Weld = null;
                p.TriangleSections.Clear();
                p.Edges.Clear();
                p.Edges.TrimExcess();
            }

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
                        points[i] = piece.Rest[i] + piece.At(i, fraction);
                    piece.Fitted[frame] = new ShapeTransfer.TriangleSurface(piece.Surface, points);
                }
            }
        }

        private void Correct(Piece p, Piece skin)
        {
            var pushes = new Dictionary<int, Vector3>();
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
                if (!inner.IsBody)
                {
                    var bounds = inner.Range;
                    bounds.Expand(radius * 2);
                    if (!bounds.Intersects(p.Range)) return;
                }
                var motionInBody = inner.IsBody ? Array.ConvertAll(p.Result, p.ToBody.MultiplyVector) : p.Motion;
                // Samples only read the search trees, so chunks run on worker threads. Each chunk keeps
                // its own strongest pushes and they are merged in a fixed preference order.
                Run(p.Rest.Length, (i, local) => Sample(i, i, i, local));
                foreach (var indices in p.TriangleSections)
                    Run(indices.Length / 3, (t, local) => Sample(indices[t * 3], indices[t * 3 + 1], indices[t * 3 + 2], local));
                var edgeList = new (int A, int B)[p.Edges.Count];
                p.Edges.CopyTo(edgeList);
                Run(edgeList.Length, (e, local) => Sample(edgeList[e].A, edgeList[e].B, -1, local));

                void Run(int count, Action<int, Dictionary<int, Vector3>> each)
                {
                    ShapeTransfer.ForRange(count, (start, end) =>
                    {
                        var local = new Dictionary<int, Vector3>();
                        for (int i = start; i < end; i++) each(i, local);
                        lock (pushes)
                            foreach (var entry in local)
                                if (!pushes.TryGetValue(entry.Key, out var prior) || Stronger(entry.Value, prior))
                                    pushes[entry.Key] = entry.Value;
                    }, 2048);
                }

                void Sample(int a, int b, int c, Dictionary<int, Vector3> store)
                {
                    var point = p.Rest[a] + p.Rest[b];
                    var motion = motionInBody[a] + motionInBody[b];
                    var previous = p.Previous == null ? Vector3.zero : p.Previous[a] + p.Previous[b];
                    if (c >= 0)
                    {
                        point += p.Rest[c];
                        motion += motionInBody[c];
                        if (p.Previous != null) previous += p.Previous[c];
                    }
                    float count = c >= 0 ? 3 : 2;
                    if (radius <= 0) return;
                    var push = Vector3.zero;
                    inner.Constrain(point/count, previous/count, motion/count, radius, ref push);
                    if (push.sqrMagnitude < 1e-14f) return;
                    Store(a); Store(b); if (c >= 0) Store(c);
                    void Store(int i)
                    {
                        if (!store.TryGetValue(p.Weld[i], out var prior) || Stronger(push, prior))
                            store[p.Weld[i]] = push;
                    }
                }
            }
            // Larger pushes win. Exact ties break on components so the outcome never depends on thread timing.
            static bool Stronger(Vector3 candidate, Vector3 current)
            {
                float a = candidate.sqrMagnitude, b = current.sqrMagnitude;
                if (a != b) return a > b;
                if (candidate.x != current.x) return candidate.x > current.x;
                if (candidate.y != current.y) return candidate.y > current.y;
                return candidate.z > current.z;
            }
            void ApplyPushes()
            {
                for (int i = 0; i < p.Result.Length; i++)
                {
                    if (!pushes.TryGetValue(p.Weld[i], out var push))
                        continue;
                    var total = Vector3.ClampMagnitude(p.Clearance[i] + push, radius * 2);
                    p.Result[i] += inverse.MultiplyVector(total - p.Clearance[i]);
                    p.Clearance[i] = total;
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
