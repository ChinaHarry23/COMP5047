MOONLIT ISLAND

Open Assets/MoonlitIsland/MoonlitIsland.unity and press Play.

Camera controls (click the Game view first):
  Drag left or right mouse button: orbit the island
  Scroll: zoom
  R: reset the camera
  O: toggle slow automatic orbit

Bat riding (IslandBatPilot on the Main Camera):
  F: mount / dismount Bat 01 and take over its flight
  Mouse: aim while riding (the cursor locks; Esc frees it, click to re-lock)
  W / S: fly forward and back
  A / D: strafe left and right (the bat banks into the turn)
  Space / C or Left Ctrl: climb / dive
  Left Shift: boost
  V: toggle first person (from the bat's head) and third person (chase view)
  While riding, the orbit camera pauses; on dismount it resumes from the
  bat's position, and the bat glides back onto its own flight path.

Scene contents:
  A 460-unit highland island: ridged, snow-capped mountain range across the
  north with two outlier peaks, forested rolling slopes and a basalt coast.
  An old-growth forest of ~5,800 pines in three distance tiers: full hero
  trees with scanned bark near the camera, merged coarse-mesh blocks at mid
  range, and crossed-silhouette treelines on the far shore. Trees follow a
  natural density field with clearings, a winding firebreak, a treeline that
  thins toward the peaks, and slope-aware placement.
  Ground surfaces use CC0 photogrammetry scans from ambientCG.com, blended
  by a painted mask on an HDRP LayeredLit material: forest floor and pine
  needles under the woods, mossy rock on steep faces and the shoreline,
  snow above the snowline. Scanned bark textures the tree trunks and
  scanned rock textures the coastal boulders.
  Native HDRP infinite ocean with animated swells, ripples and foam.
  Twenty bats with articulated scalloped wings and individual flight paths.
  Blue-hour fog, moonlight and a star field.

Scan sources (CC0, no attribution required, downloaded from ambientCG.com):
  Ground078 (forest floor), PineNeedles001, ScatteredLeaves009 (leaf litter),
  Bark014, Rock064 (mossy rock), Snow015, Ground037 (moss ground)
  Stored in Assets/MoonlitIsland/ScanTextures; reconfigure importers with
  Unity menu: Moonlit Island > Configure scan texture importers.
  Quixel Megascans (via Epic's Fab) also offers free forest scans, but
  requires an account and the Fab app; ambientCG needs neither.
Generated objects, materials, meshes and volume profiles remain editable.

The original OutdoorsScene.unity and New Terrain.asset were retained. A snapshot
of the previously open scene was also saved as Preserved/OutdoorsBeforeIsland.unity.
The project's active rendering pipeline now points to Settings/MoonlitIslandHDRP.asset,
a copy with native water enabled. The original HDRP asset was not overwritten.

Rebuild after saving any scene edits:
  Unity menu: Moonlit Island > Build or rebuild island
  CLI: unity command eval 'MoonlitIsland.Editor.MoonlitIslandBuilder.Build();'
  (rebuilds include the bat pilot on the camera)
  CLI: unity command eval 'MoonlitIsland.Editor.MoonlitIslandBuilder.EnsurePilotRig();'
  (attaches the pilot to the existing scene without rebuilding)

Rebuilding replaces the generated island scene and assets, so duplicate the scene
first if you want to preserve manual changes. Keep runtime motion changes in the
scripts; edits made to GameObjects while playing revert when Play mode stops.
