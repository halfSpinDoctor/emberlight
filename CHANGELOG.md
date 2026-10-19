# Changelog

Each release's notes. The release workflow copies the section for the tag it is building into the GitHub release,
above GitHub's automatic list of commits. Earlier releases (v0.1.0, v0.2.0) have only that automatic list.

## v0.2.2 - 2026-10-19

**New**

- Pinned folders: pin folders to a list above the folder tree (File > Pinned Folders, or right-click a folder in the
  tree), open the first nine with Ctrl+1 to Ctrl+9, reorder them, and give a pin its own name (Rename Pin).
- Right-click menu on thumbnails (Copy To, Move To, Rename, Move to Recycle Bin) and a Rename command (File menu, F2).

**Network shares and responsiveness**

- The window no longer freezes when a network connection (VPN or Wi-Fi) drops while you are browsing a share, and it
  recovers when the connection returns.
- Opening a dead network location, or starting with a last-viewed folder that is no longer reachable, no longer
  freezes the window; existence checks, the info panel's size and EXIF reads, and slideshow folder jumps now run in
  the background.
- Folders on network shares load faster: sorting by size, date or EXIF date reads file information in parallel, and
  the latest sort request wins when several are made in a row.

## v0.2.1 - 2026-10-16

This release reorganizes the application and Core logic, and fixes bugs.

**Reorganization**

- Almost all of the app's decision-making moved out of the Windows (WPF) windows into the platform-neutral
  `Emberlight.Core` library, where it is unit tested; the windows now mainly draw, route input and call Core. Core
  is grouped into area folders (Browser, Slideshow, Imaging, Files, Settings, Common). The test suite grew from 161
  to 363 tests.
- Apart from the fixes and changes below, the app behaves as before.

**Bug fixes**

- Moving files into the folder they are already in no longer takes them out of the grid.
- The folder tree now shows subfolders created or deleted while the app is open, and drives that connect later
  (a VPN reconnecting mapped drives, a USB stick) appear within a few seconds.
- The thumbnail grid no longer flickers when the folder on screen is rescanned (for example after a subfolder is
  deleted with "Include subfolders" on).
- A file that appeared while the grid was being re-sorted no longer drops out of the grid.
- The info panel no longer shows a half-written "Folder:" / "Size:" for a file that has just been deleted.
- Colour swatches no longer show thin seams at 125% or 150% display scaling.

**New and changed**

- A colour picker beside the slideshow colour preferences (presets, a colour square, hue and opacity), alongside
  typing a colour by hand. Colour preferences no longer accept the rarely used `sc#` (scRGB) form.
- Every delete to the Recycle Bin now asks first: `Are you sure you want to send "<filename>" to the Recycle Bin?`
  (previously only above 10 files).
- The slideshow help screen (H) lists each key on its own line.
