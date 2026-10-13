# Emberlight - project notes for Claude Code

**Emberlight** is a Windows-only, fully managed C# (.NET 10, WPF) port of **Phoenix Slides** (macOS Objective-C image
browser + slideshow, https://github.com/gobbledegook/creevey by Dominic Yu), plus a few features beyond the original.
See `README.md` for the mapping from original classes to this code.

Naming (decided with the user): the app was developed as "Phoenix Slides for Windows" and renamed **Emberlight** so
it doesn't reuse the original author's name. The attribution "Emberlight is a Windows port of Phoenix Slides by
Dominic Yu" must stay prominent (README first lines, About box, installer comments, `LICENSE-NOTICE.md`); the license
requires credit. The user rejected "Phoenix Photos" (too close to the original, and "Phoenix" is overloaded). The icon is
kept as designed. `SettingsStore` migrates settings once from the old `%APPDATA%\PhoenixSlides` folder.

## License - read before suggesting anything about distribution
The original is **CC BY-NC-SA 2.0**; this port is an adaptation. Keep `LICENSE-NOTICE.md`, keep attribution,
non-commercial only, share-alike. Do not copy code from the original repo's bundled third-party libraries
(libjpeg, exiftags, dcraw). The user wants a fully managed port: **no native code, no C library wrappers**.

## Commands
    dotnet build Emberlight.sln
    dotnet run --project tests/Emberlight.Tests          # core tests; exit code = number of failures
    dotnet run --project src/Emberlight.App              # the WPF app (Windows only)
Target framework is **net10.0 / net10.0-windows** (C# 14; migrated from .NET 8 on 2026-10-13 with SDK 10.0.401).
.NET 10 is an LTS release (supported until November 2028). Core and the tests build and run on Linux/macOS too; the App needs Windows.

## Packaging
`scripts/publish.ps1` (self-contained + framework-dependent single-file x64 builds), `installer/Emberlight.iss`
(Inno Setup, per-user install, optional "Open with" entries), `.github/workflows/release.yml` (tag `v*` -> GitHub
release). Icon: `src/Emberlight.App/Assets/app.ico` (placeholder generated with Pillow). Version is set in two
places: the App `.csproj` and the `.iss`. Builds are intentionally **unsigned** (user won't pay for a certificate; the
user turned off Windows 11 Smart App Control to run it). `publish.ps1` has an optional signing step (store cert, .pfx or
Azure Artifact Signing) that is inert unless a signing option is given; see README "Windows security prompts". The packaging files were written without a Windows machine; the user has since built and run an exe from them
(it launched but was blocked by Smart App Control, so the build itself works). The installer script and the release
workflow have not been confirmed on Windows - verify them on first use. WPF can't be trimmed; don't enable PublishTrimmed.

## Architecture rule: decisions live in Core, UI stays thin
`src/Emberlight.Core` (no dependencies, unit tested) holds every piece of real logic. `src/Emberlight.App`
(WPF) only renders, routes input and calls Core. When adding a feature, write the logic in Core with tests first and
keep the WPF side to glue. Notable Core pieces: `RandomizableList`, `SlideshowState` (navigation), `ImageViewport`
(zoom/pan/fit), `ImageOrientation` (EXIF 1-8 as flip+rotate), `ExifReader` (own TIFF/EXIF parser; in-place
orientation rewrite), `FileComparers`/`SortOrder`, `DirectoryScanner`, `FolderWatcher`, `FolderNavigator`
(next/previous sibling folder), `ThumbnailGridMath` (virtualized-grid geometry), `PathUtil`, `AppSettings`.

## Tests
Custom dependency-free runner in `tests/Emberlight.Tests` (helpers in `TestHarness.cs`; synthetic EXIF/JPEG/PNG
builder in `ExifTestBuilder.cs`). No NuGet packages are used anywhere; keep it that way unless there is a strong
reason (a JPEG 2000 decoder would be one). Register new test groups in `Program.cs`. `TESTS.md` (repo root)
describes every test in a line or two: update it whenever tests are added, renamed or removed. The `FolderWatcher` tests
are timing based and could be flaky on a loaded CI machine.

## Verification status (be honest about this)
- Core: ~137 tests, fuzz tests, and mutation checks (deliberately break the code, confirm tests fail).
- App: originally written on Linux where WPF cannot run, so it was only compile-checked against WPF API
  definitions. Since then the user has run it on Windows: it launches and works, large folders load well, and
  the features below were exercised. Treat anything not listed as untested until run.
- `VirtualizingGridPanel` (custom WPF panel) was written to WPF's real VirtualizingPanel contract; no problems
  reported so far, but it is the most intricate piece of UI code.

## Behaviour decisions made with the user (keep these)
- **Image formats:** whatever WIC decodes natively (JPEG, PNG, TIFF, BMP, static GIF). HEIC, WebP, animated GIF,
  RAW and JPEG 2000 are intentionally out of scope for now.
- **Include subfolders** (checkbox in the status bar + View menu): stays on while browsing inside the folder tree
  where it was switched on; switches off when you pick a folder outside that tree. During slideshow folder jumps it
  stays on, and the browser's tree root moves to the folder jumped to.
- **Preferences** are saved when OK is clicked (plus session state - last folder, window size, sort - at exit).
- **Loading feedback:** status-bar progress bar and text while scanning/sorting/loading a folder.
- **Slideshow Ctrl+Right / Ctrl+Left:** jump to the next/previous *sibling* folder in natural name order. Skips
  folders with no images, stops at the ends (beep + message, no wrap-around), keeps random mode (new folder starts
  on its first image, then shuffled). The reference folder is the folder the slideshow was started from (or last
  jumped to) while the current slide is inside it, otherwise the slide's own folder. The browser follows in the
  background (debounced 400 ms) and selects the last-viewed image when the slideshow closes.
- **Status bar (right side, in this order):** Slideshow button, Include subfolders, Random, thumbnail-size slider. The Random
  checkbox and Slideshow > Random Order are the same setting (`AppSettings.SlideshowRandom`), kept in sync by
  `SyncMenuState`. The Slideshow button is disabled while the grid is empty.
- **Folder tree:** clicking a folder's name expands it (`OnTreePreviewMouseDown`); the arrow toggles as normal; double-click
  doesn't collapse; keyboard navigation doesn't auto-expand.
- **Slideshow `S`:** toggles random for the *running* slideshow only (does not change the saved setting). Off = continue in
  sort order from the current slide; on = current slide first, rest reshuffled. Shows a toast. Quit keys: `Q`, `Esc`, `.`.
- "Rotate" in the browser edits the EXIF orientation tag only (lossless); there is no pixel rotation.

## Gotchas learned the hard way
- Name the sort-field enum `SortField` (a `SortKey` collides with `System.Globalization.SortKey`).
- A WPF binding to a *derived* property only updates if you raise `PropertyChanged` for it too
  (`ViewSettings.OuterCellWidth/Height` must be raised alongside `CellWidth`).
- Custom `VirtualizingPanel`: touch `InternalChildren` before using `ItemContainerGenerator`; `Panel` handles Reset
  but not Add/Remove/Replace/Move; use `protected override` for `BringIndexIntoView`.
- `FileSystemWatcher` doesn't reliably report the watched folder itself being deleted, hence `FolderWatcher` polls.
- With a non-virtualizing list, adding thousands of items freezes WPF (all containers are measured in one pass).
- Slideshow key bindings assume a US keyboard layout.

## Not ported / ideas
Lossless pixel rotation (jpegtran), RAW (dcraw), update checker, F1-F12 slide categories, Finder alias
resolution, localization (original has 6 languages), app icon, JPEG 2000 (WIC has no built-in decoder; would need a
managed decoder such as CSJ2K via NuGet). Possible extra: a separate key for stepping through subfolders inside the
starting folder when "include subfolders" is on.
