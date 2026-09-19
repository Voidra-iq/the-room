using Godot;

namespace TheRoom.Config;

/// <summary>
/// The single source of truth for every gameplay number in the game (GDD §9 / CHARACTER-SPEC.md Part 4).
/// Character/ability owners control *shape* (code); this file controls *strength* (numbers).
/// Edit only the .tres instance (res://tuning/tuning.tres), never hardcode a number in gameplay code.
/// </summary>
[GlobalClass]
public partial class Tuning : Resource
{
    [ExportGroup("Melee — Light")]
    [Export] public float LightWindup = 0.12f;
    [Export] public float LightDamagePercent = 0.35f;
    [Export] public float LightRange = 2.0f;
    [Export] public float LightHitRadius = 0.9f;
    [Export] public float LightRecovery = 0.15f;

    [ExportGroup("Melee — Heavy Lunge")]
    [Export] public float HeavyWindup = 0.40f;
    [Export] public float HeavyDamagePercent = 0.525f; // 1.5x a light (normal) hit
    [Export] public float HeavyLungeRange = 3.0f;
    [Export] public float HeavyHitRadius = 1.1f;
    [Export] public float HeavyRecovery = 0.6f;
    [Export] public float HeavyStaggerDuration = 0.4f;

    // The roll replaces parry and the old dash (user decision 2026-09-14, plan/main.md D7): strikes
    // pass through you for the whole roll.
    [ExportGroup("Dodge (roll)")]
    [Export] public float DodgeDuration = 0.55f;
    [Export] public float DodgeDistance = 3.5f;
    [Export] public float DodgeCooldown = 0f; // extra wait after the roll ends; 0 = stamina is the only limit

    [ExportGroup("Movement")]
    [Export] public float SprintSpeedMultiplier = 1.6f; // while Shift is held

    // Stamina limits sprint and dodge. Server-authoritative; the owner predicts it (Player.SimulateStep).
    [ExportGroup("Stamina")]
    [Export] public float MaxStamina = 100f;
    [Export] public float SprintStaminaPerSecond = 22f;
    [Export] public float DodgeStaminaCost = 30f;
    [Export] public float StaminaRegenPerSecond = 30f;
    [Export] public float StaminaRegenDelay = 0.8f; // pause after any use before it refills
    [Export] public float SprintMinStamina = 15f; // after running dry, sprint needs this much back

    [ExportGroup("Melee — Execute")]
    [Export] public float ExecuteAnimationLock = 0.6f;
    [Export] public float ExecuteBehindAngleDegrees = 60.0f;

    [ExportGroup("Health / TTK")]
    [Export] public float MaxHealth = 100.0f;
    [Export] public float HealthRegenDelay = 3.0f;    // seconds without being hit before healing starts
    [Export] public float HealthRegenAmount = 1.0f;   // HP per heal tick
    [Export] public float HealthRegenInterval = 1.0f; // seconds between heal ticks

    [ExportGroup("Respawn")]
    [Export] public float RespawnTime = 1.5f;
    [Export] public float RespawnTimeLastCall = 1.0f;
    [Export] public float SpawnProtectionDuration = 1.5f;

    [ExportGroup("Golden Knife")]
    [Export] public float GoldenKnifeFirstSpawn = 45.0f;
    [Export] public float GoldenKnifeRespawnDelay = 30.0f;
    [Export] public float GoldenKnifeDuration = 20.0f;
    [Export] public float GoldenKnifeScoreMultiplier = 2.0f;

    [ExportGroup("Bounty")]
    [Export] public int BountyAnnounceOnATear = 3;
    [Export] public int BountyAnnounceSecond = 5;
    [Export] public int BountyAnnounceThird = 8;

    [ExportGroup("Match")]
    [Export] public int ScoreTargetDuelPit = 25;
    [Export] public int ScoreTargetChaos = 40;
    [Export] public float MatchTimeLimitSeconds = 480.0f; // 8 min
    [Export] public float LastCallThresholdPercent = 0.75f;
    [Export] public float LastCallTimeRemainingSeconds = 90.0f; // also enter Last Call inside this much time left, so a timer-ended match still climaxes
    [Export] public float ResultsScreenDurationSeconds = 8.0f;
    [Export] public float DeathCamDuration = 1.5f;

    [ExportGroup("Golden Knife pickup")]
    [Export] public float GoldenKnifePickupRadius = 2.5f;

    [ExportGroup("Ability grammar — hard rules (CHARACTER-SPEC.md Part 1)")]
    [Export] public float MinAbilityTellTime = 0.3f;
    [Export] public float MaxControlEffectDuration = 1.0f;
    [Export] public float AbilityCooldownMin = 7.0f; // lowered from 12 (user decision D8) for Zain's 7s Drop Kick
    [Export] public float AbilityCooldownMax = 25.0f;

    [ExportGroup("Networking")]
    [Export] public int ServerTickRateHz = 30;
    [Export] public float MaxRewindTimeSeconds = 0.2f; // 200ms, GDD §4
    [Export] public float InterpolationDelaySeconds = 0.1f; // 100ms
    [Export] public float PingIntervalSeconds = 1.0f;
    [Export] public float ReconciliationSnapDistance = 1.5f; // beyond this, teleport-correct instead of smoothing
    [Export] public float ReconciliationSmoothTime = 0.12f;

    [ExportGroup("Safety")]
    [Export] public float VoidCatchY = -20.0f; // fall below this world Y anywhere -> reset to a spawn point

    [ExportGroup("Movement — jump/hop (GDD §5.1, TBD via grey-box A/B)")]
    [Export] public bool HopEnabled = false; // toggle here for the A/B test; playtest decides, not this default
    [Export] public float HopImpulse = 4.0f;

    [ExportGroup("Movement — vault (parkour over waist-high cover)")]
    [Export] public float VaultReach = 0.8f;       // how far in front of the capsule an obstacle can start
    [Export] public float VaultMinHeight = 0.5f;   // lower than this is a normal hop (curbs, ramps)
    [Export] public float VaultMaxHeight = 1.5f;   // taller than this is a wall
    [Export] public float VaultClearance = 0.4f;   // how far above the obstacle's top the feet peak
    [Export] public float VaultSpeed = 5.0f;       // horizontal speed, held from take-off to landing

    /// <summary>
    /// Per-ability numbers, keyed "&lt;abilityId&gt;.&lt;paramName&gt;" (e.g. "dropkick.range",
    /// "firepatch.damage_per_tick"). CHARACTER-SPEC.md Part 4: ability owners control an
    /// ability's *shape* in code (abilities/&lt;id&gt;/*.cs); every number it uses lives here
    /// instead, so the single tuning-file owner can rebalance without touching an owner's code.
    /// AbilityDef.ValidateHardRules() (abilities/AbilityValidator.cs) checks the *Cooldown and
    /// *TellSeconds keys specifically against AbilityCooldownMin/Max and MinAbilityTellTime.
    /// </summary>
    [ExportGroup("Abilities — per-ability numbers (CHARACTER-SPEC.md Part 4)")]
    [Export] public Godot.Collections.Dictionary<string, float> AbilityNumbers = new();

    public float GetAbilityNumber(string abilityId, string param, float fallback = 0f)
    {
        var key = $"{abilityId}.{param}";
        return AbilityNumbers.TryGetValue(key, out var value) ? value : fallback;
    }
}
