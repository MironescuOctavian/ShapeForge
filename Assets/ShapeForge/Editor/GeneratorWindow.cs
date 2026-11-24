using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace MilltinaBreastGenerator
{
    internal sealed class GeneratorWindow : EditorWindow
    {
        private const string Description =
            "Choose an outfit and a body shape. Click Edit to inspect the checked pieces in Scene View, or Generate to save the fit. Hover over a control for help.";

        private const string OutfitSessionKey = "Octa.OutfitBlendshapeGenerator.Outfit";
        [SerializeField] private TransferMethod transferMethod;
        [SerializeField] private bool reviewHiddenShapes;
        private Mesh inspectedBody;
        private Hash128 inspectedBodyHash;
        private bool sourceShapesDirty = true;
        private string sourceShapeError;
        private List<(string Name, bool Hidden)> bodyShapes = new();
        private GUIContent[] shapeOptions = Array.Empty<GUIContent>();
        private string[] shapeNames = Array.Empty<string>();
        [SerializeField] private string sourceShapeName = ShapeTransfer.Shape;
        [SerializeField] private GameObject outfitRoot;
        [SerializeField] private int outfitInstanceId;
        [SerializeField] private SkinnedMeshRenderer manualBody;
        private readonly HashSet<SkinnedMeshRenderer> uncheckedPieces = new();
        private readonly Dictionary<SkinnedMeshRenderer, GeneratorAssets.PieceResult> results = new();
        private Piece[] pieces = Array.Empty<Piece>();
        private Transform avatarRoot;
        private SkinnedMeshRenderer detectedBody;
        private Vector2 pieceScroll;
        private bool refreshSelection = true;
        private bool showAdvanced;
        private bool changingPlayMode;
        private GUIStyle frame;
        private GUIStyle heading;
        private GUIStyle checkmark;
        private GUIStyle cross;
        private GUIStyle footer;
        private GUIStyle hintStyle;
        private string summary;
        private MessageType summaryType;
        private FitPreview editSession;
        private bool showEditSettings;

        private readonly struct Piece
        {
            internal readonly SkinnedMeshRenderer Renderer;
            internal readonly Mesh Mesh;
            internal readonly GeneratedMeshInfo Info;
            internal readonly bool HasShape;
            internal readonly bool CanRestore;
            internal readonly bool OutsideRegion;
            internal readonly bool Compatible;
            internal readonly string GenerationError;

            internal Piece(SkinnedMeshRenderer renderer, SkinnedMeshRenderer body, Transform root, Func<SkinnedMeshRenderer, bool> overlapsRegion, string shape, bool sourceAvailable)
            {
                Renderer = renderer;
                Mesh = renderer.sharedMesh;
                Info = GeneratorAssets.GetInfo(Mesh);
                HasShape = Mesh && Mesh.GetBlendShapeIndex(shape) >= 0;
                GenerationError = HasShape ? null : sourceAvailable ? GeneratorAssets.Validate(renderer, body, root, shape) :
                    "Choose a transferable source shape.";
                CanRestore = Info && Info.original && Info.Entries.Any(entry => entry.name == shape);
                bool checkedRegion = !HasShape && GenerationError == null && overlapsRegion != null;
                Compatible = checkedRegion && overlapsRegion(renderer);
                OutsideRegion = checkedRegion && !Compatible;
            }

            internal bool CanGenerate => !HasShape && !OutsideRegion && GenerationError == null;
        }

        [MenuItem("Tools/ShapeForge/Blendshape Generator")]
        internal static void Open()
        {
            var window = GetWindow<GeneratorWindow>("ShapeForge", true, typeof(SceneView));
            window.RecoverOutfit();
            window.ClearBodyAsOutfit();
            window.InvalidateSelection();
        }

        private void OnEnable()
        {
            minSize = new Vector2(400, 440);
            saveChangesMessage = "Save the ShapeForge corrections before closing?";
            wantsMouseMove = true;
            RecoverOutfit();
            ClearBodyAsOutfit();
            EditorApplication.hierarchyChanged += InvalidateSelection;
            EditorApplication.projectChanged += InvalidateBodyShapes;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            Undo.undoRedoPerformed += OnUndoRedo;
        }

        private void RecoverOutfit()
        {
            if (outfitRoot || EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            int savedId = outfitInstanceId != 0 ? outfitInstanceId : SessionState.GetInt(OutfitSessionKey, 0);
            var candidate = EditorUtility.InstanceIDToObject(savedId) as GameObject;
            if (candidate && candidate.scene.IsValid())
            {
                outfitRoot = candidate;
                refreshSelection = true;
            }
        }

        private void ClearBodyAsOutfit()
        {
            if (outfitRoot && (outfitRoot.name == "Milltina_body" || (manualBody && manualBody.gameObject == outfitRoot)))
            {
                outfitRoot = null;
                outfitInstanceId = 0;
                SessionState.EraseInt(OutfitSessionKey);
            }
        }

        private void OnDisable()
        {
            editSession?.Release();
            editSession = null;
            EditorApplication.hierarchyChanged -= InvalidateSelection;
            EditorApplication.projectChanged -= InvalidateBodyShapes;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            Undo.undoRedoPerformed -= OnUndoRedo;
        }

        private void OnPlayModeChanged(PlayModeStateChange change)
        {
            changingPlayMode = change != PlayModeStateChange.EnteredEditMode;
            InvalidateSelection();
        }

        private void OnUndoRedo()
        {
            summary = null;
            results.Clear();
            InvalidateSelection();
        }

        private void InvalidateBodyShapes()
        {
            sourceShapesDirty = true;
            InvalidateSelection();
        }

        private void InvalidateSelection()
        {
            refreshSelection = true;
            Repaint();
        }

        internal void ShowEditSettings()
        {
            showEditSettings = true;
            Focus();
            Repaint();
        }

        internal void ShowGenerator()
        {
            showEditSettings = false;
            InvalidateSelection();
        }

        internal void EditChanged()
        {
            hasUnsavedChanges = editSession?.HasEdits == true;
            Repaint();
        }

        public override void SaveChanges()
        {
            editSession?.SaveChanges();
            EditChanged();
            if (!hasUnsavedChanges) base.SaveChanges();
        }

        public override void DiscardChanges()
        {
            editSession?.DiscardChanges();
            ShowGenerator();
            base.DiscardChanges();
        }

        internal void StartEdit(SkinnedMeshRenderer body, SkinnedMeshRenderer[] outfits, string shape, TransferMethod method)
        {
            editSession ??= new FitPreview(this);
            if (!editSession.Active) editSession.Start(body, outfits, shape, method);
            ShowEditSettings();
        }

        private void OnGUI()
        {
            var input = Event.current;
            if (input.type == EventType.MouseMove)
                Repaint();
            if (input.type == EventType.KeyDown && input.shift && input.keyCode == KeyCode.Space)
                GUI.FocusControl(null);
            if (showEditSettings && editSession != null && !changingPlayMode && !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                editSession.DrawSettings(position.width);
                return;
            }

            frame ??= new GUIStyle { padding = new RectOffset(12, 12, 10, 10) };
            heading ??= new GUIStyle(EditorStyles.boldLabel) { fontSize = 21 };
            checkmark ??= new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = EditorGUIUtility.isProSkin
                    ? new Color(0.46f, 0.82f, 0.59f) : new Color(0.14f, 0.43f, 0.23f) }
            };
            cross ??= new GUIStyle(checkmark)
            {
                normal = { textColor = EditorGUIUtility.isProSkin ? new Color(1f, 0.52f, 0.52f) : new Color(0.65f, 0.12f, 0.12f) }
            };
            footer ??= new GUIStyle(EditorStyles.wordWrappedMiniLabel);
            hintStyle ??= new GUIStyle(EditorStyles.wordWrappedLabel) { fontSize = 11 };

            using (new EditorGUILayout.VerticalScope(frame, GUILayout.ExpandHeight(true)))
            {
                var banner = GUILayoutUtility.GetRect(0, 52, GUILayout.ExpandWidth(true));
                if (Event.current.type == EventType.Repaint)
                {
                    EditorGUI.DrawRect(banner, new Color(.15f, .15f, .15f));
                    EditorGUI.DrawRect(new Rect(banner.x, banner.y, 2, banner.height), new Color(.39f, .39f, .39f));
                    GUI.Label(new Rect(banner.x + 12, banner.y + 3, banner.width - 24, 28), "ShapeForge", heading);
                    var previous = GUI.contentColor;
                    GUI.contentColor = EditorGUIUtility.isProSkin ? new Color(.78f, .78f, .78f) : Color.white;
                    GUI.Label(new Rect(banner.x + 13, banner.y + 30, banner.width - 26, 16),
                        "Precision Blendshape Generator", EditorStyles.whiteMiniLabel);
                    GUI.contentColor = previous;
                }
                if (editSession?.Active == true && GUI.Button(new Rect(banner.xMax - 118, banner.y + 13, 106, 26), "Resume Edit"))
                {
                    ShowEditSettings();
                    GUIUtility.ExitGUI();
                }
                EditorGUILayout.Space(6);

                if (changingPlayMode || EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    EditorGUILayout.HelpBox("Exit Play Mode to generate or restore outfit meshes.", MessageType.Info);
                    GUILayout.FlexibleSpace();
                }
                else
                    DrawEditor();

                EditorGUILayout.Space(8);
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                    EditorGUILayout.LabelField(string.IsNullOrEmpty(GUI.tooltip) ? Description : GUI.tooltip, hintStyle);
            }
        }

        private void DrawEditor()
        {
            RecoverOutfit();
            if (refreshSelection || pieces.Any(p => !p.Renderer || p.Mesh != p.Renderer.sharedMesh))
                RefreshPieces();

            var body = manualBody ? manualBody : detectedBody;
            EditorGUI.BeginChangeCheck();
            var selectedBody = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(new GUIContent("Body Mesh", "Pick the avatar body that already has the shape you want to copy."), body,
                typeof(SkinnedMeshRenderer), true);
            if (EditorGUI.EndChangeCheck())
            {
                manualBody = selectedBody;
                ClearBodyAsOutfit();
                summary = null;
                results.Clear();
                InvalidateSelection();
                GUIUtility.ExitGUI();
            }

            EditorGUI.BeginChangeCheck();
            var selectedOutfit = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Outfit", "Drag the clothing object or its parent from the Hierarchy."), outfitRoot, typeof(GameObject), true);
            if (EditorGUI.EndChangeCheck())
            {
                outfitRoot = selectedOutfit;
                outfitInstanceId = outfitRoot ? outfitRoot.GetInstanceID() : 0;
                SessionState.SetInt(OutfitSessionKey, outfitInstanceId);
                ClearBodyAsOutfit();
                summary = null;
                results.Clear();
                uncheckedPieces.Clear();
                pieceScroll = Vector2.zero;
                InvalidateSelection();
                GUIUtility.ExitGUI();
            }

            bool sceneOutfit = outfitRoot && !EditorUtility.IsPersistent(outfitRoot) && outfitRoot.scene.IsValid();
            bool sceneBody = body && body.sharedMesh && !EditorUtility.IsPersistent(body) && body.gameObject.scene.IsValid();
            bool validBody = sceneBody && (!sceneOutfit || (body.gameObject.scene == outfitRoot.scene &&
                GeneratorAssets.AvatarRoot(body) == avatarRoot));
            DrawSourceShapes(body ? body.sharedMesh : null);
            showAdvanced = EditorGUILayout.Foldout(showAdvanced, "Advanced · " + transferMethod, true);
            if (showAdvanced)
            {
                var nextMethod = (TransferMethod)EditorGUILayout.EnumPopup(new GUIContent("Transfer Method",
                    "Start with Barycentric, the default. Try Weighted for loose cloth or RBF for a broad change like the belly. Compare in Preview."), transferMethod);
                if (nextMethod != transferMethod)
                {
                    transferMethod = nextMethod;
                    results.Clear();
                    summary = null;
                    InvalidateSelection();
                    GUIUtility.ExitGUI();
                }
            }
            bool sourceShape = validBody && shapeNames.Contains(sourceShapeName);

            EditorGUILayout.Space(8);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                StatusRow("Source mesh", validBody, validBody ? body.name : "Select the avatar body");
                StatusRow("Source shape", sourceShape, sourceShape ? sourceShapeName : "Missing");
                StatusRow("Synchronization", ModularAvatarSync.Available,
                    ModularAvatarSync.Available ? "MA Blendshape Sync" : "Manual slider");
            }

            if (!sceneOutfit)
            {
                EditorGUILayout.HelpBox("Drag an outfit parent or a single piece from the Hierarchy.", MessageType.Info);
                GUILayout.FlexibleSpace();
                return;
            }
            if (avatarRoot == outfitRoot.transform)
            {
                EditorGUILayout.HelpBox("Select the outfit object inside the avatar.", MessageType.Info);
                GUILayout.FlexibleSpace();
                return;
            }
            if (pieces.Length == 0)
            {
                EditorGUILayout.HelpBox("This outfit has no skinned mesh pieces in its hierarchy.", MessageType.Info);
                GUILayout.FlexibleSpace();
                return;
            }
            if (sceneBody && !validBody)
                EditorGUILayout.HelpBox("Select the body mesh from the same avatar as the outfit.", MessageType.Warning);

            EditorGUILayout.Space(8);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(new GUIContent("Outfit pieces", "Generate the shape only for clothing near the selected body shape."), EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(!pieces.Any(p => p.CanGenerate)))
                {
                    if (GUILayout.Button(new GUIContent("All", "Check every piece that can receive this shape."), EditorStyles.miniButton, GUILayout.Width(42)))
                        uncheckedPieces.Clear();
                    GUILayout.Space(6);
                    if (GUILayout.Button(new GUIContent("None", "Uncheck all pieces. Check individual pieces to work on only those."), EditorStyles.miniButton, GUILayout.Width(46)))
                    {
                        foreach (var piece in pieces.Where(p => p.CanGenerate))
                            uncheckedPieces.Add(piece.Renderer);
                    }
                }
            }

            EditorGUILayout.Space(4);
            float listHeight = Mathf.Clamp(position.height * .45f, 116, Mathf.Max(116, pieces.Length * 29 + 4));
            using (var scroll = new EditorGUILayout.ScrollViewScope(pieceScroll, GUILayout.Height(listHeight)))
            {
                pieceScroll = scroll.scrollPosition;
                foreach (var piece in pieces)
                    DrawPiece(piece, sourceShape);
            }

            EditorGUILayout.Space(6);
            var selected = pieces.Where(p => p.CanGenerate && !uncheckedPieces.Contains(p.Renderer)).ToArray();
            var restorable = pieces.Where(p => p.CanRestore).ToArray();
            bool editing = editSession?.Active == true;
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(selected.Length == 0 && !editing))
                {
                    float actionWidth = Mathf.Max(90f, (position.width - 36f) * .32f);

                    if (!editing && GUILayout.Button(new GUIContent("Edit", "Go to Edit Settings in ShapeForge."), GUILayout.Height(34), GUILayout.Width(actionWidth)))
                    {
                        StartEdit(body, selected.Select(p => p.Renderer).ToArray(), sourceShapeName, transferMethod);
                        GUIUtility.ExitGUI();
                    }
                    bool generate;
                    using (new EditorGUI.DisabledScope(editing))
                        generate = GUILayout.Button(new GUIContent($"Generate ({selected.Length})", "Save the chosen shape on each checked piece. Existing outfit shapes stay available."),
                            GUILayout.Height(34), GUILayout.Width(actionWidth));
                    if (generate)
                    {
                        LayerFit fit;
                        try
                        {
                            fit = new LayerFit(body, selected.Select(p => p.Renderer).ToArray(), sourceShapeName, transferMethod);
                        }
                        catch (InvalidOperationException exception)
                        {
                            summary = exception.Message;
                            summaryType = MessageType.Error;
                            GUIUtility.ExitGUI();
                            return;
                        }
                        Run("Generate ShapeForge Blendshape", "Generated", selected, r => GeneratorAssets.Generate(r, body, avatarRoot, fit: fit, shape: sourceShapeName));
                    }
                }
                using (new EditorGUI.DisabledScope(restorable.Length == 0 || editing))
                {
                    if (GUILayout.Button(new GUIContent($"Restore All ({restorable.Length})",
                        "Remove the chosen generated shape from every applicable piece in this outfit."), GUILayout.Height(34)))
                        Run("Restore ShapeForge Blendshape", "Restored", restorable, RestorePiece);
                }
            }

            if (summary != null)
                EditorGUILayout.HelpBox(summary, summaryType);
        }

        private void DrawSourceShapes(Mesh mesh)
        {
            UpdateSourceShapes(mesh);
            if (bodyShapes.Any(shape => shape.Hidden))
            {
                var review = EditorGUILayout.ToggleLeft(new GUIContent("Review hidden shapes",
                    "Show likely body masks. Use this if a real fit slider was hidden by mistake."), reviewHiddenShapes);
                if (review != reviewHiddenShapes)
                {
                    reviewHiddenShapes = review;
                    BuildShapeOptions();
                    InvalidateSelection();
                    GUIUtility.ExitGUI();
                }
            }

            if (shapeNames.Length == 0)
            {
                EditorGUILayout.LabelField("Source Shape", sourceShapeError ?? (mesh ? "No transferable shapes found" : "Choose a body mesh"));
                return;
            }
            int current = Array.IndexOf(shapeNames, sourceShapeName);
            int selected = EditorGUILayout.Popup(new GUIContent("Source Shape",
                "Choose the body slider the outfit should follow."), current, shapeOptions);
            if (selected >= 0 && sourceShapeName != shapeNames[selected])
            {
                sourceShapeName = shapeNames[selected];
                uncheckedPieces.Clear();
                results.Clear();
                summary = null;
                InvalidateSelection();
                GUIUtility.ExitGUI();
            }
        }

        private void UpdateSourceShapes(Mesh mesh)
        {
            bool sameMesh = ReferenceEquals(inspectedBody, mesh);
            if (!sourceShapesDirty && sameMesh)
                return;
            sourceShapesDirty = false;
            string path = mesh ? AssetDatabase.GetAssetPath(mesh) : null;
            var hash = string.IsNullOrEmpty(path) ? default : AssetDatabase.GetAssetDependencyHash(path);
            if (sameMesh && hash.isValid && hash == inspectedBodyHash)
                return;
            inspectedBody = mesh;
            inspectedBodyHash = hash;
            sourceShapeError = null;
            try { bodyShapes = ShapeTransfer.DiscoverShapes(mesh); }
            catch (UnityException)
            {
                bodyShapes.Clear();
                sourceShapeError = "Body mesh data could not be read.";
            }
            BuildShapeOptions();
        }

        private void BuildShapeOptions()
        {
            var visible = bodyShapes.Where(shape => reviewHiddenShapes || !shape.Hidden).ToArray();
            shapeNames = Array.ConvertAll(visible, shape => shape.Name);
            shapeOptions = Array.ConvertAll(visible, shape =>
                new GUIContent(shape.Hidden ? shape.Name + "  (possible body mask)" : shape.Name));
            if (shapeNames.Length > 0 && !shapeNames.Contains(sourceShapeName))
                sourceShapeName = shapeNames[0];
        }

        private void RefreshPieces()
        {
            refreshSelection = false;
            detectedBody = null;
            avatarRoot = GeneratorAssets.AvatarRoot(outfitRoot ? outfitRoot.transform : null);
            if (!outfitRoot || EditorUtility.IsPersistent(outfitRoot) || !outfitRoot.scene.IsValid() ||
                avatarRoot == outfitRoot.transform)
            {
                if (!manualBody)
                    detectedBody = GeneratorAssets.FindBody(null, avatarRoot);
                pieces = Array.Empty<Piece>();
                return;
            }

            var renderers = outfitRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(r => GeneratorAssets.AvatarRoot(r) == avatarRoot).ToArray();
            if (!manualBody && renderers.Length > 0)
                detectedBody = GeneratorAssets.FindBody(renderers[0], avatarRoot);
            var body = manualBody ? manualBody : detectedBody;
            UpdateSourceShapes(body ? body.sharedMesh : null);
            bool sourceAvailable = shapeNames.Contains(sourceShapeName);
            Func<SkinnedMeshRenderer, bool> overlapsRegion = null;
            if (body && body.sharedMesh && sourceAvailable)
            {
                try
                {
                    overlapsRegion = ShapeTransfer.ShapeOverlap(body, sourceShapeName, transferMethod);
                }
                catch (InvalidOperationException)
                {
                    // Generation reports invalid source meshes when requested.
                }
            }

            pieces = renderers.Where(r => r != body && r.name != "Milltina_body")
                .Select(r => new Piece(r, body, avatarRoot, overlapsRegion, sourceShapeName, sourceAvailable))
                .OrderBy(piece => piece.OutsideRegion)
                .ToArray();
        }

        private void DrawPiece(Piece piece, bool sourceShape)
        {
            bool hasResult = results.TryGetValue(piece.Renderer, out var result);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (piece.HasShape)
                        GUILayout.Label("✓", checkmark, GUILayout.Width(18));
                    else if (piece.OutsideRegion)
                        GUILayout.Label(new GUIContent("✕", "No overlap with the selected blendshape."), cross, GUILayout.Width(18));
                    else
                    {
                        bool included = piece.CanGenerate && !uncheckedPieces.Contains(piece.Renderer);
                        using (new EditorGUI.DisabledScope(!piece.CanGenerate))
                        {
                            if (EditorGUILayout.Toggle(new GUIContent("", "Include this piece when you click Edit or Generate."), included, GUILayout.Width(18)) != included)
                            {
                                if (included)
                                    uncheckedPieces.Add(piece.Renderer);
                                else
                                    uncheckedPieces.Remove(piece.Renderer);
                            }
                        }
                    }

                    var tooltip = hasResult ? result.Message : piece.GenerationError;
                    GUILayout.Label(new GUIContent(piece.Renderer.name, tooltip), EditorStyles.boldLabel);
                    GUILayout.FlexibleSpace();

                    if (piece.CanRestore)
                    {
                        if (GUILayout.Button(new GUIContent("Restore", "Remove only the chosen generated shape from this piece."), GUILayout.Width(72)))
                            Run("Restore Milltina Piece", "Restored", new[] { piece }, RestorePiece);
                    }
                    else if (piece.OutsideRegion)
                        GUILayout.Label(new GUIContent("Outside shape area",
                            $"This piece does not overlap the area moved by {sourceShapeName}."), EditorStyles.miniLabel);
                    else if (piece.HasShape)
                        GUILayout.Label("Shape already present", EditorStyles.miniLabel);
                    else if (piece.Compatible)
                        GUILayout.Label(new GUIContent("Compatible",
                            $"This piece overlaps the area moved by {sourceShapeName}. Check the generated fit in Preview."), checkmark);
                }

                if (piece.Info && !piece.Info.original)
                    EditorGUILayout.LabelField("Original mesh is missing.", footer);
                else if (hasResult)
                    EditorGUILayout.HelpBox(result.Message, result.Failed ? MessageType.Warning : MessageType.None);
                else if (sourceShape && piece.GenerationError != null)
                    EditorGUILayout.LabelField(piece.GenerationError, footer);
            }
        }

        private void StatusRow(string label, bool present, string value)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(present ? "✓" : "–", present ? checkmark : EditorStyles.centeredGreyMiniLabel,
                    GUILayout.Width(18));
                GUILayout.Label(label, GUILayout.Width(110));
                GUILayout.Label(value);
            }
        }

        private string RestorePiece(SkinnedMeshRenderer renderer) =>
            GeneratorAssets.Restore(renderer, GeneratorAssets.GetInfo(renderer.sharedMesh), sourceShapeName);

        private void Run(string undoName, string completed, Piece[] batch, Func<SkinnedMeshRenderer, string> apply)
        {
            var batchResults = GeneratorAssets.ApplyToPieces(undoName, batch.Select(p => p.Renderer).ToArray(), apply);
            results.Clear();
            int failed = 0;
            foreach (var result in batchResults)
            {
                results[result.Renderer] = result;
                if (result.Failed)
                    failed++;
            }

            int succeeded = batchResults.Length - failed;
            summary = failed == 0
                ? $"{completed} {succeeded} of {batchResults.Length} pieces."
                : $"{completed} {succeeded} of {batchResults.Length} pieces. See the piece list for details.";
            summaryType = failed == 0 ? MessageType.Info : MessageType.Warning;
            InvalidateSelection();
            GUIUtility.ExitGUI();
        }
    }
}
