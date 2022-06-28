using ProtoBuf;
using ResourceNodes;
using Sandbox.Game.Entities;
using static Math0424.Networking.EasyNetworker;

namespace Math0424.Networking
{

    [ProtoContract]
    class DrillStateUpdate : IPacket
    {

        [ProtoMember(1)] public long blockId;
        [ProtoMember(2)] public bool isInGround;
        [ProtoMember(3)] public bool isProducing;
        [ProtoMember(4)] public bool invFull;
        [ProtoMember(5)] public string oreName;
        [ProtoMember(6)] public float penalty;
        [ProtoMember(7)] public bool forceUpdate;

        public int GetId()
        {
            return 2;
        }

        public void Execute()
        {
            var e = MyEntities.GetEntityById(blockId);
            var b = e as MyCubeBlock;
            if (b == null)
            {
                return;
            }

            if (b.MarkedForClose)
            {
                return;
            }

            var d = b.GameLogic.GetAs<AdvancedStaticDrill>();
            var d1 = b.GameLogic.GetAs<MediumStaticDrill>();
            var d2 = b.GameLogic.GetAs<BasicStaticDrill>();
            if (d != null)
            {
                d.UpdateFromState(this);
            }
            else if (d1 != null)
            {
                d1.UpdateFromState(this);
            }
            else if (d2 != null)
            {
                d2.UpdateFromState(this);
            }
        }
    }
}
