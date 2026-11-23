# Changelog

## Unreleased

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
