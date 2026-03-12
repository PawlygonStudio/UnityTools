# Pawlygon Unity Tools

Unity Editor tools for Pawlygon's avatar face-tracking workflow.

This package helps you duplicate source avatar assets, prepare a working folder structure, swap updated meshes from a modified FBX back onto a prefab, and generate `.hdiff` patch files for distribution.


## Main features

- Guided `Avatar Setup Wizard` available from `!Pawlygon/Avatar Setup Wizard`
- Batch setup for one or many avatar entries in a single run
- Shared-folder or separate-folder output layouts depending on your project needs
- Automatic creation of working folders, copied FBX assets, copied prefabs, and working scenes
- Reimport detection for modified FBX files
- Mesh review UI for matching FBX skinned meshes to prefab skinned meshes before applying replacements
- Automatic creation of `FTDiffGenerator` assets for patch generation
- `.hdiff` generation for both FBX and `.meta` changes using bundled `hdiffz` binaries
- Optional prefab helpers for [Pawlygon VRCFT](https://github.com/PawlygonStudio/VRC-Facetracking) setup and importing the latest [PatcherHub](https://github.com/PawlygonStudio/PatcherHub) package

## Wizard workflow

The current workflow is built around a five-step editor wizard:

1. `Setup` - choose source FBX/prefab assets, configure output folders, and create the working structure
2. `Import Modified FBX` - replace the copied FBX with your edited version and wait for Unity to reimport it
3. `Select Meshes` - review detected skinned mesh matches and choose which replacements to apply
4. `Prefabs` - optionally add [Pawlygon VRCFT](https://github.com/PawlygonStudio/VRC-Facetracking) setup or import the latest [PatcherHub](https://github.com/PawlygonStudio/PatcherHub) unitypackage
5. `Finish` - review the generated paths and completed output

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

HDiffPatch (`hdiff/hpatchz/`) is distributed under the [MIT License](hdiff/hpatchz/License.txt).

## Links

- [Website](https://www.pawlygon.net)
- [Discord](https://discord.com/invite/pZew3JGpjb)
- [YouTube](https://www.youtube.com/@Pawlygon)
- [X (Twitter)](https://x.com/Pawlygon_studio)

---

*Made with ❤ by Pawlygon Studio*


