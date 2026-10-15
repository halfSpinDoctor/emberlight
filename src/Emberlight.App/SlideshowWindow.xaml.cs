using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Emberlight.App.Controls;
using Emberlight.App.Services;
using Emberlight.Core;
using Cmd = Emberlight.Core.SlideshowCommand;

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
    private GifPlayer? _gif;                           // the animated GIF on screen, if any
    private readonly DispatcherTimer _timer = new();
    private readonly LruCache<string, LoadedImage> _cache = new(5, comparer: PathUtil.Comparer);
    private readonly HashSet<string> _preloading = new(PathUtil.Comparer);
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
    private readonly SlideshowOverlay _overlay;

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
        _overlay = new SlideshowOverlay(settings);
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
        Closed += (_, _) => { _closed = true; _timer.Stop(); _toastTimer.Stop(); StopGif(); };
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
                StopGif();
                Viewer.Clear();
                _current = null;
                ShowMessage(_state.Files.Count == 0 ? "No images" : "No more images\n(press \u2190 or Esc)");
                UpdateInfo();
                return;
            }

            string path = _state.CurrentFile!;
            LastShownPath = path;

            if (!_cache.TryGet(path, out var img) || !img.Info.MatchesFile(path))
            {
                int decode = DecodeTarget();
                img = await Task.Run(() => version != _displayVersion ? null : ImageService.LoadDisplay(path, decode, () => version != _displayVersion, _settings.SlideshowPlayGifs));
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

    private void StopGif()
    {
        _gif?.Dispose();
        _gif = null;
    }

    private void ShowLoaded(string path, LoadedImage? img)
    {
        StopGif();
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
            ?? (_settings.AutoRotateByOrientationTag ? img.Info.ExifOrientation : ImageOrientation.Identity);
        FitWindowToImage(img, orientation);
        Viewer.SetImage(img.Bitmap, img.Info.PixelWidth, img.Info.PixelHeight, orientation, saved?.Zoom);
        if (img.Info.Animation is { } animation)
        {
            _gif = new GifPlayer(animation);
            Viewer.ReplaceBitmap(_gif.Bitmap);     // same size as the first frame, so the view stays as it is
            _gif.Start();
        }

        if (!_fullScreen) Title = Path.GetFileName(path) + " \u2014 Emberlight";
        UpdateInfo();
    }

    /// <summary>Windowed slideshows can resize themselves to each picture (preference), keeping the window centred.</summary>
    private void FitWindowToImage(LoadedImage img, ImageOrientation orientation)
    {
        if (_fullScreen || !_settings.SlideshowWindowFitToImage || WindowState != WindowState.Normal || !IsLoaded) return;

        var work = SystemParameters.WorkArea;                                    // device-independent units
        var fit = WindowFit.FitToImage(new WindowBounds(Left, Top, ActualWidth, ActualHeight),
            (ActualWidth - Viewer.ActualWidth, ActualHeight - Viewer.ActualHeight), (work.Width, work.Height),
            img.Info.PixelWidth, img.Info.PixelHeight, orientation, VisualTreeHelper.GetDpi(this).DpiScaleX);
        Left = fit.Left;
        Top = fit.Top;
        Width = fit.Width;
        Height = fit.Height;
    }

    /// <summary>
    /// Slides are first decoded at about screen size (fast, light on memory). If the user zooms past what
    /// that bitmap can show sharply, the full-resolution decode replaces it in the background.
    /// </summary>
    private void EnsureFullResolutionIfNeeded()
    {
        var img = _current;
        string? path = _state.CurrentFile;
        if (img == null || img.Info.IsFullResolution || path == null) return;
        if (!DecodePolicy.NeedsFullResolution(img.Bitmap.PixelWidth, img.Bitmap.PixelHeight, img.Info.PixelWidth, img.Info.PixelHeight,
                Viewer.Viewport.Scale)) return;                 // already sharp enough for this zoom level
        _ = LoadFullResolutionAsync(path, _displayVersion);
    }

    private async Task LoadFullResolutionAsync(string path, int version)
    {
        try
        {
            var full = await Task.Run(() => ImageService.LoadDisplay(path, 0, animateGifs: _settings.SlideshowPlayGifs));
            if (full == null) return;
            _cache.Set(path, full);
            if (version != _displayVersion || !string.Equals(_state.CurrentFile, path, PathUtil.Comparison)) return;
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
        => DecodePolicy.SlideDecodeTarget(_settings.SlideshowActualSize, Viewer.Viewport.ViewWidth, Viewer.Viewport.ViewHeight);

    private void Preload()
    {
        // While the user is holding down an arrow key, don't burn CPU on slides they'll skip.
        if (!SlidePreload.IsSettled(Environment.TickCount64, _lastNavTicks)) return;
        int decode = DecodeTarget();
        foreach (string p in SlidePreload.Candidates(_state.Files, _state.CurrentIndex))
        {
            if (_cache.ContainsKey(p)) continue;
            lock (_preloadLock) { if (!_preloading.Add(p)) continue; }
            Task.Run(() =>
            {
                try
                {
                    var li = ImageService.LoadDisplay(p, decode, animateGifs: _settings.SlideshowPlayGifs);
                    if (li != null) _cache.Set(p, li);   // the cache is thread-safe and the bitmap is frozen
                }
                finally
                {
                    lock (_preloadLock) _preloading.Remove(p);
                }
            });
        }
    }


    // ============================================================================================
    // Auto-advance
    // ============================================================================================

    private void RestartTimer()
    {
        _timer.Stop();
        if (!_state.IsTimerActive) return;
        // An animated GIF gets to play through at least once before the slideshow moves on (a preference).
        _timer.Interval = _state.AutoAdvanceInterval(_settings.SlideshowGifsFinish ? _current?.Info.Animation?.Duration : null);
        _timer.Start();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        if (!_state.IsTimerActive) return;
        if (!_state.ContinueAutoAdvance())
        {
            UpdateInfo();               // reached the end: the timer is off now
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

    /// <summary>Runs a move (null = it didn't move, e.g. Space paused), with a beep when blocked or the loop indicator.</summary>
    private JumpResult? Navigate(Func<JumpResult?> move)
    {
        _lastNavTicks = Environment.TickCount64;   // before moving: a cached slide is displayed (and preloads) synchronously
        var result = move();
        if (result == JumpResult.Blocked) SystemSounds.Beep.Play();
        else if (result == JumpResult.Looped) ShowLoopIndicator();
        // SlideshowState raises CurrentChanged, which triggers the display
        return result;
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
        var input = MapKey(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers);
        if (input.Command == Cmd.None) return false;
        // After the last slide was deleted/finished, only some commands do anything (as in the original).
        if (_state.IsPastEnd)
        {
            switch (SlideshowCommands.WhenPastEnd(input.Command))
            {
                case PastEndRule.Ignore: return false;
                case PastEndRule.Swallow: return true;
            }
        }
        Execute(input);
        return true;
    }

    /// <summary>The slideshow's keys (US layout; see <see cref="HelpContent"/>). None for keys it doesn't use.</summary>
    private SlideshowInput MapKey(Key key, ModifierKeys mods)
    {
        bool shift = (mods & ModifierKeys.Shift) != 0;
        bool alt = (mods & ModifierKeys.Alt) != 0;
        bool ctrl = (mods & ModifierKeys.Control) != 0;
        if (ctrl)                                                // leave other Ctrl shortcuts alone
            return !alt && !shift && key is (Key.Left or Key.Right)
                ? new(key == Key.Right ? Cmd.NextFolder : Cmd.PreviousFolder)
                : default;

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
            if (keypad && digit != 0 && Viewer.Viewport.CanPan) return new(Cmd.KeypadPan, digit);
            if (digit == 0) return new(Cmd.SetAutoAdvance, 0);
            if (shift && key == Key.D1) return new(Cmd.SetAutoAdvance, 0.5);   // '!'
            if (shift && key == Key.D2) return new(Cmd.SetAutoAdvance, 1.5);   // '@'
            if (shift && key == Key.D8) return new(Cmd.ResetView);             // '*'
            if (shift) return default;
            return new(Cmd.StartAutoAdvance, digit);
        }

        return key switch
        {
            Key.Escape or Key.Q or Key.OemPeriod or Key.Decimal => new(Cmd.Quit),   // '.' and keypad '.'
            Key.S => new(Cmd.ToggleRandom),
            Key.Space => shift ? new(Cmd.Previous) : new(Cmd.PauseOrNext, InOrder: alt),
            Key.Right or Key.Down => new(Cmd.Next, InOrder: alt),
            Key.Left or Key.Up => new(Cmd.Previous, InOrder: alt),
            Key.Home => new(Cmd.First),
            Key.End => new(Cmd.Last),
            Key.PageUp => new(Cmd.BackPage),
            Key.PageDown => new(Cmd.ForwardPage),
            Key.OemPlus => shift ? new(Cmd.ZoomIn) : new(Cmd.ActualSize),       // '=' unshifted, '+' shifted (US layout)
            Key.Add => new(Cmd.ZoomIn),
            Key.OemMinus or Key.Subtract => new(Cmd.ZoomOut),
            Key.Multiply => new(Cmd.ResetView),
            Key.L => new(Cmd.RotateLeft),
            Key.R => new(Cmd.RotateRight),
            Key.F => new(Cmd.Flip),
            Key.I => shift ? new(Cmd.MoreExif) : new(Cmd.CycleInfo),
            Key.P => new(Cmd.TogglePath),
            Key.H or Key.OemQuestion => new(Cmd.Help),
            Key.Delete => new(Cmd.Delete),
            _ => default,
        };
    }

    private void Execute(SlideshowInput input)
    {
        switch (input.Command)
        {
            case Cmd.Quit: Close(); break;
            case Cmd.ToggleRandom: ToggleRandom(); break;

            case Cmd.Next: Navigate(() => _state.Jump(1, input.InOrder)); break;
            case Cmd.Previous: Navigate(() => _state.Jump(-1, input.InOrder)); break;
            case Cmd.PauseOrNext:
                if (Navigate(() => _state.PauseOrNext(input.InOrder)) == null) { RestartTimer(); UpdateInfo(); }   // paused
                break;
            case Cmd.First: Navigate(() => _state.JumpToFirst()); break;
            case Cmd.Last: Navigate(() => _state.JumpToLast()); break;
            case Cmd.BackPage: Navigate(() => _state.Jump(-SlideshowCommands.PageSize)); break;
            case Cmd.ForwardPage: Navigate(() => _state.Jump(SlideshowCommands.PageSize)); break;
            case Cmd.NextFolder: _ = JumpToSiblingFolderAsync(+1); break;
            case Cmd.PreviousFolder: _ = JumpToSiblingFolderAsync(-1); break;

            case Cmd.StartAutoAdvance:
                Navigate(() => _state.StartAutoAdvance(input.Value));
                RestartTimer();
                UpdateInfo();
                break;
            case Cmd.SetAutoAdvance: SetAutoAdvance(input.Value); break;
            case Cmd.KeypadPan: KeypadPan((int)input.Value); break;

            case Cmd.ZoomIn: ZoomCommand(Viewer.ZoomIn); break;
            case Cmd.ZoomOut: ZoomCommand(Viewer.ZoomOut); break;
            case Cmd.ActualSize: ActualSizeCommand(); break;
            case Cmd.ResetView: ResetCurrentView(); break;
            case Cmd.RotateLeft: RotateCommand(-90); break;
            case Cmd.RotateRight: RotateCommand(+90); break;
            case Cmd.Flip: FlipCommand(); break;

            case Cmd.CycleInfo: _overlay.Cycle(); UpdateInfo(); break;
            case Cmd.MoreExif: _overlay.ToggleMoreExif(); UpdateInfo(); break;
            case Cmd.TogglePath: _overlay.TogglePath(); UpdateInfo(); break;
            case Cmd.Help:
                HelpPanel.Visibility = HelpPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                break;
            case Cmd.Delete: DeleteCurrent(); break;
        }
    }

    private void KeypadPan(int digit)
    {
        if (ViewerInput.KeypadPan(digit, Viewer.Viewport.ViewWidth, Viewer.Viewport.ViewHeight) is not { } pan)
        {
            SystemSounds.Beep.Play();   // 5, as in the original
            return;
        }
        Viewer.PanBy(pan.Dx, pan.Dy);
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

    private void UpdateInfo()
    {
        InfoPanel.Visibility = _overlay.ShowsInfo ? Visibility.Visible : Visibility.Collapsed;
        if (!_overlay.ShowsInfo) return;

        InfoText.Text = DisplayText.SlideshowInfo(_state, _settings.SlideshowShowPath, _current?.Info, Viewer.Viewport.Scale, Viewer.Orientation);
        if (_overlay.ShowsExif && _state.CurrentFile is { } path)
        {
            ExifText.Text = DisplayText.ExifOverlay(path, _overlay.MoreExif);
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
        if (!RecycleBin.Instance.TryDelete(path, out string? error))
        {
            ShowMessage("Couldn't move to the Recycle Bin:\n" + error);
            return;
        }
        _cache.Remove(path);
        _state.RemoveFile(path);    // raises CurrentChanged if that was the slide on screen
    }
}
