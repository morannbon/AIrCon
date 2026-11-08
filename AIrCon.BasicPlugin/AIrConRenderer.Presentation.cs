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

    // Both the Host DTO and the rendered session row share this lifecycle test.
    private static bool IsViewerSessionActive(int? processId, string? state) =>
        processId is > 0
        && !string.Equals(state, "stopped", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(state, "closed", StringComparison.OrdinalIgnoreCase);

    private static bool IsLiveViewerSession(ViewerSessionDto session) =>
        IsViewerSessionActive(session.ProcessId, session.State);

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

    // The control bar has one semantic presentation definition. Full HTML, action
    // patches, and terminal-session patches project this same state without
    // altering the station list's layout or its scroll ownership. The action
    // button remains the same DOM/focus target: text is patched only in its
    // stable child label, never by rewriting the clicked button's contents.
    private sealed record ZappingControlPresentation(
        bool Active, string Wave, string StatusText, string StatusClass,
        string ButtonText, string ButtonTitle, string NextOperation);

    private sealed record PowerOffControlPresentation(
        bool Active, int Hours, string Label, string ButtonText,
        string ButtonTitle, string NextOperation);

    private static ZappingControlPresentation ResolveZappingControlPresentation(bool active, string wave)
    {
        var normalizedWave = NormalizeWaveFilter(wave);
        return new ZappingControlPresentation(
            active, normalizedWave,
            active ? $"巡回中（{normalizedWave}）" : "停止中",
            active ? "aircon-zapping-status-on" : "aircon-zapping-status-off",
            active ? "停止" : "開始",
            active ? "ザッピングを停止" : $"{GetZappingIntervalSeconds()}秒ごとに同じ放送波内で順送りします",
            active ? AirConActionZappingStop : AirConActionZappingStart);
    }

    private static PowerOffControlPresentation ResolvePowerOffControlPresentation(bool active, int hours)
    {
        var normalizedHours = Math.Clamp(hours, DefaultPowerOffHours, MaxPowerOffHours);
        return new PowerOffControlPresentation(
            active, normalizedHours,
            active ? "終了まで" : "電源OFF",
            active ? "停止" : "開始",
            active ? "終了タイマーを停止" : "選択中TVTestの終了タイマーを開始",
            active ? AirConActionPowerOffStop : AirConActionPowerOffStart);
    }

    private static IReadOnlyList<RuntimeUiPatch> BuildZappingControlPatches(ZappingControlPresentation state) =>
        new RuntimeUiPatch[]
        {
            RuntimeUiPatch.Text("aircon-zapping-status", state.StatusText),
            RuntimeUiPatch.Classes("aircon-zapping-status",
                add: new[] { state.StatusClass },
                remove: new[] { state.Active ? "aircon-zapping-status-off" : "aircon-zapping-status-on" }),
            RuntimeUiPatch.Text("aircon-zapping-button-label", state.ButtonText),
            RuntimeUiPatch.Classes("aircon-zapping-button",
                add: state.Active ? new[] { "aircon-zapping-button-on" } : Array.Empty<string>(),
                remove: state.Active ? Array.Empty<string>() : new[] { "aircon-zapping-button-on" }),
            new RuntimeUiPatch
            {
                ElementId = "aircon-zapping-button",
                Attributes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["title"] = state.ButtonTitle,
                    ["data-tvair-payload-operation"] = state.NextOperation,
                    ["data-tvair-payload-airconaction"] = state.NextOperation
                }
            },
            new RuntimeUiPatch
            {
                ElementId = "aircon-zapping-bar",
                Attributes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["data-aircon-zapping-active"] = state.Active ? BoolTrue : BoolFalse,
                    ["data-aircon-zapping-wave"] = state.Wave
                }
            }
        };

    private IReadOnlyList<RuntimeUiPatch> BuildZappingUiPatches(bool active, string wave, string windowId)
    {
        var state = ResolveZappingControlPresentation(active, wave);
        var patches = new List<RuntimeUiPatch>(BuildZappingControlPatches(state));
        var selectedRow = GetRenderedServiceRowId(windowId, state.Wave);
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
        var state = ResolvePowerOffControlPresentation(active, hours);
        return new RuntimeUiPatch[]
        {
            RuntimeUiPatch.Text("aircon-sleep-label", state.Label),
            RuntimeUiPatch.Visibility("aircon-sleep-select", visible: !state.Active),
            RuntimeUiPatch.Visibility("aircon-sleep-remaining", visible: state.Active),
            RuntimeUiPatch.Text("aircon-sleep-remaining", state.Hours.ToString(System.Globalization.CultureInfo.InvariantCulture) + "h"),
            RuntimeUiPatch.Text("aircon-sleep-button-label", state.ButtonText),
            RuntimeUiPatch.Classes("aircon-sleep-button",
                add: state.Active ? new[] { "aircon-sleep-button-on" } : Array.Empty<string>(),
                remove: state.Active ? Array.Empty<string>() : new[] { "aircon-sleep-button-on" }),
            new RuntimeUiPatch
            {
                ElementId = "aircon-sleep-button",
                Attributes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["title"] = state.ButtonTitle,
                    ["data-tvair-payload-operation"] = state.NextOperation
                }
            }
        };
    }

}
