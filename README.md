# Emberlight

**Emberlight is a Windows port of [Phoenix Slides](https://github.com/gobbledegook/creevey) by Dominic Yu.** It is a fast
folder-based image browser with thumbnails, EXIF-aware sorting and slideshows, written as a fully managed C# / WPF
application, plus a few additions of its own. It is not affiliated with or endorsed by the original author.
See `LICENSE-NOTICE.md` (CC BY-NC-SA 2.0) before redistributing.

## Layout

| Project | What it is |
|---|---|
| `src/Emberlight.Core` | Platform-neutral logic, **no dependencies** (net10.0). Everything with real decisions in it. |
| `src/Emberlight.App` | The WPF application (net10.0-windows): windows, rendering, input. Thin glue over Core. |
| `tests/Emberlight.Tests` | Dependency-free test runner for Core (286 tests; each one is described in `TESTS.md`). |

## Build, test, run (Windows, .NET 10 SDK)

    dotnet build Emberlight.sln
    dotnet run --project tests/Emberlight.Tests        # core tests; exit code = number of failures
    dotnet run --project src/Emberlight.App            # the app
    dotnet run --project src/Emberlight.App -- C:\Pictures\photo.jpg   # open straight into a slideshow

Core and the tests also build and run on Linux and macOS; the app itself needs Windows.

## Mapping from the original

| Original (Objective-C) | Port |
|---|---|
| `DYRandomizableArray` | `Core/RandomizableList` |
| `NSMutableArray+DYMovable` | `Core/ListExtensions` |
| `NSIndexSetSymDiffExtension` | not needed (`SortedSet<T>.SymmetricExceptWith`) |
| `CreeveyMainWindowController` comparators | `Core/FileComparers`, `SortOrder`, `NaturalStringComparer` |
| exiftags, `DYExiftags` | `Core/ExifReader` (JPEG/TIFF/PNG; in-place orientation edit; embedded thumbnails) |
| `SlideshowWindow` navigation | `Core/SlideshowState`, `Core/SlideshowCommands`; UI in `App/SlideshowWindow` |
| `DYImageView` zoom/layout | `Core/ImageViewport`, `Core/ViewerInput`; UI in `App/Controls/ImageViewerControl` |
| `DYWrappingMatrix` | `App/MainWindow` thumbnail `ListBox` with `App/Controls/VirtualizingGridPanel`; geometry in `Core/ThumbnailGridMath`; sorted list in `Core/FileListing` |
| `DYImageCache` | `Core/LruCache`, `Core/DecodePolicy`, `Core/ThumbnailScheduler`, `Core/SlidePreload` + `App/Services/ImageService`, `ThumbnailLoader` (WIC) |
| `DYFileWatcher` / `VDKQueue` | `Core/FolderWatcher` |
| `UKPrefsPanel`, NSUserDefaults | `App/PreferencesWindow`, `Core/AppSettings` (JSON in `%APPDATA%\Emberlight`), `Core/PreferencesInput`, `Core/ColorText` |
| `DirBrowserDelegate` | folder `TreeView` in `App/MainWindow` |
| `DYCarbonGoodies` (file-type helpers) | `Core/ImageFileTypes` |

## Additions beyond the original

**Look and feel**

* **Light and dark mode** - the app follows the Windows light/dark setting and accent colour (WPF's Fluent theme),
  with compact menus, folder tree, status bar and Preferences. Windows use a solid, neutral background so photos
  aren't tinted.

**Browser**

* **Status-bar controls** - a **Slideshow** button (same as `Enter`), **Subfolders** and **Random** checkboxes, and
  the thumbnail-size slider. Random is the same setting as Slideshow > Random Order. Shortcuts: `Ctrl+F` toggles
  Subfolders, `Ctrl+S` toggles Random, and `Enter` starts a slideshow even when nothing is selected.
* **Include subfolders** - by default it switches itself off when you open a folder outside the tree where you
  switched it on (moving deeper keeps it on). Preferences > View can keep it on instead.
* **Folder tree** - clicking a folder's name expands it, and clicking elsewhere collapses what earlier clicks opened
  (folders opened with the arrow or the keyboard stay open). Folders are read in the background, so a slow disk or
  network share doesn't freeze the window, and a network share without a drive letter (`\\server\share`) gets its own
  entry when you open a folder on it.
* **Info panel** - its width can be dragged, like the folder tree's, and is remembered.
* **Preferences > View** - the View menu's settings (sort order, file names, subfolders, info panel) in one place.

**Slideshow**

* **Opening files from Explorer** ("Open with", or dropping files on the exe) starts a slideshow with the browser
  open behind it, so quitting the slideshow returns to the browser instead of closing the app.
* **Random order on/off during a slideshow** - press `S`. Turning it off carries on in sort order from the slide on
  screen; turning it on keeps that slide and reshuffles the rest. A short on-screen message confirms the change. It
  affects only the running slideshow, not the saved setting.
* **Quit a slideshow** with `Q`, `Esc` or `.`.
* **Mouse wheel** - wheel up for the previous slide, down for the next (one slide per notch; touchpad scrolling adds
  up to a notch). `Ctrl`+wheel still zooms, and a zoomed-in picture still pans with the wheel.
* **Next / previous folder** - `Ctrl+Right` / `Ctrl+Left` jump to the next or previous folder at the same level of
  the tree (siblings, in the folder tree's natural name order). Folders without images are skipped, there is no
  wrap-around (a beep and a short message at the ends), and a shuffled slideshow stays shuffled. The folder is the
  one the slideshow was started from (after a jump, the folder jumped to) for as long as the slide on screen is inside
  it, otherwise the slide's own folder. So starting in an empty root folder with "Include subfolders" on moves on to
  the root's next sibling rather than between the root's subfolders. "Include subfolders" carries over. The browser
  quietly follows to the new folder, so closing the slideshow leaves it showing, and selecting, what you were just
  viewing. Logic: `Core/FolderNavigator`.
* **Animated GIFs** - see below.

## Image formats

Whatever Windows decodes out of the box: JPEG, PNG, TIFF, BMP and GIF. HEIC, WebP, RAW and JPEG 2000 are **not**
supported; registering an extension with `ImageFileTypes.Register` and adding a decoder to `ImageService` is the way
to add one.

**Animated GIFs** play in the slideshow (browser thumbnails show the first frame). When the slideshow advances
automatically, it waits for each animated GIF to play through at least once. Both behaviours can be switched off
under Preferences > Slideshow.

## Packaging a Windows executable

Run from the repository root on Windows with the .NET 10 SDK:

    powershell -ExecutionPolicy Bypass -File scripts\publish.ps1

| Output | Needs on the PC | Size |
|---|---|---|
| `artifacts\selfcontained\Emberlight.exe` | nothing (64-bit Windows 10/11) | about 60 MB |
| `artifacts\small\Emberlight.exe` | the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) | under 1 MB |

**Installer (optional).** Install [Inno Setup](https://jrsoftware.org/isinfo.php), run `publish.ps1`, then compile
`installer\Emberlight.iss`. It produces `artifacts\Emberlight-Setup-<version>.exe`: a per-user install (no admin
rights), Start Menu and optional desktop shortcuts, and an optional "Open with" entry for JPEG/PNG/TIFF/BMP/GIF. It
does not change your default image viewer. The installer script has not been tried on Windows yet.

**GitHub release.** Push a tag such as `v0.3.0`; `.github/workflows/release.yml` runs the tests, publishes the
self-contained build and attaches it to a release as a zip, with release notes generated from the commits.

**Icon and version.** The icon is `src/Emberlight.App/Assets/app.ico` (16 to 256 px). It is used for the .exe, the
taskbar, the windows, the About box and the thumbnail loading placeholder. The version lives in
`src/Emberlight.App/Emberlight.App.csproj` and `installer/Emberlight.iss` - update both when releasing.

Notes: the build is unsigned by default; see "Windows security prompts" below. Trimming is not used because WPF
doesn't support it. Settings are stored in `%APPDATA%\Emberlight` and survive upgrades and uninstalls.

## Windows security prompts (SmartScreen and Smart App Control)

The builds are **unsigned** (signing needs a paid certificate), so Windows may object. *(Information as of October
2026; Microsoft's rules and prices change, so check current documentation.)*

**SmartScreen** ("Windows protected your PC") - click **More info > Run anyway**.

**Smart App Control** (Windows 11, when set to *On*) is stricter: it refuses to run the app ("An Application Control
policy has blocked this file") and has **no per-app "Run anyway"**. It only allows code signed by a certificate from
a CA in Microsoft's trusted root program, so unsigned builds are blocked, including ones you compile yourself.
Unblocking the file, installers and self-signed certificates do not help. Your options:

1. **Turn Smart App Control off** (Settings > Privacy & security > Windows Security > App & browser control >
   Smart App Control settings). This is **one-way**: it can only be turned back on by resetting or reinstalling
   Windows. Free, and the simplest choice for a personal-use app.
2. **Sign the build** with a certificate Windows trusts. Pass signing options to `scripts\publish.ps1` (below).
   The cheapest route is Microsoft's *Azure Artifact Signing* (formerly Trusted Signing), about US$10/month, for
   organizations in the USA, Canada, EU and UK and individual developers in the USA and Canada; otherwise a standard
   (OV) certificate from a CA, roughly US$150-300/year. A freshly signed app can still be blocked for a while
   until it builds reputation.
3. **Run it where Smart App Control is off**, such as a virtual machine or another PC.

Free open-source signing programs such as SignPath Foundation require an OSI-approved open-source license, which this
project cannot offer (it is derived from CC BY-NC-SA code; see `LICENSE-NOTICE.md`).

### Signing with publish.ps1

Nothing is signed unless you pass exactly one of these (they need `signtool.exe` from the Windows SDK or Visual
Studio, and both executables are timestamped so the signature outlives the certificate):

    # a certificate installed in your certificate store
    scripts\publish.ps1 -CertThumbprint <thumbprint>

    # a .pfx file
    scripts\publish.ps1 -PfxPath C:\keys\mycert.pfx -PfxPassword (Read-Host -AsSecureString)

    # Azure Artifact Signing (client tools + a metadata.json for your account and certificate profile)
    scripts\publish.ps1 -ArtifactSigningDlib "C:\tools\Azure.CodeSigning.Dlib.dll" -ArtifactSigningMetadata .\metadata.json

After signing, the script runs `signtool verify /pa` and warns if Windows doesn't consider the signature trusted. If
you also build the installer, sign the installer too (Inno Setup's `SignTool` setting). The release workflow
(`.github/workflows/release.yml`) does not sign; adding it would mean storing certificate secrets in GitHub.
The argument handling in the script was exercised with stand-in tools; real signing has not been tried.

## Continuous integration

`.github/workflows/ci.yml` builds the whole solution (including the WPF app) on a Windows runner and runs the core
tests on every push and pull request.

## Verification status

* **Core: tested.** 286 tests (listed in `TESTS.md`), including a fuzz test of the shuffle bookkeeping against a
  simple model, pixel-level checks of all eight EXIF orientations against the EXIF spec, parser robustness tests on
  truncated or corrupted data, real file-system tests for sorting, scanning and watching, and animated GIF frame
  composition. Key behaviours are also mutation-checked (the code is deliberately broken to confirm a test fails).
* **App: used on Windows.** The WPF layer was first written on Linux and only compile-checked, but it has since been
  run and used on Windows 11, including large folders, network shares, slideshows and the features listed above.
  There are no automated UI tests, so anything not exercised by hand may still have rough edges.
* **Builds:** CI builds the whole solution and runs the tests on every push, and the release workflow has produced
  working releases. The installer script and code signing are untested.

## Not ported

Lossless pixel rotation (jpegtran), EXIF-thumbnail delete/replace, RAW (dcraw), the update checker, F1-F12 slide
categories, Finder-alias resolution, localization (6 languages), the original's app icon and Retina-specific code.

## Known limitations

* Slideshow shortcuts assume a US keyboard layout.
* The Fluent theme that gives Emberlight its light/dark mode is still marked experimental in .NET 10, so a future
  .NET release could change how the app looks.
* "Rotate" in the browser edits the EXIF orientation tag (lossless, instant) and only works on files that already
  have that tag.
