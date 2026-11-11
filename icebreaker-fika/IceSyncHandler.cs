using System;
using Comfort.Common;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;
using Fika.Core.Networking.LiteNetLib.Utils;
using HarmonyLib;
using EFT;
using Manimal.Icebreaker.Blowtorch;
using UnityEngine;

namespace Manimal.Icebreaker.Fika
{
    // one tiny reliable packet for all icebreaker world events — these are rare
    // one-shot state changes, so none of the wiki's throttling/snapshotting applies
    public struct IceWorldPacket : INetSerializable
    {
        public byte Kind;      // 0/1 chain plant/open, 2 seal, 3 heli, 4 progress door,
                               // 5-7 hatch stages, 8 torch flame, 9 cutscene activation,
                               // 10 ladder enter/exit, 11 ladder bar angle,
                               // 12 tripwire layout seed, 13 keypad unlock (doorId),
                               // 14 authoritative Combine selection, 15 selection request
        public string DoorId;  // seal doorId / profileId / ladder NetId, per kind
        public bool Sealing;   // seal state / torch flame / ladder enter
        public int IntA;       // player NetId (torch) / raid selection token (14/15)
        public float Value;    // ladder bar angle

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(Kind);
            writer.Put(DoorId ?? "");
            writer.Put(Sealing);
            writer.Put(IntA);
            writer.Put(Value);
        }

        public void Deserialize(NetDataReader reader)
        {
            Kind = reader.GetByte();
            DoorId = reader.GetString();
            Sealing = reader.GetBool();
            IntA = reader.GetInt();
            Value = reader.GetFloat();
        }
    }

    internal class IceSyncHandler : IDisposable
    {
        internal IceSyncHandler()
        {
            IcebreakerCombineEncounter.RaidReset += OnRaidReset;
            IcebreakerCombineEncounter.SelectionReady += OnSelectionReady;
            FikaEventDispatcher.SubscribeEvent<FikaNetworkManagerCreatedEvent>(OnManagerCreated);
            if (Singleton<IFikaNetworkManager>.Instantiated)
                Register(Singleton<IFikaNetworkManager>.Instance);

            IcebreakerChainDoor.WorldEvent += OnChainEvent;
            IcebreakerSealedDoors.SealEvent += OnSealEvent;
            IcebreakerHeliExfil.HeliCalled += OnHeliCalled;
            IcebreakerCrew.ProgressDoorUnlocked += OnProgressDoor;
            HatchMeltDriver.HatchStage += OnHatchStage;
            BlowtorchController.LocalTorchFiring += OnTorchFiring;
            IcebreakerCrew.CutsceneActivated += OnCutsceneActivated;
            IcebreakerTripwires.SeedRolled += OnTripwireSeed;
            Manimal.Icebreaker.Keypad.Keypad.UnlockCommitted += OnKeypadUnlock;
        }

        private SelectionRetryPump _selectionRetryPump;
        private int _selectionRequests;
        private float _selectionDeadline;
        private bool _selectionAwaitingReply;
        private bool _disposed;

        private void OnManagerCreated(FikaNetworkManagerCreatedEvent ev) => Register(ev.Manager);

        private void Register(IFikaNetworkManager manager)
        {
            manager.RegisterPacket<IceWorldPacket>(OnReceived);
            IceBodyDiag.Ensure(); // raid-time creation — the chainloader-time component never ticked
            IceTorchSync.Ensure();
            if (!FikaBridge.BotsAuthority && IcebreakerCombineEncounter.RaidInitialized)
                StartSelectionRequests();
            FikaAddonPlugin.Log.LogInfo("[IceSync] packet registered");
        }

        private void OnRaidReset()
        {
            if (_disposed || FikaBridge.BotsAuthority) return;

            // A manager can survive a raid transition. Reset the request budget and
            // deadline on the encounter edge so a prior raid cannot consume this
            // raid's retries or leave a stale state marked complete.
            _selectionRequests = 0;
            _selectionAwaitingReply = false;
            _selectionDeadline = Time.unscaledTime + 120f;
            StartSelectionRequests();
        }

        // send side: the base mod's hooks fire only on LOCAL commits (remote applies
        // are suppressed there), so every packet on the wire is an original event.
        // broadcast=true: from the host it reaches every client, from a client the
        // server relays it onward — the ladders addon relies on the same behavior.
        private void OnChainEvent(string ev)
            => Send(new IceWorldPacket { Kind = ev == "plant" ? (byte)0 : (byte)1 });

        private void OnSealEvent(string doorId, bool sealing)
            => Send(new IceWorldPacket { Kind = 2, DoorId = doorId, Sealing = sealing });

        private void OnHeliCalled()
            => Send(new IceWorldPacket { Kind = 3 });

        private void OnProgressDoor()
            => Send(new IceWorldPacket { Kind = 4 });

        // kinds 5/6/7 = frozen-hatch stages 0/1/2 (melt done / handle turned / opened)
        private void OnHatchStage(byte stage)
            => Send(new IceWorldPacket { Kind = (byte)(5 + stage) });

        // kind 8 = torch flame state; NetId rides the DoorId field, state in Sealing
        private void OnTorchFiring(bool on)
        {
            var me = Singleton<GameWorld>.Instance?.MainPlayer as global::Fika.Core.Main.Players.FikaPlayer;
            if (me == null) return;
            Send(new IceWorldPacket { Kind = 8, DoorId = me.NetId.ToString(), Sealing = on });
        }

        // kind 9 = per-player cutscene activation (ProfileId in DoorId) — feeds the
        // all-players progress-door gate on the authority
        private void OnCutsceneActivated(string profileId)
            => Send(new IceWorldPacket { Kind = 9, DoorId = profileId });

        // kind 12 = tripwire layout seed. below TripwireChance 1.0 every marker rolls
        // its own arm/skip, and each peer plants its own LOCAL wires — so without this
        // every player would walk a different minefield. only the authority ever raises
        // the event, and clients block on it before planting.
        private void OnTripwireSeed(int seed)
            => Send(new IceWorldPacket { Kind = 12, IntA = seed });

        // kind 13 = keypad unlock. the CODES already match on every peer (shared
        // raid seed), but the unlock itself is a direct DoorState write fika
        // doesnt replicate — ship the door id so peers run the same commit.
        private void OnKeypadUnlock(string doorId)
            => Send(new IceWorldPacket { Kind = 13, DoorId = doorId });

        private void OnSelectionReady(bool active)
        {
            // SelectionReady is raised on every peer, but only the bot authority may
            // publish a state packet. Fika clients never roll or echo a local guess.
            if (!FikaBridge.BotsAuthority || !IcebreakerCombineEncounter.Ready) return;
            Send(new IceWorldPacket
            {
                Kind = 14,
                Sealing = IcebreakerCombineEncounter.Active,
                IntA = IcebreakerCombineEncounter.RaidToken,
            }, quiet: true);
        }

        private void StartSelectionRequests()
        {
            if (_selectionRetryPump != null || _disposed) return;
            if (FikaBridge.BotsAuthority || !IcebreakerCombineEncounter.RaidInitialized) return;
            if (_selectionDeadline > 0f && _selectionDeadline <= Time.unscaledTime) return;
            if (_selectionDeadline <= 0f)
                _selectionDeadline = Time.unscaledTime + 120f;
            var go = new GameObject("Icebreaker_CombineSelectionSync");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _selectionRetryPump = go.AddComponent<SelectionRetryPump>();
            _selectionRetryPump.Initialize(this);
        }

        private void RequestSelection()
        {
            if (_disposed || FikaBridge.BotsAuthority || !IceGate.On
                || !IcebreakerCombineEncounter.RaidInitialized
                || IcebreakerCombineEncounter.SelectionAuthoritative) return;
            if (_selectionDeadline <= 0f)
                _selectionDeadline = Time.unscaledTime + 120f;
            if (Time.unscaledTime >= _selectionDeadline || _selectionRequests >= 120) return;
            _selectionRequests++;
            _selectionAwaitingReply = true;
            Send(new IceWorldPacket
            {
                Kind = 15,
                IntA = IcebreakerCombineEncounter.RaidToken,
            }, quiet: true);
        }

        private void OnSelectionRequest()
        {
            // Requests can arrive before the host's game object has initialized. The
            // client retries; once Ready is true this also serves late joiners.
            if (FikaBridge.BotsAuthority && IcebreakerCombineEncounter.Ready)
                OnSelectionReady(IcebreakerCombineEncounter.Active);
        }

        internal static void Send(IceWorldPacket packet, DeliveryMethod delivery = DeliveryMethod.ReliableOrdered, bool quiet = false)
        {
            var manager = Singleton<IFikaNetworkManager>.Instance;
            if (manager == null) return;
            manager.SendData(ref packet, delivery, true);
            if (!quiet) FikaAddonPlugin.Log.LogInfo($"[IceSync] sent kind={packet.Kind} door='{packet.DoorId}'");
        }

        private void OnReceived(IceWorldPacket packet)
        {
            if (packet.Kind != 11) // the 20/s bar-angle stream would drown the log
                FikaAddonPlugin.Log.LogInfo($"[IceSync] received kind={packet.Kind} door='{packet.DoorId}'");
            try { Dispatch(packet); }
            catch (Exception e) { FikaAddonPlugin.Log.LogWarning($"[IceSync] handler for kind {packet.Kind} failed: {e.Message}"); }
        }

        private void Dispatch(IceWorldPacket packet)
        {
            switch (packet.Kind)
            {
                case 0: IcebreakerChainDoor.ApplyRemote("plant"); break;
                case 1: IcebreakerChainDoor.ApplyRemote("open"); break;
                case 2: IcebreakerSealedDoors.ApplyRemote(packet.DoorId, packet.Sealing); break;
                case 3: IcebreakerHeliExfil.ApplyRemoteCall(); break;
                case 4: IcebreakerCrew.ApplyRemoteProgressDoor(); break;
                case 5: HatchMeltDriver.ApplyRemoteStage(0); break;
                case 6: HatchMeltDriver.ApplyRemoteStage(1); break;
                case 7: HatchMeltDriver.ApplyRemoteStage(2); break;
                case 8:
                    if (int.TryParse(packet.DoorId, out var torchNetId))
                        IceTorchSync.SetRemoteFlame(torchNetId, packet.Sealing);
                    break;
                case 9: IcebreakerCrew.ApplyRemoteCutsceneActivation(packet.DoorId); break;
                // 10/11 were our ladder climb sync. dropped 08-01: tarkin's own fika addon
                // throws MissingMethodException (NetDataWriter.Put(string)) out of
                // PlayerLadderController.OnDestroy on every dismount, and remote bodies kept
                // their IK welded to the ladder. that is his mod's bug to fix, not ours to
                // paper over. the kinds stay RESERVED — reusing them would make an old peer
                // decode a ladder packet as something else.
                case 12: IcebreakerTripwires.ApplyRemoteSeed(packet.IntA); break;
                case 13: Manimal.Icebreaker.Keypad.Keypad.ApplyRemoteUnlock(packet.DoorId); break;
                case 14:
                    if (FikaBridge.BotsAuthority)
                    {
                        FikaAddonPlugin.Log.LogWarning("[IceSync] ignored Combine selection state on the authority");
                        break;
                    }
                    // A client accepts state only after it has requested the host's
                    // answer for this raid. This keeps a relayed client-local guess
                    // from becoming authoritative on its peers; the host itself
                    // rejects kind 14 above.
                    if (!_selectionAwaitingReply)
                    {
                        FikaAddonPlugin.Log.LogWarning("[IceSync] ignored unsolicited Combine selection state");
                        break;
                    }
                    IcebreakerCombineEncounter.ApplyRemoteSelection(packet.Sealing, packet.IntA);
                    if (IcebreakerCombineEncounter.SelectionAuthoritative)
                        _selectionAwaitingReply = false;
                    break;
                case 15: OnSelectionRequest(); break;
                default: FikaAddonPlugin.Log.LogWarning($"[IceSync] unknown event kind {packet.Kind}"); break;
            }
        }

        public void Dispose()
        {
            IcebreakerChainDoor.WorldEvent -= OnChainEvent;
            IcebreakerSealedDoors.SealEvent -= OnSealEvent;
            IcebreakerHeliExfil.HeliCalled -= OnHeliCalled;
            IcebreakerCrew.ProgressDoorUnlocked -= OnProgressDoor;
            HatchMeltDriver.HatchStage -= OnHatchStage;
            BlowtorchController.LocalTorchFiring -= OnTorchFiring;
            IcebreakerCrew.CutsceneActivated -= OnCutsceneActivated;
            IcebreakerTripwires.SeedRolled -= OnTripwireSeed;
            Manimal.Icebreaker.Keypad.Keypad.UnlockCommitted -= OnKeypadUnlock;
            IcebreakerCombineEncounter.RaidReset -= OnRaidReset;
            IcebreakerCombineEncounter.SelectionReady -= OnSelectionReady;
            FikaEventDispatcher.UnsubscribeEvent<FikaNetworkManagerCreatedEvent>(OnManagerCreated);
            _disposed = true;
            if (_selectionRetryPump != null)
            {
                var pump = _selectionRetryPump;
                _selectionRetryPump = null;
                UnityEngine.Object.Destroy(pump.gameObject);
            }
            try
            {
                // same teardown the ladders addon uses: the processor field is not
                // exposed, so unsubscribe via reflection; failure just means a stale
                // handler until the next raid re-registers
                var manager = Singleton<IFikaNetworkManager>.Instance;
                if (manager != null)
                {
                    var processor = AccessTools.Field(manager.GetType(), "_packetProcessor")?.GetValue(manager) as NetPacketProcessor;
                    processor?.RemoveSubscription<IceWorldPacket>();
                }
            }
            catch (Exception e) { FikaAddonPlugin.Log?.LogWarning($"[IceSync] unregister failed: {e.Message}"); }
        }

        private sealed class SelectionRetryPump : MonoBehaviour
        {
            private IceSyncHandler _owner;
            private float _next;

            internal void Initialize(IceSyncHandler owner)
            {
                _owner = owner;
                _next = 0f;
            }

            private void Update()
            {
                if (_owner == null || _owner._disposed)
                {
                    Destroy(gameObject);
                    return;
                }
                if (_owner.StopSelectionRetrying)
                {
                    _owner._selectionRetryPump = null;
                    Destroy(gameObject);
                    return;
                }
                if (Time.unscaledTime < _next) return;
                _next = Time.unscaledTime + 1f;
                _owner.RequestSelection();
                if (_owner._selectionRequests >= 120 || Time.unscaledTime >= _owner._selectionDeadline)
                {
                    _owner._selectionRetryPump = null;
                    Destroy(gameObject);
                }
            }
        }

        private bool StopSelectionRetrying
            => IcebreakerCombineEncounter.SelectionAuthoritative
                || !IcebreakerCombineEncounter.RaidInitialized
                || (_selectionDeadline > 0f && Time.unscaledTime >= _selectionDeadline);
    }
}
