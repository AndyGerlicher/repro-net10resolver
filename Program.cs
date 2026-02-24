// Repro: MSBuildLoadContext.Load() causes FileLoadException in self-contained .NET 10 apps
//
// MSBuildLoadContext.Load() has a fallback that calls:
//   AssemblyLoadContext.Default.LoadFromAssemblyPath(pathInVSMSBuildDir)
//
// For a self-contained app, the Default ALC already has assemblies like System.Text.Json
// loaded from the app's TPA (e.g. version 10.0.0.0). VS ships a different file version
// (e.g. 10.0.0.2). Default.LoadFromAssemblyPath() throws FileLoadException because it
// refuses to load a different file for an already-loaded assembly.
//
// This breaks in-proc MSBuild project evaluation when VS 18 Preview (or any VS that ships
// newer serviced assemblies) is installed.
//
// Prerequisites: VS 18 Preview installed
//
// To run:
//   dotnet publish -c Release
//   bin\Release\net10.0\win-x64\publish\net10-msbuild-load.exe
//
// Expected: project evaluation succeeds
// Actual:   FileLoadException from MSBuildLoadContext during SDK resolution

using System;
using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using Microsoft.Build.Evaluation;

// Point MSBuild at the VS installation (this is the supported in-proc scenario)
var vsMSBuildPath = FindVSMSBuildPath();
if (vsMSBuildPath == null)
{
    Console.WriteLine("ERROR: Could not find VS 18 Preview MSBuild. Install VS 18 Preview to reproduce.");
    return;
}

Console.WriteLine($"VS MSBuild path: {vsMSBuildPath}");

// Show the version conflict
var stjInVs = Path.Combine(vsMSBuildPath, "System.Text.Json.dll");
if (File.Exists(stjInVs))
{
    var vsVer = System.Reflection.AssemblyName.GetAssemblyName(stjInVs).Version;
    var appVer = typeof(System.Text.Json.JsonSerializer).Assembly.GetName().Version;
    Console.WriteLine($"System.Text.Json in VS MSBuild dir: {vsVer}");
    Console.WriteLine($"System.Text.Json in app (TPA):      {appVer}");
    if (!vsVer!.Equals(appVer))
        Console.WriteLine(">>> VERSION MISMATCH - this will cause FileLoadException <<<\n");
}

// Set MSBUILD_EXE_PATH so MSBuild discovers VS targets, SDK resolvers, etc.
var msbuildExe = Path.Combine(vsMSBuildPath, "MSBuild.exe");
Environment.SetEnvironmentVariable("MSBUILD_EXE_PATH", msbuildExe);
Console.WriteLine($"MSBUILD_EXE_PATH = {msbuildExe}");

// Set MSBuildExtensionsPath so Microsoft.Common.props is found correctly
var msbuildCurrent = Path.GetFullPath(Path.Combine(vsMSBuildPath, "..", ".."));
Environment.SetEnvironmentVariable("MSBuildExtensionsPath", msbuildCurrent);

// Create a simple SDK-style project to evaluate (triggers SDK resolution)
var projPath = Path.Combine(Path.GetTempPath(), "msbuild-repro.proj");
File.WriteAllText(projPath, """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
""");

try
{
    Console.WriteLine($"Evaluating {projPath}...\n");
    using var pc = new ProjectCollection();
    var project = pc.LoadProject(projPath);
    Console.WriteLine("SUCCESS: Project evaluated without errors.");
}
catch (Exception ex)
{
    Console.WriteLine($"FAILED: {ex.GetType().Name}");
    Console.WriteLine(ex.Message);

    if (ex.ToString().Contains("MSBuildLoadContext") &&
        ex.ToString().Contains("FileLoadException"))
    {
        Console.WriteLine("\n>>> This is the MSBuildLoadContext.Load() bug <<<");
        Console.WriteLine("The fallback 'Default.LoadFromAssemblyPath()' fails because the");
        Console.WriteLine("Default ALC already has the assembly from the app's TPA with a");
        Console.WriteLine("different file version than what VS ships.");
        Console.WriteLine("\nFix: In MSBuildLoadContext.Load(), change the final fallback from:");
        Console.WriteLine("  return AssemblyLoadContext.Default.LoadFromAssemblyPath(text3);");
        Console.WriteLine("to:");
        Console.WriteLine("  return LoadFromAssemblyPath(text3);  // load into this ALC, not Default");
        Console.WriteLine("or simply return null to let normal resolution handle it.");

        // --- Demonstrate the workaround ---
        Console.WriteLine("\n=== Applying workaround: patching WellKnownAssemblyNames ===\n");
        ApplyWorkaround(vsMSBuildPath);

        try
        {
            // Retry with a plain project to prove MSBuild evaluation works.
            // (SDK projects need additional MSBuildExtensionsPath config which is
            // orthogonal to this bug.)
            var retryProjPath = Path.Combine(Path.GetTempPath(), "msbuild-repro-retry.proj");
            File.WriteAllText(retryProjPath, """
            <Project>
              <PropertyGroup>
                <TestProp>Hello from MSBuild</TestProp>
              </PropertyGroup>
            </Project>
            """);

            Console.WriteLine($"Evaluating plain project with workaround...\n");
            using var pc2 = new ProjectCollection();
            var project2 = pc2.LoadProject(retryProjPath);
            Console.WriteLine($"SUCCESS: Evaluated! TestProp = {project2.GetPropertyValue("TestProp")}");
            Console.WriteLine("The FileLoadException is gone.");
            File.Delete(retryProjPath);

            // Now retry the SDK project to show SDK resolution also works
            File.WriteAllText(projPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
            Console.WriteLine($"\nEvaluating SDK project with workaround...\n");
            using var pc3 = new ProjectCollection();
            var project3 = pc3.LoadProject(projPath);
            Console.WriteLine($"SUCCESS: SDK project evaluated! TargetFramework = {project3.GetPropertyValue("TargetFramework")}");
        }
        catch (Exception ex2)
        {
            // If this fails, it's NOT a FileLoadException anymore - it's MSBuild config
            if (ex2.ToString().Contains("FileLoadException"))
                Console.WriteLine($"Workaround did not help: {ex2.Message}");
            else
                Console.WriteLine($"SDK eval failed (not the assembly bug): {ex2.GetType().Name}: {ex2.Message}");
        }
    }
}
finally
{
    if (File.Exists(projPath))
        File.Delete(projPath);
}

static string? FindVSMSBuildPath()
{
    string[] candidates = [
        @"C:\Program Files\Microsoft Visual Studio\18\Preview\MSBuild\Current\Bin\amd64",
        @"C:\Program Files\Microsoft Visual Studio\18\Preview\MSBuild\Current\Bin",
    ];
    foreach (var path in candidates)
    {
        if (File.Exists(Path.Combine(path, "MSBuild.exe")))
            return path;
    }
    return null;
}

// Workaround: Add all conflicting assemblies to MSBuildLoadContext.WellKnownAssemblyNames
// so Load() returns null for them and the runtime resolves from the app's TPA instead.
static void ApplyWorkaround(string vsMSBuildPath)
{
    var msbuildAssembly = typeof(Microsoft.Build.Evaluation.Project).Assembly;
    var loadContextType = msbuildAssembly.GetType("Microsoft.Build.Shared.MSBuildLoadContext")!;
    var field = loadContextType.GetField("WellKnownAssemblyNames",
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;

    var current = (ImmutableHashSet<string>)field.GetValue(null)!;
    Console.WriteLine($"WellKnownAssemblyNames before: {string.Join(", ", current)}");

    // Find all assemblies with version mismatches between app and VS
    var updated = current;
    var appDir = AppContext.BaseDirectory;
    foreach (var vsDll in Directory.GetFiles(vsMSBuildPath, "*.dll"))
    {
        var appDll = Path.Combine(appDir, Path.GetFileName(vsDll));
        if (!File.Exists(appDll)) continue;
        try
        {
            var vsVer = AssemblyName.GetAssemblyName(vsDll).Version;
            var appVer = AssemblyName.GetAssemblyName(appDll).Version;
            if (!vsVer!.Equals(appVer))
            {
                var name = AssemblyName.GetAssemblyName(vsDll).Name!;
                updated = updated.Add(name);
                Console.WriteLine($"  Adding {name} (app={appVer}, VS={vsVer})");
            }
        }
        catch { }
    }

    // Use DynamicMethod to bypass the initonly restriction on the static readonly field
    var dm = new DynamicMethod("SetField", typeof(void),
        new[] { typeof(ImmutableHashSet<string>) }, loadContextType, skipVisibility: true);
    var il = dm.GetILGenerator();
    il.Emit(OpCodes.Ldarg_0);
    il.Emit(OpCodes.Stsfld, field);
    il.Emit(OpCodes.Ret);
    var setter = (Action<ImmutableHashSet<string>>)dm.CreateDelegate(typeof(Action<ImmutableHashSet<string>>));
    setter(updated);

    Console.WriteLine($"WellKnownAssemblyNames after:  {string.Join(", ", updated)}\n");
}
