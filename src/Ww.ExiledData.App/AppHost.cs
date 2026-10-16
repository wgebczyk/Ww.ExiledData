using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Ww.ExiledData.App.Controls;
using Ww.ExiledData.App.Overlay;
using Ww.ExiledData.App.Services;
using Ww.ExiledData.App.Theme;
using Ww.ExiledData.App.ViewModels;
using Ww.ExiledData.App.Views;
using Ww.ExiledData.Capture;
using Ww.ExiledData.Capture.Windows;
using Ww.ExiledData.Core.Identity;
using Ww.ExiledData.Core.Sessions;
using Ww.ExiledData.Core.Sweeps;
using Ww.ExiledData.Mapping;
using Ww.ExiledData.Storage;

namespace Ww.ExiledData.App;

/// <summary>Composition root: settings, logging, storage, capture, overlay and the main window (session list and session pages).</summary>
public sealed class AppHost : IDisposable
{
    private readonly AppPaths _paths = AppPaths.Default();
    private readonly AppLogging _logging;
    private readonly ILogger _log;
    private readonly SqliteStore _store;
    private readonly SessionController _controller;
    private readonly MappingStore _mappingStore;
    private readonly MappingService _mapping;
    private readonly PriceService _prices;
    /// <summary>Currency codes and icons (catalog) and the loot grid layout (settings).</summary>
    private CurrencyBook _currencies;
    private readonly SweepControlViewModel _sweeps;
    private readonly SessionListViewModel _list;
    private readonly MainWindowViewModel _main;
    private readonly OverlayWindow _overlay = new();
    private readonly GridOverlayWindow _gridOverlay = new();
    private readonly bool _overlayDemo;
    private readonly string? _screenshotDir;
    private AppSettings _settings;
    private readonly HotkeyHints _hotkeyHints;
    private CaptureService? _capture;
    private GridEditorWindow? _editor;
    private MainWindow? _mainWindow;
    private SettingsWindow? _settingsWindow;
    /// <summary>Hotkeys another app held when capture last started (shown on the Hotkeys page).</summary>
    private IReadOnlyList<HotkeyAction> _failedHotkeys = [];
    /// <summary>settings.json was unreadable at start and has not been saved since.</summary>
    private bool _settingsUnreadable;

    public AppHost(string[] args)
    {
        Directory.CreateDirectory(_paths.DataDirectory);
        _settings = SettingsFile.Load(_paths.Settings, out var settingsProblem);
        _hotkeyHints = new HotkeyHints(_settings);
        _logging = new AppLogging(_paths.LogsDirectory, _settings.LogLevel);
        _log = _logging.Factory.CreateLogger<AppHost>();
        _log.LogInformation("Starting Ww.ExiledData {Version} on {OS}; data in {Dir}; settings {@Settings}",
            typeof(AppHost).Assembly.GetName().Version, Environment.OSVersion, _paths.DataDirectory, _settings);
        RegisterGlobalExceptionLogging();

        _settingsUnreadable = settingsProblem is not null;
        if (settingsProblem is not null)
            _log.LogWarning("{Problem}", settingsProblem);
        if (!File.Exists(_paths.Settings))
            SettingsFile.Save(_paths.Settings, _settings); // so the defaults are visible and editable

        _store = SqliteStore.Open(_paths.Database);
        _controller = new SessionController(_store, _settings.PositionTolerance, logger: _logging.Factory.CreateLogger<SessionController>());
        // The mapping module: own tables in the same database, fed by the capture stream (the seam).
        _mappingStore = MappingStore.Open(_paths.Database);
        _mapping = new MappingService(_mappingStore, _controller, logger: _logging.Factory.CreateLogger<MappingService>());
        _prices = new PriceService(_paths.Prices, () => _settings.PriceLeague, _logging.Factory.CreateLogger<PriceService>());
        _currencies = CurrencyBook.Default.WithLayout(_settings.ParsedLootGrid()).WithPrices(_prices.Prices).WithUnit(_settings.PriceUnit);
        CurrencyIcons.Directory = _paths.IconsDirectory;
        _sweeps = new SweepControlViewModel(_controller);
        _list = new SessionListViewModel(_controller, _logging.Factory.CreateLogger<SessionListViewModel>())
        {
            ToggleTheme = ToggleTheme,
            LoadMappingSummaries = _mapping.ListSummaries,
            KnownTags = async () => (await _mapping.KnownTags()).Select(t => t.ToString()).ToList(),
            AddSessionTag = (id, text) => _mapping.AddTag(id, id, text),
        };
        // The window wires each session page's dialogs; pages are created only once the window exists.
        _main = new MainWindowViewModel(_list, _controller, id => _mainWindow!.Attach(
            new SessionViewModel(_controller, _sweeps, id, _logging.Factory.CreateLogger<SessionViewModel>(), _mapping, _currencies, _hotkeyHints)))
        {
            OpenSettings = EditSettings,
            RefreshPrices = () => _prices.RefreshIfNeeded(force: true),
        };
        _prices.Changed += OnPricesChanged;
        _main.ShowPrices(_prices);
        ApplyTheme();
        if (Application.Current is { } app)
            app.ActualThemeVariantChanged += (_, _) => UpdateThemeToggleText(); // System theme: the OS switched
        if (settingsProblem is not null)
            SetStatus(settingsProblem);

        ApplyOverlaySettings();
        _controller.FeedbackRaised += feedback => Dispatcher.UIThread.Post(() =>
        {
            _overlay.ShowResult(feedback);
            SetStatus(feedback.Message);
        });
        _controller.StateChanged += active => Dispatcher.UIThread.Post(() => UpdateGridOverlay(active?.Sweep));
        _controller.SweepHotkeyPressed += press => Dispatcher.UIThread.Post(() => OnSweepHotkey(press));
        _controller.MappingHotkeyPressed += press => Dispatcher.UIThread.Post(() => _ = OnMappingHotkey(press));
        _sweeps.BeginSweep = kind => BeginSweepAsync(kind, cursor: null);
        _sweeps.SetGridKinds(_settings.ParsedGridKinds(), _settings.LastGridKind);
        _sweeps.GridKindChosen = RememberGridKind;
        _overlayDemo = args.Contains("--overlay-demo");
        if (Array.IndexOf(args, "--screenshot") is var at and >= 0 && at + 1 < args.Length)
            _screenshotDir = args[at + 1];
    }

    public Window CreateMainWindow()
    {
        _mainWindow = new MainWindow { DataContext = _main };
        _mainWindow.Attach(_list);
        return _mainWindow;
    }

    public void Start()
    {
        if (_screenshotDir is null)
        {
            _overlay.Show();
            StartCapture();
        }
        _ = InitializeAsync();
    }

    public void Dispose()
    {
        _capture?.Dispose();
        _mappingStore.Dispose();
        _store.Dispose();
        _log.LogInformation("Exit");
        _logging.Dispose();
    }

    private async Task InitializeAsync()
    {
        try
        {
            var recovered = await _controller.RecoverInterrupted();
            await _mapping.RecoverInterrupted();
            if (recovered.Count > 0)
                SetStatus($"Closed {recovered.Count} session(s) left open by the previous run (marked interrupted).");
            await Task.WhenAll(_list.RefreshAsync(), _sweeps.Refresh());
            if (_screenshotDir is null)
            {
                new DispatcherTimer(TimeSpan.FromMinutes(5), DispatcherPriority.Background, (_, _) => _ = _prices.RefreshIfNeeded()).Start();
                _ = _prices.RefreshIfNeeded();
            }
            if (_overlayDemo)
                RunOverlayDemo();
            if (_screenshotDir is { } dir)
                await RunScreenshots(dir);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Startup failed");
            SetStatus($"Startup failed: {ex.Message}");
        }
    }

    private void StartCapture()
    {
        if (!OperatingSystem.IsWindows())
        {
            SetStatus("Item capture is only available on Windows.");
            _log.LogWarning("Capture not started: not running on Windows");
            return;
        }

        try
        {
            var capture = WindowsCapture.Create(_settings.ToCaptureOptions(), _controller, _logging.Factory);
            capture.CopyPressed += press => Dispatcher.UIThread.Post(() =>
            {
                if (_gridOverlay.IsVisible)
                    _gridOverlay.BringToTop(); // below the feedback circle, which raises itself next
                _overlay.ShowPress(press.Position);
            });
            capture.Error += ex => Dispatcher.UIThread.Post(() => SetStatus($"Capture error: {ex.Message}"));
            capture.GridKeyPressed += key => Dispatcher.UIThread.Post(() => _editor?.HandleKey(key));
            capture.GridMousePressed += press => Dispatcher.UIThread.Post(() => _editor?.HandleMouse(press));
            var failed = capture.Start();
            _failedHotkeys = failed;
            _capture = capture;
            capture.GridEditing = _editor is not null;
            _controller.UseQueue(capture.Enqueue);
            _mapping.UseQueue(capture.Enqueue);

            _sweeps.HotkeyHint = $"New {_settings.HotkeyNewSession} · Undo {_settings.HotkeyUndo} · Redo {_settings.HotkeyRedo}"
                             + $" · Sweep {_settings.HotkeySweepStart} / finish {_settings.HotkeySweepFinish} / skip {_settings.HotkeySkipCell}";
            if (failed.Count > 0)
                SetStatus($"Hotkey(s) already in use by another app: {string.Join(", ", failed)}. Change them in Settings.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Capture failed to start");
            SetStatus($"Capture failed to start: {ex.Message}");
        }
    }

    private void RestartCapture()
    {
        _log.LogInformation("Restarting capture with new settings");
        var old = _capture;
        _capture = null;
        old?.Dispose(); // drains anything already queued
        _controller.UseQueue(null);
        _mapping.UseQueue(null);
        StartCapture();
    }

    /// <summary>The Settings dialog (one at a time), modal over the main window.</summary>
    private async Task EditSettings()
    {
        if (_mainWindow is not { } owner)
            return;
        if (_settingsWindow is { } open)
        {
            open.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(CreateSettingsViewModel());
        try
        {
            await _settingsWindow.ShowDialog(owner);
        }
        finally
        {
            _settingsWindow = null;
            _capture?.HotkeysSuspended = false;
        }
    }

    private SettingsViewModel CreateSettingsViewModel() =>
        new(_settings, new SettingsEnvironment(_paths.DataDirectory, _failedHotkeys, _settingsUnreadable, _currencies))
        {
            PreviewTheme = theme =>
            {
                ThemeSwitcher.Apply(theme);
                UpdateThemeToggleText();
            },
            SuspendHotkeys = on => _capture?.HotkeysSuspended = on,
            OpenLogs = OpenLogsFolder,
            Apply = ApplySettings,
        };

    /// <summary>Saves and applies edited settings; returns the error, or null. A failed save changes nothing.</summary>
    private async Task<string?> ApplySettings(AppSettings updated)
    {
        // The area picker may have written LastGridKind while the dialog was open.
        updated = updated with { LastGridKind = _settings.LastGridKind };
        try
        {
            SettingsFile.Save(_paths.Settings, updated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogError(ex, "Saving settings failed");
            return ex.Message;
        }

        var previous = _settings;
        _settings = updated;
        _hotkeyHints.Update(updated);
        _settingsUnreadable = false;
        try
        {
            _log.LogInformation("Settings changed: {@Settings}", updated);
            _logging.SetLevel(updated.LogLevel);
            ApplyTheme();
            await _controller.SetPositionTolerance(updated.PositionTolerance);
            ApplyOverlaySettings();
            _sweeps.SetGridKinds(updated.ParsedGridKinds(), updated.LastGridKind);
            if (!updated.ParsedLootGrid().SameAs(_currencies.Layout) || updated.PriceUnit != _currencies.Unit)
            {
                _currencies = _currencies.WithLayout(updated.ParsedLootGrid()).WithUnit(updated.PriceUnit);
                foreach (var session in _main.Sessions)
                    session.SetCurrencies(_currencies);
            }

            if (updated.PriceLeague != previous.PriceLeague)
                _ = _prices.RefreshIfNeeded();

            var restart = updated.ToCaptureOptions() is var o && previous.ToCaptureOptions() is var p
                && (o.ClipboardTimeout != p.ClipboardTimeout || o.GameWindowTitle != p.GameWindowTitle
                    || !o.Hotkeys.OrderBy(h => h.Key).SequenceEqual(p.Hotkeys.OrderBy(h => h.Key)));
            if (restart)
                RestartCapture();
            // After a restart, a hotkey another app holds is reported instead (StartCapture).
            if (!restart || _failedHotkeys.Count == 0)
                SetStatus("Settings saved");
            return null;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Applying settings failed");
            SetStatus($"Applying settings failed: {ex.Message}");
            return ex.Message;
        }
    }

    /// <summary>A pull finished or started: the app bar chip follows; new prices go to the open session pages.</summary>
    private void OnPricesChanged()
    {
        _main.ShowPrices(_prices);
        if (ReferenceEquals(_prices.Prices, _currencies.Prices))
            return;
        _currencies = _currencies.WithPrices(_prices.Prices);
        foreach (var session in _main.Sessions)
            session.SetCurrencies(_currencies);
    }

    private void OpenLogsFolder()
    {
        try
        {
            Directory.CreateDirectory(_paths.LogsDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_paths.LogsDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Opening the logs folder failed");
            SetStatus($"Opening the logs folder failed: {ex.Message}");
        }
    }

    // --- windows and theme ----------------------------------------------------------------------

    /// <summary>
    /// Status for the session list and the live session pages (also called from the global exception
    /// handler, possibly early).
    /// </summary>
    private void SetStatus(string text)
    {
        _list?.StatusText = text;
        foreach (var session in _main?.Sessions ?? [])
            session.StatusText = text;
    }

    private void ApplyTheme()
    {
        ThemeSwitcher.Apply(_settings.Theme);
        UpdateThemeToggleText();
    }

    private void UpdateThemeToggleText() =>
        _list.ThemeToggleText = ThemeSwitcher.Toggled() == AppTheme.Light ? "Light theme" : "Dark theme";

    /// <summary>Session list quick toggle: flips what is shown now and remembers it.</summary>
    private void ToggleTheme()
    {
        _settings = _settings with { Theme = ThemeSwitcher.Toggled() };
        ApplyTheme();
        try
        {
            SettingsFile.Save(_paths.Settings, _settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not save the theme");
        }
    }

    // --- sweeps ---------------------------------------------------------------------------------

    /// <summary>
    /// Starts a sweep on the game's screen (else the one under <paramref name="cursor"/>, else the main window's). With a
    /// saved calibration for that exact screen it starts copying at once (the start hotkey repositions it while still
    /// empty); otherwise it shows the grid editor first and, on confirm, saves the calibration and starts the sweep.
    /// </summary>
    private async Task BeginSweepAsync(GridKind kind, ScreenPoint? cursor)
    {
        if (_editor is not null)
        {
            _editor.BringToTop();
            return;
        }

        try
        {
            // The game's screen first: "Start sweep…" is clicked in the app, which may be on another monitor.
            var screens = _overlay.Screens;
            var game = OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(_settings.GameWindowTitle)
                ? OverlayInterop.FindWindowCenter(_settings.GameWindowTitle)
                : null;
            var screen = (game is { } g ? screens.ScreenFromPoint(new PixelPoint(g.X, g.Y)) : null)
                         ?? (cursor is { } c ? screens.ScreenFromPoint(new PixelPoint(c.X, c.Y)) : null)
                         ?? (_mainWindow is { } w ? screens.ScreenFromPoint(w.Position) : null)
                         ?? screens.Primary;
            _log.LogInformation("Sweep screen: game window center {Game}, cursor {Cursor}", game, cursor);
            if (screen is null)
            {
                SetStatus("No screen found for the sweep grid");
                return;
            }

            var bounds = screen.Bounds;
            var (start, warning, saved) = await StartingGrid(kind, bounds);
            if (saved)
            {
                _log.LogInformation("Sweep for {Kind} starts on the saved grid, screen {Bounds}", kind, bounds);
                await _controller.StartSweep(kind, start);
                return;
            }
            if ((await _controller.GetActive())?.Sweep is { } running)
                warning = Join(warning, $"Starting finishes the running sweep '{running.Label}'.");

            var keys = $"Enter or {_settings.HotkeySweepStart} starts the sweep; Escape or {_settings.HotkeySweepFinish} cancels.";
            if (await PositionGridAsync(kind, start, keys, warning, screen) is not { } confirmed)
            {
                _log.LogInformation("Grid editor canceled");
                SetStatus("Sweep not started");
                await RestoreGridOverlay();
                return;
            }
            await SaveCalibration(kind, bounds, confirmed);
            await _controller.StartSweep(kind, confirmed);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Starting a sweep failed");
            SetStatus($"Starting a sweep failed: {ex.Message}");
        }
    }

    /// <summary>Reopens the editor on a running sweep that is still empty; on confirm, moves the sweep there.</summary>
    private async Task RepositionSweepAsync(ActiveSweepInfo sweep)
    {
        if (_editor is not null)
        {
            _editor.BringToTop();
            return;
        }

        try
        {
            var screens = _overlay.Screens;
            var center = new PixelPoint((int)(sweep.Grid.X + sweep.Grid.Width / 2), (int)(sweep.Grid.Y + sweep.Grid.Height / 2));
            if ((screens.ScreenFromPoint(center) ?? screens.Primary) is not { } screen)
                return;

            var keys = $"Enter or {_settings.HotkeySweepStart} moves the sweep here; Escape or {_settings.HotkeySweepFinish} keeps it where it was.";
            if (await PositionGridAsync(sweep.Kind, sweep.Grid, keys, $"Repositioning the running sweep '{sweep.Label}'.", screen) is { } confirmed)
            {
                if (await _controller.RepositionSweep(confirmed))
                    await SaveCalibration(sweep.Kind, screen.Bounds, confirmed);
                else
                    SetStatus("The sweep already has cells: grid not moved");
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Repositioning the sweep failed");
            SetStatus($"Repositioning the sweep failed: {ex.Message}");
        }
        await RestoreGridOverlay();
    }

    /// <summary>
    /// Shows the grid editor (positioning mode) and waits for it; the copying grid is hidden meanwhile. The overlay
    /// only says "Position"; how to use it (<paramref name="keys"/>) and any <paramref name="warning"/> go to the status line.
    /// </summary>
    private async Task<GridGeometry?> PositionGridAsync(GridKind kind, GridGeometry start, string keys, string? warning, Screen screen)
    {
        var editor = new GridEditorWindow(kind, start);
        editor.GrabAreasChanged += areas =>
        {
            if (_capture is { } capture)
                capture.GridChrome = areas;
        };
        _editor = editor;
        SetGridEditing(true);
        if (_gridOverlay.IsVisible)
            _gridOverlay.Hide();
        _log.LogInformation("Grid editor opened for {Kind} on screen {Bounds}", kind, screen.Bounds);
        SetStatus(Join(warning, $"Positioning: drag the title to move the grid, a handle to resize it; arrows fine-tune. {keys}"));
        editor.ShowOn(screen);
        try
        {
            return await editor.Result;
        }
        finally
        {
            _editor = null;
            SetGridEditing(false);
            if (_capture is { } capture)
                capture.GridChrome = [];
        }
    }

    private async Task RestoreGridOverlay()
    {
        try
        {
            UpdateGridOverlay((await _controller.GetActive())?.Sweep);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Restoring the sweep grid failed");
        }
    }

    private Task SaveCalibration(GridKind kind, PixelRect bounds, GridGeometry grid) =>
        _controller.SaveCalibration(new GridCalibration(kind.Name, bounds.Width, bounds.Height,
            grid.Translate(-bounds.X, -bounds.Y), DateTimeOffset.UtcNow));

    /// <summary>
    /// The saved calibration for this kind and resolution, else a default. <c>Saved</c> is true only for a calibration
    /// made on a screen of this exact size that fits it: such a grid is used without asking.
    /// </summary>
    private async Task<(GridGeometry Grid, string? Warning, bool Saved)> StartingGrid(GridKind kind, PixelRect bounds)
    {
        var centered = GridGeometry.Centered(bounds.X, bounds.Y, bounds.Width, bounds.Height, kind.Columns, kind.Rows);
        if (await _controller.FindCalibration(kind, bounds.Width, bounds.Height) is not { } saved)
        {
            return GridDefaults.For(kind, bounds.Width, bounds.Height) is { } known
                ? (known.Translate(bounds.X, bounds.Y), "Default position (measured at 100% UI scale): check it lines up, then start. It is remembered.", false)
                : (centered, "No saved grid for this area yet: align it once, it is remembered.", false);
        }

        var grid = saved.Grid.Translate(bounds.X, bounds.Y);
        if (grid.X < bounds.X || grid.Y < bounds.Y || grid.Right > bounds.Right || grid.Bottom > bounds.Bottom)
            return (centered, $"The saved grid (for a {saved.ScreenWidth}x{saved.ScreenHeight} screen) does not fit this screen: align it again.", false);
        if (saved.ScreenWidth != bounds.Width || saved.ScreenHeight != bounds.Height)
            return (grid, $"The saved grid is for a {saved.ScreenWidth}x{saved.ScreenHeight} screen, this one is {bounds.Width}x{bounds.Height}: it may be off.", false);
        return (grid, null, true);
    }

    private static string Join(string? a, string b) => a is null ? b : $"{a} {b}";

    private void OnSweepHotkey(HotkeyPress press)
    {
        switch (press.Action)
        {
            case HotkeyAction.SweepStart when _editor is { } editor:
                editor.Confirm();
                break;
            case HotkeyAction.SweepStart:
                _ = SweepStartFromHotkey(press);
                break;
            case HotkeyAction.SweepFinish when _editor is { } editor:
                editor.Cancel();
                break;
            case HotkeyAction.SweepFinish:
                _ = FinishSweepFromHotkey();
                break;
        }
    }

    /// <summary>Repositions a running sweep that is still empty; otherwise starts a new one.</summary>
    private async Task SweepStartFromHotkey(HotkeyPress press)
    {
        try
        {
            if ((await _controller.GetActive())?.Sweep is { Cells.Count: 0 } empty)
            {
                await RepositionSweepAsync(empty);
                return;
            }
            if (_sweeps.SelectedGridKind is { } kind)
            {
                await _mapping.SetNextScanKind(null); // a plain sweep in a mapping session is a swipe
                await BeginSweepAsync(kind, press.Cursor);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Sweep start hotkey failed");
            SetStatus($"Starting a sweep failed: {ex.Message}");
        }
    }

    /// <summary>Mapping hotkeys act on the recording session when it is a mapping session (MAPPING_PLAN.md 5).</summary>
    private async Task OnMappingHotkey(HotkeyPress press)
    {
        try
        {
            if (await _controller.GetActive() is not { Kind: SessionKind.Mapping } active)
            {
                _overlay.ShowResult(new Feedback(FeedbackKind.Info, "No mapping session is recording"));
                return;
            }
            switch (press.Action)
            {
                case HotkeyAction.MapInitialScan:
                    // The run's first swipe is its Initial: offered only while the run has none.
                    var session = await _mapping.Load(active.Id);
                    if (session.CurrentRun is { } current && session.InitialOf(current) is not null)
                        _overlay.ShowResult(new Feedback(FeedbackKind.Info, "This run already has its Initial"));
                    else
                        await StartMappingScan(ScanKind.Swipe, press.Cursor);
                    break;
                case HotkeyAction.MapStashScan or HotkeyAction.MapSwipe or HotkeyAction.MapFinalScan:
                    await StartMappingScan(press.Action switch
                    {
                        HotkeyAction.MapStashScan => ScanKind.Stash,
                        HotkeyAction.MapFinalScan => ScanKind.Final,
                        _ => ScanKind.Swipe,
                    }, press.Cursor);
                    break;
                case HotkeyAction.MapStartRun:
                    var run = await _mapping.StartNewRun(active.Id);
                    _overlay.ShowResult(new Feedback(FeedbackKind.RunStarted, $"Run {run.Number} started"));
                    break;
                case HotkeyAction.MapEndRun:
                    _overlay.ShowResult(await _mapping.EndRun(active.Id)
                        ? new Feedback(FeedbackKind.RunEnded, "Run ended")
                        : new Feedback(FeedbackKind.Info, "No run is open"));
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Mapping hotkey {Action} failed", press.Action);
            SetStatus($"{press.Action} failed: {ex.Message}");
        }
    }

    /// <summary>A mapping scan: the next sweep gets <paramref name="kind"/>, over the inventory area.</summary>
    private async Task StartMappingScan(ScanKind kind, ScreenPoint? cursor)
    {
        if (_sweeps.SelectInventory() is not { } inventory)
        {
            SetStatus("No sweep area: add one in Settings, Sweeps");
            return;
        }
        await _mapping.SetNextScanKind(kind);
        await BeginSweepAsync(inventory, cursor);
    }

    private async Task FinishSweepFromHotkey()
    {
        try
        {
            if (await _controller.FinishSweep() is null)
                _overlay.ShowResult(new Feedback(FeedbackKind.Info, "No sweep running"));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Finishing the sweep failed");
            SetStatus($"Finishing the sweep failed: {ex.Message}");
        }
    }

    private void SetGridEditing(bool on)
    {
        if (_capture is { } capture)
            capture.GridEditing = on;
    }

    /// <summary>Scan mode: shows/updates the grid of the running sweep, hides it when none runs.</summary>
    private void UpdateGridOverlay(ActiveSweepInfo? sweep)
    {
        if (sweep is null)
        {
            if (_gridOverlay.IsVisible)
                _gridOverlay.Hide();
            return;
        }
        if (_editor is not null)
            return; // positioning: only the orange grid shows; restored when the editor closes

        if (!_gridOverlay.IsVisible || _gridOverlay.Sweep?.Id != sweep.Id)
        {
            var center = new PixelPoint((int)(sweep.Grid.X + sweep.Grid.Width / 2), (int)(sweep.Grid.Y + sweep.Grid.Height / 2));
            if ((_gridOverlay.Screens.ScreenFromPoint(center) ?? _gridOverlay.Screens.Primary) is { } screen)
                _gridOverlay.ShowOn(screen);
        }
        _gridOverlay.Update(sweep);
    }

    private void RememberGridKind(GridKind kind)
    {
        if (kind.Name == _settings.LastGridKind)
            return;
        _settings = _settings with { LastGridKind = kind.Name };
        try
        {
            SettingsFile.Save(_paths.Settings, _settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not save the chosen grid kind");
        }
    }

    private void ApplyOverlaySettings()
    {
        GameFocus.GameWindowTitle = _settings.GameWindowTitle;
        _overlay.Placement = _settings.OverlayPlacement;
        _overlay.SetDurations(TimeSpan.FromMilliseconds(_settings.FeedbackSweepMs), TimeSpan.FromMilliseconds(_settings.FeedbackResultMs));
    }

    /// <summary>
    /// <c>--screenshot &lt;dir&gt;</c>: renders the main window on the session list and on the newest session's page in
    /// both themes to PNG files, then a recording session's page (a temporary empty session, deleted afterwards), then
    /// exits. Capture is not started in this mode. tools/design-ui/update runs it into docs/design-ui/screens with an
    /// empty data folder.
    /// </summary>
    private async Task RunScreenshots(string dir)
    {
        Directory.CreateDirectory(dir);
        var window = _mainWindow!;
        window.Show();
        await SaveAll("session-list");
        if (_list.Sessions.FirstOrDefault() is { } first)
        {
            _list.Selected = first;
            _main.Open(first.Id);
            await SaveAll("session");
        }

        // Recovery ended every session, so the recording view (sweep bar, hotkeys) needs one of its own.
        await _controller.StartNewSession();
        if (await _controller.GetActive() is { } recording)
        {
            _main.Open(recording.Id);
            await SaveAll("recording");
            await _controller.DeleteSession(recording.Id); // its page is gone: back to the list
        }

        // The Gather tab of a temporary mapping session with sample runs, then with the captured items strip open.
        var mapping = await SampleMapping.Create(_controller, _mapping);

        // The session list with that session (runs, tags, the REC pill) and the Started filter's custom range.
        _main.ShowListCommand.Execute(null);
        await _list.RefreshAsync();
        _list.StartedFilter = SessionListViewModel.StartedBetween;
        _list.StartedFrom = DateTime.Today;
        await SaveAll("session-list-filters");
        _list.StartedFilter = SessionListViewModel.StartedAny;

        _main.Open(mapping);
        await SaveAll("gather");
        var page = _main.CurrentSession!;
        page.Gather!.ShowList = true;
        page.Gather.LootOfSession = true;
        await SaveAll("gather-list");
        page.Gather.ShowItems = true;
        await SaveAll("gather-items");

        // Run 2 picked in the history (the breadcrumb ends in "Run 2"): its Final differs from its Initial (warning,
        // Review). Then the dialogs.
        page.Gather.ShowCurrencies = true;
        page.Gather.ShowGrid = true;
        page.Gather.LootOfRun = true;
        page.Gather.SelectedRun = page.Gather.Runs.First(r => r.Number == 2);
        await SaveAll("gather-review");
        await SaveDialogScreenshots(await _mapping.Load(mapping));

        // The Analyze tab (sample prices): one run excluded by hand, one currency unticked and a chart added by hand;
        // then A vs B by a numeric tag.
        var sample = await _mapping.Load(mapping);
        await _mapping.SetRunExcluded(mapping, sample.Runs[1].Id, true);
        page.IsAnalyzeMode = true;
        var analyze = page.Analyze!;
        analyze.SetBook(_currencies.WithPrices(SampleMapping.Prices));
        analyze.Unit = PriceUnit.Divine;
        analyze.AddCustomChart(["Orb of Alchemy", "Vaal Orb", "Greater Orb of Transmutation"]);
        analyze.Currencies.First(c => c.Name == "Orb of Annulment").IsUsed = false;
        await SaveAll("analyze");
        analyze.IsCompare = true;
        analyze.AddAFilter(RunFilter.Parse("rituals >= 6", out _)!);
        await SaveAll("analyze-compare");

        await _controller.DeleteSession(mapping);

        await SaveSettingsScreenshots();

        // The capture indicator's outcomes: not themed, so one picture.
        var sheet = new IndicatorSheet();
        sheet.Show();
        await Task.Delay(300);
        Save(sheet, Path.Combine(dir, "capture-indicator.png"));
        sheet.Close();
        var grids = new GridChromeSheet();
        grids.Show();
        await Task.Delay(300);
        Save(grids, Path.Combine(dir, "sweep-grid.png"));
        grids.Close();

        _log.LogInformation("Screenshots saved to {Dir}", dir);
        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();

        async Task SaveAll(string name)
        {
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                ThemeSwitcher.Apply(theme);
                await Task.Delay(800); // layout, data load, theme re-resolve
                Save(window, Path.Combine(dir, $"{name}-{theme}.png".ToLowerInvariant()));
            }
        }


        // The recall dialog (waystone, then tablets) for the last run, and the review of run 2.
        async Task SaveDialogScreenshots(MapSession data)
        {
            var run = data.Runs[^1];
            var recall = new RecallDialogViewModel(run.Number, data.RecallWaystones(), data.RecallTablets(), run.Waystone?.RawText,
                run.Tablets.Select(t => t.RawText).ToList(), data.PreviousRun(run)?.Tablets.Select(t => t.RawText).ToList() ?? [],
                startOnTablets: false);
            var review = new ReviewDialogViewModel(2, Loot.CheckOf(data, data.Runs[1])!, name => _currencies.Describe(name).Code);
            var shots = new (string Name, Func<Window> Create)[]
            {
                ("recall", () => new RecallDialog { DataContext = recall }),
                ("recall-tablets", () =>
                {
                    recall.IsTabletsTab = true;
                    return new RecallDialog { DataContext = recall };
                }),
                ("review", () => new ReviewDialog { DataContext = review }),
                ("new-session", () => new NewSessionDialog("Session 2", SampleMapping.Name)),
                ("prompt", () => new PromptDialog("Rename session", "New name for the session:", SampleMapping.Name, null, multiline: false)),
                ("confirm", () => new ConfirmDialog($"Delete session '{SampleMapping.Name}' with all its runs and captures?", "Delete", "Cancel")),
                ("add-chart", () => new AddChartDialog("Add chart",
                [
                    new("Chaos Orb", _currencies.Describe("Chaos Orb"), "43.3 DIV", true),
                    new("Divine Orb", _currencies.Describe("Divine Orb"), "9.00 DIV", false),
                    new("Orb of Transmutation", _currencies.Describe("Orb of Transmutation"), "1.07 DIV", true),
                    new("Verisium", _currencies.Describe("Verisium"), "–", false),
                ])),
            };
            foreach (var (name, create) in shots)
            {
                var window = create();
                window.Show();
                foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
                {
                    ThemeSwitcher.Apply(theme);
                    await Task.Delay(500);
                    Save(window, Path.Combine(dir, $"{name}-{theme}.png".ToLowerInvariant()));
                }
                window.Close();
            }
        }

        // Every settings page, then the states worth seeing: an edited value,
        // a hotkey being recorded, a duplicate hotkey and a duplicate area. Nothing is saved.
        async Task SaveSettingsScreenshots()
        {
            var vm = CreateSettingsViewModel();
            vm.PreviewTheme = null;
            var window = new SettingsWindow(vm);
            window.Show();
            foreach (var states in new[] { false, true })
            {
                if (states)
                {
                    vm.PositionTolerance.Text = "14";
                    vm.SweepFinish.Text = vm.Redo.Text;
                    vm.SweepStart.ToggleRecordingCommand.Execute(null);
                    vm.Areas.AddCommand.Execute(null);
                    vm.Areas.Rows[^1].Name = vm.Areas.Rows[0].Name.ToLowerInvariant();
                }
                foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
                {
                    ThemeSwitcher.Apply(theme);
                    foreach (var page in vm.Pages.Where(p => !states || p == vm.Capture || p == vm.HotkeysPage || p == vm.Sweeps))
                    {
                        vm.SelectedPage = page;
                        await Task.Delay(300);
                        Save(window, Path.Combine(dir, $"settings-{page.Key}{(states ? "-states" : "")}-{theme}.png".ToLowerInvariant()));
                    }
                }
            }
            window.CloseWithoutPrompt();
        }

        static void Save(Window window, string path)
        {
            var size = new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height);
            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size);
            bitmap.Render(window);
            bitmap.Save(path, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
    }

    /// <summary><c>--overlay-demo</c>: cycles through every feedback outcome at the screen center.</summary>
    private void RunOverlayDemo()
    {
        _log.LogInformation("Overlay demo; click-through = {ClickThrough}", _overlay.IsClickThrough);
        var screen = _overlay.Screens.Primary!.Bounds.Center;
        var center = new ScreenPoint(screen.X - 60, screen.Y - 60);
        var samples = new[]
        {
            new Feedback(FeedbackKind.Added, "Added"),
            new Feedback(FeedbackKind.SiblingAdded, "Sibling"),
            new Feedback(FeedbackKind.StackableUpdated, "Verisium: now 31", 31),
            new Feedback(FeedbackKind.StackableAdded, "Big stack", 12_345),
            new Feedback(FeedbackKind.AlreadyRecorded, "Already recorded"),
            new Feedback(FeedbackKind.Covered, "Next cell of the same item"),
            new Feedback(FeedbackKind.Added, "Added outside the grid", OutsideGrid: true),
            new Feedback(FeedbackKind.Nothing, "Nothing captured"),
            new Feedback(FeedbackKind.Info, "Nothing to undo"),
            new Feedback(FeedbackKind.Undo, "Undo"),
            new Feedback(FeedbackKind.Redo, "Redo"),
            new Feedback(FeedbackKind.NewSession, "New session"),
            new Feedback(FeedbackKind.RunStarted, "Run 3 started"),
            new Feedback(FeedbackKind.RunEnded, "Run ended"),
            new Feedback(FeedbackKind.SweepStarted, "Sweep started"),
            new Feedback(FeedbackKind.CellSkipped, "Cell skipped"),
            new Feedback(FeedbackKind.SweepEnded, "Sweep finished", 26),
            new Feedback(FeedbackKind.SweepCanceled, "Sweep canceled"),
        };
        var i = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
        timer.Tick += (_, _) =>
        {
            var sample = samples[i++ % samples.Length];
            _overlay.ShowPress(center);
            DispatcherTimer.RunOnce(() => _overlay.ShowResult(sample), TimeSpan.FromMilliseconds(60));
            SetStatus($"Overlay demo: {sample.Kind}");
        };
        timer.Start();
    }

    private void RegisterGlobalExceptionLogging()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            _log.LogCritical(e.ExceptionObject as Exception, "Unhandled exception (terminating: {Terminating})", e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            _log.LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            _log.LogError(e.Exception, "Unhandled UI exception");
            SetStatus($"Error: {e.Exception.Message}");
            e.Handled = true;
        };
    }
}
