using Godot;
using TheRoom.Animation;

namespace TheRoom.Tools;

/// <summary>
/// Side-view preview of a character model playing every clip in the shared animation set,
/// through the same CharacterModel code the game uses. Use it to check a new model before
/// registering it: `make preview-animations MODEL=res://assets/characters/&lt;name&gt;/&lt;name&gt;.fbx`.
/// Cycles idle, jump_up, run, jump, vault, light, heavy, dodge, death (then revive) and the ability
/// clip if the set has one. Flags: --closeup frames the hands, --set=res://…tres previews another
/// animation set (Zain's drop kick), --prop=res://…tscn holds another prop (the Golden Knife),
/// --only=vault|dodge|death|ability|light|heavy repeats one clip (for picking slices).
/// </summary>
public partial class AnimationPreview : Node3D
{
    private const float Height = 1.8f; // the Player capsule's height, which CharacterModel scales to

    private sealed record Step(string Name, float Seconds, System.Action? Start, Vector3 Velocity, bool OnFloor = true);

    private CharacterModel _model = null!;
    private HumanoidAnimationSet _set = null!;
    private readonly System.Collections.Generic.List<Step> _steps = new();
    private int _step = -1;
    private float _stepRemaining;

    public override void _Ready()
    {
        var modelPath = CharacterModel.DefaultModelPath;
        var setPath = HumanoidAnimationSet.DefaultPath;
        var propPath = CharacterModel.DefaultHeldPropPath;
        var closeUp = false;
        string? only = null;
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--model="))
                modelPath = arg["--model=".Length..];
            if (arg.StartsWith("--set="))
                setPath = arg["--set=".Length..];
            if (arg.StartsWith("--prop="))
                propPath = arg["--prop=".Length..];
            if (arg.StartsWith("--only="))
                only = arg["--only=".Length..];
            closeUp |= arg == "--closeup"; // hands and the held prop, for seating the knife
        }

        var camera = new Camera3D { Position = closeUp ? new Vector3(1.4f, 1.3f, -0.6f) : new Vector3(4.2f, 1.0f, 0f), Current = true };
        AddChild(camera);
        camera.LookAt(closeUp ? new Vector3(0f, 1.05f, -0.15f) : new Vector3(0f, 0.9f, 0f));

        AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-45f, 60f, 0f), ShadowEnabled = true });
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.15f, 0.15f, 0.18f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.55f, 0.55f, 0.6f),
            },
        });
        AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(6f, 6f) } });

        _set = GD.Load<HumanoidAnimationSet>(setPath);
        _model = CharacterModel.Create(GD.Load<PackedScene>(modelPath), _set, Height, GD.Load<PackedScene>(propPath));
        AddChild(_model);
        GD.Print($"[Preview] {modelPath} with {setPath}");

        var dodge = TheRoom.Core.TuningService.Instance.DodgeDuration;
        var all = new[]
        {
            new Step("idle", 1.0f, null, Vector3.Zero),
            // Standing still at take-off picks the standing jump, moving picks the running one.
            new Step("jump_up", 0.8f, null, Vector3.Zero, OnFloor: false),
            new Step("run", 1.6f, null, new Vector3(0f, 0f, -6f)),
            new Step("jump", 1.0f, null, new Vector3(0f, 0f, -6f), OnFloor: false),
            new Step("vault", 0.9f + 0.4f, () => _model.PlayOneShot(CharacterModel.Clip.Vault, 0.9f), Vector3.Zero),
            new Step("light", _set.LightAttackDuration + 0.4f, () => _model.PlayOneShot(CharacterModel.Clip.LightAttack, _set.LightAttackDuration), Vector3.Zero),
            new Step("heavy", _set.HeavyAttackDuration + 0.4f, () => _model.PlayOneShot(CharacterModel.Clip.HeavyAttack, _set.HeavyAttackDuration), Vector3.Zero),
            new Step("dodge", dodge + 0.4f, () => _model.PlayOneShot(CharacterModel.Clip.Dodge, dodge), Vector3.Zero),
            new Step("death", _set.DeathDuration + 0.8f, () => _model.PlayDeath(_set.DeathDuration), Vector3.Zero),
            new Step("revive", 0.3f, () => _model.Revive(), Vector3.Zero),
            new Step("ability", _set.AbilityDuration + 0.4f, () => _model.PlayOneShot(CharacterModel.Clip.Ability, _set.AbilityDuration), Vector3.Zero),
        };
        foreach (var step in all)
        {
            if ((step.Name == "ability" && _set.Ability is null) || (step.Name == "vault" && _set.Vault is null))
                continue;
            if (only is null || step.Name == only || (only == "death" && step.Name == "revive"))
                _steps.Add(step);
        }
    }

    public override void _Process(double delta)
    {
        _stepRemaining -= (float)delta;
        if (_stepRemaining > 0f)
        {
            var current = _steps[_step];
            if (current.Start is null) // locomotion clips need a steady "velocity" to stay selected
                _model.UpdateLocomotion((float)delta, current.Velocity, current.OnFloor);
            return;
        }

        _step = (_step + 1) % _steps.Count;
        var next = _steps[_step];
        _stepRemaining = next.Seconds;
        next.Start?.Invoke();
        GD.Print($"[Preview] {next.Name}");
    }
}
