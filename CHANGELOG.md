# Changelog

## 0.9.0

First public release.

### Transfer
- **Fabric solver (default).** Smooths body motion by the cloth's distance from the surface, lets loose fabric hang, carries unsupported straight sections by their end points, keeps small closed parts rigid, solves each garment as one surface and pushes cloth back outside the body.
- Classic Barycentric, Weighted and RBF transfers are available under Advanced.
- Preserves authored positive blendshape frames.
- Fits layered garments together and checks for cloth and body intersections.
- Generic body detection, with manual selection available.

### Editing
- Scene View editor with weight preview, cloth selection and isolation.
- Sculpt, smoothing, clipping, restore and relax brushes, vertex control, span straightening, protection masks and correction history.

### Output
- Saves separate mesh assets that keep skinning and existing blendshapes. Generated shapes can be removed.
- Optional Modular Avatar slider synchronization.

### Quality checks
- Automated checks for transfer accuracy, fit quality, the clipping workflow and Scene View editing.
- Development tools that score generated shapes against reference shapes and export per-vertex data.
