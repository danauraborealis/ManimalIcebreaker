# How EFT actually loads a map — and where Icebreaker's path differs
*(assembly: SPT 4.0 / client 0.16.9, traced 2026-07-07; file:line refs are into D:\SPT400_assembly\Assembly-CSharp)*

## 1. The vanilla pipeline, condensed

```
TarkovApplication.method_43 (the raid loader)
├─ unload hideout, create GameWorld (InitLevel)
├─ QualitySettings.asyncUploadTimeSlice/BufferSize <- backend config   (tuned for load, restored after)
├─ LoadScenesFromPreset(preset)                     TarkovApplication.cs:1702
│   ├─ preset bundle key (maps/<map>.bundle) -> ScenesPreset ScriptableObject   GClass2287.cs:49-90
│   ├─ per scene: EasyAssetHelperClass loads bundle from rootPath+key           EasyAssetHelperClass.cs:73-207
│   │     rootPath = StreamingAssets/Windows/, deps from Windows.json manifest
│   │     (CompatibilityAssetBundleManifest; SPT's bundleCheck hook can fetch
│   │      server bundles instead)                                              EasyAssets.cs:24-58
│   ├─ scenes: first Single, rest Additive, SEQUENTIAL by default
│   │     (loadInParallelMode=false), allowSceneActivation gating at 0.9        AssetsManagerClass.cs:656-714
│   ├─ ChildPresets recurse (the _AI scene preset)
│   └─ SetActiveScene(preset.ActiveSceneName)
├─ LevelSettings.OnPostLoadingScene()               LevelSettings.cs:98-107
│     applies authored ambient/fog/skybox render settings, caches TreeWind,
│     CREATES AirdropManager. LevelSettings also carries CameraPrefab +
│     PostProcessProfile + PrismPreset + RainBounds + NorthDirection + SSRFactor
├─ PerfectCullingCrossSceneSampler.InitializeAutoCulling                       PerfectCullingCrossSceneSampler.cs:136-158
│     needs scene PerfectCulling volumes + CullingGridPreProcess object +
│     StreamingAssets/Culling_Data/<guid>_packed_cull.bytes
│     -> builds GClass1238 grid (observed-player/bot body culling)
├─ EFTPhysicsClass.GClass747.Create()  (the PhysicsWorld_* trigger scenes)
├─ StaticDeferredDecalRenderer.UpdateInstancesBuffers()
├─ AmbientLight.RuntimeOptimizePrepare()   (AnalyticSource batching)
└─ SpatialAudioSystem.Initialize()  <- SpatialAudioLocationInfo.relativeBakeDataPath -> .audiobakedata

then the phase machine (BaseLocalGame):
LocationLoaded -> GamePrepared -> GameCreated -> PlayerSpawnEvent
-> GamePooled   BaseLocalGame.cs:635-698  method_12:
      location.Loot -> every item's Template.AllResources
      -> PoolManagerClass.LoadBundlesAndCreatePools(Raid, Local, keys, General)
-> bots: BotsPresets.FillCreationDataWithProfiles pools EVERY wave-bot profile's
      GetAllPrefabPaths(false) BEFORE spawning                                  BotsPresets.cs:288-304
-> GameDateTime: BaseLocalGame.method_6 assigns backend raid time into GClass4  BaseLocalGame.cs:151
-> GameSpawned: GC collect + GC DISABLED + process priority High
-> GameStarting/GameStarted
late content (corpses, airdrops): LoadBundlesAndCreatePools at JobPriority.LOW on demand
```

## 2. What Icebreaker already gets for free (no action)

- **Bundle + scene loading**: our preset bundle (`maps/icebreaker.bundle`) + scenes
  bundle load through the exact same LoadScenesFromPreset machinery, including
  additive multi-scene + active-scene selection + the _AI child preset pattern.
  Resolution is lenient — a key missing from Windows.json still loads from
  rootPath+key; only DEPENDENCY resolution needs manifest entries (we have none:
  our scenes bundle is self-contained by design).
- **LocationScene registration**: every scene's LocationScene.Awake self-registers,
  so `LocationScene.GetAllObjects<T>()` (bot zones, exfils, containers, WIOs) works
  across our 10 scenes automatically.
- **GamePooled loot pooling**: driven by `location.Loot` from the server — our
  base.json loot goes through the same pooling as vanilla.
- **asyncUpload tuning, physics trigger scenes, GC-disable at spawn, process
  priority**: all location-agnostic, all run for us.
- **Phase machine**: all phases fire; nothing is skipped for a custom location.

## 3. What we deliberately replaced (fine as-is)

| vanilla | ours | verdict |
|---|---|---|
| wave/boss spawning via location waves + BotsPresets | client-side ForceSpawn + retail trigger layer (server refused custom roles) | keep — and we now mirror BotsPresets' prewarm call |
| PerfectCulling baked volumes wired in-scene | runtime rehydration from .pcbake sidecars + our own PerfectCullingCamera | keep — working, measured fps win |
| SpatialAudioLocationInfo -> native audio bake load | our sidecar loader + patched init | keep; optional cleanup: author the SpatialAudioLocationInfo component in the Unity project so the NATIVE path loads it (deletes a patch) |
| weather/TOD authored in scene + camera prefab effects | 24-component runtime rebuild + Cam2 add-ons | keep (proven), but see LevelSettings below |
| bot gear pooled by BotsPresets pre-spawn | Prewarm() in ForceSpawn + PreMakeTriggerSquads | keep — same API, same priority discipline |

## 4. Gaps worth closing (ranked)

### 4.1 SHOULD: author a real `LevelSettings` component (in the Unity project, next bundle build)
The single highest-leverage missing piece. It is just a MonoBehaviour on a scene GO;
the rip almost certainly stripped it (`---icebreaker_levelsettings---` placeholder seen
in the scene hierarchy). With it authored:
- `OnPostLoadingScene` stops NRE-ing (the chronic method_43 exception every raid)
- authored ambient/fog/skybox render settings apply natively (replaces part of our
  RenderEnv self-drive)
- **AirdropManager gets created** (we currently have NO airdrops at all — did we know?)
- **RainBounds** exists -> DepthPhotograper.Render() works natively (deletes our
  reflection-set bounds hack)
- optional: `CameraPrefab` field — if we ever build a proper camera prefab, the whole
  Cam2 fallback chain (FrostbiteEffect/TOD_Camera/TOD_Scattering/NightVision fixes)
  becomes unnecessary
Runtime-alternative: create the component at scene load and fill fields from our
weather sidecar — works, but the editor route is one component with ~15 inspector
fields and zero code.

### 4.2 SHOULD: move the AI-bake JSON parse off the main thread
Our 12.6s frozen frame at load is OUR sidecar parse + fill running synchronously in
the RestoreData prefix. The parse (Newtonsoft, 3.8MB) is thread-safe — `Task.Run` the
JObject parse + all pure-data staging during the loading screen, keep only the
UnityEngine object surgery on the main thread. Estimated: 12.6s -> ~2-3s.
(BSG never has this problem because their bake is deserialized by Unity itself.)

### 4.3 COULD: register our bundles in the client manifest at runtime
EasyAssets builds its helper table from Windows.json at startup. A tiny patch adding
our two keys (with empty dependency lists) would make us a first-class citizen:
dependency resolution, SPT bundleCheck compatibility, and the server-bundle route
becoming viable again for distribution (no StreamingAssets pollution). Not urgent —
the lenient path works — but it is the "correct" fix for the distribution story and
likely what tripped the tester's first two attempts.

### 4.4 COULD: CullingGridPreProcess for the observed-culling grid
Without it, GClass1238 is never built and bot-body culling falls back to
distance-only checks (ObservedCullingManager.IsUsingGrid=false). Cost today: bots
render through walls at range (GPU cost only, not visibility cheating). Authoring the
preprocess data is editor work of unknown depth — park unless bot-count fps becomes
a problem again.

### 4.5 WON'T: shader warmup, parallel scene loading
Vanilla does neither (loadInParallelMode is FALSE in the actual call; no map-wide
shader warmup exists — only GPU Instancer warms its own). We match vanilla behavior;
the residual first-visit hitches are parity.

## 5. Curiosities learned
- `location.airdropParameters` exists in base.json land — once LevelSettings exists,
  airdrops become configurable for Icebreaker (naval airdrop, why not).
- Corpse customization loads at JobPriority.Low on demand — precedent for our
  premake priority choice.
- `GameSpawned` disables the GC entirely for the raid — allocation-heavy mod code
  never pays GC during raid, which matches our probe's gc0=0 readings.
- The first scene of a preset loads Single + the rest Additive with activation
  gating at 0.9 progress — the "long 90%" on the load bar is scene activation, which
  is single-threaded Unity Awake/OnEnable cost and explains our second big load
  spike (10.1s) independent of the AI bake one (12.6s).
