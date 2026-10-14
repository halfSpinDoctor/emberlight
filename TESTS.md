# Emberlight tests

The test suite covers `src/Emberlight.Core` and lives in `tests/Emberlight.Tests`. It uses its own small runner
(no NuGet packages), and the exit code is the number of failed tests.

    dotnet run --project tests/Emberlight.Tests

- `TestHarness.cs` holds the runner and assertion helpers (`T.Group`, `T.Run`, `T.Eq`, `T.SeqEq`, `T.Throws`)
  and `TempDir`, a throwaway folder that is deleted after each test.
- `ExifTestBuilder.cs` builds synthetic TIFF/EXIF blocks and wraps them in JPEG or PNG files, so the EXIF tests need
  no sample photos.
- `Program.cs` runs the groups in the order listed below. Register any new test class there.

Tests whose names start with "fuzz" or say "brute-force" run many seeded random cases, so they are repeatable.
The `FolderWatcher` tests rely on timing and could be flaky on a heavily loaded machine.

---

## CoreTests.cs

### RandomizableList
The list behind random-order slideshows. It holds a shuffled order and remembers the original (sorted) order.

- **randomize keeps the same items and remembers original order**: Shuffling keeps every item, actually changes the order,
  and un-shuffling restores the original order exactly.
- **randomize(start) puts the chosen item first**: Shuffling with a chosen start item always puts that item first
  (checked over 20 random seeds).
- **randomizing again reshuffles but keeps the original order**: A second shuffle gives a new order, and the original
  order can still be restored.
- **edge cases: empty and single-item lists**: Empty and one-item lists can be shuffled and emptied without errors,
  and an out-of-range start index throws.
- **ordered next/previous walk the original sequence**: From a shuffled list, "next/previous in order" steps through
  the items in their original sorted sequence.
- **not randomized: ordered navigation is plain adjacency**: Without a shuffle, "next/previous in order" just means
  the neighbouring index.
- **binary search finds items in randomized lists**: Sorted-order searches still find items after shuffling, and
  report where a missing item would be inserted.
- **insert places duplicates at the existing index and does not grow the list**: Inserting an item that is already in
  the list returns its existing position and adds nothing.
- **fuzz: random inserts/removes/shuffles keep every invariant**: 60 seeds × 120 random operations, checking contents,
  ordered indexes, search and next/previous against a simple model after every step.
- **ChangeBase rewrites paths in both orders**: Renaming a parent folder rewrites every file path in both the
  shuffled and the original order.

### List extensions
- **MoveItem**: Moving an item to another index shifts the other items correctly.
- **UpdateIndexOfItem re-sorts one item whose key changed**: When one item's sort value changes, only that item moves
  to its new sorted position. A missing item returns -1.
- **ChangeBase on plain lists leaves non-matching paths alone**: Rebasing paths in a plain list leaves paths on other
  drives or folders alone and ignores letter case.

### NaturalStringComparer
The "img2 before img10" ordering used for file and folder names.

- **numbers compare by value**: Runs of digits compare as numbers, so `img2` sorts before `img10`.
- **case-insensitive**: Upper- and lowercase letters sort together.
- **leading zeros: same value stays deterministic**: `a01` and `a1` have the same number but still get a fixed,
  consistent order instead of comparing as equal.
- **prefix and digits-before-letters**: Shorter prefixes come first, digits sort before letters, null sorts first,
  and identical strings are equal.
- **very long digit runs do not overflow**: Numbers too large for any integer type (60 digits) still compare
  correctly.

### FileSizeFormatter
- **formats like the original**: Formats byte counts as "bytes", "KB", "MB" and "GB" exactly as Phoenix Slides did.
- **terabytes are scaled correctly (original mislabelled these)**: Terabyte values are scaled correctly, fixing a
  labelling bug in the original.

### BuildVersion
Splits the version string the .NET SDK stamps into the app ("0.1.0+<commit hash>") for Help > About.

- **splits the SDK's version+commit string**: Gives the version, the full lowercase hash, a 7-character short hash,
  and "0.1.0 (commit e707b9e)" as the display text.
- **plain version has no commit**: A version without `+` metadata (e.g. built outside git) shows just the version.
- **commit is the last part of multi-part build metadata**: With `+ci.42.<hash>`, the hash is still found.
- **non-hex or empty metadata and missing input are handled**: Metadata that isn't a hash gives no commit; a null,
  blank or version-less string shows "unknown".

### ImageOrientation
EXIF orientation values 1-8, modelled as a flip plus a rotation.

- **all eight EXIF values place the image corners where the spec says**: For each EXIF value, the corner pixels end
  up where the EXIF spec table says, written independently of the code.
- **mapping is a bijection onto the displayed image**: Every source pixel lands on a unique, in-bounds display pixel
  for all eight orientations.
- **FromExif/ToExif round trip; unknown values become identity**: Converting 1-8 to an orientation and back gives
  the same number. Invalid values become "no change".
- **rotation normalizes and composes**: Angles normalise to 0/90/180/270, four quarter turns return to the start, and
  rotating ±90° gives EXIF 6 or 8.
- **flipping the displayed image mirrors the final picture (checked pixel-by-pixel)**: "Flip" mirrors what is on
  screen, whatever the starting orientation, and flipping twice undoes it.
- **rotating the displayed image rotates the final picture (checked pixel-by-pixel)**: "Rotate 90°" turns what is on
  screen clockwise, whatever the starting orientation.

### LruCache
- **evicts the least recently used entry**: When the cache is full, the entry used least recently is evicted, and
  reading an entry counts as using it.
- **set overwrites, remove and clear work, capacity can shrink**: Setting a key again replaces its value, and
  remove/clear work. Lowering the capacity keeps the most recent entries. A capacity of 0 is rejected.

### ImageFileTypes
- **built-in formats are recognised case-insensitively**: JPEG, PNG, TIFF, BMP and GIF are recognised in any case.
  HEIC, WebP and non-images are not.
- **register / disable / ignored list**: New extensions can be added, and built-in ones can be turned off by
  Preferences or the ignored-types list and turned back on.
- **IsJpeg**: Recognises `.jpg`/`.jpeg` (any case) and nothing else.

### Settings
- **defaults match the original app's registered defaults**: A fresh `AppSettings` starts with the same defaults as
  Phoenix Slides (thumbnail size, slideshow timing, flags, sort order). Include subfolders starts off and resets when
  changing folder trees, and "More EXIF details" starts off.
- **sort order legacy encoding round-trips**: The original's signed-integer sort code converts back and forth exactly,
  and unknown codes fall back to Name ascending.
- **save and load round trip; no temp file left behind**: Saved settings load back unchanged (including the
  subfolder mode and "More EXIF details"), and the atomic save leaves no `.tmp` file behind.
- **settings from the old app name are copied across once, and never overwrite newer ones**: Settings in the old
  `PhoenixSlides` folder are copied over once, the old file is kept, and the copy never overwrites newer settings.
- **the legacy location is the old folder name and differs from the new one**: The old settings path uses
  `PhoenixSlides`, the new one uses `Emberlight`, and the two are different.
- **missing, corrupt or hostile files fall back to sane values**: A missing file, invalid JSON, `null`, or values
  out of range (including an unknown subfolder mode) all give defaults or clamped values instead of crashing.

---

## ExifTests.cs

### ExifReader
Emberlight's own TIFF/EXIF parser, including the lossless orientation rewrite used by "Rotate".

- **parses camera/exposure/date/GPS tags from a JPEG (little-endian / big-endian)**: Two tests, one per byte order.
  Each reads orientation, dates (capture date over modified date), camera, exposure, lens, dimensions and GPS from a
  synthetic JPEG.
- **basic display string omits extended tags**: The short info text leaves out extended tags (GPS, dimensions), and
  the long version includes them.
- **embedded thumbnail is extracted only when requested**: The embedded thumbnail is returned only when asked for,
  and data that isn't a JPEG is rejected.
- **APP1 need not be the first segment**: EXIF is found whether or not a JFIF segment comes before it.
- **reads TIFF files and PNG eXIf chunks**: EXIF is read from plain TIFF files and from a PNG's `eXIf` chunk as well
  as from JPEGs.
- **files without EXIF, and non-images, give null/zero**: Files with no EXIF, non-images, empty or missing files, and
  photos with no orientation tag give null or 0 without errors.
- **invalid orientation values are ignored**: Orientation values outside 1-8 (0, 9, 255) are treated as absent.
- **TrySetOrientation rewrites exactly the two orientation bytes (jpeg-LE / jpeg-BE / tiff-LE / png-BE)**: Four tests,
  one per file format and byte order. Changing the orientation alters only those two bytes, keeps the file size and
  other tags, and can keep the modified date.
- **TrySetOrientation refuses bad input and files it can't patch**: Invalid values, files with no orientation tag,
  junk files and missing files are refused and left unchanged.
- **never throws on truncated data (every prefix length)**: Parsing a JPEG, TIFF or PNG cut off at every possible
  length never throws.
- **never throws or hangs on randomly corrupted data**: 4,000 randomly corrupted files never throw, and parsing them
  all takes well under the 20-second limit.

---

## FileTests.cs

### Sorting
`FileComparers`, which implement the browser's sort menu.

- **name sort is natural and ignores directory for the primary key**: Sorts by file name in natural order, ignoring
  which subfolder the file is in, and the descending order is the exact reverse.
- **size sort, ascending/descending, ties fall back to name**: Sorts by size in either direction, and files of equal
  size fall back to name order.
- **date modified sort**: Sorts by the file's last-modified time in either direction.
- **type sort groups by extension**: Groups files by extension, then by name within each group.
- **EXIF date sort uses the capture date, not the file date**: Sorts by the EXIF capture date even when the files'
  modified dates suggest a different order.
- **metadata cache: sorting uses cached values until invalidated**: Sorting uses cached sizes and dates until a
  file's cache entry is cleared, then picks up the new value.
- **missing files never throw; they fall back to name order**: For every sort field, files that don't exist compare
  by name instead of throwing.
- **comparers are consistent (antisymmetric) for a mixed folder**: For every sort field, comparing A to B always
  gives the opposite of B to A, which list sorting needs to be reliable.

### DirectoryScanner
- **finds supported files, skips others and macOS companion files**: Finds image files, skips other files and macOS
  `._` companion files, and looks in subfolders only when asked.
- **honours disabled types and returns sorted results**: Leaves out disabled file types, and `ScanSorted` returns
  the files in the requested order.
- **missing directory yields an empty list; cancellation is honoured**: A missing folder gives an empty list, and a
  cancelled scan stops with `OperationCanceledException`.
- **ShouldShowFile mirrors scan rules (used for watcher events)**: The per-file check used for folder-watcher events
  accepts and rejects the same files as a full scan.

### StartupArgs
What to show when Emberlight is opened with command-line arguments (e.g. "Open with").

- **one file: shows its folder, starting on that file**: Opening one image shows all images in its folder, starting
  on that image.
- **one folder: shows everything in it, optionally including subfolders**: Opening a folder shows its images, and
  includes subfolders only when that option is on.
- **several paths: exactly those files, de-duplicated and sorted; quotes stripped**: Opening several files shows just
  those images, sorted, without duplicates or non-images, and strips quotes from the paths.
- **nothing displayable gives null (and never throws)**: No arguments, blank or bad paths, missing files, or only
  non-images give "nothing to show" without errors.

### PathUtil
"Is this folder inside that folder tree?", which decides when **Include subfolders** stays on.

- **same folder and anything beneath it is inside the tree**: A folder and anything below it count as inside the
  tree, whatever the trailing separators or letter case.
- **siblings, parents, look-alike names and unrelated folders are outside**: Siblings, parent folders, unrelated
  folders, and look-alike names such as `Pics2` next to `Pics` all count as outside.
- **drive/filesystem roots work as tree roots; bad input never throws**: A drive root works as the top of a tree, and
  invalid or empty paths return false instead of throwing.
- **network share roots get an Explorer-style label; other paths get none**: `\\truenas.local\photos` becomes
  `photos (\\truenas.local)` in the folder tree (backslashes, forward slashes or a trailing separator all work); drive
  paths, a bare server name, `\\?\` and `\\.\` device paths, and empty input give no label.
- **subfolders stay on: inside the tree when resetting, anywhere when set to Always**: With "Reset when changing
  folder trees", Include subfolders stays on only inside its tree (not in siblings or parents); with "Always" it
  stays on everywhere.

### ClickExpandTracker
Folder-tree accordion: folders opened by clicking their name close again when another folder is clicked.

- **clicking a sibling collapses the folder opened by the previous click**: Open Pics, click Docs: Pics is collapsed.
- **clicking deeper keeps the ancestors open; clicking away collapses the whole chain, deepest first**: Opening
  C > Pics > 2020 keeps all three; clicking Docs then collapses 2020 and Pics but keeps C, Docs' parent.
- **clicking back up to an ancestor collapses what was opened below it**: From Pics > 2020, clicking Pics closes 2020.
- **folders not opened by a click (arrow, keyboard, already open) are never collapsed**: Only folders that the click
  itself opened are tracked.
- **forgetting a folder (its arrow was used) keeps it open**: After `Forget`, the folder is no longer auto-collapsed;
  `Clear` empties the tracker when the tree is rebuilt.
- **nodes compare by identity: a shortcut to the same folder is a different node**: A ★ shortcut and the same folder
  under its drive are tracked separately, and clicking one node twice tracks it once.

### DirectoryScanner progress
Feeds the status-bar progress text while a large folder loads.

- **reports the running count every 250 files, in increasing order**: Scanning 620 images reports progress at 250
  and 500.
- **progress is optional and also flows through ScanSorted**: Scanning works without a progress listener, and
  `ScanSorted` reports progress too.

### FolderNavigator
Slideshow Ctrl+Right / Ctrl+Left: jump to the next or previous sibling folder.

- **siblings come back in natural name order, including the current folder**: Sibling folders are listed in natural
  name order, including the current folder and empty ones.
- **subfolders (for the folder tree): natural order, folders only, hidden skipped, missing is empty**: The folder tree's
  listing gives subfolders in natural name order, leaves out files and hidden folders, and returns an empty list for
  a folder with no subfolders, a missing folder or an invalid path.
- **next and previous skip folders that have no images**: Jumping forward or back skips sibling folders that contain
  no images.
- **stops at the ends: no wrap-around**: There is no next folder after the last sibling and no previous folder before
  the first.
- **if every later folder is empty there is no next folder**: When every later sibling has no images, there is no
  next folder.
- **result has sorted files and the folder's place among its siblings**: The folder jumped to comes with its images
  sorted and its position among the siblings (e.g. "folder 4 of 6").
- **a sort order is honoured (size, descending)**: The new folder's images use the current sort order.
- **include-subfolders decides whether nested images make a folder non-empty**: A folder whose images are only in
  subfolders counts as empty unless Include subfolders is on.
- **trailing separators and different case still identify the current folder**: The current folder is recognised
  even with a trailing separator.
- **missing folder, drive root and direction 0 give null without throwing**: A missing folder, a drive root (which
  has no siblings) or a direction of 0 gives "no folder" without errors.
- **cancellation is honoured**: A cancelled search stops with `OperationCanceledException`.
- **reference folder: the folder you started in wins while the slide is inside it**: While the slide on screen is
  anywhere inside the slideshow's starting folder, jumps are made relative to that starting folder.
- **reference folder: without an anchor, or outside it, the slide's own folder is used**: With no starting folder,
  or once the slide is outside it (look-alike names included), jumps use the slide's own folder and the starting
  folder is forgotten.
- **reference folder: with no slide (e.g. all deleted) the anchor is kept**: If there is no current slide (e.g. all
  were deleted), the starting folder is kept.
- **empty root + include subfolders: jumping goes to the root's sibling, not to a subfolder of the root**: Regression
  test: when the starting folder's images are all in subfolders, Ctrl+Right goes to the next sibling folder, not to
  another subfolder inside the starting folder.

### DirectoryScanner.HasAnyImage
- **finds an image, ignores others, respects recursion and companion files**: The quick "does this folder have any
  images?" check ignores non-images and `._` files, searches subfolders only when asked, and handles missing folders.

---

## SlideshowTests.cs

### SlideshowState
Slideshow navigation logic: current slide, looping, random order, auto-advance, files added or removed.

- **forward/back navigation, clamping, and blocking at the ends**: Stepping forward and back works, large jumps stop
  at the first or last slide, and moving past either end is reported as blocked.
- **loop mode wraps in both directions**: With looping on, stepping past either end wraps around to the other end.
- **empty slideshow never moves or throws**: With no slides, every navigation call is blocked and nothing throws.
- **auto-advance: going back pauses, going forward resumes, off stays off**: Stepping back pauses the auto-advance
  timer, stepping forward resumes it, and with auto-advance off nothing is paused.
- **random mode: chosen start slide is first; ordered stepping follows the original sequence**: In random mode the
  chosen slide comes first, "ordered" steps follow sort order and stop auto-advance, and they halt at both ends.
- **ordered flag is ignored when not in random mode**: Asking for an "ordered" step outside random mode is just a
  normal step.
- **random + loop reshuffles on wrap-around but keeps the list consistent**: Wrapping around in random+loop mode
  reshuffles the slides (unless that option is off) and keeps the same set of files.
- **SetRandom toggling keeps the current slide**: Turning random on or off keeps the slide on screen, and turning it
  off restores sort order.
- **random off continues in sort order from the slide on screen; random on again keeps it first and reshuffles**:
  Covers the `S` key. Turning random off continues in sort order from the slide on screen. Turning it back on makes
  that slide first in a new shuffle.
- **toggling random repeatedly never loses the current slide or corrupts the list**: Switching random on and off 20
  times never changes the current slide or damages the list.
- **removing files keeps the current slide sensible**: Deleting an earlier slide keeps the same picture on screen,
  deleting the current slide shows the next one, and deleting the last slide is handled.
- **removing while randomized keeps invariants**: Removing random files one by one from a shuffled slideshow keeps
  the list valid until it is empty.
- **AddFile inserts in sorted position and keeps the current slide**: A file that appears during the slideshow is
  added in sort order without changing the current slide, and duplicates are ignored.
- **per-file view state is remembered and dropped on removal**: Zoom and orientation are remembered for each file
  (file names match in any case) and forgotten when the file is removed.
- **CurrentChanged fires on navigation**: The "current slide changed" event fires on each real move, but not on a
  blocked one.

### WheelStepper
The mouse wheel in the slideshow: one notch moves one slide.

- **one notch is one slide: up = back, down = forward**: A 120-unit notch up goes back one slide, down goes forward,
  and a 240-unit event (fast spin) moves two.
- **small touchpad steps add up to a notch**: Precision-touchpad steps of 30 add up; 330 units give two slides and the
  remainder carries over.
- **reversing direction or resetting drops partial movement**: A partial scroll is forgotten when the direction
  reverses or the stepper is reset; zero does nothing.

---

## ViewTests.cs

### ZoomMath
- **steps go up in half-powers of two, matching the original**: Zoom steps go 1 → 1.5 → 2 → 3 → 4 → 6… going up and
  1 → 0.75 → 0.5 → 0.375… going down, as in Phoenix Slides.
- **off-grid values snap to the next step; in/out are inverses on the grid**: A zoom level between steps moves to
  the next step, zooming in then out (or out then in) returns to the same step, and every step changes the zoom.

### ImageViewport
Zoom, pan and fit-to-window logic for the slideshow view.

- **default layout: shrink big images to fit, leave small ones alone**: Large images shrink to fit the window and
  are centred. Small images stay at 100% unless "scale up" is on.
- **actual-size and scale-up options interact like the original**: The "actual size" and "scale up" preferences
  combine as in the original, including an image exactly the size of the window.
- **zoom in/out step through the grid and mark the viewport zoomed**: Zoom in/out moves through the zoom steps, Actual
  Size goes to 100% and allows panning, and Reset returns to fit-to-window.
- **zoom limits**: Zooming stops at the maximum and minimum levels, and with no image, zooming does nothing.
- **panning is limited to the image edges, and not possible when the image fits**: Panning stops at the image edges,
  and an image that fits the window can't be panned.
- **small image while zoomed in one axis stays centred in the other**: A wide, short image can pan horizontally but
  stays centred vertically.
- **zooming in while pinned to an edge keeps that edge pinned**: When the view is at an image edge or corner,
  zooming in keeps that edge on the window's edge.
- **zooming in from the middle keeps the middle**: Zooming in keeps the point at the centre of the window in place.
- **zoom-at-point keeps the image point under the pointer fixed**: Mouse-wheel zoom keeps the point under the cursor
  fixed, in both directions.
- **view and image coordinates are inverses**: Converting screen coordinates to image coordinates and back gives the
  starting point.
- **resizing the view re-fits the default layout and re-clamps a zoomed one**: Resizing the window re-fits a
  fit-to-window image, and keeps a zoomed-in view within the image edges.
- **orientation change keeps the zoom but recentres**: Rotating the image keeps the zoom level and recentres the
  view.
- **zoom info round-trips; garbage values are tolerated**: The saved per-file zoom restores the same view, and
  missing, NaN, negative or infinite values are handled safely.
- **degenerate sizes never produce NaN or exceptions**: Zero-sized windows or images never cause errors or invalid
  numbers.
- **fuzz: any sequence of operations keeps the view inside the image**: 40 random scenarios × 150 random
  zoom/pan/resize/rotate steps. After each step the zoom is a valid number and the view stays within the image, or
  centred if the image fits.

### ThumbnailGridMath
Layout maths for the browser's thumbnail grid.

- **cell height and columns**: Cell height depends on whether file names are shown, and there is always at least one
  column.
- **visible range covers the viewport plus overscan and clamps to the item count**: The range of thumbnails to draw
  includes partly visible rows and the extra rows above and below, and stays within the list for any scroll position.
- **every item on screen is inside the computed range (brute-force cross-check)**: 300 random grid sizes. Every
  thumbnail that is actually on screen is inside the computed range.

### ThumbnailGridMath (virtualization)
The maths behind the custom `VirtualizingGridPanel`, which only creates thumbnails near the visible area.

- **rows, extent and cell positions**: Row count, total grid height and each cell's position are correct. Zero
  columns are treated as one.
- **clamping scroll offsets**: Scroll positions are kept in range, including when the grid is shorter than the window
  or the value is NaN or infinite.
- **reveal: no scroll if already visible, minimal scroll otherwise**: Bringing a thumbnail into view doesn't scroll if
  it is already fully visible, and otherwise scrolls as little as needed.
- **reveal puts any item fully on screen (brute-force cross-check)**: 2,000 random cases. After a reveal, the item is
  fully visible, the scroll position is valid, and the item is in the range that gets drawn.
- **retained window widens the visible range and clamps to the list**: The set of thumbnails kept in memory is the
  visible range plus a margin, limited to the list, including for tiny, empty or over-scrolled lists.
- **page size**: Page Up/Down moves by the number of rows that fit in the window, and always at least one.

### FileTransfer
Copy and move commands in the browser.

- **unique names never collide**: When a name is taken, Emberlight picks `a (2).jpg`, then `a (3).jpg`, and so on.
- **copy keeps the original and never overwrites**: Copying keeps the original, and copying again creates a renamed
  copy instead of overwriting.
- **move relocates, renames on conflict, and is a no-op in the same folder**: Moving removes the original and renames
  instead of overwriting an existing file. Moving into the file's own folder does nothing.

### FolderWatcher
Detects files added, changed or deleted in the open folder (timing based).

- **reports new, modified and deleted files after the quiet period**: New, changed and deleted images are reported
  once the folder has been quiet briefly, and non-image files are ignored.
- **bursts are coalesced into one notification**: Twelve quick file writes arrive as one or two notifications, with
  all files included.
- **ignores subfolders unless asked, and picks them up when asked**: Changes in subfolders are reported only when
  the watch includes subfolders.
- **refuses missing folders and recursive watches of a drive root; stop is idempotent**: Won't watch a missing folder
  or a whole drive including subfolders, and stopping or disposing twice is safe.
- **reports when the watched folder disappears**: Deleting the folder being watched raises an event. This is the case
  that `FileSystemWatcher` misses and `FolderWatcher` handles by polling.
