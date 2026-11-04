using System.Text.Json;
using System.Net;
using TvAIrPlugin;
using TvAIrPlugin.Runtime;
using TvAIrPlugin.Viewers;
using ViewerSessionDto = TvAIrPlugin.Viewers.TvAirViewerSessionDto;

namespace AIrCon.BasicPlugin;

internal static class AIrConNewApiBridge
{
    private static readonly object Sync = new();
    private static ITvAirPluginRuntimeContext? _runtimeContext;
    private static TvAirServiceDto[]? _serviceProjection;
    private static TvAirProgramGuideWaveFilterDto[]? _waveFilterProjection;
    private static TvAIrPlugin.Viewers.TvAirViewerProfileDto[]? _viewerProfileProjection;
    private static ViewerSessionDto[]? _viewerSessionProjection;
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

    internal static IDisposable SubscribeRuntimeEvents(Action<string, TvAIrPlugin.Events.PluginEventEnvelope?> onInvalidated, Action<TvAirViewerOperationPreempting> onViewerOperationPreempting)
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
            registrations.Add(context.Events.Subscribe("ViewerReservationChanged", eventEnvelope =>
            {
                onInvalidated("ViewerReservationChanged", eventEnvelope);
            }));
            registrations.Add(context.Viewers.SubscribeOperationPreempting(onViewerOperationPreempting));
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
#if AIRCON_DEVELOPER_DIAGNOSTICS
    internal static void LogInfo(string message)
    {
        lock (Sync)
        {
            var context = _runtimeContext;
            if (context == null) return;
            context.Logs.Write(new TvAirLogWriteDto { Level = "Info", Category = "AIrCon", Message = message });
        }
    }
#endif

    internal static AirConSettings LoadSettings()
    {
        lock (Sync)
        {
            bool remember = AIrConRenderer.DefaultRememberWindowPlacement;
            int interval = AIrConRenderer.DefaultZappingIntervalSeconds;
            string wave = AIrConRenderer.DefaultStartupWave;

            if (_runtimeContext != null)
            {
                remember = ReadRuntimeStorageBool(_runtimeContext.Storage, AIrConRenderer.SettingsSection, AIrConRenderer.SettingRememberPlacement, AIrConRenderer.DefaultRememberWindowPlacement);
                interval = ReadRuntimeStorageInt(_runtimeContext.Storage, AIrConRenderer.SettingsSection, AIrConRenderer.SettingZappingIntervalSeconds, AIrConRenderer.DefaultZappingIntervalSeconds);
                wave = ReadRuntimeStorageString(_runtimeContext.Storage, AIrConRenderer.SettingsSection, AIrConRenderer.SettingStartupWave, AIrConRenderer.DefaultStartupWave);
            }

            interval = AIrConRenderer.ValidateZappingInterval(interval);
            return new AirConSettings(remember, interval, AIrConRenderer.NormalizeHostWave(wave));
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
                SetRuntimeStorage(context.Storage, AIrConRenderer.SettingsSection, AIrConRenderer.SettingZappingIntervalSeconds, AIrConRenderer.ValidateZappingInterval(zappingIntervalSeconds));
                SetRuntimeStorage(context.Storage, AIrConRenderer.SettingsSection, AIrConRenderer.SettingStartupWave, AIrConRenderer.NormalizeHostWave(startupWave));
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
                ? new OperationResult(true, string.IsNullOrWhiteSpace(diagnostics) ? "statepatch_applied" : diagnostics,
                    "OK", OperationCompleted: true, ContinuationRecommended: true,
                    PatchAppliedCount: details?.AppliedPatchCount ?? -1, PatchOutcome: details?.Outcome ?? string.Empty)
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
            #if AIRCON_DEVELOPER_DIAGNOSTICS
            LogProjection("SERVICE_PROJECTION", "MISS", _serviceProjection.Length);
            #endif
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
            #if AIRCON_DEVELOPER_DIAGNOSTICS
            LogProjection("WAVE_FILTER_PROJECTION", "MISS", _waveFilterProjection.Length);
            #endif
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
                #if AIRCON_DEVELOPER_DIAGNOSTICS
                LogProgramProjection("MISS", _programProjection.Length, queryFrom, queryTo, _programProjectionExpiresAt);
                #endif
            }
            else
            {
                #if AIRCON_DEVELOPER_DIAGNOSTICS
                LogProgramProjection("HIT", _programProjection!.Length, _programProjectionFrom, _programProjectionTo, _programProjectionExpiresAt);
                #endif
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
            #if AIRCON_DEVELOPER_DIAGNOSTICS
            LogProjection("VIEWER_PROFILE_PROJECTION", "MISS", _viewerProfileProjection.Length);
            #endif
            return _viewerProfileProjection;
        }
    }

    internal static IReadOnlyList<ViewerSessionDto> ListSessions()
    {
        lock (Sync)
        {
            if (_viewerSessionProjection != null) return _viewerSessionProjection;
            _viewerSessionProjection = _runtimeContext?.Viewers.ListSessions().ToArray() ?? Array.Empty<ViewerSessionDto>();
            #if AIRCON_DEVELOPER_DIAGNOSTICS
            LogProjection("VIEWER_SESSION_PROJECTION", "MISS", _viewerSessionProjection.Length);
            #endif
            return _viewerSessionProjection;
        }
    }
#if AIRCON_DEVELOPER_DIAGNOSTICS

    [System.Diagnostics.Conditional("AIRCON_DEVELOPER_DIAGNOSTICS")]
    private static void LogProgramProjection(string result, int count, DateTimeOffset from, DateTimeOffset to, DateTimeOffset expiresAt)
    {
#if AIRCON_DEVELOPER_DIAGNOSTICS
        var context = _runtimeContext;
        if (context == null) return;
        context.Logs.Write(new TvAirLogWriteDto
        {
            Level = "Info",
            Category = "AIrCon",
            Message = $"PROGRAM_NOW_NEXT_PROJECTION result={result} count={count} cacheFrom={from:O} cacheTo={to:O} expiresAt={expiresAt:O} owner=plugin_instance invalidation=event_boundary_or_manual_refresh rule=aircon_projection_single_source"
        });
#endif
    }
#endif
#if AIRCON_DEVELOPER_DIAGNOSTICS

    [System.Diagnostics.Conditional("AIRCON_DEVELOPER_DIAGNOSTICS")]
    private static void LogProjection(string name, string result, int count)
    {
#if AIRCON_DEVELOPER_DIAGNOSTICS
        var context = _runtimeContext;
        if (context == null) return;
        context.Logs.Write(new TvAirLogWriteDto
        {
            Level = "Info",
            Category = "AIrCon",
            Message = $"{name} result={result} count={count} owner=plugin_instance invalidation=event_or_manual_refresh rule=aircon_projection_single_source"
        });
#endif
    }
#endif

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

    internal static ViewerSessionDto? GetSession(string viewerSessionId)
    {
        lock (Sync)
        {
            var context = _runtimeContext;
            if (context == null || string.IsNullOrWhiteSpace(viewerSessionId)) return null;
            return context.Viewers.GetSession(viewerSessionId);
        }
    }

    internal static IReadOnlyList<TvAirViewerReservation> ListViewerReservations()
    {
        lock (Sync)
        {
            var context = RequireRuntimeContext();
            return context.ViewerReservations.List(new TvAirViewerReservationQuery { IncludeTerminal = false }).ToArray();
        }
    }

    internal static async Task<OperationResult> CreateViewerReservationAsync(string viewerProfileId, ushort networkId, ushort transportStreamId, ushort serviceId, ushort eventId, DateTimeOffset scheduledStart, DateTimeOffset scheduledEnd)
    {
        try
        {
            var context = RequireRuntimeContext();
            var result = await context.ViewerReservations.CreateAsync(new TvAirViewerReservationCreateRequest
            {
                ViewerProfileId = viewerProfileId,
                Service = new TvAirServiceIdentityDto
                {
                    NetworkId = networkId,
                    TransportStreamId = transportStreamId,
                    ServiceId = serviceId
                },
                EventId = eventId,
                ScheduledStart = scheduledStart,
                ScheduledEnd = scheduledEnd
            }).ConfigureAwait(false);
            return result.Succeeded && result.Value != null
                ? OperationResult.Ok("viewer_reservation_created")
                : OperationResult.Fail(result.Error?.Code.ToString() ?? "viewer_reservation_create_failed", result.Error?.Message ?? "視聴予約を登録できません。");
        }
        catch (Exception ex) { return OperationResult.Fail(ex.GetType().Name, ex.Message); }
    }

    internal static async Task<OperationResult> CancelViewerReservationAsync(string reservationId)
    {
        try
        {
            var context = RequireRuntimeContext();
            var result = await context.ViewerReservations.CancelAsync(reservationId).ConfigureAwait(false);
            return result.Succeeded
                ? OperationResult.Ok("viewer_reservation_cancelled")
                : OperationResult.Fail(result.Error?.Code.ToString() ?? "viewer_reservation_cancel_failed", result.Error?.Message ?? "視聴予約を解除できません。");
        }
        catch (Exception ex) { return OperationResult.Fail(ex.GetType().Name, ex.Message); }
    }

    internal static async Task<OperationResult> TuneAsync(string viewerProfileId, string wave, int networkId, int transportStreamId, int serviceId, string? requiredSessionId = null, long? requiredGeneration = null, int? requiredProcessId = null, string? viewerActivation = null)
    {
        try
        {
            var context = RequireRuntimeContext();
            var normalizedWave = AIrConRenderer.NormalizeHostWave(wave);
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

    internal static async Task<OperationResult> ActivateAsync(string viewerSessionId, long expectedGeneration)
    {
        try
        {
            var context = RequireRuntimeContext();
            if (string.IsNullOrWhiteSpace(viewerSessionId) || expectedGeneration <= 0)
                return OperationResult.Fail("viewer_session_invalid", "視聴先を確認できませんでした。");
            var result = await context.Viewers.ActivateAsync(new TvAirViewerActivateRequest
            {
                ViewerSessionId = viewerSessionId,
                ExpectedGeneration = expectedGeneration
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
                    x.ViewerSessionId.Equals(viewerSessionId, StringComparison.Ordinal) &&
                    x.Generation == expectedGeneration &&
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
            var normalized = AIrConRenderer.NormalizeHostWave(group);
            return wave switch
            {
                "GR" => normalized == "GR",
                "CATV" => normalized == "CATV",
                _ => normalized is "BS" or "CS" || group.Contains("BSCS", StringComparison.OrdinalIgnoreCase)
            };
        });
        if (!accepted) throw new InvalidOperationException("選択した視聴先は現在の放送波に対応していません。");
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
        bool ContinuationRecommended = false,
        int PatchAppliedCount = -1,
        string PatchOutcome = "")
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

