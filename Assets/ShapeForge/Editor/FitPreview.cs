using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MilltinaBreastGenerator
{
    internal sealed class FitPreview : EditorWindow
    {
        private static readonly Unity.Profiling.ProfilerMarker RenderSample = new("ShapeForge.Preview.Render");
        private static readonly Unity.Profiling.ProfilerMarker BakeSample = new("ShapeForge.Preview.Bake");
        private static readonly Unity.Profiling.ProfilerMarker BrushSample = new("ShapeForge.Preview.Brush");
        private static readonly Unity.Profiling.ProfilerMarker ClippingSample = new("ShapeForge.Preview.Clipping");

        private sealed class Piece
        {
            internal bool[] Protection;
            internal bool[] Selection;
            internal int SelectionCount;
            internal bool[] BrushBoundary;
            internal Vector3[] RestWorld;
            internal SkinnedMeshRenderer Renderer;
            internal Mesh Baked;
            internal SkinnedMeshRenderer Source;
            internal Mesh Original;
            internal Vector3[] Draft;
            internal Material[] Materials;
            internal Vector3[] World;
            internal Vector3[] WorldNormals;
            internal ShapeTransfer.TriangleSurface PickSurface;
            internal bool PickSurfaceDirty;
            internal int[] BrushWeld;
            internal ShapeTransfer.TriangleSurface BrushSurface;
            internal Vector3[] BrushRest;
            internal List<(int Target, float Length)>[] BrushEdges;
            internal (int A, int B, int C)[] Faces;
        }

        private readonly List<Piece> pieces = new();
        private readonly List<Mesh> generated = new();
        private PreviewRenderUtility preview;
        private Vector3 focus;
        private Vector3 forward;
        private Vector3 up;
        private float size;
        private SkinnedMeshRenderer body;
        private string targetShape = ShapeTransfer.Shape;
        private enum Brush { Grab, PushOut, Smooth, Multiply, ClearSkin, ElasticDeform, Protect, Unprotect, Restore, Relax }
        private static readonly string[] BrushNames = { "Grab", "Push Out", "Smooth", "Multiply", "Clipping Brush", "Elastic Deform", "Protect", "Unprotect", "Restore Brush", "Relax" };
        private Brush brushMode;
        private float brushStrength = .5f;
        private float brushGapMm = .5f;
        private int brushReference;
        private bool restoreOriginal;
        private bool boxMode;
        private bool boxDragging;
        private int boxControl;
        private Vector2 boxStart, boxEnd;
        private bool stampQueued;
        private Rect stampViewport;
        private Vector2 stampPosition, lastStampPosition;
        private bool stampThroughBody;
        private bool SurfaceBrush => brushMode is Brush.ClearSkin or Brush.Restore or Brush.Relax;
        private float transferMultiplier = 1.2f;
        private Vector3[] strokeNormals;
        private Vector3[] strokeSmooth;
        private bool sculpt;
        private float vertexSize = 4;
        private bool showClipMarkers = true;
        private bool leftSide;
        private float brushRadius = .015f;
        private readonly Stack<(Piece Piece, float Weight, Vector3[] Prior, bool[] Mask)> brushUndo = new();
        private readonly Stack<(Piece Piece, float Weight, Vector3[] Prior, bool[] Mask)> brushRedo = new();
        private Texture viewImage;
        private Material selectionHighlight;
        private double selectionHighlightUntil;
        private Rect renderedRect;
        private Vector3 renderedPosition;
        private Quaternion renderedRotation;
        private Color renderedBackground;
        private Material renderedSky;
        private float renderedNear;
        private float renderedFar;
        private float toolbarHeight = 180f;
        private GUIStyle floatingButton;
        private GUIStyle floatingLabel;
        private GUIStyle hoverHelp;
        private string centerHelp;
        private Rect toolbarHelpRect;
        private bool showDisplay;
        private int selectedMesh;
        private bool isolateMesh;
        private GUIContent[] editableNames;
        private bool cullBackfaces = true;
        private bool zTest = true;
        private bool[] strokeVisible;
        private bool strokeThroughBody;
        private bool hoverThroughBody;
        private Piece hoverBrushPiece;
        private Vector3 hoverBrushCenter;
        private (int A, int B, int C) hoverBrushFace;
        private float hoverBrushRadius;
        private int hoverBrushAxes;
        private List<(float[] Distances, Vector3 Sign)> hoverBrushes;
        private bool visibilityThroughBody;
        private IEnumerator<int> clippingWork;
        private Piece visibilityPiece;
        private Vector3 visibilityOrigin;
        private sbyte[] vertexVisibility;
        private static readonly int[] Presets = { 0, 50, 100 };
        private sealed class ClippingPatch
        {
            internal Vector3 Point;
            internal Color Color;
            internal int Other;
            internal int Samples;
            internal float Depth;
            internal bool Existing;
        }

        private readonly List<ClippingPatch> crossings = new();
        private int activePatch = -1;
        private string[] patchLabels = Array.Empty<string>();
        private Mesh patchBaseMesh;
        private Mesh patchPreviewMesh;
        private Vector3[] patchMotion;
        private string patchFeedback;
        private bool unsavedEdits;
        private bool PreviewingPatch => patchBaseMesh != null;
        private string status;
        private float zoom = 1f;
        private Vector3 pan;
        private readonly HashSet<KeyCode> flyKeys = new();
        private bool flying;
        private bool panning;
        private int flyControl;
        private double lastFlyUpdate;
        private Quaternion flyRotation;
        private float flyDistance;
        private float flySpeed = 1f;
        private Piece strokePiece;
        private bool straighten;
        private Piece spanPiece;
        private Vector3 spanStart;
        private Vector3 spanEnd;
        private (int A, int B, int C) spanSeed;
        private Vector3[] spanOffsets;
        private float spanStrength = .75f;
        private Vector3[] strokeStart;
        private List<(float[] Distances, Vector3 Sign)> strokeBrushes;
        private int symmetryAxes;
        private Vector3 strokeCenter;
        private Vector2 strokeCurrent;
        private bool strokeInDepth;
        private Vector2 hoverMouse;
        private static readonly GUIContent SculptHelp = new GUIContent("✎  Sculpt",
            "Hover over clothing to see what moves. Left-drag to grab. Scroll during the drag to resize. Shift-drag moves in depth. Alt-grab to pick through the body. Edits apply at 100. Enable Left/right to mirror across the avatar.");
        private Vector2 strokeMouse;
        private float strokeDepth;
        private int strokeControl;
        private bool strokeMoved;
        private bool pendingDrag;
        private float dragHeight;
        private double nextDragUpdate;
        private float weight = 100;
        private float[] frameWeights = { 100 };
        private bool EditableFrame => Array.IndexOf(frameWeights, weight) >= 0;
        private int viewIndex;
        private float yaw;
        private float elevation;
        private string error;
        private static readonly string[] ViewLabels = { "Front", "Side", "Back", "Top", "Bottom" };

        internal static void Open(SkinnedMeshRenderer body, SkinnedMeshRenderer[] outfits, string shape = ShapeTransfer.Shape,
            TransferMethod method = TransferMethod.Barycentric, BodyReference mode = BodyReference.Neutral)
        {
            var window = GetWindow<FitPreview>("ShapeForge Preview");
            window.wantsMouseMove = true;
            window.minSize = new Vector2(540, 360);
            window.titleContent = new GUIContent($"ShapeForge Preview · {method}");
            window.Build(body, outfits, shape, method, mode);
            window.Show();
        }

        private LayerFit previewFit;

        private void Build(SkinnedMeshRenderer body, SkinnedMeshRenderer[] outfits, string shape, TransferMethod method, BodyReference mode)
        {
            Release();
            error = null;
            status = null;
            this.body = body;
            targetShape = shape;
            try
            {
                preview = new PreviewRenderUtility();
                preview.cameraFieldOfView = 60;
                zoom = 1f;
                pan = Vector3.zero;
                preview.lights[0].intensity = 1.2f;
                preview.lights[0].transform.rotation = Quaternion.Euler(40, 215, 0);
                preview.ambientColor = Color.gray;

                var avatar = GeneratorAssets.AvatarRoot(body);
                var orientation = avatar ? avatar : body.transform.root;
                forward = orientation.forward;
                up = orientation.up;
                var region = ShapeTransfer.ShapeBounds(body, targetShape, mode);
                focus = region.center;
                size = Mathf.Max(region.size.magnitude * 0.6f, 0.02f);

                AddPiece(body, body.sharedMesh);
                if (mode == BodyReference.Neutral)
                    for (int i = 0; i < body.sharedMesh.blendShapeCount; i++) pieces[0].Renderer.SetBlendShapeWeight(i, 0);
                previewFit = new LayerFit(body, outfits, targetShape, method, mode);
                frameWeights = previewFit.Weights;
                foreach (var outfit in outfits)
                {
                    var mesh = previewFit.Build(outfit, out _);
                    generated.Add(mesh);
                    AddPiece(outfit, mesh);
                }
                editableNames = Array.ConvertAll(outfits, outfit => new GUIContent(outfit.name));
                selectedMesh = 0;
                isolateMesh = false;
                Bake();
            }
            catch (Exception exception)
            {
                Release();
                error = exception.Message;
            }
            Repaint();
        }

        private void AddPiece(SkinnedMeshRenderer source, Mesh mesh)
        {
            var go = new GameObject("Fit Preview") { hideFlags = HideFlags.HideAndDontSave };
            preview.AddSingleGO(go);
            go.transform.SetPositionAndRotation(source.transform.position, source.transform.rotation);
            go.transform.localScale = source.transform.lossyScale;
            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            renderer.rootBone = source.rootBone;
            renderer.bones = source.bones;
            renderer.sharedMaterials = source.sharedMaterials;
            renderer.enabled = false;
            for (int i = 0; i < source.sharedMesh.blendShapeCount; i++)
                renderer.SetBlendShapeWeight(i, source.GetBlendShapeWeight(i));
            pieces.Add(new Piece
            {
                Renderer = renderer, Source = source, Original = source.sharedMesh,
                Materials = source.sharedMaterials, Baked = new Mesh { hideFlags = HideFlags.HideAndDontSave }
            });
        }

        private void Bake()
        {
            InvalidateVertexVisibility();
            ClearClipping();
            foreach (var piece in pieces)
                BakePiece(piece);
        }

        private void BakePiece(Piece piece)
        {
            using var sample = BakeSample.Auto();
            var renderer = piece.Renderer;
            renderer.SetBlendShapeWeight(renderer.sharedMesh.GetBlendShapeIndex(targetShape), piece.Source == body && previewFit.Reverse ? 100 - weight : weight);
            renderer.BakeMesh(piece.Baked);
            viewImage = null;
            var toWorld = renderer.localToWorldMatrix;
            piece.World = Array.ConvertAll(piece.Baked.vertices, toWorld.MultiplyPoint3x4);
            var normalsToWorld = renderer.worldToLocalMatrix.transpose;
            piece.WorldNormals = Array.ConvertAll(piece.Baked.normals, normalsToWorld.MultiplyVector);
            piece.PickSurfaceDirty = true;
            piece.Faces ??= new List<(int A, int B, int C)>(Triangles(piece.Baked)).ToArray();
        }

        private void OnGUI()
        {
            if (error != null)
            {
                EditorGUILayout.HelpBox(error, MessageType.Warning);
                return;
            }
            if (preview == null)
            {
                EditorGUILayout.LabelField("Open Preview from the generator to compare an outfit.");
                return;
            }

            if (PreviewingPatch)
            {
                if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
                {
                    CancelPatchPreview();
                    Event.current.Use();
                }
            }
            else
            {
                HandleHistoryShortcut();
                HandleBoxShortcut();
                HandleSpanShortcut();
            }
            var toolbar = new Rect(16, 10, position.width - 32, position.height - 20);
            var input = Event.current;
            if (input.type == EventType.MouseMove) Repaint();
            var toolbarFrame = new Rect(0, 0, position.width, toolbar.y + toolbarHeight + 8);
            var footer = new Rect(0, position.height - 32, position.width, 32);
            var viewport = new Rect(0, toolbarFrame.yMax, position.width, Mathf.Max(1, footer.y - toolbarFrame.yMax));
            var display = new Rect(position.width - 104, toolbar.y, 92, 30);
            var displayOptions = new Rect(position.width - 219, viewport.y + 6, 207, 30);
            bool overControls = toolbarFrame.Contains(input.mousePosition) || footer.Contains(input.mousePosition) ||
                display.Contains(input.mousePosition) ||
                (showDisplay && displayOptions.Contains(input.mousePosition));
            bool repaint = input.type == EventType.Repaint;
            if (repaint) centerHelp = null;
            if (repaint)
            {
                DrawViewport(viewport, overControls);
                EditorGUI.DrawRect(toolbarFrame, new Color(.105f, .102f, .095f, 1f));
                EditorGUI.DrawRect(new Rect(0, toolbarFrame.yMax - 1, toolbarFrame.width, 1),
                    new Color(.73f, .62f, .44f, .28f));
            }
            using (new GUILayout.AreaScope(toolbar))
            {
                using (new EditorGUILayout.VerticalScope())
                    DrawToolbar(toolbar.width);
                if (repaint)
                {
                    float height = GUILayoutUtility.GetLastRect().height;
                    if (!Mathf.Approximately(toolbarHeight, height))
                    {
                        toolbarHeight = height;
                        Repaint();
                    }
                }
            }
            using (new GUILayout.AreaScope(display))
                if (OverlayButton(new GUIContent("Display ▾", "Middle-drag to pan. Right-drag to look. Scroll to zoom."), showDisplay))
                    showDisplay = !showDisplay;
            if (showDisplay)
            {
                if (repaint) EditorGUI.DrawRect(displayOptions, new Color(.105f, .102f, .095f, .96f));
                using (new GUILayout.AreaScope(new Rect(displayOptions.x + 8, displayOptions.y + 4, 191, 25)))
                    using (new EditorGUILayout.HorizontalScope()) DrawVisibility();
            }

            if (repaint) EditorGUI.DrawRect(footer, new Color(.105f, .102f, .095f, 1f));
            using (new GUILayout.AreaScope(new Rect(12, footer.y + 3, footer.width - 24, 26)))
            using (new EditorGUILayout.HorizontalScope())
            {
                string line = string.IsNullOrEmpty(status) ? editableNames[selectedMesh].text : status.Split('\n')[0];
                GUILayout.Label(line, EditorStyles.whiteMiniLabel, GUILayout.ExpandWidth(true));
                LastControlHelp(status);
                var piece = pieces[selectedMesh + 1];
                if (unsavedEdits)
                    GUILayout.Label("Unsaved edits", EditorStyles.whiteMiniLabel, GUILayout.Width(79));
                if (piece.SelectionCount > 0)
                {
                    GUILayout.Label($"{piece.SelectionCount} selected", EditorStyles.whiteMiniLabel, GUILayout.Width(90));
                    using (new EditorGUI.DisabledScope(PreviewingPatch))
                        if (GUILayout.Button("Clear", EditorStyles.miniButton, GUILayout.Width(46)))
                            ClearSelection(piece);
                }
                else GUILayout.Label("Whole mesh editable", EditorStyles.whiteMiniLabel, GUILayout.Width(122));
                if (crossings.Count > 0)
                    showClipMarkers = EditorGUILayout.ToggleLeft("Markers", showClipMarkers, GUILayout.Width(72));
            }

            if (!repaint)
                DrawViewport(viewport, overControls);
            else if (!string.IsNullOrEmpty(centerHelp))
            {
                hoverHelp ??= new GUIStyle(EditorStyles.whiteMiniLabel)
                {
                    alignment = TextAnchor.MiddleCenter, wordWrap = true, fontSize = 11
                };
                var help = toolbarHelpRect;
                help.position += toolbar.position;
                GUI.Label(help, centerHelp, hoverHelp);
            }
        }

        private void LastControlHelp(string tip) => SetCenterHelp(GUILayoutUtility.GetLastRect(), tip);

        private void SetCenterHelp(Rect rect, string tip)
        {
            if (Event.current.type == EventType.Repaint && !string.IsNullOrEmpty(tip) && rect.Contains(Event.current.mousePosition))
                centerHelp = tip;
        }

        private void DrawToolbar(float width)
        {
            bool compact = width < 900;
            float fitWidth = frameWeights.Length > 1 ? 420 : 325;
            bool fitOnFirstRow = width >= 290 + fitWidth + 96;
            bool viewsOnFirstRow = width >= 290 + fitWidth + 340 + 96;
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUILayout.HorizontalScope(GUILayout.Width(fitOnFirstRow ? 290 : Mathf.Min(width, 400))))
                    using (new EditorGUI.DisabledScope(PreviewingPatch)) DrawMeshPicker();
                if (viewsOnFirstRow)
                {
                    GUILayout.FlexibleSpace();
                    DrawViews();
                    GUILayout.FlexibleSpace();
                    DrawFitControls();
                }
                else
                {
                    if (fitOnFirstRow) DrawFitControls();
                    GUILayout.FlexibleSpace();
                }
                GUILayout.Space(96);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(PreviewingPatch)) DrawEditTools();
                if (compact)
                    GUILayout.FlexibleSpace();
                else
                {
                    ReserveToolbarHelp();
                    using (new EditorGUI.DisabledScope(PreviewingPatch)) DrawHistory();
                }
            }
            if (compact) ReserveToolbarHelp();

            // Narrow windows keep the complete fit and history controls on their own rows.
            if (!fitOnFirstRow)
                using (new EditorGUILayout.HorizontalScope()) DrawFitControls();
            if (!viewsOnFirstRow)
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    DrawViews();
                    GUILayout.FlexibleSpace();
                }
            if (compact)
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    using (new EditorGUI.DisabledScope(PreviewingPatch)) DrawHistory();
                }
            if (crossings.Count > 0)
                DrawPatchControls();
            if (PreviewingPatch) return;

            if (straighten || sculpt)
                using (new EditorGUILayout.HorizontalScope()) DrawToolSettings();

            if (sculpt && SurfaceBrush && !boxMode)
                using (new EditorGUILayout.HorizontalScope()) DrawSurfaceBrushSettings();
            if (boxMode)
                OverlayLabel("Drag: replace · Shift: add · Ctrl/Cmd: subtract · Alt: select through · Esc: clear", EditorStyles.wordWrappedMiniLabel);
            if (sculpt)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Symmetry", EditorStyles.whiteMiniLabel, GUILayout.Width(58));
                    DrawSymmetryToggle("X", "Mirror across the avatar's centerline (X).", 1);
                    DrawSymmetryToggle("Y", "Mirror across the garment's vertical midpoint (Y).", 2);
                    DrawSymmetryToggle("Z", "Mirror across the garment's front/back midpoint (Z).", 4);
                    if (width >= 630) DrawVertexSize();
                }
            }
            if (straighten || (sculpt && width < 630))
                using (new EditorGUILayout.HorizontalScope()) DrawVertexSize();

            if (straighten || sculpt)
            {
                string hint = !EditableFrame ? "Select an authored frame to edit." :
                    sculpt ? BrushHint() :
                    spanPiece == null ? "Click the first attachment point." :
                    spanOffsets == null ? "Click the second attachment point." :
                    "Enter applies the span. Esc clears it.";
                OverlayLabel(hint, EditorStyles.wordWrappedMiniLabel);
            }

        }

        private void DrawFitControls()
        {
            using (new EditorGUI.DisabledScope(PreviewingPatch)) DrawWeight(136f);
        }

        private void DrawPatchControls()
        {
            if (!PreviewingPatch)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Clipping patch", EditorStyles.whiteMiniLabel, GUILayout.Width(84));
                    int row = EditorGUILayout.Popup(activePatch + 1, patchLabels, GUILayout.MinWidth(130));
                    if (row != activePatch + 1)
                    {
                        activePatch = row - 1;
                        if (activePatch >= 0) pan = crossings[activePatch].Point - focus;
                        Repaint();
                    }
                    using (new EditorGUI.DisabledScope(activePatch < 0 || !EditableFrame || strokePiece != null))
                        if (OverlayButton(new GUIContent("Preview fix", "Preview a local correction. Apply to keep it.")))
                            BeginPatchPreview();
                }
                if (activePatch >= 0 && !EditableFrame)
                    OverlayLabel("Select an authored frame to correct this patch.", EditorStyles.wordWrappedMiniLabel);
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Gap (mm)", EditorStyles.whiteMiniLabel, GUILayout.Width(55));
                EditorGUI.BeginChangeCheck();
                brushGapMm = EditorGUILayout.Slider(brushGapMm, 0, 10, GUILayout.Width(95));
                GUILayout.Label("Strength", EditorStyles.whiteMiniLabel, GUILayout.Width(48));
                brushStrength = EditorGUILayout.Slider(brushStrength, .05f, 1f, GUILayout.Width(95));
                if (EditorGUI.EndChangeCheck()) UpdatePatchPreview();
                using (new EditorGUI.DisabledScope(patchMotion == null))
                    if (OverlayButton(new GUIContent("Apply"))) ApplyPatchPreview();
                if (OverlayButton(new GUIContent("Cancel", "Discard this preview (Esc)."))) CancelPatchPreview();
            }
            if (!string.IsNullOrEmpty(patchFeedback))
                OverlayLabel(patchFeedback, EditorStyles.wordWrappedMiniLabel);
        }

        private void ReserveToolbarHelp()
        {
            var rect = GUILayoutUtility.GetRect(0, 31, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint) toolbarHelpRect = rect;
        }

        private void DrawVertexSize()
        {
            vertexSize = EditorGUILayout.Slider(new GUIContent("Vertex display size"), vertexSize, 2, 10, GUILayout.Width(285));
        }

        private bool OverlayButton(GUIContent content, bool active = false, params GUILayoutOption[] options)
        {
            if (floatingButton == null)
            {
                floatingButton = new GUIStyle(EditorStyles.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    clipping = TextClipping.Clip,
                    padding = new RectOffset(9, 9, 0, 0),
                    fixedHeight = 25,
                    margin = new RectOffset(3, 3, 2, 2),
                    stretchWidth = false
                };
                floatingButton.normal.textColor = new Color(.93f, .96f, 1f);
                floatingButton.hover.textColor = new Color(1f, .85f, .53f);
                floatingButton.active.textColor = Color.white;
            }
            var oldColor = floatingButton.normal.textColor;
            if (active)
                floatingButton.normal.textColor = new Color(1f, .82f, .47f);
            var button = GUILayoutUtility.GetRect(content, floatingButton, options);
            if (Event.current.type == EventType.Repaint)
            {
                var fill = active ? new Color(.29f, .22f, .12f) : new Color(.19f, .18f, .16f);
                if (!GUI.enabled)
                    fill = new Color(.15f, .145f, .13f);
                EditorGUI.DrawRect(button, fill);
            }
            SetCenterHelp(button, content.tooltip);
            bool clicked = GUI.Button(button, new GUIContent(content.text, content.image), floatingButton);
            floatingButton.normal.textColor = oldColor;
            return clicked;
        }

        private void OverlayLabel(string label, GUIStyle source)
        {
            floatingLabel ??= new GUIStyle(EditorStyles.wordWrappedMiniLabel);
            if (floatingLabel.fontStyle != source.fontStyle || floatingLabel.fontSize != source.fontSize)
            {
                floatingLabel.fontStyle = source.fontStyle;
                floatingLabel.fontSize = source.fontSize;
            }
            floatingLabel.normal.textColor = new Color(.93f, .96f, 1f);
            EditorGUILayout.LabelField(label, floatingLabel);
        }

        private void DrawWeight(float width)
        {
            OverlayHeading("Fit");
            GUILayout.Label($"Body {(previewFit.Reverse ? 100 - weight : weight):g}%", EditorStyles.whiteMiniLabel, GUILayout.Width(65));
            float nextWeight = EditorGUILayout.Slider(weight, 0, Mathf.Max(100, frameWeights[frameWeights.Length - 1]), GUILayout.Width(width));
            if (frameWeights.Length > 1)
            {
                int index = Array.IndexOf(frameWeights, weight);
                int selected = EditorGUILayout.Popup(index, Array.ConvertAll(frameWeights, w => $"Frame {w:g}"), GUILayout.Width(90));
                if (selected >= 0 && selected != index) nextWeight = frameWeights[selected];
            }
            LastControlHelp("Compare the original fit at 0 with the full generated shape at 100.");
            foreach (int preset in Presets)
                if (OverlayButton(new GUIContent(preset.ToString(), preset == 0 ? "Show the original fit." :
                    preset == 50 ? "Check the fit halfway through the change." : "Show the full fit. Sculpting is available here."), preset == weight, GUILayout.Width(37)))
                    nextWeight = preset;
            if (nextWeight != weight)
            {
                EndStroke();
                ClearSpan();
                weight = nextWeight;
                Bake();
            }
            GUILayout.Space(14);
        }

        private void DrawViews()
        {
            for (int i = 0; i < ViewLabels.Length; i++)
            {
                string label = i == 1 && viewIndex == 1 ? (leftSide ? "Side: Left" : "Side: Right") : ViewLabels[i];
                if (!OverlayButton(new GUIContent(label, $"Reset the camera to the {ViewLabels[i].ToLowerInvariant()} view."), i == viewIndex, GUILayout.MinWidth(42)))
                    continue;
                pan = Vector3.zero;
                if (i == 1) leftSide = viewIndex == 1 && !leftSide;
                viewIndex = i;
                yaw = i == 1 ? (leftSide ? -90 : 90) : i == 2 ? 180 : 0;
                elevation = i == 3 ? 90 : i == 4 ? -90 : 0;
                InvalidateVertexVisibility();
            }
            GUILayout.Space(14);
        }

        private static void OverlayHeading(string text)
        {
            GUILayout.Label(text, EditorStyles.whiteMiniLabel, GUILayout.Width(35), GUILayout.Height(25));
        }

        private void DrawHistory()
        {
            GUILayout.Space(14);
            using (new EditorGUI.DisabledScope(brushUndo.Count == 0 || strokePiece != null))
                if (OverlayButton(new GUIContent("↶ Undo", "Undo the last preview edit (Ctrl+Z)."), false, GUILayout.Width(62)))
                    MoveHistory(brushUndo, brushRedo);
            using (new EditorGUI.DisabledScope(brushRedo.Count == 0 || strokePiece != null))
                if (OverlayButton(new GUIContent("↷ Redo", "Redo the last preview edit (Ctrl+Y)."), false, GUILayout.Width(62)))
                    MoveHistory(brushRedo, brushUndo);
            using (new EditorGUI.DisabledScope(brushUndo.Count == 0 || strokePiece != null))
                if (OverlayButton(new GUIContent("✓ Save Corrections", "Save the previewed fit and edits as restorable meshes."), true, GUILayout.Width(126)))
                    SaveCorrections();
        }

        private void DrawEditTools()
        {
            OverlayHeading("Edit");
            if (OverlayButton(new GUIContent("Box", "B toggles box selection. Drag to select vertices on the active mesh."), boxMode))
                ToggleBoxMode();
            if (OverlayButton(SculptHelp, sculpt))
            {
                CancelBox();
                boxMode = false;
                sculpt = !sculpt;
                straighten = false;
                ClearSpan();
                if (sculpt && !EditableFrame)
                {
                    weight = 100;
                    Bake();
                }
            }
            if (OverlayButton(new GUIContent("↔  Straighten Span",
                "Pick the two ends of a strap. Review the highlighted strip, then apply."), straighten))
            {
                CancelBox();
                boxMode = false;
                straighten = !straighten;
                sculpt = false;
                ClearSpan();
                if (straighten && !EditableFrame)
                {
                    weight = 100;
                    Bake();
                }
            }
            if (OverlayButton(new GUIContent(clippingWork == null ? "◉  Scan Clipping" : "Cancel Check",
                "Check the selected mesh against the body and visible garments. Existing body clipping appears in gray.")))
            {
                if (clippingWork == null)
                    CheckClipping();
                else
                {
                    ClearClipping();
                    status = null;
                }
            }
        }

        private void DrawToolSettings()
        {
            GUILayout.Space(14);
            if (straighten)
            {
                GUILayout.Label("Span strength", EditorStyles.whiteMiniLabel, GUILayout.Width(80), GUILayout.Height(25));
                spanStrength = EditorGUILayout.Slider(spanStrength, 0f, 1f, GUILayout.Width(124));
                LastControlHelp("Set how much the section between your two clicks straightens.");
                using (new EditorGUI.DisabledScope(!EditableFrame || spanOffsets == null || spanStrength <= 0))
                    if (OverlayButton(new GUIContent("✓ Apply", "Apply the selected span (Enter).")))
                        ApplySpan();
                if (OverlayButton(new GUIContent("Reset", "Clear both anchors (Escape).")))
                    ClearSpan();
            }
            else if (sculpt)
            {
                OverlayHeading("Brush");
                brushMode = (Brush)EditorGUILayout.Popup((int)brushMode, BrushNames, GUILayout.Width(122));
                LastControlHelp(BrushHint());
                if (brushMode == Brush.Multiply)
                {
                    transferMultiplier = EditorGUILayout.Slider(transferMultiplier, .5f, 1.5f, GUILayout.Width(76));
                    LastControlHelp("Scale the existing movement in the brushed area. 1 leaves it as it is.");
                }
                GUILayout.Label("Radius (mm)", EditorStyles.whiteMiniLabel, GUILayout.Width(67));
                brushRadius = EditorGUILayout.Slider(brushRadius * 1000, size * 15, size * 500, GUILayout.Width(102)) / 1000;
                LastControlHelp("Set the size of the brush. Scroll while dragging to resize it.");

            }


        }

        private void DrawSurfaceBrushSettings()
        {
            GUILayout.Label("Strength", EditorStyles.whiteMiniLabel, GUILayout.Width(51));
            brushStrength = EditorGUILayout.Slider(brushStrength, .05f, 1f, GUILayout.Width(112));
            if (brushMode == Brush.ClearSkin)
            {
                GUILayout.Label("Gap (mm)", EditorStyles.whiteMiniLabel, GUILayout.Width(53));
                brushGapMm = EditorGUILayout.Slider(brushGapMm, 0, 10, GUILayout.Width(105));
                brushReference = SafeBrushReference;
                var names = new string[pieces.Count - 1];
                names[0] = "Body";
                for (int i = 1, row = 1; i < pieces.Count; i++)
                    if (i != selectedMesh + 1) names[row++] = editableNames[i - 1].text;
                int current = brushReference > selectedMesh + 1 ? brushReference - 1 : brushReference;
                int choice = EditorGUILayout.Popup(current, names, GUILayout.Width(115));
                brushReference = choice > selectedMesh ? choice + 1 : choice;
                LastControlHelp("Reference surface for the clipping brush.");
            }
            if (brushMode == Brush.Restore)
            {
                restoreOriginal = EditorGUILayout.Popup(restoreOriginal ? 1 : 0,
                    RestoreTargets, GUILayout.Width(145)) == 1;
                LastControlHelp("Restore generated motion or recover the original fabric shape in place.");
            }
        }

        private static readonly string[] RestoreTargets = { "As generated", "Original in place" };
        private int SafeBrushReference => brushReference >= pieces.Count || brushReference == selectedMesh + 1 ? 0 : brushReference;

        private void DrawSymmetryToggle(string label, string help, int axis)
        {
            if (OverlayButton(new GUIContent(label, help), (symmetryAxes & axis) != 0))
                symmetryAxes ^= axis;
        }

        private string BrushHint() => brushMode switch
        {
            Brush.PushOut => "Drag up to push along the fabric normal · Alt to pick through skin",
            Brush.Smooth => "Drag to smooth the blendshape motion · Alt to pick through skin",
            Brush.Multiply => "Set the multiplier, then click to scale motion inside the brush",
            Brush.ElasticDeform => "Drag the fabric · Nearby vertices follow with a soft falloff",
            Brush.Protect => "Click to protect vertices from transferred movement and corrections",
            Brush.Unprotect => "Click protected vertices to restore their movement",
            Brush.Restore => "Drag to restore the selected reference inside the brush",
            Brush.Relax => "Drag to even out vertex spacing while holding the garment boundary",
            Brush.ClearSkin => "Drag over a poke-through to lift fabric toward the selected surface gap",
            _ => "Left-drag to grab · Alt-grab through skin · Shift-drag for depth · Scroll to resize"
        };

        private bool BrushThroughBody => hoverThroughBody || brushMode == Brush.ClearSkin;

        private void DrawMeshPicker()
        {
            OverlayHeading("Mesh");
            int selection = EditorGUILayout.Popup(selectedMesh, editableNames, GUILayout.ExpandWidth(true), GUILayout.MinWidth(130));
            LastControlHelp("Choose the clothing piece you want to edit. Other pieces stay visible for comparison.");
            if (selection != selectedMesh) SelectMesh(selection);
            if (OverlayButton(new GUIContent("Isolate", "Show only the selected garment and the body. Other pieces stop blocking picks."),
                isolateMesh, GUILayout.Width(52)))
            {
                isolateMesh = !isolateMesh;
                viewImage = null;
                InvalidateVertexVisibility();
                ClearClipping();
                status = null;
                Repaint();
            }
        }

        private void SelectMesh(int selection)
        {
            EndStroke();
            CancelBox();
            ClearSpan();
            status = null;
            selectedMesh = selection;
            ClearClipping();
            InvalidateVertexVisibility();
            if (!selectionHighlight)
            {
                var shader = Shader.Find("Hidden/ShapeForge/SelectionHighlight");
                if (shader)
                    selectionHighlight = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
            selectionHighlightUntil = EditorApplication.timeSinceStartup + 2;
            viewImage = null;
            Repaint();
        }

        private bool IsVisible(Piece piece) =>
            !isolateMesh || piece == pieces[0] || piece == pieces[selectedMesh + 1];

        private void DrawVisibility()
        {
            EditorGUI.BeginChangeCheck();
            cullBackfaces = EditorGUILayout.ToggleLeft("Cull backfaces", cullBackfaces, GUILayout.Width(115));
            LastControlHelp("Skip vertices facing away from the camera.");
            zTest = EditorGUILayout.ToggleLeft("Z-test", zTest, GUILayout.Width(64));
            LastControlHelp("Skip vertices hidden by the body or another surface.");
            if (EditorGUI.EndChangeCheck())
            {
                EndStroke();
                ClearSpan();
                InvalidateVertexVisibility();
            }
        }

        private void HandleHistoryShortcut()
        {
            var input = Event.current;
            if (input.type != EventType.KeyDown || !(input.control || input.command) || input.alt ||
                EditorGUIUtility.editingTextField || strokePiece != null)
                return;
            if (input.keyCode == KeyCode.Z && !input.shift)
                MoveHistory(brushUndo, brushRedo);
            else if (input.keyCode == KeyCode.Y || (input.keyCode == KeyCode.Z && input.shift))
                MoveHistory(brushRedo, brushUndo);
            else
                return;
            input.Use();
        }

        private void HandleSpanShortcut()
        {
            var input = Event.current;
            if (boxMode || !straighten || input.type != EventType.KeyDown || EditorGUIUtility.editingTextField)
                return;
            if (input.keyCode == KeyCode.Escape)
                ClearSpan();
            else if ((input.keyCode == KeyCode.Return || input.keyCode == KeyCode.KeypadEnter) &&
                EditableFrame && spanOffsets != null && spanStrength > 0)
                ApplySpan();
            else
                return;
            input.Use();
        }

        private void MoveHistory(Stack<(Piece Piece, float Weight, Vector3[] Prior, bool[] Mask)> from,
            Stack<(Piece Piece, float Weight, Vector3[] Prior, bool[] Mask)> to)
        {
            if (from.Count == 0)
                return;
            var (piece, frameWeight, prior, mask) = from.Pop();
            weight = frameWeight;
            if (prior == null)
            {
                to.Push((piece, weight, null, piece.Protection));
                ApplyProtection(piece, mask);
            }
            else
            {
                to.Push((piece, weight, CurrentMotion(piece), null));
                ApplyDraft(piece, prior);
            }
            ClearSpan();
        }

        private void DrawViewport(Rect rect, bool overControls)
        {
            if (sculpt && EditableFrame)
                EditorGUIUtility.AddCursorRect(rect, MouseCursor.MoveArrow);
            Vector3 horizontal = Quaternion.AngleAxis(yaw, up) * forward;
            var tilt = Quaternion.AngleAxis(-elevation, Vector3.Cross(up, horizontal));
            Vector3 direction = tilt * horizontal;
            var rotation = Quaternion.LookRotation(-direction, tilt * up);
            if (rect.width <= 0 || rect.height <= 0)
                return;

            var camera = preview.camera;
            camera.orthographic = false;
            camera.fieldOfView = 60f;
            float distance = size * zoom * Mathf.Max(1, rect.height / rect.width) / Mathf.Tan(30f * Mathf.Deg2Rad);
            camera.nearClipPlane = Mathf.Max(.0001f, distance * .01f);
            camera.farClipPlane = distance + size * 12;
            camera.transform.SetPositionAndRotation(focus + pan + direction * distance, rotation);
            camera.aspect = rect.width / rect.height;
            if (!overControls || flying || panning || boxDragging || strokePiece != null)
                Navigate(rect, rotation, distance);
            if (Event.current.type != EventType.Repaint)
                return;
            // Capture the scene material before entering the isolated preview render.
            var sceneCamera = SceneView.lastActiveSceneView ? SceneView.lastActiveSceneView.camera : null;
            var sceneSky = sceneCamera ? sceneCamera.GetComponent<Skybox>() : null;
            var skyMaterial = sceneSky && sceneSky.material ? sceneSky.material : RenderSettings.skybox;
            bool showSky = sceneCamera && sceneCamera.clearFlags == CameraClearFlags.Skybox && skyMaterial;
            var background = sceneCamera ? sceneCamera.backgroundColor : camera.backgroundColor;
            var sky = showSky ? skyMaterial : null;
            if (!viewImage || renderedRect != rect || renderedPosition != camera.transform.position ||
                renderedRotation != rotation || renderedSky != sky || renderedBackground != background ||
                renderedNear != camera.nearClipPlane || renderedFar != camera.farClipPlane)
            {
                using var sample = RenderSample.Auto();
                preview.BeginPreview(rect, GUIStyle.none);
                camera.backgroundColor = background;
                camera.clearFlags = showSky ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor;
                var previewSky = camera.GetComponent<Skybox>();
                if (showSky && !previewSky)
                    previewSky = camera.gameObject.AddComponent<Skybox>();
                if (previewSky)
                {
                    previewSky.enabled = showSky;
                    previewSky.material = showSky ? skyMaterial : null;
                }
                foreach (var piece in pieces)
                {
                    if (!IsVisible(piece))
                        continue;
                    var materials = piece.Materials;
                    for (int i = 0; i < Mathf.Min(materials.Length, piece.Baked.subMeshCount); i++)
                    {
                        if (materials[i])
                            preview.DrawMesh(piece.Baked, piece.Renderer.localToWorldMatrix, materials[i], i);
                    }
                }
                if (selectionHighlight && selectionHighlightUntil > EditorApplication.timeSinceStartup)
                {
                    var selected = pieces[selectedMesh + 1];
                    for (int i = 0; i < selected.Baked.subMeshCount; i++)
                        preview.DrawMesh(selected.Baked, selected.Renderer.localToWorldMatrix, selectionHighlight, i);
                }
                preview.Render();
                viewImage = preview.EndPreview();
                renderedRect = rect;
                renderedPosition = camera.transform.position;
                renderedRotation = rotation;
                renderedSky = sky;
                renderedBackground = background;
                renderedNear = camera.nearClipPlane;
                renderedFar = camera.farClipPlane;
            }
            if (viewImage)
                GUI.DrawTexture(rect, viewImage, ScaleMode.StretchToFill, false);
            DrawVertexDots(rect);
            DrawSelection(rect);
            if (!boxMode && (!overControls || strokePiece != null))
                DrawFootprint(rect);
            DrawSpan(rect);
            if (!boxMode && !overControls)
                DrawPickCandidate(rect);
            if (sculpt && (brushMode is Brush.Protect or Brush.Unprotect))
                DrawProtection(rect);
            if (!showClipMarkers) return;
            for (int i = 0; i < crossings.Count; i++)
            {
                var marker = crossings[i];
                var point = camera.WorldToViewportPoint(marker.Point);
                float half = i == activePatch ? 5 : 3;
                if (point.z > 0 && point.x >= 0 && point.x <= 1 && point.y >= 0 && point.y <= 1)
                    EditorGUI.DrawRect(new Rect(rect.x + rect.width * point.x - half,
                        rect.y + rect.height * (1 - point.y) - half, half * 2, half * 2), i == activePatch ? Color.yellow : marker.Color);
            }
        }

        private void DrawProtection(Rect rect)
        {
            var piece = pieces[selectedMesh + 1];
            if (piece.Protection == null) return;
            for (int i = 0; i < piece.World.Length; i++)
            {
                if (!piece.Protection[i]) continue;
                var point = preview.camera.WorldToViewportPoint(piece.World[i]);
                if (point.z <= 0 || point.x < 0 || point.x > 1 || point.y < 0 || point.y > 1 ||
                    !VertexVisible(piece, i)) continue;
                EditorGUI.DrawRect(new Rect(rect.x + rect.width * point.x - 2, rect.y + rect.height * (1 - point.y) - 2, 4, 4), new Color(.36f, .90f, .65f));
            }
        }

        private void Navigate(Rect rect, Quaternion rotation, float distance)
        {
            int control = GUIUtility.GetControlID(FocusType.Passive, rect);
            if (Event.current.isMouse)
                hoverMouse = Event.current.mousePosition;
            if ((Event.current.isMouse || Event.current.isKey) && hoverThroughBody != Event.current.alt)
            {
                hoverThroughBody = Event.current.alt;
                if (sculpt)
                    Repaint();
            }
            var input = Event.current;
            if (flying && (input.type == EventType.KeyDown || input.type == EventType.KeyUp) &&
                (input.keyCode == KeyCode.W || input.keyCode == KeyCode.A ||
                 input.keyCode == KeyCode.S || input.keyCode == KeyCode.D))
            {
                if (input.type == EventType.KeyDown)
                    flyKeys.Add(input.keyCode);
                else
                    flyKeys.Remove(input.keyCode);
                input.Use();
            }

            switch (input.GetTypeForControl(control))
            {
                case EventType.MouseDown when !PreviewingPatch && boxMode && input.button == 0 && GUIUtility.hotControl == 0 && rect.Contains(input.mousePosition):
                    boxDragging = true;
                    boxControl = control;
                    boxStart = boxEnd = input.mousePosition;
                    GUIUtility.hotControl = control;
                    input.Use();
                    Repaint();
                    break;
                case EventType.MouseDrag when boxDragging && GUIUtility.hotControl == control:
                    boxEnd = input.mousePosition;
                    input.Use();
                    Repaint();
                    break;
                case EventType.MouseUp when boxDragging && input.button == 0 && GUIUtility.hotControl == control:
                    boxEnd = input.mousePosition;
                    SelectBox(rect, input.shift, input.control || input.command, input.alt);
                    CancelBox();
                    input.Use();
                    Repaint();
                    break;
                case EventType.MouseMove when (sculpt || straighten) && EditableFrame && !flying:
                    hoverMouse = input.mousePosition;
                    Repaint();
                    break;
                case EventType.ScrollWheel when strokePiece != null:
                    brushRadius = Mathf.Clamp(brushRadius * Mathf.Exp(-input.delta.y * .08f), size * .015f, size * .5f);
                    if (strokeMoved && !SurfaceBrush)
                        QueueDrag(rect, strokeCurrent, strokeInDepth);
                    input.Use();
                    Repaint();
                    break;
                case EventType.ScrollWheel when flying:
                    flySpeed = Mathf.Clamp(flySpeed * Mathf.Exp(-input.delta.y * .08f), .05f, 20f);
                    input.Use();
                    break;
                case EventType.ScrollWheel when rect.Contains(input.mousePosition):
                    zoom = Mathf.Clamp(zoom * Mathf.Exp(input.delta.y * .05f), .15f, 8f);
                    input.Use();
                    Repaint();
                    break;
                case EventType.MouseDown when !PreviewingPatch && input.button == 0 && !panning && !flying && rect.Contains(input.mousePosition) &&
                    straighten && EditableFrame:
                    SelectSpan(rect, input.mousePosition);
                    input.Use();
                    break;
                case EventType.MouseDown when input.button == 0 && rect.Contains(input.mousePosition) &&
                    sculpt && EditableFrame:
                    if (BeginStroke(rect, input.mousePosition, input.alt || brushMode == Brush.ClearSkin))
                    {
                        if (SurfaceBrush)
                        {
                            strokeControl = control;
                            GUIUtility.hotControl = control;
                            lastStampPosition = input.mousePosition;
                            StampBrush();
                        }
                        else if (brushMode is Brush.Multiply or Brush.Protect or Brush.Unprotect)
                        {
                            StampBrush();
                            EndStroke();
                        }
                        else
                        {
                            strokeControl = control;
                            GUIUtility.hotControl = control;
                        }
                    }
                    input.Use();
                    break;
                case EventType.MouseDrag when strokePiece != null && GUIUtility.hotControl == control:
                    if (SurfaceBrush)
                    {
                        stampQueued = true;
                        stampViewport = rect;
                        stampPosition = input.mousePosition;
                        stampThroughBody = input.alt || brushMode == Brush.ClearSkin;
                    }
                    else
                        QueueDrag(rect, input.mousePosition, input.shift);
                    input.Use();
                    break;
                case EventType.MouseUp when input.button == 0 && strokePiece != null && GUIUtility.hotControl == control:
                    GUIUtility.hotControl = 0;
                    EndStroke();
                    input.Use();
                    break;
                case EventType.MouseDown when input.button == 2 && GUIUtility.hotControl == 0 && rect.Contains(input.mousePosition):
                    panning = true;
                    flyControl = control;
                    GUIUtility.hotControl = control;
                    input.Use();
                    break;
                case EventType.MouseDrag when panning && GUIUtility.hotControl == control:
                    float unitsPerPixel = 2f * distance * Mathf.Tan(30f * Mathf.Deg2Rad) / rect.height;
                    pan += rotation * new Vector3(-input.delta.x, input.delta.y, 0) * unitsPerPixel;
                    input.Use();
                    Repaint();
                    break;
                case EventType.MouseUp when input.button == 2 && panning && GUIUtility.hotControl == control:
                    StopFlying();
                    input.Use();
                    break;
                case EventType.MouseDown when input.button == 1 && GUIUtility.hotControl == 0 && rect.Contains(input.mousePosition):
                    GUIUtility.hotControl = control;
                    flying = true;
                    flyControl = control;
                    flyRotation = rotation;
                    flyDistance = distance;
                    lastFlyUpdate = EditorApplication.timeSinceStartup;
                    input.Use();
                    break;
                case EventType.MouseDrag when flying && GUIUtility.hotControl == control:
                    yaw = Mathf.Repeat(yaw + input.delta.x * .55f, 360);
                    elevation = Mathf.Clamp(elevation + input.delta.y * .55f, -90, 90);
                    Vector3 heading = Quaternion.AngleAxis(yaw, up) * forward;
                    var tilt = Quaternion.AngleAxis(-elevation, Vector3.Cross(up, heading));
                    var next = Quaternion.LookRotation(-(tilt * heading), tilt * up);
                    pan += (next * Vector3.forward - rotation * Vector3.forward) * distance;
                    flyRotation = next;
                    flyDistance = distance;
                    viewIndex = -1;
                    input.Use();
                    Repaint();
                    break;
                case EventType.MouseUp when input.button == 1 && flying && GUIUtility.hotControl == control:
                    StopFlying();
                    input.Use();
                    break;
            }
        }

        private void Update()
        {
            if (selectionHighlightUntil > 0 && EditorApplication.timeSinceStartup >= selectionHighlightUntil)
            {
                selectionHighlightUntil = 0;
                viewImage = null;
                Repaint();
            }
            AdvanceClipping();
            if (stampQueued && strokePiece != null && EditorApplication.timeSinceStartup >= nextDragUpdate)
            {
                ApplyQueuedStamp();
                nextDragUpdate = EditorApplication.timeSinceStartup + 1.0 / 24;
            }
            if (pendingDrag && strokePiece != null && EditorApplication.timeSinceStartup >= nextDragUpdate)
            {
                pendingDrag = false;
                ApplyDraft(strokePiece, EvaluateDrag());
                nextDragUpdate = EditorApplication.timeSinceStartup + 1.0 / 30;
            }
            if (!flying)
                return;
            double now = EditorApplication.timeSinceStartup;
            float elapsed = Mathf.Min(.05f, (float)(now - lastFlyUpdate));
            lastFlyUpdate = now;
            var direction = Vector3.zero;
            if (flyKeys.Contains(KeyCode.W)) direction += Vector3.forward;
            if (flyKeys.Contains(KeyCode.S)) direction += Vector3.back;
            if (flyKeys.Contains(KeyCode.A)) direction += Vector3.left;
            if (flyKeys.Contains(KeyCode.D)) direction += Vector3.right;
            if (direction == Vector3.zero)
                return;
            pan += flyRotation * direction.normalized * (flyDistance * elapsed * flySpeed);
            Repaint();
        }

        private void OnLostFocus()
        {
            StopFlying();
            CancelBox();
            EndStroke();
        }

        private void StopFlying()
        {
            if ((flying || panning) && GUIUtility.hotControl == flyControl)
                GUIUtility.hotControl = 0;
            panning = false;
            flying = false;
            flyKeys.Clear();
        }

        private void HandleBoxShortcut()
        {
            var input = Event.current;
            if (input.type != EventType.KeyDown || EditorGUIUtility.editingTextField || input.control || input.command || input.alt)
                return;
            if (input.keyCode == KeyCode.B)
                ToggleBoxMode();
            else if (input.keyCode == KeyCode.Escape && (boxMode || boxDragging || pieces[selectedMesh + 1].Selection != null))
            {
                if (!boxDragging) ClearSelection(pieces[selectedMesh + 1]);
                CancelBox();
            }
            else return;
            input.Use();
            Repaint();
        }

        private void ToggleBoxMode()
        {
            EndStroke();
            CancelBox();
            boxMode = !boxMode;
        }

        private void CancelBox()
        {
            if (boxDragging && GUIUtility.hotControl == boxControl) GUIUtility.hotControl = 0;
            boxDragging = false;
        }

        private Rect BoxRect => Rect.MinMaxRect(Mathf.Min(boxStart.x, boxEnd.x), Mathf.Min(boxStart.y, boxEnd.y),
            Mathf.Max(boxStart.x, boxEnd.x), Mathf.Max(boxStart.y, boxEnd.y));

        private void SelectBox(Rect viewport, bool add, bool subtract, bool through)
        {
            var piece = pieces[selectedMesh + 1];
            if (piece.BrushWeld == null) BuildBrushGraph(piece);
            var selection = add || subtract ? piece.Selection ?? new bool[piece.World.Length] : new bool[piece.World.Length];
            var box = BoxRect;
            if (box.width >= 2 && box.height >= 2)
                for (int i = 0; i < piece.World.Length; i++)
                {
                    var point = preview.camera.WorldToViewportPoint(piece.World[i]);
                    var screen = new Vector2(viewport.x + point.x * viewport.width, viewport.y + (1 - point.y) * viewport.height);
                    if (point.z > 0 && viewport.Contains(screen) && box.Contains(screen) &&
                        (through || VertexVisible(piece, i)))
                        selection[piece.BrushWeld[i]] = !subtract;
                }
            int count = 0;
            foreach (bool selected in selection)
                if (selected) count++;
            piece.SelectionCount = count;
            piece.Selection = count > 0 ? selection : null;
        }

        private static void ClearSelection(Piece piece)
        {
            piece.Selection = null;
            piece.SelectionCount = 0;
        }

        private void DrawSelection(Rect viewport)
        {
            var piece = pieces[selectedMesh + 1];
            if (piece.Selection != null)
                for (int i = 0; i < piece.World.Length; i++)
                {
                    if (!piece.Selection[piece.BrushWeld[i]] || piece.BrushWeld[i] != i) continue;
                    var point = preview.camera.WorldToViewportPoint(piece.World[i]);
                    if (point.z <= 0 || point.x < 0 || point.y < 0 || point.x > 1 || point.y > 1 || !VertexVisible(piece, i))
                        continue;
                    float half = vertexSize * .5f;
                    EditorGUI.DrawRect(new Rect(viewport.x + point.x * viewport.width - half,
                        viewport.y + (1 - point.y) * viewport.height - half, vertexSize, vertexSize),
                        new Color(0.30f, .76f, 1f, .9f));
                }
            if (boxDragging)
            {
                var box = BoxRect;
                box.xMin = Mathf.Max(box.xMin, viewport.xMin);
                box.yMin = Mathf.Max(box.yMin, viewport.yMin);
                box.xMax = Mathf.Min(box.xMax, viewport.xMax);
                box.yMax = Mathf.Min(box.yMax, viewport.yMax);
                if (box.width > 0 && box.height > 0)
                {
                    EditorGUI.DrawRect(box, new Color(.30f, .76f, 1f, .18f));
                    var border = new Color(.30f, .76f, 1f);
                    EditorGUI.DrawRect(new Rect(box.x, box.y, box.width, 1), border);
                    EditorGUI.DrawRect(new Rect(box.x, box.yMax - 1, box.width, 1), border);
                    EditorGUI.DrawRect(new Rect(box.x, box.y, 1, box.height), border);
                    EditorGUI.DrawRect(new Rect(box.xMax - 1, box.y, 1, box.height), border);
                }
            }
        }

        private static bool InSelection(Piece piece, int vertex) =>
            piece.Selection == null || piece.Selection[piece.BrushWeld[vertex]];

        private Piece Pick(Rect rect, Vector2 mouse, out Vector3 center, out (int A, int B, int C) face, bool throughBody = false)
        {
            face = default;
            var ray = preview.camera.ViewportPointToRay(new Vector3((mouse.x - rect.x) / rect.width,
                1 - (mouse.y - rect.y) / rect.height));
            if (selectedMesh + 1 >= pieces.Count)
            {
                center = default;
                return null;
            }
            var piece = pieces[selectedMesh + 1];
            var surface = PickSurface(piece);
            float winding = cullBackfaces ? Mathf.Sign(piece.Renderer.localToWorldMatrix.determinant) : 0f;
            if (surface == null || !surface.Raycast(ray, float.PositiveInfinity, winding, out var hit, out float distance) ||
                (zTest && IsOccluded(ray, distance, throughBody)))
            {
                center = default;
                return null;
            }
            face = (hit.A, hit.B, hit.C);
            center = ray.GetPoint(distance);
            return piece;
        }

        private static ShapeTransfer.TriangleSurface PickSurface(Piece piece)
        {
            if (piece.Faces.Length == 0)
                return null;
            if (piece.PickSurface == null)
                piece.PickSurface = new ShapeTransfer.TriangleSurface(piece.Baked, piece.World);
            else if (piece.PickSurfaceDirty)
                piece.PickSurface.Refit(piece.World);
            piece.PickSurfaceDirty = false;
            return piece.PickSurface;
        }

        private bool IsOccluded(Ray ray, float distance, bool throughBody)
        {
            float limit = distance - Mathf.Max(.00005f, size * .0005f);
            foreach (var piece in pieces)
            {
                if (!IsVisible(piece) || (throughBody && piece == pieces[0]))
                    continue;
                var surface = PickSurface(piece);
                if (surface != null && surface.Raycast(ray, limit, 0, out _, out _))
                    return true;
            }
            return false;
        }

        private void InvalidateVertexVisibility()
        {
            visibilityPiece = null;
            hoverBrushes = null;
            hoverBrushPiece = null;
        }

        private bool VertexVisible(Piece piece, int vertex, bool throughBody = false)
        {
            if (!cullBackfaces && !zTest)
                return true;
            var origin = preview.camera.transform.position;
            if (visibilityPiece != piece || !visibilityOrigin.Equals(origin) || visibilityThroughBody != throughBody)
            {
                visibilityPiece = piece;
                visibilityOrigin = origin;
                visibilityThroughBody = throughBody;
                if (vertexVisibility == null || vertexVisibility.Length != piece.World.Length)
                    vertexVisibility = new sbyte[piece.World.Length];
                else
                    Array.Clear(vertexVisibility, 0, vertexVisibility.Length);
            }

            if (vertexVisibility[vertex] == 0)
            {
                var offset = piece.World[vertex] - origin;
                bool facing = !cullBackfaces || vertex >= piece.WorldNormals.Length ||
                    Vector3.Dot(piece.WorldNormals[vertex], offset) <= 0;
                bool visible = facing && (!zTest || !IsOccluded(new Ray(origin, offset.normalized), offset.magnitude, throughBody));
                vertexVisibility[vertex] = visible ? (sbyte)1 : (sbyte)-1;
            }
            return vertexVisibility[vertex] > 0;
        }

        private void DrawPickCandidate(Rect rect)
        {
            if ((!sculpt && !straighten) || !EditableFrame || flying || panning || strokePiece != null ||
                !rect.Contains(hoverMouse))
                return;
            if (sculpt || Pick(rect, hoverMouse, out var center, out _, false) == null)
                return;
            var point = preview.camera.WorldToViewportPoint(center);
            float x = rect.x + rect.width * point.x;
            float y = rect.y + rect.height * (1 - point.y);
            EditorGUI.DrawRect(new Rect(x - 5, y - 1, 10, 2), Color.white);
            EditorGUI.DrawRect(new Rect(x - 1, y - 5, 2, 10), Color.white);
        }

        private void SelectSpan(Rect rect, Vector2 mouse)
        {
            var piece = Pick(rect, mouse, out var point, out var triangle);
            if (piece == null)
                return;
            if (spanPiece == null || spanOffsets != null)
            {
                spanPiece = piece;
                spanStart = point;
                spanSeed = triangle;
                spanOffsets = null;
                status = null;
                return;
            }
            if (spanPiece != piece)
            {
                status = "Pick both ends on the same garment.";
                return;
            }
            spanEnd = point;
            spanOffsets = FindSpanOffsets(piece, spanStart, spanEnd, spanSeed);
            if (spanOffsets != null)
                FilterSpanOffsets(piece, spanOffsets);
            status = spanOffsets == null ?
                "No narrow connected strip was found. Reset and pick the attachment points closer to the string." : null;
        }

        private void FilterSpanOffsets(Piece piece, Vector3[] offsets)
        {
            for (int i = 0; i < offsets.Length; i++)
                if (offsets[i].sqrMagnitude > 0 && !VertexVisible(piece, piece.BrushWeld[i]))
                    offsets[i] = Vector3.zero;
        }

        private static Vector3[] FindSpanOffsets(Piece piece, Vector3 start, Vector3 end,
            (int A, int B, int C) seed)
        {
            Vector3 chord = end - start;
            float length = chord.magnitude;
            if (length < 1e-5f)
                return null;
            if (piece.BrushWeld == null)
                BuildBrushGraph(piece);

            Vector3 axis = chord / length;
            float width = length * .16f;
            int count = piece.World.Length;
            var positions = new float[count];
            var included = new bool[count];
            var reached = new bool[count];
            var pending = new Queue<int>();
            for (int i = 0; i < count; i++)
            {
                var offset = piece.World[i] - start;
                positions[i] = Vector3.Dot(offset, axis) / length;
                included[i] = positions[i] >= -.04f && positions[i] <= 1.04f &&
                    Vector3.Cross(offset, axis).sqrMagnitude <= width * width;
            }
            Add(piece.BrushWeld[seed.A]);
            Add(piece.BrushWeld[seed.B]);
            Add(piece.BrushWeld[seed.C]);
            while (pending.Count > 0)
            {
                int vertex = pending.Dequeue();
                var edges = piece.BrushEdges[vertex];
                if (edges != null)
                    foreach (var edge in edges)
                        Add(edge.Target);
            }

            // Estimate a single centerline so vertices across each cross section
            // move together. Leave the ends and their immediate surroundings fixed.
            const int stations = 9;
            var sums = new Vector3[stations];
            var weights = new float[stations];
            for (int i = 0; i < count; i++)
            {
                if (piece.BrushWeld[i] != i || !reached[i])
                    continue;
                float t = Mathf.Clamp01(positions[i]);
                var radial = piece.World[i] - Vector3.Lerp(start, end, t);
                for (int j = 0; j < stations; j++)
                {
                    float distance = Mathf.Abs(t * (stations - 1) - j);
                    float influence = Mathf.Max(0, 1.5f - distance);
                    sums[j] += radial * influence;
                    weights[j] += influence;
                }
            }
            if (weights[0] == 0 || weights[stations / 2] == 0 || weights[stations - 1] == 0)
                return null;
            for (int j = 0; j < stations; j++)
                if (weights[j] > 0)
                    sums[j] /= weights[j];

            var result = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                int vertex = piece.BrushWeld[i];
                float t = positions[vertex];
                if (!reached[vertex] || t <= 0 || t >= 1)
                    continue;
                float station = t * (stations - 1);
                int left = Mathf.FloorToInt(station);
                var offset = Vector3.Lerp(sums[left], sums[left + 1], station - left);
                float taper = Mathf.SmoothStep(0, 1, Mathf.Min(t, 1 - t) * 5f);
                result[i] = -Vector3.ProjectOnPlane(offset, axis) * taper;
            }
            return result;

            void Add(int vertex)
            {
                if (reached[vertex] || !included[vertex])
                    return;
                reached[vertex] = true;
                pending.Enqueue(vertex);
            }
        }

        private void DrawSpan(Rect rect)
        {
            if (!straighten || spanPiece == null || !EditableFrame)
                return;
            Mark(spanStart, 8);
            if (spanOffsets == null)
                return;
            Mark(spanEnd, 8);
            DrawAnchorGuide(rect);
            for (int i = 0; i < spanOffsets.Length; i++)
                if (spanOffsets[i].sqrMagnitude > 1e-14f)
                    Mark(spanPiece.World[i] + spanOffsets[i] * spanStrength, (int)vertexSize);

            void Mark(Vector3 world, int size)
            {
                var screen = preview.camera.WorldToViewportPoint(world);
                if (screen.z > 0 && screen.x >= 0 && screen.x <= 1 && screen.y >= 0 && screen.y <= 1)
                    EditorGUI.DrawRect(new Rect(rect.x + rect.width * screen.x - size * .5f,
                        rect.y + rect.height * (1 - screen.y) - size * .5f, size, size), Color.yellow);
            }
        }

        private void DrawAnchorGuide(Rect rect)
        {
            var a = preview.camera.WorldToViewportPoint(spanStart);
            var b = preview.camera.WorldToViewportPoint(spanEnd);
            if (a.z <= 0 || b.z <= 0)
                return;
            Handles.BeginGUI();
            var old = Handles.color;
            Handles.color = new Color(1f, .74f, .25f, .85f);
            Handles.DrawDottedLine(new Vector3(rect.x + a.x * rect.width, rect.y + (1 - a.y) * rect.height),
                new Vector3(rect.x + b.x * rect.width, rect.y + (1 - b.y) * rect.height), 4);
            Handles.color = old;
            Handles.EndGUI();
        }

        private void ApplySpan()
        {
            var piece = spanPiece;
            var previous = CurrentMotion(piece);
            var motion = (Vector3[])previous.Clone();
            var inverse = piece.Renderer.worldToLocalMatrix;
            for (int i = 0; i < motion.Length; i++)
                if (InSelection(piece, i) && (piece.Protection == null || !piece.Protection[i]))
                    motion[i] += inverse.MultiplyVector(spanOffsets[i]) * spanStrength;
            brushUndo.Push((piece, weight, previous, null));
            brushRedo.Clear();
            ApplyDraft(piece, motion);
            ClearSpan();
            CheckClipping();
        }

        private void ClearSpan()
        {
            spanPiece = null;
            spanOffsets = null;
        }

        private bool BeginStroke(Rect rect, Vector2 mouse, bool throughBody)
        {
            var piece = Pick(rect, mouse, out var center, out var face, throughBody);
            if (piece == null)
                return false;

            strokeStart = CurrentMotion(piece);
            strokePiece = piece;
            strokeThroughBody = throughBody;
            strokeNormals = piece.WorldNormals;
            if (brushMode == Brush.Smooth)
                strokeSmooth = SmoothMotion(piece, strokeStart);
            strokeMouse = mouse;
            strokeDepth = preview.camera.WorldToViewportPoint(center).z;
            strokeCenter = center;
            PrepareBrushFootprint(piece, center, face, throughBody, SurfaceBrush ? brushRadius : size * .5f);
            return true;
        }

        private void PrepareBrushFootprint(Piece piece, Vector3 center, (int A, int B, int C) face,
            bool throughBody, float reach)
        {
            strokeCenter = center;
            strokeBrushes = BrushCopies(piece, center, face, reach);
            strokeVisible = new bool[piece.World.Length];
            for (int i = 0; i < strokeVisible.Length; i++)
            {
                int vertex = piece.BrushWeld[i];
                if (vertex != i)
                    continue;
                foreach (var brush in strokeBrushes)
                {
                    if (float.IsPositiveInfinity(brush.Distances[i])) continue;
                    if (brush.Sign != Vector3.one || VertexVisible(piece, i, throughBody))
                    {
                        strokeVisible[i] = true;
                        break;
                    }
                }
            }
        }

        private void ApplyQueuedStamp()
        {
            stampQueued = false;
            if ((stampPosition - lastStampPosition).sqrMagnitude < 9) return;
            var piece = Pick(stampViewport, stampPosition, out var center, out var face, stampThroughBody);
            if (piece != strokePiece) return;
            lastStampPosition = stampPosition;
            strokeThroughBody = stampThroughBody;
            PrepareBrushFootprint(piece, center, face, stampThroughBody, brushRadius);
            StampBrush();
        }

        private void DrawVertexDots(Rect rect)
        {
            if (!straighten || spanOffsets != null || flying || panning || !EditableFrame || selectedMesh + 1 >= pieces.Count)
                return;
            var piece = spanPiece ?? pieces[selectedMesh + 1];
            var color = new Color(1f, .74f, .25f, .78f);
            bool throughBody = strokePiece != null ? strokeThroughBody : BrushThroughBody;
            for (int i = 0; i < piece.World.Length; i++)
            {
                var point = preview.camera.WorldToViewportPoint(piece.World[i]);
                if (point.z <= 0 || point.x < 0 || point.y < 0 || point.x > 1 || point.y > 1)
                    continue;
                if (!VertexVisible(piece, i, throughBody))
                    continue;
                EditorGUI.DrawRect(new Rect(rect.x + rect.width * point.x - vertexSize * .5f,
                    rect.y + rect.height * (1 - point.y) - vertexSize * .5f, vertexSize, vertexSize), color);
            }
        }

        private void DrawFootprint(Rect rect)
        {
            if (!sculpt || !EditableFrame || flying || panning)
                return;

            if (strokePiece == null && !rect.Contains(hoverMouse))
                return;
            var center = strokeCenter;
            (int A, int B, int C) face = default;
            bool throughBody = strokePiece != null ? strokeThroughBody : BrushThroughBody;
            var piece = strokePiece ?? Pick(rect, hoverMouse, out center, out face, throughBody);
            if (piece == null)
                return;
            var brushes = strokeBrushes ?? GetHoverBrushes(piece, center, face);

            var screen = preview.camera.WorldToViewportPoint(center);
            if (screen.z > 0)
            {
                var cursor = new Vector2(rect.x + rect.width * screen.x, rect.y + rect.height * (1 - screen.y));
                float radius = brushRadius * rect.height / (2f * screen.z * Mathf.Tan(30f * Mathf.Deg2Rad));
                GUI.BeginClip(rect);
                Handles.BeginGUI();
                Color original = Handles.color;
                Handles.color = strokePiece != null ? new Color(1f, .7f, .2f, .9f) : new Color(.2f, .65f, 1f, .9f);
                var circle = new Vector3[49];
                for (int i = 0; i < circle.Length; i++)
                {
                    float angle = i * (2f * Mathf.PI / (circle.Length - 1));
                    circle[i] = cursor - rect.position + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
                }
                Handles.DrawAAPolyLine(2f, circle);
                Handles.color = original;
                Handles.EndGUI();
                GUI.EndClip();
            }

            for (int i = 0; i < piece.World.Length; i++)
            {
                if (!InSelection(piece, i)) continue;
                float distance = float.PositiveInfinity;
                bool mirrored = false;
                foreach (var brush in brushes)
                {
                    if (brush.Distances[i] >= distance) continue;
                    distance = brush.Distances[i];
                    mirrored = brush.Sign != Vector3.one;
                }
                if (distance >= brushRadius || (!mirrored && (strokePiece != null
                    ? !strokeVisible[piece.BrushWeld[i]] : !VertexVisible(piece, piece.BrushWeld[i], throughBody))))
                    continue;
                var point = preview.camera.WorldToViewportPoint(piece.World[i]);
                if (point.z <= 0 || point.x < 0 || point.x > 1 || point.y < 0 || point.y > 1)
                    continue;
                float alpha = Mathf.Lerp(.2f, 1f, 1f - distance / brushRadius);
                EditorGUI.DrawRect(new Rect(rect.x + rect.width * point.x - vertexSize * .5f,
                    rect.y + rect.height * (1 - point.y) - vertexSize * .5f, vertexSize, vertexSize), mirrored ? new Color(1f, .65f, .22f, alpha) : new Color(.2f, .65f, 1f, alpha));
            }
        }

        private List<(float[] Distances, Vector3 Sign)> GetHoverBrushes(Piece piece, Vector3 center, (int A, int B, int C) face)
        {
            if (hoverBrushes == null || hoverBrushPiece != piece || !hoverBrushCenter.Equals(center) ||
                hoverBrushFace != face || hoverBrushRadius != brushRadius || hoverBrushAxes != symmetryAxes)
            {
                hoverBrushes = BrushCopies(piece, center, face, brushRadius);
                hoverBrushPiece = piece;
                hoverBrushCenter = center;
                hoverBrushFace = face;
                hoverBrushRadius = brushRadius;
                hoverBrushAxes = symmetryAxes;
            }
            return hoverBrushes;
        }

        private Transform SymmetryFrame => GeneratorAssets.AvatarRoot(body) ?? body.transform.root;

        private List<(float[] Distances, Vector3 Sign)> BrushCopies(Piece piece, Vector3 center,
            (int A, int B, int C) face, float limit)
        {
            var copies = new List<(float[], Vector3)> { (SurfaceDistances(piece, center, face, limit), Vector3.one) };
            if (symmetryAxes == 0)
                return copies;

            piece.BrushRest ??= piece.Original.vertices;
            piece.BrushSurface ??= new ShapeTransfer.TriangleSurface(piece.Original, piece.BrushRest);
            var a = piece.World[face.A];
            var ab = piece.World[face.B] - a;
            var ac = piece.World[face.C] - a;
            var normal = Vector3.Cross(ab, ac);
            float denominator = normal.sqrMagnitude;
            if (denominator < 1e-24f)
                return copies;
            float b = Vector3.Dot(Vector3.Cross(center - a, ac), normal) / denominator;
            float c = Vector3.Dot(Vector3.Cross(ab, center - a), normal) / denominator;
            var rest = piece.BrushRest[face.A] * (1 - b - c) + piece.BrushRest[face.B] * b + piece.BrushRest[face.C] * c;
            var localToAvatar = SymmetryFrame.worldToLocalMatrix * piece.Source.transform.localToWorldMatrix;
            var avatarToLocal = localToAvatar.inverse;
            var avatarPoint = localToAvatar.MultiplyPoint3x4(rest);
            var meshCenter = localToAvatar.MultiplyPoint3x4(piece.Original.bounds.center);
            // Left/right follows the avatar's centerline. The other planes bisect the garment.
            var pivot = new Vector3(0, meshCenter.y, meshCenter.z);
            float tolerance = piece.Original.bounds.size.magnitude * .025f;
            for (int mask = 1; mask < 8; mask++)
            {
                if ((mask & symmetryAxes) != mask)
                    continue;
                var sign = new Vector3((mask & 1) == 0 ? 1 : -1, (mask & 2) == 0 ? 1 : -1, (mask & 4) == 0 ? 1 : -1);
                var reflected = avatarToLocal.MultiplyPoint3x4(pivot + Vector3.Scale(avatarPoint - pivot, sign));
                if (!piece.BrushSurface.FindClosest(reflected, tolerance, out var hit, out var coordinates, out _))
                    continue;
                var mirroredCenter = piece.World[hit.A] * coordinates.x + piece.World[hit.B] * coordinates.y +
                    piece.World[hit.C] * coordinates.z;
                copies.Add((SurfaceDistances(piece, mirroredCenter, (hit.A, hit.B, hit.C), limit), sign));
            }
            return copies;
        }

        private static float[] SurfaceDistances(Piece piece, Vector3 center, (int A, int B, int C) seed, float limit)
        {
            if (piece.BrushWeld == null)
                BuildBrushGraph(piece);
            var weld = piece.BrushWeld;
            var edges = piece.BrushEdges;
            var queued = new SortedSet<(float Distance, int Vertex)>();
            var distances = new float[weld.Length];
            Array.Fill(distances, float.PositiveInfinity);

            Relax(weld[seed.A], Vector3.Distance(center, piece.World[seed.A]));
            Relax(weld[seed.B], Vector3.Distance(center, piece.World[seed.B]));
            Relax(weld[seed.C], Vector3.Distance(center, piece.World[seed.C]));
            while (queued.Count > 0)
            {
                var nearest = queued.Min;
                queued.Remove(nearest);
                if (edges[nearest.Vertex] == null)
                    continue;
                foreach (var edge in edges[nearest.Vertex])
                    Relax(edge.Target, nearest.Distance + edge.Length);
            }

            for (int i = 0; i < distances.Length; i++)
                distances[i] = distances[weld[i]];
            return distances;

            void Relax(int vertex, float distance)
            {
                if (distance >= limit || distance >= distances[vertex])
                    return;
                queued.Remove((distances[vertex], vertex));
                distances[vertex] = distance;
                queued.Add((distance, vertex));
            }
        }

        private static void BuildBrushGraph(Piece piece)
        {
            // Rest edge lengths keep the brush stable as the fabric is dragged.
            var rest = piece.Original.vertices;
            var worldRest = Array.ConvertAll(rest, piece.Renderer.localToWorldMatrix.MultiplyPoint3x4);
            var weld = piece.BrushWeld = ShapeTransfer.SeamPartners(piece.Original, rest);
            var edges = piece.BrushEdges = new List<(int Target, float Length)>[rest.Length];
            var boundary = piece.BrushBoundary = new bool[rest.Length];
            var counts = new Dictionary<(int, int), int>();
            foreach (var face in piece.Faces)
            {
                AddEdge(face.A, face.B);
                AddEdge(face.B, face.C);
                AddEdge(face.C, face.A);
            }

            foreach (var edge in counts)
                if (edge.Value != 2)
                    boundary[edge.Key.Item1] = boundary[edge.Key.Item2] = true;

            void AddEdge(int a, int b)
            {
                int from = weld[a], to = weld[b];
                if (from == to)
                    return;
                var key = from < to ? (from, to) : (to, from);
                counts.TryGetValue(key, out int count);
                counts[key] = count + 1;
                if (count > 0) return;
                float length = Vector3.Distance(worldRest[a], worldRest[b]);
                (edges[from] ??= new List<(int, float)>()).Add((to, length));
                (edges[to] ??= new List<(int, float)>()).Add((from, length));
            }
        }

        private void QueueDrag(Rect rect, Vector2 mouse, bool depth)
        {
            strokeCurrent = mouse;
            strokeInDepth = depth;
            dragHeight = rect.height;
            pendingDrag = true;
            strokeMoved = true;
        }

        private Vector3[] EvaluateDrag()
        {
            using var sample = BrushSample.Auto();
            var delta = strokeCurrent - strokeMouse;
            float unitsPerPixel = 2f * strokeDepth * Mathf.Tan(30f * Mathf.Deg2Rad) / dragHeight;
            var camera = preview.camera.transform;
            var world = strokeInDepth ? -camera.forward * delta.y :
                camera.right * delta.x - camera.up * delta.y;
            var avatar = SymmetryFrame;
            var step = avatar.worldToLocalMatrix.MultiplyVector(world * unitsPerPixel);
            var avatarToLocal = strokePiece.Renderer.worldToLocalMatrix * avatar.localToWorldMatrix;
            var motion = (Vector3[])strokeStart.Clone();
            var inverse = strokePiece.Renderer.worldToLocalMatrix;
            for (int i = 0; i < motion.Length; i++)
            {
                if (!InSelection(strokePiece, i) || !strokeVisible[strokePiece.BrushWeld[i]] || (strokePiece.Protection != null && strokePiece.Protection[i]))
                    continue;
                float nearest = float.PositiveInfinity;
                var direction = Vector3.zero;
                int matches = 0;
                foreach (var brush in strokeBrushes)
                {
                    float distance = brush.Distances[i];
                    if (distance >= brushRadius || distance > nearest + brushRadius * .0001f)
                        continue;
                    if (distance < nearest - brushRadius * .0001f)
                    {
                        nearest = distance;
                        direction = Vector3.zero;
                        matches = 0;
                    }
                    direction += Vector3.Scale(step, brush.Sign);
                    matches++;
                }
                if (matches == 0)
                    continue;
                float remaining = Mathf.Clamp01(1 - nearest / brushRadius);
                float falloff = brushMode == Brush.ElasticDeform
                    ? remaining * remaining * remaining * remaining * (5f - 4f * remaining)
                    : Mathf.SmoothStep(0, 1, remaining);
                int vertex = strokePiece.BrushWeld[i];
                if (brushMode == Brush.PushOut && vertex < strokeNormals.Length)
                    motion[i] += inverse.MultiplyVector(strokeNormals[vertex].normalized * (-delta.y * unitsPerPixel)) * falloff;
                else if (brushMode == Brush.Smooth)
                    motion[i] = Vector3.Lerp(strokeStart[i], strokeSmooth[i],
                        Mathf.Clamp01(delta.magnitude * unitsPerPixel / brushRadius) * falloff);
                else
                    motion[i] += avatarToLocal.MultiplyVector(direction / matches) * falloff;
            }
            return motion;
        }

        private static Vector3[] SmoothMotion(Piece piece, Vector3[] motion)
        {
            var result = (Vector3[])motion.Clone();
            for (int i = 0; i < motion.Length; i++)
            {
                var edges = piece.BrushEdges[piece.BrushWeld[i]];
                if (edges == null)
                    continue;
                var mean = Vector3.zero;
                float total = 0;
                foreach (var edge in edges)
                {
                    float weight = 1f / Mathf.Max(edge.Length, 1e-5f);
                    mean += motion[edge.Target] * weight;
                    total += weight;
                }
                if (total > 0)
                    result[i] = mean / total;
            }
            return result;
        }

        private void StampBrush()
        {
            using var sample = BrushSample.Auto();
            var original = CurrentMotion(strokePiece);
            var motion = (Vector3[])original.Clone();
            var falloff = new float[motion.Length];
            for (int i = 0; i < motion.Length; i++)
            {
                if (!InSelection(strokePiece, i) || !strokeVisible[strokePiece.BrushWeld[i]])
                    continue;
                float nearest = float.PositiveInfinity;
                foreach (var brush in strokeBrushes)
                    nearest = Mathf.Min(nearest, brush.Distances[i]);
                falloff[i] = Mathf.SmoothStep(0, 1, Mathf.Clamp01(1f - nearest / brushRadius));
            }

            if (brushMode is Brush.Protect or Brush.Unprotect)
            {
                var prior = strokePiece.Protection;
                var mask = prior == null ? new bool[motion.Length] : (bool[])prior.Clone();
                bool changed = false;
                bool protect = brushMode == Brush.Protect;
                for (int i = 0; i < mask.Length; i++)
                {
                    if (falloff[i] <= 0 || mask[i] == protect) continue;
                    mask[i] = protect;
                    changed = true;
                }
                if (changed)
                {
                    brushUndo.Push((strokePiece, weight, null, prior));
                    brushRedo.Clear();
                    ApplyProtection(strokePiece, mask);
                }
                return;
            }
            if (brushMode == Brush.Restore)
            {
                if (restoreOriginal)
                    RestoreInPlace(strokePiece, falloff, motion);
                else
                {
                    var generatedMotion = previewFit.GeneratedMotion(strokePiece.Source, weight);
                    for (int i = 0; i < motion.Length; i++)
                        motion[i] = Vector3.Lerp(motion[i], generatedMotion[i], falloff[i] * brushStrength);
                }
            }
            else if (brushMode == Brush.Relax)
                RelaxSurface(strokePiece, falloff, motion);
            else if (brushMode == Brush.Multiply)
            {
                for (int i = 0; i < motion.Length; i++)
                    motion[i] *= Mathf.Lerp(1f, transferMultiplier, falloff[i]);
            }
            else
            {
                var reference = pieces[SafeBrushReference];
                var surface = PickSurface(reference);
                if (surface == null) return;
                float winding = Mathf.Sign(reference.Renderer.localToWorldMatrix.determinant);
                ApplyClearance(strokePiece, surface, winding, brushRadius, falloff, motion, brushGapMm * .001f, brushStrength);
            }

            for (int i = 0; i < motion.Length; i++)
            {
                if (falloff[i] <= 0 || (strokePiece.Protection != null && strokePiece.Protection[i]))
                    motion[i] = original[i];
            }
            for (int i = 0; i < motion.Length; i++)
            {
                if (motion[i].Equals(original[i])) continue;
                ApplyDraft(strokePiece, motion);
                strokeMoved = true;
                return;
            }
            status = brushMode == Brush.ClearSkin ? "No reliable intersection with the chosen reference inside the brush." :
                "No editable vertices changed inside the brush.";
        }

        private static void ApplySkinClearance(Piece piece, ShapeTransfer.TriangleSurface skin,
            float winding, float radius, float[] falloff, Vector3[] motion) =>
            ApplyClearance(piece, skin, winding, radius, falloff, motion, Mathf.Clamp(radius * .04f, .00015f, .001f), 1);

        private static void ApplyClearance(Piece piece, ShapeTransfer.TriangleSurface skin,
            float winding, float radius, float[] falloff, Vector3[] motion, float padding, float strength)
        {
            var pushes = new Vector3[motion.Length];
            float pieceWinding = Mathf.Sign(piece.Renderer.localToWorldMatrix.determinant);
            foreach (var face in piece.Faces)
            {
                int a = face.A, b = face.B, c = face.C;
                if (falloff[a] <= 0 && falloff[b] <= 0 && falloff[c] <= 0)
                    continue;
                var normal = Vector3.Cross(piece.World[b] - piece.World[a], piece.World[c] - piece.World[a]).normalized;
                normal *= pieceWinding;
                Sample(a, a, a, normal);
                Sample(a, b, b, normal);
                Sample(b, c, c, normal);
                Sample(c, a, a, normal);
                Sample(a, b, c, normal);
            }

            var inverse = piece.Renderer.worldToLocalMatrix;
            var expanded = (Vector3[])pushes.Clone();
            for (int i = 0; i < pushes.Length; i++)
            {
                var edges = piece.BrushEdges[i];
                if (edges == null || falloff[i] <= 0)
                    continue;
                var mean = Vector3.zero;
                foreach (var edge in edges)
                    mean += pushes[edge.Target];
                expanded[i] += mean * (.28f * falloff[i] / edges.Count);
            }
            for (int i = 0; i < motion.Length; i++)
                motion[i] += inverse.MultiplyVector(Vector3.ClampMagnitude(expanded[piece.BrushWeld[i]], radius * .35f * strength));

            void Sample(int a, int b, int c, Vector3 normal)
            {
                var point = (piece.World[a] + piece.World[b] + piece.World[c]) / 3f;
                if (!skin.FindClosest(point, radius, out var face, out _, out var offset))
                    return;
                var outward = skin.Normal(face) * winding;
                float gap = Vector3.Dot(outward, offset);
                if (gap >= padding || Mathf.Abs(gap) < offset.magnitude * .8f || Vector3.Dot(normal, outward) < .5f)
                    return;
                float influence = (falloff[a] + falloff[b] + falloff[c]) / 3f;
                var correction = outward * (Mathf.Min(padding - gap, radius * .35f) * influence * strength);
                Store(a);
                Store(b);
                Store(c);

                void Store(int index)
                {
                    int vertex = piece.BrushWeld[index];
                    if (falloff[index] > 0 && correction.sqrMagnitude > pushes[vertex].sqrMagnitude)
                        pushes[vertex] = correction;
                }
            }
        }

        private void EndStroke(bool rebake = true)
        {
            if (rebake && stampQueued && strokePiece != null) ApplyQueuedStamp();
            stampQueued = false;
            if (strokePiece != null && GUIUtility.hotControl == strokeControl)
                GUIUtility.hotControl = 0;
            var piece = strokePiece;
            var final = rebake && pendingDrag && piece != null ? EvaluateDrag() : piece?.Draft;
            strokePiece = null;
            if (strokeMoved && piece != null)
            {
                brushUndo.Push((piece, weight, strokeStart, null));
                brushRedo.Clear();
                if (rebake && preview != null && final != null)
                    ApplyDraft(piece, final);
            }
            pendingDrag = false;
            nextDragUpdate = 0;
            strokeMoved = false;
            strokeStart = null;
            strokeBrushes = null;
            strokeVisible = null;
            strokeNormals = null;
            strokeSmooth = null;
        }

        private void RestoreInPlace(Piece piece, float[] falloff, Vector3[] motion)
        {
            if (piece.RestWorld == null)
            {
                var renderer = piece.Renderer;
                int shape = renderer.sharedMesh.GetBlendShapeIndex(targetShape);
                var neutral = new Mesh();
                try
                {
                    renderer.SetBlendShapeWeight(shape, 0);
                    renderer.BakeMesh(neutral);
                    piece.RestWorld = Array.ConvertAll(neutral.vertices, renderer.localToWorldMatrix.MultiplyPoint3x4);
                }
                finally
                {
                    renderer.SetBlendShapeWeight(shape, weight);
                    DestroyImmediate(neutral);
                }
            }
            if (!PreviewBrushGeometry.AlignPatch(piece.RestWorld, piece.World, falloff, out var rotation, out var offset))
                return;

            var inverse = piece.Renderer.worldToLocalMatrix;
            for (int i = 0; i < motion.Length; i++)
            {
                if (falloff[i] <= 0) continue;
                var correction = rotation * piece.RestWorld[i] + offset - piece.World[i];
                motion[i] += inverse.MultiplyVector(Vector3.ClampMagnitude(correction, brushRadius * .25f)) * falloff[i] * brushStrength;
            }
        }

        private void RelaxSurface(Piece piece, float[] falloff, Vector3[] motion)
        {
            if (piece.WorldNormals.Length != motion.Length) return;
            var skin = PickSurface(pieces[0]);
            var skinWinding = Mathf.Sign(pieces[0].Renderer.localToWorldMatrix.determinant);
            var inverse = piece.Renderer.worldToLocalMatrix;
            for (int i = 0; i < motion.Length; i++)
            {
                int representative = piece.BrushWeld[i];
                var neighbors = piece.BrushEdges[representative];
                if (falloff[i] <= 0 || piece.BrushBoundary[representative] || neighbors == null) continue;
                var center = Vector3.zero;
                foreach (var edge in neighbors)
                    center += piece.World[edge.Target];
                var point = piece.World[i];
                var shift = PreviewBrushGeometry.RelaxShift(point, center / neighbors.Count,
                    piece.WorldNormals[i], brushRadius * .12f, falloff[i] * brushStrength);
                if (skin != null && skin.FindClosest(point + shift, brushRadius, out var face, out _, out var gap))
                {
                    var normal = skin.Normal(face) * skinWinding;
                    float newGap = Vector3.Dot(gap, normal);
                    float oldGap = Vector3.Dot(gap - shift, normal);
                    if (newGap < 0 && newGap < oldGap)
                        shift += normal * (Mathf.Min(0, oldGap) - newGap);
                }
                motion[i] += inverse.MultiplyVector(shift);
            }
        }

        private Vector3[] CurrentMotion(Piece piece) =>
            previewFit.Correction(piece.Source, weight) ?? previewFit.GeneratedMotion(piece.Source, weight);

        private void ApplyProtection(Piece piece, bool[] mask)
        {
            piece.Protection = mask;
            previewFit.SetProtection(piece.Source, mask);
            RefreshDraft(piece, true);
        }

        private void ApplyDraft(Piece piece, Vector3[] deltas)
        {
            previewFit.SetCorrection(piece.Source, weight, deltas);
            RefreshDraft(piece, strokePiece != piece);
        }

        private void RefreshDraft(Piece piece, bool calculateShading)
        {
            var previous = piece.Renderer.sharedMesh;
            var mesh = previewFit.Build(piece.Source, out _, calculateShading: calculateShading);
            generated.Remove(previous);
            generated.Add(mesh);
            SwapPreviewMesh(piece, mesh);
            DestroyImmediate(previous);
            piece.Draft = CurrentMotion(piece);
            unsavedEdits = true;
            if (strokePiece == piece)
            {
                ClearClipping();
                InvalidateVertexVisibility();
                BakePiece(piece);
            }
            else
                Bake();
            Repaint();
        }

        private static void SwapPreviewMesh(Piece piece, Mesh mesh)
        {
            var weights = new float[piece.Original.blendShapeCount];
            for (int i = 0; i < weights.Length; i++)
                weights[i] = piece.Renderer.GetBlendShapeWeight(i);
            piece.Renderer.sharedMesh = mesh;
            for (int i = 0; i < weights.Length; i++)
                piece.Renderer.SetBlendShapeWeight(i, weights[i]);
        }

        private void SaveCorrections()
        {
            var changed = new List<SkinnedMeshRenderer>();
            foreach (var piece in pieces)
                if (piece.Draft != null)
                    changed.Add(piece.Source);
            if (changed.Count == 0)
                return;
            // The layer fit was computed together. Save the companion pieces as well.
            for (int i = 1; i < pieces.Count; i++)
                if (!changed.Contains(pieces[i].Source))
                    changed.Add(pieces[i].Source);
            var results = GeneratorAssets.ApplyToPieces("Save Outfit Corrections", changed.ToArray(), renderer =>
            {
                var piece = pieces.Find(candidate => candidate.Source == renderer);
                if (!renderer || renderer.sharedMesh != piece.Original)
                    throw new InvalidOperationException("The source mesh changed. Reopen Preview before saving.");
                return GeneratorAssets.Generate(renderer, body, GeneratorAssets.AvatarRoot(body), fit: previewFit, shape: targetShape);
            });
            var messages = new List<string>();
            foreach (var result in results)
                messages.Add(result.Message);
            status = string.Join("\n", messages);
            unsavedEdits = Array.Exists(results, result => result.Failed);
        }

        private void ClearClipping()
        {
            if (clippingWork != null)
            {
                clippingWork.Dispose();
                clippingWork = null;
                status = null;
            }
            crossings.Clear();
            patchLabels = Array.Empty<string>();
            activePatch = -1;
        }

        private void BeginPatchPreview()
        {
            if (activePatch < 0 || !EditableFrame) return;
            patchBaseMesh = pieces[selectedMesh + 1].Renderer.sharedMesh;
            UpdatePatchPreview();
        }

        private void UpdatePatchPreview()
        {
            var patch = crossings[activePatch];
            var piece = pieces[selectedMesh + 1];
            var reference = pieces[patch.Other];
            var surface = PickSurface(reference);
            RestorePatchMesh(piece);
            patchMotion = null;
            patchFeedback = $"Preview only · {piece.Source.name} against {reference.Source.name}";
            if (surface == null)
            {
                patchFeedback = "The reference surface has no usable triangles.";
                return;
            }
            if (piece.BrushWeld == null) BuildBrushGraph(piece);
            var original = CurrentMotion(piece);
            var motion = (Vector3[])original.Clone();
            float radius = Mathf.Clamp(brushRadius, size * .035f, size * .15f);
            var falloff = new float[motion.Length];
            for (int i = 0; i < falloff.Length; i++)
                if (InSelection(piece, i) && (piece.Protection == null || !piece.Protection[i]))
                    falloff[i] = Mathf.SmoothStep(0, 1, Mathf.Clamp01(1 - Vector3.Distance(piece.World[i], patch.Point) / radius));
            ApplyClearance(piece, surface, Mathf.Sign(reference.Renderer.localToWorldMatrix.determinant), radius, falloff, motion, brushGapMm * .001f, brushStrength);
            bool changed = false;
            for (int i = 0; i < motion.Length; i++)
            {
                if (falloff[i] <= 0) motion[i] = original[i];
                changed |= !motion[i].Equals(original[i]);
            }
            if (!changed)
            {
                patchFeedback = piece.Selection != null
                    ? "No movable contact in the current selection. Cancel and check the selection."
                    : "No reliable outward move here. Try the Clipping Brush.";
                return;
            }

            var saved = previewFit.Correction(piece.Source, weight);
            try
            {
                previewFit.SetCorrection(piece.Source, weight, motion);
                patchPreviewMesh = previewFit.Build(piece.Source, out _);
                SwapPreviewMesh(piece, patchPreviewMesh);
                InvalidateVertexVisibility();
                BakePiece(piece);
                patchMotion = motion;
            }
            finally
            {
                previewFit.SetCorrection(piece.Source, weight, saved);
            }
            Repaint();
        }

        private void RestorePatchMesh(Piece piece, bool rebake = true)
        {
            if (!patchPreviewMesh) return;
            SwapPreviewMesh(piece, patchBaseMesh);
            DestroyImmediate(patchPreviewMesh);
            patchPreviewMesh = null;
            if (rebake)
            {
                InvalidateVertexVisibility();
                BakePiece(piece);
            }
        }

        private void CancelPatchPreview(bool rebake = true)
        {
            if (!PreviewingPatch) return;
            RestorePatchMesh(pieces[selectedMesh + 1], rebake);
            patchBaseMesh = null;
            patchMotion = null;
            patchFeedback = null;
            Repaint();
        }

        private void ApplyPatchPreview()
        {
            var motion = patchMotion;
            if (motion == null) return;
            var piece = pieces[selectedMesh + 1];
            var original = CurrentMotion(piece);
            CancelPatchPreview();
            brushUndo.Push((piece, weight, original, null));
            brushRedo.Clear();
            ApplyDraft(piece, motion);
            CheckClipping();
        }

        private void CheckClipping()
        {
            ClearClipping();
            int selected = selectedMesh + 1;
            if (selected >= pieces.Count) return;
            status = $"Checking {pieces[selected].Source.name}…";
            clippingWork = ScanClipping(selected).GetEnumerator();
            Repaint();
        }

        private void AdvanceClipping()
        {
            if (clippingWork == null)
                return;
            using var sample = ClippingSample.Auto();
            double limit = EditorApplication.timeSinceStartup + .004;
            try
            {
                do
                {
                    if (clippingWork.MoveNext())
                        continue;
                    clippingWork.Dispose();
                    clippingWork = null;
                    Repaint();
                    return;
                } while (EditorApplication.timeSinceStartup < limit);
            }
            catch (Exception exception)
            {
                ClearClipping();
                status = exception.Message;
                Repaint();
            }
        }

        private IEnumerable<int> ScanClipping(int selected)
        {
            float reach = size * .15f;
            float threshold = Mathf.Max(.0001f, size * .001f);
            var surfaces = new ShapeTransfer.TriangleSurface[pieces.Count];
            var rest = new ShapeTransfer.TriangleSurface[pieces.Count];
            var restPositions = new Vector3[pieces.Count][];
            var winding = new float[pieces.Count];
            for (int i = 0; i < pieces.Count; i++)
            {
                var piece = pieces[i];
                if (!IsVisible(piece) || piece.Faces.Length == 0)
                    continue;
                surfaces[i] = PickSurface(piece);
                var toWorld = piece.Renderer.localToWorldMatrix;
                winding[i] = Mathf.Sign(toWorld.determinant);
                int shape = piece.Renderer.sharedMesh.GetBlendShapeIndex(targetShape);
                var temporary = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                try
                {
                    piece.Renderer.SetBlendShapeWeight(shape, piece.Source == body && previewFit.Reverse ? 100 : 0);
                    piece.Renderer.BakeMesh(temporary);
                    restPositions[i] = Array.ConvertAll(temporary.vertices, toWorld.MultiplyPoint3x4);
                    rest[i] = new ShapeTransfer.TriangleSurface(surfaces[i], restPositions[i]);
                }
                finally
                {
                    piece.Renderer.SetBlendShapeWeight(shape, piece.Source == body && previewFit.Reverse ? 100 - weight : weight);
                    DestroyImmediate(temporary);
                }
                yield return 0;
            }

            const int markerLimit = 200;
            int found = 0;
            float cellSize = Mathf.Max(.001f, size * .009f);
            float patchSize = Mathf.Max(.008f, size * .065f);
            var patches = new Dictionary<(int Other, Vector3Int Cell, bool Existing), ClippingPatch>();
            var marked = new HashSet<(int Piece, Vector3Int Cell, int Other)>();
            for (int i = 0; i < pieces.Count; i++)
            {
                if (surfaces[i] == null)
                    continue;
                var points = pieces[i].World;
                var authored = restPositions[i];
                var edges = new HashSet<(int, int)>();
                for (int j = 0; i != 0 && j < points.Length; j++)
                {
                    Mark(points[j], authored[j]);
                    if ((j & 31) == 31)
                        yield return 0;
                }
                int triangleCount = 0;
                foreach (var face in pieces[i].Faces)
                {
                    Midpoint(face.A, face.B);
                    Midpoint(face.B, face.C);
                    Midpoint(face.C, face.A);
                    if (i != 0) Mark((points[face.A] + points[face.B] + points[face.C]) / 3f,
                        (authored[face.A] + authored[face.B] + authored[face.C]) / 3f);
                    if ((++triangleCount & 15) == 0)
                        yield return 0;
                }

                void Midpoint(int a, int b)
                {
                    if (!edges.Add(a < b ? (a,b) : (b,a))) return;
                    if (i != 0) Mark((points[a] + points[b]) * .5f, (authored[a] + authored[b]) * .5f);
                    var edge = points[b] - points[a];
                    var prior = authored[b] - authored[a];
                    float length = edge.magnitude;
                    if (length <= threshold) return;
                    var ray = new Ray(points[a], edge / length);
                    for (int other = 0; other < surfaces.Length; other++)
                    {
                        if (other == i || (i != selected && other != selected) || surfaces[other] == null ||
                            !surfaces[other].Raycast(ray, length, 0, out _, out float hit) || hit <= threshold || length - hit <= threshold) continue;
                        if (prior.sqrMagnitude > 0 && rest[other].Raycast(new Ray(authored[a], prior.normalized), prior.magnitude, 0, out _, out _)) continue;
                        var point = ray.GetPoint(hit);
                        if (!marked.Add((i, Vector3Int.FloorToInt(point / cellSize), other))) continue;
                        found++;
                        AddPatch(i == selected ? other : i, point, 0, false);
                    }
                }

                void Mark(Vector3 point, Vector3 original)
                {
                    for (int other = 0; other < surfaces.Length; other++)
                    {
                        if (other == i || (i != selected && other != selected) || surfaces[other] == null ||
                            !surfaces[other].FindClosest(point, reach, out var face, out _, out var offset))
                            continue;
                        var normal = surfaces[other].Normal(face) * winding[other];
                        float depth = -Vector3.Dot(normal, offset);
                        if (depth < threshold || depth < offset.magnitude * .9f ||
                            (other != 0 && depth > reach * .3f))
                            continue;
                        if (!rest[other].FindClosest(original, reach, out var prior, out _, out var oldOffset))
                            continue;
                        var oldNormal = rest[other].Normal(prior) * winding[other];
                        float oldGap = Vector3.Dot(oldNormal, oldOffset);
                        if (Vector3.Dot(oldNormal, normal) < .55f)
                            continue;
                        // A garment deliberately worn below another layer already starts behind it.
                        if (other != 0 && oldGap < 0)
                            continue;

                        var cell = Vector3Int.FloorToInt(point / cellSize);
                        if (!marked.Add((i, cell, other)))
                            continue;
                        found++;
                        AddPatch(i == selected ? other : i, point, depth, oldGap < -threshold);
                    }
                }
            }

            crossings.AddRange(patches.Values);
            crossings.Sort((a, b) =>
            {
                int existing = a.Existing.CompareTo(b.Existing);
                if (existing != 0) return existing;
                int depth = b.Depth.CompareTo(a.Depth);
                return depth != 0 ? depth : b.Samples.CompareTo(a.Samples);
            });
            int newPatches = crossings.FindAll(patch => !patch.Existing).Count;
            int existingPatches = crossings.Count - newPatches;
            if (crossings.Count > markerLimit) crossings.RemoveRange(markerLimit, crossings.Count - markerLimit);
            patchLabels = new string[crossings.Count + 1];
            patchLabels[0] = "Choose a patch";
            for (int i = 0; i < crossings.Count; i++)
            {
                var patch = crossings[i];
                patchLabels[i + 1] = $"{i + 1}. {(patch.Existing ? "Existing" : "New")} · {pieces[patch.Other].Source.name}";
            }
            status = $"{pieces[selected].Source.name}: {newPatches} new, {existingPatches} existing patches ({found} contact samples) at {weight:g}%";
            if (patches.Count > markerLimit) status += $" · Showing {markerLimit} patches";

            void AddPatch(int other, Vector3 position, float depth, bool existing)
            {
                var key = (other, Vector3Int.FloorToInt(position / patchSize), existing);
                if (!patches.TryGetValue(key, out var patch))
                {
                    patch = new ClippingPatch { Point = position, Other = other, Existing = existing,
                        Color = existing ? Color.gray : other == 0 ? Color.red : new Color(1f, .55f, .12f) };
                    patches.Add(key, patch);
                }
                patch.Samples++;
                if (depth > patch.Depth)
                {
                    patch.Depth = depth;
                    patch.Point = position;
                }
            }
        }

        private static IEnumerable<(int A, int B, int C)> Triangles(Mesh mesh)
        {
            for (int section = 0; section < mesh.subMeshCount; section++)
            {
                if (mesh.GetTopology(section) != MeshTopology.Triangles)
                    continue;
                var indices = mesh.GetTriangles(section);
                for (int i = 0; i < indices.Length; i += 3)
                    yield return (indices[i], indices[i + 1], indices[i + 2]);
            }
        }

        private void OnDisable() => Release();

        private void Release()
        {
            StopFlying();
            CancelBox();
            EndStroke(rebake: false);
            CancelPatchPreview(rebake: false);
            ClearClipping();
            previewFit = null;
            ClearSpan();
            viewImage = null;
            preview?.Cleanup();
            preview = null;
            if (selectionHighlight) DestroyImmediate(selectionHighlight);
            selectionHighlight = null;
            selectionHighlightUntil = 0;
            foreach (var piece in pieces)
                DestroyImmediate(piece.Baked);
            pieces.Clear();
            InvalidateVertexVisibility();
            crossings.Clear();
            brushUndo.Clear();
            brushRedo.Clear();
            unsavedEdits = false;
            foreach (var mesh in generated)
                DestroyImmediate(mesh);
            generated.Clear();
        }
    }
}
