using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace MilltinaBreastGenerator
{
    internal sealed partial class FitPreview
    {
        private Vector3 startingScenePivot;
        private Quaternion startingSceneRotation;
        private float startingSceneSize;
        private bool startingSceneOrthographic;
        private bool startingGizmosHidden;
        private Piece outlinePiece;
        private Vector3 outlineView;
        private bool outlineOrthographic;
        private readonly List<(int A, int B)> silhouette = new();

        private void BeginSceneEditing()
        {
            editView = SceneView.lastActiveSceneView;
            if (!editView) editView = EditorWindow.GetWindow<SceneView>();
            var stage = StageUtility.GetCurrentStageHandle();
            if (pieces.Exists(piece => !piece.Source.gameObject.scene.IsValid() ||
                !StageUtility.GetStageHandle(piece.Source.gameObject).Equals(stage)))
            {
                Release();
                error = "Open the avatar and outfit in the current Scene View before editing.";
                return;
            }

            startingScenePivot = editView.pivot;
            startingSceneRotation = editView.rotation;
            startingSceneSize = editView.size;
            startingSceneOrthographic = editView.orthographic;
            startingGizmosHidden = Tools.hidden;
            Tools.hidden = true;
            editView.Frame(new Bounds(focus, Vector3.one * size * 2.5f), false);
            sceneEditing = true;
            var visibility = SceneVisibilityManager.instance;
            foreach (var piece in pieces)
            {
                var source = piece.Source;
                piece.SceneVisible = source.enabled && source.gameObject.activeInHierarchy && !visibility.IsHidden(source.gameObject);
                if (!piece.SceneVisible) continue;
                visibility.Hide(source.gameObject, false);
                piece.HiddenBySession = true;
            }
            SceneView.duringSceneGui += DrawSceneEdit;
            Camera.onPreCull += DrawSceneMeshes;
            RenderPipelineManager.beginCameraRendering += DrawPipelineSceneMeshes;
            AssemblyReloadEvents.beforeAssemblyReload += Release;
            EditorApplication.playModeStateChanged += EndEditingBeforePlay;
            EditorApplication.update += Update;
            SceneView.RepaintAll();
        }

        private void EndSceneEditing()
        {
            if (!sceneEditing) return;
            sceneEditing = false;
            SceneView.duringSceneGui -= DrawSceneEdit;
            Camera.onPreCull -= DrawSceneMeshes;
            RenderPipelineManager.beginCameraRendering -= DrawPipelineSceneMeshes;
            AssemblyReloadEvents.beforeAssemblyReload -= Release;
            EditorApplication.playModeStateChanged -= EndEditingBeforePlay;
            EditorApplication.update -= Update;
            foreach (var piece in pieces)
                if (piece.HiddenBySession && piece.Source)
                    SceneVisibilityManager.instance.Show(piece.Source.gameObject, false);
            Tools.hidden = startingGizmosHidden;
            if (editView)
            {
                editView.pivot = startingScenePivot;
                editView.rotation = startingSceneRotation;
                editView.orthographic = startingSceneOrthographic;
                editView.size = startingSceneSize;
            }
            meshPulseStarted = -1;
            outlinePiece = null;
            silhouette.Clear();
            editView = null;
            SceneView.RepaintAll();
        }

        private void EndEditingBeforePlay(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode)
            {
                Release();
                if (host) host.ShowGenerator();
            }
        }

        private void FinishSaving()
        {
            EndStroke();
            SaveCorrections();
            if (!unsavedEdits)
            {
                string report = status;
                Release();
                status = report;
            }
            Repaint();
        }

        internal void SaveChanges() => FinishSaving();

        internal void DiscardChanges()
        {
            Release();
            status = error = null;
            Repaint();
        }

        private void DrawPipelineSceneMeshes(ScriptableRenderContext context, Camera camera) => DrawSceneMeshes(camera);

        private void DrawSceneMeshes(Camera camera)
        {
            if (!sceneEditing || camera.cameraType != CameraType.SceneView) return;
            foreach (var piece in pieces)
            {
                if (!piece.Source || !IsVisible(piece)) continue;
                for (int i = 0; i < Mathf.Min(piece.Baked.subMeshCount, piece.Materials.Length); i++)
                {
                    var material = piece.Materials[i];
                    if (!material) continue;
                    Graphics.DrawMesh(piece.Baked, piece.Renderer.localToWorldMatrix, material, piece.Source.gameObject.layer,
                        camera, i, null, ShadowCastingMode.Off, false, null, LightProbeUsage.Off);
                }
            }
        }

        private void DrawSceneEdit(SceneView view)
        {
            if (!sceneEditing || view != editView || pieces.Count < 2) return;
            var camera = view.camera;
            float scale = EditorGUIUtility.pixelsPerPoint;
            var viewport = new Rect(0, 0, camera.pixelWidth / scale, camera.pixelHeight / scale);
            if (viewport.width <= 0 || viewport.height <= 0) return;
            float inset = Mathf.Min(48, viewport.width * .12f);
            float overlayWidth = Mathf.Min(430, viewport.width - inset - 10);
            bool narrow = overlayWidth < 350;
            float overlayHeight = narrow ? 86 : 60;
            if (tool != Tool.Inspect) overlayHeight += 25;
            var overlay = new Rect(inset, 10, overlayWidth, overlayHeight);
            var input = Event.current;
            bool repaint = input.type == EventType.Repaint;
            if (repaint) DrawMeshPulse(camera);

            Handles.BeginGUI();
            try
            {
                bool overOverlay = overlay.Contains(input.mousePosition);
                HandleEditKeys();
                if (repaint) DrawSceneMarkers(viewport, overOverlay);
                DrawSceneToolbar(overlay, narrow);
                if (input.type != EventType.Used) Navigate(viewport, overOverlay);
            }
            finally
            {
                Handles.EndGUI();
            }
            if ((input.type == EventType.MouseMove || input.type == EventType.KeyUp || input.type == EventType.Used) && (sculpt || tool == Tool.Vertices || straighten))
                view.Repaint();
        }

        private void DrawSceneToolbar(Rect overlay, bool narrow)
        {
            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(overlay, Chrome);
                DrawOutline(overlay, RuleColor);
                EditorGUI.DrawRect(new Rect(overlay.x, overlay.y, 3, overlay.height), Gold);
            }
            using (new GUILayout.AreaScope(new Rect(overlay.x + 8, overlay.y + 4, overlay.width - 16, overlay.height - 8)))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    ToolbarLabel(overlay.width < 238 ? "◆ Edit Mode" : "◆ ShapeForge Edit Mode", overlay.width < 238 ? 91 : 148);
                    if (!narrow)
                        using (new EditorGUI.DisabledScope(PreviewingPatch)) DrawMeshPicker();
                }
                if (narrow)
                    using (new EditorGUI.DisabledScope(PreviewingPatch))
                        using (new EditorGUILayout.HorizontalScope()) DrawMeshPicker();
                using (new EditorGUILayout.HorizontalScope()) DrawSceneWeight();
                if (tool != Tool.Inspect)
                {
                    var hint = $"{ToolLabels[(int)tool]} · {SceneToolHint()}";
                    GUILayout.Label(hint, EditorStyles.whiteMiniLabel, GUILayout.Height(21));
                }
            }
        }

        private void DrawSceneWeight()
        {
            using (new EditorGUI.DisabledScope(PreviewingPatch))
            {
                ToolbarLabel("Fit", 18);
                float next = EditorGUILayout.Slider(weight, 0, Mathf.Max(100, frameWeights[frameWeights.Length - 1]),
                    GUILayout.MinWidth(50), GUILayout.ExpandWidth(true), GUILayout.Height(ControlHeight));
                if (next != weight) SetFitWeight(next);
            }
        }

        private string SceneToolHint()
        {
            if (!EditableFrame && (sculpt || straighten)) return "Choose an authored frame in Settings to edit.";
            if (PreviewingPatch) return "Review the fix, then Apply or Cancel in Settings.";
            if (sculpt) return $"{BrushLabel()} · Drag to edit. Shift-scroll to resize.";
            if (boxMode) return pieces[selectedMesh + 1].SelectionCount > 0 ? "Drag Move to reposition." : "Click or drag to select vertices.";
            if (straighten) return ToolHint();
            return "Scan the selected cloth in Settings.";
        }

        private string BrushLabel() => brushMode switch
        {
            Brush.ClearSkin => "Clipping Brush",
            Brush.Restore => "Restore Brush",
            Brush.Relax => "Relax",
            _ => SculptBrushNames[Array.IndexOf(SculptBrushes, brushMode)]
        };

        private static readonly string[] ToolLabels =
            { "Select Cloth", "Sculpt", "Move", "Clipping", "Cleanup", "Cleanup" };

        private void DrawFramePicker()
        {
            if (frameWeights.Length > 1)
            {
                int current = Array.IndexOf(frameWeights, weight);
                int frame = EditorGUILayout.Popup("Edit frame", current, Array.ConvertAll(frameWeights, value => $"{value:g}"));
                if (frame >= 0 && frame != current) SetFitWeight(frameWeights[frame]);
            }
            foreach (int preset in Presets)
                if (OverlayButton(new GUIContent($"{preset}%"), weight == preset, GUILayout.Width(53))) SetFitWeight(preset);
        }
        private Ray SceneRay(Rect rect, Vector2 mouse) => EditCamera.ViewportPointToRay(
            new Vector3((mouse.x - rect.x) / rect.width, 1 - (mouse.y - rect.y) / rect.height));

        private int PickOutfit(Ray ray)
        {
            float distance = float.PositiveInfinity;
            int selected = -1;
            for (int i = 0; i < pieces.Count; i++)
            {
                var piece = pieces[i];
                if (!IsVisible(piece)) continue;
                var surface = PickSurface(piece);
                if (surface == null || !surface.Raycast(ray, distance, 0, out _, out float hit)) continue;
                distance = hit;
                selected = i - 1;
            }
            return selected;
        }

        private void DrawMeshPulse(Camera camera)
        {
            if (meshPulseStarted < 0) return;
            float age = (float)(EditorApplication.timeSinceStartup - meshPulseStarted);
            float opacity = Mathf.SmoothStep(0, 1, Mathf.Clamp01(age / .12f)) *
                (1 - Mathf.SmoothStep(0, 1, Mathf.Clamp01((age - .24f) / .61f)));
            var piece = pieces[selectedMesh + 1];
            if (opacity <= 0 || !IsVisible(piece)) return;

            if (piece.OutlineEdges == null) BuildOutlineEdges(piece);
            Vector3 origin = camera.transform.position;
            Vector3 toward = -camera.transform.forward;
            Vector3 view = camera.orthographic ? toward : origin;
            if (outlinePiece != piece || outlineView != view || outlineOrthographic != camera.orthographic)
            {
                outlinePiece = piece;
                outlineView = view;
                outlineOrthographic = camera.orthographic;
                silhouette.Clear();
                float winding = Mathf.Sign(piece.Renderer.localToWorldMatrix.determinant);
                foreach (var edge in piece.OutlineEdges)
                {
                    bool front = IsFront(edge.First, winding);
                    if (edge.Second < 0 ? front : front != IsFront(edge.Second, winding))
                        silhouette.Add((edge.A, edge.B));
                }
            }
            var oldColor = Handles.color;
            var oldDepth = Handles.zTest;
            Handles.color = new Color(Gold.r, Gold.g, Gold.b, .84f * opacity);
            Handles.zTest = CompareFunction.LessEqual;
            foreach (var edge in silhouette)
            {
                // A small viewward bias keeps the line on top of its own surface.
                var a = piece.World[edge.A];
                var b = piece.World[edge.B];
                float bias = Mathf.Max(.00004f, size * .0004f);
                Handles.DrawLine(a + (camera.orthographic ? toward : (origin - a).normalized) * bias,
                    b + (camera.orthographic ? toward : (origin - b).normalized) * bias, 2f);
            }
            Handles.color = oldColor;
            Handles.zTest = oldDepth;

            bool IsFront(int index, float winding)
            {
                var face = piece.Faces[index];
                var a = piece.World[face.A];
                var normal = Vector3.Cross(piece.World[face.B] - a, piece.World[face.C] - a);
                return Vector3.Dot(normal, camera.orthographic ? toward : origin - a) * winding > 0;
            }
        }

        private static void BuildOutlineEdges(Piece piece)
        {
            if (piece.BrushWeld == null) BuildBrushGraph(piece);
            var edges = new Dictionary<(int A, int B), (int First, int Second)>();
            for (int i = 0; i < piece.Faces.Length; i++)
            {
                var (a, b, c) = piece.Faces[i];
                Add(a, b, i);
                Add(b, c, i);
                Add(c, a, i);
            }
            piece.OutlineEdges = new (int, int, int, int)[edges.Count];
            int index = 0;
            foreach (var edge in edges)
                piece.OutlineEdges[index++] = (edge.Key.A, edge.Key.B, edge.Value.First, edge.Value.Second);

            void Add(int a, int b, int face)
            {
                a = piece.BrushWeld[a];
                b = piece.BrushWeld[b];
                if (a == b) return;
                var key = a < b ? (a, b) : (b, a);
                if (!edges.TryGetValue(key, out var prior)) edges.Add(key, (face, -1));
                else if (prior.Second < 0 && prior.First != face) edges[key] = (prior.First, face);
            }
        }

        private static Vector2 ScreenPoint(Rect rect, Vector3 point) =>
            new(rect.x + point.x * rect.width, rect.y + (1 - point.y) * rect.height);

        private void CaptureStroke(Piece piece)
        {
            strokePiece = piece;
            priorCorrection = previewFit.Correction(piece.Source, weight);
            priorProtection = piece.Protection;
            priorDraft = piece.Draft;
            priorUnsaved = unsavedEdits;
            strokeStart = priorCorrection ?? previewFit.GeneratedMotion(piece.Source, weight);
        }

        private bool TrySelectionHandle(Rect rect, out Vector3 projected)
        {
            projected = default;
            var piece = pieces[selectedMesh + 1];
            if (!EditableFrame || piece.SelectionCount == 0 || !IsVisible(piece)) return false;
            var center = Vector3.zero;
            int count = 0;
            for (int i = 0; i < piece.World.Length; i++)
            {
                if (piece.BrushWeld[i] != i || !piece.Selection[i] || (piece.Protection != null && piece.Protection[i])) continue;
                center += piece.World[i];
                count++;
            }
            if (count == 0) return false;
            projected = EditCamera.WorldToViewportPoint(center / count);
            return projected.z > 0 && rect.Contains(ScreenPoint(rect, projected));
        }

        private bool BeginMoveSelection(Rect rect, Event input, int control)
        {
            if (input.alt || input.control || input.command || !TrySelectionHandle(rect, out var projected) ||
                !new Rect(ScreenPoint(rect, projected) - new Vector2(21, 12), new Vector2(42, 24)).Contains(input.mousePosition))
                return false;

            CaptureStroke(pieces[selectedMesh + 1]);
            movingSelection = true;
            strokeMouse = input.mousePosition;
            strokeDepth = projected.z;
            strokeControl = control;
            GUIUtility.hotControl = control;
            input.Use();
            return true;
        }

        private static Vector3[] MoveSelectedVertices(Piece piece, Vector3[] start, Vector3 worldDelta)
        {
            var motion = (Vector3[])start.Clone();
            var localDelta = piece.Renderer.worldToLocalMatrix.MultiplyVector(worldDelta);
            for (int i = 0; i < motion.Length; i++)
                if (InSelection(piece, i) && (piece.Protection == null || !piece.Protection[i]))
                    motion[i] += localDelta;
            return motion;
        }

        private void DrawMoveHandle(Rect rect)
        {
            if (!TrySelectionHandle(rect, out var projected)) return;
            var point = ScreenPoint(rect, projected);
            var handle = new Rect(point.x - 21, point.y - 12, 42, 24);
            EditorGUI.DrawRect(handle, Chrome);
            DrawOutline(handle, Gold);
            GUI.Label(handle, "✥ Move", EditorStyles.centeredGreyMiniLabel);
            EditorGUIUtility.AddCursorRect(handle, MouseCursor.MoveArrow);
        }

        private void DrawClippingArrow(Rect rect, Piece piece, Vector3 center, (int A, int B, int C) face)
        {
            var reference = pieces[SafeBrushReference];
            var surface = PickSurface(reference);
            var normal = Vector3.Cross(piece.World[face.B] - piece.World[face.A], piece.World[face.C] - piece.World[face.A]).normalized *
                Mathf.Sign(piece.Renderer.localToWorldMatrix.determinant);
            float winding = Mathf.Sign(reference.Renderer.localToWorldMatrix.determinant);
            if (surface == null || !TryClearanceContact(surface, winding, center, normal, brushRadius, brushGapMm * .001f, out var direction, out _)) return;
            var from = EditCamera.WorldToViewportPoint(center);
            var to = EditCamera.WorldToViewportPoint(center + direction * Mathf.Max(size * .045f, brushRadius * .6f));
            if (from.z <= 0 || to.z <= 0) return;
            Vector2 start = ScreenPoint(rect, from), end = ScreenPoint(rect, to);
            var line = (end - start).normalized;
            if (line == Vector2.zero) return;
            var side = new Vector2(-line.y, line.x);
            var old = Handles.color;
            Handles.color = Gold;
            Handles.DrawAAPolyLine(2, start, end);
            Handles.DrawAAPolyLine(2, end - line * 7 + side * 4, end, end - line * 7 - side * 4);
            Handles.color = old;
        }
    }
}
