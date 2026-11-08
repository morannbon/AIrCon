using System.Text.Json;
using System.Net;
using TvAIrPlugin;
using TvAIrPlugin.Runtime;
using TvAIrPlugin.Viewers;

namespace AIrCon.BasicPlugin;

internal sealed partial class AIrConRenderer
{
    private static string NormalizeFilter(string? value)
    {
        var v = (value ?? string.Empty).Trim().ToUpperInvariant();
        return v switch
        {
            "T" or "TERRESTRIAL" or "地" or "地デジ" => "GR",
            "GR" => "GR",
            "BS" => "BS",
            "CS" => "CS",
            "CATV" or "ISDB-C" or "CABLE" => "CATV",
            _ => "ALL"
        };
    }

    // The Host may provide composite broadcast-group names; UI filters accept only
    // the explicit wave names handled by NormalizeFilter. Both use this authority.
    internal static string NormalizeHostWave(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToUpperInvariant();
        if (text is "CATV" or "ISDB-C" or "CABLE") return "CATV";
        if (text == "CS" || text.Contains("CS")) return "CS";
        if (text == "BS" || text.Contains("BS")) return "BS";
        if (text == "GR" || text.Contains("GROUND") || text.Contains("TERRESTRIAL") || text.Contains("地上")) return "GR";
        throw new InvalidOperationException("放送波を特定できません。");
    }

    private static string NormalizeWaveFilter(string? value)
    {
        var v = NormalizeFilter(value);
        return v is "BS" or "CS" or "CATV" ? v : "GR";
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
            case "CATV":
                return "CATV";
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
        // Host-managed WindowId alone identifies the live ToolWindow subscription;
        // query parameters cannot override its identity.
        var currentWindowId = (c.CurrentWindowId ?? string.Empty).Trim();
        if (c.IsHostManagedWindowContent || !string.IsNullOrWhiteSpace(currentWindowId))
            return currentWindowId;

        // Non-host-managed rendering may resolve a window identifier from the request.
        var q = ExtractQueryDictionary(c);
        foreach (var key in new[] { "__tvairWindowId", "_tvairWindowId", "currentWindowId", "windowId" })
            if (q.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)) return v.Trim();
        return string.Empty;
    }

    private static Dictionary<string, string> ExtractQueryDictionary(RuntimeUiRenderContext c)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var preferredNames = new[]
        {
            "QueryString", "RawQuery", "PathAndQuery", "RawUrl", "Url", "RequestUrl", "AbsoluteUrl", "OriginalUrl",
            "RequestQuery", "Query", "RouteQuery", "CurrentQueryString", "CurrentContentRoute", "ContentRoute"
        };
        foreach (var name in preferredNames)
        {
            var raw = ReadProperty(c, name)?.ToString();
            MergeQuery(dict, raw);
        }

        // Each property is read once. Named Host query fields retain their
        // precedence; additional query-bearing string fields are handled afterward.
        var seenNames = new HashSet<string>(preferredNames, StringComparer.OrdinalIgnoreCase);
        foreach (var prop in c.GetType().GetProperties())
        {
            var name = prop.Name;
            if (prop.PropertyType != typeof(string) || string.IsNullOrWhiteSpace(name) || !seenNames.Add(name)) continue;
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
#if AIRCON_DEVELOPER_DIAGNOSTICS
    private void SafeLog(string message)
    {
        try { AIrConNewApiBridge.LogInfo("AIrCon: " + message); } catch { }
    }
#endif

    private sealed record ViewerSessionContractState(string ViewerSessionId, long Generation);
    private sealed record ZappingState(bool Active, DateTimeOffset StartedAt, DateTimeOffset LastTickAt, DateTimeOffset NextTickAt, string LastServiceKey, string WindowId, string Wave, string ViewerProfile, long Generation, string ViewerSessionId, long ViewerGeneration, int ProcessId, int IntervalSeconds);
    private sealed record PowerOffState(bool Active, DateTimeOffset Deadline, string ViewerProfile, string WindowId, long Generation);

    private sealed record FocusTriplet(int? NetworkId, int? TransportStreamId, int? ServiceId)
    {
        public bool IsResolved => NetworkId.GetValueOrDefault() > 0 && TransportStreamId.GetValueOrDefault() > 0 && ServiceId.GetValueOrDefault() > 0;
    }

    private sealed record ViewerStartPayload(string NetworkId, string TransportStreamId, string ServiceId, string ChannelSpace, string ChannelIndex, string ChannelArgument, string ProgramGuideFilterGroup, string BroadcastGroup, string AllocationGroup, string TunerGroup, string ServiceName, string PreferredTunerName, string PreferredDid, string PreferredSlot, string ViewerProfile, string ViewerProfileName);
    private sealed record FloatingViewerData(IReadOnlyList<ServiceRow> Services, IReadOnlyList<ViewerSessionRow> ViewerSessions, IReadOnlyList<ViewerTunerRow> ViewerTuners, IReadOnlyList<WaveFilterRow> WaveFilters, ViewerProfileState ViewerProfiles, IReadOnlyList<TvAirProgramEventDto> ProgramEvents, IReadOnlyList<TvAirViewerReservation> ViewerReservations, bool ProjectionUsed, bool SafeEventContractAvailable, bool SafeDblclickEvents, int ServiceColumnWidthPx);
    private sealed record ViewerOperation(bool CanPost, string ActionEndpoint, string ActionRoute, string ActionMethod, string ActionToken, string PluginId, string RouteSegment);
    private sealed record HostActionDispatchResult(bool Success, string Diagnostics, string Message)
    {
        public static HostActionDispatchResult Ok(string diagnostics) => new(true, diagnostics, "OK");
        public static HostActionDispatchResult Failure(string diagnostics, string message) => new(false, diagnostics, message);
    }
    private sealed record WindowOperation(bool CanOpen, bool CanSelfRefresh, string WindowEndpoint, string WindowRoute, string WindowMethod, string WindowToken, string PluginId, string RouteSegment, string WindowId, bool ToolWindowSupported);
    private sealed record ViewerSessionRow(string LeaseId, string ServiceName, string ProgramGuideFilterGroup, string AllocationGroup, string TunerGroup, string TunerName, string Did, int SlotIndex, ushort? NetworkId, ushort? TransportStreamId, ushort? ServiceId, bool Current, string ViewerState, string ViewerProfile, string ViewerProfileName, string TvTestPathKey, string ViewerSessionId, long Generation, int? ProcessId)
    {
        public bool IsActive => IsViewerSessionActive(ProcessId, ViewerState);
    }
    private sealed record ViewerProfileChoice(string Value, string Name, bool Enabled, bool IsDefault, int Order, IReadOnlyList<string> AvailableGroups, int TvTestFrameIndex, string LogicalViewerSlotId, bool IsShared)
    {
        public string Id => Value;
        public bool IsAvailableForWave(string wave)
        {
            if (AvailableGroups == null || AvailableGroups.Count == 0) return NormalizeWaveFilter(wave) != "CATV";
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
        public HashSet<string> ViewingViewerProfiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool IsViewing => ViewingViewerProfiles.Count != 0;
    }
}
