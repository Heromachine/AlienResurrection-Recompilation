using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Host.Window;

namespace AlienResurrectionLauncher;

public enum SplashChoice { None, Play, Exit }

// The launcher's front screen: title, three options, and a credits panel. Shown after the disc and
// save location are resolved and BEFORE any recompile work begins, so a first run does not make
// somebody stare at a build log before they have chosen to start.
//
// Drawn as an IPanel through the engine's own PanelManager, which is public API, so this needs no
// changes to the runtime's render loop. The launcher pumps frames until Choice stops being None.
public sealed class SplashScreen : IPanel
{
    public string Name => "Launcher";
    public bool IsOpen { get; set; } = true;

    public SplashChoice Choice { get; private set; } = SplashChoice.None;

    bool _credits;
    bool _mods;
    ModSettings? _modSettings;   // loaded when the Mods page first opens, saved on Back
    bool _pendingOpen = true;
    readonly RadarBackground _radar = new();
    bool _focusPending = true;

    const string PopupId = "##splash";

    // The lowercase 'i' is a workaround for ONE font and must not leak into any other.
    //
    // In the Alien Resurrection face the capital I is the tall spiked "alien" form -- 2975 font
    // units tall against ~1188 for every other capital, overshooting far above and below the line.
    // Its lowercase glyph is a plain 4-point bar at normal cap height, and the lowercase set is
    // otherwise the same caps design (identical advance widths, a 21-unit baseline shift), so
    // spelling it with 'i' yields an ordinary I and changes nothing else.
    //
    // In an ordinary fallback face that trick backfires: lowercase 'i' is a real lowercase i, dot
    // and all, so the words would render as "ALiEN" / "RESURRECTiON". Fallbacks get proper capitals.
    static string TitleTop => HostFonts.Loaded ? "ALiEN" : "ALIEN";
    static string TitleBottom => HostFonts.Loaded ? "RESURRECTiON" : "RESURRECTION";
    const string Subtitle = "Recompilation";

    // Kept in sync with AlienResurrectionLauncher.csproj's <Version> -- there is no build step that
    // threads MSBuild's version into this string, so bump both together.
    const string AppVersion = "0.3.0";

    // How far the second title line is pulled up toward the first, in pixels. Proportional to the
    // title size so the pairing survives a font-size change; raise it to tighten further.
    static float TitleLineTighten => (HostFonts.Title != null ? HostFonts.TitleSize : 20f) * 0.34f;

    // Palette. Deliberately not the ImGui defaults: this is the first thing anyone sees, on a
    // full-screen dark field, so text and buttons need real contrast rather than the muted greys
    // that read fine inside a small debug panel and disappear here.
    static readonly Vector4 Bg      = new(0.11f, 0.12f, 0.13f, 1f);
    static readonly Vector4 Text    = new(0.94f, 0.94f, 0.95f, 1f);
    static readonly Vector4 Dim     = new(0.72f, 0.73f, 0.75f, 1f);
    static readonly Vector4 Accent  = new(1.00f, 0.62f, 0.16f, 1f);
    static readonly Vector4 Btn     = new(0.22f, 0.24f, 0.27f, 1f);
    static readonly Vector4 BtnHov  = new(0.34f, 0.37f, 0.41f, 1f);
    static readonly Vector4 BtnAct  = new(0.48f, 0.52f, 0.57f, 1f);
    static readonly Vector4 Border  = new(0.45f, 0.48f, 0.52f, 1f);
    static readonly Vector4 Warn    = new(1.00f, 0.76f, 0.30f, 1f);
    // Bio-green glow. The core stays bright and near-neutral so the letterforms read crisply; the
    // halo carries the colour. Drawing the colour alone at full strength reads as blur, not glow.
    static readonly Vector4 TitleCore = new(0.97f, 0.99f, 0.97f, 1f);   // white
    // RESURRECTION on the FALLBACK face only. The Alien face is light and open, so white reads well
    // at 40px; a fallback like DejaVu Sans Bold is a much heavier letterform and white makes it
    // look like a slab. Dropping the fill to dark grey lets the green halo carry the word as an
    // outline instead. The supplied font keeps the white treatment.
    static readonly Vector4 TitleCoreFallback = new(0.32f, 0.33f, 0.34f, 1f);
    static readonly Vector4 Glow      = new(0.24f, 1.00f, 0.42f, 1f);
    static readonly Vector4 TitleGlow = new(0.20f, 0.85f, 0.35f, 1f);

    public void Draw()
    {
        if (!IsOpen) return;

        // NOT a modal, despite a modal being the obvious choice for a front screen. A modal blocks
        // input to everything behind it -- including the main menu bar, which is where Settings ->
        // Disc/BIOS/Saves live. Locking the user out of the save-location picker on the very screen
        // where they would want it is worse than the problem a modal solves.
        //
        // The problem it solved was real, though: HostWindow.DrawDockspace() paints a full-viewport
        // DockSpace before PanelManager.DrawPanels() runs, and the first attempt lost every click to
        // it. The actual culprit was NoBringToFrontOnFocus on this window, which pinned it behind
        // that dockspace. A plain window WITHOUT that flag, focused once on the way in, sits above
        // the dockspace and below the menu bar -- which is exactly the arrangement wanted.
        var vp = ImGui.GetMainViewport();

        // WorkPos/WorkSize, not Pos/Size: the work area excludes the menu bar, so the splash stops
        // short of it instead of covering the thing it must not cover.
        ImGui.SetNextWindowPos(vp.WorkPos);
        ImGui.SetNextWindowSize(vp.WorkSize);
        if (_pendingOpen)
        {
            ImGui.SetNextWindowFocus();
            _pendingOpen = false;
        }

        ImGui.PushStyleColor(ImGuiCol.WindowBg, Bg);
        ImGui.PushStyleColor(ImGuiCol.Text, Text);
        ImGui.PushStyleColor(ImGuiCol.Button, Btn);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, BtnHov);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, BtnAct);
        ImGui.PushStyleColor(ImGuiCol.Border, Border);
        // The focus rectangle nav draws around the selected button. ImGui's default is blue, which
        // is the one colour on this screen that belongs to nothing else on it.
        ImGui.PushStyleColor(ImGuiCol.NavHighlight, Glow);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 3f);

        if (ImGui.Begin(PopupId,
                ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
                ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings |
                ImGuiWindowFlags.NoDocking))
        {
            // Before the content, so everything below draws over it. Sized off the work area's
            // shorter side so the tracker stays circular on any window shape.
            _radar.Draw(ImGui.GetWindowDrawList(),
                        vp.WorkPos + vp.WorkSize * 0.5f,
                        MathF.Min(vp.WorkSize.X, vp.WorkSize.Y) * 0.46f);

            if (_credits) DrawCredits();
            else if (_mods) DrawMods();
            else DrawMenu();
        }
        ImGui.End();

        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(7);
    }

    void Close(SplashChoice choice)
    {
        Choice = choice;
        IsOpen = false;
    }

    void DrawMenu()
    {
        float w = ImGui.GetContentRegionAvail().X;

        ImGui.Dummy(new Vector2(0, ImGui.GetContentRegionAvail().Y * 0.18f));

        // The HostFonts faces are null unless the user supplied a display font -- see the launcher's
        // font resolution. Everything still lays out correctly on the default face, just smaller.
        PushFont(HostFonts.Title);
        GlowCentered(TitleTop, w, TitleCore, TitleGlow);
        PopFont(HostFonts.Title);

        // Pull the second line up. The gap is not padding we added -- it is the font's own leading:
        // the capitals occupy only about 58% of the em box, so a 96px line reserves roughly 40px of
        // empty space beneath the letterforms, and the default item spacing sits under that again.
        // Closing it by a fraction of the title size keeps the two lines locked together at any size.
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() - TitleLineTighten);

        PushFont(HostFonts.Subtitle);
        GlowCentered(TitleBottom, w, HostFonts.Loaded ? TitleCore : TitleCoreFallback, TitleGlow);
        PopFont(HostFonts.Subtitle);

        ImGui.Dummy(new Vector2(0, 10));

        // Plain amber, no halo -- the glow stays on the title alone so it reads as the focal point
        // rather than competing with the line under it.
        PushFont(HostFonts.Ui);
        ImGui.PushStyleColor(ImGuiCol.Text, Accent);
        Centered(Subtitle, w);
        ImGui.PopStyleColor();
        PopFont(HostFonts.Ui);

        ImGui.Dummy(new Vector2(0, 40));

        // Button labels ride the same UI face so they are readable at a glance rather than at the
        // default 13px, which is sized for debug panels.
        PushFont(HostFonts.Ui);
        var btn = new Vector2(320, HostFonts.Ui != null ? 58 : 46);
        if (CenteredButton("Play Game", btn, w)) Close(SplashChoice.Play);
        // Where the focus rectangle starts, so a pad user has something selected on arrival rather
        // than having to press a direction first to find out anything is selectable.
        if (_focusPending) { ImGui.SetItemDefaultFocus(); _focusPending = false; }
        ImGui.Dummy(new Vector2(0, 8));
        if (CenteredButton("Mods", btn, w)) { _modSettings ??= ModSettings.Load(); _mods = true; }
        ImGui.Dummy(new Vector2(0, 8));
        if (CenteredButton("About", btn, w)) _credits = true;
        ImGui.Dummy(new Vector2(0, 8));
        if (CenteredButton("Exit", btn, w)) Close(SplashChoice.Exit);
        PopFont(HostFonts.Ui);

        ImGui.Dummy(new Vector2(0, 28));
        Centered("Requires your own Alien Resurrection (USA) disc. No game data is distributed.",
                 w, dim: true);
    }

    void DrawCredits()
    {
        ImGui.Dummy(new Vector2(0, 12));
        float w = ImGui.GetContentRegionAvail().X;
        ImGui.PushStyleColor(ImGuiCol.Text, Accent);
        Centered("About Recompilation", w);
        ImGui.PopStyleColor();
        Centered($"v{AppVersion}", w, dim: true);
        ImGui.Separator();

        if (ImGui.BeginChild("##creditsbody", new Vector2(0, ImGui.GetContentRegionAvail().Y - 60)))
        {
            // First, not last. A warning nobody scrolls to is a warning nobody reads -- and the
            // issues below are ones a player WILL meet, so they should not come as a surprise.
            Section("Work in progress");
            ImGui.PushStyleColor(ImGuiCol.Text, Warn);
            ImGui.TextWrapped(
                "This is an early, incomplete recompilation. It is playable, but it is not finished "
                + "and you should expect to run into problems. Known issues at this release:");
            ImGui.PopStyleColor();
            ImGui.Spacing();
            Item("Progression", "One door in the airlock section may not open, which can leave you "
                            + "stuck. The key-card and elevator doors are fixed.");
            Item("Freezes", "Far rarer since 0.3.0, but a stall during a level transition has not "
                            + "been ruled out. The window stays alive; restart if the game stops.");
            Item("Menu artwork", "The main menu's logo and glow are often garbled. The art loads "
                            + "correctly from the disc but is damaged before it is drawn; still being "
                            + "tracked down. Gameplay is not affected.");
            Item("Graphics", "The display can go black after loading a save. Leaving fullscreen may "
                            + "leave you with no menu.");
            Item("Audio", "Voice audio is missing from in-engine cutscenes.");
            Item("Timing", "The attract demo and the scene behind the main menu run too fast.");
            Item("Settings", "Do not enable native resolution -- it hangs the game at boot.");
            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Text, Dim);
            ImGui.TextWrapped(
                "Save often, and keep more than one save. Fixes for these are planned for a later "
                + "update rather than held back for this one.");
            ImGui.PopStyleColor();

            Section("Recompilation engine");
            Item("RecompOne", "by BlackLabelHQ -- the recompiler and runtime this is built on (MIT). "
                            + "This project uses a fork of it. Not affiliated with or endorsed by them.");

            Section("Original game");
            Item("Alien Resurrection", "developed by Argonaut Games, published by Fox Interactive. "
                            + "All game code, assets and trademarks belong to their respective owners. "
                            + "This launcher distributes none of them -- it recompiles the disc you "
                            + "already own, on your own machine.");

            Section("Libraries");
            Item("Dear ImGui", "by Omar Cornut and contributors (MIT)");
            Item("Silk.NET", "by the .NET Foundation and contributors (MIT)");
            Item("NativeFileDialogSharp", "bindings for nativefiledialog (zlib)");
            Item("MonoMod", "runtime detour library (MIT)");
            Item(".NET / Roslyn", "by Microsoft (MIT)");

            Section("Assets");
            Item("SDL_GameControllerDB", "community controller mappings");
            Item("Shinonome 16dot font", "by Yasuyuki Furukawa (public domain), maintained by /efont/ "
                            + "-- the source of the BIOS character fonts");
            if (HostFonts.Loaded)
                Item("\"Alien Resurrection\" font", "by Jens R. Ziehn. Supplied by you; not distributed "
                            + "with this launcher.");

            Section("This port");
            Item("Engine fixes and launcher", "built on the RecompOne fork maintained for this project.");

            ImGui.Dummy(new Vector2(0, 8));
            ImGui.TextWrapped(
                "Licenses differ between components. See each project's own license for the terms "
                + "that apply to it.");
        }
        ImGui.EndChild();

        ImGui.Separator();
        if (CenteredButton("Back", new Vector2(160, 34), ImGui.GetContentRegionAvail().X))
        {
            _credits = false;
            _focusPending = true;   // re-seat the focus rectangle on Play Game
        }
    }

    static void PushFont(ImFontPtr? f) { if (f is { } v) ImGui.PushFont(v); }
    static void PopFont(ImFontPtr? f)  { if (f != null) ImGui.PopFont(); }

    // Poor-man's glow: ImGui has no shader effects, so the halo is the same string stamped around
    // the centre at two radii with low alpha, then the core drawn on top. Cheap, and it composites
    // correctly because each stamp is additive-ish over the dark background.
    //
    // Uses the window draw list rather than ImGui.Text so every stamp lands at an exact pixel offset
    // from one computed origin -- walking the cursor instead would round differently per pass and
    // make the halo lopsided.
    static void GlowCentered(string text, float width, Vector4 core, Vector4 glow)
    {
        var sz = ImGui.CalcTextSize(text);
        var origin = ImGui.GetCursorScreenPos();
        origin.X += Math.Max(0, (width - sz.X) * 0.5f);

        var dl = ImGui.GetWindowDrawList();
        ReadOnlySpan<(float r, float a)> rings = [(4f, 0.16f), (2f, 0.30f)];
        foreach (var (r, a) in rings)
        {
            uint col = ImGui.GetColorU32(glow with { W = a });
            for (int i = 0; i < 8; i++)
            {
                float ang = MathF.PI * 2f * i / 8f;
                dl.AddText(origin + new Vector2(MathF.Cos(ang) * r, MathF.Sin(ang) * r), col, text);
            }
        }
        dl.AddText(origin, ImGui.GetColorU32(core), text);

        // The draw list does not move the cursor; reserve the space so layout continues normally.
        ImGui.Dummy(sz);
    }

    static void Section(string s)
    {
        ImGui.Dummy(new Vector2(0, 6));
        ImGui.PushStyleColor(ImGuiCol.Text, Accent);
        ImGui.TextUnformatted(s);
        ImGui.PopStyleColor();
        ImGui.Spacing();
    }

    static void Item(string name, string detail)
    {
        ImGui.Bullet();
        ImGui.SameLine();
        ImGui.TextUnformatted(name);
        ImGui.Indent();
        ImGui.PushStyleColor(ImGuiCol.Text, Dim);
        ImGui.TextWrapped(detail);
        ImGui.PopStyleColor();
        ImGui.Unindent();
    }

    // The Mods page: settings for the bundled game mods (bundled-mods/), chosen BEFORE the game
    // starts. The engine compiles and loads mods at game start and each one reads mod-settings.json
    // once then, so a change here applies from the next Play. That is why this lives on the front
    // screen rather than in-game.
    void DrawMods()
    {
        var s = _modSettings ??= ModSettings.Load();
        var fl = s.Flashlight;

        ImGui.Dummy(new Vector2(0, 12));
        float w = ImGui.GetContentRegionAvail().X;
        ImGui.PushStyleColor(ImGuiCol.Text, Accent);
        Centered("Mods", w);
        ImGui.PopStyleColor();
        Centered("Applied when the game starts. Settings are saved when you go back.", w, dim: true);
        ImGui.Separator();

        // One readable column in the middle of the window rather than full width: a slider stretched
        // across 1280px is hard to read and harder to set precisely.
        float col = MathF.Min(620f, w - 40f);
        float indent = MathF.Max(0f, (w - col) * 0.5f);

        if (ImGui.BeginChild("##modsbody", new Vector2(0, ImGui.GetContentRegionAvail().Y - 60)))
        {
            ImGui.Indent(indent);
            ImGui.PushItemWidth(col * 0.55f);

            Section("Cheats");
            bool god = s.GodMode, ammo = s.InfiniteAmmo;
            if (ImGui.Checkbox("God mode", ref god)) s.GodMode = god;
            Hint("The game's own invincibility flag: no damage, no health drain, no death.");
            if (ImGui.Checkbox("Infinite ammo", ref ammo)) s.InfiniteAmmo = ammo;
            Hint("Every ammo type you carry becomes unlimited, including ammo picked up later.");

            Section("Lighting");
            if (ImGui.RadioButton("Classic flashlight (tuned below)", s.Lighting != "modern")) s.Lighting = "classic";
            ImGui.BeginDisabled();
            ImGui.RadioButton("Modern lighting (coming later)", s.Lighting == "modern");
            ImGui.EndDisabled();
            Hint("Modern per-pixel lighting needs renderer work and is not available yet.");

            Section("Flashlight");
            bool en = fl.Enabled;
            if (ImGui.Checkbox("Use these flashlight settings", ref en)) fl.Enabled = en;
            Hint("Off = the original flashlight, untouched.");

            ImGui.BeginDisabled(!fl.Enabled);
            float bright = fl.Brightness * 100f;
            if (ImGui.SliderFloat("Brightness", ref bright, 50f, 300f, "%.0f%%")) fl.Brightness = bright / 100f;
            bool warm = fl.Warm;
            if (ImGui.Checkbox("Warm colour", ref warm)) fl.Warm = warm;
            Hint(fl.Warm ? "Warm, incandescent tint." : "Original cool blue-white tint.");
            float reach = fl.Reach * 100f;
            if (ImGui.SliderFloat("Reach", ref reach, 50f, 300f, "%.0f%%")) fl.Reach = reach / 100f;
            bool flicker = fl.Flicker;
            if (ImGui.Checkbox("Flicker", ref flicker)) fl.Flicker = flicker;
            Hint(fl.Flicker ? "The original unsteady beam." : "A steady beam at full reach.");
            bool unlimited = fl.UnlimitedBattery;
            if (ImGui.Checkbox("Unlimited battery", ref unlimited)) fl.UnlimitedBattery = unlimited;
            ImGui.BeginDisabled(fl.UnlimitedBattery);
            float batt = fl.Battery;
            if (ImGui.SliderFloat("Battery", ref batt, 1f, 10f, "x%.1f")) fl.Battery = batt;
            ImGui.EndDisabled();
            Hint("Battery capacity, as a multiple of the original.");
            ImGui.EndDisabled();

            ImGui.Dummy(new Vector2(0, 8));
            if (ImGui.Button("Reset to defaults")) _modSettings = new ModSettings();

            ImGui.PopItemWidth();
            ImGui.Unindent(indent);
        }
        ImGui.EndChild();

        ImGui.Separator();
        if (CenteredButton("Back", new Vector2(160, 34), ImGui.GetContentRegionAvail().X))
        {
            _modSettings?.Save();
            _mods = false;
            _focusPending = true;
        }
    }

    static void Hint(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Dim);
        ImGui.Indent(28f);
        ImGui.TextWrapped(text);
        ImGui.Unindent(28f);
        ImGui.PopStyleColor();
    }

    static void Centered(string text, float width, bool dim = false)
    {
        var sz = ImGui.CalcTextSize(text);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, (width - sz.X) * 0.5f));
        if (dim) ImGui.PushStyleColor(ImGuiCol.Text, Dim);
        ImGui.TextUnformatted(text);
        if (dim) ImGui.PopStyleColor();
    }

    static bool CenteredButton(string label, Vector2 size, float width)
    {
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, (width - size.X) * 0.5f));
        return ImGui.Button(label, size);
    }
}
