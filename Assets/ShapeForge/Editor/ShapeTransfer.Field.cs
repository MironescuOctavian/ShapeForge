using System;
using System.Collections.Generic;
using UnityEngine;

namespace ShapeForge
{
    // Settings of the Fabric transfer. Lengths are millimeters on a 1.4 m avatar; each body converts them with its own
    // size, so a scaled or differently sized avatar behaves the same. The values were fitted against shapes that outfit
    // creators made by hand (Validation/ReferenceBench.cs), which reads and writes these fields by name.
    internal static class SolverTuning
    {
        // Cloth stiffness: how far a disagreement between neighboring vertices spreads over the fabric.
        public static float Smoothing = 6f;
        // Body motion is blurred over this many mm of body surface per mm of gap, because fabric that stands off the
        // skin does not follow its small hills and hollows.
        public static float SigmaPerGap = 1f;
        // How strongly a vertex follows its body match: full at contact, falling with the gap toward the floor.
        public static float ConfidenceGap = 12f;
        public static float ConfidenceFloor = .05f;
        // Loose fabric hangs. The upward part of its motion fades as exp(-gap / Gravity), gap in mm.
        public static float Gravity = 50f;
        // Chord support for straight runs of fabric that cross a hollow. A run spans a hollow fully once its middle
        // stands SpanHollow mm further off the body than its ends.
        public static float Spans = 1f;
        public static float SpanHollow = 5f;
        // Cloth that started outside the body ends at least this far outside it, or at its rest gap if closer.
        public static float Clearance = 1f;
        public static float ContactWeight = 20f;
        public static int CollisionPasses = 6;
        public static int Iterations = 800;
    }

    internal static partial class ShapeTransfer
    {
        // A graph Laplacian with positive edge weights and a lumped area per node. Weights approximate the cotangent
        // Laplacian on well shaped triangles, so a smoothing length means the same distance on dense and sparse meshes.
        internal sealed class Graph
        {
            internal int Count;
            internal int[] Start, Neighbor;
            internal float[] Weight, Degree, Mass;

            // node[i] is the solver node of vertex i. Seam duplicates share one node.
            internal static Graph Build(Mesh mesh, Vector3[] points, int[] node, int count)
            {
                var weights = new Dictionary<long, float>();
                var mass = new float[count];
                for (int part = 0; part < mesh.subMeshCount; part++)
                {
                    if (mesh.GetTopology(part) != MeshTopology.Triangles) continue;
                    var indices = mesh.GetTriangles(part);
                    for (int k = 0; k < indices.Length; k += 3)
                    {
                        int ia = indices[k], ib = indices[k + 1], ic = indices[k + 2];
                        int a = node[ia], b = node[ib], c = node[ic];
                        if (a < 0 || b < 0 || c < 0 || a == b || b == c || a == c) continue;
                        float area = Vector3.Cross(points[ib] - points[ia], points[ic] - points[ia]).magnitude * .5f;
                        if (!(area > 0)) continue;
                        mass[a] += area / 3;
                        mass[b] += area / 3;
                        mass[c] += area / 3;
                        Add(a, b, (points[ia] - points[ib]).sqrMagnitude, area);
                        Add(b, c, (points[ib] - points[ic]).sqrMagnitude, area);
                        Add(c, a, (points[ic] - points[ia]).sqrMagnitude, area);
                    }
                }

                var graph = new Graph { Count = count, Mass = mass, Degree = new float[count], Start = new int[count + 1] };
                float typical = 0;
                int massive = 0;
                foreach (float m in mass)
                    if (m > 0) { typical += m; massive++; }
                typical = massive > 0 ? typical / massive : 1;
                for (int i = 0; i < count; i++)
                    if (!(mass[i] > 0)) mass[i] = typical * 1e-3f;
                foreach (var key in weights.Keys)
                {
                    graph.Start[(int)(key >> 32) + 1]++;
                    graph.Start[(int)(key & 0xffffffff) + 1]++;
                }
                for (int i = 0; i < count; i++) graph.Start[i + 1] += graph.Start[i];
                graph.Neighbor = new int[graph.Start[count]];
                graph.Weight = new float[graph.Start[count]];
                var fill = (int[])graph.Start.Clone();
                foreach (var (key, weight) in weights)
                {
                    int u = (int)(key >> 32), v = (int)(key & 0xffffffff);
                    graph.Neighbor[fill[u]] = v;
                    graph.Weight[fill[u]++] = weight;
                    graph.Neighbor[fill[v]] = u;
                    graph.Weight[fill[v]++] = weight;
                    graph.Degree[u] += weight;
                    graph.Degree[v] += weight;
                }
                return graph;

                void Add(int u, int v, float lengthSquared, float area)
                {
                    long key = u < v ? ((long)u << 32) | (uint)v : ((long)v << 32) | (uint)u;
                    weights.TryGetValue(key, out float prior);
                    weights[key] = prior + area / (1.5f * Mathf.Max(lengthSquared, 1e-20f));
                }
            }

            // Solves (diag + lambda * L) x = rhs by Jacobi-preconditioned conjugate gradients, one system per axis.
            internal Vector3[] Solve(float[] diag, float lambda, Vector3[] rhs, Vector3[] guess, int iterations = 800, float tolerance = 1e-5f)
            {
                int n = Count;
                var x = guess != null ? (Vector3[])guess.Clone() : new Vector3[n];
                var r = new Vector3[n];
                var z = new Vector3[n];
                var p = new Vector3[n];
                var q = new Vector3[n];
                var inverse = new float[n];
                for (int i = 0; i < n; i++)
                    inverse[i] = 1f / Mathf.Max(diag[i] + lambda * Degree[i], 1e-30f);

                Apply(x, q);
                double bx = 0, by = 0, bz = 0;
                for (int i = 0; i < n; i++)
                {
                    r[i] = rhs[i] - q[i];
                    z[i] = r[i] * inverse[i];
                    p[i] = z[i];
                    bx += rhs[i].x * rhs[i].x; by += rhs[i].y * rhs[i].y; bz += rhs[i].z * rhs[i].z;
                }
                double limit = tolerance * tolerance * Math.Max(bx + by + bz, 1e-30);
                Dot(r, z, out double rzx, out double rzy, out double rzz);
                for (int iteration = 0; iteration < iterations; iteration++)
                {
                    Apply(p, q);
                    Dot(p, q, out double px, out double py, out double pz);
                    float ax = px > 0 ? (float)(rzx / px) : 0, ay = py > 0 ? (float)(rzy / py) : 0, az = pz > 0 ? (float)(rzz / pz) : 0;
                    double residual = 0;
                    for (int i = 0; i < n; i++)
                    {
                        x[i] += new Vector3(ax * p[i].x, ay * p[i].y, az * p[i].z);
                        r[i] -= new Vector3(ax * q[i].x, ay * q[i].y, az * q[i].z);
                        residual += r[i].sqrMagnitude;
                        z[i] = r[i] * inverse[i];
                    }
                    if (residual < limit) break;
                    Dot(r, z, out double nx, out double ny, out double nz);
                    float gx = rzx > 0 ? (float)(nx / rzx) : 0, gy = rzy > 0 ? (float)(ny / rzy) : 0, gz = rzz > 0 ? (float)(nz / rzz) : 0;
                    for (int i = 0; i < n; i++)
                        p[i] = z[i] + new Vector3(gx * p[i].x, gy * p[i].y, gz * p[i].z);
                    (rzx, rzy, rzz) = (nx, ny, nz);
                }
                return x;

                void Apply(Vector3[] value, Vector3[] result)
                {
                    ForRange(n, (start, end) =>
                    {
                        for (int i = start; i < end; i++)
                        {
                            var sum = value[i] * (diag[i] + lambda * Degree[i]);
                            for (int k = Start[i]; k < Start[i + 1]; k++)
                                sum -= value[Neighbor[k]] * (lambda * Weight[k]);
                            result[i] = sum;
                        }
                    }, 2048);
                }
            }

            private static void Dot(Vector3[] a, Vector3[] b, out double x, out double y, out double z)
            {
                x = y = z = 0;
                for (int i = 0; i < a.Length; i++)
                {
                    x += a[i].x * b[i].x;
                    y += a[i].y * b[i].y;
                    z += a[i].z * b[i].z;
                }
            }
        }

        // Body motion blurred over the body surface at a ladder of radii. Cloth far from the skin samples a wider blur.
        internal sealed class BodyField
        {
            internal readonly float Millimeter;
            private static readonly float[] Radii = { 0, 2, 4, 8, 16, 32, 64, 128 };
            private readonly float[] scales;
            private readonly Vector3[][] deltas, normals;
            private readonly Vector3[] restNormals;

            internal BodyField(FitSource source)
            {
                var mesh = source.Mesh;
                var positions = source.Positions;
                var bounds = new Bounds(positions[0], Vector3.zero);
                foreach (var point in positions) bounds.Encapsulate(point);
                Millimeter = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z)) / 1400f;

                var weld = SeamPartners(mesh, positions);
                var node = new int[positions.Length];
                int count = 0;
                for (int i = 0; i < node.Length; i++) node[i] = weld[i] == i ? count++ : -1;
                for (int i = 0; i < node.Length; i++) node[i] = node[weld[i]];
                var graph = Graph.Build(mesh, positions, node, count);

                var compact = new Vector3[count];
                for (int i = 0; i < positions.Length; i++)
                    if (weld[i] == i) compact[node[i]] = source.Deltas[i];
                var rhs = new Vector3[count];
                for (int i = 0; i < count; i++) rhs[i] = compact[i] * graph.Mass[i];

                scales = new float[Radii.Length];
                deltas = new Vector3[Radii.Length][];
                normals = new Vector3[Radii.Length][];
                restNormals = source.RestNormals;
                deltas[0] = source.Deltas;
                normals[0] = source.FittedNormals;
                var guess = compact;
                for (int level = 1; level < Radii.Length; level++)
                {
                    // One implicit diffusion step of the given radius: (M + r^2 L) x = M d.
                    scales[level] = Radii[level] * Millimeter;
                    var solved = graph.Solve(graph.Mass, scales[level] * scales[level], rhs, guess, 1500);
                    guess = solved;
                    var expanded = new Vector3[positions.Length];
                    var moved = new Vector3[positions.Length];
                    for (int i = 0; i < positions.Length; i++)
                    {
                        expanded[i] = solved[node[i]];
                        moved[i] = positions[i] + expanded[i];
                    }
                    deltas[level] = expanded;
                    normals[level] = VertexNormals(mesh, moved);
                }
            }

            // Motion at a point of a body face blurred over the given radius, and the turn of the blurred surface there.
            internal (Vector3 Delta, Quaternion Turn) Sample(TriangleSurface.Triangle face, Vector3 weights, float radius)
            {
                int level = 0;
                float t = 0;
                if (radius >= scales[^1]) { level = scales.Length - 2; t = 1; }
                else if (radius > 0)
                {
                    while (scales[level + 1] <= radius) level++;
                    t = (radius - scales[level]) / (scales[level + 1] - scales[level]);
                }
                int next = Mathf.Min(level + 1, scales.Length - 1);
                var delta = Vector3.Lerp(At(deltas[level]), At(deltas[next]), t);
                var rest = At(restNormals).normalized;
                var moved = Vector3.Lerp(At(normals[level]).normalized, At(normals[next]).normalized, t).normalized;
                var turn = rest == Vector3.zero || moved == Vector3.zero ? Quaternion.identity : Quaternion.FromToRotation(rest, moved);
                return (delta, turn);

                Vector3 At(Vector3[] values) => values[face.A] * weights.x + values[face.B] * weights.y + values[face.C] * weights.z;
            }
        }

        // The blurred body field depends only on the body and its motion, so fits of several outfits share it.
        private static readonly List<(Mesh Mesh, Vector3[] Positions, Vector3[] Deltas, BodyField Field)> fieldCache = new();

        private static BodyField FieldFor(FitSource body)
        {
            lock (fieldCache)
            {
                foreach (var entry in fieldCache)
                    if (entry.Mesh == body.Mesh && Same(entry.Positions, body.Positions) && Same(entry.Deltas, body.Deltas))
                        return entry.Field;
            }
            var field = new BodyField(body);
            lock (fieldCache)
            {
                if (fieldCache.Count >= 6) fieldCache.RemoveAt(0);
                fieldCache.Add((body.Mesh, (Vector3[])body.Positions.Clone(), (Vector3[])body.Deltas.Clone(), field));
            }
            return field;

            static bool Same(Vector3[] a, Vector3[] b)
            {
                if (a.Length != b.Length) return false;
                for (int i = 0; i < a.Length; i++)
                    if (a[i] != b[i]) return false;
                return true;
            }
        }

        // The Fabric transfer. Each cloth vertex takes the body motion at its foot point, blurred by its distance from
        // the skin; loose cloth rises less than the body under it; one global solve spreads the targets over the fabric
        // with a stiffness that is the same on dense and sparse meshes; runs that bridge a hollow follow their ends; and
        // cloth that would end inside the moved body is pinned outside it and solved again.
        private static Vector3[] FitField(FitSource body, Mesh outfit, Vector3[] vertices, Matrix4x4 outfitToBody, out string warning,
            bool allowStationary, int[] weld)
        {
            var field = body.Smoothed ??= FieldFor(body);
            float mm = field.Millimeter;
            var partners = weld ?? SeamPartners(outfit, vertices);
            var hints = NeighborMotionHints(outfit, vertices, outfitToBody, body, partners, out var matches);

            var node = new int[vertices.Length];
            int count = 0;
            for (int i = 0; i < node.Length; i++) node[i] = partners[i] == i ? count++ : -1;
            for (int i = 0; i < node.Length; i++) node[i] = node[partners[i]];
            var all = new Vector3[vertices.Length];
            for (int i = 0; i < all.Length; i++) all[i] = outfitToBody.MultiplyPoint3x4(vertices[i]);

            var points = new Vector3[count];
            var target = new Vector3[count];
            var rigid = new Vector3[count];
            var confidence = new float[count];
            var restGap = new float[count];
            var judged = new bool[count];
            var found = new bool[count];
            var up = body.Up.sqrMagnitude > 0 ? body.Up.normalized : Vector3.up;
            int matched = 0;
            for (int i = 0; i < vertices.Length; i++)
            {
                if (partners[i] != i) continue;
                int n = node[i];
                points[n] = all[i];
                var (isMatched, face, weights, offset) = matches[i];
                if (!isMatched)
                {
                    // Cloth away from the moving region stays put. Cloth inside it with no body nearby is left to its neighbors.
                    if (!body.QueryRegion.Contains(points[n])) confidence[n] = 1;
                    continue;
                }
                // Near a tie between surfaces, prefer the face whose motion agrees with the neighboring cloth.
                if (hints[i].Count >= 2)
                {
                    var hint = hints[i].Sum / hints[i].Count;
                    var local = body.Deltas[face.A] * weights.x + body.Deltas[face.B] * weights.y + body.Deltas[face.C] * weights.z;
                    float variance = Mathf.Max(0, hints[i].Squares / hints[i].Count - hint.sqrMagnitude);
                    if ((local - hint).sqrMagnitude > Mathf.Max(variance * 4, body.Peak * body.Peak * .04f))
                    {
                        float range = Mathf.Min(body.Radius, offset.magnitude + body.Radius * .08f);
                        if (body.OriginalSurface.FindClosest(points[n], range, out var better, out var betterWeights, out var betterOffset,
                            body.Deltas, hint, hints[i].Normal.normalized))
                            (face, weights, offset) = (better, betterWeights, betterOffset);
                    }
                }
                matched++;
                float gap = offset.magnitude / mm;
                var (delta, turn) = field.Sample(face, weights, SolverTuning.SigmaPerGap * gap * mm);
                var bodyNormal = body.OriginalSurface.Normal(face);
                restGap[n] = Vector3.Dot(offset, bodyNormal);
                judged[n] = restGap[n] >= 0;
                found[n] = true;
                float falloff = gap / Mathf.Max(SolverTuning.ConfidenceGap, 1e-3f);
                confidence[n] = SolverTuning.ConfidenceFloor + (1 - SolverTuning.ConfidenceFloor) * Mathf.Exp(-falloff * falloff);

                // Fabric keeps its own direction rather than turning its offset with the skin. Small rigid parts do turn
                // with the surface they sit on; they are handled after the solve.
                target[n] = delta;
                rigid[n] = delta + (turn * offset - offset);

                // Loose fabric hangs: where the body rises under it, it rises less, fading with the gap. Cloth resting on
                // a surface that faces up is carried by it instead. Collision still lifts any cloth the body pushes into.
                float lift = Vector3.Dot(delta, up);
                if (SolverTuning.Gravity > 0 && lift > 0)
                {
                    float rise = Mathf.Exp(-Mathf.Max(restGap[n], 0) / (SolverTuning.Gravity * mm));
                    float hanging = 1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.4f, .8f, Vector3.Dot(bodyNormal, up)));
                    target[n] -= up * (lift * (1 - rise) * hanging);
                }
            }
            if (matched == 0 && !allowStationary)
                throw new InvalidOperationException("No outfit vertices matched the deforming body surface. Check the outfit fit, rest pose, and scale.");

            var graph = Graph.Build(outfit, all, node, count);
            float lambda = SolverTuning.Smoothing * mm * SolverTuning.Smoothing * mm;
            var diag = new float[count];
            var rhs = new Vector3[count];
            var contacts = new Dictionary<int, Vector3>();
            var spanTarget = new Vector3[count];
            var spanWeight = new float[count];
            Assemble();
            var motion = graph.Solve(diag, lambda, rhs, target, SolverTuning.Iterations);

            if (SolverTuning.Spans > 0)
            {
                // A straight run of fabric across a hollow, like a strap over a valley, is carried by its two ends rather
                // than by the body under its middle. Only a run whose middle stands further off the body than its ends
                // counts, so fabric that follows the surface, like a shirt down a torso, keeps following it.
                var neighbors = new List<(int Index, float Weight, Vector3 Direction)>[count];
                var proximity = new float[count];
                for (int n = 0; n < count; n++)
                {
                    proximity[n] = found[n] ? 1 : 0;
                    if (graph.Start[n + 1] == graph.Start[n]) continue;
                    neighbors[n] = new List<(int, float, Vector3)>();
                    for (int k = graph.Start[n]; k < graph.Start[n + 1]; k++)
                    {
                        int j = graph.Neighbor[k];
                        neighbors[n].Add((j, graph.Weight[k], (points[j] - points[n]).normalized));
                    }
                }
                var spans = FindStraightSpans(points, neighbors, proximity, body.Peak);
                for (int pass = 0; pass < 3; pass++)
                {
                    int carried = 0;
                    for (int n = 0; n < count; n++)
                    {
                        var span = spans[n];
                        float ends = Mathf.Max(restGap[span.A], restGap[span.B]);
                        float hollow = Mathf.Clamp01((restGap[n] - ends) / (SolverTuning.SpanHollow * mm));
                        float weight = span.Strength * (1 - confidence[n]) * hollow * SolverTuning.Spans;
                        if (weight <= 0) continue;
                        spanTarget[n] = Vector3.Lerp(motion[span.A], motion[span.B], span.T);
                        spanWeight[n] = weight;
                        carried++;
                    }
                    if (carried == 0) break;
                    Assemble();
                    motion = graph.Solve(diag, lambda, rhs, motion, SolverTuning.Iterations);
                }
            }

            var surface = body.FittedSurface;
            float clearance = SolverTuning.Clearance * mm;
            for (int pass = 0; pass < SolverTuning.CollisionPasses; pass++)
            {
                int added = 0;
                for (int n = 0; n < count; n++)
                {
                    if (!judged[n]) continue;
                    var point = points[n] + motion[n];
                    if (!surface.FindClosest(point, body.Radius, out var face, out _, out var offset)) continue;
                    var normal = surface.Normal(face);
                    float gap = Vector3.Dot(offset, normal);
                    float required = Mathf.Min(restGap[n], clearance);
                    if (gap >= required - mm * .05f) continue;
                    contacts[n] = motion[n] + normal * Mathf.Min(required - gap, body.Peak);
                    added++;
                }
                if (added == 0) break;
                Assemble();
                motion = graph.Solve(diag, lambda, rhs, motion, SolverTuning.Iterations);
            }

            var bodyToOutfit = outfitToBody.inverse;
            var deltas = new Vector3[vertices.Length];
            for (int i = 0; i < deltas.Length; i++)
                deltas[i] = bodyToOutfit.MultiplyVector(motion[node[i]]);

            // Small closed parts take the rigid motion that best fits their surface-turning targets, because the fabric
            // solve resists rotation inside a part that small. Each keeps the position the solve gave it.
            var islands = FindIslands(outfit, partners, out var closed);
            var rigidDeltas = new Vector3[vertices.Length];
            for (int i = 0; i < rigidDeltas.Length; i++)
                rigidDeltas[i] = bodyToOutfit.MultiplyVector(found[node[i]] ? rigid[node[i]] : motion[node[i]]);
            var adjusted = new HashSet<int>();
            int compactDetails = PreserveCompactDetails(vertices, rigidDeltas, islands, closed, outfitToBody, body.Extent, adjusted);
            if (adjusted.Count > 0)
            {
                var shift = new Dictionary<int, (Vector3 Sum, int Count)>();
                for (int i = 0; i < deltas.Length; i++)
                {
                    if (!adjusted.Contains(islands[i])) continue;
                    shift.TryGetValue(islands[i], out var s);
                    shift[islands[i]] = (s.Sum + deltas[i] - rigidDeltas[i], s.Count + 1);
                }
                for (int i = 0; i < deltas.Length; i++)
                    if (shift.TryGetValue(islands[i], out var s))
                        deltas[i] = rigidDeltas[i] + s.Sum / s.Count;
            }
            warning = $"Matched {matched} positions to the body. Held {contacts.Count} clear of it. Check the fit at 50 and 100.";
            if (compactDetails > 0)
                warning += $" Kept {compactDetails} small details intact.";
            return deltas;

            void Assemble()
            {
                for (int n = 0; n < count; n++)
                {
                    float m = graph.Mass[n];
                    diag[n] = m * (confidence[n] + 1e-4f + spanWeight[n]);
                    rhs[n] = target[n] * (m * confidence[n]) + spanTarget[n] * (m * spanWeight[n]);
                    if (contacts.TryGetValue(n, out var pinned))
                    {
                        diag[n] += m * SolverTuning.ContactWeight;
                        rhs[n] += pinned * (m * SolverTuning.ContactWeight);
                    }
                }
            }
        }
    }
}
