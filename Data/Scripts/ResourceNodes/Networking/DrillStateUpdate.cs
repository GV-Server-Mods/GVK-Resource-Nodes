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


        public int GetId()
        {
            return 2;
        }

        public void Execute()
        {
            var e = MyEntities.GetEntityById(blockId);
            if (e != null && e is MyCubeBlock)
            {
                var b = e as MyCubeBlock;
                if (!b.MarkedForClose)
                {
                    AdvancedStaticDrill d = b.GameLogic.GetAs<AdvancedStaticDrill>();
                    MediumStaticDrill d1 = b.GameLogic.GetAs<MediumStaticDrill>();
                    BasicStaticDrill d2 = b.GameLogic.GetAs<BasicStaticDrill>();
                    if (d != null)
                    {
                        d.IsProducing = isProducing;
                        d.State = this;
                    } 
                    else if (d1 != null)
                    {
                        d1.IsProducing = isProducing;
                        d1.State = this;
                    }
                    else if (d2 != null)
                    {
                        d2.IsProducing = isProducing;
                        d2.State = this;
                    }
                }
            }
        }

    }
}
