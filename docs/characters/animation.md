# Animation pipeline

**Goal:** every character plays the same animations, and a new model or a new clip needs no code.

## How it works

Godot's **humanoid retarget** runs at import time on both models and animation clips:

1. A `BoneMap` resource with the `SkeletonProfileHumanoid` profile maps a rig's own bone names
   (`mixamorig_LeftArm`) to standard names (`LeftUpperArm`).
2. The importer renames the bones and names the skeleton **`GeneralSkeleton`**.
3. It normalises hip height, so position keys scale to each body's proportions.
4. Animation tracks end up addressed as `%GeneralSkeleton:<StandardBone>`. They don't depend on
   which rig they came from.

Any retargeted model can therefore play any retargeted clip.
`tests/CharacterAnimationTests.cs` enforces that every clip only targets bones every registered
model has.

| File | Role |
|---|---|
| `assets/animations/humanoid/mixamo_bone_map.tres` | Mixamo → humanoid map, works for any Mixamo rig |
| `assets/animations/humanoid/*.fbx` | clips (run, jump, jump_up, jump_side, stab, dodge, death), imported as **AnimationLibrary** |
| `assets/animations/humanoid/humanoid_default.tres` | `HumanoidAnimationSet`: which clip plays for which move, attack slices and durations |
| `animation/CharacterModel.cs` | builds the playable clips and picks one each frame |

## What CharacterModel does with the clips

- **Strips root motion.** Mixamo clips often walk the hips forward, but the server-authoritative
  body moves the character, so the hips' X/Z are pinned to the first key. The jumps and the vault
  also have their upward lift clamped, since physics already raises the body.
- **Picks the jump at take-off.** Leaving the ground at walking speed or less plays `JumpUp`,
  faster plays `Jump`. The choice holds for the whole airtime, so air control can't swap clips
  mid-jump. The vault is a one-shot cued like the roll (`Player.BroadcastVaultCue`), timed to
  the vault's air time.
- **Matches run speed to movement.** The run plays faster or slower with the player's speed,
  measured against how fast the clip was authored, so feet don't slide.
- **Slices and time-scales attacks.** Each attack plays the slice `*ClipStart`–`*ClipEnd` of its
  clip, stretched to `*AttackDuration`, so the strike lands as the windup finishes.
- **Idle** uses the `Idle` clip if the set has one; otherwise it holds the light-attack clip's
  first frame, a fighting stance.
- **Attacks play only when the server says so** (`Player.BroadcastAttackCue`).

## Adding or replacing a clip

1. Download from Mixamo: **FBX, Without Skin, 30 fps**. "In Place" is optional, since root motion is
   stripped anyway.
2. Save it as `assets/animations/humanoid/<name>.fbx`, then set up its `.import` like `run.fbx.import`:
   - `importer="animation_library"` and `type="AnimationLibrary"`;
   - the same bone-map `_subresources` block.
3. Point a field of `humanoid_default.tres` at it. To give one character different clips, create
   another `HumanoidAnimationSet` and set it as that `CharacterDef.Animations`.
4. **For attacks,** choose the slice by eye:
   - temporarily set the attack's duration to the clip's full length;
   - run `make preview-animations` and note when the strike happens;
   - set `ClipStart`/`ClipEnd` around it;
   - set `AttackDuration` to windup + recovery from `tuning.tres`.
5. Run `make test`.

**A new kind of move** (say, a hit-react animation) needs a little code:

- a `Clip` enum value and a `HumanoidAnimationSet` field;
- handling in `CharacterModel.BuildLibrary`;
- a trigger from `Player`. For combat moves, cue it from a server broadcast.

## Non-Mixamo rigs

For Rigify, VRoid or other rigs, create a new `BoneMap` in the Advanced Import dialog:

- set the profile to `SkeletonProfileHumanoid`;
- Godot auto-fills most bones;
- use that map instead of the Mixamo one when importing.

The clips don't change: they only know the standard names.

## Held props (the knife)

`CharacterModel` puts a **`BoneAttachment3D` on the `RightHand` bone**, a standard humanoid name,
so it works on every retargeted model. It then instances the character's `HeldProp`
(default `assets/props/knife/knife.tscn`) under it.

- The model root is scaled up to the capsule height, so the prop's scale is set to the inverse.
  It keeps its real size.
- **The grip offset lives in `knife.tscn`**, on its `Mesh` child's transform. To re-seat the knife,
  edit that transform in the editor and check with `make preview-animations CLOSEUP=1`. No code
  changes.
- The knife mesh (`knife_mesh.res`) is **baked** from the source `knife.obj` (a 106-surface OBJ in
  centimetres) by `tools/bake_knife.gd`:
  - it merges the surfaces into 4 parts: blade, edge, bronze fittings and leather grip;
  - it gives them real materials;
  - it scales the knife to 45 cm;
  - it puts the origin at the grip, with +Y toward the tip.
  Re-run it after replacing the OBJ: `godot --headless --path . --script res://tools/bake_knife.gd`.
- Current seat: 9 cm into the palm, blade tilted 50° toward the fingers, so it points along the
  thrust in the stab clip.
- `tests/CharacterAnimationTests.cs` checks that the knife lands on the right-hand bone.

## Current clips

| Move | Clip | Slice | Plays in |
|---|---|---|---|
| Idle | first frame of `stab.fbx` | — | loops |
| Run | `run.fbx` (Mixamo "Fast Run") | whole | loops, speed-matched |
| Jump (moving) | `jump.fbx` | whole | while airborne |
| Jump (standing) | `jump_up.fbx` (Mixamo "Jumping Up") | from 0.45 s (skips the crouch) | while airborne |
| Vault | `jump_side.fbx` (Mixamo "Jumping Side", a sideways vault) | 0.15–1.1 s (without the run-up) | the vault's air time |
| Light attack | `stab.fbx` | 0.7–1.3 s | 0.35 s |
| Heavy attack | `stab.fbx` | 0.2–2.1 s | 1.0 s |
| Dodge (roll) | `dodge.fbx` | 0.25–1.25 s (the roll, without the get-up) | `Tuning.DodgeDuration` (0.55 s) |
| Death | `death.fbx` | whole | 1.3 s, then holds the pose until respawn |
| Ability (Zain only) | `dropkick.fbx` | 0.5–2.6 s | 1.0 s: the kick lands at 0.45 s, when the tell ends |

The roll and death keep their **vertical** hip motion (the dip and the fall), and the drop kick
keeps its jump. Only the horizontal drift is stripped.

A character's own set (Zain's `zain_animations.tres`) is a **full copy** of the shared set plus
its extras, not an overlay. A new shared clip has to be added to every such set as well;
`tests/CharacterAnimationTests.cs` (`EveryCharacterSetHasEverySharedClip`) fails if one is missing.

Preview another held prop with `make preview-animations PROP=res://assets/props/golden_knife/golden_knife.tscn CLOSEUP=1`.

Missing today: a dedicated idle and a hit-react clip.
