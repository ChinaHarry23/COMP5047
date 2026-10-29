# EDGE collisions

**Apply:** open the scene → **EDGE › Physics › Add Colliders To Open Scene** → read the Console summary → **save**.
Idempotent (re-run any time). Undo-able removal: **EDGE › Physics › Remove EDGE Colliders From Open Scene**.
Both scene builders run the same pass automatically before saving on rebuild.

All colliders live on new child objects named `EdgeColliders` (nothing existing is moved, renamed or
deleted), on layer **EdgeObstacle** (added to the first free user layer in Tags & Layers; falls back
to Default if none is free).

| Object | Collider |
|---|---|
| Trees (`Trees/Tree`) | Capsule fitted to the trunk from the lowest LOD's vertices (5–15 % height slice, radius 0.08–0.6 m), up to crown base; **trigger** crown sphere with `EdgeFoliage` (slows ×0.75, jostles) |
| Lamps (`Lamp_n`) | Capsule on post, boxes on arm and head |
| Benches, signposts, fountain (`Furniture/*`) | One fitted box each (bench design untouched) |
| Rocks, sandstone ledge | Fitted box, 85 % footprint |
| Bushes > 1.5 m tall | Trigger box with `EdgeFoliage`; lower bushes, ferns, grass, banksia pods: none |
| Walkers / runners / cyclists | Capsule + kinematic Rigidbody (moves with the agent). Bench sitters: none |
| Ground, footpath | Existing MeshColliders (reported, not changed) |

No MeshColliders are added. Expect roughly one collider per tree/prop (+1 crown) — a few hundred primitives.

**Bat (`BatFlightController`, Collision section):** sphere-cast along each frame's move (no tunnelling),
slide along the surface (3 iterations), `ComputePenetration` depenetration, short downward ray for ground
clearance. A hard hit (not ground grazes) bounces (`bounceSpeed`), turns the heading away (`bumpTurnDeg`),
stuns (`stunSeconds`, `stunInstability`, `stunSpeedMultiplier`), pulses the tag (`bumpVib`, `bumpVibSeconds`),
plays a procedural thud, and fires `Bumped`. Tunables: `collisions`, `batRadius` (0.12 m), `collisionMask`
(empty = EdgeObstacle + Default), `bumpCooldown`, `groundClearance`. `collisions = false` restores the old free flight.
Feeding game: each bump costs `bumpHungerCost` (tolerant 0.03, averse 0.015 — clutter specialist).
