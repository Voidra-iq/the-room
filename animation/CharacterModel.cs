using System.Collections.Generic;
using System.Linq;
using Godot;

namespace TheRoom.Animation;

/// <summary>
/// The visible body of a player: any humanoid model plus a HumanoidAnimationSet. It is purely
/// cosmetic, and collision stays on the Player's capsule. Works for any model imported with the
/// humanoid retarget preset (assets/animations/humanoid/README.md). Player.cs drives it through
/// UpdateLocomotion, PlayOneShot (attacks, dodge, ability), PlayDeath and Revive.
/// </summary>
public partial class CharacterModel : Node3D
{
    public const string DefaultModelPath = "res://assets/characters/zain/zain.fbx";
    public const string DefaultHeldPropPath = "res://assets/props/knife/knife.tscn";

    // Standard humanoid name after retargeting, so a prop attaches the same way on every model.
    private const string HandBone = "RightHand";

    public enum Clip { Idle, Run, Jump, JumpUp, Vault, LightAttack, HeavyAttack, Dodge, Death, Ability }

    private static readonly Dictionary<Clip, StringName> ClipNames = new()
    {
        [Clip.Idle] = "idle",
        [Clip.Run] = "run",
        [Clip.Jump] = "jump",
        [Clip.JumpUp] = "jump_up",
        [Clip.Vault] = "vault",
        [Clip.LightAttack] = "light_attack",
        [Clip.HeavyAttack] = "heavy_attack",
        [Clip.Dodge] = "dodge",
        [Clip.Death] = "death",
        [Clip.Ability] = "ability",
    };

    // The humanoid retarget renames the root bone to this on every model, so root-motion
    // stripping never needs to know which rig the clip came from.
    private const string RootBoneSuffix = ":Hips";

    private AnimationPlayer _animator = null!;
    private readonly List<StandardMaterial3D> _materials = new();
    private Clip? _current;
    private float _oneShotRemaining;
    private bool _dead; // holding the death pose until Revive()
    private float _authoredRunSpeed = 1f; // m/s the run clip's feet were animated for, at this model's scale
    private bool _wasOnFloor = true;
    private Clip _airClip = Clip.Jump; // picked at take-off and kept for the whole airtime
    private Skeleton3D? _skeleton;
    private float _scale = 1f;

    /// <summary>One duplicated material per mesh surface. Player tints these with the character
    /// colour and flashes them for tells, like it did with the grey-box capsule.</summary>
    public IReadOnlyList<StandardMaterial3D> Materials => _materials;
    public AnimationPlayer Animator => _animator;

    /// <summary>The prop in the right hand (the knife), if any.</summary>
    public Node3D? HeldProp { get; private set; }

    /// <summary>Builds the model without needing the scene tree, so tests can inspect it.
    /// <paramref name="height"/> is the height the model is scaled to (the hit capsule's).
    /// <paramref name="heldProp"/> is attached to the right hand; its own scene holds the grip offset.</summary>
    public static CharacterModel Create(PackedScene modelScene, HumanoidAnimationSet set, float height, PackedScene? heldProp = null)
    {
        var model = new CharacterModel { Name = "Model" };
        var root = modelScene.Instantiate<Node3D>();
        model.AddChild(root);

        // Humanoid retargeting puts models facing +Z; Player moves along -Z.
        root.RotateY(Mathf.Pi);

        // The imported model's own player only holds its bind-pose clip.
        foreach (var existing in root.FindChildren("*", nameof(AnimationPlayer), true, false))
        {
            existing.GetParent().RemoveChild(existing);
            existing.Free();
        }

        var skeleton = root.FindChildren("*", nameof(Skeleton3D), true, false).OfType<Skeleton3D>().FirstOrDefault();
        var restHeight = MeasureRestHeight(skeleton);
        var scale = restHeight > 0.01f ? height / restHeight : 1f;
        root.Scale = Vector3.One * scale;

        foreach (var mesh in root.FindChildren("*", nameof(MeshInstance3D), true, false).OfType<MeshInstance3D>())
        {
            for (var s = 0; s < mesh.GetSurfaceOverrideMaterialCount(); s++)
            {
                var material = mesh.GetActiveMaterial(s) is StandardMaterial3D source
                    ? (StandardMaterial3D)source.Duplicate()
                    : new StandardMaterial3D();
                mesh.SetSurfaceOverrideMaterial(s, material);
                model._materials.Add(material);
            }
        }

        // A child of the model root: the default root_node ("..") is then the model root, which
        // is where the clips' "%GeneralSkeleton" unique-name paths resolve.
        model._animator = new AnimationPlayer { Name = "AnimationPlayer", PlaybackDefaultBlendTime = 0.15 };
        root.AddChild(model._animator);
        model._animator.AddAnimationLibrary("", model.BuildLibrary(set, skeleton, scale));

        model._skeleton = skeleton;
        model._scale = scale;
        model.SetHeldProp(heldProp);
        return model;
    }

    /// <summary>Puts <paramref name="heldProp"/> in the right hand, replacing whatever was there
    /// (null: empty hand). Player swaps the knife for the Golden Knife while holding it.</summary>
    public void SetHeldProp(PackedScene? heldProp)
    {
        if (HeldProp is not null)
        {
            HeldProp.GetParent().RemoveChild(HeldProp);
            HeldProp.QueueFree();
            HeldProp = null;
        }
        if (heldProp is null || _skeleton is null || _skeleton.FindBone(HandBone) < 0)
            return;

        var attachment = _skeleton.GetNodeOrNull<BoneAttachment3D>("RightHandAttachment");
        if (attachment is null)
        {
            attachment = new BoneAttachment3D { Name = "RightHandAttachment", BoneName = HandBone };
            _skeleton.AddChild(attachment);
        }
        var prop = heldProp.Instantiate<Node3D>();
        // The model root is scaled up to the capsule height; undo it so the prop keeps its real size.
        prop.Scale = Vector3.One / _scale;
        attachment.AddChild(prop);
        HeldProp = prop;
    }

    public Godot.Animation? GetClip(Clip clip) =>
        _animator.HasAnimation(ClipNames[clip]) ? _animator.GetAnimation(ClipNames[clip]) : null;

    /// <summary>Called every frame with the body's velocity. Picks run, jump or idle unless an
    /// attack is still playing.</summary>
    public void UpdateLocomotion(float delta, Vector3 velocity, bool onFloor)
    {
        if (_dead)
            return;

        var flatSpeed = new Vector2(velocity.X, velocity.Z).Length();
        // Standing jump or moving jump is decided at take-off: air control can change the speed
        // mid-air, and switching clips there would pop.
        if (_wasOnFloor && !onFloor)
            _airClip = flatSpeed <= 0.5f && _animator.HasAnimation(ClipNames[Clip.JumpUp]) ? Clip.JumpUp : Clip.Jump;
        _wasOnFloor = onFloor;

        if (_oneShotRemaining > 0f)
        {
            _oneShotRemaining -= delta;
            if (_oneShotRemaining > 0f)
                return;
        }

        if (!onFloor)
        {
            Play(_airClip);
        }
        else if (flatSpeed > 0.5f)
        {
            Play(Clip.Run);
            // Match the feet to the ground instead of sliding; clamped so a sprint doesn't blur.
            _animator.SpeedScale = Mathf.Clamp(flatSpeed / _authoredRunSpeed, 0.6f, 1.6f);
        }
        else
        {
            Play(Clip.Idle);
        }
    }

    /// <summary>Plays a one-shot clip (attack, dodge, ability, death) stretched or squeezed to
    /// last <paramref name="duration"/> seconds. Locomotion resumes afterwards. Returns false (and
    /// does nothing) if this set has no such clip, or if the body is dead.</summary>
    public bool PlayOneShot(Clip clip, float duration)
    {
        if (!_animator.HasAnimation(ClipNames[clip]) || (_dead && clip != Clip.Death))
            return false;
        var length = (float)_animator.GetAnimation(ClipNames[clip]).Length;
        _current = clip;
        _animator.SpeedScale = 1f;
        _animator.Play(ClipNames[clip], customBlend: 0.05, customSpeed: duration > 0.01f ? length / duration : 1f);
        _animator.Seek(0, true);
        _oneShotRemaining = duration;
        return true;
    }

    /// <summary>Health hit 0: fall, and stay down until <see cref="Revive"/>.</summary>
    public void PlayDeath(float duration)
    {
        _dead = false; // so PlayOneShot accepts it even if a previous death wasn't revived
        PlayOneShot(Clip.Death, duration);
        _dead = true;
    }

    /// <summary>Respawned: back to normal locomotion.</summary>
    public void Revive()
    {
        _dead = false;
        _oneShotRemaining = 0f;
        _current = null;
        Play(Clip.Idle);
    }

    private void Play(Clip clip)
    {
        if (_current == clip || !_animator.HasAnimation(ClipNames[clip]))
            return;
        _current = clip;
        _animator.SpeedScale = 1f;
        _animator.Play(ClipNames[clip]);
    }

    private AnimationLibrary BuildLibrary(HumanoidAnimationSet set, Skeleton3D? skeleton, float scale)
    {
        var library = new AnimationLibrary();
        // Hips position keys are stored divided by the skeleton's motion scale (hip height), so
        // they have to be multiplied back to get metres.
        var motionScale = (skeleton?.MotionScale ?? 1f) * scale;

        var run = HumanoidAnimationSet.FirstClip(set.Run);
        if (run is not null)
        {
            var clip = StripRootMotion(run, clampRise: false, out var travelled);
            clip.LoopMode = Godot.Animation.LoopModeEnum.Linear;
            if (travelled > 0.01f && clip.Length > 0f)
                _authoredRunSpeed = travelled * motionScale / (float)clip.Length;
            library.AddAnimation(ClipNames[Clip.Run], clip);
        }

        var jump = HumanoidAnimationSet.FirstClip(set.Jump);
        if (jump is not null)
        {
            // The physics body already rises; the clip's own lift would double the jump height.
            // Crouch dips are kept, rises are clamped out.
            var clip = StripRootMotion(jump, clampRise: true, out _);
            clip.LoopMode = Godot.Animation.LoopModeEnum.None;
            library.AddAnimation(ClipNames[Clip.Jump], clip);
        }

        var jumpUp = HumanoidAnimationSet.FirstClip(set.JumpUp);
        if (jumpUp is not null)
        {
            var clip = Slice(StripRootMotion(jumpUp, clampRise: true, out _), set.JumpUpClipStart, (float)jumpUp.Length);
            clip.LoopMode = Godot.Animation.LoopModeEnum.None;
            library.AddAnimation(ClipNames[Clip.JumpUp], clip);
        }
        // A one-shot like the roll, but the body's vault already lifts it over the obstacle.
        AddOneShot(library, Clip.Vault, set.Vault, set.VaultClipStart, set.VaultClipEnd, clampRise: true);

        AddOneShot(library, Clip.LightAttack, set.LightAttack, set.LightAttackClipStart, set.LightAttackClipEnd);
        AddOneShot(library, Clip.HeavyAttack, set.HeavyAttack, set.HeavyAttackClipStart, set.HeavyAttackClipEnd);
        // The roll dips and the death falls: their vertical hip motion is kept, only the
        // horizontal drift goes (the body is moved by physics). The drop kick keeps its jump.
        AddOneShot(library, Clip.Dodge, set.Dodge, set.DodgeClipStart, set.DodgeClipEnd);
        AddOneShot(library, Clip.Death, set.Death, 0f, 0f);
        AddOneShot(library, Clip.Ability, set.Ability, set.AbilityClipStart, set.AbilityClipEnd);

        var idleSource = HumanoidAnimationSet.FirstClip(set.Idle);
        if (idleSource is not null)
        {
            var clip = StripRootMotion(idleSource, clampRise: false, out _);
            clip.LoopMode = Godot.Animation.LoopModeEnum.Linear;
            library.AddAnimation(ClipNames[Clip.Idle], clip);
        }
        else if (HumanoidAnimationSet.FirstClip(set.LightAttack) is { } stance)
        {
            library.AddAnimation(ClipNames[Clip.Idle], FirstFramePose(stance, 0f));
        }

        return library;
    }

    private static void AddOneShot(AnimationLibrary library, Clip clip, AnimationLibrary? source, float start, float end, bool clampRise = false)
    {
        var animation = HumanoidAnimationSet.FirstClip(source);
        if (animation is null)
            return;

        var sliceEnd = end > start ? Mathf.Min(end, (float)animation.Length) : (float)animation.Length;
        var sliced = Slice(StripRootMotion(animation, clampRise, out _), start, sliceEnd);
        sliced.LoopMode = Godot.Animation.LoopModeEnum.None;
        library.AddAnimation(ClipNames[clip], sliced);
    }

    /// <summary>Movement comes from the server-authoritative body, never the clip. Mixamo clips
    /// not exported "In Place" walk the hips forward and would drift away from the capsule and
    /// snap back every loop. This pins the root bone's horizontal position to its first key.</summary>
    private static Godot.Animation StripRootMotion(Godot.Animation source, bool clampRise, out float horizontalTravel)
    {
        var clip = (Godot.Animation)source.Duplicate(true);
        horizontalTravel = 0f;

        for (var t = 0; t < clip.GetTrackCount(); t++)
        {
            if (clip.TrackGetType(t) != Godot.Animation.TrackType.Position3D)
                continue;
            if (!clip.TrackGetPath(t).ToString().EndsWith(RootBoneSuffix))
                continue;

            var keyCount = clip.TrackGetKeyCount(t);
            if (keyCount == 0)
                continue;

            var first = clip.TrackGetKeyValue(t, 0).AsVector3();
            var last = clip.TrackGetKeyValue(t, keyCount - 1).AsVector3();
            horizontalTravel = new Vector2(last.X - first.X, last.Z - first.Z).Length();

            for (var k = 0; k < keyCount; k++)
            {
                var value = clip.TrackGetKeyValue(t, k).AsVector3();
                value.X = first.X;
                value.Z = first.Z;
                if (clampRise)
                    value.Y = Mathf.Min(value.Y, first.Y);
                clip.TrackSetKeyValue(t, k, value);
            }
        }

        return clip;
    }

    /// <summary>Copies keys in [start, end] into a new clip starting at 0, keeping the pose at
    /// the start boundary so the first frame doesn't pop.</summary>
    private static Godot.Animation Slice(Godot.Animation source, float start, float end)
    {
        if (start <= 0f && end >= source.Length)
            return source;

        var clip = new Godot.Animation { Length = Mathf.Max(0.05f, end - start) };
        for (var t = 0; t < source.GetTrackCount(); t++)
        {
            var track = clip.AddTrack(source.TrackGetType(t));
            clip.TrackSetPath(track, source.TrackGetPath(t));
            clip.TrackInsertKey(track, 0, SampleTrack(source, t, start));
            for (var k = 0; k < source.TrackGetKeyCount(t); k++)
            {
                var time = source.TrackGetKeyTime(t, k);
                if (time > start && time <= end)
                    clip.TrackInsertKey(track, time - start, source.TrackGetKeyValue(t, k));
            }
        }
        return clip;
    }

    private static Godot.Animation FirstFramePose(Godot.Animation source, float at)
    {
        var pose = new Godot.Animation { Length = 0.1f, LoopMode = Godot.Animation.LoopModeEnum.Linear };
        var stripped = StripRootMotion(source, clampRise: false, out _);
        for (var t = 0; t < stripped.GetTrackCount(); t++)
        {
            var track = pose.AddTrack(stripped.TrackGetType(t));
            pose.TrackSetPath(track, stripped.TrackGetPath(t));
            pose.TrackInsertKey(track, 0, SampleTrack(stripped, t, at));
        }
        return pose;
    }

    private static Variant SampleTrack(Godot.Animation clip, int track, float time) => clip.TrackGetType(track) switch
    {
        Godot.Animation.TrackType.Position3D => clip.PositionTrackInterpolate(track, time),
        Godot.Animation.TrackType.Rotation3D => clip.RotationTrackInterpolate(track, time),
        Godot.Animation.TrackType.Scale3D => clip.ScaleTrackInterpolate(track, time),
        _ => clip.TrackGetKeyValue(track, Mathf.Max(0, clip.TrackFindKey(track, time))),
    };

    /// <summary>Highest bone in the rest pose, from the feet at 0. Mixamo rigs end in a HeadTop
    /// bone, so this is the full height; rigs without one measure to the head bone and come
    /// out slightly large.</summary>
    private static float MeasureRestHeight(Skeleton3D? skeleton)
    {
        if (skeleton is null)
            return 0f;
        var top = 0f;
        for (var i = 0; i < skeleton.GetBoneCount(); i++)
            top = Mathf.Max(top, (skeleton.Transform * skeleton.GetBoneGlobalRest(i)).Origin.Y);
        return top;
    }
}
