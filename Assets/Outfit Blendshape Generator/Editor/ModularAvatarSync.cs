using System;
using UnityEditor;
using UnityEngine;

namespace MilltinaBreastGenerator
{
    internal static class ModularAvatarSync
    {
        private static readonly Type ComponentType = Type.GetType(
            "nadena.dev.modular_avatar.core.ModularAvatarBlendshapeSync, nadena.dev.modular-avatar.core");

        internal static bool Available => ComponentType != null;

        internal static SyncChange Plan(SkinnedMeshRenderer outfit, SkinnedMeshRenderer body,
            Transform root, out string bodyPath, out string status, string shape)
        {
            bodyPath = null;
            if (!Available)
            {
                status = "Modular Avatar is unavailable. Set the outfit shape manually to check the fit.";
                return SyncChange.None;
            }

            if (!root || body.GetComponents<SkinnedMeshRenderer>().Length != 1 ||
                outfit.GetComponents<SkinnedMeshRenderer>().Length != 1)
            {
                status = "MA synchronization needs both renderers under the same VRChat avatar and one renderer per object.";
                return SyncChange.None;
            }

            bodyPath = root == body.transform
                ? "$$$AVATAR_ROOT$$$"
                : AnimationUtility.CalculateTransformPath(body.transform, root);
            var component = outfit.GetComponent(ComponentType);
            if (!component)
            {
                status = "Added MA Blendshape Sync.";
                return SyncChange.AddedComponent;
            }

            var bindings = Bindings(component);
            for (int i = 0; i < bindings.arraySize; i++)
            {
                var binding = bindings.GetArrayElementAtIndex(i);
                if (!TargetsShape(binding, shape))
                    continue;

                status = Matches(binding, body.gameObject, bodyPath, shape)
                    ? "Using the existing MA Blendshape Sync binding."
                    : "An existing MA binding targets this shape. Review it in the Inspector to sync the slider.";
                return SyncChange.None;
            }

            status = "Added a binding to the existing MA Blendshape Sync.";
            return SyncChange.AddedBinding;
        }

        internal static void Apply(SkinnedMeshRenderer outfit, SkinnedMeshRenderer body,
            SyncChange change, string bodyPath, string shape)
        {
            if (change == SyncChange.None)
                return;

            var component = change == SyncChange.AddedComponent
                ? Undo.AddComponent(outfit.gameObject, ComponentType)
                : outfit.GetComponent(ComponentType);
            var bindings = Bindings(component);
            int index = bindings.arraySize++;
            var binding = bindings.GetArrayElementAtIndex(index);
            binding.FindPropertyRelative("Blendshape").stringValue = shape;
            binding.FindPropertyRelative("LocalBlendshape").stringValue = shape;
            var reference = binding.FindPropertyRelative("ReferenceMesh");
            reference.FindPropertyRelative("referencePath").stringValue = bodyPath;
            var target = reference.FindPropertyRelative("targetObject");
            if (target != null)
                target.objectReferenceValue = body.gameObject;

            var curve = binding.FindPropertyRelative("RemapCurve");
            if (curve != null)
                curve.animationCurveValue = AnimationCurve.Linear(0, 0, 100, 100);
            var valid = binding.FindPropertyRelative("RemapCurveIsValid");
            if (valid != null)
                valid.boolValue = true;

            bindings.serializedObject.ApplyModifiedProperties();
            PrefabUtility.RecordPrefabInstancePropertyModifications(component);
        }

        internal static bool RemoveOwned(SkinnedMeshRenderer outfit, GeneratedShape info)
        {
            if (!Available || info.syncChange == SyncChange.None)
                return true;
            var component = outfit.GetComponent(ComponentType);
            if (!component)
                return true;

            var bindings = Bindings(component);
            int owned = -1;
            for (int i = 0; i < bindings.arraySize; i++)
            {
                var binding = bindings.GetArrayElementAtIndex(i);
                if (!TargetsShape(binding, info.name))
                    continue;
                if (!Matches(binding, null, info.bodyPath, info.name) || owned >= 0)
                    return false;
                owned = i;
            }

            if (owned < 0)
                return true;
            if (info.syncChange == SyncChange.AddedComponent && bindings.arraySize == 1 &&
                (component as Behaviour).enabled)
            {
                Undo.DestroyObjectImmediate(component);
                return true;
            }

            bindings.DeleteArrayElementAtIndex(owned);
            bindings.serializedObject.ApplyModifiedProperties();
            PrefabUtility.RecordPrefabInstancePropertyModifications(component);
            return true;
        }

        private static SerializedProperty Bindings(Component component)
        {
            var bindings = new SerializedObject(component).FindProperty("Bindings");
            if (bindings == null || !bindings.isArray)
                throw new InvalidOperationException("This Modular Avatar version has an unsupported Blendshape Sync layout.");
            return bindings;
        }

        private static bool TargetsShape(SerializedProperty binding, string shape)
        {
            var local = binding.FindPropertyRelative("LocalBlendshape").stringValue;
            return (string.IsNullOrWhiteSpace(local) ? binding.FindPropertyRelative("Blendshape").stringValue : local)
                == shape;
        }

        private static bool Matches(SerializedProperty binding, GameObject body, string bodyPath, string shape)
        {
            if (binding.FindPropertyRelative("Blendshape").stringValue != shape)
                return false;

            var reference = binding.FindPropertyRelative("ReferenceMesh");
            var path = reference.FindPropertyRelative("referencePath").stringValue;
            var target = reference.FindPropertyRelative("targetObject")?.objectReferenceValue;
            if (body && target ? target != body : path != bodyPath)
                return false;

            var curve = binding.FindPropertyRelative("RemapCurve")?.animationCurveValue;
            return curve == null || curve.length < 2 ||
                (curve.length == 2 && curve[0].time == 0 && curve[0].value == 0 &&
                 curve[1].time == 100 && curve[1].value == 100);
        }
    }
}
