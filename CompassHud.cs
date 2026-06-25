using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Gui.NamePlate;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace SkyrimCompass;

/// <summary>
/// Renders a Skyrim-style compass bar onto the ImGui foreground draw list.
/// Uses a fisheye/lens projection so the centre looks normal while the edges
/// compress more degrees into each pixel, revealing a wider FOV without clutter.
/// </summary>
public sealed class CompassHud : IDisposable
{
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly ITargetManager targetManager;
    private readonly INamePlateGui namePlateGui;
    private readonly ITextureProvider textureProvider;
    private readonly IFateTable fateTable;
    private readonly Configuration config;
    private readonly IPluginLog log;

    // GameObjectId -> current nameplate marker icon ID (MSQ/side quest/etc.), refreshed
    // every time the game updates nameplate data. 0/absent = no active marker.
    private readonly Dictionary<ulong, int> npcMarkerIcons = new();

    // BaseId -> resolved Mining/Botany (etc.) icon ID. Unlike npcMarkerIcons this never
    // needs refreshing — a gathering node's type is static game data, not state that
    // changes during play — so it's cached permanently once resolved.
    private readonly Dictionary<uint, int> gatheringIconCache = new();
    private readonly ExcelSheet<GatheringPoint> gatheringPointSheet;
    private readonly ExcelSheet<GatheringPointBase> gatheringPointBaseSheet;
    private readonly ExcelSheet<GatheringType> gatheringTypeSheet;

    // BaseId -> NPC title text (e.g. "Merchant & Mender"), cached permanently — static
    // game data. Shared by every title-keyword icon check (Mender, Shop, and whatever
    // gets added next) rather than one bool cache per category.
    private readonly Dictionary<uint, string> npcTitleCache = new();
    private readonly ExcelSheet<ENpcResident> npcResidentSheet;
    private readonly ExcelSheet<ClassJob>     classJobSheet;

    // Curated keyword sets, each confirmed against real frequency in the actual
    // ENpcResident sheet data before being added (not guessed).
    private static readonly string[] MenderTitleKeywords = { "Mender" };

    // Reused every frame by RenderMarkers/RenderFates to sort markers by distance
    // before drawing, so closer markers paint on top of farther ones instead of in
    // whatever order objectTable/fateTable happen to enumerate. Fields (not locals)
    // purely to avoid re-allocating a List every frame.
    private readonly List<MarkerCandidate> markerCandidates = new();
    private readonly List<FateCandidate>   fateCandidates   = new();

    /// <summary>
    /// A marker that's already passed every RenderMarkers filter (range, color, FOV),
    /// carrying just enough precomputed data that the draw pass doesn't need to redo
    /// any of that work once the list is sorted by distance.
    /// </summary>
    private readonly struct MarkerCandidate
    {
        public readonly IGameObject Obj;
        public readonly float       Dist;
        public readonly float       Delta;
        public readonly uint        Col;

        public MarkerCandidate(IGameObject obj, float dist, float delta, uint col)
        {
            Obj = obj; Dist = dist; Delta = delta; Col = col;
        }
    }

    /// <summary>Same idea as <see cref="MarkerCandidate"/>, for RenderFates.</summary>
    private readonly struct FateCandidate
    {
        public readonly IFate Fate;
        public readonly float Dist;
        public readonly float Delta;

        public FateCandidate(IFate fate, float dist, float delta)
        {
            Fate = fate; Dist = dist; Delta = delta;
        }
    }

    /// <summary>
    /// Multiplier applied to NPC marker icons (quest/Mender/Shop) and the player party
    /// role/job icon, on top of their normal size formula. Most of these game icon
    /// textures have a fair amount of transparent padding baked in around the actual
    /// glyph, so drawing them at the same bounding-box size as a plain dot makes the
    /// visible icon art look noticeably smaller than the dot. A multiplier (rather than
    /// a flat px offset) keeps that compensation proportional at every distance — a flat
    /// offset would dominate the size at long range, where the base size shrinks toward
    /// zero but the offset doesn't, so small/far icons stopped shrinking properly and
    /// effectively hit a minimum size floor. Deliberately NOT applied to Gathering icons,
    /// which weren't reported as undersized. Aetheryte icons get their own larger
    /// multiplier below instead, since their texture padding runs heavier than the rest.
    /// </summary>
    private const float IconSizeMultiplier = 1.5f;
    /// <summary>
    /// Same idea as <see cref="IconSizeMultiplier"/>, but specifically for aetheryte
    /// icons (both the Big and Aethernet Shard variants) — their source textures carry
    /// noticeably more internal padding than the other icon sets, so the shared 1.4x
    /// wasn't enough to bring them up to the equivalent dot size. Only applied to the
    /// drawn icon itself, not the plain-dot fallback (see RenderMarkers), so the dot
    /// stays at the true reference size for whenever icons are off.
    /// </summary>
    private const float AetheryteIconSizeMultiplier = 1.75f;
    private static readonly string[] ShopTitleKeywords   =
    {
        // Original confirmed set
        "Merchant", "Vendor", "Trader",
        // Confirmed via real /compass debug output + frequency check against ENpcResident
        "Sutler",       // 12 matches, all camp/field vendors — zero false positives
        "Supplier",     // 133 matches, all genuine supply vendors — zero false positives
        // "Monger" alone catches rumormonger/gossipmonger/winemonger (flavor NPCs, not shops)
        // so we use the specific vendor sub-types confirmed against real data instead:
        "Junkmonger", "Fishmonger", "Dyemonger",
        // Further confirmed via /compass debug + full sheet sample — zero false positives each:
        "Jeweler",      // 8 matches (Independent Jeweler, Jeweler, traveling jeweler)
        "Apothecary",   // 19 matches (Apothecary, Independent Apothecary, various X apothecary)
        "Culinarian",   // 16 matches (Culinarian, Independent Culinarian, various X culinarian)
        // NOT added (checked and rejected): Alchemist (52 matches, almost all quest/ambient lore
        // NPCs not vendors), Carpenter (all ambient background), Armorer (partially ambiguous)
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
        IDataManager dataManager,
        Configuration config,
        IPluginLog log)
    {
        this.clientState     = clientState;
        this.objectTable     = objectTable;
        this.targetManager   = targetManager;
        this.namePlateGui    = namePlateGui;
        this.textureProvider = textureProvider;
        this.fateTable       = fateTable;
        this.config          = config;
        this.log             = log;

        gatheringPointSheet     = dataManager.GetExcelSheet<GatheringPoint>();
        gatheringPointBaseSheet = dataManager.GetExcelSheet<GatheringPointBase>();
        gatheringTypeSheet      = dataManager.GetExcelSheet<GatheringType>();
        npcResidentSheet        = dataManager.GetExcelSheet<ENpcResident>();
        classJobSheet           = dataManager.GetExcelSheet<ClassJob>();

        // OnDataUpdate (not OnNamePlateUpdate) is the one that fires every frame with
        // ALL current nameplates, not just ones that changed — exactly what we need to
        // keep a complete, accurate icon cache rather than only tracking deltas.
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

        var player = objectTable.LocalPlayer;
        if (player == null) return;

        float headingRad;
        var   originPos = player.Position;   // default: bearings/distances from the character

        if (config.UseCameraDirection)
        {
            // CameraManager->Camera is the normal in-game world camera (FieldOffset 0x00,
            // documented in FFXIVClientStructs as "[0] Camera (normal in-game camera)").
            //
            // The struct's doc comment claims DirH is "0 = north, increasing clockwise"
            // (i.e. already standard compass-bearing direction), but in-game testing
            // showed otherwise: N/S tracked correctly while E/W came out mirrored —
            // the exact signature of DirH actually increasing counter-clockwise relative
            // to true compass bearing. Negating it corrects E/W without touching N/S
            // (negation is a fixed-point identity at 0° and 180°, which is why those two
            // were unaffected by the bug).
            var camManager = CameraManager.Instance();
            var camera     = camManager != null ? camManager->Camera : null;

            if (camera != null && !float.IsNaN(camera->DirH))
            {
                headingRad = -camera->DirH;

                // First-person uses a different DirH convention than third-person —
                // confirmed via in-game testing: entering first person flips the heading
                // by exactly 180° with no other symptom. Likely cause: in third person,
                // DirH appears to encode the camera's ORBITAL position angle around the
                // character, which is 180° opposite the direction it's actually looking
                // (an orbiting camera looks back toward its own pivot). First person has
                // no orbit at all, so DirH becomes a direct view angle instead — a clean
                // 180° shift in meaning. Gating this on ZoomMode keeps third person
                // (already verified correct) completely untouched.
                if (camera->ZoomMode == CameraZoomMode.FirstPerson)
                    headingRad += MathF.PI;

                if (config.UseCameraPosition)
                {
                    // LastPosition sits directly next to LastLookAtVector in the struct —
                    // that adjacent eye/look-at pairing is the standard convention 3D camera
                    // systems use, which is why this is the camera's actual world position
                    // rather than some other camera-related point (e.g. its pivot/target).
                    var camPos = camera->LastPosition;
                    if (!float.IsNaN(camPos.X) && !float.IsNaN(camPos.Y) && !float.IsNaN(camPos.Z))
                        originPos = camPos;
                }
            }
            else if (!float.IsNaN(player.Rotation))
            {
                // Camera pointer unavailable this frame (e.g. very first frames after
                // zoning) — fall back to facing direction rather than freezing the bar.
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
    /// Maps a compass angle offset (<paramref name="delta"/>, in degrees) to a
    /// signed pixel offset from the bar centre.
    ///
    /// The projection uses f(u) = 1 − (1−u)^k, where u = |delta| / extendedHalf
    /// and k = lensStrength.  At the centre the slope equals the original linear
    /// ppd exactly; toward the edges it falls off, compressing more degrees into
    /// fewer pixels and revealing extra FOV without cluttering the centre.
    ///
    /// lensStrength = 1.0 → pure linear (no effect).
    /// </summary>
    private static float Project(float delta, float halfVis, float barHalfW, float lensStr)
    {
        // Extended visible half-range (degrees shown on each side of centre)
        float extHalf = halfVis * lensStr;

        float absD    = MathF.Min(MathF.Abs(delta), extHalf);
        float u       = absD / extHalf;                        // 0 → 1

        // f(u) = 1 − (1−u)^k  →  f(0)=0, f(1)=1, f'(0)=k
        // Scale: dx/dδ|₀ = barHalfW * k / extHalf = barHalfW / halfVis = ppd_original ✓
        float f       = 1f - MathF.Pow(1f - u, lensStr);

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
        // Drawn here (before markers/icons) rather than after them so anything that
        // overlaps it — most notably icons, which can now render past the bar's clip
        // rect (see TryDrawIcon) and are frequently taller than the bar itself — paints
        // over the border instead of the border cutting a line across the icon. Ticks
        // and labels below are unaffected either way since their own clip keeps them
        // 1px inside this border's edge, never actually touching it.
        dl.AddRect(V(bx, by), V(bx + bw, by + bh), borderCol, 0f, ImDrawFlags.None, 1.5f);

        // ── 3. Clip to bar ────────────────────────────────────────────────────
        dl.PushClipRect(V(bx + 1f, by), V(bx + bw - 1f, by + bh), true);

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
        float fontSize = ImGui.GetFontSize() * config.FontScale;
        var   font     = ImGui.GetFont();

        foreach (var (deg, label, isMajor) in Directions)
        {
            float delta = Delta(heading, deg);
            if (MathF.Abs(delta) > extHalf + 10f) continue;

            float sx  = cx + Project(delta, halfVis, barHalfW, lensStr);
            var   tsz = ImGui.CalcTextSize(label) * config.FontScale;
            float tx  = sx - tsz.X * 0.5f;
            float ty  = by + bh * 0.12f;

            // Labels start fading slightly earlier than ticks (compressed text is hard to read)
            float lensA    = LensEdgeAlpha(delta, halfVis * 0.88f, extHalf);
            uint  labelCol = WithAlpha(isMajor ? cardCol : ixCol, lensA);
            uint  shadowCol = WithAlpha(0xBB000000u, lensA);

            dl.AddText(font, fontSize, V(tx + 1f, ty + 1f), shadowCol, label);
            dl.AddText(font, fontSize, V(tx, ty), labelCol, label);
        }

        // ── 6. Entity markers (centred, lens-projected, alpha-faded) ─────────
        if (config.ShowAnyMarkers)
            RenderMarkers(dl, cx, cy, halfVis, barHalfW, lensStr, heading, player, originPos);

        // Deliberately NOT gated behind ShowAnyMarkers — FATEs are independent of every
        // other marker toggle, so this needs to work even with all of them off.
        RenderFates(dl, cx, cy, halfVis, barHalfW, lensStr, heading, originPos);

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

    // ── Entity markers ────────────────────────────────────────────────────────

    private void RenderMarkers(
        ImDrawListPtr dl,
        float cx, float cy,
        float halfVis, float barHalfW, float lensStr,
        float heading, IPlayerCharacter player, Vector3 originPos)
    {
        var   pp        = originPos;   // character position, or camera position if both toggles are on
        float maxDistSq = config.MaxMarkerDistance * config.MaxMarkerDistance;
        float extHalf   = halfVis * lensStr;

        // Pass 1: filter every object down to what actually qualifies for a marker
        // (range, color, FOV — exactly the checks this loop always did), then sort by
        // distance. ImGui paints in draw-call order, so without this the stacking
        // order when two markers overlap on screen was effectively whatever order
        // objectTable happens to enumerate in, not necessarily the closer one on top.
        markerCandidates.Clear();

        foreach (var obj in objectTable)
        {
            // Identity check stays keyed on the character regardless of which origin
            // bearings are measured from — we're still always excluding "yourself".
            if (obj == null) continue;
            if (obj.EntityId == player.EntityId) continue;

            float dx  = obj.Position.X - pp.X;
            float dy  = obj.Position.Y - pp.Y;
            float dz  = obj.Position.Z - pp.Z;
            // Full 3D distance (drives range cutoff, dot size, and fade) so something
            // far above/below you doesn't register as "right next to you" just because
            // it's horizontally close. Bearing below intentionally stays 2D (dx, dz
            // only) — height shouldn't shove a dot sideways on the compass, only affect
            // how prominent it looks.
            float dsq = dx * dx + dy * dy + dz * dz;

            if (dsq > maxDistSq || dsq < 0.25f) continue;

            uint col = MarkerColor(obj, player);
            if (col == 0) continue;

            float bearing = Normalize(MathF.Atan2(dx, -dz) * (180f / MathF.PI));
            float delta   = Delta(heading, bearing);
            if (MathF.Abs(delta) > extHalf) continue;

            markerCandidates.Add(new MarkerCandidate(obj, MathF.Sqrt(dsq), delta, col));
        }

        // Farthest first, closest last — later draw calls paint on top in ImGui, so
        // this puts the nearer marker visually in front whenever two overlap.
        markerCandidates.Sort((a, b) => b.Dist.CompareTo(a.Dist));

        // Pass 2: same per-marker visuals/draw logic the loop always had, just walked
        // in distance-sorted order instead of objectTable's native order.
        foreach (var candidate in markerCandidates)
        {
            var   obj   = candidate.Obj;
            uint  col   = candidate.Col;
            float delta = candidate.Delta;
            float dist  = candidate.Dist;

            // Apply lens projection for screen X
            float sx = cx + Project(delta, halfVis, barHalfW, lensStr);

            float t    = 1f - dist / config.MaxMarkerDistance;   // 1 = close, 0 = at max range
            // Fallback dot radius for kinds that don't have their own page-level size
            // slider (Gathering without icon, Treasure). Players, NPCs, Enemies, and
            // Aetherytes compute their own size below from their page's slider
            // instead, so each slider covers every marker on that page.
            float r    = 3f + 7f * t;

            // Three-zone alpha curve, shared with RenderFates — see ComputeFadeAlpha.
            // Multiplied by the same edge fade ticks/labels use (LensEdgeAlpha) so
            // markers ease out to fully transparent by extHalf instead of hard-cutting
            // off — previously there was no fade here at all, just the hard `continue`
            // above, which was tolerable for small clipped dots but became obvious once
            // icons grew large enough to render past the bar's edge unclipped.
            float alpha = ComputeFadeAlpha(t) * LensEdgeAlpha(delta, halfVis, extHalf);

            // Categories with a real game icon available get that instead of a plain
            // dot — the icon ID and its min/max size range both depend on which
            // category this is, so each is resolved here before the shared draw call.
            int   iconId   = 0;
            float iconSize = 0f;

            bool isAetheryteKind = ClassifyAetheryte(obj) != AetheryteNameKind.None;

            if (config.ShowAetheryteIcons && isAetheryteKind)
            {
                iconId   = GetAetheryteIconId(obj);
                iconSize = (config.AetheryteIconMinSize
                         + (config.AetheryteIconMaxSize - config.AetheryteIconMinSize) * t)
                         * AetheryteIconSizeMultiplier;
            }
            else if (config.ShowNpcQuestIcons
                && obj.ObjectKind == ObjectKind.EventNpc
                && npcMarkerIcons.TryGetValue(obj.GameObjectId, out int npcIcon))
            {
                iconId   = npcIcon;
                iconSize = (config.NpcQuestIconMinSize
                         + (config.NpcQuestIconMaxSize - config.NpcQuestIconMinSize) * t)
                         * IconSizeMultiplier;
            }
            else if (config.ShowMenderIcons
                && obj.ObjectKind == ObjectKind.EventNpc
                && NpcMatchesKeywords(obj, GetNpcTitle(obj.BaseId), MenderTitleKeywords))
            {
                // Shares the quest-marker size range above — one shared "any icon in
                // place of an NPC dot" size knob rather than a separate slider pair.
                iconId   = config.MenderIconId;
                iconSize = (config.NpcQuestIconMinSize
                         + (config.NpcQuestIconMaxSize - config.NpcQuestIconMinSize) * t)
                         * IconSizeMultiplier;
            }
            else if (config.ShowShopIcons
                && obj.ObjectKind == ObjectKind.EventNpc
                && NpcMatchesKeywords(obj, GetNpcTitle(obj.BaseId), ShopTitleKeywords))
            {
                iconId   = config.ShopIconId;
                iconSize = (config.NpcQuestIconMinSize
                         + (config.NpcQuestIconMaxSize - config.NpcQuestIconMinSize) * t)
                         * IconSizeMultiplier;
            }
            else if (config.ShowGatheringIcons && obj.ObjectKind == ObjectKind.GatheringPoint)
            {
                int gatherIcon = GetGatheringIconId(obj.BaseId);
                if (gatherIcon > 0)
                {
                    iconId   = gatherIcon;
                    iconSize = config.GatheringIconMinSize
                             + (config.GatheringIconMaxSize - config.GatheringIconMinSize) * t;
                }
            }

            bool drewIcon = iconId > 0 && TryDrawIcon(dl, iconId, sx, cy, iconSize, alpha);

            if (!drewIcon)
            {
                if (obj.ObjectKind == ObjectKind.Pc)
                {
                    // Drives EVERY player marker below — plain ring, solid friend dot,
                    // and the party role icon + its drop shadow — so the Players page's
                    // size slider controls all of them together.
                    float playerSize = config.PartyRoleIconMinSize
                                      + (config.PartyRoleIconMaxSize - config.PartyRoleIconMinSize) * t;
                    float playerR    = playerSize * 0.5f;

                    bool drewJobIcon = false;

                    if (config.ShowPartyRoleIcons && obj is ICharacter partyChar
                        && (partyChar.StatusFlags & StatusFlags.PartyMember) != 0)
                    {
                        // Unbordered class/job icons confirmed at IDs 62001-62047 via
                        // /xldata in-game. Formula: 62000 + ClassJob.RowId (RowId is
                        // 1-indexed, so GLA=62001, PGL=62002, ... PIC=62042, etc.)
                        uint jobRowId  = partyChar.ClassJob.RowId;
                        int  jobIconId = jobRowId > 0 ? (int)(62000 + jobRowId) : 0;

                        if (jobIconId > 0)
                        {
                            // Role-colored border + drop shadow behind the job icon —
                            // Tank=blue, Healer=green, DPS=red, DoH/DoL=gray. Colors match
                            // FFXIV's own role UI. The outer ring is the solid border,
                            // sitting just past the icon's edge same as before. Behind
                            // that, three filled circles step inward and fade out —
                            // most visible right at the border, nearly gone by the centre
                            // — faking a soft shadow falling inward from the border since
                            // ImGui's draw list has no blur filter to do this for real.
                            // Wrapped in PushUnclip/PopUnclip so these escape the bar's
                            // clip rect the same way TryDrawIcon's image does below —
                            // otherwise, near the bar's edge, the icon would render in
                            // full while its own border/shadow got hard-cut behind it.
                            uint  roleCol      = GetRoleColor(partyChar);
                            float iconDrawSize = playerSize * IconSizeMultiplier;
                            float iconHalf     = iconDrawSize * 0.5f;
                            PushUnclip(dl);
                            dl.AddCircle(V(sx, cy), iconHalf + 1.0f, WithAlpha(roleCol, alpha), 0, 3.0f);
                            dl.AddCircleFilled(V(sx, cy), iconHalf * 0.85f, WithAlpha(roleCol, alpha * 0.6f));
                            dl.AddCircleFilled(V(sx, cy), iconHalf * 0.65f, WithAlpha(roleCol, alpha * 0.4f));
                            dl.AddCircleFilled(V(sx, cy), iconHalf * 0.45f, WithAlpha(roleCol, alpha * 0.2f));
                            PopUnclip(dl);

                            // Job icon drawn on top — if the icon ID doesn't resolve to a
                            // loadable texture (e.g. a future job beyond the known range),
                            // the role-colored border/shadow still shows as a useful fallback.
                            TryDrawIcon(dl, jobIconId, sx, cy, iconDrawSize, alpha);
                            drewJobIcon = true;
                        }
                    }

                    if (!drewJobIcon)
                    {
                        // Friends on the list render as solid filled dots to stand out
                        // from the crowd; everyone else gets the hollow ring.
                        bool isFriend = config.SolidFriendDots
                            && obj is ICharacter ch
                            && (ch.StatusFlags & StatusFlags.Friend) != 0;

                        if (isFriend)
                        {
                            dl.AddCircleFilled(V(sx, cy), playerR, WithAlpha(col, alpha));
                            dl.AddCircle(V(sx, cy), playerR + 0.8f, WithAlpha(0x66000000u, alpha));
                        }
                        else
                        {
                            // Ring with transparent centre for non-friend, non-party players.
                            dl.AddCircle(V(sx, cy), playerR, WithAlpha(col, alpha), 0, 2.0f);
                            dl.AddCircle(V(sx, cy), playerR + 0.8f, WithAlpha(0x33000000u, alpha));
                        }
                    }
                }
                else if (obj.ObjectKind == ObjectKind.EventNpc && !isAetheryteKind)
                {
                    // Plain NPC dot fallback (no quest/Mender/Shop icon applied) — sized
                    // from the same slider as those icons, so the NPCs page's size
                    // slider controls every NPC marker, dot or icon alike. Excludes
                    // aetheryte-classified EventNpcs (Firmament crystals) since those
                    // are routed through the Aetheryte color/size logic instead,
                    // independent of NPC settings (see MarkerColor above).
                    float npcSize = config.NpcQuestIconMinSize
                                  + (config.NpcQuestIconMaxSize - config.NpcQuestIconMinSize) * t;
                    float npcR    = npcSize * 0.5f;

                    // Hollow ring instead of a filled disc — matches the non-friend
                    // player marker style below, which reads more clearly against
                    // busy backgrounds than a solid dot.
                    dl.AddCircle(V(sx, cy), npcR, WithAlpha(col, alpha), 0, 2.0f);
                    dl.AddCircle(V(sx, cy), npcR + 0.8f, WithAlpha(0x33000000u, alpha));
                }
                else if (obj.ObjectKind == ObjectKind.BattleNpc)
                {
                    // Enemy dot, sized from its own page's slider instead of the
                    // shared global radius — same pattern as Players/NPCs above.
                    float enemySize = config.EnemyMinSize
                                     + (config.EnemyMaxSize - config.EnemyMinSize) * t;
                    float enemyR    = enemySize * 0.5f;

                    dl.AddCircleFilled(V(sx, cy), enemyR, WithAlpha(col, alpha));
                    dl.AddCircle(V(sx, cy), enemyR + 0.8f, WithAlpha(0x66000000u, alpha));
                }
                else if (isAetheryteKind)
                {
                    // Plain aetheryte dot fallback (icons off, or the texture didn't
                    // resolve this frame) — sized from the same slider as the real
                    // icon above, so the Aetherytes page's size slider controls every
                    // aetheryte marker, dot or icon alike. Catches every ObjectKind
                    // ClassifyAetheryte can return Big/Shard for (Aetheryte, EventNpc
                    // Firmament crystals, EventObj housing shards) — not just the
                    // real ObjectKind.Aetheryte case.
                    float aetherSize = config.AetheryteIconMinSize
                                      + (config.AetheryteIconMaxSize - config.AetheryteIconMinSize) * t;
                    float aetherR    = aetherSize * 0.5f;

                    dl.AddCircleFilled(V(sx, cy), aetherR, WithAlpha(col, alpha));
                    dl.AddCircle(V(sx, cy), aetherR + 0.8f, WithAlpha(0x66000000u, alpha));
                }
                else
                {
                    dl.AddCircleFilled(V(sx, cy), r, WithAlpha(col, alpha));
                    dl.AddCircle(V(sx, cy), r + 0.8f, WithAlpha(0x66000000u, alpha));
                }
            }
        }
    }

    /// <summary>
    /// Three-zone distance-fade curve shared by all marker types (regular dots, NPC quest
    /// icons, and FATEs): fully opaque while closer than <see cref="Configuration.DotNearZone"/>,
    /// smoothstep down to <see cref="Configuration.DotMidAlpha"/> through the middle band,
    /// then smoothstep down to fully invisible by <see cref="Configuration.DotFarZone"/>.
    /// <paramref name="t"/> is 1 at zero distance and 0 at the relevant max range.
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
    /// Renders active FATEs as their real game icon (falling back to a plain dot if the
    /// texture isn't available). Deliberately independent of every other marker toggle —
    /// see <see cref="Configuration.ShowFates"/>.
    /// </summary>
    private void RenderFates(
        ImDrawListPtr dl,
        float cx, float cy,
        float halfVis, float barHalfW, float lensStr,
        float heading, Vector3 originPos)
    {
        if (!config.ShowFates) return;

        float maxDistSq = config.MaxFateDistance * config.MaxFateDistance;
        float extHalf   = halfVis * lensStr;

        // Same two-pass approach as RenderMarkers: filter first, then sort by distance
        // so a closer FATE paints on top of a farther one if their icons overlap.
        fateCandidates.Clear();

        foreach (var fate in fateTable)
        {
            if (fate == null) continue;
            // Only show FATEs that are actually happening or about to happen — an Ending,
            // Ended, or Failed FATE isn't something worth navigating toward.
            if (fate.State != FateState.Running && fate.State != FateState.Preparing)
                continue;

            float dx  = fate.Position.X - originPos.X;
            float dy  = fate.Position.Y - originPos.Y;
            float dz  = fate.Position.Z - originPos.Z;
            float dsq = dx * dx + dy * dy + dz * dz;

            if (dsq > maxDistSq || dsq < 0.25f) continue;

            float bearing = Normalize(MathF.Atan2(dx, -dz) * (180f / MathF.PI));
            float delta   = Delta(heading, bearing);
            if (MathF.Abs(delta) > extHalf) continue;

            fateCandidates.Add(new FateCandidate(fate, MathF.Sqrt(dsq), delta));
        }

        fateCandidates.Sort((a, b) => b.Dist.CompareTo(a.Dist));

        foreach (var candidate in fateCandidates)
        {
            var   fate  = candidate.Fate;
            float delta = candidate.Delta;
            float dist  = candidate.Dist;

            float sx = cx + Project(delta, halfVis, barHalfW, lensStr);

            float t    = 1f - dist / config.MaxFateDistance;   // 1 = close, 0 = at max range
            float iconSize = config.FateIconMinSize + (config.FateIconMaxSize - config.FateIconMinSize) * t;
            // Same edge fade as RenderMarkers — FATEs render exclusively as icons, so
            // they're exactly the case that used to hard-cut at the bar's edge with no
            // fade at all.
            float alpha    = ComputeFadeAlpha(t) * LensEdgeAlpha(delta, halfVis, extHalf);

            bool drewIcon = fate.IconId > 0 && TryDrawIcon(dl, (int)fate.IconId, sx, cy, iconSize, alpha);

            if (!drewIcon)
            {
                float r = 3f + 7f * t;
                dl.AddCircleFilled(V(sx, cy), r, WithAlpha(C(config.FateColor), alpha));
                dl.AddCircle(V(sx, cy), r + 0.8f, WithAlpha(0x66000000u, alpha));
            }
        }
    }

    /// <summary>
    /// Draws a real game icon (looked up by its game icon ID — works for nameplate
    /// marker icons, FATE icons, anything ITextureProvider can resolve) centred at the
    /// given screen position. Returns false (caller should fall back to a plain dot) if
    /// the texture isn't available this frame.
    /// </summary>
    private bool TryDrawIcon(ImDrawListPtr dl, int iconId, float sx, float cy, float size, float alpha)
    {
        if (!textureProvider.TryGetFromGameIcon(new GameIconLookup((uint)iconId), out var sharedTex))
            return false;

        var tex = sharedTex.GetWrapOrEmpty();

        float half = size * 0.5f;
        uint  tint = WithAlpha(0xFFFFFFFFu, alpha);

        // RenderBar clips everything drawn in this pass to the bar's own rectangle —
        // fine for plain dots, but icons near the left/right edge of the visible FOV
        // can have a bounding box wider than the bar (especially with
        // IconSizeMultiplier/AetheryteIconSizeMultiplier inflating icon sizes), so that
        // clip was hard-cutting them right at the bar's edge. PushUnclip/PopUnclip
        // briefly escape that clip just for this one image so it draws in full. This
        // doesn't change paint order — the end-cap diamonds are drawn afterward in
        // RenderBar (after its own clip is popped), so they still end up on top of
        // whatever this paints, clipped or not.
        PushUnclip(dl);
        dl.AddImage(
            tex.Handle,
            V(sx - half, cy - half),
            V(sx + half, cy + half),
            Vector2.Zero,
            Vector2.One,
            tint);
        PopUnclip(dl);
        return true;
    }

    /// <summary>
    /// Resolves the real Mining/Botany (etc.) icon ID for a gathering node, by chasing
    /// the chain GatheringPoint(BaseId) → GatheringPointBase → GatheringType → IconMain.
    /// Cached permanently per BaseId since this is static game data. Returns 0 if any
    /// link in the chain doesn't resolve.
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

    // Role icon IDs confirmed against xivPartyIcons source's "Role" icon set array.
    // Using ClassJob.Role (not a per-job index) so it works for every current and
    // future job automatically without needing a lookup table update.
    /// <summary>
    /// Returns a filled-dot background color matching the FFXIV UI convention for the
    /// character's combat role: Tank=blue, Healer=green, DPS=red, DoH/DoL=gray.
    /// Used as the tinted background behind the unbordered job icon.
    /// </summary>
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

    /// <summary>
    /// Resolves an NPC's title text (e.g. "Merchant &amp; Mender") via the ENpcResident
    /// sheet, cached permanently per BaseId since this is static game data. Returns ""
    /// if the NPC has no title or the BaseId doesn't resolve — most named NPCs (over
    /// 96%, per real sheet data) fall into this category.
    /// </summary>
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
    /// Checks an NPC's job-keyword match across BOTH possible locations the text can
    /// live in: the separate ENpcResident "Title" field (e.g. Eilonwy → Name="Eilonwy",
    /// Title="Merchant &amp; Mender"), or the live display Name itself for NPCs that have
    /// no personal name at all and are simply named after their role directly
    /// (e.g. Name="Independent Mender", Title=""). Confirmed via real /compass debug
    /// output that both patterns genuinely occur — checking Title alone silently missed
    /// every NPC of the second kind.
    /// </summary>
    private static bool NpcMatchesKeywords(IGameObject obj, string title, string[] keywords) =>
        TitleContainsAny(title, keywords) || TitleContainsAny(obj.Name.TextValue, keywords);

    /// <summary>Which aetheryte category, if any, an object matches.</summary>
    private enum AetheryteNameKind { None, Big, Shard }

    /// <summary>
    /// Classifies an object using a single signal — whether its name contains
    /// <see cref="Configuration.AethernetShardName"/> ("Aethernet" by default, matched
    /// as a substring so it catches every city's variant in one go) — interpreted
    /// differently depending on ObjectKind:
    ///
    /// • ObjectKind.Aetheryte objects are aetherytes by definition (the game doesn't
    ///   use this kind for anything else), so one IS always classified as Big or
    ///   Shard — Shard if the name matches, Big by default otherwise. No separate
    ///   "main aetheryte name" check is needed; Big is just the fallback.
    ///
    /// • ObjectKind.EventNpc / EventObj cover all sorts of unrelated interactable
    ///   objects (regular NPCs, housing furnishings, etc.), so a name match there
    ///   only ever means Shard — there's no safe "default to Big" assumption for
    ///   these kinds, since that would misclassify ordinary NPCs as aetherytes.
    ///   A non-match returns None so the caller falls through to whatever else
    ///   that ObjectKind would normally mean.
    ///
    /// Used as the single source of truth for both icon selection and visibility, so
    /// the two can never drift out of sync with each other.
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

    /// <summary>
    /// Checks whether an object matches a known aetheryte pattern (Big or Shard) and, if
    /// so, returns the colour to draw it with — 0u if hidden by
    /// <see cref="Configuration.ShowAetherytes"/> or <see cref="Configuration.ShowAethernetShards"/>.
    /// Returns false if it doesn't match either pattern at all, so the caller (an
    /// EventNpc/EventObj case) can fall through to whatever other marker category that
    /// ObjectKind would otherwise represent.
    /// </summary>
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
                    // Note: comparisons use GameObjectId (ulong), not EntityId (uint) —
                    // those are two distinct ID spaces in this engine, and TargetObjectId
                    // is expressed in GameObjectId terms.
                    bool targetingMe    = obj.TargetObjectId == player.GameObjectId;
                    bool iAmTargetingIt = targetManager.Target?.GameObjectId == obj.GameObjectId;
                    if (!targetingMe && !iAmTargetingIt) return 0u;
                }

                return C(config.EnemyColor);
            case ObjectKind.EventNpc:
                // Firmament teleport crystals are EventNpcs that share the same
                // "Aethernet Shard ..." display name pattern as housing-ward shards —
                // route those through the aetheryte toggles/colour instead of the NPC
                // one, completely independent of ShowNpcs.
                if (TryGetAetheryteMarkerColor(obj, out uint eventNpcAetherCol))
                    return eventNpcAetherCol;

                if (!config.ShowNpcs) return 0u;
                if (config.NpcsOnlyIfTargetable && !obj.IsTargetable) return 0u;
                return C(config.NpcColor);
            case ObjectKind.EventObj:
                // Confirmed via /compass debug: housing-ward Aethernet shards are
                // ObjectKind.EventObj (not EventNpc, and not HousingEventObject either
                // — an earlier guess that didn't pan out). We don't track any other
                // kind of EventObj on the compass, so this case exists purely for the
                // aetheryte check; anything that doesn't match falls through to nothing.
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
                // ClassifyAetheryte always returns Big or Shard (never None) for this
                // ObjectKind — the kind itself is definitional, so TryGetAetheryteMarkerColor
                // is guaranteed to return true here; this can never fall through.
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

    private static Vector2 V(float x, float y) => new(x, y);
    private static uint     C(Vector4 v)        => ImGui.ColorConvertFloat4ToU32(v);

    /// <summary>
    /// Returns 1 while |delta| is inside the linear zone, then smoothsteps to 0
    /// as it moves through the compressed zone toward the edge.
    /// <paramref name="linearHalf"/> lets labels start fading earlier than ticks.
    /// </summary>
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
    /// Pushes a clip rect spanning the full display, ignoring whatever clip RenderBar
    /// currently has active — lets whatever's drawn between this and <see
    /// cref="PopUnclip"/> render past the bar's edge instead of being hard-cut by
    /// RenderBar's own clip rect (which is sized to the bar itself). Used by anything
    /// that needs to match the bar-edge behaviour of a real icon — currently
    /// TryDrawIcon's image and the party role icon's border/drop-shadow circles, which
    /// need to escape the same clip together or they visually disagree right at the
    /// bar's edge (icon rendering in full while its border/shadow gets cut off).
    /// </summary>
    private static void PushUnclip(ImDrawListPtr dl) =>
        dl.PushClipRect(Vector2.Zero, ImGui.GetIO().DisplaySize, false);

    private static void PopUnclip(ImDrawListPtr dl) => dl.PopClipRect();

    /// <summary>
    /// Logs every object within <paramref name="radius"/> yalms of the player to
    /// Dalamud's log — real ObjectKind, BaseId, name, targetable state, distance.
    /// Exists purely as a diagnostic: when a marker category isn't appearing and the
    /// reason isn't obvious, this turns "guess the ObjectKind" into "read it directly
    /// off the actual game client" instead. View results with the in-game /xllog
    /// command, or in the Dalamud console window.
    /// </summary>
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

                // Mirrors the exact priority order RenderMarkers uses, so this tells
                // you definitively which branch would actually win for this object —
                // no more guessing whether a quest marker is silently taking priority.
                string winner = hasQuestIcon ? $"QuestMarker(icon={qIconId})"
                              : isMender     ? "Mender"
                              : isShop       ? "Shop"
                              : "none/dot";

                extra = $" | Title=\"{title}\" | QuestIcon={hasQuestIcon,-5} | " +
                        $"IsMender={isMender,-5} | IsShop={isShop,-5} | WouldShow={winner}";
            }

            log.Info(
                $"[SkyrimCompass debug] {dist,6:F1}y | Kind={obj.ObjectKind,-19} | " +
                $"BaseId={obj.BaseId,-8} | Targetable={obj.IsTargetable,-5} | " +
                $"Name=\"{obj.Name.TextValue}\"{extra}");
        }
        log.Info("[SkyrimCompass debug] Done. Use /xllog in-game to view the log window.");
    }
}
