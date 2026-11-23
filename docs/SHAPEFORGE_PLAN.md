# Phase 2 proposal for approval

## Ranked accuracy investments

Ratings are engineering estimates from static inspection. Benefit refers to affected garments. Frequency across the intended customer population is unknown.

| Priority and change | Expected benefit | Difficulty | Regression risk | Confidence | First acceptance case |
|---|---|---|---|---|---|
| 1. Preserve the source frame sequence throughout generation, preview and serialization | High for authored nonlinear motion | Medium-high | High across editing and restore | High | A sideways midpoint followed by an upward endpoint survives save/reload and scrubbing |
| 2. Refresh collision geometry and measure unresolved contacts | High for layered outfits | Medium | Medium, excess lift | High | Body pushes shirt into jacket. Final positions are checked together and reversing renderer enumeration produces equivalent output |
| 3. Resolve ambiguous surface matches using garment neighbors | High near armpits, thighs and straps | Medium-high | Medium, wrong-side bias | Medium-high | Nearby moving arm and stationary torso, each with a matching garment patch. Include reversed cloth winding |
| 4. Preserve independent shells and constrain excessive edge strain locally | High for layered seams and narrow straps | Medium-high | Medium-high, seam opening | Medium | Coincident but separate shells slide independently while a UV-cut sleeve remains closed |
| 5. Support explicitly authored reference body states | High for garments authored in a non-default state | Medium-high | High, slider semantics and caching | High for this case | Fit with a second body shape active, verify the reference endpoint is unchanged and a reverse-authored garment traverses its intended range |

## Delivery sequence

1. Establish a versioned, redistributable baseline after identifying the authoritative editor scripts and .meta GUIDs. Record package hashes, package versions and release instructions. Keep purchased avatars private. Bring forward relevant historic validation only after checking its assumptions.
2. Build small synthetic meshes with known motion, a torso/arm ambiguity, twin shells, seamed tube, slotted strap, layered panels and textured rotating triangle. Record maximum and p95 position error on known targets, relative edge-length change, normal/tangent angular error, count and depth of new intersections where sign is reliable, and excess outward displacement. Compare against each current method using identical inputs. Add private reviews on tight cloth, loose folds, straps, hardware and mirrored pieces across at least two avatars. Inspect lighting and animation at authored knots and interval midpoints. Record time and peak allocation per stage.
3. Carry source knots end to end and preserve saved-mesh invariants. For shapes outside the supported weight domain, explicitly display the supported range and verify Unity evaluation against a baked renderer before extending it. Add reference snapshots once endpoints are unambiguous. Cache using reference weights, transforms and source dependencies.
4. Fix stale layer geometry with bounded simultaneous updates and refit only affected trees. Start with collision detection as a diagnostic, then enable localized correction where baseline comparison identifies new crossings. Track residual per iteration, stop on stagnation, retain the best result and surface the remaining locations.
5. Compare nearest-face transfer against ambiguity-triggered candidate selection and boundary-aware welding. Keep topology continuity soft near genuine seams. Test a small edge-length limiter only on high-strain edges, anchored to reliable correspondences. Advance each change independently only when it improves its target cases and existing simple fixtures still pass.
6. Verify tangent transport on mirrored UVs, custom split normals, degenerate UVs and glossy normal maps. Then expose residuals, frame-specific correction and cancellation in the existing preview. Save, undo, redo, restore one generated shape, reopen the scene and compare original dependencies and renderer bone references after each milestone.

## Decisions on other proposals

| Proposal | Benefit / difficulty / risk / confidence | Decision |
|---|---|---|
| Adaptive extra frames | Medium / medium / medium / medium | After source knots work, compare interpolated cloth with an independently solved midpoint. Insert frames only above a measured error and within a frame/memory budget. |
| Full ARAP, bending or volume optimization | Uncertain / high / high / low | Defer. Existing displacement relaxation already protects folds. Benchmark a local strain limiter and a best-fit rotation for identified rigid details first. Rotating a buckle has a clear measurable target. |
| Exact intersection tests | High diagnostic value / medium / low in diagnostic mode / high | Use bounding-volume broad phase and intersect only candidate triangle pairs. Detect new versus pre-existing pairs. Open mesh signs require separate treatment. |
| UV-aware tangent transport | High for directional shading / medium / medium / high | Ship separately after a glossy-material fixture demonstrates the defect. |
| Automatic method selection | Unproven / medium / medium / low | Postpone. All methods start from the same face. A method switch cannot correct a wrong anatomical attachment. |
| Influence/protection painting | Medium for exceptions / medium / medium / medium | Add optional soft masks only after automatic ambiguity diagnostics. Keep them separate from bone weights. |
| Local shrinkwrap and relax | Local benefit / low-medium / medium / medium | Extend the existing surface brush and draft workflow only for repeated residuals. Restrict to connected selection, preserve baseline gap and support undo per stroke. |
| Preview and diagnostics | Indirect / medium / low / high | Show frame and piece residuals and navigate to the worst patch. Use labels as well as color. |

The most direct independent improvement is to measure deformation disagreement between plausible faces. Two nearly equidistant faces moving identically require no intervention. Two equally plausible faces moving apart deserve extra work. This can constrain both query cost and false alarms.

## Runtime and storage

Build the rest-tree and correspondence once per reference configuration. A bounded neighborhood query increases work mainly where the match is ambiguous. Weighted patches reuse adjacency. RBF adds a dense solve per uncached patch (cubic in patch size) and per-sample evaluation. The connected solve can execute 32 edge sweeps plus six smoothing passes. LayerFit currently compares ordered pairs of pieces and performs multiple tree queries for vertices, edges and face centers. Reject disjoint piece bounds before visiting samples and deduplicate shared edge midpoints. Record worst-case candidate counts on nested layers. Frame count multiplies solve time and delta storage. A frame of three dense Vector3 channels requires about 36 bytes per vertex before serialization overhead. Profile the actual Unity representation and preview allocations before choosing caps. These are complexity estimates, not measured timings.

## Public tool research

Eden Labs documents choosing reference and source mesh states, a minimum surface margin, preview through its controller and a helper Body Base shape for reverse-authored clothing. Its mesh editor includes local shrinkwrap with a gap in millimeters, clipping correction and Relax. ShapeForge already has local clearance, smoothing, preview and non-destructive output in the inspected path. The practical gap is an explicit reference configuration and predictable mapping for unusual authored states. Normal correction details and the internal interpolation or collision solver cannot be established from the generator guide. [Generator](https://kisetter.edenlabs.workers.dev/en/docs/blendshape-generator/) · [Shrinkwrap](https://kisetter.edenlabs.workers.dev/en/docs/mesh-editing-shrinkwrap/)

Haï documents non-destructive Transfer Blendshape, selection of applied blendshapes, and continuously weighted selections from bones, connected polygons, blendshapes and spatial regions. The manual acknowledges transfer can fail on some models. Borrow the idea of a reusable soft influence region for residual editing. Its documentation provides no basis for ranking nearest-face, RBF or collision accuracy against ShapeForge. No proprietary implementation was inspected. [Starmesh reference](https://docs.hai-vr.dev/docs/products/starmesh/reference)

Approval requested for baseline identification and validation first, followed by one independently measured geometry change at a time.
