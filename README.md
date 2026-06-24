# AIrCon v1.0.0

AIrCon is a TvAIr viewer-control plugin.

## Features

- Channel list display
- Start viewing
- Switch channels
- Stop viewing
- Toggle foreground display

## Requirements

- TvAIr
- Visual Studio 2022
- .NET 8.0 Windows Desktop Runtime / SDK

## Build

1. Open `AIrCon.BasicPlugin.sln` in Visual Studio 2022.
2. Select the `Release` configuration.
3. Build the solution.

The main output DLL is generated at:

```text
AIrCon.BasicPlugin\bin\Release\AIrCon.BasicPlugin.dll
```

## Install

Copy the built DLL to the TvAIr plugin folder:

```text
TvAIr\Plugins\AIrCon.BasicPlugin.dll
```

Then restart TvAIr.

`AIrCon.BasicPlugin.plugin.json` is included as the plugin metadata source file for this project. If your TvAIr distribution requires plugin metadata to be copied separately, place it according to the TvAIr plugin loading rules for that distribution.

## Uninstall

Delete the following file and restart TvAIr:

```text
TvAIr\Plugins\AIrCon.BasicPlugin.dll
```

## Version

AIrCon v1.0.0

## License

No open-source license has been selected for this repository yet. Public visibility on GitHub does not by itself grant reuse, modification, or redistribution rights beyond GitHub's own platform terms.
