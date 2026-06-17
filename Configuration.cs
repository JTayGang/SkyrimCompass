using System;
using System.Numerics;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace SkyrimCompass;

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
    public bool ShowEnemies       { get; set; } = true;
    /// <summary>
    /// When enabled, only shows hostile enemies that are currently targeting the
    /// player or that the player is currently targeting — i.e. enemies you're
    /// actually engaged with, rather than every hostile mob in range. Useful for
    /// decluttering big pulls, hunt trains, and FATEs.
    /// </summary>
    public bool EnemiesOnlyIfEngaged { get; set; } = true;
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
    /// <summary>Icon pixel diameter at maximum detection range — the farthest/smallest size.</summary>
    public float NpcQuestIconMinSize { get; set; } = 8f;
    /// <summary>Icon pixel diameter when very close — the nearest/largest size.</summary>
    public float NpcQuestIconMaxSize { get; set; } = 40f;
    public bool ShowGatheringNodes{ get; set; } = true;
    public bool ShowTreasure      { get; set; } = true;
    /// <summary>
    /// Maximum detection range in yalms. This is a true 3D straight-line distance
    /// (horizontal + vertical), not just horizontal — something far above or below
    /// you on a different floor/platform won't register as nearby just because it's
    /// horizontally close.
    /// </summary>
    public float MaxMarkerDistance{ get; set; } = 100f;

    // Dot distance-fade curve (all values are fractions of max detection range, 0–1)
    /// <summary>Dots are fully opaque when closer than this fraction of max range (e.g. 0.85 = within 15%).</summary>
    public float DotNearZone  { get; set; } = 0.95f;
    /// <summary>Below this fraction dots begin their final fade to zero (e.g. 0.25 = outer 25%).</summary>
    public float DotFarZone   { get; set; } = 0.325f;
    /// <summary>Opacity at the mid/far boundary (0 = invisible, 1 = fully opaque).</summary>
    public float DotMidAlpha  { get; set; } = 0.325f;

    // ── Marker colors ────────────────────────────────────────────────────────
    public Vector4 PlayerColor   { get; set; } = new(0.40f, 0.65f, 1.00f, 0.92f);
    public Vector4 EnemyColor    { get; set; } = new(1.00f, 0.25f, 0.25f, 0.92f);
    public Vector4 NpcColor      { get; set; } = new(0.95f, 0.88f, 0.35f, 0.92f);
    public Vector4 GatheringColor{ get; set; } = new(0.30f, 0.92f, 0.40f, 0.92f);
    public Vector4 TreasureColor { get; set; } = new(1.00f, 0.80f, 0.15f, 0.95f);

    /// <summary>True if at least one marker type is enabled (used to skip the object-table loop).</summary>
    public bool ShowAnyMarkers =>
        ShowPlayers || ShowEnemies || ShowNpcs || ShowGatheringNodes || ShowTreasure;

    public void Save(IDalamudPluginInterface pi) => pi.SavePluginConfig(this);
}
