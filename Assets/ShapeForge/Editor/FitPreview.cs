using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MilltinaBreastGenerator
{
    internal sealed class FitPreview : EditorWindow
    {
        private sealed class Piece
        {
            internal SkinnedMeshRenderer Renderer;
            internal Mesh Baked;
            internal SkinnedMeshRenderer Source;
            internal Mesh Original;
            internal Vector3[] OriginalPositions;
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
        private enum Brush { Grab, PushOut, Smooth, Multiply, ClearSkin, ElasticDeform }
        private static readonly string[] BrushNames = { "Grab", "Push Out", "Smooth", "Multiply", "Clear Skin", "Elastic Deform" };
        private Brush brushMode;
        private float transferMultiplier = 1.2f;
        private Vector3[] strokeNormals;
        private Vector3[] strokeSmooth;
        private bool sculpt;
        private float vertexSize = 4;
        private bool showClipMarkers = true;
        private bool leftSide;
        private float brushRadius = .015f;
        private readonly Stack<(Piece Piece, float Weight, Vector3[] Prior)> brushUndo = new();
        private readonly Stack<(Piece Piece, float Weight, Vector3[] Prior)> brushRedo = new();
        private Texture viewImage;
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
        private readonly List<(Vector3 Point, Color Color)> crossings = new();
        private string status;
        private float zoom = 1f;
        private Vector3 pan;
        private readonly HashSet<KeyCode> flyKeys = new();
        private bool flying;
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
        private Vector3[] pendingDrag;
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
            window.titleContent = new GUIContent($"ShapeForge Preview · {method}",
                $"Previewing {shape} with the {method} transfer method.");
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

            HandleHistoryShortcut();
            HandleSpanShortcut();
            var viewport = new Rect(0, 0, position.width, position.height);
            var toolbar = new Rect(16, 10, position.width - 32, position.height - 20);
            var input = Event.current;
            var toolbarFrame = new Rect(0, 0, position.width, toolbar.y + toolbarHeight + 8);
            float meshPanelWidth = Mathf.Clamp(position.width * .36f, 260f, 380f);
            var meshPanel = new Rect(position.width - meshPanelWidth - 12, toolbarFrame.yMax + 8, meshPanelWidth, 66);
            bool overControls = toolbarFrame.Contains(input.mousePosition) || meshPanel.Contains(input.mousePosition);
            bool repaint = input.type == EventType.Repaint;
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
            if (repaint) EditorGUI.DrawRect(meshPanel, new Color(.105f, .102f, .095f, .94f));
            using (new GUILayout.AreaScope(new Rect(meshPanel.x + 8, meshPanel.y + 6, meshPanel.width - 16, 58)))
            {
                using (new EditorGUILayout.HorizontalScope()) DrawMeshPicker();
                using (new EditorGUILayout.HorizontalScope()) DrawVisibility();
            }
            if (!repaint)
                DrawViewport(viewport, overControls);
            else if (!string.IsNullOrEmpty(GUI.tooltip))
                DrawHoverHelp(GUI.tooltip, meshPanel.x - 24);
        }

        private void DrawHoverHelp(string tip, float availableWidth)
        {
            hoverHelp ??= new GUIStyle(EditorStyles.wordWrappedLabel) { fontSize = 15, normal = { textColor = Color.white } };
            var style = hoverHelp;
            float width = Mathf.Min(availableWidth, 450);
            float height = style.CalcHeight(new GUIContent(tip), width - 16) + 12;
            var box = new Rect(12, toolbarHeight + 26, width, height);
            EditorGUI.DrawRect(box, new Color(.105f, .102f, .095f, 1));
            var color = GUI.contentColor;
            GUI.contentColor = Color.white;
            GUI.Label(new Rect(box.x + 8, box.y + 5, box.width - 16, box.height - 10), tip, style);
            GUI.contentColor = color;
        }

        private static void LastControlHelp(string tip) =>
            GUI.Label(GUILayoutUtility.GetLastRect(), new GUIContent("", tip), GUIStyle.none);

        private void DrawToolbar(float width)
        {
            float framePickerWidth = (frameWeights.Length > 1 ? 100f : 0f) + 65f;
            bool wide = width >= 940f + framePickerWidth;
            bool fitAndView = width >= 735f + framePickerWidth;
            using (new EditorGUILayout.HorizontalScope())
            {
                DrawWeight(136f);
                if (fitAndView)
                {
                    if (wide)
                        GroupDivider();
                    DrawViews();
                }
                GUILayout.FlexibleSpace();
            }
            if (!fitAndView)
                using (new EditorGUILayout.HorizontalScope()) DrawViews();

            RowDivider();
            using (new EditorGUILayout.HorizontalScope())
            {
                DrawEditTools();
                GUILayout.FlexibleSpace();
                if (wide)
                    DrawHistory();
            }
            if (!wide || straighten || sculpt)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (straighten || sculpt)
                        DrawToolSettings();
                    GUILayout.FlexibleSpace();
                    if (!wide && !sculpt && !straighten)
                        DrawHistory();
                }
            }

            if (!wide && (sculpt || straighten))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    DrawHistory();
                }
            }

            if (sculpt)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Symmetry", EditorStyles.whiteMiniLabel, GUILayout.Width(58));
                    DrawSymmetryToggle("Left/right", "Mirror across the avatar's centerline (X).", 1);
                    DrawSymmetryToggle("Up/down", "Mirror across the garment's vertical midpoint (Y).", 2);
                    DrawSymmetryToggle("Front/back", "Mirror across the garment's front/back midpoint (Z).", 4);
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

            if (!string.IsNullOrEmpty(status))
            {
                string line = status.Split('\n')[0];
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(new GUIContent(line, status), EditorStyles.wordWrappedMiniLabel, GUILayout.ExpandWidth(true));
                    if (crossings.Count > 0)
                        showClipMarkers = EditorGUILayout.ToggleLeft("Show markers", showClipMarkers, GUILayout.Width(106));
                }
            }
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
            bool clicked = GUI.Button(button, content, floatingButton);
            floatingButton.normal.textColor = oldColor;
            return clicked;
        }

        private static void RowDivider()
        {
            GUILayout.Space(4);
            var line = GUILayoutUtility.GetRect(0, 1, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
                EditorGUI.DrawRect(line, new Color(.73f, .62f, .44f, .20f));
            GUILayout.Space(4);
        }

        private static void GroupDivider()
        {
            GUILayout.Space(7);
            var line = GUILayoutUtility.GetRect(1, 23, GUILayout.Width(1));
            if (Event.current.type == EventType.Repaint)
                EditorGUI.DrawRect(line, new Color(.73f, .62f, .44f, .25f));
            GUILayout.Space(7);
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
            OverlayHeading("View");
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
            if (OverlayButton(SculptHelp, sculpt))
            {
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
                straighten = !straighten;
                sculpt = false;
                ClearSpan();
                if (straighten && !EditableFrame)
                {
                    weight = 100;
                    Bake();
                }
            }
            if (OverlayButton(new GUIContent(clippingWork == null ? "◉  Check Clipping" : "Cancel Check",
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
                GUILayout.Label(new GUIContent("Span strength", "Drag to control how much the selected strip straightens."),
                    EditorStyles.whiteMiniLabel, GUILayout.Width(80), GUILayout.Height(25));
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
                brushMode = (Brush)EditorGUILayout.Popup((int)brushMode, BrushNames, GUILayout.Width(112));
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
            Brush.ClearSkin => "Click a poke-through to lift the fabric off the skin",
            _ => "Left-drag to grab · Alt-grab through skin · Shift-drag for depth · Scroll to resize"
        };

        private bool BrushThroughBody => hoverThroughBody || brushMode == Brush.ClearSkin;

        private void DrawMeshPicker()
        {
            OverlayHeading("Mesh");
            int selection = EditorGUILayout.Popup(selectedMesh, editableNames, GUILayout.ExpandWidth(true), GUILayout.MinWidth(130));
            LastControlHelp("Choose the clothing piece you want to edit. Other pieces stay visible for comparison.");
            if (selection != selectedMesh)
            {
                EndStroke();
                ClearSpan();
                status = null;
                selectedMesh = selection;
                ClearClipping();
            }
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

        private bool IsVisible(Piece piece) =>
            !isolateMesh || piece == pieces[0] || piece == pieces[selectedMesh + 1];

        private void DrawVisibility()
        {
            EditorGUI.BeginChangeCheck();
            cullBackfaces = EditorGUILayout.ToggleLeft(new GUIContent("Cull backfaces",
                "Skip vertices facing away from the camera."), cullBackfaces, GUILayout.Width(115));
            zTest = EditorGUILayout.ToggleLeft(new GUIContent("Z-test",
                "Skip vertices hidden by the body or another surface."), zTest, GUILayout.Width(64));
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
            if (!straighten || input.type != EventType.KeyDown || EditorGUIUtility.editingTextField)
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

        private void MoveHistory(Stack<(Piece Piece, float Weight, Vector3[] Prior)> from,
            Stack<(Piece Piece, float Weight, Vector3[] Prior)> to)
        {
            if (from.Count == 0)
                return;
            var (piece, frameWeight, prior) = from.Peek();
            weight = frameWeight;
            var current = previewFit.Correction(piece.Source, frameWeight);
            if (current == null)
            {
                var mesh = piece.Renderer.sharedMesh;
                current = new Vector3[mesh.vertexCount];
                mesh.GetBlendShapeFrameVertices(mesh.GetBlendShapeIndex(targetShape), ShapeTransfer.FrameWeights(mesh, targetShape).IndexOf(weight), current, null, null);
            }
            ApplyDraft(piece, prior);
            from.Pop();
            to.Push((piece, weight, current));
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
            if (!overControls || flying || strokePiece != null)
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
            if (!overControls || strokePiece != null)
                DrawFootprint(rect);
            DrawSpan(rect);
            if (!overControls)
                DrawPickCandidate(rect);
            if (!showClipMarkers) return;
            foreach (var marker in crossings)
            {
                var point = camera.WorldToViewportPoint(marker.Point);
                if (point.z > 0 && point.x >= 0 && point.x <= 1 && point.y >= 0 && point.y <= 1)
                    EditorGUI.DrawRect(new Rect(rect.x + rect.width * point.x - 2,
                        rect.y + rect.height * (1 - point.y) - 2, 4, 4), marker.Color);
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
                case EventType.MouseMove when (sculpt || straighten) && EditableFrame && !flying:
                    hoverMouse = input.mousePosition;
                    Repaint();
                    break;
                case EventType.ScrollWheel when strokePiece != null:
                    brushRadius = Mathf.Clamp(brushRadius * Mathf.Exp(-input.delta.y * .08f), size * .015f, size * .5f);
                    if (strokeMoved)
                        DragStroke(rect, strokeCurrent, strokeInDepth);
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
                case EventType.MouseDown when input.button == 0 && rect.Contains(input.mousePosition) &&
                    straighten && EditableFrame:
                    SelectSpan(rect, input.mousePosition);
                    input.Use();
                    break;
                case EventType.MouseDown when input.button == 0 && rect.Contains(input.mousePosition) &&
                    sculpt && EditableFrame:
                    if (BeginStroke(rect, input.mousePosition, input.alt || brushMode == Brush.ClearSkin))
                    {
                        if (brushMode == Brush.Multiply || brushMode == Brush.ClearSkin)
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
                    DragStroke(rect, input.mousePosition, input.shift);
                    input.Use();
                    break;
                case EventType.MouseUp when input.button == 0 && strokePiece != null && GUIUtility.hotControl == control:
                    GUIUtility.hotControl = 0;
                    EndStroke();
                    input.Use();
                    break;
                case EventType.MouseDown when input.button == 1 && rect.Contains(input.mousePosition):
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
            AdvanceClipping();
            if (pendingDrag != null && strokePiece != null && EditorApplication.timeSinceStartup >= nextDragUpdate)
            {
                var deltas = pendingDrag;
                pendingDrag = null;
                ApplyDraft(strokePiece, deltas);
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
            EndStroke();
        }

        private void StopFlying()
        {
            if (flying && GUIUtility.hotControl == flyControl)
                GUIUtility.hotControl = 0;
            flying = false;
            flyKeys.Clear();
        }

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
            if ((!sculpt && !straighten) || !EditableFrame || flying || strokePiece != null ||
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
            var mesh = piece.Renderer.sharedMesh;
            var previous = new Vector3[mesh.vertexCount];
            mesh.GetBlendShapeFrameVertices(mesh.GetBlendShapeIndex(targetShape), ShapeTransfer.FrameWeights(mesh, targetShape).IndexOf(weight), previous, null, null);
            var motion = (Vector3[])previous.Clone();
            var inverse = piece.Renderer.worldToLocalMatrix;
            for (int i = 0; i < motion.Length; i++)
                motion[i] += inverse.MultiplyVector(spanOffsets[i]) * spanStrength;
            brushUndo.Push((piece, weight, previous));
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

            var mesh = piece.Renderer.sharedMesh;
            strokeStart = new Vector3[mesh.vertexCount];
            mesh.GetBlendShapeFrameVertices(mesh.GetBlendShapeIndex(targetShape), ShapeTransfer.FrameWeights(mesh, targetShape).IndexOf(weight), strokeStart, null, null);
            strokePiece = piece;
            strokeThroughBody = throughBody;
            strokeNormals = piece.WorldNormals;
            if (brushMode == Brush.Smooth)
                strokeSmooth = SmoothMotion(piece, strokeStart);
            strokeMouse = mouse;
            strokeDepth = preview.camera.WorldToViewportPoint(center).z;
            strokeCenter = center;
            strokeBrushes = BrushCopies(piece, center, face, size * .5f);
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
            return true;
        }

        private void DrawVertexDots(Rect rect)
        {
            if (!straighten || spanOffsets != null || flying || !EditableFrame || selectedMesh + 1 >= pieces.Count)
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
            if (!sculpt || !EditableFrame || flying)
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
            var weld = piece.BrushWeld = new int[rest.Length];
            var edges = piece.BrushEdges = new List<(int Target, float Length)>[rest.Length];
            var groups = new Dictionary<Vector3, int>();
            for (int i = 0; i < rest.Length; i++)
            {
                if (!groups.TryGetValue(rest[i], out int representative))
                    groups.Add(rest[i], representative = i);
                weld[i] = representative;
            }
            foreach (var face in piece.Faces)
            {
                AddEdge(face.A, face.B);
                AddEdge(face.B, face.C);
                AddEdge(face.C, face.A);
            }

            void AddEdge(int a, int b)
            {
                int from = weld[a], to = weld[b];
                if (from == to)
                    return;
                float length = Vector3.Distance(worldRest[a], worldRest[b]);
                (edges[from] ??= new List<(int, float)>()).Add((to, length));
                (edges[to] ??= new List<(int, float)>()).Add((from, length));
            }
        }

        private void DragStroke(Rect rect, Vector2 mouse, bool depth)
        {
            strokeCurrent = mouse;
            strokeInDepth = depth;
            var delta = mouse - strokeMouse;
            float unitsPerPixel = 2f * strokeDepth * Mathf.Tan(30f * Mathf.Deg2Rad) / rect.height;
            var camera = preview.camera.transform;
            var world = depth ? -camera.forward * delta.y :
                camera.right * delta.x - camera.up * delta.y;
            var avatar = SymmetryFrame;
            var step = avatar.worldToLocalMatrix.MultiplyVector(world * unitsPerPixel);
            var avatarToLocal = strokePiece.Renderer.worldToLocalMatrix * avatar.localToWorldMatrix;
            var motion = (Vector3[])strokeStart.Clone();
            var inverse = strokePiece.Renderer.worldToLocalMatrix;
            for (int i = 0; i < motion.Length; i++)
            {
                if (!strokeVisible[strokePiece.BrushWeld[i]])
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
            pendingDrag = motion;
            strokeMoved = true;
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
            var motion = (Vector3[])strokeStart.Clone();
            var falloff = new float[motion.Length];
            for (int i = 0; i < motion.Length; i++)
            {
                if (!strokeVisible[strokePiece.BrushWeld[i]])
                    continue;
                float nearest = float.PositiveInfinity;
                foreach (var brush in strokeBrushes)
                    nearest = Mathf.Min(nearest, brush.Distances[i]);
                falloff[i] = Mathf.SmoothStep(0, 1, Mathf.Clamp01(1f - nearest / brushRadius));
            }

            if (brushMode == Brush.Multiply)
            {
                for (int i = 0; i < motion.Length; i++)
                    motion[i] *= Mathf.Lerp(1f, transferMultiplier, falloff[i]);
            }
            else
            {
                var skin = PickSurface(pieces[0]);
                if (skin == null)
                    return;
                float winding = Mathf.Sign(pieces[0].Renderer.localToWorldMatrix.determinant);
                ApplySkinClearance(strokePiece, skin, winding, brushRadius, falloff, motion);
            }

            for (int i = 0; i < motion.Length; i++)
            {
                if (motion[i].Equals(strokeStart[i]))
                    continue;
                ApplyDraft(strokePiece, motion);
                strokeMoved = true;
                return;
            }
            status = brushMode == Brush.ClearSkin ? "No reliably identified skin crossing inside the brush." :
                "No transferred motion inside the brush at this strength.";
        }

        private static void ApplySkinClearance(Piece piece, ShapeTransfer.TriangleSurface skin,
            float winding, float radius, float[] falloff, Vector3[] motion)
        {
            var pushes = new Vector3[motion.Length];
            float padding = Mathf.Clamp(radius * .04f, .00015f, .001f);
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
                motion[i] += inverse.MultiplyVector(expanded[piece.BrushWeld[i]]);

            void Sample(int a, int b, int c, Vector3 normal)
            {
                var point = (piece.World[a] + piece.World[b] + piece.World[c]) / 3f;
                if (!skin.FindClosest(point, radius, out var face, out _, out var offset))
                    return;
                var outward = skin.Normal(face) * winding;
                float gap = Vector3.Dot(outward, offset);
                if (gap >= 0 || gap > -offset.magnitude * .8f || Vector3.Dot(normal, outward) < .5f)
                    return;
                float influence = (falloff[a] + falloff[b] + falloff[c]) / 3f;
                var correction = outward * (Mathf.Min(padding - gap, radius * .35f) * influence);
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
            if (strokePiece != null && GUIUtility.hotControl == strokeControl)
                GUIUtility.hotControl = 0;
            var piece = strokePiece;
            strokePiece = null;
            if (strokeMoved && piece != null)
            {
                brushUndo.Push((piece, weight, strokeStart));
                brushRedo.Clear();
                var final = pendingDrag ?? piece.Draft;
                if (rebake && preview != null && final != null)
                    ApplyDraft(piece, final);
            }
            pendingDrag = null;
            nextDragUpdate = 0;
            strokeMoved = false;
            strokeStart = null;
            strokeBrushes = null;
            strokeVisible = null;
            strokeNormals = null;
            strokeSmooth = null;
        }

        private void ApplyDraft(Piece piece, Vector3[] deltas)
        {
            var previous = piece.Renderer.sharedMesh;
            piece.OriginalPositions ??= piece.Original.vertices;
            previewFit.SetCorrection(piece.Source, weight, deltas);
            var mesh = previewFit.Build(piece.Source, out _, calculateShading: strokePiece != piece);
            generated.Remove(previous);
            generated.Add(mesh);
            var weights = new float[piece.Original.blendShapeCount];
            for (int i = 0; i < weights.Length; i++)
                weights[i] = piece.Renderer.GetBlendShapeWeight(i);
            piece.Renderer.sharedMesh = mesh;
            for (int i = 0; i < weights.Length; i++)
                piece.Renderer.SetBlendShapeWeight(i, weights[i]);
            DestroyImmediate(previous);
            piece.Draft = deltas;
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
                        if (crossings.Count < markerLimit) crossings.Add((point, i == 0 || other == 0 ? Color.red : new Color(1f,.55f,.12f)));
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
                        if (crossings.Count < markerLimit)
                            crossings.Add((point, oldGap < -threshold ? Color.gray :
                                other == 0 ? Color.red : new Color(1f, .55f, .12f)));
                    }
                }
            }

            status = $"{pieces[selected].Source.name}: {found} contact samples at {weight:g}% · Red: body · Orange: garment · Gray: present at Fit 0";
            if (found > markerLimit) status += $" · Showing {markerLimit} markers";
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
            EndStroke(rebake: false);
            ClearClipping();
            previewFit = null;
            ClearSpan();
            viewImage = null;
            preview?.Cleanup();
            preview = null;
            foreach (var piece in pieces)
                DestroyImmediate(piece.Baked);
            pieces.Clear();
            InvalidateVertexVisibility();
            crossings.Clear();
            brushUndo.Clear();
            brushRedo.Clear();
            foreach (var mesh in generated)
                DestroyImmediate(mesh);
            generated.Clear();
        }
    }
}
