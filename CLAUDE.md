# CLAUDE.md — The Room

> **Self-update rule: keep this file in sync.** When you change the project's structure,
> commands, rules or conventions, or notice the user has, update this file in the same change,
> along with the matching `docs/` page, the matching `wiki/` page (anything a player sees) and any
> directory `CLAUDE.md` it affects. A stale CLAUDE.md, doc or wiki page is a bug: fix it as soon
> as you spot one.

## What this is

A third-person, server-authoritative knife-fight arena built by the **Voidra** team. It uses
**Godot 4.7.2 (.NET) and C#**, ENet multiplayer, and a small ASP.NET lobby service for rooms and
match history. **This repo is public.**

Design intent lives in three docs: the GDD, Design Pillars and Character Spec. The pillars that
shape code decisions:

- **Pillar 2:** every death must be explainable. The server decides everything, and visuals
  never show something the server didn't confirm.
- **Pillar 3:** every character is a caricature of a real developer. Never invent characters or
  abilities as if they were real submissions.

## Map

| Path | What | Own CLAUDE.md |
|---|---|---|
| `core/` | Autoloads (Net, MatchServer, CombatServer, …), `Main` game scene, lobby client/reporter | ✅ |
| `player/` | `Player.cs`: movement, prediction, combat state machine, bots | ✅ |
| `abilities/` | Ability grammar framework, character/ability definitions | ✅ |
| `animation/` | Retargeted character models + shared humanoid animations | ✅ |
| `ui/` | Main menu, in-game menu, theme, pagination (all built in C#) | ✅ |
| `services/` | `lobby/` (ASP.NET rooms + match history API) and `Lobby.Tests/` | ✅ |
| `maps/arena/` | The 60×60 m arena, **generated** by `tools/build_arena.gd` (CC0 props, box colliders, 16 spawns). Edit the generator, not the scene | |
| `tuning/` | `tuning.tres` + `Tuning.cs`: **every gameplay number** | |
| `assets/` | FBX models + textures, animations, props (knife, Golden Knife), audio (humanoid retarget, see `docs/characters/`) | |
| `characters/` | Per-character spec sheets (`_template/`, placeholder examples) | |
| `tests/` | GoDotTest suite (`make test`) | |
| `tools/` | Dev tools (`AnimationPreview`, `bake_knife.gd`, `build_arena.gd`) | |
| `deploy/` | nginx + systemd files for the VPS | |
| `plan/` | Phased build plan; `plan/main.md` is the progress + change log | |
| `docs/` | Project documentation for developers; start at `docs/intro.md` | |
| `wiki/` | **Player wiki** (rules, controls, map…), shown on GitHub and in the game (menu → Wiki); start at `wiki/intro.md` | ✅ |

## Commands

```bash
make build            # game (The Room.sln)
make test             # lobby xUnit + game GoDotTest; both must pass before a commit
make run-local N=2    # local server + N windowed clients (skips the menu)
make run-lobby        # local lobby, then: make run-client-menu
make run-bots N=6 HOST=… PORT=…
make preview-animations MODEL=res://…fbx
make deploy-server    # rsync + build + publish lobby + restart (VPS alias: kios-chat)
make deploy-nginx
make export-mac       # build/macos/The Room.app (see docs/building.md); export-client for Windows
```

The .NET SDK sits at `/usr/local/share/dotnet` and isn't on PATH. The Makefile adds it; in a raw
shell, `export PATH="/usr/local/share/dotnet:$PATH"`. Godot is `/Applications/Godot_mono.app/Contents/MacOS/Godot`.

## Hard rules

1. **Never commit the VPS's public IP address.** The hostnames `room-udp.iscoded.com` and
   `room-api.iscoded.com` are fine to commit. Before every commit, grep the staged diff for the
   IP (ask the user if you don't have it). No secrets, tokens or webhook URLs either.
2. **Server authority.** Clients send input and requests only. The server decides hits, damage,
   scores and movement outcomes. Cosmetic effects are cued by server broadcasts, never by a local
   key press (see `Player.BroadcastAttackCue`).
3. **Every gameplay number lives in `tuning/tuning.tres`.** Never hardcode damage, timings,
   radii or cooldowns.
4. **Ability grammar is enforced in code** (`abilities/Ability.cs`, `AbilityValidator`). Don't
   bypass it.
5. **Verify by running, not by reading.**
   - Build, then `make test`.
   - Gameplay or networking: run bots against a server and read the logs.
   - Anything visual: render frames with Godot's movie writer and look at them. The testing
     page in `docs/` explains how.
   - Report failures honestly.
6. Remove temporary diagnostics (`[Diag]` prints, shortened tuning) before committing, and
   restore `tuning.tres` if you changed it for a test.
7. Commits go to `main` (the user's workflow) and end with the `Co-Authored-By` trailer.
8. Deploying touches a **shared production box**. Only touch the-room's own user, directories,
   services and nginx site. Never change the firewall or other apps.

## Code style

- C# with nullable enabled. File-scoped namespaces: `TheRoom.Core`, `TheRoom.Entities` (player),
  `TheRoom.Config` (defs/tuning), `TheRoom.Abilities`, `TheRoom.Animation`, `TheRoom.UI`,
  `TheRoom.Lobby`.
- Godot classes are `partial`. PascalCase for members, `_camelCase` for private fields, `[Export]`
  for editor-tunable references.
- Comments explain **why**: a constraint, a bug that was found, which pillar applies. Don't
  narrate the code. Match the surrounding comment density.
- Prefer small, explicit code over abstraction. UI and theme are built in C#, not hand-written
  `.tres`/`.tscn` themes.
- Godot C# gotchas already hit in this repo:
  - use `OS.GetCmdlineUserArgs()`, not `GetCmdlineArgs()`, to read args after `--`;
  - the class is `CsgBox3D`, not `CSGBox3D`;
  - set `Position` rather than `GlobalPosition` before a node is in the tree;
  - a lambda parameter named `_` shadows the discard (`_ = Task` then fails);
  - `Environment` and `HttpClient` are ambiguous between Godot and System, so qualify them;
  - `Animation.Length` is a `double`.
- RPC broadcast pattern:
  - declare it `[Rpc(AnyPeer, CallLocal = true)]`;
  - guard it with `if (!_isServer && Multiplayer.GetRemoteSenderId() != 1) return;`;
  - client→server RPCs check `GetRemoteSenderId() == _peerId`.

## Documentation rule (always)

`docs/` is the source of truth for how the project works (entry: `docs/intro.md`). In the
**same change** as any code change:

- Behaviour, commands, CLI flags, API routes, config or file layout changed: update the pages
  that describe it.
- A new main area or feature: add a page (or a subfolder with its own `Intro.md`) and link it
  from `docs/intro.md`.
- Server install or deploy steps changed: update `docs/server-config.md` and
  `docs/deployment.md`.
- The user edited code: check whether docs or CLAUDE.md now say something false, and fix them.
- Add a change-log line to `plan/main.md` for each meaningful piece of work.

## Wiki rule (always)

`wiki/` is the **player** wiki: how the game works, for players, on GitHub and in the game
(main menu → Wiki, pause menu → Wiki). In the **same change** as anything a player would notice
(rules, numbers in `tuning.tres`, controls, HUD or menus, the map, settings, characters):

- Update the matching wiki page, and retake its screenshots if what they show changed.
- New feature: add a page or section, link it from `wiki/intro.md`, follow `wiki/CLAUDE.md`
  (page template, writing rules, supported markdown, screenshot rules).
- `make test` checks every wiki link, anchor and image.
