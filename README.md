# mgsv_modmgr

Mod manager for MGSV:TPP. Handles load order, rebuilds the
affected `.fpk(d)` archives through `datfpk.exe`, overlays loose `GameDir/`
files, and keeps `PathDictionary.txt` / `ExplicitPathDictionary.txt` in sync
next to the game exe.

Two front-ends share one core:

- `csgui/` — Avalonia GUI (current). Builds `modmgr_gui.exe`.
- `src/`   — original C++ CLI + Win32 GUI. Builds `modmgr.exe`.

## Build

GUI (distributable single-file exe):

    publish.bat

GUI (dev loop):

    dotnet build MgsvModMgr.slnx

Legacy C++ (needs MSVC + `vcvarsall.bat`):

    build.bat

## Runtime layout

The manager writes alongside its own exe — not in this repo:

    state.txt        game root, datfpk path, mod list (the load order)
    mods/            stored copy of every added .mgsv
    backups/         pristine baselines so Revert works
    tmp/             work tree for unpack/repack; safe to nuke

`datfpk.exe` is third-party and not bundled. Drop it somewhere and point
the manager at it via Settings.

## Notes

- `PathDictionary.txt` keys are stripped to the path before the first `.`
  in the final segment, because that's where `PathCode`'s base hash splits.
  Multi-dot names like `ih_general.fre.lng2` collapse to `ih_general`; one
  entry resolves every language variant.
- `ExplicitPathDictionary.txt` only gets rows for `QarEntry` elements that
  carry a `Hash=""` attribute. `FpkEntry` paths feed the path dict only.
- Adding/removing a mod refreshes both dicts automatically. The sidebar
  database button forces a full rebuild against every installed mod —
  useful when seeding a fresh install.
