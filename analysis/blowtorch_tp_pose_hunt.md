# blowtorch third-person pose hunt — working notes

## symptom
FP correct (torch up, left hand on fuel can, all anims fine). TP (freecam): arms
LOWERED holding torch down, but THUMB animates on Firing — bundle finger curves
reach the TP skeleton while arm pose doesnt.

## disproven (verified, dont re-chase)
- item class family (key vs rangefinder): rangefinder class crashed CreateItemAsync
  (weapon branch requires Prefab bundle root to be a WeaponPrefab — line
  `weaponHierarchy = weaponPrefab.Hierarchy.transform` NREs). reverted to known-good
  key-clone + tpl-matched patches. user ACCEPTS container-Prefab tradeoffs = NO.
- body weapon-id: pose diag shows body WeaponTypeFloat=1 (Pistol) with torch drawn,
  GetWeaponAnimationType patch fires (animType=Pistol). body animator is correctly
  configured; PlayerAnimatorSetWeaponId(method_21) just lerps a float, no gating.

## current state of code
- icebreaker-client/Blowtorch/: BlowtorchController (UsableItemController subclass,
  4 polling ops, burner audio, PoseDiag coroutine w/ reflection by TYPE), patches
  tpl-matched (BlowtorchIds.IsTorch), manual equip chain via CreateItemUsablePrefab.
- server: torch clone 9a449693dff5334122ed7388 (clone of 67ab3d4b, parent
  KeyMechanical direct, retail Prefab, UsePrefab=manimal/torch_container.bundle,
  addtoSpecialSlots). NO custom parent node (deleted).
- item works: loot/inspect/icons/quickslot/special slots/draw/fire/holster/audio/
  hatch melt all functional. ONLY TP pose broken.

## facts from decompile (D:\SPT400_assembly + ilspycmd for async/coroutine bodies —
## the flat dump SKIPS compiler-generated bodies; use ilspycmd -t <type> when needed)
- Player._animators[0]=body (BodyAnimatorCommon), [1]=arms (ArmsAnimatorCommon).
- WeaponPrefab.RebindAnimator: RemoveBindedAnimator + ianimator_0.RebindBones() +
  events emitter rebind. nothing touches ArmsAnimatorCommon.
- UsableItemController.vmethod_0 sets _player.HandsAnimator = firearmsAnimator_0
  (ObjectInHandsAnimator property, Player.cs:1361).
- Player.cs:1529: ArmsAnimatorCommon.cullingMode = AlwaysAnimate when controller is
  UsableItemController — vanilla EXPECTS arms animator relevance for usables.
- vanilla FP arms come from additional_hands client_assets bundle dep, driven by the
  SAME bundle animator; TP body arms = body skeleton, normally follow weapon via
  body-arm IK to the weapon prefab markers (our bundle HAS weapon_L/R_hand_marker,
  Bend_Goal_Left/Right, weapon_LCollarbone_marker etc).

## next leads (in order)
1. how do TP body arms attach to a held weapon for the OWN player? suspect PlayerBody
   / TransformLinks / ProceduralWeaponAnimation arm IK chain: find who consumes
   weapon_R_hand_marker / Bend_Goal_* at runtime (grep decompile for those names).
   our manual chain may skip the step that binds body-arm IK to the new prefab
   (vanilla Process.Execute path vs our DropCurrentController+smethod_1+smethod_8+
   SpawnController).
2. compare AmmoLoad (WORKS in TP per user): same manual chain though! diff vs ours:
   their bundle = borrowed IFAK graph (stock params/layers + weapon_root_anim_fix dep
   + additional_hands dep). maybe the IFAK graph's stock layers/params (or
   weapon_root_anim_fix state on OUR Hands layer — user's controller HAS
   weapon_root_anim_fix state) matter for the TP arm chain. also their item class =
   PortableRangeFinderItemClass (usable family) — Player.cs:1529 culling gate checks
   `is UsableItemController` (controller — ours passes) not item.
3. method_6() in controller vmethod_0 (we call it, copied from rangefinder) — what
   does it do? decompile Player.ItemHandsController.method_6.
4. if IK-binding is the gap: find the binder (maybe PlayerBody.SetWeapon /
   ProceduralWeaponAnimation.method_9(weaponPrefab) — base vmethod_0 calls it) and
   check ordering vs our manual chain.

## CONTROL TEST (do first — cheapest decisive step)
draw a VANILLA Vortex Ranger 1500 rangefinder (61605e13ffa6e502ac5e7eef) and view
yourself in third person the same way (freecam). if the vanilla usable ALSO shows
lowered arms, the "bug" is vanilla behavior for usables viewed in TP and we stop
hunting entirely. if vanilla poses correctly, diff its path vs ours (it flows
through SetInHandsUsableItem -> Proceed<PortableRangeFinderController> -> vanilla
ops with WeapIn events; ours is the manual chain + polling ops).
PlayerBody.method_4 (checked) is only the slot-view sling/holster attach — dead end.

## tools
- ilspycmd installed globally; scratchpad/poolmgr + /mvctx + /wp hold decompiles.
- pose diag stays in build 21:48 (BlowtorchController.PoseDiag) — remove when done.

---
# VOLUMETRIC FOG HUNT (2026-07-18, in progress — DO NOT DEFER, user call)

## state
- MBOIT volumetric pass RENDERS on 0.16.9 (TOD_Scattering.MBOIT armed + runtime
  FogRemapDataV2 w/ retail record from analysis/icebreaker_fog_remap.json) BUT:
  hour23 = pitch black (night sky lights the media; FogBrightness lever now lifts
  Night.ColorMultiplier in volumetric branch — untested), hour10 = blown-out white,
  hard angular cutoff seam (froxel slices/depth).
- ROOT CAUSE FOUND: camera has NO MBOIT_Scattering component ("NO MBOIT_Scattering
  on render camera" log) — method_9's tuned params dead-end; rendering is
  TOD_Scattering MBOIT-branch defaults.
- MBOIT_Scattering needs 4 serialized ComputeShaders (ScatteringSlices/MBOIT/
  MBOITRCP/MBOITFinalPass ComputeShader fields). NOT SHIPPED in 0.16.9 (scanned all
  root files + bundles: zero). Retail: class name only in globalgamemanagers.assets
  (MonoScript); compute asset NAMES unknown (field-name guesses found nothing).

## next procedure (post-compaction continuation)
1. parse retail globalgamemanagers.assets: find MonoScript pathID for
   MBOIT_Scattering (m_ClassName == "MBOIT_Scattering").
2. scan retail level*/sharedassets* MonoBehaviours for m_Script refs to that
   MonoScript (the camera prefab instance — likely preloader/menu scene level0-ish).
3. read its typetree (SPT dlls typetree gen) → the 4 ComputeShader PPtrs → resolve
   external refs → note which retail .assets hold the compute shader assets + names.
4. extract compute shaders into a loadable bundle: UnityPy raw-object copy into a
   minimal UnityFS (or check AssetRipper full-export for .compute source first:
   C:\Users\peard\Desktop\IcebreakerLevels\AssetRipper_export_20260704_204314 had
   no *.compute — try ripping the retail file that HOLDS them once located).
   same Unity version both sides (2022.3.43f2) so serialized computes should load.
5. client: AddComponent<MBOIT_Scattering> on render camera, assign 4 computes +
   Sky + copy retail's serialized settings (slice counts etc from step 3 dump),
   set wc.mboit_Scattering_0 via reflection (rebind block already in
   IcebreakerWeather TickBlizzard — extend it).
6. re-test: intensity/cutoff should follow retail curves; iterate BlizzardFog/
   FogBrightness. fallback knob VolumetricFog=false stays.

## files
- IcebreakerWeather.cs: volumetric arm block + TickFog volumetric branch + mboit
  rebind check (_mboitChecked).
- RetailFogRemap.cs: generated retail record (regen from icebreaker_fog_remap.json).
MonoScript pathID 2864 (globalgamemanagers.assets, Assembly-CSharp)
step2 progress: NOT in retail level0-39 (first 40 sorted). next: scan remaining retail level files + retail sharedassets for MonoBehaviour with m_Script pathID 2864; if nowhere, retail may AddComponent it at runtime from code (check retail CameraClass/decompile 1.0 assembly at C:\Battlestate Games ... Managed) — then computes live as direct refs of that code path's prefab/asset.

## VOLUMETRIC HUNT — SYNTHESIS (2026-07-18 ~01:45)
- method_4 alive (1 startup throw only, date-not-ready; harmless).
- MBOIT fully constructed+bound (computes/dither/settings from retail level525;
  TOD_Scattering.mboit_Scattering_0 cache refreshed). All real, all irrelevant:
- THE VISIBLE VEIL IS TOD_Scattering's CLASSIC scattering pass (OnRenderImage ->
  OnRenderImageNormalMode ALWAYS runs; MBOIT is additive on top). classic pass fogs
  toward the SKY color; our rebuilt TOD sky renders flat white (day) / black
  (night) with a hard horizon seam -> white/black slab, BlizzardFog≈irrelevant,
  identical since we first enabled scat. FromLevelSettings=true pulls
  HeightFalloff/ZeroLevel from our RESTORED LevelSettings (retail-correct).
- => the SKY/atmosphere (TOD_Sky day-night atmosphere colors) is the broken input.
  next avenue: fix TOD_Sky atmosphere (retail TOD profile/scattering colors from
  level698 extraction?) so the scattering veil has a real sky to fog toward; MBOIT
  should then read correctly on top. alternatively scat stays disabled (GlobalFog
  path) — the pre-hunt look.
- VolumetricFog config default flipped to FALSE (playable default; knob stays for
  experiments).

## SKY RESTORE (next step, "fix it" user call)
- analysis/icebreaker_tod_sky.json = retail level698 TOD_Sky full dump (Day/Night/
  Sun/Moon/Stars/Light/Fog/Ambient/Reflection/World/Atmosphere backing field,
  ColorSpace/ColorRange/qualities). also TOD_Components/TOD_Time/TOD_Resources.
  COPIED to plugins/ManimalIcebreaker/icebreaker_tod_sky.json (runtime input).
- TODO applier in IcebreakerWeather (or new IcebreakerSky.cs): once
  MonoBehaviourSingleton<TOD_Sky>.Instance alive, load json + reflection-walk:
  for each top-level group (Day, Night, Sun, Moon, Stars, Light, Fog, Ambient,
  Reflection, World, Atmosphere property backing) set matching public fields:
  float/bool/int/enum(int), Color {r,g,b,a}, Vector3, AnimationCurve {m_Curve
  keyframes}, Gradient (key0..7/ctime/atime NumColorKeys/NumAlphaKeys). one-shot
  at raid start + log applied/missed counts. THEN re-test scat veil (VolumetricFog
  knob) — sky becomes the real atmosphere, scattering veil + MBOIT should finally
  read like retail. watch: our existing sky code (WarmSky/night ColorMultiplier
  lifts, TodHour pin) may need to defer to restored values.
- add csproj deploy copy for the json (PostBuild) so rebuilds keep it fresh.

## DECISIVE (2026-07-18 ~02:05): retail does NOT use MBOIT here
- level525's ONLY scattering comps live on BaseOpticCamera(Clone) with _mboit=0
  (dump: analysis/icebreaker_retail_todscat.json). retail icebreaker scenes have
  ZERO fog components. => NO evidence retail icebreaker uses MBOIT volumetric.
  the whole MBOIT arc was built on a misread camera. keep VolumetricFog=false.
- retail haze = CLASSIC TOD scattering + WORKING SKY + LevelSettings heightfog +
  backend weather + dense snow. our white/black slab = same classic pass fogging
  toward OUR broken sky. Sky applier ran (60 set/1 missed) — sidecar had already
  restored params byte-exact, so values are NOT the sky problem either.
- NEXT HUNT: why the rendered TOD sky is flat white(day)/black(night) with a hard
  horizon seam despite correct params: suspect sky DOME rendering — TOD_Sky
  Initialize/quality path, dome meshes/materials (Quad etc from TOD_Resources /
  TOD_Components refs — resolved by name at rebuild; verify EACH bound), the
  atmosphere shader variant, ColorSpace/ColorRange handling, or the sun position
  (TodHour pin at 23 = night; test daytime skies). compare against a VANILLA map's
  sky in-game (same build renders proper atmosphere on customs/lighthouse — diff
  our TOD_Sky/TOD_Components/TOD_Resources live state vs vanilla map's at runtime
  with a probe like TorchPoseProbe: dump dome renderer materials/shaders/meshes).
