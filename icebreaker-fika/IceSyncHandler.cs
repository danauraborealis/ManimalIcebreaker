using System;
using Comfort.Common;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;
using Fika.Core.Networking.LiteNetLib.Utils;
using HarmonyLib;

namespace Manimal.Icebreaker.Fika
{
    // one tiny reliable packet for all icebreaker world events — these are rare
    // one-shot state changes, so none of the wiki's throttling/snapshotting applies
    public struct IceWorldPacket : INetSerializable
    {
        public byte Kind;      // 0 = chain planted, 1 = chain opened, 2 = seal state
        public string DoorId;  // seal events only
        public bool Sealing;   // seal events only

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(Kind);
            writer.Put(DoorId ?? "");
            writer.Put(Sealing);
        }

        public void Deserialize(NetDataReader reader)
        {
            Kind = reader.GetByte();
            DoorId = reader.GetString();
            Sealing = reader.GetBool();
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
        }

        private void OnManagerCreated(FikaNetworkManagerCreatedEvent ev) => Register(ev.Manager);

        private void Register(IFikaNetworkManager manager)
        {
            manager.RegisterPacket<IceWorldPacket>(OnReceived);
            IceBodyDiag.Ensure(); // raid-time creation — the chainloader-time component never ticked
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

        private static void Send(IceWorldPacket packet)
        {
            var manager = Singleton<IFikaNetworkManager>.Instance;
            if (manager == null) return;
            manager.SendData(ref packet, DeliveryMethod.ReliableOrdered, true);
            FikaAddonPlugin.Log.LogInfo($"[IceSync] sent kind={packet.Kind} door='{packet.DoorId}'");
        }

        private void OnReceived(IceWorldPacket packet)
        {
            FikaAddonPlugin.Log.LogInfo($"[IceSync] received kind={packet.Kind} door='{packet.DoorId}'");
            switch (packet.Kind)
            {
                case 0: IcebreakerChainDoor.ApplyRemote("plant"); break;
                case 1: IcebreakerChainDoor.ApplyRemote("open"); break;
                case 2: IcebreakerSealedDoors.ApplyRemote(packet.DoorId, packet.Sealing); break;
                case 3: IcebreakerHeliExfil.ApplyRemoteCall(); break;
                case 4: IcebreakerCrew.ApplyRemoteProgressDoor(); break;
                default: FikaAddonPlugin.Log.LogWarning($"[IceSync] unknown event kind {packet.Kind}"); break;
            }
        }

        public void Dispose()
        {
            IcebreakerChainDoor.WorldEvent -= OnChainEvent;
            IcebreakerSealedDoors.SealEvent -= OnSealEvent;
            IcebreakerHeliExfil.HeliCalled -= OnHeliCalled;
            IcebreakerCrew.ProgressDoorUnlocked -= OnProgressDoor;
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
