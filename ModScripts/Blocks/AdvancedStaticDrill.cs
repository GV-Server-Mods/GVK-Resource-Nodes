using Math0424.AnimationCore;
using Math0424.Networking;
using Sandbox.Common.ObjectBuilders;
using Sandbox.ModAPI;
using VRage.Game.Components;
using VRageMath;
using static Math0424.AnimationCoreAPI.AnimationCoreAPI;

namespace ResourceNodes
{
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_Drill), false, "AdvancedStaticDrill")]
    internal sealed class AdvancedStaticDrill : MyDrillBlock
    {
        private int animationFrames = -1;
        private MySubpart drillHead, powerLever;
        private int level = 3;

        protected override void SetEmissive(Color color)
        {
            if (MyAPIGateway.Session.IsServer)
            {
                var packet = new EmissiveStateChange()
                {
                    blockId = Block.EntityId
                };
                packet.colors.Add("Emissive0", color.PackedValue);
                packet.colors.Add("Emissive1", color.PackedValue);
                packet.colors.Add("Emissive2", color.PackedValue);
                packet.colors.Add("Emissive3", color.PackedValue);
                ResourceNode.Instance.Network.TransmitToPlayersWithinRange(Block.PositionComp.GetPosition(), packet,
                    500, false);
            }

            if (!MyAPIGateway.Utilities.IsDedicated)
            {
                SetEmissives(color);
            }
        }

        public override void BlockInit()
        {
            DepositedResources += TickEmissive;
            BaseOrePerSecond = 25; // Enough to feed 5 O2/H2 gens unmodded, 10 with speed mod
            InvMultiplier = 5; // 5kL
            BasePowerMW = 2.5f; // Takes 1 engine to power fully when speedmodded
            AnimationLength = 200;
        }

        public override bool LoadSubparts()
        {
            powerLever?.Delete();

            drillHead = LoadSubpartFromFile("DrillHead", "Models/AdvancedDrill/DrillHead");
            powerLever = LoadSubpartFromFile("PowerLever", "Models/AdvancedDrill/PowerLever");

            drillHead.SubpartDeleted += ReloadSubparts;

            return true;
        }

        public void RespawnSubparts()
        {
            if (Block != null && Block.IsBuilt && Block.InScene && !Block.MarkedForClose)
            {
                ReloadSubparts();
            }
        }

        public override void BeforeFirstAnimationUpdate()
        {
            drillHead.Pos.SetOriginMatrix(MatrixD.CreateTranslation(-Vector3.Up));
            drillHead.Pos.ResetPosRot();

            powerLever.Pos.SetOriginMatrix(MatrixD.CreateTranslation(new Vector3(1.47f, -3.42, 3.07f)));
            powerLever.Pos.ResetPosRot();

            powerLever.AddComponent<PowerLeverComp>().Create(this, new Vector3(.54, 0, .83), 50, -180, Color.Green,
                Color.Red, "lever");
        }

        public override void AnimationUpdate()
        {
            if (IsProducing)
            {
                animationFrames++;
                if (animationFrames % 200 == 0)
                {
                    //drillHead.Pos.Rotate(40, Vector3.Up, 180, LerpType.Bounce, EaseType.Out);
                    drillHead.Pos.Translate(40, Vector3.Down * 2f, LerpType.Elastic, EaseType.Out);
                    drillHead.Effects.PlaySound("AdvancedDrillSlam");

                    drillHead.Pos.Rotate(160, Vector3.Down, 30f, LerpType.Expo, EaseType.InOut, 40);
                    drillHead.Pos.Translate(160, Vector3.Up * 2f, LerpType.Expo, EaseType.InOut, 40);

                    drillHead.Pos.ResetPos(200);

                    //drillHead.Pos.Translate(40, Vector3.Up * .1f, LerpType.Back, EaseType.Out, 141);
                }
            }
            else
            {
                animationFrames = -1;
                drillHead.ClearActions();
                drillHead.Pos.ResetPos();
            }
        }

        private void TickEmissive()
        {
            level = (level + 1) % 5;

            if (level == 4)
            {
                SetEmissive(Color.Gray);
            }
            else
            {
                var packet = new EmissiveStateChange()
                {
                    blockId = Block.EntityId
                };
                packet.colors.Add("Emissive" + level, Color.Green.PackedValue);
                ResourceNode.Instance.Network.TransmitToPlayersWithinRange(Block.PositionComp.GetPosition(), packet,
                    500, false);

                if (!MyAPIGateway.Utilities.IsDedicated)
                {
                    Block.SetEmissiveParts("Emissive" + level, Color.Green, 1);
                }
            }
        }

        private void SetEmissives(Color color, float newLevel = 0)
        {
            Block.SetEmissiveParts("Emissive0", color, newLevel);
            Block.SetEmissiveParts("Emissive1", color, newLevel);
            Block.SetEmissiveParts("Emissive2", color, newLevel);
            Block.SetEmissiveParts("Emissive3", color, newLevel);
        }
    }
}