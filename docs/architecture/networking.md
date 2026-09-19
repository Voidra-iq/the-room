# Networking

The Room uses Godot's high-level multiplayer over **ENet (UDP)**, with the server as the only
authority. This follows Pillar 2: every hit and death has to be explainable, so there is exactly
one simulation that decides.

## Tick and transport

- Physics runs at `Tuning.ServerTickRateHz` (30 Hz) on every peer. `Net._Ready` sets
  `Engine.PhysicsTicksPerSecond`.
- Movement input and state snapshots are **unreliable** RPCs, sent every tick. Verbs, kills,
  identity and announcements are **reliable**.
- Default port 60010. Each lobby room gets its own port (60011–60030 in production).

## The three roles of a Player node

Each connected player has a `Player` node on **every** peer. `Player._Ready` works out which role
this copy plays:

| Role | Runs | Details |
|---|---|---|
| **Server** | Authoritative `SimulateStep` with the latest input, the combat state machine, abilities | Broadcasts `ReceiveServerState(tick, position, yaw, health, stamina)` every tick |
| **Owner** (your own player) | `SimulateStep` locally for zero-latency movement, then sends `SubmitInput` | Reconciles against server snapshots |
| **Remote view** (other players) | No physics | `InterpolateRemote()` renders them `InterpolationDelaySeconds` (0.1 s) in the past, between two snapshots |

### Prediction and reconciliation (owner)

The owner keeps a short history of predicted positions per tick. When a server snapshot arrives
for tick T, it compares it with its own prediction for T:

- error > `ReconciliationSnapDistance` (1.5 m): snap to the server position;
- small error: blend toward it over `ReconciliationSmoothTime`.

### Input packet

```
SubmitInput(ulong tick, Vector2 moveDir, float yaw, int jumpCounter, bool sprint)
```

- `moveDir` is the wanted direction **in world space** (x, z). The owner turns WASD into it
  relative to its camera. The server clamps it to length 1.
- `sprint` is Shift held. It's a held state, so an unreliable packet is fine: the next one repeats it.
- `yaw` is the **body's** facing. It isn't the camera's: see "Camera and facing" below.

One-shot actions travel as **counters**, not "pressed this tick" flags. The packet is unreliable,
so a dropped flag would lose the jump; the next packet still carries the higher count. The
server jumps when the counter goes up.

## Camera and facing

The camera and the body turn separately, the usual third-person setup:

- **The mouse turns only the camera** (`_cameraYaw`). The camera pivot is pinned to that yaw every
  frame, even though it's a child of the body.
- **The body turns to face where it moves** (`UpdateFacing`), smoothly (`TurnRate`). Walking back
  or sideways turns the character around, and the run animation plays forward.
- **Attacks and abilities aim with the camera.** `FaceAim()` snaps the body to the camera,
  and an **aim lock** (0.8 s) keeps it there, so a lunge or kick doesn't curve with your
  movement. `RequestVerb(verb, yaw)` and `RequestAbility(yaw)` carry that yaw, and the server
  applies it before acting, instead of waiting for the next unreliable input packet.
- Executes check the victim's **body** facing, so "behind" matches what everyone sees.
- Bots steer by turning their body; their camera follows it.

## Hit detection with rewind

Combat verbs go to the server as `RequestVerb`, a reliable RPC. When a windup finishes, the server
resolves the hit in `CombatServer`:

1. Every tick it records each player's hitbox position.
2. When resolving an attack from peer P, it rewinds the other players by P's one-way latency
   (RTT/2 from `PingService`), capped at `MaxRewindTimeSeconds` (0.2 s).
3. It tests the attack's range and radius against the rewound positions.

So you hit what you saw on your screen, within the cap.

## Moves that displace a player

The heavy lunge and Drop Kick use `Player.ServerSweep(motion)`: a `MoveAndSlide` in chunks of 0.4 m
or less, stopping when blocked. It never teleports. Teleports and single long sweeps were measured
to leave players inside geometry, and `MoveAndCollide` stopped on the floor contact and barely
moved.

## RPC patterns

```csharp
// Server → everyone (runs on the server too, because of CallLocal):
[Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true, TransferMode = …)]
private void BroadcastSomething(…)
{
    if (!_isServer && Multiplayer.GetRemoteSenderId() != 1)
        return; // only trust the server
    …
}

// Client → server:
[Rpc(MultiplayerApi.RpcMode.AnyPeer, …)]
private void RequestSomething(…)
{
    if (!_isServer) return;
    if (Multiplayer.GetRemoteSenderId() != _peerId) return; // can't act for someone else
    …
}
```

Autoloads (`MatchServer`, `PingService`) use the same pattern: their node paths are identical on
every peer.

## Spawning and identity

- `Main.tscn` has a `MultiplayerSpawner` watching `PlayersContainer`. The server adds a Player
  per connecting peer, and clients receive it automatically.
- The spawner replicates **creation only**. Name and character go through a handshake:
  - the owner sends `AnnounceIdentity`;
  - the server broadcasts `ReceiveIdentity`;
  - late joiners ask with `RequestIdentity`.
- Multiplayer authority isn't replicated either. Every peer derives it from the node name, which
  is the peer id.

## Cosmetics follow the server

Anything the player sees about combat is cued by a server broadcast, never by a local key press:
attack animations (`BroadcastAttackCue`), dodge rolls (`BroadcastDodgeCue`, except your own,
which is predicted), vaults (`BroadcastVaultCue`, the same way), death and revive (`BroadcastKill`, `BroadcastRevive`), the stab sound on a
landed hit (`BroadcastHitSound`),
spawn protection start and cancel (`BroadcastSpawnProtection`),
ability tells, kills, death effects and announcements. A
press the server rejects never shows a swing that didn't happen.

## Testing the network

- `--sim-latency=<ms>` / `--sim-loss=<0..1>` degrade one client's own outgoing RPCs.
- `make run-bots` fills a server with AI clients. Server logs print `[Combat]` lines for every hit.
- See [Testing](../testing.md).
