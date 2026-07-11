using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Video;
using EFT;
using SysIoPath = System.IO.Path;

namespace Manimal.Icebreaker
{
    // placeholder for the live BD-infiltration cutscene: fullscreen video played over the
    // FPS camera at the cutscene trigger. lock input via the game's own NPC-dialog freeze,
    // mute the world via AudioListener.pause (video audio opts out), release everything on
    // end or skip. real timeline cutscene can replace this later without touching callers.
    public class IcebreakerCutscene : MonoBehaviour
    {
        private static bool _played; // once per raid — Plugin resets on scene load

        public static string VideoPath =>
            SysIoPath.Combine(SysIoPath.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".",
                "cutscene", "icebreaker_intro.mp4");

        public static bool Available => System.IO.File.Exists(VideoPath);

        public static void ResetForRaid() => _played = false;

        public static void TryPlay()
        {
            if (_played || !Available) return;
            _played = true;
            var go = new GameObject("Icebreaker_Cutscene");
            go.AddComponent<IcebreakerCutscene>();
        }

        private VideoPlayer _vp;
        private AudioSource _audio;
        private bool _worldMuted;
        private bool _inputLocked;
        private Camera _overlayCam;        // our own clean camera — EFT's post stack never touches it
        private readonly List<Canvas> _hiddenCanvases = new List<Canvas>();
        private bool _ended;
        private RenderTexture _rt;         // video decodes here; OnGUI paints it
        private bool _showVideo;
        private float _fade;               // 0 = clear, 1 = black (IMGUI overlay)
        private Texture2D _black;
        private const float FadeDur = 0.7f;

        private void Start() => StartCoroutine(Run());

        private IEnumerator Run()
        {
            // render the video on OUR OWN camera, drawn after everything: the FPS
            // camera's post-processing stack (tonemap/CC/scattering) runs after the
            // near-plane blit and stomped the video to black when the world was culled.
            // a bare solid-black camera at high depth has no effects to fight.
            var camGo = new GameObject("Icebreaker_CutsceneCamera");
            camGo.transform.SetParent(transform, false);
            _overlayCam = camGo.AddComponent<Camera>();
            _overlayCam.depth = 200f;                    // above the FPS + UI cameras
            _overlayCam.clearFlags = CameraClearFlags.SolidColor;
            _overlayCam.backgroundColor = Color.black;
            _overlayCam.cullingMask = 0;                 // draws nothing but the video blit
            _overlayCam.useOcclusionCulling = false;
            _overlayCam.allowHDR = false;
            _overlayCam.allowMSAA = false;
            _overlayCam.enabled = false;                 // armed under the fade

            _vp = gameObject.AddComponent<VideoPlayer>();
            _audio = gameObject.AddComponent<AudioSource>();
            _audio.ignoreListenerPause = true;   // survives the world mute below
            _audio.ignoreListenerVolume = true;
            _audio.spatialBlend = 0f;
            // the half-way audio cutout: EFT's audio engine stole this voice when the BD
            // squad's spawn burst landed (default priority 128 = fair game). 0 = highest,
            // never stolen. bypass flags keep mixer snapshots/reverb out of it too.
            _audio.priority = 0;
            _audio.bypassEffects = true;
            _audio.bypassListenerEffects = true;
            _audio.bypassReverbZones = true;
            _audio.volume = TargetVolume();

            _vp.playOnAwake = false;
            // near-plane blit rendered BLACK even on our clean overlay camera (audio ran,
            // no image — EFT's build eats the internal camera blit). decode into a
            // RenderTexture instead and draw it from OnGUI: IMGUI paints above every
            // camera and canvas, same layer the fade already uses. RT is created after
            // Prepare when the clip's native size is known.
            _vp.renderMode = VideoRenderMode.RenderTexture;
            // AudioSource (not Direct) so the player can dial CutsceneVolume live — the
            // hardening above covers why this route went silent mid-video once before
            _vp.audioOutputMode = VideoAudioOutputMode.AudioSource;
            // track must be explicitly enabled BEFORE Prepare or WMF decodes video-only
            // (the silent-cutscene bug)
            _vp.controlledAudioTrackCount = 1;
            _vp.EnableAudioTrack(0, true);
            _vp.SetTargetAudioSource(0, _audio);
            _vp.url = VideoPath;
            _vp.loopPointReached += _ => _ended = true; // isPlaying dips on buffer stalls — not an end signal

            _vp.Prepare();
            float deadline = Time.realtimeSinceStartup + 10f;
            while (!_vp.isPrepared && Time.realtimeSinceStartup < deadline) yield return null;
            if (!_vp.isPrepared)
            {
                Plugin.Log.LogWarning("[Cutscene] video prepare timed out — skipped (codec? H.264 mp4 expected)");
                Destroy(gameObject);
                yield break;
            }

            _rt = new RenderTexture((int)_vp.width, (int)_vp.height, 0);
            _rt.Create();
            _vp.targetTexture = _rt;

            // cinematic entry: lock input, fade the WORLD to black, then swap to the
            // video under the black and fade back in
            GamePlayerOwner.SetIgnoreInputInNPCDialog(true);
            _inputLocked = true;
            yield return Fade(0f, 1f);

            AudioListener.pause = true;
            _worldMuted = true;
            _overlayCam.enabled = true; // covers the whole screen incl. viewmodel/world

            // screen-space-overlay canvases draw ABOVE all cameras — the HUD (and mod
            // HUDs like the compass) must be disabled explicitly. GameUI alone wasn't it.
            foreach (var cv in UnityEngine.Object.FindObjectsOfType<Canvas>())
                if (cv != null && cv.enabled && cv.isRootCanvas && cv.renderMode == RenderMode.ScreenSpaceOverlay)
                {
                    cv.enabled = false;
                    _hiddenCanvases.Add(cv);
                }
            Plugin.Log.LogInfo($"[Cutscene] hid {_hiddenCanvases.Count} overlay canvases");

            Plugin.Log.LogWarning($"[Cutscene] playing ({_vp.length:0}s, {_vp.width}x{_vp.height}, audioTracks={_vp.audioTrackCount}) — SPACE skips");
            _vp.Play();
            _showVideo = true;
            yield return Fade(1f, 0f); // reveal the video

            // end = loopPointReached (or skip, or a hard timeout backstop) — polling
            // isPlaying ended the video early on every buffering hiccup
            float hardStop = Time.realtimeSinceStartup + (float)_vp.length + 15f;
            while (!_ended && Time.realtimeSinceStartup < hardStop)
            {
                if (_audio != null) _audio.volume = TargetVolume(); // live: game sliders + F12 trim
                if (Input.GetKeyDown(KeyCode.Space))
                {
                    Plugin.Log.LogWarning("[Cutscene] skipped");
                    break;
                }
                yield return null;
            }

            // cinematic exit: black over the video, restore the world underneath, reveal
            yield return Fade(0f, 1f);
            RestoreWorld();
            yield return Fade(1f, 0f);

            Destroy(gameObject); // OnDestroy re-runs restore harmlessly — covers teardown mid-video
        }

        // follow the game's Overall volume slider — EFT has no dedicated SFX slider
        // (only Overall/Interface/Chat/Music/Hideout/VoiceChat) and Overall is what
        // scales world sound effects; Music stays out of it so a music-off player still
        // hears the cutscene. slider is 0..10 through the game's dB curve (-80..0) —
        // convert to linear gain. our source bypasses the mixer (ignoreListener*/bypass
        // flags keep the world-mute and voice-steal fixes intact) so the slider is
        // applied here by hand. CutsceneVolume (F12) rides on top as a trim, default 1.
        private static float TargetVolume()
        {
            float gain = 1f;
            try
            {
                var s = Comfort.Common.Singleton<SharedGameSettingsClass>.Instance?.Sound?.Settings;
                if (s != null)
                {
                    float overallDb = s.GetRealSoundValue(Mathf.Clamp((int)s.OverallVolume, 0, 10));
                    gain = Mathf.Pow(10f, overallDb / 20f);
                    if (gain < 0.001f) gain = 0f; // slider at 0 = -80dB = intended silence
                }
            }
            catch { /* settings not up yet — full volume rather than silence */ }
            return gain * Plugin.CutsceneVolume.Value;
        }

        private IEnumerator Fade(float from, float to)
        {
            float t = 0f;
            while (t < FadeDur)
            {
                t += Time.unscaledDeltaTime;
                _fade = Mathf.Lerp(from, to, t / FadeDur);
                yield return null;
            }
            _fade = to;
        }

        private void OnGUI()
        {
            GUI.depth = -10000; // over everything
            if (_showVideo && _rt != null)
            {
                // FitInside letterbox — bars show the overlay camera's solid black
                float sw = Screen.width, sh = Screen.height;
                float va = (float)_rt.width / _rt.height;
                float w = sw, h = w / va;
                if (h > sh) { h = sh; w = h * va; }
                GUI.DrawTexture(new Rect((sw - w) * 0.5f, (sh - h) * 0.5f, w, h), _rt, ScaleMode.StretchToFill, false);
            }
            if (_fade <= 0f) return;
            if (_black == null)
            {
                _black = new Texture2D(1, 1);
                _black.SetPixel(0, 0, Color.black);
                _black.Apply();
            }
            GUI.color = new Color(0f, 0f, 0f, _fade); // drawn after the video = fades over it
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _black);
            GUI.color = Color.white;
        }

        // idempotent — runs at the scripted exit AND from OnDestroy as a teardown net
        private void RestoreWorld()
        {
            _showVideo = false;
            if (_vp != null) { _vp.Stop(); _vp.targetTexture = null; }
            if (_rt != null) { _rt.Release(); Destroy(_rt); _rt = null; }
            if (_overlayCam != null) _overlayCam.enabled = false;
            if (_worldMuted) { AudioListener.pause = false; _worldMuted = false; }
            foreach (var cv in _hiddenCanvases)
                if (cv != null) cv.enabled = true;
            _hiddenCanvases.Clear();
            if (_inputLocked) { GamePlayerOwner.SetIgnoreInputInNPCDialog(false); _inputLocked = false; }
        }

        private void OnDestroy()
        {
            RestoreWorld();
            if (_black != null) Destroy(_black);
        }
    }
}
