using System;
using System.IO;
using System.Text.Json;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace AlienFlashlight;

// Flashlight tuning for Alien Resurrection (USA): brightness, colour, reach, flicker and battery,
// set on the launcher's Mods page (mod-settings.json "flashlight") before the game starts.
//
// How the game's flashlight works (Ghidra, HeroLab fb51ff2b task d4143147):
//   - It is inventory item 0x4C. Using it creates a LIGHT OBJECT at [holder + 0x40], where holder is
//     [player + 0x64] (player = [0x000A49E8]); [0x000A49EC] is the same holder.
//   - Light object: +0x20 inner radius, +0x22 outer radius (the REACH, where the falloff ends),
//     +0x2C/+0x2E/+0x30 colour R/G/B. The actor lighting (func_00021410) treats colour 255 as 1.0
//     and multiplies by distance falloff, so values above 255 really are brighter.
//   - func_000625CC runs every frame: it drains the battery ([inventory + 0x44], full = s32 at
//     0x000A7B10 = 1200) and animates the reach, ramping by 12 up to the s32 at 0x000A7B20 (768) and
//     adding rand() % 25 each frame. That is the flicker.
//
// This post-hooks func_000625CC, so its values are the last word each frame:
//   - reach:   0xA7B20 = 768 * Reach. With flicker OFF the light's +0x22 is also held at that value
//              every frame, removing both the ramp wobble and the random jitter.
//   - colour:  the light's RGB = (original or warm) * Brightness, clamped to a safe 2047.
//   - battery: 0xA7B10 = 1200 * Battery (cap 32767); Unlimited keeps [inventory + 0x44] full.
// With "Use these flashlight settings" off (or Modern lighting chosen), nothing is touched.
public sealed class Flashlight : IMod
{
    const uint PlayerPtr = 0x000A49E8u, HolderPtr = 0x000A49ECu, InventoryPtr = 0x000A49F0u;
    const uint BatteryFull = 0x000A7B10u, ReachMax = 0x000A7B20u, ColourBytes = 0x000A7B0Cu;
    const int FlashlightItem = 0x4C;

    // The disc's own values, so the multipliers are relative to the original game.
    const int OrigBattery = 1200, OrigReach = 768;
    static readonly int[] OrigColour = [154, 174, 181];
    static readonly int[] WarmColour = [255, 196, 128];

    static readonly bool Enabled, Warm, Flicker, Unlimited;
    static readonly float Brightness, Reach, Battery;

    static Flashlight()
    {
        var s = Settings();
        var f = s.ValueKind == JsonValueKind.Object && s.TryGetProperty("flashlight", out var v) ? v : s;
        bool classic = !(s.ValueKind == JsonValueKind.Object && s.TryGetProperty("lighting", out var l)
                         && l.ValueKind == JsonValueKind.String && l.GetString() == "modern");
        Enabled    = EnvOr("ALIEN_FLASHLIGHT", classic && Flag(f, "enabled", true));
        Brightness = Math.Clamp(Num(f, "brightness", 1.75f), 0.25f, 4f);
        Warm       = Flag(f, "warm", false);
        Reach      = Math.Clamp(Num(f, "reach", 1f), 0.25f, 4f);
        Flicker    = Flag(f, "flicker", false);
        Battery    = Math.Clamp(Num(f, "battery", 1f), 0.25f, 27f);
        Unlimited  = Flag(f, "unlimitedBattery", false);
    }

    public void OnLoad() => Console.WriteLine(Enabled
        ? $"[flashlight] on -- brightness x{Brightness:0.##}, {(Warm ? "warm" : "original")} colour, reach x{Reach:0.##}, "
          + $"flicker {(Flicker ? "on" : "off")}, battery {(Unlimited ? "unlimited" : $"x{Battery:0.#}")}"
        : "[flashlight] off (original flashlight)");

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

    [PostHook("alien", "func_000625CC")]
    public static void AfterFlashlightUpdate(CpuContext c, IMemory m)
    {
        if (!Enabled) return;

        int full = Math.Min(32767, (int)(OrigBattery * Battery));
        int reach = Math.Min(32767, (int)(OrigReach * Reach));
        m.WriteU32(BatteryFull, (uint)full);
        m.WriteU32(ReachMax, (uint)reach);

        var baseCol = Warm ? WarmColour : OrigColour;
        Span<ushort> col = stackalloc ushort[3];
        for (int i = 0; i < 3; i++)
        {
            col[i] = (ushort)Math.Clamp((int)(baseCol[i] * Brightness), 0, 2047);
            m.WriteU8(ColourBytes + (uint)i, (byte)Math.Min(255, (int)col[i]));   // used when the light is created
        }

        uint inv = m.ReadU32(InventoryPtr);
        if (Unlimited && inv != 0 && (m.ReadU32(inv + 4) & (1u << (FlashlightItem - 0x30))) != 0)
            m.WriteU16(inv + 0xC + (uint)(FlashlightItem - 0x30) * 2, (ushort)full);

        uint player = m.ReadU32(PlayerPtr);
        uint holder = player != 0 ? m.ReadU32(player + 0x64) : 0;
        if (holder == 0) holder = m.ReadU32(HolderPtr);
        if (holder == 0) return;
        uint light = m.ReadU32(holder + 0x40);
        if (light == 0) return;                                  // flashlight is off

        m.WriteU16(light + 0x2C, col[0]);
        m.WriteU16(light + 0x2E, col[1]);
        m.WriteU16(light + 0x30, col[2]);
        if (!Flicker) m.WriteU16(light + 0x22, (ushort)reach);
    }
}
