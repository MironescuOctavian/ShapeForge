using UnityEngine;

namespace ShapeForge
{
    internal static class PreviewBrushGeometry
    {
        internal static Vector3 RelaxShift(Vector3 point, Vector3 neighborCenter, Vector3 normal, float limit, float strength)
        {
            var delta = neighborCenter - point;
            var tangent = Vector3.ProjectOnPlane(delta, normal);
            // Mostly redistribute along the surface, with a small amount of normal smoothing for creases.
            return Vector3.ClampMagnitude(tangent + (delta - tangent) * .12f, limit) * strength;
        }

        internal static bool AlignPatch(Vector3[] original, Vector3[] current, float[] weights,
            out Quaternion rotation, out Vector3 offset)
        {
            rotation = Quaternion.identity;
            offset = Vector3.zero;
            var fromCenter = Vector3.zero;
            var toCenter = Vector3.zero;
            float sum = 0;
            for (int i = 0; i < weights.Length; i++)
            {
                fromCenter += original[i] * weights[i];
                toCenter += current[i] * weights[i];
                sum += weights[i];
            }
            if (sum <= 1e-6f) return false;
            fromCenter /= sum;
            toCenter /= sum;

            // Use corresponding wide apart points to define a local frame in both shapes.
            int axis = -1, side = -1;
            float best = 1e-12f;
            for (int i = 0; i < weights.Length; i++)
            {
                float score = Mathf.Min((original[i] - fromCenter).sqrMagnitude,
                    (current[i] - toCenter).sqrMagnitude) * weights[i];
                if (score <= best) continue;
                best = score;
                axis = i;
            }
            if (axis < 0) return false;
            var fromAxis = (original[axis] - fromCenter).normalized;
            var toAxis = (current[axis] - toCenter).normalized;
            best = 1e-12f;
            for (int i = 0; i < weights.Length; i++)
            {
                float score = Mathf.Min(Vector3.Cross(fromAxis, original[i] - fromCenter).sqrMagnitude,
                    Vector3.Cross(toAxis, current[i] - toCenter).sqrMagnitude) * weights[i];
                if (score <= best) continue;
                best = score;
                side = i;
            }
            if (side < 0) return false;
            var fromUp = Vector3.ProjectOnPlane(original[side] - fromCenter, fromAxis);
            var toUp = Vector3.ProjectOnPlane(current[side] - toCenter, toAxis);
            rotation = Quaternion.LookRotation(toAxis, toUp) * Quaternion.Inverse(Quaternion.LookRotation(fromAxis, fromUp));
            offset = toCenter - rotation * fromCenter;
            return true;
        }
    }
}
