using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace SkyrimCompass;

/// <summary>
/// Per-player icon override entry.  When the display name of a nearby player
/// matches <see cref="PlayerName"/> (exact, case-insensitive) their compass
/// marker is replaced with a real game icon instead of the normal dot or ring.
///
/// Two optional decorations mirror the party-role-icon style:
///   • <see cref="ShowBorder"/> — a solid outer ring in <see cref="BorderColor"/>.
///   • <see cref="ShowFill"/>   — three inward-fading filled circles in
///                                <see cref="FillColor"/> (the same "bloom" effect
///                                used behind party job icons).
///
/// If the icon texture hasn't resolved this frame the entry falls back to a
/// plain dot so the player remains visible; the border/fill (when enabled) are
/// always drawn regardless, matching how party role icons work.
/// </summary>
[Serializable]
public class PlayerIconOverride
{
    /// <summary>Exact display name to match (case-insensitive).</summary>
    public string  PlayerName   { get; set; } = "";

    /// <summary>
    /// Game icon base ID — same numbering as everywhere else in the plugin
    /// (e.g. 62007 = Paladin, 60453 = main Aetheryte, 61802 = FC emblem).
    /// </summary>
    public int     IconBaseId   { get; set; } = 0;

    // ── Optional border ring ──────────────────────────────────────────────────
    public bool    ShowBorder   { get; set; } = false;
    public Vector4 BorderColor  { get; set; } = new(1.00f, 1.00f, 1.00f, 0.90f);

    // ── Optional inward fill (like party role icon background) ────────────────
    public bool    ShowFill     { get; set; } = false;
    public Vector4 FillColor    { get; set; } = new(1.00f, 1.00f, 1.00f, 0.40f);

    // ── Circle clip ───────────────────────────────────────────────────────────
    /// <summary>
    /// When true the icon is rendered with a circular clip (rounding = half the icon
    /// size) instead of the default square bounding box. Useful for square icon textures
    /// that look mismatched next to a circular border ring. Uses ImGui's built-in
    /// AddImageRounded at full rounding so no extra render targets or masks are needed.
    /// </summary>
    public bool    ClipToCircle  { get; set; } = false;

    // ── Per-icon size multiplier ──────────────────────────────────────────────
    /// <summary>
    /// Extra multiplier applied on top of the global <c>IconSizeMultiplier</c> (1.5×).
    /// At 1.0 (default) the icon draws at the same apparent scale as a party role icon.
    /// Raise it for icons whose texture has heavy transparent padding so the visible art
    /// fills the same space as every other marker; lower it for icons with minimal
    /// padding that would otherwise appear oversized relative to their neighbours.
    /// </summary>
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
    /// <summary>How many degrees of the full 360° circle are visible at once.</summary>
    public float VisibleDegrees  { get; set; } = 90f;
    /// <summary>
    /// Lens/fisheye distortion strength. 1.0 = linear (no effect).
    /// Higher values show a wider FOV at the edges while keeping the centre at normal scale.
    /// 1.5 shows ~50 % more degrees; 2.0 shows ~100 % more.
    /// </summary>
    public float LensStrength    { get; set; } = 2.0f;
    /// <summary>Added to the computed heading (degrees). Set to 180 if N/S appear swapped.</summary>
    public float RotationOffset  { get; set; } = 0f;
    /// <summary>
    /// When true the compass tracks the camera yaw instead of the character's facing direction.
    /// Useful in third-person with a freely-rotating camera.
    /// </summary>
    public bool  UseCameraDirection { get; set; } = true;
    /// <summary>
    /// Sub-option of <see cref="UseCameraDirection"/> (has no effect unless that's also on).
    /// When enabled, entity bearings/distances are measured from the camera's physical
    /// position instead of the character's — useful if you play heavily zoomed out or use
    /// a camera offset mod, so compass markers line up with what you're actually seeing
    /// rather than where your character's body happens to be standing.
    /// </summary>
    public bool  UseCameraPosition { get; set; } = true;
    public float FontScale       { get; set; } = 1.0f;
    public bool  ShowHeadingText { get; set; } = false;

    // ── Colors ───────────────────────────────────────────────────────────────
    // Brownish-dark background evokes Skyrim's leathery UI
    public Vector4 BackgroundColor    { get; set; } = new(0.05f, 0.04f, 0.03f, 0.82f);
    public Vector4 BorderColor        { get; set; } = new(0.48f, 0.42f, 0.27f, 0.92f);
    public Vector4 CardinalColor      { get; set; } = new(1.00f, 0.97f, 0.88f, 1.00f);
    public Vector4 IntercardinalColor { get; set; } = new(0.72f, 0.70f, 0.65f, 0.88f);
    public Vector4 TickColor          { get; set; } = new(0.58f, 0.56f, 0.52f, 0.72f);

    // ── Marker toggles ───────────────────────────────────────────────────────
    public bool ShowPlayers       { get; set; } = true;
    /// <summary>
    /// When enabled, players on your friends list render as solid filled dots instead
    /// of the default hollow ring, making them visually distinct in a crowd.
    /// Uses StatusFlags.Friend from the game's own IsFriend field — the same source
    /// the minimap and nameplate system read from.
    /// </summary>
    public bool SolidFriendDots   { get; set; } = true;
    /// <summary>
    /// When enabled, party members show their role icon (Tank/Healer/DPS) instead of
    /// a hollow ring or friend dot. Uses ClassJob.Role from the live character data —
    /// automatically correct for every current and future job without a lookup table.
    /// Takes priority over SolidFriendDots for party members.
    /// Role icon IDs confirmed against xivPartyIcons source:
    ///   Tank=62581, Healer=62582, MeleeDPS=62584, Ranged/CasterDPS=62586.
    /// </summary>
    public bool ShowPartyRoleIcons  { get; set; } = true;
    /// <summary>
    /// Pixel diameter at maximum detection range for EVERY player marker on the compass —
    /// the plain hollow ring, the solid friend dot, and the party role icon (plus its
    /// role-colored background dot). One slider pair now drives all of them so the whole
    /// Players page scales together, rather than the plain dot being a separate hardcoded
    /// size that this slider didn't reach.
    /// </summary>
    public float PartyRoleIconMinSize { get; set; } = 10f;
    /// <summary>Same as <see cref="PartyRoleIconMinSize"/> but for very close range.</summary>
    public float PartyRoleIconMaxSize { get; set; } = 24f;

    /// <summary>
    /// Per-player named overrides that replace a specific player's compass marker with
    /// a custom game icon, with optional border ring and inward fill decoration.
    /// Checked after party role icons (which stay highest priority) but before the
    /// default friend-dot and hollow-ring paths.
    /// </summary>
    public List<PlayerIconOverride> PlayerIconOverrides { get; set; } = new();

    public bool ShowEnemies       { get; set; } = true;
    /// <summary>
    /// When enabled, only shows hostile enemies that are currently targeting the
    /// player or that the player is currently targeting — i.e. enemies you're
    /// actually engaged with, rather than every hostile mob in range. Useful for
    /// decluttering big pulls, hunt trains, and FATEs.
    /// </summary>
    public bool EnemiesOnlyIfEngaged { get; set; } = true;
    /// <summary>
    /// Pixel diameter at maximum detection range for enemy markers. Defaults match the
    /// old hardcoded dot formula (radius <c>3 + 7*t</c>, i.e. 6px diameter far away) so
    /// behaviour is unchanged out of the box — but now it's slider-adjustable instead of
    /// baked into the code, same as the Players/NPCs size sliders.
    /// </summary>
    public float EnemyMinSize { get; set; } = 6f;
    /// <summary>Same as <see cref="EnemyMinSize"/> but for very close range.</summary>
    public float EnemyMaxSize { get; set; } = 20f;
    public bool ShowNpcs          { get; set; } = true;
    /// <summary>
    /// When enabled, hides EventNpc objects that aren't currently targetable.
    /// FFXIV's object table often keeps inert placeholder/anchor entries around
    /// even when nothing is actually standing there — e.g. an unoccupied chocobo
    /// stable slot in housing — which otherwise show up as "ghost" markers with
    /// nothing visibly present. On by default since these are pure clutter.
    /// </summary>
    public bool NpcsOnlyIfTargetable { get; set; } = true;
    /// <summary>
    /// When enabled, NPCs with an active quest marker (MSQ, side quest "!", blue quest,
    /// "?" in-progress, etc.) show that exact icon — the same one the game already
    /// displays above their head — instead of a plain dot. NPCs without an active
    /// marker still fall back to the normal dot.
    /// </summary>
    public bool ShowNpcQuestIcons { get; set; } = true;
    /// <summary>
    /// Pixel diameter at maximum detection range for EVERY NPC marker on the compass —
    /// the quest/Mender/Shop icons below AND the plain dot fallback used when none of
    /// those apply. One slider pair now drives all of them so the whole NPCs page scales
    /// together, rather than the plain dot being a separate hardcoded size outside its reach.
    /// </summary>
    public float NpcQuestIconMinSize { get; set; } = 8f;
    /// <summary>Same as <see cref="NpcQuestIconMinSize"/> but for very close range.</summary>
    public float NpcQuestIconMaxSize { get; set; } = 40f;
    /// <summary>
    /// Shows the real game icon for Mender NPCs (gear repair vendors), identified by
    /// their "Mender" job title. Shares <see cref="NpcQuestIconMinSize"/> and
    /// <see cref="NpcQuestIconMaxSize"/> with the quest-marker icon feature above —
    /// one shared size range for any icon shown in place of an NPC dot.
    /// </summary>
    public bool ShowMenderIcons { get; set; } = true;
    // Confirmed game icon ID for Mender NPCs — kept as a property (not shown in the UI)
    // for the same reason as AetheryteIconId: round-trips cleanly in saved config files,
    // but there's no reason a player should need to change it.
    public int MenderIconId { get; set; } = 60434;
    /// <summary>
    /// Shows the real game icon for Shop/Trader NPCs, identified by a "Merchant",
    /// "Vendor", or "Trader" job title. Shares <see cref="NpcQuestIconMinSize"/> and
    /// <see cref="NpcQuestIconMaxSize"/> with the other NPC icon features.
    /// </summary>
    public bool ShowShopIcons { get; set; } = true;
    // Shop icon ID — sourced from the same trusted reference table as MenderIconId, but
    // that table actually listed THREE candidate icon IDs for "Shops" (60412/60935/60987)
    // without indicating which is the canonical one, and there was no way to visually
    // confirm which looks right without a live client. This is the first of those three —
    // correct it (or try 60935 / 60987) if it doesn't look right in-game.
    public int ShopIconId { get; set; } = 60412;

    public bool ShowGatheringNodes{ get; set; } = true;
    /// <summary>
    /// When enabled, hides GatheringPoint objects that aren't currently targetable —
    /// the same "ghost" placeholder situation that affects NPCs (see
    /// <see cref="NpcsOnlyIfTargetable"/>) can also apply to depleted/not-yet-spawned
    /// gathering nodes.
    /// </summary>
    public bool GatheringOnlyIfTargetable { get; set; } = true;
    /// <summary>
    /// When enabled, gathering nodes show their real Mining/Botany (or Quarrying/
    /// Logging) game icon instead of a plain dot.
    /// </summary>
    public bool ShowGatheringIcons { get; set; } = false;
    /// <summary>Gathering icon pixel diameter at maximum detection range.</summary>
    public float GatheringIconMinSize { get; set; } = 20f;
    /// <summary>Gathering icon pixel diameter when very close.</summary>
    public float GatheringIconMaxSize { get; set; } = 30f;
    public bool ShowTreasure      { get; set; } = true;
    public bool ShowAetherytes    { get; set; } = true;
    /// <summary>
    /// Whether Aethernet shards (the smaller waypoints in housing wards, the Firmament,
    /// and similar areas) are shown at all, as opposed to just a city's one main
    /// aetheryte. Classification uses <see cref="AethernetShardName"/> below — anything
    /// matching that is a shard; every other real aetheryte is treated as the main one
    /// by default, no separate "main aetheryte name" needed. Icon selection (see
    /// <see cref="ShowAetheryteIcons"/>) is always correct for whichever ones are
    /// visible — this only controls whether shards appear, not which icon they get.
    /// </summary>
    public bool ShowAethernetShards { get; set; } = true;
    /// <summary>
    /// Shows a real game icon for aetherytes instead of a plain dot. Icon IDs are
    /// confirmed against a reference plugin's community-maintained icon table.
    /// </summary>
    public bool ShowAetheryteIcons { get; set; } = true;
    /// <summary>
    /// Partial or full name of Aethernet shard waypoints in your game language. Matched
    /// as a case-insensitive substring, so "Aethernet" catches "Ul'dah Aethernet Shard",
    /// "Limsa Lominsa Aethernet Shard", etc. in one go. This is the only name field
    /// needed: any real aetheryte that doesn't match this is assumed to be the main one.
    /// </summary>
    public string AethernetShardName { get; set; } = "Aethernet";
    // Icon IDs are confirmed values — kept as properties so existing config files
    // round-trip cleanly, but not exposed in the UI since there's no reason
    // players should need to change them.
    public int AetheryteIconId      { get; set; } = 60453;
    public int AethernetShardIconId { get; set; } = 60430;
    /// <summary>Fallback dot color, used whenever the real icon isn't available.</summary>
    public Vector4 AetheryteColor { get; set; } = new(0.55f, 0.85f, 0.95f, 0.92f);
    /// <summary>
    /// Pixel diameter at maximum detection range for EVERY aetheryte marker — the real
    /// icon above AND the plain dot fallback (shown when icons are off, or if a texture
    /// somehow fails to load). One slider pair now drives both, same as the NPCs page.
    /// </summary>
    public float AetheryteIconMinSize { get; set; } = 20f;
    /// <summary>Same as <see cref="AetheryteIconMinSize"/> but for very close range.</summary>
    public float AetheryteIconMaxSize { get; set; } = 30f;
    /// <summary>
    /// Maximum detection range in yalms. This is a true 3D straight-line distance
    /// (horizontal + vertical), not just horizontal — something far above or below
    /// you on a different floor/platform won't register as nearby just because it's
    /// horizontally close.
    /// </summary>
    public float MaxMarkerDistance{ get; set; } = 100f;

    // Dot distance-fade curve (all values are fractions of max detection range, 0–1)
    /// <summary>Dots are fully opaque when closer than this fraction of max range (e.g. 0.85 = within 15%).</summary>
    public float DotNearZone  { get; set; } = 0.85f;
    /// <summary>Below this fraction dots begin their final fade to zero (e.g. 0.25 = outer 25%).</summary>
    public float DotFarZone   { get; set; } = 0.25f;
    /// <summary>Opacity at the mid/far boundary (0 = invisible, 1 = fully opaque).</summary>
    public float DotMidAlpha  { get; set; } = 0.50f;

    // ── Marker colors ────────────────────────────────────────────────────────
    public Vector4 PlayerColor   { get; set; } = new(0.40f, 0.65f, 1.00f, 0.92f);
    public Vector4 EnemyColor    { get; set; } = new(1.00f, 0.25f, 0.25f, 0.92f);
    public Vector4 NpcColor      { get; set; } = new(0.95f, 0.88f, 0.35f, 0.92f);
    public Vector4 GatheringColor{ get; set; } = new(0.30f, 0.92f, 0.40f, 0.92f);
    public Vector4 TreasureColor { get; set; } = new(1.00f, 0.80f, 0.15f, 0.95f);

    // ── FATEs ────────────────────────────────────────────────────────────────
    // Deliberately independent of every toggle above and of ShowAnyMarkers below —
    // FATEs are a zone-wide point of interest, not "an enemy you're fighting", so
    // people often want this on even with every other marker category off.
    /// <summary>Shows active (Running/Preparation) FATEs on the compass.</summary>
    public bool ShowFates { get; set; } = true;
    /// <summary>Fallback dot color, only used if the FATE's real icon texture fails to load.</summary>
    public Vector4 FateColor { get; set; } = new(0.82f, 0.35f, 0.95f, 0.95f);
    /// <summary>
    /// Max FATE detection range in yalms (3D, same as <see cref="MaxMarkerDistance"/>).
    /// Deliberately much larger by default — FATEs are meant to be discoverable from
    /// well outside normal combat/NPC awareness range.
    /// </summary>
    public float MaxFateDistance { get; set; } = 150f;
    /// <summary>FATE icon pixel diameter at maximum detection range.</summary>
    public float FateIconMinSize { get; set; } = 20f;
    /// <summary>FATE icon pixel diameter when very close.</summary>
    public float FateIconMaxSize { get; set; } = 32f;

    /// <summary>True if at least one marker type is enabled (used to skip the object-table loop).</summary>
    public bool ShowAnyMarkers =>
        ShowPlayers || ShowEnemies || ShowNpcs || ShowGatheringNodes || ShowTreasure || ShowAetherytes;

    public void Save(IDalamudPluginInterface pi) => pi.SavePluginConfig(this);
}
