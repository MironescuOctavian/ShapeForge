# ShapeForge

**Precision Blendshape Transfer & Generation for Unity**

ShapeForge is a Unity Editor tool designed to generate accurate outfit blendshapes from existing avatar body blendshapes, without requiring Blender.

Built with VRChat avatar creators in mind, ShapeForge focuses on precise mesh deformation, garment preservation, and an intuitive editing experience.


## Features

- **Blendshape Transfer** — Generate missing clothing blendshapes from existing avatar body shapes.
- **Multiple Transfer Algorithms** — Barycentric, weighted, and RBF methods.
- **Garment-Aware Deformation** — Preserve clothing structure while adapting to the source deformation.
- **Interactive Preview** — Inspect generated shapes directly inside Unity.
- **Correction Tools** — Refine problem areas without switching to Blender.
- **Non-Destructive Generation** — Keep original mesh assets intact.
- **Modular Avatar Integration** — Synchronize generated shapes with avatar blendshapes where supported.

## Compatibility

- Unity 6 — primary development target
- VRChat SDK3 Avatars
- Modular Avatar integration

Specific tested versions will be listed with each release.

## Installation

1. Download the latest `ShapeForge.unitypackage` from GitHub Releases.
2. Open your Unity avatar project.
3. Select **Assets → Import Package → Custom Package**.
4. Import ShapeForge.
5. Open **Tools → ShapeForge → Blendshape Generator**.

## How to Use

1. Select the avatar's source body mesh.
2. Choose the blendshape you want to transfer.
3. Select the outfit mesh or supported outfit pieces.
4. Preview the generated deformation.
5. Generate and save your new outfit blendshape.

Use the included correction tools if an outfit needs additional adjustment.

## Scope

ShapeForge specializes in transferring and generating blendshapes for clothing already designed for the target avatar.

It is **not an outfit conversion or avatar retargeting tool**.

The objective is to achieve high-quality blendshapes with minimal manual correction, entirely inside Unity.

## Limitations

Automatic deformation quality depends on outfit topology, clothing structure, and the relationship between the source body and target mesh.

Difficult garments may require manual corrections. Blendshape generation alone cannot guarantee clipping-free behavior under every animation or physics pose.

## Contributing

Bug reports, suggestions, and contributions are welcome through GitHub Issues and Pull Requests.

## License

License information will be provided before public release.

## Disclaimer

ShapeForge is an independent community tool and is not affiliated with or endorsed by VRChat or Unity Technologies.
