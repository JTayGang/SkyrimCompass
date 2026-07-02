using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using Dalamud.Game;
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
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace SkyrimCompass;

// Renders a Skyrim-style compass bar via ImGui foreground draw list with fisheye/lens projection.
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

    // Limit-break fade-out state (frame-persistent). On a big gauge drop (LB used),
    // geometry freezes at lbFrozenProgress and a centre→edge wipe plays over LbFadeOutDuration.
    private float lbTrackedProgress  = 0f;   // last value outside a fade-out
    private float lbFrozenProgress   = 0f;   // snapshot when drain detected
    private float lbFadeOutStartTime = -1f;  // ImGui time wipe started; -1 = inactive
    private const float LbFadeOutDuration = 2f;
    private const float LbDropThreshold   = 0.4f;

    // GameObjectId → nameplate marker icon ID, refreshed every nameplate update. 0/absent = none.
    private readonly Dictionary<ulong, int> npcMarkerIcons = new();

    // BaseId → resolved gathering icon ID. Cached permanently (static game data).
    private readonly Dictionary<uint, int> gatheringIconCache = new();
    private readonly ExcelSheet<GatheringPoint>     gatheringPointSheet;
    private readonly ExcelSheet<GatheringPointBase> gatheringPointBaseSheet;
    private readonly ExcelSheet<GatheringType>      gatheringTypeSheet;

    // BaseId → NPC title text (always English, see npcResidentSheet below), cached permanently.
    private readonly Dictionary<uint, string> npcTitleCache = new();
    // BaseId → NPC "Singular" text (always English), cached permanently. Confirmed via
    // /compass debug: named NPCs (e.g. "Alistair") carry their vocation in Title and their
    // personal name in Singular, but generic flavor NPCs with no personal name (e.g.
    // "Independent Tinker", "Material Supplier") have an EMPTY Title and carry the vocation
    // word in Singular instead (stored lowercase — fine, matching below is case-insensitive).
    private readonly Dictionary<uint, string> npcSingularCache = new();
    // Requested in English regardless of the client's actual game language, so keyword
    // matching below (MenderTitleKeywords/ShopTitleKeywords) is language-independent —
    // an NPC titled "Heiler" on a German client still resolves to "Mender" here.
    private readonly ExcelSheet<ENpcResident> npcResidentSheet;
    // Client's actual game language, unforced — NOT used for matching, only so /compass
    // debug can print it side-by-side with npcResidentSheet above and show whether a
    // problem is "English-forcing isn't working" vs "this field is empty either way".
    private readonly ExcelSheet<ENpcResident> npcResidentSheetClientLang;
    private readonly ExcelSheet<ClassJob>     classJobSheet;

    // Matched against the English ENpcResident Title AND Singular (see caches above),
    // regardless of client language. Grow these lists as new NPC vocation words turn up —
    // use /compass debug near the NPC to read its TitleEN/SingularEN.
    private static readonly string[] MenderTitleKeywords = { "Mender", "Tinker" };

    // Unified candidate list (game objects + FATEs) reused every frame — no per-frame alloc.
    // Obj != null → game object; Fate != null → FATE. T is normalised distance fraction.
    private readonly List<(IGameObject? Obj, IFate? Fate, float Dist, float Delta, float T, uint Col)> allCandidates = new();

    // Static delegate avoids allocating a new Comparison<> object every frame on Sort.
    private static readonly Comparison<(IGameObject? Obj, IFate? Fate, float Dist, float Delta, float T, uint Col)>
        DistFarFirst = (a, b) => b.Dist.CompareTo(a.Dist);

    // Extra scale for icons with transparent padding (quest/Mender/Shop/job/override icons).
    // NOT applied to Gathering (not undersized) or Aetheryte (has its own multiplier).
    private const float IconSizeMultiplier          = 1.5f;
    private const float AetheryteIconSizeMultiplier = 1.75f;

    // Same English-forced matching as MenderTitleKeywords above.
    private static readonly string[] ShopTitleKeywords =
    {
        "Merchant", "Vendor", "Trader", "Sutler", "Supplier", "Junkmonger",
        "Fishmonger", "Dyemonger", "Jeweler", "Apothecary", "Culinarian",
        "Salvager", "Exchange", "Clothier",
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
        npcResidentSheet        = dataManager.GetExcelSheet<ENpcResident>(ClientLanguage.English);
        npcResidentSheetClientLang = dataManager.GetExcelSheet<ENpcResident>();
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

        // OccupiedInCutSceneEvent/WatchingCutscene/WatchingCutscene78 cover all cutscene types.
        if (config.HideDuringCutscenes && (
            condition[ConditionFlag.OccupiedInCutSceneEvent] ||
            condition[ConditionFlag.WatchingCutscene] ||
            condition[ConditionFlag.WatchingCutscene78]))
            return;

        var player = objectTable.LocalPlayer;
        if (player == null) return;

        float headingRad = 0f;
        var   originPos  = player.Position;  // default: bearings/distances from character
        bool  gotHeading = false;

        if (config.UseCameraDirection)
        {
            // DirH increases counter-clockwise (tested in-game) — negate to fix.
            var cm     = CameraManager.Instance();
            var camera = cm != null ? cm->Camera : null;
            if (camera != null && !float.IsNaN(camera->DirH))
            {
                headingRad = -camera->DirH;

                // First-person: DirH is a direct view angle (not orbital), so exactly 180° off.
                if (camera->ZoomMode == CameraZoomMode.FirstPerson)
                    headingRad += MathF.PI;

                if (config.UseCameraPosition)
                {
                    var camPos = camera->LastPosition;
                    if (!float.IsNaN(camPos.X) && !float.IsNaN(camPos.Y) && !float.IsNaN(camPos.Z))
                        originPos = camPos;
                }
                gotHeading = true;
            }
        }

        // Fallback: character facing (also covers UseCameraDirection=false or unavailable camera).
        if (!gotHeading)
        {
            if (float.IsNaN(player.Rotation)) return;
            headingRad = MathF.PI - player.Rotation;  // FFXIV: rotation=0 → south, π → north
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
    /// Maps a bearing offset (degrees) to a signed pixel offset from bar centre.
    /// f(u) = 1-(1-u)^k, k = lensStrength. Linear at centre, compressed at edges.
    /// lensStrength = 1.0 → pure linear.
    /// </summary>
    private static float Project(float delta, float halfVis, float barHalfW, float lensStr)
    {
        float extHalf = halfVis * lensStr;
        float absD    = MathF.Min(MathF.Abs(delta), extHalf);
        float u       = absD / extHalf;
        float f       = 1f - MathF.Pow(1f - u, lensStr);
        return (delta >= 0f ? 1f : -1f) * barHalfW * f;
    }

    // ── Main render ───────────────────────────────────────────────────────────

    private void RenderBar(
        ImDrawListPtr dl,
        float bx, float by, float bw, float bh,
        float heading, IPlayerCharacter player, Vector3 originPos)
    {
        float cx       = bx + bw * 0.5f;
        float cy       = by + bh * 0.5f;
        float barHalfW = bw * 0.5f;

        float halfVis = config.VisibleDegrees * 0.5f;
        float lensStr = config.LensStrength;
        float extHalf = halfVis * lensStr;

        uint bgCol     = C(config.BackgroundColor);
        uint borderCol = C(config.BorderColor);
        uint tickCol   = C(config.TickColor);
        uint cardCol   = C(config.CardinalColor);
        uint ixCol     = C(config.IntercardinalColor);

        // Fully-opaque background for the masking cap fills.
        uint solidBgCol = (bgCol & 0x00FFFFFFu) | 0xFF000000u;

        // Diamond end-cap dimensions.
        float capHW = bh * 0.44f;
        float capHH = bh * 0.64f;

        // Run unconditionally so fade-out tracks real LB usage even when glow is toggled off.
        float rawLbProgress       = GetLimitBreakProgress();
        float displayedLbProgress = UpdateLimitBreakDisplay(rawLbProgress, (float)ImGui.GetTime(), out float lbWipeProgress);
        float lbProgress          = config.ShowLimitBreakGlow ? displayedLbProgress : 0f;
        if (!config.ShowLimitBreakGlow) lbWipeProgress = 0f;

        // ── 1. Background ─────────────────────────────────────────────────────
        dl.AddRectFilled(V(bx, by), V(bx + bw, by + bh), bgCol);

        // Warm centre glow
        uint  warmGlow = ImGui.ColorConvertFloat4ToU32(new Vector4(0.9f, 0.70f, 0.35f, 0.08f));
        float gw       = bw * 0.22f;
        dl.AddRectFilledMultiColor(V(cx - gw, by), V(cx,      by + bh), 0u,       warmGlow, warmGlow, 0u);
        dl.AddRectFilledMultiColor(V(cx,      by), V(cx + gw, by + bh), warmGlow, 0u,       0u,       warmGlow);

        // Edge vignette
        dl.AddRectFilledMultiColor(V(bx,              by), V(bx + bw * 0.14f, by + bh), 0xAA000000u, 0u,          0u,          0xAA000000u);
        dl.AddRectFilledMultiColor(V(bx + bw * 0.86f, by), V(bx + bw,         by + bh), 0u,          0xAA000000u, 0xAA000000u, 0u);

        // Top bevel
        dl.AddLine(V(bx + 1f, by + 1f), V(bx + bw - 1f, by + 1f), 0x1AFFFFFF, 1f);

        // ── 2. Border ─────────────────────────────────────────────────────────
        // Drawn before markers so icons (often taller than the bar) paint over the border.
        dl.AddRect(V(bx, by), V(bx + bw, by + bh), borderCol, 0f, ImDrawFlags.None, 1.5f);

        // ── 3. Limit break glow ───────────────────────────────────────────────
        // One layer per bar; each bar's own 0–1 progress. Layers detuned to avoid lockstep waves.
        if (lbProgress > 0f)
        {
            float glowT = (float)ImGui.GetTime();
            float bar1  = Math.Clamp(lbProgress,       0f, 1f);
            float bar2  = Math.Clamp(lbProgress - 1f,  0f, 1f);
            float bar3  = Math.Clamp(lbProgress - 2f,  0f, 1f);

            static float Intensity(float tt) =>
                (0.75f + 0.25f * MathF.Sin(tt * 0.79f)) * (0.92f + 0.08f * MathF.Sin(tt * 3.23f + 1.17f));

            (float bar, float tMul, float tOff, Vector4 color)[] lbLayers =
            {
                (bar1, 1.00f, 0.0f, config.LimitBreakGlowColor),
                (bar2, 1.60f, 3.7f, config.LimitBreakGlowColor2),
                (bar3, 0.65f, 7.1f, config.LimitBreakGlowColor3),
            };
            foreach (var (bar, tMul, tOff, lbColor) in lbLayers)
            {
                if (bar <= 0f) continue;
                float t    = glowT * tMul + tOff;
                float segW = bw * 0.5f * bar;
                uint  col  = C(lbColor);
                float i    = Intensity(t);
                DrawBorderGlowBracket(dl, bx, by, bw, bh, segW, col, i, t, lbWipeProgress, bar, fromLeft: true);
                DrawBorderGlowBracket(dl, bx, by, bw, bh, segW, col, i, t, lbWipeProgress, bar, fromLeft: false);
            }
        }

        // ── 4. Clip to bar ────────────────────────────────────────────────────
        dl.PushClipRect(V(bx + 1f, by), V(bx + bw - 1f, by + bh), true);

        // Push Jupiter before the tick loop — tick-height clamp needs Jupiter's real metrics.
        // Push() returns null if not yet built; null = no push/pop, default font used.
        using var jupiterScope = jupiterFont.Available ? jupiterFont.Push() : null;

        float fontSize = ImGui.GetFontSize() * config.FontScale;
        var   font     = ImGui.GetFont();

        float labelTop    = by + bh * 0.12f;
        float labelHeight = ImGui.CalcTextSize("N").Y * config.FontScale;
        float labelBottom = labelTop + labelHeight;

        const float tickLabelGap = 0f;
        float maxTickHeight = MathF.Max(2f, (by + bh - 1f) - (labelBottom + tickLabelGap));

        // ── 5. Tick marks ─────────────────────────────────────────────────────
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
            th = MathF.Min(th, maxTickHeight);

            float lensA    = LensEdgeAlpha(delta, halfVis, extHalf);
            uint  tickDraw = WithAlpha(is90 ? cardCol : tickCol, lensA);
            dl.AddLine(V(sx, by + bh - th - 1f), V(sx, by + bh - 1f), tickDraw, is90 ? 2f : 1f);
        }

        // ── 6. Direction labels ───────────────────────────────────────────────
        foreach (var (deg, label, isMajor) in Directions)
        {
            float delta = Delta(heading, deg);
            if (MathF.Abs(delta) > extHalf + 10f) continue;

            float sx  = cx + Project(delta, halfVis, barHalfW, lensStr);
            var   tsz = ImGui.CalcTextSize(label) * config.FontScale;
            float tx  = sx - tsz.X * 0.5f;

            // Labels start fading earlier than ticks (compressed text is hard to read).
            float lensA     = LensEdgeAlpha(delta, halfVis * 0.88f, extHalf);
            uint  labelCol  = WithAlpha(isMajor ? cardCol : ixCol, lensA);
            uint  shadowCol = WithAlpha(0xBB000000u, lensA);

            dl.AddText(font, fontSize, V(tx + 1f, labelTop + 1f), shadowCol, label);
            dl.AddText(font, fontSize, V(tx,       labelTop),      labelCol,  label);
        }
        // jupiterScope disposed here → Jupiter automatically popped

        // ── 7. Markers + FATEs (single sorted pass) ───────────────────────────
        RenderAllMarkers(dl, cx, cy, halfVis, barHalfW, lensStr, heading, player, originPos);

        dl.PopClipRect();

        // ── 8. End-cap fills — opaque so they mask ticks/dots at the edges ────
        dl.AddQuadFilled(V(bx,      cy - capHH), V(bx + capHW,      cy), V(bx,      cy + capHH), V(bx - capHW,      cy), solidBgCol);
        dl.AddQuadFilled(V(bx + bw, cy - capHH), V(bx + bw + capHW, cy), V(bx + bw, cy + capHH), V(bx + bw - capHW, cy), solidBgCol);

        // ── 9. End-cap outlines ───────────────────────────────────────────────
        DrawEndCapOutlines(dl, bx,      cy, capHW, capHH, borderCol);
        DrawEndCapOutlines(dl, bx + bw, cy, capHW, capHH, borderCol);

        // ── 10. Centre notch ──────────────────────────────────────────────────
        const float nH = 10f, nW = 6f;
        dl.AddTriangleFilled(V(cx + 1f, by + nH + 2f), V(cx - nW + 1f, by + 1f), V(cx + nW + 1f, by + 1f), 0x55000000u);
        dl.AddTriangleFilled(V(cx,      by + nH + 1f), V(cx - nW,       by),      V(cx + nW,       by),      0xF2FFFFFFu);

        // ── 11. Numeric heading ───────────────────────────────────────────────
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

        uint  innerCol = (color & 0x00FFFFFFu) | (((color >> 24) * 6 / 10) << 24);
        float s        = 0.52f;
        dl.AddQuad(V(cx, cy - hh * s), V(cx + hw * s, cy), V(cx, cy + hh * s), V(cx - hw * s, cy), innerCol, 1f);

        dl.AddCircleFilled(V(cx, cy), 2.5f, color);
    }

    // ── Limit break glow helpers ──────────────────────────────────────────────

    // Sine-wave rippling ribbon along a segment.
    // Wave is anchored flat at the corner end (u=0) and ramps to chaotic at the tip (u=1).
    // fromLeft mirrors flow direction so both sides drift toward the bar's centre.
    // tipFadeStart dissolves the leading edge, closing fully opaque as the bar charges.
    // wipeProgress layers a separate centre→edge fade for the LB-used animation.
    private static void DrawGlowLine(
        ImDrawListPtr dl, Vector2 a, Vector2 b, uint col,
        float intensity, float t, bool fromLeft, float wipeProgress, float fillProgress)
    {
        Vector2 delta = b - a;
        float   len   = delta.Length();
        if (len < 1f) return;

        Vector2 dir  = delta / len;
        Vector2 perp = new(-dir.Y, dir.X);

        const float amplitude         = 4.0f;
        const float waveLenLong       = 130f;
        const float waveLenShort      = 22f;
        const float flowSpeed         = 2.0f;
        const float wipeBandHalfWidth = 0.18f;

        // Fade zone closes to 0 width (fully opaque) as bar reaches 1.0 — solid "ready" cue.
        float tipFadeStart   = Lerp(0.6f, 1.0f, Math.Clamp(fillProgress, 0f, 1f));
        float flowDir        = fromLeft ? -1f : 1f;
        float wipeBandCentre = Lerp(1f + wipeBandHalfWidth, -wipeBandHalfWidth, wipeProgress);

        int samples = Math.Clamp((int)(len / waveLenShort * 4f) + 2, 3, 96);

        // stackalloc avoids per-call heap allocation (called up to 6× per frame).
        Span<Vector2> pts   = stackalloc Vector2[96];
        Span<float>   fades = stackalloc float[96];

        float phase = 0f, prevAlong = 0f;
        for (int i = 0; i < samples; i++)
        {
            float along = len * i / (samples - 1);
            float u     = fromLeft ? along / len : 1f - along / len;

            // Integrate frequency step-by-step so phase stays continuous as freq shortens.
            float freq = Lerp(2f * MathF.PI / waveLenLong, 2f * MathF.PI / waveLenShort, u);
            phase     += freq * (along - prevAlong);
            prevAlong  = along;

            float envelope  = u * u * (3f - 2f * u);
            float timePhase = t * flowSpeed * flowDir;
            float wave      = MathF.Sin(phase + timePhase) * (1f - 0.5f * u)
                            + MathF.Sin(phase * 2.6f + timePhase * 1.5f + 1.3f) * (0.5f * u);

            pts[i] = a + dir * along + perp * (amplitude * envelope * wave);

            float tipFade  = 1f - SmoothStep(u <= tipFadeStart ? 0f
                               : Math.Clamp((u - tipFadeStart) / (1f - tipFadeStart + 1e-4f), 0f, 1f));
            float wipeFade = 1f - SmoothStep(Math.Clamp(
                               (u - (wipeBandCentre - wipeBandHalfWidth)) / (2f * wipeBandHalfWidth), 0f, 1f));
            fades[i] = tipFade * wipeFade;
        }

        ReadOnlySpan<(float alpha, float thickness)> layers =
        [
            (0.05f, 14f),
            (0.10f, 10f),
            (0.18f,  6f),
            (0.32f,  3.5f),
            (0.70f,  1.8f),
        ];
        foreach (var (alpha, thickness) in layers)
        {
            for (int i = 0; i < samples - 1; i++)
            {
                float segFade = (fades[i] + fades[i + 1]) * 0.5f;
                if (segFade <= 0.002f) continue;
                dl.AddLine(pts[i], pts[i + 1], WithAlpha(col, alpha * intensity * segFade), thickness);
            }
        }
    }

    // Top + bottom edge segments, segW wide from one end (fromLeft selects which).
    // Two calls (true/false) trace both sides of the bar when segW = bw/2.
    private static void DrawBorderGlowBracket(
        ImDrawListPtr dl, float bx, float by, float bw, float bh,
        float segW, uint col, float intensity, float t,
        float wipeProgress, float fillProgress, bool fromLeft)
    {
        float x0 = fromLeft ? bx : bx + bw - segW;
        float x1 = fromLeft ? bx + segW : bx + bw;
        DrawGlowLine(dl, V(x0, by),      V(x1, by),      col, intensity, t, fromLeft, wipeProgress, fillProgress);
        DrawGlowLine(dl, V(x0, by + bh), V(x1, by + bh), col, intensity, t, fromLeft, wipeProgress, fillProgress);
    }

    // Returns LB progress as 0.0–3.0 (integer = bars full, fraction = next bar's progress).
    private static unsafe float GetLimitBreakProgress()
    {
        var uiState = UIState.Instance();
        if (uiState == null) return 0f;
        var lb = uiState->LimitBreakController;
        return lb.BarUnits <= 0 ? 0f : Math.Clamp((float)lb.CurrentUnits / lb.BarUnits, 0f, 3f);
    }

    // Feeds raw LB progress through fade-out logic. On a sudden big drop (gauge reset),
    // freezes display at lbFrozenProgress and sweeps wipeProgress 0→1 over LbFadeOutDuration.
    // Returns the progress value driving bar1/2/3 geometry this frame.
    private float UpdateLimitBreakDisplay(float realProgress, float now, out float wipeProgress)
    {
        if (lbFadeOutStartTime < 0f)
        {
            if (realProgress < lbTrackedProgress - LbDropThreshold)
            {
                lbFrozenProgress   = lbTrackedProgress;
                lbFadeOutStartTime = now;
            }
            else
            {
                lbTrackedProgress = realProgress;
            }
        }

        if (lbFadeOutStartTime >= 0f)
        {
            float elapsed = now - lbFadeOutStartTime;
            // Resync if progress climbed back above the frozen snapshot, or wipe has finished.
            if (realProgress > lbFrozenProgress || elapsed >= LbFadeOutDuration)
            {
                lbFadeOutStartTime = -1f;
                lbTrackedProgress  = realProgress;
                wipeProgress       = 0f;
                return lbTrackedProgress;
            }
            wipeProgress = elapsed / LbFadeOutDuration;
            return lbFrozenProgress;
        }

        wipeProgress = 0f;
        return lbTrackedProgress;
    }

    // ── Unified marker + FATE render ──────────────────────────────────────────

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

        if (config.ShowAnyMarkers)
        {
            foreach (var obj in objectTable)
            {
                if (obj == null || obj.EntityId == player.EntityId) continue;
                uint col = MarkerColor(obj, player);
                if (col == 0) continue;
                if (!TryComputeBearing(obj.Position, pp, heading, maxDistSq, extHalf,
                                       out float dist, out float delta)) continue;
                allCandidates.Add((obj, null, dist, delta, 1f - dist / maxDist, col));
            }
        }

        if (config.ShowFates)
        {
            foreach (var fate in fateTable)
            {
                if (fate == null) continue;
                if (fate.State != FateState.Running && fate.State != FateState.Preparing) continue;
                if (!TryComputeBearing(fate.Position, pp, heading, fateMaxDistSq, extHalf,
                                       out float dist, out float delta)) continue;
                allCandidates.Add((null, fate, dist, delta, 1f - dist / fateMaxDist, 0u));
            }
        }

        if (allCandidates.Count == 0) return;

        allCandidates.Sort(DistFarFirst);

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

            float r = 3f + 7f * t;  // fallback dot radius (used by Gathering else-branch)

            int   iconId   = 0;
            float iconSize = 0f;

            bool  isAetheryteKind = ClassifyAetheryte(obj) != AetheryteNameKind.None;
            bool  isTransportKind = obj.ObjectKind == ObjectKind.EventNpc && FindTransportEntry(obj.BaseId) is not null;
            float npcIconSize     = Lerp(config.NpcQuestIconMinSize, config.NpcQuestIconMaxSize, t) * IconSizeMultiplier;

            if (config.ShowAetheryteIcons && isAetheryteKind)
            {
                iconId   = GetAetheryteIconId(obj);
                iconSize = Lerp(config.AetheryteIconMinSize, config.AetheryteIconMaxSize, t) * AetheryteIconSizeMultiplier;
            }
            else if (config.ShowTransportIcons && isTransportKind && config.TransportIconId > 0)
            {
                // Curated/explicit identity takes priority over heuristic signals below
                // (quest marker, title keyword) — a ferry NPC should look the same
                // regardless of whether it momentarily also has a daily quest marker lit.
                iconId   = config.TransportIconId;
                iconSize = Lerp(config.TransportIconMinSize, config.TransportIconMaxSize, t);
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
                && IsMenderNpc(obj))
            {
                iconId   = config.MenderIconId;
                iconSize = npcIconSize;
            }
            else if (config.ShowShopIcons
                && obj.ObjectKind == ObjectKind.EventNpc
                && IsShopNpc(obj))
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
                iconId   = config.TreasureIconId;
                iconSize = Lerp(config.TreasureMinSize, config.TreasureMaxSize, t);
            }

            bool drewIcon = iconId > 0 && TryDrawIcon(dl, iconId, sx, cy, iconSize, alpha);

            if (!drewIcon)
            {
                if (obj.ObjectKind == ObjectKind.Pc)
                {
                    float playerSize  = Lerp(config.PartyRoleIconMinSize, config.PartyRoleIconMaxSize, t);
                    bool  drewJobIcon = false;

                    if (config.ShowPartyRoleIcons && obj is ICharacter partyChar
                        && (partyChar.StatusFlags & StatusFlags.PartyMember) != 0)
                    {
                        int jobIconId = partyChar.ClassJob.RowId > 0 ? (int)(62000 + partyChar.ClassJob.RowId) : 0;
                        if (jobIconId > 0)
                        {
                            float iconDrawSize = playerSize * IconSizeMultiplier;
                            float iconHalf     = iconDrawSize * 0.5f;
                            uint  roleCol      = GetRoleColor(partyChar);
                            PushUnclip(dl);
                            DrawOuterRing(dl, sx, cy, iconHalf, roleCol, alpha);
                            DrawInwardShadow(dl, sx, cy, iconHalf, roleCol, alpha);
                            PopUnclip(dl);
                            TryDrawIcon(dl, jobIconId, sx, cy, iconDrawSize, alpha);
                            drewJobIcon = true;
                        }
                    }

                    if (!drewJobIcon)
                    {
                        PlayerIconOverride? nameOverride = null;
                        if (config.PlayerIconOverrides.Count > 0)
                        {
                            var objName = obj.Name.TextValue;
                            foreach (var ov in config.PlayerIconOverrides)
                            {
                                if (ov.PlayerName.Length > 0
                                    && string.Equals(ov.PlayerName, objName, StringComparison.OrdinalIgnoreCase))
                                {
                                    nameOverride = ov;
                                    break;
                                }
                            }
                        }

                        if (nameOverride is not null)
                        {
                            float overrideSize = playerSize * IconSizeMultiplier;
                            float overrideHalf = overrideSize * 0.5f;

                            if (nameOverride.ShowFill || nameOverride.ShowBorder)
                            {
                                PushUnclip(dl);
                                if (nameOverride.ShowFill)
                                    DrawInwardShadow(dl, sx, cy, overrideHalf, C(nameOverride.FillColor), alpha);
                                if (nameOverride.ShowBorder)
                                    DrawOuterRing(dl, sx, cy, overrideHalf, C(nameOverride.BorderColor), alpha);
                                PopUnclip(dl);
                            }

                            bool drewOverrideIcon = nameOverride.IconBaseId > 0
                                && TryDrawIcon(dl, nameOverride.IconBaseId, sx, cy, overrideSize,
                                               alpha, nameOverride.ClipToCircle, nameOverride.SizeMultiplier);

                            if (!drewOverrideIcon)
                            {
                                uint fallbackCol = nameOverride.ShowBorder ? C(nameOverride.BorderColor) : col;
                                DrawFilledDot(dl, sx, cy, playerSize, fallbackCol, alpha);
                            }
                        }
                        else
                        {
                            bool isFriend = config.SolidFriendDots
                                && obj is ICharacter ch
                                && (ch.StatusFlags & StatusFlags.Friend) != 0;

                            if (isFriend) DrawFilledDot(dl, sx, cy, playerSize, col, alpha);
                            else          DrawHollowDot(dl, sx, cy, playerSize, col, alpha);
                        }
                    }
                }
                else if (obj.ObjectKind == ObjectKind.EventNpc && !isAetheryteKind && !isTransportKind)
                {
                    // Excludes aetheryte-classified (Firmament crystals) and curated transport
                    // NPCs — both handled below with their own filled-dot styling.
                    DrawHollowDot(dl, sx, cy,
                        Lerp(config.NpcQuestIconMinSize, config.NpcQuestIconMaxSize, t), col, alpha);
                }
                else if (obj.ObjectKind == ObjectKind.BattleNpc)
                {
                    DrawFilledDot(dl, sx, cy, Lerp(config.EnemyMinSize, config.EnemyMaxSize, t), col, alpha);
                }
                else if (isAetheryteKind)
                {
                    DrawFilledDot(dl, sx, cy,
                        Lerp(config.AetheryteIconMinSize, config.AetheryteIconMaxSize, t), col, alpha);
                }
                else if (isTransportKind)
                {
                    // Filled dot (not hollow) — same "this is a POI with a function" language as Aetheryte.
                    DrawFilledDot(dl, sx, cy,
                        Lerp(config.TransportIconMinSize, config.TransportIconMaxSize, t), col, alpha);
                }
                else if (obj.ObjectKind == ObjectKind.Treasure)
                {
                    DrawFilledDot(dl, sx, cy, Lerp(config.TreasureMinSize, config.TreasureMaxSize, t), col, alpha);
                }
                else
                {
                    DrawFilledDot(dl, sx, cy, r * 2f, col, alpha);
                }
            }
        }
    }

    // Three-zone distance fade: opaque inside DotNearZone, smoothstep to DotMidAlpha in the
    // middle band, smoothstep to 0 below DotFarZone. t=1 at zero distance, 0 at max range.
    private float ComputeFadeAlpha(float t)
    {
        float nearZone = config.DotNearZone;
        float midEnd   = config.DotFarZone;
        float midAlpha = config.DotMidAlpha;

        if (t >= nearZone) return 1f;
        if (t >= midEnd)
            return midAlpha + (1f - midAlpha) * SmoothStep((t - midEnd) / (nearZone - midEnd));
        return midAlpha * SmoothStep(t / midEnd);
    }

    // Draws a game icon centred at (sx, cy). Returns false if texture not yet loaded.
    // clipToCircle=true: quad stays at `size`, uvZoom crops the texture (fits a border ring).
    // clipToCircle=false: uvZoom scales the quad itself. uvZoom=1.0 → no zoom either way.
    private bool TryDrawIcon(
        ImDrawListPtr dl, int iconId, float sx, float cy, float size, float alpha,
        bool clipToCircle = false, float uvZoom = 1.0f)
    {
        if (!textureProvider.TryGetFromGameIcon(new GameIconLookup((uint)iconId), out var sharedTex))
            return false;

        var  tex  = sharedTex.GetWrapOrEmpty();
        uint tint = WithAlpha(0xFFFFFFFFu, alpha);

        float   half;
        Vector2 uvMin, uvMax;

        if (clipToCircle)
        {
            half         = size * 0.5f;
            float uvHalf = 0.5f / Math.Max(0.01f, uvZoom);
            uvMin = new(0.5f - uvHalf, 0.5f - uvHalf);
            uvMax = new(0.5f + uvHalf, 0.5f + uvHalf);
        }
        else
        {
            half  = size * 0.5f * Math.Max(0.01f, uvZoom);
            uvMin = new(0f, 0f);
            uvMax = new(1f, 1f);
        }

        PushUnclip(dl);
        dl.AddImageRounded(
            tex.Handle,
            V(sx - half, cy - half),
            V(sx + half, cy + half),
            uvMin, uvMax, tint,
            clipToCircle ? half : 0f,
            ImDrawFlags.RoundCornersAll);
        PopUnclip(dl);
        return true;
    }

    // GatheringPoint(BaseId) → GatheringPointBase → GatheringType → IconMain.
    // Cached permanently per BaseId; returns 0 if any link in the chain doesn't resolve.
    private int GetGatheringIconId(uint baseId)
    {
        if (gatheringIconCache.TryGetValue(baseId, out int cached)) return cached;

        int icon = 0;
        if (gatheringPointSheet.GetRowOrDefault(baseId) is { } gp
            && gatheringPointBaseSheet.GetRowOrDefault(gp.GatheringPointBase.RowId) is { } gpb
            && gatheringTypeSheet.GetRowOrDefault(gpb.GatheringType.RowId) is { } gt)
            icon = gt.IconMain;

        return gatheringIconCache[baseId] = icon;
    }

    // Uses ClassJob.Role (not a per-job index) so future jobs work automatically.
    // Tank=blue, Healer=green, DPS=red, DoH/DoL=gray — matches FFXIV's role UI.
    private uint GetRoleColor(ICharacter character)
    {
        if (classJobSheet.GetRowOrDefault(character.ClassJob.RowId) is not { } row)
            return C(new Vector4(0.54f, 0.54f, 0.54f, 0.85f));
        return row.Role switch
        {
            1      => C(new Vector4(0.36f, 0.48f, 0.76f, 0.90f)),   // Tank — blue
            2 or 3 => C(new Vector4(0.84f, 0.30f, 0.30f, 0.90f)),   // DPS  — red
            4      => C(new Vector4(0.30f, 0.69f, 0.49f, 0.90f)),   // Healer — green
            _      => C(new Vector4(0.54f, 0.54f, 0.54f, 0.85f)),   // DoH/DoL — gray
        };
    }

    // Reflects over every public property on a Lumina row struct and prints Name=Value
    // for each. Used by /compass debug to inspect raw sheet data directly when a specific
    // field (e.g. Title) isn't behaving as expected, instead of guessing field names blind.
    private static string DumpAllFields<T>(T? row) where T : struct
    {
        if (row is not { } r) return "<no row for this BaseId>";
        var parts = new List<string>();
        foreach (var prop in typeof(T).GetProperties())
        {
            string val;
            try
            {
                var v = prop.GetValue(r);
                val = v?.ToString() ?? "null";
                if (val.Length > 60) val = val[..60] + "…"; // arrays/sub-structs can be huge
            }
            catch (Exception ex) { val = $"<threw {ex.GetType().Name}>"; }
            parts.Add($"{prop.Name}={val}");
        }
        return string.Join(", ", parts);
    }

    /// <summary>Resolves an NPC's English title via ENpcResident, cached permanently per BaseId. "" if none.</summary>
    private string GetNpcTitle(uint baseId)
    {
        if (npcTitleCache.TryGetValue(baseId, out string? cached)) return cached;
        string title = npcResidentSheet.GetRowOrDefault(baseId) is { } row ? row.Title.ToString() : "";
        return npcTitleCache[baseId] = title;
    }

    /// <summary>
    /// Resolves an NPC's English "Singular" text via ENpcResident, cached permanently per
    /// BaseId. "" if none. This is where generic flavor NPCs with no personal name (empty
    /// Title) carry their vocation word instead — see npcSingularCache above.
    /// </summary>
    private string GetNpcSingular(uint baseId)
    {
        if (npcSingularCache.TryGetValue(baseId, out string? cached)) return cached;
        string singular = npcResidentSheet.GetRowOrDefault(baseId) is { } row ? row.Singular.ToString() : "";
        return npcSingularCache[baseId] = singular;
    }

    private static bool TitleContainsAny(string title, string[] keywords)
    {
        if (string.IsNullOrEmpty(title)) return false;
        foreach (var kw in keywords)
            if (title.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    // Language-independent: title/singular come from the English-forced npcResidentSheet,
    // which doesn't depend on client language.
    private bool IsMenderNpc(IGameObject obj) =>
        TitleContainsAny(GetNpcTitle(obj.BaseId), MenderTitleKeywords) ||
        TitleContainsAny(GetNpcSingular(obj.BaseId), MenderTitleKeywords);

    private bool IsShopNpc(IGameObject obj) =>
        TitleContainsAny(GetNpcTitle(obj.BaseId), ShopTitleKeywords) ||
        TitleContainsAny(GetNpcSingular(obj.BaseId), ShopTitleKeywords);

    private enum AetheryteNameKind { None, Big, Shard }

    // ObjectKind.Aetheryte → always Big or Shard (Shard if name matches AethernetShardName).
    // EventNpc/EventObj → Shard only on match; None otherwise.
    // Single source of truth for both icon selection and visibility.
    private AetheryteNameKind ClassifyAetheryte(IGameObject obj)
    {
        bool looksLikeShard = !string.IsNullOrEmpty(config.AethernetShardName)
            && obj.Name.TextValue.Contains(config.AethernetShardName, StringComparison.OrdinalIgnoreCase);

        if (obj.ObjectKind == ObjectKind.Aetheryte)
            return looksLikeShard ? AetheryteNameKind.Shard : AetheryteNameKind.Big;

        return looksLikeShard ? AetheryteNameKind.Shard : AetheryteNameKind.None;
    }

    private int GetAetheryteIconId(IGameObject obj) =>
        ClassifyAetheryte(obj) == AetheryteNameKind.Shard
            ? config.AethernetShardIconId
            : config.AetheryteIconId;

    // Returns true if obj is any aetheryte kind. color=0 if hidden by config.
    private bool TryGetAetheryteMarkerColor(IGameObject obj, out uint color)
    {
        var kind = ClassifyAetheryte(obj);
        if (kind == AetheryteNameKind.None) { color = 0u; return false; }
        bool hidden = !config.ShowAetherytes
            || (kind == AetheryteNameKind.Shard && !config.ShowAethernetShards);
        color = hidden ? 0u : C(config.AetheryteColor);
        return true;
    }

    // Linear scan — list is small (curated by hand), same cost class as PlayerIconOverrides
    // lookup. Shared helper kept generic since Transport (the only remaining curated-BaseId
    // list — it has no automatic detection at all) reuses CuratedNpcEntry/DrawCuratedNpcList.
    private static CuratedNpcEntry? FindCuratedEntry(List<CuratedNpcEntry> list, uint baseId)
    {
        foreach (var e in list)
            if (e.BaseId == baseId) return e;
        return null;
    }

    private CuratedNpcEntry? FindTransportEntry(uint baseId) => FindCuratedEntry(config.TransportNpcs, baseId);

    // Returns true if obj's BaseId is a curated transport NPC. color=0 if hidden by config.
    // Mirrors TryGetAetheryteMarkerColor's "classify regardless of visibility" shape.
    private bool TryGetTransportMarkerColor(IGameObject obj, out uint color)
    {
        if (FindTransportEntry(obj.BaseId) is null) { color = 0u; return false; }
        color = config.ShowTransportNpcs ? C(config.TransportColor) : 0u;
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
                if (obj is not IBattleNpc bnpc || bnpc.BattleNpcKind != BattleNpcSubKind.Combatant) return 0u;
                // GameObjectId (ulong) and EntityId (uint) are distinct ID spaces; TargetObjectId is ulong.
                if (config.EnemiesOnlyIfEngaged
                    && obj.TargetObjectId != player.GameObjectId
                    && targetManager.Target?.GameObjectId != obj.GameObjectId)
                    return 0u;
                return C(config.EnemyColor);

            case ObjectKind.EventNpc:
                // Firmament crystals are EventNpcs — route through aetheryte path, not NPC color.
                if (TryGetAetheryteMarkerColor(obj, out uint eventNpcAetherCol)) return eventNpcAetherCol;
                // Curated transport NPCs (ferries, etc) — independent of ShowNpcs, same reasoning
                // as Aetherytes above: these take you somewhere, not generic background flavor.
                if (TryGetTransportMarkerColor(obj, out uint eventNpcTransportCol)) return eventNpcTransportCol;
                if (!config.ShowNpcs) return 0u;
                if (config.NpcsOnlyIfTargetable && !obj.IsTargetable) return 0u;
                return C(config.NpcColor);

            case ObjectKind.EventObj:
                // Housing-ward Aethernet shards are EventObj (not EventNpc).
                return TryGetAetheryteMarkerColor(obj, out uint eventObjAetherCol)
                    ? eventObjAetherCol : 0u;

            case ObjectKind.GatheringPoint:
                if (!config.ShowGatheringNodes) return 0u;
                if (config.GatheringOnlyIfTargetable && !obj.IsTargetable) return 0u;
                return C(config.GatheringColor);

            case ObjectKind.Treasure:
                return config.ShowTreasure ? C(config.TreasureColor) : 0u;

            case ObjectKind.Aetheryte:
                TryGetAetheryteMarkerColor(obj, out uint realAetherCol); // always Big/Shard, never None
                return realAetherCol;

            default:
                return 0u;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static float SmoothStep(float x) => x * x * (3f - 2f * x);

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

    // 3D distance for range/fade; 2D bearing (no Y) so height doesn't shift dots sideways.
    // Returns false if out of range or outside the visible FOV.
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

    /// <summary>t=1 → max, t=0 → min.</summary>
    private static float Lerp(float min, float max, float t) => min + (max - min) * t;

    private static void DrawFilledDot(ImDrawListPtr dl, float sx, float cy, float size, uint col, float alpha)
    {
        float r = size * 0.5f;
        dl.AddCircleFilled(V(sx, cy), r,        WithAlpha(col,        alpha));
        dl.AddCircle(      V(sx, cy), r + 0.8f, WithAlpha(0x66000000u, alpha));
    }

    private static void DrawHollowDot(ImDrawListPtr dl, float sx, float cy, float size, uint col, float alpha)
    {
        float r = size * 0.5f;
        dl.AddCircle(V(sx, cy), r,        WithAlpha(col,        alpha), 0, 2.0f);
        dl.AddCircle(V(sx, cy), r + 0.8f, WithAlpha(0x33000000u, alpha));
    }

    // 3 inward-fading circles faking a soft shadow behind an icon (role icon / override fill).
    private static void DrawInwardShadow(ImDrawListPtr dl, float sx, float cy, float half, uint col, float alpha)
    {
        dl.AddCircleFilled(V(sx, cy), half * 0.85f, WithAlpha(col, alpha * 0.6f));
        dl.AddCircleFilled(V(sx, cy), half * 0.65f, WithAlpha(col, alpha * 0.4f));
        dl.AddCircleFilled(V(sx, cy), half * 0.45f, WithAlpha(col, alpha * 0.2f));
    }

    // Solid ring just outside an icon's bounding box (role icon / override border).
    private static void DrawOuterRing(ImDrawListPtr dl, float sx, float cy, float half, uint col, float alpha) =>
        dl.AddCircle(V(sx, cy), half + 1.0f, WithAlpha(col, alpha), 0, 3.0f);

    // 1.0 inside linearHalf, smoothsteps to 0 at extHalf. linearHalf lets labels fade earlier than ticks.
    private static float LensEdgeAlpha(float delta, float linearHalf, float extHalf)
    {
        float absD = MathF.Abs(delta);
        if (absD <= linearHalf) return 1f;
        return 1f - SmoothStep(MathF.Min(1f, (absD - linearHalf) / (extHalf - linearHalf)));
    }

    private static uint WithAlpha(uint color, float mul)
    {
        uint newA = (uint)(((color >> 24) & 0xFFu) * Math.Clamp(mul, 0f, 1f));
        return (color & 0x00FFFFFFu) | (newA << 24);
    }

    // Temporarily overrides bar-sized clip so icons/rings can render past the bar edge.
    // Icons and their rings must escape together or they visually disagree at the edge.
    private static void PushUnclip(ImDrawListPtr dl) =>
        dl.PushClipRect(Vector2.Zero, ImGui.GetIO().DisplaySize, false);

    private static void PopUnclip(ImDrawListPtr dl) => dl.PopClipRect();

    /// <summary>Logs nearby objects for diagnostics. View via /xllog.</summary>
    public void DumpNearbyObjects(float radius = 50f)
    {
        var player = objectTable.LocalPlayer;
        if (player == null)
        {
            log.Info("[SkyrimCompass debug] No local player — are you logged in?");
            return;
        }

        var pp     = player.Position;
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
            string fieldDumpEn = "", fieldDumpLocal = "";
            if (obj.ObjectKind == ObjectKind.EventNpc)
            {
                string title        = GetNpcTitle(obj.BaseId);
                string singular     = GetNpcSingular(obj.BaseId);
                bool   hasQuestIcon = npcMarkerIcons.TryGetValue(obj.GameObjectId, out int qIconId) && qIconId > 0;
                bool   isMender     = IsMenderNpc(obj);
                bool   isShop       = IsShopNpc(obj);
                var    transport    = FindTransportEntry(obj.BaseId);
                string winner       = transport != null ? $"Transport({transport.Label})"
                                    : hasQuestIcon ? $"QuestMarker(icon={qIconId})"
                                    : isMender     ? "Mender"
                                    : isShop       ? "Shop"
                                    : "none/dot";
                // TitleEN/SingularEN are always English regardless of client language — both
                // are what MenderTitleKeywords/ShopTitleKeywords matching actually runs
                // against (named NPCs carry their vocation in Title; generic flavor NPCs with
                // no personal name carry it in Singular instead, with Title empty). If the
                // word you expect is in one of these but IsMender/IsShop is still false, that
                // word is missing from the keyword list above.
                extra = $" | TitleEN=\"{title}\" | SingularEN=\"{singular}\" | QuestIcon={hasQuestIcon,-5} | " +
                        $"IsMender={isMender,-5} | IsShop={isShop,-5} | IsTransport={(transport != null),-5} | WouldShow={winner}";

                // Raw ENpcResident dump, both language variants, so we can tell whether an
                // empty/wrong match is "English-forcing broke the lookup" (dumps would differ
                // in row-found-ness) vs "Title just isn't the field with the vendor label"
                // (both dumps find the row fine, but Title is blank in both).
                fieldDumpEn    = DumpAllFields(npcResidentSheet.GetRowOrDefault(obj.BaseId));
                fieldDumpLocal = DumpAllFields(npcResidentSheetClientLang.GetRowOrDefault(obj.BaseId));
            }
            else if (obj.ObjectKind == ObjectKind.Treasure)
            {
                extra = $" | WouldShow={(config.ShowTreasureIcons ? $"Icon({config.TreasureIconId})" : "dot")}";
            }

            log.Info(
                $"[SkyrimCompass debug] {dist,6:F1}y | Kind={obj.ObjectKind,-19} | " +
                $"BaseId={obj.BaseId,-8} | Targetable={obj.IsTargetable,-5} | " +
                $"Name=\"{obj.Name.TextValue}\"{extra}");
            if (obj.ObjectKind == ObjectKind.EventNpc)
            {
                log.Info($"    ENpcResident[EN,forced]  {fieldDumpEn}");
                log.Info($"    ENpcResident[client-lang] {fieldDumpLocal}");
            }
        }
        log.Info("[SkyrimCompass debug] Done. Use /xllog in-game to view the log window.");
    }
}
