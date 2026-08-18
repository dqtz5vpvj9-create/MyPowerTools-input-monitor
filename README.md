<!-- mypowertools-materialized-source -->
# Input Monitor for MyPowerTools

This repository owns the `input-monitor` tool source and its buildable
MyPowerTools adapter. Its submodule origin is:

```text
https://github.com/dqtz5vpvj9-create/MyPowerTools-input-monitor.git
```

## Repository layout

- `original-source/` contains the captured macOS InputMonitor app.
- `current-integration/` contains the suite adapter source, package manifest,
  Avalonia dashboard, and Windows capture host.
- `build.ps1` builds the adapter against the public projects in a MyPowerTools
  superproject checkout.
- `tool-release.json` defines the adapter, package template, output contract,
  and required suite project references.
- `artifacts/package/` is the generated package staging directory and is ignored
  by Git.

## Build

From a MyPowerTools submodule checkout, the script discovers the superproject:

```powershell
pwsh.exe -NoLogo -NoProfile -NonInteractive -File .\build.ps1
```

From an independent checkout, pass the suite path explicitly:

```powershell
pwsh.exe -NoLogo -NoProfile -NonInteractive -File .\build.ps1 `
  -MyPowerToolsRepoRoot C:\path\to\MyPowerTools `
  -Configuration Release
```

The adapter project requires the `MyPowerToolsRepoRoot` MSBuild property and
references `MyPowerTools.Abstractions`, `MyPowerTools.Protocol`, and
`MyPowerTools.Platform.Abstractions` from that checkout. Successful builds stage
the manifest, UI resources, adapter DLL, PDB, SQLite native runtimes, and
surface assemblies under `artifacts/package`.

Package integrity metadata must be refreshed by the suite signing step after the
adapter binary is injected.

Overlay into a Dev install from the superproject:

```powershell
pwsh.exe -NoLogo -NoProfile -NonInteractive -File .\scripts\Start-MyPowerTools-Dev.ps1 `
  -Scope Tools -ToolId input-monitor
```
