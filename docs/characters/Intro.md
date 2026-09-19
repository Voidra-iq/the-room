# Characters & animation

A character is three things:

| Part | Where | Owned by |
|---|---|---|
| **Identity and ability**: name, colour, ability, passive, spec answers | `abilities/CharacterRegistry.cs`, `characters/<id>/SPEC.md` | the developer the character caricatures (Pillar 3) |
| **Model**: any humanoid FBX, plus its texture | `assets/characters/<id>/` | the art owner |
| **Held prop**: the knife in the right hand | `assets/props/knife/` | the team |
| **Animations**: shared by everyone | `assets/animations/humanoid/` + `humanoid_default.tres` | the team |

Today the only model is **Zain** (`assets/characters/zain/zain.fbx`, Mixamo-rigged), textured by
`zain_albedo.png` through `zain_material.tres`. Every character without its own model uses Zain.

- The texture gets a light wash of the character's signature colour (25%), so skin stays skin.
- The name label above each player shows the character colour at full strength.

The shared animation set has **run, jump (running and standing), vault, stab, dodge (roll) and death**:

- the stab is used for both light and heavy attacks, with different slices;
- idle is the stab clip's opening stance;
- Zain's own set (`assets/characters/zain/zain_animations.tres`) adds the **drop kick** for his
  ability. `CharacterDef.AnimationsPath` points at it, loaded only on clients.

## Pages

- [Adding a character](adding-a-character.md): bring in a new model and give it to a character.
- [Animation pipeline](animation.md): how retargeting works, adding clips, attack slices, and
  rigs that don't come from Mixamo.

## Code

- `animation/CharacterModel.cs` builds the visible body: scale, facing, materials, clip selection.
- `animation/HumanoidAnimationSet.cs` is the resource mapping moves to clips.
- `abilities/CharacterDef.cs` has the `Model`, `Animations`, `TintModel` and `HeldProp` fields.
- `tests/CharacterAnimationTests.cs` checks that every clip can play on every model.
- `tools/AnimationPreview.tscn`, run with `make preview-animations` (add `CLOSEUP=1` for the hands and knife).
- `tools/bake_knife.gd` rebuilds the knife mesh from its source OBJ.
