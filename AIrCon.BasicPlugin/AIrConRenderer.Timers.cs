using System.Text.Json;
using System.Net;
using TvAIrPlugin;
using TvAIrPlugin.Runtime;
using TvAIrPlugin.Viewers;
using ThreadingTimer = System.Threading.Timer;

namespace AIrCon.BasicPlugin;

internal sealed partial class AIrConRenderer
{
    private static int GetZappingIntervalSeconds() => AIrConNewApiBridge.LoadSettings().ZappingIntervalSeconds;

    private static bool ParseBool(string value, bool fallback)
    {
        return bool.TryParse(value, out var parsed) ? parsed : value == "1" ? true : value == "0" ? false : fallback;
    }

    private static int NormalizeZappingInterval(string value)
    {
        return ValidateZappingInterval(int.TryParse(value, out var parsed) ? parsed : DefaultZappingIntervalSeconds);
    }

#if AIRCON_DEVELOPER_DIAGNOSTICS
    private bool IsWindowDisplaying(string windowId, string wave, string viewerProfile)
    {
        var displayed = GetWindowDisplay(windowId);
        return displayed != null && displayed.RequestedWave == null
            && displayed.Wave.Equals(NormalizeWaveFilter(wave), StringComparison.OrdinalIgnoreCase)
            && displayed.ViewerProfile.Equals(viewerProfile, StringComparison.OrdinalIgnoreCase);
    }
#endif

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
            var liveSession = AIrConNewApiBridge.ListSessions().FirstOrDefault(x =>
                IsLiveViewerSession(x)
                && x.ViewerProfileId.Equals(viewerProfile, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(x.ViewerSessionId)
                && x.Generation > 0
                && x.ProcessId is > 0);
            if (liveSession is null)
                throw new InvalidOperationException("ザッピング対象の視聴状態を確認できません。");
            var processId = liveSession.ProcessId.GetValueOrDefault();
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

            var created = new ZappingState(true, now, DateTimeOffset.MinValue, now.AddSeconds(intervalSeconds), currentServiceKey, windowId ?? string.Empty, NormalizeWaveFilter(wave), viewerProfile, generation, liveSession.ViewerSessionId, liveSession.Generation, processId, intervalSeconds);
            _zappingStates[key] = created;
            ScheduleNextZappingTickLocked(key, created);
            return created;
        }
    }

    // Both the Stop button and a manual station choice use this one cancellation
    // boundary. The zapping state and its operation generation change under the
    // same lock as EnsureZappingActive, independent of the displayed wave.
    private bool CancelActiveZapping(string viewerProfile, bool supersedePendingOperation = false)
    {
        var key = BuildZappingKey(viewerProfile);
        lock (_zappingLock)
        {
            var wasActive = _zappingStates.TryGetValue(key, out var state) && state.Active;
            if (!wasActive && !supersedePendingOperation)
                return false;
            NextOperationGeneration(viewerProfile);
            RemoveZappingState(viewerProfile);
            return wasActive;
        }
    }

    private async Task StopZappingAsync(string viewerProfile)
    {
        if (_runtimeUiStopped) return;
        CancelActiveZapping(viewerProfile);

        // Drain any dispatch already in flight. The gate lease survives runtime
        // retirement, and a queued old tick still fails its generation check.
        using var gate = GetProfileOperationGate(viewerProfile);
        await gate.WaitAsync().ConfigureAwait(false);
        gate.Release();
    }

    // A tick may complete after manual selection revoked its generation. It must
    // not remove a newer zapping cycle started while that old dispatch was draining.
    private bool RemoveZappingState(string viewerProfile, long? expectedGeneration = null)
    {
        var key = BuildZappingKey(viewerProfile);
        lock (_zappingLock)
        {
            if (expectedGeneration.HasValue &&
                (!_zappingStates.TryGetValue(key, out var current) ||
                 current.Generation != expectedGeneration.Value))
                return false;
            var removed = _zappingStates.Remove(key);
            RemoveAndDisposeTimer(_zappingTimers, key);
            return removed;
        }
    }

    private void ScheduleNextZappingTickLocked(string key, ZappingState state)
    {
        var lifecycleVersion = CaptureRuntimeUiLifecycle();
        RemoveAndDisposeTimer(_zappingTimers, key);
        var due = state.NextTickAt - DateTimeOffset.Now;
        if (due < TimeSpan.Zero) due = TimeSpan.Zero;
        _zappingTimers[key] = new ThreadingTimer(
            _ => _ = RunZappingTickAsync(state.ViewerProfile, state.Generation, "server_timer", lifecycleVersion),
            null,
            due,
            Timeout.InfiniteTimeSpan);
    }

    private async Task RunZappingTickAsync(
        string viewerProfile, long expectedGeneration, string source, long? expectedLifecycleVersion = null)
    {
        if (_runtimeUiStopped) return;
        var lifecycleVersion = expectedLifecycleVersion ?? CaptureRuntimeUiLifecycle();
        if (!IsCurrentRuntimeUiLifecycle(lifecycleVersion))
            return;
        using var gate = GetProfileOperationGate(viewerProfile);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsCurrentRuntimeUiLifecycle(lifecycleVersion))
                return;
            var key = BuildZappingKey(viewerProfile);
            ZappingState state;
            lock (_zappingLock)
            {
                if (!_zappingStates.TryGetValue(key, out state!) || !state.Active ||
                    state.Generation != expectedGeneration ||
                    state.Generation != CurrentOperationGeneration(viewerProfile))
                {
                    #if AIRCON_DEVELOPER_DIAGNOSTICS
                    SafeLog($"ZAPPING_TICK skipped reason=inactive_or_superseded viewerProfile={viewerProfile} source={source}");
                    #endif
                    return;
                }
            }

            var wave = state.Wave;
            var windowId = state.WindowId;

            // Lifecycle Shown/Closing/Closed owns window liveness. Do not issue a
            // presentation refresh merely to probe the zapping owner's window:
            // ViewerSessionChanged and the successful tick already refresh the view.

            var liveSession = AIrConNewApiBridge.ListSessions().FirstOrDefault(x =>
                x.ViewerSessionId.Equals(state.ViewerSessionId, StringComparison.Ordinal));
            if (liveSession is null ||
                !liveSession.ViewerProfileId.Equals(viewerProfile, StringComparison.Ordinal) ||
                liveSession.ProcessId != state.ProcessId || liveSession.Generation <= 0)
            {
                if (RemoveZappingState(viewerProfile, expectedGeneration))
                {
                    #if AIRCON_DEVELOPER_DIAGNOSTICS
                    SafeLog($"ZAPPING_STATE stopped viewerProfile={viewerProfile} reason=viewer_identity_changed expectedSession={state.ViewerSessionId} expectedPid={state.ProcessId}");
                    #endif
                }
                return;
            }

            // A Host retune on the same Viewer session/PID advances the session generation.
            // Follow its authoritative service so the next tick advances from that channel.
            // Never transfer zapping to another session or process.
            if (liveSession.Generation != state.ViewerGeneration)
            {
                var current = liveSession.CurrentService;
                var currentWave = current is { NetworkId: > 0, TransportStreamId: > 0, ServiceId: > 0 }
                    ? ResolveServiceWave(current)
                    : string.Empty;
                if (!NormalizeWaveFilter(currentWave).Equals(wave, StringComparison.OrdinalIgnoreCase))
                {
                    if (RemoveZappingState(viewerProfile, expectedGeneration))
                    {
                        #if AIRCON_DEVELOPER_DIAGNOSTICS
                        SafeLog($"ZAPPING_STATE stopped viewerProfile={viewerProfile} reason=viewer_wave_changed expectedWave={wave} actualWave={currentWave}");

                        #endif
                    }
                    return;
                }
                var currentKey = ServiceKey(current!.NetworkId, current.TransportStreamId, current.ServiceId);
                lock (_zappingLock)
                {
                    if (!_zappingStates.TryGetValue(key, out var latest) ||
                        !latest.Active || latest.Generation != state.Generation)
                        return;
                    state = latest with { ViewerGeneration = liveSession.Generation, LastServiceKey = currentKey };
                    _zappingStates[key] = state;
                }
                #if AIRCON_DEVELOPER_DIAGNOSTICS
                SafeLog($"ZAPPING_STATE resynced viewerProfile={viewerProfile} session={state.ViewerSessionId} pid={state.ProcessId} generation={state.ViewerGeneration} service={currentKey} source=host_retune_same_identity");
                #endif
            }

            var services = CaptureServiceProjection(wave)
                .Where(HasResolvedTriplet)
                .OrderBy(x => x.ProgramGuideOrder)
                .ToList();
            if (services.Count == 0)
            {
                #if AIRCON_DEVELOPER_DIAGNOSTICS
                SafeLog($"ZAPPING_TICK failed reason=no_services windowId={windowId} wave={wave} viewerProfile={viewerProfile} source={source}");
                #endif
                if (RemoveZappingState(viewerProfile, expectedGeneration))
                {
                    #if AIRCON_DEVELOPER_DIAGNOSTICS
                    SafeLog($"ZAPPING_STATE stopped viewerProfile={viewerProfile} reason=no_services");
                    #endif
                }
                return;
            }

            var lastIndex = -1;
            if (!string.IsNullOrWhiteSpace(state.LastServiceKey))
            {
                lastIndex = services.FindIndex(x => ServiceKey(x.NetworkId, x.TransportStreamId, x.ServiceId).Equals(state.LastServiceKey, StringComparison.OrdinalIgnoreCase));
            }
            if (lastIndex < 0)
            {
                // Service projection is not a Viewer session snapshot. When the
                // last key is unavailable, use the already-validated live session.
                var current = liveSession.CurrentService;
                if (current is { NetworkId: > 0, TransportStreamId: > 0, ServiceId: > 0 })
                    lastIndex = services.FindIndex(x => x.NetworkId == current.NetworkId
                        && x.TransportStreamId == current.TransportStreamId && x.ServiceId == current.ServiceId);
            }

            var nextIndex = (lastIndex + 1) % services.Count;
            var target = services[nextIndex];

            // Stop/shutdown may have superseded this tick while channel data was being captured.
            if (!IsCurrentZappingGeneration(viewerProfile, state.Generation))
            {
                #if AIRCON_DEVELOPER_DIAGNOSTICS
                SafeLog($"ZAPPING_TICK skipped reason=superseded_before_dispatch viewerProfile={viewerProfile} generation={state.Generation} source={source}");
                #endif
                return;
            }

            // The Host session event owns the corresponding UI transition.
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
                    if (RemoveZappingState(viewerProfile, expectedGeneration))
                    {
                        #if AIRCON_DEVELOPER_DIAGNOSTICS
                        SafeLog($"ZAPPING_STATE stopped viewerProfile={viewerProfile} reason=replacement_identity_missing");

                        #endif
                    }
                    return;
                }
                var replacementProcessId = replacement.ProcessId.GetValueOrDefault();
                ScheduleZappingAfterAttempt(viewerProfile, state.Generation, serviceKey, replacement.ViewerSessionId, replacement.Generation, replacementProcessId);
            }
            else
            {
                if (RemoveZappingState(viewerProfile, expectedGeneration))
                {
                    #if AIRCON_DEVELOPER_DIAGNOSTICS
                    SafeLog($"ZAPPING_STATE stopped viewerProfile={viewerProfile} reason=switch_failed diagnostics={result.Diagnostics}");
                    #endif
                }
            }

            #if AIRCON_DEVELOPER_DIAGNOSTICS
            SafeLog($"ZAPPING_TICK result={(mayContinue ? (result.HasWarning ? "WARNING" : "OK") : "FAILED")} source={source} windowId={windowId} wave={wave} viewerProfile={viewerProfile} generation={state.Generation} index={nextIndex + 1}/{services.Count} service={target.ServiceName} nid={target.NetworkId} tsid={target.TransportStreamId} sid={target.ServiceId} authoritativeService={serviceKey} operationCompleted={result.OperationCompleted} hasWarning={result.HasWarning} continuationRecommended={result.ContinuationRecommended} message={result.Message}");
            #endif
#if AIRCON_DEVELOPER_DIAGNOSTICS
            if (mayContinue && !IsWindowDisplaying(windowId, wave, viewerProfile))
                SafeLog($"ZAPPING_BACKGROUND_SYNC result=OK windowId={windowId} wave={wave} viewerProfile={viewerProfile} service={target.ServiceName} action=state_updated_visible_content_unchanged");
#endif
            // No duplicate ToolWindow refresh. The authoritative ViewerSessionChanged
            // handler patches both colors and schedules the scroll to the same row.
            return;
        }
#if AIRCON_DEVELOPER_DIAGNOSTICS
        catch (Exception ex)
#else
        catch (Exception)
#endif
        {
            // A superseded lifecycle cannot remove a new lifecycle's zapping state.
            if (IsCurrentRuntimeUiLifecycle(lifecycleVersion) &&
                RemoveZappingState(viewerProfile, expectedGeneration))
            {
                #if AIRCON_DEVELOPER_DIAGNOSTICS
                SafeLog($"ZAPPING_STATE stopped viewerProfile={viewerProfile} reason=tick_exception");
                #endif
            }
            #if AIRCON_DEVELOPER_DIAGNOSTICS
            SafeLog($"ZAPPING_TICK failed viewerProfile={viewerProfile} source={source} exception={ex.GetType().Name} message={ex.Message}");
            #endif
            return;
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
        var lifecycleVersion = CaptureRuntimeUiLifecycle();
        var key = BuildZappingKey(viewerProfile);
        var deadline = DateTimeOffset.Now.AddHours(hours);
        var generation = Interlocked.Increment(ref _powerOffGeneration);
        lock (_powerOffLock)
        {
            RemoveAndDisposeTimer(_powerOffTimers, key);
            _powerOffStates[key] = new PowerOffState(true, deadline, viewerProfile, windowId ?? string.Empty, generation);
            _powerOffTimers[key] = new ThreadingTimer(
                _ => _ = RunPowerOffTimerAsync(viewerProfile, windowId ?? string.Empty, deadline, generation, lifecycleVersion),
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
            RemoveAndDisposeTimer(_powerOffTimers, key);
            return true;
        }
    }

    private async Task RunPowerOffTimerAsync(
        string viewerProfile, string windowId, DateTimeOffset deadline, long generation, long lifecycleVersion)
    {
        if (!IsCurrentRuntimeUiLifecycle(lifecycleVersion)) return;
        if (!TryClaimPowerOffExpiry(viewerProfile, windowId, deadline, generation))
        {
            #if AIRCON_DEVELOPER_DIAGNOSTICS
            SafeLog($"POWER_OFF_TIMER skipped viewerProfile={viewerProfile} generation={generation} reason=inactive_or_superseded");
            #endif
            return;
        }

        #if AIRCON_DEVELOPER_DIAGNOSTICS
        SafeLog($"POWER_OFF_TIMER expired viewerProfile={viewerProfile} deadline={deadline:O} generation={generation}");
        #endif

        // StopWithWindowはHostの別状態照会で推測しない。実際のRefresh契約だけを
        // liveness evidenceとして使い、EntityNotFound等が返った場合だけtimerを収束させる。
        // これによりlive ToolWindowを誤ってdead判定せず、stale WindowIdでViewerを停止しない。
        if (!string.IsNullOrWhiteSpace(windowId))
        {
            var windowRefresh = AIrConNewApiBridge.RefreshToolWindow(windowId, null);
            #if AIRCON_DEVELOPER_DIAGNOSTICS
            SafeLog($"POWER_OFF_WINDOW_REFRESH result={(windowRefresh.Success ? "OK" : "FAILED")} windowId={windowId} viewerProfile={viewerProfile} diagnostics={windowRefresh.Diagnostics}");
            #endif
            if (!windowRefresh.Success && IsDefinitiveWindowGone(windowRefresh.Diagnostics))
            {
                RemoveZappingState(viewerProfile);
                StopPowerOffForWindow(viewerProfile, windowId);
                #if AIRCON_DEVELOPER_DIAGNOSTICS
                SafeLog($"POWER_OFF_TIMER cancelled viewerProfile={viewerProfile} reason=refresh_target_not_found windowId={windowId} diagnostics={windowRefresh.Diagnostics} backgroundExecution=StopWithWindow");
                #endif
                return;
            }
        }

        // The window check can await Host work; do not stop a viewer in a new
        // runtime lifecycle after that wait has completed.
        if (!IsCurrentRuntimeUiLifecycle(lifecycleVersion)) return;
        #if AIRCON_DEVELOPER_DIAGNOSTICS
        SafeLog($"POWER_OFF_TIMER shutdown_started viewerProfile={viewerProfile}");
        #endif
        HostActionDispatchResult result;
        try
        {
            result = await CompleteShutdownAsync(viewerProfile, "power_off_timer").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result = HostActionDispatchResult.Failure("power_off_timer_exception", ex.Message);
            #if AIRCON_DEVELOPER_DIAGNOSTICS
            SafeLog($"POWER_OFF_TIMER failed viewerProfile={viewerProfile} exception={ex.GetType().Name} message={ex.Message}");
            #endif
        }
        #if AIRCON_DEVELOPER_DIAGNOSTICS
        SafeLog($"POWER_OFF_TIMER {(result.Success ? "completed" : "failed")} viewerProfile={viewerProfile} result={(result.Success ? "OK" : "FAILED")} diagnostics={result.Diagnostics} message={result.Message}");
        #endif

        // The server-side expiry path has no Safe Event response that can rerender AIrCon.
        // CompleteShutdownAsync already clears both zapping and power-off state before stopping
        // the Viewer, so passively refresh the existing ToolWindow after completion to replace
        // the stale "巡回中" / timer display with the stopped state. Do not activate, reveal,
        // restore, reposition, or scroll the window during this passive state synchronization.
        if (IsCurrentRuntimeUiLifecycle(lifecycleVersion) && !string.IsNullOrWhiteSpace(windowId))
        {
            var refresh = AIrConNewApiBridge.RefreshToolWindow(windowId, string.Empty);
            #if AIRCON_DEVELOPER_DIAGNOSTICS
            SafeLog($"POWER_OFF_REFRESH result={(refresh.Success ? "OK" : "FAILED")} windowId={windowId} viewerProfile={viewerProfile} diagnostics={refresh.Diagnostics}");
            #endif
        }
    }

    private void SetPowerOffStopped(string viewerProfile)
    {
        var key = BuildZappingKey(viewerProfile);
        lock (_powerOffLock)
        {
            _powerOffStates.Remove(key);
            RemoveAndDisposeTimer(_powerOffTimers, key);
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
            RemoveAndDisposeTimer(_powerOffTimers, key);
        }
    }

    private void PruneSupersededWindowState(string currentWindowId)
    {
        if (string.IsNullOrWhiteSpace(currentWindowId)) return;

        // ReusePolicy=PerRoute means AIrCon owns at most one live ToolWindow for this route.
        // A different WindowId therefore represents a disposed/superseded Host session.
        // Remove its Plugin-owned semantic/background state instead of retaining one entry per reopen.
        string[] staleWindowIds;
        staleWindowIds = SnapshotWindowDisplays()
            .Select(pair => pair.Key)
            .Where(x => !x.Equals(currentWindowId, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var staleWindowId in staleWindowIds)
        {
            StopWindowBackgroundExecution(staleWindowId);
            #if AIRCON_DEVELOPER_DIAGNOSTICS
            SafeLog($"WINDOW_SESSION_STATE result=PRUNED staleWindowId={staleWindowId} currentWindowId={currentWindowId} reason=per_route_window_superseded");
            #endif
        }
    }

    private void StopWindowBackgroundExecution(string windowId)
    {
        if (string.IsNullOrWhiteSpace(windowId)) return;
        lock (_centerRequestLock)
        {
            if (_pendingCenterByWindow.Remove(windowId, out var pendingCenter)) pendingCenter.Cancel();
        }

        lock (_zappingLock)
        {
            var keys = _zappingStates
                .Where(x => x.Value.WindowId.Equals(windowId, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Key)
                .ToArray();
            foreach (var key in keys)
            {
                _zappingStates.Remove(key);
                RemoveAndDisposeTimer(_zappingTimers, key);
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
                RemoveAndDisposeTimer(_powerOffTimers, key);
            }
        }

        lock (_windowDisplayLock) _windowDisplays.Remove(windowId);
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

    private async Task<HostActionDispatchResult> CompleteShutdownAsync(string viewerProfile, string origin)
    {
        if (_runtimeUiStopped)
            return HostActionDispatchResult.Failure("runtime_stopped", "AIrCon is stopped.");
        var lifecycleVersion = CaptureRuntimeUiLifecycle();
        CancelActiveZapping(viewerProfile, supersedePendingOperation: true);
        SetPowerOffStopped(viewerProfile);

        using var gate = GetProfileOperationGate(viewerProfile);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsCurrentRuntimeUiLifecycle(lifecycleVersion))
                return HostActionDispatchResult.Failure("runtime_stopped", "AIrCon is stopped.");
            var result = await DispatchViewerStopForProfileAsync(viewerProfile, origin).ConfigureAwait(false);
            #if AIRCON_DEVELOPER_DIAGNOSTICS
            SafeLog($"COMPLETE_SHUTDOWN viewerProfile={viewerProfile} origin={origin} result={(result.Success ? "OK" : "FAILED")} message={result.Message}");
            #endif
            return result;
        }
        catch (Exception ex)
        {
            #if AIRCON_DEVELOPER_DIAGNOSTICS
            SafeLog($"COMPLETE_SHUTDOWN failed viewerProfile={viewerProfile} origin={origin} exception={ex.GetType().Name} message={ex.Message}");
            #endif
            return HostActionDispatchResult.Failure("viewer_stop_exception", ex.Message);
        }
        finally
        {
            gate.Release();
        }
    }


}
