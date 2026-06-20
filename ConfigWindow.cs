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

        ImGui.BeginDisabled(!qIcon);
        ImGui.Indent();
        int qMin = (int)cfg.NpcQuestIconMinSize;
        if (ImGui.SliderInt("Min size (far away)##qmin", ref qMin, 8, 50))
        { cfg.NpcQuestIconMinSize = qMin; changed = true; }
        int qMax = (int)cfg.NpcQuestIconMaxSize;
        if (ImGui.SliderInt("Max size (close up)##qmax", ref qMax, 8, 60))
        { cfg.NpcQuestIconMaxSize = qMax; changed = true; }
        ImGui.Unindent();
        ImGui.EndDisabled();

        ImGui.EndDisabled();
        ImGui.Unindent();
        ImGui.Spacing();

        b = cfg.ShowGatheringNodes; c = cfg.GatheringColor;
        if (ImGui.Checkbox("##gath_en", ref b))           { cfg.ShowGatheringNodes = b; changed = true; }
        ImGui.SameLine();
        if (ImGui.ColorEdit4("Gathering Nodes##gath_c", ref c, ColorPickerFlags)) { cfg.GatheringColor = c; changed = true; }

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
            ImGui.SetTooltip(
                "Shows the node's actual Mining/Quarrying/Logging/Botany game icon\n" +
                "instead of a plain dot.");

        ImGui.BeginDisabled(!gIcon);
        ImGui.Indent();
        int gMin = (int)cfg.GatheringIconMinSize;
        if (ImGui.SliderInt("Min size (far away)##gmin", ref gMin, 8, 50))
        { cfg.GatheringIconMinSize = gMin; changed = true; }
        int gMax = (int)cfg.GatheringIconMaxSize;
        if (ImGui.SliderInt("Max size (close up)##gmax", ref gMax, 8, 60))
        { cfg.GatheringIconMaxSize = gMax; changed = true; }
        ImGui.Unindent();
        ImGui.EndDisabled();

        ImGui.EndDisabled();
        ImGui.Unindent();
        ImGui.Spacing();

        b = cfg.ShowTreasure;  c = cfg.TreasureColor;
        if (ImGui.Checkbox("##tres_en", ref b))           { cfg.ShowTreasure = b;       changed = true; }
        ImGui.SameLine();
        if (ImGui.ColorEdit4("Treasure##tres_c", ref c, ColorPickerFlags))   { cfg.TreasureColor = c; changed = true; }

        b = cfg.ShowAetherytes; c = cfg.AetheryteColor;
        if (ImGui.Checkbox("##aeth_en", ref b))           { cfg.ShowAetherytes = b;     changed = true; }
        ImGui.SameLine();
        if (ImGui.ColorEdit4("Aetherytes##aeth_c", ref c, ColorPickerFlags)) { cfg.AetheryteColor = c; changed = true; }

        ImGui.Indent();
        ImGui.BeginDisabled(!cfg.ShowAetherytes);

        bool showShards = cfg.ShowAethernetShards;
        if (ImGui.Checkbox("Show Aethernet shards##aethshards", ref showShards))
        { cfg.ShowAethernetShards = showShards; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Aethernet shards are the smaller waypoints found in housing wards,\n" +
                "the Firmament, and similar areas, as opposed to a city's one main\n" +
                "aetheryte. Off shows only main aetherytes. Whichever ones are shown\n" +
                "always use their correct icon (see below) — this only affects\n" +
                "whether shards appear at all, not which icon they'd use.");

        bool aIcon = cfg.ShowAetheryteIcons;
        if (ImGui.Checkbox("Show real aetheryte icon##aicon", ref aIcon))
        { cfg.ShowAetheryteIcons = aIcon; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Icon IDs are confirmed against a reference plugin's icon table.\n" +
                "Falls back to the colour dot above only if an icon somehow doesn't\n" +
                "resolve to a loadable texture.");

        ImGui.BeginDisabled(!aIcon);
        ImGui.Indent();
        int aMin = (int)cfg.AetheryteIconMinSize;
        if (ImGui.SliderInt("Min size (far away)##amin", ref aMin, 8, 50))
        { cfg.AetheryteIconMinSize = aMin; changed = true; }
        int aMax = (int)cfg.AetheryteIconMaxSize;
        if (ImGui.SliderInt("Max size (close up)##amax", ref aMax, 8, 60))
        { cfg.AetheryteIconMaxSize = aMax; changed = true; }
        ImGui.Unindent();
        ImGui.EndDisabled();

        string shardName = cfg.AethernetShardName;
        if (ImGui.InputText("Aethernet shard name##shardname", ref shardName, 64))
        { cfg.AethernetShardName = shardName; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "A word that appears in every Aethernet shard's name in your game\n" +
                "language. Matched as a substring, so \"Aethernet\" catches \"Ul'dah\n" +
                "Aethernet Shard\", \"Limsa Lominsa Aethernet Shard\", etc. all at once.\n" +
                "Any real aetheryte that DOESN'T match this is assumed to be a city's\n" +
                "main aetheryte by default — no separate name needed for that.");

        ImGui.EndDisabled();
        ImGui.Unindent();

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

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextDisabled("FATEs (independent of all toggles above)");

        bool fateB = cfg.ShowFates; Vector4 fateC = cfg.FateColor;
        if (ImGui.Checkbox("##fates_en", ref fateB))      { cfg.ShowFates = fateB;  changed = true; }
        ImGui.SameLine();
        if (ImGui.ColorEdit4("Show FATEs##fates_c", ref fateC, ColorPickerFlags)) { cfg.FateColor = fateC; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Shows active or about-to-start FATEs using their real game icon.\n" +
                "The colour here is only a fallback dot, used if the icon texture\n" +
                "hasn't loaded yet. Works even with every marker category above off —\n" +
                "FATEs are a separate kind of point of interest, not \"an enemy\".");

        ImGui.Indent();
        ImGui.BeginDisabled(!fateB);

        int fateDist = (int)cfg.MaxFateDistance;
        if (ImGui.SliderInt("Detection range (yalms)##fatedist", ref fateDist, 30, 400))
        { cfg.MaxFateDistance = fateDist; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Much larger by default than the other markers' range — FATEs are\n" +
                "meant to be discoverable from well outside normal combat awareness.");

        int fateMin = (int)cfg.FateIconMinSize;
        if (ImGui.SliderInt("Min icon size (far away)##fatemin", ref fateMin, 8, 50))
        { cfg.FateIconMinSize = fateMin; changed = true; }

        int fateMax = (int)cfg.FateIconMaxSize;
        if (ImGui.SliderInt("Max icon size (close up)##fatemax", ref fateMax, 8, 64))
        { cfg.FateIconMaxSize = fateMax; changed = true; }

        ImGui.EndDisabled();
        ImGui.Unindent();

        ImGui.EndTabItem();
        return changed;
    }

    // Compact colour-edit flags: show only the small swatch, not text inputs
    private static readonly ImGuiColorEditFlags ColorPickerFlags =
        ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar;
}
