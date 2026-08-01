using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Comfort.Common;
using EFT.Interactive;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Manimal.Icebreaker
{
    // retail runs BSGs VolumetricLight (the Sandbox/VolumetricLight raymarcher) on exactly
    // 49 of icebreakers 1747 lights — hand-picked, every one a spot. the component is a
    // MonoBehaviour so it died with the rest of the il2cpp script data on export; the
    // LIGHTS themselves survived intact, so all we put back is the component plus its
    // authored fields.
    //
    // 4.0 and retail 1.0 share the class layout byte-for-byte (60 bytes of fields, same
    // order — verified by decoding the raw MonoBehaviour payloads out of level703/704/709
    // and checking them against the 4.0 field order), so retails tuned values transfer
    // straight across. 1.0 exposes a quality dropdown where 4.0 has a plain on/off, but
    // its the same renderer underneath and the same component drives it.
    //
    // only 6 fields ever differ from the class defaults — scatter, mieG, and the four
    // noise knobs. sample count 8, extinction 0.01, skybox 0.9, no height fog, ray length
    // 400 are default on all 49, so theyre left alone rather than restated.
    internal static class IcebreakerVolumetricLights
    {
        // path + range + spotAngle. the two light params are there to disambiguate
        // same-named siblings — the exfil heli carries three children called G_spot under
        // Light/Front and only the range-15 one is volumetric, so path alone picks wrong.
        private readonly struct V
        {
            internal readonly string Path;
            internal readonly float Range, Spot, Scatter, MieG, NoiseScale, NoiseIntensity, NoiseVel;
            internal readonly bool Noise;

            internal V(string path, float range, float spot, float scatter, float mieG,
                       bool noise, float noiseScale, float noiseIntensity, float noiseVel)
            {
                Path = path; Range = range; Spot = spot; Scatter = scatter; MieG = mieG;
                Noise = noise; NoiseScale = noiseScale; NoiseIntensity = noiseIntensity; NoiseVel = noiseVel;
            }
        }

        // 48 rows for 49 components: the two S_spot siblings under Home_theater_01 share a
        // path AND identical params, so one row claims both (see the every-match loop below)
        private static readonly V[] Table =
        {
            new V("INTERACTIVE_Helicopter_Extraction/Helicopter_K32_extraction/Light/Back/G_spot", 15f, 92.7f, 1f, 0f, true, 0.05f, 1f, 5f),
            new V("INTERACTIVE_Helicopter_Extraction/Helicopter_K32_extraction/Light/Bottom/G_spot", 3f, 70.4f, 1f, 0f, true, 0.05f, 5f, 5f),
            new V("INTERACTIVE_Helicopter_Extraction/Helicopter_K32_extraction/Light/Bottom/G_spot/G_spot", 3f, 70.4f, 1f, 0f, true, 0.05f, 5f, 5f),
            new V("INTERACTIVE_Helicopter_Extraction/Helicopter_K32_extraction/Light/Front/G_spot", 15f, 92.7f, 1f, 0f, true, 0.05f, 1f, 5f),
            new V("SBG_Icebreaker_Lights/OO/LIGHT/ADD2/LAMP_ON/Icebreaker_interior_lamp_03 (30)/Light_standard_00/S_spot_vol", 1.8f, 120f, 1f, 0.1f, false, 0.015f, 1f, 3f),
            new V("SBG_Icebreaker_Lights/OO/LIGHT/ADD2/LAMP_ON/PC_display_01 (1)/Light_standard_00 (1)/G_spot", 3f, 100f, 0.75f, 0.1f, false, 0.015f, 1f, 3f),
            new V("SBG_Icebreaker_Lights/OO/LIGHT/ADD2/LAMP_ON/PC_display_01 (2)/Light_standard_00 (1)/G_spot", 3f, 100f, 0.75f, 0.1f, false, 0.015f, 1f, 3f),
            new V("SBG_Icebreaker_Lights/OO/LIGHT/BOW/LAMP_ON/Icebreaker_accommodation_lamp_01/Light_standard_00_1/G_spot", 7f, 130f, 1f, 0f, true, 0.05f, 1f, 5f),
            new V("SBG_Icebreaker_Lights/OO/LIGHT/BOW/LAMP_ON/Icebreaker_accommodation_lamp_02 (1)/Light_standard_00_1/S_spot_vol", 7f, 85f, 0.65f, 0.5f, true, 0.05f, 1f, 5f),
            new V("SBG_Icebreaker_Lights/OO/LIGHT/BOW/LAMP_ON/Icebreaker_accommodation_lamp_02 (2)/Light_standard_00_1/G_spot", 15f, 60f, 1f, 0f, true, 0.05f, 1f, 5f),
            new V("SBG_Icebreaker_Lights/OO/LIGHT/BOW/LAMP_ON/Icebreaker_accommodation_lamp_02/Light_standard_00_1/S_spot_vol", 7f, 85f, 1f, 0.1f, true, 0.05f, 1f, 5f),
            new V("SBG_Icebreaker_Lights/OO/LIGHT/HELIPAD/LAMP_ON/Icebreaker_accommodation_lamp_02 (6)/Light_standard_00_1/S_spot_vol", 5f, 120f, 1f, 0.1f, true, 0.05f, 2f, 5f),
            new V("SBG_Icebreaker_Lights/OO/LIGHT/HELIPAD/LAMP_ON/Icebreaker_accommodation_lamp_02 (7)/Light_standard_00_1/S_spot_vol", 5f, 120f, 1f, 0.1f, true, 0.05f, 2f, 5f),
            new V("SBG_Icebreaker_Lights/OO/LIGHT/INTERIOR_SUPERSTRUCTURE_1st_FLOOR/LAMP_ON/Home_theater_01/Light_standard_00/S_spot", 2f, 160f, 1f, 0.1f, false, 0.015f, 1f, 3f),
            new V("SBG_Icebreaker_Lights/OO/LIGHT/INTERIOR_SUPERSTRUCTURE_1st_FLOOR/LAMP_ON/Home_theater_01/Light_standard_00/S_spot (1)", 2f, 160f, 1f, 0.1f, false, 0.015f, 1f, 3f),
            new V("SBG_Icebreaker_Lights/OO/LIGHT/INTERIOR_SUPERSTRUCTURE_1st_FLOOR/LAMP_ON/Icebreaker_interior_lamp_02 (4)/Light_standard_00/G_spot (1)", 1.9905f, 120f, 1f, 0.1f, false, 0.015f, 1f, 3f),
            new V("SBG_Icebreaker_Lights/OO/LIGHT/INTERIOR_SUPERSTRUCTURE_1st_FLOOR/LAMP_ON/Icebreaker_interior_lamp_02 (7)/Light_standard_00/G_spot", 2.6f, 140f, 0.85f, 0.1f, false, 0.015f, 1f, 3f),
            new V("SBG_Icebreaker_Lights/OO/LIGHT/INTERIOR_SUPERSTRUCTURE_1st_FLOOR/LAMP_ON/PC_display_01/Light_standard_00 (1)/G_spot", 3f, 100f, 0.75f, 0.1f, false, 0.015f, 1f, 3f),
            new V("SBG_Icebreaker_Lights/OO/LIGHT/INTERIOR_SUPERSTRUCTURE_4th_FLOOR/LAMP_ON/Icebreaker_interior_lamp_02_corrected (2)/Light_standard_00/S_spot_vol", 2.2f, 140f, 1f, 0.1f, false, 0.015f, 1f, 3f),
            new V("SBG_Icebreaker_Lights/OO/LIGHT/INTERIOR_SUPERSTRUCTURE_4th_FLOOR/LAMP_ON/Icebreaker_interior_lamp_02_corrected (3)/Light_standard_00/S_spot_vol", 2.2f, 140f, 1f, 0.1f, false, 0.015f, 1f, 3f),
            new V("SBG_Icebreaker_Lights/OO/LIGHT/INTERIOR_SUPERSTRUCTURE_4th_FLOOR/LAMP_ON/Icebreaker_interior_lamp_02_corrected (4)/Light_standard_00/S_spot_vol", 2.2f, 140f, 1f, 0.1f, false, 0.015f, 1f, 3f),
            new V("SBG_Icebreaker_Lights/OO/LIGHT/INTERIOR_SUPERSTRUCTURE_5th_FLOOR/LAMP_ON/Icebreaker_interior_lamp_02_corrected (5)/Light_standard_00/S_spot_vol", 2f, 140f, 0.7f, 0f, false, 0.015f, 1f, 3f),
            new V("SBG_Icebreaker_Lights/OO/LIGHT/INTERIOR_SUPERSTRUCTURE_5th_FLOOR/LAMP_ON/Icebreaker_interior_lamp_02_corrected (6)/Light_standard_00/S_spot_vol", 2f, 140f, 0.65f, 0f, false, 0.015f, 1f, 3f),
            new V("SBG_Icebreaker_Lights/OO/LIGHT/INTERIOR_SUPERSTRUCTURE_7th_FLOOR/LAMP_ON/Icebreaker_interior_lamp_02_corrected (2)/Light_standard_00/S_spot_vol", 1.85f, 100f, 1f, 0.1f, false, 0.015f, 1f, 3f),
            new V("SBG_Icebreaker_Lights/OO/LIGHT/INTERIOR_SUPERSTRUCTURE_7th_FLOOR/LAMP_ON/Icebreaker_interior_lamp_02_corrected (3)/Light_standard_00/S_spot_vol", 1.85f, 100f, 1f, 0.1f, false, 0.015f, 1f, 3f),
            new V("SBG_Icebreaker_Lights/OO/LIGHT/INTERIOR_SUPERSTRUCTURE_7th_FLOOR/LAMP_ON/PC_display_01 (1)/Light_standard_00 (4)/G_spot", 2.5f, 100f, 1f, 0.1f, false, 0.015f, 1f, 3f),
            new V("SBG_Icebreaker_Lights/OO/LIGHT/INTERIOR_SUPERSTRUCTURE_9th_FLOOR/LAMP_ON/Icebreaker_interior_lamp_02_corrected (5)/Light_standard_00/S_spot_vol", 2f, 140f, 1f, 0.1f, false, 0.015f, 1f, 3f),
            new V("SBG_Icebreaker_Lights/OO/LIGHT/INTERIOR_SUPERSTRUCTURE_9th_FLOOR/LAMP_ON/Icebreaker_interior_lamp_02_corrected (6)/Light_standard_00/S_spot_vol", 3f, 140f, 1f, 0.1f, false, 0.015f, 1f, 3f),
            new V("SBG_Icebreaker_Lights/OO/LIGHT/INTERIOR_SUPERSTRUCTURE_9th_FLOOR/LAMP_ON/Icebreaker_interior_lamp_02_corrected (7)/Light_standard_00/S_spot_vol", 2f, 140f, 1f, 0.1f, false, 0.015f, 1f, 3f),
            new V("SBG_Icebreaker_Lights/OO/LIGHT/INTERIOR_SUPERSTRUCTURE_9th_FLOOR/LAMP_ON/Icebreaker_interior_lamp_02_corrected (9)/Light_standard_00/S_spot_vol", 2f, 140f, 1f, 0.1f, false, 0.015f, 1f, 3f),
            new V("SBG_Icebreaker_Lights/OO/LIGHT/INTERIOR_TECH_DECKS_0/LAMP_ON/Icebreaker_interior_lamp_02_corrected (11)/Light_standard_00/S_spot_vol", 2f, 140f, 1f, 0.1f, false, 0.015f, 1f, 3f),
            new V("SBG_Icebreaker_Lights/OO/LIGHT/INTERIOR_TECH_DECKS_0/LAMP_ON/Icebreaker_interior_lamp_02_corrected (12)/Light_standard_00/S_spot_vol", 2f, 140f, 1f, 0.1f, false, 0.015f, 1f, 3f),
            new V("SBG_Icebreaker_Lights/OO/LIGHT/INTERIOR_TECH_DECKS_0/LAMP_ON/Icebreaker_interior_lamp_02_corrected (13)/Light_standard_00/S_spot_vol", 2f, 140f, 1f, 0.1f, false, 0.015f, 1f, 3f),
            new V("SBG_Icebreaker_Lights/OO/LIGHT/INTERIOR_TECH_DECKS_0/LAMP_ON/Icebreaker_interior_lamp_03 (14)/Light_standard_00/S_spot_vol", 2.2f, 130f, 0.75f, 0.1f, false, 0.015f, 1f, 3f),
            new V("SBG_Icebreaker_Lights/OO/LIGHT/INTERIOR_TECH_DECKS_0/LAMP_ON/Icebreaker_interior_lamp_03 (15)/Light_standard_00/S_spot_vol", 1.8f, 130f, 0.754f, 0.1f, false, 0.015f, 1f, 3f),
            new V("SBG_Icebreaker_Lights/OO/LIGHT/INTERIOR_TECH_DECKS_0/LAMP_ON/Icebreaker_interior_lamp_03 (19)/Light_standard_00/S_spot_vol", 1.8f, 120f, 0.75f, 0.1f, false, 0.015f, 1f, 3f),
            new V("SBG_Icebreaker_Lights/OO/LIGHT/SUPERSTRUCTURE_BACK/LAMP_ON/Icebreaker_accommodation_lamp_02 (3)/Light_standard_00_1/S_spot_vol", 7.25f, 100f, 1f, 0.1f, true, 0.05f, 2f, 5f),
            new V("SBG_Icebreaker_Lights/OO/LIGHT/SUPERSTRUCTURE_BACK/LAMP_ON/Icebreaker_accommodation_lamp_02/Light_standard_00_1/G_spot", 3f, 100f, 0.5f, 0.1f, true, 0.05f, 2f, 5f),
            new V("SBG_Icebreaker_Lights/OO/LIGHT/SUPERSTRUCTURE_FRONT/LAMP_ON/Icebreaker_accommodation_lamp_01_blue/Light_standard_00_1/G_spot", 3.5f, 100f, 1f, 0f, true, 0.05f, 0.8f, 5f),
            new V("SBG_Icebreaker_Lights/OO/LIGHT/SUPERSTRUCTURE_FRONT/LAMP_ON/Icebreaker_accommodation_lamp_01_blue/Light_standard_00_1/G_spot", 3.5f, 110f, 1f, 0.1f, true, 0.05f, 0.75f, 5f),
            new V("SBG_Icebreaker_Lights/OO/LIGHT/SUPERSTRUCTURE_FRONT/LAMP_ON/Icebreaker_accommodation_lamp_02 (2)/Light_standard_00_1/G_spot", 4f, 100f, 1f, 0.1f, true, 0.05f, 1f, 5f),
            new V("SBG_Icebreaker_Lights/OO/LIGHT/SUPERSTRUCTURE_FRONT/LAMP_ON/Icebreaker_accommodation_lamp_02 (3)/Light_standard_00_1/G_spot", 4f, 100f, 1f, 0.1f, true, 0.05f, 1f, 5f),
            new V("SBG_Icebreaker_Lights/OO/LIGHT/SUPERSTRUCTURE_FRONT/LAMP_ON/Icebreaker_accommodation_lamp_02 (6)/Light_standard_00_1/G_spot", 7f, 90f, 1f, 0.1f, true, 0.05f, 1f, 5f),
            new V("SBG_Icebreaker_Lights/OO/LIGHT/SUPERSTRUCTURE_FRONT/LAMP_ON/Light_standard_00 (1)/S_spot (2)", 12f, 95f, 1f, 0.1f, true, 0.05f, 1f, 5f),
            new V("SBG_Icebreaker_Lights/OO/LIGHT/SUPERSTRUCTURE_FRONT/LAMP_ON/Light_standard_00/S_spot (4)", 12f, 95f, 1f, 0.1f, true, 0.05f, 1f, 5f),
            new V("CutsceneRoot/Helicopter_00/VFX_Icebreaker_Cutscene_Helicopter_00/Lights/G_spot_Main", 15f, 60f, 1f, 0f, true, 0.05f, 1f, 5f),
            new V("CutsceneRoot/VFX_Icebreaker_Cutscene_Fake_Light/6st_shot_Light/Light/G_spot_Main", 10f, 40f, 1f, 0f, false, 0.05f, 1f, 5f),
            new V("CutsceneRoot/VFX_Icebreaker_Cutscene_Fake_Light/6st_shot_Light/Light/G_spot_Main (1)", 15f, 22.4f, 1f, 0f, false, 0.05f, 1f, 5f),
        };

        private static readonly FieldInfo _cloVol = AccessTools.Field(typeof(CullingLightObject), "volumetricLight_0");
        private static readonly FieldInfo _lampVol = AccessTools.Field(typeof(LampController), "list_2");

        internal static void Restore()
        {
            if (!IceGate.On) return;
            // the component destroys itself in Awake when the graphics setting is off, so
            // adding 49 of them would just be churn
            if (!SettingOn())
            {
                Plugin.Log.LogInfo("[Volumetric] graphics setting off — skipping restore");
                return;
            }

            // index every icebreaker light by hierarchy path in one sweep. cheaper than
            // resolving 48 paths by name-walk, and FindObjectsOfTypeAll catches lamps that
            // are still inactive (an off lamp gets its component now, Awake fires when the
            // lamp comes up)
            var byPath = new Dictionary<string, List<Light>>();
            foreach (var l in Resources.FindObjectsOfTypeAll<Light>())
            {
                if (l == null) continue;
                var sc = l.gameObject.scene;
                if (!sc.IsValid() || sc.name == null || !sc.name.StartsWith("Icebreaker")) continue;
                var p = PathOf(l.transform);
                if (!byPath.TryGetValue(p, out var bucket)) byPath[p] = bucket = new List<Light>();
                bucket.Add(l);
            }

            // the cutscene scene is loaded additively only when the cutscene actually
            // runs, so at raid start its 3 beams arent missing — they dont exist yet.
            // IcebreakerTimelineCutscene calls back in once its up (were idempotent)
            bool cutsceneLive = SceneManager.GetSceneByName("Icebreaker_cutscene_01").isLoaded;

            int added = 0, already = 0, unmatched = 0, deferred = 0;
            foreach (var e in Table)
            {
                if (!cutsceneLive && e.Path.StartsWith("CutsceneRoot/")) { deferred++; continue; }
                if (!byPath.TryGetValue(e.Path, out var cands)) { unmatched++; continue; }
                bool hit = false;
                foreach (var l in cands)
                {
                    if (Mathf.Abs(l.range - e.Range) > 0.01f) continue;
                    if (Mathf.Abs(l.spotAngle - e.Spot) > 0.05f) continue;
                    hit = true;
                    if (l.GetComponent<VolumetricLight>() != null) { already++; continue; }
                    if (Attach(l, e)) added++;
                }
                if (!hit) unmatched++;
            }

            if (unmatched > 0)
                Plugin.Log.LogWarning($"[Volumetric] {unmatched}/{Table.Length} entries matched no light — scene drift, report this");
            Plugin.Log.LogWarning($"[Volumetric] {added} volumetric lights restored" +
                                  (already > 0 ? $" ({already} already had one)" : "") +
                                  (deferred > 0 ? $", {deferred} deferred to cutscene load" : ""));
        }

        private static bool Attach(Light light, V e)
        {
            var go = light.gameObject;
            // Awake fires the instant AddComponent lands and it bakes the fields into the
            // material right there — set them after and youre tuning a material thats
            // already built. build inactive, populate, let activation run Awake once with
            // the real values (same trap the ambient players and patrol points hit).
            // a GO thats already inactive is left alone: its Awake hasnt run yet either way
            bool wasActive = go.activeSelf;
            try
            {
                if (wasActive) go.SetActive(false);
                var vl = go.AddComponent<VolumetricLight>();
                vl.ScatteringCoef = e.Scatter;
                vl.MieG = e.MieG;
                vl.Noise = e.Noise;
                vl.NoiseScale = e.NoiseScale;
                vl.NoiseIntensity = e.NoiseIntensity;
                vl.NoiseVelocity = new Vector2(e.NoiseVel, e.NoiseVel);
                Rebind(light, vl);
                return true;
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"[Volumetric] attach failed on {e.Path}: {ex.Message}");
                return false;
            }
            finally { if (wasActive) go.SetActive(true); }
        }

        // both native consumers cache their VolumetricLight once at init and we always
        // arrive after them, so without this the beam ignores everything that drives it:
        // CullingLightObject re-checks intensity as the light fades with distance, and
        // LampController toggles the beam with the lamp and re-checks it on flicker/dim.
        // an unregistered beam keeps burning from a lamp thats been switched off.
        private static void Rebind(Light light, VolumetricLight vl)
        {
            var clo = light.GetComponent<CullingLightObject>();
            if (clo != null) _cloVol?.SetValue(clo, vl);

            var lamp = light.GetComponentInParent<LampController>();
            if (lamp == null || _lampVol == null) return;
            // only claim the lamp that actually owns this light — GetComponentInParent
            // walks past the immediate rig on the nested lamp hierarchies
            bool owns = false;
            var lights = lamp.Lights;
            if (lights != null)
                foreach (var l in lights) if (l == light) { owns = true; break; }
            if (!owns) return;
            if (_lampVol.GetValue(lamp) is List<VolumetricLight> list && !list.Contains(vl))
            {
                list.Add(vl);
                // the lamp mirrors its state onto light.enabled, so thats a free read of
                // whether this beam should be live right now
                vl.enabled = light.enabled;
            }
        }

        private static bool SettingOn()
        {
            try
            {
                var s = Singleton<SharedGameSettingsClass>.Instance;
                return s == null || s.Graphics.Settings.VolumetricLight.Value;
            }
            catch { return true; }
        }

        private static string PathOf(Transform t)
        {
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.ToString();
        }
    }
}
