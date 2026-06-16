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
    public float CompassHeight   { get; set; } = 46f;
    public float YOffset         { get; set; } = 8f;

    // ── Behaviour ────────────────────────────────────────────────────────────
    /// <summary>How many degrees of the full 360° circle are visible at once.</summary>
    public float VisibleDegrees  { get; set; } = 90f;
    /// <summary>
    /// Lens/fisheye distortion strength. 1.0 = linear (no effect).
    /// Higher values show a wider FOV at the edges while keeping the centre at normal scale.
    /// 1.5 shows ~50 % more degrees; 2.0 shows ~100 % more.
    /// </summary>
    public float LensStrength    { get; set; } = 1.6f;
    /// <summary>Added to the computed heading (degrees). Set to 180 if N/S appear swapped.</summary>
    public float RotationOffset  { get; set; } = 0f;
    /// <summary>
    /// When true the compass tracks the camera yaw instead of the character's facing direction.
    /// Useful in third-person with a freely-rotating camera.
    /// </summary>
    public bool  UseCameraDirection { get; set; } = false;
    public float FontScale       { get; set; } = 1.0f;
    public bool  ShowHeadingText { get; set; } = true;

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
    public bool ShowNpcs          { get; set; } = false;
    public bool ShowGatheringNodes{ get; set; } = true;
    public bool ShowTreasure      { get; set; } = true;
    public float MaxMarkerDistance{ get; set; } = 60f;

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

    /// <summary>True if at least one marker type is enabled (used to skip the object-table loop).</summary>
    public bool ShowAnyMarkers =>
        ShowPlayers || ShowEnemies || ShowNpcs || ShowGatheringNodes || ShowTreasure;

    public void Save(IDalamudPluginInterface pi) => pi.SavePluginConfig(this);
}
