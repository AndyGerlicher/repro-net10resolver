# MSBuild Bug: `MSBuildLoadContext.Load()` causes `FileLoadException` for self-contained .NET apps

Minimal repro for a bug where in-process MSBuild project evaluation crashes with `FileLoadException` in self-contained .NET 10 applications.

## Problem

`MSBuildLoadContext.Load()` has a final fallback that calls `AssemblyLoadContext.Default.LoadFromAssemblyPath()` with assemblies from `BuildEnvironmentHelper.Instance.CurrentMSBuildToolsDirectory` (the VS MSBuild bin directory).

When the Default ALC already has the same assembly loaded from the app's TPA with a different **file version** (e.g., `System.Text.Json` 10.0.0.0 from the runtime vs 10.0.0.2 from VS), `Default.LoadFromAssemblyPath()` throws `FileLoadException` because it refuses to load a different file for an already-loaded assembly.

### Root cause in `MSBuildLoadContext.Load()`

```csharp
// This is the problematic fallback:
string text3 = Path.Combine(
    BuildEnvironmentHelper.Instance.CurrentMSBuildToolsDirectory,
    assemblyName.Name + ".dll");

if (FileSystems.Default.FileExists(text3))
    return AssemblyLoadContext.Default.LoadFromAssemblyPath(text3);  // THROWS
```

`Default.LoadFromAssemblyPath()` requires the file to be the exact same file/version as what's already loaded. A self-contained app bundles runtime assemblies (e.g., `System.Text.Json` 10.0.0.0) while VS ships serviced versions (10.0.0.2). Same assembly name, different file versions → `FileLoadException`.

## Suggested fix

Use `this.LoadFromAssemblyPath()` instead of `AssemblyLoadContext.Default.LoadFromAssemblyPath()` for the fallback, or return `null` to let normal resolution handle it. Loading into the custom ALC avoids the conflict entirely.

## Prerequisites

- Windows with **Visual Studio 18 Preview** installed
- .NET 10 SDK

## Repro steps

```bash
dotnet publish -c Release
bin\Release\net10.0\win-x64\publish\net10-msbuild-load.exe
```

**Expected:** Project evaluation succeeds.

**Actual:** `FileLoadException` from `MSBuildLoadContext` during SDK resolution.

## What the repro does

1. Locates the VS 18 Preview MSBuild installation
2. Shows the assembly version mismatch between the app's TPA and VS MSBuild directory
3. Attempts in-proc MSBuild project evaluation (supported scenario), which triggers SDK resolution and hits the bug
4. On failure, applies a workaround that patches `MSBuildLoadContext.WellKnownAssemblyNames` to include the conflicting assemblies, then retries successfully
