// Alien Resurrection Launcher -- ships no game-derived code. First run: get the user's own disc
// and BIOS, recompile locally through RecompOne.Recompiler, build in-process via Roslyn (already a
// transitive dependency of RecompOne.Runtime -- see RecompOne.Runtime/Modding/ModCompiler.cs for
// the established in-process-compile pattern this reuses), then run the built game in the SAME
// process and the SAME window -- no separate launcher-then-game handoff, matching how other PS1/N64
// recomp projects behave. Reuses RecompOne.Runtime's own disc/BIOS pickers for its setup UI, so
// there is no duplicate picker code between the launcher and the eventual game.

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
using RecompOne.Runtime.Memory;

// CHAIN_IRQ=1 IS MANDATORY FOR THIS GAME -- without it the controller does not work at all.
// Interrupts.cs defaults ChainMode to 0 (HLE only: handlers are called out of the BIOS IntrEnv
// table, and the SysEnqIntRP chain is never walked). Alien Resurrection drives its pad through its
// OWN SIO0 driver hung off that chain -- not LibPad, not the BIOS pad -- so with the chain unwalked
// its handler never runs, SIO0 exchanges zero bytes, and the game flashes "No Controller in
// Controller Port 1" forever. The keyboard dies with it, since both feed through that same ISR.
// Measured 2026-08-18 on identical builds: chain unwalked -> verifierCalls=0, SIO0 TXbytes=0, no
// input; CHAIN_IRQ=1 -> verifierCalls=201, TXbytes=3559, pad works. This is set here rather than by
// flipping the engine default because that default guards other titles (see the CHAIN_DELAY=600
// note in Interrupts.cs: walking the chain during early init hangs this game's boot at CD_init),
// and one title's evidence should not change it for every game.
//
// It must be set BEFORE anything touches Interrupts, whose ChainMode is a `static readonly` read
// once at type-initialization time -- setting it later would be silently ignored. Hence: first
// statement in the process, ahead of even the launcher thread. An explicit value from the
// environment still wins, so a deliberate CHAIN_IRQ=0 run for A/B testing keeps working.
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CHAIN_IRQ")))
    Environment.SetEnvironmentVariable("CHAIN_IRQ", "1");

// Namespaces this game's writable state (memory cards) under its own per-user data directory
// rather than letting it land in whatever directory the process happened to start in. Must be set
// before anything reads Runtime.CardA/CardB, which resolve their paths on first access and cache
// them -- hence here, next to CHAIN_IRQ, rather than further into the launcher flow.
RecompOne.Runtime.Storage.UserData.AppId = "AlienResurrection";

// The window/GL context and audio context are thread-affine, and gameplay's recompiled call chains
// need a deep stack (RecompOne translates every MIPS call into a real C# call) -- the same reason
// GameTemplate/Program.cs runs the game on a dedicated 64MB-stack thread. Running the ENTIRE
// launcher flow on that same kind of thread from the very start means the window created here for
// the disc/BIOS pickers is the SAME window (owned by the SAME thread) gameplay ends up rendering
// into -- one process, one window, for the whole session, instead of a launcher window handing off
// to a separately-windowed child process.
int exitCode = 1;
var thread = new Thread(() =>
{
    try { exitCode = RunLauncher(); }
    catch (Exception e) { Console.Error.WriteLine(e); exitCode = 1; }
}, 64 * 1024 * 1024);
thread.Start();
thread.Join();
return exitCode;

int RunLauncher()
{
    const string ExpectedBootExe = "SLUS_006.33";
    string launcherDir = AppContext.BaseDirectory;
    // NOT launcherDir. AppContext.BaseDirectory is read-only when running from an AppImage (a
    // mounted SquashFS image), so recompiling into "<launcherDir>/game" throws
    // System.IO.IOException: Read-only file system the first time anyone clicks Play. UserData.Dir
    // is the same per-user writable directory already used for memory cards and the display font,
    // for exactly the reason its own doc comment gives: writable state does not belong next to the
    // executable.
    string gameDir = Path.Combine(RecompOne.Runtime.Storage.UserData.Dir, "game");
    string recompiledDir = Path.Combine(gameDir, "Recompiled");
    string stampFile = Path.Combine(gameDir, ".stamp");
    string builtDllPath = Path.Combine(gameDir, "AlienResurrection.dll");

    // ---- Step 0: optional display font, user-supplied ----------------------------------------
    // The "Alien Resurrection" display font is Free for Personal Use and carries no redistribution
    // grant, so it is NOT shipped -- publishing it would have this repo's own licence promise rights
    // over someone else's work. Same answer as the disc and the BIOS: the user supplies it. Drop
    // "Alien Resurrection.ttf" beside the executable or in the save/user-data folder and the title
    // uses it; otherwise everything falls back to the default face and simply looks plainer.
    // Must be set before Initialize -- ImGui builds its font atlas inside that call.
    foreach (var candidate in new[]
             {
                 Path.Combine(RecompOne.Runtime.Storage.UserData.Dir, "Alien Resurrection.ttf"),
                 Path.Combine(AppContext.BaseDirectory, "Alien Resurrection.ttf"),
             })
    {
        if (!File.Exists(candidate)) continue;
        RecompOne.Runtime.Host.Window.HostFonts.DisplayFontPath = candidate;
        break;
    }

    // ---- Step 1: get a valid disc + resolve BIOS, reusing the engine's own pickers -------------
    string appVersion = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
    RecompOne.Runtime.Runtime.Initialize($"Alien Resurrection Recompilation v{appVersion}");
    RecompOne.Runtime.Runtime.WaitForValidDisc(); // blocks with the disc picker until CdPath is valid
    RecompOne.Runtime.Runtime.WaitForBiosResolution(); // the disc and BIOS prompts are sequenced
                                                        // (never shown in the same frame), so
                                                        // WaitForValidDisc alone can return before
                                                        // the BIOS prompt has even appeared -- this
                                                        // waits it out too.
    string discPath = ConfigManager.Game.CdPath;
    string biosPath = ConfigManager.Game.BiosPath;

    // ---- Step 2: validate the disc is actually this game, before doing anything with it --------
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

    // ---- Step 2b: front screen ---------------------------------------------------------------
    // Deliberately AFTER the disc and save location are settled and BEFORE any recompile: a first
    // run should not open on a build log. Play falls through to the cache check below; Exit leaves
    // without touching the game directory at all.
    var splash = new AlienResurrectionLauncher.SplashScreen();
    RecompOne.Runtime.Host.Window.PanelManager.Register(splash);

    // Pad/keyboard navigation for OUR menu only. Turned off again the moment the choice is made,
    // before the guest ever runs -- from that point the pad belongs to the emulated PlayStation
    // controller and ImGui must not also be consuming it. See HostNav.
    RecompOne.Runtime.Host.Window.HostNav.UiNavigation = true;
    try
    {
        // Paced deliberately. The window is created with VSync off and no frame cap (see
        // HostWindow.Initialize), so an unpaced pump loop spins a core flat out -- measured at 64%
        // of one CPU sitting on a static menu. That is tolerable for a build-progress loop lasting
        // seconds; this screen can sit here for as long as someone reads the About page. ~8ms still
        // gives well over 60fps, which is more than the radar animation needs.
        while (splash.Choice == AlienResurrectionLauncher.SplashChoice.None)
        {
            RecompOne.Runtime.Runtime.Pump();
            Thread.Sleep(8);
        }
    }
    finally
    {
        RecompOne.Runtime.Host.Window.HostNav.UiNavigation = false;
    }

    if (splash.Choice == AlienResurrectionLauncher.SplashChoice.Exit)
    {
        Console.WriteLine("[launcher] exit chosen at the front screen");
        return 0;
    }

    // ---- Step 3: cache check ---------------------------------------------------------------
    string configHash = Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(
            File.ReadAllBytes(Path.Combine(launcherDir, "assets", "config.json"))))[..16];
    string stamp = $"{discPath}|{biosPath}|{configHash}";

    bool cacheHit = File.Exists(stampFile) && File.Exists(builtDllPath)
        && File.ReadAllText(stampFile) == stamp;

    byte[]? builtBytes = null;

    if (!cacheHit)
    {
        Console.WriteLine("[launcher] no valid cache -- recompiling and building");
        RecompOne.Runtime.Runtime.ShowNotice(
            "Recompiling and building the game from your disc. This can take a little while -- " +
            "the window will stay open and responsive, please wait.");

        // The recompile+build below is genuinely slow (a few seconds to tens of seconds) and has no
        // natural "pump the window" points of its own. Run it on a background thread and keep
        // pumping the window from this (the dedicated) thread meanwhile -- exactly the pattern
        // Modding.ModLoader.LoadAll() already uses for mod compilation -- so the window keeps
        // rendering and responding to the OS instead of looking hung for the whole duration.
        string? buildError = null;
        byte[]? emitted = null;
        var buildTask = Task.Run(() =>
        {
            try
            {
                // ---- Step 4: materialize the Recompiled/ template ------------------------------
                Directory.CreateDirectory(recompiledDir);
                File.Copy(Path.Combine(launcherDir, "GameTemplate", "Stubs.cs"),
                          Path.Combine(recompiledDir, "Stubs.cs"), overwrite: true);

                // ---- Step 5: recompile in-process (no CLI subprocess needed -- OverlayWriter.Write
                // is a normal public static method) ----------------------------------------------
                var config = ConfigLoader.Load(Path.Combine(launcherDir, "assets", "config.json"));
                config.Cue = discPath; // override the shipped placeholder with the user's actual disc
                using (var fs = CueFs.Open(discPath))
                    OverlayWriter.Write(config, fs, recompiledDir);

                // ---- Step 6: build in-process via Roslyn ---------------------------------------
                var sourceFiles = Directory.GetFiles(recompiledDir, "*.cs");

                var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
                var trees = sourceFiles.Select(path => CSharpSyntaxTree.ParseText(
                    SourceText.From(File.ReadAllText(path), Encoding.UTF8), parseOptions, path)).ToList();

                // <ImplicitUsings>enable</ImplicitUsings> is an SDK/MSBuild feature (it generates a
                // GlobalUsings.g.cs under the hood) with no equivalent in raw CSharpCompilation --
                // the generated Recompiled/*.cs relies on it (matching the dev-convenience
                // AlienResurrection.Game.csproj, which has it enabled), so synthesize the same
                // implicit-usings set the SDK adds for a net10.0 project.
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

                var compileOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                    .WithAllowUnsafe(true)
                    .WithOptimizationLevel(OptimizationLevel.Release)
                    .WithNullableContextOptions(NullableContextOptions.Disable)
                    // Generated code is machine-emitted; these are expected there, same NoWarn set
                    // as the dev-convenience AlienResurrection.Game.csproj uses.
                    .WithSpecificDiagnosticOptions(new Dictionary<string, ReportDiagnostic>
                    {
                        ["CS0164"] = ReportDiagnostic.Suppress,
                        ["CS0219"] = ReportDiagnostic.Suppress,
                        ["CS0162"] = ReportDiagnostic.Suppress,
                        ["CS0168"] = ReportDiagnostic.Suppress,
                        ["CS8321"] = ReportDiagnostic.Suppress,
                    });

                var compilation = CSharpCompilation.Create("AlienResurrection", trees, references, compileOptions);
                using var ms = new MemoryStream();
                var emitResult = compilation.Emit(ms);
                if (!emitResult.Success)
                {
                    var sb = new StringBuilder();
                    foreach (var diag in emitResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
                    {
                        Console.Error.WriteLine($"[launcher] build error: {diag}");
                        sb.AppendLine(diag.ToString());
                    }
                    buildError = sb.ToString();
                    return;
                }
                emitted = ms.ToArray();
                Console.WriteLine($"[launcher] built ({emitted.Length} bytes)");

                Directory.CreateDirectory(gameDir);
                File.WriteAllBytes(builtDllPath, emitted); // kept on disk only for the cache check
                File.WriteAllText(stampFile, stamp);
            }
            catch (Exception ex)
            {
                buildError = ex.ToString();
            }
        });

        while (!buildTask.IsCompleted)
        {
            RecompOne.Runtime.Runtime.Pump();
            Thread.Sleep(16);
        }

        if (buildError != null)
        {
            Console.Error.WriteLine($"[launcher] build failed:\n{buildError}");
            RecompOne.Runtime.Runtime.ShowNotice($"Build failed:\n{buildError}");
            return 1;
        }
        builtBytes = emitted;
    }
    else
    {
        Console.WriteLine("[launcher] cache hit -- skipping recompile/build");
        builtBytes = File.ReadAllBytes(builtDllPath);
    }

    // ---- Step 7: run the built game in-process, in this same window -----------------------------
    // No child process, no separate bin/ directory, no DLL/native-lib/runtimeconfig copying -- the
    // compiled assembly loads directly into this already-running process, which already has every
    // reference (RecompOne.Runtime, etc.) and native dependency resolved, and Assembly.Load binds
    // the emitted references to those already-loaded assemblies by identity rather than duplicating
    // them (the same assumption Modding.ModLoader already relies on for compiled mod code).
    Console.WriteLine("[launcher] starting the game");
    var asm = Assembly.Load(builtBytes!);
    var entryType = asm.GetType("Recompiled.Entry")
        ?? throw new InvalidOperationException("Recompiled.Entry type not found in the built assembly");
    var runMethod = entryType.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)
        ?? throw new InvalidOperationException("Entry.Run method not found on Recompiled.Entry");

    var mem = new PSMemory();
    runMethod.Invoke(null, [mem, discPath]);
    return 0;
}
