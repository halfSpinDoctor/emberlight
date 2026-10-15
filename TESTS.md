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
- **IsGif**: Recognises `.gif` in any case, and not a name that merely contains "gif".

### Settings
- **defaults match the original app's registered defaults**: A fresh `AppSettings` starts with the same defaults as
  Phoenix Slides (thumbnail size, slideshow timing, flags, sort order). Include subfolders starts off and resets when
  changing folder trees, "More EXIF details" starts off, animated GIFs play and finish before auto-advance, transparent areas show white, and the info panel is 280 px wide.
- **sort order legacy encoding round-trips**: The original's signed-integer sort code converts back and forth exactly,
  and unknown codes fall back to Name ascending.
- **save and load round trip; no temp file left behind**: Saved settings load back unchanged (including the
  subfolder mode, "More EXIF details" and the two GIF options), and the atomic save leaves no `.tmp` file behind.
- **missing, corrupt or hostile files fall back to sane values**: A missing file, invalid JSON, `null`, or values
  out of range (including an unknown subfolder mode and a too-narrow info panel) all give defaults or clamped values instead of crashing.

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

### GifCompositor
Animated GIF playback: painting each frame onto the canvas the way the GIF asks. The tests use tiny frames and show the
canvas as text, one digit per pixel ("." = transparent).

- **a frame smaller than the canvas is drawn at its offset; the rest stays transparent**: A 2x1 frame at (1,1) lands
  there on a 4x3 canvas.
- **Keep: the next frame is drawn over the last, and its transparent pixels show what is underneath**: A frame with
  a transparent hole lets the previous frame show through it.
- **RestoreBackground clears the frame's own rectangle before the next frame**: Only that frame's area becomes
  transparent; the rest is kept.
- **RestorePrevious puts back the canvas from before the frame**: The frame is undone before the next one is drawn.
- **after the last frame it loops to the first on a clear canvas, for ever**: Looping starts from an empty canvas
  and keeps going indefinitely.
- **frames hanging off the canvas are clipped, including when disposed**: Frames at negative positions or larger
  than the canvas are drawn and cleared without errors.
- **delays: hundredths of a second, with 0 and 1 treated as 100 ms like browsers do**: Delay conversion, including
  the browser rule for very short delays.
- **one play-through lasts the sum of the frame delays**: `Duration` adds the delays (with the 100 ms rule), which is
  how long auto-advance waits on an animated GIF.
- **an empty canvas, no frames or pixel data of the wrong size are rejected**: Invalid animations throw instead of
  misbehaving later; `FrameBytes` adds up the frames' pixel data.

### Slideshow commands
What slideshow commands do, apart from which key asks for them (`SlideshowCommands`, plus the `SlideshowState`
methods the commands use).

- **after the last slide only going back, first/last, folders, help and quit still work**: Checks every command:
  those run, Space is swallowed, and everything else is ignored.
- **first slide: jumps back to the start, blocked when already there**: Home goes to slide 1 and beeps there, even in
  loop mode.
- **first slide pauses auto-advance like any step back**: Going back to the start pauses a running timer.
- **last slide: jumps to the end, even from past the end; blocked when empty**: End works after the last slide was
  deleted, and does nothing in an empty show.
- **a page is 10 slides**: Page Up and Page Down move 10 slides.
- **starting auto-advance from off moves on once; changing a running timer doesn't**: A digit key with the timer off
  steps forward first; with it running it only changes the interval.
- **starting auto-advance while paused also moves on and resumes**: A paused timer counts as off.
- **starting auto-advance on the last slide still starts the timer**: The step is blocked (a beep) but the timer
  starts; in loop mode the step wraps to the first slide.
- **Space pauses a running timer, otherwise moves on (in original order with Alt)**: Pausing doesn't move; moving
  resumes; Alt steps in sorted order through a shuffled show.
- **timer tick: carries on, except at the end of a non-looping show, where it stops**: Auto-advance switches itself
  off at the last slide unless the show loops.
- **auto-advance interval: the set time, or longer to let an animation finish**: A GIF longer than the interval gets
  to play through.

### Slideshow overlay
The info and EXIF overlays: which are showing (`SlideshowOverlay`) and their text (`DisplayText`).

- **I cycles none -> file info -> file info + EXIF -> none**: The I key steps through the three overlay modes.
- **Shift+I jumps to the EXIF view with more details, then toggles the details**: From any other view it opens the
  EXIF view with the extra tags; in that view it toggles them.
- **P toggles the full path and shows the info if it was hidden**: An already open view stays as it is.
- **info line: position and name, then size, file size and zoom**: The full text, with the file name or the full path,
  and without the second line when the slide couldn't be opened.
- **info line: rotation and flip the user applied**: "rotated 90°" and ", flipped" appear when the slide is turned.
- **info line: random, loop and auto-advance flags**: The flags in brackets, including "(paused)".
- **info line: an empty or finished show says so**: "No images" for an empty show and after the last slide was
  deleted.
- **EXIF overlay: the tags, or a note when there are none**: The overlay shows the same tags as the info panel, or
  "(no EXIF data)".

### WindowFit
Resizing a windowed slideshow to the picture (a preference).

- **the window grows or shrinks to the picture plus its frame, keeping its centre**: The exact new bounds, with the
  centre unchanged.
- **picture pixels are converted to screen units at high DPI**: At 200% a 1000 px picture needs 500 units.
- **a rotated picture swaps width and height**: EXIF orientation 6 turns a landscape picture portrait.
- **at least 320 x 240, at most 95% of the work area**: Both limits.
- **a work area smaller than the minimum doesn't throw**: The minimum wins instead of an exception.

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

---

## BrowserTests.cs

### FileListing
The browser's file list: items in sort order, looked up by path, kept in order as files change. The tests use a plain
list of stand-in items where the app uses the grid's observable collection.

- **populate keeps the given order and looks items up by path, ignoring case**: Items appear in the order given and
  are found by path in any case; populating again or clearing replaces everything.
- **populate goes through the bulk replace when one is given**: Whole-list changes use the supplied bulk replace (one
  change notification on WPF).
- **new files are inserted at their sorted place (natural name order)**: `img7` lands between `img5` and `img10`, and
  new first and last files go at the ends.
- **descending order inserts in descending place**: Insertion follows a reversed sort too.
- **a changed file keeps its item; by name or path it stays put**: A reported change in name or path order keeps the
  same item where it is.
- **a changed file moves when the sort key can change (size)**: In size order, a file that grows moves to its new
  place, as the same item.
- **remove takes the item out of the list and the lookup; unknown paths are ignored**: Removal is case-insensitive and
  removing twice or an unknown file does nothing.
- **set order swaps the comparer; sort re-orders the same item objects**: Changing the order alone moves nothing;
  sorting then re-orders the existing items.
- **a file that arrives while sorting is kept, at its sorted place**: A file added between taking the snapshot and
  applying the sorted order isn't lost (the old browser code dropped it from the grid).
- **a file removed while sorting stays removed**: Applying a sorted order made before a removal doesn't bring the file
  back.
- **sorting an empty list does nothing**: No errors on an empty folder.
- **only name, path and type sorts can skip opening the files**: `SortOrder.NeedsFileInspection` is true for the date
  and size sorts, which show "Sorting..." rather than "Preparing...".
- **fuzz: random adds, removes, changes and re-sorts keep list and lookup in sorted step**: 30 seeds x 150 random
  operations, checking after each that the list is sorted, has no duplicates and matches the lookup.

### SubfolderScope
Where "Include subfolders" applies: the folder tree where it was switched on.

- **off: navigation never switches it on and keeps no root**: With the option off nothing changes.
- **switched on: stays on inside the tree, switches off outside it**: Subfolders and the root keep it on; a folder
  outside switches it off, and it stays off.
- **Always mode keeps it on anywhere**: With "Always" it never switches itself off.
- **on from the last session: the first folder shown becomes the root**: A remembered "on" roots the tree at the
  startup folder.
- **on while a folder is already shown: that folder is the root**: The root is the folder that was on screen.
- **switching it off forgets the root**: Turning it off clears the root.
- **following a slideshow folder jump moves the root there instead of switching off**: After Ctrl+Left/Right the
  browser's tree starts at the folder jumped to; nothing changes while the option is off.
- **preferences: a change of on/off or mode re-roots at the folder on screen**: Switching on/off asks for a rescan;
  changing only the mode re-roots without one; no change keeps the root.

### DecodePolicy
How big pictures are decoded.

- **thumbnails decode at one of four sizes, by cell width in device pixels**: 160, 256, 360 or 512 px, with the
  boundaries at 150, 230 and 330 device pixels, including high-DPI scaling.
- **thumbnail cache holds about 160 MB, between 100 and 4000 entries**: Cache capacity for each decode size and both
  limits.
- **slides decode to the view's longer side, full size for 100% view, 3840 before layout**: Slide decode targets,
  including rounding up and the not-yet-laid-out case.
- **scale only when the picture is bigger than the limit; 0 means no limit**: When the decoder should scale.
- **scaled size keeps the aspect ratio with the longer side at the limit, never 0**: Landscape, portrait, rounding
  and a very thin picture.
- **embedded EXIF previews are used when at least 3/4 of the wanted size**: The 3/4 rule, including small photos and
  portrait previews.
- **full resolution is needed once zoom passes what the decoded bitmap holds**: A reduced decode is replaced once the
  zoom exceeds its detail (with a 0.1% tolerance); a full-size bitmap never is.

### ImageInfo
What is known about a decoded picture apart from its pixels, shared by both platforms' image caches.

- **a cached picture matches its file until the size or modified time changes**: Touching or resizing the file makes
  the cached decode stale; putting the time back makes it current again.
- **a missing file or a bad path never matches**: No exceptions for a missing file, an invalid path or an empty one.
- **ForFile reads the size and modified time from the file**: All fields are filled in from the file and the
  arguments.

### DisplayText (browser)
The browser's status bar, thumbnail tooltips and info panel.

- **status bar: image count, then the selection**: "1 image", "N images", "N selected", and the selected file's
  name with its size once loaded.
- **thumbnail tooltip: the name, plus size once loaded**: Name alone until the thumbnail loads.
- **info panel heading: no selection, several, or the file name**: The three headings.
- **info panel: folder, size, date and dimensions; EXIF after a blank line**: The exact lines for a plain file, with
  and without the picture size, and the EXIF tags (normal and "more") after a blank line.
- **info panel: a file that has gone gets no file lines, rather than half of them**: A deleted file shows only what
  is still known (the old code left a dangling "Folder:" and "Size:").

### Sort choices
- **every sort field is offered once, in menu order, with its label**: The Sort By menu and Preferences list.
- **choosing a new field: dates newest first, the rest ascending**: As in the original.
- **choosing the current field again reverses it**: Both directions.

### Startup folder
Which folder the browser opens at startup.

- **a folder given on the command line is opened as it is**: Nothing is selected.
- **a file given on the command line opens its folder and selects it**: The file's full path is selected.
- **otherwise the last folder or the chosen pictures folder, as set**: Follows the startup preference; a path that
  doesn't exist is ignored.
- **a remembered folder that has gone falls back to an existing folder**: The user's Pictures folder, or their home
  folder.

### PathUtil helpers
- **ancestor chain: the root, then each folder down to the path**: Used to open the folder tree level by level;
  trailing separators make no difference.
- **ancestor chain of a network share starts at the share**: `\nas\photos` is the root (Windows only).
- **ancestor chain of bad input is empty**: No exceptions.
- **same path ignores trailing separators and case, but not real differences**: Also checks `PathUtil.Comparer` and
  `PathUtil.Comparison`, which every path comparison in the app now uses.
- **nearest existing folder walks up past folders that have gone**: Used when the open folder is deleted.

### Slideshow from the browser
Which files a slideshow started from the browser shows (`StartupTarget.FromSelection`).

- **nothing selected: the whole folder from the start**.
- **one file selected: the whole folder, starting there**: Matched in any case.
- **several selected: just those, in grid order, from the first**.
- **a selected file that is no longer in the grid starts at the beginning**.

### Thumbnail sizes
- **cell widths are whole numbers within the allowed range**: Rounding and both limits.
- **cells have a 4 px margin and a 20 px caption; size steps are 20**: The constants the grid template and the
  Larger/Smaller commands use, and the cell height with and without file names.

## InteractionTests.cs

### ThumbnailScheduler
The browser's background thumbnail queue, driven with no worker threads so each step is deterministic (the
deliveries that would go to the UI thread are collected and run by the test).

- **a decoded thumbnail is shown, cached, and served from the cache next time**: Nothing is decoded on the UI thread;
  after the decode the cell shows it, and a new cell for the same file (any case on Windows) is filled from the cache
  without decoding again.
- **a cached thumbnail that no longer matches its file is decoded again**: When the file check fails, the cell is
  queued instead of shown from the cache.
- **loaded or already queued items aren't queued twice**: Asking twice queues once, and a loaded cell isn't queued.
- **newest requests are served first; a range is decoded top to bottom**: A visible range is decoded first cell first,
  and later requests jump ahead of earlier ones.
- **requests for cells scrolled away are dropped without decoding and can be requested again**: Scrolling on before
  the decodes run skips the old cells, which are free to be asked for again when scrolled back.
- **an item still in the newest range keeps its place when the range moves**: Overlapping ranges don't decode a cell
  twice; the cells only in the new range come first.
- **reset forgets the queue; results finished before a reset are thrown away**: After a reset (new folder) the queue
  is empty, a result that was in flight is neither shown nor cached, and the cell can be asked for again.
- **items queued before a reset are skipped when a worker reaches them**: A request from an older generation is
  not decoded.
- **a new decode size clears the cache and discards results at the old size**: The same size is no change; a new
  size empties the cache, drops a result decoded at the old size, and decodes at the new one.
- **invalidate forgets one cached thumbnail**: Only that file is decoded again.
- **a file that can't be decoded shows as broken and isn't cached**: The cell gets the "broken" result once, and a
  new cell tries again.
- **worker threads decode every visible item**: With three real worker threads, all 50 requested cells are shown
  with the right result.
- **default worker count is half the processors, 1 to 4**.

### ThumbnailGridMath (releasing thumbnails)
- **released items are the old window minus the new one**: Overlapping, shrinking, disjoint and covering windows.
- **a list that shrank only visits items that still exist**.
- **retain margin is four screens of items**: Columns × visible rows × 4, at least one of each.
- **fuzz: scrolling around never leaves a decoded thumbnail outside the retained window**: 200 seeds × 40 random
  scrolls (with files sometimes deleted). Every released index is outside the new window, and every thumbnail still
  decoded is inside it.

### ThumbnailGridMath (keyboard movement)
- **arrows move by one item or one row**: Including wrapping from the end of one row to the next.
- **page keys move by the visible rows; Home and End go to the ends**.
- **moves stop at the ends of the list**: Including Down into a short last row.
- **nothing selected counts as the first item; an empty list has no target**: And zero columns or rows count as one.
- **fuzz: every move lands in the list, and down/up stay in the same column when they can**: 3,000 random grids,
  every key.

### ViewerInput
Mouse and keypad input on the slideshow picture.

- **Ctrl+wheel zooms 25% in or 20% out per event, so one notch each way is a round trip**.
- **the wheel pans 128 px per notch, scaled to device pixels; Shift pans sideways**: Including half notches from a
  touchpad.
- **a press becomes a drag once it moves more than 3 px on either axis**: Exactly 3 px is still a click; moves under
  one DIP in total are ignored.
- **pixels are drawn sharp from 3x zoom**.
- **keypad keys pan half a view towards their side; 5 and non-digits do nothing**: All eight directions.
- **keypad pans move the viewport the right way**: Key 7 on a zoomed-in picture shows more of its top-left.

### Slide preloading
- **the next two slides, then the previous one**: At the start, the end, and in one-slide and empty slideshows.
- **works on a shuffled slideshow's list order**.
- **no preloading within 200 ms of a navigation**: 199 ms is too soon, 200 ms is fine.

### OrientationEditor
The browser's lossless Rotate Left/Right and Reset Orientation commands.

- **new orientation: turned from the current one, or upright for Reset**: And nothing for a file with no EXIF or no
  orientation tag.
- **rotating files updates the tagged ones and skips the rest**: On real files: a tagged JPEG is rewritten (keeping its
  modified time when asked), and an untagged JPEG, a junk PNG and a missing file are skipped.
- **summary text**: The status bar line for nothing selected, all updated, all skipped, and a mix.

### FileDeletion
The browser's Delete command, with a fake Recycle Bin.

- **more than 10 files asks first**: 10 doesn't, 11 does, and the question's wording.
- **deletes each file, reports each one that went, and keeps the first error**: Every file is tried in order; the
  failure line names the count and the first error, and there is none when everything went.
- **afterwards the item at the old position is selected, or the last one**: And nothing when the list is empty.

### Preferences input
- **auto-advance seconds: a number above 0 and up to an hour, in the user's number format**: Spaces, exponents and a
  German decimal comma are accepted; 0, negatives, over an hour, text, NaN, infinity and thousands separators are not.
- **seconds are shown with up to two decimals**: In the user's number format.
- **both colours must be valid**.
- **what changed decides between rescan, re-sort and nothing**: New file types or Include subfolders rescan; a new
  sort order (direction alone counts) re-sorts, or only records the order when a rescan follows.

### ColorText
The colour preferences, read without WPF. It was cross-checked against WPF's `ColorConverter` on about 143,000
inputs (all named colours in several spellings plus random hex-like strings); the only difference is the `sc#`
form, which ColorText doesn't read.

- **hex colours: #RRGGBB is opaque, #AARRGGBB has alpha, either case**.
- **short hex: #RGB and #ARGB double each digit**.
- **colour names in any case, spaces around ignored**: Including Transparent and the Aqua/Cyan synonyms.
- **anything else is rejected**: Wrong lengths, non-hex digits, inner spaces, missing `#`, unknown names (including
  the CSS-only RebeccaPurple), `sc#` and `rgb()`.
- **split gives the four channels**.
- **defaults in AppSettings are valid colours**.
