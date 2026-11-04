using System.Text.Json;
using System.Net;
using TvAIrPlugin;
using TvAIrPlugin.Runtime;
using TvAIrPlugin.Viewers;

namespace AIrCon.BasicPlugin;

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
#if AIRCON_DEVELOPER_DIAGNOSTICS
            , PluginPermission.WriteLogs
#endif
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
                InitialSize = new TvAIrPlugin.Windows.PluginWindowSize(AIrConRenderer.ToolWindowDefaultWidthPx, AIrConRenderer.ToolWindowDefaultHeightPx),
                MinimumSize = new TvAIrPlugin.Windows.PluginWindowSize(AIrConRenderer.ToolWindowMinimumWidthContractPx, AIrConRenderer.ToolWindowMinimumHeightContractPx),
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
    private AirConForegroundObserver? _foregroundObserver;
    public void Initialize(ITvAirPluginRuntimeContext context) => AIrConNewApiBridge.InitializeRuntime(context);
    public string RenderHtml(RuntimeUiRenderContext context) => _ui.RenderHtml(context);
    public Task<RuntimeUiActionResult> HandleActionAsync(RuntimeUiActionContext context, CancellationToken cancellationToken)
        => _ui.HandleActionAsync(context, cancellationToken);
    public void OnStart()
    {
        _ui.BeginRuntimeUi();
        _foregroundObserver?.Dispose();
        _foregroundObserver = new AirConForegroundObserver(_ui.OnOwnWindowForeground);
        var settings = AIrConNewApiBridge.LoadSettings();
        var placement = AIrConNewApiBridge.SetPlacementPersistence("main", settings.RememberWindowPlacement);
        _runtimeEventSubscriptions?.Dispose();
        _runtimeEventSubscriptions = AIrConNewApiBridge.SubscribeRuntimeEvents((eventType, eventEnvelope) =>
        {
            #if AIRCON_DEVELOPER_DIAGNOSTICS
            AIrConNewApiBridge.LogInfo($"PROJECTION_INVALIDATE source={eventType} action=invalidate_only windowRefresh=none rule=aircon_projection_single_source");
            #endif
            if (eventType.Equals("ViewerSessionChanged", StringComparison.OrdinalIgnoreCase) && eventEnvelope != null)
                _ui.ApplyViewerSessionStatePatch(eventEnvelope);
            else if (eventType.Equals("RuntimeWindowLifecycleChanged", StringComparison.OrdinalIgnoreCase) && eventEnvelope != null)
                _ui.ApplyRuntimeWindowLifecycle(eventEnvelope);
            else if (eventType.Equals("ViewerReservationChanged", StringComparison.OrdinalIgnoreCase) && eventEnvelope != null)
                _ui.ApplyViewerReservationChanged(eventEnvelope);
        }, _ui.ApplyViewerOperationPreempting);
        #if AIRCON_DEVELOPER_DIAGNOSTICS
        AIrConNewApiBridge.LogInfo($"PLACEMENT_POLICY_SYNC result={(placement.Success ? "OK" : "FAILED")} rememberPlacement={settings.RememberWindowPlacement} diagnostics={placement.Diagnostics}");
        #endif
        #if AIRCON_DEVELOPER_DIAGNOSTICS
        AIrConNewApiBridge.LogInfo("PROJECTION_LIFECYCLE result=STARTED service=plugin_instance viewerProfile=plugin_instance viewerSession=plugin_instance program=plugin_instance subscriptions=ProgramGuideUpdated,ViewerSessionChanged,SettingsChanged,RuntimeWindowLifecycleChanged,ViewerReservationChanged,ViewerOperationPreempting rule=aircon_projection_single_source");
        #endif
    }
    public void OnStop()
    {
        _foregroundObserver?.Dispose();
        _foregroundObserver = null;
        _runtimeEventSubscriptions?.Dispose();
        _runtimeEventSubscriptions = null;
        _ui.StopRuntimeUi();
        AIrConNewApiBridge.ResetRuntime();
    }
}
