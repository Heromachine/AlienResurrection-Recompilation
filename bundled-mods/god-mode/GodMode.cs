using System;
using System.IO;
using System.Text.Json;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace AlienGodMode;

// God mode for Alien Resurrection (USA) by switching on the game's OWN invincibility flag, not by
// refilling health.
//
// The byte at 0x00096BDC is a flag in a block of cheat-style flags (0x96BDA..0x96BE0) that the game
// reads but never writes directly. Decompiled with Ghidra (HeroLab project fb51ff2b), every player
// health path checks it:
//   func_000519D0  the damage function. For the player, damage is forced to 0 unless the flag is clear.
//   func_00051434  the slow health drain (-4 per tick). Skipped while the flag is set.
//   func_0005ECBC  "health < 1 -> die". Skipped while the flag is set.
// The address is fixed data inside ALIEN.BIN, so it works in every level and every save. Unlike the
// old per-frame health clamp, damage never lands in the first place.
//
// ALIEN.BIN's load overwrites the byte with 0, so the flag is (re)set right before each of those three
// functions runs rather than once at startup. Each is a pre-hook that never skips the original.
//
// ON/OFF: the "God mode" switch on the launcher's Mods page (mod-settings.json "godMode", default off).
// ALIEN_GODMODE=1/0 overrides it.
public sealed class GodMode : IMod
{
    const uint InvincibleFlag = 0x00096BDCu;

    static readonly bool Enabled = EnvOr("ALIEN_GODMODE", Flag(Settings(), "godMode", false));

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
        Console.WriteLine(Enabled
            ? "[god-mode] on -- the game's invincibility flag (0x96BDC) is held at 1"
            : "[god-mode] off");

    static void Hold(IMemory m)
    {
        if (Enabled) m.WriteU8(InvincibleFlag, 1);
    }

    [PreHook("alien", "func_000519D0")]
    public static void BeforeDamage(CpuContext c, IMemory m) => Hold(m);

    [PreHook("alien", "func_00051434")]
    public static void BeforeDrain(CpuContext c, IMemory m) => Hold(m);

    [PreHook("alien", "func_0005ECBC")]
    public static void BeforeDeathCheck(CpuContext c, IMemory m) => Hold(m);
}
