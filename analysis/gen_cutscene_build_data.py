# consolidate the cutscene extraction into ONE build file for the SDK editor
# script (Author 17): track hierarchy in retail order, per-clip timing fields,
# playable-asset payloads, signal emitters/assets, the director's scene-binding
# table (retail track pathID -> scene object fileID — the ripped scene KEPT
# retail fileIDs, so GlobalObjectId lookup in-editor resolves them), and the
# anim manifest (retail clip pathID -> SDK asset path).

import json
from pathlib import Path

HERE = Path(__file__).parent
GRAPH = json.loads((HERE / "icebreaker_cutscene_graph.json").read_text())
PASS1 = json.loads((HERE / "icebreaker_cutscene_timeline.json").read_text())
ANIMS = json.loads((HERE / "icebreaker_cutscene_anim_manifest.json").read_text())
OUT = Path(r"C:\Users\peard\Desktop\WTT-SDK-2022 Public\Assets\Icebreaker_Import\CutsceneAnims\cutscene_build_data.json")

objs = GRAPH["objects"]


def pptr(v):
    return v["m_PathID"] if isinstance(v, dict) and "m_PathID" in v else 0


def clip_row(c):
    return {
        "start": c["m_Start"], "clipIn": c["m_ClipIn"], "duration": c["m_Duration"],
        "timeScale": c["m_TimeScale"], "assetPid": pptr(c["m_Asset"]),
        "easeIn": c["m_EaseInDuration"], "easeOut": c["m_EaseOutDuration"],
        "blendIn": c["m_BlendInDuration"], "blendOut": c["m_BlendOutDuration"],
        "mixInCurve": c["m_MixInCurve"], "mixOutCurve": c["m_MixOutCurve"],
        "blendInMode": c["m_BlendInCurveMode"], "blendOutMode": c["m_BlendOutCurveMode"],
        "preExtrap": c["m_PreExtrapolationMode"], "postExtrap": c["m_PostExtrapolationMode"],
        "preExtrapTime": c["m_PreExtrapolationTime"], "postExtrapTime": c["m_PostExtrapolationTime"],
        "recordable": c.get("m_Recordable", 0),
        "displayName": c.get("m_DisplayName", ""),
    }


tracks = {}
playables = {}
signals = {"assets": {}, "emitters": {}}
root = None

for pid, v in objs.items():
    cls = v.get("class", "")
    data = v.get("data")
    if data is None:
        continue
    short = cls.split(".")[-1]
    if short == "TimelineAsset":
        root = {
            "name": data["m_Name"],
            "rootTracks": [pptr(t) for t in data["m_Tracks"]],
            "fixedDuration": data["m_FixedDuration"],
            "durationMode": data["m_DurationMode"],
            "framerate": data["m_EditorSettings"]["m_Framerate"],
        }
    elif short in ("AnimationTrack", "ActivationTrack", "GroupTrack", "AudioTrack", "SignalTrack"):
        row = {
            "type": short,
            "name": data["m_Name"],
            "muted": data.get("m_Muted", 0),
            "children": [pptr(t) for t in data.get("m_Children", [])],
            "clips": [clip_row(c) for c in data.get("m_Clips", [])],
            "markerPids": [pptr(m) for m in (data.get("m_Markers") or {}).get("m_Objects", [])],
        }
        if short == "AnimationTrack":
            row["infiniteClip"] = pptr(data.get("m_InfiniteClip", {"m_PathID": 0}))
            row["applyOffsets"] = data.get("m_ApplyOffsets", 0)
            row["position"] = data.get("m_Position")
            row["eulerAngles"] = data.get("m_EulerAngles")
            row["avatarMask"] = pptr(data.get("m_AvatarMask", {"m_PathID": 0}))
            row["applyAvatarMask"] = data.get("m_ApplyAvatarMask", 1)
            row["trackOffset"] = data.get("m_TrackOffset", 0)
        if short == "ActivationTrack":
            row["postPlaybackState"] = data.get("m_PostPlaybackState", 0)
        tracks[pid] = row
    elif short in ("AnimationPlayableAsset", "ActivationPlayableAsset", "AudioPlayableAsset"):
        row = {"type": short, "name": data.get("m_Name", "")}
        if short == "AnimationPlayableAsset":
            row.update({
                "clipPid": pptr(data["m_Clip"]),
                "position": data["m_Position"], "eulerAngles": data["m_EulerAngles"],
                "rotation": data["m_Rotation"],
                "useTrackMatchFields": data["m_UseTrackMatchFields"],
                "matchTargetFields": data["m_MatchTargetFields"],
                "removeStartOffset": data["m_RemoveStartOffset"],
                "applyFootIK": data["m_ApplyFootIK"], "loop": data["m_Loop"],
            })
        if short == "AudioPlayableAsset":
            row.update({"clipPid": pptr(data["m_Clip"]), "loop": data.get("m_Loop", 0),
                        "clipProps": data.get("m_ClipProperties")})
        playables[pid] = row
    elif short == "SignalAsset":
        signals["assets"][pid] = {"name": data["m_Name"]}
    elif short == "SignalEmitter":
        signals["emitters"][pid] = {
            "time": data["m_Time"], "assetPid": pptr(data["m_Asset"]),
            "retroactive": data.get("m_Retroactive", 0),
            "emitOnce": data.get("m_EmitOnce", 0),
        }

bindings = []
for b in PASS1["directors"][0]["m_SceneBindings"]:
    bindings.append({"trackPid": b["key"]["m_PathID"], "sceneFileId": b["value"]["m_PathID"]})

out = {
    "timeline": root,
    "tracks": tracks,
    "playables": playables,
    "signals": signals,
    "bindings": bindings,
    "anims": ANIMS,
    "audioWav": "Assets/Icebreaker_Import/CutsceneAnims/icebreaker_cutscene_master.wav",
}
OUT.write_text(json.dumps(out, indent=1))
print(f"wrote {OUT} ({OUT.stat().st_size // 1024} KB)")
print(f"root tracks: {len(root['rootTracks'])}, tracks: {len(tracks)}, "
      f"playables: {len(playables)}, bindings: {len(bindings)}, "
      f"signal assets: {len(signals['assets'])}, emitters: {len(signals['emitters'])}")
missing = [p for t in tracks.values() for c in t["clips"]
           if (p := c["assetPid"]) and str(p) not in {str(k) for k in playables}]
print("clips w/ missing playable asset:", missing or "none")
