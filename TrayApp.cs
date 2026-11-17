using Microsoft.Win32;
using TaskbarPlayer.Native;
using TaskbarPlayer.YouTube;

namespace TaskbarPlayer;

/// <summary>Windowless application: owns the taskbar widget, the media controller and the menus.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private const float WheelVolumeStep = 0.02f;

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly Control _invoker = new();
    private readonly MediaController _media;
    private readonly TaskbarWidget _widget;
    private readonly MessageWindow _messages = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    private ContextMenuStrip _menu = new();
    private readonly YouTubeService _youtube = new();
    private VolumePopup? _volumePopup;
    private bool _youtubeSetupOpen;
    private float _volume;
    private bool _muted;
    private bool _volumeWatched;

    public TrayApp()
    {
        _ = _invoker.Handle; // gives the media controller a UI-thread marshalling target
        Log.Write($"Started: placement {_settings.Placement}, monitor {_settings.Monitor ?? "main"}");
        Strings.Language = _settings.Language;

        _media = new MediaController(_invoker);
        _media.SetPreferredApp(_settings.PreferredSource);

        _widget = new TaskbarWidget(_media, _settings, LayoutCache.Load());
        _widget.PartClicked += OnPartClicked;
        _widget.ContextMenuRequested += p => ShowMenu(_menu, p, MenuLook.AtPointer(TaskbarEdge));
        _widget.WheelScrolled += delta => ChangeVolume(Math.Sign(delta) * WheelVolumeStep);
        _widget.SeekRequested += fraction => RunAsync(async () =>
        {
            await _media.SeekAsync(TimeSpan.FromTicks((long)(_media.Duration.Ticks * (double)fraction)));
            // A refused seek gives the line back; while paused nothing else would draw that.
            _widget.Render();
        });
        // YouTube sources: like / YouTube playlists. With MuSync installed: any track can go to the Garmin.
        _widget.PlaylistButtonVisible = () => _media.HasSession && (_media.IsYouTubeCapableSource || MuSync.IsAvailable);
        _media.Changed += (_, _) =>
        {
            _widget.UpdateLayout(); // the playlist button comes and goes with the source
            _widget.Render();
        };
        _youtube.Load();

        // Explorer restarted: our child window died with the old taskbar.
        _messages.TaskbarCreated += () => _widget.Attach();

        BuildMenu();
        RefreshVolume();
        _widget.Attach();

        // Windows tells of volume changes on threads of its own; a late one may find the app already closing.
        _volumeWatched = SystemVolume.Watch(() =>
        {
            try
            {
                _invoker.BeginInvoke(new Action(RefreshVolume));
            }
            catch (InvalidOperationException)
            {
            }
        });
        _timer.Tick += (_, _) =>
        {
            // Asked for only where Windows would not tell.
            if (!_volumeWatched) RefreshVolume();
            _widget.Tick();
        };
        _timer.Start();

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        _ = InitializeMediaAsync();
        _ = ScanAppsAsync();
    }

    // What the scan finds can add the playlist button; nothing else would lay the widget out again.
    private async Task ScanAppsAsync()
    {
        await MusicApps.RefreshAsync();
        _widget.UpdateLayout();
        _widget.Render();
    }

    private async Task InitializeMediaAsync()
    {
        try
        {
            await _media.InitializeAsync();
        }
        catch (Exception)
        {
            _widget.StatusText = Strings.MediaUnavailable;
            _widget.Render();
        }
    }

    private void OnPartClicked(WidgetPart part, Rectangle screenRect)
    {
        switch (part)
        {
            case WidgetPart.Previous: RunAsync(_media.PreviousAsync); break;
            case WidgetPart.PlayPause:
                // Nothing to play/pause: offer to start a music app instead.
                if (_media.HasSession) RunAsync(_media.PlayPauseAsync);
                else ShowPopupMenu(FillLaunchItems, screenRect);
                break;
            case WidgetPart.Next: RunAsync(_media.NextAsync); break;
            case WidgetPart.Volume: ToggleVolumePopup(screenRect); break;
            case WidgetPart.AddToPlaylist: ShowPlaylistMenu(screenRect); break;
            case WidgetPart.Info:
                // To the app that plays. Which app to control is chosen in the right-click menu; the choice is
                // offered here only when the app cannot be shown.
                if (_media.HasSession && _media.SourceAppId is string source && SourceWindow.Show(source)) break;
                ShowPopupMenu(_media.HasSession ? FillSourceItems : FillLaunchItems, screenRect);
                break;
        }
    }

    private void ShowPopupMenu(Action<ToolStripItemCollection> fill, Rectangle anchor)
    {
        var menu = new ContextMenuStrip();
        fill(menu.Items);
        menu.Closed += (_, _) => _invoker.BeginInvoke(new Action(menu.Dispose));
        ShowMenuBy(menu, anchor);
    }

    private void FillLaunchItems(ToolStripItemCollection items)
    {
        items.Clear();
        foreach (var app in MusicApps.Installed)
        {
            items.Add(new ToolStripMenuItem(Strings.StartApp(app.Name), null, (_, _) => MusicApps.Launch(app)));
        }
        if (!MusicApps.HasYouTubeMusicApp)
        {
            items.Add(new ToolStripMenuItem(Strings.OpenYouTubeMusicInBrowser, null, (_, _) => MusicApps.OpenYouTubeMusicInBrowser()));
        }
        // Pick up apps installed since the last scan, for next time.
        _ = ScanAppsAsync();
    }

    // ---------------------------------------------------------------- YouTube Music playlists

    private void ShowPlaylistMenu(Rectangle anchor)
    {
        // Capture the track now: it may change while the menu is open.
        string? title = _media.Title;
        string? artist = _media.Artist;
        bool musicOnly = _media.SourceName == "YouTube Music";
        bool youtubeSource = _media.IsYouTubeCapableSource;
        if (string.IsNullOrWhiteSpace(title)) return;

        if (youtubeSource && !_youtube.IsConnected && !MuSync.IsAvailable)
        {
            OpenYouTubeSetup();
            return;
        }

        var menu = new ContextMenuStrip();
        ToolStripMenuItem? loading = null;
        if (youtubeSource && _youtube.IsConnected)
        {
            menu.Items.Add(new ToolStripMenuItem(Strings.LikeSong, null, (_, _) =>
                RunYouTube(async () =>
                {
                    if (await FindTrackAsync(title, artist, musicOnly) is not YouTubeVideo video) return;
                    await _youtube.LikeAsync(video.Id);
                    _widget.Flash(Strings.Liked, TimeSpan.FromSeconds(4));
                })));
            menu.Items.Add(new ToolStripSeparator());
            loading = new ToolStripMenuItem(Strings.Loading) { Enabled = false };
            menu.Items.Add(loading);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem(Strings.RefreshPlaylists, null, (_, _) =>
                RunYouTube(() => _youtube.GetPlaylistsAsync(refresh: true))));
        }
        else if (youtubeSource)
        {
            menu.Items.Add(new ToolStripMenuItem(Strings.ConnectYouTube, null, (_, _) => OpenYouTubeSetup()));
        }

        if (MuSync.IsAvailable)
        {
            if (menu.Items.Count > 0) menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem(Strings.MuSyncHeader) { Enabled = false });
            foreach (string playlist in MuSync.GetPlaylists())
            {
                menu.Items.Add(new ToolStripMenuItem("⌚  " + playlist, null, (_, _) =>
                    RunYouTube(() => AddToMuSyncAsync(title, artist, playlist))));
            }
        }

        menu.Closed += (_, _) => _invoker.BeginInvoke(new Action(menu.Dispose));
        ShowMenuBy(menu, anchor);
        if (loading is not null) _ = FillPlaylistsAsync(menu, loading, title, artist, musicOnly);
    }

    private async Task AddToMuSyncAsync(string title, string? artist, string playlist)
    {
        _widget.Flash(Strings.DownloadingForGarmin, TimeSpan.FromMinutes(5));
        var result = await MuSync.AddAsync(title, artist, playlist);
        _widget.Flash(result.Ok ? Strings.AddedToMuSync(playlist) : result.Message ?? "MuSync", TimeSpan.FromSeconds(6));
    }

    private async Task FillPlaylistsAsync(ContextMenuStrip menu, ToolStripMenuItem placeholder, string title, string? artist, bool musicOnly)
    {
        try
        {
            var playlists = await _youtube.GetPlaylistsAsync();
            if (menu.IsDisposed) return;

            int index = menu.Items.IndexOf(placeholder);
            menu.Items.Remove(placeholder);
            if (playlists.Count == 0)
            {
                menu.Items.Insert(index, new ToolStripMenuItem(Strings.NoPlaylists) { Enabled = false });
            }
            foreach (var playlist in playlists)
            {
                menu.Items.Insert(index++, new ToolStripMenuItem(playlist.Title, null, (_, _) =>
                    RunYouTube(async () =>
                    {
                        if (await FindTrackAsync(title, artist, musicOnly) is not YouTubeVideo video) return;
                        await _youtube.AddToPlaylistAsync(playlist.Id, video.Id);
                        _widget.Flash(Strings.AddedTo(playlist.Title), TimeSpan.FromSeconds(4));
                    })));
            }
        }
        catch (Exception ex)
        {
            if (!menu.IsDisposed) placeholder.Text = DescribeError(ex);
        }
    }

    private async Task<YouTubeVideo?> FindTrackAsync(string title, string? artist, bool musicOnly)
    {
        _widget.Flash(Strings.Searching, TimeSpan.FromSeconds(15));
        var video = await _youtube.FindVideoAsync(title, artist, musicOnly);
        if (video is null) _widget.Flash(Strings.NotFoundOnYouTube, TimeSpan.FromSeconds(4));
        else Log.Write($"YouTube match for '{artist} - {title}': {video.Title} [{video.Channel}] {video.Id}");
        return video;
    }

    private async void RunYouTube(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Log.Write($"YouTube action failed: {ex}");
            _widget.Flash(DescribeError(ex), TimeSpan.FromSeconds(6));
        }
    }

    private static string DescribeError(Exception ex) => ex switch
    {
        YouTubeException { IsQuotaExceeded: true } => Strings.QuotaExceeded,
        _ => ex.Message,
    };

    private void OpenYouTubeSetup()
    {
        if (_youtubeSetupOpen) return;
        _youtubeSetupOpen = true;
        try
        {
            using var form = new YouTubeSetupForm(_youtube);
            if (form.ShowDialog() == DialogResult.OK)
            {
                _widget.Flash("✓ YouTube", TimeSpan.FromSeconds(3));
            }
        }
        finally
        {
            _youtubeSetupOpen = false;
        }
    }

    // ---------------------------------------------------------------- volume

    private void RefreshVolume()
    {
        if (SystemVolume.Get() is not (float level, bool muted)) return;
        _volume = level;
        _muted = muted;
        _widget.SetVolume(level, muted);
        _volumePopup?.SetState(level, muted);
    }

    private void ChangeVolume(float delta)
    {
        if (SystemVolume.Get() is not (float level, bool muted)) return;
        if (muted && delta > 0) SystemVolume.SetMuted(false);
        SystemVolume.SetLevel(level + delta);
        RefreshVolume();
    }

    private void ToggleVolumePopup(Rectangle anchor)
    {
        bool light = Glyphs.IsLightTaskbar();
        if (_volumePopup is not null && _volumePopup.IsLight != light)
        {
            _volumePopup.Dispose();
            _volumePopup = null;
        }

        if (_volumePopup is null)
        {
            _volumePopup = new VolumePopup(light);
            _volumePopup.VolumeChanged += level =>
            {
                if (_muted && level > 0) SystemVolume.SetMuted(false);
                SystemVolume.SetLevel(level);
                RefreshVolume();
            };
            _volumePopup.MuteToggled += () =>
            {
                SystemVolume.SetMuted(!_muted);
                RefreshVolume();
            };
        }

        if (_volumePopup.Visible)
        {
            _volumePopup.HideNow();
            return;
        }
        // Clicking the button deactivates (and hides) the popup first; treat that click as "close".
        if ((DateTime.UtcNow - _volumePopup.HiddenAt).TotalMilliseconds < 300) return;

        _volumePopup.SetState(_volume, _muted);
        _volumePopup.ShowBy(anchor, TaskbarEdge, _widget.TrayScreenBounds);
    }

    // ---------------------------------------------------------------- menus

    private void BuildMenu()
    {
        _menu.Dispose();
        _menu = new ContextMenuStrip();

        var source = new ToolStripMenuItem(Strings.Source);
        source.DropDownItems.Add(Strings.Loading);
        source.DropDownOpening += (_, _) => FillSourceItems(source.DropDownItems);

        var placement = new ToolStripMenuItem(Strings.Position);
        var auto = new ToolStripMenuItem(Strings.PositionAuto);
        var left = new ToolStripMenuItem(Strings.PositionLeft);
        var right = new ToolStripMenuItem(Strings.PositionRight);
        auto.Click += (_, _) => SetPlacement(WidgetPlacement.Auto);
        left.Click += (_, _) => SetPlacement(WidgetPlacement.Left);
        right.Click += (_, _) => SetPlacement(WidgetPlacement.Right);
        placement.DropDownItems.AddRange([auto, left, right]);
        placement.DropDownOpening += (_, _) =>
        {
            bool upright = TaskbarEdge is TaskbarEdge.Left or TaskbarEdge.Right;
            left.Text = Strings.PositionBefore(upright);
            right.Text = Strings.PositionAfter(upright);
            auto.Checked = _settings.Placement == WidgetPlacement.Auto;
            right.Checked = _settings.Placement == WidgetPlacement.Right;
            left.Checked = _settings.Placement == WidgetPlacement.Left;
        };

        var monitor = new ToolStripMenuItem(Strings.Monitor);
        monitor.DropDownItems.Add(Strings.Loading);
        monitor.DropDownOpening += (_, _) => FillMonitorItems(monitor.DropDownItems);

        var showCover = new ToolStripMenuItem(Strings.ShowCover) { Checked = _settings.ShowCover, CheckOnClick = true };
        showCover.CheckedChanged += (_, _) =>
        {
            _settings.ShowCover = showCover.Checked;
            SaveAndRelayout();
        };

        var showTitle = new ToolStripMenuItem(Strings.ShowTitle) { Checked = _settings.ShowTitle, CheckOnClick = true };
        showTitle.CheckedChanged += (_, _) =>
        {
            _settings.ShowTitle = showTitle.Checked;
            SaveAndRelayout();
        };

        var autostart = new ToolStripMenuItem(Strings.StartWithWindows) { CheckOnClick = true };
        autostart.CheckedChanged += (_, _) =>
        {
            if (autostart.Checked != Autostart.IsEnabled) Autostart.Set(autostart.Checked);
        };
        _menu.Opening += (_, _) => autostart.Checked = Autostart.IsEnabled;

        // Language names are shown in their own language.
        var youtube = new ToolStripMenuItem();
        youtube.Click += async (_, _) =>
        {
            if (_youtube.IsConnected) await _youtube.DisconnectAsync();
            else OpenYouTubeSetup();
        };
        _menu.Opening += (_, _) => youtube.Text = _youtube.IsConnected ? Strings.DisconnectYouTube : Strings.ConnectYouTube;

        var language = new ToolStripMenuItem(Strings.LanguageMenu);
        foreach (var (lang, name) in new[] { (AppLanguage.English, "English"), (AppLanguage.Dutch, "Nederlands") })
        {
            language.DropDownItems.Add(new ToolStripMenuItem(name, null, (_, _) => SetLanguage(lang)) { Checked = Strings.Language == lang });
        }

        _menu.Items.AddRange(
        [
            source,
            monitor,
            placement,
            new ToolStripSeparator(),
            showCover,
            showTitle,
            autostart,
            youtube,
            language,
            new ToolStripSeparator(),
            new ToolStripMenuItem(Strings.Exit, null, (_, _) => ExitThread()),
        ]);
    }

    private void FillSourceItems(ToolStripItemCollection items)
    {
        items.Clear();

        var auto = new ToolStripMenuItem(Strings.AutomaticSource) { Checked = _settings.PreferredSource is null };
        auto.Click += (_, _) => SelectSource(null);
        items.Add(auto);
        items.Add(new ToolStripSeparator());

        var sources = _media.GetSources();
        if (sources.Count == 0)
        {
            items.Add(new ToolStripMenuItem(Strings.NoActivePlayers) { Enabled = false });
        }
        foreach (var s in sources)
        {
            bool active = string.Equals(s.AppId, _media.SourceAppId, StringComparison.OrdinalIgnoreCase);
            var item = new ToolStripMenuItem(active ? $"{s.Name}  ●" : s.Name)
            {
                Checked = string.Equals(s.AppId, _settings.PreferredSource, StringComparison.OrdinalIgnoreCase),
                ToolTipText = s.AppId,
            };
            item.Click += (_, _) => SelectSource(s.AppId);
            items.Add(item);
        }

        var launch = new ToolStripMenuItem(Strings.StartMusicApp);
        launch.DropDownItems.Add(Strings.Loading);
        launch.DropDownOpening += (_, _) => FillLaunchItems(launch.DropDownItems);
        items.Add(new ToolStripSeparator());
        items.Add(launch);
    }

    private TaskbarEdge TaskbarEdge => _widget.TaskbarEdgeNow(_widget.TrayScreenBounds);

    private void ShowMenuBy(ContextMenuStrip menu, Rectangle part)
    {
        var (at, direction) = MenuLook.Beside(part, TaskbarEdge, _widget.TrayScreenBounds);
        ShowMenu(menu, at, direction);
    }

    private void ShowMenu(ContextMenuStrip menu, Point screenPoint, ToolStripDropDownDirection direction)
    {
        // Menus dismiss on outside clicks only if we own the foreground window.
        NativeMethods.SetForegroundWindow(_messages.Handle);
        MenuLook.Apply(menu, Glyphs.IsLightTaskbar());
        menu.Show(screenPoint, direction);
    }

    private void SelectSource(string? appId)
    {
        _settings.PreferredSource = appId;
        _settings.Save();
        _media.SetPreferredApp(appId);
    }

    private void FillMonitorItems(ToolStripItemCollection items)
    {
        items.Clear();
        var bars = TaskbarWidget.FindTaskbars().OrderBy(b => b.Screen.Bounds.Left).ToList();
        foreach (var (_, screen) in bars)
        {
            string number = new string(screen.DeviceName.Where(char.IsDigit).ToArray());
            string label = Strings.MonitorLabel(number, screen.Bounds.Width, screen.Bounds.Height);
            if (screen.Primary) label += Strings.MainDisplay;

            bool selected = _settings.Monitor is string device
                ? string.Equals(device, screen.DeviceName, StringComparison.OrdinalIgnoreCase)
                : screen.Primary;
            var item = new ToolStripMenuItem(label) { Checked = selected };
            string? target = screen.Primary ? null : screen.DeviceName;
            item.Click += (_, _) =>
            {
                _settings.Monitor = target;
                _settings.Save();
                _widget.Recheck(); // moves the widget to the other taskbar right away
            };
            items.Add(item);
        }

        if (bars.Count <= 1)
        {
            items.Add(new ToolStripSeparator());
            items.Add(new ToolStripMenuItem(Strings.EnableTaskbarOnAllDisplays) { Enabled = false });
        }
    }

    private void SetLanguage(AppLanguage language)
    {
        Strings.Language = language;
        _settings.Language = language;
        _settings.Save();
        BuildMenu(); // menu texts are created once, so rebuild them
        _widget.Render();
    }

    private void SetPlacement(WidgetPlacement placement)
    {
        _settings.Placement = placement;
        SaveAndRelayout();
    }

    private void SaveAndRelayout()
    {
        _settings.Save();
        _widget.UpdateLayout();
        _widget.Render();
    }

    // A display came, went or changed: the chosen taskbar may be another one now.
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => _invoker.BeginInvoke(new Action(_widget.Recheck));

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        // Taskbar settings (icon alignment, the Widgets button) arrive as general ones and can move the icons.
        bool mayMoveIcons = e.Category == UserPreferenceCategory.General;
        // Light/dark switch or DPI change: redraw on the UI thread.
        _invoker.BeginInvoke(new Action(() =>
        {
            if (mayMoveIcons) _widget.Recheck();
            _widget.UpdateLayout();
            _widget.Render();
        }));
    }

    private static async void RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception)
        {
            // Media apps come and go; a failed command is not worth a crash.
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SystemVolume.StopWatching();
            _timer.Dispose();
            _widget.Dispose();
            _media.Dispose();
            _menu.Dispose();
            _volumePopup?.Dispose();
            _youtube.Dispose();
            _messages.DestroyHandle();
            _invoker.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>Hidden top-level window that receives the "TaskbarCreated" broadcast.</summary>
    private sealed class MessageWindow : NativeWindow
    {
        private static readonly int WM_TaskbarCreated = (int)NativeMethods.RegisterWindowMessage("TaskbarCreated");

        public MessageWindow() => CreateHandle(new CreateParams { Caption = "TaskbarPlayer.Messages" });

        public event Action? TaskbarCreated;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_TaskbarCreated) TaskbarCreated?.Invoke();
            base.WndProc(ref m);
        }
    }
}
