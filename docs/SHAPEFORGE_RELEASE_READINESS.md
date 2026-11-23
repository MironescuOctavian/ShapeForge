# ShapeForge release readiness

## Verdict

Ready for controlled Unity 6 preview testing. Hold the first general public release until nonlinear clearance has a fixture that tests the actual generated interval, and until a second avatar family and a production normal-mapped garment have been checked. The current evidence applies to same-avatar transfers with a few selected garment pieces per fit.

## Verified behavior

- A fresh Unity 6000.0.67f1 project imported the rebuilt package and passed the independent smoke run. Unity baked expected positions at 0, 25, 50, 75 and 100 across Barycentric, Weighted and RBF for a shape that travels sideways and returns to rest. Authored knots survived editing, selective restoration and asset reimport. The previous package failed the added stationary-source test.
- The synthetic ambiguity case follows the moving source face despite a closer stationary face. The seam case separates point-touching shells and joins the tested split boundary. A planar rotation transports the tangent while preserving its handedness. A private synthetic case exercises rotated, mirrored, nonuniform source-to-garment transforms. These cases exercise specific defects rather than ranking transfer methods across garments.
- In the installed VRChat SDK and Modular Avatar environment, six private garments passed original mesh, renderer reference, skinning and existing-frame checks. Grouped Undo and Redo, restore and reuse, scene reopening, correction save and history, and a preview clipping scan passed. Modular Avatar synchronized standard and inverted sliders. A standard binding was also checked through NDMF processing and an animation curve.
- A graphical Unity run rendered the preview with a test mesh. At 540 and 710 pixels the fit slider, frame picker, 0/50/100 presets, view controls and correction tools were legible. Disabled history and save states matched an unedited draft. The main flow and colors were inspected in source and against the existing Unity capture. A simulated in-flight stroke and clipping scan exercised preview close and cleanup. A separate script reload during the simulated active preview passed resource cleanup. This did not exercise a full mouse-gesture matrix, every editor layout, or light theme.

The rerun of the Midnight layered fixture sampled 6,270 contacts at each of 50 and 100. Joint fitting measured zero penetrations at both values. Fitting the outer piece independently measured 70 at 100 and a maximum penetration of 2.88 mm. Reversing renderer enumeration yielded identical motion. The three-piece fit took a median of 6,878 ms over five runs on this workstation and reached the layer pass cap. This fixture also passed joint fitting in the previous baseline. It verifies behavior and cost on a single outfit.

## Defects fixed

| Commit | Finding and fix |
|---|---|
| 4d286ec | Preview shutdown cleared the fit before an active sculpt stroke could rebake, and a clipping iterator could remain referenced. Close now ends the stroke without rebaking and disposes the scan. Generate reports fit setup errors inline. Toolbar layout reserves space for the frame picker. |
| b2d2963 | A stationary single-frame source bypassed validation and produced a zero-motion output. It is rejected again. A stationary frame within an otherwise moving shape remains supported. Verification now also asserts original bind poses, weights, UVs and normals. |
| 1b5fffa | Modular Avatar restore matched curve endpoints alone. This could delete edits with the same endpoints. Restore now checks the actual linear tangents and weighting. Tested a nonlinear serialized curve and confirmed a persisted custom midpoint and its component survive ownership removal. |

## Geometry and performance risks to close

1. **Nonlinear clearance intervals.** Each layer solve samples a straight path from neutral to the current knot. With knots at 50 and 100, the solver for 100 internally tests half of the 100 motion, which can be a different surface than the actual authored 50. That can miss a crossing or add outward displacement for a path the garment never takes. A synthetic right-angle motion with a thin obstacle should compare actual baked intervals and require no false outward push before claiming nonlinear clipping coverage.
2. **Ambiguous attachment and seam limits.** Distance ranking uses the body's local metric. Relative point and vector transforms are correct on the mirrored fixture, but nonuniform body scaling can still change anatomical ranking. Consistent wrong matches across a whole connected patch can survive neighbor voting. Exact duplicated edges can leave near-coincident seam vertices unconnected or join independently authored panels with indistinguishable topology. Retain confidence diagnostics and validate difficult armpits, folds and straps on a second avatar family.
3. **Distortion and shading.** The local strain clamp is neither a global length nor volume guarantee. Tight folds and stacked shells still need inspection. UV tangent transport passed the planar test. Faces whose averaged normal turns 90 degrees or more keep their base shading under the current guard. Degenerate UVs fall back to rotation with the normal. A textured glossy asset and poses with strong bending remain unverified.
4. **Collision and editor responsiveness.** Nearest-face and edge intersection tests remain sampling-based. Coplanar overlaps, unsampled frame intervals and narrow contacts can escape detection. Layer correction may hit its pass cap. Multi-frame fitting repeats correspondence and clearance on the editor thread and uses dense deltas per frame. Time larger outfits before advertising responsiveness. No comparison with Kisetter or Starmesh was run.

## Package and publishing

The rebuilt local package is D:/Unity Personal/ShapeForge.unitypackage. SHA-256 97E6796DB5EEEB56A17D135C1FB6EAB98218E055141721F3A03F16C8D658778C. Exported package payloads and Unity GUIDs matched the eight tracked scripts and bundled README. No SDK, materials, validation scripts, purchased meshes or generated assets were included.

The development README, installation guide, changelog and MIT license are present. Local documentation paths and image references resolve. The interface screenshots are genuine Unity captures using synthetic assets. The outfit examples are labeled placeholders. Obtain the approved icon file and verify publication rights before committing purchased-outfit captures. The tracked tree and reachable file paths were checked for proprietary file types and common credential patterns, with no matches. Ignore rules excluded representative cache, secret and private asset paths. This was a repository inspection and pattern scan rather than proof that no secret could ever have existed.

The verified work is on dev/shapeforge-vnext. The fetched default branch was already at merge commit 4d81dee, which has the Phase 3 tree. No merge, version tag or GitHub Release was performed.
