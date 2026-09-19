using Godot;

namespace TheRoom.Animation;

/// <summary>
/// The shared animation set every humanoid character plays. The clips are AnimationLibraries
/// imported from FBX with the humanoid retarget preset (see assets/animations/humanoid/README.md),
/// so their tracks target standard bone names (`%GeneralSkeleton:Hips`, `...:LeftUpperArm`) rather
/// than one model's own rig. Any character model imported with the same preset can play them.
///
/// A character uses DefaultPath unless its CharacterDef sets its own set, for example to
/// give one character a different run.
/// </summary>
[GlobalClass]
public partial class HumanoidAnimationSet : Resource
{
    public const string DefaultPath = "res://assets/animations/humanoid/humanoid_default.tres";

    /// <summary>Optional. Without one, idle holds the first frame of LightAttack, which is a
    /// standing stance in the Mixamo "Stabbing" clip, rather than a T-pose.</summary>
    [Export] public AnimationLibrary? Idle;
    [Export] public AnimationLibrary? Run;
    /// <summary>A jump while moving, and the fallback for the two below.</summary>
    [Export] public AnimationLibrary? Jump;
    /// <summary>Optional. A jump from standing still (no direction held).</summary>
    [Export] public AnimationLibrary? JumpUp;
    /// <summary>Optional. The parkour vault over a waist-high obstacle (jump towards it). Timed
    /// to the vault's air time when it plays.</summary>
    [Export] public AnimationLibrary? Vault;
    [Export] public AnimationLibrary? LightAttack;
    [Export] public AnimationLibrary? HeavyAttack;
    /// <summary>The roll (Cmd / Ctrl). Timed to Tuning.DodgeDuration when it plays.</summary>
    [Export] public AnimationLibrary? Dodge;
    /// <summary>Plays once when health reaches 0 and holds the last frame until respawn.</summary>
    [Export] public AnimationLibrary? Death;
    /// <summary>Character-specific: only a character whose ability is acted out has one (Zain's
    /// drop kick, in zain_animations.tres). Plays on the ability's tell.</summary>
    [Export] public AnimationLibrary? Ability;

    [ExportGroup("Attack clip playback")]
    /// <summary>Which slice of the attack clip to play, in seconds of the source clip. Mixamo
    /// clips often carry long lead-in or recovery that doesn't fit a 0.3s light attack.
    /// An end of 0 or less means "to the end of the clip".</summary>
    [Export] public float LightAttackClipStart;
    [Export] public float LightAttackClipEnd;
    [Export] public float HeavyAttackClipStart;
    [Export] public float HeavyAttackClipEnd;

    /// <summary>How long the attack animation lasts in game, in seconds. The slice above is sped up or
    /// slowed down to fit. Roughly windup + recovery from tuning.tres, so the swing lands with the hit.</summary>
    [Export] public float LightAttackDuration = 0.35f;
    [Export] public float HeavyAttackDuration = 0.9f;

    [ExportGroup("Jump and vault playback")]
    /// <summary>Where the standing jump starts, in seconds of the source clip: skip the crouch,
    /// since the body has already left the ground when the clip starts.</summary>
    [Export] public float JumpUpClipStart;
    /// <summary>Slice of the vault clip to play (the jump itself, without the run-up).</summary>
    [Export] public float VaultClipStart;
    [Export] public float VaultClipEnd;

    [ExportGroup("Dodge, death and ability playback")]
    /// <summary>Slice of the roll clip to play (the roll itself, without the get-up).</summary>
    [Export] public float DodgeClipStart;
    [Export] public float DodgeClipEnd;
    /// <summary>The whole death clip, sped up to fit before respawn (Tuning.RespawnTime).</summary>
    [Export] public float DeathDuration = 1.3f;
    /// <summary>Slice of the ability clip and how long it plays. Choose them so the strike lands
    /// as the ability's tell ends, since that's when the server applies the hit.</summary>
    [Export] public float AbilityClipStart;
    [Export] public float AbilityClipEnd;
    [Export] public float AbilityDuration = 1.0f;

    public static Godot.Animation? FirstClip(AnimationLibrary? library)
    {
        if (library is null)
            return null;
        var names = library.GetAnimationList();
        return names.Count > 0 ? library.GetAnimation(names[0]) : null;
    }
}
