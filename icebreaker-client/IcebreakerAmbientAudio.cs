using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.SceneManagement;

namespace Manimal.Icebreaker
{
    // REBUILDS RETAIL'S Audio.AmbientSubsystem ON THE RIPPED SOUND SCENE.
    //
    // retail's Icebreaker_Sound ships 356 audio components. the SPATIAL half (trigger
    // areas, portals, rooms) we already restore through icebreaker_spatial_audio.json;
    // this is the AMBIENT half — the system that actually plays things. AssetRipper
    // stripped all of it (BSG scripts have no SDK counterpart to bind to), which is why
    // 126 of the scene's AudioSources sit there with a null clip: in retail the clip
    // never lives on the AudioSource at all, it lives on the player component.
    //
    // the components CANT be baked back into the bundle — unity has no script to
    // serialise against — so this is the aibake/culling/flares pattern: a sidecar
    // extracted from retail's own level707, replayed onto the scene at raid start.
    // every class involved still exists in 4.0's Assembly-CSharp (checked all 19; only
    // the two MetaXR acoustics ones are 1.0-only, and SpatialAudioSystem covers that).
    //
    // ORDER IS THE WHOLE TRICK: AddComponent runs Awake immediately, and
    // AmbientAudioSystem.Awake does GetComponentsInChildren for every player/group/
    // emitter it will ever drive. so the entire hierarchy is built while its root is
    // INACTIVE — components added, fields poured in — and only then reactivated, which
    // cascades the Awakes in the right order with their data already present.
    internal static class IcebreakerAmbientAudio
    {
        private const string SidecarName = "icebreaker_ambient_audio.json";
        private const string RootName = "AmbientAudioSystem";

        private static readonly Dictionary<string, Type> _types = new Dictionary<string, Type>();
        private static Dictionary<string, List<Transform>> _byPath;
        private static Dictionary<string, AudioClip> _clips;
        private static Dictionary<string, UnityEngine.Object> _banks;
        private static Dictionary<string, AudioMixerGroup> _mixers;

        internal static void TryRestore()
        {
            try { Restore(); }
            catch (Exception e)
            {
                // never let audio take the raid down — the scene-wired AudioSource
                // fallback (clip + playOnAwake, baked in the bundle) still gives ambience
                Plugin.Log.LogWarning($"[Ambient] restore failed, falling back to the plain AudioSources: {e}");
            }
        }

        private static void Restore()
        {
            OutdoorBedRestored = false;   // per raid — a second icebreaker raid rebuilds
            _seasonData = null;
            var path = Path.Combine(
                Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".", "acoustics", SidecarName);
            if (!File.Exists(path))
            {
                Plugin.Log.LogWarning($"[Ambient] sidecar missing at {path} — ambient subsystem not restored");
                return;
            }

            var root = FindRoot(RootName);
            if (root == null)
            {
                Plugin.Log.LogWarning($"[Ambient] no '{RootName}' object in the scene — nothing to rebuild onto");
                return;
            }

            var records = JArray.Parse(File.ReadAllText(path));
            bool wasActive = root.activeSelf;
            root.SetActive(false);   // see the ORDER note up top

            // outlives the try: the loops are voiced after the root goes live again
            var built = new List<KeyValuePair<JObject, Component>>(records.Count);

            try
            {
                IndexHierarchy(root.transform);
                IndexAssets();

                // PASS 1 — every component exists before any field is poured, so the
                // cross-references in pass 2 (players -> groups, observers -> rooms,
                // emitters -> splines: 567 of them) always find their target
                // PLAYERS FIRST. SoundPlayerRoomObserverComponent and friends declare
                // [RequireComponent] on the ABSTRACT BaseAmbientSoundPlayer, so adding one
                // to a bare object makes unity try to auto-add an abstract class and refuse
                // the whole thing ("The script class can't be abstract!", 28 of them).
                // attach the concrete player to each object first and the requirement is
                // already satisfied when its helpers land.
                var ordered = new List<JToken>(records.Count);
                foreach (var t in records) if (IsPlayerRecord(t)) ordered.Add(t);
                foreach (var t in records) if (!IsPlayerRecord(t)) ordered.Add(t);
                records = new JArray(ordered.ToArray());
                int placed = 0, noObject = 0, noType = 0, notComponent = 0, addFailed = 0;
                var refused = new HashSet<string>();
                foreach (var tok in records)
                {
                    var rec = (JObject)tok;
                    var clsName = rec.Value<string>("cls");
                    var goPath = rec.Value<string>("path");
                    var type = ResolveType(clsName);
                    if (type == null) { noType++; refused.Add(clsName ?? "?"); continue; }
                    // not everything in the sidecar is a MonoBehaviour — some of these are
                    // plain [Serializable] helpers that live as FIELDS on other components.
                    // AddComponent hands back null for those, and a null in this list is
                    // what took the whole restore down with an NRE (07-30).
                    if (!typeof(Component).IsAssignableFrom(type)) { notComponent++; refused.Add(type.Name); continue; }
                    var target = TakeTransform(goPath, type);
                    if (target == null) { noObject++; continue; }
                    var comp = target.gameObject.GetComponent(type);
                    if (comp == null) comp = target.gameObject.AddComponent(type);
                    if (comp == null) { addFailed++; refused.Add(type.Name); continue; }
                    built.Add(new KeyValuePair<JObject, Component>(rec, comp));
                    placed++;
                }

                // PASS 2 — pour the serialized data back in
                int fields = 0, fieldFails = 0;
                foreach (var kv in built)
                {
                    var f = kv.Key["fields"] as JObject;
                    if (f == null || kv.Value == null) continue;
                    foreach (var prop in f)
                    {
                        try
                        {
                            if (SetField(kv.Value, prop.Key, prop.Value)) fields++;
                        }
                        catch { fieldFails++; }
                    }
                }

                // SPATIALISE AND DRIVE. two facts from BaseAmbientSoundPlayer.Awake make
                // this necessary rather than optional:
                //
                //  1. the AudioSources in the scene carry NO usable 3D setup — retail
                //     leaves them at unity defaults (water drops ship spatialBlend 0) and
                //     the component pushes blend/distances/rolloff onto them at runtime.
                //     so a source playing without its component is a 2D sound at full
                //     volume everywhere on the ship, which is exactly what shipped.
                //  2. _playOnAwake is 0 on every one of these players — retail starts them
                //     from the room observers and point managers as you move through the
                //     ship. those higher-level drivers may not engage on a backported
                //     location, and betting the map's ambience on them means silence.
                //
                // so we push the component's OWN authored values onto its source and start
                // the loops ourselves. it's the same AudioSource either way: if BSG's
                // drivers do run, they take the volume over through their fader.
                //
                // this runs AFTER the root is live again (below), not here: Play() on an
                // AudioSource whose GameObject is disabled is a silent no-op, which is
                // exactly how 75 "started" loops managed to make no sound (07-30).
                Plugin.Log.LogWarning(
                    $"[Ambient] rebuilt {placed}/{records.Count} components ({fields} fields, {fieldFails} field errors; " +
                    $"{noObject} missing objects, {noType} unknown types, {notComponent} non-components, {addFailed} add-failed)");
                if (refused.Count > 0)
                    Plugin.Log.LogWarning($"[Ambient] not attachable: {string.Join(", ", new List<string>(refused).ToArray())}");
                // List.ToArray, not the LINQ extension — EFT ships a GClass2298.ToArray
                // extension that shadows it and only accepts a Vector3
                if (_missingClips.Count > 0)
                    Plugin.Log.LogWarning($"[Ambient] clips not found in the bundle: {string.Join(", ", new List<string>(_missingClips).ToArray())}");
                if (_missingBanks.Count > 0)
                    Plugin.Log.LogWarning($"[Ambient] sound banks not present in 4.0: {string.Join(", ", new List<string>(_missingBanks).ToArray())} " +
                                          "— the random players using them stay silent");
            }
            finally
            {
                // reactivating is what starts the whole subsystem; BSG logs its own
                // "Ambient Audio System successful init" (or an init failure) right after
                root.SetActive(wasActive);
            }

            // NOW the loops can be spatialised and started — the hierarchy is live, so
            // Play() actually plays instead of logging "Can not play a disabled audio
            // source" 75 times and leaving the ship silent
            try
            {
                OutdoorDuck.Reset();
                int voiced = 0, randoms = 0, gated = 0;
                foreach (var kv in built)
                {
                    if (!IsPlayer(kv.Value)) continue;

                    // hand the 2D global beds to the environment crossfade — these are the
                    // ones retail splits into its outdor/Indoor groups
                    var p = kv.Key.Value<string>("path") ?? "";
                    bool is2DOut = p.Contains("/2DRP/outdor/");
                    bool is2DIn = p.Contains("/2DRP/Indoor/");

                    if (ApplyToSource(kv.Value))
                    {
                        if (is2DOut || is2DIn)
                        {
                            var s = kv.Value.GetComponent<AudioSource>();
                            OutdoorDuck.Add(s, s != null ? s.volume : 0f, is2DIn);
                            gated++;
                        }
                        voiced++; continue;
                    }

                    // no loop clip = a RANDOM player. these drive themselves once started:
                    // BaseRandomAmbientSoundPlayer.OnPlay picks a clip from the bank and
                    // re-arms on _randomTimeRange, ticked by the coroutine Play() starts.
                    // so hand it to BSG's own Play() rather than the AudioSource — the
                    // component is what knows how to choose and re-trigger.
                    if (StartRandomPlayer(kv.Value))
                    {
                        randoms++;
                        if (is2DOut || is2DIn)
                        {
                            var s = kv.Value.GetComponent<AudioSource>();
                            OutdoorDuck.Add(s, s != null ? s.volume : 0f, is2DIn);
                            gated++;
                        }
                    }
                }
                // BARE sources (no player component: the Monitors etc.) authored with a
                // clip + playOnAwake ran at scene load, got stopped — first by our
                // load-screen silencer, then by the raid-start audio reset — and nothing
                // else ever restarts them. revive them here, once, configured as authored.
                int bare = 0;
                if (root != null)
                    foreach (var s in root.GetComponentsInChildren<AudioSource>(true))
                        if (s != null && !s.isPlaying && s.clip != null && s.playOnAwake) { s.Play(); bare++; }
                Plugin.Log.LogWarning($"[Ambient] spatialised + started {voiced} loops, {randoms} random players; " +
                                      $"{gated} 2D bed(s) gated to indoor/outdoor; {bare} bare playOnAwake source(s) revived");
                StartOutdoorBed(built);
                StartRoomToneTransitions();
                RouteMixerGroups();
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Ambient] voicing failed: {e.Message}"); }
            finally { _byPath = null; _clips = null; _banks = null; _mixers = null; }
        }

        // ---- scene + asset lookup -------------------------------------------------

        private static GameObject FindRoot(string name)
        {
            // GameObject.Find skips inactive objects and we deactivate as we go, so the
            // scene roots are walked by hand
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var sc = SceneManager.GetSceneAt(i);
                if (!sc.isLoaded) continue;
                foreach (var go in sc.GetRootGameObjects())
                    if (go.name == name) return go;
            }
            return null;
        }

        // full hierarchy path -> transforms. a LIST per path because retail happily gives
        // siblings the same name; TakeTransform hands them out one component at a time so
        // duplicates spread across the copies instead of piling onto the first one.
        // indexes EVERY root of the sound scene, not just AmbientAudioSystem — the parity
        // extras (SourceOccluder, GuidComponent on the SpatialAudioSystem object) live
        // under other roots.
        private static void IndexHierarchy(Transform root)
        {
            _byPath = new Dictionary<string, List<Transform>>(1024);
            var sc = root.gameObject.scene;
            if (sc.IsValid() && sc.isLoaded)
                foreach (var go in sc.GetRootGameObjects())
                    Walk(go.transform, go.name);
            else
                Walk(root, root.name);
        }

        private static void Walk(Transform t, string path)
        {
            if (!_byPath.TryGetValue(path, out var list)) _byPath[path] = list = new List<Transform>(1);
            list.Add(t);
            for (int i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i);
                Walk(c, path + "/" + c.name);
            }
        }

        private static Transform TakeTransform(string path, Type type)
        {
            if (string.IsNullOrEmpty(path) || !_byPath.TryGetValue(path, out var list)) return null;
            foreach (var t in list)
                if (t != null && t.gameObject.GetComponent(type) == null) return t;
            return list.Count > 0 ? list[0] : null;   // all taken — reuse rather than drop the record
        }

        private static void IndexAssets()
        {
            _clips = new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in Resources.FindObjectsOfTypeAll<AudioClip>())
                if (c != null && !string.IsNullOrEmpty(c.name)) _clips[c.name] = c;

            _mixers = new Dictionary<string, AudioMixerGroup>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in Resources.FindObjectsOfTypeAll<AudioMixerGroup>())
                if (m != null && !string.IsNullOrEmpty(m.name)) _mixers[m.name] = m;

            // EFT.SoundBank holds the clip sets the 41 random players draw from. all
            // twelve of icebreaker's banks ship with retail's own locations and are simply
            // absent from 4.0, so we rebuild them: the clips come from our bank bundle and
            // the membership from icebreaker_sound_banks.json.
            _banks = new Dictionary<string, UnityEngine.Object>(StringComparer.OrdinalIgnoreCase);
            var bankType = ResolveType("EFT.SoundBank");
            if (bankType != null)
            {
                // anything the game DOES have wins — no point rebuilding a live asset
                foreach (var b in Resources.FindObjectsOfTypeAll(bankType))
                    if (b != null && !string.IsNullOrEmpty(b.name)) _banks[b.name] = b;
                BuildMissingBanks(bankType);
            }
        }

        // REBUILD THE SOUND BANKS. only one path through SoundBank matters here:
        // PickSingleClip(0) -> Environments[0][0] -> a random clip. the bank's own volume/
        // pitch/rolloff fields belong to BetterSource playback and are never read by an
        // AmbientSoundPlayer, and the shuffle array self-initialises on first pick — so a
        // bank carrying the right clips in the right slot behaves exactly like the retail
        // asset. (its serialized layout drifted between 1.0 and 4.0 and can't be parsed
        // straight, which is why this is rebuilt rather than restored.)
        private const string BankSidecar = "icebreaker_sound_banks.json";
        private const string BankBundle = "streamingassets/Windows/manimal/icebreaker_banks.bundle";
        private static AssetBundle _bankBundle;

        private static void BuildMissingBanks(Type bankType)
        {
            try
            {
                var dir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".";
                var sidecar = Path.Combine(dir, "acoustics", BankSidecar);
                if (!File.Exists(sidecar)) return;
                var wanted = JObject.Parse(File.ReadAllText(sidecar));

                // pull the clips out of our own bundle and fold them into the clip index
                if (_bankBundle == null)
                {
                    var bundlePath = Path.Combine(dir, BankBundle.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(bundlePath))
                    {
                        _bankBundle = AssetBundle.LoadFromFile(bundlePath);
                        if (_bankBundle != null)
                        {
                            int added = 0;
                            foreach (var c in _bankBundle.LoadAllAssets<AudioClip>())
                                if (c != null) { _clips[c.name] = c; added++; }
                            Plugin.Log.LogWarning($"[Ambient] bank bundle loaded: {added} clips");
                        }
                    }
                    else Plugin.Log.LogWarning($"[Ambient] bank bundle not shipped ({bundlePath}) — random players stay silent");
                }

                var envType = ResolveType("EFT.EnvironmentVariety");
                var distType = ResolveType("EFT.DistanceVarity");
                if (envType == null || distType == null)
                {
                    Plugin.Log.LogWarning("[Ambient] EnvironmentVariety/DistanceVarity missing — cannot rebuild banks");
                    return;
                }

                int builtBanks = 0; var thin = new List<string>();
                foreach (var kv in wanted)
                {
                    if (_banks.ContainsKey(kv.Key)) continue;          // game already has it
                    var names = kv.Value as JArray;
                    if (names == null) continue;

                    var clips = new List<AudioClip>();
                    foreach (var n in names)
                    {
                        var cn = n.Value<string>();
                        if (cn != null && _clips.TryGetValue(cn, out var clip) && clip != null) clips.Add(clip);
                        else if (cn != null) _missingClips.Add(cn);
                    }
                    if (clips.Count == 0) { thin.Add(kv.Key); continue; }

                    var bank = ScriptableObject.CreateInstance(bankType);
                    bank.name = kv.Key;

                    // Environments[0][0].Clips = our clips, with HasEnvironment off so
                    // index 0 is always the one picked
                    var dist = Activator.CreateInstance(distType);
                    AccessTools.Field(distType, "Clips").SetValue(dist, clips.ToArray());
                    var variety = Activator.CreateInstance(envType);
                    var vClips = Array.CreateInstance(distType, 1);
                    vClips.SetValue(dist, 0);
                    AccessTools.Field(envType, "Clips").SetValue(variety, vClips);
                    var envs = Array.CreateInstance(envType, 1);
                    envs.SetValue(variety, 0);
                    AccessTools.Field(bankType, "Environments").SetValue(bank, envs);
                    AccessTools.Field(bankType, "HasEnvironment")?.SetValue(bank, false);

                    _banks[kv.Key] = bank;
                    builtBanks++;
                }
                Plugin.Log.LogWarning($"[Ambient] rebuilt {builtBanks} sound bank(s) from our own clips" +
                                      (thin.Count > 0 ? $"; no clips found for: {string.Join(", ", thin.ToArray())}" : ""));
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Ambient] bank rebuild failed: {e.Message}"); }
        }

        // THE OUTDOOR BED, retail's way. AmbientAudioSystem points at a
        // SeasonAmbientSoundDataSO; DayTimeAmbientBlender pulls a day/night container out
        // of it and drives its own two AudioSources (DaySource/NightSource), crossfading
        // them on the time of day. that SO's layout drifted between 1.0 and 4.0 so it
        // can't be parsed — but retail's icebreaker copy only ever held ONE clip,
        // amb_icebreaker_outdoor, which is the same clip our Amb_OutdoorWind bed plays.
        // so rebuild it: same clip for day and night, filed under Summer because
        // TryGetDayTimeSoundContainer falls back to Summer for any season it can't find.
        private const string OutdoorClip = "amb_icebreaker_outdoor";
        private static UnityEngine.Object _seasonData;

        private static UnityEngine.Object BuildSeasonData(Type wanted)
        {
            if (_seasonData != null) return _seasonData;
            try
            {
                var soType = ResolveType("Audio.AmbientSubsystem.Data.SeasonAmbientSoundDataSO");
                var clipsType = ResolveType("Audio.AmbientSubsystem.DayTimeAmbientSeasonClips");
                var containerType = ResolveType("Audio.AmbientSubsystem.DayTimeAmbientSoundContainer");
                var seasonEnum = ResolveType("EFT.Weather.ESeasonStatus") ?? ResolveType("ESeasonStatus");
                if (soType == null || clipsType == null || containerType == null || seasonEnum == null) return null;
                if (!_clips.TryGetValue(OutdoorClip, out var clip) || clip == null)
                {
                    _missingClips.Add(OutdoorClip);
                    return null;
                }

                var container = Activator.CreateInstance(containerType);
                AccessTools.Field(containerType, "DayAmbientClip").SetValue(container, clip);
                AccessTools.Field(containerType, "NightAmbientClip").SetValue(container, clip);

                // it derives from Dictionary<ESeasonStatus, container>, so fill it as one.
                // every season gets an entry — cheap, and it removes the fallback log spam.
                var dict = Activator.CreateInstance(clipsType) as System.Collections.IDictionary;
                if (dict == null) return null;
                foreach (var v in Enum.GetValues(seasonEnum)) dict[v] = container;

                var so = ScriptableObject.CreateInstance(soType);
                so.name = "ManimalIcebreakerSeasonAmbientSoundData";
                AccessTools.Field(soType, "_dayTimeAmbientSeasonClips").SetValue(so, dict);
                _seasonData = so;
                Plugin.Log.LogWarning($"[Ambient] rebuilt season ambient data with '{OutdoorClip}' for day+night " +
                                      $"({dict.Count} seasons) — retail's outdoor bed");
                return wanted.IsInstanceOfType(so) ? so : null;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Ambient] season data rebuild failed: {e.Message}");
                return null;
            }
        }

        private static Type ResolveType(string name)
        {
            if (name == null) return null;
            if (_types.TryGetValue(name, out var t)) return t;
            t = AccessTools.TypeByName(name);
            _types[name] = t;
            return t;
        }

        // per-clip/bank taste overrides on top of the authored volumes (user call 07-30):
        // the indoor wind reads like OUTDOOR wind bleeding through the hull at retail's
        // levels — half it everywhere it plays (the 5 positional Indoor_wind_loop players
        // and the 2D wind_howl bed). scales the COMPONENT's _volume, not just the source,
        // so BSG's own fader (which drives toward _volume) can't undo it.
        private static float TasteScale(string clipOrBank)
        {
            switch (clipOrBank)
            {
                case "amb_icebraker_indoor_wind": return 0.5f;   // BSG's own typo, kept verbatim
                case "wind_howl": return 0.5f;
                default: return 1f;
            }
        }

        private static void ScaleAuthoredVolume(Component player, Type t, float scale)
        {
            if (scale >= 1f) return;
            var f = FindField(t, "_volume");
            if (f == null) return;
            try { f.SetValue(player, (float)f.GetValue(player) * scale); } catch { }
        }

        // push a player's authored values onto its AudioSource, exactly as
        // BaseAmbientSoundPlayer.Awake would, then start it if it's a loop.
        private static bool ApplyToSource(Component player)
        {
            var src = player.GetComponent<AudioSource>();
            if (src == null) return false;
            var t = player.GetType();

            src.playOnAwake = false;
            src.mute = false;   // some of these ship muted in the scene (see the outdoor bed)
            src.spatialBlend = Get<float>(player, t, "_spatialBlend", 1f);
            src.minDistance = Get<float>(player, t, "_minDistance", 1f);
            src.maxDistance = Get<float>(player, t, "_maxDistance", 20f);
            // spread is NOT degrees in BSG's data — SetSpread does Lerp(180, 0, value), so
            // it's an INVERTED 0-1 where 1 means fully directional. multiplying by 360 (as
            // this did first) gave ~195 degrees on a 0.54 source: omnidirectional, and a
            // 3D sound smeared across every speaker reads as "right on top of me".
            float spreadVal = Mathf.Clamp01(Get<float>(player, t, "_spread", 0f));
            if (Get<bool>(player, t, "_useCustomSpreadCurve", false))
            {
                var sc = Get<AnimationCurve>(player, t, "_spreadCurve", null);
                if (sc != null && sc.length > 0) spreadVal = sc.Evaluate(spreadVal);
            }
            src.spread = Mathf.Lerp(180f, 0f, spreadVal);
            var curve = Get<AnimationCurve>(player, t, "_rolloffCurve", null);
            if (curve != null && curve.length > 0)
            {
                src.rolloffMode = AudioRolloffMode.Custom;
                src.SetCustomCurve(AudioSourceCurveType.CustomRolloff, curve);
            }
            var mixer = Get<AudioMixerGroup>(player, t, "_mixerGroup", null);
            if (mixer != null) src.outputAudioMixerGroup = mixer;

            // VOLUME BEFORE THE LOOP CHECK. this used to sit below the early-return, so
            // the 40 random players kept the scene's authored 1.0 while their real values
            // run 0.10-0.60 — up to ten times too loud, and the 2D_* ones (wind gusts,
            // ship moans, indoor wind) are global beds at spatialBlend 0, so that landed
            // everywhere at once. BSG's fader would eventually settle these, but only
            // while its coroutine ticks; this is the floor underneath that.
            src.volume = Mathf.Clamp01(Get<float>(player, t, "_volume", 1f));

            // only the LOOP players own a clip; the random ones draw from a SoundBank when
            // their driver fires, so starting them here would be meaningless
            var clip = Get<AudioClip>(player, t, "_loopClip", null);
            if (clip == null) return false;

            float taste = TasteScale(clip.name);
            if (taste < 1f) { ScaleAuthoredVolume(player, t, taste); src.volume *= taste; }

            src.clip = clip;
            src.loop = true;
            if (!src.isPlaying) src.Play();
            return true;
        }

        // true once retail's own outdoor bed is playing — IcebreakerAcoustics keeps our
        // Amb_OutdoorWind alive until this says the real one took over
        internal static bool OutdoorBedRestored { get; private set; }

        // ---- outdoor bed output watchdog ----
        // three rounds of property checks (isPlaying, then unmuted+volume+direct, then clip
        // load state) each said the retail bed was fine while the deck stayed dead silent.
        // so stop asking the source about itself and READ ITS SAMPLES. runs on the
        // KeepAmbientAlive cadence; the moment it proves nothing is coming out, our own
        // wind bed comes back and stays.
        private static AudioSource _watchDay, _watchNight;
        private static int _watchTicks;
        private static bool _watchSettled;
        private static float[] _watchBuf;

        internal static void WatchOutdoorBed()
        {
            if (_watchSettled) return;
            if (_watchDay == null && _watchNight == null) return;
            // give the bed a moment to actually spin up before judging it (~2s at the
            // 30-frame cadence this is called on)
            if (++_watchTicks < 4) return;

            if (_watchBuf == null) _watchBuf = new float[256];
            float peak = 0f;
            foreach (var s in new[] { _watchDay, _watchNight })
            {
                if (s == null || !s.isPlaying) continue;
                try { s.GetOutputData(_watchBuf, 0); }
                catch { continue; }
                for (int i = 0; i < _watchBuf.Length; i++)
                {
                    float a = _watchBuf[i] < 0f ? -_watchBuf[i] : _watchBuf[i];
                    if (a > peak) peak = a;
                }
            }
            // a real wind loop never sits this close to digital silence for a whole buffer
            if (peak > 0.0005f)
            {
                _watchSettled = true;
                Plugin.Log.LogWarning($"[Ambient] outdoor bed output verified (peak {peak:F4}) — retail's bed is genuinely carrying the deck");
                return;
            }
            if (_watchTicks < 12) return;   // ~6s of nothing before calling it
            _watchSettled = true;
            OutdoorBedRestored = false;
            IcebreakerAcoustics.ReviveOurWindBed();
            Plugin.Log.LogError($"[Ambient] retail outdoor bed emitted SILENCE for 6s (peak {peak:F5}) despite playing/unmuted/volume>0 "
                                + "— handing the deck back to our own wind bed");
        }

        // THE ROOM-TONE TRANSITION. binding a clip to all 154 SpatialAudioRooms only gets
        // you halfway: the thing that actually starts and stops them is GClass1185, which
        // subscribes to the spatial system's room-changed event and, on each move,
        //   - fades the previous room's tone out over its FadeOutSeconds
        //   - calls PlayRoomToneSound on the new one (RoomToneVolume, FadeInSeconds)
        //   - stops the tone entirely when the new room is outdoor
        // it skips rooms whose RoomToneClipHash is -1, which is every room until the clips
        // were bound — so the binding and this are useless apart.
        //
        // retail constructs it in AmbientAudioSystem.method_9. that never runs here (the
        // system logs neither its success nor its failure line), so we construct it. it
        // needs only the SpatialAudioSystem singleton and the global event bus, both of
        // which are live by the time this runs.
        private static IDisposable _roomToneHandler;

        private static void StartRoomToneTransitions()
        {
            try
            {
                // dispose the previous raid's before making another, or both would answer
                // the same room-changed event and stack tones
                if (_roomToneHandler != null)
                {
                    try { _roomToneHandler.Dispose(); } catch { }
                    _roomToneHandler = null;
                }
                var t = ResolveType("GClass1185");
                if (t == null) { Plugin.Log.LogWarning("[Ambient] room-tone handler type not found — indoor tones stay silent"); return; }
                _roomToneHandler = Activator.CreateInstance(t) as IDisposable;
                Plugin.Log.LogWarning(_roomToneHandler != null
                    ? "[Ambient] room-tone transitions armed — indoor tones now fade in/out per room"
                    : "[Ambient] room-tone handler built but not IDisposable — leaving it running");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Ambient] room-tone transitions failed: {e.Message} — indoor tones stay silent");
            }
        }

        // THE OUTDOOR BED. DayTimeAmbientBlender owns two AudioSources (DaySource /
        // NightSource) and crossfades them on the hour; Init() pulls the season data off
        // the AmbientAudioSystem singleton and SetSeasonStatus() assigns the clips and
        // hits Play on both. BSG's own init normally does this — on this map it never
        // logs either success or failure, so its higher-level bootstrap isn't reaching
        // that code and we call the two methods ourselves.
        private static void StartOutdoorBed(List<KeyValuePair<JObject, Component>> built)
        {
            try
            {
                Component blender = null;
                foreach (var kv in built)
                    if (kv.Value != null && kv.Value.GetType().Name == "DayTimeAmbientBlender") { blender = kv.Value; break; }
                if (blender == null) return;

                var t = blender.GetType();
                AccessTools.Method(t, "Init")?.Invoke(blender, null);

                var setSeason = AccessTools.Method(t, "SetSeasonStatus");
                var seasonEnum = ResolveType("ESeasonStatus");
                if (setSeason == null || seasonEnum == null) return;
                // any value works — the rebuilt data files the same container under every
                // season — but ask for the one the map actually forces
                object season = null;
                foreach (var v in Enum.GetValues(seasonEnum))
                    if (v.ToString() == "Winter") { season = v; break; }
                if (season == null) season = Enum.GetValues(seasonEnum).GetValue(0);
                setSeason.Invoke(blender, new[] { season });

                var day = Get<AudioSource>(blender, t, "_outdoorAmbientDaySource", null);
                var night = Get<AudioSource>(blender, t, "_outdoorAmbientNightSource", null);
                bool playing = (day != null && day.isPlaying) || (night != null && night.isPlaying);
                // NOT the stand-down signal — see the audibility test after the configure
                // block. isPlaying is true for a MUTED source (this file documents that trap
                // twenty lines down), and standing our own bed down on it is how the deck
                // ends up in total silence: retail's bed inaudible, ours already retired.

                // UNROUTE THE MIXER. these two sources are wired to a group inside the
                // mixer asset that came out of the rip, but the day/night crossfade
                // (method_4) sets its levels on BetterAudio.Instance.Master — the GAME's
                // mixer, a different object entirely. so nothing ever raises our groups and
                // the bed plays into a muted bus: "virtually no outdoor wind" (07-30).
                // going direct costs the mixer's effect sends, which aren't configured here
                // anyway, and hands volume to the ducker below where we can reason about it.
                //
                // with the mixer bypassed both sources would sound at once, so only the one
                // matching the pinned time of day is voiced. icebreaker sits at hour 23
                // against a 5.15-21.15 day range, i.e. night.
                if (playing)
                {
                    var range = Get<Vector2>(blender, t, "_dayTimeRange", new Vector2(5f, 21f));
                    float hour = Plugin.TodHour.Value;
                    bool isDay = hour >= Mathf.Min(range.x, range.y) && hour <= Mathf.Max(range.x, range.y);
                    // MUTED IN THE SCENE — both ship with Mute: 1, which is why the bed was
                    // dead silent while isPlaying still reported true (a muted source counts
                    // as playing). retail presumably unmutes through the mixer path we just
                    // bypassed. and 2D: this is a global weather bed, not a point source —
                    // it sits at origin with a 500m rolloff otherwise.
                    foreach (var s in new[] { day, night })
                    {
                        if (s == null) continue;
                        s.mute = false;
                        s.spatialBlend = 0f;
                        s.outputAudioMixerGroup = null;
                        s.loop = true;
                    }
                    if (day != null) day.volume = isDay ? 1f : 0f;
                    if (night != null) night.volume = isDay ? 0f : 1f;

                    // INDOOR DUCKING is ours too: retail attenuates the outdoor bed per room
                    // (RoomAmbientData.OutdoorAmbientVolume + a high-cut) inside
                    // EnvironmentSoundBlendSystem, one of the three drifted classes we can't
                    // restore, so nothing else would touch these sources indoors.
                    OutdoorDuck.Arm(day, night);
                }

                // OUR WIND BED CARRIES THE DECK. FULL STOP. (user call, 07-31)
                //
                // retail's bed was chased through four rounds — isPlaying, then
                // unmuted+volume+direct, then clip load state, then an actual output-sample
                // watchdog. the watchdog finally answered it: peak 0.0548, i.e. the bed is
                // genuinely playing real samples about 25dB down. it was never silent, it is
                // simply far too quiet to hear, so every "is it audible" test was asking the
                // wrong question and every one of them handed the deck to a bed nobody could
                // make out. meanwhile ours was audible the entire time and we kept retiring it.
                //
                // so stop arbitrating. ours plays, retail's stays muted, and OutdoorBedRestored
                // is pinned false so KeepAmbientAlive never stands ours down. if retail's bed is
                // ever worth reviving, the open question is its GAIN, not its aliveness.
                foreach (var s in new[] { day, night })
                {
                    if (s == null) continue;
                    s.mute = true;
                    s.volume = 0f;
                }
                OutdoorBedRestored = false;
                _watchSettled = true;   // nothing left to watch
                Plugin.Log.LogWarning("[Ambient] retail outdoor bed left MUTED (measured ~25dB down, inaudible) — our wind bed carries outdoor ambience");
                Plugin.Log.LogWarning($"[Ambient] outdoor bed via DayTimeAmbientBlender: " +
                                      $"day={(day != null ? (day.clip != null ? day.clip.name : "no clip") : "no source")} " +
                                      $"night={(night != null ? (night.clip != null ? night.clip.name : "no clip") : "no source")} " +
                                      $"playing={playing} — our wind bed STAYS UP (retail's muted by policy)");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Ambient] outdoor bed failed: {e.Message}"); }
        }

        // MIXER PARITY, second attempt. the first routed 8 sources onto the game master's
        // AmbientOut*/Rain groups on the assumption BSG's code drives those faders — but
        // on this map the native AmbientAudioSystem init never runs, NOBODY drives them,
        // and their resting level is muted: one raid of total outdoor silence (07-30,
        // "dead silent when I spawned"). same dead-bus trap as the ripped mixer, one
        // level up. the rule now: NEVER route a source into a fader we don't drive.
        //   - day/night bed: route ONLY if the mixer param name data (GClass1174) is
        //     available, and then SET the faders ourselves — exactly what the blender's
        //     method_4 would do — for the pinned time of day. else stay direct.
        //   - wind/precipitation sources -> Rain group: dropped entirely. those blenders
        //     are dormant here and nothing on this map drives the Rain fader.
        private static void RouteMixerGroups()
        {
            try
            {
                UnityEngine.Audio.AudioMixer master = null;
                try { var ba = MonoBehaviourSingleton<BetterAudio>.Instance; if (ba != null) master = ba.Master; }
                catch { }
                GClass1174 names;
                if (master == null || !GClass3670.TryGetData<GClass1174>(out names) || names == null)
                {
                    Plugin.Log.LogWarning("[Ambient] master/param-name data unavailable — outdoor bed stays direct (audible either way)");
                    return;
                }

                UnityEngine.Audio.AudioMixerGroup Find(string n)
                {
                    foreach (var g2 in master.FindMatchingGroups(n))
                        if (g2 != null && g2.name == n) return g2;
                    return null;
                }
                var dayG = Find("AmbientOutDay");
                var nightG = Find("AmbientOutNight");
                if (dayG == null || nightG == null)
                {
                    Plugin.Log.LogWarning("[Ambient] AmbientOutDay/Night groups not on the master — outdoor bed stays direct");
                    return;
                }

                // drive the faders BEFORE routing anything into them. blend 0 = day.
                float blend = 1f;   // icebreaker is pinned night (TodHour 23 vs 5.15-21.15 day range)
                float dayDb = GClass2313.ConvertNormalizedVolumeToDB(1f - blend);
                float nightDb = GClass2313.ConvertNormalizedVolumeToDB(blend);
                master.SetFloat(names.AmbientOutDayMixerVolume, dayDb);
                master.SetFloat(names.AmbientOutNightMixerVolume, nightDb);
                master.SetFloat(names.AmbientOutDayEffectsVolume, dayDb);
                master.SetFloat(names.AmbientOutNightEffectsVolume, nightDb);

                // THIRD attempt, and the routing itself is what goes. RestoreOutdoorBed
                // deliberately sets outputAudioMixerGroup = null on these exact two sources
                // (the 07-30 dead-bus fix) and hands volume to the direct path + OutdoorDuck.
                // this function ran AFTERWARDS and put them straight back on the bus, quietly
                // undoing it — fika headless raid 07-31, log line 1189 unroutes then 1191
                // re-routes, and the deck was silent. driving AmbientOutNight to 0dB isnt
                // enough on its own either: nothing on this map raises the PARENT groups
                // above it, because the native AmbientAudioSystem init never runs here.
                // the faders are still set below in case anything else lands on them, but
                // the bed stays DIRECT — which both of the older comments already call the
                // audible configuration.
                foreach (var path in new[] { "AmbientAudioSystem/Subsystems/DayTimeSoundBlender/DaySource",
                                             "AmbientAudioSystem/Subsystems/DayTimeSoundBlender/NightSource" })
                {
                    if (!_byPath.TryGetValue(path, out var l) || l.Count == 0) continue;
                    var src = l[0] != null ? l[0].GetComponent<AudioSource>() : null;
                    if (src != null) src.outputAudioMixerGroup = null;
                }
                Plugin.Log.LogWarning($"[Ambient] day/night faders set (day={dayDb:F0}dB night={nightDb:F0}dB) "
                                      + "but the bed stays DIRECT — routing it onto the master bus silences it");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Ambient] mixer routing failed: {e.Message} — sources stay direct"); }
        }

        // ENVIRONMENT CROSSFADE. retail keeps its 2D global beds in two groups —
        // AmbientPlayersController/2DRP/outdor (wind gusts) and .../2DRP/Indoor (ship
        // moans, wind howl) — and crossfades between them through "AmbientIn"/"AmbientOut"
        // mixer buses. those buses are part of the mixer layer we can't restore, so with
        // every player force-started they ALL sound everywhere: outdoor gusts indoors,
        // indoor howl on deck. this does the crossfade the buses would have.
        //
        // the outdoor bed rides the same ticker, held at WindIndoorFraction inside rather
        // than cut, since a completely sealed ship reads wrong through steel.
        private sealed class OutdoorDuck : MonoBehaviour
        {
            private struct Entry { public AudioSource Src; public float Base; public bool Indoor; }
            private readonly List<Entry> _entries = new List<Entry>();
            private static OutdoorDuck _live;

            private static OutdoorDuck Live()
            {
                if (_live == null)
                {
                    var go = new GameObject("Icebreaker_AmbienceEnvFade");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    _live = go.AddComponent<OutdoorDuck>();
                }
                return _live;
            }

            internal static void Reset()
            {
                if (_live != null) { UnityEngine.Object.Destroy(_live.gameObject); _live = null; }
            }

            // the outdoor bed: two sources, whichever the time of day voiced
            internal static void Arm(AudioSource day, AudioSource night)
            {
                var d = Live();
                // capture whatever the blender/our voicing left as the outdoor ceiling
                // rather than assuming 1.0
                if (day != null) d._entries.Add(new Entry { Src = day, Base = day.volume, Indoor = false });
                if (night != null) d._entries.Add(new Entry { Src = night, Base = night.volume, Indoor = false });
            }

            // a 2D group player — indoor:true plays inside and fades out on deck
            internal static void Add(AudioSource src, float baseVol, bool indoor)
            {
                if (src == null) return;
                Live()._entries.Add(new Entry { Src = src, Base = baseVol, Indoor = indoor });
            }

            private void Update()
            {
                if (_entries.Count == 0) return;
                bool indoor = false;
                try
                {
                    var em = EFT.EnvironmentEffect.EnvironmentManager.Instance;
                    if (em != null) indoor = em.Environment == EnvironmentType.Indoor;
                }
                catch { }
                float outside = indoor ? Mathf.Clamp01(Plugin.WindIndoorFraction.Value) : 1f;
                float step = Time.deltaTime / 1.5f;
                foreach (var e in _entries)
                {
                    if (e.Src == null) continue;
                    // indoor-group players are the mirror image: full inside, gone outside
                    float target = e.Base * (e.Indoor ? (indoor ? 1f : 0f) : outside);
                    e.Src.volume = Mathf.MoveTowards(e.Src.volume, target, step * Mathf.Max(e.Base, 0.2f));
                }
            }
        }

        // a random player only sounds if it has a bank AND something calls Play() — retail
        // relies on the point managers for the second half, which is the part that doesn't
        // engage on a backported location.
        private static bool StartRandomPlayer(Component player)
        {
            try
            {
                var t = player.GetType();
                var bank = Get<UnityEngine.Object>(player, t, "_ambientBank", null);
                if (bank == null) return false;              // no bank = nothing to pick from
                float taste = TasteScale(bank.name);
                ScaleAuthoredVolume(player, t, taste);
                // the source too — the 2D env-gate captures src.volume as its base right
                // after this returns, and BSG's fader drives toward the (scaled) _volume;
                // both must see the same number or they tug the volume in a loop
                if (taste < 1f)
                {
                    var s = player.GetComponent<AudioSource>();
                    if (s != null) s.volume *= taste;
                }
                var play = AccessTools.Method(t, "Play");
                if (play == null) return false;
                play.Invoke(player, null);
                return true;
            }
            catch { return false; }
        }

        private static T Get<T>(object target, Type type, string field, T fallback)
        {
            var f = FindField(type, field);
            if (f == null) return fallback;
            var v = f.GetValue(target);
            return v is T typed ? typed : fallback;
        }

        // by class name, before anything is instantiated — used to order pass 1
        private static bool IsPlayerRecord(JToken rec)
        {
            var cls = rec?.Value<string>("cls");
            return cls != null && cls.EndsWith("AmbientSoundPlayer", StringComparison.Ordinal);
        }

        private static bool IsPlayer(Component c)
        {
            if (c == null) return false;
            for (var t = c.GetType(); t != null; t = t.BaseType)
                if (t.Name == "BaseAmbientSoundPlayer") return true;
            return false;
        }

        // ---- field pouring --------------------------------------------------------

        private static readonly HashSet<string> _missingClips = new HashSet<string>();
        private static readonly HashSet<string> _missingBanks = new HashSet<string>();

        private static FieldInfo FindField(Type type, string name)
        {
            // private serialized fields live all over the hierarchy (_volume is on
            // BaseAmbientSoundPlayer, _loopClip on the leaf), so climb it by hand
            for (var t = type; t != null; t = t.BaseType)
            {
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public |
                                         BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            return null;
        }

        private static bool SetField(object target, string name, JToken value)
        {
            var f = FindField(target.GetType(), name);
            if (f == null) return false;
            var converted = Convert(value, f.FieldType);
            if (converted == null && f.FieldType.IsValueType) return false;
            f.SetValue(target, converted);
            return true;
        }

        private static object Convert(JToken tok, Type type)
        {
            if (tok == null || tok.Type == JTokenType.Null) return null;

            if (type == typeof(float)) return tok.Value<float>();
            if (type == typeof(double)) return tok.Value<double>();
            if (type == typeof(int)) return tok.Value<int>();
            if (type == typeof(long)) return tok.Value<long>();
            if (type == typeof(byte)) return (byte)tok.Value<int>();
            if (type == typeof(bool)) return tok.Type == JTokenType.Boolean ? tok.Value<bool>() : tok.Value<int>() != 0;
            if (type == typeof(string)) return tok.Value<string>();
            if (type.IsEnum) return Enum.ToObject(type, tok.Value<int>());

            var obj = tok as JObject;

            if (type == typeof(Vector2) && obj != null)
                return new Vector2(F(obj, "x"), F(obj, "y"));
            if (type == typeof(Vector3) && obj != null)
                return new Vector3(F(obj, "x"), F(obj, "y"), F(obj, "z"));
            if (type == typeof(Vector4) && obj != null)
                return new Vector4(F(obj, "x"), F(obj, "y"), F(obj, "z"), F(obj, "w"));
            if (type == typeof(Quaternion) && obj != null)
                return new Quaternion(F(obj, "x"), F(obj, "y"), F(obj, "z"), F(obj, "w"));
            if (type == typeof(Color) && obj != null)
                return new Color(F(obj, "r"), F(obj, "g"), F(obj, "b"), F(obj, "a"));
            if (type == typeof(AnimationCurve) && obj != null)
                return BuildCurve(obj);

            // unity object references, written by the extractor as one-key tags
            if (typeof(UnityEngine.Object).IsAssignableFrom(type) && obj != null)
                return ResolveReference(obj, type);

            // arrays / lists
            if (tok is JArray arr)
            {
                if (type.IsArray)
                {
                    var elem = type.GetElementType();
                    var a = Array.CreateInstance(elem, arr.Count);
                    for (int i = 0; i < arr.Count; i++) a.SetValue(Convert(arr[i], elem), i);
                    return a;
                }
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
                {
                    var elem = type.GetGenericArguments()[0];
                    var list = (System.Collections.IList)Activator.CreateInstance(type);
                    foreach (var item in arr) list.Add(Convert(item, elem));
                    return list;
                }
                return null;
            }

            // a nested serializable struct/class — recurse field by field
            if (obj != null && !type.IsPrimitive)
            {
                object inst;
                try { inst = Activator.CreateInstance(type); }
                catch { return null; }
                foreach (var p in obj)
                {
                    try { SetField(inst, p.Key, p.Value); } catch { }
                }
                return inst;
            }
            return null;
        }

        private static float F(JObject o, string k)
        {
            var t = o[k];
            return t == null || t.Type == JTokenType.Null ? 0f : t.Value<float>();
        }

        private static AnimationCurve BuildCurve(JObject obj)
        {
            var keysTok = obj["m_Curve"] as JArray;
            if (keysTok == null) return new AnimationCurve();
            var keys = new Keyframe[keysTok.Count];
            for (int i = 0; i < keysTok.Count; i++)
            {
                var k = (JObject)keysTok[i];
                var kf = new Keyframe(F(k, "time"), F(k, "value"), F(k, "inSlope"), F(k, "outSlope"));
                // weighted tangents exist on these curves (the rolloffs are authored) —
                // dropping them flattens the falloff BSG tuned
                kf.weightedMode = (WeightedMode)(k["weightedMode"]?.Value<int>() ?? 0);
                kf.inWeight = F(k, "inWeight");
                kf.outWeight = F(k, "outWeight");
                keys[i] = kf;
            }
            var curve = new AnimationCurve(keys);
            // DO NOT restore m_PreInfinity/m_PostInfinity. unity serialises those as 2
            // (WrapMode.Loop) by default, and on an audio ROLLOFF curve that is poison:
            // the curve falls to 0 at max distance and then wraps straight back to full
            // volume, so every 5m drip loop is heard across the whole ship at full
            // loudness (07-30 — this is what "everything sounds like it's on top of me"
            // actually was). clamped is the only sane reading for a falloff.
            curve.preWrapMode = WrapMode.ClampForever;
            curve.postWrapMode = WrapMode.ClampForever;
            return curve;
        }

        private static UnityEngine.Object ResolveReference(JObject obj, Type wanted)
        {
            // clips: matched by NAME against everything loaded — the scene wiring put all
            // 14 of them in the bundle, so they're here by the time this runs
            var clip = obj["$AudioClip"]?.Value<string>();
            if (clip != null)
            {
                if (_clips.TryGetValue(clip, out var c)) return c;
                _missingClips.Add(clip);
                return null;
            }

            var mixer = obj["$AudioMixerGroupController"]?.Value<string>();
            if (mixer != null) return _mixers.TryGetValue(mixer, out var m) ? m : null;

            // "$asset:SoundBank", "$asset:SeasonAmbientSoundDataSO", ...
            foreach (var p in obj)
            {
                if (!p.Key.StartsWith("$asset:", StringComparison.Ordinal)) continue;
                var name = p.Value?.Value<string>();
                if (string.IsNullOrEmpty(name)) return null;
                if (_banks.TryGetValue(name, out var bank) && wanted.IsInstanceOfType(bank)) return bank;
                if (p.Key == "$asset:SeasonAmbientSoundDataSO") return BuildSeasonData(wanted);
                // a missing BANK just means that player is quiet
                if (p.Key == "$asset:SoundBank") _missingBanks.Add(name);
                return null;
            }

            // a plain unity component on another object (the day/night ambient AudioSources
            // the blender crossfades between, mostly) — resolved by the object it lives on
            var compPath = obj["$comp"]?.Value<string>();
            if (compPath != null)
            {
                if (!_byPath.TryGetValue(compPath, out var cl) || cl.Count == 0) return null;
                foreach (var tr in cl)
                {
                    var c = tr.GetComponent(wanted);
                    if (c != null) return c;
                }
                return null;
            }

            // a component on another object in the hierarchy (players <-> groups, observers
            // <-> rooms). pass 1 guarantees the target component already exists.
            var refPath = obj["$ref"]?.Value<string>();
            if (refPath != null)
            {
                if (!_byPath.TryGetValue(refPath, out var list) || list.Count == 0) return null;
                foreach (var t in list)
                {
                    var c = t.GetComponent(wanted);
                    if (c != null) return c;
                }
                return null;
            }

            var goPath = obj["$go"]?.Value<string>();
            if (goPath != null && _byPath.TryGetValue(goPath, out var gl) && gl.Count > 0)
            {
                if (wanted == typeof(GameObject)) return gl[0].gameObject;
                if (typeof(Component).IsAssignableFrom(wanted)) return gl[0].GetComponent(wanted);
            }
            return null;
        }
    }
}
