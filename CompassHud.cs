using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;

namespace SkyrimCompass;

/// <summary>
/// Renders a Skyrim-style compass bar onto the ImGui foreground draw list.
/// Uses a fisheye/lens projection so the centre looks normal while the edges
/// compress more degrees into each pixel, revealing a wider FOV without clutter.
/// </summary>
public sealed class CompassHud
{
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly ITargetManager targetManager;
    private readonly Configuration config;
    private readonly IPluginLog log;

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
        Configuration config,
        IPluginLog log)
    {
        this.clientState    = clientState;
        this.objectTable    = objectTable;
        this.targetManager  = targetManager;
        this.config         = config;
        this.log            = log;
    }

    // ── Public entry ─────────────────────────────────────────────────────────

    public unsafe void Draw()
    {
        if (!config.Enabled) return;

        var player = objectTable.LocalPlayer;
        if (player == null) return;

        float headingRad;

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

        RenderBar(dl, bx, by, bw, bh, heading, player);
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
        float heading, IPlayerCharacter player)
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

        // ── 2. Clip to bar ────────────────────────────────────────────────────
        dl.PushClipRect(V(bx + 1f, by), V(bx + bw - 1f, by + bh), true);

        // ── 3. Tick marks (every 5°, using lens projection) ──────────────────
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

        // ── 4. Direction labels (upper band, lens-projected) ──────────────────
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

        // ── 5. Entity markers (centred, lens-projected, alpha-faded) ─────────
        if (config.ShowAnyMarkers)
            RenderMarkers(dl, cx, cy, halfVis, barHalfW, lensStr, heading, player);

        dl.PopClipRect();

        // ── 6. Bar border ─────────────────────────────────────────────────────
        dl.AddRect(V(bx, by), V(bx + bw, by + bh), borderCol, 0f, ImDrawFlags.None, 1.5f);

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
        float heading, IPlayerCharacter player)
    {
        var   pp        = player.Position;
        float maxDistSq = config.MaxMarkerDistance * config.MaxMarkerDistance;
        float extHalf   = halfVis * lensStr;

        foreach (var obj in objectTable)
        {
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

            // Apply lens projection for screen X
            float sx = cx + Project(delta, halfVis, barHalfW, lensStr);

            float dist = MathF.Sqrt(dsq);
            float t    = 1f - dist / config.MaxMarkerDistance;   // 1 = close, 0 = at max range
            float r    = 3f + 7f * t;

            // Three-zone alpha curve:
            //  t > nearZone              → fully opaque
            //  farZone < t ≤ nearZone    → smoothstep from 1.0 → midAlpha
            //  0 < t ≤ farZone           → smoothstep from midAlpha → 0
            float nearZone = config.DotNearZone;
            float midEnd   = config.DotFarZone;
            float midAlpha = config.DotMidAlpha;
            float alpha;
            if (t >= nearZone)
            {
                alpha = 1f;
            }
            else if (t >= midEnd)
            {
                float u = (t - midEnd) / (nearZone - midEnd);
                float sm = u * u * (3f - 2f * u);
                alpha = midAlpha + (1f - midAlpha) * sm;
            }
            else
            {
                float u = t / midEnd;
                float sm = u * u * (3f - 2f * u);
                alpha = midAlpha * sm;
            }

            dl.AddCircleFilled(V(sx, cy), r, WithAlpha(col, alpha));
            dl.AddCircle(V(sx, cy), r + 0.8f, WithAlpha(0x66000000u, alpha));
        }
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
                return config.ShowNpcs ? C(config.NpcColor) : 0u;
            case ObjectKind.GatheringPoint:
                return config.ShowGatheringNodes ? C(config.GatheringColor) : 0u;
            case ObjectKind.Treasure:
                return config.ShowTreasure ? C(config.TreasureColor) : 0u;
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
}
