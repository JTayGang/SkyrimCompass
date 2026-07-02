using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace SkyrimCompass;

/// <summary>
/// Per-player icon override. Case-insensitive name match swaps that player's
/// marker for a real game icon. Falls back to a dot if texture doesn't resolve.
/// </summary>
[Serializable]
public class PlayerIconOverride
{
    public string  PlayerName    { get; set; } = "";
    /// <summary>Icon ID (e.g. 62007=Paladin, 60453=Aetheryte, 61802=FC emblem).</summary>
    public int     IconBaseId    { get; set; } = 0;
    public bool    ShowBorder    { get; set; } = false;
    public Vector4 BorderColor   { get; set; } = new(1.00f, 1.00f, 1.00f, 0.90f);
    public bool    ShowFill      { get; set; } = false;
    public Vector4 FillColor     { get; set; } = new(1.00f, 1.00f, 1.00f, 0.40f);
    /// <summary>Circular clip (AddImageRounded) instead of square bounds.</summary>
    public bool    ClipToCircle  { get; set; } = false;
    /// <summary>Extra scale on top of the global 1.5× IconSizeMultiplier.</summary>
    public float   SizeMultiplier { get; set; } = 1.0f;
}

/// <summary>
/// One curated NPC, matched by exact BaseId. Currently used only by TransportNpcs, which
/// has no automatic detection at all (unlike Mender/Shop, which are keyword-matched — see
/// MenderTitleKeywords/ShopTitleKeywords in CompassHud.cs). BaseId-only because many of
/// these reuse generic flavor names (e.g. "Storm Private") shared with unrelated background
/// NPCs. Find a BaseId via /compass debug while standing next to the NPC.
/// </summary>
[Serializable]
public class CuratedNpcEntry
{
    public uint   BaseId { get; set; } = 0;
    /// <summary>Just a note to remember what this is (e.g. "Limsa Ferry Dock"). No effect on matching.</summary>
    public string Label  { get; set; } = "";
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool Enabled { get; set; } = true;

    // ── Layout ───────────────────────────────────────────────────────────────
    public float CompassWidth   { get; set; } = 560f;
    public float CompassHeight  { get; set; } = 35f;
    public float YOffset        { get; set; } = 8f;

    // ── Behaviour ────────────────────────────────────────────────────────────
    /// <summary>Degrees of the full 360° visible at once.</summary>
    public float VisibleDegrees     { get; set; } = 90f;
    /// <summary>Fisheye strength. 1.0 = linear; 2.0 ≈ +100% FOV at edges.</summary>
    public float LensStrength       { get; set; } = 2.0f;
    /// <summary>Added to computed heading. Set 180 if N/S are swapped.</summary>
    public float RotationOffset     { get; set; } = 0f;
    /// <summary>Tracks camera yaw instead of character facing.</summary>
    public bool  UseCameraDirection { get; set; } = true;
    /// <summary>Sub-option: measure bearings/distances from camera position, not character.</summary>
    public bool  UseCameraPosition  { get; set; } = true;
    public float FontScale          { get; set; } = 1.0f;
    public bool  ShowHeadingText    { get; set; } = false;
    /// <summary>Skips drawing during cutscenes (OccupiedInCutSceneEvent / WatchingCutscene / WatchingCutscene78).</summary>
    public bool  HideDuringCutscenes { get; set; } = true;

    // ── Colors ───────────────────────────────────────────────────────────────
    public Vector4 BackgroundColor    { get; set; } = new(0.05f, 0.04f, 0.03f, 0.82f);
    public Vector4 BorderColor        { get; set; } = new(0.48f, 0.42f, 0.27f, 0.92f);
    public Vector4 CardinalColor      { get; set; } = new(1.00f, 0.97f, 0.88f, 1.00f);
    public Vector4 IntercardinalColor { get; set; } = new(0.72f, 0.70f, 0.65f, 0.88f);
    public Vector4 TickColor          { get; set; } = new(0.58f, 0.56f, 0.52f, 0.72f);

    // ── Marker toggles ───────────────────────────────────────────────────────
    public bool ShowPlayers     { get; set; } = true;
    /// <summary>Friends render as solid dots (StatusFlags.Friend).</summary>
    public bool SolidFriendDots { get; set; } = true;
    /// <summary>Party members show job icon via ClassJob.RowId (62001–62047) with a role-colored ring.</summary>
    public bool ShowPartyRoleIcons    { get; set; } = true;
    public float PartyRoleIconMinSize { get; set; } = 10f;
    public float PartyRoleIconMaxSize { get; set; } = 24f;

    /// <summary>Named player overrides. Checked after party role icons, before friend/ring fallback.</summary>
    public List<PlayerIconOverride> PlayerIconOverrides { get; set; } = new();

    public bool ShowEnemies          { get; set; } = true;
    /// <summary>Only show enemies you're targeting or being targeted by.</summary>
    public bool EnemiesOnlyIfEngaged { get; set; } = true;
    public float EnemyMinSize { get; set; } = 6f;
    public float EnemyMaxSize { get; set; } = 20f;

    // ── Limit break glow ─────────────────────────────────────────────────────
    /// <summary>
    /// One independent glowing border layer per bar: bar N's own 0–100% progress creeps
    /// in from both ends in its configured color, reaching the whole border when full.
    /// Stacking layers let you read charged bar count at a glance.
    /// </summary>
    public bool    ShowLimitBreakGlow   { get; set; } = false;
    public Vector4 LimitBreakGlowColor  { get; set; } = new(1.00f, 0.65f, 0.10f, 0.95f);
    /// <summary>Layer 2 (bar 2's own progress) — bright yellow by default.</summary>
    public Vector4 LimitBreakGlowColor2 { get; set; } = new(1.00f, 0.95f, 0.20f, 0.95f);
    /// <summary>Layer 3 (bar 3's own progress) — white by default.</summary>
    public Vector4 LimitBreakGlowColor3 { get; set; } = new(1.00f, 1.00f, 1.00f, 0.95f);

    public bool ShowNpcs              { get; set; } = true;
    /// <summary>Hides non-targetable EventNpc placeholders (e.g. empty chocobo stable slot).</summary>
    public bool NpcsOnlyIfTargetable  { get; set; } = true;
    /// <summary>Shows the NPC's real active-quest icon instead of a dot.</summary>
    public bool ShowNpcQuestIcons     { get; set; } = true;
    public float NpcQuestIconMinSize  { get; set; } = 8f;
    public float NpcQuestIconMaxSize  { get; set; } = 40f;
    /// <summary>
    /// Real icon for Mender NPCs. Detected via ENpcResident's Title (named NPCs, e.g.
    /// "Alistair") or Singular (generic flavor NPCs with no personal name, e.g. a plain
    /// "Mender" — Title is empty for those), always read in English regardless of client
    /// language — so this works the same on any game language.
    /// Shares NpcQuestIcon size range.
    /// </summary>
    public bool ShowMenderIcons { get; set; } = true;
    public int  MenderIconId    { get; set; } = 60434;
    /// <summary>
    /// Real icon for Shop/Trader NPCs. Detected via ENpcResident's Title (named NPCs, e.g.
    /// "Syneyhil" titled "Fieldcraft Supplier") or Singular (generic flavor NPCs with no
    /// personal name, e.g. "Material Supplier" — Title is empty for those), always read in
    /// English regardless of client language — so this works the same on any game language.
    /// Shares NpcQuestIcon size range.
    /// </summary>
    public bool ShowShopIcons { get; set; } = true;
    public int  ShopIconId    { get; set; } = 60412;

    // ── Transport NPCs (ferries, etc) ───────────────────────────────────────
    // Curated by BaseId (see CuratedNpcEntry) — independent of ShowNpcs, same
    // carve-out pattern as Aetherytes, since these are functionally "things that
    // take you somewhere" rather than generic flavor NPCs.
    public bool    ShowTransportNpcs    { get; set; } = true;
    public Vector4 TransportColor       { get; set; } = new(0.40f, 0.78f, 0.70f, 0.90f);
    /// <summary>No confirmed icon ID yet — browse with /xldata icons and set manually. 0 = dot only.</summary>
    public bool    ShowTransportIcons   { get; set; } = true;
    public int     TransportIconId      { get; set; } = 0;
    public float   TransportIconMinSize { get; set; } = 16f;
    public float   TransportIconMaxSize { get; set; } = 28f;
    public List<CuratedNpcEntry> TransportNpcs { get; set; } = new()
    {
        new CuratedNpcEntry { BaseId = 1007994, Label = "Limsa Ferry Dock" },
    };

    public bool ShowGatheringNodes          { get; set; } = true;
    /// <summary>Hides non-targetable gathering node placeholders.</summary>
    public bool GatheringOnlyIfTargetable   { get; set; } = true;
    /// <summary>Real Mining/Botany/Quarrying/Logging icon instead of a dot.</summary>
    public bool ShowGatheringIcons          { get; set; } = false;
    public float GatheringIconMinSize       { get; set; } = 20f;
    public float GatheringIconMaxSize       { get; set; } = 30f;
    public bool ShowTreasure                { get; set; } = true;
    /// <summary>All coffers show the same icon — no sheet maps BaseId to visual type.</summary>
    public bool ShowTreasureIcons { get; set; } = true;
    /// <summary>Icon for every coffer. 60354/60355/60356 are known variants — swap if wrong.</summary>
    public int  TreasureIconId    { get; set; } = 60354;
    public float TreasureMinSize  { get; set; } = 6f;
    public float TreasureMaxSize  { get; set; } = 20f;
    public bool ShowAetherytes    { get; set; } = true;
    /// <summary>Shows Aethernet shards too, matched via AethernetShardName.</summary>
    public bool ShowAethernetShards  { get; set; } = true;
    public bool ShowAetheryteIcons   { get; set; } = true;
    /// <summary>Substring match (case-insensitive) identifying Aethernet shards.</summary>
    public string AethernetShardName  { get; set; } = "Aethernet";
    public int    AetheryteIconId     { get; set; } = 60453;
    public int    AethernetShardIconId { get; set; } = 60430;
    public Vector4 AetheryteColor     { get; set; } = new(0.55f, 0.85f, 0.95f, 0.92f);
    public float AetheryteIconMinSize { get; set; } = 20f;
    public float AetheryteIconMaxSize { get; set; } = 30f;
    /// <summary>Max detection range in yalms (true 3D distance).</summary>
    public float MaxMarkerDistance    { get; set; } = 100f;

    // Distance-fade curve (fractions of max range, 0–1)
    public float DotNearZone  { get; set; } = 0.85f;
    public float DotFarZone   { get; set; } = 0.25f;
    public float DotMidAlpha  { get; set; } = 0.50f;

    // ── Marker colors ────────────────────────────────────────────────────────
    public Vector4 PlayerColor    { get; set; } = new(0.40f, 0.65f, 1.00f, 0.92f);
    public Vector4 EnemyColor     { get; set; } = new(1.00f, 0.25f, 0.25f, 0.92f);
    public Vector4 NpcColor       { get; set; } = new(0.95f, 0.88f, 0.35f, 0.92f);
    public Vector4 GatheringColor { get; set; } = new(0.30f, 0.92f, 0.40f, 0.92f);
    public Vector4 TreasureColor  { get; set; } = new(1.00f, 0.80f, 0.15f, 0.95f);

    // ── FATEs ────────────────────────────────────────────────────────────────
    // Zone-wide POI, independent of ShowAnyMarkers — often wanted with all other markers off.
    public bool    ShowFates              { get; set; } = true;
    /// <summary>Fallback color if FATE icon texture fails to load.</summary>
    public Vector4 FateColor              { get; set; } = new(0.82f, 0.35f, 0.95f, 0.95f);
    /// <summary>FATE range = MaxMarkerDistance × FateDistanceMultiplier.</summary>
    public float   FateDistanceMultiplier { get; set; } = 2.5f;
    public float   FateIconMinSize        { get; set; } = 20f;
    public float   FateIconMaxSize        { get; set; } = 32f;

    /// <summary>True if any marker type is enabled (skips the object-table loop).</summary>
    public bool ShowAnyMarkers =>
        ShowPlayers || ShowEnemies || ShowNpcs || ShowGatheringNodes || ShowTreasure || ShowAetherytes || ShowTransportNpcs;

    public void Save(IDalamudPluginInterface pi) => pi.SavePluginConfig(this);
}
