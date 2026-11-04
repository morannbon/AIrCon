using System.Text.Json;
using System.Net;
using TvAIrPlugin;
using TvAIrPlugin.Runtime;
using TvAIrPlugin.Viewers;
using ViewerSessionDto = TvAIrPlugin.Viewers.TvAirViewerSessionDto;

namespace AIrCon.BasicPlugin;

internal sealed partial class AIrConRenderer
{
    // One canonical station-emphasis projection is shared by full HTML renders,
    // live ViewerSession patches, zapping start/stop and Viewer terminal transitions.
    // Color is solely the theme-specific rendering of this semantic state.
    private enum ServiceEmphasis { None, OtherViewer, Viewing, Zapping }

    private static readonly string[] ServiceEmphasisClasses =
    {
        "aircon-row-viewing-other", "aircon-row-viewing-selected", "aircon-row-zapping-selected"
    };

    private static bool IsLiveViewerSession(ViewerSessionDto session) =>
        session.ProcessId is > 0
        && !session.State.Equals("stopped", StringComparison.OrdinalIgnoreCase)
        && !session.State.Equals("closed", StringComparison.OrdinalIgnoreCase);

    private static ServiceEmphasis ResolveServiceEmphasis(bool viewedBySelectedProfile,
        bool viewedByOtherProfile, bool zappingThisWave) =>
        viewedBySelectedProfile
            ? (zappingThisWave ? ServiceEmphasis.Zapping : ServiceEmphasis.Viewing)
            : viewedByOtherProfile ? ServiceEmphasis.OtherViewer : ServiceEmphasis.None;

    private static string ServiceEmphasisClass(ServiceEmphasis emphasis) => emphasis switch
    {
        ServiceEmphasis.OtherViewer => "aircon-row-viewing-other",
        ServiceEmphasis.Viewing => "aircon-row-viewing-selected",
        ServiceEmphasis.Zapping => "aircon-row-zapping-selected",
        _ => string.Empty
    };

    private static RuntimeUiPatch BuildServiceEmphasisPatch(string rowId, ServiceEmphasis emphasis)
    {
        var selectedClass = ServiceEmphasisClass(emphasis);
        return RuntimeUiPatch.Classes(rowId,
            add: selectedClass.Length == 0 ? Array.Empty<string>() : new[] { selectedClass },
            remove: ServiceEmphasisClasses.Where(c => !c.Equals(selectedClass, StringComparison.Ordinal)).ToArray());
    }

    private static bool IsServiceWatchedBy(ViewerSessionDto session, string serviceKey) =>
        IsLiveViewerSession(session)
        && session.CurrentService is { NetworkId: > 0, TransportStreamId: > 0, ServiceId: > 0 } identity
        && ServiceKey(identity.NetworkId, identity.TransportStreamId, identity.ServiceId)
            .Equals(serviceKey, StringComparison.OrdinalIgnoreCase);

    private static ServiceEmphasis ResolveLiveServiceEmphasis(string serviceKey,
        string selectedViewerProfile, bool zappingThisWave,
        IReadOnlyList<ViewerSessionDto> sessions)
    {
        var selected = sessions.Any(s =>
            s.ViewerProfileId.Equals(selectedViewerProfile, StringComparison.OrdinalIgnoreCase)
            && IsServiceWatchedBy(s, serviceKey));
        var other = sessions.Any(s =>
            !s.ViewerProfileId.Equals(selectedViewerProfile, StringComparison.OrdinalIgnoreCase)
            && IsServiceWatchedBy(s, serviceKey));
        return ResolveServiceEmphasis(selected, other, zappingThisWave);
    }

    private IReadOnlyList<RuntimeUiPatch> BuildZappingUiPatches(bool active, string wave, string windowId)
    {
        var normalizedWave = NormalizeWaveFilter(wave);
        var nextOperation = active ? AirConActionZappingStop : AirConActionZappingStart;
        var intervalSeconds = GetZappingIntervalSeconds();
        var patches = new List<RuntimeUiPatch>
        {
            RuntimeUiPatch.Text("aircon-zapping-status", active ? $"巡回中（{normalizedWave}）" : "停止中"),
            RuntimeUiPatch.Classes("aircon-zapping-status",
                add: new[] { active ? "aircon-zapping-status-on" : "aircon-zapping-status-off" },
                remove: new[] { active ? "aircon-zapping-status-off" : "aircon-zapping-status-on" }),
            RuntimeUiPatch.Text("aircon-zapping-button", active ? "停止" : "開始"),
            RuntimeUiPatch.Classes("aircon-zapping-button",
                add: active ? new[] { "aircon-zapping-button-on" } : Array.Empty<string>(),
                remove: active ? Array.Empty<string>() : new[] { "aircon-zapping-button-on" }),
            // The selected row is targeted by its stable service ID below. The same service
            // identity also owns external retune highlight updates and terminal clearing.
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
        var selectedRow = GetRenderedServiceRowId(windowId, normalizedWave);
        if (selectedRow.Length > 0)
        {
            var serviceKey = GetRenderedServiceKey(windowId);
            var sessions = AIrConNewApiBridge.ListSessions();
            patches.Add(BuildServiceEmphasisPatch(selectedRow,
                ResolveLiveServiceEmphasis(serviceKey, GetDisplayedViewerProfile(windowId),
                    active, sessions)));
        }
        return patches;
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

}
