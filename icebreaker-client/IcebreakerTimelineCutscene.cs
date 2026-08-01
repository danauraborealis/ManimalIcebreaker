using System;
using System.Collections;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;

namespace Manimal.Icebreaker
{
    // the REAL in-engine BD-infiltration cutscene: retail 1.0's Icebreaker_cutscene_01
    // scene (helicopter insertion, rebuilt timeline via SDK Author 17) played at the
    // existing story-beat trigger, replacing the fullscreen video placeholder.
    //
    // how it renders: the timeline animates the scene's own camera rig
    // (CutsceneRoot/CutsceneCamera/CameraAddEffect/Camera — retail reparented the real
    // camera under it, per CutsceneActionCameraToTimelineRoot). we do it without
    // touching the hierarchy: Camera.onPreCull fires AFTER every LateUpdate writer
    // (EFT's camera controller included), so copying pose+FOV there gives the rig the
    // last word while the real camera keeps its full post stack (tonemap, our volfog).
    //
    // retail CutsceneObjects for this scene (recovered from level709): activate
    // CutsceneRoot/Canvas on start, deactivate on end — that's the whole list.
    public class IcebreakerTimelineCutscene : MonoBehaviour
    {
        private const string SceneName = "Icebreaker_cutscene_01";
        private const float FadeDur = 0.7f;
        // fade out at 28.5s (frame 1710 of the 60fps timeline) — trims the dead tail
        // without clipping the story beat (27.5 cut too early)
        private const double EndAt = 28.5;

        // the helipad crew stand posed on the pad for the whole scene, and the early
        // wide shots fly right past them, so they read as frozen mannequins in the
        // background. keep the group out of the scene until the shot that is actually
        // meant to find them. same 60fps timeline as EndAt, so frame 1124.
        private const string HelipadGroup = "HelipadCenter";
        private const double HelipadRevealAt = 1124.0 / 60.0;

        public static bool Available
        {
            get
            {
                try { return Application.CanStreamedLevelBeLoaded(SceneName); }
                catch { return false; }
            }
        }

        public static void Play()
        {
            var go = new GameObject("Icebreaker_TimelineCutscene");
            go.AddComponent<IcebreakerTimelineCutscene>();
        }

        private Scene _scene;
        private bool _sceneLoaded;
        private PlayableDirector _director;
        private Camera _rigCam;            // the scene's animated camera (disabled, pose source)
        private Camera _realCam;
        private GameObject _canvas;        // CutsceneRoot/Canvas — retail toggles exactly this
        private GameObject _helipad;       // HelipadCenter, withheld until its shot
        private bool _helipadWasActive = true;
        private bool _driving;
        private bool _inputLocked;
        private readonly List<Canvas> _hiddenCanvases = new List<Canvas>();
        private readonly List<Renderer> _hiddenRenderers = new List<Renderer>();
        private readonly List<Behaviour> _pausedFx = new List<Behaviour>();
        private readonly List<Renderer> _indoorOff = new List<Renderer>();
        private float _fade;
        private Texture2D _black;
        private bool _restored;
        private float _savedFov = -1f;     // DriveCamera stomps fov — restore on exit

        private void Start() => StartCoroutine(Run());

        private IEnumerator Run()
        {
            GamePlayerOwner.SetIgnoreInputInNPCDialog(true);
            _inputLocked = true;
            yield return Fade(0f, 1f);

            var load = SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Additive);
            if (load == null) { Bail("LoadSceneAsync returned null"); yield break; }
            while (!load.isDone) yield return null;
            _scene = SceneManager.GetSceneByName(SceneName);
            _sceneLoaded = _scene.IsValid() && _scene.isLoaded;
            if (!_sceneLoaded) { Bail("cutscene scene failed to load"); yield break; }

            // the raid-start pass deferred this scene's 3 volumetric beams (the BD heli's
            // main spot among them) because the scene wasn't loaded yet — claim them now
            try { IcebreakerVolumetricLights.Restore(); }
            catch (System.Exception e) { Plugin.Log.LogWarning($"[Volumetric] cutscene pass failed: {e.Message}"); }

            // locate the rig inside the loaded scene
            GameObject cutsceneRoot = null;
            foreach (var rgo in _scene.GetRootGameObjects())
                if (rgo.name == "CutsceneRoot") { cutsceneRoot = rgo; break; }
            if (cutsceneRoot == null) { Bail("no CutsceneRoot in cutscene scene"); yield break; }

            _director = cutsceneRoot.GetComponentInChildren<PlayableDirector>(true);
            if (_director == null || _director.playableAsset == null)
            { Bail("TimelineDirector missing or playableAsset empty (Author 17 not run?)"); yield break; }

            // the scene arrived long after the raid-start shader rebinds — its materials
            // still hold the bundle's broken smap copies (white in deferred). rebind now.
            try { RenderEnvProbe.RebindNow(); } catch (Exception e)
            { Plugin.Log.LogWarning($"[TimelineCutscene] shader rebind failed: {e.Message}"); }

            // the light/distance cullers key off camera position — wide helicopter shots
            // would cull every deck lamp (>80m) and pop props. hold them + force all on
            // for the duration (under the fade); Restore() releases and they re-cull.
            try { RenderEnvProbe.CutsceneHold = true; RenderEnvProbe.CutsceneShowAll(); }
            catch (Exception e) { Plugin.Log.LogWarning($"[TimelineCutscene] culler hold failed: {e.Message}"); }

            // the cutscene never looks inside — skip draw submission for the whole
            // interior via forceRenderingOff: a flag the PC/distance cullers never write
            // (no state fights), and unlike SetActive it leaves colliders/audio/AI alive
            // (the BD squad is spawning below decks right now)
            try
            {
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scn = SceneManager.GetSceneAt(i);
                    if (!scn.isLoaded || scn.name == null || !scn.name.StartsWith("Icebreaker_Indoor")) continue;
                    foreach (var rgo in scn.GetRootGameObjects())
                        foreach (var r in rgo.GetComponentsInChildren<Renderer>(true))
                            if (!r.forceRenderingOff) { r.forceRenderingOff = true; _indoorOff.Add(r); }
                }
                Plugin.Log.LogWarning($"[TimelineCutscene] interior draw-off: {_indoorOff.Count} renderers");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[TimelineCutscene] interior cull failed: {e.Message}"); }

            var camTf = cutsceneRoot.transform.Find("CutsceneCamera/CameraAddEffect/Camera");
            _rigCam = camTf != null ? camTf.GetComponent<Camera>()
                                    : cutsceneRoot.GetComponentInChildren<Camera>(true);
            if (_rigCam == null) { Bail("no camera in cutscene rig"); yield break; }
            // pose/FOV source only — must never render or listen
            _rigCam.enabled = false;
            var lst = _rigCam.GetComponent<AudioListener>();
            if (lst != null) lst.enabled = false;

            _realCam = CameraClass.Instance?.Camera;
            if (_realCam == null) _realCam = Camera.main;
            if (_realCam == null) { Bail("no main camera"); yield break; }

            // pull the helipad group before the first frame is ever drawn
            _helipad = FindInScene(_scene, HelipadGroup);
            if (_helipad != null)
            {
                _helipadWasActive = _helipad.activeSelf;
                _helipad.SetActive(false);
                Plugin.Log.LogInfo($"[TimelineCutscene] '{HelipadGroup}' held back until {HelipadRevealAt:0.00}s");
            }
            else Plugin.Log.LogWarning($"[TimelineCutscene] no '{HelipadGroup}' in the cutscene scene");

            // retail CutsceneObjects.ApplyStart
            var canvasTf = cutsceneRoot.transform.Find("Canvas");
            _canvas = canvasTf != null ? canvasTf.gameObject : null;
            if (_canvas != null) _canvas.SetActive(true);

            // pause player screen effects (painkiller B&W, contusion vignette, double
            // vision...) — they're CC_* image effects on the real camera driven by
            // EffectsController, so they'd grade the cutscene too. disable the driver
            // first so it can't re-enable the CC components mid-playback. CC_Sharpen
            // stays: it hosts the weather-desat the cutscene fog profile tunes.
            foreach (var b in _realCam.GetComponents<Behaviour>())
                if (b != null && b.enabled && (b is EffectsController || b is CC_Base) && !(b is CC_Sharpen))
                {
                    b.enabled = false;
                    _pausedFx.Add(b);
                }

            // cutscene fog profile on — the F9 tuner now edits VolumetricFog2.Cutscene
            IcebreakerVolFog.CutsceneProfile = true;
            try { IcebreakerVolFog.Tick(); } catch { }

            // hide the player (body + hands + weapon) — the flying camera would
            // otherwise film the frozen scav standing at the trigger
            var player = Singleton<GameWorld>.Instance?.MainPlayer;
            if (player != null)
                foreach (var r in player.gameObject.GetComponentsInChildren<Renderer>(false))
                    if (r != null && r.enabled) { r.enabled = false; _hiddenRenderers.Add(r); }

            // hide the HUD, but never the cutscene's own canvas (fade/letterbox)
            foreach (var cv in UnityEngine.Object.FindObjectsOfType<Canvas>())
                if (cv != null && cv.enabled && cv.isRootCanvas
                    && cv.renderMode == RenderMode.ScreenSpaceOverlay
                    && cv.gameObject.scene != _scene)
                {
                    cv.enabled = false;
                    _hiddenCanvases.Add(cv);
                }

            _savedFov = _realCam.fieldOfView;
            Camera.onPreCull += DriveCamera;
            _driving = true;

            _director.extrapolationMode = DirectorWrapMode.Hold; // no loop surprises
            _director.Play();
            Plugin.Log.LogWarning($"[TimelineCutscene] playing '{_director.playableAsset.name}' " +
                                  $"({_director.duration:0.0}s) — SPACE skips");
            yield return Fade(1f, 0f);

            double dur = Math.Min(_director.duration, EndAt);
            float hardStop = Time.realtimeSinceStartup + (float)dur + 10f;
            bool paused = false;
            while (Time.realtimeSinceStartup < hardStop)
            {
                if (_director == null) break;
                if (!paused && _director.state != PlayState.Playing) break;
                if (!paused && _director.time >= dur - 0.05) break;

                // driven off director time, not wall clock, so pausing with P holds the
                // reveal too and a skip never strands the group hidden
                if (_helipad != null)
                {
                    bool due = _director.time >= HelipadRevealAt;
                    if (due != _helipad.activeSelf) _helipad.SetActive(due);
                }

                // P freezes the frame (director paused, world keeps rendering) — for
                // tuning the cutscene fog profile with F9 mid-shot
                if (Input.GetKeyDown(KeyCode.P))
                {
                    paused = !paused;
                    if (paused) _director.Pause(); else _director.Resume();
                    Plugin.Log.LogWarning($"[TimelineCutscene] {(paused ? "PAUSED (P resumes, F9 tunes)" : "resumed")}");
                }
                if (paused) hardStop += Time.unscaledDeltaTime; // clock stops with the frame
                if (Input.GetKeyDown(KeyCode.Space))
                {
                    Plugin.Log.LogWarning("[TimelineCutscene] skipped");
                    break;
                }
                yield return null;
            }

            yield return Fade(0f, 1f);
            Restore();
            if (_sceneLoaded)
            {
                var unload = SceneManager.UnloadSceneAsync(_scene);
                while (unload != null && !unload.isDone) yield return null;
                _sceneLoaded = false;
            }
            // linger at black, then a slow reveal: the cullers re-settle for the returned
            // camera during these frames (PC driver drains budgeted toggles) — fading in
            // immediately showed the whole re-cull as visible pop-in
            yield return new WaitForSecondsRealtime(0.8f);
            yield return Fade(1f, 0f, 1.6f);
            Destroy(gameObject);
        }

        // by name anywhere in the loaded scene, inactive included — the group sits under
        // the scene roots rather than the cutscene rig
        private static GameObject FindInScene(Scene scn, string name)
        {
            if (!scn.IsValid() || !scn.isLoaded) return null;
            foreach (var rgo in scn.GetRootGameObjects())
            {
                if (rgo.name == name) return rgo;
                foreach (var t in rgo.GetComponentsInChildren<Transform>(true))
                    if (t != null && t.name == name) return t.gameObject;
            }
            return null;
        }

        private void DriveCamera(Camera cam)
        {
            if (cam != _realCam || _rigCam == null) return;
            var t = _rigCam.transform;
            cam.transform.SetPositionAndRotation(t.position, t.rotation);
            cam.fieldOfView = _rigCam.fieldOfView;
        }

        private void Bail(string why)
        {
            // no video fallback anymore (mp4 removed 07-30) — a broken timeline just
            // skips the cinematic; the story beat already fired from the watcher
            Plugin.Log.LogWarning($"[TimelineCutscene] {why} — cutscene skipped");
            Restore();
            if (_sceneLoaded) { SceneManager.UnloadSceneAsync(_scene); _sceneLoaded = false; }
            Destroy(gameObject);
        }

        // idempotent — scripted exit + OnDestroy teardown net
        private void Restore()
        {
            if (_restored) return;
            _restored = true;
            if (_driving) { Camera.onPreCull -= DriveCamera; _driving = false; }
            try { RenderEnvProbe.CutsceneRelease(); } catch { }
            if (_realCam != null && _savedFov > 0f) _realCam.fieldOfView = _savedFov;
            if (_director != null && _director.state == PlayState.Playing) _director.Stop();
            if (_canvas != null) _canvas.SetActive(false); // retail ApplyEnd
            // hand the group back as we found it — the scene unloads right after, but a
            // bail can happen before that and must not leave the map short a helipad crew
            if (_helipad != null) { _helipad.SetActive(_helipadWasActive); _helipad = null; }
            foreach (var r in _hiddenRenderers) if (r != null) r.enabled = true;
            _hiddenRenderers.Clear();
            foreach (var b in _pausedFx) if (b != null) b.enabled = true;
            _pausedFx.Clear();
            IcebreakerVolFog.CutsceneProfile = false; // back to the raid fog look
            try { IcebreakerVolFog.Tick(); } catch { }
            foreach (var r in _indoorOff) if (r != null) r.forceRenderingOff = false;
            _indoorOff.Clear();
            foreach (var cv in _hiddenCanvases) if (cv != null) cv.enabled = true;
            _hiddenCanvases.Clear();
            if (_inputLocked) { GamePlayerOwner.SetIgnoreInputInNPCDialog(false); _inputLocked = false; }
        }

        private IEnumerator Fade(float from, float to, float dur = FadeDur)
        {
            float t = 0f;
            while (t < dur)
            {
                t += Time.unscaledDeltaTime;
                _fade = Mathf.Lerp(from, to, t / dur);
                yield return null;
            }
            _fade = to;
        }

        private void OnGUI()
        {
            if (_fade <= 0f) return;
            if (_black == null)
            {
                _black = new Texture2D(1, 1);
                _black.SetPixel(0, 0, Color.black);
                _black.Apply();
            }
            GUI.depth = -10000;
            GUI.color = new Color(0f, 0f, 0f, _fade);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _black);
            GUI.color = Color.white;
        }

        private void OnDestroy()
        {
            Restore();
            if (_sceneLoaded) { SceneManager.UnloadSceneAsync(_scene); _sceneLoaded = false; }
            if (_black != null) Destroy(_black);
        }
    }
}
