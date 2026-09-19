# Combat

How fights work: the three attacks, the roll, stamina, health, healing and respawning. Every hit
is decided by the server, so what you see is what happened.

## Health and damage

Everyone has **100 health**. Your health is the red bar at the top left of your screen.

| Hit | Damage | Hits to kill |
|---|---|---|
| Light attack | 35 | 3 |
| Heavy attack | 52.5 | 2 |
| Zain's Drop Kick | 70 | 2 (or 1 after any other hit) |
| Execute (heavy from behind) | everything | 1 |
| Anything while holding the [Golden Knife](match-rules.md#the-golden-knife) | everything | 1 |

![A landed stab: blood sprays from where the blade went in](images/blood.jpg)

## Attacks

### Light attack

A quick stab: a 0.12 s wind-up, 2 m of reach, 35 damage and a short recovery. Your bread and
butter.

### Heavy attack

A lunge: a 0.4 s wind-up that others can see coming, then you lunge up to 3 m and hit for 52.5
damage. It **staggers** the victim for 0.4 s. If it misses, you're stuck recovering for 0.6 s,
which is your opponent's chance to punish you.

### Execute

A heavy attack that lands within 60° of someone's **back** kills them instantly, whatever their
health.

## The roll (dodge)

Press roll to dive 3.5 m in 0.55 s in the direction you're moving (or facing, if you're
standing still).

- **Attacks pass straight through you for the whole roll.** A well-timed roll beats any
  attack, including the Drop Kick.
- The direction is locked once you start: you can't steer or stop mid-roll.
- It costs **30 stamina**. There's no cooldown, so you can roll again as soon as a roll ends
  if you have the stamina.
- You can't roll out of your own attack's recovery.

## Sprint, jump and stamina

- **Sprint** (hold): 1.6× your normal speed (6 → 9.6 m/s). It drains stamina at 22 per
  second, so about 4.5 s from full.
- **Jump**: standing still, it's a small hop straight up.
- **Vault**: jump while running at waist-high cover (a barrier, a crate, a rock) and you vault
  over it. It works on cover from **0.5 m to 1.5 m** tall that's right in front of you. Once
  you leave the ground you can't steer until you land, and you can still be hit in the air.
  Anything taller is a wall.
- **Stamina** is the green bar under your health (100 max). It refills at 30 per second after
  0.8 s of not using it. If you run it dry, the bar turns amber and you can't sprint again
  until it's back to 15.

## Healing

If nobody hits you for **3 seconds**, you heal **+1 health every second** until you're full.
Any hit restarts the 3-second wait. Everyone can see a healing player: green **+** signs float
up from their body.

![Green + signs: that player is healing](images/healing.jpg)

## Dying and respawning

- At 0 health you fall, and your camera turns towards whoever killed you for 1.5 s.
- You respawn after **1.5 s** (1 s during [Last Call](match-rules.md#last-call)) at a spawn
  point along the walls.
- You're **spawn protected** for 1.5 s: you shimmer and can't be hurt. Attacking, rolling or
  using your ability ends it early.

> **Tip:** the killfeed at the top right always says who killed whom and how.

## See also

- [Abilities](abilities.md)
- [Tips](tips.md)
- [Glossary](glossary.md)
