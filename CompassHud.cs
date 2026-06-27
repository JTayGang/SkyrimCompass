using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Gui.NamePlate;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace SkyrimCompass;

/// <summary>
/// Renders a Skyrim-style compass bar via ImGui foreground draw list. Uses a
/// fisheye/lens projection so edges compress more degrees per pixel, widening FOV.
/// </summary>
public sealed class CompassHud : IDisposable
{
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly ITargetManager targetManager;
    private readonly INamePlateGui namePlateGui;
    private readonly ITextureProvider textureProvider;
    private readonly IFateTable fateTable;
    private readonly ICondition condition;
    private readonly Configuration config;
    private readonly IPluginLog log;
    private readonly IFontHandle jupiterFont;

    // GameObjectId -> current nameplate marker icon ID (quest/etc.), refreshed every
    // nameplate update. 0/absent = no active marker.
    private readonly Dictionary<ulong, int> npcMarkerIcons = new();

    // BaseId -> resolved Mining/Botany icon ID. Static game data, cached permanently.
    private readonly Dictionary<uint, int> gatheringIconCache = new();
    private readonly ExcelSheet<GatheringPoint> gatheringPointSheet;
    private readonly ExcelSheet<GatheringPointBase> gatheringPointBaseSheet;
    private readonly ExcelSheet<GatheringType> gatheringTypeSheet;

    // BaseId -> NPC title text (e.g. "Merchant & Mender"), cached permanently. Shared
    // by every title-keyword check (Mender, Shop, ...).
    private readonly Dictionary<uint, string> npcTitleCache = new();
    private readonly ExcelSheet<ENpcResident> npcResidentSheet;
    private readonly ExcelSheet<ClassJob>     classJobSheet;

    // Keyword sets, each confirmed against real ENpcResident sheet frequency.
    private static readonly string[] MenderTitleKeywords = { "Mender" };

    // Unified candidate list reused every frame (no per-frame alloc). Holds both
    // game-object markers and FATE markers so one sort pass gives correct
    // closer-on-top draw order across all entity types.
    // Obj != null → game-object entry; Fate != null → FATE entry.
    // T is the distance fraction normalised within each entry's own category range.
    private readonly List<(IGameObject? Obj, IFate? Fate, float Dist, float Delta, float T, uint Col)> allCandidates = new();

    /// <summary>
    /// Extra scale for NPC/party-role icons (quest/Mender/Shop, job icon, player
    /// overrides) on top of their size formula — their textures have transparent
    /// padding that would otherwise make the icon look smaller than a plain dot.
    /// A multiplier (not a flat px offset) keeps that proportional at long range.
    /// NOT applied to Gathering icons (not undersized) or Aetheryte (own multiplier below).
    /// </summary>
    private const float IconSizeMultiplier = 1.5f;
    /// <summary>Like <see cref="IconSizeMultiplier"/> but for aetheryte icons, which need more compensation. Dot fallback unaffected.</summary>
    private const float AetheryteIconSizeMultiplier = 1.75f;
    private static readonly string[] ShopTitleKeywords   =
    {
        "Merchant", "Vendor", "Trader",
        // All below confirmed via /compass debug + ENpcResident frequency check, zero false positives:
        "Sutler", "Supplier", "Junkmonger", "Fishmonger", "Dyemonger",
        "Jeweler", "Apothecary", "Culinarian",
        // Rejected after checking: Alchemist (mostly lore NPCs), Carpenter (ambient), Armorer (ambiguous)
    };

    private static readonly (float Deg, string Label, bool IsMajor)[] Directions =
    [
        (0f,   "N",  true),
        (45f,  "NE", false),
        (90f,  "E",  true),
        (135f, "SE", false),
        (180f, "S",  true),
        (225f, "SW", false),
        (270f, "W",  true),
        (315f, "NW", false),
    ];

    public CompassHud(
        IClientState clientState,
        IObjectTable objectTable,
        ITargetManager targetManager,
        INamePlateGui namePlateGui,
        ITextureProvider textureProvider,
        IFateTable fateTable,
        ICondition condition,
        IDataManager dataManager,
        Configuration config,
        IPluginLog log,
        IFontHandle jupiterFont)
    {
        this.clientState     = clientState;
        this.objectTable     = objectTable;
        this.targetManager   = targetManager;
        this.namePlateGui    = namePlateGui;
        this.textureProvider = textureProvider;
        this.fateTable       = fateTable;
        this.condition       = condition;
        this.config          = config;
        this.log             = log;
        this.jupiterFont     = jupiterFont;

        gatheringPointSheet     = dataManager.GetExcelSheet<GatheringPoint>();
        gatheringPointBaseSheet = dataManager.GetExcelSheet<GatheringPointBase>();
        gatheringTypeSheet      = dataManager.GetExcelSheet<GatheringType>();
        npcResidentSheet        = dataManager.GetExcelSheet<ENpcResident>();
        classJobSheet           = dataManager.GetExcelSheet<ClassJob>();

        // OnDataUpdate fires every frame with ALL current nameplates (not just deltas).
        this.namePlateGui.OnDataUpdate += OnNamePlateDataUpdate;
    }

    public void Dispose()
    {
        namePlateGui.OnDataUpdate -= OnNamePlateDataUpdate;
    }

    private void OnNamePlateDataUpdate(
        INamePlateUpdateContext context, IReadOnlyList<INamePlateUpdateHandler> handlers)
    {
        npcMarkerIcons.Clear();
        foreach (var h in handlers)
        {
            if (h.MarkerIconId > 0)
                npcMarkerIcons[h.GameObjectId] = h.MarkerIconId;
        }
    }

    // ── Public entry ─────────────────────────────────────────────────────────

    public unsafe void Draw()
    {
        if (!config.Enabled) return;
        // No reason to render a navigation aid while the camera's locked to a cutscene.
        // Checking all three flags is the pattern other Dalamud plugins (PeepingTom,
        // Pictomancy, etc.) settled on: OccupiedInCutSceneEvent covers in-engine story
        // cutscenes, WatchingCutscene covers the big skippable ones (and gpose),
        // WatchingCutscene78 covers a newer non-skippable variant.
        if (config.HideDuringCutscenes && (
            condition[ConditionFlag.OccupiedInCutSceneEvent] ||
            condition[ConditionFlag.WatchingCutscene] ||
            condition[ConditionFlag.WatchingCutscene78]))
            return;

        var player = objectTable.LocalPlayer;
        if (player == null) return;

        float headingRad;
        var   originPos = player.Position;   // default: bearings/distances from the character

        if (config.UseCameraDirection)
        {
            // CameraManager->Camera = normal in-game world camera. Its doc comment claims
            // DirH is "0=north, increasing clockwise", but testing showed E/W mirrored
            // (N/S fine) — DirH actually increases counter-clockwise. Negate to fix.
            var camManager = CameraManager.Instance();
            var camera     = camManager != null ? camManager->Camera : null;

            if (camera != null && !float.IsNaN(camera->DirH))
            {
                headingRad = -camera->DirH;

                // First-person uses a different DirH convention (confirmed in-game: exactly
                // 180° off, no other symptom). Likely cause: third-person DirH encodes the
                // camera's orbital angle (180° from its actual look direction); first-person
                // has no orbit, so DirH becomes a direct view angle instead.
                if (camera->ZoomMode == CameraZoomMode.FirstPerson)
                    headingRad += MathF.PI;

                if (config.UseCameraPosition)
                {
                    // LastPosition sits next to LastLookAtVector — the standard eye/look-at
                    // pairing, confirming this is the camera's actual world position.
                    var camPos = camera->LastPosition;
                    if (!float.IsNaN(camPos.X) && !float.IsNaN(camPos.Y) && !float.IsNaN(camPos.Z))
                        originPos = camPos;
                }
            }
            else if (!float.IsNaN(player.Rotation))
            {
                // Camera pointer unavailable (e.g. first frames after zoning) — fall back
                // to facing direction rather than freezing the bar.
                headingRad = MathF.PI - player.Rotation;
            }
            else
            {
                return;
            }
        }
        else
        {
            if (float.IsNaN(player.Rotation)) return;
            // FFXIV: rotation = 0 → south, π → north
            headingRad = MathF.PI - player.Rotation;
        }

        float heading = Normalize(headingRad * (180f / MathF.PI) + config.RotationOffset);

        var io = ImGui.GetIO();
        var dl = ImGui.GetForegroundDrawList();

        float bw = config.CompassWidth;
        float bh = config.CompassHeight;
        float bx = (io.DisplaySize.X - bw) * 0.5f;
        float by = config.YOffset;

        RenderBar(dl, bx, by, bw, bh, heading, player, originPos);
    }

    // ── Lens projection ───────────────────────────────────────────────────────

    /// <summary>
    /// Maps a compass angle offset (degrees) to a signed pixel offset from bar centre.
    /// f(u) = 1-(1-u)^k, u = |delta|/extendedHalf, k = lensStrength. Slope matches linear
    /// ppd at centre, falls off toward edges (more degrees per pixel = wider FOV).
    /// lensStrength = 1.0 → pure linear.
    /// </summary>
    private static float Project(float delta, float halfVis, float barHalfW, float lensStr)
    {
        float extHalf = halfVis * lensStr;            // extended visible half-range (deg)
        float absD    = MathF.Min(MathF.Abs(delta), extHalf);
        float u       = absD / extHalf;                        // 0 → 1
        float f       = 1f - MathF.Pow(1f - u, lensStr);        // f(0)=0, f(1)=1, f'(0)=k

        return (delta >= 0f ? 1f : -1f) * barHalfW * f;
    }

    // ── Main render ───────────────────────────────────────────────────────────

    private void RenderBar(
        ImDrawListPtr dl,
        float bx, float by, float bw, float bh,
        float heading, IPlayerCharacter player, Vector3 originPos)
    {
        float cx      = bx + bw * 0.5f;
        float cy      = by + bh * 0.5f;
        float barHalfW = bw * 0.5f;

        float halfVis = config.VisibleDegrees * 0.5f;
        float lensStr = config.LensStrength;
        float extHalf = halfVis * lensStr;   // widest angle shown on each side

        uint bgCol     = C(config.BackgroundColor);
        uint borderCol = C(config.BorderColor);
        uint tickCol   = C(config.TickColor);
        uint cardCol   = C(config.CardinalColor);
        uint ixCol     = C(config.IntercardinalColor);

        // Fully-opaque background for the masking cap fills
        uint solidBgCol = (bgCol & 0x00FFFFFFu) | 0xFF000000u;

        // Diamond end-cap dimensions
        float capHW = bh * 0.44f;
        float capHH = bh * 0.64f;

        // ── 1. Main bar background ────────────────────────────────────────────
        dl.AddRectFilled(V(bx, by), V(bx + bw, by + bh), bgCol);

        // Warm centre glow
        uint warmGlow = ImGui.ColorConvertFloat4ToU32(new Vector4(0.9f, 0.70f, 0.35f, 0.08f));
        float gw = bw * 0.22f;
        dl.AddRectFilledMultiColor(V(cx - gw, by), V(cx,      by + bh), 0u,       warmGlow, warmGlow, 0u);
        dl.AddRectFilledMultiColor(V(cx,      by), V(cx + gw, by + bh), warmGlow, 0u,       0u,       warmGlow);

        // Edge vignette
        dl.AddRectFilledMultiColor(V(bx,              by), V(bx + bw * 0.14f, by + bh), 0xAA000000u, 0u,          0u,          0xAA000000u);
        dl.AddRectFilledMultiColor(V(bx + bw * 0.86f, by), V(bx + bw,         by + bh), 0u,          0xAA000000u, 0xAA000000u, 0u);

        // Top bevel
        dl.AddLine(V(bx + 1f, by + 1f), V(bx + bw - 1f, by + 1f), 0x1AFFFFFF, 1f);

        // ── 2. Bar border ──────────────────────────────────────────────────────
        // Drawn before markers/icons so icons (which can render past the bar's clip
        // rect and are often taller than the bar) paint over the border, not the
        // other way round.
        dl.AddRect(V(bx, by), V(bx + bw, by + bh), borderCol, 0f, ImDrawFlags.None, 1.5f);

        // ── 3. Clip to bar ────────────────────────────────────────────────────
        dl.PushClipRect(V(bx + 1f, by), V(bx + bw - 1f, by + bh), true);

        // Push Jupiter — FFXIV's ornate serif heading font — for the Skyrim feel.
        // Push() returns an IDisposable that auto-pops; null when not yet built
        // (first frame or two), which means no push/pop and the default font is used.
        // Pushed before the tick loop (not just the label loop) because the tick-height
        // clamp below needs Jupiter's real metrics, not the default font's.
        using var jupiterScope = jupiterFont.Available ? jupiterFont.Push() : null;

        float fontSize = ImGui.GetFontSize() * config.FontScale;
        var   font     = ImGui.GetFont();   // Jupiter after push, default otherwise

        // Where the direction labels sit + how tall one line of them renders.
        // "N"'s height stands in for all labels — CalcTextSize's Y is the font's
        // line height, not glyph-dependent, so it's the same for "N" and "NE" alike.
        float labelTop    = by + bh * 0.12f;
        float labelHeight = ImGui.CalcTextSize("N").Y * config.FontScale;
        float labelBottom = labelTop + labelHeight;

        // Ticks must stop at least this far above the label row, or the 90° ticks
        // (tallest of the four lengths) draw straight through the cardinal letters
        // on anything shorter than the tallest compass heights.
        const float tickLabelGap = 0f;
        float maxTickHeight = MathF.Max(2f, (by + bh - 1f) - (labelBottom + tickLabelGap));

        // ── 4. Tick marks (every 5°, using lens projection) ──────────────────
        for (int d = 0; d < 360; d += 5)
        {
            float delta = Delta(heading, d);
            if (MathF.Abs(delta) > extHalf + 2f) continue;

            float sx   = cx + Project(delta, halfVis, barHalfW, lensStr);
            bool  is90 = d % 90 == 0;
            bool  is45 = d % 45 == 0;
            bool  is10 = d % 10 == 0;

            float th = is90 ? bh * 0.52f
                     : is45 ? bh * 0.36f
                     : is10 ? bh * 0.22f
                             : bh * 0.13f;
            th = MathF.Min(th, maxTickHeight);   // clipped so it never reaches the letters

            // Fade ticks that are in the lens-compressed zone
            float lensA    = LensEdgeAlpha(delta, halfVis, extHalf);
            uint  tickDraw = WithAlpha(is90 ? cardCol : tickCol, lensA);

            dl.AddLine(
                V(sx, by + bh - th - 1f),
                V(sx, by + bh - 1f),
                tickDraw,
                is90 ? 2f : 1f);
        }

        // ── 5. Direction labels (upper band, lens-projected) ──────────────────
        foreach (var (deg, label, isMajor) in Directions)
        {
            float delta = Delta(heading, deg);
            if (MathF.Abs(delta) > extHalf + 10f) continue;

            float sx  = cx + Project(delta, halfVis, barHalfW, lensStr);
            var   tsz = ImGui.CalcTextSize(label) * config.FontScale;
            float tx  = sx - tsz.X * 0.5f;
            float ty  = labelTop;

            // Labels start fading slightly earlier than ticks (compressed text is hard to read)
            float lensA     = LensEdgeAlpha(delta, halfVis * 0.88f, extHalf);
            uint  labelCol  = WithAlpha(isMajor ? cardCol : ixCol, lensA);
            uint  shadowCol = WithAlpha(0xBB000000u, lensA);

            dl.AddText(font, fontSize, V(tx + 1f, ty + 1f), shadowCol, label);
            dl.AddText(font, fontSize, V(tx, ty), labelCol, label);
        }
        // jupiterScope disposed here → Jupiter automatically popped

        // ── 6. Entity markers + FATEs (single sorted pass) ───────────────────
        // Both types share one candidate list and sort so closer items always paint
        // on top regardless of type. Game objects are gated behind ShowAnyMarkers;
        // FATEs use their own ShowFates flag and are unaffected by that toggle.
        RenderAllMarkers(dl, cx, cy, halfVis, barHalfW, lensStr, heading, player, originPos);

        dl.PopClipRect();

        // ── 7. End-cap FILLS — opaque so they mask ticks/dots at the edges ───
        dl.AddQuadFilled(V(bx,          cy - capHH), V(bx + capHW, cy),            V(bx,          cy + capHH), V(bx - capHW, cy),            solidBgCol);
        dl.AddQuadFilled(V(bx + bw,     cy - capHH), V(bx + bw + capHW, cy),       V(bx + bw,     cy + capHH), V(bx + bw - capHW, cy),       solidBgCol);

        // ── 8. End-cap OUTLINES on top ────────────────────────────────────────
        DrawEndCapOutlines(dl, bx,      cy, capHW, capHH, borderCol);
        DrawEndCapOutlines(dl, bx + bw, cy, capHW, capHH, borderCol);

        // ── 9. Centre notch ───────────────────────────────────────────────────
        const float nH = 10f, nW = 6f;
        dl.AddTriangleFilled(V(cx + 1f, by + nH + 2f), V(cx - nW + 1f, by + 1f), V(cx + nW + 1f, by + 1f), 0x55000000u);
        dl.AddTriangleFilled(V(cx,      by + nH + 1f), V(cx - nW,       by),      V(cx + nW,       by),      0xF2FFFFFFu);

        // ── 10. Numeric heading ───────────────────────────────────────────────
        if (config.ShowHeadingText)
        {
            string txt = $"{(int)heading:000}°";
            var    sz  = ImGui.CalcTextSize(txt);
            dl.AddText(V(cx - sz.X * 0.5f, by + bh + 3f), 0xBBCCBB99u, txt);
        }
    }

    // ── End-cap outline helper ────────────────────────────────────────────────

    private static void DrawEndCapOutlines(
        ImDrawListPtr dl, float cx, float cy, float hw, float hh, uint color)
    {
        dl.AddQuad(V(cx, cy - hh), V(cx + hw, cy), V(cx, cy + hh), V(cx - hw, cy), color, 1.5f);

        uint innerCol = (color & 0x00FFFFFFu) | (((color >> 24) * 6 / 10) << 24);
        float s = 0.52f;
        dl.AddQuad(V(cx, cy - hh * s), V(cx + hw * s, cy), V(cx, cy + hh * s), V(cx - hw * s, cy), innerCol, 1f);

        dl.AddCircleFilled(V(cx, cy), 2.5f, color);
    }

    // ── Unified marker + FATE render (single sorted pass) ────────────────────

    private void RenderAllMarkers(
        ImDrawListPtr dl,
        float cx, float cy,
        float halfVis, float barHalfW, float lensStr,
        float heading, IPlayerCharacter player, Vector3 originPos)
    {
        var   pp            = originPos;
        float maxDist       = config.MaxMarkerDistance;
        float maxDistSq     = maxDist * maxDist;
        float fateMaxDist   = maxDist * config.FateDistanceMultiplier;
        float fateMaxDistSq = fateMaxDist * fateMaxDist;
        float extHalf       = halfVis * lensStr;

        allCandidates.Clear();

        // ── Collect game-object markers ──────────────────────────────────────
        if (config.ShowAnyMarkers)
        {
            foreach (var obj in objectTable)
            {
                if (obj == null || obj.EntityId == player.EntityId) continue;

                uint col = MarkerColor(obj, player);
                if (col == 0) continue;

                if (!TryComputeBearing(obj.Position, pp, heading, maxDistSq, extHalf,
                                       out float dist, out float delta))
                    continue;

                allCandidates.Add((obj, null, dist, delta, 1f - dist / maxDist, col));
            }
        }

        // ── Collect FATE markers (independent of ShowAnyMarkers) ─────────────
        if (config.ShowFates)
        {
            foreach (var fate in fateTable)
            {
                if (fate == null) continue;
                if (fate.State != FateState.Running && fate.State != FateState.Preparing)
                    continue;

                if (!TryComputeBearing(fate.Position, pp, heading, fateMaxDistSq, extHalf,
                                       out float dist, out float delta))
                    continue;

                allCandidates.Add((null, fate, dist, delta, 1f - dist / fateMaxDist, 0u));
            }
        }

        if (allCandidates.Count == 0) return;

        // Farthest first → closest last: later ImGui draw calls paint on top.
        allCandidates.Sort((a, b) => b.Dist.CompareTo(a.Dist));

        foreach (var candidate in allCandidates)
        {
            float delta = candidate.Delta;
            float t     = candidate.T;
            float sx    = cx + Project(delta, halfVis, barHalfW, lensStr);
            float alpha = ComputeFadeAlpha(t) * LensEdgeAlpha(delta, halfVis, extHalf);

            // ── FATE branch ───────────────────────────────────────────────────
            if (candidate.Fate is { } fate)
            {
                float fateIconSize = Lerp(config.FateIconMinSize, config.FateIconMaxSize, t);
                bool  drewFateIcon = fate.IconId > 0
                                  && TryDrawIcon(dl, (int)fate.IconId, sx, cy, fateIconSize, alpha);
                if (!drewFateIcon)
                    DrawFilledDot(dl, sx, cy, (3f + 7f * t) * 2f, C(config.FateColor), alpha);
                continue;
            }

            // ── Game-object branch ────────────────────────────────────────────
            var  obj = candidate.Obj!;
            uint col = candidate.Col;

            // Fallback dot radius — only used by the Gathering-node else-branch below.
            float r = 3f + 7f * t;

            // Each category with a real icon resolves its iconId/iconSize here, in
            // priority order, before the shared draw call below.
            int   iconId   = 0;
            float iconSize = 0f;

            bool  isAetheryteKind = ClassifyAetheryte(obj) != AetheryteNameKind.None;
            // Shared by quest/Mender/Shop icons — every NPC icon category uses this range.
            float npcIconSize = Lerp(config.NpcQuestIconMinSize, config.NpcQuestIconMaxSize, t)
                              * IconSizeMultiplier;

            if (config.ShowAetheryteIcons && isAetheryteKind)
            {
                iconId   = GetAetheryteIconId(obj);
                iconSize = Lerp(config.AetheryteIconMinSize, config.AetheryteIconMaxSize, t)
                         * AetheryteIconSizeMultiplier;
            }
            else if (config.ShowNpcQuestIcons
                && obj.ObjectKind == ObjectKind.EventNpc
                && npcMarkerIcons.TryGetValue(obj.GameObjectId, out int npcIcon))
            {
                iconId   = npcIcon;
                iconSize = npcIconSize;
            }
            else if (config.ShowMenderIcons
                && obj.ObjectKind == ObjectKind.EventNpc
                && NpcMatchesKeywords(obj, GetNpcTitle(obj.BaseId), MenderTitleKeywords))
            {
                // Shares the quest-marker size range above.
                iconId   = config.MenderIconId;
                iconSize = npcIconSize;
            }
            else if (config.ShowShopIcons
                && obj.ObjectKind == ObjectKind.EventNpc
                && NpcMatchesKeywords(obj, GetNpcTitle(obj.BaseId), ShopTitleKeywords))
            {
                iconId   = config.ShopIconId;
                iconSize = npcIconSize;
            }
            else if (config.ShowGatheringIcons && obj.ObjectKind == ObjectKind.GatheringPoint)
            {
                int gatherIcon = GetGatheringIconId(obj.BaseId);
                if (gatherIcon > 0)
                {
                    iconId   = gatherIcon;
                    iconSize = Lerp(config.GatheringIconMinSize, config.GatheringIconMaxSize, t);
                }
            }
            else if (config.ShowTreasureIcons && obj.ObjectKind == ObjectKind.Treasure)
            {
                // No data sheet exposes a coffer's visual type from its BaseId, so
                // every coffer just shows the same configured icon.
                iconId   = config.TreasureIconId;
                iconSize = Lerp(config.TreasureMinSize, config.TreasureMaxSize, t);
            }

            bool drewIcon = iconId > 0 && TryDrawIcon(dl, iconId, sx, cy, iconSize, alpha);

            if (!drewIcon)
            {
                if (obj.ObjectKind == ObjectKind.Pc)
                {
                    // Drives every player marker: ring, friend dot, party role icon.
                    float playerSize = Lerp(config.PartyRoleIconMinSize, config.PartyRoleIconMaxSize, t);

                    bool drewJobIcon = false;

                    if (config.ShowPartyRoleIcons && obj is ICharacter partyChar
                        && (partyChar.StatusFlags & StatusFlags.PartyMember) != 0)
                    {
                        // Unbordered class/job icons confirmed at 62001-62047 via /xldata:
                        // 62000 + ClassJob.RowId (1-indexed: GLA=62001, PGL=62002, ...).
                        uint jobRowId  = partyChar.ClassJob.RowId;
                        int  jobIconId = jobRowId > 0 ? (int)(62000 + jobRowId) : 0;

                        if (jobIconId > 0)
                        {
                            // Role-colored ring + shadow (Tank=blue/Healer=green/DPS=red/
                            // DoH-DoL=gray, matching FFXIV's role UI), unclipped so it
                            // isn't cut at the bar's edge.
                            uint  roleCol      = GetRoleColor(partyChar);
                            float iconDrawSize = playerSize * IconSizeMultiplier;
                            float iconHalf     = iconDrawSize * 0.5f;
                            PushUnclip(dl);
                            DrawOuterRing(dl, sx, cy, iconHalf, roleCol, alpha);
                            DrawInwardShadow(dl, sx, cy, iconHalf, roleCol, alpha);
                            PopUnclip(dl);

                            // Drawn on top — if the job icon ID doesn't resolve (e.g. a
                            // future job), the role ring/shadow still shows as a fallback.
                            TryDrawIcon(dl, jobIconId, sx, cy, iconDrawSize, alpha);
                            drewJobIcon = true;
                        }
                    }

                    if (!drewJobIcon)
                    {
                        // Named override checked before friend-dot/hollow-ring defaults;
                        // party role icons (above) take priority over this.
                        PlayerIconOverride? nameOverride = null;
                        if (config.PlayerIconOverrides.Count > 0)
                        {
                            var objName = obj.Name.TextValue;
                            foreach (var ov in config.PlayerIconOverrides)
                            {
                                if (ov.PlayerName.Length > 0
                                    && string.Equals(ov.PlayerName, objName,
                                                     StringComparison.OrdinalIgnoreCase))
                                {
                                    nameOverride = ov;
                                    break;
                                }
                            }
                        }

                        if (nameOverride is not null)
                        {
                            // Border/fill always sit at this base size, matching the other
                            // player markers. The icon itself shares it as its baseline too,
                            // but TryDrawIcon may scale the icon past it: ClipToCircle keeps
                            // the icon bounded here (zooms via UV crop instead), while a
                            // square icon has no ring to respect and genuinely grows/shrinks
                            // with SizeMultiplier.
                            float overrideSize = playerSize * IconSizeMultiplier;
                            float overrideHalf = overrideSize * 0.5f;

                            // Fill/border drawn first and always shown when enabled, even
                            // if the icon texture hasn't loaded — same as party role icons.
                            if (nameOverride.ShowFill || nameOverride.ShowBorder)
                            {
                                PushUnclip(dl);
                                if (nameOverride.ShowFill)
                                    DrawInwardShadow(dl, sx, cy, overrideHalf,
                                                     C(nameOverride.FillColor), alpha);
                                if (nameOverride.ShowBorder)
                                    DrawOuterRing(dl, sx, cy, overrideHalf,
                                                  C(nameOverride.BorderColor), alpha);
                                PopUnclip(dl);
                            }

                            bool drewOverrideIcon = nameOverride.IconBaseId > 0
                                && TryDrawIcon(dl, nameOverride.IconBaseId, sx, cy, overrideSize,
                                               alpha, nameOverride.ClipToCircle,
                                               nameOverride.SizeMultiplier);

                            if (!drewOverrideIcon)
                            {
                                // Texture not loaded / ID is zero — fall back to a dot,
                                // tinted with the border color if one's configured.
                                uint fallbackCol = nameOverride.ShowBorder
                                    ? C(nameOverride.BorderColor) : col;
                                DrawFilledDot(dl, sx, cy, playerSize, fallbackCol, alpha);
                            }
                        }
                        else
                        {
                            // No override — default friend-dot / hollow-ring rendering.
                            bool isFriend = config.SolidFriendDots
                                && obj is ICharacter ch
                                && (ch.StatusFlags & StatusFlags.Friend) != 0;

                            if (isFriend)
                                DrawFilledDot(dl, sx, cy, playerSize, col, alpha);
                            else
                                DrawHollowDot(dl, sx, cy, playerSize, col, alpha);
                        }
                    }
                }
                else if (obj.ObjectKind == ObjectKind.EventNpc && !isAetheryteKind)
                {
                    // Plain NPC dot, sized from the same slider as the icons above.
                    // Excludes aetheryte-classified EventNpcs (Firmament crystals),
                    // which use the Aetheryte color/size path instead.
                    DrawHollowDot(dl, sx, cy,
                        Lerp(config.NpcQuestIconMinSize, config.NpcQuestIconMaxSize, t), col, alpha);
                }
                else if (obj.ObjectKind == ObjectKind.BattleNpc)
                {
                    // Enemy dot, sized from its own page's slider.
                    DrawFilledDot(dl, sx, cy, Lerp(config.EnemyMinSize, config.EnemyMaxSize, t), col, alpha);
                }
                else if (isAetheryteKind)
                {
                    // Plain aetheryte dot (icons off or texture didn't resolve), sized
                    // from the same slider as the icon. Covers every kind
                    // ClassifyAetheryte can return Big/Shard for, not just ObjectKind.Aetheryte.
                    DrawFilledDot(dl, sx, cy,
                        Lerp(config.AetheryteIconMinSize, config.AetheryteIconMaxSize, t), col, alpha);
                }
                else if (obj.ObjectKind == ObjectKind.Treasure)
                {
                    // Plain treasure dot, sized from the same slider as the chest icon.
                    DrawFilledDot(dl, sx, cy,
                        Lerp(config.TreasureMinSize, config.TreasureMaxSize, t), col, alpha);
                }
                else
                {
                    // Plain dot fallback for Gathering nodes that don't have their own
                    // size slider yet — next on the list.
                    DrawFilledDot(dl, sx, cy, r * 2f, col, alpha);
                }
            }
        }
    }

    /// <summary>
    /// Three-zone distance fade shared by all markers/FATEs: opaque inside DotNearZone,
    /// smoothstep to DotMidAlpha through the middle band, smoothstep to 0 by DotFarZone.
    /// t = 1 at zero distance, 0 at max range.
    /// </summary>
    private float ComputeFadeAlpha(float t)
    {
        float nearZone = config.DotNearZone;
        float midEnd   = config.DotFarZone;
        float midAlpha = config.DotMidAlpha;

        if (t >= nearZone) return 1f;

        if (t >= midEnd)
        {
            float u  = (t - midEnd) / (nearZone - midEnd);
            float sm = u * u * (3f - 2f * u);
            return midAlpha + (1f - midAlpha) * sm;
        }

        float uu = t / midEnd;
        float ss = uu * uu * (3f - 2f * uu);
        return midAlpha * ss;
    }


    /// <summary>
    /// Draws a game icon by ID, centred at the given screen position. Returns false
    /// (fall back to a dot) if the texture isn't resolved yet. <paramref name="uvZoom"/>
    /// behaves differently depending on <paramref name="clipToCircle"/>:
    /// <list type="bullet">
    /// <item>Circle-clipped: the drawn quad stays fixed at <paramref name="size"/> (so it
    /// never grows past a same-sized border ring) and zooming instead crops into the
    /// texture (&gt;1) or zooms out past its edge (&lt;1).</item>
    /// <item>Square (uncropped): there's no fixed boundary to respect, so zooming
    /// genuinely scales the drawn quad itself — the icon grows on screen and can
    /// spill past a same-sized border ring, showing the full texture throughout.</item>
    /// </list>
    /// 1.0 (default) shows the icon at <paramref name="size"/> either way.
    /// </summary>
    private bool TryDrawIcon(ImDrawListPtr dl, int iconId, float sx, float cy, float size, float alpha, bool clipToCircle = false, float uvZoom = 1.0f)
    {
        if (!textureProvider.TryGetFromGameIcon(new GameIconLookup((uint)iconId), out var sharedTex))
            return false;

        var tex = sharedTex.GetWrapOrEmpty();

        uint tint = WithAlpha(0xFFFFFFFFu, alpha);

        float   half;
        Vector2 uvMin, uvMax;

        if (clipToCircle)
        {
            // Fixed footprint matching the border ring — zoom crops the texture
            // (UV window shrinks) instead of growing the quad, so the visible art
            // never spills past the ring it's framed by.
            half          = size * 0.5f;
            float uvHalf  = 0.5f / Math.Max(0.01f, uvZoom);
            uvMin = new(0.5f - uvHalf, 0.5f - uvHalf);
            uvMax = new(0.5f + uvHalf, 0.5f + uvHalf);
        }
        else
        {
            // No clip shape to respect, so zoom scales the quad itself — full
            // texture throughout, the icon just gets bigger/smaller on screen.
            half  = size * 0.5f * Math.Max(0.01f, uvZoom);
            uvMin = new(0f, 0f);
            uvMax = new(1f, 1f);
        }

        // RenderBar clips to the bar rect, which can cut off icons whose bounding box
        // (inflated by the size multipliers) is wider than the bar near the edges.
        // Briefly unclip just for this image; paint order is unaffected since the
        // end-cap diamonds are drawn after RenderBar's own clip is popped anyway.
        PushUnclip(dl);
        // rounding=0 draws a plain rect, so one call covers both clipToCircle states.
        dl.AddImageRounded(
            tex.Handle,
            V(sx - half, cy - half),
            V(sx + half, cy + half),
            uvMin, uvMax,
            tint,
            clipToCircle ? half : 0f,
            ImDrawFlags.RoundCornersAll);
        PopUnclip(dl);
        return true;
    }

    /// <summary>
    /// Resolves a gathering node's icon via GatheringPoint(BaseId) → GatheringPointBase
    /// → GatheringType → IconMain. Cached permanently per BaseId. Returns 0 if any link
    /// in the chain doesn't resolve.
    /// </summary>
    private int GetGatheringIconId(uint baseId)
    {
        if (gatheringIconCache.TryGetValue(baseId, out int cached))
            return cached;

        int icon = 0;
        var gpRow = gatheringPointSheet.GetRowOrDefault(baseId);
        if (gpRow != null)
        {
            var gpBaseRow = gatheringPointBaseSheet.GetRowOrDefault(gpRow.Value.GatheringPointBase.RowId);
            if (gpBaseRow != null)
            {
                var typeRow = gatheringTypeSheet.GetRowOrDefault(gpBaseRow.Value.GatheringType.RowId);
                if (typeRow != null)
                    icon = typeRow.Value.IconMain;
            }
        }

        gatheringIconCache[baseId] = icon;
        return icon;
    }

    // Role icon IDs confirmed against xivPartyIcons. Uses ClassJob.Role (not a
    // per-job index) so it auto-works for every current/future job.
    /// <summary>Tank=blue, Healer=green, DPS=red, DoH/DoL=gray — matches FFXIV's role UI.</summary>
    private uint GetRoleColor(ICharacter character)
    {
        var row = classJobSheet.GetRowOrDefault(character.ClassJob.RowId);
        if (row == null) return C(new Vector4(0.54f, 0.54f, 0.54f, 0.85f));
        return row.Value.Role switch
        {
            1     => C(new Vector4(0.36f, 0.48f, 0.76f, 0.90f)),   // Tank — blue
            2 or 3 => C(new Vector4(0.84f, 0.30f, 0.30f, 0.90f)), // DPS  — red
            4     => C(new Vector4(0.30f, 0.69f, 0.49f, 0.90f)),   // Healer — green
            _     => C(new Vector4(0.54f, 0.54f, 0.54f, 0.85f)),   // DoH/DoL — gray
        };
    }

    /// <summary>Resolves an NPC's title via ENpcResident, cached permanently per BaseId. "" if none.</summary>
    private string GetNpcTitle(uint baseId)
    {
        if (npcTitleCache.TryGetValue(baseId, out string? cached))
            return cached;

        string title = "";
        var row = npcResidentSheet.GetRowOrDefault(baseId);
        if (row != null)
            title = row.Value.Title.ToString();

        npcTitleCache[baseId] = title;
        return title;
    }

    /// <summary>Case-insensitive "does title contain any of these keywords" check.</summary>
    private static bool TitleContainsAny(string title, string[] keywords)
    {
        if (string.IsNullOrEmpty(title)) return false;
        foreach (var kw in keywords)
            if (title.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// Checks both places job-keyword text can live: the ENpcResident Title field, or
    /// the live display Name for NPCs with no personal name (e.g. "Independent Mender").
    /// Both patterns occur in practice — Title alone misses the second kind.
    /// </summary>
    private static bool NpcMatchesKeywords(IGameObject obj, string title, string[] keywords) =>
        TitleContainsAny(title, keywords) || TitleContainsAny(obj.Name.TextValue, keywords);

    /// <summary>Which aetheryte category, if any, an object matches.</summary>
    private enum AetheryteNameKind { None, Big, Shard }

    /// <summary>
    /// Classifies by name match against AethernetShardName ("Aethernet" substring,
    /// default). ObjectKind.Aetheryte is always Big or Shard (Shard if matched, Big
    /// otherwise — the game uses this kind for nothing else). EventNpc/EventObj only
    /// ever return Shard on a match, never Big, since those kinds cover ordinary NPCs/
    /// furnishings too — a non-match there returns None.
    /// Single source of truth for both icon selection and visibility.
    /// </summary>
    private AetheryteNameKind ClassifyAetheryte(IGameObject obj)
    {
        bool looksLikeShard = !string.IsNullOrEmpty(config.AethernetShardName)
            && obj.Name.TextValue.Contains(config.AethernetShardName, StringComparison.OrdinalIgnoreCase);

        if (obj.ObjectKind == ObjectKind.Aetheryte)
            return looksLikeShard ? AetheryteNameKind.Shard : AetheryteNameKind.Big;

        return looksLikeShard ? AetheryteNameKind.Shard : AetheryteNameKind.None;
    }

    /// <summary>Icon selection is always correct for whatever this is classified as.</summary>
    private int GetAetheryteIconId(IGameObject obj) =>
        ClassifyAetheryte(obj) == AetheryteNameKind.Shard
            ? config.AethernetShardIconId
            : config.AetheryteIconId;   // Big, or unmatched — default to the Big icon

    /// <summary>Matches an object against the aetheryte pattern; returns the color (0u if hidden by config), or false if no match at all (caller falls through).</summary>
    private bool TryGetAetheryteMarkerColor(IGameObject obj, out uint color)
    {
        var kind = ClassifyAetheryte(obj);
        if (kind == AetheryteNameKind.None)
        {
            color = 0u;
            return false;
        }

        bool hidden = !config.ShowAetherytes
            || (kind == AetheryteNameKind.Shard && !config.ShowAethernetShards);
        color = hidden ? 0u : C(config.AetheryteColor);
        return true;
    }

    private uint MarkerColor(IGameObject obj, IPlayerCharacter player)
    {
        switch (obj.ObjectKind)
        {
            case ObjectKind.Pc:
                return config.ShowPlayers ? C(config.PlayerColor) : 0u;
            case ObjectKind.BattleNpc:
                if (!config.ShowEnemies) return 0u;
                if (obj is not IBattleNpc bnpc || bnpc.BattleNpcKind != BattleNpcSubKind.Combatant)
                    return 0u;

                if (config.EnemiesOnlyIfEngaged)
                {
                    // GameObjectId (ulong) and EntityId (uint) are distinct ID spaces;
                    // TargetObjectId is in GameObjectId terms.
                    bool targetingMe    = obj.TargetObjectId == player.GameObjectId;
                    bool iAmTargetingIt = targetManager.Target?.GameObjectId == obj.GameObjectId;
                    if (!targetingMe && !iAmTargetingIt) return 0u;
                }

                return C(config.EnemyColor);
            case ObjectKind.EventNpc:
                // Firmament teleport crystals are EventNpcs sharing the "Aethernet
                // Shard ..." name pattern — route through aetheryte color, not NPC's.
                if (TryGetAetheryteMarkerColor(obj, out uint eventNpcAetherCol))
                    return eventNpcAetherCol;

                if (!config.ShowNpcs) return 0u;
                if (config.NpcsOnlyIfTargetable && !obj.IsTargetable) return 0u;
                return C(config.NpcColor);
            case ObjectKind.EventObj:
                // Housing-ward Aethernet shards are ObjectKind.EventObj (confirmed via
                // /compass debug — not EventNpc/HousingEventObject). Only EventObj kind tracked.
                return TryGetAetheryteMarkerColor(obj, out uint eventObjAetherCol)
                    ? eventObjAetherCol
                    : 0u;
            case ObjectKind.GatheringPoint:
                if (!config.ShowGatheringNodes) return 0u;
                if (config.GatheringOnlyIfTargetable && !obj.IsTargetable) return 0u;
                return C(config.GatheringColor);
            case ObjectKind.Treasure:
                return config.ShowTreasure ? C(config.TreasureColor) : 0u;
            case ObjectKind.Aetheryte:
                // ClassifyAetheryte always returns Big or Shard for this kind (never
                // None), so this call always succeeds.
                TryGetAetheryteMarkerColor(obj, out uint realAetherCol);
                return realAetherCol;
            default:
                return 0u;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static float Normalize(float a)
    {
        a %= 360f;
        return a < 0f ? a + 360f : a;
    }

    private static float Delta(float from, float to)
    {
        float d = to - from;
        while (d >  180f) d -= 360f;
        while (d < -180f) d += 360f;
        return d;
    }

    /// <summary>
    /// Shared by RenderMarkers/RenderFates: range + FOV filter and bearing/distance calc
    /// for one target. Full 3D distance (range/size/fade); bearing stays 2D (dx,dz) since
    /// height shouldn't shove a dot sideways on the compass. Returns false (dist/delta
    /// undefined) if out of range or outside the visible FOV.
    /// </summary>
    private static bool TryComputeBearing(
        Vector3 targetPos, Vector3 originPos, float heading, float maxDistSq, float extHalf,
        out float dist, out float delta)
    {
        float dx  = targetPos.X - originPos.X;
        float dy  = targetPos.Y - originPos.Y;
        float dz  = targetPos.Z - originPos.Z;
        float dsq = dx * dx + dy * dy + dz * dz;

        dist = 0f; delta = 0f;
        if (dsq > maxDistSq || dsq < 0.25f) return false;

        float bearing = Normalize(MathF.Atan2(dx, -dz) * (180f / MathF.PI));
        delta = Delta(heading, bearing);
        if (MathF.Abs(delta) > extHalf) return false;

        dist = MathF.Sqrt(dsq);
        return true;
    }

    private static Vector2 V(float x, float y) => new(x, y);
    private static uint     C(Vector4 v)        => ImGui.ColorConvertFloat4ToU32(v);

    /// <summary>t=1 → max, t=0 → min. Used for every "size at this distance" calc.</summary>
    private static float Lerp(float min, float max, float t) => min + (max - min) * t;

    /// <summary>Solid disc + soft dark outline — the shared look for every filled marker dot.</summary>
    private static void DrawFilledDot(ImDrawListPtr dl, float sx, float cy, float size, uint col, float alpha)
    {
        float r = size * 0.5f;
        dl.AddCircleFilled(V(sx, cy), r, WithAlpha(col, alpha));
        dl.AddCircle(V(sx, cy), r + 0.8f, WithAlpha(0x66000000u, alpha));
    }

    /// <summary>Open ring + faint outline — the shared look for every hollow marker dot.</summary>
    private static void DrawHollowDot(ImDrawListPtr dl, float sx, float cy, float size, uint col, float alpha)
    {
        float r = size * 0.5f;
        dl.AddCircle(V(sx, cy), r, WithAlpha(col, alpha), 0, 2.0f);
        dl.AddCircle(V(sx, cy), r + 0.8f, WithAlpha(0x33000000u, alpha));
    }

    /// <summary>3 inward-fading circles faking a soft shadow behind an icon (party role icon / player override fill).</summary>
    private static void DrawInwardShadow(ImDrawListPtr dl, float sx, float cy, float half, uint col, float alpha)
    {
        dl.AddCircleFilled(V(sx, cy), half * 0.85f, WithAlpha(col, alpha * 0.6f));
        dl.AddCircleFilled(V(sx, cy), half * 0.65f, WithAlpha(col, alpha * 0.4f));
        dl.AddCircleFilled(V(sx, cy), half * 0.45f, WithAlpha(col, alpha * 0.2f));
    }

    /// <summary>Solid ring just outside an icon's bounding box (party role icon / player override border).</summary>
    private static void DrawOuterRing(ImDrawListPtr dl, float sx, float cy, float half, uint col, float alpha) =>
        dl.AddCircle(V(sx, cy), half + 1.0f, WithAlpha(col, alpha), 0, 3.0f);

    /// <summary>1 inside the linear zone, smoothsteps to 0 toward the edge. linearHalf lets labels fade earlier than ticks.</summary>
    private static float LensEdgeAlpha(float delta, float linearHalf, float extHalf)
    {
        float absD = MathF.Abs(delta);
        if (absD <= linearHalf) return 1f;
        float t = (absD - linearHalf) / (extHalf - linearHalf);   // 0→1 in fade zone
        t = MathF.Min(1f, t);
        float s = t * t * (3f - 2f * t);   // smoothstep
        return 1f - s;
    }

    private static uint WithAlpha(uint color, float mul)
    {
        uint origA = (color >> 24) & 0xFFu;
        uint newA  = (uint)(origA * MathF.Min(1f, MathF.Max(0f, mul)));
        return (color & 0x00FFFFFFu) | (newA << 24);
    }

    /// <summary>
    /// Pushes a full-display clip rect, overriding RenderBar's bar-sized clip, so
    /// content between this and <see cref="PopUnclip"/> can render past the bar edge.
    /// Used by TryDrawIcon's image and the role-icon/override border+fill circles,
    /// which must escape together or visually disagree at the edge.
    /// </summary>
    private static void PushUnclip(ImDrawListPtr dl) =>
        dl.PushClipRect(Vector2.Zero, ImGui.GetIO().DisplaySize, false);

    private static void PopUnclip(ImDrawListPtr dl) => dl.PopClipRect();

    /// <summary>Logs every object within radius yalms (ObjectKind, BaseId, name, distance) — diagnostic for "why isn't this showing". View via /xllog.</summary>
    public void DumpNearbyObjects(float radius = 50f)
    {
        var player = objectTable.LocalPlayer;
        if (player == null)
        {
            log.Info("[SkyrimCompass debug] No local player — are you logged in?");
            return;
        }

        var pp = player.Position;
        var nearby = new List<(float dist, IGameObject obj)>();

        foreach (var obj in objectTable)
        {
            if (obj == null || obj.EntityId == player.EntityId) continue;
            float dx = obj.Position.X - pp.X;
            float dy = obj.Position.Y - pp.Y;
            float dz = obj.Position.Z - pp.Z;
            float dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            if (dist <= radius)
                nearby.Add((dist, obj));
        }

        nearby.Sort((a, b) => a.dist.CompareTo(b.dist));

        log.Info($"[SkyrimCompass debug] {nearby.Count} object(s) within {radius}y — nearest first:");
        foreach (var (dist, obj) in nearby)
        {
            string extra = "";
            if (obj.ObjectKind == ObjectKind.EventNpc)
            {
                string title       = GetNpcTitle(obj.BaseId);
                bool   hasQuestIcon = npcMarkerIcons.TryGetValue(obj.GameObjectId, out int qIconId) && qIconId > 0;
                bool   isMender     = NpcMatchesKeywords(obj, title, MenderTitleKeywords);
                bool   isShop       = NpcMatchesKeywords(obj, title, ShopTitleKeywords);

                // Mirrors RenderMarkers' priority order exactly.
                string winner = hasQuestIcon ? $"QuestMarker(icon={qIconId})"
                              : isMender     ? "Mender"
                              : isShop       ? "Shop"
                              : "none/dot";

                extra = $" | Title=\"{title}\" | QuestIcon={hasQuestIcon,-5} | " +
                        $"IsMender={isMender,-5} | IsShop={isShop,-5} | WouldShow={winner}";
            }
            else if (obj.ObjectKind == ObjectKind.Treasure)
            {
                string winner = config.ShowTreasureIcons
                    ? $"Icon({config.TreasureIconId})"
                    : "dot";

                extra = $" | WouldShow={winner}";
            }

            log.Info(
                $"[SkyrimCompass debug] {dist,6:F1}y | Kind={obj.ObjectKind,-19} | " +
                $"BaseId={obj.BaseId,-8} | Targetable={obj.IsTargetable,-5} | " +
                $"Name=\"{obj.Name.TextValue}\"{extra}");
        }
        log.Info("[SkyrimCompass debug] Done. Use /xllog in-game to view the log window.");
    }
}
