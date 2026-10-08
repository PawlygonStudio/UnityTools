# Pawlygon Unity Tools

Unity Editor tools for Pawlygon's avatar face-tracking workflow.

This package helps you duplicate source avatar assets, prepare a working folder structure, swap updated meshes and the primary humanoid Animator rig from a modified FBX back onto a prefab, and generate `.hdiff` patch files for distribution.

![Unity](https://img.shields.io/badge/Unity-2022.3%2B-black?logo=unity)
![License](https://img.shields.io/badge/License-CC%20BY--NC--SA%204.0-lightgrey)
![Version](https://img.shields.io/github/v/tag/PawlygonStudio/UnityTools?label=version&cb=1)

## Main features

- `Getting Started` window from `!Pawlygon/Getting Started` — lists every tool in workflow order (Prepare, Check, Tune, Publish) with what it's for and a quick status for the selected avatar. It can optionally open once after each package update (off by default)
- Guided `Avatar Setup Wizard` available from `!Pawlygon/Avatar Setup Wizard`, with a step bar you can go back through and **Resume** when the working folders already exist
- Batch setup for one or many avatar entries in a single run
- Shared-folder or separate-folder output layouts depending on your project needs
- Automatic creation of working folders, copied FBX assets, copied prefabs, and working scenes
- Reimport detection for modified FBX files
- Review UI for matching FBX skinned meshes to prefab skinned meshes and replacing the prefab's primary humanoid Animator rig before applying replacements. When the modified FBX changes a mesh's skeleton, its bones are remapped onto the prefab by name, and meshes whose bones can't be found are flagged before anything is applied
- Automatic creation of `FTDiffGenerator` assets for patch generation
- `.hdiff` generation for both FBX and `.meta` changes using bundled `hdiffz` binaries
- Optional prefab helpers for [Pawlygon VRCFT](https://github.com/PawlygonStudio/VRC-Facetracking) setup and importing the latest [PatcherHub](https://github.com/PawlygonStudio/PatcherHub) package
- Built-in `FX Check` wizard step that runs the gesture and eye-blink analysis on each generated avatar, copies its FX controller into the avatar's `VRChat/` folder, applies the guards to that copy and assigns it to the avatar
- `FX Gesture Checker` available from `!Pawlygon/Tools/FX Gesture Checker` — scans an avatar's FX AnimatorController for gesture-driven facial expression transitions (`GestureLeft`/`GestureRight`) and applies a `FacialExpressionsDisabled` guard so they do not fire when face tracking is active, and detects eye-blink layers to apply an `EyeTrackingActive` guard so blinking stops while eye tracking is active. Supports per-transition and per-layer guards, blink-layer confidence detection, sub-state machines and work-on-copy mode. Transitions that return to neutral are left unguarded so expressions can't get stuck, guarded layers return to their default state once face or eye tracking stops, and guards from older versions are detected and repaired. A single **Apply Recommended** action guards every gesture expression layer, repairs outdated guards and guards high-confidence blink layers; individual choices are under Advanced
- `Face Tracking Blendshapes` available from `!Pawlygon/Tools/Face Tracking Blendshapes` — checks a scene avatar, prefab or model for the [Unified Expressions](https://docs.vrcft.io/docs/tutorial-avatars/tutorial-avatars-extras/unified-blendshapes) blendshapes face tracking needs, groups missing names by face region, flags names that only differ in case, and copies the missing list to the clipboard
- `Patch Config Package Rules` available from `!Pawlygon/Tools/Patch Config Package Rules` — adds per-config package requirements to existing [PatcherHub](https://github.com/PawlygonStudio/PatcherHub) `FTPatchConfig` assets. Auto-lists every config in the project for multi-select, auto-fills a rule from your installed packages (common avatar packages listed first) with pre-written missing/outdated messages, and supports full add/edit/reorder/remove plus batch-applying a rule to many configs at once
- `Eye Muscle Settings` available from `!Pawlygon/Tools/Eye Muscle Settings` — reads and adjusts the humanoid eye muscle limit settings (In, Out, Up, Down) on an avatar's ModelImporter for face tracking compatibility. Provides synced or split left/right sliders, live scene preview with bone rotation and blendshape activation, and writes changes back to the ModelImporter after a confirmation, with a one-click revert to the previous limits
- `Face Tracking Extras` available from `!Pawlygon/Tools/Face Tracking Extras` — generates extra face tracking driven animations (ears, tail and pupils) from a handful of poses. Detects the ear and tail bone chains (skipping constraint helper bones), lets you pose Look Right/Up/Down, Sad, Happy, Happy Flick, Tail Right and Tail Happy on the avatar in the scene with live ear mirroring (Look Left and Tail Left are mirrored automatically), and previews how they blend, including the happy ear flick and tail wag loops. Generates baked clips, a 4-layer FX controller (each ear follows its own eye, mood from the average smile, a "Tail follows Jaw" toggle, and an improved fake pupil dilation that steps aside when real dilation is available), and a `!Pawlygon - Face Tracking Extras` VRCFury prefab in `Prefabs/FaceTrackingExtras`. The Custom tab adds your own animation clips: make one follow a face tracking parameter (fade in, scrub through the clip, or a different clip on each side), or play it when conditions are met, for example an eye shine when both brows stay above 0.75 for one second. Triggered animations can play once, loop or hold while active, and each one can get an optional menu toggle and be previewed on the avatar with parameter sliders. A finished avatar can be saved as a **baseline** (settings, custom animations and poses) that new avatars start from; poses carry over to any avatar whose ear and tail chains have the same bone counts

## Wizard workflow

The current workflow is built around a six-step editor wizard. Any step you have reached can be reopened from the step bar without losing your choices, and running Setup on working folders that already exist offers to **Resume** where you left off:

1. `Setup` - choose source FBX/prefab assets, configure output folders, and create the working structure
2. `Import Modified FBX` - replace the copied FBX with your edited version and wait for Unity to reimport it
3. `Select Replacements` - review detected skinned mesh matches plus the primary humanoid Animator rig and choose which replacements to apply
4. `Prefabs` - optionally add [Pawlygon VRCFT](https://github.com/PawlygonStudio/VRC-Facetracking) setup or import the latest [PatcherHub](https://github.com/PawlygonStudio/PatcherHub) unitypackage
5. `FX Check` - analyze each avatar's FX controller for gesture-driven facial expressions and eye-blink layers, then apply `FacialExpressionsDisabled` and `EyeTrackingActive` guards
6. `Finish` - review the outcome for each avatar, open the working scene or folder, and jump to the next tools

During setup, the wizard creates a working structure like this:

```text
Assets/<MainFolder>/<AvatarName>/
  FBX/
  Prefabs/
  Internal/
    Scenes/
```

In shared-folder mode, multiple avatar entries can be placed into the same `FBX/`, `Prefabs/`, and `Internal/Scenes/` structure.

## Diff generation

The package includes `FTDiffGenerator`, an editor asset that compares the original FBX against the modified FBX and writes patch files into:

```text
patcher/data/DiffFiles/
```

The generator creates:

- one `.hdiff` file for the FBX itself
- one `.hdiff` file for the FBX `.meta`

The wizard creates these generator assets for you automatically as part of the avatar setup flow.

## Requirements

- Unity 2022.3 or later

## Credits

- [**Hash's EditDistributionTools**](https://github.com/HashEdits/EditDistributionTools) — Inspiration for distribution workflows using binary patching
- [**hpatchz**](https://github.com/sisong/HDiffPatch) — High-performance binary diff/patch library by housisong
- **tkya** — Countless hours of technical support to the community
- **VRChat Community** — Feedback, testing, and feature requests

*Thank you to everyone who helped make Pawlygon Unity Tools possible!*

## License

This project is licensed under [CC BY-NC-SA 4.0](LICENSE.md).

HDiffPatch (`Packages/net.pawlygon.unitytools/hdiff/hdiffz/`) is distributed under the [MIT License](Packages/net.pawlygon.unitytools/hdiff/hdiffz/License.txt).

## Links

- [Website](https://www.pawlygon.net)
- [Discord](https://discord.com/invite/pZew3JGpjb)
- [YouTube](https://www.youtube.com/@Pawlygon)
- [X (Twitter)](https://x.com/Pawlygon_studio)

---

*Made with ❤ by Pawlygon Studio*