using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace SkyrimCompass;

public sealed class ConfigWindow : Window
{
    private readonly Plugin plugin;

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

        // Top-level enable toggle
        bool enabled = cfg.Enabled;
        if (ImGui.Checkbox("##enabled", ref enabled)) { cfg.Enabled = enabled; changed = true; }
        ImGui.SameLine();
        ImGui.Text("Enable Compass");
        ImGui.Separator();

        if (ImGui.BeginTabBar("##tabs"))
        {
            changed |= DrawLayoutTab(cfg);
            changed |= DrawColorsTab(cfg);
            changed |= DrawMarkersTab(cfg);
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

        v = cfg.CompassWidth;
        if (ImGui.SliderFloat("Width##w", ref v, 200f, 1400f))
        { cfg.CompassWidth = v; changed = true; }

        v = cfg.CompassHeight;
        if (ImGui.SliderFloat("Height##h", ref v, 20f, 80f))
        { cfg.CompassHeight = v; changed = true; }

        v = cfg.YOffset;
        if (ImGui.SliderFloat("Y Offset (from top)##yo", ref v, 0f, 300f))
        { cfg.YOffset = v; changed = true; }

        ImGui.Spacing();

        v = cfg.VisibleDegrees;
        if (ImGui.SliderFloat("Visible Degrees##vd", ref v, 30f, 180f))
        { cfg.VisibleDegrees = v; changed = true; }
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

        ImGui.Spacing();
        ImGui.TextDisabled("Rotation Offset  (set to 180 if N and S are swapped)");
        v = cfg.RotationOffset;
        if (ImGui.SliderFloat("##rotoff", ref v, -180f, 180f))
        { cfg.RotationOffset = v; changed = true; }

        ImGui.EndTabItem();
        return changed;
    }

    // ── Colors tab ───────────────────────────────────────────────────────────

    private static bool DrawColorsTab(Configuration cfg)
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

        ImGui.EndTabItem();
        return changed;
    }

    // ── Markers tab ──────────────────────────────────────────────────────────

    private static bool DrawMarkersTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem("Markers")) return false;
        bool changed = false;

        ImGui.TextDisabled("Toggle a marker type and customise its colour:");
        ImGui.Spacing();

        bool    b;
        Vector4 c;

        // Each row: [checkbox] [colour swatch + label]
        b = cfg.ShowPlayers;   c = cfg.PlayerColor;
        if (ImGui.Checkbox("##players_en", ref b))        { cfg.ShowPlayers = b;        changed = true; }
        ImGui.SameLine();
        if (ImGui.ColorEdit4("Players##players_c", ref c, ColorPickerFlags)) { cfg.PlayerColor = c;    changed = true; }

        b = cfg.ShowEnemies;   c = cfg.EnemyColor;
        if (ImGui.Checkbox("##enemies_en", ref b))        { cfg.ShowEnemies = b;        changed = true; }
        ImGui.SameLine();
        if (ImGui.ColorEdit4("Enemies##enemies_c", ref c, ColorPickerFlags)) { cfg.EnemyColor = c;    changed = true; }

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
        ImGui.EndDisabled();
        ImGui.Unindent();
        ImGui.Spacing();

        b = cfg.ShowNpcs;      c = cfg.NpcColor;
        if (ImGui.Checkbox("##npcs_en", ref b))           { cfg.ShowNpcs = b;           changed = true; }
        ImGui.SameLine();
        if (ImGui.ColorEdit4("NPCs##npcs_c", ref c, ColorPickerFlags))       { cfg.NpcColor = c;      changed = true; }

        b = cfg.ShowGatheringNodes; c = cfg.GatheringColor;
        if (ImGui.Checkbox("##gath_en", ref b))           { cfg.ShowGatheringNodes = b; changed = true; }
        ImGui.SameLine();
        if (ImGui.ColorEdit4("Gathering Nodes##gath_c", ref c, ColorPickerFlags)) { cfg.GatheringColor = c; changed = true; }

        b = cfg.ShowTreasure;  c = cfg.TreasureColor;
        if (ImGui.Checkbox("##tres_en", ref b))           { cfg.ShowTreasure = b;       changed = true; }
        ImGui.SameLine();
        if (ImGui.ColorEdit4("Treasure##tres_c", ref c, ColorPickerFlags))   { cfg.TreasureColor = c; changed = true; }

        ImGui.Spacing();
        ImGui.TextDisabled("Maximum detection distance (straight-line, includes height):");
        float md = cfg.MaxMarkerDistance;
        if (ImGui.SliderFloat("yalms##maxd", ref md, 10f, 200f))
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

    // Compact colour-edit flags: show only the small swatch, not text inputs
    private static readonly ImGuiColorEditFlags ColorPickerFlags =
        ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar;
}
