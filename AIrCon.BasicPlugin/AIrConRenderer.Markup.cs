using System.Text.Json;
using System.Net;
using TvAIrPlugin;
using TvAIrPlugin.Runtime;
using TvAIrPlugin.Viewers;

namespace AIrCon.BasicPlugin;

internal sealed partial class AIrConRenderer
{
    private static string BuildLauncherHtml(RuntimeUiRenderContext context, FloatingViewerData data, WindowOperation window, string filter, string selectedTuner, string selectedViewerProfile, bool alwaysOnTop)
    {
        var openForm = BuildOpenWindowForm(window, alwaysOnTop, filter, selectedTuner, selectedViewerProfile);
        return $$"""
<!doctype html>
<meta charset="utf-8">
<style>
{{BuildLauncherCss(context)}}
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

    private static string BuildLauncherCss(RuntimeUiRenderContext context)
    {
        return ResolveThemePalette(context).Apply(@"html,body{margin:0;background:var(--aircon-page);color:var(--aircon-text);font-family:Meiryo,""Yu Gothic"",Arial,sans-serif;font-size:13px;}
.aircon-launch{padding:12px;}
.aircon-card{width:260px;max-width:100%;background:var(--aircon-surface);border:1px solid var(--aircon-border);box-shadow:0 1px 3px rgba(0,0,0,.25);}
.aircon-head{background:var(--aircon-subtle);color:var(--aircon-text);padding:6px 9px;font-weight:bold;}
.aircon-body{padding:10px;line-height:1.35;}
.aircon-open{font-family:inherit;border:1px solid var(--aircon-button-border);background:var(--aircon-accent);color:var(--aircon-accent-text);border-radius:3px;padding:5px 11px;font-weight:bold;cursor:pointer;}
.aircon-open:hover{background:var(--aircon-accent-hover);}
.aircon-note,.aircon-status{display:none;}");
    }

    private static string BuildFloatingViewerHtml(RuntimeUiRenderContext context, FloatingViewerData data, ViewerOperation action, WindowOperation window, string filter, string selectedTunerValue, ViewerProfileChoice selectedViewerProfile, bool alwaysOnTop, bool zappingActive, string activeZappingWave, DateTimeOffset? powerOffDeadline, long renderVersion)
    {
        var tunerChoices = BuildTunerChoices(data.ViewerTuners, filter).ToList();
        var selected = ResolveSelectedTuner(tunerChoices, selectedTunerValue);
        var rows = BuildRows(context, data.Services, data.ViewerSessions, action, window, selected, selectedViewerProfile, filter, zappingActive, activeZappingWave, powerOffDeadline);
        var reservationMenu = BuildViewerReservationMenu(context, data, action, window, filter, selectedViewerProfile.Value);
        var toolbar = BuildToolbar(context, data.WaveFilters, data.ViewerSessions, data.ViewerProfiles, action, window, filter, selected.Value, selectedViewerProfile, alwaysOnTop);
        return $$"""
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<style>
{{BuildToolWindowCss(context, data.ServiceColumnWidthPx)}}
</style>
</head>
<body class="aircon-runtime-root">
<div class="aircon-float" id="aircon-render-{{renderVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)}}">
  {{toolbar}}
  <div class="aircon-list" id="aircon-service-list">
    {{rows}}
  </div>
  {{reservationMenu}}
  <script>
  (function(){
    var list=document.getElementById('aircon-service-list');
    var menu=document.getElementById('aircon-reservation-menu');
    if(!list||!menu)return;
    // Station positioning is owned by the native ToolWindow's service-list scroll.
    // The inline script controls only local marquee and menu interactions.
    var marqueeDelayMs=400,marqueeSpeedPxPerSec=54;
    var marqueeDelay=0,marqueeFrame=0,marqueeTarget=null,marqueeStart=0;
    function clearMarquee(target){
      var owns=!target||target===marqueeTarget;
      if(owns){
        if(marqueeDelay){window.clearTimeout(marqueeDelay);marqueeDelay=0;}
        if(marqueeFrame){window.cancelAnimationFrame(marqueeFrame);marqueeFrame=0;}
      }
      var el=target||(owns?marqueeTarget:null);
      if(el){var track=el.querySelector('.aircon-current-track');if(track)track.style.transform='translateX(0px)';el.classList.remove('aircon-current-marquee');var copy=el.querySelector('.aircon-current-copy');if(copy)copy.style.marginLeft='';}
      if(owns)marqueeTarget=null;
    }
    function measureMarquee(el){
      var text=el&&el.querySelector('.aircon-current-text');
      if(!text)return null;
      var available=el.clientWidth,textWidth=text.scrollWidth;
      if(available<=0||textWidth<=available+1)return null;
      return {available:available,textWidth:textWidth,gap:Math.max(28,Math.floor(available/3))};
    }
    function marqueeTick(now){
      var el=marqueeTarget;if(!el)return;
      var m=measureMarquee(el);if(!m){clearMarquee();return;}
      var track=el.querySelector('.aircon-current-track');if(!track){clearMarquee();return;}
      var travel=Math.max(1,m.textWidth+m.gap);
      var phase=((now-marqueeStart)*marqueeSpeedPxPerSec/1000)%travel;
      track.style.transform='translateX('+(-phase)+'px)';
      marqueeFrame=window.requestAnimationFrame(marqueeTick);
    }
    function startMarquee(el){
      clearMarquee();
      var m=measureMarquee(el);if(!m)return;
      marqueeTarget=el;
      marqueeDelay=window.setTimeout(function(){
        marqueeDelay=0;if(marqueeTarget!==el)return;
        var latest=measureMarquee(el);if(!latest){clearMarquee();return;}
        var copy=el.querySelector('.aircon-current-copy');if(copy)copy.style.marginLeft=latest.gap+'px';
        el.classList.add('aircon-current-marquee');
        marqueeStart=window.performance&&window.performance.now?window.performance.now():Date.now();
        marqueeFrame=window.requestAnimationFrame(marqueeTick);
      },marqueeDelayMs);
    }
    var marqueeItems=list.querySelectorAll('.aircon-current[data-aircon-marquee=\"true\"]');
    for(var mi=0;mi<marqueeItems.length;mi++){
      (function(el){el.addEventListener('mouseenter',function(){startMarquee(el);});el.addEventListener('mouseleave',function(){clearMarquee(el);});})(marqueeItems[mi]);
    }
    window.addEventListener('resize',function(){
      if(!marqueeTarget)return;
      var el=marqueeTarget;var latest=measureMarquee(el);
      if(!latest){clearMarquee();return;}
      var copy=el.querySelector('.aircon-current-copy');if(copy)copy.style.marginLeft=latest.gap+'px';
    });
    function closeMenu(){menu.style.display='none';var sections=menu.getElementsByClassName('aircon-reservation-section');for(var i=0;i<sections.length;i++)sections[i].style.display='none';}
    list.oncontextmenu=function(ev){
      ev=ev||window.event;var el=ev.target||ev.srcElement;
      while(el&&el!==list&&!((' '+el.className+' ').indexOf(' aircon-row ')>=0))el=el.parentNode;
      if(!el||el===list||!el.getAttribute('data-aircon-service-id'))return true;
      if(ev.preventDefault)ev.preventDefault();ev.returnValue=false;
      closeMenu();
      var section=document.getElementById('aircon-reservation-'+el.getAttribute('data-aircon-service-id'));
      if(!section)return false;
      section.style.display='block';menu.style.display='block';
      var x=ev.clientX||0,y=ev.clientY||0;menu.style.left=x+'px';menu.style.top=y+'px';
      var r=menu.getBoundingClientRect();
      if(r.right>document.documentElement.clientWidth)menu.style.left=Math.max(0,document.documentElement.clientWidth-r.width-4)+'px';
      if(r.bottom>document.documentElement.clientHeight)menu.style.top=Math.max(0,document.documentElement.clientHeight-r.height-4)+'px';
      return false;
    };
    document.onclick=function(ev){var t=(ev||window.event).target||window.event.srcElement;if(menu.style.display==='block'&&!menu.contains(t))closeMenu();};
    document.onkeydown=function(ev){ev=ev||window.event;if(ev.keyCode===27)closeMenu();};
    list.onscroll=function(){closeMenu();clearMarquee();};
  })();
  </script>
</div>
</body>
</html>
""";
    }

    private static string BuildToolWindowCss(RuntimeUiRenderContext context, int serviceColumnWidthPx)
    {
        var toolbarHeight = ToolWindowToolbarHeightPx;
        var toolbarContentTop = ToolWindowToolbarContentTopPx;
        var toolbarPaddingX = ToolWindowToolbarPaddingXPx;
        var toolbarCellGap = ToolWindowToolbarCellGapPx;
        var toolbarGroupGap = ToolWindowToolbarGroupGapPx;
        var toolbarLabelPaddingRight = ToolWindowToolbarLabelPaddingRightPx;
        var waveButtonWidth = ToolWindowWaveButtonWidthPx;
        var waveAreaWidth = ToolWindowWaveAreaWidthPx;
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
        var serviceWidth = Math.Max(ToolWindowServiceColumnMinimumWidthPx, Math.Min(ToolWindowServiceColumnMaximumWidthPx, serviceColumnWidthPx));
        var timeWidth = ToolWindowTimeColumnWidthPx;
        
        return ResolveThemePalette(context).Apply($$"""
html,body{margin:0;padding:0;width:100%;height:100%;min-width:0;min-height:{{ToolWindowMinimumHeightPx}}px;background:var(--aircon-page);color:var(--aircon-text);font-family:Meiryo,"Yu Gothic",Arial,sans-serif;font-size:12px;overflow:hidden;}
body.aircon-runtime-root{position:static;}
.aircon-runtime-root,.aircon-runtime-root *{box-sizing:border-box;}
.aircon-float{position:static;width:100%;height:100%;min-width:0;min-height:{{ToolWindowMinimumHeightPx}}px;background:var(--aircon-page);overflow:hidden;}
.aircon-toolbar{position:fixed;left:0;right:0;top:0;height:{{toolbarHeight}}px;padding:0;border-bottom:{{ToolWindowToolbarSeparatorPx}}px solid var(--aircon-border);background:var(--aircon-surface);white-space:nowrap;overflow:hidden;text-align:left;z-index:2;}
.aircon-toolbar-inner{position:absolute;left:{{toolbarPaddingX}}px;right:{{toolbarPaddingX}}px;top:{{toolbarContentTop}}px;height:{{buttonHeight}}px;overflow:hidden;white-space:nowrap;}
.aircon-toolbar-wave-area{position:absolute;left:0;top:0;width:{{waveAreaWidth}}px;height:{{cellHeight}}px;line-height:{{cellHeight}}px;white-space:nowrap;overflow:hidden;}
.aircon-toolbar-profile-slot{position:absolute;left:{{waveAreaWidth + toolbarGroupGap}}px;right:{{actionButtonGroupWidth + toolbarGroupGap}}px;top:0;width:auto;height:{{cellHeight}}px;line-height:{{cellHeight}}px;white-space:nowrap;overflow:hidden;}
.aircon-toolbar-profile-slot-reserved{visibility:hidden;}
.aircon-toolbar-actions{position:absolute;right:0;top:0;width:{{actionButtonGroupWidth}}px;height:{{cellHeight}}px;display:flex;flex-direction:row;justify-content:flex-end;align-items:flex-start;gap:{{toolbarCellGap}}px;white-space:nowrap;overflow:visible;}
.aircon-toolbar-label{display:inline-block;height:{{cellHeight}}px;line-height:{{buttonLineHeight}}px;margin:0;padding:{{buttonBorder}}px {{toolbarLabelPaddingRight}}px {{buttonBorder}}px 0;color:var(--aircon-muted);font-size:11px;font-weight:bold;vertical-align:top;white-space:nowrap;}
.aircon-wave-group{display:inline-block;margin:0;padding:0;width:{{waveButtonGroupWidth}}px;height:{{cellHeight}}px;line-height:{{cellHeight}}px;white-space:nowrap;vertical-align:top;overflow:visible;}
.aircon-nav-form,.aircon-action-form,.aircon-profile-form{display:inline-block;margin:0;padding:0;height:{{cellHeight}}px;line-height:{{cellHeight}}px;white-space:nowrap;vertical-align:top;}
.aircon-toolbar-actions .aircon-action-form{width:{{actionButtonSize}}px;margin:0;}
.aircon-wave-group .aircon-nav-form{margin:0 0 0 -{{waveButtonOverlap}}px;}
.aircon-wave-group .aircon-nav-form:first-child{margin-left:0;}
.aircon-toolbar-button,.aircon-toolbar-select{display:inline-block;height:{{buttonHeight}}px;line-height:{{buttonLineHeight}}px;padding:0 6px;margin:0;border:{{buttonBorder}}px solid var(--aircon-button-border);background:var(--aircon-button-bg);color:var(--aircon-button-text);border-radius:2px;font-size:11px;font-family:inherit;font-weight:bold;text-align:center;cursor:pointer;vertical-align:top;white-space:nowrap;text-decoration:none;}
.aircon-toolbar-button:hover,.aircon-toolbar-select:hover{background:var(--aircon-button-hover);}
.aircon-toolbar-button:focus,.aircon-toolbar-select:focus{outline:1px solid var(--aircon-focus);outline-offset:1px;}
.aircon-toolbar-button-disabled:hover,.aircon-profile-button-disabled:hover{background:var(--aircon-disabled-bg);color:var(--aircon-disabled-text);}
.aircon-profile-label{display:inline-block;width:auto;height:{{cellHeight}}px;line-height:{{buttonLineHeight}}px;margin:0;padding:{{buttonBorder}}px {{toolbarLabelPaddingRight}}px {{buttonBorder}}px 0;color:var(--aircon-profile-label);font-size:11px;font-weight:bold;vertical-align:top;white-space:nowrap;overflow:visible;text-align:left;}
.aircon-profile-segments{display:inline-block;width:auto;height:{{cellHeight}}px;line-height:{{cellHeight}}px;white-space:nowrap;overflow:visible;vertical-align:top;}
.aircon-profile-segment-form{display:inline-block;margin:0 {{toolbarCellGap}}px 0 0;padding:0;width:{{viewerProfileButtonWidth}}px;height:{{cellHeight}}px;line-height:{{cellHeight}}px;vertical-align:top;white-space:nowrap;overflow:visible;}
.aircon-profile-button{width:{{viewerProfileButtonWidth}}px;min-width:{{viewerProfileButtonWidth}}px;max-width:{{viewerProfileButtonWidth}}px;height:{{buttonHeight}}px;line-height:{{buttonLineHeight}}px;padding:0;text-align:center;}
.aircon-profile-button-on{background:var(--aircon-accent);color:var(--aircon-accent-text);border-color:var(--aircon-button-border);}
.aircon-profile-button-on:hover,.aircon-profile-button-on:focus,.aircon-profile-button-on:active{background:var(--aircon-accent);color:var(--aircon-accent-text);border-color:var(--aircon-button-border);}
.aircon-profile-button-disabled{opacity:.45;cursor:default;background:var(--aircon-disabled-bg);color:var(--aircon-disabled-text);}
.aircon-toolbar-button-disabled{opacity:.70;cursor:default;background:var(--aircon-disabled-bg);color:var(--aircon-disabled-text);}
.aircon-toolbar-wave-area.aircon-wave-four .aircon-wave-group{width:153px;}
.aircon-toolbar-wave-area.aircon-wave-four .aircon-wave-button{width:39px;min-width:39px;max-width:39px;padding-left:0;padding-right:0;text-align:center;}
.aircon-wave-button{width:{{waveButtonWidth}}px;min-width:{{waveButtonWidth}}px;border-radius:0;background:var(--aircon-button-bg);border-color:var(--aircon-button-border);color:var(--aircon-button-text);}
.aircon-wave-button-on{background:var(--aircon-accent);color:var(--aircon-accent-text);border-color:var(--aircon-button-border);}
.aircon-wave-button-on:hover,.aircon-wave-button-on:focus,.aircon-wave-button-on:active{background:var(--aircon-accent);color:var(--aircon-accent-text);border-color:var(--aircon-button-border);}
.aircon-wave-button-disabled,.aircon-wave-button-disabled:hover{background:var(--aircon-disabled-bg);color:var(--aircon-disabled-text);border-color:var(--aircon-disabled-border);cursor:default;}
.aircon-action-button{width:{{actionButtonSize}}px;min-width:{{actionButtonSize}}px;max-width:{{actionButtonSize}}px;padding:0;font-size:15px;font-family:"Segoe UI Symbol","Meiryo","Yu Gothic",Arial,sans-serif;font-weight:bold;line-height:{{buttonLineHeight}}px;text-align:center;}
.aircon-action-refresh{background:var(--aircon-refresh-bg);border-color:var(--aircon-refresh-border);color:var(--aircon-refresh-text);}
.aircon-action-refresh:hover{background:var(--aircon-refresh-hover);}
.aircon-action-settings{font-size:14px;}
.aircon-action-power{background:var(--aircon-power-bg);border-color:var(--aircon-power-border);color:var(--aircon-power-text);}
.aircon-action-power:hover{background:var(--aircon-power-hover);}
.aircon-action-power.aircon-toolbar-button-disabled{background:var(--aircon-power-disabled-bg);border-color:var(--aircon-power-disabled-border);color:var(--aircon-power-disabled-text);}
.aircon-action-topmost{background:var(--aircon-topmost-bg);border-color:var(--aircon-topmost-border);color:var(--aircon-topmost-text);}
.aircon-action-topmost:hover{background:var(--aircon-topmost-hover);}
.aircon-action-topmost-on{background:var(--aircon-topmost-on-bg);border-color:var(--aircon-topmost-on-border);color:var(--aircon-topmost-on-text);}
.aircon-action-topmost-on:hover{background:var(--aircon-topmost-on-hover);}
.aircon-action-topmost.aircon-toolbar-button-disabled{background:var(--aircon-topmost-disabled-bg);border-color:var(--aircon-topmost-disabled-border);color:var(--aircon-topmost-disabled-text);}
.aircon-list{position:fixed;left:0;right:0;top:{{listTop}}px;bottom:0;background:var(--aircon-page);overflow-x:hidden;overflow-y:scroll;width:100%;height:auto;}
.aircon-row{display:block;position:relative;width:100%;margin:0;padding:0 8px;border:0;border-bottom:1px solid var(--aircon-border);background:var(--aircon-row);cursor:pointer;font-family:inherit;text-align:left;color:var(--aircon-text);height:{{rowHeight}}px;line-height:{{rowHeight}}px;white-space:nowrap;overflow:hidden;}
.aircon-row-even{background:var(--aircon-row-alt);}
.aircon-row-odd{background:var(--aircon-row);}
.aircon-row:hover{background:var(--aircon-hover);}
.aircon-row:focus,.aircon-row:active{outline:none;}
.aircon-row-disabled,.aircon-row-disabled:hover{cursor:default;color:var(--aircon-disabled-text);background:var(--aircon-disabled-bg);}
.aircon-row-viewing-selected{background:var(--aircon-viewing-bg);cursor:pointer;}
.aircon-row-viewing-other{background:var(--aircon-viewing-other-bg);cursor:pointer;}
.aircon-viewer-tune-form{display:block;margin:0;padding:0;border:0;}
.aircon-row-zapping-selected{background:var(--aircon-zapping-bg);cursor:pointer;}
.aircon-row-zapping-selected:hover{background:var(--aircon-zapping-hover);}
.aircon-row-viewing-selected:hover{background:var(--aircon-viewing-hover);}
.aircon-row-viewing-other:hover{background:var(--aircon-viewing-other-hover);}
.aircon-row span{cursor:inherit;}
.aircon-service{position:absolute;left:8px;top:0;display:block;width:{{serviceWidth}}px;height:{{rowHeight}}px;line-height:{{rowHeight}}px;vertical-align:top;color:var(--aircon-text);font-weight:bold;font-size:12px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;}
.aircon-time{position:absolute;left:{{8 + serviceWidth}}px;top:0;display:block;width:{{timeWidth}}px;height:{{rowHeight}}px;margin:0;padding:3px 2px 0 0;text-align:center;vertical-align:top;white-space:nowrap;overflow:hidden;font-size:10px;line-height:11px;font-weight:bold;}
.aircon-time-start,.aircon-time-end{display:block;height:11px;line-height:11px;margin:0;padding:0;white-space:nowrap;overflow:hidden;}
.aircon-time-start{color:var(--aircon-time-start);}
.aircon-time-end{color:var(--aircon-time-end);}
.aircon-current{position:absolute;left:{{8 + serviceWidth + timeWidth}}px;right:8px;top:0;display:block;width:auto;margin:0;height:{{rowHeight}}px;line-height:{{rowHeight}}px;vertical-align:top;color:var(--aircon-text);font-size:12px;font-weight:bold;white-space:nowrap;overflow:hidden;text-overflow:clip;}
.aircon-current-track{display:inline-flex;align-items:center;height:100%;line-height:inherit;white-space:nowrap;will-change:transform;transform:translateX(0);}
.aircon-current-copy{display:none;}
.aircon-current-marquee .aircon-current-copy{display:inline-block;}
.aircon-float [hidden]{display:none;}
.aircon-zapping-bar{display:flex;position:relative;align-items:center;justify-content:space-between;gap:8px;min-width:0;margin:0;padding:5px 8px;border-top:1px solid var(--aircon-border);background:var(--aircon-subtle);white-space:nowrap;overflow:hidden;box-sizing:border-box;}
.aircon-zapping-left,.aircon-sleep-right{display:flex;align-items:center;gap:0;min-width:0;height:24px;}
.aircon-zapping-left{flex:0 1 auto;overflow:hidden;}
.aircon-sleep-right{flex:0 0 auto;margin-left:auto;padding-left:9px;border-left:1px solid var(--aircon-border);}
.aircon-zapping-label,.aircon-sleep-label{display:inline-flex;align-items:center;margin:0 7px 0 0;height:22px;line-height:22px;color:var(--aircon-muted);font-size:11px;font-weight:bold;vertical-align:middle;white-space:nowrap;}
.aircon-zapping-status{display:inline-flex;align-items:center;justify-content:center;min-width:42px;margin:0 9px 0 0;padding:0 7px;height:22px;line-height:20px;color:var(--aircon-muted);font-size:10px;font-weight:bold;text-align:center;vertical-align:middle;overflow:hidden;text-overflow:ellipsis;border:1px solid var(--aircon-status-border);border-radius:11px;box-sizing:border-box;}
.aircon-zapping-status-off{background:var(--aircon-status-off-bg);border-color:var(--aircon-status-off-border);color:var(--aircon-status-off-text);}
.aircon-zapping-status-on,.aircon-sleep-remaining{background:var(--aircon-status-on-bg);border-color:var(--aircon-status-on-border);color:var(--aircon-status-on-text);}
.aircon-zapping-form,.aircon-sleep-form{display:inline-flex;align-items:center;margin:0;padding:0;vertical-align:middle;}
.aircon-zapping-button,.aircon-sleep-button,.aircon-sleep-select{display:inline-block;height:22px;line-height:20px;margin:0;border:1px solid var(--aircon-control-border);background:var(--aircon-input);color:var(--aircon-control-text);border-radius:3px;font-size:11px;font-family:inherit;font-weight:bold;text-align:center;vertical-align:middle;box-sizing:border-box;}
.aircon-zapping-button{min-width:44px;padding:0 8px;cursor:pointer;}
.aircon-sleep-button{min-width:38px;padding:0 6px;cursor:pointer;}
.aircon-sleep-select{width:45px;padding:0 2px;cursor:pointer;}
.aircon-zapping-button:hover,.aircon-sleep-button:hover{background:var(--aircon-control-hover);border-color:var(--aircon-control-hover-border);}
.aircon-zapping-button-on,.aircon-sleep-button-on{background:var(--aircon-control-on-bg);color:var(--aircon-control-on-text);border-color:var(--aircon-control-on-border);}
.aircon-zapping-button-on:hover,.aircon-zapping-button-on:focus,.aircon-zapping-button-on:active,.aircon-sleep-button-on:hover,.aircon-sleep-button-on:focus,.aircon-sleep-button-on:active{background:var(--aircon-control-on-bg);color:var(--aircon-control-on-text);border-color:var(--aircon-control-on-border);}
.aircon-sleep-select,.aircon-sleep-remaining{margin-right:9px;}
.aircon-sleep-remaining{display:inline-flex;align-items:center;justify-content:center;min-width:45px;height:22px;line-height:20px;padding:0 4px;border:1px solid;border-radius:3px;box-sizing:border-box;font-size:11px;font-weight:bold;text-align:center;vertical-align:middle;}
.aircon-sleep-hidden{display:none;}
.aircon-reservation-menu{display:none;position:fixed;z-index:20;min-width:300px;max-width:440px;max-height:70%;overflow-y:auto;background:var(--aircon-surface);border:1px solid var(--aircon-border);box-shadow:0 3px 10px rgba(0,0,0,.35);padding:3px;}
.aircon-reservation-section{display:none;}
.aircon-reservation-program{display:block;position:relative;min-height:34px;padding:4px 76px 4px 6px;border-bottom:1px solid var(--aircon-border);background:var(--aircon-row);}
.aircon-reservation-program:last-child{border-bottom:0;}
.aircon-reservation-time{display:block;color:var(--aircon-muted);font-size:10px;line-height:12px;}
.aircon-reservation-title{display:block;color:var(--aircon-text);font-size:11px;font-weight:bold;line-height:15px;white-space:normal;}
.aircon-reservation-targets{position:absolute;right:5px;top:6px;display:flex;gap:3px;}
.aircon-reservation-target{min-width:30px;height:22px;padding:0 5px;border:1px solid var(--aircon-control-border);background:var(--aircon-input);color:var(--aircon-control-text);border-radius:3px;font:700 11px Meiryo,"Yu Gothic",Arial,sans-serif;cursor:pointer;}
.aircon-reservation-target:hover{background:var(--aircon-control-hover);border-color:var(--aircon-control-hover-border);}
.aircon-reservation-target-on{background:var(--aircon-accent);color:var(--aircon-accent-text);border-color:var(--aircon-button-border);}
.aircon-reservation-target-conflict,.aircon-reservation-target:disabled{opacity:.42;cursor:default;background:var(--aircon-disabled-bg);color:var(--aircon-disabled-text);border-color:var(--aircon-disabled-border);}
.aircon-reservation-empty{padding:9px;color:var(--aircon-muted);font-size:11px;}
@media(max-width:360px){.aircon-zapping-bar{gap:4px;padding-left:5px;padding-right:5px}.aircon-sleep-right{padding-left:5px}.aircon-zapping-label,.aircon-sleep-label{margin-right:4px;font-size:10px}.aircon-zapping-status,.aircon-sleep-select,.aircon-sleep-remaining{margin-right:5px}.aircon-zapping-status{min-width:38px;padding-left:5px;padding-right:5px}.aircon-zapping-button{min-width:40px;padding-left:5px;padding-right:5px} }
@media(max-width:285px){.aircon-zapping-label,.aircon-sleep-label{display:none}.aircon-sleep-right{border-left:0;padding-left:0} }
.aircon-empty{padding:16px;color:var(--aircon-muted);}
""");
    }


    private static string BuildHostActionAttributes(
        RuntimeUiRenderContext context,
        IReadOnlyDictionary<string, string?> fields,
        string eventName,
        string responseMode,
        string formCapture = "")
    {
        return context.BuildPluginActionAttributes(fields, eventName, responseMode, formCapture);
    }

    private static string BuildToolbar(RuntimeUiRenderContext context, IReadOnlyList<WaveFilterRow> waveFilters, IReadOnlyList<ViewerSessionRow> viewerSessions, ViewerProfileState viewerProfiles, ViewerOperation action, WindowOperation window, string filter, string selectedTuner, ViewerProfileChoice selectedViewerProfile, bool alwaysOnTop)
    {
        var filterForms = new List<string>();
        foreach (var f in CanonicalWaveFilters(waveFilters))
        {
            filterForms.Add(BuildFilterForm(context, f.Group, f.Label, filter, selectedViewerProfile, viewerProfiles));
        }

        var refresh = BuildRefreshForm(window, filter, selectedViewerProfile.Value);
        var selectedProfileSession = ResolveSelectedProfileSession(viewerSessions, selectedViewerProfile.Value);
        var power = BuildToolbarStopForm(context, selectedProfileSession, action, window, filter, selectedViewerProfile.Value);
        var pin = BuildPinForm(window, !alwaysOnTop, filter, selectedTuner, selectedViewerProfile.Value, alwaysOnTop);
        var settings = BuildSettingsButton(context, action, window, filter, selectedViewerProfile.Value);

        // The four-wave layout must not depend on CSS :has(), which is unsupported by the Host WebBrowser fallback.
        // The actual client width may be smaller than the Host's 360px outer-window minimum.
        var fourWaveClass = CanonicalWaveFilters(waveFilters).Any(x => x.Group.Equals("CATV", StringComparison.OrdinalIgnoreCase))
            ? " aircon-wave-four" : string.Empty;
        var waveGroup =
            "<div class='aircon-toolbar-wave-area" + fourWaveClass + "' data-role='wave-selector-group'>" +
            "<span class='aircon-toolbar-label'>放送波：</span>" +
            "<span class='aircon-wave-group' role='group' aria-label='放送波'>" + string.Join("", filterForms) + "</span>" +
            "</div>";

        var viewerProfileGroup = BuildViewerProfileSelector(context, viewerProfiles, action, window, filter, selectedTuner, selectedViewerProfile.Value, alwaysOnTop);

        var actionGroup =
            "<div class='aircon-toolbar-actions' data-role='viewer-and-window-actions'>" +
            refresh + power + pin + settings +
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

    private static string BuildViewerProfileSelector(RuntimeUiRenderContext context, ViewerProfileState state, ViewerOperation action, WindowOperation window, string filter, string selectedTuner, string selectedViewerProfile, bool alwaysOnTop)
    {
        if (!state.SelectorVisibleRecommended)
        {
            return "<div class='aircon-toolbar-profile-slot aircon-toolbar-profile-slot-reserved' data-role='viewer-profile-reserved'></div>";
        }

        var buttons = new List<string>();
        // The displayed wave is the authority for Viewer Profile visibility.
        // GR shows only GR profiles; BS and CS show only BSCS profiles.
        var waveProfiles = state.AvailableForWave(filter).ToList();
        foreach (var p in waveProfiles)
        {
            var active = p.Id.Equals(selectedViewerProfile, StringComparison.OrdinalIgnoreCase);
            var groups = p.AvailableGroups == null || p.AvailableGroups.Count == 0 ? "ALL" : string.Join(",", p.AvailableGroups);
            var displayLabel = ViewerProfileSegmentLabel(p, filter);
            var sharedSuffix = p.IsShared ? "（共用）" : string.Empty;
            var title = p.Name + sharedSuffix;
            var cls = "aircon-toolbar-button aircon-profile-button" + (active ? " aircon-profile-button-on" : string.Empty);

            var fields = BuildViewerActivateFields(filter, p.Id);
            var attrs = BuildHostActionAttributes(context, fields, "click", "refreshWindow");
            buttons.Add("<span class=\"aircon-profile-segment-form\" data-role=\"viewer-profile-selector\" data-viewer-profile=\"" + HtmlAttr(p.Id) + "\" data-groups=\"" + HtmlAttr(groups) + "\">" +
                "<button class=\"" + cls + "\" type=\"button\" " + attrs + " data-role=\"viewer-profile-option\" data-viewer-profile=\"" + HtmlAttr(p.Id) + "\" aria-pressed=\"" + (active ? BoolTrue : BoolFalse) + "\" title=\"" + HtmlAttr(title) + "\">" + Html(displayLabel) + "</button></span>");
        }

        if (buttons.Count == 0)
        {
            buttons.Add("<button class=\"aircon-toolbar-button aircon-profile-button aircon-profile-button-disabled\" type=\"button\" disabled aria-disabled=\"true\" title=\"視聴先候補なし\">-</button>");
        }

        return "<div class='aircon-toolbar-profile-slot aircon-profile-form' data-role='viewer-profile-selector'" +
            " data-tvair-current-viewer-profile='" + HtmlAttr(selectedViewerProfile) + "'" +
            " data-tvair-profile-count='" + state.SelectableProfiles.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "'>" +
            "<span class='aircon-profile-label'>チューナー </span>" +
            "<span class='aircon-profile-segments' role='group' aria-label='TVTest'>" + string.Join("", buttons) + "</span>" +
            "</div>";
    }

    private static string ViewerProfileSegmentLabel(ViewerProfileChoice profile, string wave)
    {
        // User-visible identity follows the TvAIr settings virtual-slot identifier (Tn/Sn).
        // ViewerProfileId remains the internal authority; physical tuner identity is never shown here.
        var frame = profile.TvTestFrameIndex > 0
            ? profile.TvTestFrameIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "?";
        return (NormalizeWaveFilter(wave) == "GR" ? "T" : NormalizeWaveFilter(wave) == "CATV" ? "C" : "S") + frame;
    }

    private static IReadOnlyList<WaveFilterRow> CanonicalWaveFilters(IReadOnlyList<WaveFilterRow> source)
    {
        static string LabelFor(string group) => group.Equals("GR", StringComparison.OrdinalIgnoreCase) ? "地上波" : group.ToUpperInvariant();
        var groups = StartupWaves
            .Where(group => source.Any(x => NormalizeWaveFilter(x.Group) == group))
            .ToList();
        return groups.Select(group => new WaveFilterRow(group, group, LabelFor(group)))
            .ToArray();
    }

    private static string BuildRows(RuntimeUiRenderContext context, IReadOnlyList<ServiceRow> services, IReadOnlyList<ViewerSessionRow> viewerSessions, ViewerOperation action, WindowOperation window, TunerChoice selectedTuner, ViewerProfileChoice selectedViewerProfile, string filter, bool zappingActive, string activeZappingWave, DateTimeOffset? powerOffDeadline)
    {
        if (services.Count == 0) return "<div class=\"aircon-empty\">表示対象の局がありません。</div>" + BuildZappingBar(context, false, viewerSessions, action, window, filter, selectedViewerProfile.Value, zappingActive, activeZappingWave, powerOffDeadline);
        var parts = new List<string>();
        if (!selectedViewerProfile.Enabled)
            parts.Add("<div class=\"aircon-empty\">局一覧のみ表示しています。視聴には対応する視聴用チューナーの設定が必要です。</div>");
        var rowIndex = 0;
        var selectedSession = viewerSessions
            .Where(x => x.IsActive && x.ViewerProfile.Equals(selectedViewerProfile.Value, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Current)
            .FirstOrDefault();
        foreach (var row in services)
        {
            // The active toolbar button already represents the selected wave.
            // Do not render an additional GR/BS/CS section band inside the scroll area.
            parts.Add(BuildServiceRow(context, row, action, window, selectedTuner, selectedViewerProfile, selectedSession, rowIndex++,
                zappingActive && NormalizeWaveFilter(activeZappingWave).Equals(NormalizeWaveFilter(filter), StringComparison.OrdinalIgnoreCase)));
        }
        parts.Add(BuildZappingBar(context, true, viewerSessions, action, window, filter, selectedViewerProfile.Value, zappingActive, activeZappingWave, powerOffDeadline));
        return string.Join("", parts);
    }

    private static string BuildZappingBar(RuntimeUiRenderContext context, bool enabled, IReadOnlyList<ViewerSessionRow> viewerSessions, ViewerOperation action, WindowOperation window, string filter, string selectedViewerProfile, bool active, string activeWave, DateTimeOffset? powerOffDeadline)
    {
        var canPost = enabled && !string.IsNullOrWhiteSpace(selectedViewerProfile)
            && !string.IsNullOrWhiteSpace(action.ActionEndpoint)
            && !string.IsNullOrWhiteSpace(action.ActionToken)
            && !string.IsNullOrWhiteSpace(window.WindowId);
        var disabled = canPost ? string.Empty : " disabled=\"disabled\" aria-disabled=\"true\"";
        var statusText = active ? "巡回中（" + NormalizeWaveFilter(activeWave) + "）" : "停止中";
        var buttonText = active ? "停止" : "開始";
        var title = active ? "ザッピングを停止" : GetZappingIntervalSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture) + "秒ごとに同じ放送波内で順送りします";
        var buttonClass = active ? "aircon-zapping-button aircon-zapping-button-on" : "aircon-zapping-button";
        var statusClass = active ? "aircon-zapping-status aircon-zapping-status-on" : "aircon-zapping-status aircon-zapping-status-off";
        var operation = active ? AirConActionZappingStop : AirConActionZappingStart;

        var fields = new Dictionary<string, string?>
        {
            ["operation"] = operation,
            ["wave"] = filter,
            ["viewerProfile"] = selectedViewerProfile,
            ["refreshQuery"] = "wave=" + filter + "&viewerProfile=" + selectedViewerProfile,
            ["clientVersion"] = ClientVersion
        };

        var selectedSleepSession = viewerSessions.FirstOrDefault(s => s.ViewerProfile.Equals(selectedViewerProfile, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(s.LeaseId));
        var sleepDisabled = selectedSleepSession == null ? " disabled=\"disabled\" aria-disabled=\"true\"" : string.Empty;
        var powerActive = powerOffDeadline.HasValue && powerOffDeadline.Value > DateTimeOffset.Now;
        var remainingHours = powerActive ? Math.Max(1, (int)Math.Ceiling((powerOffDeadline!.Value - DateTimeOffset.Now).TotalHours)) : 1;
        var powerOperation = powerActive ? AirConActionPowerOffStop : AirConActionPowerOffStart;
        var powerFields = new Dictionary<string, string?>(fields)
        {
            ["refreshQuery"] = "wave=" + filter + "&viewerProfile=" + selectedViewerProfile
        };

        return "<div id=\"aircon-zapping-bar\" class=\"aircon-zapping-bar\" data-aircon-zapping-wave=\"" + HtmlAttr(filter) + "\" data-aircon-zapping-profile=\"" + HtmlAttr(selectedViewerProfile) + "\" data-aircon-zapping-window=\"" + HtmlAttr(window.WindowId) + "\" data-aircon-zapping-active=\"" + (active ? BoolTrue : BoolFalse) + "\">"
            + "<div class=\"aircon-zapping-left\">"
            + "<span class=\"aircon-zapping-label\">ザッピング</span>"
            + "<span id=\"aircon-zapping-status\" class=\"" + statusClass + "\">" + Html(statusText) + "</span>"
            + "<button id=\"aircon-zapping-button\" class=\"" + buttonClass + "\" type=\"button\" " + BuildHostActionAttributes(context, new Dictionary<string, string?>(fields) { ["operation"] = operation, ["airconAction"] = operation }, "click", "patchWindow") + " title=\"" + HtmlAttr(title) + "\"" + disabled + ">" + Html(buttonText) + "</button></div>"
            + "<form id=\"aircon-sleep-form\" class=\"aircon-sleep-right aircon-sleep-form\">"
            + "<span id=\"aircon-sleep-label\" class=\"aircon-sleep-label\">" + (powerActive ? "終了まで" : "電源OFF") + "</span>"
            + "<select id=\"aircon-sleep-select\" name=\"hours\" class=\"aircon-sleep-select\" aria-label=\"終了タイマー\" title=\"終了タイマー\"" + (powerActive ? " hidden=\"hidden\"" : string.Empty) + "><option value=\"1\">1h</option><option value=\"2\">2h</option><option value=\"3\">3h</option><option value=\"4\">4h</option><option value=\"5\">5h</option><option value=\"6\">6h</option></select>"
            + "<span id=\"aircon-sleep-remaining\" class=\"aircon-sleep-remaining\"" + (powerActive ? string.Empty : " hidden=\"hidden\"") + ">" + remainingHours.ToString(System.Globalization.CultureInfo.InvariantCulture) + "h</span>"
            + "<button id=\"aircon-sleep-button\" class=\"aircon-sleep-button" + (powerActive ? " aircon-sleep-button-on" : string.Empty) + "\" type=\"button\" " + BuildHostActionAttributes(context, new Dictionary<string, string?>(powerFields) { ["operation"] = powerOperation }, "click", "patchWindow", "closestForm") + " title=\"" + (powerActive ? "終了タイマーを停止" : "選択中TVTestの終了タイマーを開始") + "\"" + sleepDisabled + ">" + (powerActive ? "停止" : "開始") + "</button>"
            + "</form></div>";
    }

    private static string BuildViewerReservationMenu(RuntimeUiRenderContext context, FloatingViewerData data, ViewerOperation action, WindowOperation window, string filter, string displayedViewerProfile)
    {
        var now = DateTimeOffset.Now;
        var limit = now.AddHours(6);
        var profiles = data.ViewerProfiles.AvailableForWave(filter).ToArray();
        if (profiles.Length == 0) return "<div id=\"aircon-reservation-menu\" class=\"aircon-reservation-menu\"></div>";

        var sections = new List<string>();
        foreach (var service in data.Services.Where(HasResolvedTriplet))
        {
            var events = data.ProgramEvents
                .Where(x => x.NetworkId == service.NetworkId && x.TransportStreamId == service.TransportStreamId && x.ServiceId == service.ServiceId)
                .Where(x => x.Start > now && x.Start < limit && x.End > x.Start && x.EventNumber is >= 0 and <= ushort.MaxValue)
                .OrderBy(x => x.Start)
                .ToArray();
            var items = new List<string>();
            foreach (var program in events)
            {
                var buttons = new List<string>();
                foreach (var profile in profiles)
                {
                    var existing = data.ViewerReservations.FirstOrDefault(x =>
                        x.ViewerProfileId.Equals(profile.Value, StringComparison.OrdinalIgnoreCase) &&
                        x.Service.NetworkId == service.NetworkId && x.Service.TransportStreamId == service.TransportStreamId && x.Service.ServiceId == service.ServiceId &&
                        x.EventId == (ushort)program.EventNumber && x.State.Equals("Scheduled", StringComparison.OrdinalIgnoreCase));
                    var conflict = existing == null && data.ViewerReservations.Any(x =>
                        x.ViewerProfileId.Equals(profile.Value, StringComparison.OrdinalIgnoreCase) &&
                        x.State.Equals("Scheduled", StringComparison.OrdinalIgnoreCase) &&
                        x.ScheduledStart == program.Start);
                    var label = ViewerProfileSegmentLabel(profile, filter);
                    if (conflict)
                    {
                        buttons.Add("<button type=\"button\" class=\"aircon-reservation-target aircon-reservation-target-conflict\" disabled aria-disabled=\"true\" title=\"同時刻に別の視聴予約があります\">" + Html(label) + "</button>");
                        continue;
                    }
                    var fields = new Dictionary<string, string?>
                    {
                        ["operation"] = AirConActionViewerReservationToggle,
                        ["airconAction"] = AirConActionViewerReservationToggle,
                        ["wave"] = filter,
                        ["viewerProfile"] = profile.Value,
                        ["returnViewerProfile"] = displayedViewerProfile,
                        ["networkId"] = service.NetworkId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["transportStreamId"] = service.TransportStreamId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["serviceId"] = service.ServiceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["eventId"] = program.EventNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["scheduledStart"] = program.Start.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                        ["scheduledEnd"] = program.End.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                        ["reservationId"] = existing?.ReservationId ?? string.Empty,
                        ["refreshQuery"] = "wave=" + filter + "&viewerProfile=" + profile.Value,
                        ["clientVersion"] = ClientVersion
                    };
                    var attrs = BuildHostActionAttributes(context, fields, "click", "refreshWindow");
                    var cls = "aircon-reservation-target" + (existing != null ? " aircon-reservation-target-on" : string.Empty);
                    var title = existing != null ? "解除" : "登録";
                    buttons.Add("<button type=\"button\" class=\"" + cls + "\" " + attrs + " title=\"" + title + "\">" + Html(label) + "</button>");
                }
                items.Add("<div class=\"aircon-reservation-program\"><span class=\"aircon-reservation-time\">" + Html(program.Start.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)) + " - " + Html(program.End.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)) + "</span><span class=\"aircon-reservation-title\">" + Html(program.Title) + "</span><span class=\"aircon-reservation-targets\">" + string.Join("", buttons) + "</span></div>");
            }
            if (items.Count == 0) items.Add("<div class=\"aircon-reservation-empty\">6時間以内の番組がありません。</div>");
            sections.Add("<div id=\"aircon-reservation-" + HtmlAttr(BuildServiceDomId(service)) + "\" class=\"aircon-reservation-section\">" + string.Join("", items) + "</div>");
        }
        return "<div id=\"aircon-reservation-menu\" class=\"aircon-reservation-menu\">" + string.Join("", sections) + "</div>";
    }

    private static string BuildServiceRow(RuntimeUiRenderContext context, ServiceRow row, ViewerOperation action, WindowOperation window, TunerChoice selectedTuner, ViewerProfileChoice selectedViewerProfile, ViewerSessionRow? selectedSession, int rowIndex, bool zappingActive)
    {
        var hasTriplet = HasResolvedTriplet(row);
        var parityClass = (rowIndex % 2 == 0) ? "aircon-row-even" : "aircon-row-odd";
        var selectedProfileViewing = row.ViewingViewerProfiles.Contains(selectedViewerProfile.Value);
        var anotherProfileViewing = row.ViewingViewerProfiles.Any(profile =>
            !profile.Equals(selectedViewerProfile.Value, StringComparison.OrdinalIgnoreCase));
        var emphasis = ResolveServiceEmphasis(selectedProfileViewing, anotherProfileViewing, zappingActive);
        var emphasisClass = ServiceEmphasisClass(emphasis);
        var cls = "aircon-row " + parityClass
            + (emphasisClass.Length > 0 ? " " + emphasisClass : string.Empty)
            + (!hasTriplet ? " aircon-row-disabled" : string.Empty);
        var canView = hasTriplet && selectedViewerProfile.Enabled
            && selectedViewerProfile.IsAvailableForWave(row.ProgramGuideFilterGroup);
        var title = !selectedViewerProfile.Enabled ? "対応する視聴用チューナーが設定されていません" : hasTriplet ? "ダブルクリックで視聴 / 右クリックで視聴予約" : "このチャンネルは現在視聴できません";
        var current = string.IsNullOrWhiteSpace(row.CurrentTitle) ? "番組情報取得中" : row.CurrentTitle;
        var currentClass = "aircon-current";
        var currentTitleAttr = " title=\"\" aria-label=\"" + HtmlAttr(current) + "\" data-aircon-marquee=\"true\"";
        var serviceDomId = hasTriplet ? BuildServiceDomId(row) : string.Empty;
        // Never rename a row's DOM ID when it becomes selected: rapid Host retunes must
        // be able to address the previous and next rows without a full navigation.
        var rowId = hasTriplet ? " id=\"" + HtmlAttr(serviceDomId) + "\"" : string.Empty;
        var serviceDataId = hasTriplet ? " data-aircon-service-id=\"" + HtmlAttr(serviceDomId) + "\" data-aircon-zapping-row=\"true\" data-aircon-zapping-index=\"" + rowIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\"" : string.Empty;
        var content =
            $"<span class=\"aircon-service\">{Html(row.ServiceName)}</span>" +
            BuildCurrentTimeHtml(row) +
            $"<span class=\"{currentClass}\"{currentTitleAttr}><span class=\"aircon-current-track\"><span class=\"aircon-current-text\">{Html(current)}</span><span class=\"aircon-current-copy\" aria-hidden=\"true\">{Html(current)}</span></span></span>";

        if (!canView)
        {
            // Non-actionable rows still retain their stable station identity so
            // status patches and scroll targets never disappear with button state.
            return $"<div{rowId}{serviceDataId} class=\"{cls}\" title=\"{HtmlAttr(title)}\">" + content + "</div>";
        }

        var tuneFields = BuildViewerTuneFields(row, action, window, selectedTuner, selectedViewerProfile, selectedSession);
        // The Host must honor the returned RefreshRequested value: running-Viewer
        // retunes patch in the session event; only a first launch requests a render.
        var hostAttrs = BuildHostActionAttributes(context, tuneFields, "dblclick", "hostHandled");
        return $"<div{rowId}{serviceDataId} class=\"{cls}\" title=\"{HtmlAttr(title)}\" {hostAttrs} data-aircon-viewer-tune=\"true\">" + content + "</div>";
    }

    private static Dictionary<string, string?> BuildViewerTuneFields(ServiceRow row, ViewerOperation action, WindowOperation window, TunerChoice selectedTuner, ViewerProfileChoice selectedViewerProfile, ViewerSessionRow? selectedSession)
    {
        var payload = BuildViewerStartPayload(row, selectedTuner, selectedViewerProfile);
        var fields = new Dictionary<string, string?>
        {
            ["operation"] = AirConActionViewerTune,
            ["wave"] = payload.BroadcastGroup,
            ["viewerProfile"] = payload.ViewerProfile,
            ["networkId"] = payload.NetworkId,
            ["transportStreamId"] = payload.TransportStreamId,
            ["serviceId"] = payload.ServiceId,
            ["channelSpace"] = payload.ChannelSpace,
            ["channelIndex"] = payload.ChannelIndex,
            ["channelArgument"] = payload.ChannelArgument,
            ["viewerProfileName"] = payload.ViewerProfileName,
            ["refreshQuery"] = BuildViewerRefreshQuery(row, selectedViewerProfile.Value),
            ["clientVersion"] = ClientVersion
        };
        AppendViewerSessionContractFields(fields, selectedSession == null ? null : new ViewerSessionContractState(selectedSession.ViewerSessionId, selectedSession.Generation));
        if (!selectedTuner.IsAuto)
        {
            fields["preferredTunerName"] = selectedTuner.Name;
            fields["preferredDid"] = selectedTuner.Did;
            fields["preferredSlot"] = selectedTuner.SlotIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return fields;
    }

    private static string BuildCurrentTimeHtml(ServiceRow row)
    {
        var start = FormatTime(row.CurrentStart);
        var end = FormatTime(row.CurrentEnd);
        var title = string.IsNullOrEmpty(start) && string.IsNullOrEmpty(end)
            ? string.Empty
            : " title=\"" + HtmlAttr((start.Length > 0 ? start : "--:--") + " - " + (end.Length > 0 ? end : "--:--")) + "\"";
        return "<span class=\"aircon-time\"" + title + ">"
            + "<span class=\"aircon-time-start\">" + Html(start) + "</span>"
            + "<span class=\"aircon-time-end\">" + Html(end) + "</span>"
            + "</span>";
    }

    private static string FormatTime(DateTimeOffset? value)
        => value.HasValue ? value.Value.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) : string.Empty;

    private static int CalculateServiceColumnWidth(IReadOnlyList<ServiceRow> services)
    {
        var maxTextWidth = 0;
        foreach (var service in services)
        {
            var name = service.ServiceName ?? string.Empty;
            maxTextWidth = Math.Max(maxTextWidth, EstimateServiceNameWidthPx(name));
        }

        var width = maxTextWidth + ToolWindowServiceColumnHorizontalReservePx;
        return Math.Max(ToolWindowServiceColumnMinimumWidthPx, Math.Min(ToolWindowServiceColumnMaximumWidthPx, width));
    }

    private static int EstimateServiceNameWidthPx(string text)
    {
        var width = 0;
        foreach (var ch in text)
        {
            width += IsNarrowDisplayChar(ch) ? 7 : 12;
        }
        return width;
    }

    private static bool IsNarrowDisplayChar(char ch)
        => (ch >= '\u0020' && ch <= '\u007e') || (ch >= '\uff61' && ch <= '\uff9f');

    private static string BuildServiceDomId(ServiceRow row)
        => BuildServiceDomId(row.NetworkId, row.TransportStreamId, row.ServiceId);

    private static string BuildServiceDomId(int networkId, int transportStreamId, int serviceId)
        => "svc-" + networkId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "-" + transportStreamId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "-" + serviceId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string BuildServiceDomIdFromKey(string serviceKey)
    {
        var parts = serviceKey.Split(':');
        return parts.Length == 3
            && int.TryParse(parts[0], out var nid) && nid > 0
            && int.TryParse(parts[1], out var tsid) && tsid > 0
            && int.TryParse(parts[2], out var sid) && sid > 0
            ? BuildServiceDomId(nid, tsid, sid) : string.Empty;
    }

    private string GetRenderedServiceKey(string windowId)
        => GetWindowDisplay(windowId)?.ServiceKey ?? string.Empty;

    private string GetDisplayedViewerProfile(string windowId)
        => GetWindowDisplay(windowId)?.ViewerProfile ?? string.Empty;

    private string GetRenderedServiceRowId(string windowId, string displayedWave)
    {
        var displayed = GetWindowDisplay(windowId);
        if (displayed == null || !displayed.Wave.Equals(displayedWave, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(displayed.ServiceKey)) return string.Empty;
        // A service outside the displayed wave has no row to patch.
        if (!AIrConNewApiBridge.ListServices().Any(row =>
            ServiceKey(row.NetworkId, row.TransportStreamId, row.ServiceId)
                .Equals(displayed.ServiceKey, StringComparison.OrdinalIgnoreCase)
            && NormalizeWaveFilter(row.BroadcastType).Equals(displayedWave, StringComparison.OrdinalIgnoreCase)))
            return string.Empty;
        return BuildServiceDomIdFromKey(displayed.ServiceKey);
    }

    private static bool HasResolvedTriplet(ServiceRow row)
        => row.NetworkId > 0 && row.TransportStreamId > 0 && row.ServiceId > 0;

    // ContentRoute is a Plugin navigation path, never a bare query string.
    // Keep the existing route when the Viewer Profile is intentionally absent
    // (e.g. a wave without a configured viewer).
    private static string BuildViewerContentRoute(string wave, string? viewerProfile)
    {
        var route = "/plugin/aircon?wave=" + Url(wave);
        return string.IsNullOrWhiteSpace(viewerProfile)
            ? route
            : route + "&viewerProfile=" + Url(viewerProfile);
    }

    private static string Url(string value) => WebUtility.UrlEncode(value);

    private static string BuildViewerRefreshQuery(ServiceRow row, string selectedViewerProfile)
    {
        var wave = string.IsNullOrWhiteSpace(row.ProgramGuideFilterGroup) ? "GR" : row.ProgramGuideFilterGroup;
        return "wave=" + Url(wave)
            + "&viewerProfile=" + Url(selectedViewerProfile);
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
            return "<span class=\"aircon-action-form\" data-role=\"toolbar-action-slot\"><button class=\"aircon-toolbar-button aircon-action-button aircon-action-topmost aircon-toolbar-button-disabled\" type=\"button\" aria-disabled=\"true\" aria-label=\"前面固定\" title=\"AIrConを常に前面\">⚑</button></span>";
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

    private static string BuildToolbarStopForm(RuntimeUiRenderContext context, ViewerSessionRow? session, ViewerOperation action, WindowOperation window, string filter, string selectedViewerProfile)
    {
        if (string.IsNullOrWhiteSpace(action.ActionEndpoint)
            || string.IsNullOrWhiteSpace(action.ActionToken)
            || string.IsNullOrWhiteSpace(window.WindowId))
        {
            return "<span class=\"aircon-action-form\" data-role=\"toolbar-action-slot\"><button id=\"aircon-viewer-power-button\" class=\"aircon-toolbar-button aircon-action-button aircon-action-power aircon-toolbar-button-disabled\" type=\"button\" aria-disabled=\"true\" aria-label=\"視聴停止\" title=\"現在は視聴を停止できません\">⏻</button></span>";
        }

        if (session == null || string.IsNullOrWhiteSpace(session.LeaseId) || !session.ViewerProfile.Equals(selectedViewerProfile, StringComparison.OrdinalIgnoreCase))
        {
            var disabledTitle = string.IsNullOrWhiteSpace(selectedViewerProfile)
                ? "停止する視聴がありません"
                : "選択中のTVTestは停止済みです";
            return "<span class=\"aircon-action-form\" data-role=\"toolbar-action-slot\"><button id=\"aircon-viewer-power-button\" class=\"aircon-toolbar-button aircon-action-button aircon-action-power aircon-toolbar-button-disabled\" type=\"button\" aria-disabled=\"true\" aria-label=\"視聴停止\" title=\"" + HtmlAttr(disabledTitle) + "\">⏻</button></span>";
        }

        var fields = new Dictionary<string, string?>
        {
            ["pluginId"] = action.PluginId,
            ["plugin-id"] = action.PluginId,
            ["routeSegment"] = action.RouteSegment,
            ["route-segment"] = action.RouteSegment,
            ["action"] = ActionPluginOwned,
            ["safeEventAction"] = ActionPluginOwned,
            ["safe-event-action"] = ActionPluginOwned,
            ["token"] = action.ActionToken,
            ["actionToken"] = action.ActionToken,
            ["action-token"] = action.ActionToken,
            ["responseMode"] = "refreshWindow",
            ["response-mode"] = "refreshWindow",
            ["windowId"] = window.WindowId,
            ["window-id"] = window.WindowId,
            ["currentWindowId"] = window.WindowId,
            ["current-window-id"] = window.WindowId,
            ["refreshTarget"] = RefreshTargetContent,
            ["refresh-target"] = RefreshTargetContent,
            ["wave"] = filter,
            ["viewerProfile"] = selectedViewerProfile,
            ["viewer-profile"] = selectedViewerProfile,
            ["clientVersion"] = ClientVersion
        };
        fields["operation"] = AirConActionViewerPowerOff;
        fields["airconAction"] = AirConActionViewerPowerOff;
        var attrs = BuildHostActionAttributes(context, fields, "click", "refreshWindow");
        return "<button id=\"aircon-viewer-power-button\" class=\"aircon-toolbar-button aircon-action-button aircon-action-power\" type=\"button\" " + attrs + " aria-label=\"視聴停止\" title=\"ザッピングを停止してAIrCon管理の現在の視聴TVTestを閉じる\">⏻</button>";
    }

    private static string BuildOpenWindowForm(WindowOperation window, bool alwaysOnTop, string filter, string selectedTuner, string selectedViewerProfile)
    {
        if (!window.CanOpen) return "<span>AIrConを開けません。</span>";
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
            ["title"] = PluginToolWindowTitle,
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

    private static string BuildSettingsButton(RuntimeUiRenderContext context, ViewerOperation action, WindowOperation window, string filter, string selectedViewerProfile)
    {
        if (string.IsNullOrWhiteSpace(action.ActionToken) || string.IsNullOrWhiteSpace(window.WindowId))
            return "<span class=\"aircon-action-form\" data-role=\"toolbar-action-slot\"><button class=\"aircon-toolbar-button aircon-action-button aircon-toolbar-button-disabled\" type=\"button\" aria-disabled=\"true\" aria-label=\"設定\" title=\"現在は設定を開けません\">⚙</button></span>";
        var fields = new Dictionary<string, string?>
        {
            ["operation"] = AirConActionSettingsOpen,
            ["windowId"] = window.WindowId,
            ["wave"] = filter,
            ["viewerProfile"] = selectedViewerProfile,
            ["clientVersion"] = ClientVersion
        };
        var attrs = BuildHostActionAttributes(context, fields, "click", "hostHandled");
        return "<span class=\"aircon-action-form\" data-role=\"toolbar-action-slot\"><button class=\"aircon-toolbar-button aircon-action-button aircon-action-settings\" type=\"button\" " + attrs + " aria-label=\"設定\" title=\"AIrCon設定\">⚙</button></span>";
    }

    private static string BuildSettingsHtml(RuntimeUiRenderContext context, ViewerOperation action, WindowOperation window, AirConSettings settings, string returnWave, string returnViewerProfile)
    {
        var saveFields = new Dictionary<string, string?>
        {
            ["operation"] = AirConActionSettingsSave,
            ["windowId"] = window.WindowId,
            ["returnWave"] = returnWave,
            ["returnViewerProfile"] = returnViewerProfile,
            ["clientVersion"] = ClientVersion
        };
        var closeFields = new Dictionary<string, string?>
        {
            ["operation"] = AirConActionSettingsClose,
            ["windowId"] = window.WindowId,
            ["returnWave"] = returnWave,
            ["returnViewerProfile"] = returnViewerProfile,
            ["clientVersion"] = ClientVersion
        };
        var saveAttrs = BuildHostActionAttributes(context, saveFields, "click", "hostHandled", "closestForm");
        var closeAttrs = BuildHostActionAttributes(context, closeFields, "click", "hostHandled");
        var checkedAttr = settings.RememberWindowPlacement ? " checked" : string.Empty;
        var intervalOptions = string.Concat(ZappingIntervals.Select(value =>
            $"<option value=\"{value}\"{(settings.ZappingIntervalSeconds == value ? " selected" : string.Empty)}>{value}秒</option>"));
        var waveOptions = string.Concat(StartupWaves.Select(value =>
            $"<option value=\"{value}\"{(settings.StartupWave.Equals(value, StringComparison.OrdinalIgnoreCase) ? " selected" : string.Empty)}>{value}</option>"));
        var css = ResolveThemePalette(context).Apply(@"html,body{margin:0;width:100%;height:100%;background:var(--aircon-page);color:var(--aircon-text);font-family:Meiryo,'Yu Gothic',Arial,sans-serif;font-size:12px;overflow:hidden}.aircon-runtime-root,.aircon-runtime-root *{box-sizing:border-box}.aircon-settings{height:100%;padding:14px;background:var(--aircon-page)}.aircon-settings-title{font-size:14px;font-weight:bold;margin:0 0 14px}.aircon-settings-row{display:table;width:100%;margin:0 0 12px}.aircon-settings-label{display:table-cell;width:62%;vertical-align:middle}.aircon-settings-control{display:table-cell;text-align:right;vertical-align:middle}.aircon-settings select{width:118px;height:26px;border:1px solid var(--aircon-control-border);background:var(--aircon-input);color:var(--aircon-control-text);font-family:inherit}.aircon-toggle{width:18px;height:18px;vertical-align:middle}.aircon-settings-actions{text-align:right;margin-top:18px}.aircon-settings-button{min-width:64px;height:28px;margin-left:8px;border:1px solid var(--aircon-button-border);border-radius:3px;background:var(--aircon-button-bg);color:var(--aircon-button-text);font-family:inherit;font-weight:bold;cursor:pointer}.aircon-settings-button:hover{background:var(--aircon-button-hover)}");
        return "<!doctype html><html><head><meta charset=\"utf-8\"><style>" + css + "</style></head><body><div class=\"aircon-settings\"><div class=\"aircon-settings-title\">AIrCon設定</div><form><div class=\"aircon-settings-row\"><div class=\"aircon-settings-label\">ウィンドウ位置とサイズを記憶する</div><div class=\"aircon-settings-control\"><input class=\"aircon-toggle\" type=\"checkbox\" name=\"" + SettingRememberPlacement + "\" value=\"true\"" + checkedAttr + "></div></div><div class=\"aircon-settings-row\"><div class=\"aircon-settings-label\">ザッピング間隔</div><div class=\"aircon-settings-control\"><select name=\"" + SettingZappingIntervalSeconds + "\">" + intervalOptions + "</select></div></div><div class=\"aircon-settings-row\"><div class=\"aircon-settings-label\">起動時に表示する放送波</div><div class=\"aircon-settings-control\"><select name=\"" + SettingStartupWave + "\">" + waveOptions + "</select></div></div><div class=\"aircon-settings-actions\"><button class=\"aircon-settings-button\" type=\"button\" " + closeAttrs + ">戻る</button><button class=\"aircon-settings-button\" type=\"button\" " + saveAttrs + ">保存</button></div></form></div></body></html>";
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

    private static string BuildFilterForm(RuntimeUiRenderContext context, string group, string label, string current, ViewerProfileChoice selectedViewerProfile, ViewerProfileState profiles)
    {
        var isCurrent = group.Equals(current, StringComparison.OrdinalIgnoreCase);
        var cls = isCurrent ? "aircon-toolbar-button aircon-wave-button aircon-wave-button-on" : "aircon-toolbar-button aircon-wave-button";
        var availableProfiles = profiles.AvailableForWave(group).ToArray();
        var targetProfile = availableProfiles.FirstOrDefault(x => x.Id.Equals(selectedViewerProfile.Value, StringComparison.OrdinalIgnoreCase))
            ?? availableProfiles.FirstOrDefault();
        var fields = new Dictionary<string, string?>
        {
            ["operation"] = targetProfile == null ? AirConActionBrowseWave : AirConActionViewerActivate,
            ["wave"] = group,
            ["viewerProfile"] = targetProfile?.Id ?? string.Empty,
            ["waveButton"] = "true",
            ["refreshQuery"] = "wave=" + group + "&viewerProfile=" + (targetProfile?.Id ?? string.Empty),
            ["clientVersion"] = ClientVersion
        };
        // Wave buttons browse a wave and may focus an already-running compatible viewer.
        // The action handler skips unchanged navigation and redundant foreground requests.
        // With no viewer profile (notably CATV), browseWave must still permit list-only browsing.
        // Never remove it as a UI cleanup, and never start TVTest or retune merely on wave click.
        // AIrCon owns row positioning inside its own HTML after render.
        var attrs = BuildHostActionAttributes(context, fields, "click", "hostHandled");
        return "<span class=\"aircon-nav-form\" data-role=\"wave-selector\" data-wave=\"" + HtmlAttr(group) + "\">" +
            $"<button class=\"{cls}\" type=\"button\" {attrs} data-role=\"wave-selector\" data-wave=\"{HtmlAttr(group)}\" aria-pressed=\"{(isCurrent ? "true" : "false")}\" title=\"{HtmlAttr("放送波: " + label)}\">{Html(label)}</button></span>";
    }

    private static Dictionary<string, string?> BuildViewerActivateFields(string wave, string viewerProfile)
    {
        return new Dictionary<string, string?>
        {
            ["operation"] = AirConActionViewerActivate,
            ["wave"] = wave,
            ["viewerProfile"] = viewerProfile,
            ["refreshQuery"] = "wave=" + wave + "&viewerProfile=" + viewerProfile,
            ["clientVersion"] = ClientVersion
        };
    }

    private static IReadOnlyList<TunerChoice> BuildTunerChoices(IReadOnlyList<ViewerTunerRow> tuners, string filter)
    {
        var list = new List<TunerChoice> { TunerChoice.Auto };
        foreach (var t in tuners.Where(x => x.IsSelectableForViewer).OrderBy(x => x.SlotIndex))
        {
            if (filter == "GR" && !t.ProgramGuideFilterGroup.Equals("GR", StringComparison.OrdinalIgnoreCase)) continue;
            if ((filter == "BS" || filter == "CS") && (t.ProgramGuideFilterGroup.Equals("GR", StringComparison.OrdinalIgnoreCase) || t.ProgramGuideFilterGroup.Equals("CATV", StringComparison.OrdinalIgnoreCase))) continue;
            if (filter == "CATV" && !t.ProgramGuideFilterGroup.Equals("CATV", StringComparison.OrdinalIgnoreCase)) continue;
            var value = t.Name + "|" + t.Did + "|" + t.SlotIndex;
            list.Add(new TunerChoice(value, t.Name, t.Did, t.SlotIndex, t.Name, false));
        }
        return list;
    }

    private static TunerChoice ResolveSelectedTuner(IReadOnlyList<TunerChoice> choices, string value)
        => choices.FirstOrDefault(x => x.Value.Equals(value, StringComparison.OrdinalIgnoreCase)) ?? TunerChoice.Auto;

}
