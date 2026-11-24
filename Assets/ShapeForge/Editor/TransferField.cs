using System;
using System.Collections.Generic;
using UnityEngine;

namespace ShapeForge
{
    internal enum TransferMethod { Barycentric, Weighted, RBF }

    // Estimate motion on the contacted body patch. The downstream fit and clearance
    // solve are shared so method comparisons only change the starting displacement.
    internal sealed class TransferField
    {
        private const int SampleLimit = 12;
        private readonly Vector3[] positions, deltas;
        private readonly List<int>[] adjacent;
        private readonly TransferMethod method;
        private readonly Dictionary<(int, int, int), Patch> patches = new();

        private sealed class Patch
        {
            internal int[] Indices;
            internal Vector3[] Coefficients;
            internal Vector3 Mean, Min, Max;
            internal float ScaleSquared;
        }

        internal TransferField(Mesh mesh, Vector3[] positions, Vector3[] deltas, TransferMethod method)
        {
            this.positions = positions;
            this.deltas = deltas;
            this.method = method;
            adjacent = new List<int>[positions.Length];
            var edges = new HashSet<(int, int)>();
            for (int part = 0; part < mesh.subMeshCount; part++)
            {
                if (mesh.GetTopology(part) != MeshTopology.Triangles)
                    continue;
                var indices = mesh.GetTriangles(part);
                for (int i = 0; i < indices.Length; i += 3)
                {
                    Add(indices[i], indices[i + 1]);
                    Add(indices[i + 1], indices[i + 2]);
                    Add(indices[i + 2], indices[i]);
                }
            }

            void Add(int a, int b)
            {
                if (a == b || !edges.Add(a < b ? (a, b) : (b, a)))
                    return;
                (adjacent[a] ??= new List<int>()).Add(b);
                (adjacent[b] ??= new List<int>()).Add(a);
            }
        }

        internal Vector3 Sample(ShapeTransfer.TriangleSurface.Triangle face, Vector3 point, Vector3 original)
        {
            var key = (face.A, face.B, face.C);
            if (!patches.TryGetValue(key, out var patch))
                patches.Add(key, patch = BuildPatch(face));
            if (patch.Indices.Length < 3)
                return original;

            Vector3 motion = Vector3.zero;
            if (patch.Coefficients != null)
            {
                motion = patch.Mean;
                for (int i = 0; i < patch.Indices.Length; i++)
                    motion += patch.Coefficients[i] * Kernel(positions[patch.Indices[i]], point, patch.ScaleSquared);
                // Keep an extrapolated result within the displacement range supported by this patch.
                motion = Vector3.Max(patch.Min, Vector3.Min(patch.Max, motion));
            }
            else if (method == TransferMethod.Weighted)
            {
                float sum = 0;
                foreach (int index in patch.Indices)
                {
                    float weight = 1f / ((positions[index] - point).sqrMagnitude + patch.ScaleSquared * .04f);
                    motion += deltas[index] * weight;
                    sum += weight;
                }
                motion /= sum;
            }
            else
                return original;

            return float.IsFinite(motion.sqrMagnitude) ? Vector3.Lerp(original, motion, .7f) : original;
        }

        private Patch BuildPatch(ShapeTransfer.TriangleSurface.Triangle face)
        {
            var indices = new List<int>(SampleLimit);
            var locations = new HashSet<Vector3>();
            var center = (positions[face.A] + positions[face.B] + positions[face.C]) / 3f;
            float faceSize = Mathf.Max((positions[face.A] - center).sqrMagnitude,
                Mathf.Max((positions[face.B] - center).sqrMagnitude, (positions[face.C] - center).sqrMagnitude));
            float reachSquared = faceSize * 16f;
            Add(face.A);
            Add(face.B);
            Add(face.C);
            for (int i = 0; i < indices.Count && indices.Count < SampleLimit; i++)
            {
                var neighbors = adjacent[indices[i]];
                if (neighbors == null)
                    continue;
                foreach (int neighbor in neighbors)
                    if ((positions[neighbor] - center).sqrMagnitude <= reachSquared)
                        Add(neighbor);
            }

            var patch = new Patch
            {
                Indices = indices.ToArray(),
                Min = deltas[face.A],
                Max = deltas[face.A],
                ScaleSquared = Mathf.Max(faceSize * 4f, 1e-12f)
            };
            foreach (int index in indices)
            {
                var delta = deltas[index];
                patch.Min = Vector3.Min(patch.Min, delta);
                patch.Max = Vector3.Max(patch.Max, delta);
                patch.Mean += delta;
            }
            if (indices.Count > 0)
                patch.Mean /= indices.Count;
            if (method == TransferMethod.RBF && indices.Count >= 3)
                patch.Coefficients = Solve(patch);
            return patch;

            void Add(int vertex)
            {
                if (indices.Count < SampleLimit && locations.Add(positions[vertex]))
                    indices.Add(vertex);
            }
        }

        private Vector3[] Solve(Patch patch)
        {
            int count = patch.Indices.Length;
            var matrix = new float[count, count];
            var values = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                int index = patch.Indices[i];
                values[i] = deltas[index] - patch.Mean;
                for (int j = 0; j < count; j++)
                    matrix[i, j] = Kernel(positions[index], positions[patch.Indices[j]], patch.ScaleSquared);
                matrix[i, i] += .015f;
            }

            for (int column = 0; column < count; column++)
            {
                int pivot = column;
                for (int row = column + 1; row < count; row++)
                    if (Mathf.Abs(matrix[row, column]) > Mathf.Abs(matrix[pivot, column]))
                        pivot = row;
                if (Mathf.Abs(matrix[pivot, column]) < 1e-6f)
                    return null;
                if (pivot != column)
                {
                    for (int j = column; j < count; j++)
                        (matrix[pivot, j], matrix[column, j]) = (matrix[column, j], matrix[pivot, j]);
                    (values[pivot], values[column]) = (values[column], values[pivot]);
                }
                for (int row = column + 1; row < count; row++)
                {
                    float factor = matrix[row, column] / matrix[column, column];
                    for (int j = column + 1; j < count; j++)
                        matrix[row, j] -= factor * matrix[column, j];
                    values[row] -= values[column] * factor;
                }
            }
            for (int i = count - 1; i >= 0; i--)
            {
                for (int j = i + 1; j < count; j++)
                    values[i] -= values[j] * matrix[i, j];
                values[i] /= matrix[i, i];
            }
            return values;
        }

        private static float Kernel(Vector3 a, Vector3 b, float scaleSquared) =>
            Mathf.Exp(-(a - b).sqrMagnitude / scaleSquared);
    }
}
