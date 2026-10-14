# Emberlight

**Emberlight is a Windows port of [Phoenix Slides](https://github.com/gobbledegook/creevey) by Dominic Yu.** It is a fast
folder-based image browser with thumbnails, EXIF-aware sorting and slideshows, written as a fully managed C# / WPF
application, plus a few additions of its own. It is not affiliated with or endorsed by the original author.
See `LICENSE-NOTICE.md` (CC BY-NC-SA 2.0) before redistributing.

> Emberlight was developed under the working name "Phoenix Slides for Windows". On first run it copies any settings
> from `%APPDATA%\PhoenixSlides` to `%APPDATA%\Emberlight` (the old file is left in place).

## Layout

| Project | What it is |
|---|---|
| `src/Emberlight.Core` | Platform-neutral logic, **no dependencies** (net10.0). Everything with real decisions in it. |
| `src/Emberlight.App` | The WPF application (net10.0-windows): windows, rendering, input. Thin glue over Core. |
| `tests/Emberlight.Tests` | Dependency-free test runner for Core (137 tests). |

## Build, test, run (Windows, .NET 10 SDK)

    dotnet run --project tests/Emberlight.Tests        # core tests; exit code = number of failures
    dotnet run --project src/Emberlight.App            # the app
    dotnet run --project src/Emberlight.App -- C:\Pictures\photo.jpg   # open straight into a slideshow

The core tests also run on Linux/macOS.

## Mapping from the original

| Original (Objective-C) | Port |
|---|---|
| `DYRandomizableArray` | `Core/RandomizableList` |
| `NSMutableArray+DYMovable` | `Core/ListExtensions` |
| `NSIndexSetSymDiffExtension` | not needed (`SortedSet<T>.SymmetricExceptWith`) |
| `CreeveyMainWindowController` comparators | `Core/FileComparers`, `SortOrder`, `NaturalStringComparer` |
| exiftags, `DYExiftags` | `Core/ExifReader` (JPEG/TIFF/PNG; in-place orientation edit; embedded thumbnails) |
| `SlideshowWindow` navigation | `Core/SlideshowState`; UI in `App/SlideshowWindow` |
| `DYImageView` zoom/layout | `Core/ImageViewport`; UI in `App/Controls/ImageViewerControl` |
| `DYWrappingMatrix` | `App/MainWindow` thumbnail `ListBox` with `App/Controls/VirtualizingGridPanel`; geometry in `Core/ThumbnailGridMath` |
| `DYImageCache` | `Core/LruCache` + `App/Services/ImageService`, `ThumbnailLoader` (WIC) |
| `DYFileWatcher` / `VDKQueue` | `Core/FolderWatcher` |
| `UKPrefsPanel`, NSUserDefaults | `App/PreferencesWindow`, `Core/AppSettings` (JSON in `%APPDATA%\Emberlight`) |
| `DirBrowserDelegate` | folder `TreeView` in `App/MainWindow` |
| `DYCarbonGoodies` (file-type helpers) | `Core/ImageFileTypes` |

## Additions beyond the original

* **Status-bar controls** - next to the thumbnail-size slider: a **Slideshow** button (same as Enter), **Include
  subfolders** and **Random** checkboxes (Random is the same setting as Slideshow > Random Order).
* **Click a folder to expand it** in the folder tree (the arrow still toggles; arrow-key navigation doesn't auto-expand).
* **Random order on/off during a slideshow** - press `S`. Turning it off carries on in sort order from the slide on
  screen; turning it on keeps that slide and reshuffles the rest. A short on-screen message confirms the change. It
  affects only the running slideshow, not the saved setting.
* **Quit a slideshow** with `Q`, `Esc` or `.`.
* **Mouse wheel in the slideshow** - wheel up for the previous slide, down for the next (one slide per notch; touchpad
  scrolling adds up to a notch). `Ctrl`+wheel still zooms, and a zoomed-in picture still pans with the wheel.
* **Next / previous folder in the slideshow** - `Ctrl+Right` / `Ctrl+Left` jump to the next or previous folder at the
  same level of the tree (siblings, in the folder tree's natural name order). Folders without images are skipped,
  there is no wrap-around (a beep and a short message at the ends), and a shuffled slideshow stays shuffled. The folder
  is the folder the slideshow was started from (after a jump, the folder jumped to) for as long as the slide on screen
  is inside it, otherwise the slide's own folder. So starting in an empty root folder with "Include subfolders" on
  moves on to the root's next sibling rather than between the root's subfolders. "Include subfolders" carries over.
  The browser window quietly follows to the new folder, so closing the slideshow leaves it showing, and selecting,
  what you were just viewing. Logic: `Core/FolderNavigator`.

## Image formats

Whatever Windows decodes natively: JPEG, PNG, TIFF, BMP and GIF. Animated GIFs play in the slideshow (and an
auto-advancing slideshow lets each one play through at least once); browser thumbnails show the first frame. Both can
be switched off under Preferences > Slideshow. HEIC,
WebP, RAW and JPEG 2000 are **not** supported. `ImageFileTypes.Register(".jp2")` plus a decoder in `ImageService` is the
extension point for adding more.

## Packaging a Windows executable

Run from the repository root on Windows with the .NET 10 SDK:

    powershell -ExecutionPolicy Bypass -File scripts\publish.ps1

| Output | Needs on the PC | Size |
|---|---|---|
| `artifacts\selfcontained\Emberlight.exe` | nothing (64-bit Windows 10/11) | ~70-150 MB |
| `artifacts\small\Emberlight.exe` | the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) | a few MB |

**Installer (optional).** Install [Inno Setup](https://jrsoftware.org/isinfo.php), run `publish.ps1`, then compile
`installer\Emberlight.iss`. It produces `artifacts\Emberlight-Setup-<version>.exe`: a per-user install (no admin
rights), Start Menu and optional desktop shortcuts, and an optional "Open with" entry for JPEG/PNG/TIFF/BMP/GIF. It
does not change your default image viewer.

**GitHub release.** Push a tag such as `v0.2.0`; `.github/workflows/release.yml` runs the tests, publishes the
self-contained build and attaches it to a release as a zip.

**Icon and version.** The icon is `src/Emberlight.App/Assets/app.ico` (a generated placeholder; replace it with any
multi-size .ico). It is used for the .exe, the taskbar and all windows. The version lives in
`src/Emberlight.App/Emberlight.App.csproj` and `installer/Emberlight.iss` - update both when releasing.

Notes: the build is unsigned by default; see "Windows security prompts" below. Trimming is not used because WPF
doesn't support it. Settings are stored in `%APPDATA%\Emberlight` and survive
upgrades and uninstalls.

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

* **Core: tested.** 137 tests, including a fuzz test of the shuffle bookkeeping against a simple model,
  pixel-level checks of all eight EXIF orientations against the EXIF spec, parser robustness tests on
  truncated/corrupted data, and real file-system tests for sorting, scanning and watching.
* **App: compiled, never run.** The WPF layer was developed on Linux, where WPF cannot run. It was verified by
  (1) compiling every source file against WPF reference assemblies built from the `dotnet/wpf` repository's
  reference sources (0 errors) and (2) checking every XAML element, property, event and attached-property name
  against that same API metadata (0 problems). XAML binding expressions, layout, visuals and runtime
  behaviour have **not** been exercised. Expect to find and fix bugs on first run.

## Not ported

Lossless pixel rotation (jpegtran), EXIF-thumbnail delete/replace, RAW (dcraw), the update checker, F1-F12 slide
categories, Finder-alias resolution, localization (6 languages), the app icon and Retina-specific code.

## Known limitations

* The thumbnail grid is virtualized (`VirtualizingGridPanel`): only the rows on screen have containers, and items far
  from the viewport release their bitmaps. The panel's geometry is unit tested; the panel itself (WPF container
  generation, scrolling, keyboard navigation) was compiled but not run during development.
* Slideshow shortcuts assume a US keyboard layout.
* "Rotate" in the browser edits the EXIF orientation tag (lossless, instant) and only works on files that already
  have that tag.
