using System;
using System.Collections.Generic;
using UnityEngine;

namespace MilltinaBreastGenerator
{
    internal enum BodyReference { Neutral, OtherActiveShapes, OtherActiveShapesAndTargetAt100 }

    internal static class ShapeTransfer
    {
        internal const string Shape = "(No bra)Breasts_Small";
        internal static List<(string Name, bool Hidden)> DiscoverShapes(Mesh mesh)
        {
            var options = new List<(string Name, bool Hidden)>();
            if (!mesh || mesh.blendShapeCount == 0)
                return options;
            var vertices = mesh.vertices;
            var triangles = new List<(int A, int B, int C, float CollapseThreshold)>();
            for (int part = 0; part < mesh.subMeshCount; part++)
            {
                if (mesh.GetTopology(part) != MeshTopology.Triangles)
                    continue;
                var indices = mesh.GetTriangles(part);
                for (int i = 0; i < indices.Length; i += 3)
                {
                    int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                    float area = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]).sqrMagnitude;
                    if (area > 1e-22f)
                        triangles.Add((a, b, c, area * .01f));
                }
            }
            bool milltinaMasks = mesh.GetBlendShapeIndex(Shape) >= 0 &&
                mesh.GetBlendShapeIndex("Shrink_Clothes_on") >= 0;
            var points = new Vector3[vertices.Length];
            var moves = new bool[vertices.Length];
            var upper = new Vector3[vertices.Length];

            for (int index = 0; index < mesh.blendShapeCount; index++)
            {
                string name = mesh.GetBlendShapeName(index);
                if (string.IsNullOrWhiteSpace(name) || name.TrimStart().StartsWith("---", StringComparison.Ordinal))
                    continue;

                bool any = false, finite = true, collapsed = false;
                for (int frame = 0; frame < mesh.GetBlendShapeFrameCount(index); frame++)
                {
                    float weight = mesh.GetBlendShapeFrameWeight(index, frame);
                    if (!float.IsFinite(weight) || weight <= 0) { finite = false; break; }
                    mesh.GetBlendShapeFrameVertices(index, frame, upper, null, null);
                    for (int i = 0; i < upper.Length; i++)
                    {
                        float squared = upper[i].sqrMagnitude;
                        finite &= float.IsFinite(squared);
                        moves[i] = squared > 1e-12f;
                        any |= moves[i];
                        points[i] = vertices[i] + upper[i];
                    }
                    collapsed |= HasCollapsedFaces(points, moves, triangles);
                }
                if (!any || !finite)
                    continue;

                string normalized = name.ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
                bool namedMask = normalized.StartsWith("hide_", StringComparison.Ordinal) ||
                    normalized.StartsWith("mask_", StringComparison.Ordinal) ||
                    normalized.Contains("body_hide") || normalized.Contains("body_mask") ||
                    normalized.Contains("shrink_clothes") ||
                    (milltinaMasks && normalized.StartsWith("shrink_", StringComparison.Ordinal));
                options.Add((name, namedMask || collapsed));
            }
            return options;
        }

        private static bool HasCollapsedFaces(Vector3[] fitted, bool[] moves,
            List<(int A, int B, int C, float CollapseThreshold)> triangles)
        {
            int affected = 0, collapsed = 0;
            foreach (var (a, b, c, threshold) in triangles)
            {
                if (!moves[a] || !moves[b] || !moves[c])
                    continue;
                affected++;
                float fittedArea = Vector3.Cross(fitted[b] - fitted[a], fitted[c] - fitted[a]).sqrMagnitude;
                if (fittedArea <= threshold)
                    collapsed++;
            }
            return collapsed > 0 && collapsed * 3 >= affected;
        }

        internal static Vector3[] ReferencePositions(SkinnedMeshRenderer renderer, string target, BodyReference mode)
        {
            if (mode == BodyReference.Neutral) return null;
            var mesh = renderer.sharedMesh;
            var positions = mesh.vertices;
            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                var name = mesh.GetBlendShapeName(i);
                float weight = name == target ? (mode == BodyReference.OtherActiveShapesAndTargetAt100 ? 100 : 0) : renderer.GetBlendShapeWeight(i);
                if (weight == 0) continue;
                var delta = ReadSourceDeltas(mesh, name, weight);
                for (int j = 0; j < delta.Length; j++) positions[j] += delta[j];
            }
            return positions;
        }

        internal sealed class FitSource
        {
            internal readonly string ShapeName;
            internal readonly Vector3[] Positions, Deltas;
            // Smooth area-weighted vertex normals before and after the source motion.
            internal readonly Vector3[] RestNormals, FittedNormals;
            internal readonly float Peak, Radius;
            internal readonly Bounds QueryRegion;
            internal readonly TriangleSurface OriginalSurface;
            private TriangleSurface fittedSurface;
            internal readonly TransferField Field;

            internal FitSource(Mesh mesh, string shape, TransferMethod method = TransferMethod.Barycentric, float weight = 100, Vector3[] reference = null, bool reverse = false)
            {
                ShapeName = shape;
                Positions = reference ?? mesh.vertices;
                Deltas = ReadSourceDeltas(mesh, shape, reverse ? 100 - weight : weight);
                if (reverse)
                {
                    var authored = ReadSourceDeltas(mesh, shape);
                    for (int i = 0; i < Deltas.Length; i++) Deltas[i] -= authored[i];
                }
                foreach (var delta in Deltas)
                {
                    if (!float.IsFinite(delta.sqrMagnitude))
                        throw new InvalidOperationException("The source shape contains invalid vertex deltas.");
                    Peak = Mathf.Max(Peak, delta.magnitude);
                }
                if (Peak <= 1e-6f)
                    return;

                var queryRegion = MovingRegion(mesh, Positions, Deltas);
                // Reach follows the size of the moving region as well as the shape's peak, so a subtle shape
                // still carries loose fabric that stands off the body.
                Radius = Mathf.Max(Peak * 1.5f, queryRegion.size.magnitude * .1f);
                queryRegion.Expand(Radius * 2);
                RestNormals = VertexNormals(mesh, Positions);
                var deformed = new Vector3[Positions.Length];
                for (int i = 0; i < deformed.Length; i++)
                    deformed[i] = Positions[i] + Deltas[i];
                FittedNormals = VertexNormals(mesh, deformed);
                QueryRegion = queryRegion;
                // Keep stationary faces that can win a nearest-face query in this region.
                var indexRegion = queryRegion;
                indexRegion.Expand(Radius * 2);
                OriginalSurface = new TriangleSurface(mesh, Positions, indexRegion);
                if (method != TransferMethod.Barycentric)
                    Field = new TransferField(mesh, Positions, Deltas, method);
            }

            internal TriangleSurface FittedSurface
            {
                get
                {
                    if (fittedSurface == null)
                    {
                        var points = new Vector3[Positions.Length];
                        for (int i = 0; i < points.Length; i++)
                            points[i] = Positions[i] + Deltas[i];
                        fittedSurface = new TriangleSurface(OriginalSurface, points);
                    }
                    return fittedSurface;
                }
            }
        }

        // Positions are shared by split vertices, so UV and hard-edge duplicates still agree on one normal.
        private static Vector3[] VertexNormals(Mesh mesh, Vector3[] points)
        {
            var sums = new Dictionary<Vector3, Vector3>();
            for (int part = 0; part < mesh.subMeshCount; part++)
            {
                if (mesh.GetTopology(part) != MeshTopology.Triangles) continue;
                var indices = mesh.GetTriangles(part);
                for (int i = 0; i < indices.Length; i += 3)
                {
                    int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                    var area = Vector3.Cross(points[b] - points[a], points[c] - points[a]);
                    Accumulate(a);
                    Accumulate(b);
                    Accumulate(c);

                    void Accumulate(int v)
                    {
                        sums.TryGetValue(points[v], out var sum);
                        sums[points[v]] = sum + area;
                    }
                }
            }
            var normals = new Vector3[points.Length];
            for (int i = 0; i < normals.Length; i++)
                if (sums.TryGetValue(points[i], out var sum)) normals[i] = sum;
            return normals;
        }

        internal static Mesh Generate(Mesh body, Mesh outfit, Matrix4x4 outfitToBody, out string warning, string shape = Shape)
        {
            var vertices = outfit.vertices;
            var weights = FrameWeights(body, shape);
            var frames = new List<(float, Vector3[])>();
            warning = "";
            foreach (float weight in weights)
                frames.Add((weight, Fit(new FitSource(body, shape, weight: weight), outfit, vertices, outfitToBody, out warning, weights.Count > 1)));
            return BuildMesh(outfit, vertices, frames, shape);
        }

        internal static Vector3[] Fit(FitSource body, Mesh outfit, Vector3[] vertices, Matrix4x4 outfitToBody, out string warning, bool allowStationary = false)
        {
            float determinant = outfitToBody.determinant;
            if (!float.IsFinite(determinant) || Mathf.Abs(determinant) < 1e-12f)
                throw new InvalidOperationException("The outfit transform is singular.");

            if (outfit.GetBlendShapeIndex(body.ShapeName) >= 0)
                throw new InvalidOperationException("The outfit already has this shape.");

            if (body.Peak <= 1e-6f)
            {
                if (!allowStationary)
                    throw new InvalidOperationException("The source shape has no measurable deformation.");
                warning = "Stationary frame.";
                return new Vector3[vertices.Length];
            }

            var source = body.Positions;
            var deformation = body.Deltas;
            float largestDelta = body.Peak;
            float radius = body.Radius;
            var surface = body.OriginalSurface;
            var deltas = new Vector3[vertices.Length];
            var restGap = new float[vertices.Length];
            var normals = new Vector3[vertices.Length];
            var confidence = new float[vertices.Length];
            var proximity = new float[vertices.Length];
            var bodyToOutfit = outfitToBody.inverse;
            var hints = NeighborMotionHints(outfit, vertices, outfitToBody, body, out var matches);
            int changed = 0;
            int faded = 0;
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 point = outfitToBody.MultiplyPoint3x4(vertices[i]);
                if (!float.IsFinite(point.sqrMagnitude))
                    throw new InvalidOperationException("The outfit contains an invalid transformed vertex.");

                var (matched, triangle, barycentric, offset) = matches[i];
                if (!matched) continue;

                if (hints[i].Count >= 2)
                {
                    var hint = hints[i].Sum / hints[i].Count;
                    var local = deformation[triangle.A] * barycentric.x + deformation[triangle.B] * barycentric.y + deformation[triangle.C] * barycentric.z;
                    float mismatch = (local - hint).sqrMagnitude;
                    float variance = Mathf.Max(0, hints[i].Squares / hints[i].Count - hint.sqrMagnitude);
                    if (mismatch > Mathf.Max(variance * 4, largestDelta * largestDelta * .04f))
                    {
                        float range = Mathf.Min(radius, offset.magnitude + radius * .08f);
                        if (surface.FindClosest(point, range, out var face, out var weights, out var separation, deformation, hint, hints[i].Normal.normalized))
                        {
                            triangle = face;
                            barycentric = weights;
                            offset = separation;
                        }
                    }
                }
                float distance = offset.magnitude;
                float fade = 1f - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(radius * 0.45f, radius, distance));
                proximity[i] = fade;

                var da = deformation[triangle.A];
                var db = deformation[triangle.B];
                var dc = deformation[triangle.C];
                var displacement = da * barycentric.x + db * barycentric.y + dc * barycentric.z;
                if (body.Field != null)
                    displacement = body.Field.Sample(triangle, point - offset, displacement);
                float magnitude = displacement.magnitude;

                var ab = source[triangle.B] - source[triangle.A];
                var ac = source[triangle.C] - source[triangle.A];
                var oldNormal = Vector3.Cross(ab, ac).normalized;
                var newNormal = Vector3.Cross(ab + db - da, ac + dc - da).normalized;
                // Carry the offset with smooth normals so it does not jump where the matched face changes.
                var smoothOld = (body.RestNormals[triangle.A] * barycentric.x + body.RestNormals[triangle.B] * barycentric.y +
                    body.RestNormals[triangle.C] * barycentric.z).normalized;
                var smoothNew = (body.FittedNormals[triangle.A] * barycentric.x + body.FittedNormals[triangle.B] * barycentric.y +
                    body.FittedNormals[triangle.C] * barycentric.z).normalized;
                if (smoothOld == Vector3.zero || smoothNew == Vector3.zero || Vector3.Dot(smoothOld, oldNormal) < .5f)
                {
                    smoothOld = oldNormal;
                    smoothNew = newNormal;
                }

                // Rotate the normal separation with the body. Keep the tangential residual at edges
                // and fade the offset correction where the source deformation ends.
                float normalAgreement = Vector3.Dot(oldNormal, newNormal);
                restGap[i] = Vector3.Dot(offset, oldNormal);
                if (normalAgreement > 0.25f && distance > 0)
                {
                    float height = Vector3.Dot(offset, smoothOld);
                    float activity = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(0, largestDelta * 0.12f, magnitude));
                    var correction = (smoothNew - smoothOld) * height * activity;
                    displacement += Vector3.ClampMagnitude(correction, magnitude * 0.28f);
                }

                // Trust a contact plane when the offset is near-normal and the two faces agree.
                normals[i] = newNormal;
                float alignment = distance > largestDelta * 0.005f
                    ? Mathf.Abs(Vector3.Dot(offset, oldNormal)) / distance : 1f;
                confidence[i] = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.6f, .95f, alignment))
                    * Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.35f, .85f, normalAgreement))
                    * (1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(radius * .22f, radius * .7f, distance)));
                if (magnitude < largestDelta * 0.001f)
                    continue;
                deltas[i] = bodyToOutfit.MultiplyVector(displacement * fade);
                if (fade < 0.7f)
                    faded++;
                changed++;
            }

            if (changed == 0 && !allowStationary)
                throw new InvalidOperationException("No outfit vertices matched the deforming body surface. Check the outfit fit, rest pose, and scale.");

            var partners = SeamPartners(outfit, vertices);
            var islands = FindIslands(outfit, partners, out var closed);

            FitConnected(outfit, partners, vertices, deltas, proximity, normals, confidence, outfitToBody, largestDelta,
                restGap, body.FittedSurface);
            int compactDetails = PreserveCompactDetails(vertices, deltas, islands, closed, outfitToBody, largestDelta);
            warning = faded > 0
                ? $"Reduced transfer on {faded} distant vertices. Check thick or loose areas at 50 and 100."
                : $"Transferred {changed} vertices. Check the fit at 50 and 100.";
            if (compactDetails > 0)
                warning += $" Kept {compactDetails} small details intact.";
            if (!outfit.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Normal))
                warning += " The mesh has no usable base normals.";
            return deltas;
        }

        private static (Vector3 Sum, Vector3 Normal, float Squares, int Count)[] NeighborMotionHints(Mesh mesh, Vector3[] vertices, Matrix4x4 toBody, FitSource body,
            out (bool Matched, TriangleSurface.Triangle Face, Vector3 Weights, Vector3 Offset)[] matches)
        {
            matches = new (bool, TriangleSurface.Triangle, Vector3, Vector3)[vertices.Length];
            var hints = new (Vector3 Sum, Vector3 Normal, float Squares, int Count)[vertices.Length];
            var motion = new Vector3[vertices.Length];
            var faceNormals = new Vector3[vertices.Length];
            var nearest = matches;
            for (int i = 0; i < vertices.Length; i++)
            {
                var point = toBody.MultiplyPoint3x4(vertices[i]);
                if (!body.QueryRegion.Contains(point) || !body.OriginalSurface.FindClosest(point, body.Radius, out var face, out var weights, out var offset)) continue;
                nearest[i] = (true, face, weights, offset);
                faceNormals[i] = body.OriginalSurface.Normal(face);
                motion[i] = body.Deltas[face.A] * weights.x + body.Deltas[face.B] * weights.y + body.Deltas[face.C] * weights.z;
            }
            var edges = new HashSet<(int, int)>();
            for (int part = 0; part < mesh.subMeshCount; part++)
            {
                if (mesh.GetTopology(part) != MeshTopology.Triangles) continue;
                var indices = mesh.GetTriangles(part);
                for (int k = 0; k < indices.Length; k += 3)
                {
                    Edge(indices[k], indices[k+1]);
                    Edge(indices[k+1], indices[k+2]);
                    Edge(indices[k+2], indices[k]);
                }
            }
            return hints;

            void Edge(int a, int b)
            {
                if (!edges.Add(a < b ? (a,b) : (b,a))) return;
                if (nearest[a].Matched) Add(b, a);
                if (nearest[b].Matched) Add(a, b);
            }
            void Add(int i, int neighbor)
            {
                var value = motion[neighbor];
                hints[i].Normal += faceNormals[neighbor];
                hints[i].Sum += value;
                hints[i].Squares += value.sqrMagnitude;
                hints[i].Count++;
            }
        }

        internal static List<float> FrameWeights(Mesh mesh, string shape, bool reverse = false)
        {
            int index = mesh.GetBlendShapeIndex(shape);
            if (index < 0) throw new InvalidOperationException($"Missing source shape {shape}.");
            var weights = new List<float>();
            for (int frame = 0; frame < mesh.GetBlendShapeFrameCount(index); frame++)
            {
                float weight = mesh.GetBlendShapeFrameWeight(index, frame);
                if (!float.IsFinite(weight) || weight <= (frame == 0 ? 0 : weights[frame - 1]))
                    throw new InvalidOperationException("Expected ascending positive frame weights.");
                weights.Add(weight);
            }
            if (!weights.Contains(100f)) { weights.Add(100f); weights.Sort(); }
            if (reverse)
            {
                weights.RemoveAll(w => w >= 100);
                for (int i = 0; i < weights.Count; i++) weights[i] = 100 - weights[i];
                weights.Reverse();
                weights.Add(100);
            }
            return weights;
        }

        internal static Mesh BuildMesh(Mesh outfit, Vector3[] vertices, Vector3[] deltas, string shape = Shape)
            => BuildMesh(outfit, vertices, new[] { (100f, deltas) }, shape);

        internal static Mesh BuildMesh(Mesh outfit, Vector3[] vertices, IEnumerable<(float Weight, Vector3[] Motion)> frames, string shape, bool calculateShading = true)
        {
            var copy = UnityEngine.Object.Instantiate(outfit);
            try
            {
                copy.name = outfit.name + " - " + shape;
                var bounds = copy.bounds;
                foreach (var (weight, deltas) in frames)
                {
                    Vector3[] normalDeltas = null, tangentDeltas = null;
                    if (calculateShading)
                        CalculateShading(outfit, vertices, deltas, out normalDeltas, out tangentDeltas);
                    copy.AddBlendShapeFrame(shape, weight, deltas, normalDeltas, tangentDeltas);
                    for (int i = 0; i < vertices.Length; i++)
                        bounds.Encapsulate(vertices[i] + deltas[i]);
                }
                copy.bounds = bounds;
            }
            catch
            {
                UnityEngine.Object.DestroyImmediate(copy);
                throw;
            }
            return copy;
        }

        private static Bounds MovingRegion(Mesh body, Vector3[] points, Vector3[] motion)
        {
            var bounds = new Bounds();
            bool first = true;
            for (int section = 0; section < body.subMeshCount; section++)
            {
                if (body.GetTopology(section) != MeshTopology.Triangles)
                    continue;
                var indices = body.GetTriangles(section);
                for (int k = 0; k < indices.Length; k += 3)
                {
                    int a = indices[k], b = indices[k + 1], c = indices[k + 2];
                    if (motion[a].sqrMagnitude + motion[b].sqrMagnitude + motion[c].sqrMagnitude == 0)
                        continue;
                    if (first)
                    {
                        bounds.center = points[a];
                        first = false;
                    }
                    bounds.Encapsulate(points[a]);
                    bounds.Encapsulate(points[b]);
                    bounds.Encapsulate(points[c]);
                }
            }
            return bounds;
        }

        internal static int[] SeamPartners(Mesh mesh, Vector3[] vertices)
        {
            var parent = new int[vertices.Length];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;
            var edges = new Dictionary<(Vector3, Vector3), (int A, int B, Vector3 Normal, int Count)>();
            for (int part = 0; part < mesh.subMeshCount; part++)
            {
                if (mesh.GetTopology(part) != MeshTopology.Triangles) continue;
                var indices = mesh.GetTriangles(part);
                for (int k = 0; k < indices.Length; k += 3)
                {
                    int a = indices[k], b = indices[k + 1], c = indices[k + 2];
                    var normal = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]).normalized;
                    Count(a, b, normal);
                    Count(b, c, normal);
                    Count(c, a, normal);
                }
            }
            foreach (var item in edges)
            {
                var edge = item.Value;
                if (edge.Count != 1 || !edges.TryGetValue((item.Key.Item2, item.Key.Item1), out var other) ||
                    other.Count != 1 || Vector3.Dot(edge.Normal, other.Normal) < -.5f) continue;
                parent[Root(edge.A)] = Root(other.B);
                parent[Root(edge.B)] = Root(other.A);
            }
            for (int i = 0; i < parent.Length; i++) parent[i] = Root(i);
            return parent;

            int Root(int i)
            {
                while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; }
                return i;
            }
            void Count(int a, int b, Vector3 normal)
            {
                var key = (vertices[a], vertices[b]);
                edges.TryGetValue(key, out var prior);
                edges[key] = (a, b, normal, prior.Count + 1);
            }
        }

        private static int[] FindIslands(Mesh mesh, int[] weld, out bool[] closed)
        {
            var parents = (int[])weld.Clone();
            var incidence = new Dictionary<(int, int), int>();
            for (int section = 0; section < mesh.subMeshCount; section++)
            {
                if (mesh.GetTopology(section) != MeshTopology.Triangles)
                    continue;
                var triangles = mesh.GetTriangles(section);
                for (int i = 0; i < triangles.Length; i += 3)
                {
                    int a = weld[triangles[i]], b = weld[triangles[i + 1]], c = weld[triangles[i + 2]];
                    if (a == b || b == c || a == c)
                        continue;
                    CountEdge(a, b);
                    CountEdge(b, c);
                    CountEdge(c, a);
                    int first = Root(triangles[i]);
                    parents[Root(triangles[i + 1])] = first;
                    parents[Root(triangles[i + 2])] = first;
                }
            }

            for (int i = 0; i < parents.Length; i++)
                parents[i] = Root(i);
            closed = new bool[parents.Length];
            Array.Fill(closed, true);
            foreach (var edge in incidence)
                if (edge.Value != 2)
                    closed[parents[edge.Key.Item1]] = false;
            return parents;

            void CountEdge(int a, int b)
            {
                var key = a < b ? (a, b) : (b, a);
                incidence.TryGetValue(key, out int count);
                incidence[key] = count + 1;
            }

            int Root(int index)
            {
                while (parents[index] != index)
                {
                    parents[index] = parents[parents[index]];
                    index = parents[index];
                }
                return index;
            }
        }

        private static int PreserveCompactDetails(Vector3[] vertices, Vector3[] deltas, int[] islands, bool[] closed,
            Matrix4x4 outfitToBody, float scale)
        {
            var sections = new Dictionary<int, List<int>>();
            for (int i = 0; i < islands.Length; i++)
            {
                if (!sections.TryGetValue(islands[i], out var section))
                    sections.Add(islands[i], section = new List<int>());
                section.Add(i);
            }

            int adjusted = 0;
            foreach (var section in sections.Values)
            {
                if (section.Count < 8 || section.Count == vertices.Length || !closed[islands[section[0]]])
                    continue;

                var bounds = new Bounds(outfitToBody.MultiplyPoint3x4(vertices[section[0]]), Vector3.zero);
                var translation = Vector3.zero;
                var unique = new HashSet<Vector3>();
                foreach (int i in section)
                {
                    if (!unique.Add(vertices[i]))
                        continue;
                    bounds.Encapsulate(outfitToBody.MultiplyPoint3x4(vertices[i]));
                    translation += deltas[i];
                }

                // Compact disconnected rings, studs, and fasteners can move together.
                // Broad panels and long narrow straps keep their flexible fit.
                if (bounds.size.magnitude > scale * 0.76f ||
                    Mathf.Min(bounds.size.x, Mathf.Min(bounds.size.y, bounds.size.z)) < scale * 0.018f ||
                    unique.Count < 8)
                    continue;

                translation /= unique.Count;
                float variance = 0;
                foreach (int i in section)
                    variance = Mathf.Max(variance, (deltas[i] - translation).sqrMagnitude);
                if (variance <= 1e-12f)
                    continue;

                foreach (int i in section)
                    deltas[i] = translation;
                adjusted++;
            }
            return adjusted;
        }


        internal static Bounds ShapeBounds(SkinnedMeshRenderer body, string shape, BodyReference mode = BodyReference.Neutral)
        {
            var positions = ReferencePositions(body, shape, mode) ?? body.sharedMesh.vertices;
            var weights = FrameWeights(body.sharedMesh, shape);
            var bounds = new Bounds(body.bounds.center, Vector3.zero);
            bool first = true;
            foreach (float weight in weights)
            {
                var deltas = ReadSourceDeltas(body.sharedMesh, shape, weight);
                for (int i = 0; i < positions.Length; i++)
                {
                    if (deltas[i].sqrMagnitude <= 1e-8f)
                        continue;
                    var point = body.transform.TransformPoint(positions[i]);
                    if (first)
                    {
                        bounds.center = point;
                        first = false;
                    }
                    bounds.Encapsulate(point);
                }
            }
            return bounds;
        }

        internal static Func<SkinnedMeshRenderer, bool> ShapeOverlap(SkinnedMeshRenderer body, string shape, TransferMethod method = TransferMethod.Barycentric, BodyReference mode = BodyReference.Neutral)
        {
            var reference = ReferencePositions(body, shape, mode);
            bool reverse = mode == BodyReference.OtherActiveShapesAndTargetAt100;
            var sources = FrameWeights(body.sharedMesh, shape, reverse).ConvertAll(weight => new FitSource(body.sharedMesh, shape, method, weight, reference, reverse));
            var worldToBody = body.transform.worldToLocalMatrix;
            return outfit =>
            {
                var matrix = worldToBody * outfit.transform.localToWorldMatrix;
                var vertices = outfit.sharedMesh.vertices;
                foreach (var source in sources)
                {
                    if (source.Peak <= 1e-6f) continue;
                    var deltas = source.Deltas;
                    foreach (var vertex in vertices)
                    {
                        var point = matrix.MultiplyPoint3x4(vertex);
                        if (!source.QueryRegion.Contains(point) || !source.OriginalSurface.FindClosest(point, source.Radius,
                            out var triangle, out var barycentric, out var offset))
                            continue;

                        var displacement = deltas[triangle.A] * barycentric.x +
                            deltas[triangle.B] * barycentric.y + deltas[triangle.C] * barycentric.z;
                        if (source.Field != null)
                            displacement = source.Field.Sample(triangle, point - offset, displacement);
                        if (displacement.magnitude >= source.Peak * 0.001f)
                            return true;
                    }
                }
                return false;
            };
        }

        internal static Vector3[] ReadSourceDeltas(Mesh body, string shapeName = Shape, float weight = 100)
        {
            int shape = body.GetBlendShapeIndex(shapeName);
            if (shape < 0)
                throw new InvalidOperationException($"The body mesh is missing {shapeName}.");

            return ReadSourceDeltas(body, shape, new Vector3[body.vertexCount], new Vector3[body.vertexCount], weight);
        }

        private static Vector3[] ReadSourceDeltas(Mesh body, int shape, Vector3[] lower, Vector3[] upper, float weight = 100)
        {
            int frames = body.GetBlendShapeFrameCount(shape);
            float lowerWeight = 0;
            for (int frame = 0; frame < frames; frame++)
            {
                float upperWeight = body.GetBlendShapeFrameWeight(shape, frame);
                if (upperWeight <= lowerWeight || !float.IsFinite(upperWeight))
                    throw new InvalidOperationException("The source shape must have ascending positive frame weights.");

                body.GetBlendShapeFrameVertices(shape, frame, upper, null, null);
                if (upperWeight >= weight || frame == frames - 1)
                {
                    float t = (weight - lowerWeight) / (upperWeight - lowerWeight);
                    if (frame == 0)
                    {
                        if (t != 1f)
                            for (int i = 0; i < upper.Length; i++)
                                upper[i] *= t;
                    }
                    else
                    {
                        for (int i = 0; i < upper.Length; i++)
                            upper[i] = Vector3.LerpUnclamped(lower[i], upper[i], t);
                    }
                    return upper;
                }

                (lower, upper) = (upper, lower);
                lowerWeight = upperWeight;
            }
            throw new InvalidOperationException("The source blendshape has no frames.");
        }

        private static void FitConnected(Mesh mesh, int[] weld, Vector3[] positions, Vector3[] deltas,
            float[] proximity, Vector3[] normals, float[] confidence, Matrix4x4 toBody, float scale,
            float[] restGap, TriangleSurface smallBody)
        {
            // Solve on welded topology so hard shading edges and UV splits share motion.
            var neighbors = new List<(int Index, float Weight, Vector3 Direction)>[positions.Length];
            var totals = new float[positions.Length];
            var edges = new HashSet<(int, int)>();

            for (int section = 0; section < mesh.subMeshCount; section++)
            {
                if (mesh.GetTopology(section) != MeshTopology.Triangles)
                    continue;
                var indices = mesh.GetTriangles(section);
                for (int k = 0; k < indices.Length; k += 3)
                {
                    int a = weld[indices[k]], b = weld[indices[k + 1]], c = weld[indices[k + 2]];
                    AddEdge(a, b);
                    AddEdge(b, c);
                    AddEdge(c, a);
                }
            }

            var target = Array.ConvertAll(deltas, toBody.MultiplyVector);
            var spans = FindStraightSpans(Array.ConvertAll(positions, toBody.MultiplyVector), neighbors, proximity, scale);
            var current = (Vector3[])target.Clone();
            var next = (Vector3[])target.Clone();
            // Relax displacement rather than positions to retain the rest shape of folds.
            // Contacts constrain clearance more strongly than tangential slip.
            for (int pass = 0; pass < 32; pass++)
            {
                float largestChange = 0;
                for (int i = 0; i < current.Length; i++)
                {
                    if (neighbors[i] == null || proximity[i] <= 0)
                        continue;
                    var mean = Vector3.zero;
                    foreach (var edge in neighbors[i])
                        mean += current[edge.Index] * edge.Weight;
                    mean /= totals[i];
                    var residual = target[i] - mean;
                    float contact = target[i].sqrMagnitude > 0 ? confidence[i] : 0;
                    var motion = mean + residual * Mathf.Lerp(.015f, .060f, contact);
                    motion += normals[i] * (Vector3.Dot(residual, normals[i]) * contact * .14f);
                    // A straight run can span empty space. Its two ends constrain bending
                    // even when its midpoint projects onto a different part of the body.
                    var span = spans[i];
                    if (span.Strength > 0)
                    {
                        var alongSpan = Vector3.Lerp(current[span.A], current[span.B], span.T);
                        motion = Vector3.Lerp(motion, alongSpan, span.Strength * Mathf.Lerp(.65f, .10f, contact));
                    }
                    next[i] = Vector3.Lerp(target[i], motion, proximity[i]);
                    largestChange = Mathf.Max(largestChange, (next[i] - current[i]).sqrMagnitude);
                }
                (next, current) = (current, next);
                if (largestChange < scale * scale * 1e-8f)
                    break;
            }

            // Retain the authored deviation from the endpoint chord on well-supported runs.
            Array.Copy(current, next, current.Length);
            for (int i = 0; i < spans.Length; i++)
            {
                var span = spans[i];
                if (span.Strength > 0)
                    next[i] = Vector3.Lerp(current[i], Vector3.Lerp(current[span.A], current[span.B], span.T),
                        span.Strength * Mathf.Lerp(1f, .35f, confidence[i]));
            }
            (current, next) = (next, current);

            // Filter the final motion over neighboring triangles. This removes isolated normal
            // corrections on glossy surfaces without averaging the sculpted vertices.
            for (int pass = 0; pass < 6; pass++)
            {
                for (int i = 0; i < current.Length; i++)
                {
                    if (neighbors[i] == null || proximity[i] <= 0)
                        continue;
                    var mean = Vector3.zero;
                    foreach (var edge in neighbors[i])
                        mean += current[edge.Index] * edge.Weight;
                    next[i] = Vector3.Lerp(current[i], mean / totals[i], .55f);
                }
                (current, next) = (next, current);
            }
            // A poorly supported bridge may stretch despite smooth displacement. Keep its anchored side fixed.
            Array.Copy(current, next, current.Length);
            for (int i = 0; i < current.Length; i++)
            {
                if (neighbors[i] == null || proximity[i] <= 0 || confidence[i] >= .5f) continue;
                var strainShift = Vector3.zero;
                int count = 0;
                foreach (var edge in neighbors[i])
                {
                    int j = edge.Index;
                    if (confidence[j] <= confidence[i] + .25f) continue;
                    var rest = toBody.MultiplyVector(positions[i] - positions[j]);
                    var adjustment = LimitEdgeStrain(rest, current[i] - current[j]);
                    if (adjustment.sqrMagnitude <= 1e-16f) continue;
                    strainShift += adjustment;
                    count++;
                }
                if (count > 0)
                    next[i] += Vector3.ClampMagnitude(strainShift / count, scale * .05f);
            }
            (current, next) = (next, current);
            // Check the actual shrunken surface after the connected solve. Its nearest
            // face can differ from the original match, especially beneath a fold.
            // Smoothing flattens crowns, so re-project several times. Each pass measures the remaining
            // gap error from the updated positions and shrinks it.
            var inverse = toBody.inverse;
            var collision = new Vector3[current.Length];
            var correction = new Vector3[current.Length];
            for (int iteration = 0; iteration < 8; iteration++)
            {
            Array.Clear(collision, 0, collision.Length);
            Array.Clear(correction, 0, correction.Length);
            float worst = 0;
            for (int i = 0; i < current.Length; i++)
            {
                if (weld[i] != i || confidence[i] <= 0 || current[i].sqrMagnitude == 0)
                    continue;
                var point = toBody.MultiplyPoint3x4(positions[i]) + current[i];
                if (!smallBody.FindClosest(point, scale, out var face, out _, out var offset))
                    continue;
                var normal = smallBody.Normal(face);
                float agreement = Vector3.Dot(normals[i], normal);
                if (agreement <= .6f)
                    continue;
                float gap = Vector3.Dot(offset, normal);
                float error = restGap[i] - gap;
                // Preserve pre-existing gaps and penetration. Allow more outward travel
                // for collisions than inward travel for floating fabric.
                float amount = Mathf.Clamp(error, -scale * .10f, scale * .35f);
                worst = Mathf.Max(worst, Mathf.Abs(amount) * confidence[i]);
                if (gap < 0 && restGap[i] >= 0 && confidence[i] > .35f)
                    collision[i] = normal * Mathf.Clamp(error, 0, scale * .18f);
                correction[i] = normal * (amount * confidence[i] * (1f - spans[i].Strength) * Mathf.InverseLerp(.6f, .9f, agreement));
            }
            // Average the correction over cloth edges to avoid a crease at a face switch.
            for (int i = 0; i < current.Length; i++)
            {
                if (neighbors[i] == null || proximity[i] <= 0)
                    continue;
                var mean = Vector3.zero;
                foreach (var edge in neighbors[i])
                    mean += correction[edge.Index] * edge.Weight;
                var shift = Vector3.Lerp(correction[i], mean / totals[i], .65f);
                if (collision[i].sqrMagnitude > 0)
                {
                    var outward = collision[i].normalized;
                    shift += outward * Mathf.Max(0, Vector3.Dot(collision[i] - shift, outward));
                }
                current[i] += shift;
            }
            if (worst < scale * 1e-3f)
                break;
            }
            for (int i = 0; i < deltas.Length; i++)
                deltas[i] = inverse.MultiplyVector(current[weld[i]]);

            void AddEdge(int a, int b)
            {
                if (a == b || (proximity[a] <= 0 && proximity[b] <= 0) ||
                    !edges.Add(a < b ? (a, b) : (b, a)))
                    return;
                var direction = toBody.MultiplyVector(positions[b] - positions[a]);
                float length = direction.magnitude;
                direction = length > 0 ? direction / length : Vector3.zero;
                float weight = 1f / Mathf.Max(length, scale * .06f);
                (neighbors[a] ??= new List<(int, float, Vector3)>()).Add((b, weight, direction));
                (neighbors[b] ??= new List<(int, float, Vector3)>()).Add((a, weight, -direction));
                totals[a] += weight;
                totals[b] += weight;
            }
        }

        internal static Vector3 LimitEdgeStrain(Vector3 rest, Vector3 relativeMotion)
        {
            var edge = rest + relativeMotion;
            float length = edge.magnitude;
            float original = rest.magnitude;
            if (length <= 1e-8f || original <= 1e-8f) return Vector3.zero;
            return edge * (Mathf.Clamp(length, original * .75f, original * 1.25f) / length - 1f);
        }

        private static (int A, int B, float T, float Strength)[] FindStraightSpans(Vector3[] rest,
            List<(int Index, float Weight, Vector3 Direction)>[] neighbors, float[] proximity, float scale)
        {
            var spans = new (int A, int B, float T, float Strength)[rest.Length];
            for (int i = 0; i < spans.Length; i++)
            {
                if (neighbors[i] == null || proximity[i] <= 0)
                    continue;
                float longest = scale * .65f;
                foreach (var edge in neighbors[i])
                {
                    var axis = edge.Direction;
                    int b = Trace(i, -axis);
                    if (b == i)
                        continue;
                    int a = Trace(i, axis);
                    if (a == i)
                        continue;
                    float left = Vector3.Dot(rest[a] - rest[i], axis);
                    float right = Vector3.Dot(rest[i] - rest[b], axis);
                    float length = left + right;
                    if (length <= longest)
                        continue;
                    longest = length;
                    float support = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(scale * .03f, scale * .28f, Mathf.Min(left, right)));
                    spans[i] = (a, b, left / length, support);
                }
            }

            return spans;

            int Trace(int start, Vector3 axis)
            {
                int cursor = start;
                // Monotone projection prevents cycles, including on closed bands.
                while (neighbors[cursor] != null)
                {
                    int best = cursor;
                    float alignment = .96f;
                    foreach (var edge in neighbors[cursor])
                    {
                        float dot = Vector3.Dot(edge.Direction, axis);
                        if (dot <= alignment)
                            continue;
                        var fromStart = rest[edge.Index] - rest[start];
                        if (fromStart.sqrMagnitude > scale * scale * 3f ||
                            Vector3.Cross(fromStart, axis).sqrMagnitude > scale * scale * .0025f)
                            continue;
                        alignment = dot;
                        best = edge.Index;
                    }
                    if (best == cursor)
                        break;
                    cursor = best;
                }
                return cursor;
            }
        }

        private static void CalculateShading(Mesh mesh, Vector3[] positions, Vector3[] deltas,
            out Vector3[] normalDeltas, out Vector3[] tangentDeltas)
        {
            normalDeltas = null;
            tangentDeltas = null;
            var normals = mesh.normals;
            if (normals.Length != positions.Length)
                return;

            var tangents = mesh.tangents;
            normalDeltas = new Vector3[positions.Length];
            if (tangents.Length == positions.Length)
                tangentDeltas = new Vector3[positions.Length];

            // Average across coincident UV splits only when the imported normals also agree.
            var groups = new Dictionary<(Vector3, Vector3), int>();
            var groupAtVertex = new int[positions.Length];
            for (int i = 0; i < positions.Length; i++)
            {
                var key = (positions[i], normals[i]);
                if (!groups.TryGetValue(key, out int group))
                {
                    group = groups.Count;
                    groups.Add(key, group);
                }
                groupAtVertex[i] = group;
            }

            var uv = mesh.uv;
            bool transportTangents = tangentDeltas != null && uv.Length == positions.Length;
            var oldU = transportTangents ? new Vector3[positions.Length] : null;
            var newU = transportTangents ? new Vector3[positions.Length] : null;
            var before = new Vector3[groups.Count];
            var after = new Vector3[groups.Count];
            for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
            {
                if (mesh.GetTopology(submesh) != MeshTopology.Triangles)
                    continue;
                var indices = mesh.GetTriangles(submesh);
                for (int i = 0; i < indices.Length; i += 3)
                {
                    int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                    var ab = positions[b] - positions[a];
                    var ac = positions[c] - positions[a];
                    var oldFace = Vector3.Cross(ab, ac);
                    var newFace = Vector3.Cross(ab + deltas[b] - deltas[a], ac + deltas[c] - deltas[a]);
                    if (transportTangents)
                    {
                        var uvB = uv[b] - uv[a];
                        var uvC = uv[c] - uv[a];
                        float det = uvB.x * uvC.y - uvC.x * uvB.y;
                        if (Mathf.Abs(det) > 1e-10f)
                        {
                            var rest = ((ab * uvC.y - ac * uvB.y) / det).normalized * oldFace.magnitude;
                            var moved = (((ab + deltas[b] - deltas[a]) * uvC.y - (ac + deltas[c] - deltas[a]) * uvB.y) / det).normalized * oldFace.magnitude;
                            oldU[a] += rest; oldU[b] += rest; oldU[c] += rest;
                            newU[a] += moved; newU[b] += moved; newU[c] += moved;
                        }
                    }
                    before[groupAtVertex[a]] += oldFace;
                    before[groupAtVertex[b]] += oldFace;
                    before[groupAtVertex[c]] += oldFace;
                    after[groupAtVertex[a]] += newFace;
                    after[groupAtVertex[b]] += newFace;
                    after[groupAtVertex[c]] += newFace;
                }
            }

            for (int i = 0; i < positions.Length; i++)
            {
                var from = before[groupAtVertex[i]];
                var to = after[groupAtVertex[i]];
                if (from.sqrMagnitude < 1e-20f || to.sqrMagnitude < 1e-20f)
                    continue;

                var a = from / from.magnitude;
                var b = to / to.magnitude;
                if (Vector3.Dot(a, b) <= 0)
                    continue;

                var rotation = Quaternion.FromToRotation(a, b);
                normalDeltas[i] = rotation * normals[i] - normals[i];
                if (tangentDeltas != null)
                {
                    Vector3 tangent = tangents[i];
                    var moved = rotation * tangent;
                    if (transportTangents)
                    {
                        var normal = rotation * normals[i];
                        var restDir = Vector3.ProjectOnPlane(rotation * oldU[i], normal);
                        var movedDir = Vector3.ProjectOnPlane(newU[i], normal);
                        if (restDir.sqrMagnitude > 1e-20f && movedDir.sqrMagnitude > 1e-20f)
                            moved = Quaternion.AngleAxis(Vector3.SignedAngle(restDir, movedDir, normal), normal) * moved;
                    }
                    tangentDeltas[i] = moved - tangent;
                }
            }
        }

        internal sealed class TriangleSurface
        {
            internal readonly struct Triangle
            {
                internal readonly int A, B, C;

                internal Triangle(int a, int b, int c)
                {
                    A = a;
                    B = b;
                    C = c;
                }
            }

            private struct Node
            {
                internal Bounds Bounds;
                internal int Start, Count, Left, Right;
            }

            private Vector3[] positions;
            private readonly Triangle[] triangles;
            private readonly Vector3[] centers;
            private readonly List<Node> nodes = new();

            internal TriangleSurface(Mesh mesh, Vector3[] positions, Bounds? region = null)
            {
                this.positions = positions;
                var faces = new List<Triangle>();
                var centroids = new List<Vector3>();
                for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
                {
                    if (mesh.GetTopology(submesh) != MeshTopology.Triangles)
                        continue;
                    var indices = mesh.GetTriangles(submesh);
                    for (int i = 0; i < indices.Length; i += 3)
                    {
                        int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                        if (region.HasValue)
                        {
                            var faceBounds = new Bounds(positions[a], Vector3.zero);
                            faceBounds.Encapsulate(positions[b]);
                            faceBounds.Encapsulate(positions[c]);
                            if (!region.Value.Intersects(faceBounds))
                                continue;
                        }
                        if (Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]).sqrMagnitude < 1e-22f)
                            continue;
                        faces.Add(new Triangle(a, b, c));
                        centroids.Add((positions[a] + positions[b] + positions[c]) / 3f);
                    }
                }
                if (faces.Count == 0)
                    throw new InvalidOperationException("The source mesh has no usable triangles.");
                triangles = faces.ToArray();
                centers = centroids.ToArray();
                Build(0, triangles.Length);
            }

            private int Build(int start, int count)
            {
                var bounds = new Bounds(positions[triangles[start].A], Vector3.zero);
                var centroidBounds = new Bounds(centers[start], Vector3.zero);
                for (int i = start; i < start + count; i++)
                {
                    var face = triangles[i];
                    bounds.Encapsulate(positions[face.A]);
                    bounds.Encapsulate(positions[face.B]);
                    bounds.Encapsulate(positions[face.C]);
                    centroidBounds.Encapsulate(centers[i]);
                }

                int index = nodes.Count;
                nodes.Add(default);
                var node = new Node { Bounds = bounds, Start = start, Count = count };
                if (count > 12)
                {
                    var size = centroidBounds.size;
                    int axis = size.x >= size.y && size.x >= size.z ? 0 : size.y >= size.z ? 1 : 2;
                    // Partition once per level. Sorting each complete subtree dominated
                    // generation time even for small accessories.
                    float middle = centroidBounds.center[axis];
                    int split = start;
                    for (int i = start; i < start + count; i++)
                    {
                        if (centers[i][axis] >= middle)
                            continue;
                        (centers[i], centers[split]) = (centers[split], centers[i]);
                        (triangles[i], triangles[split]) = (triangles[split], triangles[i]);
                        split++;
                    }
                    if (split == start || split == start + count)
                        split = start + count / 2;
                    node.Count = 0;
                    node.Left = Build(start, split - start);
                    node.Right = Build(split, start + count - split);
                }
                nodes[index] = node;
                return index;
            }

            internal TriangleSurface(TriangleSurface topology, Vector3[] moved)
            {
                triangles = topology.triangles;
                nodes.AddRange(topology.nodes);
                Refit(moved);
            }

            internal void Refit(Vector3[] moved)
            {
                positions = moved;
                // Children were appended after their parent, so reverse order gives
                // their final bounds before the parent is updated.
                for (int i = nodes.Count - 1; i >= 0; i--)
                {
                    var node = nodes[i];
                    if (node.Count == 0)
                    {
                        node.Bounds = nodes[node.Left].Bounds;
                        node.Bounds.Encapsulate(nodes[node.Right].Bounds);
                    }
                    else
                    {
                        node.Bounds = new Bounds(moved[triangles[node.Start].A], Vector3.zero);
                        for (int k = node.Start; k < node.Start + node.Count; k++)
                        {
                            var face = triangles[k];
                            node.Bounds.Encapsulate(moved[face.A]);
                            node.Bounds.Encapsulate(moved[face.B]);
                            node.Bounds.Encapsulate(moved[face.C]);
                        }
                    }
                    nodes[i] = node;
                }
            }

            internal Vector3 Normal(Triangle face) =>
                Vector3.Cross(positions[face.B] - positions[face.A], positions[face.C] - positions[face.A]).normalized;

            internal bool Raycast(Ray ray, float limit, float winding, out Triangle triangle, out float distance)
            {
                distance = limit;
                triangle = default;
                return RaycastNode(0, ray, winding, ref triangle, ref distance);
            }

            private bool RaycastNode(int index, Ray ray, float winding, ref Triangle hit, ref float closest)
            {
                var node = nodes[index];
                if (!node.Bounds.IntersectRay(ray, out float entry) || entry >= closest)
                    return false;
                if (node.Count == 0)
                {
                    bool left = RaycastNode(node.Left, ray, winding, ref hit, ref closest);
                    return RaycastNode(node.Right, ray, winding, ref hit, ref closest) || left;
                }

                bool found = false;
                for (int i = node.Start; i < node.Start + node.Count; i++)
                {
                    var face = triangles[i];
                    var a = positions[face.A];
                    var ab = positions[face.B] - a;
                    var ac = positions[face.C] - a;
                    var cross = Vector3.Cross(ray.direction, ac);
                    float determinant = Vector3.Dot(ab, cross);
                    if (Mathf.Abs(determinant) < 1e-12f || determinant * winding < 0)
                        continue;
                    var offset = ray.origin - a;
                    float u = Vector3.Dot(offset, cross) / determinant;
                    var q = Vector3.Cross(offset, ab);
                    float v = Vector3.Dot(ray.direction, q) / determinant;
                    float t = Vector3.Dot(ac, q) / determinant;
                    if (u < 0 || v < 0 || u + v > 1 || t <= 0 || t >= closest)
                        continue;
                    closest = t;
                    hit = face;
                    found = true;
                }
                return found;
            }

            internal bool FindClosest(Vector3 point, float radius, out Triangle triangle,
                out Vector3 barycentric, out Vector3 offset, Vector3[] motion = null, Vector3 hint = default, Vector3 expectedNormal = default)
            {
                float best = radius * radius;
                int face = -1;
                barycentric = default;
                offset = default;
                Search(0, point, nodes[0].Bounds.SqrDistance(point), ref best, ref face, ref barycentric, ref offset, motion, hint, expectedNormal);
                triangle = face >= 0 ? triangles[face] : default;
                return face >= 0;
            }

            private void Search(int index, Vector3 point, float bound, ref float best,
                ref int face, ref Vector3 barycentric, ref Vector3 offset, Vector3[] motion, Vector3 hint, Vector3 expectedNormal)
            {
                if (bound >= best)
                    return;

                var node = nodes[index];
                if (node.Count == 0)
                {
                    int first = node.Left, second = node.Right;
                    float firstBound = nodes[first].Bounds.SqrDistance(point);
                    float secondBound = nodes[second].Bounds.SqrDistance(point);
                    if (firstBound > secondBound)
                    {
                        (first, second) = (second, first);
                        (firstBound, secondBound) = (secondBound, firstBound);
                    }
                    Search(first, point, firstBound, ref best, ref face, ref barycentric, ref offset, motion, hint, expectedNormal);
                    Search(second, point, secondBound, ref best, ref face, ref barycentric, ref offset, motion, hint, expectedNormal);
                    return;
                }

                for (int i = node.Start; i < node.Start + node.Count; i++)
                {
                    var triangle = triangles[i];
                    var a = positions[triangle.A];
                    var ab = positions[triangle.B] - a;
                    var ac = positions[triangle.C] - a;
                    var weights = ClosestBarycentric(point - a, ab, ac);
                    var residual = point - (a + ab * weights.y + ac * weights.z);
                    float distance = residual.sqrMagnitude;
                    if (motion != null)
                    {
                        var interpolated = motion[triangle.A] * weights.x + motion[triangle.B] * weights.y + motion[triangle.C] * weights.z;
                        float disagreement = (interpolated - hint).sqrMagnitude;
                        float orientation = Vector3.Dot(expectedNormal, Vector3.Cross(ab, ac).normalized);
                        distance += disagreement * (.25f + .125f * (1 - orientation));
                    }
                    if (!(distance < best))
                        continue;
                    best = distance;
                    face = i;
                    barycentric = weights;
                    offset = residual;
                }
            }

            // Test the vertex and edge Voronoi regions first, then use the interior projection.
            private static Vector3 ClosestBarycentric(Vector3 ap, Vector3 ab, Vector3 ac)
            {
                var bp = ap - ab;
                var cp = ap - ac;
                float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
                float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
                float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
                if (d1 <= 0 && d2 <= 0) return new Vector3(1, 0, 0);
                if (d3 >= 0 && d4 <= d3) return new Vector3(0, 1, 0);
                if (d6 >= 0 && d5 <= d6) return new Vector3(0, 0, 1);

                float vc = d1 * d4 - d3 * d2;
                if (vc <= 0 && d1 >= 0 && d3 <= 0)
                {
                    float v = d1 / (d1 - d3);
                    return new Vector3(1 - v, v, 0);
                }

                float vb = d5 * d2 - d1 * d6;
                if (vb <= 0 && d2 >= 0 && d6 <= 0)
                {
                    float w = d2 / (d2 - d6);
                    return new Vector3(1 - w, 0, w);
                }

                float va = d3 * d6 - d5 * d4;
                if (va <= 0 && d4 >= d3 && d5 >= d6)
                {
                    float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                    return new Vector3(0, 1 - w, w);
                }

                float inverse = 1 / (va + vb + vc);
                return new Vector3(va * inverse, vb * inverse, vc * inverse);
            }
        }
    }
}
