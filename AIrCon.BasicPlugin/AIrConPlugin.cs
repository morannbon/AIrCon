using System.Text.Json;
using System.Net;
using TvAIrPlugin;
using TvAIrPlugin.Runtime;
using TvAIrPlugin.Viewers;

namespace AIrCon.BasicPlugin;

/// <summary>
/// AIrCon 正式リリース版。
/// ToolWindow内の行ダブルクリック視聴を主導線にする。
/// </summary>
internal sealed class AIrConRenderer
{
    internal const string PluginVersion = "1.0.3";
    internal const string PluginListTitle = "AIrCon";
    internal static string PluginToolWindowTitle => $"{PluginListTitle} {PluginVersion}";
    internal const string PluginId = "aircon.basic";
    internal const string RouteSegment = "aircon";
    private const string ClientVersion = "AIrCon-" + PluginVersion;

    private const string ResponseModeHostHandled = "hostHandled";
    private const string RefreshTargetContent = "content";
    private const string BoolTrue = "true";
    private const string BoolFalse = "false";
    private const string ActionViewerStop = "viewerStop";
    private const string ActionPluginOwned = "pluginOwnedAction";
    private const string ActionUpdateWindow = "updateWindow";
    private const string AirConActionZappingStart = "zappingStart";
    private const string AirConActionZappingStop = "zappingStop";
    private const string AirConActionZappingTick = "zappingTick";
    private const string AirConActionPowerOffStart = "powerOffStart";
    private const string AirConActionPowerOffStop = "powerOffStop";
    private const string AirConActionViewerPowerOff = "viewerPowerOff";
    private const string AirConActionViewerTune = "viewerTune";
    private const string AirConActionViewerActivate = "viewerActivate";
    private const string AirConActionSettingsOpen = "settingsOpen";
    private const string AirConActionSettingsSave = "settingsSave";
    private const string AirConActionSettingsClose = "settingsClose";
    internal const string SettingsSection = "settings";
    internal const string SettingRememberPlacement = "rememberWindowPlacement";
    internal const bool DefaultRememberWindowPlacement = false;
    internal const string SettingZappingIntervalSeconds = "zappingIntervalSeconds";
    internal const string SettingStartupWave = "startupWave";
    internal const int DefaultZappingIntervalSeconds = 60;

    // Tool-window layout metrics are centralized here.
    // WinForms WebBrowser fallback is IE-like, so CSS custom properties / flex / sticky / 100vh are avoided intentionally.
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
    private const int ToolWindowDefaultWidthPx = 540;
    private const int ToolWindowDefaultHeightPx = 320;
    private const int ToolWindowMinimumWidthContractPx = 360;
    private const int ToolWindowMinimumHeightContractPx = 180;
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
    private const string CurrentViewingAnchorId = "aircon-current-viewing-anchor";

    private static int ToolWindowToolbarButtonHeightPx => ToolWindowButtonTextLineHeightPx + (ToolWindowButtonVerticalPaddingPx * 2) + (ToolWindowButtonBorderPx * 2);
    private static int ToolWindowToolbarCellHeightPx => ToolWindowToolbarButtonHeightPx;
    private static int ToolWindowToolbarHeightPx => ToolWindowToolbarContentTopPx + ToolWindowToolbarButtonHeightPx + ToolWindowToolbarContentBottomPx + ToolWindowToolbarSeparatorPx;
    private static int ToolWindowListTopPx => ToolWindowToolbarHeightPx;
    private static int ToolWindowButtonLineHeightPx => Math.Max(1, ToolWindowToolbarButtonHeightPx - (ToolWindowButtonBorderPx * 2));
    private static int ToolWindowMinimumWidthPx => ToolWindowMinimumWidthContractPx;
    private static int ToolWindowMinimumHeightPx => Math.Max(ToolWindowMinimumHeightContractPx, ToolWindowToolbarHeightPx + (ToolWindowRowHeightPx * ToolWindowMinimumListRowsPx) + 16);

    private readonly Dictionary<string, string> _lastWaveByWindowId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _lastViewerProfileByWindowId = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _zappingLock = new();
    private readonly Dictionary<string, ZappingState> _zappingStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Timer> _zappingTimers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _powerOffLock = new();
    private readonly Dictionary<string, PowerOffState> _powerOffStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Timer> _powerOffTimers = new(StringComparer.OrdinalIgnoreCase);
    private long _powerOffGeneration;
    private readonly object _operationLock = new();
    private readonly Dictionary<string, long> _operationGenerations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SemaphoreSlim> _profileOperationGates = new(StringComparer.OrdinalIgnoreCase);

    internal void ApplyRuntimeWindowLifecycle(TvAIrPlugin.Events.PluginEventEnvelope eventEnvelope)
    {
        var lifecycle = TryReadRuntimeWindowLifecycle(eventEnvelope.Payload);
        if (lifecycle == null)
        {
            return;
        }

        if (!lifecycle.PluginId.Equals(PluginId, StringComparison.OrdinalIgnoreCase))
            return;

        if (lifecycle.State is not (TvAIrPlugin.Windows.PluginWindowLifecycleState.Closing or TvAIrPlugin.Windows.PluginWindowLifecycleState.Closed))
            return;

        if (lifecycle.BackgroundExecution != TvAIrPlugin.Windows.PluginWindowBackgroundExecution.StopWithWindow)
            return;

        StopWindowBackgroundExecution(lifecycle.WindowInstanceId);
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

    internal void StopRuntimeUi()
    {
        lock (_zappingLock)
        {
            foreach (var timer in _zappingTimers.Values) timer.Dispose();
            _zappingTimers.Clear();
            _zappingStates.Clear();
        }
        lock (_powerOffLock)
        {
            foreach (var timer in _powerOffTimers.Values) timer.Dispose();
            _powerOffTimers.Clear();
            _powerOffStates.Clear();
        }
        lock (_operationLock)
        {
            _operationGenerations.Clear();
            foreach (var gate in _profileOperationGates.Values) gate.Dispose();
            _profileOperationGates.Clear();
        }
        lock (_lastWaveByWindowId) _lastWaveByWindowId.Clear();
        lock (_lastViewerProfileByWindowId) _lastViewerProfileByWindowId.Clear();
    }

    public async Task<RuntimeUiActionResult> HandleActionAsync(RuntimeUiActionContext request, CancellationToken cancellationToken)
    {
        try
        {
            var airconAction = PayloadValue(request, "operation", "airconAction", "aircon-action");
            if (string.IsNullOrWhiteSpace(airconAction)) airconAction = request.ActionName;
            if (airconAction.Equals(AirConActionSettingsOpen, StringComparison.OrdinalIgnoreCase) ||
                airconAction.Equals(AirConActionSettingsClose, StringComparison.OrdinalIgnoreCase) ||
                airconAction.Equals(AirConActionSettingsSave, StringComparison.OrdinalIgnoreCase))
            {
                var settingsWindowId = FirstNonEmpty(request.CurrentWindowId, PayloadValue(request, "windowId", "window-id", "currentWindowId", "current-window-id"));
                var returnWave = NormalizeWaveFilter(PayloadValue(request, "returnWave", "return-wave", "wave", "currentWave", "current-wave"));
                var returnProfile = NormalizeViewerProfileId(PayloadValue(request, "returnViewerProfile", "return-viewer-profile", "viewerProfile", "viewer-profile"));
                if (airconAction.Equals(AirConActionSettingsSave, StringComparison.OrdinalIgnoreCase))
                {
                    var remember = ParseBool(PayloadValue(request, "rememberWindowPlacement", "remember-window-placement"), false);
                    var interval = NormalizeZappingInterval(PayloadValue(request, "zappingIntervalSeconds", "zapping-interval-seconds"));
                    var startupWave = NormalizeWaveFilter(PayloadValue(request, "startupWave", "startup-wave"));
                    var saved = AIrConNewApiBridge.SaveSettings(remember, interval, startupWave);
                    if (!saved.Success)
                    {
                        return new RuntimeUiActionResult { Succeeded = false, Message = "設定保存失敗", Diagnostics = saved.Diagnostics };
                    }
                    var placement = AIrConNewApiBridge.SetPlacementPersistence("main", remember);
                    if (!placement.Success)
                    {
                        return new RuntimeUiActionResult { Succeeded = false, Message = "位置記憶設定失敗", Diagnostics = placement.Diagnostics };
                    }
                    var verifiedSettings = AIrConNewApiBridge.LoadSettings();
                    if (verifiedSettings.RememberWindowPlacement != remember ||
                        verifiedSettings.ZappingIntervalSeconds != interval ||
                        !string.Equals(verifiedSettings.StartupWave, startupWave, StringComparison.OrdinalIgnoreCase))
                    {
                        return new RuntimeUiActionResult { Succeeded = false, Message = "設定保存確認失敗", Diagnostics = "settings_readback_mismatch" };
                    }
                    var route = "/plugin/aircon?wave=" + Url(returnWave) + (string.IsNullOrWhiteSpace(returnProfile) ? string.Empty : "&viewerProfile=" + Url(returnProfile));
                    var refresh = AIrConNewApiBridge.RefreshToolWindow(settingsWindowId, route);
                    return new RuntimeUiActionResult { Succeeded = refresh.Success, Message = refresh.Success ? "設定を保存" : "設定画面更新失敗", Diagnostics = refresh.Diagnostics };
                }

                var returnQuery = "returnWave=" + Url(returnWave) + (string.IsNullOrWhiteSpace(returnProfile) ? string.Empty : "&returnViewerProfile=" + Url(returnProfile));
                var targetRoute = airconAction.Equals(AirConActionSettingsOpen, StringComparison.OrdinalIgnoreCase)
                    ? "/plugin/aircon?view=settings&" + returnQuery
                    : "/plugin/aircon?wave=" + Url(returnWave) + (string.IsNullOrWhiteSpace(returnProfile) ? string.Empty : "&viewerProfile=" + Url(returnProfile));
                var settingsRefresh = AIrConNewApiBridge.RefreshToolWindow(settingsWindowId, targetRoute);
                return new RuntimeUiActionResult { Succeeded = settingsRefresh.Success, Message = settingsRefresh.Success ? "OK" : "設定画面更新失敗", Diagnostics = settingsRefresh.Diagnostics };
            }

            if (airconAction.Equals(AirConActionZappingStart, StringComparison.OrdinalIgnoreCase) ||
                airconAction.Equals(AirConActionZappingStop, StringComparison.OrdinalIgnoreCase) ||
                airconAction.Equals(AirConActionZappingTick, StringComparison.OrdinalIgnoreCase) ||
                airconAction.Equals(AirConActionPowerOffStart, StringComparison.OrdinalIgnoreCase) ||
                airconAction.Equals(AirConActionPowerOffStop, StringComparison.OrdinalIgnoreCase) ||
                airconAction.Equals(AirConActionViewerPowerOff, StringComparison.OrdinalIgnoreCase) ||
                airconAction.Equals(AirConActionViewerActivate, StringComparison.OrdinalIgnoreCase) ||
                airconAction.Equals(AirConActionViewerTune, StringComparison.OrdinalIgnoreCase))
            {
                var windowId = FirstNonEmpty(request.CurrentWindowId, PayloadValue(request, "windowId", "window-id", "currentWindowId", "current-window-id"));
                var wave = NormalizeWaveFilter(PayloadValue(request, "wave", "currentWave", "current-wave"));
                var viewerProfile = NormalizeViewerProfileId(PayloadValue(request, "viewerProfile", "viewer-profile"));
                if (string.IsNullOrWhiteSpace(viewerProfile))
                {
                    return new RuntimeUiActionResult { Succeeded = false, Message = "視聴先を選択してください。", Diagnostics = "viewer_profile_missing" };
                }

                if (airconAction.Equals(AirConActionViewerActivate, StringComparison.OrdinalIgnoreCase))
                {
                    // The toolbar selection and Viewer activation are separate responsibilities.
                    // A configured profile remains selectable even when its TVTest is not running yet.
                    // When a live session exists, activate only that exact profile; otherwise keep the
                    // selection successful so a subsequent manual double-click can start that profile.
                    var liveSession = AIrConNewApiBridge.ListSessions()
                        .Where(x => x.ProcessId.HasValue)
                        .Where(x => !x.State.Equals("stopped", StringComparison.OrdinalIgnoreCase))
                        .Where(x => !x.State.Equals("closed", StringComparison.OrdinalIgnoreCase))
                        .FirstOrDefault(x => x.ViewerProfileId.Equals(viewerProfile, StringComparison.OrdinalIgnoreCase));
                    var contentRoute = "/plugin/aircon?wave=" + Url(wave) + "&viewerProfile=" + Url(viewerProfile);
                    if (liveSession == null)
                    {
                        var refreshOnly = AIrConNewApiBridge.RefreshToolWindow(windowId, contentRoute);
                        return new RuntimeUiActionResult { Succeeded = refreshOnly.Success, Message = "視聴先を選択", Diagnostics = refreshOnly.Success ? "viewer_not_running_selection_applied_and_refreshed" : refreshOnly.Diagnostics };
                    }

                    var activated = await AIrConNewApiBridge.ActivateAsync(viewerProfile).ConfigureAwait(false);
                    var refresh = AIrConNewApiBridge.RefreshToolWindow(windowId, contentRoute);
                    var recoveredNotRunning = activated.Success && activated.Diagnostics.Equals("viewer_process_exited_recovered", StringComparison.OrdinalIgnoreCase);
                    var success = activated.Success && refresh.Success;
                    return new RuntimeUiActionResult
                    {
                        Succeeded = success,
                        Message = recoveredNotRunning && success ? "視聴先を選択" : success ? "TVTestを前面化" : (!activated.Success ? activated.Message : "AIrCon画面更新失敗"),
                        Diagnostics = recoveredNotRunning && success ? "viewer_not_running_selection_applied_and_refreshed" : !activated.Success ? activated.Diagnostics : refresh.Diagnostics
                    };
                }

                if (airconAction.Equals(AirConActionViewerTune, StringComparison.OrdinalIgnoreCase))
                {
                    var nidText = PayloadValue(request, "networkId", "network-id", "nid");
                    var tsidText = PayloadValue(request, "transportStreamId", "transport-stream-id", "tsid");
                    var sidText = PayloadValue(request, "serviceId", "service-id", "sid");
                    if (!int.TryParse(nidText, out var nid) || !int.TryParse(tsidText, out var tsid) || !int.TryParse(sidText, out var sid))
                        return new RuntimeUiActionResult { Succeeded = false, Message = "このチャンネルは現在視聴できません。", Diagnostics = "viewer_identity_invalid" };
                    var tuned = await SwitchViewerServiceAsync(viewerProfile, wave, nid, tsid, sid, "manual_dblclick", TvAirViewerActivation.Activate).ConfigureAwait(false);
                    return new RuntimeUiActionResult
                    {
                        Succeeded = tuned.Success,
                        Message = tuned.Success ? "選局" : tuned.Message,
                        Diagnostics = tuned.Diagnostics,
                        RefreshRequested = tuned.Success,
                        RefreshTarget = "content",
                        PreserveScroll = false,
                        ContentRoute = "/plugin/aircon?wave=" + Url(wave) + "&viewerProfile=" + Url(viewerProfile)
                    };
                }

                if (airconAction.Equals(AirConActionZappingStop, StringComparison.OrdinalIgnoreCase))
                {
                    await StopZappingAsync(viewerProfile).ConfigureAwait(false);
                    return new RuntimeUiActionResult
                    {
                        Succeeded = true,
                        Message = "ザッピング停止",
                        Diagnostics = "zapping_stopped",
                        UiPatches = BuildZappingUiPatches(false, wave)
                    };
                }

                if (airconAction.Equals(AirConActionZappingStart, StringComparison.OrdinalIgnoreCase))
                {
                    var existing = GetActiveZappingState(viewerProfile);
                    if (existing != null)
                    {
                        if (!existing.Wave.Equals(wave, StringComparison.OrdinalIgnoreCase))
                        {
                            return new RuntimeUiActionResult { Succeeded = false, Message = $"巡回中（{existing.Wave}）", Diagnostics = "viewer_profile_locked_by_active_zapping" };
                        }
                        return new RuntimeUiActionResult
                        {
                            Succeeded = true,
                            Message = $"巡回中（{existing.Wave}）",
                            Diagnostics = "zapping_already_active",
                            UiPatches = BuildZappingUiPatches(true, existing.Wave)
                        };
                    }
                    var liveSession = AIrConNewApiBridge.ListSessions().FirstOrDefault(x =>
                        x.ViewerProfileId.Equals(viewerProfile, StringComparison.Ordinal) &&
                        !string.IsNullOrWhiteSpace(x.ViewerSessionId) &&
                        x.Generation > 0 &&
                        x.ProcessId is > 0);
                    if (liveSession is null)
                    {
                        return new RuntimeUiActionResult
                        {
                            Succeeded = true,
                            Message = "視聴中のTVTestがありません",
                            Diagnostics = "zapping_not_started_viewer_not_running",
                            UiPatches = BuildZappingUiPatches(false, wave)
                        };
                    }

                    var started = EnsureZappingActive(windowId, wave, viewerProfile);

                    // 開始時は現在局を維持し、最初の選局は設定された間隔後のserver_timerだけが行う。
                    // 開始操作からRunZappingTickAsyncを直接呼ばない。
                    return new RuntimeUiActionResult
                    {
                        Succeeded = true,
                        Message = $"巡回中（{started.Wave}）",
                        Diagnostics = "zapping_started_waiting_first_interval",
                        UiPatches = BuildZappingUiPatches(true, started.Wave)
                    };
                }

                if (airconAction.Equals(AirConActionViewerPowerOff, StringComparison.OrdinalIgnoreCase))
                {
                    var stopResult = await CompleteShutdownAsync(viewerProfile, windowId, "toolbar_power").ConfigureAwait(false);
                    var stopped = stopResult.Success;
                    return new RuntimeUiActionResult { Succeeded = stopped, Message = stopped ? "視聴停止" : "視聴停止失敗", Diagnostics = stopped ? "viewer_power_off_ok" : "viewer_power_off_failed" };
                }

                if (airconAction.Equals(AirConActionPowerOffStop, StringComparison.OrdinalIgnoreCase))
                {
                    SetPowerOffStopped(viewerProfile);
                    return new RuntimeUiActionResult
                    {
                        Succeeded = true,
                        Message = "終了タイマー停止",
                        Diagnostics = "power_off_stopped",
                        UiPatches = BuildPowerOffUiPatches(false, 1)
                    };
                }

                if (airconAction.Equals(AirConActionPowerOffStart, StringComparison.OrdinalIgnoreCase))
                {
                    var hoursText = PayloadValue(request, "hours", "powerOffHours", "power-off-hours");
                    if (!int.TryParse(hoursText, out var hours) || hours < 1 || hours > 6) hours = 1;
                    StartPowerOff(viewerProfile, hours, windowId);
                    return new RuntimeUiActionResult
                    {
                        Succeeded = true,
                        Message = "終了タイマー開始",
                        Diagnostics = "power_off_started",
                        UiPatches = BuildPowerOffUiPatches(true, hours)
                    };
                }

                var tick = await RunZappingTickAsync(viewerProfile, null, "plugin_action").ConfigureAwait(false);
                return new RuntimeUiActionResult { Succeeded = tick.Success, Message = tick.Success ? "ザッピング中" : "ザッピング停止", Diagnostics = tick.Diagnostics };
            }

            return new RuntimeUiActionResult { Succeeded = true, Message = "OK", Diagnostics = "ignored" };
        }
        catch (Exception ex)
        {
            return new RuntimeUiActionResult { Succeeded = false, Message = "失敗", Diagnostics = ex.GetType().Name };
        }
    }

    private static IReadOnlyList<RuntimeUiPatch> BuildZappingUiPatches(bool active, string wave)
    {
        var normalizedWave = NormalizeWaveFilter(wave);
        var nextOperation = active ? AirConActionZappingStop : AirConActionZappingStart;
        var intervalSeconds = GetZappingIntervalSeconds();
        return new RuntimeUiPatch[]
        {
            RuntimeUiPatch.Text("aircon-zapping-status", active ? $"巡回中（{normalizedWave}）" : "停止中"),
            RuntimeUiPatch.Classes("aircon-zapping-status",
                add: new[] { active ? "aircon-zapping-status-on" : "aircon-zapping-status-off" },
                remove: new[] { active ? "aircon-zapping-status-off" : "aircon-zapping-status-on" }),
            RuntimeUiPatch.Text("aircon-zapping-button", active ? "停止" : "開始"),
            RuntimeUiPatch.Classes("aircon-zapping-button",
                add: active ? new[] { "aircon-zapping-button-on" } : Array.Empty<string>(),
                remove: active ? Array.Empty<string>() : new[] { "aircon-zapping-button-on" }),
            // The zapping state becomes authoritative at Start/Stop action completion, before the
            // first timer-driven retune. Keep the current Viewer row's semantic class in the same
            // declarative patch transaction so visual state never lags the automation state.
            RuntimeUiPatch.Classes(CurrentViewingAnchorId,
                add: new[] { active ? "aircon-row-zapping-selected" : "aircon-row-viewing-selected" },
                remove: new[] { active ? "aircon-row-viewing-selected" : "aircon-row-zapping-selected" }),
            new RuntimeUiPatch
            {
                ElementId = "aircon-zapping-button",
                Attributes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["title"] = active ? "ザッピングを停止" : $"{intervalSeconds}秒ごとに同じ放送波内で順送りします",
                    ["data-tvair-payload-operation"] = nextOperation,
                    ["data-tvair-payload-airconaction"] = nextOperation
                }
            },
            new RuntimeUiPatch
            {
                ElementId = "aircon-zapping-bar",
                Attributes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["data-aircon-zapping-active"] = active ? BoolTrue : BoolFalse,
                    ["data-aircon-zapping-wave"] = normalizedWave
                }
            }
        };
    }

    private static IReadOnlyList<RuntimeUiPatch> BuildPowerOffUiPatches(bool active, int hours)
    {
        var normalizedHours = Math.Max(1, Math.Min(6, hours));
        var nextOperation = active ? AirConActionPowerOffStop : AirConActionPowerOffStart;
        return new RuntimeUiPatch[]
        {
            RuntimeUiPatch.Text("aircon-sleep-label", active ? "終了まで" : "電源OFF"),
            RuntimeUiPatch.Visibility("aircon-sleep-select", visible: !active),
            RuntimeUiPatch.Visibility("aircon-sleep-remaining", visible: active),
            RuntimeUiPatch.Text("aircon-sleep-remaining", normalizedHours.ToString(System.Globalization.CultureInfo.InvariantCulture) + "h"),
            RuntimeUiPatch.Text("aircon-sleep-button", active ? "停止" : "開始"),
            RuntimeUiPatch.Classes("aircon-sleep-button",
                add: active ? new[] { "aircon-sleep-button-on" } : Array.Empty<string>(),
                remove: active ? Array.Empty<string>() : new[] { "aircon-sleep-button-on" }),
            new RuntimeUiPatch
            {
                ElementId = "aircon-sleep-button",
                Attributes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["title"] = active ? "終了タイマーを停止" : "選択中TVTestの終了タイマーを開始",
                    ["data-tvair-payload-operation"] = nextOperation
                }
            }
        };
    }

    public string RenderHtml(RuntimeUiRenderContext context)
    {
        try
        {
            var isToolWindow = IsToolWindow(context);
            var query = ExtractQueryDictionary(context);
            var requestedWave = QueryString(query, "wave");
            var requestedView = QueryString(query, "view");
            var windowIdForWave = QueryWindowId(context);
            if (isToolWindow) PruneSupersededWindowState(windowIdForWave);
            var filter = ResolveEffectiveWave(requestedWave, windowIdForWave, isToolWindow);
            if (isToolWindow && requestedView.Equals("settings", StringComparison.OrdinalIgnoreCase))
            {
                var settingsAction = CaptureAction(context);
                var settingsWindow = CaptureWindow(context, true);
                var returnWave = NormalizeWaveFilter(QueryString(query, "returnWave"));
                var returnViewerProfile = NormalizeViewerProfileId(QueryString(query, "returnViewerProfile"));
                return BuildSettingsHtml(context, settingsAction, settingsWindow, AIrConNewApiBridge.LoadSettings(), returnWave, returnViewerProfile);
            }
            RememberWave(windowIdForWave, filter, isToolWindow);
            var selectedTunerValue = "auto";
            var action = CaptureAction(context);
            var window = CaptureWindow(context, isToolWindow);
            var alwaysOnTop = ResolveWindowAlwaysOnTop(context, window, isToolWindow);
            var viewerProfiles = CaptureViewerProfiles(context);
            var requestedViewerProfile = ResolveRequestedViewerProfile(query, windowIdForWave);
            var selectedViewerProfile = ResolveSelectedViewerProfile(requestedViewerProfile, viewerProfiles, filter);
            RememberViewerProfile(windowIdForWave, selectedViewerProfile.Value, isToolWindow);
            var focusTriplet = new FocusTriplet(null, null, null);
            var activeZappingState = GetActiveZappingState(selectedViewerProfile.Value);
            var zappingActive = activeZappingState != null;
            var activeZappingWave = activeZappingState?.Wave ?? string.Empty;
            if (zappingActive && activeZappingWave.Equals(filter, StringComparison.OrdinalIgnoreCase)) UpdateZappingWindowSubscription(selectedViewerProfile.Value, windowIdForWave, filter);
            var powerOffDeadline = ResolvePowerOffDeadline(selectedViewerProfile.Value);
            var data = CaptureData(filter, viewerProfiles, focusTriplet);

            return isToolWindow
                ? BuildFloatingViewerHtml(context, data, action, window, filter, selectedTunerValue, selectedViewerProfile, alwaysOnTop, zappingActive, activeZappingWave, powerOffDeadline)
                : BuildLauncherHtml(context, data, window, filter, selectedTunerValue, selectedViewerProfile.Value, alwaysOnTop);
        }
        catch (Exception)
        {
            return BuildRenderFailureHtml(context);
        }
    }

    private string ResolveEffectiveWave(string requestedWave, string windowId, bool isToolWindow)
    {
        var normalized = NormalizeWaveFilter(requestedWave);
        if (!string.IsNullOrWhiteSpace(requestedWave)) return normalized;
        if (isToolWindow && !string.IsNullOrWhiteSpace(windowId))
        {
            lock (_lastWaveByWindowId)
            {
                if (_lastWaveByWindowId.TryGetValue(windowId, out var remembered) && !string.IsNullOrWhiteSpace(remembered)) return NormalizeWaveFilter(remembered);
            }
        }
        return AIrConNewApiBridge.LoadSettings().StartupWave;
    }

    private static int GetZappingIntervalSeconds() => AIrConNewApiBridge.LoadSettings().ZappingIntervalSeconds;

    private static bool ParseBool(string value, bool fallback)
    {
        return bool.TryParse(value, out var parsed) ? parsed : value == "1" ? true : value == "0" ? false : fallback;
    }

    private static int NormalizeZappingInterval(string value)
    {
        return int.TryParse(value, out var parsed) && parsed is 30 or 60 or 90 or 120 or 180 ? parsed : DefaultZappingIntervalSeconds;
    }

    private void RememberWave(string windowId, string wave, bool isToolWindow)
    {
        if (!isToolWindow || string.IsNullOrWhiteSpace(windowId)) return;
        var normalized = NormalizeWaveFilter(wave);
        lock (_lastWaveByWindowId) _lastWaveByWindowId[windowId] = normalized;
    }

    private bool IsWindowDisplaying(string windowId, string wave, string viewerProfile)
    {
        if (string.IsNullOrWhiteSpace(windowId) || string.IsNullOrWhiteSpace(viewerProfile)) return false;
        string displayedWave;
        string displayedProfile;
        lock (_lastWaveByWindowId)
        {
            if (!_lastWaveByWindowId.TryGetValue(windowId, out displayedWave!)) return false;
        }
        lock (_lastViewerProfileByWindowId)
        {
            if (!_lastViewerProfileByWindowId.TryGetValue(windowId, out displayedProfile!)) return false;
        }
        return NormalizeWaveFilter(displayedWave).Equals(NormalizeWaveFilter(wave), StringComparison.OrdinalIgnoreCase)
            && displayedProfile.Equals(viewerProfile, StringComparison.OrdinalIgnoreCase);
    }

    private ZappingState? GetActiveZappingState(string viewerProfile)
    {
        var key = BuildZappingKey(viewerProfile);
        lock (_zappingLock)
        {
            return _zappingStates.TryGetValue(key, out var state) && state.Active ? state : null;
        }
    }

    private void UpdateZappingWindowSubscription(string viewerProfile, string windowId, string wave)
    {
        if (string.IsNullOrWhiteSpace(windowId)) return;
        var key = BuildZappingKey(viewerProfile);
        lock (_zappingLock)
        {
            if (!_zappingStates.TryGetValue(key, out var state) || !state.Active) return;
            if (!state.Wave.Equals(NormalizeWaveFilter(wave), StringComparison.OrdinalIgnoreCase)) return;
            if (state.WindowId.Equals(windowId, StringComparison.OrdinalIgnoreCase)) return;
            _zappingStates[key] = state with { WindowId = windowId };
        }
    }

    private ZappingState EnsureZappingActive(string windowId, string wave, string viewerProfile)
    {
        var key = BuildZappingKey(viewerProfile);
        lock (_zappingLock)
        {
            if (_zappingStates.TryGetValue(key, out var existing) && existing.Active) return existing;
            var liveSession = AIrConNewApiBridge.ListSessions().FirstOrDefault(x => x.ViewerProfileId.Equals(viewerProfile, StringComparison.Ordinal));
            if (liveSession is null || string.IsNullOrWhiteSpace(liveSession.ViewerSessionId) || liveSession.Generation <= 0 || liveSession.ProcessId is null or <= 0)
                throw new InvalidOperationException("ザッピング対象の視聴状態を確認できません。");
            var generation = NextOperationGeneration(viewerProfile);
            var now = DateTimeOffset.Now;
            var intervalSeconds = GetZappingIntervalSeconds();

            // The first scheduled tick must advance from the service that is already being viewed.
            // Do not rely on the rendered service projection's IsViewing flag here: the AIrCon
            // window can display another wave/profile while this Viewer continues zapping in the
            // background.  The Runtime Viewer Session is the authoritative current-service source.
            var currentServiceKey = liveSession.CurrentService is { NetworkId: > 0, TransportStreamId: > 0, ServiceId: > 0 } currentService
                ? ServiceKey(currentService.NetworkId, currentService.TransportStreamId, currentService.ServiceId)
                : string.Empty;

            var created = new ZappingState(true, now, DateTimeOffset.MinValue, now.AddSeconds(intervalSeconds), currentServiceKey, windowId ?? string.Empty, NormalizeWaveFilter(wave), viewerProfile, generation, liveSession.ViewerSessionId, liveSession.Generation, liveSession.ProcessId.Value, intervalSeconds);
            _zappingStates[key] = created;
            ScheduleNextZappingTickLocked(key, created);
            return created;
        }
    }

    private async Task StopZappingAsync(string viewerProfile)
    {
        NextOperationGeneration(viewerProfile);
        RemoveZappingState(viewerProfile);

        // Drain any dispatch already in flight. A queued old tick will fail its generation check.
        var gate = GetProfileOperationGate(viewerProfile);
        await gate.WaitAsync().ConfigureAwait(false);
        gate.Release();
    }

    private void RemoveZappingState(string viewerProfile)
    {
        var key = BuildZappingKey(viewerProfile);
        lock (_zappingLock)
        {
            _zappingStates.Remove(key);
            if (_zappingTimers.Remove(key, out var timer)) timer.Dispose();
        }
    }

    private void ScheduleNextZappingTickLocked(string key, ZappingState state)
    {
        if (_zappingTimers.Remove(key, out var oldTimer)) oldTimer.Dispose();
        var due = state.NextTickAt - DateTimeOffset.Now;
        if (due < TimeSpan.Zero) due = TimeSpan.Zero;
        _zappingTimers[key] = new Timer(
            _ => _ = RunZappingTickAsync(state.ViewerProfile, state.Generation, "server_timer"),
            null,
            due,
            Timeout.InfiniteTimeSpan);
    }

    private async Task<ZappingTickResult> RunZappingTickAsync(string viewerProfile, long? expectedGeneration, string source)
    {
        var gate = GetProfileOperationGate(viewerProfile);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var key = BuildZappingKey(viewerProfile);
            ZappingState state;
            lock (_zappingLock)
            {
                if (!_zappingStates.TryGetValue(key, out state!) || !state.Active ||
                    (expectedGeneration.HasValue && state.Generation != expectedGeneration.Value) ||
                    state.Generation != CurrentOperationGeneration(viewerProfile))
                {
                    return new ZappingTickResult(false, "zapping_inactive_or_superseded");
                }
            }

            var wave = state.Wave;
            var windowId = state.WindowId;

            // BackgroundExecution=StopWithWindow is owned by the ToolWindow that started the
            // automation.  The Refresh contract may be used as the owner preflight only when this
            // exact wave/profile is currently rendered.  A background zapping profile must never
            // refresh or rerender the ToolWindow merely as a liveness probe, because that crosses
            // the Plugin semantic-state boundary and can disturb the visible wave/profile.
            // This remains intentionally not Windows.Get/List.
            if (!string.IsNullOrWhiteSpace(windowId) && IsWindowDisplaying(windowId, wave, viewerProfile))
            {
                var ownerRefresh = AIrConNewApiBridge.RefreshToolWindow(windowId, null);
                if (!ownerRefresh.Success && IsDefinitiveWindowGone(ownerRefresh.Diagnostics))
                {
                    StopWindowBackgroundExecution(windowId);
                    return new ZappingTickResult(false, "zapping_window_closed");
                }
            }
            else if (!string.IsNullOrWhiteSpace(windowId))
            {
            }

            var services = CaptureServiceProjection(wave)
                .Where(HasResolvedTriplet)
                .OrderBy(x => x.ProgramGuideOrder)
                .ToList();
            if (services.Count == 0)
            {
                RemoveZappingState(viewerProfile);
                return new ZappingTickResult(false, "zapping_no_services");
            }

            var lastIndex = -1;
            if (!string.IsNullOrWhiteSpace(state.LastServiceKey))
            {
                lastIndex = services.FindIndex(x => ServiceKey(x.NetworkId, x.TransportStreamId, x.ServiceId).Equals(state.LastServiceKey, StringComparison.OrdinalIgnoreCase));
            }
            if (lastIndex < 0)
            {
                lastIndex = services.FindIndex(x => x.IsViewing && (string.IsNullOrWhiteSpace(x.ViewingViewerProfile) || x.ViewingViewerProfile.Equals(viewerProfile, StringComparison.OrdinalIgnoreCase)));
            }

            var nextIndex = (lastIndex + 1) % services.Count;
            var target = services[nextIndex];

            // Stop/shutdown may have superseded this tick while channel data was being captured.
            if (!IsCurrentZappingGeneration(viewerProfile, state.Generation))
            {
                return new ZappingTickResult(false, "zapping_superseded");
            }

            var liveSession = AIrConNewApiBridge.ListSessions().FirstOrDefault(x => x.ViewerSessionId.Equals(state.ViewerSessionId, StringComparison.Ordinal));
            if (liveSession is null || !liveSession.ViewerProfileId.Equals(viewerProfile, StringComparison.Ordinal) || liveSession.ProcessId != state.ProcessId || liveSession.Generation != state.ViewerGeneration)
            {
                RemoveZappingState(viewerProfile);
                return new ZappingTickResult(false, "zapping_viewer_identity_changed");
            }
            var result = await SwitchViewerServiceCoreAsync(
                viewerProfile,
                wave,
                target.NetworkId,
                target.TransportStreamId,
                target.ServiceId,
                "zapping_" + source,
                TvAirViewerActivation.Preserve,
                () => IsCurrentZappingGeneration(viewerProfile, state.Generation)).ConfigureAwait(false);
            var mayContinue = result.OperationCompleted && result.ContinuationRecommended;
            var serviceKey = mayContinue
                ? ServiceKey(target.NetworkId, target.TransportStreamId, target.ServiceId)
                : state.LastServiceKey;
            if (mayContinue)
            {
                var replacement = FindActiveViewerSession(viewerProfile);
                if (replacement == null || string.IsNullOrWhiteSpace(replacement.ViewerSessionId) || replacement.ProcessId.GetValueOrDefault() <= 0)
                {
                    RemoveZappingState(viewerProfile);
                    return new ZappingTickResult(false, "zapping_replacement_identity_missing");
                }
                var replacementProcessId = replacement.ProcessId.GetValueOrDefault();
                ScheduleZappingAfterAttempt(viewerProfile, state.Generation, serviceKey, replacement.ViewerSessionId, replacement.Generation, replacementProcessId);
            }
            else
            {
                RemoveZappingState(viewerProfile);
            }

            if (mayContinue)
            {
                // Keep the visible zapping wave current without activating or revealing AIrCon.
                // When another wave/profile is displayed, update only the Viewer state and leave
                // the current content untouched; selecting the zapping wave later performs one
                // fresh render and centers the authoritative current Viewer row.
                if (IsWindowDisplaying(windowId, wave, viewerProfile))
                {
                    var refresh = AIrConNewApiBridge.RefreshToolWindow(windowId, null);
                    if (!refresh.Success && IsDefinitiveWindowGone(refresh.Diagnostics))
                    {
                        RemoveZappingState(viewerProfile);
                        StopPowerOffForWindow(viewerProfile, windowId);
                    }
                }
                else
                {
                }
            }
            return new ZappingTickResult(result.Success, result.Success ? "zapping_tick_ok" : "zapping_tick_failed");
        }
        catch (Exception)
        {
            RemoveZappingState(viewerProfile);
            return new ZappingTickResult(false, "zapping_tick_exception");
        }
        finally
        {
            gate.Release();
        }
    }

    private void ScheduleZappingAfterAttempt(string viewerProfile, long generation, string serviceKey, string viewerSessionId, long viewerGeneration, int processId)
    {
        var key = BuildZappingKey(viewerProfile);
        var now = DateTimeOffset.Now;
        lock (_zappingLock)
        {
            if (!_zappingStates.TryGetValue(key, out var existing) || !existing.Active || existing.Generation != generation || generation != CurrentOperationGeneration(viewerProfile)) return;
            var updated = existing with
            {
                LastTickAt = now,
                NextTickAt = now.AddSeconds(existing.IntervalSeconds),
                LastServiceKey = serviceKey ?? string.Empty,
                ViewerSessionId = viewerSessionId,
                ViewerGeneration = viewerGeneration,
                ProcessId = processId
            };
            _zappingStates[key] = updated;
            ScheduleNextZappingTickLocked(key, updated);
        }
    }

    private bool IsCurrentZappingGeneration(string viewerProfile, long generation)
    {
        var key = BuildZappingKey(viewerProfile);
        lock (_zappingLock)
        {
            return _zappingStates.TryGetValue(key, out var state) && state.Active && state.Generation == generation && generation == CurrentOperationGeneration(viewerProfile);
        }
    }

    private void StartPowerOff(string viewerProfile, int hours, string windowId)
    {
        var key = BuildZappingKey(viewerProfile);
        var deadline = DateTimeOffset.Now.AddHours(hours);
        var generation = Interlocked.Increment(ref _powerOffGeneration);
        lock (_powerOffLock)
        {
            if (_powerOffTimers.Remove(key, out var oldTimer)) oldTimer.Dispose();
            _powerOffStates[key] = new PowerOffState(true, deadline, viewerProfile, windowId ?? string.Empty, generation);
            _powerOffTimers[key] = new Timer(
                _ => _ = RunPowerOffTimerAsync(viewerProfile, windowId ?? string.Empty, deadline, generation),
                null,
                deadline - DateTimeOffset.Now,
                Timeout.InfiniteTimeSpan);
        }
    }

    private bool TryClaimPowerOffExpiry(string viewerProfile, string windowId, DateTimeOffset deadline, long generation)
    {
        var key = BuildZappingKey(viewerProfile);
        lock (_powerOffLock)
        {
            if (!_powerOffStates.TryGetValue(key, out var state) || !state.Active ||
                state.Generation != generation || state.Deadline != deadline ||
                !state.WindowId.Equals(windowId ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                return false;

            _powerOffStates.Remove(key);
            if (_powerOffTimers.Remove(key, out var timer)) timer.Dispose();
            return true;
        }
    }

    private async Task RunPowerOffTimerAsync(string viewerProfile, string windowId, DateTimeOffset deadline, long generation)
    {
        if (!TryClaimPowerOffExpiry(viewerProfile, windowId, deadline, generation))
        {
            return;
        }


        // StopWithWindowはHostの別状態照会で推測しない。実際のRefresh契約だけを
        // liveness evidenceとして使い、EntityNotFound等が返った場合だけtimerを収束させる。
        // これによりlive ToolWindowを誤ってdead判定せず、stale WindowIdでViewerを停止しない。
        if (!string.IsNullOrWhiteSpace(windowId))
        {
            var windowRefresh = AIrConNewApiBridge.RefreshToolWindow(windowId, null);
            if (!windowRefresh.Success && IsDefinitiveWindowGone(windowRefresh.Diagnostics))
            {
                RemoveZappingState(viewerProfile);
                StopPowerOffForWindow(viewerProfile, windowId);
                return;
            }
        }

        HostActionDispatchResult result;
        try
        {
            result = await CompleteShutdownAsync(viewerProfile, windowId, "power_off_timer").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result = HostActionDispatchResult.Failure("power_off_timer_exception", ex.Message);
        }

        // The server-side expiry path has no Safe Event response that can rerender AIrCon.
        // CompleteShutdownAsync already clears both zapping and power-off state before stopping
        // the Viewer, so passively refresh the existing ToolWindow after completion to replace
        // the stale "巡回中" / timer display with the stopped state. Do not activate, reveal,
        // restore, reposition, or scroll the window during this passive state synchronization.
        if (!string.IsNullOrWhiteSpace(windowId))
        {
            var refresh = AIrConNewApiBridge.RefreshToolWindow(windowId, string.Empty);
        }
    }

    private void SetPowerOffStopped(string viewerProfile)
    {
        var key = BuildZappingKey(viewerProfile);
        lock (_powerOffLock)
        {
            _powerOffStates.Remove(key);
            if (_powerOffTimers.Remove(key, out var timer)) timer.Dispose();
        }
    }

    private void StopPowerOffForWindow(string viewerProfile, string windowId)
    {
        if (string.IsNullOrWhiteSpace(windowId)) return;
        var key = BuildZappingKey(viewerProfile);
        lock (_powerOffLock)
        {
            if (!_powerOffStates.TryGetValue(key, out var state) || !state.Active ||
                !state.WindowId.Equals(windowId, StringComparison.OrdinalIgnoreCase)) return;
            _powerOffStates.Remove(key);
            if (_powerOffTimers.Remove(key, out var timer)) timer.Dispose();
        }
    }

    private void PruneSupersededWindowState(string currentWindowId)
    {
        if (string.IsNullOrWhiteSpace(currentWindowId)) return;

        // ReusePolicy=PerRoute means AIrCon owns at most one live ToolWindow for this route.
        // A different WindowId therefore represents a disposed/superseded Host session.
        // Remove its Plugin-owned semantic/background state instead of retaining one entry per reopen.
        string[] staleWindowIds;
        lock (_lastWaveByWindowId)
        lock (_lastViewerProfileByWindowId)
        {
            staleWindowIds = _lastWaveByWindowId.Keys
                .Concat(_lastViewerProfileByWindowId.Keys)
                .Where(x => !string.IsNullOrWhiteSpace(x) && !x.Equals(currentWindowId, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        foreach (var staleWindowId in staleWindowIds)
        {
            StopWindowBackgroundExecution(staleWindowId);
        }
    }

    private void StopWindowBackgroundExecution(string windowId)
    {
        if (string.IsNullOrWhiteSpace(windowId)) return;

        lock (_zappingLock)
        {
            var keys = _zappingStates
                .Where(x => x.Value.WindowId.Equals(windowId, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Key)
                .ToArray();
            foreach (var key in keys)
            {
                _zappingStates.Remove(key);
                if (_zappingTimers.Remove(key, out var timer)) timer.Dispose();
            }
        }

        lock (_powerOffLock)
        {
            var keys = _powerOffStates
                .Where(x => x.Value.WindowId.Equals(windowId, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Key)
                .ToArray();
            foreach (var key in keys)
            {
                _powerOffStates.Remove(key);
                if (_powerOffTimers.Remove(key, out var timer)) timer.Dispose();
            }
        }

        lock (_lastWaveByWindowId) _lastWaveByWindowId.Remove(windowId);
        lock (_lastViewerProfileByWindowId) _lastViewerProfileByWindowId.Remove(windowId);
    }

    private static bool IsDefinitiveWindowGone(string diagnostics)
    {
        if (string.IsNullOrWhiteSpace(diagnostics)) return false;
        return diagnostics.Equals("EntityNotFound", StringComparison.OrdinalIgnoreCase)
            || diagnostics.Equals("window_closed", StringComparison.OrdinalIgnoreCase)
            || diagnostics.Equals("window_closing", StringComparison.OrdinalIgnoreCase);
    }

    private DateTimeOffset? ResolvePowerOffDeadline(string viewerProfile)
    {
        var key = BuildZappingKey(viewerProfile);
        lock (_powerOffLock)
        {
            if (_powerOffStates.TryGetValue(key, out var state) && state.Active && state.Deadline > DateTimeOffset.Now) return state.Deadline;
        }
        return null;
    }

    private async Task<HostActionDispatchResult> CompleteShutdownAsync(string viewerProfile, string? windowId, string origin)
    {
        var generation = NextOperationGeneration(viewerProfile);
        RemoveZappingState(viewerProfile);
        SetPowerOffStopped(viewerProfile);

        var gate = GetProfileOperationGate(viewerProfile);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var result = await DispatchViewerStopForProfileAsync(viewerProfile, windowId ?? string.Empty, origin).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex)
        {
            return HostActionDispatchResult.Failure("viewer_stop_exception", ex.Message);
        }
        finally
        {
            gate.Release();
        }
    }


    private async Task<AIrConNewApiBridge.OperationResult> SwitchViewerServiceAsync(string viewerProfile, string wave, int networkId, int transportStreamId, int serviceId, string source, string viewerActivation)
    {
        var gate = GetProfileOperationGate(viewerProfile);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await SwitchViewerServiceCoreAsync(viewerProfile, wave, networkId, transportStreamId, serviceId, source, viewerActivation, null).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<AIrConNewApiBridge.OperationResult> SwitchViewerServiceCoreAsync(
        string viewerProfile,
        string wave,
        int networkId,
        int transportStreamId,
        int serviceId,
        string source,
        string viewerActivation,
        Func<bool>? continuationGuard)
    {
        if (continuationGuard != null && !continuationGuard())
            return AIrConNewApiBridge.OperationResult.Fail("superseded_before_tune", "Viewer switch was superseded before tuning.");

        var before = FindActiveViewerSession(viewerProfile);
        var start = await AIrConNewApiBridge.TuneAsync(
            viewerProfile,
            wave,
            networkId,
            transportStreamId,
            serviceId,
            viewerActivation: viewerActivation).ConfigureAwait(false);
        if (!start.Success)
        {
            return start;
        }

        if (continuationGuard != null && !continuationGuard())
            return AIrConNewApiBridge.OperationResult.Fail("superseded_after_tune", "Viewer switch was superseded after tuning.");

        if (start.NetworkId != networkId || start.TransportStreamId != transportStreamId || start.ServiceId != serviceId)
        {
            return AIrConNewApiBridge.OperationResult.Fail("viewer_operation_identity_mismatch", "Viewer operation returned a different service identity.");
        }

        AIrConNewApiBridge.InvalidateViewerProjection();
        return start;
    }

    private async Task<HostActionDispatchResult> DispatchViewerStopForProfileAsync(string viewerProfile, string windowId, string origin)
    {
        var result = await AIrConNewApiBridge.StopAsync(viewerProfile).ConfigureAwait(false);
        if (result.Success) AIrConNewApiBridge.InvalidateViewerProjection();
        return result.Success ? HostActionDispatchResult.Ok(result.Diagnostics) : HostActionDispatchResult.Failure(result.Diagnostics, result.Message);
    }

    private static void AppendViewerSessionContractFields(Dictionary<string, string?> fields, ViewerSessionContractState? sessionContract)
    {
        if (sessionContract == null || string.IsNullOrWhiteSpace(sessionContract.ViewerSessionId) || sessionContract.Generation <= 0) return;
        fields["viewerSessionId"] = sessionContract.ViewerSessionId;
        fields["expectedGeneration"] = sessionContract.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private ViewerSessionRow? FindActiveViewerSession(string viewerProfile)
    {
        try
        {
            return CaptureViewerSessionsNewApi(new List<string>())
                .Where(x => x.ViewerProfile.Equals(viewerProfile, StringComparison.OrdinalIgnoreCase))
                .Where(x => x.IsActive && !string.IsNullOrWhiteSpace(x.ViewerSessionId))
                .OrderByDescending(x => x.Current)
                .FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private SemaphoreSlim GetProfileOperationGate(string viewerProfile)
    {
        var key = BuildZappingKey(viewerProfile);
        lock (_operationLock)
        {
            if (!_profileOperationGates.TryGetValue(key, out var gate))
            {
                gate = new SemaphoreSlim(1, 1);
                _profileOperationGates[key] = gate;
            }
            return gate;
        }
    }

    private long NextOperationGeneration(string viewerProfile)
    {
        var key = BuildZappingKey(viewerProfile);
        lock (_operationLock)
        {
            var next = _operationGenerations.TryGetValue(key, out var current) ? checked(current + 1) : 1L;
            _operationGenerations[key] = next;
            return next;
        }
    }

    private long CurrentOperationGeneration(string viewerProfile)
    {
        var key = BuildZappingKey(viewerProfile);
        lock (_operationLock)
        {
            return _operationGenerations.TryGetValue(key, out var current) ? current : 0L;
        }
    }

    private static string BuildZappingKey(string viewerProfile)
    {
        return NormalizeViewerProfileId(viewerProfile);
    }

    internal void ApplyViewerSessionStatePatch(TvAIrPlugin.Events.PluginEventEnvelope eventEnvelope)
    {
        if (eventEnvelope.Sequence <= 0)
        {
            return;
        }

        var viewerSessionId = ResolveViewerSessionId(eventEnvelope.EntityId);
        if (string.IsNullOrWhiteSpace(viewerSessionId))
        {
            return;
        }

        var eventSession = AIrConNewApiBridge.GetSession(viewerSessionId);
        if (eventSession == null || string.IsNullOrWhiteSpace(eventSession.ViewerProfileId))
        {
            return;
        }

        var viewerProfile = eventSession.ViewerProfileId.Trim();
        var active = eventSession.ProcessId is > 0
            && !eventSession.State.Equals("stopped", StringComparison.OrdinalIgnoreCase)
            && !eventSession.State.Equals("closed", StringComparison.OrdinalIgnoreCase);
        if (active)
        {
            return;
        }

        // Runtime Viewer Session is the single lifecycle authority for AIrCon-managed TVTest.
        // Whether the process ended through AIrCon power-off, TVTest's X button, or an external
        // process termination, the same terminal transition clears Plugin-owned background state.
        RemoveZappingState(viewerProfile);
        SetPowerOffStopped(viewerProfile);

        KeyValuePair<string, string>[] targetWindows;
        lock (_lastViewerProfileByWindowId)
            targetWindows = _lastViewerProfileByWindowId
                .Where(x => x.Value.Equals(viewerProfile, StringComparison.OrdinalIgnoreCase))
                .ToArray();

        if (targetWindows.Length == 0)
        {
            return;
        }

        var terminalWave = ResolveServiceWave(eventSession.CurrentService);

        // ViewerSessionChanged is authoritative for the affected session. Patch only ToolWindows
        // currently displaying that exact Viewer Profile; never substitute a default GR/BSCS
        // profile and never sweep unrelated windows.
        foreach (var pair in targetWindows)
        {
            var windowId = pair.Key;
            if (string.IsNullOrWhiteSpace(windowId)) continue;

            var patches = new List<RuntimeUiPatch>
            {
                RuntimeUiPatch.Enabled("aircon-viewer-power-button", false),
                RuntimeUiPatch.Classes(
                    "aircon-viewer-power-button",
                    add: new[] { "aircon-toolbar-button-disabled" },
                    remove: Array.Empty<string>()),
                new RuntimeUiPatch
                {
                    ElementId = "aircon-viewer-power-button",
                    Attributes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["aria-disabled"] = "true",
                        ["title"] = "選択中のTVTestは停止済みです"
                    }
                },
                RuntimeUiPatch.Text("aircon-zapping-status", "停止中"),
                RuntimeUiPatch.Classes("aircon-zapping-status",
                    add: new[] { "aircon-zapping-status-off" },
                    remove: new[] { "aircon-zapping-status-on" }),
                RuntimeUiPatch.Text("aircon-zapping-button", "開始"),
                RuntimeUiPatch.Classes("aircon-zapping-button",
                    add: Array.Empty<string>(),
                    remove: new[] { "aircon-zapping-button-on" }),
                RuntimeUiPatch.Text("aircon-sleep-label", "電源OFF"),
                RuntimeUiPatch.Visibility("aircon-sleep-select", visible: true),
                RuntimeUiPatch.Visibility("aircon-sleep-remaining", visible: false),
                RuntimeUiPatch.Text("aircon-sleep-button", "開始"),
                RuntimeUiPatch.Classes("aircon-sleep-button",
                    add: Array.Empty<string>(),
                    remove: new[] { "aircon-sleep-button-on" })
            };

            var displayedWave = string.Empty;
            lock (_lastWaveByWindowId)
            {
                if (_lastWaveByWindowId.TryGetValue(windowId, out var rememberedWave))
                    displayedWave = rememberedWave;
            }

            // The viewing row exists only when this window is displaying the terminal session's
            // exact service wave. Remove its semantic viewing/zapping class in the same lifecycle
            // transaction that disables the power button; no separate visual-state authority.
            if (!string.IsNullOrWhiteSpace(terminalWave)
                && NormalizeWaveFilter(displayedWave).Equals(terminalWave, StringComparison.OrdinalIgnoreCase))
            {
                patches.Add(RuntimeUiPatch.Classes(CurrentViewingAnchorId,
                    add: Array.Empty<string>(),
                    remove: new[] { "aircon-row-viewing-selected", "aircon-row-viewing-other", "aircon-row-zapping-selected" }));
            }

            var result = AIrConNewApiBridge.PatchToolWindow(windowId, patches, eventEnvelope.Sequence);
            if (result.Success && result.Diagnostics.Contains("reason=window_closed", StringComparison.OrdinalIgnoreCase))
                StopWindowBackgroundExecution(windowId);
        }
    }

    private static string ResolveServiceWave(TvAIrPlugin.Viewers.TvAirServiceIdentityDto? service)
    {
        if (service == null) return string.Empty;
        try
        {
            var row = AIrConNewApiBridge.ListServices().FirstOrDefault(x =>
                x.NetworkId == service.NetworkId
                && x.TransportStreamId == service.TransportStreamId
                && x.ServiceId == service.ServiceId);
            return row == null ? string.Empty : NormalizeWaveFilter(row.BroadcastType);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ResolveViewerSessionId(string? entityId)
    {
        const string prefix = "viewer-session:";
        var value = (entityId ?? string.Empty).Trim();
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? value[prefix.Length..].Trim()
            : string.Empty;
    }

    private string ResolveRequestedViewerProfile(IReadOnlyDictionary<string, string> query, string windowId)
    {
        var requested = FirstNonEmpty(
            QueryString(query, "viewerProfile"),
            QueryString(query, "viewer-profile"),
            QueryString(query, "viewer_profile"),
            QueryString(query, "viewerProfileId"),
            QueryString(query, "viewer-profile-id"));

        var vtuner = QueryString(query, "vtuner");
        if (string.IsNullOrWhiteSpace(requested) && IsViewerProfileLike(vtuner)) requested = vtuner;

        if (!string.IsNullOrWhiteSpace(requested)) return requested;
        if (!string.IsNullOrWhiteSpace(windowId))
        {
            lock (_lastViewerProfileByWindowId)
            {
                if (_lastViewerProfileByWindowId.TryGetValue(windowId, out var remembered) && !string.IsNullOrWhiteSpace(remembered)) return remembered;
            }
        }
        return string.Empty;
    }

    private void RememberViewerProfile(string windowId, string viewerProfile, bool isToolWindow)
    {
        if (!isToolWindow || string.IsNullOrWhiteSpace(windowId) || string.IsNullOrWhiteSpace(viewerProfile)) return;
        if (IsAutoProfileId(viewerProfile)) return;
        lock (_lastViewerProfileByWindowId)
        {
            _lastViewerProfileByWindowId[windowId] = viewerProfile.Trim();
        }
    }

    private static bool IsViewerProfileLike(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var v = value.Trim();
        return v.StartsWith("tvtest", StringComparison.OrdinalIgnoreCase)
            || v.StartsWith("viewer", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeViewerProfileId(string value)
    {
        var text = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        if (IsAutoProfileId(text)) return string.Empty;
        return text;
    }

    private static string BuildRenderFailureHtml(RuntimeUiRenderContext context)
    {
        return "<!doctype html><html><head><meta charset=\"utf-8\"><style>" +
            ResolveThemePalette(context).Apply(
                "html,body{margin:0;background:var(--aircon-page);color:var(--aircon-text);font-family:Meiryo,Arial,sans-serif;font-size:12px}.aircon-error{padding:12px}") +
            "</style></head><body><div class=\"aircon-error\">AIrCon表示を生成できませんでした。</div></body></html>";
    }


    private ViewerProfileState CaptureViewerProfiles(RuntimeUiRenderContext context)
    {
        try
        {
            // Runtime Viewers API is the authoritative generic source for configured viewing slots.
            // Do not infer profiles from active sessions or UI request state.
            var runtimeProfiles = AIrConNewApiBridge.ListProfiles();
            if (runtimeProfiles.Count > 0)
            {
                var runtimeSelectable = runtimeProfiles
                    .Where(x => x.IsAvailable && !string.IsNullOrWhiteSpace(x.ViewerProfileId))
                    .Select((x, index) => new ViewerProfileChoice(
                        x.ViewerProfileId.Trim(),
                        FirstNonEmpty(x.DisplayName, x.ViewerProfileId),
                        true,
                        x.IsDefault,
                        index,
                        x.BroadcastGroups?.Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).ToArray() ?? Array.Empty<string>(),
                        x.TvTestFrameIndex,
                        x.LogicalViewerSlotId ?? string.Empty,
                        false))
                    .Where(x => !IsAutoProfileId(x.Id))
                    .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();

                var runtimeDefaultId = FirstNonEmpty(
                    runtimeSelectable.FirstOrDefault(x => x.IsDefault)?.Id,
                    runtimeSelectable.FirstOrDefault()?.Id);
                var runtimeVisible = runtimeSelectable.Count >= 2;
                return new ViewerProfileState(runtimeSelectable, runtimeDefaultId, runtimeVisible, true, true);
            }

            return ViewerProfileState.Unavailable;
        }
        catch (Exception)
        {
            return ViewerProfileState.Unavailable;
        }
    }

    private static IReadOnlyList<string> ReadStringList(object? obj, params string[] names)
    {
        foreach (var name in names)
        {
            var value = ReadProperty(obj, name);
            var list = ConvertToStringList(value);
            if (list.Count > 0) return list;
        }
        return Array.Empty<string>();
    }

    private static IReadOnlyList<string> ConvertToStringList(object? value)
    {
        var result = new List<string>();
        if (value == null) return result;
        if (value is string text)
        {
            foreach (var part in text.Split(new[] { ',', ';', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var normalized = NormalizeAvailableGroup(part);
                if (!string.IsNullOrWhiteSpace(normalized) && !result.Contains(normalized, StringComparer.OrdinalIgnoreCase)) result.Add(normalized);
            }
            return result;
        }
        if (value is System.Collections.IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                var normalized = NormalizeAvailableGroup(item?.ToString() ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(normalized) && !result.Contains(normalized, StringComparer.OrdinalIgnoreCase)) result.Add(normalized);
            }
        }
        return result;
    }

    private static string NormalizeAvailableGroup(string value)
    {
        var text = (value ?? string.Empty).Trim().ToUpperInvariant();
        if (text == "地上波" || text == "TERRESTRIAL" || text == "GROUND") return "GR";
        if (text == "BS" || text == "CS" || text == "BS/CS" || text == "BSCS" || text == "BS-CS") return "BSCS";
        if (text == "GR") return "GR";
        return text;
    }

    private static string RequiredProfileGroupForWave(string wave)
    {
        var normalized = NormalizeWaveFilter(wave);
        return normalized.Equals("GR", StringComparison.OrdinalIgnoreCase) ? "GR" : "BSCS";
    }

    private static bool IsAutoProfileId(string id)
        => string.IsNullOrWhiteSpace(id)
           || id.Equals("auto", StringComparison.OrdinalIgnoreCase)
           || id.Equals("default", StringComparison.OrdinalIgnoreCase);

    private static ViewerProfileChoice ResolveSelectedViewerProfile(string requested, ViewerProfileState state, string wave)
    {
        var selectable = state.SelectableProfiles.Where(x => x.Enabled).ToList();
        var available = state.AvailableForWave(wave).ToList();
        var desired = FirstNonEmpty(requested, state.DefaultViewerProfile, available.FirstOrDefault()?.Id, selectable.FirstOrDefault()?.Id);
        if (IsAutoProfileId(desired)) desired = FirstNonEmpty(state.DefaultViewerProfile, available.FirstOrDefault()?.Id, selectable.FirstOrDefault()?.Id);

        // The displayed wave owns the selectable Viewer Profile set.
        // Keep the requested stable ProfileId only when it belongs to the displayed wave;
        // otherwise select the first host-projected profile available for that wave.
        var exact = available.FirstOrDefault(x => x.Id.Equals(desired, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;

        return available.FirstOrDefault() ?? selectable.FirstOrDefault() ?? state.SelectableProfiles.FirstOrDefault() ?? ViewerProfileChoice.Unavailable;
    }

    private static ServiceRow FromRuntimeService(TvAirServiceDto service, int index)
    {
        var filter = NormalizeRuntimeBroadcastType(service.BroadcastType);
        return new ServiceRow
        {
            ProgramGuideOrder = service.DisplayOrder != 0 ? service.DisplayOrder : index,
            ProgramGuideFilterGroup = filter,
            ProgramGuideFilterLabel = FilterLabel(filter),
            AllocationGroup = filter == "GR" ? "GR" : "BSCS",
            TunerGroup = filter == "GR" ? "GR" : "BSCS",
            ServiceName = service.ServiceName,
            NetworkId = service.NetworkId,
            TransportStreamId = service.TransportStreamId,
            ServiceId = service.ServiceId
        };
    }

    private static string NormalizeRuntimeBroadcastType(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToUpperInvariant();
        if (text is "GR" or "TERRESTRIAL" or "地上波" or "地デジ") return "GR";
        if (text.StartsWith("CS", StringComparison.Ordinal)) return "CS";
        if (text is "BS" or "BSCS" or "SATELLITE" or "BS/CS") return "BS";
        return text;
    }

    private List<ServiceRow> CaptureServiceProjection(string filter)
    {
        var diagnostics = new List<string>();
        var services = AIrConNewApiBridge.ListServices()
            .Where(x => x.IsEnabled)
            .OrderBy(x => x.DisplayOrder)
            .ThenBy(x => x.ServiceName, StringComparer.OrdinalIgnoreCase)
            .Select((x, index) => FromRuntimeService(x, index))
            .ToList();
        services = NormalizeViewerServiceAuthority(services, diagnostics);
        return services
            .Where(x => x.ProgramGuideFilterGroup.Equals(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.ProgramGuideOrder)
            .ToList();
    }

    private FloatingViewerData CaptureData(string filter, ViewerProfileState viewerProfiles, FocusTriplet focusTriplet)
    {
        var services = new List<ServiceRow>();
        var sessions = new List<ViewerSessionRow>();
        var tuners = new List<ViewerTunerRow>();
        var waveFilters = new List<WaveFilterRow>();
        var diagnostics = new List<string>();
        var projectionUsed = false;
        var safeEventContractAvailable = false;
        var safeDblclickEvents = false;

        // Runtime UIのクリック可能な局一覧はRuntime Channels APIを唯一の正本とし、
        // ProgramGuide APIはNow/Next表示のoverlayにだけ使用する。
        try
        {
            services = AIrConNewApiBridge.ListServices()
                .Where(x => x.IsEnabled)
                .OrderBy(x => x.DisplayOrder)
                .ThenBy(x => x.ServiceName, StringComparer.OrdinalIgnoreCase)
                .Select((x, index) => FromRuntimeService(x, index))
                .ToList();
            diagnostics.Add("viewerControlChannels=Runtime count=" + services.Count + " source=runtime_channels");

            waveFilters = AIrConNewApiBridge.ListWaveFilters()
                .Where(x => x.IsProgramGuideFilter)
                .OrderBy(x => x.Order)
                .Select(x => new WaveFilterRow(FirstNonEmpty(x.Key, x.BroadcastType), FirstNonEmpty(x.BroadcastType, x.Key), FirstNonEmpty(x.Label, x.BroadcastType, x.Key)))
                .Where(x => !string.IsNullOrWhiteSpace(x.Group))
                .ToList();
            diagnostics.Add("waveFilters=Runtime count=" + waveFilters.Count);

            var now = DateTimeOffset.Now;
            var events = AIrConNewApiBridge.ListProgramEvents(now, now.AddHours(6));
            var applied = ApplyRuntimeNowNext(services, events, now);
            projectionUsed = true;
            diagnostics.Add("programGuide=Runtime overlayNowNext=" + applied + " events=" + events.Count + " listAuthority=runtime_channels");
        }
        catch (Exception ex)
        {
            diagnostics.Add("runtimeProjection exception=" + ex.Message);
        }

        try
        {
            sessions = CaptureViewerSessionsNewApi(diagnostics);
            diagnostics.Add("viewerSessions=Runtime count=" + sessions.Count + " source=runtime_viewer_sessions");
        }
        catch (Exception ex) { diagnostics.Add("viewerSessions exception=" + ex.Message); }

services = NormalizeViewerServiceAuthority(services, diagnostics);

        var zeroTripletFinal = services.Count(x => x.NetworkId <= 0 || x.TransportStreamId <= 0 || x.ServiceId <= 0);
        if (zeroTripletFinal > 0)
        {
        }

        if (waveFilters.Count == 0)
        {
            waveFilters.Add(new WaveFilterRow("GR", "GR", "地"));
            waveFilters.Add(new WaveFilterRow("BS", "BS", "BS"));
            waveFilters.Add(new WaveFilterRow("CS", "CS", "CS"));
        }

        if (services.Count > 0 && sessions.Count > 0)
        {
            var map = services
                .Where(x => HasResolvedTriplet(x))
                .GroupBy(x => ServiceKey(x.NetworkId, x.TransportStreamId, x.ServiceId), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.OrderBy(r => r.ProgramGuideOrder).First(), StringComparer.OrdinalIgnoreCase);
            foreach (var s in sessions.OrderByDescending(x => x.Current).ThenByDescending(x => x.IsActive))
            {
                if (s.NetworkId == null || s.TransportStreamId == null || s.ServiceId == null) continue;
                if (!map.TryGetValue(ServiceKey(s.NetworkId.Value, s.TransportStreamId.Value, s.ServiceId.Value), out var row)) continue;
                row.IsViewing = true;
                row.ViewingViewerProfile = s.ViewerProfile;
            }
        }

        var sessionHighlighted = services.Count(x => x.IsViewing);
        diagnostics.Add("currentRowHighlight source=viewer_session session=" + sessionHighlighted + " focusOverride=disabled");

        var filtered = services
            .Where(x => x.ProgramGuideFilterGroup.Equals(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.ProgramGuideOrder)
            .ToList();


        var serviceColumnWidth = CalculateServiceColumnWidth(services);

        return new FloatingViewerData(filtered, sessions, tuners, waveFilters, viewerProfiles, diagnostics, projectionUsed, safeEventContractAvailable, safeDblclickEvents, serviceColumnWidth);
    }

    private static List<ServiceRow> NormalizeViewerServiceAuthority(IReadOnlyList<ServiceRow> source, List<string> diagnostics)
    {
        // AIrCon の局行は「視聴開始できる triplet 行」を正本にする。
        // ProgramGuide/NowNext は番組名と時刻の overlay 専用で、局行の採否には使わない。
        // ここでだけ invalid/duplicate を落とし、HTML/CSS 側で隠す後段処理は作らない。
        var normalized = new List<ServiceRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var invalid = 0;
        var duplicate = 0;

        foreach (var row in source.OrderBy(x => x.ProgramGuideOrder).ThenBy(x => x.ServiceName, StringComparer.OrdinalIgnoreCase))
        {
            if (!HasResolvedTriplet(row))
            {
                invalid++;
                continue;
            }

            var key = ServiceKey(row.NetworkId, row.TransportStreamId, row.ServiceId);
            if (!seen.Add(key))
            {
                duplicate++;
                continue;
            }

            normalized.Add(row);
        }

        diagnostics.Add("serviceAuthority=viewer_start_triplet rows=" + normalized.Count
            + " invalidDropped=" + invalid
            + " duplicateDropped=" + duplicate
            + " overlayDoesNotPrune=True");
        return normalized;
    }

    private static List<ViewerSessionRow> CaptureViewerSessionsNewApi(List<string> diagnostics)
    {
        try
        {
            return AIrConNewApiBridge.ListSessions()
                .Select(s => new ViewerSessionRow(
                    s.ViewerSessionId,
                    s.CurrentService?.ServiceName ?? string.Empty,
                    NormalizeFilter(string.Empty),
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    0,
                    s.CurrentService?.NetworkId,
                    s.CurrentService?.TransportStreamId,
                    s.CurrentService?.ServiceId,
                    true,
                    s.State,
                    s.ViewerProfileId,
                    s.ViewerProfileId,
                    string.Empty,
                    s.ViewerSessionId,
                    s.Generation,
                    s.ProcessId))
                .OrderByDescending(x => x.Current)
                .ThenByDescending(x => x.IsActive)
                .ToList();
        }
        catch (Exception ex)
        {
            diagnostics.Add("viewerSessionsRuntime exception=" + ex.Message);
            return new List<ViewerSessionRow>();
        }
    }

    private static int ApplyRuntimeNowNext(List<ServiceRow> services, IReadOnlyList<TvAirProgramEventDto> events, DateTimeOffset now)
    {
        var byService = events
            .Where(x => x.Start <= now && x.End > now)
            .GroupBy(x => ServiceKey(x.NetworkId, x.TransportStreamId, x.ServiceId), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(e => e.Start).First(), StringComparer.OrdinalIgnoreCase);
        var applied = 0;
        foreach (var row in services)
        {
            if (!byService.TryGetValue(ServiceKey(row.NetworkId, row.TransportStreamId, row.ServiceId), out var current)) continue;
            row.CurrentTitle = current.Title;
            row.CurrentStart = current.Start;
            row.CurrentEnd = current.End;
            row.HasCurrentProgramProjection = true;
            applied++;
        }
        return applied;
    }

    private ViewerOperation CaptureAction(RuntimeUiRenderContext c)
    {
        var contract = ReadStringDictionary(c, "ActionContract");
        var endpoint = FirstNonEmpty(ReadString(c, "PluginActionEndpoint", "ActionEndpoint"), GetValue(contract, "endpoint"), GetValue(contract, "actionEndpoint"), "/api/plugins/action");
        var route = FirstNonEmpty(ReadString(c, "PluginActionRoute", "ActionRoute"), GetValue(contract, "route"), GetValue(contract, "pluginActionRoute"), "/plugin-action");
        var method = FirstNonEmpty(ReadString(c, "PluginActionMethod", "ActionMethod"), GetValue(contract, "method"), "POST");
        var token = FirstNonEmpty(ReadString(c, "PluginActionToken", "ActionToken"), GetValue(contract, "token"), GetValue(contract, "actionToken"));
        var pluginId = FirstNonEmpty(ReadString(c, "PluginId"), GetValue(contract, "pluginId"), PluginId);
        var routeSegment = FirstNonEmpty(ReadString(c, "RouteSegment"), GetValue(contract, "routeSegment"), RouteSegment);
        var supported = ReadStringList(c, "PluginSupportedActions", "SupportedActions");
        var contractActions = SplitCsv(GetValue(contract, "actions"));
        var canPost = method.Equals("POST", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(endpoint)
            && !string.IsNullOrWhiteSpace(token)
            && ((supported.Count == 0 && contractActions.Count == 0)
                || supported.Any(x => x.Equals("viewerStart", StringComparison.OrdinalIgnoreCase))
                || contractActions.Any(x => x.Equals("viewerStart", StringComparison.OrdinalIgnoreCase)));
        return new ViewerOperation(canPost, endpoint, route, method, token, pluginId, routeSegment);
    }

    private WindowOperation CaptureWindow(RuntimeUiRenderContext c, bool isToolWindow)
    {
        var endpoint = FirstNonEmpty(ReadString(c, "WindowEndpoint"), "/api/plugins/window");
        var route = FirstNonEmpty(ReadString(c, "WindowRoute"), "/plugin-window");
        var method = FirstNonEmpty(ReadString(c, "WindowMethod"), "POST");
        var contract = ReadStringDictionary(c, "WindowContract");
        var token = FirstNonEmpty(ReadString(c, "WindowToken"), GetValue(contract, "token"));
        var pluginId = FirstNonEmpty(ReadString(c, "PluginId"), GetValue(contract, "pluginId"), PluginId);
        var routeSegment = FirstNonEmpty(ReadString(c, "RouteSegment"), RouteSegment);
        var windowId = FirstNonEmpty(ReadString(c, "CurrentWindowId", "WindowId"), QueryWindowId(c));
        var supported = ReadStringList(c, "SupportedWindowActions");
        var capabilities = ReadStringDictionary(c, "ToolWindowCapabilities");
        var modes = FirstNonEmpty(GetValue(capabilities, "openWindowModes"), GetValue(contract, "openWindowModes"));
        var toolWindowSupported = ReadDictBool(capabilities, contract, "toolWindowSupported") || modes.Contains("toolWindow", StringComparison.OrdinalIgnoreCase);
        var canOpen = method.Equals("POST", StringComparison.OrdinalIgnoreCase) && !isToolWindow && !string.IsNullOrWhiteSpace(endpoint);
        var canRefresh = method.Equals("POST", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(endpoint)
            && (supported.Count == 0 || supported.Any(x => x.Equals("refreshWindow", StringComparison.OrdinalIgnoreCase)) || supported.Any(x => x.Equals("rerenderWindow", StringComparison.OrdinalIgnoreCase)));
        var stateEndpoint = ResolveWindowStateEndpoint(c, contract, windowId);
        return new WindowOperation(canOpen, canRefresh && !string.IsNullOrWhiteSpace(windowId), endpoint, route, method, token, pluginId, routeSegment, windowId, stateEndpoint, toolWindowSupported);
    }

    private static string ResolveWindowStateEndpoint(RuntimeUiRenderContext c, IReadOnlyDictionary<string, string> contract, string windowId)
    {
        // Compatibility only: official no longer calls this endpoint from RenderHtml.
        // TvAIr SDK contract supplies direct CurrentWindowAlwaysOnTop state instead.
        var escapedWindowId = Uri.EscapeDataString(windowId ?? string.Empty);
        var absolute = FirstNonEmpty(
            ReadString(c, "CurrentWindowStateUrl", "WindowStateUrl", "AbsoluteWindowStateUrl", "CurrentWindowStateAbsoluteUrl"),
            GetValue(contract, "currentWindowStateUrl"),
            GetValue(contract, "windowStateUrl"),
            GetValue(contract, "absoluteWindowStateUrl"));
        if (!string.IsNullOrWhiteSpace(absolute)) return absolute.Replace("{windowId}", escapedWindowId, StringComparison.OrdinalIgnoreCase);

        var direct = FirstNonEmpty(
            ReadString(c, "CurrentWindowStateEndpoint", "WindowStateEndpoint"),
            GetValue(contract, "currentWindowStateEndpoint"),
            GetValue(contract, "stateEndpoint"));
        if (!string.IsNullOrWhiteSpace(direct)) return direct.Replace("{windowId}", escapedWindowId, StringComparison.OrdinalIgnoreCase);

        var template = FirstNonEmpty(
            ReadString(c, "WindowStateUrlTemplate", "WindowStateEndpointTemplate"),
            GetValue(contract, "windowStateUrlTemplate"),
            GetValue(contract, "stateEndpointTemplate"),
            GetValue(contract, "windowStateEndpointTemplate"));
        if (!string.IsNullOrWhiteSpace(template)) return template.Replace("{windowId}", escapedWindowId, StringComparison.OrdinalIgnoreCase);

        return !string.IsNullOrWhiteSpace(windowId) ? "/plugin-window/" + escapedWindowId + "/state" : string.Empty;
    }

    private bool ResolveWindowAlwaysOnTop(RuntimeUiRenderContext context, WindowOperation window, bool isToolWindow)
    {
        if (!isToolWindow || string.IsNullOrWhiteSpace(window.WindowId)) return false;

        // TvAIr SDK contract: RenderHtml must not synchronously call WindowStateUrl.
        // The host injects the current tool-window state directly into RuntimeUiRenderContext / WindowContract.
        if (TryReadBoolProperty(context, out var directValue, "CurrentWindowAlwaysOnTop", "WindowAlwaysOnTop"))
        {
            return directValue;
        }

        if (TryReadBoolFromObject(ReadProperty(context, "CurrentWindowState"), "AlwaysOnTop", "alwaysOnTop", out var objectValue)
            || TryReadBoolFromObject(ReadProperty(context, "WindowState"), "AlwaysOnTop", "alwaysOnTop", out objectValue))
        {
            return objectValue;
        }

        foreach (var mapName in new[] { "WindowContract", "ToolWindowCapabilities", "WindowState", "CurrentWindowState" })
        {
            var dict = ReadStringDictionary(context, mapName);
            if (TryReadBoolFromDictionary(dict, "currentWindowAlwaysOnTop", out var dictValue)
                || TryReadBoolFromDictionary(dict, "CurrentWindowAlwaysOnTop", out dictValue)
                || TryReadBoolFromDictionary(dict, "alwaysOnTop", out dictValue)
                || TryReadBoolFromDictionary(dict, "AlwaysOnTop", out dictValue))
            {
                return dictValue;
            }
        }

        return false;
    }

    private static bool TryReadBoolProperty(object? obj, out bool value, params string[] names)
    {
        value = false;
        if (obj == null) return false;
        foreach (var name in names)
        {
            var prop = obj.GetType().GetProperty(name);
            if (prop == null) continue;
            try
            {
                var raw = prop.GetValue(obj);
                if (raw is bool b) { value = b; return true; }
                if (raw is string s && TryParseBool(s, out var parsed)) { value = parsed; return true; }
            }
            catch { }
        }
        return false;
    }

    private static bool TryReadBoolFromObject(object? obj, string name1, string name2, out bool value)
    {
        value = false;
        if (obj == null) return false;
        var raw = ReadProperty(obj, name1) ?? ReadProperty(obj, name2);
        return TryParseBool(raw?.ToString(), out value);
    }

    private static bool TryReadBoolFromDictionary(IReadOnlyDictionary<string, string> dict, string key, out bool value)
    {
        value = false;
        return dict.TryGetValue(key, out var raw) && TryParseBool(raw, out value);
    }

    private static bool TryParseBool(string? raw, out bool value)
    {
        value = false;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (bool.TryParse(raw, out value)) return true;
        if (raw == "1" || raw.Equals("on", StringComparison.OrdinalIgnoreCase)) { value = true; return true; }
        if (raw == "0" || raw.Equals("off", StringComparison.OrdinalIgnoreCase)) { value = false; return true; }
        return false;
    }

    private static AirConThemePalette ResolveThemePalette(RuntimeUiRenderContext context)
    {
        var dark = string.Equals(context?.HostEffectiveTheme, "dark", StringComparison.OrdinalIgnoreCase);
        var contract = context?.ThemeContract;
        string Role(string key, string lightFallback, string darkFallback)
        {
            if (contract != null && contract.TryGetValue(key, out var value) && IsSafeCssColor(value)) return value.Trim();
            return dark ? darkFallback : lightFallback;
        }

        var page = Role("pageBackground", "#eef4f8", "#111820");
        var surface = Role("surfaceBackground", "#d6e8f4", "#1b2732");
        var subtle = Role("subtleBackground", "#e5f1f8", "#243441");
        var input = Role("inputBackground", "#f8fbfd", "#18232d");
        var text = Role("text", "#102334", "#f3f7fa");
        var muted = Role("mutedText", "#27465a", "#c4d0d8");
        var border = Role("border", "#8fb2c9", "#536978");
        var accent = Role("accent", "#245b80", "#3c78a0");
        var accentText = Role("accentText", "#ffffff", "#ffffff");
        var focus = Role("focus", "#276f9e", "#77b7df");
        var hover = dark ? "#2b3e4d" : "#e5f2fb";
        var accentHover = dark ? "#4b8bb5" : "#1d4d6d";
        var disabledBg = dark ? "#27313a" : "#edf2f5";
        var disabledText = dark ? "#8f9da7" : "#708190";
        var row = dark ? "#151f28" : "#ffffff";
        var rowAlt = dark ? "#1b2832" : "#f2f8fc";
        var buttonBg = dark ? "#18232d" : "#f8fbfd";
        var buttonText = dark ? "#f3f7fa" : "#102f46";
        var buttonBorder = dark ? "#536978" : "#6d94ad";
        var buttonHover = dark ? "#2b3e4d" : "#e5f2fb";
        var profileLabel = dark ? "#c4d0d8" : "#27465a";
        var disabledBorder = dark ? "#46545f" : "#b6c4ce";
        var refreshBg = dark ? "#1f3528" : "#edf8ef";
        var refreshHover = dark ? "#294633" : "#dff2e3";
        var refreshBorder = dark ? "#4f8c61" : "#5fa872";
        var refreshText = dark ? "#bde9c8" : "#174820";
        var powerBg = dark ? "#3b2525" : "#faeeee";
        var powerHover = dark ? "#4b2d2d" : "#f5dddd";
        var powerBorder = dark ? "#a86464" : "#c86a6a";
        var powerText = dark ? "#f1bcbc" : "#7b1f1f";
        var topmostBg = dark ? "#2a3137" : "#eceff2";
        var topmostHover = dark ? "#343d45" : "#e1e7ec";
        var topmostBorder = dark ? "#667785" : "#9aa7b0";
        var topmostText = dark ? "#d5dde3" : "#3f4b54";
        var viewingBg = dark ? "#5b4b22" : "#fff2bd";
        var viewingHover = dark ? "#6b5928" : "#ffe9a2";
        var viewingOtherBg = dark ? "#243c4f" : "#e7f2fb";
        var viewingOtherHover = dark ? "#2d4a60" : "#dcecf7";
        var zappingBg = dark ? "#5a3030" : "#ffdede";
        var zappingHover = dark ? "#6c3838" : "#ffcaca";
        var timeStart = dark ? "#9fc6e8" : "#4f6f9f";
        var timeEnd = dark ? "#e4a7a7" : "#9a5a5a";
        var statusBorder = dark ? "#607887" : "#9bbccd";
        var statusOffBg = dark ? "#233540" : "#edf7fc";
        var statusOffBorder = dark ? "#4b7186" : "#a9c9da";
        var statusOffText = dark ? "#b8d8e8" : "#315f78";
        var statusOnBg = dark ? "#493d25" : "#fff8e8";
        var statusOnBorder = dark ? "#8f784c" : "#b69d73";
        var statusOnText = dark ? "#f0d9a9" : "#5d4723";
        var controlText = dark ? "#d8e6ef" : "#12384f";
        var controlBorder = dark ? "#607d8e" : "#789db3";
        var controlHover = dark ? "#2b3e4d" : "#eef7fc";
        var controlHoverBorder = dark ? "#7aa2b9" : "#5f8faa";
        var controlOnBg = dark ? "#4b2d2d" : "#fff0f0";
        var controlOnText = dark ? "#f1bcbc" : "#7a2020";
        var controlOnBorder = dark ? "#a86464" : "#cf9292";
        var powerDisabledBg = dark ? "#342828" : "#f2e6e6";
        var powerDisabledBorder = dark ? "#755454" : "#cfa0a0";
        var powerDisabledText = dark ? "#aa8c8c" : "#8a6868";
        var topmostOnBg = dark ? "#a83e3e" : "#cf4b4b";
        var topmostOnHover = dark ? "#bc4a4a" : "#bd3838";
        var topmostOnBorder = dark ? "#d06b6b" : "#9b2222";
        var topmostOnText = "#ffffff";
        var topmostDisabledBg = dark ? "#2b3237" : "#edf0f2";
        var topmostDisabledBorder = dark ? "#56636d" : "#c0c8ce";
        var topmostDisabledText = dark ? "#89969f" : "#87939b";

        return new AirConThemePalette(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["--aircon-page"] = page,
            ["--aircon-surface"] = surface,
            ["--aircon-subtle"] = subtle,
            ["--aircon-input"] = input,
            ["--aircon-text"] = text,
            ["--aircon-muted"] = muted,
            ["--aircon-border"] = border,
            ["--aircon-accent"] = accent,
            ["--aircon-accent-hover"] = accentHover,
            ["--aircon-accent-text"] = accentText,
            ["--aircon-focus"] = focus,
            ["--aircon-hover"] = hover,
            ["--aircon-disabled-bg"] = disabledBg,
            ["--aircon-disabled-text"] = disabledText,
            ["--aircon-row"] = row,
            ["--aircon-row-alt"] = rowAlt,
            ["--aircon-button-bg"] = buttonBg,
            ["--aircon-button-text"] = buttonText,
            ["--aircon-button-border"] = buttonBorder,
            ["--aircon-button-hover"] = buttonHover,
            ["--aircon-profile-label"] = profileLabel,
            ["--aircon-disabled-border"] = disabledBorder,
            ["--aircon-refresh-bg"] = refreshBg,
            ["--aircon-refresh-hover"] = refreshHover,
            ["--aircon-refresh-border"] = refreshBorder,
            ["--aircon-refresh-text"] = refreshText,
            ["--aircon-power-bg"] = powerBg,
            ["--aircon-power-hover"] = powerHover,
            ["--aircon-power-border"] = powerBorder,
            ["--aircon-power-text"] = powerText,
            ["--aircon-topmost-bg"] = topmostBg,
            ["--aircon-topmost-hover"] = topmostHover,
            ["--aircon-topmost-border"] = topmostBorder,
            ["--aircon-topmost-text"] = topmostText,
            ["--aircon-viewing-bg"] = viewingBg,
            ["--aircon-viewing-hover"] = viewingHover,
            ["--aircon-viewing-other-bg"] = viewingOtherBg,
            ["--aircon-viewing-other-hover"] = viewingOtherHover,
            ["--aircon-zapping-bg"] = zappingBg,
            ["--aircon-zapping-hover"] = zappingHover,
            ["--aircon-time-start"] = timeStart,
            ["--aircon-time-end"] = timeEnd,
            ["--aircon-status-border"] = statusBorder,
            ["--aircon-status-off-bg"] = statusOffBg,
            ["--aircon-status-off-border"] = statusOffBorder,
            ["--aircon-status-off-text"] = statusOffText,
            ["--aircon-status-on-bg"] = statusOnBg,
            ["--aircon-status-on-border"] = statusOnBorder,
            ["--aircon-status-on-text"] = statusOnText,
            ["--aircon-control-text"] = controlText,
            ["--aircon-control-border"] = controlBorder,
            ["--aircon-control-hover"] = controlHover,
            ["--aircon-control-hover-border"] = controlHoverBorder,
            ["--aircon-control-on-bg"] = controlOnBg,
            ["--aircon-control-on-text"] = controlOnText,
            ["--aircon-control-on-border"] = controlOnBorder,
            ["--aircon-power-disabled-bg"] = powerDisabledBg,
            ["--aircon-power-disabled-border"] = powerDisabledBorder,
            ["--aircon-power-disabled-text"] = powerDisabledText,
            ["--aircon-topmost-on-bg"] = topmostOnBg,
            ["--aircon-topmost-on-hover"] = topmostOnHover,
            ["--aircon-topmost-on-border"] = topmostOnBorder,
            ["--aircon-topmost-on-text"] = topmostOnText,
            ["--aircon-topmost-disabled-bg"] = topmostDisabledBg,
            ["--aircon-topmost-disabled-border"] = topmostDisabledBorder,
            ["--aircon-topmost-disabled-text"] = topmostDisabledText,
        });
    }

    private sealed record AirConThemePalette(IReadOnlyDictionary<string, string> Colors)
    {
        public string Apply(string css)
        {
            var resolved = css;
            foreach (var pair in Colors)
            {
                resolved = resolved.Replace("var(" + pair.Key + ")", pair.Value, StringComparison.Ordinal);
            }

            if (resolved.Contains("var(--aircon-", StringComparison.Ordinal))
                throw new InvalidOperationException("AIrCon theme palette did not resolve every semantic color token.");

            return resolved;
        }
    }

    private static bool IsSafeCssColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var v = value.Trim();
        if (v.Length > 64) return false;
        return v.StartsWith("#", StringComparison.Ordinal) ||
               v.StartsWith("rgb(", StringComparison.OrdinalIgnoreCase) ||
               v.StartsWith("rgba(", StringComparison.OrdinalIgnoreCase) ||
               v.StartsWith("hsl(", StringComparison.OrdinalIgnoreCase) ||
               v.StartsWith("hsla(", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildLauncherHtml(RuntimeUiRenderContext context, FloatingViewerData data, WindowOperation window, string filter, string selectedTuner, string selectedViewerProfile, bool alwaysOnTop)
    {
        var openForm = BuildOpenWindowForm(window, alwaysOnTop, filter, selectedTuner, selectedViewerProfile);
        return $$"""
<!doctype html>
<meta charset="utf-8">
<style>
{{BuildLauncherCss(context)}}
</style>
<div class="aircon-launch">
  <div class="aircon-card">
    <div class="aircon-head">AIrCon</div>
    <div class="aircon-body">
      {{openForm}}
    </div>
  </div>
</div>
""";
    }

    private static string BuildLauncherCss(RuntimeUiRenderContext context)
    {
        return ResolveThemePalette(context).Apply(@"html,body{margin:0;background:var(--aircon-page);color:var(--aircon-text);font-family:Meiryo,""Yu Gothic"",Arial,sans-serif;font-size:13px;}
.aircon-launch{padding:12px;}
.aircon-card{width:260px;max-width:100%;background:var(--aircon-surface);border:1px solid var(--aircon-border);box-shadow:0 1px 3px rgba(0,0,0,.25);}
.aircon-head{background:var(--aircon-subtle);color:var(--aircon-text);padding:6px 9px;font-weight:bold;}
.aircon-body{padding:10px;line-height:1.35;}
.aircon-open{font-family:inherit;border:1px solid var(--aircon-button-border);background:var(--aircon-accent);color:var(--aircon-accent-text);border-radius:3px;padding:5px 11px;font-weight:bold;cursor:pointer;}
.aircon-open:hover{background:var(--aircon-accent-hover);}
.aircon-note,.aircon-status{display:none;}");
    }

    private static string BuildFloatingViewerHtml(RuntimeUiRenderContext context, FloatingViewerData data, ViewerOperation action, WindowOperation window, string filter, string selectedTunerValue, ViewerProfileChoice selectedViewerProfile, bool alwaysOnTop, bool zappingActive, string activeZappingWave, DateTimeOffset? powerOffDeadline)
    {
        var tunerChoices = BuildTunerChoices(data.ViewerTuners, filter).ToList();
        var selected = ResolveSelectedTuner(tunerChoices, selectedTunerValue);
        var rows = BuildRows(context, data.Services, data.ViewerSessions, action, window, selected, selectedViewerProfile, filter, zappingActive, activeZappingWave, powerOffDeadline);
        var toolbar = BuildToolbar(context, data.WaveFilters, data.ViewerSessions, data.ViewerProfiles, action, window, filter, selected.Value, selectedViewerProfile, alwaysOnTop);
        return $$"""
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<style>
{{BuildToolWindowCss(context, data.ServiceColumnWidthPx)}}
</style>
</head>
<body class="aircon-runtime-root">
<div class="aircon-float">
  {{toolbar}}
  <div class="aircon-list" id="aircon-service-list">
    {{rows}}
  </div>
  <script>
  (function(){
    var list=document.getElementById('aircon-service-list');
    var row=document.getElementById('aircon-current-viewing-anchor');
    if(!list||!row)return;
    var top=row.offsetTop-Math.floor((list.clientHeight-row.offsetHeight)/2);
    list.scrollTop=Math.max(0,top);
  })();
  </script>
</div>
</body>
</html>
""";
    }

    private static string BuildToolWindowCss(RuntimeUiRenderContext context, int serviceColumnWidthPx)
    {
        var toolbarHeight = ToolWindowToolbarHeightPx;
        var toolbarContentTop = ToolWindowToolbarContentTopPx;
        var toolbarPaddingX = ToolWindowToolbarPaddingXPx;
        var toolbarCellGap = ToolWindowToolbarCellGapPx;
        var toolbarGroupGap = ToolWindowToolbarGroupGapPx;
        var toolbarLabelPaddingRight = ToolWindowToolbarLabelPaddingRightPx;
        var waveButtonWidth = ToolWindowWaveButtonWidthPx;
        var waveAreaWidth = ToolWindowWaveAreaWidthPx;
        var viewerProfileButtonWidth = ToolWindowViewerProfileNumericButtonWidthPx;
        var actionButtonSize = ToolWindowActionButtonSizePx;
        var actionButtonGroupWidth = ToolWindowActionButtonGroupWidthPx;
        var waveButtonGroupWidth = ToolWindowWaveButtonGroupWidthPx;
        var waveButtonOverlap = ToolWindowWaveButtonOverlapPx;
        var buttonHeight = ToolWindowToolbarButtonHeightPx;
        var buttonLineHeight = ToolWindowButtonLineHeightPx;
        var buttonBorder = ToolWindowButtonBorderPx;
        var cellHeight = ToolWindowToolbarCellHeightPx;
        var listTop = ToolWindowListTopPx;
        var rowHeight = ToolWindowRowHeightPx;
        var serviceWidth = Math.Max(ToolWindowServiceColumnMinimumWidthPx, Math.Min(ToolWindowServiceColumnMaximumWidthPx, serviceColumnWidthPx));
        var timeWidth = ToolWindowTimeColumnWidthPx;
        
        return ResolveThemePalette(context).Apply($$"""
html,body{margin:0;padding:0;width:100%;height:100%;min-width:{{ToolWindowMinimumWidthPx}}px;min-height:{{ToolWindowMinimumHeightPx}}px;background:var(--aircon-page);color:var(--aircon-text);font-family:Meiryo,"Yu Gothic",Arial,sans-serif;font-size:12px;overflow:hidden;}
body.aircon-runtime-root{position:static;}
.aircon-runtime-root,.aircon-runtime-root *{box-sizing:border-box;}
.aircon-float{position:static;width:100%;height:100%;min-width:{{ToolWindowMinimumWidthPx}}px;min-height:{{ToolWindowMinimumHeightPx}}px;background:var(--aircon-page);overflow:hidden;}
.aircon-toolbar{position:fixed;left:0;right:0;top:0;height:{{toolbarHeight}}px;padding:0;border-bottom:{{ToolWindowToolbarSeparatorPx}}px solid var(--aircon-border);background:var(--aircon-surface);white-space:nowrap;overflow:hidden;text-align:left;z-index:2;}
.aircon-toolbar-inner{position:absolute;left:{{toolbarPaddingX}}px;right:{{toolbarPaddingX}}px;top:{{toolbarContentTop}}px;height:{{buttonHeight}}px;overflow:hidden;white-space:nowrap;}
.aircon-toolbar-wave-area{position:absolute;left:0;top:0;width:{{waveAreaWidth}}px;height:{{cellHeight}}px;line-height:{{cellHeight}}px;white-space:nowrap;overflow:hidden;}
.aircon-toolbar-profile-slot{position:absolute;left:{{waveAreaWidth + toolbarGroupGap}}px;right:{{actionButtonGroupWidth + toolbarGroupGap}}px;top:0;width:auto;height:{{cellHeight}}px;line-height:{{cellHeight}}px;white-space:nowrap;overflow:hidden;}
.aircon-toolbar-profile-slot-reserved{visibility:hidden;}
.aircon-toolbar-actions{position:absolute;right:0;top:0;width:{{actionButtonGroupWidth}}px;height:{{cellHeight}}px;display:flex;flex-direction:row;justify-content:flex-end;align-items:flex-start;gap:{{toolbarCellGap}}px;white-space:nowrap;overflow:visible;}
.aircon-toolbar-label{display:inline-block;height:{{cellHeight}}px;line-height:{{buttonLineHeight}}px;margin:0;padding:{{buttonBorder}}px {{toolbarLabelPaddingRight}}px {{buttonBorder}}px 0;color:var(--aircon-muted);font-size:11px;font-weight:bold;vertical-align:top;white-space:nowrap;}
.aircon-wave-group{display:inline-block;margin:0;padding:0;width:{{waveButtonGroupWidth}}px;height:{{cellHeight}}px;line-height:{{cellHeight}}px;white-space:nowrap;vertical-align:top;overflow:visible;}
.aircon-nav-form,.aircon-action-form,.aircon-profile-form{display:inline-block;margin:0;padding:0;height:{{cellHeight}}px;line-height:{{cellHeight}}px;white-space:nowrap;vertical-align:top;}
.aircon-toolbar-actions .aircon-action-form{width:{{actionButtonSize}}px;margin:0;}
.aircon-wave-group .aircon-nav-form{margin:0 0 0 -{{waveButtonOverlap}}px;}
.aircon-wave-group .aircon-nav-form:first-child{margin-left:0;}
.aircon-toolbar-button,.aircon-toolbar-select{display:inline-block;height:{{buttonHeight}}px;line-height:{{buttonLineHeight}}px;padding:0 6px;margin:0;border:{{buttonBorder}}px solid var(--aircon-button-border);background:var(--aircon-button-bg);color:var(--aircon-button-text);border-radius:2px;font-size:11px;font-family:inherit;font-weight:bold;text-align:center;cursor:pointer;vertical-align:top;white-space:nowrap;text-decoration:none;}
.aircon-toolbar-button:hover,.aircon-toolbar-select:hover{background:var(--aircon-button-hover);}
.aircon-toolbar-button:focus,.aircon-toolbar-select:focus{outline:1px solid var(--aircon-focus);outline-offset:1px;}
.aircon-toolbar-button-disabled:hover,.aircon-profile-button-disabled:hover{background:var(--aircon-disabled-bg);color:var(--aircon-disabled-text);}
.aircon-profile-label{display:inline-block;width:auto;height:{{cellHeight}}px;line-height:{{buttonLineHeight}}px;margin:0;padding:{{buttonBorder}}px {{toolbarLabelPaddingRight}}px {{buttonBorder}}px 0;color:var(--aircon-profile-label);font-size:11px;font-weight:bold;vertical-align:top;white-space:nowrap;overflow:visible;text-align:left;}
.aircon-profile-segments{display:inline-block;width:auto;height:{{cellHeight}}px;line-height:{{cellHeight}}px;white-space:nowrap;overflow:visible;vertical-align:top;}
.aircon-profile-segment-form{display:inline-block;margin:0 {{toolbarCellGap}}px 0 0;padding:0;width:{{viewerProfileButtonWidth}}px;height:{{cellHeight}}px;line-height:{{cellHeight}}px;vertical-align:top;white-space:nowrap;overflow:visible;}
.aircon-profile-button{width:{{viewerProfileButtonWidth}}px;min-width:{{viewerProfileButtonWidth}}px;max-width:{{viewerProfileButtonWidth}}px;height:{{buttonHeight}}px;line-height:{{buttonLineHeight}}px;padding:0;text-align:center;}
.aircon-profile-button-on{background:var(--aircon-accent);color:var(--aircon-accent-text);border-color:var(--aircon-button-border);}
.aircon-profile-button-on:hover,.aircon-profile-button-on:focus,.aircon-profile-button-on:active{background:var(--aircon-accent);color:var(--aircon-accent-text);border-color:var(--aircon-button-border);}
.aircon-profile-button-disabled{opacity:.45;cursor:default;background:var(--aircon-disabled-bg);color:var(--aircon-disabled-text);}
.aircon-toolbar-button-disabled{opacity:.70;cursor:default;background:var(--aircon-disabled-bg);color:var(--aircon-disabled-text);}
.aircon-wave-button{width:{{waveButtonWidth}}px;min-width:{{waveButtonWidth}}px;border-radius:0;background:var(--aircon-button-bg);border-color:var(--aircon-button-border);color:var(--aircon-button-text);}
.aircon-wave-button-on{background:var(--aircon-accent);color:var(--aircon-accent-text);border-color:var(--aircon-button-border);}
.aircon-wave-button-on:hover,.aircon-wave-button-on:focus,.aircon-wave-button-on:active{background:var(--aircon-accent);color:var(--aircon-accent-text);border-color:var(--aircon-button-border);}
.aircon-wave-button-disabled,.aircon-wave-button-disabled:hover{background:var(--aircon-disabled-bg);color:var(--aircon-disabled-text);border-color:var(--aircon-disabled-border);cursor:default;}
.aircon-action-button{width:{{actionButtonSize}}px;min-width:{{actionButtonSize}}px;max-width:{{actionButtonSize}}px;padding:0;font-size:15px;font-family:"Segoe UI Symbol","Meiryo","Yu Gothic",Arial,sans-serif;font-weight:bold;line-height:{{buttonLineHeight}}px;text-align:center;}
.aircon-action-refresh{background:var(--aircon-refresh-bg);border-color:var(--aircon-refresh-border);color:var(--aircon-refresh-text);}
.aircon-action-refresh:hover{background:var(--aircon-refresh-hover);}
.aircon-action-settings{font-size:14px;}
.aircon-action-power{background:var(--aircon-power-bg);border-color:var(--aircon-power-border);color:var(--aircon-power-text);}
.aircon-action-power:hover{background:var(--aircon-power-hover);}
.aircon-action-power.aircon-toolbar-button-disabled{background:var(--aircon-power-disabled-bg);border-color:var(--aircon-power-disabled-border);color:var(--aircon-power-disabled-text);}
.aircon-action-topmost{background:var(--aircon-topmost-bg);border-color:var(--aircon-topmost-border);color:var(--aircon-topmost-text);}
.aircon-action-topmost:hover{background:var(--aircon-topmost-hover);}
.aircon-action-topmost-on{background:var(--aircon-topmost-on-bg);border-color:var(--aircon-topmost-on-border);color:var(--aircon-topmost-on-text);}
.aircon-action-topmost-on:hover{background:var(--aircon-topmost-on-hover);}
.aircon-action-topmost.aircon-toolbar-button-disabled{background:var(--aircon-topmost-disabled-bg);border-color:var(--aircon-topmost-disabled-border);color:var(--aircon-topmost-disabled-text);}
.aircon-list{position:fixed;left:0;right:0;top:{{listTop}}px;bottom:0;background:var(--aircon-page);overflow-x:hidden;overflow-y:scroll;width:100%;height:auto;}
.aircon-row{display:block;position:relative;width:100%;margin:0;padding:0 8px;border:0;border-bottom:1px solid var(--aircon-border);background:var(--aircon-row);cursor:pointer;font-family:inherit;text-align:left;color:var(--aircon-text);height:{{rowHeight}}px;line-height:{{rowHeight}}px;white-space:nowrap;overflow:hidden;}
.aircon-row-even{background:var(--aircon-row-alt);}
.aircon-row-odd{background:var(--aircon-row);}
.aircon-row:hover{background:var(--aircon-hover);}
.aircon-row:focus,.aircon-row:active{outline:none;}
.aircon-row-disabled,.aircon-row-disabled:hover{cursor:default;color:var(--aircon-disabled-text);background:var(--aircon-disabled-bg);}
.aircon-row-viewing-selected{background:var(--aircon-viewing-bg);cursor:pointer;}
.aircon-row-viewing-other{background:var(--aircon-viewing-other-bg);cursor:pointer;}
.aircon-viewer-tune-form{display:block;margin:0;padding:0;border:0;}
.aircon-row-zapping-selected{background:var(--aircon-zapping-bg);cursor:pointer;}
.aircon-row-zapping-selected:hover{background:var(--aircon-zapping-hover);}
.aircon-row-viewing-selected:hover{background:var(--aircon-viewing-hover);}
.aircon-row-viewing-other:hover{background:var(--aircon-viewing-other-hover);}
.aircon-row span{cursor:inherit;}
.aircon-service{position:absolute;left:8px;top:0;display:block;width:{{serviceWidth}}px;height:{{rowHeight}}px;line-height:{{rowHeight}}px;vertical-align:top;color:var(--aircon-text);font-weight:bold;font-size:12px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;}
.aircon-time{position:absolute;left:{{8 + serviceWidth}}px;top:0;display:block;width:{{timeWidth}}px;height:{{rowHeight}}px;margin:0;padding:3px 2px 0 0;text-align:center;vertical-align:top;white-space:nowrap;overflow:hidden;font-size:10px;line-height:11px;font-weight:bold;}
.aircon-time-start,.aircon-time-end{display:block;height:11px;line-height:11px;margin:0;padding:0;white-space:nowrap;overflow:hidden;}
.aircon-time-start{color:var(--aircon-time-start);}
.aircon-time-end{color:var(--aircon-time-end);}
.aircon-current{position:absolute;left:{{8 + serviceWidth + timeWidth}}px;right:8px;top:0;display:block;width:auto;margin:0;height:{{rowHeight}}px;line-height:{{rowHeight}}px;vertical-align:top;color:var(--aircon-text);font-size:12px;font-weight:bold;white-space:nowrap;overflow:hidden;text-overflow:clip;}
.aircon-float [hidden]{display:none;}
.aircon-scroll-anchor{display:block;width:100%;height:1px;line-height:1px;font-size:0;overflow:hidden;margin:0;padding:0;}
.aircon-zapping-bar{display:flex;position:relative;align-items:center;justify-content:space-between;gap:8px;min-width:0;margin:0;padding:5px 8px;border-top:1px solid var(--aircon-border);background:var(--aircon-subtle);white-space:nowrap;overflow:hidden;box-sizing:border-box;}
.aircon-zapping-left,.aircon-sleep-right{display:flex;align-items:center;gap:0;min-width:0;height:24px;}
.aircon-zapping-left{flex:0 1 auto;overflow:hidden;}
.aircon-sleep-right{flex:0 0 auto;margin-left:auto;padding-left:9px;border-left:1px solid var(--aircon-border);}
.aircon-zapping-label,.aircon-sleep-label{display:inline-flex;align-items:center;margin:0 7px 0 0;height:22px;line-height:22px;color:var(--aircon-muted);font-size:11px;font-weight:bold;vertical-align:middle;white-space:nowrap;}
.aircon-zapping-status{display:inline-flex;align-items:center;justify-content:center;min-width:42px;margin:0 9px 0 0;padding:0 7px;height:22px;line-height:20px;color:var(--aircon-muted);font-size:10px;font-weight:bold;text-align:center;vertical-align:middle;overflow:hidden;text-overflow:ellipsis;border:1px solid var(--aircon-status-border);border-radius:11px;box-sizing:border-box;}
.aircon-zapping-status-off{background:var(--aircon-status-off-bg);border-color:var(--aircon-status-off-border);color:var(--aircon-status-off-text);}
.aircon-zapping-status-on,.aircon-sleep-remaining{background:var(--aircon-status-on-bg);border-color:var(--aircon-status-on-border);color:var(--aircon-status-on-text);}
.aircon-zapping-form,.aircon-sleep-form{display:inline-flex;align-items:center;margin:0;padding:0;vertical-align:middle;}
.aircon-zapping-button,.aircon-sleep-button,.aircon-sleep-select{display:inline-block;height:22px;line-height:20px;margin:0;border:1px solid var(--aircon-control-border);background:var(--aircon-input);color:var(--aircon-control-text);border-radius:3px;font-size:11px;font-family:inherit;font-weight:bold;text-align:center;vertical-align:middle;box-sizing:border-box;}
.aircon-zapping-button{min-width:44px;padding:0 8px;cursor:pointer;}
.aircon-sleep-button{min-width:38px;padding:0 6px;cursor:pointer;}
.aircon-sleep-select{width:45px;padding:0 2px;cursor:pointer;}
.aircon-zapping-button:hover,.aircon-sleep-button:hover{background:var(--aircon-control-hover);border-color:var(--aircon-control-hover-border);}
.aircon-zapping-button-on,.aircon-sleep-button-on{background:var(--aircon-control-on-bg);color:var(--aircon-control-on-text);border-color:var(--aircon-control-on-border);}
.aircon-zapping-button-on:hover,.aircon-zapping-button-on:focus,.aircon-zapping-button-on:active,.aircon-sleep-button-on:hover,.aircon-sleep-button-on:focus,.aircon-sleep-button-on:active{background:var(--aircon-control-on-bg);color:var(--aircon-control-on-text);border-color:var(--aircon-control-on-border);}
.aircon-sleep-select,.aircon-sleep-remaining{margin-right:9px;}
.aircon-sleep-remaining{display:inline-flex;align-items:center;justify-content:center;min-width:45px;height:22px;line-height:20px;padding:0 4px;border:1px solid;border-radius:3px;box-sizing:border-box;font-size:11px;font-weight:bold;text-align:center;vertical-align:middle;}
.aircon-sleep-hidden{display:none;}
@media(max-width:360px){.aircon-zapping-bar{gap:4px;padding-left:5px;padding-right:5px}.aircon-sleep-right{padding-left:5px}.aircon-zapping-label,.aircon-sleep-label{margin-right:4px;font-size:10px}.aircon-zapping-status,.aircon-sleep-select,.aircon-sleep-remaining{margin-right:5px}.aircon-zapping-status{min-width:38px;padding-left:5px;padding-right:5px}.aircon-zapping-button{min-width:40px;padding-left:5px;padding-right:5px} }
@media(max-width:285px){.aircon-zapping-label,.aircon-sleep-label{display:none}.aircon-sleep-right{border-left:0;padding-left:0} }
.aircon-empty{padding:16px;color:var(--aircon-muted);}
""");
    }


    private static string BuildHostActionAttributes(
        RuntimeUiRenderContext context,
        IReadOnlyDictionary<string, string?> fields,
        string eventName,
        string responseMode,
        string formCapture = "")
    {
        return context.BuildPluginActionAttributes(fields, eventName, responseMode, formCapture);
    }

    private static string BuildToolbar(RuntimeUiRenderContext context, IReadOnlyList<WaveFilterRow> waveFilters, IReadOnlyList<ViewerSessionRow> viewerSessions, ViewerProfileState viewerProfiles, ViewerOperation action, WindowOperation window, string filter, string selectedTuner, ViewerProfileChoice selectedViewerProfile, bool alwaysOnTop)
    {
        var filterForms = new List<string>();
        foreach (var f in CanonicalWaveFilters(waveFilters))
        {
            filterForms.Add(BuildFilterForm(context, f.Group, f.Label, filter, selectedTuner, selectedViewerProfile, alwaysOnTop, viewerProfiles, action, window));
        }

        var refresh = BuildRefreshForm(window, filter, selectedViewerProfile.Value);
        var selectedProfileSession = ResolveSelectedProfileSession(viewerSessions, selectedViewerProfile.Value);
        var power = BuildToolbarStopForm(context, selectedProfileSession, action, window, filter, selectedViewerProfile.Value);
        var pin = BuildPinForm(window, !alwaysOnTop, filter, selectedTuner, selectedViewerProfile.Value, alwaysOnTop);
        var settings = BuildSettingsButton(context, action, window, filter, selectedViewerProfile.Value);

        var waveGroup =
            "<div class='aircon-toolbar-wave-area' data-role='wave-selector-group'>" +
            "<span class='aircon-toolbar-label'>放送波：</span>" +
            "<span class='aircon-wave-group' role='group' aria-label='放送波'>" + string.Join("", filterForms) + "</span>" +
            "</div>";

        var viewerProfileGroup = BuildViewerProfileSelector(context, viewerProfiles, action, window, filter, selectedTuner, selectedViewerProfile.Value, alwaysOnTop);

        var actionGroup =
            "<div class='aircon-toolbar-actions' data-role='viewer-and-window-actions'>" +
            refresh + power + pin + settings +
            "</div>";

        return "<div class='aircon-toolbar'><div class='aircon-toolbar-inner'>" + waveGroup + viewerProfileGroup + actionGroup + "</div></div>";
    }


    private static ViewerSessionRow? ResolveSelectedProfileSession(IReadOnlyList<ViewerSessionRow> sessions, string selectedViewerProfile)
    {
        if (sessions == null || sessions.Count == 0 || string.IsNullOrWhiteSpace(selectedViewerProfile)) return null;
        return sessions
            .Where(x => !string.IsNullOrWhiteSpace(x.LeaseId))
            .Where(x => x.ViewerProfile.Equals(selectedViewerProfile, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.IsActive)
            .ThenByDescending(x => x.Current)
            .FirstOrDefault();
    }

    private static string BuildViewerProfileSelector(RuntimeUiRenderContext context, ViewerProfileState state, ViewerOperation action, WindowOperation window, string filter, string selectedTuner, string selectedViewerProfile, bool alwaysOnTop)
    {
        if (!state.SelectorVisibleRecommended)
        {
            return "<div class='aircon-toolbar-profile-slot aircon-toolbar-profile-slot-reserved' data-role='viewer-profile-reserved'></div>";
        }

        var buttons = new List<string>();
        // The displayed wave is the authority for Viewer Profile visibility.
        // GR shows only GR profiles; BS and CS show only BSCS profiles.
        var waveProfiles = state.AvailableForWave(filter).ToList();
        foreach (var p in waveProfiles)
        {
            var active = p.Id.Equals(selectedViewerProfile, StringComparison.OrdinalIgnoreCase);
            var groups = p.AvailableGroups == null || p.AvailableGroups.Count == 0 ? "ALL" : string.Join(",", p.AvailableGroups);
            var displayLabel = ViewerProfileSegmentLabel(p);
            var sharedSuffix = p.IsShared ? "（共用）" : string.Empty;
            var title = p.Name + sharedSuffix;
            var cls = "aircon-toolbar-button aircon-profile-button" + (active ? " aircon-profile-button-on" : string.Empty);

            var fields = BuildViewerActivateFields(action, window, filter, p.Id);
            var attrs = BuildHostActionAttributes(context, fields, "click", "refreshWindow");
            buttons.Add("<span class=\"aircon-profile-segment-form\" data-role=\"viewer-profile-selector\" data-viewer-profile=\"" + HtmlAttr(p.Id) + "\" data-groups=\"" + HtmlAttr(groups) + "\">" +
                "<button class=\"" + cls + "\" type=\"button\" " + attrs + " data-role=\"viewer-profile-option\" data-viewer-profile=\"" + HtmlAttr(p.Id) + "\" aria-pressed=\"" + (active ? BoolTrue : BoolFalse) + "\" title=\"" + HtmlAttr(title) + "\">" + Html(displayLabel) + "</button></span>");
        }

        if (buttons.Count == 0)
        {
            buttons.Add("<button class=\"aircon-toolbar-button aircon-profile-button aircon-profile-button-disabled\" type=\"button\" disabled aria-disabled=\"true\" title=\"視聴先候補なし\">-</button>");
        }

        return "<div class='aircon-toolbar-profile-slot aircon-profile-form' data-role='viewer-profile-selector'" +
            " data-tvair-current-viewer-profile='" + HtmlAttr(selectedViewerProfile) + "'" +
            " data-tvair-profile-count='" + state.SelectableProfiles.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "'>" +
            "<span class='aircon-profile-label'>" + (NormalizeWaveFilter(filter) == "GR" ? "チューナーT:" : "チューナーS:") + "</span>" +
            "<span class='aircon-profile-segments' role='group' aria-label='TVTest'>" + string.Join("", buttons) + "</span>" +
            "</div>";
    }

    private static string ViewerProfileSegmentLabel(ViewerProfileChoice profile)
    {
        // Device number is projected directly from the TvAIr Viewer Profile contract.
        // Do not infer or renumber it from enumeration order, display name, DID, or logical slot.
        var frame = profile.TvTestFrameIndex > 0
            ? profile.TvTestFrameIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "?";
        return profile.IsShared ? frame + "共" : frame;
    }

    private static IReadOnlyList<WaveFilterRow> CanonicalWaveFilters(IReadOnlyList<WaveFilterRow> source)
    {
        static string LabelFor(string group) => group.Equals("GR", StringComparison.OrdinalIgnoreCase) ? "地上波" : group.ToUpperInvariant();
        return new[] { "GR", "BS", "CS" }
            .Select(group => new WaveFilterRow(group, group, LabelFor(group)))
            .ToArray();
    }

    private static string BuildRows(RuntimeUiRenderContext context, IReadOnlyList<ServiceRow> services, IReadOnlyList<ViewerSessionRow> viewerSessions, ViewerOperation action, WindowOperation window, TunerChoice selectedTuner, ViewerProfileChoice selectedViewerProfile, string filter, bool zappingActive, string activeZappingWave, DateTimeOffset? powerOffDeadline)
    {
        if (services.Count == 0) return "<div class=\"aircon-empty\">表示対象の局がありません。</div>" + BuildZappingBar(context, false, viewerSessions, action, window, filter, selectedViewerProfile.Value, zappingActive, activeZappingWave, powerOffDeadline);
        var parts = new List<string>();
        var rowIndex = 0;
        var selectedSession = viewerSessions
            .Where(x => x.IsActive && x.ViewerProfile.Equals(selectedViewerProfile.Value, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Current)
            .FirstOrDefault();
        foreach (var row in services)
        {
            // official: wave is already represented by the active toolbar button.
            // Do not render an additional GR/BS/CS section band inside the scroll area.
            parts.Add(BuildServiceRow(context, row, action, window, selectedTuner, selectedViewerProfile, selectedSession, rowIndex++, zappingActive));
        }
        parts.Add(BuildZappingBar(context, true, viewerSessions, action, window, filter, selectedViewerProfile.Value, zappingActive, activeZappingWave, powerOffDeadline));
        return string.Join("", parts);
    }

    private static string BuildZappingBar(RuntimeUiRenderContext context, bool enabled, IReadOnlyList<ViewerSessionRow> viewerSessions, ViewerOperation action, WindowOperation window, string filter, string selectedViewerProfile, bool active, string activeWave, DateTimeOffset? powerOffDeadline)
    {
        var canPost = enabled
            && !string.IsNullOrWhiteSpace(action.ActionEndpoint)
            && !string.IsNullOrWhiteSpace(action.ActionToken)
            && !string.IsNullOrWhiteSpace(window.WindowId);
        var disabled = canPost ? string.Empty : " disabled=\"disabled\" aria-disabled=\"true\"";
        var statusText = active ? "巡回中（" + NormalizeWaveFilter(activeWave) + "）" : "停止中";
        var buttonText = active ? "停止" : "開始";
        var title = active ? "ザッピングを停止" : GetZappingIntervalSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture) + "秒ごとに同じ放送波内で順送りします";
        var buttonClass = active ? "aircon-zapping-button aircon-zapping-button-on" : "aircon-zapping-button";
        var statusClass = active ? "aircon-zapping-status aircon-zapping-status-on" : "aircon-zapping-status aircon-zapping-status-off";
        var operation = active ? AirConActionZappingStop : AirConActionZappingStart;

        var fields = new Dictionary<string, string?>
        {
            ["operation"] = operation,
            ["wave"] = filter,
            ["viewerProfile"] = selectedViewerProfile,
            ["refreshQuery"] = "wave=" + filter + "&viewerProfile=" + selectedViewerProfile,
            ["clientVersion"] = ClientVersion
        };

        var selectedSleepSession = viewerSessions.FirstOrDefault(s => s.ViewerProfile.Equals(selectedViewerProfile, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(s.LeaseId));
        var sleepDisabled = selectedSleepSession == null ? " disabled=\"disabled\" aria-disabled=\"true\"" : string.Empty;
        var powerActive = powerOffDeadline.HasValue && powerOffDeadline.Value > DateTimeOffset.Now;
        var remainingHours = powerActive ? Math.Max(1, (int)Math.Ceiling((powerOffDeadline!.Value - DateTimeOffset.Now).TotalHours)) : 1;
        var powerOperation = powerActive ? AirConActionPowerOffStop : AirConActionPowerOffStart;
        var powerFields = new Dictionary<string, string?>(fields)
        {
            ["refreshQuery"] = "wave=" + filter + "&viewerProfile=" + selectedViewerProfile
        };

        return "<div id=\"aircon-zapping-bar\" class=\"aircon-zapping-bar\" data-aircon-zapping-wave=\"" + HtmlAttr(filter) + "\" data-aircon-zapping-profile=\"" + HtmlAttr(selectedViewerProfile) + "\" data-aircon-zapping-window=\"" + HtmlAttr(window.WindowId) + "\" data-aircon-zapping-active=\"" + (active ? BoolTrue : BoolFalse) + "\">"
            + "<div class=\"aircon-zapping-left\">"
            + "<span class=\"aircon-zapping-label\">ザッピング</span>"
            + "<span id=\"aircon-zapping-status\" class=\"" + statusClass + "\">" + Html(statusText) + "</span>"
            + "<button id=\"aircon-zapping-button\" class=\"" + buttonClass + "\" type=\"button\" " + BuildHostActionAttributes(context, new Dictionary<string, string?>(fields) { ["operation"] = operation, ["airconAction"] = operation }, "click", "patchWindow") + " title=\"" + HtmlAttr(title) + "\"" + disabled + ">" + Html(buttonText) + "</button></div>"
            + "<form id=\"aircon-sleep-form\" class=\"aircon-sleep-right aircon-sleep-form\">"
            + "<span id=\"aircon-sleep-label\" class=\"aircon-sleep-label\">" + (powerActive ? "終了まで" : "電源OFF") + "</span>"
            + "<select id=\"aircon-sleep-select\" name=\"hours\" class=\"aircon-sleep-select\" aria-label=\"終了タイマー\" title=\"終了タイマー\"" + (powerActive ? " hidden=\"hidden\"" : string.Empty) + "><option value=\"1\">1h</option><option value=\"2\">2h</option><option value=\"3\">3h</option><option value=\"4\">4h</option><option value=\"5\">5h</option><option value=\"6\">6h</option></select>"
            + "<span id=\"aircon-sleep-remaining\" class=\"aircon-sleep-remaining\"" + (powerActive ? string.Empty : " hidden=\"hidden\"") + ">" + remainingHours.ToString(System.Globalization.CultureInfo.InvariantCulture) + "h</span>"
            + "<button id=\"aircon-sleep-button\" class=\"aircon-sleep-button" + (powerActive ? " aircon-sleep-button-on" : string.Empty) + "\" type=\"button\" " + BuildHostActionAttributes(context, new Dictionary<string, string?>(powerFields) { ["operation"] = powerOperation }, "click", "patchWindow", "closestForm") + " title=\"" + (powerActive ? "終了タイマーを停止" : "選択中TVTestの終了タイマーを開始") + "\"" + sleepDisabled + ">" + (powerActive ? "停止" : "開始") + "</button>"
            + "</form></div>";
    }

    private static string BuildServiceRow(RuntimeUiRenderContext context, ServiceRow row, ViewerOperation action, WindowOperation window, TunerChoice selectedTuner, ViewerProfileChoice selectedViewerProfile, ViewerSessionRow? selectedSession, int rowIndex, bool zappingActive)
    {
        var hasTriplet = HasResolvedTriplet(row);
        var attrs = hasTriplet ? BuildFloatingViewerActionAttributes(row, action, window, selectedTuner, selectedViewerProfile, selectedSession) : string.Empty;
        var parityClass = (rowIndex % 2 == 0) ? "aircon-row-even" : "aircon-row-odd";
        var isSelectedProfileViewing = row.IsViewing && (string.IsNullOrWhiteSpace(row.ViewingViewerProfile) || row.ViewingViewerProfile.Equals(selectedViewerProfile.Value, StringComparison.OrdinalIgnoreCase));
        var isOtherProfileViewing = row.IsViewing && !isSelectedProfileViewing;
        var cls = isSelectedProfileViewing
            ? (zappingActive ? "aircon-row aircon-row-zapping-selected" : "aircon-row aircon-row-viewing-selected")
            : isOtherProfileViewing
                ? "aircon-row aircon-row-viewing-other"
                : hasTriplet ? "aircon-row " + parityClass : "aircon-row aircon-row-disabled";
        var title = hasTriplet ? "ダブルクリックで視聴" : "このチャンネルは現在視聴できません";
        var current = string.IsNullOrWhiteSpace(row.CurrentTitle) ? "番組情報取得中" : row.CurrentTitle;
        var currentClass = "aircon-current";
        var currentTitleAttr = " title=\"" + HtmlAttr(current) + "\"";
        var serviceDomId = hasTriplet ? BuildServiceDomId(row) : string.Empty;
        var rowId = hasTriplet
            ? " id=\"" + HtmlAttr(isSelectedProfileViewing ? CurrentViewingAnchorId : serviceDomId) + "\""
            : string.Empty;
        var serviceDataId = hasTriplet ? " data-aircon-service-id=\"" + HtmlAttr(serviceDomId) + "\" data-aircon-zapping-row=\"true\" data-aircon-zapping-index=\"" + rowIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\"" : string.Empty;
        var content =
            $"<span class=\"aircon-service\">{Html(row.ServiceName)}</span>" +
            BuildCurrentTimeHtml(row) +
            $"<span class=\"{currentClass}\"{currentTitleAttr}>{Html(current)}</span>";

        if (!hasTriplet)
        {
            return $"<div class=\"{cls}\" title=\"{HtmlAttr(title)}\">" + content + "</div>";
        }

        var tuneFields = BuildViewerTuneFields(row, action, window, selectedTuner, selectedViewerProfile, selectedSession);
        var hostAttrs = BuildHostActionAttributes(context, tuneFields, "dblclick", "refreshWindow");
        return $"<div{rowId}{serviceDataId} class=\"{cls}\" title=\"{HtmlAttr(title)}\" {hostAttrs} data-aircon-viewer-tune=\"true\">" + content + "</div>";
    }

    private static Dictionary<string, string?> BuildViewerTuneFields(ServiceRow row, ViewerOperation action, WindowOperation window, TunerChoice selectedTuner, ViewerProfileChoice selectedViewerProfile, ViewerSessionRow? selectedSession)
    {
        var payload = BuildViewerStartPayload(row, selectedTuner, selectedViewerProfile);
        var fields = new Dictionary<string, string?>
        {
            ["operation"] = AirConActionViewerTune,
            ["wave"] = payload.BroadcastGroup,
            ["viewerProfile"] = payload.ViewerProfile,
            ["networkId"] = payload.NetworkId,
            ["transportStreamId"] = payload.TransportStreamId,
            ["serviceId"] = payload.ServiceId,
            ["channelSpace"] = payload.ChannelSpace,
            ["channelIndex"] = payload.ChannelIndex,
            ["channelArgument"] = payload.ChannelArgument,
            ["viewerProfileName"] = payload.ViewerProfileName,
            ["refreshQuery"] = BuildViewerRefreshQuery(row, selectedViewerProfile.Value),
            ["clientVersion"] = ClientVersion
        };
        AppendViewerSessionContractFields(fields, selectedSession == null ? null : new ViewerSessionContractState(selectedSession.ViewerSessionId, selectedSession.Generation));
        if (!selectedTuner.IsAuto)
        {
            fields["preferredTunerName"] = selectedTuner.Name;
            fields["preferredDid"] = selectedTuner.Did;
            fields["preferredSlot"] = selectedTuner.SlotIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return fields;
    }

    private static string BuildCurrentTimeHtml(ServiceRow row)
    {
        var start = FormatTime(row.CurrentStart);
        var end = FormatTime(row.CurrentEnd);
        var title = string.IsNullOrEmpty(start) && string.IsNullOrEmpty(end)
            ? string.Empty
            : " title=\"" + HtmlAttr((start.Length > 0 ? start : "--:--") + " - " + (end.Length > 0 ? end : "--:--")) + "\"";
        return "<span class=\"aircon-time\"" + title + ">"
            + "<span class=\"aircon-time-start\">" + Html(start) + "</span>"
            + "<span class=\"aircon-time-end\">" + Html(end) + "</span>"
            + "</span>";
    }

    private static string FormatTime(DateTimeOffset? value)
        => value.HasValue ? value.Value.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) : string.Empty;

    private static int CalculateServiceColumnWidth(IReadOnlyList<ServiceRow> services)
    {
        var maxTextWidth = 0;
        foreach (var service in services)
        {
            var name = service.ServiceName ?? string.Empty;
            maxTextWidth = Math.Max(maxTextWidth, EstimateServiceNameWidthPx(name));
        }

        var width = maxTextWidth + ToolWindowServiceColumnHorizontalReservePx;
        return Math.Max(ToolWindowServiceColumnMinimumWidthPx, Math.Min(ToolWindowServiceColumnMaximumWidthPx, width));
    }

    private static int EstimateServiceNameWidthPx(string text)
    {
        var width = 0;
        foreach (var ch in text)
        {
            width += IsNarrowDisplayChar(ch) ? 7 : 12;
        }
        return width;
    }

    private static bool IsNarrowDisplayChar(char ch)
        => (ch >= '\u0020' && ch <= '\u007e') || (ch >= '\uff61' && ch <= '\uff9f');

    private static string BuildServiceDomId(ServiceRow row)
        => "svc-" + row.NetworkId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "-" + row.TransportStreamId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "-" + row.ServiceId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static bool HasResolvedTriplet(ServiceRow row)
        => row.NetworkId > 0 && row.TransportStreamId > 0 && row.ServiceId > 0;

    private static string Url(string value) => WebUtility.UrlEncode(value);

    private static string BuildViewerRefreshQuery(ServiceRow row, string selectedViewerProfile)
    {
        var wave = string.IsNullOrWhiteSpace(row.ProgramGuideFilterGroup) ? "GR" : row.ProgramGuideFilterGroup;
        return "wave=" + Url(wave)
            + "&viewerProfile=" + Url(selectedViewerProfile);
    }

    private static string BuildFloatingViewerActionAttributes(ServiceRow row, ViewerOperation action, WindowOperation window, TunerChoice selectedTuner, ViewerProfileChoice selectedViewerProfile, ViewerSessionRow? selectedSession)
    {
        if (!action.CanPost || string.IsNullOrWhiteSpace(window.WindowId) || !HasResolvedTriplet(row)) return string.Empty;
        var payload = BuildViewerStartPayload(row, selectedTuner, selectedViewerProfile);
        var fields = new Dictionary<string, string?>
        {
            ["safe-event"] = "true",
            ["event"] = "dblclick",
            ["action"] = ActionPluginOwned,
            ["plugin-id"] = action.PluginId,
            ["route-segment"] = action.RouteSegment,
            ["token"] = action.ActionToken,
            ["method"] = action.ActionMethod,
            ["endpoint"] = action.ActionEndpoint,
            ["response-mode"] = ResponseModeHostHandled,
            ["refresh-target"] = RefreshTargetContent,
            ["refresh-after"] = BoolTrue,
            ["refresh-query"] = BuildViewerRefreshQuery(row, selectedViewerProfile.Value),
            ["window-id"] = window.WindowId,
            ["payload-networkId"] = payload.NetworkId,
            ["payload-transportStreamId"] = payload.TransportStreamId,
            ["payload-serviceId"] = payload.ServiceId,
            ["payload-channelSpace"] = payload.ChannelSpace,
            ["payload-channelIndex"] = payload.ChannelIndex,
            ["payload-channelArgument"] = payload.ChannelArgument,
            ["payload-broadcastGroup"] = payload.BroadcastGroup,
            ["payload-viewerProfileName"] = payload.ViewerProfileName,
            ["payload-airconAction"] = AirConActionViewerTune,
            ["payload-wave"] = payload.BroadcastGroup,
            ["payload-windowId"] = window.WindowId
        };
        AppendViewerSessionContractFields(fields, selectedSession == null ? null : new ViewerSessionContractState(selectedSession.ViewerSessionId, selectedSession.Generation));
        if (!selectedTuner.IsAuto)
        {
            fields["payload-preferredTunerName"] = selectedTuner.Name;
            fields["payload-preferredDid"] = selectedTuner.Did;
            fields["payload-preferredSlot"] = selectedTuner.SlotIndex.ToString();
        }
        return "role=\"button\" tabindex=\"0\" " +
            string.Join(" ", fields.Select(kv => $"data-tvair-{HtmlAttr(kv.Key)}=\"{HtmlAttr(kv.Value ?? string.Empty)}\""));
    }

    private static ViewerStartPayload BuildViewerStartPayload(ServiceRow row, TunerChoice selectedTuner, ViewerProfileChoice selectedViewerProfile)
    {
        return new ViewerStartPayload(
            row.NetworkId.ToString(),
            row.TransportStreamId.ToString(),
            row.ServiceId.ToString(),
            row.ChannelSpace.ToString(),
            row.ChannelIndex.ToString(),
            row.ChannelArgument ?? string.Empty,
            row.ProgramGuideFilterGroup ?? string.Empty,
            row.ProgramGuideFilterGroup ?? string.Empty,
            row.AllocationGroup ?? string.Empty,
            row.TunerGroup ?? string.Empty,
            row.ServiceName ?? string.Empty,
            selectedTuner.IsAuto ? string.Empty : selectedTuner.Name,
            selectedTuner.IsAuto ? string.Empty : selectedTuner.Did,
            selectedTuner.IsAuto ? string.Empty : selectedTuner.SlotIndex.ToString(),
            selectedViewerProfile.Value,
            selectedViewerProfile.Name);
    }

    private static string BuildPinForm(WindowOperation window, bool next, string filter, string selectedTuner, string selectedViewerProfile, bool current)
    {
        if (string.IsNullOrWhiteSpace(window.WindowEndpoint) || string.IsNullOrWhiteSpace(window.WindowId))
        {
            return "<span class=\"aircon-action-form\" data-role=\"toolbar-action-slot\"><button class=\"aircon-toolbar-button aircon-action-button aircon-action-topmost aircon-toolbar-button-disabled\" type=\"button\" aria-disabled=\"true\" aria-label=\"前面固定\" title=\"AIrConを常に前面\">⚑</button></span>";
        }
        var fields = new Dictionary<string, string?>
        {
            ["pluginId"] = window.PluginId,
            ["plugin-id"] = window.PluginId,
            ["routeSegment"] = window.RouteSegment,
            ["route-segment"] = window.RouteSegment,
            ["action"] = ActionUpdateWindow,
            ["token"] = window.WindowToken,
            ["windowId"] = window.WindowId,
            ["window-id"] = window.WindowId,
            ["currentWindowId"] = window.WindowId,
            ["alwaysOnTop"] = next ? BoolTrue : BoolFalse,
            ["payload-alwaysOnTop"] = next ? BoolTrue : BoolFalse,
            ["responseMode"] = ResponseModeHostHandled,
            ["response-mode"] = ResponseModeHostHandled,
            ["refreshAfter"] = BoolTrue,
            ["refresh-after"] = BoolTrue,
            ["target"] = RefreshTargetContent,
            ["refreshTarget"] = RefreshTargetContent,
            ["refresh-target"] = RefreshTargetContent,
            ["preserveScroll"] = BoolTrue,
            ["wave"] = filter,
            ["currentWave"] = filter,
            ["viewerProfile"] = selectedViewerProfile,
            ["viewer-profile"] = selectedViewerProfile,
            ["refreshQuery"] = "wave=" + filter + "&viewerProfile=" + selectedViewerProfile,
            ["forceReload"] = BoolFalse,
            ["clientVersion"] = ClientVersion
        };
        var cls = current ? "aircon-toolbar-button aircon-action-button aircon-action-topmost aircon-action-topmost-on" : "aircon-toolbar-button aircon-action-button aircon-action-topmost";
        var title = current ? "常に前面を解除" : "常に前面に固定";
        return "<form class=\"aircon-action-form\" method=\"" + HtmlAttr(window.WindowMethod) + "\" action=\"" + HtmlAttr(window.WindowEndpoint) + "\">" +
            HiddenInputs(fields) +
            "<button class=\"" + cls + "\" type=\"submit\" aria-pressed=\"" + (current ? BoolTrue : BoolFalse) + "\" aria-label=\"前面固定\" title=\"" + HtmlAttr(title) + "\">⚑</button></form>";
    }

    private static Dictionary<string, string?> BuildViewerStopFields(ViewerSessionRow session, ViewerOperation action, WindowOperation window, string filter, string viewerProfile)
    {
        var clientId = window.PluginId + ":viewer:" + viewerProfile;
        var fields = new Dictionary<string, string?>
        {
            ["pluginId"] = action.PluginId,
            ["plugin-id"] = action.PluginId,
            ["routeSegment"] = action.RouteSegment,
            ["route-segment"] = action.RouteSegment,
            ["clientId"] = clientId,
            ["client-id"] = clientId,
            ["viewerClientId"] = clientId,
            ["viewer-client-id"] = clientId,
            ["event"] = "click",
            ["safeEvent"] = BoolTrue,
            ["safe-event"] = BoolTrue,
            ["action"] = ActionViewerStop,
            ["safeEventAction"] = ActionViewerStop,
            ["safe-event-action"] = ActionViewerStop,
            ["token"] = action.ActionToken,
            ["actionToken"] = action.ActionToken,
            ["action-token"] = action.ActionToken,
            ["leaseId"] = session.LeaseId,
            ["lease-id"] = session.LeaseId,
            ["payload-leaseId"] = session.LeaseId,
            ["payload-lease-id"] = session.LeaseId,
            ["responseMode"] = ResponseModeHostHandled,
            ["response-mode"] = ResponseModeHostHandled,
            ["windowId"] = window.WindowId,
            ["window-id"] = window.WindowId,
            ["currentWindowId"] = window.WindowId,
            ["current-window-id"] = window.WindowId,
            ["preserveScroll"] = BoolTrue,
            ["preserve-scroll"] = BoolTrue,
            ["wave"] = filter,
            ["currentWave"] = filter,
            ["viewerProfile"] = viewerProfile,
            ["refreshQuery"] = "wave=" + filter + "&viewerProfile=" + viewerProfile,
            ["contentRoute"] = "/plugin/aircon?wave=" + filter + "&viewerProfile=" + viewerProfile,
            ["clientVersion"] = ClientVersion
        };
        AppendViewerSessionContractFields(fields, new ViewerSessionContractState(session.ViewerSessionId, session.Generation));
        return fields;
    }

    private static string BuildToolbarStopForm(RuntimeUiRenderContext context, ViewerSessionRow? session, ViewerOperation action, WindowOperation window, string filter, string selectedViewerProfile)
    {
        if (string.IsNullOrWhiteSpace(action.ActionEndpoint)
            || string.IsNullOrWhiteSpace(action.ActionToken)
            || string.IsNullOrWhiteSpace(window.WindowId))
        {
            return "<span class=\"aircon-action-form\" data-role=\"toolbar-action-slot\"><button id=\"aircon-viewer-power-button\" class=\"aircon-toolbar-button aircon-action-button aircon-action-power aircon-toolbar-button-disabled\" type=\"button\" aria-disabled=\"true\" aria-label=\"視聴停止\" title=\"現在は視聴を停止できません\">⏻</button></span>";
        }

        if (session == null || string.IsNullOrWhiteSpace(session.LeaseId) || !session.ViewerProfile.Equals(selectedViewerProfile, StringComparison.OrdinalIgnoreCase))
        {
            var disabledTitle = string.IsNullOrWhiteSpace(selectedViewerProfile)
                ? "停止する視聴がありません"
                : "選択中のTVTestは停止済みです";
            return "<span class=\"aircon-action-form\" data-role=\"toolbar-action-slot\"><button id=\"aircon-viewer-power-button\" class=\"aircon-toolbar-button aircon-action-button aircon-action-power aircon-toolbar-button-disabled\" type=\"button\" aria-disabled=\"true\" aria-label=\"視聴停止\" title=\"" + HtmlAttr(disabledTitle) + "\">⏻</button></span>";
        }

        var fields = new Dictionary<string, string?>
        {
            ["pluginId"] = action.PluginId,
            ["plugin-id"] = action.PluginId,
            ["routeSegment"] = action.RouteSegment,
            ["route-segment"] = action.RouteSegment,
            ["action"] = ActionPluginOwned,
            ["safeEventAction"] = ActionPluginOwned,
            ["safe-event-action"] = ActionPluginOwned,
            ["token"] = action.ActionToken,
            ["actionToken"] = action.ActionToken,
            ["action-token"] = action.ActionToken,
            ["responseMode"] = "refreshWindow",
            ["response-mode"] = "refreshWindow",
            ["windowId"] = window.WindowId,
            ["window-id"] = window.WindowId,
            ["currentWindowId"] = window.WindowId,
            ["current-window-id"] = window.WindowId,
            ["refreshTarget"] = RefreshTargetContent,
            ["refresh-target"] = RefreshTargetContent,
            ["wave"] = filter,
            ["viewerProfile"] = selectedViewerProfile,
            ["viewer-profile"] = selectedViewerProfile,
            ["clientVersion"] = ClientVersion
        };
        fields["operation"] = AirConActionViewerPowerOff;
        fields["airconAction"] = AirConActionViewerPowerOff;
        var attrs = BuildHostActionAttributes(context, fields, "click", "refreshWindow");
        return "<button id=\"aircon-viewer-power-button\" class=\"aircon-toolbar-button aircon-action-button aircon-action-power\" type=\"button\" " + attrs + " aria-label=\"視聴停止\" title=\"ザッピングを停止してAIrCon管理の現在の視聴TVTestを閉じる\">⏻</button>";
    }

    private static string BuildOpenWindowForm(WindowOperation window, bool alwaysOnTop, string filter, string selectedTuner, string selectedViewerProfile)
    {
        if (!window.CanOpen) return "<span>AIrConを開けません。</span>";
        var fields = WindowOpenFields(window, alwaysOnTop, filter, selectedTuner, selectedViewerProfile);
        return "<form method=\"" + HtmlAttr(window.WindowMethod) + "\" action=\"" + HtmlAttr(window.WindowEndpoint) + "\">" +
            HiddenInputs(fields) +
            "<button class=\"aircon-open\" type=\"submit\">AIrCon</button></form>";
    }

    private static Dictionary<string, string?> WindowOpenFields(WindowOperation window, bool alwaysOnTop, string filter, string selectedTuner, string selectedViewerProfile)
    {
        return new Dictionary<string, string?>
        {
            ["pluginId"] = window.PluginId,
            ["routeSegment"] = window.RouteSegment,
            ["action"] = "openWindow",
            ["token"] = window.WindowToken,
            ["responseMode"] = window.ToolWindowSupported ? "redirectBack" : "redirect",
            ["title"] = PluginToolWindowTitle,
            ["width"] = ToolWindowDefaultWidthPx.ToString(),
            ["height"] = ToolWindowDefaultHeightPx.ToString(),
            ["minWidth"] = ToolWindowMinimumWidthPx.ToString(),
            ["minHeight"] = ToolWindowMinimumHeightPx.ToString(),
            ["resizable"] = "true",
            ["movable"] = "true",
            ["alwaysOnTop"] = alwaysOnTop ? BoolTrue : BoolFalse,
            ["hostManaged"] = "true",
            ["toolWindowContentOnly"] = "true",
            ["reuseExisting"] = "true",
            ["activateExisting"] = "true",
            ["target"] = RefreshTargetContent,
            ["refreshTarget"] = RefreshTargetContent,
            ["preserveScroll"] = BoolTrue,
            // returnUrl は通常ブラウザ側へ戻るURL。ToolWindow内部フラグを絶対に混ぜない。
            ["returnUrl"] = "/plugin/aircon",
            ["wave"] = filter,
            ["currentWave"] = filter,
            ["viewerProfile"] = selectedViewerProfile,
            ["viewer-profile"] = selectedViewerProfile,
            ["refreshQuery"] = "wave=" + filter + "&viewerProfile=" + selectedViewerProfile,
            ["clientVersion"] = ClientVersion
        };
    }

    private static string BuildSettingsButton(RuntimeUiRenderContext context, ViewerOperation action, WindowOperation window, string filter, string selectedViewerProfile)
    {
        if (string.IsNullOrWhiteSpace(action.ActionToken) || string.IsNullOrWhiteSpace(window.WindowId))
            return "<span class=\"aircon-action-form\" data-role=\"toolbar-action-slot\"><button class=\"aircon-toolbar-button aircon-action-button aircon-toolbar-button-disabled\" type=\"button\" aria-disabled=\"true\" aria-label=\"設定\" title=\"現在は設定を開けません\">⚙</button></span>";
        var fields = new Dictionary<string, string?>
        {
            ["operation"] = AirConActionSettingsOpen,
            ["windowId"] = window.WindowId,
            ["wave"] = filter,
            ["viewerProfile"] = selectedViewerProfile,
            ["clientVersion"] = ClientVersion
        };
        var attrs = BuildHostActionAttributes(context, fields, "click", "hostHandled");
        return "<span class=\"aircon-action-form\" data-role=\"toolbar-action-slot\"><button class=\"aircon-toolbar-button aircon-action-button aircon-action-settings\" type=\"button\" " + attrs + " aria-label=\"設定\" title=\"AIrCon設定\">⚙</button></span>";
    }

    private static string BuildSettingsHtml(RuntimeUiRenderContext context, ViewerOperation action, WindowOperation window, AirConSettings settings, string returnWave, string returnViewerProfile)
    {
        var saveFields = new Dictionary<string, string?>
        {
            ["operation"] = AirConActionSettingsSave,
            ["windowId"] = window.WindowId,
            ["returnWave"] = returnWave,
            ["returnViewerProfile"] = returnViewerProfile,
            ["clientVersion"] = ClientVersion
        };
        var closeFields = new Dictionary<string, string?>
        {
            ["operation"] = AirConActionSettingsClose,
            ["windowId"] = window.WindowId,
            ["returnWave"] = returnWave,
            ["returnViewerProfile"] = returnViewerProfile,
            ["clientVersion"] = ClientVersion
        };
        var saveAttrs = BuildHostActionAttributes(context, saveFields, "click", "hostHandled", "closestForm");
        var closeAttrs = BuildHostActionAttributes(context, closeFields, "click", "hostHandled");
        var checkedAttr = settings.RememberWindowPlacement ? " checked" : string.Empty;
        string Selected(int value) => settings.ZappingIntervalSeconds == value ? " selected" : string.Empty;
        string WaveSelected(string value) => settings.StartupWave.Equals(value, StringComparison.OrdinalIgnoreCase) ? " selected" : string.Empty;
        var css = ResolveThemePalette(context).Apply(@"html,body{margin:0;width:100%;height:100%;background:var(--aircon-page);color:var(--aircon-text);font-family:Meiryo,'Yu Gothic',Arial,sans-serif;font-size:12px;overflow:hidden}.aircon-runtime-root,.aircon-runtime-root *{box-sizing:border-box}.aircon-settings{height:100%;padding:14px;background:var(--aircon-page)}.aircon-settings-title{font-size:14px;font-weight:bold;margin:0 0 14px}.aircon-settings-row{display:table;width:100%;margin:0 0 12px}.aircon-settings-label{display:table-cell;width:62%;vertical-align:middle}.aircon-settings-control{display:table-cell;text-align:right;vertical-align:middle}.aircon-settings select{width:118px;height:26px;border:1px solid var(--aircon-control-border);background:var(--aircon-input);color:var(--aircon-control-text);font-family:inherit}.aircon-toggle{width:18px;height:18px;vertical-align:middle}.aircon-settings-actions{text-align:right;margin-top:18px}.aircon-settings-button{min-width:64px;height:28px;margin-left:8px;border:1px solid var(--aircon-button-border);border-radius:3px;background:var(--aircon-button-bg);color:var(--aircon-button-text);font-family:inherit;font-weight:bold;cursor:pointer}.aircon-settings-button:hover{background:var(--aircon-button-hover)}");
        return "<!doctype html><html><head><meta charset=\"utf-8\"><style>" + css + "</style></head><body><div class=\"aircon-settings\"><div class=\"aircon-settings-title\">AIrCon設定</div><form><div class=\"aircon-settings-row\"><div class=\"aircon-settings-label\">ウィンドウ位置とサイズを記憶する</div><div class=\"aircon-settings-control\"><input class=\"aircon-toggle\" type=\"checkbox\" name=\"rememberWindowPlacement\" value=\"true\"" + checkedAttr + "></div></div><div class=\"aircon-settings-row\"><div class=\"aircon-settings-label\">ザッピング間隔</div><div class=\"aircon-settings-control\"><select name=\"zappingIntervalSeconds\"><option value=\"30\"" + Selected(30) + ">30秒</option><option value=\"60\"" + Selected(60) + ">60秒</option><option value=\"90\"" + Selected(90) + ">90秒</option><option value=\"120\"" + Selected(120) + ">120秒</option><option value=\"180\"" + Selected(180) + ">180秒</option></select></div></div><div class=\"aircon-settings-row\"><div class=\"aircon-settings-label\">起動時に表示する放送波</div><div class=\"aircon-settings-control\"><select name=\"startupWave\"><option value=\"GR\"" + WaveSelected("GR") + ">GR</option><option value=\"BS\"" + WaveSelected("BS") + ">BS</option><option value=\"CS\"" + WaveSelected("CS") + ">CS</option></select></div></div><div class=\"aircon-settings-actions\"><button class=\"aircon-settings-button\" type=\"button\" " + closeAttrs + ">戻る</button><button class=\"aircon-settings-button\" type=\"button\" " + saveAttrs + ">保存</button></div></form></div></body></html>";
    }

    private static string BuildRefreshForm(WindowOperation window, string filter, string selectedViewerProfile)
    {
        if (!window.CanSelfRefresh) return string.Empty;
        var fields = new Dictionary<string, string?>
        {
            ["pluginId"] = window.PluginId,
            ["routeSegment"] = window.RouteSegment,
            ["action"] = "refreshWindow",
            ["token"] = window.WindowToken,
            ["windowId"] = window.WindowId,
            ["currentWindowId"] = window.WindowId,
            ["target"] = RefreshTargetContent,
            ["refreshTarget"] = RefreshTargetContent,
            ["preserveScroll"] = BoolTrue,
            ["responseMode"] = ResponseModeHostHandled,
            ["response-mode"] = ResponseModeHostHandled,
            ["refresh-target"] = RefreshTargetContent,
            ["wave"] = filter,
            ["currentWave"] = filter,
            ["viewerProfile"] = selectedViewerProfile,
            ["viewer-profile"] = selectedViewerProfile,
            ["refreshQuery"] = "wave=" + filter + "&viewerProfile=" + selectedViewerProfile,
            ["clientVersion"] = ClientVersion
        };
        return "<form class=\"aircon-action-form\" method=\"" + HtmlAttr(window.WindowMethod) + "\" action=\"" + HtmlAttr(window.WindowEndpoint) + "\" data-role=\"content-refresh\" data-action=\"refreshWindow\">" + HiddenInputs(fields) + "<button class=\"aircon-toolbar-button aircon-action-button aircon-action-refresh\" type=\"submit\" data-role=\"content-refresh\" data-action=\"refreshWindow\" aria-label=\"更新\" title=\"一覧を更新\">↻</button></form>";
    }
    private static string HiddenInputs(Dictionary<string, string?> fields)
        => string.Join("", fields.Select(kv => $"<input type=\"hidden\" name=\"{HtmlAttr(kv.Key)}\" value=\"{HtmlAttr(kv.Value ?? string.Empty)}\">"));

    private static string BuildFilterForm(RuntimeUiRenderContext context, string group, string label, string current, string selectedTuner, ViewerProfileChoice selectedViewerProfile, bool top, ViewerProfileState profiles, ViewerOperation action, WindowOperation window)
    {
        var isCurrent = group.Equals(current, StringComparison.OrdinalIgnoreCase);
        var cls = isCurrent ? "aircon-toolbar-button aircon-wave-button aircon-wave-button-on" : "aircon-toolbar-button aircon-wave-button";
        var targetProfile = profiles.AvailableForWave(group)
            .FirstOrDefault(x => x.Id.Equals(selectedViewerProfile.Value, StringComparison.OrdinalIgnoreCase))
            ?? profiles.AvailableForWave(group).FirstOrDefault();
        if (targetProfile == null)
            return $"<button class=\"{cls} aircon-wave-button-disabled\" type=\"button\" disabled aria-disabled=\"true\">{Html(label)}</button>";
        var fields = BuildViewerActivateFields(action, window, group, targetProfile.Id);
        // Wave selection refreshes only the ToolWindow content route.
        // AIrCon owns row positioning inside its own HTML after render.
        var attrs = BuildHostActionAttributes(context, fields, "click", "hostHandled");
        return "<span class=\"aircon-nav-form\" data-role=\"wave-selector\" data-wave=\"" + HtmlAttr(group) + "\">" +
            $"<button class=\"{cls}\" type=\"button\" {attrs} data-role=\"wave-selector\" data-wave=\"{HtmlAttr(group)}\" aria-pressed=\"{(isCurrent ? "true" : "false")}\" title=\"{HtmlAttr("放送波: " + label)}\">{Html(label)}</button></span>";
    }

    private static Dictionary<string, string?> BuildViewerActivateFields(ViewerOperation action, WindowOperation window, string wave, string viewerProfile)
    {
        return new Dictionary<string, string?>
        {
            ["operation"] = AirConActionViewerActivate,
            ["wave"] = wave,
            ["viewerProfile"] = viewerProfile,
            ["refreshQuery"] = "wave=" + wave + "&viewerProfile=" + viewerProfile,
            ["clientVersion"] = ClientVersion
        };
    }

    private static Dictionary<string, string?> ToolContentFields(string filter, string tuner, string selectedViewerProfile, bool top, WindowOperation window)
    {
        var fields = new Dictionary<string, string?>
        {
            ["wave"] = filter,
            ["viewerProfile"] = selectedViewerProfile,
            ["viewer-profile"] = selectedViewerProfile,
            ["__tvairHostWindow"] = "1",
            ["__tvairToolHostContent"] = "1"
        };
        if (!string.IsNullOrWhiteSpace(window.WindowId)) fields["__tvairWindowId"] = window.WindowId;
        return fields;
    }

    private static IReadOnlyList<TunerChoice> BuildTunerChoices(IReadOnlyList<ViewerTunerRow> tuners, string filter)
    {
        var list = new List<TunerChoice> { TunerChoice.Auto };
        foreach (var t in tuners.Where(x => x.IsSelectableForViewer).OrderBy(x => x.SlotIndex))
        {
            if (filter == "GR" && !t.ProgramGuideFilterGroup.Equals("GR", StringComparison.OrdinalIgnoreCase)) continue;
            if ((filter == "BS" || filter == "CS") && t.ProgramGuideFilterGroup.Equals("GR", StringComparison.OrdinalIgnoreCase)) continue;
            var value = t.Name + "|" + t.Did + "|" + t.SlotIndex;
            list.Add(new TunerChoice(value, t.Name, t.Did, t.SlotIndex, t.Name, false));
        }
        return list;
    }

    private static TunerChoice ResolveSelectedTuner(IReadOnlyList<TunerChoice> choices, string value)
        => choices.FirstOrDefault(x => x.Value.Equals(value, StringComparison.OrdinalIgnoreCase)) ?? TunerChoice.Auto;

    private static string NormalizeFilter(string? value)
    {
        var v = (value ?? string.Empty).Trim().ToUpperInvariant();
        return v switch
        {
            "T" or "TERRESTRIAL" or "地" or "地デジ" => "GR",
            "GR" => "GR",
            "BS" => "BS",
            "CS" => "CS",
            _ => "ALL"
        };
    }

    private static string NormalizeWaveFilter(string? value)
    {
        var v = NormalizeFilter(value);
        return v is "BS" or "CS" ? v : "GR";
    }

    private static string FilterLabel(string group)
    {
        switch (NormalizeFilter(group))
        {
            case "GR":
                return "地デジ";
            case "BS":
                return "BS";
            case "CS":
                return "CS";
            default:
                return "全";
        }
    }
    private static string ServiceKey(int nid, int tsid, int sid) => nid + ":" + tsid + ":" + sid;

    private static bool IsToolWindow(RuntimeUiRenderContext c)
    {
        // ToolWindow判定はTvAIr本体から渡るhost-managed contextだけを正とする。
        // __tvairToolHost系クエリは通常ブラウザ側のreturnUrlへ混入し得るため、
        // それ単体でToolWindow扱いにしない。
        return c.IsHostManagedWindowContent || !string.IsNullOrWhiteSpace(c.CurrentWindowId);
    }

    private static string QueryWindowId(RuntimeUiRenderContext c)
    {
        // Host-managed ToolWindowではCurrentWindowIdが唯一の正本。
        // route/query内のwindowIdはreturnUrlや過去navigation由来の値を含み得るため、
        // liveなToolWindow subscriptionへ逆投影してはならない。
        var currentWindowId = (c.CurrentWindowId ?? string.Empty).Trim();
        if (c.IsHostManagedWindowContent || !string.IsNullOrWhiteSpace(currentWindowId))
            return currentWindowId;

        // 非host-managed描画だけ互換入力としてquery由来IDを許可する。
        var q = ExtractQueryDictionary(c);
        foreach (var key in new[] { "__tvairWindowId", "_tvairWindowId", "currentWindowId", "windowId" })
            if (q.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)) return v.Trim();
        return string.Empty;
    }

    private static Dictionary<string, string> ExtractQueryDictionary(RuntimeUiRenderContext c)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[]
        {
            "QueryString", "RawQuery", "PathAndQuery", "RawUrl", "Url", "RequestUrl", "AbsoluteUrl", "OriginalUrl",
            "RequestQuery", "Query", "RouteQuery", "CurrentQueryString", "CurrentContentRoute", "ContentRoute"
        })
        {
            var raw = ReadProperty(c, name)?.ToString();
            MergeQuery(dict, raw);
        }

        // TvAIr本体が将来Query専用propertyを増やしても拾えるよう、URL/Route/Query系string propertyは横断的に見る。
        foreach (var prop in c.GetType().GetProperties())
        {
            if (prop.PropertyType != typeof(string)) continue;
            var name = prop.Name ?? string.Empty;
            if (!(name.Contains("Query", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Url", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Route", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Path", StringComparison.OrdinalIgnoreCase))) continue;
            try { MergeQuery(dict, prop.GetValue(c)?.ToString()); } catch { }
        }

        foreach (var mapName in new[] { "WindowContract", "ActionContract", "ToolWindowCapabilities", "ViewerControlActionContract" })
        {
            var map = ReadStringDictionary(c, mapName);
            foreach (var kv in map)
            {
                if (!string.IsNullOrWhiteSpace(kv.Key) && kv.Key.Equals("wave", StringComparison.OrdinalIgnoreCase)) dict["wave"] = kv.Value;
                MergeQuery(dict, kv.Value);
            }
        }
        return dict;
    }

    private static void MergeQuery(Dictionary<string, string> dict, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;
        var q = query;
        var idx = q.IndexOf('?');
        if (idx >= 0) q = q[(idx + 1)..];
        var hash = q.IndexOf('#');
        if (hash >= 0) q = q[..hash];
        foreach (var pair in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = WebUtility.UrlDecode(parts[0] ?? string.Empty) ?? string.Empty;
            var value = parts.Length > 1 ? WebUtility.UrlDecode(parts[1] ?? string.Empty) ?? string.Empty : string.Empty;
            if (!string.IsNullOrWhiteSpace(key)) dict[key] = value;
        }
    }

    private static string PayloadValue(RuntimeUiActionContext request, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (request.Payload.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return string.Empty;
    }

    private static string QueryString(IReadOnlyDictionary<string, string> query, string key) => query.TryGetValue(key, out var v) ? v : string.Empty;

    private static string ReadString(object? obj, params string[] names)
    {
        foreach (var name in names)
        {
            var v = ReadProperty(obj, name)?.ToString();
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }
        return string.Empty;
    }

    private static Dictionary<string, string> ReadStringDictionary(object? obj, params string[] names)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var value = ReadProperty(obj, name);
            if (value is System.Collections.IDictionary d)
            {
                foreach (System.Collections.DictionaryEntry e in d)
                {
                    var k = e.Key?.ToString();
                    if (!string.IsNullOrWhiteSpace(k)) result[k] = e.Value?.ToString() ?? string.Empty;
                }
            }
            else if (value is System.Collections.IEnumerable en && value is not string)
            {
                foreach (var item in en)
                {
                    var k = ReadProperty(item, "Key")?.ToString();
                    if (!string.IsNullOrWhiteSpace(k)) result[k] = ReadProperty(item, "Value")?.ToString() ?? string.Empty;
                }
            }
        }
        return result;
    }

    private static bool ReadDictBool(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b, string key)
    {
        if ((a.TryGetValue(key, out var v) || b.TryGetValue(key, out v)) && bool.TryParse(v, out var parsed)) return parsed;
        return false;
    }

    private static string GetValue(IReadOnlyDictionary<string, string> dict, string key) => dict.TryGetValue(key, out var v) ? v : string.Empty;

    private static IReadOnlyList<string> SplitCsv(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(new[] { ',', '|', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static object? ReadProperty(object? obj, string name)
    {
        if (obj == null) return null;
        var prop = obj.GetType().GetProperty(name);
        if (prop == null) return null;
        try { return prop.GetValue(obj); } catch { return null; }
    }

    private static string FirstNonEmpty(params string?[] values) => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? string.Empty;
    private static string Html(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
    private static string HtmlAttr(string? value) => Html(value).Replace("\"", "&quot;");

    private sealed record ViewerSessionContractState(string ViewerSessionId, long Generation);
    private sealed record ZappingState(bool Active, DateTimeOffset StartedAt, DateTimeOffset LastTickAt, DateTimeOffset NextTickAt, string LastServiceKey, string WindowId, string Wave, string ViewerProfile, long Generation, string ViewerSessionId, long ViewerGeneration, int ProcessId, int IntervalSeconds);
    private sealed record PowerOffState(bool Active, DateTimeOffset Deadline, string ViewerProfile, string WindowId, long Generation);
    private sealed record ZappingTickResult(bool Success, string Diagnostics);

    private sealed record FocusTriplet(int? NetworkId, int? TransportStreamId, int? ServiceId)
    {
        public bool IsResolved => NetworkId.GetValueOrDefault() > 0 && TransportStreamId.GetValueOrDefault() > 0 && ServiceId.GetValueOrDefault() > 0;
    }

    private sealed record ViewerStartPayload(string NetworkId, string TransportStreamId, string ServiceId, string ChannelSpace, string ChannelIndex, string ChannelArgument, string ProgramGuideFilterGroup, string BroadcastGroup, string AllocationGroup, string TunerGroup, string ServiceName, string PreferredTunerName, string PreferredDid, string PreferredSlot, string ViewerProfile, string ViewerProfileName);
    private sealed record FloatingViewerData(IReadOnlyList<ServiceRow> Services, IReadOnlyList<ViewerSessionRow> ViewerSessions, IReadOnlyList<ViewerTunerRow> ViewerTuners, IReadOnlyList<WaveFilterRow> WaveFilters, ViewerProfileState ViewerProfiles, IReadOnlyList<string> Diagnostics, bool ProjectionUsed, bool SafeEventContractAvailable, bool SafeDblclickEvents, int ServiceColumnWidthPx);
    private sealed record ViewerOperation(bool CanPost, string ActionEndpoint, string ActionRoute, string ActionMethod, string ActionToken, string PluginId, string RouteSegment);
    private sealed record HostActionDispatchResult(bool Success, string Diagnostics, string Message)
    {
        public static HostActionDispatchResult Ok(string diagnostics) => new(true, diagnostics, "OK");
        public static HostActionDispatchResult Failure(string diagnostics, string message) => new(false, diagnostics, message);
    }
    private sealed record WindowOperation(bool CanOpen, bool CanSelfRefresh, string WindowEndpoint, string WindowRoute, string WindowMethod, string WindowToken, string PluginId, string RouteSegment, string WindowId, string WindowStateEndpoint, bool ToolWindowSupported);
    private sealed record ViewerSessionRow(string LeaseId, string ServiceName, string ProgramGuideFilterGroup, string AllocationGroup, string TunerGroup, string TunerName, string Did, int SlotIndex, ushort? NetworkId, ushort? TransportStreamId, ushort? ServiceId, bool Current, string ViewerState, string ViewerProfile, string ViewerProfileName, string TvTestPathKey, string ViewerSessionId, long Generation, int? ProcessId)
    {
        public bool IsActive => Current || ViewerState.Equals("launched", StringComparison.OrdinalIgnoreCase) || ViewerState.Equals("active", StringComparison.OrdinalIgnoreCase) || ViewerState.Equals("viewing", StringComparison.OrdinalIgnoreCase);
    }
    private sealed record ViewerProfileChoice(string Value, string Name, bool Enabled, bool IsDefault, int Order, IReadOnlyList<string> AvailableGroups, int TvTestFrameIndex, string LogicalViewerSlotId, bool IsShared)
    {
        public string Id => Value;
        public bool IsAvailableForWave(string wave)
        {
            if (AvailableGroups == null || AvailableGroups.Count == 0) return true;
            var required = RequiredProfileGroupForWave(wave);
            return AvailableGroups.Any(x => NormalizeAvailableGroup(x).Equals(required, StringComparison.OrdinalIgnoreCase));
        }
        public static ViewerProfileChoice Unavailable { get; } = new(string.Empty, string.Empty, false, false, 0, Array.Empty<string>(), 0, string.Empty, false);
    }

    private sealed record ViewerProfileState(IReadOnlyList<ViewerProfileChoice> SelectableProfiles, string DefaultViewerProfile, bool SelectorVisibleRecommended, bool MinWidthInvariantRequired, bool ContractAvailable)
    {
        public IEnumerable<ViewerProfileChoice> AvailableForWave(string wave) => SelectableProfiles.Where(x => x.Enabled && x.IsAvailableForWave(wave));
        public static ViewerProfileState Unavailable { get; } = new(Array.Empty<ViewerProfileChoice>(), string.Empty, false, true, false);
    }

    private sealed record ViewerTunerRow(string Name, string Did, int SlotIndex, string ProgramGuideFilterGroup, string AllocationGroup, bool Busy, bool IsSelectableForViewer, string Role);
    private sealed record WaveFilterRow(string Key, string Group, string Label);
    private sealed record TunerChoice(string Value, string Name, string Did, int SlotIndex, string Label, bool IsAuto)
    {
        public static TunerChoice Auto { get; } = new("auto", string.Empty, string.Empty, -1, "自動", true);
    }

    private sealed class ServiceRow
    {
        public int ProgramGuideOrder { get; set; }
        public string ProgramGuideFilterGroup { get; set; } = "GR";
        public string ProgramGuideFilterLabel { get; set; } = "地デジ";
        public string AllocationGroup { get; set; } = "GR";
        public string TunerGroup { get; set; } = "GR";
        public string ServiceName { get; set; } = string.Empty;
        public int NetworkId { get; set; }
        public int TransportStreamId { get; set; }
        public int ServiceId { get; set; }
        public int ChannelSpace { get; set; }
        public int ChannelIndex { get; set; }
        public string ChannelArgument { get; set; } = string.Empty;
        public string CurrentTitle { get; set; } = string.Empty;
        public DateTimeOffset? CurrentStart { get; set; }
        public DateTimeOffset? CurrentEnd { get; set; }
        public bool HasCurrentProgramProjection { get; set; }
        public bool IsViewing { get; set; }
        public string ViewingViewerProfile { get; set; } = string.Empty;
    }
}

internal sealed record AirConSettings(bool RememberWindowPlacement, int ZappingIntervalSeconds, string StartupWave);

public sealed class AIrConRuntimePlugin : ITvAirRuntimeCapabilityPlugin, ITvAirRuntimeUiPlugin, ITvAirRuntimeLifecyclePlugin
{
    public TvAirPluginRuntimeDescriptor Descriptor { get; } = new()
    {
        PluginId = AIrConRenderer.PluginId,
        DisplayName = AIrConRenderer.PluginListTitle,
        Version = AIrConRenderer.PluginVersion,
        SdkContractVersion = TvAIrPluginSdkContract.HostContractVersion,
        RequiredCapabilities = new[] { TvAirRuntimeCapabilities.StorageRead, TvAirRuntimeCapabilities.StorageWrite },
        RequiredPermissions = new[]
        {
            PluginPermission.ShowUi,
            PluginPermission.OpenToolWindow,
            PluginPermission.ReadChannels,
            PluginPermission.ReadEpg,
            PluginPermission.ReadTunerStatus,
            PluginPermission.ControlViewer,
            PluginPermission.ReadProgramGuideProjection,
            PluginPermission.ReadViewerSessions,
            PluginPermission.ReadViewerTuners,
            PluginPermission.ReadHostContracts,
            PluginPermission.UseActionApi,
            PluginPermission.UseWindowApi,
            PluginPermission.UseAssetApi,
            PluginPermission.UseSafeEvent,
            PluginPermission.ReadTheme,
            PluginPermission.ReadPluginStorage,
            PluginPermission.WritePluginStorage
        },
        Assets = new[]
        {
            new TvAIrPlugin.Assets.PluginAssetDefinition
            {
                LogicalPath = "AIrCon.ico",
                ResourceName = "AIrCon.ico",
                ContentType = "image/x-icon",
                CachePolicy = TvAIrPlugin.Assets.PluginAssetCachePolicy.Revalidate
            }
        },
        Windows = new[]
        {
            new TvAIrPlugin.Windows.PluginWindowDefinition
            {
                WindowDefinitionId = "main", Title = AIrConRenderer.PluginToolWindowTitle,
                InitialSize = new TvAIrPlugin.Windows.PluginWindowSize(540, 320),
                MinimumSize = new TvAIrPlugin.Windows.PluginWindowSize(360, 180),
                ShowInTaskbar = true, RememberPlacement = AIrConRenderer.DefaultRememberWindowPlacement
            }
        },
        Surfaces = new[]
        {
            new TvAIrPlugin.Surfaces.PluginSurfaceDefinition
            {
                SurfaceDefinitionId = "main.web", Kind = TvAIrPlugin.Surfaces.PluginSurfaceKind.Web,
                EntryPoint = "aircon"
            }
        },
        UiDefinitions = new[]
        {
            new RuntimeUiDefinition
            {
                UiDefinitionId = "main", Route = AIrConRenderer.RouteSegment, Kind = RuntimeUiKind.ToolWindow,
                WindowDefinitionId = "main", SurfaceDefinitionId = "main.web"
            }
        },
        MenuActions = new[]
        {
            new PluginMenuActionDefinition
            {
                ActionId = "open",
                Label = AIrConRenderer.PluginListTitle,
                Kind = PluginMenuActionKind.ToolWindow,
                Priority = 420,
                Route = AIrConRenderer.RouteSegment,
                WindowDefinitionId = "main",
                SurfaceDefinitionId = "main.web",
                ShowInTaskbar = true
            }
        },
        Lifecycle = new PluginLifecycleDefinition()
    };
    private readonly AIrConRenderer _ui = new();
    private IDisposable? _runtimeEventSubscriptions;
    public void Initialize(ITvAirPluginRuntimeContext context) => AIrConNewApiBridge.InitializeRuntime(context);
    public string RenderHtml(RuntimeUiRenderContext context) => _ui.RenderHtml(context);
    public Task<RuntimeUiActionResult> HandleActionAsync(RuntimeUiActionContext context, CancellationToken cancellationToken)
        => _ui.HandleActionAsync(context, cancellationToken);
    public void OnStart()
    {
        var settings = AIrConNewApiBridge.LoadSettings();
        var placement = AIrConNewApiBridge.SetPlacementPersistence("main", settings.RememberWindowPlacement);
        _runtimeEventSubscriptions?.Dispose();
        _runtimeEventSubscriptions = AIrConNewApiBridge.SubscribeRuntimeEvents((eventType, eventEnvelope) =>
        {
            if (eventType.Equals("ViewerSessionChanged", StringComparison.OrdinalIgnoreCase) && eventEnvelope != null)
                _ui.ApplyViewerSessionStatePatch(eventEnvelope);
            else if (eventType.Equals("RuntimeWindowLifecycleChanged", StringComparison.OrdinalIgnoreCase) && eventEnvelope != null)
                _ui.ApplyRuntimeWindowLifecycle(eventEnvelope);
        });
    }
    public void OnStop()
    {
        _runtimeEventSubscriptions?.Dispose();
        _runtimeEventSubscriptions = null;
        _ui.StopRuntimeUi();
        AIrConNewApiBridge.ResetRuntime();
    }
}

internal static class AIrConNewApiBridge
{
    private static readonly object Sync = new();
    private static ITvAirPluginRuntimeContext? _runtimeContext;
    private static TvAirServiceDto[]? _serviceProjection;
    private static TvAirProgramGuideWaveFilterDto[]? _waveFilterProjection;
    private static TvAIrPlugin.Viewers.TvAirViewerProfileDto[]? _viewerProfileProjection;
    private static TvAIrPlugin.Viewers.TvAirViewerSessionDto[]? _viewerSessionProjection;
    private static TvAirProgramEventDto[]? _programProjection;
    private static DateTimeOffset _programProjectionFrom;
    private static DateTimeOffset _programProjectionTo;
    private static DateTimeOffset _programProjectionExpiresAt;

    internal static void InitializeRuntime(ITvAirPluginRuntimeContext context)
    {
        lock (Sync)
        {
            _runtimeContext = context;
            InvalidateAllProjectionsLocked();
        }
    }

    internal static void ResetRuntime()
    {
        lock (Sync)
        {
            _runtimeContext = null;
            InvalidateAllProjectionsLocked();
        }
    }

    internal static IDisposable SubscribeRuntimeEvents(Action<string, TvAIrPlugin.Events.PluginEventEnvelope?> onInvalidated)
    {
        var registrations = new List<IDisposable>();
        lock (Sync)
        {
            var context = _runtimeContext;
            if (context == null) return new CompositeDisposable(registrations);
            registrations.Add(context.Events.Subscribe("ProgramGuideUpdated", _ =>
            {
                InvalidateProgramProjection();
                onInvalidated("ProgramGuideUpdated", null);
            }));
            registrations.Add(context.Events.Subscribe("ViewerSessionChanged", eventEnvelope =>
            {
                InvalidateViewerProjection();
                onInvalidated("ViewerSessionChanged", eventEnvelope);
            }));
            registrations.Add(context.Events.Subscribe("SettingsChanged", _ =>
            {
                InvalidateServiceProjection();
                InvalidateViewerProfileProjection();
                onInvalidated("SettingsChanged", null);
            }));
            registrations.Add(context.Events.Subscribe("RuntimeWindowLifecycleChanged", eventEnvelope =>
            {
                onInvalidated("RuntimeWindowLifecycleChanged", eventEnvelope);
            }));
        }
        return new CompositeDisposable(registrations);
    }

    internal static void InvalidateServiceProjection()
    {
        lock (Sync)
        {
            _serviceProjection = null;
            _waveFilterProjection = null;
        }
    }

    internal static void InvalidateViewerProfileProjection()
    {
        lock (Sync) _viewerProfileProjection = null;
    }

    internal static void InvalidateViewerProjection()
    {
        lock (Sync)
        {
            _viewerSessionProjection = null;
        }
    }

    internal static void InvalidateProgramProjection()
    {
        lock (Sync)
        {
            _programProjection = null;
            _programProjectionFrom = default;
            _programProjectionTo = default;
            _programProjectionExpiresAt = default;
        }
    }

    private static void InvalidateAllProjectionsLocked()
    {
        _serviceProjection = null;
        _waveFilterProjection = null;
        _viewerProfileProjection = null;
        _viewerSessionProjection = null;
        _programProjection = null;
        _programProjectionFrom = default;
        _programProjectionTo = default;
        _programProjectionExpiresAt = default;
    }

    internal static AirConSettings LoadSettings()
    {
        lock (Sync)
        {
            bool remember = AIrConRenderer.DefaultRememberWindowPlacement;
            int interval = AIrConRenderer.DefaultZappingIntervalSeconds;
            string wave = "GR";

            if (_runtimeContext != null)
            {
                remember = ReadRuntimeStorageBool(_runtimeContext.Storage, AIrConRenderer.SettingsSection, AIrConRenderer.SettingRememberPlacement, AIrConRenderer.DefaultRememberWindowPlacement);
                interval = ReadRuntimeStorageInt(_runtimeContext.Storage, AIrConRenderer.SettingsSection, AIrConRenderer.SettingZappingIntervalSeconds, AIrConRenderer.DefaultZappingIntervalSeconds);
                wave = ReadRuntimeStorageString(_runtimeContext.Storage, AIrConRenderer.SettingsSection, AIrConRenderer.SettingStartupWave, "GR");
            }

            if (interval is not (30 or 60 or 90 or 120 or 180)) interval = AIrConRenderer.DefaultZappingIntervalSeconds;
            return new AirConSettings(remember, interval, NormalizeWave(wave));
        }
    }

    internal static OperationResult SaveSettings(bool rememberPlacement, int zappingIntervalSeconds, string startupWave)
    {
        try
        {
            lock (Sync)
            {
                var context = RequireRuntimeContext();
                SetRuntimeStorage(context.Storage, AIrConRenderer.SettingsSection, AIrConRenderer.SettingRememberPlacement, rememberPlacement);
                SetRuntimeStorage(context.Storage, AIrConRenderer.SettingsSection, AIrConRenderer.SettingZappingIntervalSeconds, zappingIntervalSeconds);
                SetRuntimeStorage(context.Storage, AIrConRenderer.SettingsSection, AIrConRenderer.SettingStartupWave, NormalizeWave(startupWave));
            }
            return OperationResult.Ok("settings_saved");
        }
        catch (Exception ex) { return OperationResult.Fail(ex.GetType().Name, ex.Message); }
    }

    internal static OperationResult PatchToolWindow(string windowId, IReadOnlyList<RuntimeUiPatch> uiPatches, long stateRevision)
    {
        ITvAirPluginRuntimeContext? context;
        lock (Sync) context = _runtimeContext;
        if (context == null) return OperationResult.Fail("runtime_context_missing", "Runtime context is unavailable.");
        if (string.IsNullOrWhiteSpace(windowId)) return OperationResult.Fail("window_id_missing", "Window id is unavailable.");
        if (stateRevision <= 0) return OperationResult.Fail("state_revision_invalid", "State revision must be positive.");

        try
        {
            var result = context.Windows.PatchToolWindow(new TvAirToolWindowStatePatchRequestDto
            {
                WindowId = windowId,
                UiPatches = uiPatches ?? Array.Empty<RuntimeUiPatch>(),
                StateRevision = stateRevision
            });
            var details = result.Value;
            var diagnostics = details == null
                ? result.Error?.Message ?? string.Empty
                : $"outcome={details.Outcome} requested={details.RequestedPatchCount} applied={details.AppliedPatchCount} revision={details.StateRevision} reason={details.Reason}";
            return result.Succeeded
                ? OperationResult.Ok(string.IsNullOrWhiteSpace(diagnostics) ? "statepatch_applied" : diagnostics)
                : OperationResult.Fail(result.Error?.Code.ToString() ?? "statepatch_failed", result.Error?.Message ?? "StatePatch failed.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("statepatch_exception", ex.Message);
        }
    }

    internal static OperationResult SetPlacementPersistence(string windowDefinitionId, bool rememberPlacement)
    {
        try
        {
            ITvAirPluginRuntimeContext context;
            lock (Sync)
                context = _runtimeContext ?? throw new InvalidOperationException("AIrCon Runtime APIが初期化されていません。");

            var result = context.Windows.SetToolWindowPlacementPersistence(new TvAirToolWindowPlacementPersistenceRequestDto
            {
                WindowDefinitionId = windowDefinitionId,
                RememberPlacement = rememberPlacement,
                ClearSavedPlacement = false
            });
            return result.Succeeded
                ? OperationResult.Ok("placement_persistence_updated")
                : OperationResult.Fail(result.Error?.Code.ToString() ?? "placement_persistence_failed", result.Value?.FailureReason ?? result.Error?.Message ?? "位置記憶設定に失敗しました。");
        }
        catch (Exception ex) { return OperationResult.Fail(ex.GetType().Name, ex.Message); }
    }

    private static bool ReadRuntimeStorageBool(TvAIrPlugin.Storage.ITvAirPluginStorageApi storage, string ns, string key, bool defaultValue)
    {
        var result = storage.Get(ns, key);
        if (!result.Succeeded || result.Value?.Value == null) return defaultValue;
        return TryConvertStorageBool(result.Value.Value, out var value) ? value : defaultValue;
    }

    private static bool TryConvertStorageBool(object raw, out bool value)
    {
        switch (raw)
        {
            case bool boolValue:
                value = boolValue;
                return true;
            case string text:
                if (bool.TryParse(text, out value)) return true;
                if (text == "1") { value = true; return true; }
                if (text == "0") { value = false; return true; }
                break;
            case byte byteValue when byteValue is 0 or 1:
                value = byteValue == 1;
                return true;
            case sbyte sbyteValue when sbyteValue is 0 or 1:
                value = sbyteValue == 1;
                return true;
            case short shortValue when shortValue is 0 or 1:
                value = shortValue == 1;
                return true;
            case ushort ushortValue when ushortValue is 0 or 1:
                value = ushortValue == 1;
                return true;
            case int intValue when intValue is 0 or 1:
                value = intValue == 1;
                return true;
            case uint uintValue when uintValue is 0 or 1:
                value = uintValue == 1;
                return true;
            case long longValue when longValue is 0 or 1:
                value = longValue == 1;
                return true;
            case ulong ulongValue when ulongValue is 0 or 1:
                value = ulongValue == 1;
                return true;
            case JsonElement element:
                if (element.ValueKind == JsonValueKind.True) { value = true; return true; }
                if (element.ValueKind == JsonValueKind.False) { value = false; return true; }
                if (element.ValueKind == JsonValueKind.String)
                    return TryConvertStorageBool(element.GetString() ?? string.Empty, out value);
                if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var numeric))
                    return TryConvertStorageBool(numeric, out value);
                break;
        }

        value = false;
        return false;
    }

    private static int ReadRuntimeStorageInt(TvAIrPlugin.Storage.ITvAirPluginStorageApi storage, string ns, string key, int defaultValue)
    {
        var result = storage.Get(ns, key);
        if (!result.Succeeded || result.Value?.Value == null) return defaultValue;
        return TryConvertStorageInt(result.Value.Value, out var value) ? value : defaultValue;
    }

    private static bool TryConvertStorageInt(object raw, out int value)
    {
        switch (raw)
        {
            case int intValue:
                value = intValue;
                return true;
            case long longValue when longValue is >= int.MinValue and <= int.MaxValue:
                value = (int)longValue;
                return true;
            case string text when int.TryParse(text, out var parsed):
                value = parsed;
                return true;
            case JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var jsonInt):
                value = jsonInt;
                return true;
            case JsonElement element when element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out var jsonStringInt):
                value = jsonStringInt;
                return true;
            default:
                value = default;
                return false;
        }
    }

    private static string ReadRuntimeStorageString(TvAIrPlugin.Storage.ITvAirPluginStorageApi storage, string ns, string key, string defaultValue)
    {
        var result = storage.Get(ns, key);
        if (!result.Succeeded || result.Value?.Value == null) return defaultValue;
        return result.Value.Value is JsonElement element
            ? element.ValueKind == JsonValueKind.String ? element.GetString() ?? defaultValue : element.ToString()
            : Convert.ToString(result.Value.Value) ?? defaultValue;
    }

    private static void SetRuntimeStorage(TvAIrPlugin.Storage.ITvAirPluginStorageApi storage, string ns, string key, object value)
    {
        var result = storage.Set(ns, key, value);
        if (!result.Succeeded)
            throw new InvalidOperationException(result.Error?.Message ?? $"Plugin Storage書込に失敗しました: {ns}/{key}");
    }

    internal static IReadOnlyList<TvAirServiceDto> ListServices()
    {
        lock (Sync)
        {
            if (_serviceProjection != null) return _serviceProjection;
            _serviceProjection = _runtimeContext?.Channels.ListServices(new TvAirServiceQueryDto { Enabled = true }).ToArray()
                ?? Array.Empty<TvAirServiceDto>();
            return _serviceProjection;
        }
    }

    internal static IReadOnlyList<TvAirProgramGuideWaveFilterDto> ListWaveFilters()
    {
        lock (Sync)
        {
            if (_waveFilterProjection != null) return _waveFilterProjection;
            _waveFilterProjection = _runtimeContext?.ProgramGuide.ListWaveFilters().ToArray()
                ?? Array.Empty<TvAirProgramGuideWaveFilterDto>();
            return _waveFilterProjection;
        }
    }

    internal static IReadOnlyList<TvAirProgramEventDto> ListProgramEvents(DateTimeOffset from, DateTimeOffset to)
    {
        lock (Sync)
        {
            var now = DateTimeOffset.Now;
            var cacheValid = _programProjection != null
                && now < _programProjectionExpiresAt
                && from >= _programProjectionFrom
                && to <= _programProjectionTo;
            if (!cacheValid)
            {
                // RenderHtml asks for [now, now+6h]. Cache a deliberate superset so
                // the next render's slightly advanced upper bound remains covered.
                // Freshness is owned by ProgramGuideUpdated and the nearest programme
                // boundary, not by comparing two independently sampled now values.
                var queryFrom = from.AddMinutes(-1);
                var queryTo = to.AddMinutes(10);
                _programProjection = _runtimeContext?.ProgramGuide.ListEvents(new TvAirProgramGuideQueryDto
                {
                    From = queryFrom,
                    To = queryTo,
                    Limit = 20000
                }).ToArray() ?? Array.Empty<TvAirProgramEventDto>();
                _programProjectionFrom = queryFrom;
                _programProjectionTo = queryTo;

                var nextBoundary = _programProjection
                    .Where(x => x.Start <= now && x.End > now)
                    .Select(x => x.End)
                    .DefaultIfEmpty(now.AddMinutes(5))
                    .Min();
                var safetyExpiry = now.AddMinutes(5);
                _programProjectionExpiresAt = nextBoundary < safetyExpiry ? nextBoundary : safetyExpiry;
            }
            else
            {
            }
            return _programProjection!
                .Where(x => x.End > from && x.Start < to)
                .ToArray();
        }
    }

    internal static IReadOnlyList<TvAIrPlugin.Viewers.TvAirViewerProfileDto> ListProfiles()
    {
        lock (Sync)
        {
            if (_viewerProfileProjection != null) return _viewerProfileProjection;
            _viewerProfileProjection = _runtimeContext?.Viewers.ListProfiles().ToArray() ?? Array.Empty<TvAIrPlugin.Viewers.TvAirViewerProfileDto>();
            return _viewerProfileProjection;
        }
    }

    internal static IReadOnlyList<TvAIrPlugin.Viewers.TvAirViewerSessionDto> ListSessions()
    {
        lock (Sync)
        {
            if (_viewerSessionProjection != null) return _viewerSessionProjection;
            _viewerSessionProjection = _runtimeContext?.Viewers.ListSessions().ToArray() ?? Array.Empty<TvAIrPlugin.Viewers.TvAirViewerSessionDto>();
            return _viewerSessionProjection;
        }
    }

    private sealed class CompositeDisposable : IDisposable
    {
        private readonly List<IDisposable> _items;
        private int _disposed;
        internal CompositeDisposable(List<IDisposable> items) => _items = items;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            foreach (var item in _items)
            {
                try { item.Dispose(); } catch { }
            }
            _items.Clear();
        }
    }

    internal static TvAIrPlugin.Viewers.TvAirViewerSessionDto? GetSession(string viewerSessionId)
    {
        lock (Sync)
        {
            var context = _runtimeContext;
            if (context == null || string.IsNullOrWhiteSpace(viewerSessionId)) return null;
            return context.Viewers.GetSession(viewerSessionId);
        }
    }

    internal static async Task<OperationResult> TuneAsync(string viewerProfileId, string wave, int networkId, int transportStreamId, int serviceId, string? requiredSessionId = null, long? requiredGeneration = null, int? requiredProcessId = null, string? viewerActivation = null)
    {
        try
        {
            var context = RequireRuntimeContext();
            var normalizedWave = NormalizeWave(wave);
            ValidateProfile(context, viewerProfileId, normalizedWave);
            var service = context.Channels.ListServices(new TvAirServiceQueryDto { Enabled = true })
                .FirstOrDefault(x => x.NetworkId == networkId && x.TransportStreamId == transportStreamId && x.ServiceId == serviceId);
            if (service is null) return OperationResult.Fail("service_not_found", "選択した局が見つかりません。");
            if (networkId is < 0 or > ushort.MaxValue || transportStreamId is < 0 or > ushort.MaxValue || serviceId is < 0 or > ushort.MaxValue)
                return OperationResult.Fail("service_identity_out_of_range", "局識別子が範囲外です。");
            var profiles = context.Viewers.ListProfiles();
            var profile = profiles.First(x => x.ViewerProfileId.Equals(viewerProfileId, StringComparison.Ordinal));
            var sessions = context.Viewers.ListSessions();
            var currentMatches = sessions.Where(x => x.ViewerProfileId.Equals(viewerProfileId, StringComparison.Ordinal)).ToArray();
            if (currentMatches.Length > 1)
                return OperationResult.Fail("viewer_profile_duplicate_sessions", "視聴状態を確認できません。もう一度お試しください。");
            var current = currentMatches.FirstOrDefault();
            if (current is null)
            {
                var slotOccupants = sessions.Where(x =>
                    !string.IsNullOrWhiteSpace(x.LogicalViewerSlotId)
                    && x.LogicalViewerSlotId.Equals(profile.LogicalViewerSlotId, StringComparison.Ordinal)
                    && !x.ViewerProfileId.Equals(viewerProfileId, StringComparison.Ordinal)).ToArray();
                if (slotOccupants.Length > 0)
                    return OperationResult.Fail("viewer_slot_occupied", "選択した視聴先は使用中です。");
            }
            if (!TvAirViewerActivation.Activate.Equals(viewerActivation, StringComparison.OrdinalIgnoreCase)
                && !TvAirViewerActivation.Preserve.Equals(viewerActivation, StringComparison.OrdinalIgnoreCase))
                return OperationResult.Fail("viewer_activation_invalid", "Viewer Activation指定が不正です。");
            if (!string.IsNullOrWhiteSpace(requiredSessionId))
            {
                if (current is null || !current.ViewerSessionId.Equals(requiredSessionId, StringComparison.Ordinal) || current.Generation != requiredGeneration || current.ProcessId != requiredProcessId)
                    return OperationResult.Fail("viewer_identity_changed", "ザッピング開始時に固定したTVTestと現在のTVTestが一致しません。");
            }
            var result = await context.Viewers.StartAsync(new TvAirViewerStartRequest
            {
                ViewerProfileId = viewerProfileId,
                Service = new TvAirServiceIdentityDto
                {
                    NetworkId = (ushort)networkId,
                    TransportStreamId = (ushort)transportStreamId,
                    ServiceId = (ushort)serviceId,
                    ServiceName = service.ServiceName
                },
                ViewerSessionId = current?.ViewerSessionId,
                ExpectedGeneration = current?.Generation,
                PreserveViewerWindowState = true,
                ViewerActivation = viewerActivation,
                RetuneExistingViewer = current is not null
            }).ConfigureAwait(false);
            if (!result.Succeeded || result.Value is null)
                return OperationResult.Fail(result.Error?.Code.ToString() ?? "viewer_start_failed", result.Error?.Message ?? "選局に失敗しました。");
            return OperationResult.FromViewer(current is null ? "viewer_started" : "viewer_retuned", result.Value);
        }
        catch (Exception ex) { return OperationResult.Fail(ex.GetType().Name, ex.Message); }
    }



    internal static OperationResult RefreshToolWindow(string windowId, string? contentRoute)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(windowId))
                return OperationResult.Fail("window_id_missing", "AIrCon画面を更新できませんでした。");
            var context = RequireRuntimeContext();
            var result = context.Windows.RefreshToolWindow(new TvAirToolWindowRefreshRequestDto
            {
                WindowId = windowId,
                ContentRoute = contentRoute
            });
            return result.Succeeded
                ? OperationResult.Ok("toolwindow_refreshed")
                : OperationResult.Fail(result.Error?.Code.ToString() ?? "toolwindow_refresh_failed", result.Error?.Message ?? "AIrCon画面を更新できませんでした。");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail(ex.GetType().Name, ex.Message);
        }
    }

    internal static async Task<OperationResult> ActivateAsync(string viewerProfileId)
    {
        try
        {
            var context = RequireRuntimeContext();
            var session = context.Viewers.ListSessions().FirstOrDefault(x => x.ViewerProfileId.Equals(viewerProfileId, StringComparison.Ordinal));
            if (session is null) return OperationResult.Fail("viewer_session_not_found", "対象TVTestは起動していません。");
            var result = await context.Viewers.ActivateAsync(new TvAirViewerActivateRequest
            {
                ViewerSessionId = session.ViewerSessionId,
                ExpectedGeneration = session.Generation
            }).ConfigureAwait(false);
            if (!result.Succeeded || result.Value is null)
            {
                // Activate is intentionally non-starting. The host may discover that the projected
                // process exited, synchronously close the stale ViewerSession/lease, and report the
                // operation as skipped. Some capability adapters surface that skipped outcome as a
                // non-success result, so confirm the authoritative post-operation session state
                // before classifying it as an error. Only disappearance of the exact session that
                // we attempted to activate is accepted here; genuine activation failures remain errors.
                var sessionStillExists = context.Viewers.ListSessions().Any(x =>
                    x.ViewerSessionId.Equals(session.ViewerSessionId, StringComparison.Ordinal) &&
                    !x.State.Equals("stopped", StringComparison.OrdinalIgnoreCase) &&
                    !x.State.Equals("closed", StringComparison.OrdinalIgnoreCase));
                if (!sessionStillExists)
                    return OperationResult.Ok("viewer_process_exited_recovered");

                return OperationResult.Fail(result.Error?.Code.ToString() ?? "viewer_activate_failed", result.Error?.Message ?? "TVTestの前面化に失敗しました。");
            }
            return OperationResult.FromViewer("viewer_activated", result.Value);
        }
        catch (Exception ex) { return OperationResult.Fail(ex.GetType().Name, ex.Message); }
    }

    internal static async Task<OperationResult> StopAsync(string viewerProfileId)
    {
        try
        {
            var context = RequireRuntimeContext();
            var session = context.Viewers.ListSessions().FirstOrDefault(x => x.ViewerProfileId.Equals(viewerProfileId, StringComparison.Ordinal));
            if (session is null) return OperationResult.Ok("viewer_already_stopped");
            var result = await context.Viewers.StopAsync(new TvAirViewerStopRequest
            {
                ViewerSessionId = session.ViewerSessionId,
                ExpectedGeneration = session.Generation
            }).ConfigureAwait(false);
            if (!result.Succeeded || result.Value is null)
                return OperationResult.Fail(result.Error?.Code.ToString() ?? "viewer_stop_failed", result.Error?.Message ?? "視聴停止に失敗しました。");
            return OperationResult.FromViewer("viewer_stopped", result.Value);
        }
        catch (Exception ex) { return OperationResult.Fail(ex.GetType().Name, ex.Message); }
    }

    private static ITvAirPluginRuntimeContext RequireRuntimeContext() { lock (Sync) return _runtimeContext ?? throw new InvalidOperationException("AIrCon Runtimeが初期化されていません。"); }
    private static void ValidateProfile(ITvAirPluginRuntimeContext context, string viewerProfileId, string wave)
    {
        if (string.IsNullOrWhiteSpace(viewerProfileId)) throw new InvalidOperationException("視聴先を選択してください。");
        var profile = context.Viewers.ListProfiles().FirstOrDefault(x => x.ViewerProfileId.Equals(viewerProfileId, StringComparison.Ordinal));
        if (profile is null || !profile.IsAvailable) throw new InvalidOperationException("選択した視聴先を使用できません。");
        var accepted = profile.BroadcastGroups.Any(group =>
        {
            var normalized = NormalizeWave(group);
            return wave == "GR" ? normalized == "GR" : normalized is "BS" or "CS" || group.Contains("BSCS", StringComparison.OrdinalIgnoreCase);
        });
        if (!accepted) throw new InvalidOperationException("選択した視聴先は現在の放送波に対応していません。");
    }
    private static string NormalizeWave(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToUpperInvariant();
        if (text == "CS" || text.Contains("CS")) return "CS";
        if (text == "BS" || text.Contains("BS")) return "BS";
        if (text == "GR" || text.Contains("GROUND") || text.Contains("TERRESTRIAL") || text.Contains("地上")) return "GR";
        throw new InvalidOperationException("放送波を特定できません。");
    }
    internal sealed record OperationResult(
        bool Success,
        string Diagnostics,
        string Message,
        string ViewerSessionId = "",
        long Generation = 0,
        int? ProcessId = null,
        int? NetworkId = null,
        int? TransportStreamId = null,
        int? ServiceId = null,
        bool OperationCompleted = false,
        bool HasWarning = false,
        bool ContinuationRecommended = false)
    {
        internal static OperationResult Ok(string diagnostics) => new(true, diagnostics, "OK", OperationCompleted: true, ContinuationRecommended: true);
        internal static OperationResult FromViewer(string diagnostics, TvAirViewerOperationDto value)
        {
            var focusDiagnostics = string.IsNullOrWhiteSpace(value.FocusPolicyRequested)
                ? diagnostics
                : diagnostics
                    + " focusPolicyRequested=" + value.FocusPolicyRequested
                    + " focusPolicyApplied=" + value.FocusPolicyApplied
                    + " foregroundBeforePid=" + (value.ForegroundBeforePid?.ToString() ?? "-")
                    + " foregroundAfterRetunePid=" + (value.ForegroundAfterRetunePid?.ToString() ?? "-")
                    + " foregroundFinalPid=" + (value.ForegroundFinalPid?.ToString() ?? "-")
                    + " foregroundChanged=" + value.ForegroundChanged
                    + " changedToTargetViewer=" + value.ChangedToTargetViewer
                    + " restorationAttempted=" + value.RestorationAttempted
                    + " restorationSucceeded=" + value.RestorationSucceeded
                    + " focusPreserved=" + value.FocusPreserved
                    + " focusFailureReason=" + (string.IsNullOrWhiteSpace(value.FocusPreserveFailureReason) ? "-" : value.FocusPreserveFailureReason)
                    + " operationCompleted=" + value.OperationCompleted
                    + " hasWarning=" + value.HasWarning
                    + " continuationRecommended=" + value.ContinuationRecommended;
            return new(
                value.OperationCompleted,
                focusDiagnostics,
                value.Message,
                value.ViewerSessionId,
                value.Generation,
                value.ProcessId,
                value.CurrentService?.NetworkId,
                value.CurrentService?.TransportStreamId,
                value.CurrentService?.ServiceId,
                value.OperationCompleted,
                value.HasWarning,
                value.ContinuationRecommended);
        }
        internal static OperationResult Fail(string diagnostics, string message) => new(false, diagnostics, message);
    }
}

