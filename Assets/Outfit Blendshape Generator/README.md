# Outfit Blendshape Generator v0.34

Outfit Blendshape Generator copies a selected body blendshape to compatible clothing. It creates a copy of each selected mesh, preserves the original fit at slider 0 and keeps the existing skinning. It leaves the source avatar, imported outfits, and PhysBones untouched.

## Compatibility

- **Unity 6000.0.67f1:** Core generation and Modular Avatar slider synchronization were verified with VRChat Avatars SDK 3.11.0-beta.1 and Modular Avatar 1.19.0-rc.0.
- **Unity 2022.3.22f1:** Core generation, batch restore, Undo/Redo, and scene reopening passed in a separate validation project. Modular Avatar synchronization on this editor version remains unverified.
- **Avatar and outfits:** Select an avatar body and use clothing already fitted to it at weight 0. The package contains the editor tool. Avatar and outfit assets are obtained separately.

## Install and generate

1. Import `OutfitBlendshapeGenerator.unitypackage` in Edit Mode.
2. Open **Tools → Octa → Outfit Blendshape Generator**.
3. Set **Body Mesh** to your avatar’s body renderer. The tool can also detect Milltina’s body automatically.
4. Drag the outfit’s parent object from the Hierarchy into **Outfit**. Drag a single piece instead if you only want to work on that mesh.
5. Choose a **Source Shape**. In **Outfit Pieces**, check the garments that overlap the area it moves. Use **Preview** to compare the fit at 0, 50, and 100 and make local corrections.
6. Click **Generate**. Check the fit and shading at **0**, **50**, and **100**, including poses you use in VRChat.

With Modular Avatar installed, the tool can use **MA Blendshape Sync** to drive the generated outfit shape from the matching body slider. Check any reported binding conflict in the Inspector. Without Modular Avatar, set the garment’s blendshape weight manually in Unity to preview it.

## Restore and regenerate

- **Restore** beside a piece removes the selected generated shape. Removing the final generated shape reassigns the original mesh.
- **Restore All** removes the selected generated shape from all applicable pieces in the outfit, including unchecked pieces.
- A batch of changes can be undone as one Unity Undo action.
- To regenerate a piece after an update, restore it first, then generate again. If the outfit already includes the target blendshape, the tool leaves that shape alone.

Restoring a piece leaves the generated mesh asset on disk so it can be reused. Check scene and prefab references before deleting generated assets.

## Where files go

The package installs to `Assets/Outfit Blendshape Generator`. Generated mesh assets go in its `Generated` subfolder. You can move the entire tool folder anywhere under `Assets` using Unity’s **Project** window. For example, moving it under `Assets/Tools` makes new meshes appear in `Assets/Tools/Outfit Blendshape Generator/Generated`.

## Fit and clipping

The fitter uses the garment’s original clearance and its own triangle connections. Straight fabric spans take support from their ends. Compact, closed details can move as a unit. The same rules apply to every compatible outfit. For a separate belt that already fits, leave it out of the generation batch.

Select overlapping pieces together. Preview and Generate compare the fitted layers with their original separation and automatically adjust close outer surfaces. For a belt that already sits correctly, restore it and leave it unchecked.

The fit is generated from the neutral body mesh and the selected source shape. Preview also shows your current renderer weights. Other body shapes can substantially change coverage at every slider value. Check weight 0 first to see the fit your other settings produce.

Keep any body shrink shapes or mesh hiding the outfit already relies on. Fitting works best on garments already aligned to the body. Loose or layered pieces, strong folds, and glossy materials can make stretching or shading changes more visible near slider 100. The generator limits local distortion, but results still depend on the garment geometry and its existing skinning. It does not solve collisions or simulate PhysBones.

## Export from source

In Unity’s Project window, select the tool’s `Editor` folder and `README.md`, then choose **Assets → Export Package**. Clear **Include dependencies**. Check that the export contains only the tool files. Leave `Generated`, purchased assets, the VRChat SDK, and Modular Avatar out of the package.

## Fix a small area in Preview

Open Preview with the pieces you want to fit together. Click **Check clipping** at 50 or 100. Red marks possible skin crossings, orange marks possible clothing crossings, and gray marks a crossing also found at 0. Marks are sampled hints. Compare with the actual surface as you orbit.

Click **Sculpt** to switch to weight 100. Red dots show the selected mesh’s vertices. Hover to see affected vertices in blue and drag to see the moving vertices in amber, then left-drag the fabric. Scroll during a drag to resize the brush. Hold Shift while dragging to move it in depth. The deformation falls off toward the edge of the brush. Toggle **X**, **Y**, or **Z** to mirror the stroke across the mesh’s local axes. You can enable several axes together. Use **Undo** and **Redo** or Ctrl+Z and Ctrl+Y to move through edits. Recheck at 50 and 100, then click **Save corrections** to generate meshes for the previewed pieces. For a bowed strap, choose **Straighten Span**, click both attachment points, and review the yellow strip. Set **Strength** and click **Apply Span**. The clipping check runs after applying. **Restore** on the generator returns each piece to its original mesh.
Choose the garment in **Mesh** before editing. Turn on **Isolate** to show only that piece and the body. Turn it off to check clipping between all layers. **Cull backfaces** skips vertices facing away. **Z-test** skips hidden surfaces. The white cross shows where a click will land.

## Source shapes

Choose **Source Shape** to generate one independent blendshape at a time. **Compatible** means the piece overlaps the area that shape deforms. Check the fit in Preview. You can generate another supported shape on the same outfit piece. **Restore** and **Restore All** remove the currently selected shape from the affected pieces. Previously generated and authored shapes remain available. Preview uses the selected source shape for its slider and saves corrections to that shape.

The list comes from the selected body mesh. Empty shapes and section headers are omitted. Likely body masks are hidden by default. Turn on **Review hidden shapes** if a real body slider was filtered by mistake. For other avatars, select their body manually and use clothing authored to fit it at zero. Combination fits should be checked with the other outfit sliders active.

### Sculpt brush modes

**Grab** drags the selected fabric. **Push Out** moves along the fabric normals as you drag up or down. **Smooth** softens changes in blendshape displacement along connected edges. **Multiply** applies the chosen scale to the existing motion inside the highlighted brush on click. **Clear Skin** lets you click on a poke-through and adds a bounded outward correction where it finds skin under the selected garment. Clear Skin picks through the body while leaving garment depth checks enabled.

Use **Undo** to compare edits, and check the fit again at both 50 and 100. Multiply scales the existing direction, so it may also increase unwanted movement. Clear Skin uses sampled local contact and works best on small patches.
### Transfer method

**Barycentric** is the default and uses the nearest triangle. **Weighted** and **RBF** are experimental alternatives for the initial transfer. Weighted blends nearby vertices on the contacted patch. RBF fits a local smooth displacement field to the same patch. All three share the connected-fabric and clearance passes. Compare in Preview before saving. The default remains selected for existing setups.
