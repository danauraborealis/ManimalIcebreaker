using System.Text;
using Comfort.Common;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace Manimal.Icebreaker
{
    // sky-dome debug probe (temporary): F11 dumps the live TOD_Sky rendering state —
    // dome part renderers, their materials/shaders/meshes, quality/colorspace, sun
    // and moon state. run once on the icebreaker (broken flat sky) and once on a
    // vanilla map (working atmosphere, same build) — the diff names the broken piece.
    public class SkyProbe : MonoBehaviour
    {
        [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.OnGameStarted))]
        private static class Patch_SpawnSkyProbe
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!IceGate.On) return; // diag probe is icebreaker-only
                new GameObject("Icebreaker_SkyProbe").AddComponent<SkyProbe>();
            }
        }

        private void Update()
        {
            if (!Plugin.DiagHotkeys.Value || !Input.GetKeyDown(KeyCode.F11)) return;
            try { Dump(); }
            catch (System.Exception e) { Plugin.Log.LogError($"[SkyProbe] dump failed: {e}"); }
        }

        private static void Dump()
        {
            var sky = MonoBehaviourSingleton<TOD_Sky>.Instance;
            var sb = new StringBuilder();
            if (sky == null) { Plugin.Log.LogWarning("[SkyProbe] no TOD_Sky.Instance"); return; }
            sb.AppendLine($"[SkyProbe] sky go='{sky.gameObject.name}' active={sky.isActiveAndEnabled} Initialized={sky.Initialized}");
            sb.AppendLine($"  quality: Sky={sky.SkyQuality} Cloud={sky.CloudQuality} Mesh={sky.MeshQuality} ColorSpace={sky.ColorSpace} ColorRange={sky.ColorRange}");
            try { sb.AppendLine($"  cycle: hour={sky.Cycle.Hour:0.00} SunZenith={sky.SunZenith:0.0} SunsetTime? IsDay={sky.IsDay} IsNight={sky.IsNight}"); } catch { }
            try { sb.AppendLine($"  sun dir={sky.SunDirection} moon dir={sky.MoonDirection} LerpValue={sky.LerpValue:0.00}"); } catch { }

            var comps = sky.Components;
            if (comps == null) { sb.AppendLine("  Components NULL"); Plugin.Log.LogWarning(sb.ToString()); return; }
            DumpPart(sb, "Space", comps.SpaceRenderer);
            DumpPart(sb, "Atmosphere", comps.AtmosphereRenderer);
            DumpPart(sb, "Clear", comps.ClearRenderer);
            DumpPart(sb, "Cloud", comps.CloudRenderer);
            DumpPart(sb, "Sun", comps.SunRenderer);
            DumpPart(sb, "Moon", comps.MoonRenderer);
            try
            {
                sb.AppendLine($"  RenderSettings: fog={RenderSettings.fog} mode={RenderSettings.fogMode} density={RenderSettings.fogDensity:0.0000} color={RenderSettings.fogColor} ambientMode={RenderSettings.ambientMode} ambientIntensity={RenderSettings.ambientIntensity:0.00} skybox={(RenderSettings.skybox != null ? RenderSettings.skybox.name + "/" + (RenderSettings.skybox.shader != null ? RenderSettings.skybox.shader.name : "noshader") : "NULL")}");
            }
            catch { }
            Plugin.Log.LogWarning(sb.ToString());
        }

        private static void DumpPart(StringBuilder sb, string tag, Renderer r)
        {
            if (r == null) { sb.AppendLine($"  {tag}: renderer NULL"); return; }
            var mat = r.sharedMaterial;
            var mf = r.GetComponent<MeshFilter>();
            sb.AppendLine($"  {tag}: go='{r.gameObject.name}' activeInHierarchy={r.gameObject.activeInHierarchy} rendererEnabled={r.enabled} " +
                          $"mat='{(mat != null ? mat.name : "NULL")}' shader='{(mat != null && mat.shader != null ? mat.shader.name : "NULL")}' supported={(mat != null && mat.shader != null ? mat.shader.isSupported.ToString() : "?")} " +
                          $"mesh='{(mf != null && mf.sharedMesh != null ? mf.sharedMesh.name : "NULL")}'");
        }
    }
}
