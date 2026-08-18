using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace Recompiled;

public static class Bios
{
    public static void Syscall(CpuContext c, IMemory m) { }
    public static void Break(CpuContext c, IMemory m) { }
}
