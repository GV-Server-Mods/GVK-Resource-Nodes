using Math0424.Networking;
using System.Collections.Generic;
using VRage.Game;
using VRage.Game.Components;
using VRageMath;
using static Math0424.Networking.EasyNetworker;

namespace ResourceNodes
{
    [MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
    class ResourceNode : MySessionComponentBase
    {
        public readonly List<string> MiningBlacklist = new List<string>();

        public MyDynamicAABBTreeD MinerTree =
            new MyDynamicAABBTreeD(MyConstants.GAME_PRUNING_STRUCTURE_AABB_EXTENSION, 1.0);

        public EasyNetworker Network;
        public string ModPath;

        public static ResourceNode Instance { get; private set; }

        public override void Init(MyObjectBuilder_SessionComponent sessionComponent)
        {
            Instance = this;

            ModPath = ModContext.ModPath;
            Network = new EasyNetworker(51821);
            Network.Register();

            Network.OnRecievedPacket += PacketIn;
        }

        private void PacketIn(PacketIn raw)
        {
            if (raw.IsFromServer && raw.PacketId == 1)
            {
                raw.UnWrap<EmissiveStateChange>()?.Execute();
            }
            else if (raw.IsFromServer && raw.PacketId == 2)
            {
                raw.UnWrap<DrillStateUpdate>()?.Execute();
            }
        }

        protected override void UnloadData()
        {
            Network?.UnRegister();
        }
    }
}