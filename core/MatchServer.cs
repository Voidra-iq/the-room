using System.Collections.Generic;
using Godot;
using TheRoom.Effects;
using TheRoom.Entities;

namespace TheRoom.Core;

/// <summary>
/// Autoload ("MatchServer" in project.godot). Owns the match loop (GDD §5.6/§5.5/§5.4, Pillar 4):
/// score/timer, Last Call, results + auto-rematch, Bounty, and the Golden Knife. Authoritative
/// on the server (and the offline-solo instance, which is its own authority same as everywhere
/// else in this codebase); every other peer just reacts to broadcasts from here, same
/// CallLocal=true pattern as Player.BroadcastKill.
///
/// Deliberately NOT built here (see plan/phase-4-match-shape.md): real shutter geometry for
/// Last Call (grey-box stand-in is a screen-tint announcement instead — level-design work, not
/// a systems gap), full map modularity / Duel-Pit-vs-Chaos map sections (one static room exists;
/// --config only switches the score target), a structured telemetry log (console prints only).
/// </summary>
public partial class MatchServer : Node
{
    public static MatchServer Instance { get; private set; } = null!;

    private enum MatchState { InProgress, LastCall, Ended }
    private MatchState _state = MatchState.InProgress;
    private double _matchElapsed;
    private double _resultsTimeRemaining;
    private int _scoreTarget;
    // This peer actually runs match logic (server, or offline-solo). Read live: a client can
    // switch between menu, practice and online rooms without restarting the game.
    private bool _isAuthoritative => Net.Instance.IsServer || Net.Instance.IsOffline;
    // True while the game scene (Main.tscn) is loaded. The main menu has no match to run.
    private bool _sessionActive;

    private readonly Dictionary<long, int> _score = new();
    private readonly Dictionary<long, int> _bounty = new();
    private readonly Dictionary<long, int> _kills = new();
    private readonly Dictionary<long, int> _deaths = new();

    // Phase 5 award telemetry (GDD §7 "award titles"). All server-only, all reset in
    // ResetMatch() alongside score/bounty. Console prints double as the "structured log" this
    // phase's plan doc explicitly deferred building for real — genuinely enough for now since
    // nothing consumes it but the end-of-match announcement below.
    private readonly Dictionary<long, int> _dodges = new();
    private readonly Dictionary<long, int> _heavyWhiffs = new();
    private readonly Dictionary<long, int> _executesTaken = new(); // died to an execute this match
    private readonly Dictionary<long, int> _biggestBountyClaimed = new(); // largest single bounty collected in one kill

    public string StateName => _state switch
    {
        MatchState.LastCall => "last call",
        MatchState.Ended => "results",
        _ => "playing",
    };
    public bool IsLastCall => _state == MatchState.LastCall;
    public bool IsResults => _state == MatchState.Ended;
    public int GetScore(long peerId) => _score.GetValueOrDefault(peerId);
    public int GetBounty(long peerId) => _bounty.GetValueOrDefault(peerId);
    public float MatchTimeRemaining => Mathf.Max(0f, TuningService.Instance.MatchTimeLimitSeconds - (float)_matchElapsed);
    public float ResultsTimeRemaining => Mathf.Max(0f, (float)_resultsTimeRemaining);
    public int ScoreTarget => _scoreTarget;

    // Persistent (for the whole results screen, not just a 4s banner flash) — see
    // core/KillfeedUI.cs's ResultsPanel, populated once here per match, read every frame there.
    public string LastMvpText { get; private set; } = "";
    public IReadOnlyList<string> LastAwards { get; private set; } = System.Array.Empty<string>();
    public IReadOnlyList<(string name, int score)> LastStandings { get; private set; } = System.Array.Empty<(string, int)>();

    // --- Golden Knife (GDD §5.4) ---
    private enum KnifeState { Respawning, Available, Held }
    private KnifeState _knifeState = KnifeState.Respawning;
    private double _knifeTimer;
    private long _knifeHolderId = -1;
    private static readonly Vector3 KnifeSpawnPosition = new(0, 1.5f, 0);
    private Node3D? _knifeVisual;
    public const string GoldenKnifePropPath = "res://assets/props/golden_knife/golden_knife.tscn";

    public bool IsGoldenKnifeHolder(long peerId) => _knifeState == KnifeState.Held && _knifeHolderId == peerId;

    public override void _Ready()
    {
        Instance = this;
    }

    /// <summary>Called by Main when the game scene loads (dedicated server, online room or
    /// practice). Starts a fresh match with this session's config.</summary>
    public void BeginSession()
    {
        ClearMatchState();
        _scoreTarget = Net.Instance.IsChaosConfig ? TuningService.Instance.ScoreTargetChaos : TuningService.Instance.ScoreTargetDuelPit;
        _lastSyncedState = MatchState.InProgress;
        _clockSyncTimer = 0;
        LastMvpText = "";
        LastAwards = System.Array.Empty<string>();
        LastStandings = System.Array.Empty<(string, int)>();
        _sessionActive = true;
    }

    /// <summary>Called by Main when the game scene unloads (leaving a room). The next room or
    /// practice session must not inherit this one's scores, knife or results.</summary>
    public void EndSession()
    {
        _sessionActive = false;
        SetKnifeVisual(false);
        SetHolderVisuals(-1);
        ClearMatchState();
    }

    private void ClearMatchState()
    {
        _state = MatchState.InProgress;
        _matchElapsed = 0;
        _resultsTimeRemaining = 0;
        _score.Clear();
        _bounty.Clear();
        _kills.Clear();
        _deaths.Clear();
        _dodges.Clear();
        _heavyWhiffs.Clear();
        _executesTaken.Clear();
        _biggestBountyClaimed.Clear();
        _knifeState = KnifeState.Respawning;
        _knifeTimer = TuningService.Instance.GoldenKnifeFirstSpawn;
        _knifeHolderId = -1;
        SetHolderVisuals(-1);
    }

    private double _clockSyncTimer;
    private MatchState _lastSyncedState = MatchState.InProgress;

    public override void _PhysicsProcess(double delta)
    {
        if (!_sessionActive)
            return;

        if (!_isAuthoritative)
        {
            // Non-authoritative peers never run match logic — only a local display countdown
            // between the server's clock syncs (BroadcastMatchClock), so the scoreboard timer and
            // results countdown tick smoothly instead of jumping once a second.
            if (_state is MatchState.InProgress or MatchState.LastCall)
                _matchElapsed += delta;
            else
                _resultsTimeRemaining = Mathf.Max(0, _resultsTimeRemaining - delta);
            return;
        }

        TickMatch(delta);
        TickGoldenKnife(delta);

        // Before this, match state/clock/score-target/scores were server-only: on every client
        // IsResults and IsLastCall were permanently false (so the results screen could never
        // show), the timer never moved, clients used their OWN --config's score target, and late
        // joiners never learned existing scores. Found by rendering real client frames. Synced
        // once a second (late joiners, drift) and immediately on every state change.
        _clockSyncTimer -= delta;
        if (Net.Instance.IsServer && (_state != _lastSyncedState || _clockSyncTimer <= 0))
        {
            _lastSyncedState = _state;
            _clockSyncTimer = 1.0;
            Rpc(nameof(BroadcastMatchClock), (int)_state, _matchElapsed, _resultsTimeRemaining, _scoreTarget, SerializeScores());
        }
    }

    private string SerializeScores()
    {
        var parts = new List<string>();
        foreach (var (id, s) in _score)
            parts.Add($"{id}:{s}");
        return string.Join(";", parts);
    }

    private void TickMatch(double delta)
    {
        var tuning = TuningService.Instance;

        switch (_state)
        {
            case MatchState.InProgress:
            case MatchState.LastCall:
                _matchElapsed += delta;
                var timeLeft = tuning.MatchTimeLimitSeconds - _matchElapsed;
                var leaderScore = 0;
                foreach (var s in _score.Values)
                    if (s > leaderScore) leaderScore = s;

                if (_state == MatchState.InProgress &&
                    (leaderScore >= _scoreTarget * tuning.LastCallThresholdPercent || timeLeft <= tuning.LastCallTimeRemainingSeconds))
                {
                    EnterLastCall();
                }

                if (leaderScore >= _scoreTarget || timeLeft <= 0)
                {
                    EndMatch();
                }
                break;

            case MatchState.Ended:
                _resultsTimeRemaining -= delta;
                if (_resultsTimeRemaining <= 0)
                    ResetMatch();
                break;
        }
    }

    private void EnterLastCall()
    {
        _state = MatchState.LastCall;
        GD.Print("[Match] Last Call.");
        Rpc(nameof(BroadcastAnnouncement), "LAST CALL");
    }

    private void EndMatch()
    {
        _state = MatchState.Ended;
        _resultsTimeRemaining = TuningService.Instance.ResultsScreenDurationSeconds;

        long mvpId = -1;
        var mvpScore = -1;
        foreach (var (id, s) in _score)
        {
            if (s <= mvpScore) continue;
            mvpScore = s;
            mvpId = id;
        }

        var mvpName = mvpId >= 0 ? Main.GetPlayerName(mvpId) : "Nobody";
        var mvpText = $"MVP: {mvpName} ({Mathf.Max(0, mvpScore)} pts)";
        GD.Print($"[Match] Ended. {mvpText}.");
        Rpc(nameof(BroadcastAnnouncement), $"MATCH OVER — {mvpText}");

        // Awards go on the results screen (KillfeedUI's ResultsPanel), not the banner: sent as
        // successive banners, each replaced the last instantly, so only the final award was ever
        // readable and it wiped "MATCH OVER — MVP" off the screen (seen in real client frames).
        var awards = ComputeAwards();

        var standings = new List<(string, int)>();
        foreach (var (id, s) in _score)
            standings.Add((Main.GetPlayerName(id), s));
        standings.Sort((a, b) => b.Item2.CompareTo(a.Item2));

        var standingsJoined = string.Join(";", standings.ConvertAll(s => $"{s.Item1}:{s.Item2}"));
        var awardsJoined = string.Join("|", awards);
        Rpc(nameof(BroadcastMatchResults), mvpText, awardsJoined, standingsJoined);

        // GDD §4 meta loop: persistent season stats + (optional, unconfigured by default)
        // results webhook. See core/SeasonStats.cs.
        SeasonStats.Instance.RecordMatch(standings, awards);
        SeasonStats.Instance.PostResultsWebhook(mvpText, standings, awards);

        // Match history (lobby-managed rooms only): everyone who played, including anyone who
        // scored and then left, with the character they were playing if still connected.
        var characters = new Dictionary<long, string>();
        foreach (var player in CombatServer.Instance.AllPlayers())
            characters[player.PeerId] = player.Character?.Id ?? "";
        var peers = new HashSet<long>(_score.Keys);
        peers.UnionWith(_kills.Keys);
        peers.UnionWith(_deaths.Keys);
        peers.UnionWith(characters.Keys);

        var report = new List<RoomReporter.PlayerResult>();
        foreach (var id in peers)
        {
            report.Add(new RoomReporter.PlayerResult(Main.GetPlayerName(id), characters.GetValueOrDefault(id, ""),
                _score.GetValueOrDefault(id), _kills.GetValueOrDefault(id), _deaths.GetValueOrDefault(id)));
        }
        RoomReporter.Instance.ReportMatch(_matchElapsed, report, awards);
    }

    /// <summary>GDD §7 "award titles" — computed from this match's telemetry, one line per
    /// award that actually happened (a stat of 0 doesn't get a title; nobody needs to be told
    /// they whiffed zero heavies). Real presentation (victory poses, etc.) is Phase 5 art-pass
    /// territory; this is the systems half.</summary>
    private List<string> ComputeAwards()
    {
        var awards = new List<string>();

        void AddTop(Dictionary<long, int> stat, string title, string suffix)
        {
            long bestId = -1;
            var best = 0;
            foreach (var (id, v) in stat)
            {
                if (v <= best) continue;
                best = v;
                bestId = id;
            }
            if (bestId >= 0)
                awards.Add($"{title}: {Main.GetPlayerName(bestId)} ({best} {suffix})");
        }

        AddTop(_executesTaken, "MOST STABBED IN THE BACK", "times");
        AddTop(_dodges, "UNTOUCHABLE", "dodged hits");
        AddTop(_heavyWhiffs, "ALL BARK, NO BITE", "whiffed heavies");
        AddTop(_biggestBountyClaimed, "HIGHWAY ROBBERY", "pts in one bounty");

        return awards;
    }

    private void ResetMatch()
    {
        GD.Print("[Match] New match starting.");
        ClearMatchState();

        foreach (var player in CombatServer.Instance.AllPlayers())
            player.ServerMatchReset();

        Rpc(nameof(BroadcastAnnouncement), "NEW MATCH");
    }

    /// <summary>Server-only (or offline-solo). Called from Player.ServerApplyDamage's lethal
    /// branch — updates score, bounty, and Golden Knife holder state for one kill.</summary>
    public void ServerRegisterKill(long attackerId, long victimId, string method)
    {
        if (!_isAuthoritative || _state == MatchState.Ended)
            return; // no scoring during the results screen

        var tuning = TuningService.Instance;
        var victimBounty = _bounty.GetValueOrDefault(victimId);
        var points = 1 + victimBounty;

        if (IsGoldenKnifeHolder(attackerId))
            points = Mathf.RoundToInt(points * tuning.GoldenKnifeScoreMultiplier);

        _score[attackerId] = _score.GetValueOrDefault(attackerId) + points;
        _kills[attackerId] = _kills.GetValueOrDefault(attackerId) + 1;
        _deaths[victimId] = _deaths.GetValueOrDefault(victimId) + 1;
        _bounty[attackerId] = _bounty.GetValueOrDefault(attackerId) + 1;
        _bounty[victimId] = 0;

        if (method == "execute")
            _executesTaken[victimId] = _executesTaken.GetValueOrDefault(victimId) + 1;

        var newBounty = _bounty[attackerId];
        if (newBounty == tuning.BountyAnnounceOnATear)
            Rpc(nameof(BroadcastAnnouncement), $"{Main.GetPlayerName(attackerId)} IS ON A TEAR");
        else if (newBounty == tuning.BountyAnnounceSecond || newBounty == tuning.BountyAnnounceThird)
            Rpc(nameof(BroadcastAnnouncement), $"{Main.GetPlayerName(attackerId)} IS UNSTOPPABLE ({newBounty})");

        if (victimBounty > 0)
        {
            Rpc(nameof(BroadcastAnnouncement), $"{Main.GetPlayerName(attackerId)} COLLECTED {Main.GetPlayerName(victimId)}'S BOUNTY (+{victimBounty})");
            if (victimBounty > _biggestBountyClaimed.GetValueOrDefault(attackerId))
                _biggestBountyClaimed[attackerId] = victimBounty;
        }

        if (IsGoldenKnifeHolder(victimId))
            LoseGoldenKnife();

        Rpc(nameof(BroadcastScore), attackerId, _score[attackerId]);
    }

    /// <summary>Server-only. CombatServer reports every strike a player rolled through —
    /// award telemetry ("Untouchable").</summary>
    public void ServerRegisterDodge(long defenderId)
    {
        if (!_isAuthoritative)
            return;
        _dodges[defenderId] = _dodges.GetValueOrDefault(defenderId) + 1;
    }

    /// <summary>Server-only. Player.ResolveMeleeAttack calls this when a heavy lands nobody —
    /// Phase 5 award telemetry ("All Bark, No Bite").</summary>
    public void ServerRegisterHeavyWhiff(long attackerId)
    {
        if (!_isAuthoritative)
            return;
        _heavyWhiffs[attackerId] = _heavyWhiffs.GetValueOrDefault(attackerId) + 1;
    }

    /// <summary>Server-only. Player.ServerApplyDamage picks between Tuning.RespawnTime and
    /// RespawnTimeLastCall based on this.</summary>
    public float CurrentRespawnTime => IsLastCall ? TuningService.Instance.RespawnTimeLastCall : TuningService.Instance.RespawnTime;

    // ------------------------------------------------------------------
    // Golden Knife
    // ------------------------------------------------------------------

    private void TickGoldenKnife(double delta)
    {
        switch (_knifeState)
        {
            case KnifeState.Respawning:
                _knifeTimer -= delta;
                if (_knifeTimer <= 0)
                {
                    _knifeState = KnifeState.Available;
                    Rpc(nameof(BroadcastAnnouncement), "THE GOLDEN KNIFE HAS APPEARED");
                }
                break;

            case KnifeState.Available:
                foreach (var player in CombatServer.Instance.AllPlayers())
                {
                    if (player.IsDead)
                        continue;
                    if (player.GlobalPosition.DistanceTo(KnifeSpawnPosition) > TuningService.Instance.GoldenKnifePickupRadius)
                        continue;

                    _knifeState = KnifeState.Held;
                    _knifeHolderId = player.PeerId;
                    _knifeTimer = TuningService.Instance.GoldenKnifeDuration;
                    Rpc(nameof(BroadcastKnifeHolder), player.PeerId);
                    Rpc(nameof(BroadcastAnnouncement), $"{Main.GetPlayerName(player.PeerId)} TOOK THE GOLDEN KNIFE");
                    break;
                }
                break;

            case KnifeState.Held:
                _knifeTimer -= delta;
                if (_knifeTimer <= 0)
                    LoseGoldenKnife();
                break;
        }
    }

    private void LoseGoldenKnife()
    {
        _knifeState = KnifeState.Respawning;
        _knifeHolderId = -1;
        _knifeTimer = TuningService.Instance.GoldenKnifeRespawnDelay;
        Rpc(nameof(BroadcastKnifeHolder), -1L);
        Rpc(nameof(BroadcastAnnouncement), "THE GOLDEN KNIFE WAS LOST");
    }

    /// <summary>Cosmetic-only pickup marker: the Golden Knife model spinning above the plinth,
    /// spawned/removed on every peer independently in reaction to the announcement broadcast, same
    /// pattern as Player.SpawnDeathEffect. No collision, purely visual; the actual pickup check above
    /// is a plain distance check run only where _isAuthoritative is true.</summary>
    private void SetKnifeVisual(bool visible)
    {
        if (visible)
        {
            if (_knifeVisual is not null && IsInstanceValid(_knifeVisual))
                return;

            var tree = (SceneTree)Engine.GetMainLoop();
            var root = tree.CurrentScene;
            if (root is null || DisplayServer.GetName() == "headless")
                return;

            // The prop is posed for a hand (tilted 50° forward, see golden_knife.tscn); tilt it
            // back so the blade stands straight up, and scale it up to read from across the arena.
            var pivot = new Node3D { Name = "GoldenKnifePickup", Position = KnifeSpawnPosition + Vector3.Down * 0.15f };
            var knife = GD.Load<PackedScene>(GoldenKnifePropPath).Instantiate<Node3D>();
            knife.Rotation = new Vector3(Mathf.DegToRad(-50f), 0f, 0f);
            knife.Scale = Vector3.One * 2.5f;
            pivot.AddChild(knife);
            pivot.AddChild(new OmniLight3D { Position = Vector3.Up * 0.4f, LightColor = new Color(1f, 0.8f, 0.3f), LightEnergy = 1.5f, OmniRange = 4f });
            root.AddChild(pivot);
            var spin = pivot.CreateTween().SetLoops();
            spin.TweenProperty(pivot, "rotation:y", Mathf.Tau, 3.0).From(0f);
            _knifeVisual = pivot;
        }
        else
        {
            if (_knifeVisual is not null && IsInstanceValid(_knifeVisual))
                _knifeVisual.QueueFree();
            _knifeVisual = null;
        }
    }

    // ------------------------------------------------------------------
    // Broadcasts — autoloads have the same stable NodePath on every peer, so RPCs work the same
    // way as Player's per-node broadcasts (BroadcastKill), just without needing a Player instance.
    // ------------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void BroadcastAnnouncement(string text)
    {
        if (!_isAuthoritative && Multiplayer.GetRemoteSenderId() != 1)
            return;

        GD.Print($"[Match] {text}");

        if (text.Contains("GOLDEN KNIFE HAS APPEARED")) SetKnifeVisual(true);
        else if (text.Contains("TOOK THE GOLDEN KNIFE") || text.Contains("GOLDEN KNIFE WAS LOST")) SetKnifeVisual(false);

        Events.Instance.EmitSignal(Events.SignalName.MatchAnnouncement, text);
    }

    /// <summary>Who holds the Golden Knife (-1: nobody). Every client puts the gold beam
    /// (effects/GoldenBeam) on that player, so the whole room can see the holder from anywhere, and
    /// swaps their knife for the Golden Knife.</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void BroadcastKnifeHolder(long holderId)
    {
        if (!_isAuthoritative && Multiplayer.GetRemoteSenderId() != 1)
            return;
        SetHolderVisuals(holderId);
    }

    private Node3D? _holderBeam;
    private long _visualHolderId = -1;

    private void SetHolderVisuals(long holderId)
    {
        if (_holderBeam is not null && IsInstanceValid(_holderBeam))
            _holderBeam.QueueFree();
        _holderBeam = null;
        var players = ((SceneTree)Engine.GetMainLoop()).CurrentScene?.GetNodeOrNull("PlayersContainer");
        if (players?.GetNodeOrNull<Player>(_visualHolderId.ToString()) is { } previous)
            previous.SetHoldsGoldenKnife(false);
        _visualHolderId = holderId;
        if (holderId < 0 || DisplayServer.GetName() == "headless")
            return;
        if (players?.GetNodeOrNull<Player>(holderId.ToString()) is not { } holder)
            return;
        _holderBeam = new GoldenBeam { Name = "GoldenBeam" };
        holder.AddChild(_holderBeam);
        holder.SetHoldsGoldenKnife(true);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
    private void BroadcastScore(long peerId, int newScore)
    {
        if (!_isAuthoritative && Multiplayer.GetRemoteSenderId() != 1)
            return;

        _score[peerId] = newScore;
    }

    /// <summary>Populates LastMvpText/LastAwards/LastStandings for the whole results screen
    /// (core/KillfeedUI.cs's ResultsPanel) — unlike BroadcastAnnouncement's transient banner
    /// lines, these persist until the next match's results.</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void BroadcastMatchResults(string mvpText, string awardsJoined, string standingsJoined)
    {
        if (!_isAuthoritative && Multiplayer.GetRemoteSenderId() != 1)
            return;

        LastMvpText = mvpText;
        LastAwards = string.IsNullOrEmpty(awardsJoined) ? System.Array.Empty<string>() : awardsJoined.Split('|');

        var standings = new List<(string, int)>();
        if (!string.IsNullOrEmpty(standingsJoined))
        {
            foreach (var entry in standingsJoined.Split(';'))
            {
                var parts = entry.Split(':');
                if (parts.Length == 2 && int.TryParse(parts[1], out var score))
                    standings.Add((parts[0], score));
            }
        }
        LastStandings = standings;
    }

    /// <summary>Server → clients, unreliable, once a second and on every state change — see the
    /// comment in _PhysicsProcess for the bug this fixes. Replaces the client's score table
    /// wholesale so resets and late joins both come out right.</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
    private void BroadcastMatchClock(int state, double matchElapsed, double resultsRemaining, int scoreTarget, string scoresJoined)
    {
        if (_isAuthoritative || Multiplayer.GetRemoteSenderId() != 1)
            return;

        _state = (MatchState)state;
        _matchElapsed = matchElapsed;
        _resultsTimeRemaining = resultsRemaining;
        _scoreTarget = scoreTarget;

        _score.Clear();
        foreach (var entry in scoresJoined.Split(';', System.StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split(':');
            if (parts.Length == 2 && long.TryParse(parts[0], out var id) && int.TryParse(parts[1], out var s))
                _score[id] = s;
        }
    }
}
