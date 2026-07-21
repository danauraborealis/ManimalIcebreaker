#!/usr/bin/env python3
# patches retail's icebreaker ScenesPreset bundle for the SPT backport:
#  - keeps 9 scenes (drops Sound / Culling / cutscene_01)
#  - rewrites every SceneResourceKey: path = our single scene-bundle key (passed verbatim
#    to BundlesManager.LoadBundleAsync), rcid = the scene name (what LoadSceneAsync gets,
#    matching the BSG UI-scene key style: path=*.bundle + rcid=SceneName)
# the MonoBehaviour's script reference stays BSG's own (binds to EFT.ScenesPreset by
# class identity in the SPT client). output: icebreaker.scenespreset.bundle
import UnityPy, os, sys

SRC = r"C:/Battlestate Games/Escape from Tarkov/EscapeFromTarkov_Data/StreamingAssets/Windows/maps/icebreaker.bundle"
OUT = r"C:/Users/peard/Desktop/IcebreakerBundleOut/icebreaker.scenespreset.bundle"
BUNDLE_KEY = "assets/content/locations/icebreaker/icebreaker_scenes.bundle"
KEEP = ["Icebreaker_Scripts", "Icebreaker_Outdoor", "Icebreaker_Indoor_01", "Icebreaker_Indoor_02",
        "Icebreaker_Indoor_03", "Icebreaker_Lights", "Icebreaker_Design_Main", "Icebreaker_Design_Stuff",
        "Icebreaker_AI", "Icebreaker_Sound", "Icebreaker_Culling"]

env = UnityPy.load(SRC)
patched = False
for obj in env.objects:
    if obj.type.name != "MonoBehaviour":
        continue
    tt = obj.read_typetree()
    if "ScenesPreset" not in str(tt.get("m_Name", "")):
        continue
    old = tt["_scenesResourceKeys"]
    name_of = lambda e: os.path.splitext(os.path.basename(e["path"]))[0]
    kept = [e for e in old if name_of(e) in KEEP]
    # order: active scene (Icebreaker_Scripts) first, then preset order
    kept.sort(key=lambda e: KEEP.index(name_of(e)))
    for e in kept:
        scene_name = name_of(e)
        e["path"] = BUNDLE_KEY
        e["rcid"] = scene_name
        e["_onlyOffline"] = 0
    tt["_scenesResourceKeys"] = kept
    # ScenesGuids parallel list: trim to same count (guids unused by the loader path we
    # traced, but keep the structure sane)
    tt["ScenesGuids"] = tt["ScenesGuids"][:len(kept)]
    obj.save_typetree(tt)
    patched = True
    print(f"patched preset: {len(old)} -> {len(kept)} scenes, all keys -> {BUNDLE_KEY}")
    for e in kept:
        print(f"   rcid={e['rcid']}")

if not patched:
    sys.exit("no ScenesPreset MonoBehaviour found!")
os.makedirs(os.path.dirname(OUT), exist_ok=True)
with open(OUT, "wb") as f:
    f.write(env.file.save())
print(f"wrote {OUT} ({os.path.getsize(OUT)} bytes)")
