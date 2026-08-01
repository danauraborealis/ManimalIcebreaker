using System.Text;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace Manimal.Icebreaker
{
    // live tuning panel (F9) for OUR volumetric fog (VolumetricFog & Mist 2 embed).
    // the old MBOIT/classic tuner is gone — that pipeline was a dead end (no
    // WindowsManager on this map, retail never ran it). the fog values left the
    // config on 07-30, so sliders now write the in-memory Val holders — changes do
    // NOT persist; 'DUMP to log' is the way to capture values for hardcoding.
    public class FogTuner : MonoBehaviour
    {
        [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.OnGameStarted))]
        private static class Patch_SpawnFogTuner
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                IcebreakerVolFog.ResetForRaid(); // statics reset is safe/needed everywhere
                if (!IceGate.On || !Plugin.DevMode.Value) return; // the tuner and its F9 key are dev-only
                new GameObject("Icebreaker_FogTuner").AddComponent<FogTuner>();
            }
        }

        private bool _open;
        private Rect _win = new Rect(40, 40, 440, 560);
        private Vector2 _scroll;

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F9))
            {
                _open = !_open;
                // freeze look/movement while tuning — sliders shouldnt swing the camera
                try { GamePlayerOwner.SetIgnoreInput(_open); } catch { }
            }
            if (_open)
            {
                // the raid re-locks the cursor every frame; re-unlock every frame
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
        }

        private void LateUpdate()
        {
            // instant slider feedback — the normal driver only ticks every 30 frames
            if (_open) IcebreakerVolFog.Tick();
        }

        // the ignore flag is a STATIC on GamePlayerOwner — release on death (raid end)
        // or input stays dead in the next raid
        private void OnDestroy()
        {
            if (_open) { try { GamePlayerOwner.SetIgnoreInput(false); } catch { } }
        }

        private void OnGUI()
        {
            if (!_open) return;
            _win = GUI.Window(0x1CEB07, _win, DrawWindow, "icebreaker fog tuner (F9)");
        }

        private void DrawWindow(int id)
        {
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(_win.height - 30));

            // Plugin.Fog() routes every entry to its VolumetricFog2.Cutscene twin while
            // the cutscene profile is live — same sliders tune either look
            if (IcebreakerVolFog.CutsceneProfile)
                GUILayout.Label("=== CUTSCENE PROFILE — edits only the cutscene look ===");

            Plugin.VolFog.Value = GUILayout.Toggle(Plugin.VolFog.Value, "volumetric fog enabled");

            GUILayout.Label("— shape —");
            Slider(Plugin.Fog(Plugin.VolFogDensity), "density", 0f, 1f, "0.00");
            Slider(Plugin.Fog(Plugin.VolFogNoise), "noise strength", 0f, 1f, "0.00");
            Slider(Plugin.Fog(Plugin.VolFogNoiseScale), "noise scale (billow size)", 0.2f, 5f, "0.00");
            Slider(Plugin.Fog(Plugin.VolFogHeight), "height (m)", 1f, 200f, "0");
            Slider(Plugin.Fog(Plugin.VolFogBaseline), "baseline Y (m)", -50f, 100f, "0.0");
            Slider(Plugin.Fog(Plugin.VolFogMaxLength), "max fog length (m)", 100f, 2000f, "0");
            Slider(Plugin.Fog(Plugin.VolFogMaxLengthFallOff), "wall ramp width (opaque at max len)", 0f, 1f, "0.00");
            Slider(Plugin.Fog(Plugin.VolFogWallShade), "wall darkness (0=black gloom)", 0f, 1f, "0.00");
            Slider(Plugin.Fog(Plugin.VolFogVoidFalloff), "void edge sharpness", 1f, 20f, "0.0");
            Slider(Plugin.Fog(Plugin.VolFogVoidBlend), "void blend (bridge gaps)", 0f, 1f, "0.00");
            Plugin.VolFogVoidDebug.Value = GUILayout.Toggle(Plugin.VolFogVoidDebug.Value, "void DEBUG (paint voids red)");
            Slider(Plugin.Fog(Plugin.VolFogDeepObscurance), "deep obscurance (darken w/ depth)", 0f, 3f, "0.00");
            Slider(Plugin.Fog(Plugin.VolFogSkyHaze), "sky haze", 0f, 500f, "0");
            Slider(Plugin.Fog(Plugin.VolFogAlpha), "alpha", 0f, 1f, "0.00");
            Slider(Plugin.Fog(Plugin.VolFogSpeed), "wind speed", 0f, 0.5f, "0.000");

            GUILayout.Label("— color —");
            var colorEntry = Plugin.FogColorEntry;
            var c = colorEntry.Value;
            float r = SliderRaw("  r", c.r, 0f, 1f, "0.00");
            float g = SliderRaw("  g", c.g, 0f, 1f, "0.00");
            float b = SliderRaw("  b", c.b, 0f, 1f, "0.00");
            if (r != c.r || g != c.g || b != c.b) colorEntry.Value = new Color(r, g, b);

            GUILayout.Label("— perf / misc —");
            int ds = (int)SliderRaw("downsampling", Plugin.VolFogDownsampling.Value, 1f, 4f, "0");
            if (ds != Plugin.VolFogDownsampling.Value) Plugin.VolFogDownsampling.Value = ds;
            Slider(Plugin.Fog(Plugin.WeatherDesaturate), "weather desaturate (-1 = vanilla)", -1f, 1f, "0.00");

            GUILayout.Space(8);
            if (GUILayout.Button("DUMP to log")) Dump();

            GUILayout.EndScrollView();
            GUI.DragWindow(new Rect(0, 0, 10000, 22));
        }

        private static void Slider(Plugin.Val<float> entry, string label, float min, float max, string fmt)
        {
            float v = SliderRaw(label, entry.Value, min, max, fmt);
            if (v != entry.Value) entry.Value = v;
        }

        private static float SliderRaw(string label, float v, float min, float max, string fmt)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{label}: {v.ToString(fmt)}", GUILayout.Width(200));
            v = GUILayout.HorizontalSlider(v, min, max);
            GUILayout.EndHorizontal();
            return v;
        }

        private void Dump()
        {
            var c = Plugin.VolFogColor.Value;
            var sb = new StringBuilder("[FogTuner] DUMP (VolumetricFog2)\n");
            sb.AppendLine($"  density={Plugin.VolFogDensity.Value:0.###} noise={Plugin.VolFogNoise.Value:0.###} noiseScale={Plugin.VolFogNoiseScale.Value:0.###}");
            sb.AppendLine($"  height={Plugin.VolFogHeight.Value:0.#} baseline={Plugin.VolFogBaseline.Value:0.#} maxLength={Plugin.VolFogMaxLength.Value:0} lenFalloff={Plugin.VolFogMaxLengthFallOff.Value:0.##} deepObsc={Plugin.VolFogDeepObscurance.Value:0.##}");
            sb.AppendLine($"  skyHaze={Plugin.VolFogSkyHaze.Value:0.#} alpha={Plugin.VolFogAlpha.Value:0.###} windSpeed={Plugin.VolFogSpeed.Value:0.###}");
            sb.AppendLine($"  color=({c.r:0.###},{c.g:0.###},{c.b:0.###}) downsampling={Plugin.VolFogDownsampling.Value} desat={Plugin.WeatherDesaturate.Value:0.##}");
            Plugin.Log.LogDebug(sb.ToString());
        }
    }
}
