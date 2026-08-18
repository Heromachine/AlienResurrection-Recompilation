// Alien Resurrection Launcher -- ships no game-derived code. First run: get the user's own disc
// and BIOS, recompile locally through RecompOne.Recompiler, build in-process via Roslyn (already a
// transitive dependency of RecompOne.Runtime -- see RecompOne.Runtime/Modding/ModCompiler.cs for
// the established in-process-compile pattern this reuses), then launch the built game as a
// separate process. Reuses RecompOne.Runtime's own disc/BIOS pickers for its setup UI, so there is
// no duplicate picker code between the launcher and the eventual game.

using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using RecompOne.Recompiler.CodeGen;
using RecompOne.Recompiler.Config;
using RecompOne.Recompiler.Psx;
using RecompOne.Runtime.Cdrom;
using RecompOne.Runtime.Config;

const string ExpectedBootExe = "SLUS_006.33";
string launcherDir = AppContext.BaseDirectory;
string gameDir = Path.Combine(launcherDir, "game");
string recompiledDir = Path.Combine(gameDir, "Recompiled");
string binDir = Path.Combine(gameDir, "bin");
string stampFile = Path.Combine(gameDir, ".stamp");

// ---- Step 1: get a valid disc + resolve BIOS, reusing the engine's own pickers -----------------
RecompOne.Runtime.Runtime.Initialize("Alien Resurrection Launcher");
RecompOne.Runtime.Runtime.WaitForValidDisc(); // blocks with the disc picker until CdPath is valid;
                                               // the BIOS picker (optional, never blocks) was
                                               // already offered once during OnLoad above.
string discPath = ConfigManager.Game.CdPath;
string biosPath = ConfigManager.Game.BiosPath;

// ---- Step 2: validate the disc is actually this game, before doing anything with it ------------
using (var validateFs = CueFs.Open(discPath))
{
    var sysCfg = SystemCfg.Parse(validateFs);
    if (!string.Equals(sysCfg.BootExe, ExpectedBootExe, StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine(
            $"[launcher] this disc's boot executable is '{sysCfg.BootExe}', expected " +
            $"'{ExpectedBootExe}' -- this doesn't look like Alien Resurrection (USA). Pick the " +
            "correct disc and try again.");
        RecompOne.Runtime.Runtime.ShowNotice(
            $"Wrong disc: found '{sysCfg.BootExe}', expected '{ExpectedBootExe}'.\n" +
            "Please select your Alien Resurrection (USA) disc.");
        return 1;
    }
    Console.WriteLine($"[launcher] disc OK: {sysCfg.BootExe}");
}

// ---- Step 3: cache check -------------------------------------------------------------------
string configHash = Convert.ToHexString(
    System.Security.Cryptography.SHA256.HashData(
        File.ReadAllBytes(Path.Combine(launcherDir, "assets", "config.json"))))[..16];
string stamp = $"{discPath}|{biosPath}|{configHash}";
string builtDll = Path.Combine(binDir, "AlienResurrection.dll");

bool cacheHit = File.Exists(stampFile) && File.Exists(builtDll)
    && File.ReadAllText(stampFile) == stamp;

if (!cacheHit)
{
    Console.WriteLine("[launcher] no valid cache -- recompiling and building");

    // ---- Step 4: materialize the game/ template -------------------------------------------
    Directory.CreateDirectory(recompiledDir);
    Directory.CreateDirectory(binDir);
    File.Copy(Path.Combine(launcherDir, "GameTemplate", "Program.cs"),
              Path.Combine(gameDir, "Program.cs"), overwrite: true);
    File.Copy(Path.Combine(launcherDir, "GameTemplate", "Stubs.cs"),
              Path.Combine(recompiledDir, "Stubs.cs"), overwrite: true);

    // ---- Step 5: recompile in-process (no CLI subprocess needed -- OverlayWriter.Write is a
    // normal public static method) --------------------------------------------------------------
    var config = ConfigLoader.Load(Path.Combine(launcherDir, "assets", "config.json"));
    config.Cue = discPath; // override the shipped placeholder with the user's actual disc
    using (var fs = CueFs.Open(discPath))
        OverlayWriter.Write(config, fs, recompiledDir);

    // ---- Step 6: build in-process via Roslyn -----------------------------------------------
    var sourceFiles = Directory.GetFiles(recompiledDir, "*.cs")
        .Append(Path.Combine(gameDir, "Program.cs"));

    var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
    var trees = sourceFiles.Select(path => CSharpSyntaxTree.ParseText(
        SourceText.From(File.ReadAllText(path), Encoding.UTF8), parseOptions, path)).ToList();

    // <ImplicitUsings>enable</ImplicitUsings> is an SDK/MSBuild feature (it generates a
    // GlobalUsings.g.cs under the hood) with no equivalent in raw CSharpCompilation -- the
    // generated Recompiled/*.cs and the template Program.cs both rely on it (matching the
    // dev-convenience AlienResurrection.Game.csproj, which has it enabled), so synthesize the
    // same implicit-usings set the SDK adds for an Exe-output net10.0 project.
    trees.Add(CSharpSyntaxTree.ParseText(SourceText.From(
        "global using System;\n" +
        "global using System.Collections.Generic;\n" +
        "global using System.IO;\n" +
        "global using System.Linq;\n" +
        "global using System.Threading;\n" +
        "global using System.Threading.Tasks;\n",
        Encoding.UTF8), parseOptions, "GlobalUsings.g.cs"));

    var references = AppDomain.CurrentDomain.GetAssemblies()
        .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
        .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
        .ToList();

    var compileOptions = new CSharpCompilationOptions(OutputKind.ConsoleApplication)
        .WithAllowUnsafe(true)
        .WithOptimizationLevel(OptimizationLevel.Release)
        .WithNullableContextOptions(NullableContextOptions.Disable)
        // Generated code is machine-emitted; these are expected there, same NoWarn set as the
        // dev-convenience AlienResurrection.Game.csproj uses.
        .WithSpecificDiagnosticOptions(new Dictionary<string, ReportDiagnostic>
        {
            ["CS0164"] = ReportDiagnostic.Suppress,
            ["CS0219"] = ReportDiagnostic.Suppress,
            ["CS0162"] = ReportDiagnostic.Suppress,
            ["CS0168"] = ReportDiagnostic.Suppress,
            ["CS8321"] = ReportDiagnostic.Suppress,
        });

    var compilation = CSharpCompilation.Create("AlienResurrection", trees, references, compileOptions);
    var emitResult = compilation.Emit(builtDll);
    if (!emitResult.Success)
    {
        foreach (var diag in emitResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
            Console.Error.WriteLine($"[launcher] build error: {diag}");
        return 1;
    }
    Console.WriteLine($"[launcher] built {builtDll}");

    // Runtime dependencies: the launcher's own output directory already has RecompOne.Runtime.dll
    // and everything it transitively needs (MSBuild resolved all of it when building the launcher
    // itself), so a blanket copy is simple and correct rather than hand-picking which DLLs matter.
    foreach (var dll in Directory.GetFiles(launcherDir, "*.dll"))
        File.Copy(dll, Path.Combine(binDir, Path.GetFileName(dll)), overwrite: true);

    // A minimal runtimeconfig.json so `dotnet AlienResurrection.dll` knows which runtime to host --
    // Emit() alone only produces the IL, not this file. Base it on the launcher's own (present
    // alongside its DLL after a normal SDK build) rather than hand-writing version numbers.
    string launcherRuntimeConfig = Path.Combine(launcherDir, "AlienResurrectionLauncher.runtimeconfig.json");
    if (File.Exists(launcherRuntimeConfig))
        File.Copy(launcherRuntimeConfig, Path.Combine(binDir, "AlienResurrection.runtimeconfig.json"), overwrite: true);

    File.WriteAllText(stampFile, stamp);
}
else
{
    Console.WriteLine("[launcher] cache hit -- skipping recompile/build");
}

// ---- Step 7: write settings.json for the built game (so it never re-prompts), then launch ------
File.WriteAllText(Path.Combine(binDir, "settings.json"),
    System.Text.Json.JsonSerializer.Serialize(new { CdPath = discPath, BiosPath = biosPath },
        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

Console.WriteLine("[launcher] launching the game");
var psi = new System.Diagnostics.ProcessStartInfo("dotnet", $"\"{builtDll}\"")
{
    WorkingDirectory = binDir,
    UseShellExecute = false,
};
using var proc = System.Diagnostics.Process.Start(psi);
proc?.WaitForExit();
return proc?.ExitCode ?? 1;
