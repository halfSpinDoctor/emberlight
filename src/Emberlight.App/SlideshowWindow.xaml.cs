using System.IO;
using System.Media;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Emberlight.App.Controls;
using Emberlight.App.Services;
using Emberlight.Core;

namespace Emberlight.App;

/// <summary>
/// Full-screen (or windowed) slideshow. Port of SlideshowWindow: navigation, shuffle, loop, auto-advance,
/// zoom/rotate/flip, info and EXIF overlays. The navigation rules live in <see cref="SlideshowState"/> and
/// the zoom maths in <see cref="ImageViewport"/>; this class is the keyboard/mouse/timer glue.
/// </summary>
public partial class SlideshowWindow : Window
{
    private const string HelpContent =
        "Slideshow keys\n" +
        "\n" +
        "Right  Down  Space       next slide\n" +
        "Left   Up    Shift+Space previous slide\n" +
        "Alt+Right / Alt+Left     next / previous in original order (shuffled show)\n" +
        "Home  End                first / last slide\n" +
        "Ctrl+Right  Ctrl+Left    next / previous folder (same level; skips empty folders)\n" +
        "PageUp  PageDown         back / forward 10 slides\n" +
        "\n" +
        "1-9                      auto-advance every N seconds     0  stop\n" +
        "Shift+1  Shift+2         auto-advance every 0.5 / 1.5 seconds\n" +
        "Space                    pause auto-advance\n" +
        "\n" +
        "+  -                     zoom in / out     =  actual size     *  reset\n" +
        "L  R                     rotate left / right     F  flip\n" +
        "Keypad 1-9               pan around when zoomed in\n" +
        "\n" +
        "I                        cycle file info / EXIF / none\n" +
        "Shift+I                  more EXIF        P  show full path\n" +
        "Delete                   move current file to the Recycle Bin\n" +
        "S                        random order on / off (reshuffles when turned on)\n" +
        "H  ?                     this help        Q  Esc  .  quit\n" +
        "\n" +
        "Mouse: click = next, double-click = quit, wheel = previous / next,\n" +
        "       Ctrl+wheel = zoom, drag or wheel = pan when zoomed";

    private readonly AppSettings _settings;
    private readonly SlideshowState _state;
    private readonly WheelStepper _wheel = new();      // mouse wheel: one notch = one slide
    private readonly DispatcherTimer _timer = new();
    private readonly LruCache<string, LoadedImage> _cache = new(5, comparer: StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _preloading = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _preloadLock = new();
    private readonly bool _fullScreen;
    private readonly DirectoryScanner _scanner;
    private readonly SortOrder _sortOrder;
    private readonly bool _includeSubfolders;
    private readonly DispatcherTimer _toastTimer = new();

    private string? _jumpAnchor;       // folder we started in / last jumped to; the reference for Ctrl+Left/Right while the slide is inside it
    private bool _folderJumpBusy;
    private bool _closed;

    private LoadedImage? _current;
    private int _displayVersion;
    private long _lastNavTicks;
    private bool _moreExif;

    /// <summary>Raised after Ctrl+Left/Right loaded another folder, so the browser can follow along.</summary>
    public event Action<string>? FolderJumped;

    /// <summary>The slide that was showing when the window closed (so the browser can select it).</summary>
    public string? LastShownPath { get; private set; }

    /// <param name="sortOrder">Used to sort the files of any folder jumped to with Ctrl+Left/Right.</param>
    /// <param name="includeSubfolders">Whether those folders include their own subfolders' images.</param>
    /// <param name="startFolder">The folder the slideshow was started from, if there was one. Ctrl+Left/Right are
    /// relative to it while the slide on screen is inside it, even when its images are in subfolders.</param>
    public SlideshowWindow(AppSettings settings, IReadOnlyList<string> files, int startIndex,
                           SortOrder sortOrder, DirectoryScanner scanner, bool includeSubfolders,
                           string? startFolder, bool fullScreen)
    {
        InitializeComponent();
        _settings = settings;
        _fullScreen = fullScreen;
        _sortOrder = sortOrder;
        _scanner = scanner;
        _includeSubfolders = includeSubfolders;
        _jumpAnchor = !string.IsNullOrWhiteSpace(startFolder) && Directory.Exists(startFolder) ? startFolder : null;
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); ToastPanel.Visibility = Visibility.Collapsed; };

        Background = BrushHelper.FromString(settings.SlideshowBackgroundColor, Brushes.Black);
        Viewer.ImageBackground = BrushHelper.FromString(settings.TransparentImageBackgroundColor, null);
        Viewer.ScaleUp = settings.SlideshowScaleUp;
        Viewer.ActualSize = settings.SlideshowActualSize;
        HelpText.Text = HelpContent;

        _state = new SlideshowState
        {
            Loop = settings.SlideshowLoop,
            RerandomizeOnLoop = settings.RerandomizeOnLoop,
        };
        _state.Load(files, startIndex, settings.SlideshowRandom);
        if (settings.SlideshowAutoAdvance) _state.SetAutoAdvance(settings.SlideshowAutoAdvanceSeconds);
        _state.CurrentChanged += () => _ = DisplayCurrentAsync();   // subscribed after Load: the first display happens in Loaded

        _timer.Tick += OnTimerTick;
        Viewer.Clicked += OnViewerClicked;
        Viewer.UserViewChanged += (_, _) => OnUserChangedView();

        if (fullScreen)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            Topmost = true;
            // keep a fullscreen show from covering other apps after alt-tab
            Deactivated += (_, _) => Topmost = false;
            Activated += (_, _) => Topmost = true;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        Loaded += (_, _) =>
        {
            Activate();
            Focus();
            _ = DisplayCurrentAsync();
        };
        Closed += (_, _) => { _closed = true; _timer.Stop(); _toastTimer.Stop(); };
    }

    // ============================================================================================
    // Display pipeline
    // ============================================================================================

    private async Task DisplayCurrentAsync()
    {
        int version = ++_displayVersion;
        _timer.Stop();
        try
        {
            if (_state.IsPastEnd)
            {
                Viewer.Clear();
                _current = null;
                ShowMessage(_state.Files.Count == 0 ? "No images" : "No more images\n(press \u2190 or Esc)");
                UpdateInfo();
                return;
            }

            string path = _state.CurrentFile!;
            LastShownPath = path;

            if (!_cache.TryGet(path, out var img) || !IsFresh(img, path))
            {
                int decode = DecodeTarget();
                img = await Task.Run(() => version != _displayVersion ? null : ImageService.LoadDisplay(path, decode, () => version != _displayVersion));
                if (version != _displayVersion) return;     // the user has already moved on
                if (img != null) _cache.Set(path, img);
            }

            ShowLoaded(path, img);
            EnsureFullResolutionIfNeeded();
            RestartTimer();
            Preload();
        }
        catch (Exception ex)
        {
            ShowMessage(ex.Message);
        }
    }

    private void ShowLoaded(string path, LoadedImage? img)
    {
        _current = img;
        if (img == null)
        {
            Viewer.Clear();
            ShowMessage("Could not open\n" + Path.GetFileName(path));
            UpdateInfo();
            return;
        }

        MessageText.Visibility = Visibility.Collapsed;
        _state.TryGetViewState(path, out var saved);
        ImageOrientation orientation = saved?.Orientation
            ?? (_settings.AutoRotateByOrientationTag ? img.ExifOrientation : ImageOrientation.Identity);
        FitWindowToImage(img, orientation);
        Viewer.SetImage(img.Bitmap, img.PixelWidth, img.PixelHeight, orientation, saved?.Zoom);

        if (!_fullScreen) Title = Path.GetFileName(path) + " \u2014 Emberlight";
        UpdateInfo();
    }

    /// <summary>Windowed slideshows can resize themselves to each picture (preference), keeping the window centred.</summary>
    private void FitWindowToImage(LoadedImage img, ImageOrientation orientation)
    {
        if (_fullScreen || !_settings.SlideshowWindowFitToImage || WindowState != WindowState.Normal || !IsLoaded) return;

        var (w, h) = orientation.ApplyToSize(img.PixelWidth, img.PixelHeight);   // device pixels
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double chromeW = ActualWidth - Viewer.ActualWidth, chromeH = ActualHeight - Viewer.ActualHeight;
        var work = SystemParameters.WorkArea;                                    // device-independent units
        double newW = Math.Clamp(w / dpi + chromeW, 320, work.Width * 0.95);
        double newH = Math.Clamp(h / dpi + chromeH, 240, work.Height * 0.95);

        Left += (ActualWidth - newW) / 2;
        Top += (ActualHeight - newH) / 2;
        Width = newW;
        Height = newH;
    }

    /// <summary>
    /// Slides are first decoded at about screen size (fast, light on memory). If the user zooms past what
    /// that bitmap can show sharply, the full-resolution decode replaces it in the background.
    /// </summary>
    private void EnsureFullResolutionIfNeeded()
    {
        var img = _current;
        string? path = _state.CurrentFile;
        if (img == null || img.IsFullResolution || path == null) return;
        double ratio = (double)Math.Max(img.Bitmap.PixelWidth, img.Bitmap.PixelHeight) / Math.Max(img.PixelWidth, img.PixelHeight);
        if (Viewer.Viewport.Scale <= ratio * 1.001) return;     // already sharp enough for this zoom level
        _ = LoadFullResolutionAsync(path, _displayVersion);
    }

    private async Task LoadFullResolutionAsync(string path, int version)
    {
        try
        {
            var full = await Task.Run(() => ImageService.LoadDisplay(path, 0));
            if (full == null) return;
            _cache.Set(path, full);
            if (version != _displayVersion || !string.Equals(_state.CurrentFile, path, StringComparison.OrdinalIgnoreCase)) return;
            _current = full;
            Viewer.ReplaceBitmap(full.Bitmap);
            UpdateInfo();
        }
        catch (Exception)
        {
            // keep showing the lower-resolution version
        }
    }

    private int DecodeTarget()
    {
        if (_settings.SlideshowActualSize) return 0;                    // 100% view needs every pixel
        double d = Math.Max(Viewer.Viewport.ViewWidth, Viewer.Viewport.ViewHeight);
        return d > 64 ? (int)Math.Ceiling(d) : 3840;
    }

    private void Preload()
    {
        // While the user is holding down an arrow key, don't burn CPU on slides they'll skip.
        if (Environment.TickCount64 - _lastNavTicks < 200) return;
        int decode = DecodeTarget();
        int i = _state.CurrentIndex;
        var wanted = new List<string>(3);
        if (i + 1 < _state.Files.Count) wanted.Add(_state.Files[i + 1]);
        if (i + 2 < _state.Files.Count) wanted.Add(_state.Files[i + 2]);
        if (i - 1 >= 0) wanted.Add(_state.Files[i - 1]);

        foreach (string p in wanted)
        {
            if (_cache.ContainsKey(p)) continue;
            lock (_preloadLock) { if (!_preloading.Add(p)) continue; }
            Task.Run(() =>
            {
                try
                {
                    var li = ImageService.LoadDisplay(p, decode);
                    if (li != null) _cache.Set(p, li);   // the cache is thread-safe and the bitmap is frozen
                }
                finally
                {
                    lock (_preloadLock) _preloading.Remove(p);
                }
            });
        }
    }

    private static bool IsFresh(LoadedImage img, string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists && fi.Length == img.FileSize && fi.LastWriteTimeUtc == img.ModifiedUtc;
        }
        catch (IOException) { return false; }
    }

    // ============================================================================================
    // Auto-advance
    // ============================================================================================

    private void RestartTimer()
    {
        _timer.Stop();
        if (!_state.IsTimerActive) return;
        _timer.Interval = TimeSpan.FromSeconds(_state.AutoAdvanceSeconds);
        _timer.Start();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        if (!_state.IsTimerActive) return;
        if (_state.CurrentIndex + 1 >= _state.Files.Count && !_state.Loop)
        {
            _state.SetAutoAdvance(0);   // reached the end: stop the timer
            UpdateInfo();
            return;
        }
        Navigate(() => _state.Jump(1));
    }

    private void SetAutoAdvance(double seconds)
    {
        _state.SetAutoAdvance(seconds);
        RestartTimer();
        UpdateInfo();
    }

    // ============================================================================================
    // Navigation helpers
    // ============================================================================================

    private void Navigate(Func<JumpResult> move)
    {
        _lastNavTicks = Environment.TickCount64;
        var result = move();
        if (result == JumpResult.Blocked) SystemSounds.Beep.Play();
        else if (result == JumpResult.Looped) ShowLoopIndicator();
        // SlideshowState raises CurrentChanged, which triggers the display
    }

    private void ShowLoopIndicator()
    {
        if (_settings.SuppressLoopIndicator) return;
        LoopIndicator.BeginAnimation(OpacityProperty, new DoubleAnimation(0.85, 0, TimeSpan.FromMilliseconds(1100)));
    }

    private void OnViewerClicked(object? sender, int clickCount)
    {
        if (clickCount == 1) Navigate(() => _state.Jump(1));
        else if (clickCount == 2) Close();
    }

    // ============================================================================================
    // Keyboard
    // ============================================================================================

    /// <summary>
    /// Wheel up = previous slide, wheel down = next. The viewer sees the wheel first and keeps it for Ctrl+wheel
    /// zoom and for panning a zoomed-in picture; only what it leaves unhandled changes the slide.
    /// </summary>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e.Handled) return;
        int steps = _wheel.Add(e.Delta);
        if (steps != 0) Navigate(() => _state.Jump(steps));
        e.Handled = true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled) return;
        if (HandleKey(e)) e.Handled = true;
    }

    private bool HandleKey(KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool shift = (mods & ModifierKeys.Shift) != 0;
        bool alt = (mods & ModifierKeys.Alt) != 0;
        bool ctrl = (mods & ModifierKeys.Control) != 0;
        if (ctrl && !alt && !shift && key is (Key.Left or Key.Right))
        {
            _ = JumpToSiblingFolderAsync(key == Key.Right ? +1 : -1);
            return true;
        }
        if (ctrl) return false;                                  // leave other Ctrl shortcuts alone

        bool pastEnd = _state.IsPastEnd;
        // After the last slide was deleted/finished, only these keys do anything (as in the original);
        // Shift+Space goes back, plain Space is ignored.
        if (pastEnd && key is not (Key.Left or Key.Up or Key.Home or Key.End or Key.PageUp or Key.Q or Key.Escape
                                   or Key.OemPeriod or Key.Decimal or Key.H or Key.OemQuestion or Key.Space))
            return false;
        if (pastEnd && key == Key.Space && !shift) return true;

        // ---- digits: auto-advance timer / keypad panning ---------------------------------------
        int digit = key switch
        {
            >= Key.D0 and <= Key.D9 => key - Key.D0,
            >= Key.NumPad0 and <= Key.NumPad9 => key - Key.NumPad0,
            _ => -1,
        };
        if (digit >= 0)
        {
            bool keypad = key >= Key.NumPad0 && key <= Key.NumPad9;
            if (keypad && digit != 0 && Viewer.Viewport.CanPan) { KeypadPan(digit); return true; }
            if (digit == 0) { SetAutoAdvance(0); return true; }
            if (shift && key == Key.D1) { SetAutoAdvance(0.5); return true; }   // '!'
            if (shift && key == Key.D2) { SetAutoAdvance(1.5); return true; }   // '@'
            if (shift && key == Key.D8) { ResetCurrentView(); return true; }    // '*'
            if (shift) return false;
            // Starting the timer from "off" also advances once, so the first interval isn't wasted.
            if (_state.AutoAdvanceSeconds == 0 || _state.Paused) Navigate(() => _state.Jump(1));
            SetAutoAdvance(digit);
            return true;
        }

        switch (key)
        {
            case Key.Escape:
            case Key.Q:
            case Key.OemPeriod:          // '.'
            case Key.Decimal:            // keypad '.'
                Close();
                return true;

            case Key.S:
                ToggleRandom();
                return true;

            case Key.Space:
                if (shift) { Navigate(() => _state.Jump(-1)); return true; }
                if (_state.IsTimerActive) { _state.Pause(); RestartTimer(); UpdateInfo(); return true; }
                Navigate(() => _state.Jump(1, alt));
                return true;
            case Key.Right:
            case Key.Down:
                Navigate(() => _state.Jump(1, alt));
                return true;
            case Key.Left:
            case Key.Up:
                Navigate(() => _state.Jump(-1, alt));
                return true;
            case Key.Home:
                Navigate(() => _state.CurrentIndex > 0 ? _state.Jump(-_state.CurrentIndex) : JumpResult.Blocked);
                return true;
            case Key.End:
                Navigate(() => _state.JumpTo(_state.Files.Count - 1));
                return true;
            case Key.PageUp:
                Navigate(() => _state.Jump(-10));
                return true;
            case Key.PageDown:
                Navigate(() => _state.Jump(10));
                return true;

            case Key.OemPlus:                 // '=' unshifted, '+' shifted (US layout)
                if (shift) ZoomCommand(Viewer.ZoomIn); else ActualSizeCommand();
                return true;
            case Key.Add:
                ZoomCommand(Viewer.ZoomIn);
                return true;
            case Key.OemMinus:
            case Key.Subtract:
                ZoomCommand(Viewer.ZoomOut);
                return true;
            case Key.Multiply:
                ResetCurrentView();
                return true;

            case Key.L:
                RotateCommand(-90);
                return true;
            case Key.R:
                RotateCommand(+90);
                return true;
            case Key.F:
                FlipCommand();
                return true;

            case Key.I:
                if (shift) ToggleMoreExif(); else CycleInfo();
                return true;
            case Key.P:
                _settings.SlideshowShowPath = !_settings.SlideshowShowPath;
                if (_settings.SlideshowInfoMode == 0) _settings.SlideshowInfoMode = 1;
                UpdateInfo();
                return true;
            case Key.H:
            case Key.OemQuestion:
                HelpPanel.Visibility = HelpPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                return true;
            case Key.Delete:
                DeleteCurrent();
                return true;
        }
        return false;
    }

    private void KeypadPan(int digit)
    {
        if (digit == 5) { SystemSounds.Beep.Play(); return; }
        int col = (digit - 1) % 3, row = (digit - 1) / 3;            // 1-3 bottom row, 7-9 top row
        double w = Viewer.Viewport.ViewWidth / 2, h = Viewer.Viewport.ViewHeight / 2;
        double dx = col == 0 ? +w : col == 2 ? -w : 0;               // left keys reveal the left, so the picture moves right
        double dy = row == 2 ? +h : row == 0 ? -h : 0;               // top keys reveal the top, so the picture moves down
        Viewer.PanBy(dx, dy);
        SaveViewState();
    }

    // ============================================================================================
    // Zoom / rotate / flip (remembered per slide)
    // ============================================================================================

    private void ZoomCommand(Func<bool> zoom)
    {
        if (_current == null) { SystemSounds.Beep.Play(); return; }
        if (!zoom()) SystemSounds.Beep.Play();
        OnUserChangedView();
    }

    private void ActualSizeCommand()
    {
        if (_current == null) { SystemSounds.Beep.Play(); return; }
        Viewer.ZoomActualSize();
        OnUserChangedView();
    }

    private void OnUserChangedView()
    {
        SaveViewState();
        EnsureFullResolutionIfNeeded();
        UpdateInfo();
    }

    private void SaveViewState()
    {
        if (_state.CurrentFile is not { } path) return;
        var vs = _state.GetViewState(path);
        vs.Orientation = Viewer.Orientation;
        vs.Zoom = Viewer.Viewport.GetZoomInfo();
    }

    private void RotateCommand(int degreesCw)
    {
        if (_current == null) { SystemSounds.Beep.Play(); return; }
        Viewer.Orientation = Viewer.Orientation.RotatedBy(degreesCw);
        SaveViewState();
        UpdateInfo();
    }

    private void FlipCommand()
    {
        if (_current == null) { SystemSounds.Beep.Play(); return; }
        Viewer.Orientation = Viewer.Orientation.FlippedDisplay();
        SaveViewState();
        UpdateInfo();
    }

    private void ResetCurrentView()
    {
        if (_state.CurrentFile is not { } path || _current == null) return;
        _state.ResetViewState(path);
        ShowLoaded(path, _current);     // back to the EXIF orientation, fit-to-window
    }

    // ============================================================================================
    // Info overlays
    // ============================================================================================

    private void CycleInfo()
    {
        // none -> file info -> file info + EXIF -> none
        _settings.SlideshowInfoMode = (_settings.SlideshowInfoMode + 1) % 3;
        UpdateInfo();
    }

    private void ToggleMoreExif()
    {
        if (_settings.SlideshowInfoMode != 2) { _settings.SlideshowInfoMode = 2; _moreExif = true; }
        else _moreExif = !_moreExif;
        UpdateInfo();
    }

    private void UpdateInfo()
    {
        int mode = _settings.SlideshowInfoMode;
        InfoPanel.Visibility = mode > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (mode == 0) return;

        string? path = _state.CurrentFile;
        if (path == null)
        {
            InfoText.Text = "No images";
            ExifText.Visibility = Visibility.Collapsed;
            return;
        }

        var sb = new StringBuilder();
        sb.Append(_state.CurrentIndex + 1).Append('/').Append(_state.Files.Count).Append("   ")
          .Append(_settings.SlideshowShowPath ? path : Path.GetFileName(path)).Append('\n');

        if (_current != null)
        {
            sb.Append(_current.PixelWidth).Append(" \u00D7 ").Append(_current.PixelHeight)
              .Append("   ").Append(FileSizeFormatter.Format((ulong)_current.FileSize))
              .Append("   ").Append((Viewer.Viewport.Scale * 100).ToString("0.#")).Append('%');
            var o = Viewer.Orientation;
            if (o != ImageOrientation.Identity)
                sb.Append("   rotated ").Append(o.RotationCw).Append('\u00B0').Append(o.Flipped ? ", flipped" : "");
            sb.Append('\n');
        }

        var flags = new List<string>();
        if (_state.RandomMode) flags.Add("random");
        if (_state.Loop) flags.Add("loop");
        if (_state.AutoAdvanceSeconds > 0)
            flags.Add($"auto {_state.AutoAdvanceSeconds:0.#}s" + (_state.Paused ? " (paused)" : ""));
        if (flags.Count > 0) sb.Append('[').Append(string.Join("] [", flags)).Append(']');
        InfoText.Text = sb.ToString().TrimEnd();

        if (mode == 2)
        {
            string text = ExifReader.Read(path)?.ToDisplayString(_moreExif) ?? "";
            ExifText.Text = text.Length > 0 ? text : "(no EXIF data)";
            ExifText.Visibility = Visibility.Visible;
        }
        else
        {
            ExifText.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowMessage(string text)
    {
        MessageText.Text = text;
        MessageText.Visibility = Visibility.Visible;
    }

    // ============================================================================================
    // Next / previous folder (Ctrl+Right / Ctrl+Left)
    // ============================================================================================

    /// <summary>
    /// The folder that "next/previous folder" is relative to: the folder the slideshow started from (or last
    /// jumped to) while the slide on screen is inside it, otherwise the slide's own folder. See
    /// <see cref="FolderNavigator.ResolveReferenceFolder"/>.
    /// </summary>
    private string? CurrentFolderForJump()
    {
        string? folder = FolderNavigator.ResolveReferenceFolder(_jumpAnchor, _state.CurrentFile ?? LastShownPath, out var anchor);
        _jumpAnchor = anchor;
        return folder;
    }

    private async Task JumpToSiblingFolderAsync(int direction)
    {
        if (_folderJumpBusy || _closed) return;        // ignore key repeat while a folder is still loading
        string? folder = CurrentFolderForJump();
        if (folder == null) { SystemSounds.Beep.Play(); return; }

        _folderJumpBusy = true;
        ShowToast(direction > 0 ? "Next folder\u2026" : "Previous folder\u2026", 10);

        var scanner = _scanner;
        var order = _sortOrder;
        bool subfolders = _includeSubfolders;
        SiblingFolder? target = null;
        bool failed = false;
        try
        {
            // Listing and sorting a big folder can take a moment, so keep it off the UI thread. The comparer is
            // created here (with its own metadata cache) rather than shared with the browser window.
            target = await Task.Run(() => FolderNavigator.FindSibling(folder, direction, scanner, FileComparers.For(order), subfolders));
        }
        catch (Exception)
        {
            failed = true;
        }
        finally
        {
            _folderJumpBusy = false;
        }
        if (_closed) return;

        if (target == null)
        {
            SystemSounds.Beep.Play();
            ShowToast(failed ? "Couldn't read that folder" : direction > 0 ? "No next folder" : "No previous folder");
            return;
        }

        _jumpAnchor = target.Path;
        // Keep a shuffled slideshow shuffled; the new folder starts on its first image and is then shuffled.
        _state.Load(target.Files, 0, _state.RandomMode);     // raises CurrentChanged -> shows the first slide
        ShowToast($"{Path.GetFileName(target.Path)}   ({target.Position} of {target.Total})");
        FolderJumped?.Invoke(target.Path);
    }

    /// <summary>
    /// 'S': toggles random order for the running slideshow. The slide on screen never changes. Turning it off
    /// carries on in sort order from this slide; turning it on keeps this slide first and reshuffles the rest.
    /// (Only this slideshow is affected; the "Random" checkbox in the browser is left alone.)
    /// </summary>
    private void ToggleRandom()
    {
        if (_state.Files.Count == 0) { SystemSounds.Beep.Play(); return; }
        bool on = !_state.RandomMode;
        _state.SetRandom(on);
        ShowToast(on ? "Random order: ON  (reshuffled)" : "Random order: OFF  (continuing in sort order)");
        UpdateInfo();
    }

    private void ShowToast(string text, double seconds = 2.4)
    {
        ToastText.Text = text;
        ToastPanel.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(seconds);
        _toastTimer.Start();
    }

    // ============================================================================================
    // Delete
    // ============================================================================================

    private void DeleteCurrent()
    {
        if (_state.CurrentFile is not { } path) return;
        if (!RecycleBin.TryDelete(path, out string? error))
        {
            ShowMessage("Couldn't move to the Recycle Bin:\n" + error);
            return;
        }
        _cache.Remove(path);
        _state.RemoveFile(path);    // raises CurrentChanged if that was the slide on screen
    }
}
