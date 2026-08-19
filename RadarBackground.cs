using System.Numerics;
using ImGuiNET;

namespace AlienResurrectionLauncher;

// Minimal motion tracker behind the menu: faint static range rings, pulses travelling out from the
// centre, and the occasional contact that surfaces and fades.
//
// Drawn entirely with ImGui's draw list -- no textures, no shaders, no extra dependency. "Fuzzy" is
// several concentric strokes at falling alpha rather than a real blur, which is cheap and reads
// correctly at any size; a genuine blur would need a render target and a shader pass for an effect
// nobody looks at directly.
//
// Purely decorative and stateless from the caller's side: it owns its own timing off
// ImGui.GetIO().DeltaTime, so it animates for as long as the launcher pumps frames.
public sealed class RadarBackground
{
    // A travelling pulse ring.
    struct Pulse { public float R; }

    // A contact: surfaces, holds, fades.
    struct Blip
    {
        public Vector2 P;
        public float Age;
        public float Life;
        public float Size;
    }

    readonly List<Pulse> _pulses = [];
    readonly List<Blip> _blips = [];
    readonly Random _rng = new();

    float _nextPulse;
    float _nextBlip = 1.5f;

    const float PulseEvery = 2.6f;   // seconds between pulses
    const float PulseSpeed = 0.34f;  // fraction of max radius per second
    const float BlipMinGap = 2.0f;
    const float BlipMaxGap = 5.5f;

    // The sweep covers the TOP half only. In ImGui's angle space 0 is +X and the screen's Y axis
    // points down, so PI..2PI traces the upper semicircle. Swap to 0..PI for the lower half.
    const float ArcFrom = MathF.PI;
    const float ArcTo = MathF.PI * 2f;

    // The fixed grid: original faint alphas, drawn with a heavier stroke so the arcs read as
    // structure without getting brighter. The travelling pulses compute their own alpha and
    // thickness and are deliberately untouched by these.
    const float GridAlpha = 0.07f;
    const float AxisAlpha = 0.06f;
    const float GridThickness = 2.2f;

    static readonly Vector4 Green = new(0.22f, 1.00f, 0.42f, 1f);
    static readonly Vector4 White = new(0.92f, 1.00f, 0.94f, 1f);

    public void Draw(ImDrawListPtr dl, Vector2 centre, float maxRadius)
    {
        float dt = Math.Clamp(ImGui.GetIO().DeltaTime, 0f, 0.1f);

        StaticRings(dl, centre, maxRadius);
        Advance(dt, maxRadius);
        DrawPulses(dl, centre, maxRadius);
        DrawBlips(dl, centre);
    }

    void StaticRings(ImDrawListPtr dl, Vector2 c, float max)
    {
        uint faint = Col(Green, GridAlpha);
        for (int i = 1; i <= 4; i++)
            Arc(dl, c, max * i / 4f, faint, GridThickness);

        // The baseline the sweep sits on, plus a short vertical tick. No full crosshair: on a
        // semicircle the lower arm would hang off the display with nothing to close it.
        uint axis = Col(Green, AxisAlpha);
        dl.AddLine(c with { X = c.X - max }, c with { X = c.X + max }, axis, GridThickness);
        dl.AddLine(c with { Y = c.Y - max }, c with { Y = c.Y - max * 0.06f }, axis, GridThickness);
    }

    void Advance(float dt, float max)
    {
        _nextPulse -= dt;
        if (_nextPulse <= 0f)
        {
            _pulses.Add(new Pulse { R = 0f });
            _nextPulse = PulseEvery;
        }

        for (int i = _pulses.Count - 1; i >= 0; i--)
        {
            var p = _pulses[i];
            p.R += PulseSpeed * max * dt;
            if (p.R > max * 1.05f) _pulses.RemoveAt(i);
            else _pulses[i] = p;
        }

        _nextBlip -= dt;
        if (_nextBlip <= 0f)
        {
            // A contact is REVEALED BY a pulse, so it is born on a live ring rather than at an
            // arbitrary point: pick a pulse that has travelled far enough to have visible arc, and
            // sit the contact exactly on its radius. With no pulse in flight there is nothing to
            // reveal anything, so wait rather than inventing one out of the dark.
            var lit = _pulses.Where(x => x.R > max * 0.12f && x.R < max * 0.97f).ToList();
            if (lit.Count == 0)
            {
                _nextBlip = 0.25f;   // check again shortly, do not burn the whole interval
            }
            else
            {
                var ring = lit[_rng.Next(lit.Count)];
                float ang = ArcFrom + (float)_rng.NextDouble() * (ArcTo - ArcFrom);
                _blips.Add(new Blip
                {
                    P = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * ring.R,
                    Age = 0f,
                    Life = 1.8f + (float)_rng.NextDouble() * 1.6f,
                    Size = 14f + (float)_rng.NextDouble() * 10f,
                });
                _nextBlip = BlipMinGap + (float)_rng.NextDouble() * (BlipMaxGap - BlipMinGap);
            }
        }

        for (int i = _blips.Count - 1; i >= 0; i--)
        {
            var b = _blips[i];
            b.Age += dt;
            if (b.Age >= b.Life) _blips.RemoveAt(i);
            else _blips[i] = b;
        }
    }

    void DrawPulses(ImDrawListPtr dl, Vector2 c, float max)
    {
        foreach (var p in _pulses)
        {
            // Fade out over the journey, and in again over the first fifth so a ring does not pop
            // into existence at full strength.
            float t = p.R / max;
            float a = MathF.Min(t / 0.2f, 1f) * (1f - t) * 0.55f;
            if (a <= 0.001f) continue;

            for (int k = -2; k <= 2; k++)
            {
                float ringA = a * (1f - MathF.Abs(k) * 0.32f);
                Arc(dl, c, p.R + k * 1.6f, Col(Green, ringA), k == 0 ? 2f : 1f);
            }
        }
    }

    void DrawBlips(ImDrawListPtr dl, Vector2 c)
    {
        foreach (var b in _blips)
        {
            float t = b.Age / b.Life;
            // Quick surface, long decay -- a contact that appears instantly and lingers reads as
            // something moving, where a symmetric fade just looks like a pulsing dot.
            float a = t < 0.12f ? t / 0.12f : 1f - (t - 0.12f) / 0.88f;
            a = Math.Clamp(a, 0f, 1f);
            if (a <= 0.001f) continue;

            var p = c + b.P;
            for (int k = 4; k >= 1; k--)
                dl.AddCircleFilled(p, b.Size * k * 0.55f, Col(White, a * 0.11f / k), 24);
            dl.AddCircleFilled(p, b.Size * 0.42f, Col(White, a * 0.85f), 24);
        }
    }

    static void Arc(ImDrawListPtr dl, Vector2 c, float r, uint col, float thickness)
    {
        if (r <= 0.5f) return;
        dl.PathArcTo(c, r, ArcFrom, ArcTo, 96);
        dl.PathStroke(col, ImDrawFlags.None, thickness);
    }

    static uint Col(Vector4 c, float a) => ImGui.GetColorU32(c with { W = Math.Clamp(a, 0f, 1f) });
}
