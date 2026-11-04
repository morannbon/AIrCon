using System.Text.Json;
using System.Net;
using TvAIrPlugin;
using TvAIrPlugin.Runtime;
using TvAIrPlugin.Viewers;

namespace AIrCon.BasicPlugin;

internal sealed partial class AIrConRenderer
{
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
            var programGuideInitialProfile = string.Empty;
            if (isToolWindow && string.IsNullOrWhiteSpace(requestedWave))
            {
                lock (_zappingLock)
                {
                    if (_pendingProgramGuideInitialSelection is { } pending)
                    {
                        _pendingProgramGuideInitialSelection = null;
                        if (pending.Deadline > DateTimeOffset.UtcNow)
                        {
                            requestedWave = pending.Wave;
                            programGuideInitialProfile = pending.Profile;
                        }
                    }
                }
            }
            var filter = ResolveEffectiveWave(requestedWave, windowIdForWave, isToolWindow);
            if (isToolWindow && requestedView.Equals("settings", StringComparison.OrdinalIgnoreCase))
            {
                var settingsAction = CaptureAction(context);
                var settingsWindow = CaptureWindow(context, true);
                var returnWave = NormalizeWaveFilter(QueryString(query, "returnWave"));
                var returnViewerProfile = NormalizeViewerProfileId(QueryString(query, "returnViewerProfile"));
                return BuildSettingsHtml(context, settingsAction, settingsWindow, AIrConNewApiBridge.LoadSettings(), returnWave, returnViewerProfile);
            }
            var selectedTunerValue = "auto";
            var action = CaptureAction(context);
            var window = CaptureWindow(context, isToolWindow);
            var alwaysOnTop = ResolveWindowAlwaysOnTop(context, window, isToolWindow);
            var viewerProfiles = CaptureViewerProfiles(context);
            var requestedViewerProfile = FirstNonEmpty(programGuideInitialProfile, ResolveRequestedViewerProfile(query, windowIdForWave));
            var focusTriplet = new FocusTriplet(null, null, null);
            var data = CaptureData(filter, viewerProfiles, focusTriplet);
            // A saved wave may no longer have any configured service. Select a visible
            // wave from the same Host channel snapshot; never infer reception from tuners.
            var visibleWaves = CanonicalWaveFilters(data.WaveFilters);
            if (visibleWaves.Count > 0 && !visibleWaves.Any(x => x.Group.Equals(filter, StringComparison.OrdinalIgnoreCase)))
            {
                filter = visibleWaves[0].Group;
                data = CaptureData(filter, viewerProfiles, focusTriplet);
            }
            // Resolve all viewer-scoped state only against the final visible wave.
            // The fallback wave may have a different profile and active timer.
            var selectedViewerProfile = ResolveSelectedViewerProfile(requestedViewerProfile, viewerProfiles, filter);
            var activeZappingState = GetActiveZappingState(selectedViewerProfile.Value);
            var zappingActive = activeZappingState != null;
            var activeZappingWave = activeZappingState?.Wave ?? string.Empty;
            if (zappingActive && activeZappingWave.Equals(filter, StringComparison.OrdinalIgnoreCase))
                UpdateZappingWindowSubscription(selectedViewerProfile.Value, windowIdForWave, filter);
            var powerOffDeadline = ResolvePowerOffDeadline(selectedViewerProfile.Value);
            // The Host can finish loading this HTML after RenderHtml returns. Its unique
            // existing-element ID is checked in the actual WebBrowser before any scroll.
            // Render publication is only a request, never proof of a displayed document.
            var renderVersion = isToolWindow ? Interlocked.Increment(ref _nextRenderVersion) : 0L;
            var html = isToolWindow
                ? BuildFloatingViewerHtml(context, data, action, window, filter, selectedTunerValue, selectedViewerProfile, alwaysOnTop, zappingActive, activeZappingWave, powerOffDeadline, renderVersion)
                : BuildLauncherHtml(context, data, window, filter, selectedTunerValue, selectedViewerProfile.Value, alwaysOnTop);

            if (isToolWindow && !string.IsNullOrWhiteSpace(windowIdForWave))
            {
                var shownSession = data.ViewerSessions.FirstOrDefault(x =>
                    x.IsActive && x.ViewerProfile.Equals(selectedViewerProfile.Value, StringComparison.OrdinalIgnoreCase));
                // A viewer may be tuned to another wave while this window browses an
                // unrelated list. In that case there is no highlighted row in this DOM.
                var shownService = shownSession is { NetworkId: > 0, TransportStreamId: > 0, ServiceId: > 0 }
                    && data.Services.Any(row => row.NetworkId == shownSession.NetworkId.Value
                        && row.TransportStreamId == shownSession.TransportStreamId.Value
                        && row.ServiceId == shownSession.ServiceId.Value)
                    ? ServiceKey(shownSession.NetworkId.Value, shownSession.TransportStreamId.Value, shownSession.ServiceId.Value)
                    : string.Empty;
                PublishWindowDisplay(windowIdForWave, filter, selectedViewerProfile.Value,
                    shownService, renderVersion);
                // Initial display, manual navigation and theme re-render converge only
                // after the browser exposes this exact render's DOM identity.
                if (shownService.Length > 0)
                    ScheduleCurrentServiceCenter(windowIdForWave, 260, shownService);
            }
#if AIRCON_DEVELOPER_DIAGNOSTICS
            // This confirms HTML generation only. The Host controls final WebBrowser painting.
            SafeLog($"RenderHtml route=aircon mode=viewer_current toolWindow={isToolWindow} requestedWave={requestedWave} effectiveWave={filter} viewerProfile={selectedViewerProfile.Value} selectorVisible={viewerProfiles.SelectorVisibleRecommended} profiles={viewerProfiles.SelectableProfiles.Count} services={data.Services.Count} viewers={data.ViewerSessions.Count} activeViewers={data.ViewerSessions.Count(x => x.IsActive)} highlighted={data.Services.Count(x => x.IsViewing)} projectionUsed={data.ProjectionUsed} renderStage=html_generated htmlLength={html.Length}");
            if (zappingActive) SafeLog($"ZAPPING_STATE active windowId={windowIdForWave} activeWave={activeZappingWave} displayedWave={filter} viewerProfile={selectedViewerProfile.Value} services={data.Services.Count} intervalSeconds={activeZappingState!.IntervalSeconds}");
            else if (QueryString(query, "zapping").Equals("off", StringComparison.OrdinalIgnoreCase)) SafeLog($"ZAPPING_STATE stopped windowId={windowIdForWave} wave={filter} viewerProfile={selectedViewerProfile.Value}");
#endif
            return html;
        }
#if AIRCON_DEVELOPER_DIAGNOSTICS
        catch (Exception ex)
#else
        catch (Exception)
#endif
        {
            #if AIRCON_DEVELOPER_DIAGNOSTICS
            SafeLog("RenderHtml failed: " + ex.GetType().Name + " " + ex.Message);
            #endif
            return BuildRenderFailureHtml(context);
        }
    }

    private string ResolveEffectiveWave(string requestedWave, string windowId, bool isToolWindow)
    {
        var normalized = NormalizeWaveFilter(requestedWave);
        if (!string.IsNullOrWhiteSpace(requestedWave)) return normalized;
        if (isToolWindow && !string.IsNullOrWhiteSpace(windowId))
        {
            var state = GetWindowDisplay(windowId);
            var remembered = state?.RequestedWave ?? state?.Wave;
            if (!string.IsNullOrWhiteSpace(remembered)) return NormalizeWaveFilter(remembered);
        }
        return AIrConNewApiBridge.LoadSettings().StartupWave;
    }

}
