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
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Game.ObjectBuilders.Definitions;
using VRage.ObjectBuilders;
using VRage.Voxels;
using VRageMath;

namespace ResourceNodes
{
    abstract class MyDrillBlock : MyAbstractAnimatedBlock
    {

        private static MyDefinitionId EId = new MyDefinitionId(typeof(MyObjectBuilder_GasProperties), "Electricity");

        protected IMyFunctionalBlock Blocc;
        protected IMyInventory Inventory;

        protected int tick = -1;
        protected int timesChecked = 0;
        protected int slowdown = 1;
        public bool IsProducing;
        protected bool InvFull, InGround;
        protected MyVoxelMaterialDefinition myOre = null;
        protected Action DepositedResources;

        protected int baseSpeed = 27;
        protected int invMultiplier = 8;

        private readonly Dictionary<byte, int> materials = new Dictionary<byte, int>();

        public DrillStateUpdate state;

        public abstract void SetEmissive(Color color);

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            LoadOntoBlock();

            Blocc = (IMyFunctionalBlock)Block;
            Block.UpgradeValues.Add("Productivity", 1f);
            Block.UpgradeValues.Add("Effectiveness", 1f);

            Block.OnClose += RemoveFromMiners;
            Blocc.AppendingCustomInfo += CustomInfo;

            BlockInit();
        }

        public abstract void BlockInit();

        public override void BeforeFirstUpdate()
        {
            if (MyAPIGateway.Session.IsServer)
            {
                MyInventory component = new MyInventory(invMultiplier, new Vector3(1), MyInventoryFlags.CanSend);
                foreach (var i in Inv?.GetItems())
                {
                    if (i != null)
                    {
                        component.AddItems(i.Amount, i.Content);
                    }
                }
                Block.Components.Remove(typeof(MyInventoryBase));
                Block.Components.Add<MyInventoryBase>(component);
            }
            ((MyInventory)Inv).Constraint = new MyInventoryConstraint(MySpaceTexts.ToolTipItemFilter_AnyOre, null, true).AddObjectBuilderType(typeof(MyObjectBuilder_Ore));
        }

        public override void GameUpdate()
        {
            if (!MyAPIGateway.Session.IsServer)
                return;

            tick++;
            if (!Blocc.CubeGrid.IsStatic)
            {
                Blocc.Enabled = false;
            }

            if (tick % 1000 == 0 && timesChecked < 20)
            {
                materials.Clear();
                List<MyVoxelBase> detected = new List<MyVoxelBase>();
                Vector3D position = Block.PositionComp.GetPosition() + (Block.PositionComp.WorldMatrixRef.Down * (Block.BlockDefinition.Size.Y + .25));
                BoundingSphereD boundingSphereD = new BoundingSphereD(position, 2);
                MyGamePruningStructure.GetAllVoxelMapsInSphere(ref boundingSphereD, detected);
                foreach (var map in detected)
                {
                    GetResources(position, map);
                }
                if (materials.Count >= 1)
                {
                    InGround = true;
                    AssignNewMaterial();
                }
                timesChecked++;
            }

            IsProducing = Blocc.Enabled && Blocc.IsWorking && !Inv.IsFull && InGround;

            if (IsProducing)
            {
                IsProducing = Block.ResourceSink.IsPoweredByType(EId) && Block.ResourceSink.IsPowerAvailable(EId, Block.ResourceSink.MaxRequiredInput);
            }

            if (tick % 10 == 0)
            {
                DrillStateUpdate packet = new DrillStateUpdate()
                {
                    blockId = Block.EntityId,
                };
                packet.isInGround = InGround;
                packet.invFull = InvFull;
                packet.oreName = myOre?.MinedOre ?? "nothing";
                packet.isProducing = IsProducing;
                ResourceNode.Instance.Network.TransmitToPlayersWithinRange(Block.PositionComp.GetPosition(), packet, 1500, false);

                if (Block.IsBuilt)
                {
                    if (!Block.IsFunctional)
                    {
                        SetEmissive(Color.Orange);
                    }
                    else
                    {
                        if (!InGround || !Blocc.Enabled || !Blocc.IsWorking)
                        {
                            SetEmissive(Color.Red);
                        }
                        else if (!IsProducing || myOre == null || Inv.IsFull || InvFull)
                        {
                            SetEmissive(Color.Yellow);
                        }
                    }
                }
                else
                {
                    if (!InGround)
                    {
                        SetEmissive(Color.Red);
                    }
                    else if (myOre != null)
                    {
                        SetEmissive(Color.Aqua);
                    }
                    else
                    {
                        SetEmissive(Color.Yellow);
                    }
                }
            }

            if (IsProducing)
            {
                if (myOre != null)
                {
                    int speed = (int)(baseSpeed / Block.UpgradeValues["Productivity"]) + slowdown;

                    if (tick % speed == 0)
                    {
                        float yield = 1 * Block.UpgradeValues["Effectiveness"];
                        MyObjectBuilder_Ore oreObject = MyObjectBuilderSerializer.CreateNewObject<MyObjectBuilder_Ore>(myOre.MinedOre);
                        
                        double amount = (yield * myOre.MinedOreRatio);
                        InvFull = !Inv.CanItemsBeAdded((MyFixedPoint)amount, oreObject);

                        if (!InvFull)
                        {
                            Inv.AddItems((MyFixedPoint)amount, oreObject);
                            DepositedResources?.Invoke();
                        }

                    }
                }

                if (tick % 1000 == 0)
                {
                    if (myOre != null)
                    {
                        double closest = double.MaxValue;
                        int close = 1;
                        foreach (var m in ResourceNode.Instance.Miners[myOre.MinedOre])
                        {
                            if (m != Block.EntityId)
                            {
                                double dist = Vector3D.DistanceSquared(ResourceNode.Instance.Locations[m], Block.PositionComp.GetPosition());
                                if (dist < closest)
                                {
                                    closest = dist;
                                }
                                if (dist < 2500)
                                {
                                    close++;
                                }
                            }
                            
                        }
                        closest = Math.Sqrt(closest);
                        slowdown = (int)(closest < 50 ? 50 - closest : 1) + (10 * close);
                    }
                }
            }
        }

        private void AssignNewMaterial()
        {
            //get all the materials
            for(int i = 0; i < 30; i++)
            {
                List<MyVoxelBase> detected = new List<MyVoxelBase>();
                Vector3D position = Block.PositionComp.GetPosition() + Block.PositionComp.WorldMatrixRef.Down * i * 3;
                BoundingSphereD boundingSphereD = new BoundingSphereD(position, 10);
                MyGamePruningStructure.GetAllVoxelMapsInSphere(ref boundingSphereD, detected);
                foreach (var map in detected)
                {
                    GetResources(position, map);
                }
            }

            //sort materials and pick ores
            Dictionary<MyVoxelMaterialDefinition, int> options = new Dictionary<MyVoxelMaterialDefinition, int>();
            foreach (var m in materials.Keys)
            {
                var def = MyDefinitionManager.Static.GetVoxelMaterialDefinition(m);
                if (def != null)
                {
                    if (def.CanBeHarvested && def.IsRare && !string.IsNullOrEmpty(def.MinedOre) && !ResourceNode.Instance.MiningBlacklist.Contains(def.MinedOre))
                    {
                        options.Add(def, materials[m]);
                    }
                }
            }

            if (options.Count == 0 && materials.Count >= 1)
            {
                var e = materials.Keys.GetEnumerator();
                e.MoveNext();
                var def = MyDefinitionManager.Static.GetVoxelMaterialDefinition(e.Current);
                if (def != null)
                {
                    options.Add(def, materials[e.Current]);
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

            RemoveFromMiners(Block);
            myOre = top;
            AddToMiners();
        }

        private void CustomInfo(IMyTerminalBlock block, StringBuilder builder)
        {
            if (state != null)
            {
                builder.Clear();
                builder.Append($"\nCurrently extracting {state.oreName}");
                builder.Append($"\nIs producing: {state.isProducing}");
                builder.Append($"\nIn ground: {state.isInGround}");
                builder.Append($"\nInventory full: {state.invFull}");
            }
        }

        private void RemoveFromMiners(MyEntity e)
        {
            if (myOre != null && !string.IsNullOrEmpty(myOre.MinedOre))
            {
                if (ResourceNode.Instance.Miners.ContainsKey(myOre.MinedOre))
                {
                    ResourceNode.Instance.Miners[myOre.MinedOre].Remove(e.EntityId);
                    ResourceNode.Instance.Locations.Remove(e.EntityId);
                }
            }
        }

        private void AddToMiners()
        {
            if (myOre != null && !string.IsNullOrEmpty(myOre.MinedOre))
            {
                if (!ResourceNode.Instance.Miners.ContainsKey(myOre.MinedOre))
                {
                    ResourceNode.Instance.Miners.Add(myOre.MinedOre, new HashSet<long>());
                }
                ResourceNode.Instance.Miners[myOre.MinedOre].Remove(Block.EntityId);
                ResourceNode.Instance.Miners[myOre.MinedOre].Add(Block.EntityId);

                ResourceNode.Instance.Locations.Remove(Block.EntityId);
                ResourceNode.Instance.Locations.Add(Block.EntityId, Block.PositionComp.GetPosition());
            }
        }

        private void GetResources(Vector3D pos, MyVoxelBase map)
        {
            MyStorageData cache = new MyStorageData(MyStorageDataTypeFlags.ContentAndMaterial);
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

        protected IMyInventory Inv
        {
            get { return Blocc.GetInventory(0); }
        }

    }
}
