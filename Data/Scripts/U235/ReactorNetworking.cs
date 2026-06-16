using System;
using System.Collections.Generic;
using ProtoBuf;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;

namespace TSUT.U235
{
    [MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
    public class ReactorSession : MySessionComponentBase
    {
        public static readonly Networking Networking = new Networking(7959);

        public override void BeforeStart()
        {
            Networking.Register();
        }

        protected override void UnloadData()
        {
            Networking.Unregister();
        }
    }

    public class Networking
    {
        public readonly ushort ChannelId;
        private List<IMyPlayer> _tempPlayers;

        public Networking(ushort channelId)
        {
            ChannelId = channelId;
        }

        public void Register()
        {
            MyAPIGateway.Multiplayer.RegisterMessageHandler(ChannelId, ReceivedPacket);
        }

        public void Unregister()
        {
            MyAPIGateway.Multiplayer.UnregisterMessageHandler(ChannelId, ReceivedPacket);
        }

        private void ReceivedPacket(byte[] rawData)
        {
            try
            {
                var packet = MyAPIGateway.Utilities.SerializeFromBinary<PacketBase>(rawData);
                HandlePacket(packet, rawData);
            }
            catch (Exception e)
            {
                MyLog.Default.WriteLineAndConsole($"[HMS.U235] Networking error: {e.Message}\n{e.StackTrace}");
            }
        }

        private void HandlePacket(PacketBase packet, byte[] rawData = null)
        {
            if (packet.Received())
                RelayToClients(packet, rawData);
        }

        public void SendToServer(PacketBase packet)
        {
            if (MyAPIGateway.Multiplayer.IsServer)
            {
                HandlePacket(packet);
                return;
            }
            MyAPIGateway.Multiplayer.SendMessageToServer(ChannelId, MyAPIGateway.Utilities.SerializeToBinary(packet));
        }

        public void RelayToClients(PacketBase packet, byte[] rawData = null)
        {
            if (!MyAPIGateway.Multiplayer.IsServer)
                return;

            if (_tempPlayers == null)
                _tempPlayers = new List<IMyPlayer>(MyAPIGateway.Session.SessionSettings.MaxPlayers);
            else
                _tempPlayers.Clear();

            MyAPIGateway.Players.GetPlayers(_tempPlayers);

            foreach (var p in _tempPlayers)
            {
                if (p.IsBot) continue;
                if (p.SteamUserId == MyAPIGateway.Multiplayer.ServerId) continue;
                if (p.SteamUserId == packet.SenderId) continue;

                if (rawData == null)
                    rawData = MyAPIGateway.Utilities.SerializeToBinary(packet);

                MyAPIGateway.Multiplayer.SendMessageTo(ChannelId, rawData, p.SteamUserId);
            }

            _tempPlayers.Clear();
        }
    }

    [ProtoInclude(1000, typeof(ReactorStateSync))]
    [ProtoInclude(1001, typeof(ReactorLaunchRequest))]
    [ProtoInclude(1002, typeof(ReactorStopRequest))]
    [ProtoInclude(1003, typeof(ReactorSetAutoRestart))]
    [ProtoInclude(1004, typeof(ReactorSetControlRod))]
    [ProtoContract]
    public abstract class PacketBase
    {
        [ProtoMember(1)]
        public ulong SenderId;

        protected PacketBase()
        {
            SenderId = MyAPIGateway.Multiplayer.MyId;
        }

        public abstract bool Received();
    }

    [ProtoContract]
    public class ReactorStateSync : PacketBase
    {
        public ReactorStateSync() { }

        [ProtoMember(1)] public long EntityId;
        [ProtoMember(2)] public float CoreTemp;
        [ProtoMember(3)] public float FuelCountdown;
        [ProtoMember(4)] public int State;
        [ProtoMember(5)] public bool AutoRestart;
        [ProtoMember(6)] public float ControlRodThreshold;
        [ProtoMember(7)] public float OutputMW;

        public override bool Received()
        {
            IMyEntity entity;
            if (MyAPIGateway.Entities.TryGetEntityById(EntityId, out entity))
                (entity as IMyCubeBlock)?.GameLogic?.GetAs<ReactorGameLogic>()?.ReceiveStateSync(
                    CoreTemp, FuelCountdown, (ReactorState)State, AutoRestart, ControlRodThreshold, OutputMW);
            return false;
        }
    }

    [ProtoContract]
    public class ReactorLaunchRequest : PacketBase
    {
        public ReactorLaunchRequest() { }

        [ProtoMember(1)] public long EntityId;

        public override bool Received()
        {
            IMyEntity entity;
            if (MyAPIGateway.Entities.TryGetEntityById(EntityId, out entity))
                (entity as IMyCubeBlock)?.GameLogic?.GetAs<ReactorGameLogic>()?.ManualLaunch();
            return false;
        }
    }

    [ProtoContract]
    public class ReactorStopRequest : PacketBase
    {
        public ReactorStopRequest() { }

        [ProtoMember(1)] public long EntityId;

        public override bool Received()
        {
            IMyEntity entity;
            if (MyAPIGateway.Entities.TryGetEntityById(EntityId, out entity))
                (entity as IMyCubeBlock)?.GameLogic?.GetAs<ReactorGameLogic>()?.ManualStop();
            return false;
        }
    }

    [ProtoContract]
    public class ReactorSetAutoRestart : PacketBase
    {
        public ReactorSetAutoRestart() { }

        [ProtoMember(1)] public long EntityId;
        [ProtoMember(2)] public bool Value;

        public override bool Received()
        {
            IMyEntity entity;
            if (MyAPIGateway.Entities.TryGetEntityById(EntityId, out entity))
            {
                var logic = (entity as IMyCubeBlock)?.GameLogic?.GetAs<ReactorGameLogic>();
                if (logic != null) logic.AutoRestartOn = Value;
            }
            return false;
        }
    }

    [ProtoContract]
    public class ReactorSetControlRod : PacketBase
    {
        public ReactorSetControlRod() { }

        [ProtoMember(1)] public long EntityId;
        [ProtoMember(2)] public float Value;

        public override bool Received()
        {
            IMyEntity entity;
            if (MyAPIGateway.Entities.TryGetEntityById(EntityId, out entity))
            {
                var logic = (entity as IMyCubeBlock)?.GameLogic?.GetAs<ReactorGameLogic>();
                if (logic != null) logic.ControlRodThreshold = Value;
            }
            return false;
        }
    }
}
