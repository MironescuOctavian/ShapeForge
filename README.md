# ShapeForge

![ShapeForge — Precision Blendshape Generator](docs/images/banner.png)

ShapeForge transfers body blendshapes to clothing already made for the same avatar. Preview the fit in Unity, correct problem areas, then save a generated mesh for each selected garment.

## Compatibility

- **Unity 6.** Development and package validation target **6000.0.67f1**.
- **VRChat avatars.** Phase 2 was tested with VRChat Avatars SDK **3.11.0-beta.1** and Modular Avatar **1.19.0**. Modular Avatar is optional. Without it, set the generated garment shape manually.
- **Unity Editor only.** The package contains ShapeForge editor scripts and documentation.

## What it does

- Transfers shapes using Barycentric, Weighted or RBF interpolation. Barycentric is the default.
- Preserves authored positive blendshape frames.
- Fits selected pieces together and checks for suspect cloth and body intersections.
- Provides a separate avatar preview with fit scrubbing, fixed views, isolation, sculpting, smoothing, Straighten Span, and correction history.
- Saves a separate mesh asset, preserving original mesh data, skinning, and existing shapes. Restore removes generated shapes.

## Install

Download **ShapeForge.unitypackage** from [GitHub Releases](https://github.com/MironescuOctavian/ShapeForge/releases) once a release is available. For a development build, follow the [installation and package guide](docs/INSTALLATION.md).

In Unity, choose **Assets → Import Package → Custom Package**, select the package, and import it. Open **Tools → ShapeForge → Blendshape Generator**.

## Transfer a shape

1. Select the **Body Mesh**, **Outfit**, and **Source Shape**. You can drag a clothing object or the parent of several pieces from the Hierarchy.
2. Check the eligible pieces you want to fit. Open **Advanced** to choose a transfer method.
3. Click **Edit**. In Unity’s Scene View, look for **ShapeForge Edit Mode**, then click a visible garment or choose it in the mesh menu. Compare fit at **0**, **50**, and **100**.
4. Open **Settings** in the Scene View bar to return to the docked ShapeForge panel. Use **‹ Generator** and **Resume Edit** to switch views without losing the draft. In **Vertex Control** (**B**), click or box-select vertices, then drag **Move**. Hold **Shift** to add or **Ctrl/Cmd** to subtract. Use **Straighten Span** for straps. For brushes, **Shift-scroll** resizes the brush and **Esc** cancels an active stroke.
5. Click **Save Corrections** or **Discard** to end Edit Mode. **Generate** applies the automatic fit, and **Restore** removes generated shapes.

## Unity interface

The screenshots show the actual Unity windows with a synthetic test outfit.

<img src="docs/images/main-window.png" alt="ShapeForge generator with garment selection and the gold Generate button" width="330"> <img src="docs/images/fit-preview.png" alt="ShapeForge fit preview with its fit slider, view controls and correction tools" width="390">

## Outfit demonstrations

The [capture checklist](docs/images/README.md) identifies the stills awaiting permission for publication. Each example needs a real Unity capture at 0%, 50%, and 100% using the same camera and lighting.

## Known limitations

ShapeForge targets garments authored for the same avatar. Existing intersections can remain, especially around tight cups, straps, and stacked panels. The clipping check flags suspect contacts in the preview. It cannot certify all animation poses. Negative authored frame weights are currently unsupported. Layered generation and additional source frames take longer.

## Development

Work on main and follow [AGENTS.md](AGENTS.md). Run the [Unity smoke test](docs/INSTALLATION.md#clean-project-check) and see the [changelog](CHANGELOG.md) and [release-readiness report](docs/SHAPEFORGE_RELEASE_READINESS.md). For bug reports, include Unity and SDK versions, blendshape weights, transfer method, and a shareable reproduction.

[MIT license](LICENSE)
