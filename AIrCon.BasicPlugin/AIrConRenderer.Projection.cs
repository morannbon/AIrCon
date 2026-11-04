using System.Text.Json;
using System.Net;
using TvAIrPlugin;
using TvAIrPlugin.Runtime;
using TvAIrPlugin.Viewers;

namespace AIrCon.BasicPlugin;

internal sealed partial class AIrConRenderer
{
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
            var state = GetWindowDisplay(windowId);
            var remembered = state?.RequestedViewerProfile ?? state?.ViewerProfile;
            if (!string.IsNullOrWhiteSpace(remembered)) return remembered;
        }
        return string.Empty;
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
                #if AIRCON_DEVELOPER_DIAGNOSTICS
                SafeLog("VIEWER_PROFILE_SELECTOR source=runtime_viewers_api profiles=" + runtimeSelectable.Count + " visible=" + runtimeVisible + " default=" + runtimeDefaultId + " profileIds=" + string.Join(",", runtimeSelectable.Select(x => x.Id)));
                #endif
                return new ViewerProfileState(runtimeSelectable, runtimeDefaultId, runtimeVisible, true, true);
            }

            #if AIRCON_DEVELOPER_DIAGNOSTICS
            SafeLog("VIEWER_PROFILE_SELECTOR source=runtime_viewers_api profiles=0 visible=False fallback=none");
            #endif
            return ViewerProfileState.Unavailable;
        }
#if AIRCON_DEVELOPER_DIAGNOSTICS
        catch (Exception ex)
#else
        catch (Exception)
#endif
        {
            #if AIRCON_DEVELOPER_DIAGNOSTICS
            SafeLog("VIEWER_PROFILE_SELECTOR exception=" + ex.GetType().Name + " " + ex.Message);
            #endif
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
        if (text is "CATV" or "ISDB-C" or "CABLE") return "CATV";
        return text;
    }

    private static string RequiredProfileGroupForWave(string wave)
    {
        var normalized = NormalizeWaveFilter(wave);
        return normalized.Equals("GR", StringComparison.OrdinalIgnoreCase) ? "GR" : normalized.Equals("CATV", StringComparison.OrdinalIgnoreCase) ? "CATV" : "BSCS";
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

        return available.FirstOrDefault() ?? ViewerProfileChoice.Unavailable;
    }

    private static ServiceRow FromRuntimeService(TvAirServiceDto service, int index)
    {
        var filter = NormalizeRuntimeBroadcastType(service.BroadcastType);
        return new ServiceRow
        {
            ProgramGuideOrder = service.DisplayOrder != 0 ? service.DisplayOrder : index,
            ProgramGuideFilterGroup = filter,
            ProgramGuideFilterLabel = FilterLabel(filter),
            AllocationGroup = filter == "GR" ? "GR" : filter == "CATV" ? "CATV" : "BSCS",
            TunerGroup = filter == "GR" ? "GR" : filter == "CATV" ? "CATV" : "BSCS",
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
        if (text is "CATV" or "ISDB-C" or "CABLE") return "CATV";
        if (text.StartsWith("CS", StringComparison.Ordinal)) return "CS";
        if (text is "BS" or "BSCS" or "SATELLITE" or "BS/CS") return "BS";
        return text;
    }

    private List<ServiceRow> CaptureServiceProjection(string filter)
    {
        var services = AIrConNewApiBridge.ListServices()
            .Where(x => x.IsEnabled)
            .OrderBy(x => x.DisplayOrder)
            .ThenBy(x => x.ServiceName, StringComparer.OrdinalIgnoreCase)
            .Select((x, index) => FromRuntimeService(x, index))
            .ToList();
        services = NormalizeViewerServiceAuthority(services);
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
        var programEvents = new List<TvAirProgramEventDto>();
        var viewerReservations = new List<TvAirViewerReservation>();
        var projectionUsed = false;
#if AIRCON_DEVELOPER_DIAGNOSTICS
        var channelSnapshotReceived = false;
#endif
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
#if AIRCON_DEVELOPER_DIAGNOSTICS
            channelSnapshotReceived = true;
#endif
#if AIRCON_DEVELOPER_DIAGNOSTICS
            SafeLog("viewerControlChannels=Runtime count=" + services.Count + " source=runtime_channels");
#endif

            waveFilters = AIrConNewApiBridge.ListWaveFilters()
                .Where(x => x.IsProgramGuideFilter)
                .OrderBy(x => x.Order)
                .Select(x => new WaveFilterRow(FirstNonEmpty(x.Key, x.BroadcastType), FirstNonEmpty(x.BroadcastType, x.Key), FirstNonEmpty(x.Label, x.BroadcastType, x.Key)))
                .Where(x => !string.IsNullOrWhiteSpace(x.Group))
                .ToList();
#if AIRCON_DEVELOPER_DIAGNOSTICS
            SafeLog("waveFilters=Runtime count=" + waveFilters.Count);
#endif

            var now = DateTimeOffset.Now;
            programEvents = AIrConNewApiBridge.ListProgramEvents(now, now.AddHours(6)).ToList();
            ApplyRuntimeNowNext(services, programEvents, now);
            projectionUsed = true;
#if AIRCON_DEVELOPER_DIAGNOSTICS
            SafeLog("programGuide=Runtime events=" + programEvents.Count + " listAuthority=runtime_channels");
#endif
        }
#if AIRCON_DEVELOPER_DIAGNOSTICS
        catch (Exception ex) { SafeLog("runtimeProjection exception=" + ex.Message); }
#else
        catch (Exception) { }
#endif

        try
        {
            viewerReservations = AIrConNewApiBridge.ListViewerReservations().Where(x => x.State.Equals("Scheduled", StringComparison.OrdinalIgnoreCase)).ToList();
#if AIRCON_DEVELOPER_DIAGNOSTICS
            SafeLog("viewerReservations=Runtime scheduled=" + viewerReservations.Count);
#endif
        }
#if AIRCON_DEVELOPER_DIAGNOSTICS
        catch (Exception ex) { SafeLog("viewerReservations exception=" + ex.Message); }
#else
        catch (Exception) { }
#endif

        try
        {
            sessions = CaptureViewerSessionsNewApi();
#if AIRCON_DEVELOPER_DIAGNOSTICS
            SafeLog("viewerSessions=Runtime count=" + sessions.Count + " source=runtime_viewer_sessions");
#endif
        }
#if AIRCON_DEVELOPER_DIAGNOSTICS
        catch (Exception ex) { SafeLog("viewerSessions exception=" + ex.Message); }
#else
        catch (Exception) { }
#endif

services = NormalizeViewerServiceAuthority(services);

        // A successful empty Channels snapshot means no configured enabled stations.
        // A failed Channels lookup is unknown, not evidence of GR/BS/CS reception.
        // Never add ungrounded wave buttons after filtering the authoritative services.
        var availableWaves = services.Select(x => NormalizeWaveFilter(x.ProgramGuideFilterGroup))
            .Where(x => x is "GR" or "BS" or "CS" or "CATV")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        waveFilters = waveFilters
            .Where(x => availableWaves.Contains(NormalizeWaveFilter(x.Group)))
            .ToList();
        // A missing wave-filter catalogue must not hide stations the Channels API did return.
        // Labels/order are canonically normalized by CanonicalWaveFilters at render time.
        foreach (var wave in StartupWaves)
        {
            if (availableWaves.Contains(wave) && !waveFilters.Any(x => NormalizeWaveFilter(x.Group) == wave))
                waveFilters.Add(new WaveFilterRow(wave, wave, wave == "GR" ? "地上波" : wave));
        }
#if AIRCON_DEVELOPER_DIAGNOSTICS
        SafeLog("WAVE_VISIBILITY source=runtime_channels snapshot="
            + (channelSnapshotReceived ? "received" : "failed_or_unavailable")
            + " enabledServices=" + services.Count + " visibleWaves=" + string.Join(",", availableWaves)
            + " filters=" + waveFilters.Count);
#endif

#if AIRCON_DEVELOPER_DIAGNOSTICS
        var zeroTripletFinal = services.Count(x => x.NetworkId <= 0 || x.TransportStreamId <= 0 || x.ServiceId <= 0);
        if (zeroTripletFinal > 0) SafeLog("TRIPLET_DIAG finalZero=" + zeroTripletFinal);
#endif

        if (services.Count > 0 && sessions.Count > 0)
        {
            var map = services
                .Where(x => HasResolvedTriplet(x))
                .GroupBy(x => ServiceKey(x.NetworkId, x.TransportStreamId, x.ServiceId), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.OrderBy(r => r.ProgramGuideOrder).First(), StringComparer.OrdinalIgnoreCase);
            foreach (var s in sessions.Where(x => x.IsActive))
            {
                if (s.NetworkId == null || s.TransportStreamId == null || s.ServiceId == null) continue;
                if (!map.TryGetValue(ServiceKey(s.NetworkId.Value, s.TransportStreamId.Value, s.ServiceId.Value), out var row)) continue;
                row.ViewingViewerProfiles.Add(s.ViewerProfile);
            }
        }

#if AIRCON_DEVELOPER_DIAGNOSTICS
        var sessionHighlighted = services.Count(x => x.IsViewing);
        SafeLog("currentRowHighlight source=viewer_session session=" + sessionHighlighted + " focusOverride=disabled");
#endif

        var filtered = services
            .Where(x => x.ProgramGuideFilterGroup.Equals(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.ProgramGuideOrder)
            .ToList();

        #if AIRCON_DEVELOPER_DIAGNOSTICS
        SafeLog("WAVE_CLASSIFICATION requested=" + filter
            + " totalRows=" + services.Count
            + " renderedCount=" + filtered.Count
            + " rule=viewer_service_authority_triplet_overlay_only");
        #endif

        var serviceColumnWidth = CalculateServiceColumnWidth(services);

        return new FloatingViewerData(filtered, sessions, tuners, waveFilters, viewerProfiles, programEvents, viewerReservations, projectionUsed, safeEventContractAvailable, safeDblclickEvents, serviceColumnWidth);
    }

    private static List<ServiceRow> NormalizeViewerServiceAuthority(IReadOnlyList<ServiceRow> source)
    {
        // AIrCon の局行は「視聴開始できる triplet 行」を正本にする。
        // ProgramGuide/NowNext は番組名と時刻の overlay 専用で、局行の採否には使わない。
        // ここでだけ invalid/duplicate を落とし、HTML/CSS 側で隠す後段処理は作らない。
        var normalized = new List<ServiceRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
#if AIRCON_DEVELOPER_DIAGNOSTICS
        var invalid = 0;
        var duplicate = 0;
#endif

        foreach (var row in source.OrderBy(x => x.ProgramGuideOrder).ThenBy(x => x.ServiceName, StringComparer.OrdinalIgnoreCase))
        {
            if (!HasResolvedTriplet(row))
            {
#if AIRCON_DEVELOPER_DIAGNOSTICS
                invalid++;
#endif
                continue;
            }

            var key = ServiceKey(row.NetworkId, row.TransportStreamId, row.ServiceId);
            if (!seen.Add(key))
            {
#if AIRCON_DEVELOPER_DIAGNOSTICS
                duplicate++;
#endif
                continue;
            }

            normalized.Add(row);
        }

#if AIRCON_DEVELOPER_DIAGNOSTICS
        AIrConNewApiBridge.LogInfo("AIrCon: serviceAuthority=viewer_start_triplet rows=" + normalized.Count
            + " invalidDropped=" + invalid
            + " duplicateDropped=" + duplicate
            + " overlayDoesNotPrune=True");
#endif
        return normalized;
    }

    private static List<ViewerSessionRow> CaptureViewerSessionsNewApi()
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
                    IsLiveViewerSession(s),
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
        catch (Exception)
        {
            return new List<ViewerSessionRow>();
        }
    }

    private static void ApplyRuntimeNowNext(List<ServiceRow> services, IReadOnlyList<TvAirProgramEventDto> events, DateTimeOffset now)
    {
        var byService = events
            .Where(x => x.Start <= now && x.End > now)
            .GroupBy(x => ServiceKey(x.NetworkId, x.TransportStreamId, x.ServiceId), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(e => e.Start).First(), StringComparer.OrdinalIgnoreCase);
        foreach (var row in services)
        {
            if (!byService.TryGetValue(ServiceKey(row.NetworkId, row.TransportStreamId, row.ServiceId), out var current)) continue;
            row.CurrentTitle = current.Title;
            row.CurrentStart = current.Start;
            row.CurrentEnd = current.End;
            row.HasCurrentProgramProjection = true;
        }
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
        return new WindowOperation(canOpen, canRefresh && !string.IsNullOrWhiteSpace(windowId), endpoint, route, method, token, pluginId, routeSegment, windowId, toolWindowSupported);
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

}
