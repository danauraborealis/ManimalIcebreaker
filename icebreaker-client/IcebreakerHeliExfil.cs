using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.Interactive;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace Manimal.Icebreaker
{
    // retail-style flare extraction: the heli exfil starts LOCKED; entering the retail
    // NotificationZone tells the player to signal with a green flare; firing an
    // exit-activation flare inside the zone unlocks the exfil. detection is 100% BSG's own
    // FlareShootDetectorZone (green-only via ammo FlareEventType, zone bounds, group
    // support, quest counters) — rebuilt at runtime because the ripped scene component
    // died. we only supply the missing exit-unlock consumer (a 1.0 feature 0.16.9 lacks).
    public class IcebreakerHeliExfil : MonoBehaviour
    {
        private const string ZoneId = "icebreaker_heli_zone";
        private const string ExitName = "Icebreaker_Exit_Heli";

        private ExfiltrationPoint _exit;
        private BoxCollider _exitCol;
        private Vector3 _exitHome;
        private Action _unsubscribe;
        private bool _called;     // flare accepted — heli inbound, animation running
        private bool _activated;  // heli landed — exfil open
        private float _lastEnterNotify = -999f;

        // the flown-in helicopter: INTERACTIVE_Helicopter_Extraction carries the
        // Icebreaker_extraction_helicopter controller; Call_01=true plays the 44s
        // arrival (Still -> Call). the exfil opens when the skids touch down.
        private const string HeliRigName = "INTERACTIVE_Helicopter_Extraction";
        private const string CallParam = "Call_01";
        private const string EuroTpl = "569668774bdc2da2298b4568";
        private const float ArrivalSeconds = 44f;

        private void Start()
        {
            _exit = FindObjectsOfType<ExfiltrationPoint>()
                .FirstOrDefault(e => e.Settings != null && e.Settings.Name == ExitName);
            if (_exit == null)
            {
                Plugin.Log.LogWarning($"[HeliExfil] no '{ExitName}' exfil in scene — flare gating skipped");
                Destroy(this);
                return;
            }

            _live = this; // remote heli-call applies route through the live component

            AttachFare();

            var zoneSrc = GameObject.Find("NotificationZone");
            if (zoneSrc == null || zoneSrc.GetComponent<BoxCollider>() == null)
            {
                Plugin.Log.LogDebug("[HeliExfil] no NotificationZone volume — heli exfil stays always-on");
                return;
            }

            // InfiltrationMatch LOWERCASES the player's entry point before comparing:
            // authored ["Icebreaker"] never matches "icebreaker", so every exfil-mod and
            // eligibility check saw this exit as not-for-this-player (InteractableExfils
            // then latched an EMPTY prompt into the interaction slot = no extract prompt).
            _exit.EligibleEntryPoints = new[] { "icebreaker" };

            // lock the exit until the flare — Update() HOLDS this: the exit's own
            // SetInitialStatus opens it (our config has no requirements) and a server
            // state-sync can re-apply that, either of which lands after this one-shot set.
            _exit.Status = EExfiltrationStatus.UncompleteRequirements;
            _exitCol = _exit.GetComponent<BoxCollider>();

            // THE definitive lock (user call): the whole point PHYSICALLY leaves the map
            // until the heli lands. no collider to toggle, no world marker on the pad,
            // nothing for any mod to prompt against. (exfil-mod compat used to live here
            // too — retired 08-07: TickIeapiDisable removes InteractableExfils from this
            // map entirely, so there is no prompt trigger left to chase.)
            _exitHome = _exit.transform.position;
            _exit.transform.position = _exitHome + Vector3.down * 1000f;

            BuildDetectorZone(zoneSrc.GetComponent<BoxCollider>());

            _unsubscribe = GlobalEventHandlerClass.Instance.SubscribeOnEvent<GClass3552>(OnZoneEvent);

            // rotor wash must not run before the heli exists: if the wind systems shipped
            // active/playOnAwake they'd blow from raid start — silence them until the call
            try
            {
                var rig = GameObject.Find(HeliRigName);
                if (rig != null)
                {
                    foreach (var t in rig.GetComponentsInChildren<Transform>(true))
                        if (t.name == "VFX")
                        {
                            foreach (var ps in t.GetComponentsInChildren<ParticleSystem>(true))
                                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                            break;
                        }
                    HealHeliMaterials(rig);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[HeliExfil] wind pre-stop failed: {e.Message}"); }

            Plugin.Log.LogDebug("[HeliExfil] armed — heli locked until a green flare is fired in the notification zone");
        }

        // the pilot doesn't fly for free. this is the same paid extract every V-Ex uses,
        // built the way ExfiltrationPoint.LoadSettings would have from a server exits
        // entry: TransferItemRequirement carrying a tpl and a count. we do it in code
        // because this exfil has no exits row at all — the point is baked in the scene and
        // runs on its own serialized settings, so there is nothing server-side to hang a
        // PassageRequirement off.
        //
        // Start() is what makes it a real till: it builds the fake stash and registers a
        // TraderControllerClass(EOwnerType.ExfilPoint) against the point's TRANSFORM, so
        // the money box follows the exfil when the flare gate exiles it under the map and
        // brings it back. paying calls OnItemTransferred, which queues the player, and
        // Met() is a QueuedPlayers check from then on.
        //
        // status is deliberately left alone: SetInitialStatus only forces
        // UncompleteRequirements for SharedTimer or WorldEvent requirements, so a plain
        // transfer requirement sits in RegularMode exactly like a vanilla car extract, and
        // the flare lock keeps owning the status.
        // idempotent, and called from MORE than raid start: the requirements array has
        // exactly two writers in the engine (LoadSettings — whose PassageRequirement=None
        // path assigns an EMPTY array — and OnDestroy), and a free ride was reported
        // 08-07 with the fare verifiably attached at raid start. so the fare re-asserts
        // at heli arrival and after any late LoadSettings instead of trusting one attach.
        private void EnsureFare(string when)
        {
            try
            {
                int fare = Plugin.HeliExfilCost.Value;
                if (fare <= 0 || _exit == null) return;
                if (_exit.Requirements != null && _exit.Requirements.OfType<TransferItemRequirement>().Any()) return;

                var req = ExfiltrationRequirement.CreateRequirement(ERequirementState.TransferItem) as ExfiltrationRequirement;
                if (req == null) { Plugin.Log.LogWarning("[HeliExfil] could not build the transfer requirement"); return; }

                req.Requirement = ERequirementState.TransferItem;
                req.Id = EuroTpl;
                req.Count = fare;
                // "Bring {0}", the same key every vanilla paid extract uses — the tip
                // formats the item's ShortName in and shows any discount alongside
                req.RequirementTip = "EXFIL_Item";
                req.Start(_exit);

                _exit.Requirements = new ExfiltrationRequirement[] { req };
                Plugin.Log.LogWarning($"[HeliExfil] fare attached ({when}): {fare} euros to board");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[HeliExfil] fare attach failed ({when}), ride stays free: {e.Message}"); }
        }

        private void AttachFare() => EnsureFare("raid start");

        // a LoadSettings that lands AFTER our attach rebuilds Requirements from the
        // server exits row (PassageRequirement None = EMPTY) — the one engine path that
        // silently deletes the fare. re-assert immediately and say so.
        [HarmonyPatch(typeof(ExfiltrationPoint), nameof(ExfiltrationPoint.LoadSettings))]
        internal static class Patch_FareSurvivesLoadSettings
        {
            [HarmonyPostfix]
            private static void Postfix(ExfiltrationPoint __instance)
            {
                try
                {
                    if (!IceGate.On || _live == null || __instance == null) return;
                    if (__instance.Settings == null || __instance.Settings.Name != ExitName) return;
                    Plugin.Log.LogWarning("[HeliExfil] LoadSettings ran on the heli exit AFTER raid start — re-asserting the fare");
                    _live.EnsureFare("post-LoadSettings");
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[HeliExfil] LoadSettings re-assert failed: {e.Message}"); }
            }
        }

        // forensics for the free-ride report: every Proceed on the heli logs what the
        // requirement check actually saw. one line per entry, cheap, and it turns the
        // next "it was free" into an immediate diagnosis instead of a deduction chain.
        [HarmonyPatch(typeof(ExfiltrationPoint), nameof(ExfiltrationPoint.Proceed))]
        internal static class Patch_ProceedDiag
        {
            [HarmonyPostfix]
            private static void Postfix(ExfiltrationPoint __instance, Player player)
            {
                try
                {
                    if (!IceGate.On || __instance?.Settings == null || __instance.Settings.Name != ExitName) return;
                    if (player == null || !player.IsYourPlayer) return;
                    int reqs = __instance.Requirements?.Length ?? 0;
                    int unmet = __instance.UnmetRequirements(player).Count();
                    bool queued = __instance.QueuedPlayers.Contains(player.ProfileId);
                    Plugin.Log.LogWarning($"[HeliExfil] Proceed: requirements={reqs} unmet={unmet} queued={queued} "
                        + $"status={__instance.Status} met={__instance.HasMetRequirements(player.ProfileId)}");
                }
                catch { }
            }
        }

        // FLAT FARE. the pilot is not Fence and does not do loyalty deals: 2400 euros is
        // 2400 euros. Profile.GetExfiltrationPrice stacks three multipliers on any
        // TransferItem requirement — fence loyalty (ExfiltrationPriceModifier), Mark of
        // Unknown (TradersDiscount) and charisma (CharismaExfiltrationDiscount) — and it is
        // the same call that computes the amount ACTUALLY charged in TransferExitItem, not
        // just the number on the prompt. so patching the function itself is the only place
        // all three call sites (tip, action count, transfer) agree.
        //
        // scoped hard: icebreaker only, and only for a price exactly equal to our configured
        // fare. our heli requirement is the map's only TransferItem requirement (the map
        // crossing fare is a pre-raid grid removal, not this path), so nothing else
        // on this map can collide — and every paid extract on every OTHER map keeps its
        // discounts untouched.
        [HarmonyPatch(typeof(Profile), nameof(Profile.GetExfiltrationPrice))]
        internal static class Patch_FlatHeliFare
        {
            [HarmonyPostfix]
            private static void Postfix(int price, ref int __result)
            {
                if (!IceGate.On) return;
                int fare = Plugin.HeliExfilCost.Value;
                if (fare <= 0 || price != fare) return;
                if (__result == price) return;      // no discount applied, nothing to undo
                __result = price;
            }
        }

        // the NATIVE pay prompt. the game already has the whole flow: Proceed sets
        // Player.ExfiltrationPoint, GamePlayerOwner.InteractionsChangedHandler polls its
        // interaction sources, and GetActionsClass.smethod_4 builds the "EXFIL_Transfer"
        // action whose press calls TransferExitItem — the same discounted, networked
        // transfer a vanilla car extract runs. the catch is the source CHAIN: the exfil
        // point is the LAST fallback in the handler, so any earlier source (a raycast
        // interactable, an exfil-mod trigger) wins the slot outright and the pay action
        // never surfaces. so after the native handler settles the slot, merge the pay
        // action in instead of letting first-wins eat it. no charge without a prompt:
        // paying is the player pressing this action, exactly like a car extract.
        [HarmonyPatch(typeof(GamePlayerOwner), nameof(GamePlayerOwner.InteractionsChangedHandler))]
        internal static class Patch_NativePayPrompt
        {
            [HarmonyPostfix]
            private static void Postfix(GamePlayerOwner __instance)
            {
                try
                {
                    if (!IceGate.On) return;
                    var player = __instance != null ? __instance.Player : null;
                    if (player == null || !player.IsYourPlayer) return;
                    var point = player.ExfiltrationPoint;
                    if (point == null || point.Settings == null || point.Settings.Name != ExitName) return;
                    var req = point.TransferItemRequirement;
                    if (req == null || point.QueuedPlayers.Contains(player.ProfileId)) return;

                    // native builder: returns null when already paid or when no single
                    // stack covers the (discounted) price — same rule as a car extract
                    var native = GetActionsClass.smethod_4(__instance, point);
                    if (native == null || native.Actions == null || native.Actions.Count == 0) return;

                    var state = __instance.AvailableInteractionState.Value;
                    if (state == null)
                    {
                        native.InitSelected();
                        __instance.AvailableInteractionState.Value = native;
                        return;
                    }

                    string payName = native.Actions[0].Name;
                    foreach (var a in state.Actions)
                        if (a != null && a.Name == payName) return;   // already offered

                    // fresh object on purpose: the bindable only notifies the prompt UI on
                    // a reference change, mutating the current list would redraw nothing
                    var merged = new ActionsReturnClass { Actions = new List<ActionsTypesClass>() };
                    merged.Actions.AddRange(native.Actions);
                    merged.Actions.AddRange(state.Actions);
                    merged.InitSelected();
                    __instance.AvailableInteractionState.Value = merged;
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[HeliExfil] pay prompt merge failed: {e.Message}"); }
            }
        }

        // reconstruct BSG's FlareShootDetectorZone over the retail NotificationZone bounds.
        // built on an INACTIVE child so the private fields are set before Awake subscribes.
        private void BuildDetectorZone(BoxCollider src)
        {
            var go = new GameObject("Icebreaker_FlareDetector");
            go.SetActive(false);
            go.layer = src.gameObject.layer;
            go.transform.SetPositionAndRotation(src.transform.position, src.transform.rotation);

            var col = go.AddComponent<BoxCollider>();
            col.center = src.center;
            col.size = src.size; // EXACT retail bounds — padding let a flare register from under the pad (user call: dont touch trigger sizes)
            col.isTrigger = true;

            var handler = go.AddComponent<PhysicsTriggerHandler>();
            handler.trigger = col;

            var zone = go.AddComponent<FlareShootDetectorZone>();
            AccessTools.Field(typeof(FlareShootDetectorZone), "zoneID").SetValue(zone, ZoneId);
            AccessTools.Field(typeof(FlareShootDetectorZone), "flareTypeForHandle").SetValue(zone, FlareEventType.ExitActivate);
            AccessTools.Field(typeof(FlareShootDetectorZone), "_triggerHandlers")
                .SetValue(zone, new System.Collections.Generic.List<PhysicsTriggerHandler> { handler });

            go.SetActive(true); // NOW Awake runs with everything wired
        }

        // hold the lock: anything that flips the exit off UncompleteRequirements before
        // the flare (SetInitialStatus, server sync, an errant countdown from the player
        // standing in it) gets slapped back. once the flare fires we stop and release.
        // no Update() status-slapping, no collider fights, no prompt-latch ejection: the
        // teleport lock supersedes the whole patch pile — a point a kilometer under the
        // ship can't be prompted, toggled or extracted through, whatever any mod does.

        private void OnZoneEvent(GClass3552 ev)
        {
            try
            {
                if (ev.ZoneID != ZoneId || _called) return;

                // every event, always: last raid the flare only registered on a late
                // attempt and the log couldn't say why the earlier shots didn't count
                // (outside the box? wrong cartridge — acid green is AIFollow, not
                // ExitActivate). now each enter/exit/shot shows up.
                Plugin.Log.LogDebug($"[HeliExfil] zone event: {ev.ZoneEventType} (profile {ev.PlayerProfileID})");

                if (ev.ZoneEventType == GClass3552.EZoneEventType.PlayerEnteredZone
                    && ev.PlayerProfileID == Singleton<GameWorld>.Instance?.MainPlayer?.ProfileId
                    && Time.time - _lastEnterNotify > 60f)
                {
                    _lastEnterNotify = Time.time;
                    NotificationManagerClass.DisplayMessageNotification(
                        "Signal the helicopter with a green flare to extract",
                        ENotificationDurationType.Long, ENotificationIconType.Default, Color.white);
                }

                if (ev.ZoneEventType == GClass3552.EZoneEventType.FiredPlayerAddedInShotList
                    || ev.ZoneEventType == GClass3552.EZoneEventType.PlayerByPartyAddedInShotList)
                {
                    CallHeli(remote: false);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[HeliExfil] event handling failed: {e.Message}"); }
        }

        // world-event hook for the fika sync addon, same contract as the chain door's:
        // one flare calls the bird for EVERYONE — the flight/audio/exfil-unlock runs
        // locally on each peer, only the call moment crosses the wire.
        internal static event Action HeliCalled;
        private static IcebreakerHeliExfil _live; // the raid's component, for remote applies

        internal static void ApplyRemoteCall()
        {
            if (_live != null) _live.CallHeli(remote: true);
            else Plugin.Log.LogWarning("[HeliExfil] remote heli call but no live exfil component — ignored");
        }

        private void CallHeli(bool remote)
        {
            try
            {
                if (_called) return;
                _called = true; // one flare is enough — further shots are fireworks
                if (!remote)
                {
                    try { HeliCalled?.Invoke(); }
                    catch (Exception e) { Plugin.Log.LogWarning($"[HeliExfil] world-event hook failed: {e.Message}"); }
                }
                var rig = GameObject.Find(HeliRigName);
                    var anim = rig != null ? rig.GetComponentInChildren<Animator>(true) : null;
                    if (anim != null)
                    {
                        anim.SetBool(CallParam, true); // Still -> Call, one-way
                        StartHeliAudio(anim);          // synced to the same moment as the animation
                        StartCoroutine(WindSchedule(anim)); // rotor-wash snow VFX on the flight timeline
                        // native culling fades the parked rig's lights to intensity 0 (300m
                        // out) and would re-fade any one-shot heal next frame — free them
                        // from the manager for good; a flying bird's lights shine from afar,
                        // and the rig's animation still drives blink states on top.
                        int freed = RenderEnvProbe.FreeNativeLights(rig.transform);
                        int lit = 0;
                        foreach (var hl in anim.GetComponentsInChildren<Light>(true))
                            if (!hl.enabled) { hl.enabled = true; lit++; }
                        Plugin.Log.LogDebug($"[HeliExfil] green flare accepted — {CallParam} set, heli inbound ({ArrivalSeconds:0}s flight), {freed} native lights freed + {lit} plain re-lit");
                    }
                    else
                        Plugin.Log.LogWarning($"[HeliExfil] '{HeliRigName}' rig/animator not found — skipping the flight, unlocking on the timer anyway");
                    NotificationManagerClass.DisplayMessageNotification(
                        "The helicopter has been signaled — inbound, hold the pad",
                        ENotificationDurationType.Long, ENotificationIconType.Default, Color.green);
                    StartCoroutine(HeliArrival());
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[HeliExfil] heli call failed: {e.Message}"); }
        }

        // the heli + pilots rendered transparent/unlit: the bundle carries AssetRipper
        // DUMMY shader assets (name preserved, body dead) — the snow flakes disease. the
        // game has the real shaders loaded, so rebind every broken material by shader
        // NAME to the native copy. unresolvable names get logged for manual mapping.
        private static void HealHeliMaterials(GameObject rig)
        {
            int rebound = 0, dead = 0, shadowFixed = 0;
            var seen = new System.Collections.Generic.HashSet<Material>();
            foreach (var r in rig.GetComponentsInChildren<Renderer>(true))
            {
                // SHADOW_* meshes are caster-only in retail; the rip dropped that renderer
                // flag and they drew as an opaque white shell over the whole airframe (the
                // 'blinding white heli' — its own materials were fine underneath, textures
                // and all). ShadowsOnly restores the authored behavior.
                if (r.name.Contains("SHADOW") && r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly)
                {
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
                    shadowFixed++;
                    continue; // its materials never render — no point dumping them
                }

                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || !seen.Add(m)) continue;
                    var sh = m.shader;
                    // isSupported LIES: the bundle ships the RIP's recompiled copy of the
                    // p0 shaders — it compiles (so the old broken-check skipped it) but its
                    // reconstructed variant set renders the DYNAMIC-object pass white (the
                    // supernova heli; live bots are fine because their gear binds the
                    // game's own shader instances). force every material onto the game
                    // registry's instance whenever it differs.
                    bool broken = sh == null || !sh.isSupported || sh.name.Contains("InternalError");
                    if (!broken && sh != null)
                    {
                        Shader reg = null;
                        try { reg = GClass872.Find(sh.name); } catch { }
                        if (reg != null && reg.isSupported && !ReferenceEquals(reg, sh))
                        {
                            m.shader = reg;
                            rebound++;
                            Plugin.Log.LogDebug($"[HeliExfil] '{m.name}': bundle copy of '{sh.name}' swapped for the game's registry instance");
                            continue;
                        }
                    }
                    // name every material either way — the blinding-white body needs the
                    // actual shader identified before it can be fixed
                    Plugin.Log.LogWarning($"[HeliExfil] heli material '{m.name}' on '{r.name}': shader '{(sh != null ? sh.name : "null")}' supported={(sh != null && sh.isSupported)}{(broken ? " -> rebinding" : "")} keywords=[{string.Join(",", m.shaderKeywords)}] lmIndex={r.lightmapIndex} probes={r.lightProbeUsage}");
                    // stale rip keywords select wrong shader VARIANTS (the doors — same
                    // shader family, also dynamic — render fine; the difference has to be
                    // per-material state). clearing keywords falls back to the shader's
                    // default variant.
                    if (m.shaderKeywords != null && m.shaderKeywords.Length > 0)
                    {
                        Plugin.Log.LogDebug($"[HeliExfil]   cleared {m.shaderKeywords.Length} keyword(s) on '{m.name}'");
                        m.shaderKeywords = new string[0];
                    }
                    // full property sheet: the white-out is a VALUE problem on a working
                    // shader (reflectivity? emission? missing cubemap?) — name the knobs
                    if (sh != null && sh.isSupported)
                    {
                        try
                        {
                            var props = new System.Collections.Generic.List<string>();
                            int n = sh.GetPropertyCount();
                            for (int i = 0; i < n; i++)
                            {
                                var pname = sh.GetPropertyName(i);
                                switch (sh.GetPropertyType(i))
                                {
                                    case UnityEngine.Rendering.ShaderPropertyType.Float:
                                    case UnityEngine.Rendering.ShaderPropertyType.Range:
                                        props.Add($"{pname}={m.GetFloat(pname):0.###}"); break;
                                    case UnityEngine.Rendering.ShaderPropertyType.Color:
                                        props.Add($"{pname}={m.GetColor(pname)}"); break;
                                    case UnityEngine.Rendering.ShaderPropertyType.Texture:
                                        var tex = m.GetTexture(pname);
                                        props.Add($"{pname}={(tex != null ? tex.name : "NULL")}"); break;
                                }
                            }
                            Plugin.Log.LogDebug($"[HeliExfil]   props: {string.Join(" | ", props)}");
                        }
                        catch (Exception pe) { Plugin.Log.LogWarning($"[HeliExfil]   prop dump failed: {pe.Message}"); }
                    }
                    if (!broken) continue;
                    // GClass872 = the game's shader registry (what fixed the snow); plain
                    // Shader.Find can hand back the bundle's own dead copy on a name tie.
                    // SMap variants are the LIGHTMAPPED family — on a dynamic object the
                    // lightmap sample is the default white texture = the supernova heli.
                    // bind the dynamic (suffix-less) variant instead when it exists.
                    Shader native = null;
                    if (sh != null)
                    {
                        var wantName = sh.name.EndsWith(" SMap") ? sh.name.Substring(0, sh.name.Length - 5) : sh.name;
                        try { native = GClass872.Find(wantName); } catch { }
                        if (native == null || !native.isSupported) native = Shader.Find(wantName);
                        if ((native == null || !native.isSupported) && wantName != sh.name)
                        {
                            Plugin.Log.LogWarning($"[HeliExfil] no dynamic variant '{wantName}' — falling back to the SMap original");
                            try { native = GClass872.Find(sh.name); } catch { }
                            if (native == null || !native.isSupported) native = Shader.Find(sh.name);
                        }
                    }
                    if (native != null && native.isSupported && native != sh) { m.shader = native; rebound++; }
                    else
                    {
                        dead++;
                        Plugin.Log.LogDebug($"[HeliExfil] material '{m.name}': shader '{(sh != null ? sh.name : "null")}' has no native match");
                    }
                }
            }
            Plugin.Log.LogDebug($"[HeliExfil] heli material heal: {rebound} shader(s) rebound, {dead} unresolved, {shadowFixed} SHADOW mesh(es) set caster-only");

            // the control group: a door — same shader family, also dynamic (1U), renders
            // CORRECTLY. whatever state differs between this line and the heli lines above
            // is the white-out culprit.
            try
            {
                var door = UnityEngine.Object.FindObjectOfType<EFT.Interactive.Door>();
                var dr = door != null ? door.GetComponentInChildren<Renderer>() : null;
                var dm = dr != null ? dr.sharedMaterial : null;
                if (dm != null)
                    Plugin.Log.LogDebug($"[HeliExfil] REFERENCE door material '{dm.name}': shader '{dm.shader.name}' keywords=[{string.Join(",", dm.shaderKeywords)}] lmIndex={dr.lightmapIndex} probes={dr.lightProbeUsage}");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[HeliExfil] reference dump failed: {e.Message}"); }
        }

        // heli flight audio, all scheduled up front at call time. POSITIONAL, on the rig:
        // the old 2D reading ("retail's carrier is parked at origin, the mix is the
        // distance cue") mistook the carrier for the emitter — level704's Heli_sound GO
        // holds HandlerPlaySoundSequenced/Advanced + PlaybackStateSync, BSG's positional
        // trigger-sound machinery, and the handler config carries its own sound position
        // (_overrideSoundPosition). the rig starts ~350m out and flies in, so a source ON
        // the rig gives the approach, the flyby pan and a pad-localised idle for free —
        // 2D was "the helicopter is everywhere at once" (07-30). exact retail radii are
        // locked in the drifted handler struct, so min/max here are ours, tuned by ear.
        //   helicopter_exit_start    at 0s      (46.3s — the whole approach)
        //   helicopter_exit_landing  at 37s     (8.5s — touchdown layer, ends ~45.5s)
        //   helicopter_exit_loop     at start's end, looping (idle on the pad)
        private static AudioClip FindClip(string name)
        {
            foreach (var c in Resources.FindObjectsOfTypeAll<AudioClip>())
                if (c != null && c.name == name) return c;
            return null;
        }

        private void StartHeliAudio(Animator anim)
        {
            var start = FindClip("helicopter_exit_start");
            var landing = FindClip("helicopter_exit_landing");
            var loop = FindClip("helicopter_exit_loop");
            if (start == null || landing == null || loop == null)
                Plugin.Log.LogWarning($"[HeliExfil] heli foley incomplete (start={start != null} landing={landing != null} loop={loop != null}) — clips missing from bundle? rerun 1R + rebuild");

            // ON THE RIG, not the origin-parked carrier — the sound flies in with the
            // helicopter and settles on the pad
            Transform host = anim.transform;

            AudioSource Make(AudioClip c, bool looped)
            {
                var src = host.gameObject.AddComponent<AudioSource>();
                src.clip = c;
                src.loop = looped;
                src.playOnAwake = false;
                src.spatialBlend = 1f;
                // a K-32 is LOUD: generous minDistance so the whole pad area gets full
                // level (and the wav's own baked approach ramp still shapes the far
                // field), 500m reach so the ship never fully loses it, log falloff.
                src.minDistance = 25f;
                src.maxDistance = 500f;
                src.rolloffMode = AudioRolloffMode.Logarithmic;
                src.dopplerLevel = 0f;   // animated rig + doppler = pitch warble, retail handlers ship 0
                src.spread = 60f;        // some stereo width up close instead of a hard point
                src.priority = 64;       // the story sound on the map — never channel-starved
                return src;
            }
            if (start != null) Make(start, false).Play();
            if (landing != null) Make(landing, false).PlayDelayed(37f);
            if (loop != null) Make(loop, true).PlayDelayed(start != null ? start.length : 46f);
        }

        // rotor-wash snow VFX under the rig's VFX group, on the flight timeline:
        //   Wind_start_01 at 30s, Wind_start_02 at 36s (one-shot approach gusts),
        //   Wind_state at 43s — loops as long as the heli sits on the pad (its systems
        //   are authored looping; we just start them and never stop)
        private System.Collections.IEnumerator WindSchedule(Animator anim)
        {
            Transform vfx = null;
            foreach (var t in anim.GetComponentsInChildren<Transform>(true))
                if (t.name == "VFX") { vfx = t; break; }
            if (vfx == null) { Plugin.Log.LogWarning("[HeliExfil] no VFX group under the heli rig — wind skipped"); yield break; }

            Transform Wind(string name) { var t = vfx.Find(name); if (t == null) Plugin.Log.LogWarning($"[HeliExfil] wind '{name}' missing"); return t; }
            var s1 = Wind("Wind_start_01");
            var s2 = Wind("Wind_start_02");
            var st = Wind("Wind_state");

            void Blow(Transform t)
            {
                if (t == null) return;
                if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
                foreach (var ps in t.GetComponentsInChildren<ParticleSystem>(true))
                    ps.Play();
            }

            yield return new WaitForSeconds(30f);
            Blow(s1);
            yield return new WaitForSeconds(6f);  // t=36
            Blow(s2);
            yield return new WaitForSeconds(7f);  // t=43
            Blow(st);
            Plugin.Log.LogInfo("[HeliExfil] wind state live — rotor wash looping");
        }

        // the exfil stays LOCKED while the heli flies its 44s arrival — Update()'s re-lock
        // hold keeps running because _activated is still false. unlock lands with the skids.
        // ENTRY-PATH-AGNOSTIC ELIGIBILITY. vanilla InfiltrationMatch demands the player's
        // Profile.Info.EntryPoint be non-empty AND present in EligibleEntryPoints — and a
        // TRANSIT arrival satisfies neither reliably (its entry point is empty or carries
        // the origin map's value). the failure is perfectly silent: the exfil collider is
        // on, the player stands inside, and no timer ever starts — measured 2026-08-03 as
        // "IEAPI toggle does nothing" on a transited fresh-install raid, while menu-entry
        // raids (EntryPoint='Icebreaker' from base.json) always worked. on OUR map every
        // exit is authored by us for every entry path, so the match is simply YES here.
        [HarmonyPatch(typeof(EFT.Interactive.ExfiltrationPoint), nameof(EFT.Interactive.ExfiltrationPoint.InfiltrationMatch))]
        internal static class Patch_EntryAgnosticExfil
        {
            private static bool _logged;

            [HarmonyPostfix]
            private static void Postfix(Player player, ref bool __result)
            {
                if (__result || !IceGate.On) return;
                if (player == null || !player.IsYourPlayer) return;
                __result = true;
                if (!_logged)
                {
                    _logged = true;
                    Plugin.Log.LogInfo($"[HeliExfil] exfil eligibility forced for entry point "
                        + $"'{player.Profile?.Info?.EntryPoint ?? "<null>"}' — vanilla match rejects transit arrivals");
                }
            }
        }

        private System.Collections.IEnumerator HeliArrival()
        {
            yield return new WaitForSeconds(ArrivalSeconds);
            _activated = true;
            EnsureFare("heli arrival"); // last line of defense — never open the doors on a wiped fare
            _exit.transform.position = _exitHome; // bring the point home
            if (_exitCol != null) _exitCol.enabled = true; // trigger back online with the skids
            _exit.Status = EExfiltrationStatus.RegularMode;
            NotificationManagerClass.DisplayMessageNotification(
                "The helicopter has landed — extraction active",
                ENotificationDurationType.Long, ENotificationIconType.Default, Color.green);
            Plugin.Log.LogDebug("[HeliExfil] heli arrived — exfil UNLOCKED");
        }

        private void OnDestroy()
        {
            if (_live == this) _live = null;
            _unsubscribe?.Invoke();
            _unsubscribe = null;
        }
    }
}
