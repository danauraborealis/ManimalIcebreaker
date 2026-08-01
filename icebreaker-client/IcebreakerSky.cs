using System;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Manimal.Icebreaker
{
    // retail TOD_Sky atmosphere restore. the backport's rebuilt sky renders a flat
    // white (day) / black (night) atmosphere with a hard horizon seam — and the
    // scattering fog pass colors its veil FROM the sky, which is why every fog
    // experiment produced white/black slabs. this loads the retail Scripts-scene
    // TOD_Sky dump (icebreaker_tod_sky.json next to the plugin dll, extracted from
    // level698) and reflection-walks it onto the live sky singleton: Day/Night/Sun/
    // Moon/Stars/Light/Fog/Ambient/Reflection/World/Atmosphere, floats to gradients.
    internal static class IcebreakerSky
    {
        private static bool _done;

        internal static void TryApply()
        {
            if (_done) return;
            var sky = MonoBehaviourSingleton<TOD_Sky>.Instance;
            if (sky == null) return;
            _done = true;
            // freeze first and unconditionally — it must not die with RetailSky, theyre
            // independent levers (retail values caused artifacts; the freeze is the fix)
            try { FreezeSkybox(sky); }
            catch (Exception e) { Plugin.Log.LogError($"[Sky] freeze failed: {e}"); }
            if (!Plugin.RetailSky.Value) return;
            try
            {
                string path = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "icebreaker_tod_sky.json");
                if (!File.Exists(path))
                {
                    Plugin.Log.LogWarning("[Sky] icebreaker_tod_sky.json missing next to the dll — retail sky NOT applied");
                    return;
                }
                var skyJson = JObject.Parse(File.ReadAllText(path))["TOD_Sky"] as JObject;
                if (skyJson == null) { Plugin.Log.LogDebug("[Sky] json has no TOD_Sky block"); return; }

                int set = 0, miss = 0;
                foreach (var prop in skyJson.Properties())
                {
                    string name = prop.Name;
                    if (name.StartsWith("<")) name = name.Substring(1, name.IndexOf('>') - 1); // <X>k__BackingField
                    if (name == "Cycle") continue; // the TodHour pin owns time-of-day
                    Apply(sky, name, prop.Value, ref set, ref miss);
                }
                Plugin.Log.LogDebug($"[Sky] RETAIL TOD_Sky applied — {set} values set, {miss} missed");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Sky] apply failed: {e}");
            }
        }

        // FACTORY MODEL (user call): factory ships NO TOD sky at all — static skybox,
        // fixed lighting. retail authored exactly that for the icebreaker: level707
        // sets RenderSettings.skybox = skybox_night (builtin Skybox/Cubemap shader,
        // material ships in our bundle via the Sound scene). the procedural dome
        // painters are what render the broken flat-white/black atmosphere with the
        // horizon seam — kill them and let the authored skybox be the sky. clouds and
        // moon stay (they looked right); TOD keeps ticking for the weather systems.
        private static void FreezeSkybox(TOD_Sky sky)
        {
            if (!Plugin.StaticSkybox.Value) return;
            Material skybox = null;
            foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
                if (m != null && m.name == "skybox_night") { skybox = m; break; }
            if (skybox == null)
            {
                Plugin.Log.LogWarning("[Sky] skybox_night material not found in loaded assets — freeze skipped");
                return;
            }
            RenderSettings.skybox = skybox;

            var c = sky.Components;
            int off = 0;
            foreach (var go in new[] { c?.Atmosphere, c?.Clear, c?.Space, c?.Sun, c?.Moon, c?.CloudRenderer != null ? c.CloudRenderer.gameObject : null })
                if (go != null && go.activeSelf) { go.SetActive(false); off++; }
            Plugin.Log.LogDebug($"[Sky] FACTORY FREEZE — skybox_night applied, {off} dome painters disabled (pure static skybox)");
        }

        private static void Apply(object target, string name, JToken tok, ref int set, ref int miss)
        {
            var t = target.GetType();
            var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var p = f == null ? t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) : null;
            if (f == null && p == null) { miss++; return; }
            Type mt = f != null ? f.FieldType : p.PropertyType;

            object val = Convert(tok, mt, f != null ? (object)f.GetValue(target) : p.GetValue(target), ref set, ref miss);
            if (val == null) return;
            if (f != null) f.SetValue(target, val);
            else if (p.CanWrite) p.SetValue(target, val);
            set++;
        }

        // returns the value to assign, or null when it recursed into (and mutated) the
        // existing instance / couldn't convert
        private static object Convert(JToken tok, Type mt, object existing, ref int set, ref int miss)
        {
            try
            {
                if (mt == typeof(float)) return tok.Value<float>();
                if (mt == typeof(int)) return tok.Value<int>();
                if (mt == typeof(bool)) return tok.Type == JTokenType.Boolean ? tok.Value<bool>() : tok.Value<int>() != 0;
                if (mt.IsEnum) return Enum.ToObject(mt, tok.Value<int>());
                var o = tok as JObject;
                if (o == null) { miss++; return null; }
                if (mt == typeof(Color))
                    return new Color(F(o, "r"), F(o, "g"), F(o, "b"), F(o, "a", 1f));
                if (mt == typeof(Vector3))
                    return new Vector3(F(o, "x"), F(o, "y"), F(o, "z"));
                if (mt == typeof(Vector4))
                    return new Vector4(F(o, "x"), F(o, "y"), F(o, "z"), F(o, "w"));
                if (mt == typeof(AnimationCurve))
                {
                    var keys = o["m_Curve"] as JArray;
                    if (keys == null) { miss++; return null; }
                    var ks = new Keyframe[keys.Count];
                    for (int i = 0; i < keys.Count; i++)
                    {
                        var k = (JObject)keys[i];
                        ks[i] = new Keyframe(F(k, "time"), F(k, "value"), F(k, "inSlope"), F(k, "outSlope"), F(k, "inWeight"), F(k, "outWeight"));
                    }
                    return new AnimationCurve(ks);
                }
                if (mt == typeof(Gradient))
                {
                    int nc = o.Value<int?>("m_NumColorKeys") ?? 2;
                    int na = o.Value<int?>("m_NumAlphaKeys") ?? 2;
                    var cks = new GradientColorKey[nc];
                    for (int i = 0; i < nc; i++)
                    {
                        var k = (JObject)o[$"key{i}"];
                        cks[i] = new GradientColorKey(new Color(F(k, "r"), F(k, "g"), F(k, "b")), (o.Value<int?>($"ctime{i}") ?? 0) / 65535f);
                    }
                    var aks = new GradientAlphaKey[na];
                    for (int i = 0; i < na; i++)
                    {
                        var k = (JObject)o[$"key{i}"];
                        aks[i] = new GradientAlphaKey(F(k, "a", 1f), (o.Value<int?>($"atime{i}") ?? 0) / 65535f);
                    }
                    var g = new Gradient { colorKeys = cks, alphaKeys = aks };
                    return g;
                }
                // nested parameter group (TOD_DayParameters etc): recurse into the
                // EXISTING instance rather than constructing
                if (existing != null && mt.IsClass)
                {
                    foreach (var sub in o.Properties())
                    {
                        string n = sub.Name;
                        if (n.StartsWith("<")) n = n.Substring(1, n.IndexOf('>') - 1);
                        Apply(existing, n, sub.Value, ref set, ref miss);
                    }
                    return null; // mutated in place
                }
                miss++;
                return null;
            }
            catch
            {
                miss++;
                return null;
            }
        }

        private static float F(JObject o, string k, float def = 0f)
            => o.Value<float?>(k) ?? def;
    }
}
