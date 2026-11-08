using System.Text.Json;
using System.Net;
using TvAIrPlugin;
using TvAIrPlugin.Runtime;
using TvAIrPlugin.Viewers;
using ThreadingTimer = System.Threading.Timer;

namespace AIrCon.BasicPlugin;

/// <summary>
/// AIrCon 視聴操作Plugin。
/// ToolWindow内の行ダブルクリック視聴と右クリック視聴予約を主導線にする。
/// </summary>
internal sealed partial class AIrConRenderer
{
    internal const string PluginVersion = "1.0.11";
    internal const string PluginListTitle = "AIrCon";
    internal static string PluginToolWindowTitle => $"{PluginListTitle} {PluginVersion}";
    internal const string PluginId = "aircon.basic";
    internal const string RouteSegment = "aircon";
    private const string ClientVersion = "AIrCon-" + PluginVersion;

    private const string ResponseModeHostHandled = "hostHandled";
    private const string RefreshTargetContent = "content";
    private const string BoolTrue = "true";
    private const string BoolFalse = "false";
    private const string ActionPluginOwned = "pluginOwnedAction";
    private const string ActionUpdateWindow = "updateWindow";
    private const string AirConActionZappingStart = "zappingStart";
    private const string AirConActionZappingStop = "zappingStop";
    private const string AirConActionPowerOffStart = "powerOffStart";
    private const string AirConActionPowerOffStop = "powerOffStop";
    private const string AirConActionViewerPowerOff = "viewerPowerOff";
    private const string AirConActionViewerTune = "viewerTune";
    private const string AirConActionViewerReservationToggle = "viewerReservationToggle";
    private const string AirConActionViewerActivate = "viewerActivate";
    private const string AirConActionBrowseWave = "browseWave";
    private const string AirConActionSettingsOpen = "settingsOpen";
    private const string AirConActionSettingsSave = "settingsSave";
    private const string AirConActionSettingsClose = "settingsClose";
    internal const string SettingsSection = "settings";
    internal const string SettingRememberPlacement = "rememberWindowPlacement";
    internal const bool DefaultRememberWindowPlacement = false;
    internal const string SettingZappingIntervalSeconds = "zappingIntervalSeconds";
    internal const string SettingStartupWave = "startupWave";
    internal const int DefaultZappingIntervalSeconds = 60;
    private const int DefaultPowerOffHours = 1;
    private const int MaxPowerOffHours = 6;
    internal const string DefaultStartupWave = "GR";
    private static readonly int[] ZappingIntervals = { 30, 60, 90, 120, 180 };
    private static readonly string[] StartupWaves = { "GR", "BS", "CS", "CATV" };

    internal static int ValidateZappingInterval(int value) =>
        Array.IndexOf(ZappingIntervals, value) >= 0 ? value : DefaultZappingIntervalSeconds;

    private static int ValidatePowerOffHours(int value) =>
        value is >= DefaultPowerOffHours and <= MaxPowerOffHours ? value : DefaultPowerOffHours;

    // Tool-window layout metrics are centralized here.
    // The WinForms WebBrowser fallback is IE-like. Use only layout features already validated in this host;
    // semantic color tokens are resolved before delivery, and flex is limited to the established toolbar/status/marquee regions.
    // Avoid introducing unvalidated browser-dependent layout such as sticky positioning or 100vh sizing.
    // Do not scatter toolbar/list pixel values in CSS strings. Derived values must come from this metric contract.
    // Toolbar wave buttons are text selectors; action buttons are compact symbolic square buttons.
    // Symbol sizing is derived from the toolbar button metric contract and is not an ad hoc icon reserve.
    private const int ToolWindowButtonTextLineHeightPx = 18;
    private const int ToolWindowButtonVerticalPaddingPx = 3;
    private const int ToolWindowButtonBorderPx = 1;
    private const int ToolWindowToolbarContentTopPx = 5;
    private const int ToolWindowToolbarContentBottomPx = 5;
    private const int ToolWindowToolbarPaddingXPx = 6;
    private const int ToolWindowToolbarCellGapPx = 3;
    private const int ToolWindowToolbarGroupGapPx = 6;
    private const int ToolWindowToolbarLabelPaddingRightPx = 3;
    private const int ToolWindowWaveButtonWidthPx = 50;
    private const int ToolWindowWaveAreaWidthPx = 206;
    private const int ToolWindowViewerProfileNumericButtonWidthPx = 24;
    internal const int ToolWindowDefaultWidthPx = 540;
    internal const int ToolWindowDefaultHeightPx = 320;
    internal const int ToolWindowMinimumWidthContractPx = 360;
    internal const int ToolWindowMinimumHeightContractPx = 180;
    private const int ToolWindowMinimumListRowsPx = 1;
    private static int ToolWindowActionButtonSizePx => ToolWindowToolbarButtonHeightPx;
    private static int ToolWindowActionButtonGroupWidthPx => (ToolWindowActionButtonSizePx * 4) + (ToolWindowToolbarCellGapPx * 3);
    private static int ToolWindowWaveButtonGroupWidthPx => (ToolWindowWaveButtonWidthPx * 3) - (ToolWindowWaveButtonOverlapPx * 2);
    private const int ToolWindowWaveButtonOverlapPx = ToolWindowButtonBorderPx;
    private const int ToolWindowToolbarSeparatorPx = 1;
    private const int ToolWindowRowHeightPx = 30;
    private const int ToolWindowServiceColumnMinimumWidthPx = 132;
    private const int ToolWindowServiceColumnMaximumWidthPx = 240;
    private const int ToolWindowServiceColumnHorizontalReservePx = 12;
    private const int ToolWindowTimeColumnWidthPx = 42;

    private static int ToolWindowToolbarButtonHeightPx => ToolWindowButtonTextLineHeightPx + (ToolWindowButtonVerticalPaddingPx * 2) + (ToolWindowButtonBorderPx * 2);
    private static int ToolWindowToolbarCellHeightPx => ToolWindowToolbarButtonHeightPx;
    private static int ToolWindowToolbarHeightPx => ToolWindowToolbarContentTopPx + ToolWindowToolbarButtonHeightPx + ToolWindowToolbarContentBottomPx + ToolWindowToolbarSeparatorPx;
    private static int ToolWindowListTopPx => ToolWindowToolbarHeightPx;
    private static int ToolWindowButtonLineHeightPx => Math.Max(1, ToolWindowToolbarButtonHeightPx - (ToolWindowButtonBorderPx * 2));
    private static int ToolWindowMinimumWidthPx => ToolWindowMinimumWidthContractPx;
    private static int ToolWindowMinimumHeightPx => Math.Max(ToolWindowMinimumHeightContractPx, ToolWindowToolbarHeightPx + (ToolWindowRowHeightPx * ToolWindowMinimumListRowsPx) + 16);

    // One per-window snapshot owns the latest render request and navigation intent.
    // RenderHtml generates markup, but does NOT prove that the Host has displayed it.
    // Only the browser's matching DOM identity authorizes a native scroll. A deferred
    // navigation remains separate from the last generated wave/profile until rendered.
    private sealed record WindowDisplayState(string Wave, string ViewerProfile,
        string ServiceKey, long RenderVersion,
        string? RequestedWave = null, string? RequestedViewerProfile = null);
    private readonly object _windowDisplayLock = new();
    private readonly Dictionary<string, WindowDisplayState> _windowDisplays =
        new(StringComparer.OrdinalIgnoreCase);

    private WindowDisplayState? GetWindowDisplay(string windowId)
    {
        if (string.IsNullOrWhiteSpace(windowId)) return null;
        lock (_windowDisplayLock)
            return _windowDisplays.TryGetValue(windowId, out var state) ? state : null;
    }

    private KeyValuePair<string, WindowDisplayState>[] SnapshotWindowDisplays()
    {
        lock (_windowDisplayLock) return _windowDisplays.ToArray();
    }

    private void PublishWindowDisplay(string windowId, string wave, string viewerProfile,
        string serviceKey, long renderVersion)
    {
        if (string.IsNullOrWhiteSpace(windowId)) return;
        lock (_windowDisplayLock)
            _windowDisplays[windowId] = new WindowDisplayState(
                NormalizeWaveFilter(wave), viewerProfile, serviceKey, renderVersion);
    }

    // A navigation request is not a completed navigation. Record its intent only
    // with an actual Host refresh. Failed requests restore the prior snapshot;
    // successful requests are committed by RenderHtml for the new document.
    private AIrConNewApiBridge.OperationResult RequestWindowDisplayNavigation(
        string windowId, string wave, string viewerProfile, string? contentRoute = null)
    {
        WindowDisplayState? before;
        WindowDisplayState? request = null;
        lock (_windowDisplayLock)
        {
            _windowDisplays.TryGetValue(windowId, out before);
            if (before != null)
            {
                request = before with
                {
                    RequestedWave = NormalizeWaveFilter(wave),
                    RequestedViewerProfile = viewerProfile
                };
                _windowDisplays[windowId] = request;
            }
        }

        var refresh = AIrConNewApiBridge.RefreshToolWindow(windowId,
            contentRoute ?? BuildViewerContentRoute(wave, viewerProfile));
        if (!refresh.Success && request != null)
        {
            lock (_windowDisplayLock)
            {
                // Reference identity protects an overlapping navigation even when
                // it targets the same wave/profile and render generation.
                if (_windowDisplays.TryGetValue(windowId, out var current)
                    && ReferenceEquals(current, request))
                    _windowDisplays[windowId] = before!;
            }
        }
        return refresh;
    }

    // A wave click selects the list, not a refresh command. A fully rendered, unchanged
    // wave/profile has nothing to navigate; avoid a needless Host document rebuild.
    // Pending navigation is never treated as completed rendering.
    private AIrConNewApiBridge.OperationResult RequestWaveNavigation(
        string windowId, string wave, string viewerProfile)
    {
        var displayed = GetWindowDisplay(windowId);
        if (displayed != null && displayed.RequestedWave == null &&
            displayed.RequestedViewerProfile == null &&
            displayed.Wave.Equals(wave, StringComparison.OrdinalIgnoreCase) &&
            displayed.ViewerProfile.Equals(viewerProfile, StringComparison.OrdinalIgnoreCase))
            return AIrConNewApiBridge.OperationResult.Ok("wave_already_displayed");
        return RequestWindowDisplayNavigation(windowId, wave, viewerProfile);
    }

    private void SetRenderedService(string windowId, string serviceKey)
    {
        lock (_windowDisplayLock)
            if (_windowDisplays.TryGetValue(windowId, out var prior))
                _windowDisplays[windowId] = prior with { ServiceKey = serviceKey };
    }
    private long _nextRenderVersion;
    private const int CenterScrollMaxAttempts = 16;
    private const int CenterScrollRetryDelayMs = 200;
    // Scroll position belongs to the existing list element. Selection and destination
    // come from the same ViewerSession, never from separate URL fragments or CSS offsets.
    // Selection and its scroll destination share one service identity. Never use a
    // separate, mutable DOM ID for the same station during an asynchronous retune.
    private sealed record CurrentStation(string Wave, string ViewerProfile, string ServiceKey, string RowId);
    private readonly object _centerRequestLock = new();
    private readonly Dictionary<string, CancellationTokenSource> _pendingCenterByWindow = new(StringComparer.OrdinalIgnoreCase);
    // Runtime lifecycle is read from timer and UI threads. A restarted plugin
    // must never accept work queued by the preceding lifecycle.
    private volatile bool _runtimeUiStopped;
    private long _runtimeUiLifecycleVersion;

    private long CaptureRuntimeUiLifecycle() => Interlocked.Read(ref _runtimeUiLifecycleVersion);
    private bool IsCurrentRuntimeUiLifecycle(long version) =>
        !_runtimeUiStopped && CaptureRuntimeUiLifecycle() == version;


    private readonly object _zappingLock = new();
    private readonly Dictionary<string, ZappingState> _zappingStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ThreadingTimer> _zappingTimers = new(StringComparer.OrdinalIgnoreCase);
    // One short-lived, profile-scoped intent from a TvAIr ProgramGuide selection.
    // Unlike background retunes, it explicitly requests that AIrCon follow the new
    // wave/profile even when the user had been browsing a different list.
    private readonly Dictionary<string, DateTimeOffset> _programGuideFollowUntil = new(StringComparer.OrdinalIgnoreCase);
    // ProgramGuide may tune before the newly opened ToolWindow has its first render.
    private (string Wave, string Profile, DateTimeOffset Deadline)? _pendingProgramGuideInitialSelection;
    private readonly object _powerOffLock = new();
    private readonly Dictionary<string, PowerOffState> _powerOffStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ThreadingTimer> _powerOffTimers = new(StringComparer.OrdinalIgnoreCase);
    private long _powerOffGeneration;
    private readonly object _operationLock = new();
    private readonly Dictionary<string, long> _operationGenerations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ProfileOperationGate> _profileOperationGates = new(StringComparer.OrdinalIgnoreCase);

    internal void ApplyRuntimeWindowLifecycle(TvAIrPlugin.Events.PluginEventEnvelope eventEnvelope)
    {
        var lifecycle = TryReadRuntimeWindowLifecycle(eventEnvelope.Payload);
        if (lifecycle == null)
        {
            #if AIRCON_DEVELOPER_DIAGNOSTICS
            SafeLog("WINDOW_LIFECYCLE result=SKIPPED reason=payload_missing_or_invalid");
            #endif
            return;
        }

        if (!lifecycle.PluginId.Equals(PluginId, StringComparison.OrdinalIgnoreCase))
            return;

        if (lifecycle.State == TvAIrPlugin.Windows.PluginWindowLifecycleState.Shown)
        {
            ScheduleCurrentServiceCenter(lifecycle.WindowInstanceId, 550);
            return;
        }
        if (lifecycle.State is not (TvAIrPlugin.Windows.PluginWindowLifecycleState.Closing or TvAIrPlugin.Windows.PluginWindowLifecycleState.Closed))
            return;

        if (lifecycle.BackgroundExecution != TvAIrPlugin.Windows.PluginWindowBackgroundExecution.StopWithWindow)
            return;

        StopWindowBackgroundExecution(lifecycle.WindowInstanceId);
        #if AIRCON_DEVELOPER_DIAGNOSTICS
        SafeLog($"WINDOW_LIFECYCLE result=TERMINAL state={lifecycle.State} windowId={lifecycle.WindowInstanceId} source={lifecycle.Source} sessionDisposed={lifecycle.SessionDisposed} action=stop_window_background_execution");
        #endif
    }

    private static TvAirRuntimeWindowLifecycleDto? TryReadRuntimeWindowLifecycle(object? payload)
    {
        if (payload is TvAirRuntimeWindowLifecycleDto lifecycle) return lifecycle;
        if (payload is TvAirEventDto eventDto) return eventDto.RuntimeWindowLifecycle;
        if (payload is JsonElement json)
        {
            try
            {
                if (json.ValueKind == JsonValueKind.Object)
                {
                    if (json.TryGetProperty("runtimeWindowLifecycle", out var nested) ||
                        json.TryGetProperty("RuntimeWindowLifecycle", out nested))
                        return nested.Deserialize<TvAirRuntimeWindowLifecycleDto>();
                }
                return json.Deserialize<TvAirRuntimeWindowLifecycleDto>();
            }
            catch (JsonException)
            {
                return null;
            }
        }
        return null;
    }

    internal void BeginRuntimeUi()
    {
        Interlocked.Increment(ref _runtimeUiLifecycleVersion);
        _runtimeUiStopped = false;
    }

    // The native Win32 foreground observer only reports activation of our own ToolWindow.
    // No activation, focus, Host window inspection or scrolling is performed by the observer.
    internal void OnOwnWindowForeground()
    {
        foreach (var windowId in SnapshotWindowDisplays().Select(pair => pair.Key))
            ScheduleCurrentServiceCenter(windowId, 150);
    }

    private void ScheduleCurrentServiceCenter(string windowId, int delayMs, string? expectedServiceKey = null)
    {
        if (string.IsNullOrWhiteSpace(windowId) || _runtimeUiStopped) return;
        var lifecycleVersion = CaptureRuntimeUiLifecycle();
        CancellationTokenSource scheduled;
        lock (_centerRequestLock)
        {
            if (_pendingCenterByWindow.Remove(windowId, out var previous))
                previous.Cancel();
            scheduled = new CancellationTokenSource();
            _pendingCenterByWindow[windowId] = scheduled;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs, scheduled.Token).ConfigureAwait(false);
                // The document may still be navigating when the first callback arrives.
                // Bounded retries allow the displayed WebBrowser to complete navigation.
                for (var attempt = 1; attempt <= CenterScrollMaxAttempts; attempt++)
                {
                    scheduled.Token.ThrowIfCancellationRequested();
                    if (!IsCurrentRuntimeUiLifecycle(lifecycleVersion)) return;
                    var displayed = GetWindowDisplay(windowId);
                    if (displayed == null || displayed.RenderVersion == 0
                        || displayed.RequestedWave != null)
                    {
#if AIRCON_DEVELOPER_DIAGNOSTICS
                        SafeLog($"CENTER_SCROLL result=SKIPPED windowId={windowId} reason={(displayed == null || displayed.RenderVersion == 0 ? "display_unavailable" : "navigation_pending")} attempt={attempt}");
#endif
                        return;
                    }
                    // One canonical viewer-state snapshot per retry. The callback running on the
                    // WinForms UI thread checks only the immutable per-window snapshot:
                    // never perform a Host API lookup while the browser is being scrolled.
                    var station = ResolveCurrentStation(displayed);
                    if (station == null || !station.ServiceKey.Equals(displayed.ServiceKey, StringComparison.OrdinalIgnoreCase)
                        || (expectedServiceKey != null &&
                        !station.ServiceKey.Equals(expectedServiceKey, StringComparison.OrdinalIgnoreCase)))
                    {
#if AIRCON_DEVELOPER_DIAGNOSTICS
                        SafeLog($"CENTER_SCROLL result=SKIPPED windowId={windowId} wave={displayed.Wave} service={displayed.ServiceKey} attempt={attempt} reason={(station == null ? "no_visible_viewer" : "selection_changed")}");
#endif
                        return;
                    }
                    var key = station.ServiceKey;
                    bool IsCurrentRender()
                    {
                        if (!IsCurrentRuntimeUiLifecycle(lifecycleVersion) || scheduled.IsCancellationRequested) return false;
                        // Compare one immutable snapshot before and inside the UI callback;
                        // no mixed wave/profile/version pair can escape this boundary.
                        var currentDisplay = GetWindowDisplay(windowId);
                        if (currentDisplay == null || currentDisplay.RequestedWave != null
                            || currentDisplay.RenderVersion != displayed.RenderVersion
                            || !currentDisplay.Wave.Equals(displayed.Wave, StringComparison.OrdinalIgnoreCase)
                            || !currentDisplay.ViewerProfile.Equals(displayed.ViewerProfile, StringComparison.OrdinalIgnoreCase)) return false;
                        // This is the canonical selected row already verified above.
                        // A replacement render, navigation or selection invalidates it
                        // without any synchronous Host call from the UI callback.
                        return currentDisplay.ServiceKey.Equals(key, StringComparison.OrdinalIgnoreCase);
                    }
                    var result = await AirConForegroundObserver.CenterOwnListRowAsync(
                        station.RowId, displayed.RenderVersion,
                        IsCurrentRender, scheduled.Token).ConfigureAwait(false);
#if AIRCON_DEVELOPER_DIAGNOSTICS
                    // A navigating WebBrowser may still contain the preceding DOM.
                    // Report the final result rather than flood logs during navigation.
                    if (result.Applied || attempt == CenterScrollMaxAttempts ||
                        result.Reason is not ("document_not_ready" or "document_outdated" or "row_not_ready"))
                        SafeLog($"CENTER_SCROLL result={(result.Applied ? "APPLIED" : "FAILED")} windowId={windowId} wave={station.Wave} service={station.ServiceKey} attempt={attempt} reason={result.Reason} target={result.TargetTop} actual={result.ActualTop} max={result.MaxTop}");
#endif
                    if (result.Applied || result.Reason is "superseded" or "control_missing" or "window_not_visible") return;
                    if (attempt < CenterScrollMaxAttempts)
                        await Task.Delay(CenterScrollRetryDelayMs, scheduled.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
#if AIRCON_DEVELOPER_DIAGNOSTICS
                SafeLog("CENTER_SCROLL result=FAILED reason=unexpected_exception");
#endif
            }
            finally
            {
                // A timed-out BeginInvoke can still be queued on the WinForms
                // thread. Invalidate its callback before releasing our request:
                // abandoned UI work must never scroll a later document.
                scheduled.Cancel();
                lock (_centerRequestLock)
                {
                    if (_pendingCenterByWindow.TryGetValue(windowId, out var current)
                        && ReferenceEquals(current, scheduled))
                        _pendingCenterByWindow.Remove(windowId);
                }
                scheduled.Dispose();
            }
        });
    }

    private CurrentStation? ResolveCurrentStation(string windowId)
        => GetWindowDisplay(windowId) is { } displayed ? ResolveCurrentStation(displayed) : null;

    private static CurrentStation? ResolveCurrentStation(WindowDisplayState displayed)
    {
        var wave = displayed.Wave;
        var profile = displayed.ViewerProfile;
        if (string.IsNullOrWhiteSpace(profile)) return null;
        var session = AIrConNewApiBridge.ListSessions().FirstOrDefault(x =>
            IsLiveViewerSession(x) && x.ViewerProfileId.Equals(profile, StringComparison.OrdinalIgnoreCase)
            && x.CurrentService is { NetworkId: > 0, TransportStreamId: > 0, ServiceId: > 0 });
        var identity = session?.CurrentService;
        if (identity == null) return null;
        var service = AIrConNewApiBridge.ListServices().FirstOrDefault(x =>
            x.NetworkId == identity.NetworkId && x.TransportStreamId == identity.TransportStreamId
            && x.ServiceId == identity.ServiceId);
        if (service == null || !NormalizeWaveFilter(service.BroadcastType).Equals(NormalizeWaveFilter(wave), StringComparison.OrdinalIgnoreCase)) return null;
        return new CurrentStation(NormalizeWaveFilter(wave), profile,
            ServiceKey(identity.NetworkId, identity.TransportStreamId, identity.ServiceId),
            BuildServiceDomId(identity.NetworkId, identity.TransportStreamId, identity.ServiceId));
    }

    internal void StopRuntimeUi()
    {
        _runtimeUiStopped = true;
        Interlocked.Increment(ref _runtimeUiLifecycleVersion);
        lock (_centerRequestLock)
        {
            foreach (var request in _pendingCenterByWindow.Values) request.Cancel();
            _pendingCenterByWindow.Clear();
        }
        lock (_zappingLock)
        {
            DisposeTimersAndClear(_zappingTimers);
            _zappingStates.Clear();
            _programGuideFollowUntil.Clear();
            _pendingProgramGuideInitialSelection = null;
        }
        lock (_powerOffLock)
        {
            DisposeTimersAndClear(_powerOffTimers);
            _powerOffStates.Clear();
        }
        lock (_operationLock)
        {
            _operationGenerations.Clear();
            // Retire every gate, but defer disposing its semaphore until all
            // queued/in-flight users have released their acquired leases.
            foreach (var gate in _profileOperationGates.Values)
            {
                gate.Retired = true;
                if (gate.LeaseCount == 0) gate.Semaphore.Dispose();
            }
            _profileOperationGates.Clear();
        }
        lock (_windowDisplayLock) _windowDisplays.Clear();
    }

    // Call under the owning state lock. Remove scheduled callbacks before clearing state;
    // a callback already queued must still pass its generation/state checks.
    // Under the owning state lock, detach and dispose one scheduled timer.
    // Generation/lifecycle checks still reject callbacks already queued.
    private static void RemoveAndDisposeTimer(Dictionary<string, ThreadingTimer> timers, string key)
    {
        if (timers.Remove(key, out var timer)) timer.Dispose();
    }

    private static void DisposeTimersAndClear(Dictionary<string, ThreadingTimer> timers)
    {
        foreach (var timer in timers.Values) timer.Dispose();
        timers.Clear();
    }

    public async Task<RuntimeUiActionResult> HandleActionAsync(RuntimeUiActionContext request, CancellationToken cancellationToken)
    {
        try
        {
            var airconAction = PayloadValue(request, "operation", "airconAction", "aircon-action");
            if (string.IsNullOrWhiteSpace(airconAction)) airconAction = request.ActionName;
            if (airconAction.Equals(AirConActionBrowseWave, StringComparison.OrdinalIgnoreCase))
            {
                var windowId = FirstNonEmpty(request.CurrentWindowId, PayloadValue(request, "windowId", "window-id", "currentWindowId", "current-window-id"));
                var wave = NormalizeWaveFilter(PayloadValue(request, "wave"));
                var viewerProfile = NormalizeViewerProfileId(PayloadValue(request, "viewerProfile", "viewer-profile"));
                // No viewer is required for browsing. Do not activate, start or retune.
                var refresh = RequestWaveNavigation(windowId, wave, viewerProfile);
                return new RuntimeUiActionResult { Succeeded = refresh.Success, Message = refresh.Success ? "局一覧を表示" : "局一覧の更新に失敗しました。", Diagnostics = refresh.Diagnostics };
            }
            if (airconAction.Equals(AirConActionSettingsOpen, StringComparison.OrdinalIgnoreCase) ||
                airconAction.Equals(AirConActionSettingsClose, StringComparison.OrdinalIgnoreCase) ||
                airconAction.Equals(AirConActionSettingsSave, StringComparison.OrdinalIgnoreCase))
            {
                var settingsWindowId = FirstNonEmpty(request.CurrentWindowId, PayloadValue(request, "windowId", "window-id", "currentWindowId", "current-window-id"));
                var returnWave = NormalizeWaveFilter(PayloadValue(request, "returnWave", "return-wave", "wave", "currentWave", "current-wave"));
                var returnProfile = NormalizeViewerProfileId(PayloadValue(request, "returnViewerProfile", "return-viewer-profile", "viewerProfile", "viewer-profile"));
                if (airconAction.Equals(AirConActionSettingsSave, StringComparison.OrdinalIgnoreCase))
                {
                    var remember = ParseBool(PayloadValue(request, SettingRememberPlacement, "remember-window-placement"), DefaultRememberWindowPlacement);
                    var interval = NormalizeZappingInterval(PayloadValue(request, SettingZappingIntervalSeconds, "zapping-interval-seconds"));
                    var startupWave = NormalizeWaveFilter(PayloadValue(request, SettingStartupWave, "startup-wave"));
                    var saved = AIrConNewApiBridge.SaveSettings(remember, interval, startupWave);
                    if (!saved.Success)
                    {
                        #if AIRCON_DEVELOPER_DIAGNOSTICS
                        SafeLog($"SETTINGS_SAVE result=FAILED diagnostics={saved.Diagnostics}");
                        #endif
                        return new RuntimeUiActionResult { Succeeded = false, Message = "設定保存失敗", Diagnostics = saved.Diagnostics };
                    }
                    var placement = AIrConNewApiBridge.SetPlacementPersistence("main", remember);
                    if (!placement.Success)
                    {
                        #if AIRCON_DEVELOPER_DIAGNOSTICS
                        SafeLog($"SETTINGS_SAVE result=FAILED phase=placement diagnostics={placement.Diagnostics}");
                        #endif
                        return new RuntimeUiActionResult { Succeeded = false, Message = "位置記憶設定失敗", Diagnostics = placement.Diagnostics };
                    }
                    var verifiedSettings = AIrConNewApiBridge.LoadSettings();
                    if (verifiedSettings.RememberWindowPlacement != remember ||
                        verifiedSettings.ZappingIntervalSeconds != interval ||
                        !string.Equals(verifiedSettings.StartupWave, startupWave, StringComparison.OrdinalIgnoreCase))
                    {
                        #if AIRCON_DEVELOPER_DIAGNOSTICS
                        SafeLog($"SETTINGS_SAVE_VERIFY result=FAILED writtenRemember={remember} readBackRemember={verifiedSettings.RememberWindowPlacement} writtenInterval={interval} readBackInterval={verifiedSettings.ZappingIntervalSeconds} writtenWave={startupWave} readBackWave={verifiedSettings.StartupWave}");
                        #endif
                        return new RuntimeUiActionResult { Succeeded = false, Message = "設定保存確認失敗", Diagnostics = "settings_readback_mismatch" };
                    }
                    #if AIRCON_DEVELOPER_DIAGNOSTICS
                    SafeLog($"SETTINGS_SAVE_VERIFY result=OK writtenRemember={remember} readBackRemember={verifiedSettings.RememberWindowPlacement} writtenInterval={interval} readBackInterval={verifiedSettings.ZappingIntervalSeconds} writtenWave={startupWave} readBackWave={verifiedSettings.StartupWave}");
                    #endif
                    var refresh = RequestWindowDisplayNavigation(settingsWindowId, returnWave, returnProfile);
                    #if AIRCON_DEVELOPER_DIAGNOSTICS
                    SafeLog($"SETTINGS_SAVE result={(refresh.Success ? "OK" : "FAILED")} rememberPlacement={remember} zappingIntervalSeconds={interval} startupWave={startupWave} refresh={refresh.Diagnostics}");
                    #endif
                    return new RuntimeUiActionResult { Succeeded = refresh.Success, Message = refresh.Success ? "設定を保存" : "設定画面更新失敗", Diagnostics = refresh.Diagnostics };
                }

                var returnQuery = "returnWave=" + Url(returnWave) + (string.IsNullOrWhiteSpace(returnProfile) ? string.Empty : "&returnViewerProfile=" + Url(returnProfile));
                var targetRoute = airconAction.Equals(AirConActionSettingsOpen, StringComparison.OrdinalIgnoreCase)
                    ? "/plugin/aircon?view=settings&" + returnQuery
                    : BuildViewerContentRoute(returnWave, returnProfile);
                var settingsRefresh = RequestWindowDisplayNavigation(settingsWindowId, returnWave, returnProfile, targetRoute);
                #if AIRCON_DEVELOPER_DIAGNOSTICS
                SafeLog($"SETTINGS_VIEW action={airconAction} result={(settingsRefresh.Success ? "OK" : "FAILED")} diagnostics={settingsRefresh.Diagnostics}");
                #endif
                return new RuntimeUiActionResult { Succeeded = settingsRefresh.Success, Message = settingsRefresh.Success ? "OK" : "設定画面更新失敗", Diagnostics = settingsRefresh.Diagnostics };
            }

            if (airconAction.Equals(AirConActionZappingStart, StringComparison.OrdinalIgnoreCase) ||
                airconAction.Equals(AirConActionZappingStop, StringComparison.OrdinalIgnoreCase) ||
                airconAction.Equals(AirConActionPowerOffStart, StringComparison.OrdinalIgnoreCase) ||
                airconAction.Equals(AirConActionPowerOffStop, StringComparison.OrdinalIgnoreCase) ||
                airconAction.Equals(AirConActionViewerPowerOff, StringComparison.OrdinalIgnoreCase) ||
                airconAction.Equals(AirConActionViewerActivate, StringComparison.OrdinalIgnoreCase) ||
                airconAction.Equals(AirConActionViewerTune, StringComparison.OrdinalIgnoreCase) ||
                airconAction.Equals(AirConActionViewerReservationToggle, StringComparison.OrdinalIgnoreCase))
            {
                var windowId = FirstNonEmpty(request.CurrentWindowId, PayloadValue(request, "windowId", "window-id", "currentWindowId", "current-window-id"));
                var wave = NormalizeWaveFilter(PayloadValue(request, "wave", "currentWave", "current-wave"));
                var viewerProfile = NormalizeViewerProfileId(PayloadValue(request, "viewerProfile", "viewer-profile"));
                if (string.IsNullOrWhiteSpace(viewerProfile))
                {
                    #if AIRCON_DEVELOPER_DIAGNOSTICS
                    SafeLog($"VIEWER_OPERATION rejected action={airconAction} reason=viewer_profile_missing");
                    #endif
                    return new RuntimeUiActionResult { Succeeded = false, Message = "視聴先を選択してください。", Diagnostics = "viewer_profile_missing" };
                }

                if (airconAction.Equals(AirConActionViewerActivate, StringComparison.OrdinalIgnoreCase))
                {
                    // The toolbar selection and Viewer activation are separate responsibilities.
                    // A configured profile remains selectable even when its TVTest is not running yet.
                    // When a live session exists, activate only that exact profile; otherwise keep the
                    // selection successful so a subsequent manual double-click can start that profile.
                    var liveSession = AIrConNewApiBridge.ListSessions()
                        .FirstOrDefault(x => IsLiveViewerSession(x)
                            && x.ViewerProfileId.Equals(viewerProfile, StringComparison.OrdinalIgnoreCase));
                    if (liveSession == null)
                    {
                        var refreshOnly = RequestWaveNavigation(windowId, wave, viewerProfile);
                        #if AIRCON_DEVELOPER_DIAGNOSTICS
                        SafeLog($"VIEWER_MANUAL_ACTIVATE viewerProfile={viewerProfile} wave={wave} result=SKIPPED reason=viewer_not_running selectionApplied=True refreshResult={(refreshOnly.Success ? "OK" : "FAILED")} refreshDiagnostics={refreshOnly.Diagnostics}");
                        #endif
                        return new RuntimeUiActionResult { Succeeded = refreshOnly.Success, Message = "視聴先を選択", Diagnostics = refreshOnly.Success ? "viewer_not_running_selection_applied_and_refreshed" : refreshOnly.Diagnostics };
                    }

                    // A wave button changes only the list and may bring its existing viewer forward.
                    // Observe foreground before navigation as well as afterwards: the Host may
                    // asynchronously replace the browser document during a wave switch, and that
                    // transient UI activity must not cause a second activation of a viewer that
                    // was already foreground when the click was received.
                    var waveButtonClick = PayloadValue(request, "waveButton")
                        .Equals("true", StringComparison.OrdinalIgnoreCase);
                    var targetPid = liveSession.ProcessId.GetValueOrDefault();
                    var targetWasForeground = waveButtonClick && targetPid > 0
                        && AirConForegroundObserver.IsViewerProcessForeground(targetPid);
                    var refresh = waveButtonClick
                        ? RequestWaveNavigation(windowId, wave, viewerProfile)
                        : AIrConNewApiBridge.OperationResult.Ok("navigation_not_requested");
                    if (!refresh.Success)
                        return new RuntimeUiActionResult { Succeeded = false, Message = "局一覧の更新に失敗しました。", Diagnostics = refresh.Diagnostics };
                    var foregroundAlreadyTarget = waveButtonClick && (targetWasForeground ||
                        (targetPid > 0 && AirConForegroundObserver.IsViewerProcessForeground(targetPid)));
                    var activated = foregroundAlreadyTarget
                        ? AIrConNewApiBridge.OperationResult.Ok("viewer_already_foreground")
                        : await AIrConNewApiBridge.ActivateAsync(liveSession.ViewerSessionId, liveSession.Generation).ConfigureAwait(false);
                    if (!waveButtonClick)
                        refresh = RequestWindowDisplayNavigation(windowId, wave, viewerProfile);
                    var recoveredNotRunning = activated.Success && activated.Diagnostics.Equals("viewer_process_exited_recovered", StringComparison.OrdinalIgnoreCase);
                    var success = activated.Success && refresh.Success;
                    #if AIRCON_DEVELOPER_DIAGNOSTICS
                    SafeLog($"VIEWER_MANUAL_ACTIVATE viewerProfile={viewerProfile} wave={wave} session={liveSession.ViewerSessionId} pid={liveSession.ProcessId} result={(recoveredNotRunning ? "SKIPPED" : activated.Success ? "OK" : "FAILED")}"
                        + (recoveredNotRunning ? " reason=viewer_process_exited_recovered selectionApplied=True" : string.Empty)
                        + $" diagnostics={activated.Diagnostics} refreshResult={(refresh.Success ? "OK" : "FAILED")} refreshDiagnostics={refresh.Diagnostics}");
                    #endif
                    return new RuntimeUiActionResult
                    {
                        Succeeded = success,
                        Message = recoveredNotRunning && success ? "視聴先を選択" : success ? (foregroundAlreadyTarget ? "局一覧を表示" : "TVTestを前面化") : (!activated.Success ? activated.Message : "AIrCon画面更新失敗"),
                        Diagnostics = recoveredNotRunning && success ? "viewer_not_running_selection_applied_and_refreshed" : !activated.Success ? activated.Diagnostics : refresh.Diagnostics
                    };
                }

                if (airconAction.Equals(AirConActionViewerReservationToggle, StringComparison.OrdinalIgnoreCase))
                {
                    var reservationId = PayloadValue(request, "reservationId", "reservation-id");
                    AIrConNewApiBridge.OperationResult reservationResult;
                    if (!string.IsNullOrWhiteSpace(reservationId))
                    {
                        reservationResult = await AIrConNewApiBridge.CancelViewerReservationAsync(reservationId).ConfigureAwait(false);
                    }
                    else
                    {
                        var nidText = PayloadValue(request, "networkId", "network-id", "nid");
                        var tsidText = PayloadValue(request, "transportStreamId", "transport-stream-id", "tsid");
                        var sidText = PayloadValue(request, "serviceId", "service-id", "sid");
                        var eventIdText = PayloadValue(request, "eventId", "event-id", "eid");
                        var startText = PayloadValue(request, "scheduledStart", "scheduled-start");
                        var endText = PayloadValue(request, "scheduledEnd", "scheduled-end");
                        if (!ushort.TryParse(nidText, out var nid) || !ushort.TryParse(tsidText, out var tsid) ||
                            !ushort.TryParse(sidText, out var sid) || !ushort.TryParse(eventIdText, out var eventId) ||
                            !DateTimeOffset.TryParse(startText, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var scheduledStart) ||
                            !DateTimeOffset.TryParse(endText, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var scheduledEnd))
                            return new RuntimeUiActionResult { Succeeded = false, Message = "視聴予約を登録できません。", Diagnostics = "viewer_reservation_payload_invalid" };

                        reservationResult = await AIrConNewApiBridge.CreateViewerReservationAsync(viewerProfile, nid, tsid, sid, eventId, scheduledStart, scheduledEnd).ConfigureAwait(false);
                    }

#if AIRCON_DEVELOPER_DIAGNOSTICS
                    SafeLog($"VIEWER_RESERVATION_TOGGLE viewerProfile={viewerProfile} reservation={(string.IsNullOrWhiteSpace(reservationId) ? "new" : reservationId)} result={(reservationResult.Success ? "OK" : "FAILED")} diagnostics={reservationResult.Diagnostics}");
#endif
                    var returnViewerProfile = NormalizeViewerProfileId(PayloadValue(request, "returnViewerProfile", "return-viewer-profile"));
                    if (string.IsNullOrWhiteSpace(returnViewerProfile)) returnViewerProfile = viewerProfile;
                    return new RuntimeUiActionResult
                    {
                        Succeeded = reservationResult.Success,
                        Message = reservationResult.Success ? "OK" : reservationResult.Message,
                        Diagnostics = reservationResult.Diagnostics,
                        RefreshRequested = reservationResult.Success,
                        RefreshTarget = "content",
                        PreserveScroll = true,
                        ContentRoute = BuildViewerContentRoute(wave, returnViewerProfile)
                    };
                }

                if (airconAction.Equals(AirConActionViewerTune, StringComparison.OrdinalIgnoreCase))
                {
                    var nidText = PayloadValue(request, "networkId", "network-id", "nid");
                    var tsidText = PayloadValue(request, "transportStreamId", "transport-stream-id", "tsid");
                    var sidText = PayloadValue(request, "serviceId", "service-id", "sid");
                    if (!int.TryParse(nidText, out var nid) || !int.TryParse(tsidText, out var tsid) || !int.TryParse(sidText, out var sid))
                        return new RuntimeUiActionResult { Succeeded = false, Message = "このチャンネルは現在視聴できません。", Diagnostics = "viewer_identity_invalid" };
                    var (tuned, zappingStopped) = await SwitchManualViewerServiceAsync(
                        viewerProfile, wave, nid, tsid, sid).ConfigureAwait(false);
                    // ViewerSessionChanged is the sole display-update authority for both
                    // existing retunes and first launches. The event decides whether the
                    // current HTML needs one full render to enable Viewer controls.
                    // Re-selecting the already tuned station emits no session change;
                    // recenter its existing row without navigating.
                    var selectedKey = ServiceKey(nid, tsid, sid);
                    if (tuned.Success
                        && GetRenderedServiceKey(windowId).Equals(selectedKey, StringComparison.OrdinalIgnoreCase))
                        ScheduleCurrentServiceCenter(windowId, 100, selectedKey);
#if AIRCON_DEVELOPER_DIAGNOSTICS
                    SafeLog($"VIEWER_MANUAL_PRESENTATION result={(tuned.Success ? "EVENT_OWNED" : "FAILED")} profile={viewerProfile} service={selectedKey} generation={tuned.Generation} refreshRequested=False");
#endif
                    return new RuntimeUiActionResult
                    {
                        Succeeded = tuned.Success,
                        Message = tuned.Success ? "選局" : tuned.Message,
                        Diagnostics = tuned.Diagnostics,
                        RefreshRequested = false,
                        RefreshTarget = "content",
                        PreserveScroll = true,
                        // Reuse the Stop button's canonical control/row projection.
                        // The Host session event owns any changed station selection.
                        // A repeat selection has no session event, so this also turns
                        // its existing red row back to the normal viewing highlight.
                        UiPatches = zappingStopped
                            ? BuildZappingUiPatches(false, wave, windowId)
                            : Array.Empty<RuntimeUiPatch>()
                    };
                }

                if (airconAction.Equals(AirConActionZappingStop, StringComparison.OrdinalIgnoreCase))
                {
                    await StopZappingAsync(viewerProfile).ConfigureAwait(false);
                    #if AIRCON_DEVELOPER_DIAGNOSTICS
                    SafeLog($"ZAPPING_STATE stopped windowId={windowId} wave={wave} viewerProfile={viewerProfile}");
                    #endif
                    return new RuntimeUiActionResult
                    {
                        Succeeded = true,
                        Message = "ザッピング停止",
                        Diagnostics = "zapping_stopped",
                        UiPatches = BuildZappingUiPatches(false, wave, windowId)
                    };
                }

                if (airconAction.Equals(AirConActionZappingStart, StringComparison.OrdinalIgnoreCase))
                {
                    var existing = GetActiveZappingState(viewerProfile);
                    if (existing != null)
                    {
                        if (!existing.Wave.Equals(wave, StringComparison.OrdinalIgnoreCase))
                        {
                            #if AIRCON_DEVELOPER_DIAGNOSTICS
                            SafeLog($"ZAPPING_STATE blocked viewerProfile={viewerProfile} activeWave={existing.Wave} requestedWave={wave} reason=viewer_profile_locked_by_active_zapping");
                            #endif
                            return new RuntimeUiActionResult { Succeeded = false, Message = $"巡回中（{existing.Wave}）", Diagnostics = "viewer_profile_locked_by_active_zapping" };
                        }
                        #if AIRCON_DEVELOPER_DIAGNOSTICS
                        SafeLog($"ZAPPING_STATE already_active windowId={windowId} wave={existing.Wave} viewerProfile={viewerProfile} generation={existing.Generation}");
                        #endif
                        return new RuntimeUiActionResult
                        {
                            Succeeded = true,
                            Message = $"巡回中（{existing.Wave}）",
                            Diagnostics = "zapping_already_active",
                            UiPatches = BuildZappingUiPatches(true, existing.Wave, windowId)
                        };
                    }
                    var liveSession = AIrConNewApiBridge.ListSessions().FirstOrDefault(x =>
                        IsLiveViewerSession(x)
                        && x.ViewerProfileId.Equals(viewerProfile, StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(x.ViewerSessionId)
                        && x.Generation > 0);
                    if (liveSession is null)
                    {
                        #if AIRCON_DEVELOPER_DIAGNOSTICS
                        SafeLog($"ZAPPING_STATE skipped windowId={windowId} wave={wave} viewerProfile={viewerProfile} reason=viewer_not_running");
                        #endif
                        return new RuntimeUiActionResult
                        {
                            Succeeded = true,
                            Message = "視聴中のTVTestがありません",
                            Diagnostics = "zapping_not_started_viewer_not_running",
                            UiPatches = BuildZappingUiPatches(false, wave, windowId)
                        };
                    }

                    var started = EnsureZappingActive(windowId, wave, viewerProfile);
                    #if AIRCON_DEVELOPER_DIAGNOSTICS
                    SafeLog($"ZAPPING_STATE started windowId={windowId} wave={started.Wave} viewerProfile={viewerProfile} generation={started.Generation} intervalSeconds={started.IntervalSeconds}");
                    #endif

                    // 開始時は現在局を維持し、最初の選局は設定された間隔後のserver_timerだけが行う。
                    // 開始操作からRunZappingTickAsyncを直接呼ばない。
                    return new RuntimeUiActionResult
                    {
                        Succeeded = true,
                        Message = $"巡回中（{started.Wave}）",
                        Diagnostics = "zapping_started_waiting_first_interval",
                        UiPatches = BuildZappingUiPatches(true, started.Wave, windowId)
                    };
                }

                if (airconAction.Equals(AirConActionViewerPowerOff, StringComparison.OrdinalIgnoreCase))
                {
                    var stopResult = await CompleteShutdownAsync(viewerProfile, "toolbar_power").ConfigureAwait(false);
                    var stopped = stopResult.Success;
                    #if AIRCON_DEVELOPER_DIAGNOSTICS
                    SafeLog($"VIEWER_POWER_OFF viewerProfile={viewerProfile} result={(stopped ? "OK" : "FAILED")} message={stopResult.Message}");
                    #endif
                    return new RuntimeUiActionResult { Succeeded = stopped, Message = stopped ? "視聴停止" : "視聴停止失敗", Diagnostics = stopped ? "viewer_power_off_ok" : "viewer_power_off_failed" };
                }

                if (airconAction.Equals(AirConActionPowerOffStop, StringComparison.OrdinalIgnoreCase))
                {
                    SetPowerOffStopped(viewerProfile);
                    #if AIRCON_DEVELOPER_DIAGNOSTICS
                    SafeLog($"POWER_OFF_TIMER stopped viewerProfile={viewerProfile}");
                    #endif
                    return new RuntimeUiActionResult
                    {
                        Succeeded = true,
                        Message = "終了タイマー停止",
                        Diagnostics = "power_off_stopped",
                        UiPatches = BuildPowerOffUiPatches(false, DefaultPowerOffHours)
                    };
                }

                if (airconAction.Equals(AirConActionPowerOffStart, StringComparison.OrdinalIgnoreCase))
                {
                    var hoursText = PayloadValue(request, "hours", "powerOffHours", "power-off-hours");
                    var hours = ValidatePowerOffHours(int.TryParse(hoursText, out var requestedHours)
                        ? requestedHours : DefaultPowerOffHours);
                    StartPowerOff(viewerProfile, hours, windowId);
                    #if AIRCON_DEVELOPER_DIAGNOSTICS
                    SafeLog($"POWER_OFF_TIMER started viewerProfile={viewerProfile} hours={hours}");
                    #endif
                    return new RuntimeUiActionResult
                    {
                        Succeeded = true,
                        Message = "終了タイマー開始",
                        Diagnostics = "power_off_started",
                        UiPatches = BuildPowerOffUiPatches(true, hours)
                    };
                }

                // Every accepted viewer action must have an explicit handler.
                return new RuntimeUiActionResult { Succeeded = false, Message = "操作できませんでした。", Diagnostics = "action_not_supported" };
            }

            #if AIRCON_DEVELOPER_DIAGNOSTICS
            SafeLog($"PLUGIN_ACTION ignored action={request.ActionName} airconAction={airconAction}");
            #endif
            return new RuntimeUiActionResult { Succeeded = true, Message = "OK", Diagnostics = "ignored" };
        }
        catch (Exception ex)
        {
            #if AIRCON_DEVELOPER_DIAGNOSTICS
            SafeLog("PLUGIN_ACTION failed: " + ex.GetType().Name + " " + ex.Message);
            #endif
            return new RuntimeUiActionResult { Succeeded = false, Message = "失敗", Diagnostics = ex.GetType().Name };
        }
    }

}
