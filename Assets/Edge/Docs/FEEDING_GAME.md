# EDGE feeding game (two bats, one lamp-lit path)

A short round-based mini game on the existing Sydney Bushland path. Moths cluster at the lamps in
both variants; what changes is whether the bat can use them.

## Add / play
1. Open `EdgeSydneyBushland` (or `EdgeBushland`).
2. **EDGE › Feeding Game › Add Feeding Game To Open Scene** — creates `EdgeFeedingGame`, wires the bat
   (`BatFlightController`), `UdpLink`, every `LampZone`, and creates the two presets in
   `Assets/Edge/Feeding/`. **Save the scene** to keep it (the menu only marks it dirty).
3. **EDGE › Feeding Game › Set Variant: Light-Tolerant / Light-Averse** (works in Play mode too).
4. Press Play → start screen → **Space / Enter** (gamepad Start) or **1 / 2**. The chosen bat introduces itself for ~4 s (held still, timer paused), then the round starts.

Remove it by deleting the `EdgeFeedingGame` object — the exhibit then behaves exactly as before
(all `BatFlightController` modifiers default to neutral and are reset when the component is disabled).

## Controls
| Key | Action |
|---|---|
| WASD / arrows, gamepad left stick, EDGE tag tilt | fly (unchanged `Fly` actions) |
| Space / Enter / gamepad Start | start / replay |
| 1 / 2 | switch to light-tolerant / light-averse and restart |
| R | restart round |
| E | echolocation ping (variant B; also automatic every 2.5 s) |
| H | toggle full debug HUD (exhibit HUD + game details) |
| Z / tag long-press | zero the tag (unchanged) · C camera · F fps (unchanged) |

Eat by flying within `eatRadius` of a moth: crunch sound (procedural), a tiny brief pop, and a short VIB
burst to the tag (LED on D3 flashes). In the light-averse variant the tag LED also glows while you are in light.

## HUD
* **Playing:** one small top-right cluster — `score · time`, a thin green energy bar, and (variant B only)
  a thin amber night vision (light budget) bar that turns red while you are in light. A one-line hint fades out after ~3 s.
* The exhibit `EdgeHud` goes **compact** while the game is active: only a tiny dot top-left
  (green = tag streaming, grey = keyboard). In variant A the exhibit budget-spent overlay is hidden too.
* **Start screen:** species blurb + all key hints. **End screen:** score + one-line species message.
* **H** restores the full exhibit HUD and adds a debug line (lux, lit/dark, hunger %, budget %).

## Moths and echolocation
* Moths are **dark brown/black HDRP/Lit** (no glow): they read as silhouettes against the lamp light,
  which is how insects at a streetlight actually look. Wings flap by per-instance scale/roll flicker
  (`flapHz`); `insectSize` default 7 cm wingspan (larger than most real moths, for playability).
* Lamp moths orbit tightly (`lampOrbitRadius` 0.5–2 m) on tilted, breathing orbits, reverse direction
  and dart at random (`dartRate`) — never into the lamp head.
* **Echolocation cue** (default Auto = on in the light-averse variant): every `echoInterval` s or on **E**,
  moths within `echoRadius` get a brief pale-blue highlight that fades over `echoDuration`. This stands in
  for the bat's echolocation — *N. gouldi* is a gleaning, clutter forager that finds prey by sound
  (and listening) in the dark, not by sight. It is a gameplay cue, not a simulation of sonar.

## Variants by design
* **A — Light-tolerant** (Gould's wattled bat, *Chalinolobus gouldii*; also white-striped free-tailed bat):
  80 % of moths at lamps, 15 % faster flight, no light budget, dose instability ignored. Fly lamp to lamp
  and "buffet" — easy, high scores.
* **B — Light-averse** (Gould's long-eared bat, *Nyctophilus gouldi*, EDGE's subject): same moth
  distribution, slower flight, **low light budget (12 FMM vs exhibit 60)** drained with the *same*
  inverse-square lamp model and FMM conversion as `LampZone`/`DoseModel` ×2.5. In light: 30 % slower,
  steering instability, extra hunger drain, tag LED glows. Budget empty = **forced retreat** (round
  over). You must pick off sparse moths in the dark and only dart into lamp pools briefly — hard, low scores.
* Round ends at timer 0 (90 s), hunger 0 (starved), or (B) budget 0. End screen shows score, best per
  variant this session, and the comparison message.

## Tuning (`EdgeFeedingPreset` assets, `Assets/Edge/Feeding/`)
Round: `roundSeconds`, `hungerDrainPerSec`, `hungerPerInsect`, `startHunger`.
Insects: `insectCount` (≤300), `lampShare`, `lampOrbitRadius`, `dartRate`, `darkSpread`, `darkHeight`,
`eatRadius`, `respawnSeconds`, `insectSpeed` (`lampSwarmRadius` is no longer used). Echo: `echoMode`
(Auto/On/Off), `echoInterval`, `echoRadius`, `echoDuration`. Light: `lightHurts`, `budgetFmm`, `doseMultiplier`, `litLuxThreshold`,
`litSpeedMultiplier`, `litInstability`, `litHungerDrainPerSec`, `ignoreDoseInstability`. Flight: `speedMultiplier`.
Component: `insectMaterial` (optional override), `mothColor`, `insectSize`, `flapHz`, `echoColor/Emissive/Key`,
sound, `eatVibLevel/Seconds`, `debugHudKey`, `hintFadeSeconds`, `showStartScreen`, `exhibitHud`.

## Implementation notes
* Lamps come from `LampZone` components (head = `measureFrom`); fallback: point/spot `Light`s named "*lamp*".
* Insects are plain structs (pool ≤300) drawn with one `Graphics.RenderMeshInstanced` call (+1 for echo highlights); no GameObjects
  or Rigidbodies. Dark moths are placed by a ground raycast, kept ≥8 m from lamp heads.
* `EdgeHud` gained `compact` / `hideBudget` flags (default off = unchanged exhibit HUD).
* `BatFlightController` gained neutral-by-default modifiers (`speedMultiplier`, `extraInstability01`,
  `ignoreDoseInstability`, `extraVib01`) and `ResetPose()`. The exhibit's `DoseModel` budget is not changed;
  variant B keeps its own game budget. Note: `LampZone` still adds to `DoseModel` during the game as before.
* Hooks for the narrator (not wired): `InsectEaten(score)`, `RoundStarted(variant)`, `RoundEnded(variant, score, reason)`.

## The science — honest notes
* Moths do gather at lamps, and fast open-space foragers like *C. gouldii* and *Austronomus australis* were
  **unaffected** by streetlights at Sydney urban-forest edges, using lit and dark edges similarly; slow
  clutter-foraging *Nyctophilus* spp. were negatively affected (Haddock et al. 2019a). Insect sampling there
  found larger moths attracted to lights *but in low numbers* — the game exaggerates the "buffet".
* Radio-tracked *N. gouldi* in Cumberland State Forest stayed almost entirely in bushland, and lit open
  spaces had significantly lower *Nyctophilus* activity (Threlfall et al. 2013).
* Benefit to light-tolerant bats depends on the lamp: Sydney's change from mercury-vapour to low-UV LED
  streetlights *decreased* *C. gouldii* activity, while clutter species stayed more active in unlit
  bushland (Haddock et al. 2019b). The game's amber lamps are a simplification.
* The game's "light budget" (FMM) is EDGE's own design metric, not a published physiological threshold;
  the 12 FMM budget and penalties are gameplay choices.

## References (checked by web search, 15 Oct 2026)
1. Haddock, J.K., Threlfall, C.G., Law, B., Hochuli, D.F. (2019a). Light pollution at the urban forest edge
   negatively impacts insectivorous bats. *Biological Conservation*. https://www.sciencedirect.com/science/article/abs/pii/S0006320718313685
2. Threlfall, C.G., Law, B., Banks, P.B. (2013). The urban matrix and artificial light restricts the nightly
   ranging behaviour of Gould's long-eared bat (*Nyctophilus gouldi*). *Austral Ecology*. https://doi.org/10.1111/aec.12034
3. Haddock, J.K., Threlfall, C.G., Law, B., Hochuli, D.F. (2019b). Responses of insectivorous bats and nocturnal
   insects to local changes in street light technology. *Austral Ecology*. https://doi.org/10.1111/aec.12772

(Author lists from memory of these papers — confirm before citing in the report.)
