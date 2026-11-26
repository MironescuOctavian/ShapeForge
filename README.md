<div align="center">

<img src="docs/images/banner.svg" alt="ShapeForge: precision blendshape transfer for Unity" width="100%">

<br>

![Unity 6](docs/images/badges/unity.svg)
![VRChat avatars](docs/images/badges/vrchat.svg)
![Editor only](docs/images/badges/editor.svg)
[![MIT license](docs/images/badges/license.svg)](LICENSE)

[Installation](#installation) · [Usage](#usage) · [How it works](#how-it-works) · [Evaluation](#evaluation) · [Limitations](#limitations)

</div>

---

ShapeForge is a Unity Editor tool that transfers blendshapes from a character's body mesh to the clothing meshes made for that character. When the body changes shape, the clothing follows it.

Done by hand, this means sculpting every garment separately in a 3D modeling package. ShapeForge generates the shape inside Unity, lets you inspect and correct it in the Scene View, and saves the result as a new mesh asset. Source meshes are never modified.

## Features

| Feature | Description |
|---|---|
| **Fabric transfer** | A custom solver in which fitted cloth follows the body, while loose cloth hangs and spans gaps. |
| **Layered clothing** | Fits several garments together and checks for cloth-to-cloth and cloth-to-body intersections. |
| **Multi-frame shapes** | Preserves the positive in-between frames of the source blendshape. |
| **Scene View editor** | Previews the fit at any weight, with sculpting, smoothing, vertex editing, span straightening and undo. |
| **Non-destructive output** | Saves separate mesh assets that keep skinning and existing blendshapes. Generated shapes can be removed. |
| **Modular Avatar support** | Optionally links garment sliders to the body. Modular Avatar is not required. |

## Installation

1. Download the `.unitypackage` file from [Releases](https://github.com/MironescuOctavian/ShapeForge/releases).
2. In Unity, choose **Assets → Import Package → Custom Package** and import the file.
3. Open **Tools → ShapeForge → Blendshape Generator**.

To build the package from source, see the [installation and release guide](docs/INSTALLATION.md).

## Usage

1. Select the **Body Mesh**, the **Outfit** and the **Source Shape**. The outfit can be a single garment or the parent of several.
2. Check the garments to include.
3. Click **Edit** to review the fit at weights **0**, **50** and **100**. This step is optional.
4. Click **Generate**.

<details>
<summary><b>Editor controls</b></summary>
<br>

- The Scene View shows **ShapeForge Edit Mode** while editing. Editing starts on **All Cloth**. To work on one garment, pick it from the cloth list or use **Select Cloth**.
- **Back** and **Resume Edit** switch views without discarding the draft.
- **Vertex Control** (<kbd>B</kbd>): click or box-select vertices, then drag the **Move** handle. <kbd>Shift</kbd> adds to the selection and <kbd>Ctrl</kbd>/<kbd>Cmd</kbd> subtracts from it.
- **Straighten Span:** click one attachment point, the middle of the strip, then the other attachment point. Adjust **Strip half-width** and **Span strength** before applying.
- **Brushes:** <kbd>Shift</kbd> + scroll changes the size. <kbd>Esc</kbd> cancels a stroke.
- **End Edit** closes a draft with no pending changes. Otherwise, choose **Save Corrections** or **Discard**.

</details>

## How it works

<img src="docs/images/pipeline.svg" alt="Fabric transfer in four steps: read the body, let fabric hang, solve the garment as one piece, keep it outside the body" width="100%">

The Fabric solver treats each garment as a connected surface instead of moving every vertex on its own:

- **Distance-aware sampling.** Body motion is smoothed according to how far each part of the garment sits from the surface.
- **Whole-garment solve.** A sparse linear system with area-weighted terms, solved by conjugate gradients, keeps stiffness consistent across dense and low-polygon meshes.
- **Geometric constraints.** Unsupported straight sections follow their end points, small closed parts stay rigid, and cloth that would end up inside the body is pushed out and solved again.

The classic **Barycentric**, **Weighted** and **RBF** (radial basis function) transfers remain available under **Advanced** for comparison.

## Evaluation

Some garments ship with a blendshape that their creator sculpted by hand. These serve as reference data: the creator's shape is removed, ShapeForge regenerates it, and the two results are compared vertex by vertex. A higher score means a closer match.

| Test set | Previous solver | Fabric solver |
|---|:---:|:---:|
| Shape used for tuning | 48.6% | **61.3%** |
| Held-out shape A | 55.7% | **57.1%** |
| Held-out shape B | 55.9% | **61.4%** |
| Held-out shape C | 34.6% | **40.0%** |
| Held-out shape D (8 meshes) | 79.5% | **84.7%** |

On the tuning shape, the number of vertices ending up inside the body at full weight fell from **775 to 311**.

> [!NOTE]
> All reference data comes from clothing made for **one character model**, so results on other models are not yet measured. The score reflects how closely per-vertex motion matches the reference. It is not a pass rate.

## Compatibility

| Component | Details |
|---|---|
| **Unity** | Unity 6, developed and validated on 6000.0.67f1. Editor only. |
| **VRChat Avatars SDK** | Optional. Version 3.11.0-beta.1 was used in earlier integration testing. |
| **Modular Avatar** | Optional. Version 1.19.0 was used in earlier integration testing. |

## Limitations

- Clothing must be made for the same character model. ShapeForge does not refit clothing between different models.
- Tightly fitted areas, thin straps and stacked layers can still intersect. The clipping check flags likely problems but cannot cover every animation pose.
- Source blendshapes with negative frame weights are not supported.
- Layered clothing and multi-frame shapes take longer to generate.

## Development

The editor tool is written in C# and lives in [`Assets/ShapeForge`](Assets/ShapeForge). Automated checks and evaluation tools are in [`Validation`](Validation). See the [changelog](CHANGELOG.md) for the release history.

**Reporting an issue:** include your Unity version, package versions, blendshape weights, the transfer method and, if you are permitted to share one, a reproduction.

---

<div align="center">

Released under the [MIT license](LICENSE).

</div>
