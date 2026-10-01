using System;
using System.IO;
using System.Text.Json;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace AlienInfiniteAmmo;

// Infinite ammo for Alien Resurrection (USA), using the game's OWN "unlimited" value.
//
// Inventory (decompiled with Ghidra, HeroLab fb51ff2b task e866c6cd): the u32 at 0x000A49F0
// points to it. +4 is an owned-items bitmask indexed by (item - 0x30), and the count of item N is
// an s16 at +0xC + (N - 0x30) * 2. Ammo types are items 0x38..0x3F (the weapons are 0x30..0x37).
// A count of -1 means UNLIMITED: the game's own cheat (flag 0x96BDB) gives ammo as -1, and the
// pickup path skips adding to a negative count.
//
// Every gameplay frame (pre-hook on func_0001F408, the per-frame update) this sets each OWNED ammo
// type to -1 and keeps the cheat flag on, so ammo picked up later is unlimited too. Only owned ammo
// is touched, so it never hands out weapons.
//
// ON/OFF: the "Infinite ammo" switch on the launcher's Mods page (mod-settings.json "infiniteAmmo",
// default off). ALIEN_INFAMMO=1/0 overrides it.
public sealed class InfiniteAmmo : IMod
{
    const uint InventoryPtr = 0x000A49F0u;
    const uint AmmoCheatFlag = 0x00096BDBu;
    static readonly bool Enabled = EnvOr("ALIEN_INFAMMO", Flag(Settings(), "infiniteAmmo", false));

    // mod-settings.json is written by the launcher's Mods page (ModSettings.cs) into the per-user
    // data directory. Read once at load; missing file or key -> the launcher's defaults.
    static JsonElement Settings()
    {
        try
        {
            var p = Path.Combine(RecompOne.Runtime.Storage.UserData.Dir, "mod-settings.json");
            if (File.Exists(p)) using (var d = JsonDocument.Parse(File.ReadAllText(p))) return d.RootElement.Clone();
        }
        catch (Exception e) { Console.Error.WriteLine($"[mods] mod-settings.json unreadable: {e.Message}"); }
        using var empty = JsonDocument.Parse("{}");
        return empty.RootElement.Clone();
    }

    static bool Flag(JsonElement o, string key, bool dflt) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(key, out var v) &&
        (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : dflt;

    static float Num(JsonElement o, string key, float dflt) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(key, out var v) &&
        v.ValueKind == JsonValueKind.Number ? v.GetSingle() : dflt;

    // An explicit environment variable wins over the page, for A/B runs: "1" forces on, "0" off.
    static bool EnvOr(string name, bool fromPage) =>
        Environment.GetEnvironmentVariable(name) switch { "1" => true, "0" => false, _ => fromPage };

    public void OnLoad() =>
        Console.WriteLine(Enabled ? "[infinite-ammo] on" : "[infinite-ammo] off");

    [PreHook("alien", "func_0001F408")]
    public static void EveryFrame(CpuContext c, IMemory m)
    {
        if (!Enabled) return;
        uint inv = m.ReadU32(InventoryPtr);
        if (inv == 0) return;                          // no game in progress yet
        m.WriteU8(AmmoCheatFlag, 1);
        uint owned = m.ReadU32(inv + 4);               // bits for items 0x30..0x4F
        for (int item = 0x38; item <= 0x3F; item++)
        {
            int bit = item - 0x30;
            if ((owned & (1u << bit)) == 0) continue;  // not carrying this ammo type
            uint slot = inv + 0xC + (uint)bit * 2;
            if ((short)m.ReadU16(slot) >= 0) m.WriteU16(slot, 0xFFFF);
        }
    }
}
