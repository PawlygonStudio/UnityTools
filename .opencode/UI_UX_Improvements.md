# Unity UI/UX Improvements Log

## Overview

This document tracks the current UI and workflow improvements in Pawlygon Unity Tools. The focus is on making the editor flow easier to understand during multi-avatar setup, FBX reimport tracking, mesh review, and patch generation.

The tools continue to use Unity IMGUI, but the interaction model now behaves more like a guided production workflow than a basic utility window.

---

## 1. AvatarSetupWizard improvements

The `AvatarSetupWizard` has evolved into a multi-step workflow with clear progression and stronger batch-processing support.

### Visual structure

- Added a branded header and step indicator so users can immediately see where they are in the process
- Wrapped major areas in boxed sections to separate setup, review, and optional helper actions
- Styled primary actions so the main progression buttons stand out from secondary actions

### Setup experience

- Supports multiple avatar entries in one session instead of forcing a single-avatar flow
- Lets users switch between shared-folder and separate-folder output modes
- Uses inline validation so missing or invalid FBX/prefab inputs are shown before the user starts the process
- Surfaces created paths and generated assets as part of the later review screens

### Import tracking experience

- Replaced a blind wait state with a per-entry import progress summary
- Shows which copied FBXs have updated and which are still waiting on reimport
- Supports both automatic transition after all imports are ready and a manual continue/skip path when the user needs it

### Mesh review experience

- Review is scoped to one avatar entry at a time, which keeps larger batch jobs understandable
- Entry selection uses a toolbar so users can move between avatars quickly
- Added bulk selection actions for mesh mappings
- Matched and unmatched mesh rows show clearer status feedback so missing mappings are easier to spot
- Users can explicitly apply mesh replacements or skip an avatar entry without leaving the wizard in an ambiguous state

### Prefab helper experience

- Added a dedicated `Prefabs` step instead of mixing optional post-processing into the main mesh workflow
- Separates `Pawlygon VRCFT` setup from `PatcherHub` import so both actions are discoverable and independently optional
- Displays contextual status messages after helper actions complete or fail

### Completion experience

- The finish screen summarizes output for every processed avatar entry
- Users can review generated paths and restart the workflow from the same window

---

## 2. FTDiffGenerator improvements

`FTDiffGenerator` is no longer hidden behind a context-only workflow. A custom inspector now presents the generator as a clear editor tool.

### Inspector UX

- Added a visible `Generate Diff Files` button to the inspector
- Grouped the references and output settings into clearer sections
- Updated validation around the current data model, which now uses FBX references rather than prefab references
- Disables the main action until both FBX references and the output directory are valid

### Workflow alignment

- The wizard creates diff generator assets automatically, so the inspector now acts as both a fallback and a direct manual tool
- The inspector language matches the actual patch workflow: compare original FBX against modified FBX and write `.hdiff` output

---

## Technical notes

- No external editor UI framework is required
- Styling is built with `GUIStyle`, `EditorStyles`, icon content, and theme-aware colors
- Layout choices favor readability during longer batch sessions, especially in import review and mesh selection
