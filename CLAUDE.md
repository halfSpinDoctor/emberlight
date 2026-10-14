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
- Core: ~153 tests, fuzz tests, and mutation checks (deliberately break the code, confirm tests fail).
- App: originally written on Linux where WPF cannot run, so it was only compile-checked against WPF API
  definitions. Since then the user has run it on Windows: it launches and works, large folders load well, and
  the features below were exercised. Treat anything not listed as untested until run.
- `VirtualizingGridPanel` (custom WPF panel) was written to WPF's real VirtualizingPanel contract; no problems
  reported so far, but it is the most intricate piece of UI code.

## Behaviour decisions made with the user (keep these)
- **Image formats:** whatever WIC decodes natively (JPEG, PNG, TIFF, BMP, static GIF). HEIC, WebP, animated GIF,
  RAW and JPEG 2000 are intentionally out of scope for now.
- **Include subfolders** (checkbox in the status bar + View menu): by default stays on while browsing inside the folder
  tree where it was switched on and switches off when you pick a folder outside that tree. Preferences > View lets the
  user choose "Always" instead (`AppSettings.IncludeSubfoldersMode`, `PathUtil.SubfoldersStayOn`); this choice is
  deliberately not in the View menu. During slideshow folder jumps it
  stays on, and the browser's tree root moves to the folder jumped to.
- **Preferences** are saved when OK is clicked (plus session state - last folder, window size, sort - at exit).
  The Preferences View tab mirrors the View menu's settings (sort, filenames, subfolders, info panel, More EXIF
  details, which is now persisted) and the Slideshow tab mirrors the Slideshow menu.
- **Loading feedback:** status-bar progress bar and text while scanning/sorting/loading a folder.
- **Slideshow Ctrl+Right / Ctrl+Left:** jump to the next/previous *sibling* folder in natural name order. Skips
  folders with no images, stops at the ends (beep + message, no wrap-around), keeps random mode (new folder starts
  on its first image, then shuffled). The reference folder is the folder the slideshow was started from (or last
  jumped to) while the current slide is inside it, otherwise the slide's own folder. The browser follows in the
  background (debounced 400 ms) and selects the last-viewed image when the slideshow closes.
- **Status bar (right side, in this order):** Slideshow button, Include subfolders, Random, thumbnail-size slider. The Random
  checkbox and Slideshow > Random Order are the same setting (`AppSettings.SlideshowRandom`), kept in sync by
  `SyncMenuState`. The Slideshow button is disabled while the grid is empty. Browser shortcuts: `Ctrl+F` toggles
  Include subfolders, `Ctrl+S` toggles Random (handled in `OnPreviewKeyDown`).
- **Network shares:** a UNC share without a drive letter (`\\server\share`) gets a top-level tree entry, labelled
  `share (\\server)`, the first time a folder on it is opened (`SelectPathInTree`, `PathUtil.NetworkShareLabel`).
  For this session only; there's no browsing of a server's shares (that needs native calls). Tree folders read their
  subfolders on a worker thread (`EnsureChildrenLoadedAsync`); `SelectPathInTree` walks down level by level and stops
  if the user picks another folder meanwhile (`_treeSelectVersion`).
  The top level is built off the UI thread too (`BuildTreeRootsAsync`): each drive's readiness is checked separately
  and the drive slotted into letter order when it answers, so an offline mapped drive can't stall startup. File > Open
  and drag-and-drop still check that the folder exists on the UI thread (the user chose to leave those).
- **Folder tree:** clicking a folder's name expands it (`OnTreePreviewMouseDown`), and clicking another folder collapses
  the folders earlier clicks opened, except the new folder's parents (`ClickExpandTracker`). Folders opened with the
  arrow or keyboard are never auto-collapsed (using a folder's arrow stops tracking it). Folders the app opens itself
  (startup folder, File > Open, drag and drop, slideshow follow) count as clicked at every level (`OpenAsIfClicked`). Double-click doesn't collapse;
  keyboard navigation neither auto-expands nor auto-collapses.
- **Slideshow `S`:** toggles random for the *running* slideshow only (does not change the saved setting). Off = continue in
  sort order from the current slide; on = current slide first, rest reshuffled. Shows a toast. Quit keys: `Q`, `Esc`, `.`.
- **Slideshow mouse wheel:** up = previous slide, down = next, one slide per notch (`WheelStepper` adds up touchpad
  steps). Ctrl+wheel zooms, and a zoomed-in picture pans with the wheel instead (the viewer handles those first).
- **"Open with" / files on the command line:** start a slideshow (if the preference is on) with the browser opened
  behind it at that folder (`MainWindow.ShowSlideshow`), so quitting the slideshow returns to the browser with the
  last-viewed image selected instead of exiting the app.
- **Theme:** WPF's Fluent theme (`ThemeMode="System"` in `App.xaml`) follows the Windows light/dark setting and accent
  colour. The API is still experimental in .NET 10 (warning WPF0001, suppressed in the App `.csproj`). Windows use the
  solid `ApplicationBackgroundBrush` rather than Fluent's translucent Mica (wallpaper tint would skew photos). The
  slideshow window stays black by design.
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
- The thumbnail loading placeholder is the app icon with square corners (`ImageService.MakeIconPlaceholder`): it paints
  the icon's gradient (colours sampled from `app.ico`) and draws the icon clipped inside its rounded rim. If the icon
  artwork changes, re-sample those colours; if `app.ico` loses its 256 px frame, the old grey placeholder is used.
- Fluent theme: a custom `Style` must use `BasedOn="{StaticResource {x:Type ...}}"`, or it silently falls back to the
  classic look. Don't hard-code UI colours; use Fluent brushes such as `TextFillColorSecondaryBrush` or
  `DividerStrokeColorDefaultBrush` so both light and dark work. Fluent list items have a minimum height and padding:
  `ThumbContainerStyle` resets them, because the grid maths needs exact cell sizes. The status bar has compact styles
  in `StatusBar.Resources` (no 120 px checkbox minimum width or 32 px minimum heights; slider 30 px tall, since any
  shorter clips the bottom of its thumb, and lifted 1 px so the track lines up with the labels); it is 36 px tall instead
  of Fluent's 66. If the slider height changes, re-measure the lift: it depends on the height.
  The folder tree is compact too (`TreeView.Resources`): 12 px text and 22 px rows instead of 14 px and ~31 px.
  Menus likewise: 12 px text, a 30 px menu bar and 24 px items instead of 49 and 36. Fluent's menu templates hard-code
  their margins (padding only adds to them), so `OnMenuItemLoaded` shrinks the named template parts (`Border`, its
  content grid, `CheckBoxIconBorder`) when each item loads; it does nothing if a future template renames them.
  It also fixes the check box column at 22 px in every drop-down (Fluent sizes it per menu, to zero without toggles),
  indents submenu headers (Sort By) to match, and shows the Sort By direction arrow in that column: the arrow is the
  sort item's `Icon` (set in `SyncMenuState`), bound into the template's `CheckBoxIcon`.
  Preferences uses 12 px text too, with slimmer fields and 13 px group headings (styles in `PreferencesWindow.xaml`'s
  resources), but check box and radio rows keep Fluent's 32 px height: the user found tighter rows too cramped. The
  window is 520 x 540 (was 560 x 640), sized to the tallest tab (Slideshow); if a tab gains rows, check it still fits.
- `TreeViewItem.BringIntoView()` right after adding rows does nothing useful (they aren't laid out yet); defer it with
  `Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ...)`, as `SelectPathInTreeAsync` does.

## Not ported / ideas
Lossless pixel rotation (jpegtran), RAW (dcraw), update checker, F1-F12 slide categories, Finder alias
resolution, localization (original has 6 languages), app icon, JPEG 2000 (WIC has no built-in decoder; would need a
managed decoder such as CSJ2K via NuGet). Possible extra: a separate key for stepping through subfolders inside the
starting folder when "include subfolders" is on.
