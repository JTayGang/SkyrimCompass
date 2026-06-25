using System;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace SkyrimCompass;

public sealed class ConfigWindow : Window
{
    private readonly Plugin plugin;

    // ── Transient state for the "add new player override" form ───────────────
    // These live on the window instance (not in Configuration) because they're
    // only needed while the user is typing in the add-new row and never need to
    // be persisted to disk — they reset to empty each time the entry is committed.
    private string  _newOverrideName        = "";
    private int     _newOverrideIconId      = 0;
    private bool    _newOverrideBorder      = false;
    private Vector4 _newOverrideBorderColor = new(1.00f, 1.00f, 1.00f, 0.90f);
    private bool    _newOverrideFill        = false;
    private Vector4 _newOverrideFillColor   = new(1.00f, 1.00f, 1.00f, 0.40f);
    private bool    _newOverrideClipToCircle  = false;
    private float   _newOverrideSizeMultiplier = 1.0f;

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
            changed |= DrawDetectionTab(cfg);
            changed |= DrawPlayersTab(cfg);   // instance method — needs _newOverride* fields
            changed |= DrawEnemiesTab(cfg);
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

    // ── Detection tab ────────────────────────────────────────────────────────
    // Houses the settings that are genuinely shared across multiple marker
    // categories rather than belonging to any one of them: MaxMarkerDistance
    // covers everything in the main object-table loop (Players/Enemies/NPCs/
    // Gathering/Treasure/Aetherytes — FATEs have their own separate range on
    // their own tab), and the fade curve is shared by literally every category
    // including FATEs (one ComputeFadeAlpha method, used everywhere).

    private static bool DrawDetectionTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem("Detection")) return false;
        bool changed = false;

        ImGui.TextDisabled(
            "Settings shared across every marker category below\n" +
            "(FATEs have their own separate detection range on their own tab).");
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
    // This is the one tab that's NOT static — it needs access to the _newOverride*
    // instance fields that hold the transient "add new entry" form state.

    private bool DrawPlayersTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem("Players")) return false;
        bool    changed = false;
        bool    b = cfg.ShowPlayers;
        Vector4 c = cfg.PlayerColor;

        if (ImGui.Checkbox("##players_en", ref b))        { cfg.ShowPlayers = b;        changed = true; }
        ImGui.SameLine();
        if (ImGui.ColorEdit4("Players##players_c", ref c, ColorPickerFlags)) { cfg.PlayerColor = c;    changed = true; }

        ImGui.Indent();
        ImGui.BeginDisabled(!cfg.ShowPlayers);

        int prMin = (int)cfg.PartyRoleIconMinSize;
        if (ImGui.SliderInt("Min size (far away)##prmin", ref prMin, 8, 50))
        { cfg.PartyRoleIconMinSize = prMin; changed = true; }
        int prMax = (int)cfg.PartyRoleIconMaxSize;
        if (ImGui.SliderInt("Max size (close up)##prmax", ref prMax, 8, 60))
        { cfg.PartyRoleIconMaxSize = prMax; changed = true; }
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
        {
            ImGui.TextDisabled("  (no overrides — add one below)");
        }

        // ── Existing entries (one editable row each) ──────────────────────────
        int removeAt = -1;
        for (int i = 0; i < cfg.PlayerIconOverrides.Count; i++)
        {
            var ov = cfg.PlayerIconOverrides[i];
            ImGui.PushID(i);

            // [X] remove button
            if (ImGui.Button("X##rmov"))
                removeAt = i;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Remove this override");
            ImGui.SameLine();

            // Editable player name
            string ovName = ov.PlayerName;
            ImGui.SetNextItemWidth(110f);
            if (ImGui.InputText("##ovname", ref ovName, 64))
            { ov.PlayerName = ovName; changed = true; }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Player display name (exact, case-insensitive)");
            ImGui.SameLine();

            // Icon base ID — no step arrows, 5-digit IDs are easier to type directly
            int ovId = ov.IconBaseId;
            ImGui.SetNextItemWidth(68f);
            if (ImGui.InputInt("##ovid", ref ovId, 0, 0))
            { ov.IconBaseId = Math.Max(0, ovId); changed = true; }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Game icon base ID\n(e.g. 62007 Paladin, 60453 Aetheryte, 61802 FC emblem)\nBrowse all icons with: /xldata icons");
            ImGui.SameLine();

            // Border checkbox + color swatch
            bool ovBorder = ov.ShowBorder;
            if (ImGui.Checkbox("B##ovb", ref ovBorder))
            { ov.ShowBorder = ovBorder; changed = true; }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Draw a solid outer ring around the icon");
            ImGui.SameLine();
            ImGui.BeginDisabled(!ov.ShowBorder);
            Vector4 ovBc = ov.BorderColor;
            if (ImGui.ColorEdit4("##ovbc", ref ovBc, ColorPickerFlags))
            { ov.BorderColor = ovBc; changed = true; }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Border ring color");
            ImGui.EndDisabled();
            ImGui.SameLine();

            // Fill checkbox + color swatch
            bool ovFill = ov.ShowFill;
            if (ImGui.Checkbox("F##ovf", ref ovFill))
            { ov.ShowFill = ovFill; changed = true; }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Draw an inward-fading fill behind the icon\n(same bloom effect as party role icon backgrounds)");
            ImGui.SameLine();
            ImGui.BeginDisabled(!ov.ShowFill);
            Vector4 ovFc = ov.FillColor;
            if (ImGui.ColorEdit4("##ovfc", ref ovFc, ColorPickerFlags))
            { ov.FillColor = ovFc; changed = true; }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Fill color");
            ImGui.EndDisabled();
            ImGui.SameLine();

            // Circle clip — rounds the icon to match the border ring shape
            bool ovClip = ov.ClipToCircle;
            if (ImGui.Checkbox("○##ovcirc", ref ovClip))
            { ov.ClipToCircle = ovClip; changed = true; }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Clip icon to a circle\n" +
                    "Rounds square icon textures to fit neatly inside the border ring.\n" +
                    "Uses ImGui's built-in rounded image rendering — no extra cost.");
            ImGui.SameLine();

            // Per-icon size multiplier — stacks on top of the global 1.5× compensation
            float ovMul = ov.SizeMultiplier;
            ImGui.SetNextItemWidth(58f);
            if (ImGui.DragFloat("##ovmul", ref ovMul, 0.05f, 0.5f, 3.0f, "%.2fx"))
            { ov.SizeMultiplier = Math.Clamp(ovMul, 0.5f, 3.0f); changed = true; }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Per-icon size multiplier (stacks on top of the global 1.5× padding compensation).\n" +
                    "1.0 = same apparent size as a party role icon.\n" +
                    "Drag right for icons with heavy transparent padding,\n" +
                    "drag left for icons with minimal padding that look oversized.");

            ImGui.PopID();
        }

        if (removeAt >= 0)
        { cfg.PlayerIconOverrides.RemoveAt(removeAt); changed = true; }

        // ── Add new override ──────────────────────────────────────────────────
        ImGui.Spacing();
        ImGui.TextDisabled("Add override:");
        ImGui.SameLine();

        ImGui.SetNextItemWidth(120f);
        ImGui.InputText("##newovname", ref _newOverrideName, 64);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Player display name to match");
        ImGui.SameLine();

        ImGui.SetNextItemWidth(68f);
        ImGui.InputInt("##newovid", ref _newOverrideIconId, 0, 0);
        _newOverrideIconId = Math.Max(0, _newOverrideIconId);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Game icon base ID\n(e.g. 62007 Paladin, 60453 Aetheryte, 61802 FC emblem)\nBrowse all icons with: /xldata icons");
        ImGui.SameLine();

        if (ImGui.Checkbox("B##newovb", ref _newOverrideBorder)) { }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Border ring");
        ImGui.SameLine();
        ImGui.BeginDisabled(!_newOverrideBorder);
        ImGui.ColorEdit4("##newovbc", ref _newOverrideBorderColor, ColorPickerFlags);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Border color");
        ImGui.EndDisabled();
        ImGui.SameLine();

        if (ImGui.Checkbox("F##newovf", ref _newOverrideFill)) { }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Inward fill");
        ImGui.SameLine();
        ImGui.BeginDisabled(!_newOverrideFill);
        ImGui.ColorEdit4("##newovfc", ref _newOverrideFillColor, ColorPickerFlags);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Fill color");
        ImGui.EndDisabled();
        ImGui.SameLine();

        if (ImGui.Checkbox("○##newovcirc", ref _newOverrideClipToCircle)) { }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Clip icon to circle");
        ImGui.SameLine();

        ImGui.SetNextItemWidth(58f);
        ImGui.DragFloat("##newovmul", ref _newOverrideSizeMultiplier, 0.05f, 0.5f, 3.0f, "%.2fx");
        _newOverrideSizeMultiplier = Math.Clamp(_newOverrideSizeMultiplier, 0.5f, 3.0f);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Per-icon size multiplier");
        ImGui.SameLine();

        bool canAdd = !string.IsNullOrWhiteSpace(_newOverrideName) && _newOverrideIconId > 0;
        ImGui.BeginDisabled(!canAdd);
        if (ImGui.Button("Add##addov"))
        {
            cfg.PlayerIconOverrides.Add(new PlayerIconOverride
            {
                PlayerName    = _newOverrideName.Trim(),
                IconBaseId    = _newOverrideIconId,
                ShowBorder    = _newOverrideBorder,
                BorderColor   = _newOverrideBorderColor,
                ShowFill      = _newOverrideFill,
                FillColor     = _newOverrideFillColor,
                ClipToCircle  = _newOverrideClipToCircle,
                SizeMultiplier = _newOverrideSizeMultiplier,
            });
            // Reset form for the next entry
            _newOverrideName        = "";
            _newOverrideIconId      = 0;
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

    // ── Enemies tab ──────────────────────────────────────────────────────────

    private static bool DrawEnemiesTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem("Enemies")) return false;
        bool    changed = false;
        bool    b = cfg.ShowEnemies;
        Vector4 c = cfg.EnemyColor;

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

        int enMin = (int)cfg.EnemyMinSize;
        if (ImGui.SliderInt("Min size (far away)##enmin", ref enMin, 8, 50))
        { cfg.EnemyMinSize = enMin; changed = true; }
        int enMax = (int)cfg.EnemyMaxSize;
        if (ImGui.SliderInt("Max size (close up)##enmax", ref enMax, 8, 60))
        { cfg.EnemyMaxSize = enMax; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Controls the size of every enemy marker.");

        ImGui.EndDisabled();
        ImGui.Unindent();

        ImGui.EndTabItem();
        return changed;
    }

    // ── NPCs tab ─────────────────────────────────────────────────────────────

    private static bool DrawNpcsTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem("NPCs")) return false;
        bool    changed = false;
        bool    b = cfg.ShowNpcs;
        Vector4 c = cfg.NpcColor;

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

        bool mIcon = cfg.ShowMenderIcons;
        if (ImGui.Checkbox("Show real Mender icon##micon", ref mIcon))
        { cfg.ShowMenderIcons = mIcon; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Shows the real game icon for Mender NPCs (gear repair vendors),\n" +
                "identified by their \"Mender\" job title — confirmed against real\n" +
                "game data. Shares the size sliders below with every other NPC marker.");

        bool sIcon = cfg.ShowShopIcons;
        if (ImGui.Checkbox("Show real Shop/Trader icon##sicon", ref sIcon))
        { cfg.ShowShopIcons = sIcon; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Shows the real game icon for Shop/Trader NPCs, identified by a\n" +
                "\"Merchant\", \"Vendor\", or \"Trader\" job title — confirmed against\n" +
                "real game data. Shares the size sliders below with every other NPC marker.\n" +
                "Note: the exact icon variant used couldn't be visually confirmed\n" +
                "without a live client — let me know if it doesn't look right.");

        int qMin = (int)cfg.NpcQuestIconMinSize;
        if (ImGui.SliderInt("Min size (far away)##qmin", ref qMin, 8, 50))
        { cfg.NpcQuestIconMinSize = qMin; changed = true; }
        int qMax = (int)cfg.NpcQuestIconMaxSize;
        if (ImGui.SliderInt("Max size (close up)##qmax", ref qMax, 8, 60))
        { cfg.NpcQuestIconMaxSize = qMax; changed = true; }
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
        bool    changed = false;
        bool    b = cfg.ShowGatheringNodes;
        Vector4 c = cfg.GatheringColor;

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

        ImGui.EndTabItem();
        return changed;
    }

    // ── Treasure tab ─────────────────────────────────────────────────────────

    private static bool DrawTreasureTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem("Treasure")) return false;
        bool    changed = false;
        bool    b = cfg.ShowTreasure;
        Vector4 c = cfg.TreasureColor;

        if (ImGui.Checkbox("##tres_en", ref b))           { cfg.ShowTreasure = b;       changed = true; }
        ImGui.SameLine();
        if (ImGui.ColorEdit4("Treasure##tres_c", ref c, ColorPickerFlags))   { cfg.TreasureColor = c; changed = true; }

        ImGui.EndTabItem();
        return changed;
    }

    // ── Aetherytes tab ───────────────────────────────────────────────────────

    private static bool DrawAetherytesTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem("Aetherytes")) return false;
        bool    changed = false;
        bool    b = cfg.ShowAetherytes;
        Vector4 c = cfg.AetheryteColor;

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
                "Falls back to the colour dot below only if an icon somehow doesn't\n" +
                "resolve to a loadable texture.");

        int aMin = (int)cfg.AetheryteIconMinSize;
        if (ImGui.SliderInt("Min size (far away)##amin", ref aMin, 8, 50))
        { cfg.AetheryteIconMinSize = aMin; changed = true; }
        int aMax = (int)cfg.AetheryteIconMaxSize;
        if (ImGui.SliderInt("Max size (close up)##amax", ref aMax, 8, 60))
        { cfg.AetheryteIconMaxSize = aMax; changed = true; }
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
                "Any real aetheryte that DOESN'T match this is assumed to be a city's\n" +
                "main aetheryte by default — no separate name needed for that.");

        ImGui.EndDisabled();
        ImGui.Unindent();

        ImGui.EndTabItem();
        return changed;
    }

    // ── FATEs tab ────────────────────────────────────────────────────────────

    private static bool DrawFatesTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem("FATEs")) return false;
        bool    changed = false;
        bool    fateB = cfg.ShowFates;
        Vector4 fateC = cfg.FateColor;

        ImGui.TextDisabled("Independent of every other tab's toggles.");
        ImGui.Spacing();

        if (ImGui.Checkbox("##fates_en", ref fateB))      { cfg.ShowFates = fateB;  changed = true; }
        ImGui.SameLine();
        if (ImGui.ColorEdit4("Show FATEs##fates_c", ref fateC, ColorPickerFlags)) { cfg.FateColor = fateC; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Shows active or about-to-start FATEs using their real game icon.\n" +
                "The colour here is only a fallback dot, used if the icon texture\n" +
                "hasn't loaded yet. Works even with every marker category on every\n" +
                "other tab turned off — FATEs are a separate kind of point of\n" +
                "interest, not \"an enemy\".");

        ImGui.Indent();
        ImGui.BeginDisabled(!fateB);

        int fateDist = (int)cfg.MaxFateDistance;
        if (ImGui.SliderInt("Detection range (yalms)##fatedist", ref fateDist, 30, 400))
        { cfg.MaxFateDistance = fateDist; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Much larger by default than the other markers' range — FATEs are\n" +
                "meant to be discoverable from well outside normal combat awareness.\n" +
                "This is FATEs' own range, separate from the Detection tab's slider.");

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
