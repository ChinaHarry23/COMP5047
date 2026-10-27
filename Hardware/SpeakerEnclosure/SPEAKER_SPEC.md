# Speaker reference: Core Electronics CE08483 "Enclosed Speaker - 8 Ohm, 5 Watt" (Makerverse)

Everything the enclosure model depends on, with where each number came from and how
much to trust it. The same speaker is sold by Waveshare as "8Ω 5W Speaker" (their
version ships a JST-PH 2.0 plug instead of tinned leads; the moulding is identical).

Sources (opened 27 Oct 2026):

- Core Electronics product page, core-electronics.com.au/makerverse-enclosed-speaker-8-ohm-5-watt.html
  (page is behind a bot check; text taken from the search-engine copy of the listing).
- Core Electronics forum, "Enclosed Speaker - 8 Ohm, 5 Watt (CE08483)", forum.core-electronics.com.au/t/20375.
- Waveshare product page waveshare.com/8ohm-5w-speaker.htm and its photos
  `8ohm-5w-speaker-size.jpg`, `-1.jpg`, `-2.jpg` (the "92mm / 36mm" dimensioned photo and
  the front/back views).
- Rare Components listing "8Ω 5W Speaker - Set of 2 - 100mm x 45mm x 21 mm".

## Numbers

| Item | Value | Confidence | Basis |
|---|---|---|---|
| Overall envelope L × W × H | **100 × 45 × 21 mm** | High | Core, Waveshare and Rare Components all quote it |
| Mounting-hole pattern | **91.2 × 36.2 mm**, four holes, one at each corner | High | Core quotes the pattern; Waveshare's photo is dimensioned 92 × 36; photos show an ear at all four corners |
| Hole centres from the overall outline | 4.4 mm from each end, 4.4 mm from each side | High (derived) | (100 − 91.2)/2 and (45 − 36.2)/2, pattern assumed centred on the envelope |
| Ear hole | Core says "6 mm diameter holes". Photos show a tab about 6–7 mm wide with a hole that looks nearer 3–3.5 mm. | **Low** | Treat 6 mm as the tab's outside width. Design for an M3 screw (clearance 3.4) and do not rely on the hole being larger. |
| Ear thickness | ≈ 2 mm (1.5–2.5) | Low | Photos only |
| Ear plane (depth of the ear's rear face behind the front face) | ≈ 7.5 mm (6–9) | **Low** | The body is two shells; the ears sit at the parting line, and the front shell is about a third of the height in the side photos |
| Driver and passive radiator diameter | **35 mm** each | High | Core |
| Driver / radiator centres | on the long centreline, **27.5 mm from each end** (so 45 mm apart) | Medium | Core says "around 27.5 mm from top and bottom of case" |
| Front recess around each cone | ≈ 40 mm | Low | Photos: a shallow ring wider than the cone |
| Leads | 2 × ≈ 250 mm, pre-tinned, red/black, ~24–26 AWG | High (length) | Core. Waveshare's unit has a JST-PH 2.0 plug instead |
| Lead exit | end face, beside one corner ear, at about the parting plane | Medium | Photos (Waveshare -1 and -2) |
| Weight | not published; estimate 60–90 g | — | similar 100 × 45 × 21 units |

## Shape, as read from the photos

```
            front face (drivers)                     side view, front face up
   ┌─┬─────────────────────────────────┬─┐          ┌───────────────────────────┐  z = 0 front face
   │o│                                 │o│          │  front shell, full 45 wide │
   ├─┘   (  )   driver   ( ) radiator  └─┤  45    ──┤o                         o├── z ≈ 7.5  parting line, ears here
   ├─┐                                 ┌─┤          │  rear shell, ~1 mm inset,  │
   │o│                                 │o│          │  full length, notched at   │
   └─┴─────────────────────────────────┴─┘          │  the corners for the ears  │
     <-------------- 100 -------------->            └───────────────────────────┘  z = 21 back face
     o = ear with hole, 91.2 x 36.2 pattern
```

- **Front shell:** the full 45 mm width, shorter than 100 mm; its four corners carry the
  ears, which lie in the parting plane and reach out to about the 100 mm ends.
- **Rear shell:** slightly narrower than the front shell (≈ 1 mm per side), runs the full
  100 mm, with corner notches so the ears can sit beside it.
- **Front face:** flat, two shallow round recesses, driver nearer the lead end and the
  passive radiator at the other end (either way round is possible; design symmetric).
- The speaker is itself a sealed enclosure. An outer housing is for protection, looks,
  mounting and cable strain relief; it does not need to be airtight, and it must not
  cover the passive radiator.

## Design consequences

1. **A plain rectangular pocket of 100 × 45 × 21 plus clearance contains everything**
   (ears, notches, both shells), so the housing does not depend on the low-confidence
   ear measurements.
2. **Do not locate the speaker by the ears alone.** Clamp the body between a front frame
   and a back tray (front face against the frame, back face against the tray floor) and
   let the ears sit in clearance pockets. Optional bosses under the ears for people who
   want M3 screws through them must be parametric in height.
3. **Grille openings ≥ Ø38 mm at 27.5 mm from each end** (or a continuous grille over the
   whole 85 × 40 mm front area) so the ±2 mm uncertainty in the cone positions cannot
   mask a cone.
4. **Lead slot in one end wall, about the parting plane, offset toward one side,**
   wide enough for two 1.5 mm wires with room to pass the tinned ends (≥ 4 × 8 mm), plus
   a strain-relief anchor outside the pocket.
