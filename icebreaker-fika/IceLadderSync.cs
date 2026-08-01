using System.Collections.Generic;
using Comfort.Common;
using EFT;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;
using tarkin.ladders.bep;
using tarkin.ladders.fika;
using tarkin.ladders.shared;
using UnityEngine;

namespace Manimal.Icebreaker.Fika
{
    // OUR ladder sync (07-30): tarkin's ladders.fika addon was built against an older
    // fika and its SEND path dies at runtime (MissingMethodException on the embedded
    // LiteNetLib's Put(string)) — so nobody ever sees anyone climb. his RECEIVE-side
    // machinery (ObservedPlayerLadderController) is healthy, so we send the state
    // through OUR packet channel (proven against 2.3.9) and drive his observed
    // controller on arrival.
    //
    // send-side detection POLLS instead of subscribing to his enter/exit events: his
    // broken tracker sits on those same events, and one thrown handler aborts the
    // whole invocation chain for everyone after it. the bar-angle event is subscribed
    // (his int/float packet serializes fine, so that chain survives) — if that ever
    // regresses, remote climbers just lose the roll detail, not the climb.
    internal class IceLadderSync : MonoBehaviour
    {
        private const float AngleSendCooldown = 0.05f; // tarkin's own 20/s cadence

        private PlayerLadderController _tracked;
        private string _trackedLadderId;
        private float _nextPoll, _nextAngle;

        internal static void Ensure()
        {
            if (Object.FindObjectOfType<IceLadderSync>() != null) return;
            var go = new GameObject("Icebreaker_FikaLadderSync");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<IceLadderSync>();
        }

        private void Update()
        {
            if (Time.time < _nextPoll) return;
            _nextPoll = Time.time + 0.1f;
            try
            {
                var main = Singleton<GameWorld>.Instance?.MainPlayer;
                var ctrl = main != null ? main.GetComponent<PlayerLadderController>() : null;
                bool climbing = ctrl != null && ctrl.Ladder != null;

                if (climbing && _tracked == null)
                {
                    _tracked = ctrl;
                    _trackedLadderId = ctrl.Ladder.NetId;
                    ctrl.OnBarAngleChanged += OnBarAngle;
                    SendState(main, _trackedLadderId, true);
                }
                else if (!climbing && _tracked != null)
                {
                    try { _tracked.OnBarAngleChanged -= OnBarAngle; } catch { }
                    SendState(main, _trackedLadderId, false);
                    _tracked = null;
                    _trackedLadderId = null;
                }
            }
            catch (System.Exception e) { FikaAddonPlugin.Log.LogWarning($"[LadderSync] poll failed: {e.Message}"); }
        }

        private static int LocalNetId()
        {
            var me = Singleton<GameWorld>.Instance?.MainPlayer as global::Fika.Core.Main.Players.FikaPlayer;
            return me != null ? me.NetId : -1;
        }

        private static void SendState(Player main, string ladderId, bool enter)
        {
            int netId = LocalNetId();
            if (netId < 0 || string.IsNullOrEmpty(ladderId)) return;
            IceSyncHandler.Send(new IceWorldPacket { Kind = 10, IntA = netId, DoorId = ladderId, Sealing = enter });
            FikaAddonPlugin.Log.LogInfo($"[LadderSync] {(enter ? "ENTER" : "EXIT")} ladder '{ladderId}'");
        }

        private void OnBarAngle(float angle)
        {
            if (Time.unscaledTime < _nextAngle) return;
            _nextAngle = Time.unscaledTime + AngleSendCooldown;
            int netId = LocalNetId();
            if (netId < 0) return;
            IceSyncHandler.Send(new IceWorldPacket { Kind = 11, IntA = netId, Value = angle }, DeliveryMethod.Sequenced, quiet: true);
        }

        // ---- receive side: drive tarkin's observed controller on the remote body ----

        private static Player FindObserved(int netId)
        {
            var manager = Singleton<IFikaNetworkManager>.Instance;
            if (manager == null || manager.ObservedPlayers == null) return null;
            foreach (var op in manager.ObservedPlayers)
                if (op != null && op.NetId == netId) return op;
            return null;
        }

        internal static void ApplyRemoteLadderState(int netId, string ladderId, bool enter)
        {
            try
            {
                var p = FindObserved(netId);
                if (p == null) return;
                if (enter)
                {
                    if (!Ladder.TryGetLadderInstanceByNetId(ladderId, out var ladder))
                    {
                        FikaAddonPlugin.Log.LogWarning($"[LadderSync] no ladder '{ladderId}' on this peer");
                        return;
                    }
                    var obs = p.gameObject.GetComponent<ObservedPlayerLadderController>();
                    if (obs == null) obs = p.gameObject.AddComponent<ObservedPlayerLadderController>();
                    obs.Init(ladder);
                }
                else
                {
                    var obs = p.gameObject.GetComponent<ObservedPlayerLadderController>();
                    if (obs != null) Object.Destroy(obs);
                }
            }
            catch (System.Exception e) { FikaAddonPlugin.Log.LogWarning($"[LadderSync] remote state failed: {e.Message}"); }
        }

        internal static void ApplyRemoteBarAngle(int netId, float angle)
        {
            try
            {
                var p = FindObserved(netId);
                var obs = p != null ? p.gameObject.GetComponent<ObservedPlayerLadderController>() : null;
                if (obs != null) obs.ReceiveBarAngle(angle);
            }
            catch { }
        }
    }
}
