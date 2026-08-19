// Boot harness for the recompiled game. Compiled in-process by the launcher alongside the
// freshly-generated Recompiled/*.cs and launched as a separate process. Unlike the private repo's
// dev-convenience Program.cs, this ships NO hardcoded disc path and pre-seeds nothing: by the time
// this assembly is built, the launcher has already resolved the disc/BIOS paths and written a
// correct settings.json into this same directory, so ConfigManager/WaitForValidDisc just find them.

using RecompOne.Runtime.Memory;
using Recompiled;

// Matches the launcher's own setting. The launcher normally invokes Recompiled.Entry.Run directly
// in-process, so this Main is not the usual entry path -- but if this assembly is ever run on its
// own, the cards must resolve to the same per-user directory rather than to the CWD.
RecompOne.Runtime.Storage.UserData.AppId = "AlienResurrection";

if (Environment.GetEnvironmentVariable("INTERP_SELFTEST") == "1")
{
    RecompOne.Runtime.Dispatch.MipsInterp.SelfTest();
    return 0;
}

var mem = new PSMemory();
// See the private repo's Program.cs for why this needs a large stack: RecompOne translates every
// MIPS call into a real C# call, so a deeply nested game-logic call chain can need more native
// stack than .NET's default ~1MB thread stack.
Exception? crash = null;
var thread = new Thread(() =>
{
    try { Entry.Run(mem); }
    catch (Exception e) { crash = e; }
}, 64 * 1024 * 1024);
thread.Start();
thread.Join();
if (crash != null) { Console.Error.WriteLine(crash); return 1; }
return 0;
