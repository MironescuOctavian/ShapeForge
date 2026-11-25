# Changelog

## Unreleased

- Fixed rough fits on real avatars. An earlier change in this release tuned the solver on ideal test bodies and made real fits two to five times bumpier (Milltina no-bra: Belt Chest roughness p95 2.1 mm before, 10.3 mm after). The solver smooths strongly again, and real fits are now as smooth as before or smoother (Belt Chest 1.7 mm, Leotard 0.8 mm) and closer to an authored no-bra shape. Garment offsets still turn fully with the body surface.
- Small closed parts such as buttons, rings and studs now rotate with the body as well as move, and stay rigid. Which parts qualify depends on their size and how much they would bend, not on the shape's strength.
- Grabbing in Edit mode keeps the cloth's shading instead of flashing back to rest shading during the drag.
- Edit mode drives outfit pieces that already have the shape to the preview weight. Added Sync Existing Shapes to add MA Blendshape Sync bindings for shapes an outfit already has.
- The Back button in Edit fit is a small arrow left of the title. Generator titles and hints keep their text color after a reload.
- Removed avatar-specific defaults: there is no built-in source shape name, and body masks are detected by how strongly a shape collapses or shrinks faces rather than by one avatar's shape names.
- FitQualityCheck adds a shrinking shape, a torso, cleavage, a strap bridge and a button case, and scores sideways drift against the ideal fit.
- Improved fit accuracy: reach now follows the size of the moving region so loose fabric follows subtle shapes, the normal offset uses smooth normals, the final clearance pass repeats, seams weld within a small tolerance, and a vertex that sank into the front of a thin body part no longer follows the surface behind it.
- Generation is about 2.7 times faster for a single piece and 3.5 times faster for layered fits on dense meshes (parallel solves, shared seam maps, cheaper search-tree build). Results are unchanged.
- Renamed the code namespace to ShapeForge and detect the body by name or by the renderer with the most blendshapes.
- Added Validation/FitQualityCheck.cs and Validation/FitMetrics.cs to measure gap preservation on synthetic bodies and on your own avatar (Tools > ShapeForge > Validation > Measure Fit Quality).

- Redesigned docked Edit Settings with Unity’s native background, a visible outfit list, and actions directly beneath the work area. Clean sessions show End Edit.

- Put Edit Settings inside the existing ShapeForge panel. Back and Resume Edit keep the draft active. The Scene View bar now carries only live fit controls and the active tool hint.

- Moved fit editing into Unity’s Scene View with an Edit Mode badge, fit controls and dockable Edit Settings.
- Added click-to-pick with a fading silhouette, visible vertices, click and box selection, and a selection Move handle. Brushes preview their reach and can cancel the current stroke.

- Added toolbar borders, group separators and aligned controls to match the Fit Editor layout.

- Renamed Preview to Fit Editor. The outfit mesh picker leads the top row, tool help sits beside the active settings, and Sculpt, Vertex Control, Clipping, Restore Brush and Relax have dedicated tabs.

- Clipping scans list patches for the selected piece. Choose a patch to frame it, then preview a local fix and apply or cancel.
- Preview shows whether the whole mesh or a vertex selection is editable, and flags unsaved corrections.

- Added drag-to-paint Clipping, Restore and Relax brushes. Clipping has an independent surface gap, strength and body or garment reference. Restore can recover generated motion or align the original fabric shape locally.
- Added per-garment box selection. Drag to replace, Shift-drag to add, Ctrl/Cmd-drag to subtract, Alt-drag to select through, and Esc to clear. Existing selections limit corrections until cleared.

- Added middle-mouse panning and moved camera views and Display to the top toolbar.
- Grouped clipping contacts into patches with Next issue and a local Fix patch action.
- Added Protect, Unprotect and Restore Generated brushes. Protected vertices retain their original positions across generated frames.
- Sampled clearance between adjacent authored frames and capped accumulated correction per vertex.
- Coalesced brush drags and added Unity Profiler samples for preview rendering, baking, brushes and clipping.

- Fixed preview cleanup during an active stroke or clipping scan, fit setup error reporting, and toolbar wrapping with authored frames.
- Reject stationary source shapes and leave user-edited Modular Avatar remaps in place during restore.

- Branded the compact Unity window and separate fit preview as ShapeForge.
- Moved the menu to Tools → ShapeForge → Blendshape Generator.
- Made Generate the primary action and placed transfer and reference settings under Advanced.
- Added a package guide, a branded README banner and an outfit capture checklist.
- Retains the Phase 2 transfer pipeline, including positive authored frames, per-frame correction, layer refitting, UV tangent transport, reference states and reverse Modular Avatar sync.
