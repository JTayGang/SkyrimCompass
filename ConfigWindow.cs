using System;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace SkyrimCompass;

public sealed class ConfigWindow : Window
{
    private readonly Plugin plugin;

    // "Add new override" form state — not persisted, resets on commit.
    private PlayerIconOverride _newOverride = new();

    // Dropdown selection — not persisted (applied colors themselves are).
    private int _selectedThemeIndex = 0;

    public ConfigWindow(Plugin plugin)
        : base("Skyrim Compass Settings##skyrimcompasscfg",
               ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar)
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(370, 200),
            MaximumSize = new Vector2(620, 800),
        };
    }

    public override void Draw()
    {
        var  cfg     = plugin.Config;
        bool changed = false;

        bool enabled = cfg.Enabled;
        if (ImGui.Checkbox("##enabled", ref enabled)) { cfg.Enabled = enabled; changed = true; }
        ImGui.SameLine();
        ImGui.Text("Enable Compass");
        ImGui.Separator();

        if (ImGui.BeginTabBar("##tabs"))
        {
            changed |= DrawLayoutTab(cfg);
            changed |= DrawColorsTab(cfg);
            changed |= DrawDetectionTab(cfg);
            changed |= DrawPlayersTab(cfg);
            changed |= DrawCombatTab(cfg);
            changed |= DrawNpcsTab(cfg);
            changed |= DrawGatheringTab(cfg);
            changed |= DrawTreasureTab(cfg);
            changed |= DrawAetherytesTab(cfg);
            changed |= DrawFatesTab(cfg);
            ImGui.EndTabBar();
        }

        ImGui.Separator();
        if (ImGui.Button("Close", new Vector2(80, 0)))
            IsOpen = false;

        if (changed)
            cfg.Save(plugin.PluginInterface);
    }

    // ── Layout tab ───────────────────────────────────────────────────────────

    private static bool DrawLayoutTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem("Layout")) return false;
        bool changed = false;

        float v;
        int   iv;

        iv = (int)cfg.CompassWidth;
        if (ImGui.SliderInt("Width##w", ref iv, 200, 1400))
        { cfg.CompassWidth = iv; changed = true; }

        iv = (int)cfg.CompassHeight;
        if (ImGui.SliderInt("Height##h", ref iv, 20, 80))
        { cfg.CompassHeight = iv; changed = true; }

        iv = (int)cfg.YOffset;
        if (ImGui.SliderInt("Y Offset (from top)##yo", ref iv, 0, 300))
        { cfg.YOffset = iv; changed = true; }

        ImGui.Spacing();

        iv = (int)cfg.VisibleDegrees;
        if (ImGui.SliderInt("Visible Degrees##vd", ref iv, 30, 180))
        { cfg.VisibleDegrees = iv; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "How many degrees are visible in the LINEAR centre zone.\n" +
                "The lens effect extends additional degrees beyond this at the edges.");

        v = cfg.LensStrength;
        if (ImGui.SliderFloat("Lens Strength##ls", ref v, 1.0f, 3.0f))
        { cfg.LensStrength = v; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Fisheye/lens distortion at the edges.\n" +
                "1.0 = linear (no effect).\n" +
                "1.6 = shows ~60%% more degrees at the edges, compressed.\n" +
                "2.0 = shows twice the degrees at the edges.");

        v = cfg.FontScale;
        if (ImGui.SliderFloat("Font Scale##fs", ref v, 0.5f, 2.5f))
        { cfg.FontScale = v; changed = true; }

        ImGui.Spacing();

        bool sh = cfg.ShowHeadingText;
        if (ImGui.Checkbox("Show numeric heading below bar", ref sh))
        { cfg.ShowHeadingText = sh; changed = true; }

        bool hdc = cfg.HideDuringCutscenes;
        if (ImGui.Checkbox("Hide during cutscenes", ref hdc))
        { cfg.HideDuringCutscenes = hdc; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Skips drawing the compass entirely while the camera is locked to a\n" +
                "cutscene (story cutscenes, skippable cinematics, and group pose) —\n" +
                "there's nothing to navigate to while the camera isn't yours anyway.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        bool ucd = cfg.UseCameraDirection;
        if (ImGui.Checkbox("Use camera direction instead of character facing", ref ucd))
        { cfg.UseCameraDirection = ucd; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "On: the compass follows where your CAMERA is looking (third-person\n" +
                "free camera, screenshots, sightseeing).\n" +
                "Off: the compass follows your CHARACTER's facing direction,\n" +
                "matching how Skyrim's compass behaves (recommended for combat/navigation).");

        ImGui.Indent();
        ImGui.BeginDisabled(!cfg.UseCameraDirection);
        bool ucp = cfg.UseCameraPosition;
        if (ImGui.Checkbox("Also use camera location for distances##ucp", ref ucp))
        { cfg.UseCameraPosition = ucp; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Measures entity bearings/distances from your CAMERA's position instead\n" +
                "of your character's. Useful if you play heavily zoomed out or use a\n" +
                "camera offset mod — keeps compass markers consistent with what you're\n" +
                "actually seeing on screen rather than where your character is standing.\n" +
                "Only takes effect while 'Use camera direction' above is also on.");
        ImGui.EndDisabled();
        ImGui.Unindent();

        ImGui.Spacing();
        ImGui.TextDisabled("Rotation Offset  (set to 180 if N and S are swapped)");
        iv = (int)cfg.RotationOffset;
        if (ImGui.SliderInt("##rotoff", ref iv, -180, 180))
        { cfg.RotationOffset = iv; changed = true; }

        ImGui.EndTabItem();
        return changed;
    }

    // ── Colors tab ───────────────────────────────────────────────────────────

    private sealed class ColorTheme
    {
        public string  Name          = "";
        public Vector4 Background;
        public Vector4 Border;
        public Vector4 Cardinal;
        public Vector4 Intercardinal;
        public Vector4 Tick;
        public Vector4 Player;
        public Vector4 Enemy;
        public Vector4 Npc;
        public Vector4 Gathering;
        public Vector4 Treasure;
        public Vector4 Aetheryte;
        public Vector4 Fate;
    }

    // "Original" mirrors Configuration's defaults exactly — picking it restores the out-of-box look.
    private static readonly ColorTheme[] ColorThemes =
    {
        new ColorTheme
        {
            Name          = "Original",
            Background    = new(0.05f, 0.04f, 0.03f, 0.82f),
            Border        = new(0.48f, 0.42f, 0.27f, 0.92f),
            Cardinal      = new(1.00f, 0.97f, 0.88f, 1.00f),
            Intercardinal = new(0.72f, 0.70f, 0.65f, 0.88f),
            Tick          = new(0.58f, 0.56f, 0.52f, 0.72f),
            Player        = new(0.40f, 0.65f, 1.00f, 0.92f),
            Enemy         = new(1.00f, 0.25f, 0.25f, 0.92f),
            Npc           = new(0.95f, 0.88f, 0.35f, 0.92f),
            Gathering     = new(0.30f, 0.92f, 0.40f, 0.92f),
            Treasure      = new(1.00f, 0.80f, 0.15f, 0.95f),
            Aetheryte     = new(0.55f, 0.85f, 0.95f, 0.92f),
            Fate          = new(0.82f, 0.35f, 0.95f, 0.95f),
        },
        new ColorTheme
        {
            Name          = "Frostfall",
            Background    = new(0.03f, 0.06f, 0.10f, 0.84f),
            Border        = new(0.55f, 0.75f, 0.88f, 0.92f),
            Cardinal      = new(0.92f, 0.97f, 1.00f, 1.00f),
            Intercardinal = new(0.68f, 0.82f, 0.90f, 0.88f),
            Tick          = new(0.55f, 0.68f, 0.78f, 0.72f),
            Player        = new(0.50f, 0.85f, 1.00f, 0.92f),
            Enemy         = new(1.00f, 0.35f, 0.40f, 0.92f),
            Npc           = new(0.85f, 0.95f, 1.00f, 0.92f),
            Gathering     = new(0.40f, 0.95f, 0.85f, 0.92f),
            Treasure      = new(0.95f, 0.92f, 0.65f, 0.95f),
            Aetheryte     = new(0.60f, 0.90f, 1.00f, 0.92f),
            Fate          = new(0.75f, 0.55f, 1.00f, 0.95f),
        },
        new ColorTheme
        {
            Name          = "Inferno",
            Background    = new(0.08f, 0.03f, 0.02f, 0.85f),
            Border        = new(0.75f, 0.32f, 0.10f, 0.92f),
            Cardinal      = new(1.00f, 0.88f, 0.60f, 1.00f),
            Intercardinal = new(0.88f, 0.58f, 0.32f, 0.88f),
            Tick          = new(0.65f, 0.38f, 0.22f, 0.72f),
            Player        = new(0.45f, 0.75f, 1.00f, 0.92f),
            Enemy         = new(1.00f, 0.18f, 0.10f, 0.95f),
            Npc           = new(1.00f, 0.78f, 0.30f, 0.92f),
            Gathering     = new(0.55f, 0.90f, 0.35f, 0.92f),
            Treasure      = new(1.00f, 0.70f, 0.10f, 0.95f),
            Aetheryte     = new(0.95f, 0.55f, 0.85f, 0.92f),
            Fate          = new(1.00f, 0.40f, 0.85f, 0.95f),
        },
        new ColorTheme
        {
            Name          = "Verdant",
            Background    = new(0.03f, 0.06f, 0.03f, 0.84f),
            Border        = new(0.38f, 0.58f, 0.32f, 0.92f),
            Cardinal      = new(0.92f, 1.00f, 0.85f, 1.00f),
            Intercardinal = new(0.68f, 0.82f, 0.60f, 0.88f),
            Tick          = new(0.50f, 0.62f, 0.45f, 0.72f),
            Player        = new(0.45f, 0.80f, 1.00f, 0.92f),
            Enemy         = new(1.00f, 0.30f, 0.25f, 0.92f),
            Npc           = new(0.92f, 0.85f, 0.40f, 0.92f),
            Gathering     = new(0.45f, 1.00f, 0.50f, 0.95f),
            Treasure      = new(1.00f, 0.85f, 0.25f, 0.95f),
            Aetheryte     = new(0.55f, 0.92f, 0.80f, 0.92f),
            Fate          = new(0.78f, 0.95f, 0.35f, 0.95f),
        },
        new ColorTheme
        {
            Name          = "Void",
            Background    = new(0.04f, 0.02f, 0.08f, 0.85f),
            Border        = new(0.58f, 0.38f, 0.78f, 0.92f),
            Cardinal      = new(0.92f, 0.85f, 1.00f, 1.00f),
            Intercardinal = new(0.72f, 0.62f, 0.85f, 0.88f),
            Tick          = new(0.55f, 0.48f, 0.65f, 0.72f),
            Player        = new(0.55f, 0.72f, 1.00f, 0.92f),
            Enemy         = new(1.00f, 0.30f, 0.55f, 0.92f),
            Npc           = new(0.88f, 0.78f, 1.00f, 0.92f),
            Gathering     = new(0.55f, 0.95f, 0.65f, 0.92f),
            Treasure      = new(1.00f, 0.75f, 0.95f, 0.95f),
            Aetheryte     = new(0.68f, 0.55f, 1.00f, 0.92f),
            Fate          = new(0.85f, 0.40f, 1.00f, 0.95f),
        },
    };

    private static readonly string[] ColorThemeNames = Array.ConvertAll(ColorThemes, t => t.Name);

    private static void ApplyColorTheme(Configuration cfg, ColorTheme theme)
    {
        cfg.BackgroundColor    = theme.Background;
        cfg.BorderColor        = theme.Border;
        cfg.CardinalColor      = theme.Cardinal;
        cfg.IntercardinalColor = theme.Intercardinal;
        cfg.TickColor          = theme.Tick;
        cfg.PlayerColor        = theme.Player;
        cfg.EnemyColor         = theme.Enemy;
        cfg.NpcColor           = theme.Npc;
        cfg.GatheringColor     = theme.Gathering;
        cfg.TreasureColor      = theme.Treasure;
        cfg.AetheryteColor     = theme.Aetheryte;
        cfg.FateColor          = theme.Fate;
    }

    private bool DrawColorsTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem("Colors")) return false;
        bool    changed = false;
        Vector4 c;

        c = cfg.BackgroundColor;
        if (ImGui.ColorEdit4("Background##bgc", ref c))
        { cfg.BackgroundColor = c; changed = true; }

        c = cfg.BorderColor;
        if (ImGui.ColorEdit4("Border##bdc", ref c))
        { cfg.BorderColor = c; changed = true; }

        c = cfg.CardinalColor;
        if (ImGui.ColorEdit4("Cardinal labels  (N / S / E / W)##cdc", ref c))
        { cfg.CardinalColor = c; changed = true; }

        c = cfg.IntercardinalColor;
        if (ImGui.ColorEdit4("Intercardinal labels  (NE / SW …)##icc", ref c))
        { cfg.IntercardinalColor = c; changed = true; }

        c = cfg.TickColor;
        if (ImGui.ColorEdit4("Tick marks##tkc", ref c))
        { cfg.TickColor = c; changed = true; }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextDisabled("Color theme presets");
        ImGui.SetNextItemWidth(180);
        if (ImGui.Combo("##colortheme", ref _selectedThemeIndex, ColorThemeNames, ColorThemeNames.Length))
        {
            ApplyColorTheme(cfg, ColorThemes[_selectedThemeIndex]);
            changed = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Overwrites every compass color in one click — bar background/border/\n" +
                "labels/ticks above, AND every marker category color over in its own\n" +
                "tab (players, enemies, NPCs, gathering, treasure, aetherytes, FATEs).\n" +
                "Pick \"Original\" to restore the plugin's defaults.\n" +
                "Anything can still be hand-tweaked afterward — picking a theme is just\n" +
                "a starting point, not a locked-in mode.");

        ImGui.EndTabItem();
        return changed;
    }

    // ── Detection tab ────────────────────────────────────────────────────────

    private static bool DrawDetectionTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem("Detection")) return false;
        bool changed = false;

        ImGui.TextDisabled(
            "Settings shared across every marker category, including FATEs.\n" +
            "(FATEs use a multiplier of this distance — see the FATEs tab.)");
        ImGui.Spacing();

        ImGui.TextDisabled("Maximum detection distance (straight-line, includes height):");
        int md = (int)cfg.MaxMarkerDistance;
        if (ImGui.SliderInt("yalms##maxd", ref md, 10, 200))
        { cfg.MaxMarkerDistance = md; changed = true; }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextDisabled("Dot distance-fade curve");

        float nz = cfg.DotNearZone;
        if (ImGui.SliderFloat("Full opacity zone##nz", ref nz, 0.5f, 1.0f))
        { cfg.DotNearZone = nz; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Dots are fully opaque while closer than this fraction of max range.\n" +
                "0.85 = only the nearest 15%% of range is full brightness.\n" +
                "1.00 = always fully opaque (disables distance fade).");

        float fz = cfg.DotFarZone;
        if (ImGui.SliderFloat("Fade-to-zero zone##fz", ref fz, 0.0f, 0.5f))
        { cfg.DotFarZone = fz; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Dots fade to invisible below this fraction of max range.\n" +
                "0.25 = the outermost 25%% fades to zero.\n" +
                "0.00 = no fade-to-zero (dots stay at mid opacity until max range).");

        float ma = cfg.DotMidAlpha;
        if (ImGui.SliderFloat("Mid-range opacity##ma", ref ma, 0.0f, 1.0f))
        { cfg.DotMidAlpha = ma; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Opacity of dots in the middle distance band.\n" +
                "0.5 = 50%% visible.  0.0 = invisible in mid-range.  1.0 = fully opaque.");

        ImGui.EndTabItem();
        return changed;
    }

    // ── Players tab ──────────────────────────────────────────────────────────

    // Shared by existing override entries and the "add new" form.
    private static bool DrawOverrideRow(PlayerIconOverride ov, string idSuffix, float nameWidth)
    {
        bool changed = false;

        string name = ov.PlayerName;
        ImGui.SetNextItemWidth(nameWidth);
        if (ImGui.InputText($"##{idSuffix}name", ref name, 64)) { ov.PlayerName = name; changed = true; }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Player display name (exact, case-insensitive)");
        ImGui.SameLine();

        int iconId = ov.IconBaseId;
        ImGui.SetNextItemWidth(68f);
        if (ImGui.InputInt($"##{idSuffix}id", ref iconId, 0, 0)) { ov.IconBaseId = Math.Max(0, iconId); changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Game icon base ID\n(e.g. 62007 Paladin, 60453 Aetheryte, 61802 FC emblem)\nBrowse all icons with: /xldata icons");
        ImGui.SameLine();

        bool border = ov.ShowBorder;
        if (ImGui.Checkbox($"B##{idSuffix}b", ref border)) { ov.ShowBorder = border; changed = true; }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Draw a solid outer ring around the icon");
        ImGui.SameLine();
        ImGui.BeginDisabled(!ov.ShowBorder);
        Vector4 bc = ov.BorderColor;
        if (ImGui.ColorEdit4($"##{idSuffix}bc", ref bc, ColorPickerFlags)) { ov.BorderColor = bc; changed = true; }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Border ring color");
        ImGui.EndDisabled();
        ImGui.SameLine();

        bool fill = ov.ShowFill;
        if (ImGui.Checkbox($"F##{idSuffix}f", ref fill)) { ov.ShowFill = fill; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Draw an inward-fading fill behind the icon\n(same bloom effect as party role icon backgrounds)");
        ImGui.SameLine();
        ImGui.BeginDisabled(!ov.ShowFill);
        Vector4 fc = ov.FillColor;
        if (ImGui.ColorEdit4($"##{idSuffix}fc", ref fc, ColorPickerFlags)) { ov.FillColor = fc; changed = true; }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Fill color");
        ImGui.EndDisabled();
        ImGui.SameLine();

        bool clip = ov.ClipToCircle;
        if (ImGui.Checkbox($"○##{idSuffix}circ", ref clip)) { ov.ClipToCircle = clip; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Clip icon to a circle\n" +
                "Rounds square icon textures to fit neatly inside the border ring.\n" +
                "Uses ImGui's built-in rounded image rendering — no extra cost.");
        ImGui.SameLine();

        float mul = ov.SizeMultiplier;
        ImGui.SetNextItemWidth(58f);
        if (ImGui.DragFloat($"##{idSuffix}mul", ref mul, 0.05f, 0.5f, 3.0f, "%.2fx")) { ov.SizeMultiplier = Math.Clamp(mul, 0.5f, 3.0f); changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Per-icon size multiplier (stacks on top of the global 1.5× padding compensation).\n" +
                "1.0 = same apparent size as a party role icon.\n" +
                "Drag right for icons with heavy transparent padding,\n" +
                "drag left for icons with minimal padding that look oversized.");

        return changed;
    }

    private bool DrawPlayersTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem("Players")) return false;
        bool    b = cfg.ShowPlayers;
        Vector4 c = cfg.PlayerColor;
        bool changed = DrawEnableAndColor("players", "Players", ref b, ref c);
        cfg.ShowPlayers = b; cfg.PlayerColor = c;

        ImGui.Indent();
        ImGui.BeginDisabled(!cfg.ShowPlayers);

        float prMin = cfg.PartyRoleIconMinSize, prMax = cfg.PartyRoleIconMaxSize;
        if (DrawSizeSliders(ref prMin, ref prMax, 50, 60, "pr"))
        { cfg.PartyRoleIconMinSize = prMin; cfg.PartyRoleIconMaxSize = prMax; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Controls the size of EVERY player marker — the plain hollow ring,\n" +
                "the solid friend dot, and the party role icon below — together.");

        ImGui.Spacing();

        bool sfr = cfg.SolidFriendDots;
        if (ImGui.Checkbox("Solid dot for friends##sfr", ref sfr))
        { cfg.SolidFriendDots = sfr; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Players on your friends list render as a solid filled dot instead\n" +
                "of the default hollow ring, making them stand out in a crowd.\n" +
                "Uses the same friend flag the game's minimap and nameplates read from.\n" +
                "Has no effect on party members when role icons are enabled below.\n" +
                "Has no effect on players who have a named override below.");

        bool pri = cfg.ShowPartyRoleIcons;
        if (ImGui.Checkbox("Show job icon for party members##pri", ref pri))
        { cfg.ShowPartyRoleIcons = pri; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Party members show their unbordered class/job icon (IDs 62001-62047)\n" +
                "on a role-colored background dot: Tank=blue, Healer=green, DPS=red.\n" +
                "Takes priority over the solid friend dot above and over named overrides\n" +
                "below for anyone in your party.\n" +
                "Uses the same size slider above as every other player marker.");

        // ── Named player icon overrides ───────────────────────────────────────
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextDisabled("Named player overrides");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Replace the compass marker for specific players (by display name) with\n" +
                "a custom game icon — uses the same game icon base IDs as everywhere\n" +
                "else in the plugin (e.g. 62007 Paladin, 60453 Aetheryte, 61802 FC).\n" +
                "Browse all available icons and their IDs with: /xldata icons\n" +
                "Name match is exact and case-insensitive.\n" +
                "Party role icons still take priority over these for anyone in your party.\n" +
                "B = outer border ring.  F = inward-fading fill behind icon\n" +
                "  (same bloom effect used behind party job icons).\n" +
                "Both border and fill remain visible even if the icon hasn't loaded yet.");

        if (cfg.PlayerIconOverrides.Count == 0)
            ImGui.TextDisabled("  (no overrides — add one below)");

        // ── Existing entries ──────────────────────────────────────────────────
        int removeAt = -1;
        for (int i = 0; i < cfg.PlayerIconOverrides.Count; i++)
        {
            var ov = cfg.PlayerIconOverrides[i];
            ImGui.PushID(i);

            if (ImGui.Button("X##rmov")) removeAt = i;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Remove this override");
            ImGui.SameLine();

            changed |= DrawOverrideRow(ov, "ov", 110f);
            ImGui.PopID();
        }

        if (removeAt >= 0)
        { cfg.PlayerIconOverrides.RemoveAt(removeAt); changed = true; }

        // ── Add new override ──────────────────────────────────────────────────
        ImGui.Spacing();
        ImGui.TextDisabled("Add override:");
        ImGui.SameLine();

        DrawOverrideRow(_newOverride, "newov", 120f);
        ImGui.SameLine();

        bool canAdd = !string.IsNullOrWhiteSpace(_newOverride.PlayerName) && _newOverride.IconBaseId > 0;
        ImGui.BeginDisabled(!canAdd);
        if (ImGui.Button("Add##addov"))
        {
            _newOverride.PlayerName = _newOverride.PlayerName.Trim();
            cfg.PlayerIconOverrides.Add(_newOverride);
            // Carry over border/fill/clip/multiplier (handy when adding several with the same look); reset name/icon.
            _newOverride = new PlayerIconOverride
            {
                ShowBorder     = _newOverride.ShowBorder,
                BorderColor    = _newOverride.BorderColor,
                ShowFill       = _newOverride.ShowFill,
                FillColor      = _newOverride.FillColor,
                ClipToCircle   = _newOverride.ClipToCircle,
                SizeMultiplier = _newOverride.SizeMultiplier,
            };
            changed = true;
        }
        ImGui.EndDisabled();
        if (!canAdd && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Enter a player name and a non-zero icon ID to enable");

        ImGui.EndDisabled(); // !cfg.ShowPlayers
        ImGui.Unindent();

        ImGui.EndTabItem();
        return changed;
    }

    // ── Combat tab ───────────────────────────────────────────────────────────

    private static bool DrawCombatTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem("Combat")) return false;
        bool    b = cfg.ShowEnemies;
        Vector4 c = cfg.EnemyColor;
        bool changed = DrawEnableAndColor("enemies", "Enemies", ref b, ref c);
        cfg.ShowEnemies = b; cfg.EnemyColor = c;

        ImGui.Indent();
        ImGui.BeginDisabled(!cfg.ShowEnemies);
        bool eng = cfg.EnemiesOnlyIfEngaged;
        if (ImGui.Checkbox("Only show enemies I'm engaged with##eng", ref eng))
        { cfg.EnemiesOnlyIfEngaged = eng; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Only shows hostile enemies that are targeting you, or that you're\n" +
                "currently targeting — instead of every hostile mob in range.\n" +
                "Great for decluttering big pulls, hunt trains, and FATEs.");

        float enMin = cfg.EnemyMinSize, enMax = cfg.EnemyMaxSize;
        if (DrawSizeSliders(ref enMin, ref enMax, 50, 60, "en"))
        { cfg.EnemyMinSize = enMin; cfg.EnemyMaxSize = enMax; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Controls the size of every enemy marker.");

        ImGui.EndDisabled();
        ImGui.Unindent();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        bool    lbB = cfg.ShowLimitBreakGlow;
        Vector4 lbC = cfg.LimitBreakGlowColor;
        if (DrawEnableAndColor("lbglow", "Limit break glow (bar 1 color)", ref lbB, ref lbC))
        { cfg.ShowLimitBreakGlow = lbB; cfg.LimitBreakGlowColor = lbC; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Skyrim-style: a glowing border creeps in from each end as limit\n" +
                "break charges — one independent layer per bar, stacked on top\n" +
                "of each other as each fills. Bar 1 alone already reaches the\n" +
                "*whole* border once it's full (not just a fraction of it), so\n" +
                "the number of full layers lit up tells you how many bars are\n" +
                "charged at a glance.");

        ImGui.Indent();
        ImGui.BeginDisabled(!cfg.ShowLimitBreakGlow);

        Vector4 lb2 = cfg.LimitBreakGlowColor2;
        if (ImGui.ColorEdit4("Bar 2 color##lbc2", ref lb2))
        { cfg.LimitBreakGlowColor2 = lb2; changed = true; }
        Vector4 lb3 = cfg.LimitBreakGlowColor3;
        if (ImGui.ColorEdit4("Bar 3 color##lbc3", ref lb3))
        { cfg.LimitBreakGlowColor3 = lb3; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Each bar's layer also waves at its own speed and phase — bar 2\n" +
                "and bar 3 are both deliberately detuned from bar 1 and from each\n" +
                "other, so the three never ripple in lockstep. That's intentional:\n" +
                "it's meant to look a little overflowing and chaotic at a full break.");

        ImGui.EndDisabled();
        ImGui.Unindent();

        ImGui.EndTabItem();
        return changed;
    }

    // ── NPCs tab ─────────────────────────────────────────────────────────────

    private static bool DrawNpcsTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem("NPCs")) return false;
        bool    b = cfg.ShowNpcs;
        Vector4 c = cfg.NpcColor;
        bool changed = DrawEnableAndColor("npcs", "NPCs", ref b, ref c);
        cfg.ShowNpcs = b; cfg.NpcColor = c;

        ImGui.Indent();
        ImGui.BeginDisabled(!cfg.ShowNpcs);
        bool tgt = cfg.NpcsOnlyIfTargetable;
        if (ImGui.Checkbox("Hide non-targetable \"ghost\" NPCs##tgt", ref tgt))
        { cfg.NpcsOnlyIfTargetable = tgt; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Filters out inert placeholder NPCs that the game keeps in its object\n" +
                "table even when nothing is actually standing there — e.g. an empty\n" +
                "chocobo stable slot in housing. Recommended to leave this on.");

        bool qIcon = cfg.ShowNpcQuestIcons;
        if (ImGui.Checkbox("Show real quest marker icons##qicon", ref qIcon))
        { cfg.ShowNpcQuestIcons = qIcon; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "NPCs with an active quest marker (MSQ, side quest \"!\", blue quest,\n" +
                "in-progress \"?\", etc.) show that exact icon — the same one the game\n" +
                "already displays above their head — instead of a plain dot.");

        bool mIcon = cfg.ShowMenderIcons;
        if (ImGui.Checkbox("Show real Mender icon##micon", ref mIcon))
        { cfg.ShowMenderIcons = mIcon; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Shows the real game icon for Mender NPCs (gear repair vendors),\n" +
                "identified by their \"Mender\" job title.\n" +
                "Shares the size sliders below with every other NPC marker.");

        bool sIcon = cfg.ShowShopIcons;
        if (ImGui.Checkbox("Show real Shop/Trader icon##sicon", ref sIcon))
        { cfg.ShowShopIcons = sIcon; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Shows the real game icon for Shop/Trader NPCs, identified by a\n" +
                "\"Merchant\", \"Vendor\", or \"Trader\" job title.\n" +
                "Shares the size sliders below with every other NPC marker.");

        float qMin = cfg.NpcQuestIconMinSize, qMax = cfg.NpcQuestIconMaxSize;
        if (DrawSizeSliders(ref qMin, ref qMax, 50, 60, "q"))
        { cfg.NpcQuestIconMinSize = qMin; cfg.NpcQuestIconMaxSize = qMax; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Controls the size of EVERY NPC marker — the quest/Mender/Shop icons\n" +
                "above AND the plain dot shown when none of those apply.");

        ImGui.EndDisabled();
        ImGui.Unindent();

        ImGui.EndTabItem();
        return changed;
    }

    // ── Gathering tab ────────────────────────────────────────────────────────

    private static bool DrawGatheringTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem("Gathering")) return false;
        bool    b = cfg.ShowGatheringNodes;
        Vector4 c = cfg.GatheringColor;
        bool changed = DrawEnableAndColor("gath", "Gathering Nodes", ref b, ref c);
        cfg.ShowGatheringNodes = b; cfg.GatheringColor = c;

        ImGui.Indent();
        ImGui.BeginDisabled(!cfg.ShowGatheringNodes);
        bool gTgt = cfg.GatheringOnlyIfTargetable;
        if (ImGui.Checkbox("Hide non-targetable \"ghost\" nodes##gtgt", ref gTgt))
        { cfg.GatheringOnlyIfTargetable = gTgt; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Filters out depleted or not-yet-spawned gathering nodes that the game\n" +
                "keeps in its object table even when nothing is currently interactable\n" +
                "there. Recommended to leave this on.");

        bool gIcon = cfg.ShowGatheringIcons;
        if (ImGui.Checkbox("Show real Mining/Botany icons##gicon", ref gIcon))
        { cfg.ShowGatheringIcons = gIcon; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Shows the node's actual Mining/Quarrying/Logging/Botany game icon instead of a plain dot.");

        ImGui.BeginDisabled(!gIcon);
        ImGui.Indent();
        float gMin = cfg.GatheringIconMinSize, gMax = cfg.GatheringIconMaxSize;
        if (DrawSizeSliders(ref gMin, ref gMax, 50, 60, "g"))
        { cfg.GatheringIconMinSize = gMin; cfg.GatheringIconMaxSize = gMax; changed = true; }
        ImGui.Unindent();
        ImGui.EndDisabled();

        ImGui.EndDisabled();
        ImGui.Unindent();

        ImGui.EndTabItem();
        return changed;
    }

    // ── Treasure tab ─────────────────────────────────────────────────────────

    private static bool DrawTreasureTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem("Treasure")) return false;
        bool    b = cfg.ShowTreasure;
        Vector4 c = cfg.TreasureColor;
        bool changed = DrawEnableAndColor("tres", "Treasure", ref b, ref c);
        cfg.ShowTreasure = b; cfg.TreasureColor = c;

        ImGui.Indent();
        ImGui.BeginDisabled(!cfg.ShowTreasure);

        float trMin = cfg.TreasureMinSize, trMax = cfg.TreasureMaxSize;
        if (DrawSizeSliders(ref trMin, ref trMax, 50, 60, "tr"))
        { cfg.TreasureMinSize = trMin; cfg.TreasureMaxSize = trMax; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Controls the size of EVERY treasure marker — the chest icon below\n" +
                "AND the plain dot fallback.");

        ImGui.Spacing();

        bool trIcon = cfg.ShowTreasureIcons;
        if (ImGui.Checkbox("Show real chest icon##tricon", ref trIcon))
        { cfg.ShowTreasureIcons = trIcon; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Shows a real treasure-chest icon instead of a plain dot. There's no\n" +
                "game-data sheet that exposes a chest's visual type from its BaseId,\n" +
                "so every coffer currently shows the same icon (below).");

        ImGui.Indent();
        ImGui.BeginDisabled(!cfg.ShowTreasureIcons);

        int trIconId = cfg.TreasureIconId;
        ImGui.SetNextItemWidth(90f);
        if (ImGui.InputInt("Icon ID##triconid", ref trIconId, 0, 0))
        { cfg.TreasureIconId = Math.Max(0, trIconId); changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Game icon ID shown for every treasure coffer — 60354 / 60355 / 60356\n" +
                "are three known treasure-chest icon variants.");

        ImGui.EndDisabled();
        ImGui.Unindent();

        ImGui.EndDisabled();
        ImGui.Unindent();

        ImGui.EndTabItem();
        return changed;
    }

    // ── Aetherytes tab ───────────────────────────────────────────────────────

    private static bool DrawAetherytesTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem("Aetherytes")) return false;
        bool    b = cfg.ShowAetherytes;
        Vector4 c = cfg.AetheryteColor;
        bool changed = DrawEnableAndColor("aeth", "Aetherytes", ref b, ref c);
        cfg.ShowAetherytes = b; cfg.AetheryteColor = c;

        ImGui.Indent();
        ImGui.BeginDisabled(!cfg.ShowAetherytes);

        bool showShards = cfg.ShowAethernetShards;
        if (ImGui.Checkbox("Show Aethernet shards##aethshards", ref showShards))
        { cfg.ShowAethernetShards = showShards; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Aethernet shards are the smaller waypoints found in housing wards,\n" +
                "the Firmament, and similar areas, as opposed to a city's one main\n" +
                "aetheryte. Off shows only main aetherytes.");

        bool aIcon = cfg.ShowAetheryteIcons;
        if (ImGui.Checkbox("Show real aetheryte icon##aicon", ref aIcon))
        { cfg.ShowAetheryteIcons = aIcon; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Icon IDs confirmed against a reference plugin's icon table.\n" +
                "Falls back to the colour dot only if an icon doesn't resolve.");

        float aMin = cfg.AetheryteIconMinSize, aMax = cfg.AetheryteIconMaxSize;
        if (DrawSizeSliders(ref aMin, ref aMax, 50, 60, "a"))
        { cfg.AetheryteIconMinSize = aMin; cfg.AetheryteIconMaxSize = aMax; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Controls the size of EVERY aetheryte marker — the real icon above\n" +
                "AND the plain dot shown when icons are off or a texture fails to load.");

        string shardName = cfg.AethernetShardName;
        if (ImGui.InputText("Aethernet shard name##shardname", ref shardName, 64))
        { cfg.AethernetShardName = shardName; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "A word that appears in every Aethernet shard's name in your game\n" +
                "language. Matched as a substring, so \"Aethernet\" catches \"Ul'dah\n" +
                "Aethernet Shard\", \"Limsa Lominsa Aethernet Shard\", etc. all at once.\n" +
                "Any real aetheryte that DOESN'T match this is assumed to be a main\n" +
                "aetheryte by default — no separate name needed for that.");

        ImGui.EndDisabled();
        ImGui.Unindent();

        ImGui.EndTabItem();
        return changed;
    }

    // ── FATEs tab ────────────────────────────────────────────────────────────

    private static bool DrawFatesTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem("FATEs")) return false;
        bool    fateB = cfg.ShowFates;
        Vector4 fateC = cfg.FateColor;

        ImGui.TextDisabled("Independent of every other tab's toggles.");
        ImGui.Spacing();

        bool changed = DrawEnableAndColor("fates", "Show FATEs", ref fateB, ref fateC);
        cfg.ShowFates = fateB; cfg.FateColor = fateC;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Shows active or about-to-start FATEs using their real game icon.\n" +
                "FATEs sort in the same pass as every other marker so closer items\n" +
                "always paint on top. Detection range = Detection tab range\n" +
                "× the multiplier below. Works even with all other markers off.");

        ImGui.Indent();
        ImGui.BeginDisabled(!fateB);

        float fateMul = cfg.FateDistanceMultiplier;
        if (ImGui.SliderFloat("Distance multiplier##fatemul", ref fateMul, 0.5f, 5.0f, "%.1f×"))
        { cfg.FateDistanceMultiplier = Math.Max(0.5f, fateMul); changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "FATEs are detected up to (Detection tab range × this value) yalms.\n" +
                "At 2.5× with the default 100 y detection range, FATEs appear\n" +
                "up to 250 y away — zone-wide, discoverable long before you're near them.");
        ImGui.TextDisabled($"Effective FATE range: {cfg.MaxMarkerDistance * cfg.FateDistanceMultiplier:F0} yalms");

        float fateMin = cfg.FateIconMinSize, fateMax = cfg.FateIconMaxSize;
        if (DrawSizeSliders(ref fateMin, ref fateMax, 50, 64, "fate",
                             "Min icon size (far away)", "Max icon size (close up)"))
        { cfg.FateIconMinSize = fateMin; cfg.FateIconMaxSize = fateMax; changed = true; }

        ImGui.EndDisabled();
        ImGui.Unindent();

        ImGui.EndTabItem();
        return changed;
    }

    // Compact colour-edit flags: show only the small swatch, not text inputs.
    private static readonly ImGuiColorEditFlags ColorPickerFlags =
        ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar;

    // ── Shared tab building blocks ────────────────────────────────────────────

    private static bool DrawEnableAndColor(string idPrefix, string label, ref bool enabled, ref Vector4 color)
    {
        bool changed = false;
        if (ImGui.Checkbox($"##{idPrefix}_en", ref enabled)) changed = true;
        ImGui.SameLine();
        if (ImGui.ColorEdit4($"{label}##{idPrefix}_c", ref color, ColorPickerFlags)) changed = true;
        return changed;
    }

    private static bool DrawSizeSliders(
        ref float min, ref float max, int minHi, int maxHi, string idPrefix,
        string minLabel = "Min size (far away)", string maxLabel = "Max size (close up)", int lo = 8)
    {
        bool changed = false;
        int mn = (int)min;
        if (ImGui.SliderInt($"{minLabel}##{idPrefix}min", ref mn, lo, minHi)) { min = mn; changed = true; }
        int mx = (int)max;
        if (ImGui.SliderInt($"{maxLabel}##{idPrefix}max", ref mx, lo, maxHi)) { max = mx; changed = true; }
        return changed;
    }
}
