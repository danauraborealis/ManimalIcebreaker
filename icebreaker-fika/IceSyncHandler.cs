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

namespace Manimal.Icebreaker.Fika
{
    // one tiny reliable packet for all icebreaker world events — these are rare
    // one-shot state changes, so none of the wiki's throttling/snapshotting applies
    public struct IceWorldPacket : INetSerializable
    {
        public byte Kind;      // 0/1 chain plant/open, 2 seal, 3 heli, 4 progress door,
                               // 5-7 hatch stages, 8 torch flame, 9 cutscene activation,
                               // 10 ladder enter/exit, 11 ladder bar angle,
                               // 12 tripwire layout seed
        public string DoorId;  // seal doorId / profileId / ladder NetId, per kind
        public bool Sealing;   // seal state / torch flame / ladder enter
        public int IntA;       // player NetId (torch/ladder kinds)
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
        }

        private void OnManagerCreated(FikaNetworkManagerCreatedEvent ev) => Register(ev.Manager);

        private void Register(IFikaNetworkManager manager)
        {
            manager.RegisterPacket<IceWorldPacket>(OnReceived);
            IceBodyDiag.Ensure(); // raid-time creation — the chainloader-time component never ticked
            IceTorchSync.Ensure();
            FikaAddonPlugin.Log.LogInfo("[IceSync] packet registered");
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
            FikaEventDispatcher.UnsubscribeEvent<FikaNetworkManagerCreatedEvent>(OnManagerCreated);
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
    }
}
