using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using EFT;
using EFT.Interactive;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

namespace Manimal.Icebreaker.Keypad
{
    // icebreaker multi-terminal passcode system, driven by the sidecar json
    // the SDK's Author 9 exports (icebreaker_passcodes.json next to this dll).
    //
    // per terminal, per raid:
    //  1. code = the authored override (e.g. the hardcoded 312220 terragroup
    //     panels) or a fresh random 6-digit roll when the override is empty
    //  2. the map's own PASSCODE_TERMINAL panel becomes the interactive keypad:
    //     Interactive layer, trigger collider, Keypad component bound to its
    //     door by Id — the action patch + KeypadSession handle the rest. the
    //     keypad prefab's audio children (keypress/denied/granted) are grafted
    //     onto the panel so the cues play positionally; the prefab's model is
    //     discarded (the map has its own terminal meshes).
    //  3. TWO note spots are picked at random from the authored pool and a
    //     post-it spawns at each carrying one 3-digit half.
    //
    // icebreaker rides the Suburbs location id (SPT's Locations record is
    // closed to new ids) — that's the map gate.
    [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.OnGameStarted))]
    internal static class Patch_IcebreakerPasscodes
    {
        private const string IcebreakerLocationId = "Suburbs";
        private const string SidecarName = "icebreaker_passcodes.json";
        private const string InteractiveLayerName = "Interactive";

        [HarmonyPostfix]
        private static void Postfix(GameWorld __instance)
        {
            try
            {
                if (Plugin.PasscodeTerminals != null && !Plugin.PasscodeTerminals.Value) return;
                var loc = __instance?.LocationId;
                if (!string.Equals(loc, IcebreakerLocationId, StringComparison.OrdinalIgnoreCase))
                    return;
                var path = Path.Combine(
                    Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".", SidecarName);
                if (!File.Exists(path))
                {
                    Plugin.Log?.LogInfo("[Passcodes] no sidecar — terminals inactive (run Author 9 in the SDK).");
                    return;
                }
                _ = BuildAsync(__instance, path);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[Passcodes] patch threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static async Task BuildAsync(GameWorld gw, string sidecarPath)
        {
            JObject root;
            try { root = JObject.Parse(File.ReadAllText(sidecarPath)); }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[Passcodes] sidecar unreadable: {ex.Message}");
                return;
            }

            // one transform sweep for terminal panel matching (name + proximity)
            var panels = new List<Transform>();
            foreach (var tr in UnityEngine.Object.FindObjectsOfType<Transform>())
                if (tr.name.Contains("PasscodeTerminal")) panels.Add(tr);

            var doors = UnityEngine.Object.FindObjectsOfType<Door>(true);
            var (keypadPrefab, uiPrefab) = await KeypadBundleLoader.EnsureLoaded();
            var notePrefab = await PostItBundleLoader.EnsureLoaded();
            if (uiPrefab == null)
            {
                Plugin.Log?.LogError("[Passcodes] keypad UI bundle missing — terminals not built.");
                return;
            }
            var rng = new System.Random();

            // ---- parse all terminals first — shared-code groups need the full set ----
            var terms = new List<TermRec>();
            int idx = 0;
            foreach (var jt in root["terminals"] as JArray ?? new JArray())
            {
                var rec = new TermRec
                {
                    Id = jt.Value<string>("id") ?? "?",
                    Pos = ReadV3(jt["pos"]),
                    OverrideCode = jt.Value<string>("overrideCode"),
                    DoorId = jt.Value<string>("doorId"),
                    // codeGroup missing (pre-feature sidecar) = own group
                    Group = jt.Value<int?>("codeGroup") ?? idx,
                };
                foreach (var js in jt["spots"] as JArray ?? new JArray())
                    rec.Spots.Add((ReadV3(js["pos"]), ReadV3(js["euler"])));
                terms.Add(rec);
                idx++;
            }

            // ---- one code per group: any authored override wins, else a fresh roll ----
            var groupCode = new Dictionary<int, string>();
            foreach (var t in terms)
            {
                if (!groupCode.TryGetValue(t.Group, out var code) || string.IsNullOrEmpty(code))
                    groupCode[t.Group] = t.OverrideCode;
                else if (!string.IsNullOrEmpty(t.OverrideCode) && t.OverrideCode != code)
                    Plugin.Log?.LogWarning($"[Passcodes] {t.Id}: conflicting override '{t.OverrideCode}' in shared group — keeping '{code}'.");
            }
            foreach (var g in new List<int>(groupCode.Keys))
            {
                if (!string.IsNullOrEmpty(groupCode[g])) continue;
                var sb = new System.Text.StringBuilder(PasscodeFormat.Length);
                for (int i = 0; i < PasscodeFormat.Length; i++) sb.Append(rng.Next(0, 10));
                groupCode[g] = sb.ToString();
            }

            // ---- wire each terminal's panel ----
            var builtByGroup = new Dictionary<int, List<Keypad>>();
            int built = 0, noPanel = 0, noDoor = 0;
            foreach (var t in terms)
            {
                Transform panel = null;
                float best = 0.75f * 0.75f;
                foreach (var cand in panels)
                {
                    float d = (cand.position - t.Pos).sqrMagnitude;
                    if (d < best) { best = d; panel = cand; }
                }
                if (panel == null)
                {
                    Plugin.Log?.LogWarning($"[Passcodes] {t.Id}: no PasscodeTerminal panel within 0.75m of {t.Pos} — skipped.");
                    noPanel++;
                    continue;
                }

                Door door = null;
                if (!string.IsNullOrEmpty(t.DoorId))
                    foreach (var d in doors)
                        if (d != null && d.Id == t.DoorId) { door = d; break; }
                if (door == null)
                {
                    Plugin.Log?.LogWarning($"[Passcodes] {t.Id}: door '{t.DoorId}' not found — terminal will accept the code but unlock nothing.");
                    noDoor++;
                }

                var code = groupCode[t.Group];
                var go = panel.gameObject;
                int layer = LayerMask.NameToLayer(InteractiveLayerName);
                if (layer >= 0) SetLayerRecursive(go, layer);
                if (go.GetComponent<Collider>() == null)
                {
                    var box = go.AddComponent<BoxCollider>();
                    box.isTrigger = true;
                    box.size = KeypadConstants.InteractionBoxSize; // panel-sized fallback; a mesh COLLIDER child usually exists
                }
                GraftAudioChildren(keypadPrefab, go.transform);
                var keypad = go.GetComponent<Keypad>() ?? go.AddComponent<Keypad>();
                keypad.Configure(door, code);
                if (!builtByGroup.TryGetValue(t.Group, out var groupList))
                    builtByGroup[t.Group] = groupList = new List<Keypad>();
                groupList.Add(keypad);

                built++;
                Plugin.Log?.LogInfo($"[Passcodes] {t.Id} live: code='{code}'{(string.IsNullOrEmpty(t.OverrideCode) ? "" : " (authored)")} door='{t.DoorId}' group={t.Group} spots={t.Spots.Count}");
            }

            // ---- cross-link group peers so success can disable the door's twin panel ----
            foreach (var kv in builtByGroup)
            {
                if (kv.Value.Count < 2) continue;
                foreach (var k in kv.Value)
                    foreach (var other in kv.Value)
                        if (!ReferenceEquals(k, other)) k.LinkedKeypads.Add(other);
            }

            // ---- one pair of notes per code group, drawn from the merged spot pool ----
            if (notePrefab != null)
            {
                foreach (var g in groupCode.Keys)
                {
                    var members = terms.FindAll(t => t.Group == g);
                    var spots = new List<(Vector3 p, Vector3 e)>();
                    foreach (var m in members) spots.AddRange(m.Spots);
                    var label = members.Count > 1
                        ? string.Join("+", members.ConvertAll(m => m.Id))
                        : members.Count == 1 ? members[0].Id : $"group{g}";

                    if (spots.Count < 2)
                    {
                        Plugin.Log?.LogWarning($"[Passcodes] {label}: only {spots.Count} note spot(s) authored — notes skipped, code is '{groupCode[g]}' (log-only).");
                        continue;
                    }
                    // fisher-yates, take the first two
                    for (int i = spots.Count - 1; i > 0; i--)
                    {
                        int j = rng.Next(i + 1);
                        (spots[i], spots[j]) = (spots[j], spots[i]);
                    }
                    SpawnNote(gw, notePrefab, spots[0], groupCode[g].Substring(0, PasscodeFormat.FirstHalfLength), label);
                    SpawnNote(gw, notePrefab, spots[1], groupCode[g].Substring(PasscodeFormat.FirstHalfLength), label);
                }
            }

            Plugin.Log?.LogInfo($"[Passcodes] {built} terminal(s) live in {groupCode.Count} code group(s) ({noPanel} panel-missing, {noDoor} door-missing).");
        }

        private sealed class TermRec
        {
            public string Id;
            public Vector3 Pos;
            public string OverrideCode;
            public string DoorId;
            public int Group;
            public readonly List<(Vector3 p, Vector3 e)> Spots = new List<(Vector3, Vector3)>();
        }

        // the map's terminal panels have no audio sources — pull the keypress/
        // denied/granted children off the keypad world prefab and reparent them
        // as direct children of the panel so Keypad.Configure resolves them by
        // name. the rest of the prefab instance (mesh etc.) is discarded.
        private static void GraftAudioChildren(GameObject keypadPrefab, Transform panel)
        {
            if (keypadPrefab == null) return;
            if (panel.Find(KeypadConstants.AudioKeypressName) != null) return; // already grafted
            try
            {
                var inst = UnityEngine.Object.Instantiate(keypadPrefab, panel.position, panel.rotation);
                var wanted = new[]
                {
                    KeypadConstants.AudioKeypressName,
                    KeypadConstants.AudioDeniedName,
                    KeypadConstants.AudioGrantedName,
                };
                foreach (var name in wanted)
                {
                    var child = inst.transform.Find(name);
                    if (child == null || child.GetComponent<AudioSource>() == null) continue;
                    child.SetParent(panel, false);
                    child.localPosition = Vector3.zero;
                }
                UnityEngine.Object.Destroy(inst);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[Passcodes] audio graft failed: {ex.Message} — keypad will be silent.");
            }
        }

        private static void SpawnNote(GameWorld gw, GameObject prefab, (Vector3 p, Vector3 e) spot, string digits, string terminalId)
        {
            try
            {
                var note = UnityEngine.Object.Instantiate(prefab, spot.p, Quaternion.Euler(spot.e), gw != null ? gw.transform : null);

                // multi-variant prefab: keep one SM_note child at random, drop the rest
                var children = new List<Transform>();
                foreach (Transform c in note.transform) children.Add(c);
                Transform variant = null;
                if (children.Count > 0)
                {
                    int keep = UnityEngine.Random.Range(0, children.Count);
                    for (int i = 0; i < children.Count; i++)
                        if (i == keep) variant = children[i];
                        else UnityEngine.Object.Destroy(children[i].gameObject);
                }
                var tmpHost = variant != null ? variant : note.transform;
                var byName = tmpHost.Find(PostItConstants.TmpChildName);
                var tmp = byName != null ? byName.GetComponent<TMP_Text>() : tmpHost.GetComponentInChildren<TMP_Text>(true);
                if (tmp != null) tmp.text = digits;
                else Plugin.Log?.LogWarning($"[Passcodes] {terminalId}: note has no '{PostItConstants.TmpChildName}' TMP child — digits not shown.");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[Passcodes] {terminalId}: note spawn failed: {ex.Message}");
            }
        }

        private static Vector3 ReadV3(JToken t) =>
            t is JArray a && a.Count >= 3
                ? new Vector3(a.Value<float>(0), a.Value<float>(1), a.Value<float>(2))
                : Vector3.zero;

        private static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform c in go.transform) SetLayerRecursive(c.gameObject, layer);
        }
    }
}
