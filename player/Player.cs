using System.Collections.Generic;
using Godot;
using TheRoom.Effects;
using TheRoom.Abilities;
using TheRoom.Animation;
using TheRoom.Config;
using TheRoom.Core;
using TheRoom.UI;

namespace TheRoom.Entities;

/// <summary>
/// Player controller with three distinct simulation roles depending on who's looking at it
/// (see plan/phase-1-network-spike.md):
///   - Server: always runs the ONE authoritative physics + combat simulation, driven by the
///     latest input received from the owning client.
///   - Owning client: predicts movement locally from real input (zero perceived latency),
///     sends that input to the server, and reconciles toward the server's periodic
///     corrections when they disagree. Combat verbs are NOT predicted — they go straight to
///     the server and wait for the result (Pillar 2: every hit must be explainable, so a
///     locally-guessed hit that gets overruled would be exactly the wrong kind of "fun").
///   - Every other client (watching a peer that isn't them and isn't the server): runs no
///     physics at all — just interpolates visually between buffered server snapshots,
///     delayed by Tuning.InterpolationDelaySeconds.
/// Offline mode (no MultiplayerPeer) skips all of the above and behaves like a single predicting
/// client with no server round-trip at all — combat resolves locally and instantly.
///
/// Combat (Phase 2, GDD §5.2 / CHARACTER-SPEC.md grammar): a server-authoritative state machine
/// per player — Idle → {LightWindup, HeavyWindup, Dodging} → Recovery/Staggered/Executing →
/// Idle, plus a terminal Dead state. Hit resolution reuses the rewind lag-compensation proven in
/// Phase 1 (core/CombatServer.cs), parameterized per verb.
/// </summary>
public partial class Player : CharacterBody3D
{
    [Export] public float MoveSpeed = 6.0f;
    [Export] public float MouseSensitivity = 0.0035f;
    [Export] public float MinPitchDegrees = -60f;
    [Export] public float MaxPitchDegrees = 70f;

    [Export] public NodePath SpringArmPath = "CameraPivot/SpringArm3D";
    [Export] public NodePath MeshPath = "MeshInstance3D";
    [Export] public Label3D? NameLabel;

    private SpringArm3D _springArm = null!;
    // The camera turns on its own (mouse) and the body turns to face where it moves, the usual
    // third-person setup. Before this the body WAS the camera: walking back or sideways made the
    // character slide backwards or sideways, still facing forward.
    private Node3D _cameraPivot = null!;
    private float _cameraYaw;
    // After an attack or ability the body keeps facing the camera for a moment, so the swing,
    // lunge or kick goes where you aimed instead of curving with your movement.
    private float _aimLockRemaining;
    private const float AimLockSeconds = 0.8f;
    private bool _faceCentrePending;
    private const float StickLookSpeed = 3.2f;   // radians/s at full right-stick tilt
    private const float StickPitchFactor = 0.6f; // vertical look is slower, as in most pad shooters
    private const float TurnRate = 14f; // per second, exponential: most of a turn in ~0.15 s
    private MeshInstance3D? _meshInstance;
    // Everything tinted with the character colour and flashed for tells: the grey-box capsule's
    // material, or every surface of the character model once one is attached.
    private readonly List<StandardMaterial3D> _bodyMaterials = new();
    private CharacterModel? _model;
    private Vector3 _lastVisualPosition;
    private Vector3 _visualVelocity;
    private float _visualAirborneHold;
    private float _pitchRadians;

    // --- role, decided once in _Ready ---
    private bool _isServer;
    private bool _isOffline;
    private bool _isOwner;      // this machine predicts/controls this node (owning client, or offline local player)
    private bool _isRemoteView; // another client watching a peer that is neither them nor the server
    private bool _isBot;
    private long _peerId;
    public long PeerId => _peerId;

    // --- client-side prediction (owning client, networked only) ---
    private readonly List<PredictedState> _predictedHistory = new();
    private readonly struct PredictedState
    {
        public readonly ulong Tick;
        public readonly Vector3 Position;
        public PredictedState(ulong tick, Vector3 position) { Tick = tick; Position = position; }
    }

    // --- server-authoritative input, latest received from the owning client (server only) ---
    private Vector2 _serverPendingInput;
    private float _serverPendingYaw;
    private int _jumpCounter;            // owner: total jump presses so far
    private int _serverLastJumpCounter;  // server: highest count seen from the owner
    private bool _serverJumpQueued;

    // --- remote entity interpolation (remote-view clients only) ---
    private readonly List<RemoteSnapshot> _remoteSnapshots = new();
    private readonly struct RemoteSnapshot
    {
        public readonly double ReceivedAt;
        public readonly Vector3 Position;
        public readonly float YawRadians;
        public RemoteSnapshot(double receivedAt, Vector3 position, float yaw) { ReceivedAt = receivedAt; Position = position; YawRadians = yaw; }
    }

    // --- combat state machine (authoritative on the server; every peer's own copy of ITS OWN
    // node also mirrors it locally purely to gate re-sending an input the server will reject
    // anyway — never trusted for hit outcomes) ---
    private enum CombatState { Idle, LightWindup, HeavyWindup, Recovery, Dodging, Staggered, Executing, Dead }
    private enum Verb { Light, Heavy, Dodge }

    private CombatState _combatState = CombatState.Idle;
    private float _stateTimer;
    private float _dodgeCooldownRemaining;
    private float _spawnProtectionRemaining;
    private float _health;

    // The dodge roll overrides horizontal movement for its duration. It runs on the server
    // (authoritative) and is predicted on the owner, so the roll starts the instant the key is
    // pressed instead of snapping forward a round-trip later (the old dash did exactly that).
    private Vector3 _dodgeVelocity;
    private float _dodgeTimeRemaining;
    // Owner-side guesses, so a roll the server is sure to refuse isn't predicted: right after an
    // attack (windup + recovery) or while the cooldown runs.
    private double _localDodgeReadyAt;
    private double _localBusyUntil;
    private bool _serverPendingSprint;

    // Jumping towards waist-high cover vaults it: a higher jump sized to the obstacle, with the
    // horizontal speed held until landing. Same place as the dodge: SimulateStep, so the server
    // runs it for real and the owner predicts it.
    private Vector3 _vaultVelocity;
    private bool _vaulting;

    // Stamina: spent by sprinting and dodging, refills after a short pause. The server's value is
    // authoritative; the owner runs the same rules as a prediction (SimulateStep) and snaps to the
    // server's value when they drift apart.
    private float _stamina;
    private float _staminaRegenIn;
    private bool _sprinting;
    private double _abilityReadyAt; // owner: when the ability comes off cooldown, for the HUD

    public bool IsDead => _combatState == CombatState.Dead;
    /// <summary>Mid-roll: strikes pass through (CombatServer skips this player's hitbox).</summary>
    public bool IsDodging => _combatState == CombatState.Dodging;
    public bool IsSpawnProtected => _spawnProtectionRemaining > 0f;
    public float HealthFraction => Mathf.Clamp(_health / Mathf.Max(1f, TuningService.Instance.MaxHealth), 0f, 1f);
    public float Health => _health;
    public float StaminaFraction => Mathf.Clamp(_stamina / Mathf.Max(1f, TuningService.Instance.MaxStamina), 0f, 1f);
    /// <summary>Ran dry and can't sprint until it's back to Tuning.SprintMinStamina.</summary>
    public bool IsExhausted => !_sprinting && _stamina < TuningService.Instance.SprintMinStamina;
    /// <summary>Owner only (HUD): seconds until the ability can be used again, 0 when ready.</summary>
    public float AbilityCooldownRemaining => Mathf.Max(0f, (float)(_abilityReadyAt - Time.GetTicksMsec() / 1000.0));
    public float AbilityCooldownTotal => _characterDef?.Ability is { UsesCooldown: true } def
        ? TuningService.Instance.GetAbilityNumber(def.Id, "cooldown", TuningService.Instance.AbilityCooldownMin)
        : 0f;

    // --- bot AI (owning client with --bot; see core/Net.cs) ---
    private Vector2 _botMoveInput;
    private Vector3 _botTarget;
    private double _botRetargetIn;
    private double _botVerbIn;
    private double _botAbilityIn;

    // --- debug overlay stats (owning client only, see core/DebugOverlay.cs) ---
    public int AttackRequestCount { get; private set; }
    public int ConfirmedHitCount { get; private set; }
    public float LastRewindMs { get; private set; }
    public bool IsLocallyControlled => _isOwner;
    public bool IsBotControlled => _isBot;
    public string RoleLabel => _isServer ? "Server" : _isOffline ? "Offline" : _isBot ? "Bot" : _isOwner ? "Client (owner)" : "Remote view";

    // --- death cam (owning client only) ---
    private float _deathCamRemaining;
    private Vector3 _deathCamLookAt;

    // --- character/ability (Phase 3, CHARACTER-SPEC.md) ---
    private CharacterDef? _characterDef;
    private Ability? _ability;
    private string _displayName = "";
    public string DisplayName => _displayName;
    public CharacterDef? Character => _characterDef;

    // Ability shared-kit state (abilities/Ability.cs ApplySlow/Reveal helpers write these).
    private float _slowMultiplier = 1f;
    private float _slowTimeRemaining;
    private float _revealRemaining;

    public override void _Ready()
    {
        _springArm = GetNode<SpringArm3D>(SpringArmPath);
        _cameraPivot = GetNode<Node3D>("CameraPivot");
        _cameraYaw = GlobalRotation.Y;
        _faceCentrePending = true;
        // The exported NameLabel was never assigned in Player.tscn, so SetDisplayName() silently
        // did nothing from Phase 0 until the first time anyone actually looked at a rendered frame.
        NameLabel ??= GetNodeOrNull<Label3D>("NameLabel");
        _meshInstance = GetNodeOrNull<MeshInstance3D>(MeshPath);
        if (_meshInstance?.GetActiveMaterial(0) is StandardMaterial3D baseMat)
        {
            var capsuleMaterial = (StandardMaterial3D)baseMat.Duplicate();
            _meshInstance.SetSurfaceOverrideMaterial(0, capsuleMaterial);
            _bodyMaterials.Add(capsuleMaterial);
        }

        _health = TuningService.Instance.MaxHealth;
        _stamina = TuningService.Instance.MaxStamina;
        _peerId = long.TryParse(Name, out var parsedId) ? parsedId : GetMultiplayerAuthority();

        // MultiplayerSpawner replicates node creation, NOT the multiplayer-authority flag — every
        // peer's own local copy of a spawned node defaults to authority 1 (the server) until it
        // sets this itself. The node's Name is the owning peer id (see Main.SpawnPlayer), so every
        // peer — including the owner — can derive the correct authority here identically.
        SetMultiplayerAuthority((int)_peerId);

        _isServer = Net.Instance.IsServer;
        _isOffline = Net.Instance.IsOffline;
        _isOwner = IsMultiplayerAuthority();
        _isRemoteView = !_isServer && !_isOwner;
        _isBot = _isOwner && !_isServer && !_isOffline && Net.Instance.IsBot;

        if (_isServer)
            CombatServer.Instance.RegisterPlayer(_peerId, this);

        if (_isOwner)
        {
            var camera = _springArm.GetNodeOrNull<Camera3D>("Camera3D");
            camera?.MakeCurrent();

            // You don't need to read your own name — and when the spring arm pulls the camera in
            // against a wall, your own label sits right in front of the lens (seen in a real
            // rendered frame, not guessed).
            if (NameLabel is not null)
                NameLabel.Visible = false;
            PickNewBotTarget();

            if (!_isBot)
                Input.MouseMode = Input.MouseModeEnum.Captured;
        }

        // A freshly spawned player is protected on the server (which decides damage) and shows the
        // shimmer on its owner's screen. Later respawns and cancels are broadcast to everyone.
        if (_isServer || _isOwner)
            _spawnProtectionRemaining = TuningService.Instance.SpawnProtectionDuration;

        // Identity handshake: the owning client tells the server its chosen name + character;
        // the server validates and broadcasts the result to everyone (this node's Name label,
        // Main's peer-id->name registry for killfeed/scoreboard, and the equipped ability all
        // depend on every peer — not just the server — actually receiving this). Before this,
        // SetDisplayName() was only ever called locally on the server's own copy, which
        // MultiplayerSpawner never replicates — a real bug: every client's name label and
        // killfeed/scoreboard entries silently stayed blank/placeholder for anyone but the
        // server. See plan/phase-3-abilities.md.
        if (_isOffline)
        {
            ApplyIdentity(Net.Instance.LocalPlayerName, Net.Instance.ChosenCharacterId);
        }
        else if (_isOwner)
        {
            RpcId(1, nameof(AnnounceIdentity), Net.Instance.LocalPlayerName, Net.Instance.ChosenCharacterId ?? "");
        }
        else if (_isRemoteView)
        {
            // Late-join fix: the server's ReceiveIdentity broadcast only fires once, when each
            // player announces — anyone who connects *afterwards* never heard it and would show
            // every earlier player with the default colour/name forever. Asking from this node's
            // own _Ready guarantees the node already exists on this client when the reply lands.
            RpcId(1, nameof(RequestIdentity));
        }
    }

    public override void _ExitTree()
    {
        if (_isServer)
            CombatServer.Instance.UnregisterPlayer(_peerId);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!_isOwner || _isBot || GameMenu.IsOpen)
            return;

        if (@event is InputEventMouseMotion mouseMotion && Input.MouseMode == Input.MouseModeEnum.Captured)
            Look(mouseMotion.Relative.X * MouseSensitivity, mouseMotion.Relative.Y * MouseSensitivity);
    }

    private void Look(float yawRadians, float pitchRadians)
    {
        _cameraYaw -= yawRadians;
        _pitchRadians = Mathf.Clamp(
            _pitchRadians - pitchRadians,
            Mathf.DegToRad(MinPitchDegrees),
            Mathf.DegToRad(MaxPitchDegrees));

        var armRotation = _springArm.Rotation;
        armRotation.X = _pitchRadians;
        _springArm.Rotation = armRotation;
    }

    /// <summary>Right stick camera and the verbs. Verbs are polled rather than read from events
    /// because a trigger (RT = heavy) sends a stream of motion events while held; "just pressed"
    /// fires once for a key, a click or a trigger alike.</summary>
    private void PollOwnerInput(double delta)
    {
        // Spawns are in the perimeter lane. Point the camera at the arena, not the wall behind:
        // once, when the (possibly still syncing) position has left the origin.
        if (_faceCentrePending && new Vector2(GlobalPosition.X, GlobalPosition.Z).LengthSquared() > 1f)
        {
            _faceCentrePending = false;
            _cameraYaw = YawFacing(new Vector3(-GlobalPosition.X, 0f, -GlobalPosition.Z).Normalized());
        }

        if (GameMenu.IsOpen)
            return;

        var stick = Input.GetVector("look_left", "look_right", "look_up", "look_down");
        if (stick != Vector2.Zero)
        {
            // Squared response: fine aim near the centre, fast turns at full tilt.
            var step = stick * stick.Length() * StickLookSpeed * (float)delta;
            Look(step.X, step.Y * StickPitchFactor);
        }

        if (IsDead)
            return; // no verbs while dead/in death cam

        if (Input.IsActionJustPressed("attack_light")) RequestVerbLocal(Verb.Light);
        if (Input.IsActionJustPressed("attack_heavy")) RequestVerbLocal(Verb.Heavy);
        if (Input.IsActionJustPressed("dodge")) RequestVerbLocal(Verb.Dodge);
        if (Input.IsActionJustPressed("ability")) RequestAbilityLocal();
    }

    public override void _Process(double delta)
    {
        if (_isRemoteView)
            InterpolateRemote();

        if (_isBot)
            RunBotAi(delta);

        if (_isOwner && !_isBot)
            PollOwnerInput(delta);

        if (_isOwner && _deathCamRemaining > 0f)
            RunDeathCam(delta);

        if (_isOwner)
        {
            // Bots have no mouse: their camera just follows the body, as before.
            if (_isBot)
                _cameraYaw = GlobalRotation.Y;
            _cameraPivot.GlobalRotation = new Vector3(0f, _cameraYaw, 0f);
        }

        UpdateVisualEffects(delta);
        UpdateModelAnimation((float)delta);
    }

    public override void _PhysicsProcess(double delta)
    {
        // QueueFree() (disconnect handling, Main.OnPlayerDisconnected) defers actual removal to
        // end-of-frame — a node can still get one more _PhysicsProcess tick after being queued
        // for removal, and GlobalPosition/Rpc() on a node no longer in the tree logs a Godot
        // engine error. Cheap, standard guard for that race.
        if (!IsInsideTree())
            return;

        // Every copy of a player counts its own protection down: server, owner, remote views and
        // practice. It used to tick only on the server and in practice, so on an online client
        // your own character stayed white forever.
        if (_spawnProtectionRemaining > 0f)
            _spawnProtectionRemaining = Mathf.Max(0f, _spawnProtectionRemaining - (float)delta);

        if (_isServer)
        {
            TickCombatState((float)delta);
            _ability?.Tick((float)delta);
            ServerTickRegen((float)delta);
            RunServerPhysics(delta);
        }
        else if (_isOwner && !_isOffline)
        {
            RunPredictedPhysics(delta);
        }
        else if (_isOffline)
        {
            TickCombatState((float)delta); // offline: this instance is also its own authority
            _ability?.Tick((float)delta);
            ServerTickRegen((float)delta);
            RunOfflinePhysics(delta);
        }
        // Remote-view clients simulate nothing here — see InterpolateRemote() in _Process.
    }

    // ------------------------------------------------------------------
    // Movement — shared step function, three different drivers
    // ------------------------------------------------------------------

    /// <summary><paramref name="worldDir"/> is the wanted move direction in world space (length ≤ 1).</summary>
    private void SimulateStep(Vector3 worldDir, double delta, bool jump = false, bool sprint = false)
    {
        var velocity = Velocity;

        if (_vaulting && IsOnFloor() && velocity.Y <= 0f)
            _vaulting = false; // landed, on the far side or on top

        if (!IsOnFloor())
            velocity.Y -= Gravity * (float)delta;
        else if (jump && TuningService.Instance.HopEnabled)
        {
            var wanted = worldDir.Normalized();
            if (wanted.LengthSquared() > 0.0001f && FindVault(GetWorld3D().DirectSpaceState, GetRid(), GlobalPosition, wanted, TuningService.Instance, out var height))
                velocity.Y = StartVault(wanted, height);
            else
                velocity.Y = TuningService.Instance.HopImpulse;
        }

        if (_dodgeTimeRemaining > 0f)
        {
            // The roll overrides normal horizontal input for its whole duration.
            velocity.X = _dodgeVelocity.X;
            velocity.Z = _dodgeVelocity.Z;
            _dodgeTimeRemaining -= (float)delta;
        }
        else if (_vaulting)
        {
            velocity.X = _vaultVelocity.X;
            velocity.Z = _vaultVelocity.Z;
        }
        else
        {
            var direction = worldDir.Normalized();
            var sprintMultiplier = UpdateStamina(sprint && direction.LengthSquared() > 0.0001f, (float)delta)
                ? TuningService.Instance.SprintSpeedMultiplier
                : 1f;
            var effectiveSpeed = MoveSpeed * sprintMultiplier * _slowMultiplier; // ability shared-kit: Ability.ApplySlow

            if (direction.LengthSquared() > 0.0001f)
            {
                velocity.X = direction.X * effectiveSpeed;
                velocity.Z = direction.Z * effectiveSpeed;
            }
            else
            {
                velocity.X = Mathf.MoveToward(velocity.X, 0f, effectiveSpeed);
                velocity.Z = Mathf.MoveToward(velocity.Z, 0f, effectiveSpeed);
            }
        }

        Velocity = velocity;
        MoveAndSlide();

        // Safety net: rescue anyone who ends up below the arena instead of free-falling forever.
        // Root cause of the Phase 1 "some players fall through the floor" flake, found by
        // rendering real frames: spawn markers sat at Y=0 (floor level), so every capsule spawned
        // half-inside the floor's CSG trimesh collider, which Jolt can depenetrate either way.
        // Markers are now at Y=1 (the arena's SpawnPoints). This stays as a permanent "void"
        // feature for any map, and logs so falls are countable in headless tests.
        if (GlobalPosition.Y < TuningService.Instance.VoidCatchY)
        {
            if (_isServer)
                GD.Print($"[Physics] {Main.GetPlayerName(_peerId)} fell out of the world — rescued.");
            var spawn = Main.PickRandomSpawn();
            if (spawn is not null)
                GlobalPosition = spawn.GlobalPosition;
            Velocity = Vector3.Zero;
            _vaulting = false;
        }
    }

    private static float Gravity => (float)ProjectSettings.GetSetting("physics/3d/default_gravity");

    // Player.tscn's capsule, which the vault probes measure from.
    private const float BodyHalfHeight = 0.9f;
    private const float BodyRadius = 0.4f;

    /// <summary>Is there cover to vault in front of a body centred at <paramref name="centre"/>,
    /// moving along <paramref name="direction"/>? A knee-high probe finds something in the way, then
    /// a probe down onto it measures its top: from VaultMinHeight to VaultMaxHeight above the feet
    /// counts. Taller cover starts the downward probe inside itself and finds no top, so walls never
    /// vault. Players are not cover. Static and query-only, so tests can run it on a bare world.</summary>
    public static bool FindVault(PhysicsDirectSpaceState3D space, Rid self, Vector3 centre, Vector3 direction, Tuning tuning, out float height)
    {
        height = 0f;
        var feet = centre + Vector3.Down * BodyHalfHeight;
        var knee = feet + Vector3.Up * (tuning.VaultMinHeight - 0.1f);
        var probe = PhysicsRayQueryParameters3D.Create(knee, knee + direction * (BodyRadius + tuning.VaultReach));
        probe.Exclude = new Godot.Collections.Array<Rid> { self };
        var hit = space.IntersectRay(probe);
        if (hit.Count == 0 || hit["collider"].AsGodotObject() is CharacterBody3D)
            return false;

        // A little way into the obstacle, so thin cover (a barrier) still gets its top measured.
        var over = hit["position"].AsVector3() + direction * 0.15f;
        var top = PhysicsRayQueryParameters3D.Create(
            new Vector3(over.X, feet.Y + tuning.VaultMaxHeight + 0.05f, over.Z),
            new Vector3(over.X, feet.Y, over.Z));
        top.Exclude = probe.Exclude;
        var surface = space.IntersectRay(top);
        if (surface.Count == 0 || surface["normal"].AsVector3().Y < 0.7f)
            return false;

        height = surface["position"].AsVector3().Y - feet.Y;
        return height >= tuning.VaultMinHeight && height <= tuning.VaultMaxHeight;
    }

    /// <summary>Starts a vault over cover <paramref name="height"/> tall and returns the take-off
    /// speed: just enough for the feet to clear it by VaultClearance. Runs wherever SimulateStep
    /// does; the animation follows the dodge's pattern (the owner shows its own prediction, the
    /// server cues everyone else).</summary>
    private float StartVault(Vector3 direction, float height)
    {
        var tuning = TuningService.Instance;
        var launch = Mathf.Sqrt(2f * Gravity * (height + tuning.VaultClearance));
        _vaultVelocity = direction * tuning.VaultSpeed;
        _vaulting = true;
        GlobalRotation = new Vector3(GlobalRotation.X, YawFacing(direction), GlobalRotation.Z);

        var airTime = 2f * launch / Gravity; // up and back down to the take-off height
        if (_isServer)
            Rpc(nameof(BroadcastVaultCue), airTime);
        else
            PlayVaultAnimation(airTime); // the owner's prediction, or practice
        return launch;
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
    private void BroadcastVaultCue(float airTime)
    {
        if (_isServer || Multiplayer.GetRemoteSenderId() != 1)
            return;
        if (_isOwner && !_isBot)
            return; // already playing: the owner predicted it on the key press
        PlayVaultAnimation(airTime);
    }

    private void PlayVaultAnimation(float airTime) => _model?.PlayOneShot(CharacterModel.Clip.Vault, airTime);

    private void RunOfflinePhysics(double delta)
    {
        if (IsDead) return;
        var worldDir = CameraRelative(ReadMoveInput());
        UpdateFacing(worldDir, (float)delta);
        SimulateStep(worldDir, delta, !GameMenu.IsOpen && Input.IsActionJustPressed("jump"), WantsSprint());
    }


    /// <summary>Drains stamina while sprinting and refills it otherwise. Returns whether the sprint
    /// actually happens: once empty, it stays off until stamina is back to SprintMinStamina, so it
    /// doesn't flicker on and off at zero.</summary>
    private bool UpdateStamina(bool wantsSprint, float delta)
    {
        var tuning = TuningService.Instance;
        _sprinting = wantsSprint && (_sprinting ? _stamina > 0f : _stamina >= tuning.SprintMinStamina);
        if (_sprinting)
        {
            _stamina = Mathf.Max(0f, _stamina - tuning.SprintStaminaPerSecond * delta);
            _staminaRegenIn = tuning.StaminaRegenDelay;
        }
        else if (_staminaRegenIn > 0f)
        {
            _staminaRegenIn -= delta;
        }
        else
        {
            _stamina = Mathf.Min(tuning.MaxStamina, _stamina + tuning.StaminaRegenPerSecond * delta);
        }
        return _sprinting;
    }

    private void SpendStamina(float amount)
    {
        _stamina = Mathf.Max(0f, _stamina - amount);
        _staminaRegenIn = TuningService.Instance.StaminaRegenDelay;
    }

    /// <summary>Shift held, for the local human (bots never sprint).</summary>
    private bool WantsSprint() => !_isBot && !GameMenu.IsOpen && Input.IsActionPressed("sprint");

    /// <summary>WASD turned into a world direction relative to where the camera looks.</summary>
    private Vector3 CameraRelative(Vector2 input) => new Basis(Vector3.Up, _cameraYaw) * new Vector3(input.X, 0f, input.Y);

    /// <summary>The yaw that makes a body (which faces -Z) look along <paramref name="direction"/>.</summary>
    public static float YawFacing(Vector3 direction) => Mathf.Atan2(-direction.X, -direction.Z);

    /// <summary>Owner only: turn the body toward the way it's moving, or toward the camera while
    /// an attack's aim lock lasts. The server takes this yaw from SubmitInput, so remote players
    /// see the turn too.</summary>
    private void UpdateFacing(Vector3 worldDir, float delta)
    {
        // Mid-roll (and mid-vault) the body keeps the direction it started in, whatever keys are held.
        if (_dodgeTimeRemaining > 0f || _vaulting)
            return;

        float? target = null;
        if (_aimLockRemaining > 0f)
        {
            _aimLockRemaining -= delta;
            target = _cameraYaw;
        }
        else if (worldDir.LengthSquared() > 0.01f)
        {
            target = YawFacing(worldDir);
        }

        if (target is { } yaw)
            GlobalRotation = new Vector3(GlobalRotation.X, Mathf.LerpAngle(GlobalRotation.Y, yaw, 1f - Mathf.Exp(-TurnRate * delta)), GlobalRotation.Z);
    }

    /// <summary>Owner: snap the body to the camera before an attack or ability, and hold it there briefly.</summary>
    private void FaceAim()
    {
        GlobalRotation = new Vector3(GlobalRotation.X, _cameraYaw, GlobalRotation.Z);
        _aimLockRemaining = AimLockSeconds;
    }

    /// <summary>WASD for the local human; nothing while the Esc menu is open (the match keeps
    /// running, so the character just stands still).</summary>
    private static Vector2 ReadMoveInput() => GameMenu.IsOpen
        ? Vector2.Zero
        : Input.GetVector("move_left", "move_right", "move_forward", "move_back");

    private void RunPredictedPhysics(double delta)
    {
        Vector3 worldDir;
        if (_isBot)
        {
            // Bots steer by turning their body (RunBotAi) and walking "forward".
            worldDir = Transform.Basis * new Vector3(_botMoveInput.X, 0f, _botMoveInput.Y);
        }
        else
        {
            worldDir = CameraRelative(ReadMoveInput());
            if (!IsDead)
                UpdateFacing(worldDir, (float)delta);
        }
        var jump = !_isBot && !IsDead && !GameMenu.IsOpen && Input.IsActionJustPressed("jump");
        if (jump)
            _jumpCounter++;
        var sprint = WantsSprint();
        SimulateStep(worldDir, delta, jump, sprint);

        var tick = Engine.GetPhysicsFrames();
        _predictedHistory.Add(new PredictedState(tick, GlobalPosition));
        var maxBuffered = (ulong)(TuningService.Instance.ServerTickRateHz * 2);
        while (_predictedHistory.Count > 0 && tick - _predictedHistory[0].Tick > maxBuffered)
            _predictedHistory.RemoveAt(0);

        var yaw = GlobalRotation.Y;
        var jumpCounter = _jumpCounter;
        var moveDir = new Vector2(worldDir.X, worldDir.Z);
        Net.Instance.SendWithSimulation(() => RpcId(1, nameof(SubmitInput), tick, moveDir, yaw, jumpCounter, sprint));
    }

    private void RunServerPhysics(double delta)
    {
        if (!IsDead && _dodgeTimeRemaining <= 0f && !_vaulting) // mid-roll (or vault) the facing is locked to its direction
            GlobalRotation = new Vector3(GlobalRotation.X, _serverPendingYaw, GlobalRotation.Z);

        if (!IsDead)
            SimulateStep(new Vector3(_serverPendingInput.X, 0f, _serverPendingInput.Y), delta, _serverJumpQueued, _serverPendingSprint);
        _serverJumpQueued = false;

        var tick = Engine.GetPhysicsFrames();
        Rpc(nameof(ReceiveServerState), tick, GlobalPosition, GlobalRotation.Y, _health, _stamina);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
    private void SubmitInput(ulong tick, Vector2 moveDir, float yaw, int jumpCounter, bool sprint)
    {
        if (!_isServer)
            return;
        if (Multiplayer.GetRemoteSenderId() != _peerId)
            return; // reject input claiming to control someone else's node

        _serverPendingInput = moveDir.LimitLength(1f); // world-space x/z; never faster than walking
        _serverPendingYaw = yaw;
        _serverPendingSprint = sprint; // a held state, so an unreliable packet is fine: the next one repeats it

        // Jumps travel as a running count, not a one-tick "pressed" flag: input packets are
        // unreliable, and a dropped flag would silently eat the jump. Every later packet still
        // carries the higher count, so the server catches up on the next one that arrives.
        if (jumpCounter > _serverLastJumpCounter)
        {
            _serverLastJumpCounter = jumpCounter;
            _serverJumpQueued = true;
        }
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
    private void ReceiveServerState(ulong tick, Vector3 serverPosition, float serverYaw, float health, float stamina)
    {
        if (_isServer)
            return;

        // Health only ever lived on the server before, so no client (and no HUD) knew it.
        _health = health;

        if (_isOwner)
        {
            ReconcileOwner(tick, serverPosition);
            // Stamina is predicted locally (smooth HUD); the server's value is a round-trip old, so
            // only take it when the two have really drifted apart.
            if (Mathf.Abs(stamina - _stamina) > 12f)
                _stamina = stamina;
        }
        else if (_isRemoteView)
        {
            var now = Time.GetTicksMsec() / 1000.0;
            _remoteSnapshots.Add(new RemoteSnapshot(now, serverPosition, serverYaw));
            _remoteSnapshots.RemoveAll(s => now - s.ReceivedAt > 2.0);
        }
    }

    /// <summary>
    /// Simplified reconciliation: a big mismatch (just joined, teleported, hit something the
    /// client didn't predict) snaps hard; small drift is blended in over
    /// Tuning.ReconciliationSmoothTime rather than popping. This corrects the live body
    /// directly rather than doing a full input-replay resimulation — cheaper to write for a
    /// feasibility spike, at the cost of a visible correction on rough connections. Worth
    /// revisiting with real playtest numbers as Phase 2 combat puts more weight on precise
    /// positioning (dodge spacing, execute's behind-the-back check).
    /// </summary>
    private void ReconcileOwner(ulong tick, Vector3 serverPosition)
    {
        var idx = _predictedHistory.FindIndex(s => s.Tick == tick);
        var predictedAtTick = idx >= 0 ? _predictedHistory[idx].Position : GlobalPosition;
        var error = serverPosition.DistanceTo(predictedAtTick);
        var tuning = TuningService.Instance;

        if (error > tuning.ReconciliationSnapDistance)
        {
            GlobalPosition = serverPosition;
            _predictedHistory.Clear();
        }
        else if (error > 0.01f)
        {
            var fixedDelta = 1f / Mathf.Max(1, tuning.ServerTickRateHz);
            var t = 1f - Mathf.Exp(-fixedDelta / Mathf.Max(0.001f, tuning.ReconciliationSmoothTime));
            var target = GlobalPosition + (serverPosition - predictedAtTick);
            GlobalPosition = GlobalPosition.Lerp(target, t);
        }
    }

    /// <summary>Delayed-buffer interpolation toward where the server said this peer was
    /// Tuning.InterpolationDelaySeconds ago — never simulated locally, purely visual.</summary>
    private void InterpolateRemote()
    {
        if (_remoteSnapshots.Count < 2)
            return;

        var renderTime = Time.GetTicksMsec() / 1000.0 - TuningService.Instance.InterpolationDelaySeconds;

        for (var i = _remoteSnapshots.Count - 1; i > 0; i--)
        {
            if (_remoteSnapshots[i - 1].ReceivedAt > renderTime || renderTime > _remoteSnapshots[i].ReceivedAt)
                continue;

            var span = _remoteSnapshots[i].ReceivedAt - _remoteSnapshots[i - 1].ReceivedAt;
            var t = span > 0 ? (float)((renderTime - _remoteSnapshots[i - 1].ReceivedAt) / span) : 0f;
            GlobalPosition = _remoteSnapshots[i - 1].Position.Lerp(_remoteSnapshots[i].Position, t);
            var yaw = Mathf.LerpAngle(_remoteSnapshots[i - 1].YawRadians, _remoteSnapshots[i].YawRadians, t);
            GlobalRotation = new Vector3(GlobalRotation.X, yaw, GlobalRotation.Z);
            return;
        }

        // renderTime is outside the buffered range (just connected, or a spike) — snap to latest known.
        var latest = _remoteSnapshots[^1];
        GlobalPosition = latest.Position;
        GlobalRotation = new Vector3(GlobalRotation.X, latest.YawRadians, GlobalRotation.Z);
    }

    // ------------------------------------------------------------------
    // Combat — verb requests (client -> server), state machine (server only)
    // ------------------------------------------------------------------

    private void RequestVerbLocal(Verb verb)
    {
        var tuning = TuningService.Instance;
        var now = Time.GetTicksMsec() / 1000.0;
        if (verb == Verb.Dodge)
        {
            if (IsDead || now < _localDodgeReadyAt || now < _localBusyUntil || _stamina < tuning.DodgeStaminaCost)
                return; // the server would refuse it anyway
            // Roll the way you're moving (camera-relative); standing still, roll the way you face.
            var worldDir = CameraRelative(ReadMoveInput());
            if (!_isBot && worldDir.LengthSquared() > 0.01f)
                GlobalRotation = new Vector3(GlobalRotation.X, YawFacing(worldDir), GlobalRotation.Z);
            _aimLockRemaining = 0f;
            _localDodgeReadyAt = now + tuning.DodgeDuration + tuning.DodgeCooldown;
            if (!_isOffline && !_isServer)
                StartDodge(predicted: true); // offline, TryStartVerb below does it for real
        }
        else
        {
            AttackRequestCount++;
            FaceAim();
            _localBusyUntil = now + (verb == Verb.Heavy
                ? tuning.HeavyWindup + tuning.HeavyRecovery
                : tuning.LightWindup + tuning.LightRecovery);
        }

        if (_isOffline)
        {
            // No server round-trip offline — this instance IS its own authority.
            TryStartVerb(verb);
            return;
        }

        // The facing travels with the verb: the unreliable input stream could be a tick or two
        // behind, and a roll or lunge must go exactly where the player aimed.
        var yaw = GlobalRotation.Y;
        Net.Instance.SendWithSimulation(() => RpcId(1, nameof(RequestVerb), (int)verb, yaw));
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestVerb(int verbInt, float yaw)
    {
        if (!_isServer)
            return;
        if (Multiplayer.GetRemoteSenderId() != _peerId)
            return; // reject a verb claiming to be from someone else's node

        _serverPendingYaw = yaw;
        GlobalRotation = new Vector3(GlobalRotation.X, yaw, GlobalRotation.Z);

        TryStartVerb((Verb)verbInt);
    }

    private void TryStartVerb(Verb verb)
    {
        if (_combatState == CombatState.Dead)
            return;

        // Any deliberate action cancels spawn protection immediately (GDD §5.7: "cancelled
        // instantly on attacking"). A dodge counts too — using the shimmer window to reposition
        // for free would be the obvious abuse case a playtester would find first.
        if (_spawnProtectionRemaining > 0f)
            SetSpawnProtection(0f);

        switch (verb)
        {
            case Verb.Light:
                if (_combatState != CombatState.Idle) return;
                _combatState = CombatState.LightWindup;
                _stateTimer = TuningService.Instance.LightWindup;
                CueAttackAnimation(heavy: false);
                break;

            case Verb.Heavy:
                if (_combatState != CombatState.Idle) return;
                _combatState = CombatState.HeavyWindup;
                _stateTimer = TuningService.Instance.HeavyWindup;
                CueAttackAnimation(heavy: true);
                break;

            case Verb.Dodge:
                if (_combatState != CombatState.Idle) return; // recovery locks out the escape too — that's what makes whiffing costly
                if (_dodgeCooldownRemaining > 0f) return;
                if (_stamina < TuningService.Instance.DodgeStaminaCost) return;
                var dodgeTuning = TuningService.Instance;
                _dodgeCooldownRemaining = dodgeTuning.DodgeDuration + dodgeTuning.DodgeCooldown;
                _combatState = CombatState.Dodging;
                _stateTimer = dodgeTuning.DodgeDuration;
                StartDodge(predicted: false);
                break;
        }
    }

    /// <summary>Starts the roll along the body's facing. The server (and practice) runs it for real
    /// and cues the animation for everyone; the owner also runs it locally as a prediction.</summary>
    private void StartDodge(bool predicted)
    {
        var tuning = TuningService.Instance;
        var forward = -GlobalTransform.Basis.Z.Normalized();
        _dodgeVelocity = forward * (tuning.DodgeDistance / Mathf.Max(0.01f, tuning.DodgeDuration));
        _dodgeTimeRemaining = tuning.DodgeDuration;
        SpendStamina(tuning.DodgeStaminaCost);

        if (predicted)
            PlayDodgeAnimation(); // your own roll shows at once; the server's cue is ignored for you
        else if (_isOffline)
            PlayDodgeAnimation();
        else if (_isServer)
            Rpc(nameof(BroadcastDodgeCue));
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
    private void BroadcastDodgeCue()
    {
        if (_isServer || Multiplayer.GetRemoteSenderId() != 1)
            return;
        if (_isOwner && !_isBot)
            return; // already playing: the owner predicted it on the key press
        PlayDodgeAnimation();
    }

    private void PlayDodgeAnimation()
    {
        _model?.PlayOneShot(CharacterModel.Clip.Dodge, TuningService.Instance.DodgeDuration);
        // Every copy that shows the roll also hears it and kicks up dust along it (UpdateMovementFx).
        _rollFxRemaining = TuningService.Instance.DodgeDuration;
        GameAudio.Play3D(this, "roll", GlobalPosition, -2f);
        Fx.Dust(this, GlobalPosition, 0.8f);
    }

    /// <summary>Server-only (and offline-solo): advances windup/recovery/dodge/stagger/execute
    /// timers and resolves the attack the instant a windup completes.</summary>
    private void TickCombatState(float delta)
    {
        if (_dodgeCooldownRemaining > 0f)
            _dodgeCooldownRemaining = Mathf.Max(0f, _dodgeCooldownRemaining - delta);

        if (_slowTimeRemaining > 0f)
        {
            _slowTimeRemaining = Mathf.Max(0f, _slowTimeRemaining - delta);
            if (_slowTimeRemaining <= 0f)
                _slowMultiplier = 1f;
        }
        if (_revealRemaining > 0f)
            _revealRemaining = Mathf.Max(0f, _revealRemaining - delta);

        if (_combatState is CombatState.Idle or CombatState.Dead)
            return;

        _stateTimer -= delta;
        if (_stateTimer > 0f)
            return;

        switch (_combatState)
        {
            case CombatState.LightWindup:
                ResolveMeleeAttack(isHeavy: false);
                _combatState = CombatState.Recovery;
                _stateTimer = TuningService.Instance.LightRecovery;
                break;

            case CombatState.HeavyWindup:
                ResolveMeleeAttack(isHeavy: true);
                _combatState = CombatState.Recovery;
                _stateTimer = TuningService.Instance.HeavyRecovery;
                break;

            case CombatState.Recovery:
            case CombatState.Dodging:  // roll finished: hittable again
            case CombatState.Staggered:
            case CombatState.Executing:
                _combatState = CombatState.Idle;
                break;
        }
    }

    /// <summary>Server-only: move this capsule by `motion` in a single physics step, sliding along
    /// the floor and stopping at walls, pillars and other players. Used by the heavy lunge and by
    /// abilities (Drop Kick). The lunge originally used MoveAndCollide, which — on a capsule resting on the floor —
    /// reports the floor itself as the first collision and moves 0m (measured: every ability lunge
    /// travelled "0.0m of 6.0m"). MoveAndSlide handles floor contact correctly.
    /// Must be called from inside _PhysicsProcess (MoveAndSlide uses the physics delta).</summary>
    public float ServerSweep(Vector3 motion)
    {
        var start = GlobalPosition;
        var savedVelocity = Velocity;
        var step = 1f / Mathf.Max(1, TuningService.Instance.ServerTickRateHz);
        var flat = new Vector3(motion.X, 0f, motion.Z);

        // Sub-stepped: a single 6m MoveAndSlide into the octagonal plinth's CSG trimesh
        // occasionally left the capsule penetrating it (measured in clustered play: samples inside
        // geometry rose from 2 to 8 of 510, plus one fall through the floor). Chunks no longer than
        // the capsule radius keep every contact shallow; stop as soon as a chunk is mostly blocked.
        const float maxChunk = 0.4f;
        var chunks = Mathf.Max(1, Mathf.CeilToInt(flat.Length() / maxChunk));
        var chunk = flat / chunks;
        for (var i = 0; i < chunks; i++)
        {
            var before = GlobalPosition;
            Velocity = chunk / step;
            MoveAndSlide();
            if (GlobalPosition.DistanceTo(before) < chunk.Length() * 0.25f)
                break;
        }

        Velocity = savedVelocity;
        return GlobalPosition.DistanceTo(start);
    }

    private void ResolveMeleeAttack(bool isHeavy)
    {
        var tuning = TuningService.Instance;
        var forward = -GlobalTransform.Basis.Z.Normalized();

        if (isHeavy)
        {
            // The lunge itself: close the gap toward whatever's in front, stopping on collision
            // so it can't be used to phase through walls/pillars.
            ServerSweep(forward * tuning.HeavyLungeRange);
        }

        var range = isHeavy ? tuning.HeavyLungeRange : tuning.LightRange;
        var radius = isHeavy ? tuning.HeavyHitRadius : tuning.LightHitRadius;
        var rewindSeconds = PingService.Instance.GetOneWayLatencySeconds(_peerId);

        var victimId = CombatServer.Instance.TryResolveMeleeHit(_peerId, GlobalPosition, forward, range, radius, rewindSeconds,
            dodgedBy => MatchServer.Instance.ServerRegisterDodge(dodgedBy));
        LastRewindMs = rewindSeconds * 1000f;

        if (victimId is not { } vId)
        {
            if (isHeavy)
                MatchServer.Instance.ServerRegisterHeavyWhiff(_peerId); // Phase 5 award telemetry ("All Bark No Bite")
            SendAttackResult(false, -1L);
            return;
        }

        var victim = CombatServer.Instance.GetPlayerNode(vId);
        if (victim is null)
        {
            SendAttackResult(false, -1L);
            return;
        }

        if (victim.IsSpawnProtected)
        {
            SendAttackResult(false, vId);
            return; // shimmer means shimmer — no damage, no execute, nothing
        }

        // Execute check (heavy only): attacker is directly behind the victim's own facing.
        var isExecute = false;
        if (isHeavy)
        {
            var victimRewound = CombatServer.Instance.GetRewoundPosition(vId, rewindSeconds) ?? victim.GlobalPosition;
            var victimForward = -victim.GlobalTransform.Basis.Z.Normalized();
            var victimToAttacker = GlobalPosition - victimRewound;
            if (victimToAttacker.LengthSquared() > 0.0001f)
            {
                // Angle between where the victim is FACING and where the attacker IS: near 180°
                // means the attacker is behind the victim's back, not in front of their face.
                var angle = Mathf.RadToDeg(victimForward.AngleTo(victimToAttacker.Normalized()));
                isExecute = angle > (180f - tuning.ExecuteBehindAngleDegrees);
            }
        }

        if (isExecute)
        {
            victim.ServerApplyDamage(victim._health, _peerId, "execute");
            _combatState = CombatState.Executing;
            _stateTimer = tuning.ExecuteAnimationLock; // "suicidal in a crowd" — you're locked and exposed right after
        }
        else if (MatchServer.Instance.IsGoldenKnifeHolder(_peerId))
        {
            // GDD §5.4: holder deals one-hit kills for the whole duration, on light or heavy.
            victim.ServerApplyDamage(victim._health, _peerId, "golden knife");
            if (isHeavy && !victim.IsDead)
            {
                victim._combatState = CombatState.Staggered;
                victim._stateTimer = tuning.HeavyStaggerDuration;
            }
        }
        else
        {
            var damage = tuning.MaxHealth * (isHeavy ? tuning.HeavyDamagePercent : tuning.LightDamagePercent);
            victim.ServerApplyDamage(damage, _peerId, isHeavy ? "heavy" : "light");
            if (isHeavy && !victim.IsDead) // don't resurrect a kill into a stagger
            {
                victim._combatState = CombatState.Staggered;
                victim._stateTimer = tuning.HeavyStaggerDuration;
            }
        }

        CueHitSound(StrikeContactPoint(victim, KnifeHandHeight), forward, isHeavy || isExecute);
        SendAttackResult(true, vId);
    }

    /// <summary>Ability shared kit (abilities/Ability.Strike): a melee strike from this player,
    /// resolved exactly like the knife: rewind lag compensation, dodges and spawn protection pass
    /// through, stab sound on a hit, and the killfeed names <paramref name="method"/>.
    /// Returns whether it hit.</summary>
    public bool ServerStrike(float range, float radius, float damage, string method, bool staggers)
    {
        if (!_isServer)
            return false;

        var forward = -GlobalTransform.Basis.Z.Normalized();
        var rewindSeconds = PingService.Instance.GetOneWayLatencySeconds(_peerId);
        var victimId = CombatServer.Instance.TryResolveMeleeHit(_peerId, GlobalPosition, forward, range, radius, rewindSeconds,
            dodgedBy => MatchServer.Instance.ServerRegisterDodge(dodgedBy));
        if (victimId is not { } vId || CombatServer.Instance.GetPlayerNode(vId) is not { } victim || victim.IsSpawnProtected)
            return false;

        victim.ServerApplyDamage(damage, _peerId, method);
        if (staggers && !victim.IsDead)
        {
            victim._combatState = CombatState.Staggered;
            victim._stateTimer = TuningService.Instance.HeavyStaggerDuration;
        }
        CueHitSound(StrikeContactPoint(victim, KickHeight), victim.GlobalPosition - GlobalPosition, heavy: true);
        GD.Print($"[Combat] {Main.GetPlayerName(_peerId)} hit {Main.GetPlayerName(vId)} with {method} ({damage:F0}).");
        return true;
    }


    /// <summary>Server (or practice): a melee hit just landed. Every client plays the stab where the
    /// victim stands, as positional 3D audio, so you can hear which way a hit came from. Only for
    /// hits the server confirmed, never a dodged or protected swing.</summary>
    // Heights above a player's origin, which is the middle of its 1.8 m capsule (feet at -0.9).
    private const float KnifeHandHeight = 0.2f; // the knife hand at the moment of a stab
    private const float KickHeight = 0.1f;      // the drop kick's feet meet the body a little lower
    private const float CapsuleRadius = 0.4f;   // Player.tscn

    /// <summary>Where the blow meets the victim: on the surface of the victim's capsule, on the
    /// side facing the attacker, at the attacker's striking height. The server is headless (no
    /// knife mesh to read), so it's worked out from the two bodies.</summary>
    private Vector3 StrikeContactPoint(Player victim, float heightAboveOrigin)
    {
        var toAttacker = GlobalPosition - victim.GlobalPosition;
        toAttacker.Y = 0f;
        toAttacker = toAttacker.LengthSquared() > 0.0001f ? toAttacker.Normalized() : GlobalTransform.Basis.Z;
        var contact = victim.GlobalPosition + toAttacker * CapsuleRadius;
        contact.Y = GlobalPosition.Y + heightAboveOrigin;
        return contact;
    }

    /// <summary>Server: a hit landed. Every client hears the stab and sees the blood, sprayed from
    /// the contact point the way the blow was going (<paramref name="direction"/>).</summary>
    private void CueHitSound(Vector3 at, Vector3 direction, bool heavy)
    {
        if (_isOffline)
            PlayHitEffects(at, direction, heavy);
        else if (_isServer)
            Rpc(nameof(BroadcastHitSound), at, direction, heavy);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
    private void BroadcastHitSound(Vector3 at, Vector3 direction, bool heavy)
    {
        if (_isServer || Multiplayer.GetRemoteSenderId() != 1)
            return; // the dedicated server has no one to play it to
        PlayHitEffects(at, direction, heavy);
    }

    private void PlayHitEffects(Vector3 at, Vector3 direction, bool heavy)
    {
        // Heavies land lower and louder.
        GameAudio.Play3D(this, "stab", at, heavy ? 3f : 0f, heavy ? 0.85f : 1f);
        Fx.Blood(this, at, direction, heavy);
    }

    /// <summary>In practice the attacker is this process, and Godot refuses an RpcId to yourself
    /// without CallLocal (it logged an error on every whiff), so call it directly.</summary>
    private void SendAttackResult(bool confirmed, long victimId)
    {
        if (_peerId == Multiplayer.GetUniqueId())
            ReceiveAttackResult(confirmed, victimId);
        else
            RpcId(_peerId, nameof(ReceiveAttackResult), confirmed, victimId);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
    private void ReceiveAttackResult(bool confirmed, long victimId)
    {
        if (!_isOwner)
            return;

        if (confirmed)
            ConfirmedHitCount++;
    }

    /// <summary>Server-only. Applies damage; on lethal, kills, broadcasts the kill (killfeed +
    /// death cam trigger), and schedules a respawn. GDD §5.2 target TTK: 2-3 connected hits.</summary>
    public void ServerApplyDamage(float amount, long attackerId, string method)
    {
        if (!_isServer || IsDead || !IsInsideTree())
            return;

        _secondsSinceHit = 0f; // any hit restarts the wait before healing
        _regenTimer = 0f;
        _health -= amount;
        if (_health > 0f)
            return;

        _health = 0f;
        _combatState = CombatState.Dead;
        Velocity = Vector3.Zero;

        GD.Print($"[Combat] {Main.GetPlayerName(attackerId)} killed {Main.GetPlayerName(_peerId)} ({method}).");

        MatchServer.Instance.ServerRegisterKill(attackerId, _peerId, method);

        // Broadcast from the VICTIM node (this) to everyone — killfeed + this player's own
        // death cam trigger on their own client (see BroadcastKill).
        Rpc(nameof(BroadcastKill), attackerId, _peerId, method, GlobalPosition);

        // Last Call (GDD §5.6) drops respawn to 1s so the closing arena stays a real climax
        // instead of a slow trickle back in.
        GetTree().CreateTimer(MatchServer.Instance.CurrentRespawnTime).Timeout += ServerRespawn;
    }

    private float _secondsSinceHit;
    private float _regenTimer;

    /// <summary>Server (and practice): after <c>HealthRegenDelay</c> seconds without being hit, heal
    /// <c>HealthRegenAmount</c> every <c>HealthRegenInterval</c>. Health reaches clients through
    /// ReceiveServerState like any other change; the "+" effect is a separate cue.</summary>
    private void ServerTickRegen(float delta)
    {
        var tuning = TuningService.Instance;
        if (IsDead)
        {
            _secondsSinceHit = 0f;
            return;
        }
        _secondsSinceHit += delta;
        if (_secondsSinceHit < tuning.HealthRegenDelay || _health >= tuning.MaxHealth)
        {
            _regenTimer = 0f;
            return;
        }
        _regenTimer += delta;
        if (_regenTimer < tuning.HealthRegenInterval)
            return;
        _regenTimer -= tuning.HealthRegenInterval;
        _health = Mathf.Min(tuning.MaxHealth, _health + tuning.HealthRegenAmount);
        if (_isOffline)
            Fx.Heal(this);
        else
            Rpc(nameof(BroadcastHeal));
    }

    /// <summary>Everyone sees a healing player: green "+" signs drift up from their body.</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
    private void BroadcastHeal()
    {
        if (_isServer || Multiplayer.GetRemoteSenderId() != 1)
            return; // the dedicated server draws nothing
        Fx.Heal(this);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void BroadcastKill(long killerId, long victimId, string method, Vector3 deathPosition)
    {
        // CallLocal is on so this also fires for the server's own call (needed for offline mode,
        // where "the server" and "the only client" are the same process). GetRemoteSenderId()
        // reads 0 for that local invocation, so only gate remote calls, and only the real server
        // (peer 1) is ever allowed to have actually sent one in the first place.
        if (!_isServer && Multiplayer.GetRemoteSenderId() != 1)
            return;

        Events.Instance.EmitSignal(Events.SignalName.PlayerKilled, killerId, victimId, method);
        // This RPC runs on the victim's own node, so the victim's model plays its death. The old
        // tumbling capsule is only a fallback for a player with no model.
        if (_model is not null && _modelAnimations is not null)
            _model.PlayDeath(_modelAnimations.DeathDuration);
        else
            SpawnDeathEffect(deathPosition);
        GameAudio.Play3D(this, "death", deathPosition);

        if (_isOwner && victimId == _peerId)
        {
            _deathCamRemaining = TuningService.Instance.DeathCamDuration;
            var killer = CombatServer.Instance.GetPlayerNode(killerId);
            _deathCamLookAt = killer is not null ? killer.GlobalPosition : deathPosition;
        }
    }

    private void ServerRespawn()
    {
        if (!_isServer || !IsDead)
            return;

        _health = TuningService.Instance.MaxHealth;
        _stamina = TuningService.Instance.MaxStamina;
        _combatState = CombatState.Idle;
        Velocity = Vector3.Zero;

        var spawn = Main.PickRandomSpawn();
        if (spawn is not null)
            GlobalPosition = spawn.GlobalPosition;

        SetSpawnProtection(TuningService.Instance.SpawnProtectionDuration);
        Rpc(nameof(BroadcastRevive));
    }

    /// <summary>Server → everyone: this player is alive again, so the model stands back up from its death pose.</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void BroadcastRevive()
    {
        if (!_isServer && Multiplayer.GetRemoteSenderId() != 1)
            return;
        _model?.Revive();
        _stamina = TuningService.Instance.MaxStamina;
        _vaulting = false;
        _faceCentrePending = true;
    }

    /// <summary>Server-only. Called by MatchServer at the start of every new match: resets
    /// health/state/position regardless of current state (unlike ServerRespawn, which only
    /// fires from a death timer and requires IsDead).</summary>
    public void ServerMatchReset()
    {
        if (!_isServer)
            return;

        _health = TuningService.Instance.MaxHealth;
        _combatState = CombatState.Idle;
        Velocity = Vector3.Zero;
        _slowMultiplier = 1f;
        _slowTimeRemaining = 0f;
        _revealRemaining = 0f;

        var spawn = Main.PickRandomSpawn();
        if (spawn is not null)
            GlobalPosition = spawn.GlobalPosition;

        SetSpawnProtection(TuningService.Instance.SpawnProtectionDuration);
        Rpc(nameof(BroadcastRevive));
    }

    /// <summary>Server (or practice): start or cancel spawn protection, and tell every client so
    /// the shimmer matches what the server enforces.</summary>
    private void SetSpawnProtection(float seconds)
    {
        _spawnProtectionRemaining = seconds;
        if (_isServer)
            Rpc(nameof(BroadcastSpawnProtection), seconds);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void BroadcastSpawnProtection(float seconds)
    {
        if (Multiplayer.GetRemoteSenderId() != 1)
            return;
        _spawnProtectionRemaining = seconds;
    }

    // ------------------------------------------------------------------
    // Identity (name + character) — see the _Ready() handshake comment for why this exists
    // ------------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AnnounceIdentity(string requestedName, string? characterId)
    {
        if (!_isServer)
            return;
        if (Multiplayer.GetRemoteSenderId() != _peerId)
            return; // reject an identity claim for someone else's node

        var safeName = string.IsNullOrWhiteSpace(requestedName) ? $"Player {_peerId}" : requestedName;
        Rpc(nameof(ReceiveIdentity), safeName, characterId ?? "");
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveIdentity(string name, string? characterId)
    {
        if (!_isServer && Multiplayer.GetRemoteSenderId() != 1)
            return;

        ApplyIdentity(name, characterId);
    }

    /// <summary>Server-only: a late-joining client's copy of this node asking who it is. If the
    /// owner hasn't announced yet, stay silent — the normal broadcast will reach the asker too,
    /// since its copy of this node now exists.</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestIdentity()
    {
        if (!_isServer || string.IsNullOrEmpty(_displayName))
            return;

        RpcId(Multiplayer.GetRemoteSenderId(), nameof(ReceiveIdentity), _displayName, _characterDef?.Id ?? "");
    }

    private void ApplyIdentity(string name, string? characterId)
    {
        _displayName = name;
        SetDisplayName(name);
        Main.RegisterPlayerName(_peerId, name);
        EquipCharacter(characterId);
    }

    private void EquipCharacter(string? characterId)
    {
        _characterDef = CharacterRegistry.GetOrDefault(characterId);
        _ability = CreateAbility(_characterDef.Ability, this);

        AttachModel(_characterDef);
        if (_model is null || _characterDef.TintModel)
        {
            // Full colour on untextured bodies (the capsule); only a light wash over a texture,
            // which would otherwise be multiplied into blue or orange skin.
            foreach (var material in _bodyMaterials)
            {
                material.AlbedoColor = material.AlbedoTexture is null
                    ? _characterDef.SilhouetteColor
                    : Colors.White.Lerp(_characterDef.SilhouetteColor, TexturedTintStrength);
            }
        }

        // The name keeps the character's colour readable even when the body is textured.
        if (NameLabel is not null)
            NameLabel.Modulate = _characterDef.SilhouetteColor.Lightened(0.35f);
    }

    private const float TexturedTintStrength = 0.25f;

    /// <summary>Swaps the grey-box capsule for the character's animated model. Skipped when
    /// headless (the dedicated server and --bot clients draw nothing), and if the model or
    /// animation set fails to load the capsule stays, so a bad asset never costs a player their body.</summary>
    private void AttachModel(CharacterDef def)
    {
        if (DisplayServer.GetName() == "headless")
            return;

        var modelScene = def.Model ?? GD.Load<PackedScene>(CharacterModel.DefaultModelPath);
        var animations = def.Animations
            ?? GD.Load<HumanoidAnimationSet>(string.IsNullOrEmpty(def.AnimationsPath) ? HumanoidAnimationSet.DefaultPath : def.AnimationsPath);
        if (modelScene is null || animations is null)
            return;
        _ownHeldProp = def.HeldProp ?? GD.Load<PackedScene>(CharacterModel.DefaultHeldPropPath);

        _model?.QueueFree();
        var capsuleHeight = GetNodeOrNull<CollisionShape3D>("CollisionShape3D")?.Shape is CapsuleShape3D capsule ? capsule.Height : 1.8f;
        _model = CharacterModel.Create(modelScene, animations, capsuleHeight, HeldPropScene());
        _model.Position = new Vector3(0f, -capsuleHeight / 2f, 0f); // the capsule is centred on the body origin
        AddChild(_model);
        _modelAnimations = animations;

        if (_meshInstance is not null)
            _meshInstance.Visible = false;
        _bodyMaterials.Clear();
        _bodyMaterials.AddRange(_model.Materials);
        _lastVisualPosition = GlobalPosition;
    }
    private HumanoidAnimationSet? _modelAnimations;
    private PackedScene? _ownHeldProp; // the character's knife, back in hand when the Golden Knife goes
    private bool _holdsGoldenKnife;

    /// <summary>Cosmetic: MatchServer calls this on every client when the Golden Knife changes
    /// hands, so the holder visibly carries it. What the knife does is decided on the server
    /// (MatchServer.IsGoldenKnifeHolder).</summary>
    public void SetHoldsGoldenKnife(bool holds)
    {
        if (_holdsGoldenKnife == holds)
            return;
        _holdsGoldenKnife = holds;
        _model?.SetHeldProp(HeldPropScene());
    }

    private PackedScene? HeldPropScene() =>
        _holdsGoldenKnife ? GD.Load<PackedScene>(MatchServer.GoldenKnifePropPath) : _ownHeldProp;

    private void UpdateModelAnimation(float delta)
    {
        if (_model is null)
            return;

        Vector3 velocity;
        bool onFloor;
        if (_isRemoteView)
        {
            // Remote players are interpolated, not simulated: no Velocity or IsOnFloor to read,
            // so both are recovered from how the interpolated position actually moved.
            if (delta > 0f)
            {
                var raw = (GlobalPosition - _lastVisualPosition) / delta;
                _visualVelocity = _visualVelocity.Lerp(raw, 1f - Mathf.Exp(-delta * 12f));
            }
            // Hold "airborne" briefly so the apex of a jump (vertical speed ~0) doesn't read as landing.
            _visualAirborneHold = Mathf.Abs(_visualVelocity.Y) > 0.6f ? 0.15f : Mathf.Max(0f, _visualAirborneHold - delta);
            velocity = _visualVelocity;
            onFloor = _visualAirborneHold <= 0f;
        }
        else
        {
            velocity = Velocity;
            onFloor = IsOnFloor();
        }
        _lastVisualPosition = GlobalPosition;

        _model.UpdateLocomotion(delta, velocity, onFloor);
        UpdateMovementFx(delta, velocity, onFloor);
    }

    private bool _fxWasOnFloor = true;
    private float _fxFallSpeed; // fastest downward speed in the current airtime
    private float _fxStepTimer;
    private float _fxDustTimer;
    private float _rollFxRemaining;

    /// <summary>Jump, landing, footstep and dust cues. Every copy derives them from how the body
    /// moves (remote players from their interpolated motion, as the animations are), so nothing
    /// extra crosses the network. Only copies with a model (not headless) get here.</summary>
    private void UpdateMovementFx(float delta, Vector3 velocity, bool onFloor)
    {
        if (!onFloor)
            _fxFallSpeed = Mathf.Max(_fxFallSpeed, -velocity.Y);
        if (_fxWasOnFloor && !onFloor && velocity.Y > 1f)
        {
            GameAudio.Play3D(this, "jump", GlobalPosition, -6f);
            Fx.Dust(this, GlobalPosition, 0.4f);
        }
        else if (!_fxWasOnFloor && onFloor)
        {
            // Any height: a hop is a soft puff, a drop off a deck a big one.
            var impact = Mathf.Clamp(_fxFallSpeed / 6f, 0.3f, 2f);
            GameAudio.Play3D(this, "land", GlobalPosition, Mathf.Lerp(-10f, 2f, impact / 2f));
            Fx.Dust(this, GlobalPosition, impact);
            _fxFallSpeed = 0f;
        }
        _fxWasOnFloor = onFloor;

        if (_rollFxRemaining > 0f)
        {
            _rollFxRemaining -= delta;
            _fxDustTimer -= delta;
            if (_fxDustTimer <= 0f && onFloor)
            {
                _fxDustTimer = 0.09f;
                Fx.Dust(this, GlobalPosition, 0.45f);
            }
            return;
        }

        var horizontal = new Vector2(velocity.X, velocity.Z);
        if (!onFloor || horizontal.Length() < 1.5f)
        {
            _fxStepTimer = Mathf.Min(_fxStepTimer, 0.1f); // the first step comes quickly
            return;
        }
        // Faster than walking pace (MoveSpeed) by a margin: a sprint, which scuffs up dust.
        var sprinting = horizontal.Length() > MoveSpeed * 1.25f;
        _fxStepTimer -= delta;
        if (_fxStepTimer > 0f)
            return;
        _fxStepTimer = sprinting ? 0.27f : 0.36f;
        GameAudio.Play3D(this, "step", GlobalPosition, sprinting ? -8f : -13f);
        if (sprinting)
        {
            var behind = new Vector3(horizontal.X, 0f, horizontal.Y).Normalized() * -0.3f;
            Fx.Dust(this, GlobalPosition + behind, 0.3f);
        }
    }

    /// <summary>Called when the server starts a light or heavy windup. The attack animation
    /// comes from the server rather than the key press, so a press the server rejects never
    /// shows a swing that didn't happen (Pillar 2).</summary>
    private void CueAttackAnimation(bool heavy)
    {
        if (_isOffline)
            PlayAttackAnimation(heavy);
        else if (_isServer)
            Rpc(nameof(BroadcastAttackCue), heavy);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
    private void BroadcastAttackCue(bool heavy)
    {
        if (!_isServer && Multiplayer.GetRemoteSenderId() != 1)
            return;
        PlayAttackAnimation(heavy);
    }

    private void PlayAttackAnimation(bool heavy)
    {
        GameAudio.Play3D(this, "swing", GlobalPosition + Vector3.Up * 1.2f, heavy ? 0f : -4f, heavy ? 0.85f : 1f);
        if (_model is null || _modelAnimations is null)
            return;
        _model.PlayOneShot(heavy ? CharacterModel.Clip.HeavyAttack : CharacterModel.Clip.LightAttack,
            heavy ? _modelAnimations.HeavyAttackDuration : _modelAnimations.LightAttackDuration);
    }

    private static Ability? CreateAbility(AbilityDef? def, Player caster) => def?.Id switch
    {
        "firepatch" => new FirePatchAbility(def, caster),
        "dropkick" => new DropKickAbility(def, caster),
        _ => null,
    };

    // ------------------------------------------------------------------
    // Ability activation (client -> server), separate timer from the melee state machine —
    // see abilities/Ability.cs's class doc for why they're decoupled
    // ------------------------------------------------------------------

    private void RequestAbilityLocal()
    {
        if (_combatState != CombatState.Idle)
            return;

        _spawnProtectionRemaining = 0f; // cancelled instantly on any deliberate action, same as the melee verbs
        FaceAim(); // abilities aim with the camera too (the drop kick goes where you look)

        if (_isOffline)
        {
            _ability?.TryActivate();
            return;
        }

        var yaw = GlobalRotation.Y;
        Net.Instance.SendWithSimulation(() => RpcId(1, nameof(RequestAbility), yaw));
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestAbility(float yaw)
    {
        if (!_isServer)
            return;
        if (Multiplayer.GetRemoteSenderId() != _peerId)
            return;
        if (_combatState != CombatState.Idle)
            return;

        _serverPendingYaw = yaw;
        GlobalRotation = new Vector3(GlobalRotation.X, yaw, GlobalRotation.Z);
        if (_spawnProtectionRemaining > 0f)
            SetSpawnProtection(0f); // the ability cancels protection on the server too, not just on screen

        _ability?.TryActivate();
    }

    /// <summary>Called by Ability.TryActivate() the instant a windup starts — broadcasts a
    /// visible tell on the caster (grey-box stand-in: an emissive flash, see
    /// UpdateVisualEffects) to every client, since only the server ever runs ability logic and
    /// nothing here replicates automatically.</summary>
    public void BroadcastAbilityTell(string abilityId, float tellSeconds)
    {
        if (_isOffline)
            ReceiveAbilityTell(tellSeconds); // practice: no server to broadcast it
        else if (_isServer)
            Rpc(nameof(ReceiveAbilityTell), tellSeconds);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
    private void ReceiveAbilityTell(float tellSeconds)
    {
        if (!_isServer && !_isOffline && Multiplayer.GetRemoteSenderId() != 1)
            return;

        // A character whose animation set has an Ability clip (Zain's drop kick) acts it out, and
        // the animation is the tell: no colour flash on top. Its slice is timed so the hit lands
        // as the tell ends. Abilities without an animation still flash.
        var actedOut = _model is not null && _modelAnimations is not null
            && _model.PlayOneShot(CharacterModel.Clip.Ability, _modelAnimations.AbilityDuration);
        if (!actedOut)
            _abilityTellRemaining = tellSeconds;

        // The feet come down just after the tell (the lunge is short): a thump and a dust burst.
        if (DisplayServer.GetName() != "headless")
        {
            GetTree().CreateTimer(tellSeconds + 0.12).Timeout += () =>
            {
                if (!IsInstanceValid(this) || !IsInsideTree())
                    return;
                GameAudio.Play3D(this, "kick", GlobalPosition, 2f);
                Fx.Dust(this, GlobalPosition, 1.4f);
            };
        }

        if (_isOwner)
            _abilityReadyAt = Time.GetTicksMsec() / 1000.0 + tellSeconds + AbilityCooldownTotal;
    }
    private float _abilityTellRemaining;

    // ------------------------------------------------------------------
    // Ability shared-kit support (CHARACTER-SPEC.md Part 1: abilities build on these, they
    // don't invent their own systems) — called from abilities/Ability.cs's protected helpers
    // ------------------------------------------------------------------

    public void ServerTeleport(Vector3 position)
    {
        if (!_isServer)
            return;
        GlobalPosition = position;
    }

    public void ServerApplyDisplacement(Vector3 impulse)
    {
        if (!_isServer)
            return;
        Velocity += impulse;
    }

    /// <summary>Duration is expected pre-clamped by Ability.ApplySlow (≤ Tuning.MaxControlEffectDuration).
    /// Multiple overlapping slows take the strongest (lowest) multiplier rather than stacking multiplicatively.</summary>
    public void ServerApplySlow(float speedMultiplier, float durationSeconds)
    {
        if (!_isServer)
            return;
        _slowMultiplier = Mathf.Min(_slowMultiplier, speedMultiplier);
        _slowTimeRemaining = Mathf.Max(_slowTimeRemaining, durationSeconds);
    }

    public void ServerApplyReveal(float durationSeconds)
    {
        if (!_isServer)
            return;
        Rpc(nameof(BroadcastReveal), durationSeconds);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
    private void BroadcastReveal(float durationSeconds)
    {
        if (!_isServer && Multiplayer.GetRemoteSenderId() != 1)
            return;
        _revealRemaining = durationSeconds;
    }

    /// <summary>Zone Denial / Trap shared kit (Ability.SpawnDamageZone): broadcasts from the
    /// caster so every client spawns its own AbilityZone — visible everywhere, damage only ever
    /// real on the server copy (see AbilityZone's own IsServer guard).</summary>
    public void ServerSpawnDamageZone(Vector3 position, float radius, float durationSeconds, float damagePerSecond, string method, Color color)
    {
        if (!_isServer)
            return;
        Rpc(nameof(BroadcastAbilityZone), position, radius, durationSeconds, damagePerSecond, method, color);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void BroadcastAbilityZone(Vector3 position, float radius, float durationSeconds, float damagePerSecond, string method, Color color)
    {
        if (!_isServer && Multiplayer.GetRemoteSenderId() != 1)
            return;

        var attackerId = _peerId; // this node IS the caster
        AbilityZone.Spawn(position, radius, durationSeconds, color, oneShot: false, (victim, delta) =>
        {
            victim.ServerApplyDamage(damagePerSecond * delta, attackerId, method);
        });
    }

    /// <summary>Cosmetic-only "ragdoll": a primitive tumbling capsule dropped where the player
    /// died, no networking, each client spawns its own on receiving BroadcastKill. Grey-box
    /// stand-in per plan/phase-2-greybox-combat.md — a real skinned ragdoll waits for Phase 5
    /// character art. Self-frees after a few seconds.</summary>
    private static void SpawnDeathEffect(Vector3 position)
    {
        // Cosmetic only — never on the dedicated server. BroadcastKill is CallLocal, so the
        // server used to spawn its own physical corpse too, and live players' authoritative
        // movement got blocked by it (seen in a sweep diagnostic: "blockedBy=[@RigidBody3D@7]")
        // while every client showed its own corpse somewhere else — an unexplainable stop,
        // which Pillar 2 forbids.
        if (Net.Instance.IsServer)
            return;

        var tree = (SceneTree)Engine.GetMainLoop();
        var root = tree.CurrentScene;
        if (root is null) return;

        // Set local Position, not GlobalPosition: the node has no parent yet, and
        // GlobalPosition's setter reads the current global transform to compute a relative
        // offset — on a node not yet in the tree that read fails (logs a harmless-but-noisy
        // engine error and falls back to identity). Local == global with no parent, so this is
        // equivalent and avoids the tree dependency entirely.
        // Layer 2, mask 1: the corpse lands on the world but nothing on layer 1 (players, the
        // world) ever collides with it — it can't block, push, or be stood on by anyone.
        var body = new RigidBody3D { Position = position + Vector3.Up * 0.3f, CollisionLayer = 2, CollisionMask = 1 };
        var shape = new CollisionShape3D { Shape = new CapsuleShape3D { Radius = 0.35f, Height = 1.6f } };
        var mesh = new MeshInstance3D { Mesh = new CapsuleMesh { Radius = 0.35f, Height = 1.6f } };
        body.AddChild(shape);
        body.AddChild(mesh);
        root.AddChild(body);

        var rng = new RandomNumberGenerator();
        body.ApplyImpulse(new Vector3(rng.RandfRange(-2f, 2f), rng.RandfRange(1f, 3f), rng.RandfRange(-2f, 2f)));
        body.ApplyTorqueImpulse(new Vector3(rng.RandfRange(-3f, 3f), rng.RandfRange(-3f, 3f), rng.RandfRange(-3f, 3f)));

        tree.CreateTimer(3.0).Timeout += () => { if (IsInstanceValid(body)) body.QueueFree(); };
    }

    private void RunDeathCam(double delta)
    {
        _deathCamRemaining = Mathf.Max(0f, _deathCamRemaining - (float)delta);

        var toKiller = _deathCamLookAt - GlobalPosition;
        toKiller.Y = 0;
        if (toKiller.LengthSquared() > 0.01f)
        {
            _cameraYaw = YawFacing(toKiller.Normalized());
        }
    }

    /// <summary>Grey-box stand-in for every "needs a visible tell/state" requirement that
    /// doesn't have a real VFX asset yet (spawn protection shimmer GDD §5.7; an ability's tell,
    /// CHARACTER-SPEC.md Part 1; being Revealed by an Information-slot ability) — an emissive
    /// color flash on the same mesh material. Real shader work is Phase 5 art-pass territory.
    /// Priority when more than one is active at once: ability tell, then reveal, then spawn
    /// protection — arbitrary but consistent, and rare to actually overlap.</summary>
    private void UpdateVisualEffects(double delta)
    {
        if (_abilityTellRemaining > 0f)
            _abilityTellRemaining = Mathf.Max(0f, _abilityTellRemaining - (float)delta);

        Color? color = _abilityTellRemaining > 0f ? new Color(0.2f, 0.9f, 1f) // cyan
            : _revealRemaining > 0f ? new Color(1f, 0.15f, 0.15f) // red
            : _spawnProtectionRemaining > 0f ? new Color(1f, 1f, 1f) // white
            : null;

        // Protection pulses at partial strength so the character's texture still shows through;
        // tells and reveals stay at full strength.
        var energy = _abilityTellRemaining <= 0f && _revealRemaining <= 0f && _spawnProtectionRemaining > 0f
            ? 0.25f + 0.35f * (0.5f + 0.5f * Mathf.Sin((float)Time.GetTicksMsec() / 1000f * Mathf.Tau * 3f))
            : 1f;
        foreach (var material in _bodyMaterials)
        {
            material.EmissionEnabled = color is not null;
            if (color is { } c)
            {
                material.Emission = c;
                material.EmissionEnergyMultiplier = energy;
            }
        }
    }

    // ------------------------------------------------------------------
    // Bot AI — wander + occasional verbs, see core/Net.cs --bot
    // ------------------------------------------------------------------

    private void PickNewBotTarget()
    {
        var reach = Mathf.Max(4f, Main.ArenaHalfExtent - 4f); // anywhere on the map, away from the walls
        var x = (float)GD.RandRange(-reach, reach);
        var z = (float)GD.RandRange(-reach, reach);
        _botTarget = new Vector3(x, GlobalPosition.Y, z);
        _botRetargetIn = GD.RandRange(2.0, 5.0);
    }

    private void RunBotAi(double delta)
    {
        if (IsDead)
        {
            _botMoveInput = Vector2.Zero;
            return;
        }

        _botRetargetIn -= delta;
        if (_botRetargetIn <= 0 || GlobalPosition.DistanceTo(_botTarget) < 1.0f)
            PickNewBotTarget();

        var toTarget = _botTarget - GlobalPosition;
        toTarget.Y = 0;

        if (toTarget.LengthSquared() > 0.01f)
        {
            var lookBasis = Basis.LookingAt(toTarget.Normalized(), Vector3.Up);
            var desiredYaw = lookBasis.GetEuler().Y;
            if (_dodgeTimeRemaining <= 0f) // bots too: no turning mid-roll
                GlobalRotation = new Vector3(GlobalRotation.X, Mathf.LerpAngle(GlobalRotation.Y, desiredYaw, 0.1f), GlobalRotation.Z);
            _botMoveInput = new Vector2(0, -1); // "forward" per the move_forward/back convention below
        }
        else
        {
            _botMoveInput = Vector2.Zero;
        }

        _botVerbIn -= delta;
        if (_botVerbIn <= 0)
        {
            // Weighted toward light (cheap, spammable) with occasional heavy/dodge — rough
            // stand-in for "a bot that presses buttons", not remotely competent play.
            var roll = GD.Randf();
            var verb = roll switch
            {
                < 0.60f => Verb.Light,
                < 0.85f => Verb.Heavy,
                _ => Verb.Dodge,
            };
            RequestVerbLocal(verb);
            _botVerbIn = GD.RandRange(1.0, 3.0);
        }

        _botAbilityIn -= delta;
        if (_botAbilityIn <= 0)
        {
            RequestAbilityLocal();
            _botAbilityIn = GD.RandRange(4.0, 10.0);
        }
    }

    public void SetDisplayName(string playerName)
    {
        if (NameLabel is not null)
            NameLabel.Text = playerName;
    }
}
