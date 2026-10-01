using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Host.Window;

namespace AlienResurrectionLauncher;

// What the launcher shows while it recompiles the game off the disc and builds the result.
//
// The work runs on a background thread and the launcher thread pumps frames (see Program.cs), so
// something has to occupy the window for the length of a genuinely slow operation. Previously that
// was a notice popup over an empty screen, which is indistinguishable from a hang. This is the
// front screen's motion tracker carried over, so the first run feels like part of the same launcher.
//
// NO PERCENTAGE, DELIBERATELY -- same reasoning as the Tenchu and Survivor launchers. Neither half of
// the work can report progress: OverlayWriter.Write runs to completion in one call, and so does the
// Roslyn emit. A bar creeping to 90% and sitting there tells the user the thing is nearly done when
// nothing is known. What IS known is which phase is running, so that is what is shown; the radar's
// motion answers "is it still alive", and the elapsed time answers "is this normal".
public sealed class BuildScreen : IPanel
{
    public string Name => "Building";
    public bool IsOpen { get; set; }

    public enum Stage { Recompiling, Building }

    /// <summary>Set from the build thread; read on the UI thread. Assignment is atomic.</summary>
    public volatile Stage Current = Stage.Recompiling;

    readonly RadarBackground _radar = new();
    float _elapsed;
    bool _focusPending = true;

    static string Heading(Stage s) => s switch
    {
        Stage.Recompiling => "RECOMPILING",
        _                 => "BUILDING",
    };

    static string Caption(Stage s) => s switch
    {
        Stage.Recompiling => "Translating the disc to C#",
        _                 => "Compiling the translated code",
    };

    // The front screen's palette (SplashScreen.cs), so the two screens read as one launcher.
    static readonly Vector4 Bg        = new(0.11f, 0.12f, 0.13f, 1f);
    static readonly Vector4 TextCore  = new(0.97f, 0.99f, 0.97f, 1f);
    static readonly Vector4 TextGlow  = new(0.20f, 0.85f, 0.35f, 1f);

    public void Draw()
    {
        if (!IsOpen) return;

        _elapsed += Math.Clamp(ImGui.GetIO().DeltaTime, 0f, 0.1f);

        var vp = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(vp.WorkPos);
        ImGui.SetNextWindowSize(vp.WorkSize);

        // Focused once, on the way in, and NOT NoBringToFrontOnFocus -- see the note in
        // SplashScreen.Draw: that flag pins a window behind HostWindow's full-viewport dockspace.
        if (_focusPending)
        {
            ImGui.SetNextWindowFocus();
            _focusPending = false;
        }
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Bg);

        if (ImGui.Begin("##building",
                ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
                ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings |
                ImGuiWindowFlags.NoDocking))
        {
            // Same placement and size as on the front screen, so the tracker does not jump when the
            // splash closes and this opens.
            var centre = vp.WorkPos + vp.WorkSize * 0.5f;
            _radar.Draw(ImGui.GetWindowDrawList(), centre,
                        MathF.Min(vp.WorkSize.X, vp.WorkSize.Y) * 0.46f);

            // The tracker sweeps the TOP half, so the text sits below its baseline, clear of it.
            var stage = Current;
            float line = ImGui.GetTextLineHeightWithSpacing();
            float y = centre.Y + line * 1.5f;

            Centred(Heading(stage), y, vp, HostFonts.Subtitle);
            y += (HostFonts.Subtitle != null ? HostFonts.SubtitleSize : line) * 1.4f;
            Centred(Caption(stage), y, vp, HostFonts.Ui);
            y += line * 2.2f;
            Centred($"{_elapsed:0} s elapsed  --  first run only, the result is cached",
                    y, vp, HostFonts.Ui, 0.8f);
        }
        ImGui.End();
        ImGui.PopStyleColor();
    }

    // Same construction as SplashScreen.GlowCentered -- the string stamped at two radii in the
    // glow colour, then the core on top -- but positioned absolutely, since everything here is laid
    // out around the radar rather than down the cursor.
    static void Centred(string text, float screenY, ImGuiViewportPtr vp, ImFontPtr? font, float alpha = 1f)
    {
        if (font is { } f) ImGui.PushFont(f);

        var sz = ImGui.CalcTextSize(text);
        var origin = new Vector2(vp.WorkPos.X + (vp.WorkSize.X - sz.X) * 0.5f, screenY);
        var dl = ImGui.GetWindowDrawList();

        ReadOnlySpan<(float r, float a)> rings = [(4f, 0.16f), (2f, 0.30f)];
        foreach (var (r, a) in rings)
        {
            uint col = ImGui.GetColorU32(TextGlow with { W = a * alpha });
            for (int i = 0; i < 8; i++)
            {
                float ang = MathF.PI * 2f * i / 8f;
                dl.AddText(origin + new Vector2(MathF.Cos(ang) * r, MathF.Sin(ang) * r), col, text);
            }
        }
        dl.AddText(origin, ImGui.GetColorU32(TextCore with { W = alpha }), text);

        if (font is not null) ImGui.PopFont();
    }
}
