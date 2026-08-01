using System.Collections.Generic;
using Comfort.Common;
using EFT;
using Manimal.Icebreaker.Blowtorch;
using Fika.Core.Networking;
using UnityEngine;

namespace Manimal.Icebreaker.Fika
{
    // observed players' blowtorches run the STOCK UsableItemController — none of the
    // BlowtorchController fixes exist on remote views, and our ripped torch clips lost
    // the ThirdAction events the stock flow needs to pose the body (07-29 coop: the
    // burner's third-person body stuck in the old bugged state). this watcher mirrors
    // the essentials per observed player:
    //   equip torch   -> body FIRST_PERSON_ACTION = 1 (the draw pose fix)
    //   unequip torch -> body FIRST_PERSON_ACTION = 2 (the put-away fix)
    //   remote flame  -> applied by IceSyncHandler via SetRemoteFlame (packet kind 8)
    internal class IceTorchSync : MonoBehaviour
    {
        private readonly Dictionary<int, bool> _hadTorch = new Dictionary<int, bool>();
        private float _next;

        internal static void Ensure()
        {
            if (Object.FindObjectOfType<IceTorchSync>() != null) return;
            var go = new GameObject("Icebreaker_FikaTorchSync");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<IceTorchSync>();
        }

        private void Update()
        {
            if (Time.time < _next) return;
            _next = Time.time + 0.5f;
            try
            {
                if (!IceGate.On) return;
                var manager = Singleton<IFikaNetworkManager>.Instance;
                if (manager == null || manager.ObservedPlayers == null) return;
                foreach (var op in manager.ObservedPlayers)
                {
                    if (op == null) continue;
                    bool torchNow = op.HandsController != null && BlowtorchIds.IsTorch(op.HandsController.Item);
                    _hadTorch.TryGetValue(op.NetId, out var torchBefore);
                    if (torchNow == torchBefore) continue;
                    _hadTorch[op.NetId] = torchNow;
                    BlowtorchController.PokeBodyAction(op, torchNow ? 1 : 2);
                    FikaAddonPlugin.Log.LogInfo($"[TorchSync] '{op.Profile?.Nickname}' {(torchNow ? "drew" : "holstered")} the torch — body pose poked");
                }
            }
            catch (System.Exception e) { FikaAddonPlugin.Log.LogWarning($"[TorchSync] watcher failed: {e.Message}"); }
        }

        // remote flame toggle: drive the observed torch viewmodel's animator the same
        // way the local controller does — the flame VFX/states hang off the graph
        internal static void SetRemoteFlame(int netId, bool on)
        {
            try
            {
                var manager = Singleton<IFikaNetworkManager>.Instance;
                if (manager == null || manager.ObservedPlayers == null) return;
                foreach (var op in manager.ObservedPlayers)
                {
                    if (op == null || op.NetId != netId) continue;
                    if (op.HandsController == null || !BlowtorchIds.IsTorch(op.HandsController.Item)) return;
                    var anim = op.HandsController.ControllerGameObject != null
                        ? op.HandsController.ControllerGameObject.GetComponentInChildren<Animator>(true) : null;
                    if (anim == null) return;
                    anim.SetBool("Firing", on);
                    int layer = Mathf.Min(1, anim.layerCount - 1); // same clamp as the local controller
                    anim.CrossFade(on ? "torch_start" : "torch_end", 0.05f, layer);
                    return;
                }
            }
            catch (System.Exception e) { FikaAddonPlugin.Log.LogWarning($"[TorchSync] remote flame failed: {e.Message}"); }
        }
    }
}
