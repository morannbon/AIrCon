using System.Text.Json;
using System.Net;
using TvAIrPlugin;
using TvAIrPlugin.Runtime;
using TvAIrPlugin.Viewers;

namespace AIrCon.BasicPlugin;

internal sealed partial class AIrConRenderer
{
    private async Task<AIrConNewApiBridge.OperationResult> SwitchViewerServiceAsync(string viewerProfile, string wave, int networkId, int transportStreamId, int serviceId, string source, string viewerActivation)
    {
        if (_runtimeUiStopped)
            return AIrConNewApiBridge.OperationResult.Fail("runtime_stopped", "AIrCon is stopped.");
        var lifecycleVersion = CaptureRuntimeUiLifecycle();
        using var gate = GetProfileOperationGate(viewerProfile);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsCurrentRuntimeUiLifecycle(lifecycleVersion))
                return AIrConNewApiBridge.OperationResult.Fail("runtime_stopped", "AIrCon is stopped.");
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
            #if AIRCON_DEVELOPER_DIAGNOSTICS
            SafeLog($"VIEWER_SWITCH source={source} phase=tune profileId={viewerProfile} result=FAILED diagnostics={start.Diagnostics}");
            #endif
            return start;
        }

        if (continuationGuard != null && !continuationGuard())
        {
            return AIrConNewApiBridge.OperationResult.Fail("superseded_after_tune", "Viewer switch was superseded after tuning.");
        }

        if (start.NetworkId != networkId || start.TransportStreamId != transportStreamId || start.ServiceId != serviceId)
        {
            #if AIRCON_DEVELOPER_DIAGNOSTICS
            SafeLog($"VIEWER_SWITCH source={source} phase=operation_result profileId={viewerProfile} wave={wave} nid={networkId} tsid={transportStreamId} sid={serviceId} result=FAILED diagnostics=viewer_operation_identity_mismatch");
            #endif
            return AIrConNewApiBridge.OperationResult.Fail("viewer_operation_identity_mismatch", "Viewer operation returned a different service identity.");
        }

        AIrConNewApiBridge.InvalidateViewerProjection();
        #if AIRCON_DEVELOPER_DIAGNOSTICS
        SafeLog($"VIEWER_SWITCH source={source} profileId={viewerProfile} oldSession={before?.ViewerSessionId ?? "-"} oldPid={before?.ProcessId?.ToString() ?? "-"} newSession={start.ViewerSessionId} newPid={start.ProcessId?.ToString() ?? "-"} generation={start.Generation} wave={wave} nid={networkId} tsid={transportStreamId} sid={serviceId} result=OK route=runtime_viewer_operation_result projection=viewer_invalidated");
        #endif
        return start;
    }

    private async Task<HostActionDispatchResult> DispatchViewerStopForProfileAsync(string viewerProfile, string origin)
    {
        var result = await AIrConNewApiBridge.StopAsync(viewerProfile).ConfigureAwait(false);
        if (result.Success) AIrConNewApiBridge.InvalidateViewerProjection();
        #if AIRCON_DEVELOPER_DIAGNOSTICS
        SafeLog($"VIEWER_OPERATION action=Stop profileId={viewerProfile} origin=AIrCon reason={origin} result={(result.Success ? "OK" : "FAILED")} diagnostics={result.Diagnostics}");
        #endif
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
            return CaptureViewerSessionsNewApi()
                .Where(x => x.ViewerProfile.Equals(viewerProfile, StringComparison.OrdinalIgnoreCase))
                .Where(x => x.IsActive && !string.IsNullOrWhiteSpace(x.ViewerSessionId))
                .OrderByDescending(x => x.Current)
                .FirstOrDefault();
        }
#if AIRCON_DEVELOPER_DIAGNOSTICS
        catch (Exception ex)
#else
        catch (Exception)
#endif
        {
            #if AIRCON_DEVELOPER_DIAGNOSTICS
            SafeLog($"VIEWER_SESSION_LOOKUP failed viewerProfile={viewerProfile} exception={ex.GetType().Name} message={ex.Message}");
            #endif
            return null;
        }
    }

    // Gate storage is per viewer profile. The lease keeps a semaphore alive across
    // an awaited Host call even if OnStop retires the corresponding runtime state.
    private sealed class ProfileOperationGate
    {
        internal readonly SemaphoreSlim Semaphore = new(1, 1);
        internal int LeaseCount; // guarded by _operationLock
        internal bool Retired;   // guarded by _operationLock
    }

    private sealed class ProfileOperationLease : IDisposable
    {
        private readonly AIrConRenderer _owner;
        private ProfileOperationGate? _gate;

        internal ProfileOperationLease(AIrConRenderer owner, ProfileOperationGate gate)
        {
            _owner = owner;
            _gate = gate;
        }

        internal Task WaitAsync() => _gate!.Semaphore.WaitAsync();
        internal void Release() => _gate!.Semaphore.Release();

        public void Dispose()
        {
            var gate = Interlocked.Exchange(ref _gate, null);
            if (gate == null) return;
            lock (_owner._operationLock)
            {
                gate.LeaseCount--;
                if (gate.Retired && gate.LeaseCount == 0)
                    gate.Semaphore.Dispose();
            }
        }
    }

    private ProfileOperationLease GetProfileOperationGate(string viewerProfile)
    {
        var key = BuildZappingKey(viewerProfile);
        lock (_operationLock)
        {
            if (!_profileOperationGates.TryGetValue(key, out var gate))
            {
                gate = new ProfileOperationGate();
                _profileOperationGates[key] = gate;
            }
            gate.LeaseCount++;
            return new ProfileOperationLease(this, gate);
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

    private static bool IsExternalProgramGuideFollow(TvAirViewerOperationPreempting operation)
        => operation.SourceKind.Equals("ProgramGuideLiveView", StringComparison.OrdinalIgnoreCase)
            && !operation.SourceOwnerId.Equals(PluginId, StringComparison.OrdinalIgnoreCase);

    internal void ApplyViewerOperationPreempting(TvAirViewerOperationPreempting operation)
    {
        if (operation == null || string.IsNullOrWhiteSpace(operation.ViewerProfileId)) return;
        // Host reports AIrCon's own TuneAsync with the same SourceKind as a
        // ProgramGuide live-view. SourceOwnerId is the required distinction:
        // only an external ProgramGuide request may redirect the browsed wave.
        lock (_zappingLock)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var stale in _programGuideFollowUntil.Where(x => x.Value <= now).Select(x => x.Key).ToArray())
                _programGuideFollowUntil.Remove(stale);
            // A newer operation supersedes the prior follow intent for this profile.
            _programGuideFollowUntil.Remove(operation.ViewerProfileId);
            if (IsExternalProgramGuideFollow(operation))
                _programGuideFollowUntil[operation.ViewerProfileId] = now.AddSeconds(20);
        }
        if (string.IsNullOrWhiteSpace(operation.ViewerReservationId)) return;
        ClearAutomaticViewerState(operation.ViewerProfileId);
#if AIRCON_DEVELOPER_DIAGNOSTICS
        SafeLog($"VIEWER_OPERATION_PREEMPT viewerProfile={operation.ViewerProfileId} sourceKind={operation.SourceKind} reservation={operation.ViewerReservationId} action=clear_automatic_viewer_state");
#endif
    }

    private void ClearAutomaticViewerState(string viewerProfile)
    {
        // One profile-scoped boundary for every Host-owned explicit Viewer Operation.
        // Reservation code does not know individual automation features.
        NextOperationGeneration(viewerProfile);
        RemoveZappingState(viewerProfile);
        SetPowerOffStopped(viewerProfile);
    }

    internal void ApplyViewerReservationChanged(TvAIrPlugin.Events.PluginEventEnvelope eventEnvelope)
    {
        var reservation = TryReadViewerReservation(eventEnvelope.Payload);
        if (reservation == null || string.IsNullOrWhiteSpace(reservation.ViewerProfileId)) return;

        var completed = eventEnvelope.ChangeKind?.Equals("Completed", StringComparison.OrdinalIgnoreCase) == true
            || reservation.State.Equals("Completed", StringComparison.OrdinalIgnoreCase);
        if (!completed) return;

        var wave = ResolveServiceWave(new TvAirServiceIdentityDto
        {
            NetworkId = (ushort)reservation.NetworkId,
            TransportStreamId = (ushort)reservation.TransportStreamId,
            ServiceId = (ushort)reservation.ServiceId
        });
        if (string.IsNullOrWhiteSpace(wave)) return;

        foreach (var windowId in SnapshotWindowDisplays().Select(pair => pair.Key))
        {
            var refresh = RequestWindowDisplayNavigation(windowId, wave, reservation.ViewerProfileId);
#if AIRCON_DEVELOPER_DIAGNOSTICS
            SafeLog($"VIEWER_RESERVATION_DISPLAY result={(refresh.Success ? "OK" : "FAILED")} reservation={reservation.ReservationId} viewerProfile={reservation.ViewerProfileId} wave={wave} windowId={windowId} diagnostics={refresh.Diagnostics}");
#endif
        }
    }

    private static TvAirViewerReservationDto? TryReadViewerReservation(object? payload)
    {
        if (payload is TvAirEventDto eventDto) return eventDto.ViewerReservation;
        if (payload is TvAirViewerReservationDto reservation) return reservation;
        if (payload is TvAirViewerReservation runtimeReservation)
        {
            return new TvAirViewerReservationDto
            {
                ReservationId = runtimeReservation.ReservationId,
                ViewerProfileId = runtimeReservation.ViewerProfileId,
                NetworkId = runtimeReservation.Service.NetworkId,
                TransportStreamId = runtimeReservation.Service.TransportStreamId,
                ServiceId = runtimeReservation.Service.ServiceId,
                EventId = runtimeReservation.EventId,
                ScheduledStart = runtimeReservation.ScheduledStart,
                ScheduledEnd = runtimeReservation.ScheduledEnd,
                State = runtimeReservation.State,
                FailureReason = runtimeReservation.FailureReason,
                CreatedAt = runtimeReservation.CreatedAt,
                UpdatedAt = runtimeReservation.UpdatedAt
            };
        }
        if (payload is JsonElement json)
        {
            try
            {
                if (json.ValueKind == JsonValueKind.Object &&
                    (json.TryGetProperty("viewerReservation", out var nested) || json.TryGetProperty("ViewerReservation", out nested)))
                    return nested.Deserialize<TvAirViewerReservationDto>();
                return json.Deserialize<TvAirViewerReservationDto>();
            }
            catch (JsonException) { return null; }
        }
        return null;
    }

    internal void ApplyViewerSessionStatePatch(TvAIrPlugin.Events.PluginEventEnvelope eventEnvelope)
    {
        if (eventEnvelope.Sequence <= 0)
        {
            #if AIRCON_DEVELOPER_DIAGNOSTICS
            SafeLog($"VIEWER_SESSION_STATEPATCH result=SKIPPED reason=invalid_revision revision={eventEnvelope.Sequence}");
            #endif
            return;
        }

        var viewerSessionId = ResolveViewerSessionId(eventEnvelope.EntityId);
        if (string.IsNullOrWhiteSpace(viewerSessionId))
        {
            #if AIRCON_DEVELOPER_DIAGNOSTICS
            SafeLog($"VIEWER_SESSION_STATEPATCH result=SKIPPED reason=event_session_missing entityId={eventEnvelope.EntityId ?? "-"} revision={eventEnvelope.Sequence}");
            #endif
            return;
        }

        var eventSession = AIrConNewApiBridge.GetSession(viewerSessionId);
        if (eventSession == null || string.IsNullOrWhiteSpace(eventSession.ViewerProfileId))
        {
            #if AIRCON_DEVELOPER_DIAGNOSTICS
            SafeLog($"VIEWER_SESSION_STATEPATCH result=SKIPPED reason=event_session_unresolved viewerSession={viewerSessionId} revision={eventEnvelope.Sequence}");
            #endif
            return;
        }

        var viewerProfile = eventSession.ViewerProfileId.Trim();
        var active = IsLiveViewerSession(eventSession);
        if (active)
        {
            // Every live session change uses the current Host service identity,
            // including retunes of an existing TVTest.
            var service = eventSession.CurrentService;
            if (service is not { NetworkId: > 0, TransportStreamId: > 0, ServiceId: > 0 }) return;
            var currentKey = ServiceKey(service.NetworkId, service.TransportStreamId, service.ServiceId);
            var currentWave = ResolveServiceWave(service);
            // TvAIr's ProgramGuide operation is a user selection, not passive zapping.
            // Consume its bounded intent on the authoritative Viewer session change.
            bool followProgramGuide;
            lock (_zappingLock)
            {
                followProgramGuide = _programGuideFollowUntil.Remove(viewerProfile, out var deadline)
                    && deadline > DateTimeOffset.UtcNow;
            }
            // ViewerSessionChanged owns viewer presentation: first launches refresh
            // controls once, while existing retunes update only affected rows.
            var windows = SnapshotWindowDisplays()
                .Where(pair => followProgramGuide || pair.Value.ViewerProfile.Equals(viewerProfile, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (followProgramGuide && windows.Length == 0)
            {
                // A ProgramGuide launch can create the ToolWindow after the Viewer
                // session event. Its initial render must use that tuned wave/profile.
                lock (_zappingLock)
                    _pendingProgramGuideInitialSelection = (currentWave, viewerProfile, DateTimeOffset.UtcNow.AddSeconds(20));
            }
            foreach (var pair in windows)
            {
                var windowId = pair.Key;
                if (string.IsNullOrWhiteSpace(windowId)) continue;
                var renderedKey = pair.Value.ServiceKey;
                // A Host navigation already owns the next page. Do not patch the
                // outgoing DOM or mutate its snapshot while that request is pending;
                // RenderHtml will project the latest Viewer session when it arrives.
                if (pair.Value.RequestedWave != null)
                {
#if AIRCON_DEVELOPER_DIAGNOSTICS
                    SafeLog($"VIEWER_SESSION_SELECTION result=WAITING_FOR_RENDER windowId={windowId} viewerProfile={viewerProfile} service={currentKey} revision={eventEnvelope.Sequence}");
#endif
                    continue;
                }
                // The first live session changes toolbar actions as well as row color.
                // In that one case the authoritative event requests a single full
                // render. Never infer first launch in the click response from a
                // generation number: re-selecting generation 1 is a no-op, not a launch.
                // No separate suppressor or action-owned redirect is needed.
                if (eventSession.Generation == 1 && renderedKey.Length == 0
                    && pair.Value.ViewerProfile.Equals(viewerProfile, StringComparison.OrdinalIgnoreCase)
                    && pair.Value.Wave.Equals(currentWave, StringComparison.OrdinalIgnoreCase))
                {
                    var first = RequestWindowDisplayNavigation(windowId, currentWave, viewerProfile);
#if AIRCON_DEVELOPER_DIAGNOSTICS
                    SafeLog($"VIEWER_SESSION_SELECTION result={(first.Success ? "FIRST_RENDER_REQUESTED" : "FAILED")} windowId={windowId} viewerProfile={viewerProfile} service={currentKey} revision={eventEnvelope.Sequence} diagnostics={first.Diagnostics}");
#endif
                    if (!first.Success && IsDefinitiveWindowGone(first.Diagnostics))
                        StopWindowBackgroundExecution(windowId);
                    continue;
                }
                if (renderedKey.Equals(currentKey, StringComparison.OrdinalIgnoreCase)
                    && pair.Value.ViewerProfile.Equals(viewerProfile, StringComparison.OrdinalIgnoreCase))
                {
                    // A repeated explicit ProgramGuide selection must recenter even if
                    // the same viewer was already tuned and no row-class patch is needed.
                    if (followProgramGuide)
                        ScheduleCurrentServiceCenter(windowId, 100, currentKey);
                    continue;
                }

                // Passive retunes preserve an unrelated manually browsed wave. An
                // explicit ProgramGuide selection instead follows its wave and profile.
                var displayedFilter = pair.Value.Wave;
                var navigationRequired = false;
                if (!displayedFilter.Equals(currentWave, StringComparison.OrdinalIgnoreCase)
                    || !pair.Value.ViewerProfile.Equals(viewerProfile, StringComparison.OrdinalIgnoreCase))
                {
                    if (!followProgramGuide)
                    {
                        var priorService = AIrConNewApiBridge.ListServices().FirstOrDefault(row =>
                            ServiceKey(row.NetworkId, row.TransportStreamId, row.ServiceId)
                                .Equals(renderedKey, StringComparison.OrdinalIgnoreCase));
                        if (priorService == null ||
                            !NormalizeWaveFilter(priorService.BroadcastType).Equals(displayedFilter, StringComparison.OrdinalIgnoreCase))
                        {
                            #if AIRCON_DEVELOPER_DIAGNOSTICS
                            SafeLog($"VIEWER_SESSION_STATEPATCH result=SKIPPED reason=browsing_other_wave windowId={windowId} viewerProfile={viewerProfile} displayedWave={displayedFilter} currentWave={currentWave} revision={eventEnvelope.Sequence}");
                            #endif
                            continue;
                        }
                    }
                    navigationRequired = true;
                }

                // A same-wave retune changes only the selected row. Patch stable DOM
                // identities instead of requesting a navigation that may be acknowledged
                // without ever producing new HTML (especially during rapid selection).
                // Cross-wave changes still require the canonical filtered-list render.
                if (!navigationRequired)
                {
                    var patches = new List<RuntimeUiPatch>();
                    // The event session is authoritative for the newly tuned selected viewer;
                    // the live snapshot supplies *other* viewers for both affected rows.
                    var liveSessions = AIrConNewApiBridge.ListSessions();
                    var oldRowId = BuildServiceDomIdFromKey(renderedKey);
                    var newRowId = BuildServiceDomId(service.NetworkId, service.TransportStreamId, service.ServiceId);
                    // A cross-wave retune into an already-browsed wave may have no old
                    // service row in this filtered list. Do not count a nonexistent row
                    // as an expected application, and preserve any other active Viewer.
                    var oldService = AIrConNewApiBridge.ListServices().FirstOrDefault(row =>
                        ServiceKey(row.NetworkId, row.TransportStreamId, row.ServiceId)
                            .Equals(renderedKey, StringComparison.OrdinalIgnoreCase));
                    if (oldRowId.Length > 0 && oldService != null
                        && NormalizeWaveFilter(oldService.BroadcastType).Equals(displayedFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        var oldWatchedByOther = liveSessions.Any(session =>
                            !session.ViewerProfileId.Equals(viewerProfile, StringComparison.OrdinalIgnoreCase)
                            && IsServiceWatchedBy(session, renderedKey));
                        patches.Add(BuildServiceEmphasisPatch(oldRowId,
                            ResolveServiceEmphasis(false, oldWatchedByOther, false)));
                    }
                    var isZapping = GetActiveZappingState(viewerProfile) is { Active: true } state
                        && state.Wave.Equals(displayedFilter, StringComparison.OrdinalIgnoreCase);
                    var newWatchedByOther = liveSessions.Any(session =>
                        !session.ViewerProfileId.Equals(viewerProfile, StringComparison.OrdinalIgnoreCase)
                        && IsServiceWatchedBy(session, currentKey));
                    patches.Add(BuildServiceEmphasisPatch(newRowId,
                        ResolveServiceEmphasis(true, newWatchedByOther, isZapping)));
                    var patched = AIrConNewApiBridge.PatchToolWindow(windowId, patches, eventEnvelope.Sequence);
                    // Only the exact expected number of applied rows confirms a real DOM
                    // transition; do not advance the displayed key on request acceptance.
                    var applied = patched.Success
                        && patched.PatchOutcome.Equals("APPLIED", StringComparison.OrdinalIgnoreCase)
                        && patched.PatchAppliedCount == patches.Count;
                    if (applied)
                    {
                        SetRenderedService(windowId, currentKey);
                        // The same service key drives the color patch and the list's actual
                        // scrollTop. Never trigger a second page navigation to center it.
                        ScheduleCurrentServiceCenter(windowId, 100, currentKey);
                    }
                    #if AIRCON_DEVELOPER_DIAGNOSTICS
                    SafeLog($"VIEWER_SESSION_SELECTION result={(applied ? "APPLIED" : "UNCONFIRMED")} windowId={windowId} viewerProfile={viewerProfile} service={currentKey} revision={eventEnvelope.Sequence} diagnostics={patched.Diagnostics}");
                    #endif
                    if (!applied)
                    {
                        if (!patched.Success && IsDefinitiveWindowGone(patched.Diagnostics))
                            StopWindowBackgroundExecution(windowId);
                        else
                        {
                            // StatePatch can refuse a superseded sequence. A recovery
                            // must never reload an older selected channel over a newer one.
                            var latest = ResolveCurrentStation(windowId);
                            if (latest != null && latest.ServiceKey.Equals(currentKey, StringComparison.OrdinalIgnoreCase)
                                && latest.ViewerProfile.Equals(viewerProfile, StringComparison.OrdinalIgnoreCase))
                            {
                                var recovered = RequestWindowDisplayNavigation(windowId, latest.Wave, latest.ViewerProfile);
#if AIRCON_DEVELOPER_DIAGNOSTICS
                                SafeLog($"VIEWER_SESSION_SELECTION_RECOVERY result={(recovered.Success ? "REQUESTED" : "FAILED")} windowId={windowId} service={latest.ServiceKey} diagnostics={recovered.Diagnostics}");
#endif
                            }
                        }
                    }
                }
                else
                {
                    // Cross-wave navigation is completed by RenderHtml. The shared
                    // navigation owner blocks the outgoing DOM until the matching
                    // render arrives, independent of the operation's source.
                    var refreshed = RequestWindowDisplayNavigation(windowId, currentWave, viewerProfile);
                    #if AIRCON_DEVELOPER_DIAGNOSTICS
                    SafeLog($"VIEWER_SESSION_SELECTION result={(refreshed.Success ? "NAVIGATION_REQUESTED" : "FAILED")} windowId={windowId} viewerProfile={viewerProfile} service={currentKey} revision={eventEnvelope.Sequence} diagnostics={refreshed.Diagnostics}");
                    #endif
                    if (!refreshed.Success && IsDefinitiveWindowGone(refreshed.Diagnostics))
                        StopWindowBackgroundExecution(windowId);
                }
            }
            return;
        }

        // A terminal event from an obsolete Viewer Session must never erase a
        // replacement session's active row or its running controls.
        if (AIrConNewApiBridge.ListSessions().Any(session =>
            session.ViewerProfileId.Equals(viewerProfile, StringComparison.OrdinalIgnoreCase)
            && !session.ViewerSessionId.Equals(viewerSessionId, StringComparison.Ordinal)
            && IsLiveViewerSession(session)))
            return;

        // Runtime Viewer Session is the single lifecycle authority for AIrCon-managed TVTest.
        // Whether the process ended through AIrCon power-off, TVTest's X button, or an external
        // process termination, the same terminal transition clears Plugin-owned background state.
        RemoveZappingState(viewerProfile);
        SetPowerOffStopped(viewerProfile);

        var targetWindows = SnapshotWindowDisplays()
            .Where(pair => pair.Value.ViewerProfile.Equals(viewerProfile, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (targetWindows.Length == 0)
        {
            #if AIRCON_DEVELOPER_DIAGNOSTICS
            SafeLog($"VIEWER_SESSION_STATEPATCH result=SKIPPED reason=event_profile_not_displayed viewerSession={viewerSessionId} viewerProfile={viewerProfile} active=False backgroundStateCleared=True revision={eventEnvelope.Sequence}");
            #endif
            return;
        }

#if AIRCON_DEVELOPER_DIAGNOSTICS
        var terminalWave = ResolveServiceWave(eventSession.CurrentService);
#endif

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

            var displayedWave = pair.Value.Wave;

            // Clear the displayed station's selected state in the same transaction
            // that disables the Viewer controls. CurrentService can already be null
            // by the time Host publishes the terminal event.
            // The displayed selection, not the now-missing terminal service, identifies
            // the row that must lose its yellow/red highlight.
            if (!string.IsNullOrWhiteSpace(displayedWave))
            {
                var selectedRowId = GetRenderedServiceRowId(windowId, NormalizeWaveFilter(displayedWave));
                if (selectedRowId.Length > 0)
                {
                    var serviceKey = GetRenderedServiceKey(windowId);
                    var otherWatched = AIrConNewApiBridge.ListSessions().Any(session =>
                        !session.ViewerProfileId.Equals(viewerProfile, StringComparison.OrdinalIgnoreCase)
                        && IsServiceWatchedBy(session, serviceKey));
                    patches.Add(BuildServiceEmphasisPatch(selectedRowId,
                        ResolveServiceEmphasis(false, otherWatched, false)));
                }
            }

            var result = AIrConNewApiBridge.PatchToolWindow(windowId, patches, eventEnvelope.Sequence);
            // Terminal events invalidate the displayed *selected* service. Without this,
            // restarting the same station would be incorrectly suppressed as unchanged.
            SetRenderedService(windowId, string.Empty);
            #if AIRCON_DEVELOPER_DIAGNOSTICS
            SafeLog($"VIEWER_SESSION_STATEPATCH result={(result.Success ? "OK" : "FAILED")} windowId={windowId} viewerSession={viewerSessionId} viewerProfile={viewerProfile} active=False lifecycleAuthority=runtime_viewer_session terminalWave={terminalWave} displayedWave={displayedWave} backgroundStateCleared=True revision={eventEnvelope.Sequence} diagnostics={result.Diagnostics}");
            #endif
            if (result.Success && result.Diagnostics.Contains("reason=window_closed", StringComparison.OrdinalIgnoreCase))
                StopWindowBackgroundExecution(windowId);
        }
    }

}
