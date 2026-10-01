using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlienResurrectionLauncher;

// Player-chosen settings for the bundled game mods (bundled-mods/), edited on the front screen's
// Mods page BEFORE the game starts and read by the mods themselves when the engine loads them.
//
// One JSON file in the per-user data directory, next to the memory cards, because that is the one
// writable place both sides already agree on: the launcher sets UserData.AppId before anything
// else, and a mod running in the same process resolves the same UserData.Dir. The mods parse it
// with their own few lines of JsonDocument rather than referencing this type, so a mod never
// depends on the launcher assembly. Keep the property names below and the mods' readers in step.
public sealed class ModSettings
{
    public const string FileName = "mod-settings.json";

    [JsonPropertyName("godMode")]      public bool GodMode { get; set; }
    [JsonPropertyName("infiniteAmmo")] public bool InfiniteAmmo { get; set; }

    /// <summary>"classic" = the game's own light, tuned by <see cref="Flashlight"/>; "modern" = a
    /// per-pixel renderer replacement that does not exist yet (the page shows it disabled).</summary>
    [JsonPropertyName("lighting")]     public string Lighting { get; set; } = "classic";

    [JsonPropertyName("flashlight")]   public FlashlightSettings Flashlight { get; set; } = new();

    public sealed class FlashlightSettings
    {
        [JsonPropertyName("enabled")]    public bool Enabled { get; set; } = true;
        /// <summary>Multiplier on the light's colour. 1.0 = original; above 1 is genuinely brighter
        /// (the game's lighting maths treats 255 as 1.0 and allows more).</summary>
        [JsonPropertyName("brightness")] public float Brightness { get; set; } = 1.75f;
        [JsonPropertyName("warm")]       public bool Warm { get; set; }
        /// <summary>Multiplier on the beam's reach (the original peaks at 768 units).</summary>
        [JsonPropertyName("reach")]      public float Reach { get; set; } = 1.0f;
        [JsonPropertyName("flicker")]    public bool Flicker { get; set; }
        /// <summary>Multiplier on battery capacity (original 1200 internal units, shown as 100).</summary>
        [JsonPropertyName("battery")]    public float Battery { get; set; } = 1.0f;
        [JsonPropertyName("unlimitedBattery")] public bool UnlimitedBattery { get; set; }
    }

    static string PathOf() => Path.Combine(RecompOne.Runtime.Storage.UserData.Dir, FileName);

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static ModSettings Load()
    {
        try
        {
            var p = PathOf();
            if (File.Exists(p))
                return JsonSerializer.Deserialize<ModSettings>(File.ReadAllText(p), Json) ?? new();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[mods] could not read {FileName}, using defaults: {e.Message}");
        }
        return new();
    }

    public void Save()
    {
        try
        {
            var p = PathOf();
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[mods] could not save {FileName}: {e.Message}");
        }
    }
}
