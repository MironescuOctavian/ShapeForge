# Installation and release workflow

## Install in Unity 6

1. Obtain ShapeForge.unitypackage from your development build or from GitHub Releases when available.
2. Open Assets → Import Package → Custom Package and select the file.
3. Import the ShapeForge tool folder. The existing folder name and Unity GUIDs support upgrades from the predecessor.
4. Open Tools → ShapeForge → Blendshape Generator.
5. Select the avatar body, outfit and source shape, then preview or generate.

## Build the package

Import or copy the tracked Assets/Outfit Blendshape Generator folder and its .meta file into a clean Unity 6000.0.67f1 project. Select the tool folder and use Assets → Export Package. Disable Include Dependencies and review the export list. Include the eight ShapeForge Editor scripts, their metadata and the bundled README. Save the package as ShapeForge.unitypackage.

## Clean project check

Import the package into a second scratch Unity 6 project. Once import has completed, place Validation/AccuracyChecks.cs under Assets/Editor. Run Unity batch mode with -executeMethod AccuracyChecks.Run and require exit code zero plus the SHAPEFORGE ACCURACY CHECKS PASSED log marker. Open the generator and its separate fit preview in a graphical editor when testing interface changes.

## GitHub release

After approval, create the version tag from the verified commit and open a release on GitHub. Attach the tested package, provide its SHA-256 and include the matching changelog and compatibility details. A development branch push does not create a release.
