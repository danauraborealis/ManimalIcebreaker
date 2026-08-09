using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Manimal.Icebreaker
{
    // CAMERA DONOR — steal a real 0.16.9 map's camera at runtime instead of shipping one.
    //
    // the bundle route died twice, for reasons now measured: bundle binding needs stubs
    // and exact compiled layouts (stripped typetrees), and the rip's serialized DATA is
    // dead on arrival — shaders/materials null, curves empty — so the shipped components
    // crashed Awake/Start, and the un-shippable SSAA broke CameraClass.SetSSR inside
    // PlayerCameraController.Create: error screen, no spawn.
    //
    // this flips the approach to the mod's own from-live-data pattern (levelsettings,
    // culling, doors): run any VANILLA raid once with DevMode on — the dumper walks the
    // real player camera, a live object where every shader/material/curve ref is VALID,
    // and writes camera/camera_donor.json next to the dll. on icebreaker, Cam2 stays the
    // chassis (valid core data, boots reliably) and the graft ADDS the donor's missing
    // components at runtime — AddComponent of the game's own classes needs no bundle
    // binding at all, which dissolves the SSAA problem outright — then fills fields from
    // the dump, resolving asset refs BY NAME against the game's loaded assets.
    //
    // graft timing is the part that must not drift: a prefix on CameraClass.SetCamera,
    // which the crash stack proves runs before the settings binding that calls SetSSR —
    // so SSAA exists by the time the game dereferences it. components are added with the
    // GO INACTIVE and fields filled before reactivation (the Awake-ordering trap): Awakes
    // then fire on populated data, and any that still fail are swallowed per-component by
    // unity rather than faulting the spawn chain like SetSSR did.
    internal static class IcebreakerCameraDonor
    {
        private static string DonorPath => System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".",
            "camera", "camera_donor.json");

        // never dump, never graft: core objects the chassis already owns, and transforms
        private static readonly HashSet<string> SkipTypes = new HashSet<string>
        {
            "Transform", "Camera", "AudioListener", "FlareLayer",
        };

        // POISON — never grafted, measured on the second grafted boot (black screen with
        // HUD burn-in, zero exceptions). these are the added components whose refs cannot
        // resolve by name on this map, and an OnRenderImage owner with null
        // shaders/materials blits nothing SILENTLY:
        //   MBOIT_Scattering       all 4 compute shaders unresolved — and already known
        //                          broken here (needs WindowsManager; the fog solution
        //                          exists precisely because MBOIT cannot run on this map)
        //   ScreenWater            every ref dead (WaterFlows/WaterMask/WetScreen)
        //   InfectionEffect        _effectMaterial unresolved; known Awake NRE
        //   RainScreenDrops        _dropMaterial unresolved — the rain prize stays lost
        //                          until its material is findable on this map
        //   ContactShadows         _noiseTextures is a ref ARRAY the dump can't encode
        //   PerfectCullingCrossSceneSampler  Start() NREs (GClass1238) — sidecar culling
        //                          already owns cross-scene
        //   StreamingController    factory's streaming manager; not a camera effect and
        //                          has no business on another map's camera
        // still grafted after this cut: UltimateBloom, DesaturateEffect, Antialiasing,
        // Tonemapping, PerfectCullingCamera — all refs resolved. if the screen is STILL
        // black, bisect those five via CamDonorSkip.
        // shrunk once the Author 22 asset carrier existed: ScreenWater, InfectionEffect
        // and RainScreenDrops moved from poison to SELF-WITHHOLDING — grafted only when
        // every asset ref resolves (the carrier supplies them), destroyed otherwise.
        // what stays here can never work regardless of assets:
        //   MBOIT_Scattering                 needs WindowsManager; dead on this map
        //   PerfectCullingCrossSceneSampler  Start() NREs; sidecar culling owns cross-scene
        //   StreamingController              factory's streaming manager, not an effect
        //   ContactShadows                   its NoiseTextureSet is a ScriptableObject the
        //                                    carrier cannot ride yet
        // ALLOWLIST — the graft model flipped after the fresh-install test (2026-08-03):
        // UltimateBloom, permanently withheld on the dev install (dust texture never
        // resolved), GRAFTED on a fresh install because a different mod set put a
        // name-matching texture in the asset pool — and blitted garbage. that is the
        // third name-resolution lottery (RainScreenDrops' cross-raid pool, ScreenWater's
        // rip assets, now this), so eligibility is no longer "refs happened to resolve":
        // ONLY components proven stable across installs graft at all. these four resolve
        // exclusively against game-global assets (Shader.Find) and have rendered clean on
        // every machine tested. additions require proving, not just resolving.
        private static readonly HashSet<string> AllowGraft = new HashSet<string>
        {
            "DesaturateEffect", "Antialiasing", "Tonemapping", "PerfectCullingCamera",
        };

        private static readonly HashSet<string> NeverGraft = new HashSet<string>
        {
            "MBOIT_Scattering", "ContactShadows",
            "PerfectCullingCrossSceneSampler", "StreamingController",
            // back in the poison after the Author 22 carrier let them THROUGH self-withhold
            // and the screen went black again (delta vs the working raid was exactly these
            // two): resolving a ripped asset by name is not the same as the asset WORKING —
            // a missed shader rebind or garbage ripped texture blits black without any
            // exception. the carrier stays (RainScreenDrops may still be salvageable);
            // these two effects are not worth the render chain.
            "ScreenWater", "InfectionEffect",
            // the TRANSIT special: on a menu raid its refs never resolve (withheld, fine),
            // but transiting FROM a rain-capable map, FindObjectsOfTypeAll finds the OLD
            // raid's rain assets still in memory — self-withhold passes honestly, then the
            // origin raid's resources unload and the component blits black off dead native
            // objects (2026-08-02 transit log: the only delta vs the working raid). name
            // resolution against a cross-raid asset pool is inherently unstable — poison.
            "RainScreenDrops",
        };

        // ------------------------------------------------------------------ dump side

        [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.OnGameStarted))]
        internal static class Patch_DumpDonorCamera
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                try
                {
                    // late graft: on a menu load SetCamera ran too early (bare camera) and
                    // deferred — by OnGameStarted the stack is built, so try once more
                    if (IceGate.On)
                    {
                        var live = CameraClass.Instance?.Camera;
                        if (live != null) TryGraft(live.gameObject, "OnGameStarted");
                    }
                    // vanilla maps only — dumping our own grafted camera would feed the
                    // graft its own output next raid
                    if (IceGate.On || !Plugin.DevMode.Value) return;
                    if (System.IO.File.Exists(DonorPath)) return; // one blessed donor, dump once
                    var cc = CameraClass.Instance;
                    var cam = cc != null ? cc.Camera : null;
                    if (cam == null) { Plugin.Log.LogWarning("[CamDonor] no CameraClass.Camera to dump"); return; }
                    Dump(cam.gameObject);
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[CamDonor] dump failed: {e.Message}"); }
            }
        }

        private static void Dump(GameObject go)
        {
            var comps = new JArray();
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null) continue;
                var t = c.GetType();
                if (SkipTypes.Contains(t.Name)) continue;
                var fields = new JObject();
                int skipped = 0;
                foreach (var f in SerializedFields(t))
                {
                    try
                    {
                        var tok = Encode(f.GetValue(c));
                        if (tok != null) fields[f.Name] = tok; else skipped++;
                    }
                    catch { skipped++; }
                }
                comps.Add(new JObject
                {
                    ["type"] = t.FullName,
                    ["enabled"] = !(c is Behaviour b) || b.enabled,
                    ["fields"] = fields,
                    ["unencodable"] = skipped,
                });
            }
            var root = new JObject
            {
                ["map"] = Singleton<GameWorld>.Instance?.LocationId ?? "?",
                ["go"] = go.name,
                ["components"] = comps,
            };
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(DonorPath));
            System.IO.File.WriteAllText(DonorPath, root.ToString());
            Plugin.Log.LogWarning($"[CamDonor] DUMPED '{go.name}' ({comps.Count} components) from "
                + $"'{root["map"]}' -> {DonorPath} — this is now the icebreaker camera donor");
        }

        // ------------------------------------------------------------------ graft side

        [HarmonyPatch(typeof(CameraClass), "SetCamera", typeof(Camera))]
        internal static class Patch_GraftDonorCamera
        {
            [HarmonyPrefix]
            private static void Prefix(Camera camera)
            {
                if (!IceGate.On || camera == null) return;
                TryGraft(camera.gameObject, "SetCamera");
            }
        }

        // the camera GO we already grafted — per-GO, because CameraClass persists across
        // raids but its camera object does not
        private static GameObject _graftedGo;

        // the UltimateBloom we grafted FOR HollywoodGraphics — the dead-effect guard must
        // not strip this one (it's donor-serialized and HG-configured, not a raw corpse)
        internal static Behaviour ProvisionedBloom;

        // BARE-CAMERA TRAP (2026-08-03, the direct-from-menu black screen): SetCamera
        // fires at two very different moments. transiting in, the camera is FULLY BUILT
        // (working raid logged 50 chassis components already present, 106 donor fields) and
        // our four effects graft onto the end of a live render chain. loading straight from
        // the MENU it fires on a BARE camera — zero chassis components — so the graft's
        // OnRenderImage owners end up FIRST in the chain, before the game builds its own
        // stack, and the result is the classic silent non-blit: the first (black) fade
        // frame never gets overwritten and the HUD accumulates on top of it.
        // so: only graft a camera the game has finished furnishing (EffectsController is
        // that marker), and retry from OnGameStarted, by which point it always is. if it
        // never furnishes, we simply dont graft — plain Cam2 renders, better absent than
        // hollow (the doctrine that got the allowlist here in the first place).
        internal static void TryGraft(GameObject go, string site)
        {
            if (go == null || _graftedGo == go) return;
            if (go.GetComponent<EffectsController>() == null)
            {
                Plugin.Log.LogDebug($"[CamDonor] {site}: camera not furnished yet (no EffectsController) — deferring graft");
                return;
            }
            try
            {
                if (!System.IO.File.Exists(DonorPath))
                {
                    Plugin.Log.LogWarning("[CamDonor] no camera/camera_donor.json — run one vanilla raid "
                        + "(any map) with DevMode on to record a donor. staying on plain Cam2.");
                    return;
                }
                Graft(go, JObject.Parse(System.IO.File.ReadAllText(DonorPath)));
                _graftedGo = go;
            }
            catch (Exception e) { Plugin.Log.LogError($"[CamDonor] graft failed — plain Cam2 stands: {e}"); }
        }

        private static void Graft(GameObject go, JObject donor)
        {
            var comps = donor["components"] as JArray;
            if (comps == null) return;

            // ADD-ONLY, hard-learned on the first grafted boot: writing the donor's 747
            // fields over the CHASSIS components produced a black screen with UI burn-in
            // and ZERO exceptions — on these obfuscated classes many public fields are
            // live runtime wiring, not tuning, and re-pointing them by name silently puts
            // an OnRenderImage owner into a non-blitting state. Cam2's own components
            // already work; the graft's whole job is only what Cam2 LACKS.
            var skip = new HashSet<string>((Plugin.CamDonorSkip?.Value ?? "")
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()),
                StringComparer.OrdinalIgnoreCase);

            int filled = 0, missingType = 0, refMiss = 0, existing = 0, skipped = 0;
            var addedNames = new List<string>();
            var refMissDetail = new List<string>();
            var restoreEnabled = new List<(Behaviour, bool)>();

            // inactive while we add+populate: AddComponent fires Awake IMMEDIATELY on an
            // active GO, and a donor component awakening on default fields is exactly the
            // NightVision empty-curve crash from the bundle attempt
            bool wasActive = go.activeSelf;
            go.SetActive(false);
            try
            {
                foreach (var row in comps.OfType<JObject>())
                {
                    var typeName = (string)row["type"];
                    var type = AccessTools.TypeByName(typeName);
                    if (type == null || !typeof(Component).IsAssignableFrom(type)) { missingType++; continue; }
                    if (SkipTypes.Contains(type.Name)) continue;
                    // HG PROVISION: HollywoodGraphics' Bloom wrapper AddComponents a raw
                    // UltimateBloom when the camera has none — and a raw one has NULL
                    // serialized arrays (m_BloomIntensities etc. only exist via prefab
                    // deserialization), so its ctor NREs, GraphicsController.Start dies,
                    // and the half-built bloom eats the frame (the Sonic black screen).
                    // when HG is installed, graft the donor's fully-serialized UltimateBloom
                    // so HG finds a retail-shaped component and configures it like on any
                    // map. the dust-texture lottery that banned UltimateBloom from the
                    // allowlist doesnt apply here: the field is skipped below and HG loads
                    // its OWN LensDust png from disk right after (Bloom.UpdateLensDust).
                    // ALSO provisioned for HollywoodFX-without-HG (08-07, yamaica's
                    // permanent battle blur): HFX's ConcussionController does
                    // camera.GetComponent<UltimateBloom>() with NO null check and its
                    // Update writes _bloom.m_DustIntensity BEFORE the concussion decay
                    // line — on a camera without the component the write NREs every
                    // frame and the blur applied by the preceding line never fades.
                    // retail cameras all ship an UltimateBloom so vanilla maps never
                    // see it; our Cam2 fallback must provide one. parked-but-present
                    // satisfies GetComponent and the dust write lands harmlessly.
                    bool hgBloom = type.Name == "UltimateBloom"
                        && (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.janky.hollywoodgraphics")
                         || BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.janky.hollywoodfx"));
                    if (!AllowGraft.Contains(type.Name) && !hgBloom) { skipped++; continue; } // allowlist: proven-stable only
                    if (skip.Contains(type.Name)) { skipped++; continue; }        // bisect knob
                    if (go.GetComponent(type) != null) { existing++; continue; }  // chassis owns it

                    var c = go.AddComponent(type);
                    if (c == null) { missingType++; continue; }

                    int compRefMiss = 0;
                    var compMissed = new List<string>();
                    var fields = row["fields"] as JObject;
                    if (fields != null)
                        foreach (var kv in fields)
                        {
                            // the dust texture never resolves here and HG supplies its own
                            if (hgBloom && kv.Key == "m_DustTexture") continue;
                            var f = SerializedFields(type).FirstOrDefault(x => x.Name == kv.Key);
                            if (f == null) continue;
                            try
                            {
                                int before = compRefMiss;
                                object v = Decode(kv.Value, f.FieldType, go, ref compRefMiss);
                                if (compRefMiss > before) compMissed.Add(kv.Key);
                                if (v != null || !f.FieldType.IsValueType) { f.SetValue(c, v); filled++; }
                            }
                            catch { }
                        }

                    // no dust texture until HG assigns one — lens dust off so nothing
                    // samples a null; HG's UpdateSettings sets it from its own config.
                    // NOTE the renderer does NOT survive: even fully serialized it blits
                    // nothing on some installs (lottery strike three), so the dead-effect
                    // guard parks it right after HG's Start reads it. the provision exists
                    // purely so HG's Bloom ctor lives and the REST of HG initializes.
                    if (hgBloom && compRefMiss == 0)
                    {
                        try
                        {
                            var lensDust = SerializedFields(type).FirstOrDefault(x => x.Name == "m_UseLensDust");
                            if (lensDust != null) lensDust.SetValue(c, false);
                        }
                        catch { }
                        ProvisionedBloom = c as Behaviour;
                        Plugin.Log.LogWarning("[CamDonor] UltimateBloom provisioned for HollywoodGraphics (keeps its init alive; the renderer itself gets parked by the guard)");
                    }

                    // SELF-WITHHOLD: a camera effect with an unresolved shader/material/
                    // texture is exactly the silent black-blit class from the second boot.
                    // better absent than hollow — and the log names what the carrier still
                    // owes so the fix is a carrier addition, not a debugging session.
                    if (compRefMiss > 0)
                    {
                        refMiss += compRefMiss;
                        refMissDetail.Add($"{type.Name}({string.Join("+", compMissed)})");
                        UnityEngine.Object.DestroyImmediate(c);
                        continue;
                    }
                    addedNames.Add(type.Name);

                    if (c is Behaviour beh)
                    {
                        bool want = row["enabled"] == null || (bool)row["enabled"];
                        restoreEnabled.Add((beh, want));
                        beh.enabled = false; // OnEnable waits until everything is populated
                    }
                }
            }
            finally
            {
                go.SetActive(wasActive);
            }
            foreach (var (beh, want) in restoreEnabled)
                if (beh != null) beh.enabled = want;

            Plugin.Log.LogWarning($"[CamDonor] GRAFTED (add-only) onto '{go.name}': "
                + $"added [{string.Join(", ", addedNames)}] ({filled} fields), "
                + $"{existing} chassis component(s) left untouched, {skipped} skipped by config, "
                + $"{missingType} type(s) unresolved"
                + (refMissDetail.Count > 0 ? $"; WITHHELD (carrier owes refs): {string.Join(", ", refMissDetail)}" : ""));
        }

        // ------------------------------------------------------------- (de)serialization

        private static IEnumerable<FieldInfo> SerializedFields(Type t)
        {
            for (var cur = t; cur != null && cur != typeof(MonoBehaviour) && cur != typeof(Behaviour) && cur != typeof(Component); cur = cur.BaseType)
                foreach (var f in cur.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (f.IsInitOnly || f.IsLiteral) continue;
                    bool serialized = f.IsPublic || f.GetCustomAttribute<SerializeField>() != null;
                    if (!serialized) continue;
                    if (typeof(Delegate).IsAssignableFrom(f.FieldType)) continue;
                    yield return f;
                }
        }

        // json-encodes what a camera component plausibly carries; null = not encodable
        // (the dump records how many were skipped so a hollow donor is visible in the log)
        private static JToken Encode(object v)
        {
            switch (v)
            {
                case null: return JValue.CreateNull();
                case bool _: case string _: return new JValue(v);
                case int _: case long _: case short _: case byte _: case sbyte _:
                case uint _: case ushort _: case ulong _:
                case float _: case double _: return new JValue(v);
                case Enum e: return new JValue(Convert.ToInt64(e));
                case Vector2 v2: return new JObject { ["x"] = v2.x, ["y"] = v2.y };
                case Vector3 v3: return new JObject { ["x"] = v3.x, ["y"] = v3.y, ["z"] = v3.z };
                case Vector4 v4: return new JObject { ["x"] = v4.x, ["y"] = v4.y, ["z"] = v4.z, ["w"] = v4.w };
                case Color c: return new JObject { ["r"] = c.r, ["g"] = c.g, ["b"] = c.b, ["a"] = c.a };
                case LayerMask lm: return new JObject { ["mask"] = lm.value };
                case AnimationCurve ac:
                    var keys = new JArray();
                    foreach (var k in ac.keys)
                        keys.Add(new JObject { ["t"] = k.time, ["v"] = k.value, ["i"] = k.inTangent, ["o"] = k.outTangent });
                    return new JObject { ["curve"] = keys, ["pre"] = (int)ac.preWrapMode, ["post"] = (int)ac.postWrapMode };
                case UnityEngine.Object uo:
                    // by NAME + type — the whole point: the ref is re-resolved against the
                    // game's own loaded assets on the destination map
                    return new JObject { ["ref"] = uo.GetType().Name, ["name"] = uo.name };
                default:
                    return null; // nested classes, arrays of refs, etc — count as unencodable
            }
        }

        private static object Decode(JToken tok, Type want, GameObject host, ref int refMiss)
        {
            if (tok == null || tok.Type == JTokenType.Null) return null;
            if (want.IsEnum) return Enum.ToObject(want, (long)tok);
            if (want == typeof(bool)) return (bool)tok;
            if (want == typeof(string)) return (string)tok;
            if (want == typeof(int)) return (int)tok;
            if (want == typeof(long)) return (long)tok;
            if (want == typeof(float)) return (float)tok;
            if (want == typeof(double)) return (double)tok;
            if (want == typeof(byte)) return (byte)tok;
            if (want == typeof(Vector2)) return new Vector2((float)tok["x"], (float)tok["y"]);
            if (want == typeof(Vector3)) return new Vector3((float)tok["x"], (float)tok["y"], (float)tok["z"]);
            if (want == typeof(Vector4)) return new Vector4((float)tok["x"], (float)tok["y"], (float)tok["z"], (float)tok["w"]);
            if (want == typeof(Color)) return new Color((float)tok["r"], (float)tok["g"], (float)tok["b"], (float)tok["a"]);
            if (want == typeof(LayerMask)) return (LayerMask)(int)tok["mask"];
            if (want == typeof(AnimationCurve))
            {
                var ac = new AnimationCurve(((JArray)tok["curve"])
                    .Select(k => new Keyframe((float)k["t"], (float)k["v"], (float)k["i"], (float)k["o"])).ToArray());
                ac.preWrapMode = (WrapMode)(int)tok["pre"];
                ac.postWrapMode = (WrapMode)(int)tok["post"];
                return ac;
            }
            if (typeof(UnityEngine.Object).IsAssignableFrom(want) && tok["ref"] != null)
            {
                var name = (string)tok["name"];
                if (string.IsNullOrEmpty(name)) return null;
                // sibling components first (PrismEffects-style same-camera refs), then
                // shaders by Find, then anything the game has loaded, by exact name
                if (typeof(Component).IsAssignableFrom(want))
                {
                    var sib = host.GetComponent(want);
                    if (sib != null) return sib;
                }
                if (want == typeof(Shader))
                {
                    var sh = Shader.Find(name);
                    if (sh != null) return sh;
                }
                var found = Resources.FindObjectsOfTypeAll(want).FirstOrDefault(o => o.name == name);
                if (found == null) { refMiss++; return null; }
                // carried materials come from the rip, whose shader is decompiled garbage —
                // swap in the game's own same-name shader (the RebindShaders lesson)
                if (found is Material mat && mat.shader != null)
                {
                    try
                    {
                        var live = Shader.Find(mat.shader.name);
                        if (live != null && live != mat.shader) mat.shader = live;
                    }
                    catch { }
                }
                return found;
            }
            return null;
        }
    }
}
