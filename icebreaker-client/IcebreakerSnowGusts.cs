using System;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.EnvironmentEffect;
using EFT.Weather;
using UnityEngine;

namespace Manimal.Icebreaker
{
    // wind-driven sheets of blown snow around the player: big, soft, low-alpha puffs that
    // read as smoke rather than flakes. this sits ON TOP of BSG's SnowFlakes rather than
    // replacing it — SnowFlakes is a camera-space quad pair that draws individual flakes
    // and has no notion of gusting, so the two do different jobs.
    //
    // no new art ships for this. the material is grafted off a donor particle system
    // already in the map (the same trick the tripwires use for their visual), which
    // guarantees a shader that survived the build and a soft puff texture that matches the
    // map's look. a procedural soft-disc texture is the last resort so the effect can
    // never hard-fail into invisibility.
    //
    // everything is driven live from config, so the look can be dialled in mid-raid
    // without a rebuild.
    internal class IcebreakerSnowGusts : MonoBehaviour
    {
        private ParticleSystem _ps;
        private ParticleSystemRenderer _renderer;
        private Transform _tf;
        private SnowFlakes _snow;
        private bool _failed;

        // (08-29 field report: 90%+ of the frame going to IcebreakerSnowGusts.LateUpdate)
        // Spawn() creates its OWN standalone GameObject rather than attaching to a caller's,
        // so unlike every sibling in RaidFixPatches's init chain it never had a
        // GetComponent<T>()==null guard at the call site - and StageTwoInit (which calls it)
        // is known to re-run multiple times per raid (12x measured in one session). Every
        // re-run stacked another full, independently-ticking gust system on top of the
        // last. Guard here instead, same pattern WedgeVoice already uses for its own
        // find-or-create.
        internal static void Spawn()
        {
            if (UnityEngine.Object.FindObjectOfType<IcebreakerSnowGusts>() != null) return;
            var go = new GameObject("Icebreaker_SnowGusts");
            go.AddComponent<IcebreakerSnowGusts>();
        }

        private void Start()
        {
            try
            {
                if (!Build())
                {
                    Plugin.Log.LogWarning("[Gusts] could not build the gust system, skipping");
                    Destroy(gameObject);
                    return;
                }
                Plugin.Log.LogDebug("[Gusts] blown-snow gusts live");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Gusts] build failed: {e.Message}");
                Destroy(gameObject);
            }
        }

        private bool Build()
        {
            _tf = transform;
            _ps = gameObject.AddComponent<ParticleSystem>();
            _renderer = GetComponent<ParticleSystemRenderer>();

            // the emitter follows the player but the particles must NOT: world simulation
            // is what makes a gust blow past you instead of travelling glued to your head
            var main = _ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.loop = true;
            main.playOnAwake = false;
            main.maxParticles = 400;
            main.startLifetime = new ParticleSystem.MinMaxCurve(2.5f, 5f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.gravityModifier = 0.02f;   // barely any: these are airborne sheets

            var shape = _ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;

            var emission = _ps.emission;
            emission.enabled = true;

            var noise = _ps.noise;          // the curl that sells it as billowing, not sliding
            noise.enabled = true;
            noise.strength = 3f;
            noise.frequency = 0.15f;
            noise.scrollSpeed = 0.4f;
            noise.damping = true;

            var colour = _ps.colorOverLifetime;   // fade in and out, never pop
            colour.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[]
                {
                    new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.25f),
                    new GradientAlphaKey(1f, 0.6f), new GradientAlphaKey(0f, 1f),
                });
            colour.color = new ParticleSystem.MinMaxGradient(grad);

            var size = _ps.sizeOverLifetime;      // puffs expand as they tear apart
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.7f, 1f, 1.4f));

            _renderer.renderMode = ParticleSystemRenderMode.Billboard;
            _renderer.alignment = ParticleSystemRenderSpace.View;
            _renderer.sortMode = ParticleSystemSortMode.Distance;
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.material = BuildMaterial();

            _ps.Play();
            return _renderer.material != null;
        }

        // prefer a material already proven to render on this map. the heli's rotor wash is
        // literally blown snow, so it is the ideal donor when the rig is present.
        private Material BuildMaterial()
        {
            try
            {
                var donor = FindDonor();
                if (donor != null)
                {
                    var m = new Material(donor) { name = "manimal_snowgusts" };
                    Plugin.Log.LogDebug($"[Gusts] grafted donor material '{donor.name}' (shader '{donor.shader?.name}')");
                    return m;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Gusts] donor graft failed: {e.Message}"); }

            // nothing to borrow: build a soft disc and hang it on whichever particle-capable
            // shader survived the build. logged either way, because a wrong-looking gust is
            // almost always a wrong shader.
            foreach (var name in new[]
                     {
                         "Particles/Standard Unlit", "Legacy Shaders/Particles/Alpha Blended",
                         "Particles/Alpha Blended", "Sprites/Default", "UI/Default",
                     })
            {
                var sh = Shader.Find(name);
                if (sh == null) continue;
                var m = new Material(sh) { name = "manimal_snowgusts", mainTexture = SoftDisc() };
                Plugin.Log.LogDebug($"[Gusts] no donor found, fell back to shader '{name}'");
                return m;
            }
            Plugin.Log.LogDebug("[Gusts] no usable particle shader found");
            return null;
        }

        private static Material FindDonor()
        {
            ParticleSystemRenderer best = null;
            int bestScore = 0;
            foreach (var r in UnityEngine.Object.FindObjectsOfType<ParticleSystemRenderer>(true))
            {
                if (r == null || r.sharedMaterial == null || r.sharedMaterial.shader == null) continue;
                var n = (r.name + " " + r.sharedMaterial.name).ToLowerInvariant();
                int score = 1;
                if (n.Contains("snow")) score += 4;
                if (n.Contains("wind") || n.Contains("blow") || n.Contains("dust")) score += 3;
                if (n.Contains("smoke") || n.Contains("mist") || n.Contains("fog")) score += 2;
                if (score > bestScore) { bestScore = score; best = r; }
            }
            return best != null ? best.sharedMaterial : null;
        }

        private static Texture2D SoftDisc()
        {
            const int N = 64;
            var tex = new Texture2D(N, N, TextureFormat.ARGB32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float dx = (x - N / 2f) / (N / 2f), dy = (y - N / 2f) / (N / 2f);
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    // squared falloff: a hard edge is what makes particles read as discs
                    float a = Mathf.Clamp01(1f - d);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a * a));
                }
            tex.Apply();
            return tex;
        }

        private void LateUpdate()
        {
            if (_failed || _ps == null) return;
            try
            {
                var player = Singleton<GameWorld>.Instance?.MainPlayer;
                var wc = WeatherController.Instance;
                if (player == null || wc == null) { SetRate(0f); return; }

                // indoors the gusts have no business existing, and the environment switcher
                // already knows which side of the hull you are on
                bool indoor = false;
                try
                {
                    var em = EnvironmentManager.Instance;
                    indoor = em != null && em.Environment == EnvironmentType.Indoor;
                }
                catch { }

                if (!Plugin.SnowGusts.Value || indoor) { SetRate(0f); return; }

                // wind is a world XZ vector on the weather curve. gusts ride it, so the
                // emitter sits UPWIND and everything blows through the player.
                Vector2 w;
                try { w = wc.WeatherCurve != null ? wc.WeatherCurve.Wind : Vector2.zero; }
                catch { w = Vector2.zero; }
                var wind = new Vector3(w.x, 0f, w.y);
                float windMag = wind.magnitude;
                var dir = windMag > 0.01f ? wind / windMag : Vector3.forward;

                // ride the same storm signal the native snow uses, so the gusts thicken
                // with the blizzard instead of arguing with it
                float storm = 0f;
                try
                {
                    if (_snow == null) _snow = UnityEngine.Object.FindObjectOfType<SnowFlakes>();
                    if (_snow != null) storm = Mathf.Clamp01(_snow.StormFactor);
                }
                catch { }

                float reach = Mathf.Max(5f, Plugin.SnowGustsReach.Value);
                _tf.position = player.Position + Vector3.up * 2f - dir * (reach * 0.5f);
                _tf.rotation = Quaternion.LookRotation(dir, Vector3.up);

                var shape = _ps.shape;
                shape.scale = new Vector3(reach * 1.5f, reach * 0.5f, 1f);

                var main = _ps.main;
                // the puffs must actually CROSS the player. they spawn reach*0.5 upwind,
                // and the first version set speed from the raw wind vector (~1-3 m/s):
                // over a 2.5-5s lifetime that is 8m of travel against a 22m offset, so
                // every puff died before arriving — a smoke bank parked off to one side.
                // floor the speed on the geometry (cover the offset plus a full pass-by
                // within a mid lifetime); wind and config only push it faster.
                const float lifeMid = 3.75f;
                float speed = Mathf.Max(
                    Mathf.Max(1f, windMag) * Plugin.SnowGustsSpeed.Value,
                    reach * 0.9f / lifeMid);
                main.startSpeed = new ParticleSystem.MinMaxCurve(speed * 0.8f, speed * 1.3f);
                main.startSize = new ParticleSystem.MinMaxCurve(
                    Plugin.SnowGustsSize.Value * 0.6f, Plugin.SnowGustsSize.Value * 1.6f);

                // storm drives density and opacity together: a calm day should be the odd
                // drifting wisp, a blizzard should be a wall of it
                float weight = Mathf.Lerp(0.25f, 1f, storm);
                main.startColor = new ParticleSystem.MinMaxGradient(
                    new Color(1f, 1f, 1f, Mathf.Clamp01(Plugin.SnowGustsOpacity.Value * weight)));
                SetRate(Plugin.SnowGustsDensity.Value * weight);
            }
            catch (Exception e)
            {
                _failed = true;
                Plugin.Log.LogWarning($"[Gusts] tick failed, gusts off: {e.Message}");
                SetRate(0f);
            }
        }

        private void SetRate(float r)
        {
            var em = _ps.emission;
            em.rateOverTime = new ParticleSystem.MinMaxCurve(Mathf.Max(0f, r));
        }
    }
}
