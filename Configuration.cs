using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace SkyrimCompass;

/// <summary>
/// Per-player icon override. Exact case-insensitive name match on
/// <see cref="PlayerName"/> swaps that player's marker for a real game icon.
/// Falls back to a plain dot if the texture doesn't resolve; border/fill draw
/// regardless, like party role icons.
/// </summary>
[Serializable]
public class PlayerIconOverride
{
    public string  PlayerName   { get; set; } = "";

    /// <summary>Icon ID (e.g. 62007=Paladin, 60453=main Aetheryte, 61802=FC emblem).</summary>
    public int     IconBaseId   { get; set; } = 0;

    // ── Optional border ring ──────────────────────────────────────────────────
    public bool    ShowBorder   { get; set; } = false;
    public Vector4 BorderColor  { get; set; } = new(1.00f, 1.00f, 1.00f, 0.90f);

    // ── Optional inward fill (like party role icon background) ────────────────
    public bool    ShowFill     { get; set; } = false;
    public Vector4 FillColor    { get; set; } = new(1.00f, 1.00f, 1.00f, 0.40f);

    /// <summary>Circular clip (AddImageRounded, full rounding) instead of square bounds.</summary>
    public bool    ClipToCircle  { get; set; } = false;

    /// <summary>Extra scale on top of the global 1.5× IconSizeMultiplier. 1.0 = normal.</summary>
    public float   SizeMultiplier { get; set; } = 1.0f;
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // ── General ─────────────────────────────────────────────────────────────
    public bool Enabled { get; set; } = true;

    // ── Layout ───────────────────────────────────────────────────────────────
    public float CompassWidth    { get; set; } = 560f;
    public float CompassHeight   { get; set; } = 35f;
    public float YOffset         { get; set; } = 8f;

    // ── Behaviour ────────────────────────────────────────────────────────────
    /// <summary>Degrees of the full 360° circle visible at once.</summary>
    public float VisibleDegrees  { get; set; } = 90f;
    /// <summary>Fisheye strength. 1.0 = linear; 1.5 ≈ +50% FOV at edges, 2.0 ≈ +100%.</summary>
    public float LensStrength    { get; set; } = 2.0f;
    /// <summary>Added to computed heading. Set 180 if N/S look swapped.</summary>
    public float RotationOffset  { get; set; } = 0f;
    /// <summary>Tracks camera yaw instead of character facing.</summary>
    public bool  UseCameraDirection { get; set; } = true;
    /// <summary>Sub-option of UseCameraDirection: measure from camera position, not character.</summary>
    public bool  UseCameraPosition { get; set; } = true;
    public float FontScale       { get; set; } = 1.0f;
    public bool  ShowHeadingText { get; set; } = false;
    /// <summary>Skips drawing entirely while a cutscene has the camera locked (checks OccupiedInCutSceneEvent, WatchingCutscene, WatchingCutscene78).</summary>
    public bool  HideDuringCutscenes { get; set; } = true;

    // ── Colors ───────────────────────────────────────────────────────────────
    public Vector4 BackgroundColor    { get; set; } = new(0.05f, 0.04f, 0.03f, 0.82f);
    public Vector4 BorderColor        { get; set; } = new(0.48f, 0.42f, 0.27f, 0.92f);
    public Vector4 CardinalColor      { get; set; } = new(1.00f, 0.97f, 0.88f, 1.00f);
    public Vector4 IntercardinalColor { get; set; } = new(0.72f, 0.70f, 0.65f, 0.88f);
    public Vector4 TickColor          { get; set; } = new(0.58f, 0.56f, 0.52f, 0.72f);

    // ── Marker toggles ───────────────────────────────────────────────────────
    public bool ShowPlayers       { get; set; } = true;
    /// <summary>Friends render as solid dots instead of hollow rings (StatusFlags.Friend).</summary>
    public bool SolidFriendDots   { get; set; } = true;
    /// <summary>
    /// Party members show role icon (Tank/Healer/DPS) via ClassJob.Role, taking
    /// priority over SolidFriendDots. IDs confirmed via xivPartyIcons:
    /// Tank=62581, Healer=62582, MeleeDPS=62584, Ranged/CasterDPS=62586.
    /// </summary>
    public bool ShowPartyRoleIcons  { get; set; } = true;
    /// <summary>Size (px) for every player marker — ring, friend dot, and role icon share one slider pair.</summary>
    public float PartyRoleIconMinSize { get; set; } = 10f;
    public float PartyRoleIconMaxSize { get; set; } = 24f;

    /// <summary>Named player overrides. Checked after party role icons, before friend/ring fallback.</summary>
    public List<PlayerIconOverride> PlayerIconOverrides { get; set; } = new();

    public bool ShowEnemies       { get; set; } = true;
    /// <summary>Only show enemies you're targeting or being targeted by.</summary>
    public bool EnemiesOnlyIfEngaged { get; set; } = true;
    /// <summary>Marker size (px). Defaults match the old hardcoded dot formula (3 + 7*t).</summary>
    public float EnemyMinSize { get; set; } = 6f;
    public float EnemyMaxSize { get; set; } = 20f;

    // ── Limit break glow ─────────────────────────────────────────────────────
    /// <summary>
    /// Glows the compass centre notch when limit break is ready, lighting the
    /// left end-cap diamond too at 2 bars and both end-caps at a full 3-bar break.
    /// </summary>
    public bool    ShowLimitBreakGlow  { get; set; } = false;
    public Vector4 LimitBreakGlowColor { get; set; } = new(1.00f, 0.65f, 0.10f, 0.95f);

    public bool ShowNpcs          { get; set; } = true;
    /// <summary>Hides non-targetable EventNpc "ghost" placeholders (e.g. empty chocobo stable slot).</summary>
    public bool NpcsOnlyIfTargetable { get; set; } = true;
    /// <summary>Shows the NPC's real active-quest icon (MSQ/side/"!"/"?") instead of a dot.</summary>
    public bool ShowNpcQuestIcons { get; set; } = true;
    /// <summary>Size (px) for every NPC marker — quest/Mender/Shop icons and dot fallback all share this.</summary>
    public float NpcQuestIconMinSize { get; set; } = 8f;
    public float NpcQuestIconMaxSize { get; set; } = 40f;
    /// <summary>Real icon for Mender NPCs (job title "Mender"). Shares NpcQuestIcon size range.</summary>
    public bool ShowMenderIcons { get; set; } = true;
    public int MenderIconId { get; set; } = 60434;  // confirmed; not exposed in UI
    /// <summary>Real icon for Shop/Trader NPCs (title "Merchant"/"Vendor"/"Trader"). Shares NpcQuestIcon size range.</summary>
    public bool ShowShopIcons { get; set; } = true;
    // Reference table listed 3 candidates for "Shop" (60412/60935/60987) with no
    // canonical pick and no client to confirm against. First candidate; try the
    // others if wrong.
    public int ShopIconId { get; set; } = 60412;

    public bool ShowGatheringNodes{ get; set; } = true;
    /// <summary>Hides non-targetable gathering nodes (same ghost-placeholder issue as NPCs).</summary>
    public bool GatheringOnlyIfTargetable { get; set; } = true;
    /// <summary>Real Mining/Botany/Quarrying/Logging icon instead of a dot.</summary>
    public bool ShowGatheringIcons { get; set; } = false;
    public float GatheringIconMinSize { get; set; } = 20f;
    public float GatheringIconMaxSize { get; set; } = 30f;
    public bool ShowTreasure      { get; set; } = true;
    /// <summary>
    /// Real chest icon instead of a dot. No data sheet exposes a coffer's visual
    /// type from its BaseId, so every coffer shows the same icon (TreasureIconId).
    /// </summary>
    public bool ShowTreasureIcons { get; set; } = true;
    /// <summary>Icon for every coffer. 60354 is one of three known chest icons (60354/60355/60356) — swap if wrong.</summary>
    public int TreasureIconId { get; set; } = 60354;
    /// <summary>Marker size (px). Defaults match the old hardcoded dot formula (3 + 7*t).</summary>
    public float TreasureMinSize { get; set; } = 6f;
    public float TreasureMaxSize { get; set; } = 20f;
    public bool ShowAetherytes    { get; set; } = true;
    /// <summary>Shows Aethernet shards too, not just a city's main aetheryte. Matched via AethernetShardName.</summary>
    public bool ShowAethernetShards { get; set; } = true;
    /// <summary>Real icon for aetherytes; IDs confirmed via a community plugin's icon table.</summary>
    public bool ShowAetheryteIcons { get; set; } = true;
    /// <summary>Substring match (case-insensitive) identifying Aethernet shards, e.g. "Aethernet".</summary>
    public string AethernetShardName { get; set; } = "Aethernet";
    public int AetheryteIconId      { get; set; } = 60453;  // confirmed; not exposed in UI
    public int AethernetShardIconId { get; set; } = 60430;
    public Vector4 AetheryteColor { get; set; } = new(0.55f, 0.85f, 0.95f, 0.92f);
    /// <summary>Size (px) for every aetheryte marker — icon and dot fallback share this.</summary>
    public float AetheryteIconMinSize { get; set; } = 20f;
    public float AetheryteIconMaxSize { get; set; } = 30f;
    /// <summary>Max detection range in yalms. True 3D distance, not just horizontal.</summary>
    public float MaxMarkerDistance{ get; set; } = 100f;

    // Dot distance-fade curve (fractions of max range, 0–1)
    public float DotNearZone  { get; set; } = 0.85f;
    public float DotFarZone   { get; set; } = 0.25f;
    public float DotMidAlpha  { get; set; } = 0.50f;

    // ── Marker colors ────────────────────────────────────────────────────────
    public Vector4 PlayerColor   { get; set; } = new(0.40f, 0.65f, 1.00f, 0.92f);
    public Vector4 EnemyColor    { get; set; } = new(1.00f, 0.25f, 0.25f, 0.92f);
    public Vector4 NpcColor      { get; set; } = new(0.95f, 0.88f, 0.35f, 0.92f);
    public Vector4 GatheringColor{ get; set; } = new(0.30f, 0.92f, 0.40f, 0.92f);
    public Vector4 TreasureColor { get; set; } = new(1.00f, 0.80f, 0.15f, 0.95f);

    // ── FATEs ────────────────────────────────────────────────────────────────
    // Independent of every toggle above and ShowAnyMarkers — zone-wide POI,
    // often wanted even with all other markers off.
    public bool ShowFates { get; set; } = true;
    /// <summary>Fallback color if the FATE icon texture fails to load.</summary>
    public Vector4 FateColor { get; set; } = new(0.82f, 0.35f, 0.95f, 0.95f);
    /// <summary>FATE detection range = MaxMarkerDistance × FateDistanceMultiplier. Default 2.5× lets FATEs be discoverable from well outside normal combat awareness.</summary>
    public float FateDistanceMultiplier { get; set; } = 2.5f;
    public float FateIconMinSize { get; set; } = 20f;
    public float FateIconMaxSize { get; set; } = 32f;

    /// <summary>True if any marker type is enabled (skips the object-table loop).</summary>
    public bool ShowAnyMarkers =>
        ShowPlayers || ShowEnemies || ShowNpcs || ShowGatheringNodes || ShowTreasure || ShowAetherytes;

    public void Save(IDalamudPluginInterface pi) => pi.SavePluginConfig(this);
}
