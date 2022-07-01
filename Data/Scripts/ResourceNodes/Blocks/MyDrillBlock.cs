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
using ParallelTasks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using VRage;
using VRage.Collections;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entities;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Game.ObjectBuilders.ComponentSystem;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.Voxels;
using VRageMath;
using IMyInventory = VRage.Game.ModAPI.IMyInventory;

namespace ResourceNodes
{
    internal class MinerWorkData : WorkData
    {
        public BoundingSphereD Nearby;
        public List<MyDrillBlock> Drills = new List<MyDrillBlock>();

        public MinerWorkData(BoundingSphereD nearby)
        {
            Nearby = nearby;
        }
    }

    internal class MaterialScanWorkData : WorkData
    {
        public readonly Dictionary<byte, int> Materials = new Dictionary<byte, int>();
        public string Ore;
        public float Ratio;

        public MaterialScanWorkData(string currentOre, float currentOreRatio)
        {
            Ore = currentOre;
            Ratio = currentOreRatio;
        }
    }

    internal abstract class MyDrillBlock : MyAbstractAnimatedBlock
    {
        private const int TickRate = 3 * 60; // Every 3 seconds
        private const double RateInSeconds = TickRate / 60d;
        private const float EncroachmentDistance = 50f; // Distance to other miner mining same ore
        private const float EncroachmentPenalty = 0.50f; // 50% multiplicative reduction in effectiveness
        private const int ResourceUpdateRate = 60 * 30; // 60 ticks per second * however many seconds to check

        private static readonly Guid StorageGuid = new Guid("80B6388C-EE37-4C8D-B0E3-2272E3892901");
        private static readonly MyDefinitionId Electricity = MyResourceDistributorComponent.ElectricityId;

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
        private string currentOre;
        private float currentOreRatio;
        private MyObjectBuilder_Ore oreObject;
        private float allowedDistanceToGround;

        public bool IsProducing;
        protected Action DepositedResources;

        protected int InvMultiplier = 8;
        protected float BaseOrePerSecond = 1;
        protected float BasePowerMW = 1f;

        private Task? groundScanTask = null;

        private int lastUpdateTick;

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

            Block.OnMarkForClose += RemoveFromMiners;
            functionalBlock.AppendingCustomInfo += CustomInfo;
            Block.OnUpgradeValuesChanged += MarkForUpdate;
            functionalBlock.IsWorkingChanged += OnWorkingChanged;

            tickResource = Math.Abs(GetHashCode() % TickRate);
            tickGroundUpdate = Math.Abs(GetHashCode() % ResourceUpdateRate);
            BlockInit();
        }

        private bool IsProjection()
        {
            return Block != null && Block.CubeGrid.Physics == null;
        }

        private void OnWorkingChanged(IMyCubeBlock obj)
        {
            lastUpdateTick = MyAPIGateway.Session.GameplayFrameCounter;
            MarkForUpdate();
        }

        private void MarkForUpdate()
        {
            needsUpdate = true;
            functionalBlock.RefreshCustomInfo();
        }

        private void UpdateMultipliers()
        {
            if (IsProjection())
            {
                return;
            }

            if (string.IsNullOrEmpty(currentOre))
            {
                return;
            }

            var data = new MinerWorkData(new BoundingSphereD(Block.PositionComp.GetPosition(), EncroachmentDistance));

            MyAPIGateway.Parallel.Start(d =>
            {
                var minerData = (MinerWorkData)d;
                GetAllDrillsInSphere(minerData.Nearby, minerData.Drills);
            }, d =>
            {
                if (!IsValid(this) || !IsValid(Entity))
                {
                    return;
                }

                var minerData = (MinerWorkData)d;
                penaltyFactor = 0;
                foreach (var other in minerData.Drills)
                {
                    if (!IsValid(other) || !IsValid(other.Entity) ||
                        other.Entity.EntityId == Entity.EntityId ||
                        other.currentOre != currentOre || !other.IsProducing)
                    {
                        continue;
                    }

                    penaltyFactor += (1 - penaltyFactor) * EncroachmentPenalty;
                }

                var productivity = Block.UpgradeValues["Productivity"];
                var effectiveness = (Block.UpgradeValues["Effectiveness"] - 1f) * 3f + 1f;
                var powerEfficiency = (Block.UpgradeValues["PowerEfficiency"] - 1f) * 3f + 1f;

                yieldMultiplier = (productivity + effectiveness) * (1 - penaltyFactor);
                var drill = Block as IMyShipDrill;
                if (drill == null)
                {
                    return;
                }

                MyResourceSinkComponent sink;
                if (!drill.Components.TryGet(out sink))
                {
                    return;
                }

                var modifier = 1 * (1 + productivity) * (1f / powerEfficiency) * (1f / effectiveness);
                var oldPower = sink.MaxRequiredInputByType(Electricity);
                var newPower = BasePowerMW * modifier;
                if (Math.Abs(oldPower - newPower) < float.Epsilon)
                {
                    return;
                }

                sink.SetRequiredInputByType(Electricity, 0.002f);
                sink.SetMaxRequiredInputByType(Electricity, newPower);
                sink.SetRequiredInputFuncByType(Electricity, () => Block.IsWorking ? BasePowerMW * modifier : 0.002f);
                sink.Update();
                functionalBlock.RefreshCustomInfo();
            }, data);
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

                if (drill.MarkedForClose || drill.Closed ||
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
            groundScanTask = UpdateInGround();
        }

        public override void GameUpdate()
        {
            tick = MyAPIGateway.Session.GameplayFrameCounter;
            var updateThisTick = needsUpdate;
            needsUpdate = false;

            if (!functionalBlock.CubeGrid.IsStatic)
            {
                functionalBlock.Enabled = false;
                return;
            }

            if (tick % ResourceUpdateRate == tickGroundUpdate)
            {
                if (!MyAPIGateway.Session.IsServer || (groundScanTask != null && string.IsNullOrEmpty(currentOre)))
                {
                    return;
                }

                groundScanTask = UpdateInGround();
            }

            if (updateThisTick && !string.IsNullOrEmpty(currentOre))
            {
                UpdateMultipliers();
                functionalBlock.RefreshCustomInfo();
                if (!MyAPIGateway.Session.IsServer)
                {
                    return;
                }
            }

            if (tick % 10 == 0 || updateThisTick)
            {
                var packet = new DrillStateUpdate
                {
                    penalty = penaltyFactor,
                    blockId = Block.EntityId,
                    isInGround = inGround,
                    invFull = invFull,
                    oreName = string.IsNullOrEmpty(currentOre) ? "nothing" : currentOre,
                    isProducing = IsProducing,
                    forceUpdate = updateThisTick
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
                    else if (!string.IsNullOrEmpty(currentOre))
                    {
                        SetEmissive(Color.Aqua);
                    }
                    else
                    {
                        SetEmissive(Color.Yellow);
                    }
                }
            }

            if (!updateThisTick && tick % TickRate != tickResource)
            {
                return;
            }

            UpdateIsProducing();

            if (!IsProducing)
            {
                return;
            }

            if (oreObject == null)
            {
                return;
            }

            var periodMul = ((float)tick - lastUpdateTick) / TickRate;
            var amount = BaseOrePerSecond * currentOreRatio * RateInSeconds * yieldMultiplier;
            amount *= periodMul;
            lastUpdateTick = tick;

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
                IsProducing = Block.ResourceSink.IsPoweredByType(Electricity) &&
                              Block.ResourceSink.IsPowerAvailable(Electricity,
                                  Block.ResourceSink.MaxRequiredInputByType(Electricity));
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

        private Task? UpdateInGround()
        {
            if (IsProjection())
            {
                return null;
            }

            var data = new MaterialScanWorkData(currentOre, currentOreRatio);

            return MyAPIGateway.Parallel.Start(d =>
            {
                var materialData = (MaterialScanWorkData)d;
                var detected = new List<MyVoxelBase>();
                var position = Block.PositionComp.GetPosition() +
                               Block.PositionComp.WorldMatrixRef.Down * allowedDistanceToGround;
                var boundingSphereD = new BoundingSphereD(position, 2);
                MyGamePruningStructure.GetAllVoxelMapsInSphere(ref boundingSphereD, detected);
                foreach (var map in detected)
                {
                    GetResources(position, map, materialData);
                }

                if (materialData.Materials.Count >= 1)
                {
                    AssignNewMaterial(materialData);
                }
            }, d =>
            {
                var materialData = (MaterialScanWorkData)d;
                inGround = materialData.Materials.Count >= 1;
                currentOre = materialData.Ore;
                currentOreRatio = materialData.Ratio;
                oreObject = MyObjectBuilderSerializer.CreateNewObject<MyObjectBuilder_Ore>(currentOre);
                UpdateNearbyDrills(Block.EntityId);
                MarkForUpdate();
                groundScanTask = null;
            }, data);
        }

        private void AssignNewMaterial(MaterialScanWorkData data)
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
                    GetResources(position, map, data);
                }
            }

            //sort materials and pick ores
            var options = new Dictionary<MyVoxelMaterialDefinition, int>();
            foreach (var mat in data.Materials)
            {
                var def = MyDefinitionManager.Static.GetVoxelMaterialDefinition(mat.Key);
                if (def == null)
                {
                    continue;
                }

                if (def.CanBeHarvested && def.IsRare && !string.IsNullOrEmpty(def.MinedOre) &&
                    !ResourceNode.Instance.MiningBlacklist.Contains(def.MinedOre))
                {
                    options.Add(def, mat.Value);
                }
            }

            if (options.Count == 0)
            {
                foreach (var material in data.Materials)
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

            if (top == null || data.Ore == top.MinedOre)
            {
                return;
            }

            data.Ore = top.MinedOre;
            data.Ratio = top.MinedOreRatio;
        }

        private void CustomInfo(IMyTerminalBlock block, StringBuilder builder)
        {
            builder.Clear();
            builder.AppendLine();

            var productivity = Block.UpgradeValues["Productivity"];
            var effectiveness = (Block.UpgradeValues["Effectiveness"] - 1) * 3f + 1;

            if (IsProducing)
            {
                if (penaltyFactor > float.Epsilon)
                {
                    builder.AppendLine("Mining rates reduced! A nearby block is mining the same ore.");
                    builder.AppendLine();
                }

                builder.AppendFormat("Production Multiplier: {0:P2}",
                    (productivity + effectiveness) * (1 - penaltyFactor));
                builder.AppendLine();
            }

            builder.AppendFormat("Effectiveness: {0:P2}", productivity + effectiveness);
            builder.AppendLine();
            builder.AppendFormat("Detected Ore: ");
            builder.AppendLine(string.IsNullOrEmpty(currentOre) ? "nothing" : currentOre);
            builder.Append("Is producing: ");
            builder.AppendLine(IsProducing.ToString());
            builder.Append("In ground: ");
            builder.AppendLine(inGround.ToString());
            builder.Append("Inventory full: ");
            builder.Append(invFull.ToString());
            UpdateTerminal();
        }

        private void UpdateTerminal()
        {
            functionalBlock.ShowOnHUD = !functionalBlock.ShowOnHUD;
            functionalBlock.ShowOnHUD = !functionalBlock.ShowOnHUD;
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

            UpdateNearbyDrills(e.EntityId);
        }

        private void AddToMiners()
        {
            if (string.IsNullOrEmpty(currentOre) || IsProjection())
            {
                return;
            }

            if (minerProxyId == -1) // Add to the tree since it doesn't already exist
            {
                var loc = new BoundingSphereD(Block.PositionComp.GetPosition(), EncroachmentDistance);
                var boundingBox = BoundingBoxD.CreateFromSphere(loc);
                minerProxyId = ResourceNode.Instance.MinerTree.AddProxy(ref boundingBox, this, 0U, true);
            }

            UpdateNearbyDrills(Block.EntityId);
        }

        private void UpdateNearbyDrills(long entityId)
        {
            var data = new MinerWorkData(new BoundingSphereD(Block.PositionComp.GetPosition(), EncroachmentDistance));

            MyAPIGateway.Parallel.Start(d =>
            {
                var minerData = (MinerWorkData)d;
                GetAllDrillsInSphere(minerData.Nearby, minerData.Drills);
            }, d =>
            {
                if (!IsValid(this) || !IsValid(Entity))
                {
                    return;
                }

                var minerData = (MinerWorkData)d;

                foreach (var other in minerData.Drills)
                {
                    if (!IsValid(other) || !IsValid(other.Entity) ||
                        other.Entity.EntityId == Entity.EntityId ||
                        other.currentOre != currentOre)
                    {
                        continue;
                    }

                    other.MarkForUpdate();
                }
            }, data);
        }

        private void GetResources(Vector3D pos, MyVoxelBase map, MaterialScanWorkData data)
        {
            var cache = new MyStorageData(MyStorageDataTypeFlags.ContentAndMaterial);
            cache.Resize(new Vector3I(1));

            Vector3I voxelPos;
            MyVoxelCoordSystems.WorldPositionToVoxelCoord(map.PositionLeftBottomCorner, ref pos, out voxelPos);
            map.Storage.ReadRange(cache, MyStorageDataTypeFlags.ContentAndMaterial, 0, voxelPos, voxelPos);

            if (cache.Material(0) != 255)
            {
                if (data.Materials.ContainsKey(cache.Material(0)))
                {
                    data.Materials[cache.Material(0)] += cache.Content(0);
                }
                else
                {
                    data.Materials.Add(cache.Material(0), cache.Content(0));
                }
            }
        }

        private static bool IsValid(IMyEntity entity)
        {
            return !(entity == null || entity.MarkedForClose || entity.Closed);
        }

        private static bool IsValid(MyGameLogicComponent entity)
        {
            return !(entity == null || entity.MarkedForClose || entity.Closed);
        }

        public void UpdateFromState(DrillStateUpdate state)
        {
            if (MyAPIGateway.Session.IsServer)
            {
                return;
            }

            penaltyFactor = state.penalty;
            invFull = state.invFull;
            IsProducing = state.isProducing;
            currentOre = state.oreName;
            inGround = state.isInGround;
            if (state.forceUpdate)
            {
                needsUpdate = true;
            }
        }
    }
}