# Phase 1 handoff · 2026-11-23

Dedicated checkout D:/Unity Personal/ShapeForge-Phase1, branch dev/shapeforge-vnext, based on origin/main 3f5b242. History before the audit consists of cced8bd, 15b8b21 and 3f5b242. The remote has no versioned application source at this revision. Confirm the intended source baseline before interpreting local findings as defects in a branded release.

The local project uses Unity 6000.0.67f1, VRChat Avatars 3.11.0-beta.1 and Modular Avatar 1.19.0 from installed manifests. A local tool README records earlier checks against a release candidate. The project contains generated belt, bikini, leotard and other meshes. Historical temporary Unity validation projects contain MilltinaValidation.cs and MilltinaWindowValidation.cs. These are leads for recovering fixtures. No current Unity execution, comparison benchmark, screenshot reproduction or visual approval was performed during Phase 1.

See SHAPEFORGE_TECHNICAL_FINDINGS.md for the pipeline, defect triggers, SDK stack and UI review. See SHAPEFORGE_PLAN.md for the ranking, comparative research, acceptance fixtures and decision gates. The website reference was unavailable through the web reader. Styling guidance follows the requested design brief.

The following SHA-256 fingerprints identify the inspected complete local package source independently of the remote README history.
- FitPreview.cs  531FC98F19FCD001D29B362806A03D9A6140860E2B3BB156D8B12F96C4478BB9
- GeneratedMeshInfo.cs  13C896E7661D242622FC04D92D999F945C1425547533649AD98FB0FF899C0A6C
- GeneratorAssets.cs  F147B44D72DDF6B41B9E84A6AD56B3DA364623CD533A1AE6633B6F2C042EF994
- GeneratorWindow.cs  F0EAA61CA73B2A9966AC032E51B6CFDDF69D5A7170C650606D758BD6EC44520D
- LayerFit.cs  D3D5D15C69EDA9C159E63A4CA7350D7174233723A92924EA9B99FF11BFDEF95C
- ModularAvatarSync.cs  4DF82E041DB47A2964AD1F695815AC2B416DD45AE9D7FAF34A7F39A20D0FA88A
- ShapeTransfer.cs  CDCECC81ADE5E81305FCEBC4C4EB657C65BE7E2915BD85C4EEEFAFF471F5435C
- TransferField.cs  5939A01723C3814C3634B94CB30B95287FC415FF6C9C6E8BBCA28FCB44EEE5DF

## Phase 2 delivery · 2026-11-23

The user confirmed the complete local Outfit Blendshape Generator folder as authoritative. Development continued locally after GitHub HTTPS authentication blocked the first push. The signed-in Unity environment is independent of GitHub authentication.

| Commit | Verified task |
|---|---|
| 9edf8fa | Capture eight Editor scripts, package README and original Unity GUIDs. Confirm all 20 copied files matched their source. |
| 4dc723b | Generate and serialize authored positive frame weights, include weight 100, preserve additional frames during preview and selective restore. |
| fe74b64 | Rotate imported tangents with the changing texture direction while retaining handedness. |
| 8f0be11 | Use neighboring motion to resolve ambiguous attachment, distinguish seam partners from touching vertices, limit strain at weak anchors and refresh corrected layer surfaces. |
| f856c0f | Expose explicit authored reference modes, support reverse mapping, edit individual knots, preserve per-frame redo precision and extend clipping inspection. |

Unity 6000.0.67f1 ran in isolated projects. The synthetic suite passed all three methods, actual baked vertices at 0/25/50/75/100, a shape returning to rest at 100, an ambiguous moving surface, split seam versus touching panels, rigid tangent rotation, mirrored tangent handedness, reference offset, reversed motion, strain clamp, frame editing and saved asset reimport after selective restoration. A corrected private regression harness queries the 100 frame by weight and includes the edited weight in its reflected history entry. Its previous assumptions were specific to single-frame output.

The real-asset suite passed on Day_slip, Day_shirts, Ink Qipao, Hoodie, Bikini and Belt Chest. It verified base geometry and skinning, renderer references, existing shape frames, interpolation, restore/reuse, grouped Undo/Redo, scene reopening, hard detail and seam checks. Additional runs passed preview corrections and history, layer enumeration order, and Modular Avatar preview plus NDMF processing on the installed SDK dependencies. A reversed binding mapped 37 to 63 and could be removed by restore. No private meshes or fixture exports were added to Git.

For a repeatable public smoke test, import the local package into a disposable Unity 6 project under Assets. Copy Validation/AccuracyChecks.cs into Assets/Editor **after** the package import completes. Run the editor with -batchmode -nographics -quit -projectPath <scratch-project> -executeMethod AccuracyChecks.Run -logFile <log>. Require exit zero and the SHAPEFORGE ACCURACY CHECKS PASSED marker. The check creates synthetic assets in the scratch project.

Local package: D:/Unity Personal/ShapeForge-Phase2-Local.unitypackage. SHA-256 44BECA656930B2F7B1493D0F16AF7EA7CF10C51C40548A31B402FED106E90578. Verified the nine asset payloads and package GUIDs against the checkout. Clean import and smoke test both exited zero. The development branch is local. No publishing or installation into the user's authoring project was performed.

## Phase 3 delivery · 2026-11-23

Commit 09001f5 applies the ShapeForge menu and branding, a collapsed Advanced section, and clearer preview controls. The compact generator and separate Unity preview were captured in a graphical Unity 6000.0.67f1 session using a synthetic garment. See docs/images for both interface captures and the purchased-outfit capture checklist.

The final ShapeForge.unitypackage at D:/Unity Personal/ShapeForge.unitypackage contains eight Editor scripts and the bundled README with the original Unity asset metadata. Its SHA-256 is 83906DB9BF1CAD0BBD9D8C1821DEEFDEC022FD7907A4C4446B2A8B8F432BAA17. Package export and clean import exited zero. The imported package passed AccuracyChecks.Run, including all three transfer methods, baked intermediate weights, alternate reference and reverse transfer, seam separation, shading and selective restore.

The approved icon was unavailable. The README banner uses the requested charcoal and gold palette without an icon. Purchased outfit images are pending documented publication rights. No release or tag is part of this phase.

## Phase 4 verification

See [release readiness](SHAPEFORGE_RELEASE_READINESS.md) for independently rerun evidence, regression fixes, the rebuilt package checksum and public release gates.
