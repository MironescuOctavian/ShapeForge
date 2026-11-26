using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ShapeForge
{
    internal static class GeneratorAssets
    {
        private const string GeneratorScriptGuid = "c3cccc9ef0d46c34daacb7c11dad5fe0";
        private const string DescriptorType = "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor";

        internal static Transform AvatarRoot(SkinnedMeshRenderer renderer) =>
            AvatarRoot(renderer ? renderer.transform : null);

        internal static Transform AvatarRoot(Transform child)
        {
            for (var node = child; node; node = node.parent)
            {
                foreach (var component in node.GetComponents<MonoBehaviour>())
                {
                    if (component && component.GetType().FullName == DescriptorType)
                        return node;
                }
            }
            return null;
        }

        internal static SkinnedMeshRenderer FindBody(SkinnedMeshRenderer outfit, Transform root)
        {
            var scope = root ? root : outfit ? outfit.transform.root : null;
            var renderers = scope
                ? scope.GetComponentsInChildren<SkinnedMeshRenderer>(false)
                : StageUtility.GetCurrentStageHandle().FindComponentsOfType<SkinnedMeshRenderer>();

            // A renderer recognised as a body by name and shape count wins. Otherwise accept the renderer carrying clearly
            // the most blendshapes, which is the body on a typical avatar.
            SkinnedMeshRenderer named = null, richest = null;
            bool ambiguousName = false;
            int most = 0, second = 0;
            foreach (var renderer in renderers)
            {
                if (!renderer.gameObject.activeInHierarchy || renderer == outfit ||
                    !renderer.sharedMesh || renderer.sharedMesh.blendShapeCount == 0)
                    continue;
                if (scope && AvatarRoot(renderer) != root)
                    continue;
                if (IsBodyRenderer(renderer))
                {
                    ambiguousName |= named;
                    named = renderer;
                }
                int count = renderer.sharedMesh.blendShapeCount;
                if (count > most) { second = most; most = count; richest = renderer; }
                else if (count > second) second = count;
            }
            if (named || ambiguousName)
                return ambiguousName ? null : named;
            return most >= FallbackBodyShapeCount && most >= second * 2 ? richest : null;
        }

        private const int FallbackBodyShapeCount = 20;

        // A mesh named like "Avatar_body" with many blendshapes is recognised as the avatar body without further evidence.
        internal static bool IsBodyRenderer(SkinnedMeshRenderer renderer) =>
            renderer && renderer.sharedMesh && renderer.sharedMesh.blendShapeCount >= FallbackBodyShapeCount &&
            renderer.name.EndsWith("_body", StringComparison.OrdinalIgnoreCase);

        internal static string Validate(SkinnedMeshRenderer outfit, SkinnedMeshRenderer body, Transform root, string shape)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return "Exit Play Mode before generating a mesh.";
            if (!outfit || !outfit.sharedMesh)
                return "Select an outfit SkinnedMeshRenderer with a mesh.";
            if (EditorUtility.IsPersistent(outfit) || !outfit.gameObject.scene.IsValid())
                return "Select a renderer in the Hierarchy or open an editable prefab.";
            if (outfit.sharedMesh.GetBlendShapeIndex(shape) >= 0)
            {
                var info = GetInfo(outfit.sharedMesh);
                if (!info || !Array.Exists(info.Entries, entry => entry.name == shape))
                    return "The outfit already has this blendshape.";
                return info.original
                    ? "The outfit already uses a generated mesh. Restore the original to generate again."
                    : "The original mesh asset for this generated mesh is missing.";
            }
            if (!body)
                return "Select the avatar body that contains the source blendshape.";
            if (body == outfit || !body.sharedMesh || body.sharedMesh.GetBlendShapeIndex(shape) < 0)
                return $"The body must be a separate renderer with the {shape} blendshape.";
            if (body.gameObject.scene != outfit.gameObject.scene || AvatarRoot(body) != root || AvatarRoot(outfit) != root)
                return "Select body and outfit renderers on the same avatar in the same scene or prefab stage.";
            if (!AssetDatabase.Contains(outfit.sharedMesh) || !AssetDatabase.Contains(body.sharedMesh))
                return "Both meshes must be saved project assets.";
            return null;
        }

        internal static GeneratedMeshInfo GetInfo(Mesh mesh)
        {
            if (!mesh)
                return null;
            string path = AssetDatabase.GetAssetPath(mesh);
            if (!path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                return null;
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (asset is GeneratedMeshInfo info)
                    return info;
            }
            return null;
        }

        internal static string Generate(SkinnedMeshRenderer outfit, SkinnedMeshRenderer body, Transform root, string shape, LayerFit fit = null, Vector3[] draft = null)
        {
            string error = Validate(outfit, body, root, shape);
            if (error != null)
                throw new InvalidOperationException(error);

            var original = outfit.sharedMesh;
            var sourceInfo = GetInfo(original);
            if (sourceInfo && !sourceInfo.original)
                throw new InvalidOperationException("The original mesh asset for this generated mesh is missing.");
            var baseMesh = sourceInfo ? sourceInfo.original : original;
            var bodyMesh = body.sharedMesh;
            var matrix = body.transform.worldToLocalMatrix * outfit.transform.localToWorldMatrix;
            var sync = ModularAvatarSync.Plan(outfit, body, root, out var bodyPath, out var syncStatus, shape, fit?.Reverse == true);
            string scriptPath = AssetDatabase.GUIDToAssetPath(GeneratorScriptGuid);
            if (!scriptPath.StartsWith("Assets/", StringComparison.Ordinal))
                throw new InvalidOperationException("Install the tool in a folder under Assets before generating meshes.");
            string toolFolder = scriptPath.Substring(0, scriptPath.LastIndexOf("/Editor/", StringComparison.Ordinal));
            string outputFolder = toolFolder + "/Generated";
            string path = OutputPath(outputFolder, original, bodyMesh, matrix, sync, bodyPath, shape, (fit?.Key(outfit) ?? "") + (draft != null ? Hash128.Compute(draft).ToString() : ""));
            var generated = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            var info = GetInfo(generated);
            bool create = !generated || !info || info.original != baseMesh ||
                generated.GetBlendShapeIndex(shape) < 0;

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Generate ShapeForge Blendshape");
            string createdPath = null;

            try
            {
                if (create)
                {
                    string diagnostic;
                    if (draft != null)
                    {
                        generated = fit != null ? fit.Build(outfit, out _, draft) : ShapeTransfer.BuildMesh(original, original.vertices, draft, shape);
                        diagnostic = "Saved preview corrections.";
                    }
                    else
                        generated = fit != null ? fit.Build(outfit, out diagnostic) :
                        ShapeTransfer.Generate(bodyMesh, original, matrix, out diagnostic, shape,
                            body.transform.InverseTransformDirection(Vector3.up).normalized);
                    if (!AssetDatabase.IsValidFolder(outputFolder))
                        AssetDatabase.CreateFolder(toolFolder, "Generated");
                    path = AssetDatabase.GenerateUniqueAssetPath(path);
                    AssetDatabase.CreateAsset(generated, path);
                    createdPath = path;

                    info = ScriptableObject.CreateInstance<GeneratedMeshInfo>();
                    info.name = "ShapeForge Mesh Source";
                    info.hideFlags = HideFlags.HideInHierarchy | HideFlags.HideInInspector;
                    info.original = baseMesh;
                    var entry = new GeneratedShape { name = shape, reverse = fit?.Reverse == true, syncChange = sync, bodyPath = bodyPath };
                    info.shapes = sourceInfo ? sourceInfo.Entries.Append(entry).ToArray() : new[] { entry };
                    info.diagnostic = diagnostic;
                    AssetDatabase.AddObjectToAsset(info, generated);
                    EditorUtility.SetDirty(info);
                    AssetDatabase.SaveAssetIfDirty(generated);
                }

                AssignMesh(outfit, generated);
                ModularAvatarSync.Apply(outfit, body, sync, bodyPath, shape, fit?.Reverse == true);
                Undo.CollapseUndoOperations(undoGroup);
                return $"{(create ? "Generated" : "Reused")} {shape} for {outfit.name}. {info.diagnostic}\n{syncStatus}";
            }
            catch
            {
                Undo.FlushUndoRecordObjects();
                Undo.RevertAllDownToGroup(undoGroup);
                if (createdPath != null)
                    AssetDatabase.DeleteAsset(createdPath);
                else if (create && generated && !EditorUtility.IsPersistent(generated))
                    UnityEngine.Object.DestroyImmediate(generated);
                throw;
            }
        }

        internal static string Restore(SkinnedMeshRenderer outfit, GeneratedMeshInfo info, string shape)
        {
            if (!outfit || GetInfo(outfit.sharedMesh) != info || !info || !info.original ||
                EditorUtility.IsPersistent(outfit) || EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("The selected renderer has no restorable original mesh.");

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Restore Original Outfit Mesh");
            try
            {
                var entries = info.Entries;
                int selected = Array.FindIndex(entries, entry => entry.name == shape);
                if (selected < 0)
                    throw new InvalidOperationException("The selected shape was not generated by this tool.");
                var removed = entries[selected];
                var remaining = entries.Where(entry => entry.name != shape).ToArray();
                bool clean = ModularAvatarSync.RemoveOwned(outfit, removed);
                if (clean && removed.syncChange == SyncChange.AddedComponent)
                {
                    int successor = Array.FindIndex(remaining, entry => entry.syncChange == SyncChange.AddedBinding);
                    if (successor >= 0)
                        remaining[successor].syncChange = SyncChange.AddedComponent;
                }
                var restored = remaining.Length == 0 ? info.original : SaveRemainingShapes(outfit.sharedMesh, info, remaining);
                AssignMesh(outfit, restored);
                Undo.CollapseUndoOperations(group);
                return clean ? $"Restored {shape}."
                    : $"Restored {shape}. Review the edited MA Blendshape Sync binding in the Inspector.";
            }
            catch
            {
                Undo.FlushUndoRecordObjects();
                Undo.RevertAllDownToGroup(group);
                throw;
            }
        }

        private static Mesh SaveRemainingShapes(Mesh current, GeneratedMeshInfo info, GeneratedShape[] remaining)
        {
            string path = AssetDatabase.GenerateUniqueAssetPath(AssetDatabase.GetAssetPath(current));
            var mesh = UnityEngine.Object.Instantiate(info.original);
            mesh.name = info.original.name + " - Generated";
            bool saved = false;
            try
            {
                var deltas = new Vector3[mesh.vertexCount];
                var normals = new Vector3[mesh.vertexCount];
                var tangents = new Vector3[mesh.vertexCount];
                var vertices = mesh.vertices;
                var bounds = mesh.bounds;
                foreach (var entry in remaining)
                {
                    int index = current.GetBlendShapeIndex(entry.name);
                    for (int frame = 0; frame < current.GetBlendShapeFrameCount(index); frame++)
                    {
                        current.GetBlendShapeFrameVertices(index, frame, deltas, normals, tangents);
                        mesh.AddBlendShapeFrame(entry.name, current.GetBlendShapeFrameWeight(index, frame), deltas, normals, tangents);
                        for (int i = 0; i < vertices.Length; i++)
                            bounds.Encapsulate(vertices[i] + deltas[i]);
                    }
                }
                mesh.bounds = bounds;
                AssetDatabase.CreateAsset(mesh, path);
                saved = true;
                var metadata = ScriptableObject.CreateInstance<GeneratedMeshInfo>();
                metadata.name = "ShapeForge Mesh Source";
                metadata.hideFlags = HideFlags.HideInHierarchy | HideFlags.HideInInspector;
                metadata.original = info.original;
                metadata.shapes = remaining;
                metadata.diagnostic = info.diagnostic;
                AssetDatabase.AddObjectToAsset(metadata, mesh);
                AssetDatabase.SaveAssetIfDirty(mesh);
                return mesh;
            }
            catch
            {
                if (saved)
                    AssetDatabase.DeleteAsset(path);
                else
                    UnityEngine.Object.DestroyImmediate(mesh);
                throw;
            }
        }

        internal readonly struct PieceResult
        {
            internal readonly SkinnedMeshRenderer Renderer;
            internal readonly string Message;
            internal readonly bool Failed;

            internal PieceResult(SkinnedMeshRenderer renderer, string message, bool failed)
            {
                Renderer = renderer;
                Message = message;
                Failed = failed;
            }
        }

        internal static PieceResult[] ApplyToPieces(string undoName, SkinnedMeshRenderer[] pieces,
            Func<SkinnedMeshRenderer, string> apply)
        {
            if (pieces.Length == 0)
                return Array.Empty<PieceResult>();

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(undoName);

            var results = new PieceResult[pieces.Length];
            for (int i = 0; i < pieces.Length; i++)
            {
                var renderer = pieces[i];
                try
                {
                    results[i] = new PieceResult(renderer, apply(renderer), false);
                }
                catch (InvalidOperationException exception)
                {
                    results[i] = new PieceResult(renderer, exception.Message, true);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, renderer);
                    results[i] = new PieceResult(renderer, exception.Message, true);
                }
            }

            Undo.FlushUndoRecordObjects();
            Undo.CollapseUndoOperations(group);
            Undo.IncrementCurrentGroup();
            return results;
        }


        private static void AssignMesh(SkinnedMeshRenderer renderer, Mesh mesh)
        {
            var previous = renderer.sharedMesh;
            var weights = new float[mesh.blendShapeCount];
            for (int i = 0; i < weights.Length; i++)
            {
                int oldIndex = previous.GetBlendShapeIndex(mesh.GetBlendShapeName(i));
                if (oldIndex >= 0)
                    weights[i] = renderer.GetBlendShapeWeight(oldIndex);
            }

            Undo.RecordObject(renderer, Undo.GetCurrentGroupName());
            renderer.sharedMesh = mesh;
            for (int i = 0; i < weights.Length; i++)
                renderer.SetBlendShapeWeight(i, weights[i]);
            PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
            EditorSceneManager.MarkSceneDirty(renderer.gameObject.scene);
        }

        private static string OutputPath(string outputFolder, Mesh outfit, Mesh body, Matrix4x4 matrix, SyncChange change, string bodyPath, string shape, string layerKey)
        {
            var key = new StringBuilder("v13-source-frames").Append('|').Append(shape).Append('|').Append(layerKey);
            foreach (var mesh in new[] { outfit, body })
            {
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out string guid, out long fileId);
                key.Append('|').Append(guid).Append(':').Append(fileId).Append(':')
                    .Append(AssetDatabase.GetAssetDependencyHash(AssetDatabase.GetAssetPath(mesh)));
            }

            for (int i = 0; i < 16; i++)
                key.Append('|').Append(matrix[i].ToString("R", CultureInfo.InvariantCulture));
            key.Append('|').Append((int)change).Append('|').Append(bodyPath);

            var name = outfit.name;
            foreach (char invalid in Path.GetInvalidFileNameChars())
                name = name.Replace(invalid, '_');
            if (name.Length > 45)
                name = name.Substring(0, 45);
            return $"{outputFolder}/{name} - {Hash128.Compute(key.ToString())}.asset";
        }
    }
}
