using Math0424.AnimationCore;
using Math0424.Networking;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Localization;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders;
using VRage.Voxels;
using VRageMath;
using IMyInventory = VRage.Game.ModAPI.IMyInventory;

namespace ResourceNodes
{
    internal abstract class MyDrillBlock : MyAbstractAnimatedBlock
    {
        private const int TickRate = 3 * 60; // Every 3 seconds
        private const double RateInSeconds = TickRate / 60d;
        private const float EncroachmentDistance = 50f; // Distance to other miner mining same ore
        private const float EncroachmentPenalty = 0.50f; // 50% multiplicative reduction in effectiveness
        private const int ResourceUpdateRate = 60 * 30; // 60 ticks per second * however many seconds to check

        private int tick;
        private int tickResource;
        private int tickGroundUpdate;
        private float yieldMultiplier = 1;

        private IMyFunctionalBlock functionalBlock;
        private bool invFull;
        private bool inGround;
        private bool needsUpdate;
        private float penaltyFactor;
        private int minerProxyId = -1;
        private MyVoxelMaterialDefinition currentOre;
        private float allowedDistanceToGround;

        public bool IsProducing;
        protected Action DepositedResources;

        protected int InvMultiplier = 8;
        protected int BaseOrePerSecond = 1;
        protected float BasePowerMW = 1f;

        private readonly Dictionary<byte, int> materials = new Dictionary<byte, int>();

        public DrillStateUpdate State;

        private IMyInventory Inv => functionalBlock.GetInventory(0);

        protected abstract void SetEmissive(Color color);

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            LoadOntoBlock();
            var cubeSize = Block.BlockDefinition.CubeSize == MyCubeSize.Large
                ? 2.5f
                : 0.5f;
            allowedDistanceToGround = Block.BlockDefinition.Size.Y * cubeSize / 2f + 0.25f;
            Block.UpgradeValues.Add("Productivity", 0f);
            Block.UpgradeValues.Add("Effectiveness", 1f);
            Block.UpgradeValues.Add("PowerEfficiency", 1f);

            functionalBlock = (IMyFunctionalBlock)Block;

            Block.OnClose += RemoveFromMiners;
            functionalBlock.AppendingCustomInfo += CustomInfo;
            Block.OnUpgradeValuesChanged += MarkForUpdate;
            functionalBlock.IsWorkingChanged += MarkForUpdate;

            tickResource = Math.Abs(GetHashCode() % TickRate);
            tickGroundUpdate = Math.Abs(GetHashCode() % ResourceUpdateRate);
            BlockInit();
        }

        private bool IsProjection()
        {
            return Block != null && Block.CubeGrid.Physics == null;
        }

        private void MarkForUpdate(IMyCubeBlock obj)
        {
            MarkForUpdate();
        }

        private void MarkForUpdate()
        {
            needsUpdate = true;
        }

        private void UpdateMultipliers()
        {
            if (IsProjection())
            {
                return;
            }

            UpdatePenaltyFactor();

            Dictionary<string, float> v = Block.UpgradeValues;
            yieldMultiplier = (v["Productivity"] + v["Effectiveness"]) * (1 - penaltyFactor);
            IMyShipDrill drill = Block as IMyShipDrill;
            if (drill != null)
            {
                MyResourceSinkComponent resourceSink;
                if (drill.Components.TryGet(out resourceSink))
                {
                    var modifier = 1 * (1 + v["Productivity"]) * (1f / v["PowerEfficiency"]) *
                                   (1f / v["Effectiveness"]);
                    resourceSink.SetMaxRequiredInputByType(MyResourceDistributorComponent.ElectricityId,
                        BasePowerMW * modifier);
                }
            }

            needsUpdate = false;
        }

        private void UpdatePenaltyFactor()
        {
            if (string.IsNullOrEmpty(currentOre?.MinedOre))
            {
                return;
            }

            penaltyFactor = 0;

            var nearby = new BoundingSphereD(Block.PositionComp.GetPosition(), EncroachmentDistance);
            List<MyDrillBlock> nearbyDrills = new List<MyDrillBlock>();
            GetAllDrillsInSphere(nearby, nearbyDrills);

            foreach (var other in nearbyDrills)
            {
                if (other.MarkedForClose ||
                    other.Entity.EntityId == Entity.EntityId ||
                    other.currentOre?.MinedOre != currentOre?.MinedOre ||
                    !other.IsProducing)
                {
                    continue;
                }

                penaltyFactor += (1 - penaltyFactor) * EncroachmentPenalty;
            }

            MarkForUpdate();
        }

        private static void GetAllDrillsInSphere(BoundingSphereD sphere, List<MyDrillBlock> result)
        {
            ResourceNode.Instance.MinerTree.OverlapAllBoundingSphere(ref sphere, result, false);

            for (int i = result.Count - 1; i >= 0; i--)
            {
                MyDrillBlock drill = result[i];
                if (drill?.Entity == null)
                {
                    continue;
                }

                if (drill.MarkedForClose ||
                    Vector3D.Distance(sphere.Center, drill.Block.PositionComp.GetPosition()) > EncroachmentDistance)
                {
                    result.RemoveAtFast(i);
                }
            }
        }

        public abstract void BlockInit();

        public override void BeforeFirstUpdate()
        {
            var currentInv = Inv as MyInventory;
            if (currentInv == null)
            {
                return;
            }

            currentInv.Constraint = new MyInventoryConstraint(MySpaceTexts.ToolTipItemFilter_AnyOre, null, true)
                .AddObjectBuilderType(typeof(MyObjectBuilder_Ore));

            if (!MyAPIGateway.Session.IsServer)
            {
                return;
            }

            var component = new MyInventory(InvMultiplier, new Vector3(1), MyInventoryFlags.CanSend);
            var invItems = currentInv.GetItems();
            foreach (var i in invItems)
            {
                component.AddItems(i.Amount, i.Content);
            }

            Block.Components.Remove(typeof(MyInventoryBase));
            Block.Components.Add<MyInventoryBase>(component);

            UpdateMultipliers();
            UpdateInGround();
        }

        public override void GameUpdate()
        {
            if (!MyAPIGateway.Session.IsServer)
            {
                return;
            }

            if (!functionalBlock.CubeGrid.IsStatic)
            {
                functionalBlock.Enabled = false;
                return;
            }

            tick = MyAPIGateway.Session.GameplayFrameCounter;

            if (tick % ResourceUpdateRate == tickGroundUpdate)
            {
                UpdateInGround();
            }

            if (needsUpdate && !string.IsNullOrEmpty(currentOre?.MinedOre))
            {
                UpdateMultipliers();
            }

            if (tick % 10 == 0)
            {
                var packet = new DrillStateUpdate
                {
                    penalty = penaltyFactor,
                    blockId = Block.EntityId,
                    isInGround = inGround,
                    invFull = invFull,
                    oreName = currentOre?.MinedOre ?? "nothing",
                    isProducing = IsProducing
                };
                ResourceNode.Instance.Network.TransmitToPlayersWithinRange(Block.PositionComp.GetPosition(), packet,
                    1500, false);

                if (Block.IsBuilt)
                {
                    if (!Block.IsFunctional)
                    {
                        SetEmissive(Color.Orange);
                    }
                    else
                    {
                        if (!inGround || !functionalBlock.Enabled || !functionalBlock.IsWorking)
                        {
                            SetEmissive(Color.Red);
                        }
                        else if (!IsProducing || currentOre == null || Inv.IsFull || invFull)
                        {
                            SetEmissive(Color.Yellow);
                        }
                    }
                }
                else
                {
                    if (!inGround)
                    {
                        SetEmissive(Color.Red);
                    }
                    else if (currentOre != null)
                    {
                        SetEmissive(Color.Aqua);
                    }
                    else
                    {
                        SetEmissive(Color.Yellow);
                    }
                }
            }

            if (tick % TickRate != tickResource)
            {
                return;
            }

            UpdateIsProducing();

            if (!IsProducing)
            {
                return;
            }

            if (currentOre == null)
            {
                return;
            }

            var oreObject = MyObjectBuilderSerializer.CreateNewObject<MyObjectBuilder_Ore>(currentOre.MinedOre);

            var amount = BaseOrePerSecond * currentOre.MinedOreRatio * RateInSeconds * yieldMultiplier;
            invFull = !Inv.CanItemsBeAdded((MyFixedPoint)amount, oreObject);

            if (invFull)
            {
                return;
            }

            Inv.AddItems((MyFixedPoint)amount, oreObject);
            DepositedResources?.Invoke();
        }

        private void UpdateIsProducing()
        {
            if (IsProjection())
            {
                IsProducing = false;
                return;
            }

            var wasProducing = IsProducing;
            IsProducing = functionalBlock.Enabled && functionalBlock.IsWorking && !Inv.IsFull && inGround;

            if (IsProducing)
            {
                IsProducing = Block.ResourceSink.IsPoweredByType(MyResourceDistributorComponent.ElectricityId) &&
                              Block.ResourceSink.IsPowerAvailable(MyResourceDistributorComponent.ElectricityId,
                                  Block.ResourceSink.MaxRequiredInputByType(
                                      MyResourceDistributorComponent.ElectricityId));
            }

            if (wasProducing == IsProducing)
            {
                return;
            }

            // Only triggered on change
            if (IsProducing)
            {
                AddToMiners();
            }
            else
            {
                RemoveFromMiners((MyEntity)Entity);
            }
        }

        private void UpdateInGround()
        {
            if (IsProjection())
            {
                return;
            }

            materials.Clear();
            var detected = new List<MyVoxelBase>();
            var position = Block.PositionComp.GetPosition() +
                           Block.PositionComp.WorldMatrixRef.Down * allowedDistanceToGround;
            var boundingSphereD = new BoundingSphereD(position, 2);
            MyGamePruningStructure.GetAllVoxelMapsInSphere(ref boundingSphereD, detected);
            foreach (var map in detected)
            {
                GetResources(position, map);
            }

            inGround = materials.Count >= 1;
            if (inGround)
            {
                AssignNewMaterial();
            }
        }

        private void AssignNewMaterial()
        {
            //get all the materials
            for (var i = 0; i < 60; i++)
            {
                var detected = new List<MyVoxelBase>();
                var position = Block.PositionComp.GetPosition() + Block.PositionComp.WorldMatrixRef.Down * i * 3;
                var boundingSphereD = new BoundingSphereD(position, 10);
                MyGamePruningStructure.GetAllVoxelMapsInSphere(ref boundingSphereD, detected);
                foreach (var map in detected)
                {
                    GetResources(position, map);
                }
            }

            //sort materials and pick ores
            var options = new Dictionary<MyVoxelMaterialDefinition, int>();
            foreach (var m in materials.Keys)
            {
                var def = MyDefinitionManager.Static.GetVoxelMaterialDefinition(m);
                if (def == null)
                {
                    continue;
                }

                if (def.CanBeHarvested && def.IsRare && !string.IsNullOrEmpty(def.MinedOre) &&
                    !ResourceNode.Instance.MiningBlacklist.Contains(def.MinedOre))
                {
                    options.Add(def, materials[m]);
                }
            }

            if (options.Count == 0 && materials.Count >= 1)
            {
                foreach (var material in materials)
                {
                    var def = MyDefinitionManager.Static.GetVoxelMaterialDefinition(material.Key);
                    if (def != null)
                    {
                        options.Add(def, material.Value);
                    }
                }
            }

            //pick top value and add it to the producer
            MyVoxelMaterialDefinition top = null;
            foreach (var m in options)
            {
                if (top == null)
                {
                    top = m.Key;
                }
                else if (options[top] * top.MinedOreRatio < m.Value * m.Key.MinedOreRatio)
                {
                    top = m.Key;
                }
            }

            if (currentOre?.MinedOre == top?.MinedOre)
            {
                return;
            }

            currentOre = top;

            var nearby = new BoundingSphereD(Block.PositionComp.GetPosition(), (double)EncroachmentDistance);
            List<MyDrillBlock> nearbyDrills = new List<MyDrillBlock>();
            GetAllDrillsInSphere(nearby, nearbyDrills);

            foreach (var other in nearbyDrills)
            {
                if (other.MarkedForClose ||
                    other.Entity.EntityId == Entity.EntityId ||
                    other.currentOre?.MinedOre != currentOre?.MinedOre)
                {
                    continue;
                }

                other.MarkForUpdate();
            }

            MarkForUpdate();
        }

        private void CustomInfo(IMyTerminalBlock block, StringBuilder builder)
        {
            if (State == null)
            {
                return;
            }

            builder.Clear();
            builder.AppendLine();
            if (State.isProducing)
            {
                if (State.penalty > float.Epsilon)
                {
                    builder.AppendLine("Mining rates reduced! A nearby block is mining the same ore.");
                    builder.AppendLine();
                }

                builder.AppendFormat("Production Multiplier: {0:P2}",
                    (block.UpgradeValues["Productivity"] + block.UpgradeValues["Effectiveness"]) * (1 - State.penalty));
                builder.AppendLine();
            }

            builder.AppendFormat("Effectiveness: {0:P2}",
                block.UpgradeValues["Productivity"] + block.UpgradeValues["Effectiveness"]);
            builder.AppendLine();
            builder.AppendFormat("Detected Ore: ");
            builder.AppendLine(string.IsNullOrEmpty(State.oreName) ? "nothing" : State.oreName);
            builder.Append("Is producing: ");
            builder.AppendLine(State.isProducing.ToString());
            builder.Append("In ground: ");
            builder.AppendLine(State.isInGround.ToString());
            builder.Append("Inventory full: ");
            builder.Append(State.invFull.ToString());
        }

        private void RemoveFromMiners(MyEntity e)
        {
            if (minerProxyId != -1)
            {
                ResourceNode.Instance.MinerTree.RemoveProxy(minerProxyId);
                minerProxyId = -1;
            }

            if (IsProjection())
            {
                return;
            }

            var nearby = new BoundingSphereD(e.PositionComp.GetPosition(), EncroachmentDistance);
            List<MyDrillBlock> nearbyDrills = new List<MyDrillBlock>();
            GetAllDrillsInSphere(nearby, nearbyDrills);

            foreach (var other in nearbyDrills)
            {
                if (other.MarkedForClose || other.Entity.EntityId == e.EntityId ||
                    other.currentOre?.MinedOre != currentOre?.MinedOre)
                {
                    continue;
                }

                other.MarkForUpdate();
            }
        }

        private void AddToMiners()
        {
            if (string.IsNullOrEmpty(currentOre?.MinedOre) || IsProjection())
            {
                return;
            }

            var nearby = new BoundingSphereD(Block.PositionComp.GetPosition(), (double)EncroachmentDistance);
            if (minerProxyId == -1) // Add to the tree since it doesn't already exist
            {
                BoundingBoxD boundingBoxD = BoundingBoxD.CreateFromSphere(nearby);
                minerProxyId = ResourceNode.Instance.MinerTree.AddProxy(ref boundingBoxD, this, 0U, true);
            }

            List<MyDrillBlock> nearbyDrills = new List<MyDrillBlock>();
            GetAllDrillsInSphere(nearby, nearbyDrills);

            foreach (var other in nearbyDrills)
            {
                if (other.MarkedForClose || other.Entity.EntityId == Entity.EntityId ||
                    other.currentOre?.MinedOre != currentOre?.MinedOre)
                {
                    continue;
                }

                other.MarkForUpdate();
            }
        }

        private void GetResources(Vector3D pos, MyVoxelBase map)
        {
            var cache = new MyStorageData(MyStorageDataTypeFlags.ContentAndMaterial);
            cache.Resize(new Vector3I(1));

            Vector3I voxelPos;
            MyVoxelCoordSystems.WorldPositionToVoxelCoord(map.PositionLeftBottomCorner, ref pos, out voxelPos);
            map.Storage.ReadRange(cache, MyStorageDataTypeFlags.ContentAndMaterial, 0, voxelPos, voxelPos);

            if (cache.Material(0) != 255)
            {
                if (materials.ContainsKey(cache.Material(0)))
                {
                    materials[cache.Material(0)] += cache.Content(0);
                }
                else
                {
                    materials.Add(cache.Material(0), cache.Content(0));
                }
            }
        }
    }
}