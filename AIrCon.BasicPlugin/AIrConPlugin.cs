using System.Net;
using TvAIrPlugin;

namespace AIrCon.BasicPlugin;

/// <summary>
/// AIrCon 正式リリース版。
/// ToolWindow内の行ダブルクリック視聴を主導線にする。
/// </summary>
public sealed class AIrConPlugin : IUiPlugin, IManifestPlugin, IViewerPlugin
{
    private const string PluginVersion = "1.0.0";
    private const string PluginListTitle = "AIrCon";
    private const string PluginId = "aircon.basic";
    private const string RouteSegment = "aircon";
    private const string ClientVersion = "AIrCon-" + PluginVersion;

    private const string ResponseModeHostHandled = "hostHandled";
    private const string RefreshTargetContent = "content";
    private const string BoolTrue = "true";
    private const string BoolFalse = "false";
    private const string ActionViewerStart = "viewerStart";
    private const string ActionViewerStop = "viewerStop";
    private const string ActionUpdateWindow = "updateWindow";

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
    private const int ToolWindowViewerProfileSelectorReservedWidthPx = 210;
    private const int ToolWindowViewerProfileLabelWidthPx = 46;
    private const int ToolWindowViewerProfileNumericButtonWidthPx = 24;
    private const int ToolWindowDefaultWidthPx = 540;
    private const int ToolWindowDefaultHeightPx = 320;
    private const int ToolWindowMinimumHeightContractPx = 240;
    private const int ToolWindowMinimumListRowsPx = 6;
    private static int ToolWindowActionButtonSizePx => ToolWindowToolbarButtonHeightPx;
    private static int ToolWindowActionButtonGroupWidthPx => (ToolWindowActionButtonSizePx * 3) + (ToolWindowToolbarCellGapPx * 2);
    private static int ToolWindowWaveButtonGroupWidthPx => (ToolWindowWaveButtonWidthPx * 3) - (ToolWindowWaveButtonOverlapPx * 2);
    private const int ToolWindowWaveButtonOverlapPx = ToolWindowButtonBorderPx;
    private const int ToolWindowToolbarSeparatorPx = 1;
    private const int ToolWindowRowHeightPx = 30;
    private const int ToolWindowServiceColumnWidthPx = 132;
    private const string CurrentViewingAnchorId = "aircon-current-viewing-anchor";
    private const string RefreshScrollModeCenter = "center";

    private static int ToolWindowToolbarButtonHeightPx => ToolWindowButtonTextLineHeightPx + (ToolWindowButtonVerticalPaddingPx * 2) + (ToolWindowButtonBorderPx * 2);
    private static int ToolWindowToolbarCellHeightPx => ToolWindowToolbarButtonHeightPx;
    private static int ToolWindowToolbarHeightPx => ToolWindowToolbarContentTopPx + ToolWindowToolbarButtonHeightPx + ToolWindowToolbarContentBottomPx + ToolWindowToolbarSeparatorPx;
    private static int ToolWindowListTopPx => ToolWindowToolbarHeightPx;
    private static int ToolWindowButtonLineHeightPx => Math.Max(1, ToolWindowToolbarButtonHeightPx - (ToolWindowButtonBorderPx * 2));
    private static int ToolWindowToolbarMinimumContentWidthPx => ToolWindowWaveAreaWidthPx + ToolWindowToolbarGroupGapPx + ToolWindowViewerProfileSelectorReservedWidthPx + ToolWindowToolbarGroupGapPx + ToolWindowActionButtonGroupWidthPx;
    private static int ToolWindowMinimumWidthPx => Math.Max(ToolWindowDefaultWidthPx, (ToolWindowToolbarPaddingXPx * 2) + ToolWindowToolbarMinimumContentWidthPx);
    private static int ToolWindowMinimumHeightPx => Math.Max(ToolWindowMinimumHeightContractPx, ToolWindowToolbarHeightPx + (ToolWindowRowHeightPx * ToolWindowMinimumListRowsPx) + 16);

    private IPluginContext? _context;
    private readonly Dictionary<string, string> _lastWaveByWindowId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _lastViewerProfileByWindowId = new(StringComparer.OrdinalIgnoreCase);

    public string Name => "AIrCon";
    public string Version => PluginVersion;

    public PluginUiDescriptor Ui { get; } = new()
    {
        RouteSegment = RouteSegment,
        MenuText = PluginListTitle,
        Description = "常駐型フロート視聴パネル",
        Capabilities = new[]
        {
            "ShowUi",
            "OpenToolWindow",
            "ControlViewer",
            "ReadChannels",
            "ReadEpg",
            "ReadTunerStatus",
            "ReadViewerSessions",
            "UseActionApi",
            "UseWindowApi",
            "UseSafeEvent",
            "UseAssetApi"
        },
        Icon = "AIrCon.ico",
        DisplayOrder = 420,
        Enabled = true,
        ToolWindowWidth = ToolWindowDefaultWidthPx,
        ToolWindowHeight = ToolWindowDefaultHeightPx,
        ToolWindowMinWidth = ToolWindowMinimumWidthPx,
        ToolWindowMinHeight = ToolWindowMinimumHeightPx,
        PreferredOpenMode = "toolWindow",
        DefaultMenuActionKind = "toolWindow",
        DefaultMenuActionLabel = PluginListTitle,
        DefaultMenuActionPriority = 420,
        ToolWindowShowInTaskbar = true
    };

    public ViewerPluginCapabilities Capabilities { get; } = new()
    {
        SupportsExternalProcess = false,
        SupportsLiveView = true,
        Description = "安定viewer payloadを維持し、GetViewerSessionsのtriplet/current状態で現在視聴中背景色を反映します。"
    };

    public PluginManifest Manifest { get; } = new()
    {
        Id = PluginId,
        Name = "AIrCon",
        Version = PluginVersion,
        Route = RouteSegment,
        DefaultRoute = RouteSegment,
        Entry = "AIrCon.BasicPlugin.AIrConPlugin",
        Description = "常駐型小型リモコン視聴パネル",
        Vendor = "AIrCon Plugin Team",
        HostContractVersion = "0.11.315",
        Capabilities = new[]
        {
            "ShowUi",
            "OpenToolWindow",
            "ControlViewer",
            "ReadChannels",
            "ReadEpg",
            "ReadTunerStatus",
            "ReadViewerSessions",
            "UseActionApi",
            "UseWindowApi",
            "UseSafeEvent",
            "UseAssetApi"
        },
        Tags = new[] { "official", "host-managed-toolwindow", "viewer-control" },
        Icon = "AIrCon.ico",
        PreferredOpenMode = "toolWindow",
        ToolWindowWidth = ToolWindowDefaultWidthPx,
        ToolWindowHeight = ToolWindowDefaultHeightPx,
        ToolWindowMinWidth = ToolWindowMinimumWidthPx,
        ToolWindowMinHeight = ToolWindowMinimumHeightPx,
        ToolWindowReuseExisting = true,
        ToolWindowActivateExisting = true,
        DefaultMenuActionKind = "toolWindow",
        DefaultMenuActionLabel = PluginListTitle,
        DefaultMenuActionPriority = 420,
        ToolWindowShowInTaskbar = true,
        Kind = new[] { "Viewer", "UI" },
        Permissions = new[]
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
            PluginPermission.ReadViewerControlContracts,
            PluginPermission.ReadHostContracts,
            PluginPermission.UseActionApi,
            PluginPermission.UseWindowApi,
            PluginPermission.UseAssetApi,
            PluginPermission.UseSafeEvent
        }
    };

    public void Initialize(IPluginContext context)
    {
        _context = context;
        SafeLog("Initialize completed. AIrCon v1.0.0.");
    }

    public void OnStart() => SafeLog("OnStart completed.");
    public void OnStop() => SafeLog("OnStop completed.");

    public string RenderHtml(PluginUiContext context)
    {
        try
        {
            var isToolWindow = IsToolWindow(context);
            var query = ExtractQueryDictionary(context);
            var requestedWave = QueryString(query, "wave");
            var windowIdForWave = QueryWindowId(context);
            var filter = ResolveEffectiveWave(requestedWave, windowIdForWave, isToolWindow);
            RememberWave(windowIdForWave, filter, isToolWindow);
            var selectedTunerValue = "auto";
            var action = CaptureAction(context);
            var window = CaptureWindow(context, isToolWindow);
            var alwaysOnTop = ResolveWindowAlwaysOnTop(context, window, isToolWindow);
            var viewerProfiles = CaptureViewerProfiles(context);
            var requestedViewerProfile = ResolveRequestedViewerProfile(query, windowIdForWave);
            var selectedViewerProfile = ResolveSelectedViewerProfile(requestedViewerProfile, viewerProfiles, filter);
            RememberViewerProfile(windowIdForWave, selectedViewerProfile.Value, isToolWindow);
            var focusTriplet = CaptureFocusTriplet(query);
            var data = CaptureData(filter, viewerProfiles, focusTriplet);
            if (isToolWindow && string.IsNullOrWhiteSpace(requestedWave))
            {
                var sessionWave = InferWaveFromViewerSessions(data.ViewerSessions);
                if (!string.IsNullOrWhiteSpace(sessionWave) && !sessionWave.Equals(filter, StringComparison.OrdinalIgnoreCase))
                {
                    filter = sessionWave;
                    RememberWave(windowIdForWave, filter, isToolWindow);
                    data = CaptureData(filter, viewerProfiles, focusTriplet);
                }
            }

            SafeLog($"RenderHtml route=aircon mode=viewer_release_1_0_0 toolWindow={isToolWindow} requestedWave={requestedWave} effectiveWave={filter} viewerProfile={selectedViewerProfile.Value} selectorVisible={viewerProfiles.SelectorVisibleRecommended} profiles={viewerProfiles.SelectableProfiles.Count} services={data.Services.Count} viewers={data.ViewerSessions.Count} activeViewers={data.ViewerSessions.Count(x => x.IsActive)} highlighted={data.Services.Count(x => x.IsViewing)} projectionUsed={data.ProjectionUsed}");

            return isToolWindow
                ? BuildFloatingViewerHtml(data, action, window, filter, selectedTunerValue, selectedViewerProfile, alwaysOnTop)
                : BuildLauncherHtml(data, window, filter, selectedTunerValue, selectedViewerProfile.Value, alwaysOnTop);
        }
        catch (Exception ex)
        {
            SafeLog("RenderHtml failed: " + ex.GetType().Name + " " + ex.Message);
            return BuildRenderFailureHtml();
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
        return "GR";
    }

    private void RememberWave(string windowId, string wave, bool isToolWindow)
    {
        if (!isToolWindow || string.IsNullOrWhiteSpace(windowId)) return;
        var normalized = NormalizeWaveFilter(wave);
        lock (_lastWaveByWindowId) _lastWaveByWindowId[windowId] = normalized;
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

    private static string InferWaveFromViewerSessions(IReadOnlyList<ViewerSessionRow> sessions)
    {
        var group = sessions.FirstOrDefault()?.ProgramGuideFilterGroup ?? string.Empty;
        return NormalizeWaveFilter(group);
    }

    private static string BuildRenderFailureHtml()
    {
        return @"<!doctype html><html><head><meta charset=""utf-8""><style>html,body{margin:0;background:#f5f8fb;color:#172635;font-family:Meiryo,Arial,sans-serif;font-size:12px}.aircon-error{padding:12px}</style></head><body><div class=""aircon-error"">AIrCon表示を生成できませんでした。</div></body></html>";
    }


    private ViewerProfileState CaptureViewerProfiles(PluginUiContext context)
    {
        try
        {
            // official: TvAIr v0.11.72+ projects TVTest-instance viewer profiles into PluginUiContext.
            // Prefer the RenderHtml context over IPluginContext/fallback so host-generated
            // selectableViewerProfiles/defaultViewerProfile/availableGroups are not lost.
            var contextSource = ReadProperty(context, "ViewerProfilesContract")
                ?? ReadProperty(context, "ViewerProfileContract")
                ?? ReadProperty(context, "ViewerProfilesProjection")
                ?? ReadProperty(context, "ViewerProfileProjection");
            var source = contextSource
                ?? InvokeContextMethod("GetViewerProfiles")
                ?? ReadProperty(_context, "ViewerProfilesContract")
                ?? ReadProperty(_context, "ViewerProfiles")
                ?? ReadProperty(_context, "ViewerProfileContract");

            var selectableSource = ReadProperty(context, "SelectableViewerProfiles")
                ?? ReadProperty(context, "selectableViewerProfiles")
                ?? ReadProperty(contextSource, "SelectableViewerProfiles")
                ?? ReadProperty(contextSource, "selectableViewerProfiles")
                ?? ReadProperty(source, "SelectableViewerProfiles")
                ?? ReadProperty(source, "selectableViewerProfiles")
                ?? ReadProperty(context, "ViewerProfiles")
                ?? ReadProperty(context, "viewerProfiles")
                ?? ReadProperty(contextSource, "ViewerProfiles")
                ?? ReadProperty(contextSource, "viewerProfiles")
                ?? ReadProperty(source, "ViewerProfiles")
                ?? ReadProperty(source, "viewerProfiles")
                ?? source;
            if (selectableSource == null)
            {
                SafeLog("VIEWER_PROFILE_SELECTOR source=unavailable fallback=tvtest1_only");
                return ViewerProfileState.Unavailable;
            }
            var selectable = ReadViewerProfileChoices(selectableSource)
                .Where(x => x.Enabled)
                .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderBy(x => x.Order).First())
                .OrderBy(x => x.Order)
                .ThenBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // official: auto is a TvAIr-side backward-compatibility alias only.
            // Do not add it as an AIrCon UI option.
            selectable = selectable
                .Where(x => !IsAutoProfileId(x.Id))
                .ToList();

            var defaultObject = ReadProperty(context, "DefaultViewerProfile")
                ?? ReadProperty(context, "defaultViewerProfile")
                ?? ReadProperty(contextSource, "DefaultViewerProfile")
                ?? ReadProperty(contextSource, "defaultViewerProfile")
                ?? ReadProperty(source, "DefaultViewerProfile")
                ?? ReadProperty(source, "defaultViewerProfile");
            var defaultId = FirstNonEmpty(
                ReadString(context, "DefaultViewerProfile", "defaultViewerProfile"),
                ReadString(contextSource, "DefaultViewerProfile", "defaultViewerProfile"),
                ReadString(source, "DefaultViewerProfile", "defaultViewerProfile"),
                ReadString(defaultObject, "Id", "id"),
                defaultObject is string ds ? ds : string.Empty,
                selectable.FirstOrDefault(x => x.IsDefault)?.Id,
                selectable.FirstOrDefault()?.Id,
                "tvtest1");
            if (IsAutoProfileId(defaultId)) defaultId = FirstNonEmpty(selectable.FirstOrDefault(x => x.IsDefault)?.Id, selectable.FirstOrDefault()?.Id, "tvtest1");
            var selectorRecommended = TryReadBoolAny(out var selectorValue,
                    (context, new[] { "SelectorVisibleRecommended", "selectorVisibleRecommended" }),
                    (contextSource, new[] { "SelectorVisibleRecommended", "selectorVisibleRecommended" }),
                    (source, new[] { "SelectorVisibleRecommended", "selectorVisibleRecommended" }))
                ? selectorValue
                : true;
            var minWidthInvariant = TryReadBoolAny(out var invariantValue,
                    (context, new[] { "MinWidthInvariantRequired", "minWidthInvariantRequired" }),
                    (contextSource, new[] { "MinWidthInvariantRequired", "minWidthInvariantRequired" }),
                    (source, new[] { "MinWidthInvariantRequired", "minWidthInvariantRequired" }))
                ? invariantValue
                : true;
            var visible = selectorRecommended && selectable.Count >= 2;
            var selectorSourceLabel = contextSource != null ? "PluginUiContext" : (ReferenceEquals(selectableSource, source) ? "fallback" : "PluginUiContext");
            SafeLog("VIEWER_PROFILE_SELECTOR source=" + selectorSourceLabel + " profiles=" + selectable.Count + " visible=" + visible + " selectorRecommended=" + selectorRecommended + " default=" + defaultId + " minWidthInvariant=" + minWidthInvariant + " profileIds=" + string.Join(",", selectable.Select(x => x.Id)));
            return new ViewerProfileState(selectable, defaultId, visible, minWidthInvariant, true);
        }
        catch (Exception ex)
        {
            SafeLog("VIEWER_PROFILE_SELECTOR exception=" + ex.GetType().Name + " " + ex.Message);
            return ViewerProfileState.Unavailable;
        }
    }

    private object? InvokeContextMethod(string methodName)
    {
        if (_context == null) return null;
        try
        {
            var method = _context.GetType().GetMethod(methodName, Type.EmptyTypes);
            return method?.Invoke(_context, Array.Empty<object>());
        }
        catch { return null; }
    }

    private static IReadOnlyList<ViewerProfileChoice> ReadViewerProfileChoices(object? source)
    {
        var result = new List<ViewerProfileChoice>();
        if (source == null) return result;
        if (source is string text)
        {
            if (!string.IsNullOrWhiteSpace(text)) result.Add(new ViewerProfileChoice(text.Trim(), text.Trim(), true, false, 0, Array.Empty<string>()));
            return result;
        }
        if (source is System.Collections.IEnumerable enumerable)
        {
            var index = 0;
            foreach (var item in enumerable)
            {
                if (item == null) continue;
                var id = FirstNonEmpty(ReadString(item, "Id", "id", "Key", "key", "Value", "value"), item is string s ? s : string.Empty);
                if (string.IsNullOrWhiteSpace(id)) continue;
                var name = FirstNonEmpty(ReadString(item, "Name", "name", "Label", "label", "DisplayName", "displayName"), id);
                var enabled = ReadBoolOrDefault(item, true, "Enabled", "enabled", "IsEnabled", "isEnabled");
                var isDefault = ReadBoolOrDefault(item, false, "IsDefault", "isDefault", "Default", "default");
                var order = ReadInt(item, "Order", "order", "DisplayOrder", "displayOrder");
                var availableGroups = ReadStringList(item, "AvailableGroups", "availableGroups", "Groups", "groups", "SupportedGroups", "supportedGroups");
                if (order == 0) order = index;
                result.Add(new ViewerProfileChoice(id, name, enabled, isDefault, order, availableGroups));
                index++;
            }
        }
        return result;
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

    private static bool TryReadBoolAny(out bool value, params (object? Source, string[] Names)[] candidates)
    {
        value = false;
        foreach (var candidate in candidates)
        {
            if (candidate.Source == null) continue;
            foreach (var name in candidate.Names)
            {
                var raw = ReadProperty(candidate.Source, name);
                if (raw is bool b) { value = b; return true; }
                if (raw != null && TryParseBool(raw.ToString(), out var parsed)) { value = parsed; return true; }
            }
        }
        return false;
    }

    private static bool ReadBoolOrDefault(object? obj, bool defaultValue, params string[] names)
    {
        foreach (var name in names)
        {
            var value = ReadProperty(obj, name);
            if (value == null) continue;
            if (value is bool b) return b;
            var text = value.ToString()?.Trim() ?? string.Empty;
            if (text.Equals("1", StringComparison.OrdinalIgnoreCase) || text.Equals("true", StringComparison.OrdinalIgnoreCase) || text.Equals("yes", StringComparison.OrdinalIgnoreCase)) return true;
            if (text.Equals("0", StringComparison.OrdinalIgnoreCase) || text.Equals("false", StringComparison.OrdinalIgnoreCase) || text.Equals("no", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return defaultValue;
    }

    private static FocusTriplet CaptureFocusTriplet(IReadOnlyDictionary<string, string> query)
    {
        return new FocusTriplet(
            ParseNullableInt(QueryString(query, "focusNid")),
            ParseNullableInt(QueryString(query, "focusTsid")),
            ParseNullableInt(QueryString(query, "focusSid")));
    }

    private static int? ParseNullableInt(string value)
    {
        if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed > 0) return parsed;
        return null;
    }

    private static ViewerProfileChoice ResolveSelectedViewerProfile(string requested, ViewerProfileState state, string wave)
    {
        var selectable = state.SelectableProfiles.Where(x => x.Enabled).ToList();
        var available = state.AvailableForWave(wave).ToList();
        var desired = FirstNonEmpty(requested, state.DefaultViewerProfile, available.FirstOrDefault()?.Id, selectable.FirstOrDefault()?.Id, "tvtest1");
        if (IsAutoProfileId(desired)) desired = FirstNonEmpty(state.DefaultViewerProfile, available.FirstOrDefault()?.Id, selectable.FirstOrDefault()?.Id, "tvtest1");

        // Preserve any valid projected profile id as UI state, even when the current wave cannot use it.
        // Sending is prevented by disabling unavailable rows/options; do not silently rewrite TVTestN to another profile.
        var exact = selectable.FirstOrDefault(x => x.Id.Equals(desired, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;

        return available.FirstOrDefault() ?? selectable.FirstOrDefault() ?? state.SelectableProfiles.FirstOrDefault() ?? ViewerProfileChoice.TvTest1Fallback;
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

        if (_context is IPluginReadContextV5 v5)
        {
            try
            {
                var host = v5.GetHostContractInfo();
                diagnostics.Add("hostContract=V5 version=" + host.ContractVersion
                    + " stable=" + (host.StableReadContracts?.Count ?? 0)
                    + " actions=" + (host.ControlledActionContracts?.Count ?? 0)
                    + " notExposed=" + (host.NotExposedByDesign?.Count ?? 0));
            }
            catch (Exception ex) { diagnostics.Add("hostContract exception=" + ex.Message); }
        }

        if (_context is IPluginReadContextV4 v4)
        {
            try
            {
                waveFilters = v4.GetProgramGuideWaveFilters()
                    .OrderBy(x => x.Order)
                    .Select(x => new WaveFilterRow(FirstNonEmpty(x.Key, x.Group), FirstNonEmpty(x.Group, x.Key), FirstNonEmpty(x.Label, x.Group, x.Key)))
                    .Where(x => !string.IsNullOrWhiteSpace(x.Group))
                    .ToList();
                diagnostics.Add("waveFilters=V4 count=" + waveFilters.Count);
            }
            catch (Exception ex) { diagnostics.Add("waveFilters exception=" + ex.Message); }

            try
            {
                tuners = v4.GetViewerTuners(new PluginViewerTunerQuery { IncludeBusy = true, IncludeRecordingRole = false })
                    .Where(x => x.IsSelectableForViewer || x.IsViewingRole)
                    .Select(x => new ViewerTunerRow(
                        x.Name,
                        x.Did,
                        x.SlotIndex,
                        NormalizeFilter(FirstNonEmpty(x.ProgramGuideFilterGroup, x.DisplayGroup)),
                        FirstNonEmpty(x.AllocationGroup, x.TunerGroup),
                        x.Busy,
                        x.IsSelectableForViewer,
                        x.Role))
                    .ToList();
                diagnostics.Add("viewerTuners=V4 count=" + tuners.Count);
            }
            catch (Exception ex) { diagnostics.Add("viewerTuners exception=" + ex.Message); }

            try
            {
                var contract = v4.GetViewerControlHostContract();
                safeEventContractAvailable = contract.Success;
                safeDblclickEvents = contract.ToolWindowOnlySafeEvents
                    && contract.SupportedEvents.Any(x => x.Equals("dblclick", StringComparison.OrdinalIgnoreCase))
                    && contract.SupportedActions.Any(x => x.Equals("viewerStart", StringComparison.OrdinalIgnoreCase));
                diagnostics.Add("safeEventContract=V4 success=" + contract.Success + " safeEvents=" + safeDblclickEvents + " version=" + contract.ContractVersion);
            }
            catch (Exception ex) { diagnostics.Add("safeEventContract exception=" + ex.Message); }

            try
            {
                var query = new PluginProgramGuideChannelQuery();
                if (filter is "GR" or "BS" or "CS") query.ProgramGuideFilterGroup = filter;
                var viewerChannels = v4.GetViewerControlChannels(query) ?? Array.Empty<PluginViewerControlChannelInfo>();
                services = viewerChannels
                    .Where(IsVisibleViewerControlChannel)
                    .OrderBy(x => x.ProgramGuideOrder)
                    .Select((x, i) => FromViewerControlChannel(x, i))
                    .ToList();
                if (services.Count > 0) projectionUsed = true;
                diagnostics.Add("viewerControlChannels=V4 services=" + services.Count + " source=GetViewerControlChannels");
            }
            catch (Exception ex) { diagnostics.Add("viewerControlChannels exception=" + ex.Message); }
        }

        if (_context is IPluginReadContextV3 v3)
        {
            try
            {
                var query = new PluginProgramGuideQuery { IncludeNowNext = true, Limit = 500 };
                if (filter is "GR" or "BS" or "CS") query.ProgramGuideFilterGroup = filter;
                var snapshot = v3.GetProgramGuideSnapshot(query);
                projectionUsed = true;
                var rows = (snapshot.NowNext ?? Array.Empty<PluginProgramGuideNowNext>()).ToList();
                if (services.Count > 0)
                {
                    var applied = ApplyNowNextFromSnapshot(services, rows);
                    diagnostics.Add("programGuideSnapshot=V3 overlayNowNext=" + applied + " base=ViewerControlChannels revision=" + snapshot.Revision);
                }
                else if (rows.Count > 0)
                {
                    var index = 0;
                    foreach (var item in rows)
                    {
                        if (!IsVisibleChannel(item.Channel)) continue;
                        var row = FromGuideChannel(item.Channel, index++);
                        ApplyNowNext(row, item);
                        services.Add(row);
                    }
                    diagnostics.Add("programGuideSnapshot=V3 services=" + services.Count + " source=NowNext revision=" + snapshot.Revision);
                }
                else
                {
                    var index = 0;
                    foreach (var channel in (snapshot.Channels ?? Array.Empty<PluginProgramGuideChannel>()).Where(IsVisibleChannel))
                    {
                        services.Add(FromGuideChannel(channel, index++));
                    }
                    diagnostics.Add("programGuideSnapshot=V3 services=" + services.Count + " source=Channels revision=" + snapshot.Revision);
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add("programGuideSnapshot exception=" + ex.Message);
            }

            try
            {
                sessions = CaptureViewerSessions(v3, viewerProfiles, diagnostics);
                diagnostics.Add("viewerSessions=V3 count=" + sessions.Count + " source=profile_scoped_client_ids");
            }
            catch (Exception ex) { diagnostics.Add("viewerSessions exception=" + ex.Message); }
        }

        var zeroTripletBeforeCurrentFallback = services.Count(x => x.NetworkId <= 0 || x.TransportStreamId <= 0 || x.ServiceId <= 0);
        if (zeroTripletBeforeCurrentFallback > 0 && _context is IPluginReadContextV2 v2)
        {
            try
            {
                var restored = ApplyTripletFromCurrentPrograms(services, v2.GetCurrentPrograms(new PluginCurrentProgramQuery()) ?? Array.Empty<PluginEpgEvent>());
                var zeroTripletAfterCurrentFallback = services.Count(x => x.NetworkId <= 0 || x.TransportStreamId <= 0 || x.ServiceId <= 0);
                diagnostics.Add($"tripletCurrentProgramFallback before={zeroTripletBeforeCurrentFallback} restored={restored} after={zeroTripletAfterCurrentFallback}");
                if (zeroTripletAfterCurrentFallback > 0)
                {
                    SafeLog("TRIPLET_DIAG zeroAfterCurrentFallback=" + zeroTripletAfterCurrentFallback);
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add("tripletCurrentProgramFallback exception=" + ex.Message);
                SafeLog("TRIPLET_DIAG currentProgramFallbackException=" + ex.Message);
            }
        }

        var zeroTripletBeforeChannelFallback = services.Count(x => x.NetworkId <= 0 || x.TransportStreamId <= 0 || x.ServiceId <= 0);
        if (zeroTripletBeforeChannelFallback > 0 && _context is IPluginExtendedContextV1 ext)
        {
            try
            {
                var channels = ext.GetChannels(new PluginChannelQuery()) ?? Array.Empty<PluginChannelInfo>();
                var restored = ApplyTripletFromChannels(services, channels);
                var zeroTripletAfterChannelFallback = services.Count(x => x.NetworkId <= 0 || x.TransportStreamId <= 0 || x.ServiceId <= 0);
                diagnostics.Add($"tripletChannelFallback before={zeroTripletBeforeChannelFallback} restored={restored} after={zeroTripletAfterChannelFallback} source=GetChannels");
                SafeLog("TRIPLET_DIAG channelFallback before=" + zeroTripletBeforeChannelFallback + " restored=" + restored + " after=" + zeroTripletAfterChannelFallback);
            }
            catch (Exception ex)
            {
                diagnostics.Add("tripletChannelFallback exception=" + ex.Message);
                SafeLog("TRIPLET_DIAG channelFallbackException=" + ex.Message);
            }
        }

        var zeroTripletFinal = services.Count(x => x.NetworkId <= 0 || x.TransportStreamId <= 0 || x.ServiceId <= 0);
        if (zeroTripletFinal > 0)
        {
            SafeLog("TRIPLET_DIAG finalZero=" + zeroTripletFinal);
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
                row.ViewingLeaseId = s.LeaseId;
                row.ViewingTunerName = s.TunerName;
                row.ViewingDid = s.Did;
                row.ViewingViewerProfile = s.ViewerProfile;
                row.ViewingViewerProfileName = s.ViewerProfileName;
            }
        }

        var sessionHighlighted = services.Count(x => x.IsViewing);
        var focusHighlighted = 0;
        if (focusTriplet.IsResolved)
        {
            var focused = services
                .Where(x => HasResolvedTriplet(x))
                .FirstOrDefault(x => x.NetworkId == focusTriplet.NetworkId && x.TransportStreamId == focusTriplet.TransportStreamId && x.ServiceId == focusTriplet.ServiceId);
            var focusAlreadyHighlighted = focused?.IsViewing == true;
            if (focused != null && !focusAlreadyHighlighted)
            {
                foreach (var row in services)
                {
                    row.IsViewing = false;
                    row.ViewingLeaseId = string.Empty;
                    row.ViewingTunerName = string.Empty;
                    row.ViewingDid = string.Empty;
                    row.ViewingViewerProfile = string.Empty;
                    row.ViewingViewerProfileName = string.Empty;
                }
                focused.IsViewing = true;
                focused.ViewingLeaseId = "focus-triplet";
                focused.ViewingTunerName = string.Empty;
                focused.ViewingDid = string.Empty;
                focused.ViewingViewerProfile = string.Empty;
                focused.ViewingViewerProfileName = string.Empty;
                focusHighlighted = 1;
            }
            else if (focusAlreadyHighlighted)
            {
                focusHighlighted = 1;
            }
        }
        diagnostics.Add("currentRowHighlight session=" + sessionHighlighted + " focus=" + focusHighlighted + " focusTriplet=" + focusTriplet.ToLogString());
        if (focusTriplet.IsResolved)
        {
            SafeLog("CURRENT_ROW_ANCHOR_FOCUS focus=" + focusTriplet.ToLogString() + " applied=" + focusHighlighted + " sessionHighlighted=" + sessionHighlighted + " rule=aircon_release");
        }

        var filtered = services
            .Where(x => x.ProgramGuideFilterGroup.Equals(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.ProgramGuideOrder)
            .ToList();

        SafeLog("WAVE_CLASSIFICATION requested=" + filter
            + " totalRows=" + services.Count
            + " renderedCount=" + filtered.Count
            + " rule=program_guide_projection_group_first_fallback_chspace");

        return new FloatingViewerData(filtered, sessions, tuners, waveFilters, viewerProfiles, diagnostics, projectionUsed, safeEventContractAvailable, safeDblclickEvents);
    }

    private static List<ViewerSessionRow> CaptureViewerSessions(IPluginReadContextV3 v3, ViewerProfileState viewerProfiles, List<string> diagnostics)
    {
        var rows = new List<ViewerSessionRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddSessions(string label, PluginViewerSessionQuery query, bool filterByAirConClientPrefix)
        {
            try
            {
                var source = v3.GetViewerSessions(query) ?? Array.Empty<PluginViewerSessionInfo>();
                var added = 0;
                foreach (var session in source)
                {
                    if (filterByAirConClientPrefix && !IsAIrConViewerClientId(session.ClientId)) continue;
                    var row = FromViewerSession(session);
                    if (string.IsNullOrWhiteSpace(row.LeaseId)) continue;
                    var key = string.IsNullOrWhiteSpace(row.LeaseId)
                        ? (session.ClientId + ":" + (session.ProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"))
                        : row.LeaseId;
                    if (!seen.Add(key)) continue;
                    rows.Add(row);
                    added++;
                }
                diagnostics.Add("viewerSessionsQuery=" + label + " raw=" + source.Count + " added=" + added);
            }
            catch (Exception ex)
            {
                diagnostics.Add("viewerSessionsQuery=" + label + " exception=" + ex.Message);
            }
        }

        // TvAIr v0.11.52+ scopes viewer leases by profile: <pluginId>:viewer:<profileId>.
        // Querying the legacy <pluginId>:viewer client only misses active sessions and breaks highlight/anchor.
        AddSessions("all_aircon_prefix", new PluginViewerSessionQuery(), filterByAirConClientPrefix: true);
        AddSessions("legacy", new PluginViewerSessionQuery { ClientId = PluginId + ":viewer" }, filterByAirConClientPrefix: false);

        var profileIds = viewerProfiles.SelectableProfiles
            .Select(x => x.Id)
            .Concat(new[] { "tvtest1", "tvtest2" })
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var profileId in profileIds)
        {
            AddSessions("profile:" + profileId, new PluginViewerSessionQuery { ClientId = PluginId + ":viewer:" + profileId }, filterByAirConClientPrefix: false);
        }

        return rows
            .OrderByDescending(x => x.Current)
            .ThenByDescending(x => x.IsActive)
            .ThenBy(x => x.ProgramGuideFilterGroup, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsAIrConViewerClientId(string? clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return false;
        return clientId.Equals(PluginId + ":viewer", StringComparison.OrdinalIgnoreCase)
            || clientId.StartsWith(PluginId + ":viewer:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVisibleViewerControlChannel(PluginViewerControlChannelInfo c)
        => !string.IsNullOrWhiteSpace(c.ServiceName);

    private static ServiceRow FromViewerControlChannel(PluginViewerControlChannelInfo c, int index)
    {
        var filter = NormalizeFilter(FirstNonEmpty(c.ProgramGuideFilterGroup, c.BroadcastGroup, c.ProgramGuideFilterKey));
        if (filter is not ("GR" or "BS" or "CS"))
        {
            if (c.AllocationGroup.Equals("GR", StringComparison.OrdinalIgnoreCase) || c.TunerGroup.Equals("GR", StringComparison.OrdinalIgnoreCase)) filter = "GR";
            else if (c.ChannelSpace == 1) filter = "CS";
            else if (c.ChannelSpace == 0) filter = "BS";
            else filter = "GR";
        }

        var row = new ServiceRow
        {
            ProgramGuideOrder = c.ProgramGuideOrder != 0 ? c.ProgramGuideOrder : index,
            ProgramGuideFilterGroup = filter,
            ProgramGuideFilterLabel = FirstNonEmpty(c.ProgramGuideFilterLabel, FilterLabel(filter)),
            AllocationGroup = FirstNonEmpty(c.AllocationGroup, c.TunerGroup, filter == "GR" ? "GR" : "BSCS"),
            TunerGroup = FirstNonEmpty(c.TunerGroup, c.AllocationGroup, filter == "GR" ? "GR" : "BSCS"),
            ServiceName = c.ServiceName,
            NetworkId = FirstPositive(c.NetworkId, c.Nid),
            TransportStreamId = FirstPositive(c.TransportStreamId, c.Tsid),
            ServiceId = FirstPositive(c.ServiceId, c.Sid),
            ChannelSpace = c.ChannelSpace,
            ChannelIndex = c.ChannelIndex,
            ChannelArgument = c.ChannelArgument
        };
        return row;
    }

    private static int FirstPositive(params int[] values)
        => values.FirstOrDefault(x => x > 0);

    private static int ApplyNowNextFromSnapshot(IReadOnlyList<ServiceRow> services, IReadOnlyList<PluginProgramGuideNowNext> rows)
    {
        if (services.Count == 0 || rows.Count == 0) return 0;
        var byTriplet = services
            .Where(HasResolvedTriplet)
            .GroupBy(x => ServiceKey(x.NetworkId, x.TransportStreamId, x.ServiceId), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var byName = services
            .Where(x => !string.IsNullOrWhiteSpace(x.ServiceName))
            .GroupBy(x => NormalizeServiceKey(x.ServiceName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var applied = 0;
        foreach (var item in rows)
        {
            ServiceRow? row = null;
            if (item.Channel.NetworkId > 0 && item.Channel.TransportStreamId > 0 && item.Channel.ServiceId > 0)
            {
                byTriplet.TryGetValue(ServiceKey(item.Channel.NetworkId, item.Channel.TransportStreamId, item.Channel.ServiceId), out row);
            }
            row ??= byName.TryGetValue(NormalizeServiceKey(item.Channel.ServiceName), out var byServiceName) ? byServiceName : null;
            if (row == null) continue;
            ApplyNowNext(row, item);
            applied++;
        }
        return applied;
    }

    private static bool IsVisibleChannel(PluginProgramGuideChannel c)
        => c.IsEnabledInUserChannelSet && c.IsProgramGuideVisible;

    private static string ResolveProgramGuideFilterGroup(PluginProgramGuideChannel c)
    {
        // 番組表表示分類を最優先する。チューナー割当group=BSCSはBS/CS表示分類には使わない。
        var official = NormalizeFilter(FirstNonEmpty(
            ReadString(c, "ProgramGuideFilterGroup", "BroadcastWave", "ProgramGuideGroup", "DisplayGroup", "BroadcastGroup", "Wave", "Band", "ProgramGuideBand"),
            c.ProgramGuideFilterKey));
        if (official is "GR" or "BS" or "CS") return official;

        var allocation = FirstNonEmpty(c.AllocationGroup, c.TunerGroup);
        if (allocation.Equals("GR", StringComparison.OrdinalIgnoreCase)) return "GR";
        if (allocation.Equals("BSCS", StringComparison.OrdinalIgnoreCase))
        {
            if (c.ChannelSpace == 1) return "CS";
            if (c.ChannelSpace == 0) return "BS";
        }
        return "GR";
    }

    private static ServiceRow FromGuideChannel(PluginProgramGuideChannel c, int index)
    {
        var filter = ResolveProgramGuideFilterGroup(c);
        var row = new ServiceRow
        {
            ProgramGuideOrder = c.ProgramGuideOrder != 0 ? c.ProgramGuideOrder : index,
            ProgramGuideFilterGroup = filter,
            ProgramGuideFilterLabel = FirstNonEmpty(c.ProgramGuideFilterLabel, FilterLabel(filter)),
            AllocationGroup = FirstNonEmpty(c.AllocationGroup, c.TunerGroup, filter == "GR" ? "GR" : "BSCS"),
            TunerGroup = FirstNonEmpty(c.TunerGroup, c.AllocationGroup, filter == "GR" ? "GR" : "BSCS"),
            ServiceName = c.ServiceName,
            NetworkId = c.NetworkId,
            TransportStreamId = c.TransportStreamId,
            ServiceId = c.ServiceId,
            ChannelSpace = c.ChannelSpace,
            ChannelIndex = c.ChannelIndex,
            ChannelArgument = c.ChannelArgument
        };

        // official: ProgramGuideChannelのtripletが0で投影される環境に備え、
        // 同じchannel object上の互換/別名プロパティも横串で読む。
        // ここで0のままなら、ApplyNowNext()でCurrent/Nextイベント側tripletから補完する。
        ApplyTriplet(row,
            ReadInt(c, "NetworkId", "Nid", "NID", "OriginalNetworkId", "OriginalNetworkID", "Onid", "ONID"),
            ReadInt(c, "TransportStreamId", "TransportStreamID", "Tsid", "TSID"),
            ReadInt(c, "ServiceId", "ServiceID", "Sid", "SID"));
        return row;
    }

    private static void ApplyNowNext(ServiceRow row, PluginProgramGuideNowNext item)
    {
        if (item.Current != null)
        {
            // official: safe event payloadは局行から出るため、
            // Channel側が0でも現在番組イベント側にtripletがある場合はここで復元する。
            ApplyTriplet(row, item.Current.NetworkId, item.Current.TransportStreamId, item.Current.ServiceId);
            row.CurrentTitle = FirstNonEmpty(item.Current.Title, "番組情報取得中");
            row.CurrentStart = item.Current.Start;
            row.CurrentEnd = item.Current.End;
        }
        if (item.Next != null)
        {
            ApplyTriplet(row, item.Next.NetworkId, item.Next.TransportStreamId, item.Next.ServiceId);
            row.NextTitle = item.Next.Title;
            row.NextStart = item.Next.Start;
            row.NextEnd = item.Next.End;
        }
    }

    private static void ApplyTriplet(ServiceRow row, int nid, int tsid, int sid)
    {
        if (row.NetworkId <= 0 && nid > 0) row.NetworkId = nid;
        if (row.TransportStreamId <= 0 && tsid > 0) row.TransportStreamId = tsid;
        if (row.ServiceId <= 0 && sid > 0) row.ServiceId = sid;
    }

    private static int ReadInt(object? obj, params string[] names)
    {
        foreach (var name in names)
        {
            var value = ReadProperty(obj, name);
            if (value == null) continue;
            try
            {
                if (value is int i) return i;
                if (value is ushort us) return us;
                if (value is short sh) return sh;
                if (value is uint ui && ui <= int.MaxValue) return (int)ui;
                if (value is long l && l <= int.MaxValue && l >= int.MinValue) return (int)l;
                if (int.TryParse(value.ToString(), out var parsed)) return parsed;
            }
            catch { }
        }
        return 0;
    }


    private static bool ReadBoolCompat(object? obj, params string[] names)
    {
        foreach (var name in names)
        {
            var value = ReadProperty(obj, name);
            if (value == null) continue;
            if (value is bool b) return b;
            var text = value.ToString()?.Trim() ?? string.Empty;
            if (text.Equals("1", StringComparison.OrdinalIgnoreCase) || text.Equals("true", StringComparison.OrdinalIgnoreCase) || text.Equals("active", StringComparison.OrdinalIgnoreCase) || text.Equals("current", StringComparison.OrdinalIgnoreCase)) return true;
            if (text.Equals("0", StringComparison.OrdinalIgnoreCase) || text.Equals("false", StringComparison.OrdinalIgnoreCase) || text.Equals("inactive", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return false;
    }

    private static int ApplyTripletFromCurrentPrograms(IReadOnlyList<ServiceRow> rows, IReadOnlyList<PluginEpgEvent> events)
    {
        if (rows.Count == 0 || events.Count == 0) return 0;
        var restored = 0;
        var eventsByService = events
            .Where(e => !string.IsNullOrWhiteSpace(e.ServiceName) && e.NetworkId > 0 && e.TransportStreamId > 0 && e.ServiceId > 0)
            .GroupBy(e => NormalizeServiceKey(e.ServiceName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            if (row.NetworkId > 0 && row.TransportStreamId > 0 && row.ServiceId > 0) continue;
            if (!eventsByService.TryGetValue(NormalizeServiceKey(row.ServiceName), out var candidates) || candidates.Count == 0) continue;

            var selected = candidates.FirstOrDefault(e => !string.IsNullOrWhiteSpace(row.CurrentTitle) &&
                e.Title.Equals(row.CurrentTitle, StringComparison.OrdinalIgnoreCase))
                ?? candidates[0];
            var before = row.NetworkId > 0 && row.TransportStreamId > 0 && row.ServiceId > 0;
            ApplyTriplet(row, selected.NetworkId, selected.TransportStreamId, selected.ServiceId);
            var after = row.NetworkId > 0 && row.TransportStreamId > 0 && row.ServiceId > 0;
            if (!before && after) restored++;
        }
        return restored;
    }

    private static int ApplyTripletFromChannels(IReadOnlyList<ServiceRow> rows, IReadOnlyList<PluginChannelInfo> channels)
    {
        if (rows.Count == 0 || channels.Count == 0) return 0;
        var restored = 0;
        var enabledChannels = channels
            .Where(c => c.IsEnabledInUserChannelSet && c.NetworkId > 0 && c.TransportStreamId > 0 && c.ServiceId > 0 && !string.IsNullOrWhiteSpace(c.ServiceName))
            .ToList();

        var byService = enabledChannels
            .GroupBy(c => NormalizeServiceKey(c.ServiceName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            if (row.NetworkId > 0 && row.TransportStreamId > 0 && row.ServiceId > 0) continue;
            if (!byService.TryGetValue(NormalizeServiceKey(row.ServiceName), out var candidates) || candidates.Count == 0) continue;

            var selected = SelectBestChannelCandidate(row, candidates);
            var before = row.NetworkId > 0 && row.TransportStreamId > 0 && row.ServiceId > 0;
            ApplyTriplet(row, selected.NetworkId, selected.TransportStreamId, selected.ServiceId);
            if (row.ChannelSpace <= 0 && selected.ChannelSpace > 0) row.ChannelSpace = selected.ChannelSpace;
            if (row.ChannelIndex <= 0 && selected.ChannelIndex > 0) row.ChannelIndex = selected.ChannelIndex;
            if (string.IsNullOrWhiteSpace(row.ChannelArgument)) row.ChannelArgument = selected.ChannelArgument;
            if (string.IsNullOrWhiteSpace(row.AllocationGroup)) row.AllocationGroup = selected.Group.Equals("GR", StringComparison.OrdinalIgnoreCase) ? "GR" : "BSCS";
            if (string.IsNullOrWhiteSpace(row.TunerGroup)) row.TunerGroup = row.AllocationGroup;
            var after = row.NetworkId > 0 && row.TransportStreamId > 0 && row.ServiceId > 0;
            if (!before && after) restored++;
        }
        return restored;
    }

    private static PluginChannelInfo SelectBestChannelCandidate(ServiceRow row, IReadOnlyList<PluginChannelInfo> candidates)
    {
        if (candidates.Count == 1) return candidates[0];
        var allocationGroup = FirstNonEmpty(row.AllocationGroup, row.TunerGroup, row.ProgramGuideFilterGroup == "GR" ? "GR" : "BSCS");
        var byGroup = candidates.FirstOrDefault(c =>
            c.Group.Equals(row.ProgramGuideFilterGroup, StringComparison.OrdinalIgnoreCase)
            || c.Group.Equals(allocationGroup, StringComparison.OrdinalIgnoreCase)
            || (allocationGroup.Equals("BSCS", StringComparison.OrdinalIgnoreCase) && !c.Group.Equals("GR", StringComparison.OrdinalIgnoreCase)));
        if (byGroup != null) return byGroup;
        if (row.ChannelSpace > 0 || row.ChannelIndex > 0)
        {
            var byChannel = candidates.FirstOrDefault(c =>
                (row.ChannelSpace <= 0 || c.ChannelSpace == row.ChannelSpace)
                && (row.ChannelIndex <= 0 || c.ChannelIndex == row.ChannelIndex));
            if (byChannel != null) return byChannel;
        }
        return candidates[0];
    }

    private static string NormalizeServiceKey(string? value)
        => new string((value ?? string.Empty).Where(c => !char.IsWhiteSpace(c) && c != '　').ToArray()).Trim();

    private static ViewerSessionRow FromViewerSession(PluginViewerSessionInfo s)
    {
        var current = ReadBoolCompat(s, "Current", "IsCurrent", "Active", "IsActive");
        var state = FirstNonEmpty(ReadString(s, "ViewerState"), s.State, s.LaunchResult);
        return new(
            s.LeaseId,
            ViewerSessionServiceName(s),
            NormalizeFilter(FirstNonEmpty(s.ProgramGuideFilterGroup, s.DisplayGroup, s.AllocationGroup)),
            s.AllocationGroup,
            s.TunerGroup,
            s.TunerName,
            s.Did,
            s.SlotIndex,
            s.NetworkId,
            s.TransportStreamId,
            s.ServiceId,
            current,
            state,
            FirstNonEmpty(ReadString(s, "ViewerProfile", "viewerProfile"), "auto"),
            FirstNonEmpty(ReadString(s, "ViewerProfileName", "viewerProfileName"), ReadString(s, "ViewerProfile", "viewerProfile")),
            ReadString(s, "TvTestPathKey", "tvTestPathKey"));
    }

    private static string ViewerSessionServiceName(PluginViewerSessionInfo s)
        => FirstNonEmpty(ReadString(s, "ServiceName", "ResolvedServiceName", "ViewerServiceName", "CurrentServiceName"));

    private ViewerOperation CaptureAction(PluginUiContext c)
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

    private WindowOperation CaptureWindow(PluginUiContext c, bool isToolWindow)
    {
        var endpoint = FirstNonEmpty(ReadString(c, "WindowEndpoint"), "/api/plugins/window");
        var route = FirstNonEmpty(ReadString(c, "WindowRoute"), "/plugin-window");
        var method = FirstNonEmpty(ReadString(c, "WindowMethod"), "POST");
        var token = ReadString(c, "WindowToken");
        var pluginId = FirstNonEmpty(ReadString(c, "PluginId"), PluginId);
        var routeSegment = FirstNonEmpty(ReadString(c, "RouteSegment"), RouteSegment);
        var windowId = FirstNonEmpty(ReadString(c, "CurrentWindowId", "WindowId"), QueryWindowId(c));
        var supported = ReadStringList(c, "SupportedWindowActions");
        var capabilities = ReadStringDictionary(c, "ToolWindowCapabilities");
        var contract = ReadStringDictionary(c, "WindowContract");
        var modes = FirstNonEmpty(GetValue(capabilities, "openWindowModes"), GetValue(contract, "openWindowModes"));
        var toolWindowSupported = ReadDictBool(capabilities, contract, "toolWindowSupported") || modes.Contains("toolWindow", StringComparison.OrdinalIgnoreCase);
        var canOpen = method.Equals("POST", StringComparison.OrdinalIgnoreCase) && !isToolWindow && !string.IsNullOrWhiteSpace(endpoint);
        var canRefresh = method.Equals("POST", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(endpoint)
            && (supported.Count == 0 || supported.Any(x => x.Equals("refreshWindow", StringComparison.OrdinalIgnoreCase)) || supported.Any(x => x.Equals("rerenderWindow", StringComparison.OrdinalIgnoreCase)));
        var stateEndpoint = ResolveWindowStateEndpoint(c, contract, windowId);
        return new WindowOperation(canOpen, canRefresh && !string.IsNullOrWhiteSpace(windowId), endpoint, route, method, token, pluginId, routeSegment, windowId, stateEndpoint, toolWindowSupported);
    }

    private static string ResolveWindowStateEndpoint(PluginUiContext c, IReadOnlyDictionary<string, string> contract, string windowId)
    {
        // Compatibility only: official no longer calls this endpoint from RenderHtml.
        // TvAIr v0.11.13 supplies direct CurrentWindowAlwaysOnTop state instead.
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

    private bool ResolveWindowAlwaysOnTop(PluginUiContext context, WindowOperation window, bool isToolWindow)
    {
        if (!isToolWindow || string.IsNullOrWhiteSpace(window.WindowId)) return false;

        // TvAIr v0.11.13 contract: RenderHtml must not synchronously call WindowStateUrl.
        // The host injects the current tool-window state directly into PluginUiContext / WindowContract.
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

    private static string BuildLauncherHtml(FloatingViewerData data, WindowOperation window, string filter, string selectedTuner, string selectedViewerProfile, bool alwaysOnTop)
    {
        var openForm = BuildOpenWindowForm(window, alwaysOnTop, filter, selectedTuner, selectedViewerProfile);
        var viewerText = data.ViewerSessions.Count == 0
            ? "視聴中なし"
            : string.Join(" / ", data.ViewerSessions.Select(x => FirstNonEmpty(x.TunerName, "-") + "-" + FirstNonEmpty(x.Did, "-")));
        return $$"""
<!doctype html>
<meta charset="utf-8">
<style>
{{BuildLauncherCss()}}
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

    private static string BuildLauncherCss()
    {
        return @"html,body{margin:0;background:#0f1822;color:#e8f2fb;font-family:Meiryo,""Yu Gothic"",Arial,sans-serif;font-size:13px;}
.aircon-launch{padding:12px;}
.aircon-card{width:260px;max-width:100%;background:#162433;border:1px solid #2d4a63;box-shadow:0 1px 3px rgba(0,0,0,.25);}
.aircon-head{background:#253947;color:#fff;padding:6px 9px;font-weight:bold;}
.aircon-body{padding:10px;line-height:1.35;}
.aircon-open{font-family:inherit;border:1px solid #6fa1c5;background:#203850;color:#fff;border-radius:3px;padding:5px 11px;font-weight:bold;cursor:pointer;}
.aircon-open:hover{background:#2b4a68;}
.aircon-note,.aircon-status{display:none;}";
    }

    private static string BuildFloatingViewerHtml(FloatingViewerData data, ViewerOperation action, WindowOperation window, string filter, string selectedTunerValue, ViewerProfileChoice selectedViewerProfile, bool alwaysOnTop)
    {
        var tunerChoices = BuildTunerChoices(data.ViewerTuners, filter).ToList();
        var selected = ResolveSelectedTuner(tunerChoices, selectedTunerValue);
        var rows = BuildRows(data.Services, action, window, selected, selectedViewerProfile);
        var toolbar = BuildToolbar(data.WaveFilters, data.ViewerSessions, data.ViewerProfiles, action, window, filter, selected.Value, selectedViewerProfile, alwaysOnTop);
        return $$"""
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<style>
{{BuildToolWindowCss()}}
</style>
</head>
<body>
<div class="aircon-float">
  {{toolbar}}
  <div class="aircon-list">
    {{rows}}
  </div>
</div>
</body>
</html>
""";
    }

    private static string BuildToolWindowCss()
    {
        var toolbarHeight = ToolWindowToolbarHeightPx;
        var toolbarContentTop = ToolWindowToolbarContentTopPx;
        var toolbarPaddingX = ToolWindowToolbarPaddingXPx;
        var toolbarCellGap = ToolWindowToolbarCellGapPx;
        var toolbarGroupGap = ToolWindowToolbarGroupGapPx;
        var toolbarLabelPaddingRight = ToolWindowToolbarLabelPaddingRightPx;
        var waveButtonWidth = ToolWindowWaveButtonWidthPx;
        var waveAreaWidth = ToolWindowWaveAreaWidthPx;
        var viewerProfileLabelWidth = ToolWindowViewerProfileLabelWidthPx;
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
        var serviceWidth = ToolWindowServiceColumnWidthPx;
        
        return $$"""
html,body{margin:0;padding:0;width:100%;height:100%;background:#eef4f8;color:#102334;font-family:Meiryo,"Yu Gothic",Arial,sans-serif;font-size:12px;overflow:hidden;}
body{position:static;}
*{box-sizing:border-box;}
.aircon-float{position:static;width:100%;height:100%;background:#eef4f8;overflow:hidden;}
.aircon-toolbar{position:fixed;left:0;right:0;top:0;height:{{toolbarHeight}}px;padding:0;border-bottom:{{ToolWindowToolbarSeparatorPx}}px solid #8fb2c9;background:#d6e8f4;white-space:nowrap;overflow:hidden;text-align:left;z-index:2;}
.aircon-toolbar-inner{position:absolute;left:{{toolbarPaddingX}}px;right:{{toolbarPaddingX}}px;top:{{toolbarContentTop}}px;height:{{buttonHeight}}px;overflow:hidden;white-space:nowrap;}
.aircon-toolbar-wave-area{position:absolute;left:0;top:0;width:{{waveAreaWidth}}px;height:{{cellHeight}}px;line-height:{{cellHeight}}px;white-space:nowrap;overflow:hidden;}
.aircon-toolbar-profile-slot{position:absolute;left:{{waveAreaWidth + toolbarGroupGap}}px;right:{{actionButtonGroupWidth + toolbarGroupGap}}px;top:0;width:auto;height:{{cellHeight}}px;line-height:{{cellHeight}}px;white-space:nowrap;overflow:hidden;}
.aircon-toolbar-profile-slot-reserved{visibility:hidden;}
.aircon-toolbar-actions{position:absolute;right:0;top:0;width:{{actionButtonGroupWidth}}px;height:{{cellHeight}}px;line-height:{{cellHeight}}px;white-space:nowrap;text-align:right;overflow:visible;}
.aircon-toolbar-label{display:inline-block;height:{{cellHeight}}px;line-height:{{cellHeight}}px;margin:0;padding:0 {{toolbarLabelPaddingRight}}px 0 0;color:#27465a;font-size:11px;font-weight:bold;vertical-align:top;white-space:nowrap;}
.aircon-wave-group{display:inline-block;margin:0;padding:0;width:{{waveButtonGroupWidth}}px;height:{{cellHeight}}px;line-height:{{cellHeight}}px;white-space:nowrap;vertical-align:top;overflow:visible;}
.aircon-nav-form,.aircon-action-form,.aircon-profile-form{display:inline-block;margin:0;padding:0;height:{{cellHeight}}px;line-height:{{cellHeight}}px;white-space:nowrap;vertical-align:top;}
.aircon-toolbar-actions .aircon-action-form{width:{{actionButtonSize}}px;}
.aircon-wave-group .aircon-nav-form{margin:0 0 0 -{{waveButtonOverlap}}px;}
.aircon-wave-group .aircon-nav-form:first-child{margin-left:0;}
.aircon-toolbar-actions .aircon-action-form{margin:0 0 0 {{toolbarCellGap}}px;}
.aircon-toolbar-actions .aircon-action-form:first-child{margin-left:0;}
.aircon-toolbar-actions .aircon-toolbar-button-disabled{margin-left:{{toolbarCellGap}}px;}
.aircon-toolbar-actions .aircon-toolbar-button-disabled:first-child{margin-left:0;}
.aircon-toolbar-button,.aircon-toolbar-select{display:inline-block;height:{{buttonHeight}}px;line-height:{{buttonLineHeight}}px;padding:0 6px;margin:0;border:{{buttonBorder}}px solid #6d94ad;background:#f8fbfd;color:#102f46;border-radius:2px;font-size:11px;font-family:inherit;font-weight:bold;text-align:center;cursor:pointer;vertical-align:top;white-space:nowrap;text-decoration:none;}
.aircon-toolbar-button:hover,.aircon-toolbar-select:hover{background:#e5f2fb;}
.aircon-profile-label{display:inline-block;width:{{viewerProfileLabelWidth}}px;height:{{cellHeight}}px;line-height:{{cellHeight}}px;margin:0;padding:0 {{toolbarLabelPaddingRight}}px 0 0;color:#27465a;font-size:11px;font-weight:bold;vertical-align:top;white-space:nowrap;overflow:hidden;text-align:right;}
.aircon-profile-segments{display:inline-block;width:auto;height:{{cellHeight}}px;line-height:{{cellHeight}}px;white-space:nowrap;overflow:visible;vertical-align:top;}
.aircon-profile-segment-form{display:inline-block;margin:0 {{toolbarCellGap}}px 0 0;padding:0;width:{{viewerProfileButtonWidth}}px;height:{{cellHeight}}px;line-height:{{cellHeight}}px;vertical-align:top;white-space:nowrap;overflow:visible;}
.aircon-profile-button{width:{{viewerProfileButtonWidth}}px;min-width:{{viewerProfileButtonWidth}}px;max-width:{{viewerProfileButtonWidth}}px;height:{{buttonHeight}}px;line-height:{{buttonLineHeight}}px;padding:0;text-align:center;}
.aircon-profile-button-on{background:#245b80;color:#fff;border-color:#6d94ad;}
.aircon-profile-button-disabled{opacity:.45;cursor:default;background:#edf2f5;color:#708190;}
.aircon-toolbar-button-disabled{opacity:.70;cursor:default;background:#edf2f5;color:#708190;}
.aircon-wave-button{width:{{waveButtonWidth}}px;min-width:{{waveButtonWidth}}px;border-radius:0;background:#f8fbfd;border-color:#6d94ad;color:#102f46;}
.aircon-wave-button-on{background:#245b80;color:#fff;border-color:#6d94ad;}
.aircon-wave-button-disabled,.aircon-wave-button-disabled:hover{background:#edf2f5;color:#8a98a3;border-color:#b6c4ce;cursor:default;}
.aircon-action-button{width:{{actionButtonSize}}px;min-width:{{actionButtonSize}}px;max-width:{{actionButtonSize}}px;padding:0;font-size:15px;font-family:"Segoe UI Symbol","Meiryo","Yu Gothic",Arial,sans-serif;font-weight:bold;line-height:{{buttonLineHeight}}px;text-align:center;}
.aircon-action-refresh{background:#edf8ef;border-color:#5fa872;color:#174820;}
.aircon-action-refresh:hover{background:#dff2e3;}
.aircon-action-power{background:#faeeee;border-color:#c86a6a;color:#7b1f1f;}
.aircon-action-power:hover{background:#f5dddd;}
.aircon-action-power.aircon-toolbar-button-disabled{background:#f2e6e6;border-color:#cfa0a0;color:#8a6868;}
.aircon-action-topmost{background:#eceff2;border-color:#9aa7b0;color:#3f4b54;}
.aircon-action-topmost:hover{background:#e1e7ec;}
.aircon-action-topmost-on{background:#cf4b4b;border-color:#9b2222;color:#ffffff;}
.aircon-action-topmost-on:hover{background:#bd3838;}
.aircon-action-topmost.aircon-toolbar-button-disabled{background:#edf0f2;border-color:#c0c8ce;color:#87939b;}
.aircon-list{position:fixed;left:0;right:0;top:{{listTop}}px;bottom:0;background:#fff;overflow-x:hidden;overflow-y:scroll;width:100%;height:auto;}
.aircon-row{display:block;width:100%;margin:0;padding:0 8px;border:0;border-bottom:1px solid #d5e2ec;background:#ffffff;cursor:pointer;font-family:inherit;text-align:left;color:#102334;height:{{rowHeight}}px;line-height:{{rowHeight}}px;white-space:nowrap;overflow:hidden;}
.aircon-row-even{background:#f2f8fc;}
.aircon-row-odd{background:#ffffff;}
.aircon-row:hover{background:#eaf5fd;}
.aircon-row:focus,.aircon-row:active{background:#fff2bd;outline:none;}
.aircon-row-disabled,.aircon-row-disabled:hover{cursor:default;color:#657681;background:#eef2f5;}
.aircon-row-viewing-selected{background:#fff2bd;cursor:pointer;}
.aircon-row-viewing-other{background:#e7f2fb;cursor:pointer;}
.aircon-row-viewing-selected:hover{background:#ffe9a2;}
.aircon-row-viewing-other:hover{background:#dcecf7;}
.aircon-row span{cursor:inherit;}
.aircon-viewer-badge{float:left;display:block;width:22px;height:{{rowHeight}}px;line-height:{{rowHeight}}px;margin:0;padding:0;text-align:center;font-size:11px;font-weight:bold;color:#245b80;white-space:nowrap;overflow:hidden;}
.aircon-viewer-badge-on{color:#fff;background:#245b80;border-radius:2px;height:18px;line-height:18px;margin-top:6px;}
.aircon-viewer-badge-other{color:#245b80;background:#d5e9f6;border:1px solid #8fb2c9;border-radius:2px;height:18px;line-height:16px;margin-top:6px;}
.aircon-service{float:left;display:block;width:{{serviceWidth - 22}}px;height:{{rowHeight}}px;line-height:{{rowHeight}}px;vertical-align:top;color:#07344f;font-weight:bold;font-size:12px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;}
.aircon-current{display:block;width:auto;margin-left:{{serviceWidth}}px;height:{{rowHeight}}px;line-height:{{rowHeight}}px;vertical-align:top;color:#071522;font-size:12px;font-weight:bold;white-space:nowrap;overflow:hidden;text-overflow:clip;}
.aircon-scroll-anchor{display:block;width:100%;height:1px;line-height:1px;font-size:0;overflow:hidden;margin:0;padding:0;}
.aircon-empty{padding:16px;color:#60778a;}
""";
    }

    private static string BuildToolbar(IReadOnlyList<WaveFilterRow> waveFilters, IReadOnlyList<ViewerSessionRow> viewerSessions, ViewerProfileState viewerProfiles, ViewerOperation action, WindowOperation window, string filter, string selectedTuner, ViewerProfileChoice selectedViewerProfile, bool alwaysOnTop)
    {
        var filterForms = new List<string>();
        foreach (var f in CanonicalWaveFilters(waveFilters))
        {
            filterForms.Add(BuildFilterForm(f.Group, f.Label, filter, selectedTuner, selectedViewerProfile, alwaysOnTop, window));
        }

        var refresh = BuildRefreshForm(window, filter, selectedViewerProfile.Value);
        var selectedProfileSession = ResolveSelectedProfileSession(viewerSessions, selectedViewerProfile.Value);
        var power = BuildToolbarStopForm(selectedProfileSession, action, window, filter, selectedViewerProfile.Value);
        var pin = BuildPinForm(window, !alwaysOnTop, filter, selectedTuner, selectedViewerProfile.Value, alwaysOnTop);

        var waveGroup =
            "<div class='aircon-toolbar-wave-area' data-role='wave-selector-group'>" +
            "<span class='aircon-toolbar-label'>放送波：</span>" +
            "<span class='aircon-wave-group' role='group' aria-label='放送波'>" + string.Join("", filterForms) + "</span>" +
            "</div>";

        var viewerProfileGroup = BuildViewerProfileSelector(viewerProfiles, window, filter, selectedTuner, selectedViewerProfile.Value, alwaysOnTop);

        var actionGroup =
            "<div class='aircon-toolbar-actions' data-role='viewer-and-window-actions'>" +
            refresh + power + pin +
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

    private static string BuildViewerProfileSelector(ViewerProfileState state, WindowOperation window, string filter, string selectedTuner, string selectedViewerProfile, bool alwaysOnTop)
    {
        if (!state.SelectorVisibleRecommended)
        {
            return "<div class='aircon-toolbar-profile-slot aircon-toolbar-profile-slot-reserved' data-role='viewer-profile-reserved'></div>";
        }

        var requiredGroup = RequiredProfileGroupForWave(filter);
        var buttons = new List<string>();
        foreach (var p in state.SelectableProfiles)
        {
            var unavailable = !p.Enabled || !p.IsAvailableForWave(filter);
            var active = p.Id.Equals(selectedViewerProfile, StringComparison.OrdinalIgnoreCase);
            var groups = p.AvailableGroups == null || p.AvailableGroups.Count == 0 ? "ALL" : string.Join(",", p.AvailableGroups);
            var displayLabel = ViewerProfileSegmentLabel(p);
            var title = unavailable ? p.Name + "（" + requiredGroup + "では利用不可）" : p.Name;
            var cls = "aircon-toolbar-button aircon-profile-button" + (active ? " aircon-profile-button-on" : string.Empty) + (unavailable ? " aircon-profile-button-disabled" : string.Empty);

            if (unavailable)
            {
                buttons.Add("<button class=\"" + cls + "\" type=\"button\" disabled aria-disabled=\"true\" data-role=\"viewer-profile-option\" data-viewer-profile=\"" + HtmlAttr(p.Id) + "\" data-groups=\"" + HtmlAttr(groups) + "\" title=\"" + HtmlAttr(title) + "\">" + Html(displayLabel) + "</button>");
                continue;
            }

            var fields = ToolContentFields(filter, selectedTuner, p.Id, alwaysOnTop, window);
            fields["viewerProfile"] = p.Id;
            fields["viewer-profile"] = p.Id;
            fields["viewerProfileId"] = p.Id;
            fields["viewer_profile"] = p.Id;
            fields["selectedViewerProfile"] = p.Id;
            buttons.Add("<form class=\"aircon-profile-segment-form\" method=\"get\" action=\"/plugin/aircon\" data-role=\"viewer-profile-selector\" data-viewer-profile=\"" + HtmlAttr(p.Id) + "\" data-groups=\"" + HtmlAttr(groups) + "\">" +
                HiddenInputs(fields) +
                "<button class=\"" + cls + "\" type=\"submit\" data-role=\"viewer-profile-option\" data-viewer-profile=\"" + HtmlAttr(p.Id) + "\" aria-pressed=\"" + (active ? BoolTrue : BoolFalse) + "\" title=\"" + HtmlAttr(title) + "\">" + Html(displayLabel) + "</button></form>");
        }

        if (buttons.Count == 0)
        {
            buttons.Add("<button class=\"aircon-toolbar-button aircon-profile-button aircon-profile-button-disabled\" type=\"button\" disabled aria-disabled=\"true\" title=\"視聴先候補なし\">-</button>");
        }

        return "<div class='aircon-toolbar-profile-slot aircon-profile-form' data-role='viewer-profile-selector'" +
            " data-tvair-current-viewer-profile='" + HtmlAttr(selectedViewerProfile) + "'" +
            " data-tvair-profile-count='" + state.SelectableProfiles.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "'>" +
            "<span class='aircon-profile-label'>TVTest:</span>" +
            "<span class='aircon-profile-segments' role='group' aria-label='TVTest'>" + string.Join("", buttons) + "</span>" +
            "</div>";
    }

    private static string ViewerProfileSegmentLabel(ViewerProfileChoice profile)
    {
        var id = profile.Id ?? string.Empty;
        const string prefix = "tvtest";
        if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var suffix = id.Substring(prefix.Length).Trim();
            if (!string.IsNullOrWhiteSpace(suffix) && suffix.All(char.IsDigit)) return suffix;
        }

        var name = profile.Name ?? string.Empty;
        if (name.StartsWith("TVTest", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = name.Substring("TVTest".Length).Trim();
            if (!string.IsNullOrWhiteSpace(suffix) && suffix.All(char.IsDigit)) return suffix;
        }

        return string.IsNullOrWhiteSpace(profile.Name) ? profile.Id : profile.Name;
    }

private static IReadOnlyList<WaveFilterRow> CanonicalWaveFilters(IReadOnlyList<WaveFilterRow> source)
    {
        static string LabelFor(string group) => group.Equals("GR", StringComparison.OrdinalIgnoreCase) ? "地上波" : group.ToUpperInvariant();
        return new[] { "GR", "BS", "CS" }
            .Select(group => new WaveFilterRow(group, group, LabelFor(group)))
            .ToArray();
    }

    private static string BuildRows(IReadOnlyList<ServiceRow> services, ViewerOperation action, WindowOperation window, TunerChoice selectedTuner, ViewerProfileChoice selectedViewerProfile)
    {
        if (services.Count == 0) return "<div class=\"aircon-empty\">表示対象の局がありません。</div>";
        var parts = new List<string>();
        var rowIndex = 0;
        foreach (var row in services)
        {
            // official: wave is already represented by the active toolbar button.
            // Do not render an additional GR/BS/CS section band inside the scroll area.
            parts.Add(BuildServiceRow(row, action, window, selectedTuner, selectedViewerProfile, rowIndex++));
        }
        return string.Join("", parts);
    }

    private static string BuildServiceRow(ServiceRow row, ViewerOperation action, WindowOperation window, TunerChoice selectedTuner, ViewerProfileChoice selectedViewerProfile, int rowIndex)
    {
        var hasTriplet = HasResolvedTriplet(row);
        var attrs = hasTriplet ? BuildFloatingViewerActionAttributes(row, action, window, selectedTuner, selectedViewerProfile) : string.Empty;
        var parityClass = (rowIndex % 2 == 0) ? "aircon-row-even" : "aircon-row-odd";
        var isSelectedProfileViewing = row.IsViewing && (string.IsNullOrWhiteSpace(row.ViewingViewerProfile) || row.ViewingViewerProfile.Equals(selectedViewerProfile.Value, StringComparison.OrdinalIgnoreCase));
        var isOtherProfileViewing = row.IsViewing && !isSelectedProfileViewing;
        var cls = isSelectedProfileViewing
            ? "aircon-row aircon-row-viewing-selected"
            : isOtherProfileViewing
                ? "aircon-row aircon-row-viewing-other"
                : hasTriplet ? "aircon-row " + parityClass : "aircon-row aircon-row-disabled";
        var title = hasTriplet ? "ダブルクリックで視聴" : "NID/TSID/SID未解決のため視聴操作は無効";
        var current = string.IsNullOrWhiteSpace(row.CurrentTitle) ? "番組情報取得中" : row.CurrentTitle;
        var currentClass = "aircon-current";
        var currentTitleAttr = " title=\"" + HtmlAttr(current) + "\"";
        var serviceDomId = hasTriplet ? BuildServiceDomId(row) : string.Empty;
        var rowId = hasTriplet
            ? " id=\"" + HtmlAttr(row.IsViewing ? CurrentViewingAnchorId : serviceDomId) + "\""
            : string.Empty;
        var serviceDataId = hasTriplet ? " data-aircon-service-id=\"" + HtmlAttr(serviceDomId) + "\"" : string.Empty;
        var content =
            $"<span class=\"aircon-service\">{Html(row.ServiceName)}</span>" +
            $"<span class=\"{currentClass}\"{currentTitleAttr}>{Html(current)}</span>";

        if (!hasTriplet)
        {
            return $"<div class=\"{cls}\" title=\"{HtmlAttr(title)}\">" + content + "</div>";
        }

        return $"<div{rowId}{serviceDataId} class=\"{cls}\" title=\"{HtmlAttr(title)}\" {attrs}>" + content + "</div>";
    }
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
            + "&viewerProfile=" + Url(selectedViewerProfile)
            + "&focusNid=" + row.NetworkId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "&focusTsid=" + row.TransportStreamId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "&focusSid=" + row.ServiceId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

private static string BuildFloatingViewerActionAttributes(ServiceRow row, ViewerOperation action, WindowOperation window, TunerChoice selectedTuner, ViewerProfileChoice selectedViewerProfile)
    {
        if (!action.CanPost || string.IsNullOrWhiteSpace(window.WindowId) || !HasResolvedTriplet(row)) return string.Empty;
        var payload = BuildViewerStartPayload(row, selectedTuner, selectedViewerProfile);
        var fields = new Dictionary<string, string?>
        {
            ["safe-event"] = "true",
            ["event"] = "dblclick",
            ["action"] = ActionViewerStart,
            ["plugin-id"] = action.PluginId,
            ["route-segment"] = action.RouteSegment,
            ["token"] = action.ActionToken,
            ["method"] = action.ActionMethod,
            ["endpoint"] = action.ActionEndpoint,
            ["response-mode"] = ResponseModeHostHandled,
            ["refresh-target"] = RefreshTargetContent,
            ["refresh-after"] = BoolTrue,
            ["refresh-scroll-target"] = CurrentViewingAnchorId,
            ["refresh-scroll-mode"] = RefreshScrollModeCenter,
            ["scroll-target"] = CurrentViewingAnchorId,
            ["focus-target"] = CurrentViewingAnchorId,
            ["refresh-query"] = BuildViewerRefreshQuery(row, selectedViewerProfile.Value),
            ["focusNid"] = payload.NetworkId,
            ["focusTsid"] = payload.TransportStreamId,
            ["focusSid"] = payload.ServiceId,
            ["payload-focusNid"] = payload.NetworkId,
            ["payload-focusTsid"] = payload.TransportStreamId,
            ["payload-focusSid"] = payload.ServiceId,
            ["window-id"] = window.WindowId,
            ["payload-networkId"] = payload.NetworkId,
            ["payload-transportStreamId"] = payload.TransportStreamId,
            ["payload-serviceId"] = payload.ServiceId,
            ["payload-channelSpace"] = payload.ChannelSpace,
            ["payload-channelIndex"] = payload.ChannelIndex,
            ["payload-channelArgument"] = payload.ChannelArgument,
            ["payload-broadcastGroup"] = payload.BroadcastGroup,
            ["payload-viewerProfile"] = payload.ViewerProfile,
            ["payload-viewer-profile"] = payload.ViewerProfile,
            ["payload-viewerProfileName"] = payload.ViewerProfileName
        };
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
            return "<button class=\"aircon-toolbar-button aircon-action-button aircon-action-topmost aircon-toolbar-button-disabled\" type=\"button\" aria-disabled=\"true\" aria-label=\"前面固定\" title=\"AIrConを常に前面\">⚑</button>";
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
            ["refreshScrollTarget"] = CurrentViewingAnchorId,
            ["refresh-scroll-target"] = CurrentViewingAnchorId,
            ["refreshScrollMode"] = RefreshScrollModeCenter,
            ["refresh-scroll-mode"] = RefreshScrollModeCenter,
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

    private static string BuildToolbarStopForm(ViewerSessionRow? session, ViewerOperation action, WindowOperation window, string filter, string selectedViewerProfile)
    {
        if (!action.CanPost || string.IsNullOrWhiteSpace(window.WindowId))
        {
            return "<button class=\"aircon-toolbar-button aircon-action-button aircon-action-power aircon-toolbar-button-disabled\" type=\"button\" aria-disabled=\"true\" aria-label=\"視聴停止\" title=\"視聴停止契約待ち\">⏻</button>";
        }

        if (session == null || string.IsNullOrWhiteSpace(session.LeaseId) || !session.ViewerProfile.Equals(selectedViewerProfile, StringComparison.OrdinalIgnoreCase))
        {
            var disabledTitle = string.IsNullOrWhiteSpace(selectedViewerProfile)
                ? "停止対象のTVTestが選択されていません"
                : "選択中TVTest(" + selectedViewerProfile + ")の視聴セッションがありません";
            return "<button class=\"aircon-toolbar-button aircon-action-button aircon-action-power aircon-toolbar-button-disabled\" type=\"button\" aria-disabled=\"true\" aria-label=\"視聴停止\" title=\"" + HtmlAttr(disabledTitle) + "\">⏻</button>";
        }

        // viewerStop is intentionally scoped to the selected viewerProfile.
        // Do not fall back to another profile's lease, and do not send lease-less current-client stop.
        var clientId = window.PluginId + ":viewer:" + selectedViewerProfile;
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
            ["safeEvent"] = "true",
            ["safe-event"] = "true",
            ["action"] = ActionViewerStop,
            ["safeEventAction"] = ActionViewerStop,
            ["safe-event-action"] = "viewerStop",
            ["token"] = action.ActionToken,
            ["actionToken"] = action.ActionToken,
            ["action-token"] = action.ActionToken,
            ["leaseId"] = session?.LeaseId ?? string.Empty,
            ["lease-id"] = session?.LeaseId ?? string.Empty,
            ["payload-leaseId"] = session?.LeaseId ?? string.Empty,
            ["payload-lease-id"] = session?.LeaseId ?? string.Empty,
            ["responseMode"] = ResponseModeHostHandled,
            ["response-mode"] = ResponseModeHostHandled,
            ["windowId"] = window.WindowId,
            ["window-id"] = window.WindowId,
            ["currentWindowId"] = window.WindowId,
            ["current-window-id"] = window.WindowId,
            ["preserveScroll"] = BoolTrue,
            ["preserve-scroll"] = "true",
            ["refreshScrollTarget"] = CurrentViewingAnchorId,
            ["refresh-scroll-target"] = CurrentViewingAnchorId,
            ["refreshScrollMode"] = RefreshScrollModeCenter,
            ["refresh-scroll-mode"] = RefreshScrollModeCenter,
            ["wave"] = filter,
            ["currentWave"] = filter,
            ["viewerProfile"] = selectedViewerProfile,
            ["viewer-profile"] = selectedViewerProfile,
            ["refreshQuery"] = "wave=" + filter + "&viewerProfile=" + selectedViewerProfile,
            ["clientVersion"] = ClientVersion
        };
        return "<form class=\"aircon-action-form\" method=\"" + HtmlAttr(action.ActionMethod) + "\" action=\"" + HtmlAttr(action.ActionEndpoint) + "\">" +
            HiddenInputs(fields) +
            "<button class=\"aircon-toolbar-button aircon-action-button aircon-action-power\" type=\"submit\" aria-label=\"視聴停止\" title=\"AIrCon管理の現在の視聴TVTestを閉じる\">⏻</button></form>";
    }

    private static string BuildOpenWindowForm(WindowOperation window, bool alwaysOnTop, string filter, string selectedTuner, string selectedViewerProfile)
    {
        if (!window.CanOpen) return "<span>ToolWindow契約待ち</span>";
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
            ["title"] = PluginListTitle,
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
            ["refreshScrollTarget"] = CurrentViewingAnchorId,
            ["refresh-scroll-target"] = CurrentViewingAnchorId,
            ["refreshScrollMode"] = RefreshScrollModeCenter,
            ["refresh-scroll-mode"] = RefreshScrollModeCenter,
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

    private static string BuildFilterForm(string group, string label, string current, string selectedTuner, ViewerProfileChoice selectedViewerProfile, bool top, WindowOperation window)
    {
        var isCurrent = group.Equals(current, StringComparison.OrdinalIgnoreCase);
        var unavailable = !selectedViewerProfile.IsAvailableForWave(group);
        var cls = isCurrent ? "aircon-toolbar-button aircon-wave-button aircon-wave-button-on" : "aircon-toolbar-button aircon-wave-button";
        if (unavailable) cls += " aircon-wave-button-disabled";
        var title = unavailable
            ? "TVTest" + ViewerProfileSegmentLabel(selectedViewerProfile) + " では " + label + " を利用できません"
            : "放送波: " + label;
        if (unavailable)
        {
            return "<form class=\"aircon-nav-form\" method=\"get\" action=\"/plugin/aircon\" data-role=\"wave-selector\" data-wave=\"" + HtmlAttr(group) + "\" data-disabled=\"true\">" +
                $"<button class=\"{cls}\" type=\"button\" disabled aria-disabled=\"true\" data-role=\"wave-selector\" data-wave=\"{HtmlAttr(group)}\" aria-pressed=\"{(isCurrent ? "true" : "false")}\" title=\"{HtmlAttr(title)}\">{Html(label)}</button></form>";
        }
        var fields = ToolContentFields(group, selectedTuner, selectedViewerProfile.Value, top, window);
        return "<form class=\"aircon-nav-form\" method=\"get\" action=\"/plugin/aircon\" data-role=\"wave-selector\" data-wave=\"" + HtmlAttr(group) + "\">" +
            HiddenInputs(fields) +
            $"<button class=\"{cls}\" type=\"submit\" data-role=\"wave-selector\" data-wave=\"{HtmlAttr(group)}\" aria-pressed=\"{(isCurrent ? "true" : "false")}\" title=\"{HtmlAttr(title)}\">{Html(label)}</button></form>";
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

    private static bool IsToolWindow(PluginUiContext c)
    {
        // ToolWindow判定はTvAIr本体から渡るhost-managed contextだけを正とする。
        // __tvairToolHost系クエリは通常ブラウザ側のreturnUrlへ混入し得るため、
        // それ単体でToolWindow扱いにしない。
        return c.IsHostManagedWindowContent || !string.IsNullOrWhiteSpace(c.CurrentWindowId) || !string.IsNullOrWhiteSpace(c.WindowId);
    }

    private static string QueryWindowId(PluginUiContext c)
    {
        var q = ExtractQueryDictionary(c);
        foreach (var key in new[] { "__tvairWindowId", "_tvairWindowId", "currentWindowId", "windowId" })
            if (q.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
        return string.Empty;
    }

    private static Dictionary<string, string> ExtractQueryDictionary(PluginUiContext c)
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

    private void SafeLog(string message)
    {
        try { _context?.Log(PluginLogLevel.Info, "AIrCon: " + message); } catch { }
    }

    private sealed record FocusTriplet(int? NetworkId, int? TransportStreamId, int? ServiceId)
    {
        public bool IsResolved => NetworkId.GetValueOrDefault() > 0 && TransportStreamId.GetValueOrDefault() > 0 && ServiceId.GetValueOrDefault() > 0;
        public string ToLogString() => IsResolved
            ? NetworkId!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + TransportStreamId!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + ServiceId!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "none";
    }

    private sealed record ViewerStartPayload(string NetworkId, string TransportStreamId, string ServiceId, string ChannelSpace, string ChannelIndex, string ChannelArgument, string ProgramGuideFilterGroup, string BroadcastGroup, string AllocationGroup, string TunerGroup, string ServiceName, string PreferredTunerName, string PreferredDid, string PreferredSlot, string ViewerProfile, string ViewerProfileName);
    private sealed record FloatingViewerData(IReadOnlyList<ServiceRow> Services, IReadOnlyList<ViewerSessionRow> ViewerSessions, IReadOnlyList<ViewerTunerRow> ViewerTuners, IReadOnlyList<WaveFilterRow> WaveFilters, ViewerProfileState ViewerProfiles, IReadOnlyList<string> Diagnostics, bool ProjectionUsed, bool SafeEventContractAvailable, bool SafeDblclickEvents);
    private sealed record ViewerOperation(bool CanPost, string ActionEndpoint, string ActionRoute, string ActionMethod, string ActionToken, string PluginId, string RouteSegment);
    private sealed record WindowOperation(bool CanOpen, bool CanSelfRefresh, string WindowEndpoint, string WindowRoute, string WindowMethod, string WindowToken, string PluginId, string RouteSegment, string WindowId, string WindowStateEndpoint, bool ToolWindowSupported);
    private sealed record ViewerSessionRow(string LeaseId, string ServiceName, string ProgramGuideFilterGroup, string AllocationGroup, string TunerGroup, string TunerName, string Did, int SlotIndex, ushort? NetworkId, ushort? TransportStreamId, ushort? ServiceId, bool Current, string ViewerState, string ViewerProfile, string ViewerProfileName, string TvTestPathKey)
    {
        public bool IsActive => Current || ViewerState.Equals("launched", StringComparison.OrdinalIgnoreCase) || ViewerState.Equals("active", StringComparison.OrdinalIgnoreCase) || ViewerState.Equals("viewing", StringComparison.OrdinalIgnoreCase);
    }
    private sealed record ViewerProfileChoice(string Value, string Name, bool Enabled, bool IsDefault, int Order, IReadOnlyList<string> AvailableGroups)
    {
        public string Id => Value;
        public bool IsAvailableForWave(string wave)
        {
            if (AvailableGroups == null || AvailableGroups.Count == 0) return true;
            var required = RequiredProfileGroupForWave(wave);
            return AvailableGroups.Any(x => NormalizeAvailableGroup(x).Equals(required, StringComparison.OrdinalIgnoreCase));
        }
        public static ViewerProfileChoice TvTest1Fallback { get; } = new("tvtest1", "TVTest1", true, true, 1, Array.Empty<string>());
    }

    private sealed record ViewerProfileState(IReadOnlyList<ViewerProfileChoice> SelectableProfiles, string DefaultViewerProfile, bool SelectorVisibleRecommended, bool MinWidthInvariantRequired, bool ContractAvailable)
    {
        public IEnumerable<ViewerProfileChoice> AvailableForWave(string wave) => SelectableProfiles.Where(x => x.Enabled && x.IsAvailableForWave(wave));
        public static ViewerProfileState Unavailable { get; } = new(new[] { ViewerProfileChoice.TvTest1Fallback }, "tvtest1", false, true, false);
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
        public string NextTitle { get; set; } = string.Empty;
        public DateTimeOffset? NextStart { get; set; }
        public DateTimeOffset? NextEnd { get; set; }
        public bool IsViewing { get; set; }
        public string ViewingLeaseId { get; set; } = string.Empty;
        public string ViewingTunerName { get; set; } = string.Empty;
        public string ViewingDid { get; set; } = string.Empty;
        public string ViewingViewerProfile { get; set; } = string.Empty;
        public string ViewingViewerProfileName { get; set; } = string.Empty;
    }
}
