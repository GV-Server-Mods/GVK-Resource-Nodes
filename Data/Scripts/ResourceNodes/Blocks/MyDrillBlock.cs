using Math0424.AnimationCore;
using Math0424.Networking;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Localization;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.Game.EntityComponents;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
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
        private const int CheckLongTicks = 60 * 5; // 60 ticks per second * however many seconds to check

        private ulong tick;
        private uint tickSmear;
        private ulong lastGeneration;
        private float yieldMultiplier = 1;

        private IMyFunctionalBlock functionalBlock;
        private bool invFull;
        private bool inGround;
        private MyVoxelMaterialDefinition currentOre;

        public bool IsProducing;
        protected Action DepositedResources;

        protected int InvMultiplier = 8;
        protected int BaseOrePerSecond = 1;
        protected float BasePowerConsumptionMultiplier = 1f;

        private readonly Dictionary<byte, int> materials = new Dictionary<byte, int>();

        public DrillStateUpdate State;

        private IMyInventory Inv => functionalBlock.GetInventory(0);

        protected abstract void SetEmissive(Color color);

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            LoadOntoBlock();
            Block.UpgradeValues.Add("Productivity", 0f);
            Block.UpgradeValues.Add("Effectiveness", 1f);
            Block.UpgradeValues.Add("PowerEfficiency", 1f);

            functionalBlock = (IMyFunctionalBlock)Block;

            Block.OnClose += RemoveFromMiners;
            functionalBlock.AppendingCustomInfo += CustomInfo;
            Block.OnUpgradeValuesChanged += MarkForUpdate;

            tickSmear = (uint)Math.Abs(GetHashCode() % TickRate);
            BlockInit();
        }

        private void MarkForUpdate()
        {
            lastGeneration = 0;
        }

        private void UpdateMultipliers()
        {
            Utils.Log($"Starting upgrades update");
            Dictionary<string, float> v = Block.UpgradeValues;
            yieldMultiplier = (v["Productivity"] + v["Effectiveness"]) * GetSlowdownFactor();
            Utils.Log($"Yield Mul: ${yieldMultiplier}");
            IMyShipDrill drill = Block as IMyShipDrill;
            if (drill != null)
            {
                Utils.Log($"Base power: ${BasePowerConsumptionMultiplier}");
                drill.PowerConsumptionMultiplier =
                    BasePowerConsumptionMultiplier *
                    (1 + v["Productivity"]) * (1f / v["PowerEfficiency"]) * (1f / v["Effectiveness"]);

                Utils.Log($"New power: {BasePowerConsumptionMultiplier * (1 + v["Productivity"]) * (1f / v["PowerEfficiency"]) * (1f / v["Effectiveness"])}");
            }
        }

        private float GetSlowdownFactor()
        {
            if (currentOre == null)
            {
                return 1;
            }

            List<long> miners;
            if (!ResourceNode.Instance.Miners.TryGetValue(currentOre.MinedOre, out miners))
            {
                return 1;
            }

            float slowdownFactor = 1;

            foreach (var entityId in miners)
            {
                if (entityId == Block.EntityId)
                {
                    continue;
                }

                double dist;
                Vector3D otherLocation;
                if (!ResourceNode.Instance.Locations.TryGetValue(entityId, out otherLocation))
                {
                    continue;
                }

                var location = Block.PositionComp.GetPosition();
                Vector3D.Distance(ref otherLocation, ref location, out dist);

                if (dist < EncroachmentDistance)
                {
                    slowdownFactor *= EncroachmentPenalty;
                }
            }

            return slowdownFactor;
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
        }

        public override void GameUpdate()
        {
            if (!MyAPIGateway.Session.IsServer)
            {
                return;
            }

            tick++;

            if (!functionalBlock.CubeGrid.IsStatic)
            {
                functionalBlock.Enabled = false;
            }

            if (tick % CheckLongTicks == tickSmear)
            {
                UpdateInGround();

                uint generation;
                if (currentOre != null &&
                    ResourceNode.Instance.Generation.TryGetValue(currentOre.MinedOre, out generation) &&
                    generation != lastGeneration)
                {
                    Utils.Log($"Generation for ore {currentOre?.MinedOre ?? "nothing"} is {generation}");
                    lastGeneration = generation;
                    UpdateMultipliers();
                }
            }

            IsProducing = functionalBlock.Enabled && functionalBlock.IsWorking && !Inv.IsFull && inGround;

            if (IsProducing)
            {
                IsProducing = Block.ResourceSink.IsPoweredByType(MyResourceDistributorComponent.ElectricityId) &&
                              Block.ResourceSink.IsPowerAvailable(MyResourceDistributorComponent.ElectricityId,
                                  Block.ResourceSink.MaxRequiredInputByType(
                                      MyResourceDistributorComponent.ElectricityId));
            }

            if (tick % 10 == 0)
            {
                var packet = new DrillStateUpdate()
                {
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

            if (!IsProducing)
            {
                return;
            }

            if (currentOre == null)
            {
                return;
            }

            if (tick % TickRate != tickSmear)
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

        private void UpdateInGround()
        {
            materials.Clear();
            var detected = new List<MyVoxelBase>();
            var position = Block.PositionComp.GetPosition() +
                           Block.PositionComp.WorldMatrixRef.Down * (Block.BlockDefinition.Size.Y + .25);
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

            RemoveFromMiners(Block);
            currentOre = top;
            AddToMiners();
        }

        private void CustomInfo(IMyTerminalBlock block, StringBuilder builder)
        {
            if (State != null)
            {
                builder.Clear();
                builder.Append($"\nCurrently extracting {State.oreName}");
                builder.Append($"\nIs producing: {State.isProducing}");
                builder.Append($"\nIn ground: {State.isInGround}");
                builder.Append($"\nInventory full: {State.invFull}");
            }
        }

        private void RemoveFromMiners(MyEntity e)
        {
            var entityId = e.EntityId;
            ResourceNode.Instance.Locations.Remove(entityId);
            if (currentOre == null || string.IsNullOrEmpty(currentOre.MinedOre))
            {
                return;
            }

            List<long> miners;
            if (!ResourceNode.Instance.Miners.TryGetValue(currentOre.MinedOre, out miners))
            {
                return;
            }

            Utils.Log($"Removing miner {entityId}");
            var index = miners.IndexOf(entityId);
            if (index == -1)
            {
                return;
            }

            miners.RemoveAtFast(index);

            uint currentGeneration;
            ResourceNode.Instance.Generation.TryGetValue(currentOre.MinedOre, out currentGeneration);
            ResourceNode.Instance.Generation[currentOre.MinedOre] = currentGeneration + 1;
        }

        private void AddToMiners()
        {
            if (currentOre != null && !string.IsNullOrEmpty(currentOre.MinedOre))
            {
                List<long> miners;
                if (!ResourceNode.Instance.Miners.TryGetValue(currentOre.MinedOre, out miners))
                {
                    miners = new List<long>();
                    ResourceNode.Instance.Miners.Add(currentOre.MinedOre, miners);
                }

                miners.Add(Block.EntityId);
                Utils.Log($"Adding miner {Block.EntityId}");

                ResourceNode.Instance.Locations.Remove(Block.EntityId);
                ResourceNode.Instance.Locations.Add(Block.EntityId, Block.PositionComp.GetPosition());
                uint currentGeneration;
                ResourceNode.Instance.Generation.TryGetValue(currentOre.MinedOre, out currentGeneration);
                ResourceNode.Instance.Generation[currentOre.MinedOre] = currentGeneration + 1;
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