AIrCon v1.0.0
==============================

TvAIr 用の視聴操作プラグインです。


内容物
------------------------------

README.txt
AIrCon.BasicPlugin.sln
AIrCon.BasicPlugin.plugin.json

AIrCon.BasicPlugin\
  AIrCon.BasicPlugin.csproj
  AIrConPlugin.cs
  AIrCon.ico

TvAIrPlugin\
  IPluginContext.cs
  ITvAIrPlugin.cs
  PluginContracts.cs
  TvAIrPlugin.csproj


動作条件
------------------------------

- TvAIr
- Visual Studio 2022
- .NET 8.0 Windows Desktop Runtime / SDK


ビルド
------------------------------

1. AIrCon.BasicPlugin.sln を Visual Studio 2022 で開きます。
2. Release 構成でビルドします。

生成される DLL:

AIrCon.BasicPlugin\bin\Release\AIrCon.BasicPlugin.dll


インストール
------------------------------

以下の DLL をコピーします。

AIrCon.BasicPlugin\bin\Release\AIrCon.BasicPlugin.dll

コピー先:

TvAIr\Plugins\AIrCon.BasicPlugin.dll

コピー後、TvAIr を再起動します。


主な機能
------------------------------

- チャンネル一覧表示
- 視聴開始
- チャンネル切替
- 視聴停止
- 前面表示切替


アンインストール
------------------------------

以下のファイルを削除します。

TvAIr\Plugins\AIrCon.BasicPlugin.dll

削除後、TvAIr を再起動します。


バージョン
------------------------------

AIrCon v1.0.0
