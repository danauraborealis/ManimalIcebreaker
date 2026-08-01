using System.Text;
using Comfort.Common;
using EFT;
using EFT.Communications;
using Fika.Core.Networking;
using UnityEngine;

namespace Manimal.Icebreaker.Fika
{
    // TEMPORARY observed-body diagnostic (07-28): on fika CLIENTS, alive bots render
    // as floating gear — the body is hidden until death reveals it. the meshes exist
    // (death shows them), so something hides them: renderer.enabled, forceRenderingOff
    // (EFT's first-person hiding flag), layer, an inactive GO, or a dead shader.
    //
    // ProbeKey (default F11): dumps the observed bot nearest to where the camera is
    // POINTING, full renderer detail, with an on-screen toast so the tester knows it
    // fired. a 20s background sample of the nearest bot runs as backup.
    // remove once the invisible-bot bug is fixed.
    internal class IceBodyDiag : MonoBehaviour
    {
        private float _next;
        private float _nextState;

        // chainloader-time AddComponent produced a component whose Update NEVER ran
        // (three silent raids, not even the unconditional state log) — create/verify
        // at RAID time instead, from the network-manager event, like every component
        // in this mod that actually works
        internal static void Ensure()
        {
            if (UnityEngine.Object.FindObjectOfType<IceBodyDiag>() != null) return;
            var go = new GameObject("Icebreaker_FikaBodyDiag");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<IceBodyDiag>();
            FikaAddonPlugin.Log.LogDebug("[BodyDiag] probe component created (raid-time)");
        }

        private void Start() => FikaAddonPlugin.Log.LogDebug("[BodyDiag] probe alive — Update ticking");

        // the probe was silent for THREE raids while its host component demonstrably
        // ran — some early-out below trips every frame. name the blocker once a minute
        // instead of guessing which.
        private void LogState()
        {
            if (Time.time < _nextState) return;
            _nextState = Time.time + 60f;
            try
            {
                var manager = Singleton<IFikaNetworkManager>.Instance;
                FikaAddonPlugin.Log.LogDebug($"[BodyDiag] state: iceGate={IceGate.On} manager={(manager != null)}"
                    + $" observed={(manager != null && manager.ObservedPlayers != null ? manager.ObservedPlayers.Count.ToString() : "n/a")}"
                    + $" cam={(GameCam() != null)}");
            }
            catch (System.Exception e) { FikaAddonPlugin.Log.LogDebug($"[BodyDiag] state failed: {e.Message}"); }
        }

        private void Update()
        {
            try
            {
                LogState();
                if (!IceGate.On) return;
                if (FikaAddonPlugin.ProbeKey != null && FikaAddonPlugin.ProbeKey.Value.IsDown())
                {
                    var aimed = PickObserved(byAim: true);
                    if (aimed != null)
                    {
                        Dump(aimed, "AIMED", 999);
                        Toast($"BodyDiag captured '{aimed.Profile?.Nickname}' — in the log");
                    }
                    else Toast("BodyDiag: no observed player found");
                    return;
                }
                if (Time.time < _next) return;
                _next = Time.time + 20f;
                var near = PickObserved(byAim: false);
                if (near != null) Dump(near, "nearest", 6);
            }
            catch (System.Exception e) { FikaAddonPlugin.Log.LogDebug($"[BodyDiag] failed: {e.Message}"); }
        }

        // Camera.main is null in EFT (the FPS camera isn't tagged MainCamera) — the
        // probe silently sampled NOTHING for two raids because of it. CameraClass is
        // the real accessor.
        private static Camera GameCam()
        {
            try
            {
                var cc = CameraClass.Instance;
                if (cc != null && cc.Camera != null) return cc.Camera;
            }
            catch { }
            return Camera.main;
        }

        private static Player PickObserved(bool byAim)
        {
            var manager = Singleton<IFikaNetworkManager>.Instance;
            if (manager == null || manager.ObservedPlayers == null || manager.ObservedPlayers.Count == 0) return null;
            var cam = GameCam();
            if (cam == null) return null;

            Player best = null;
            float bestScore = float.MaxValue;
            foreach (var op in manager.ObservedPlayers)
            {
                if (op == null || op.HealthController == null || !op.HealthController.IsAlive) continue;
                var to = op.Position + Vector3.up - cam.transform.position;
                float score = byAim
                    ? Vector3.Angle(cam.transform.forward, to) + to.magnitude * 0.05f // mostly aim, slight distance bias
                    : to.sqrMagnitude;
                if (score < bestScore) { bestScore = score; best = op; }
            }
            return best;
        }

        private static void Dump(Player p, string how, int cap)
        {
            var cam = GameCam();
            float dist = cam != null ? Vector3.Distance(cam.transform.position, p.Position) : -1f;
            var sb = new StringBuilder();
            sb.AppendLine($"[BodyDiag] ({how}) '{p.Profile?.Nickname}' dist={dist:0}m goActive={p.gameObject.activeInHierarchy} layer={p.gameObject.layer} pov={p.PointOfView}");
            int shown = 0;
            foreach (var r in p.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (shown++ >= cap) { sb.AppendLine("  ...skinned capped"); break; }
                sb.AppendLine($"  skin '{r.name}': en={r.enabled} act={r.gameObject.activeInHierarchy} off={r.forceRenderingOff} layer={r.gameObject.layer} shadow={r.shadowCastingMode} shader={(r.sharedMaterial != null && r.sharedMaterial.shader != null ? r.sharedMaterial.shader.name : "NULL")}");
            }
            int meshShown = 0;
            foreach (var r in p.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (meshShown++ >= 8) { sb.AppendLine("  ...mesh capped"); break; }
                sb.AppendLine($"  mesh '{r.name}': en={r.enabled} act={r.gameObject.activeInHierarchy} off={r.forceRenderingOff} layer={r.gameObject.layer}");
            }
            FikaAddonPlugin.Log.LogDebug(sb.ToString());
        }

        private static void Toast(string text)
        {
            try
            {
                NotificationManagerClass.DisplayMessageNotification(
                    text, ENotificationDurationType.Default, ENotificationIconType.Default, Color.cyan);
            }
            catch { }
        }
    }
}
