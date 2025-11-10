using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using SpaceEngineers.Game.ModAPI;
using VRage.Game.ModAPI;
using VRage.Utils;
using VRageMath;

namespace TSUT.HeatManagement
{
    public class HmsApi
    {
        public string ApiVersion = "1.0.2";
        public static long HeatApiMessageId = 35136709491; // Unique message ID for heat API
        public static long HeatProviderMesageId = 35136709492; // Unique message ID for heat provider

        private static HmsApi _instance;
        private Action _onReady;
        public IHeatUtils Utils;
        public IHeatEffects Effects;
        private bool _isApiReceived = false;

        public HmsApi(Action onReady)
        {
            if (_instance != null)
            {
                return; // Prevent multiple instances
            }
            _instance = this;
            _onReady = onReady;
            MyAPIGateway.Utilities.RegisterMessageHandler(HeatApiMessageId, OnApiReceived);
        }

        private void OnApiReceived(object obj)
        {
            if (_isApiReceived)
                return;
            Utils = new HmsUtils(obj);
            Effects = new HmsEffects(obj);
            _onReady?.Invoke();
            _isApiReceived = true;
        }

        public void RegisterHeatBehaviorFactory(
            Func<MyCubeGrid, List<IMyCubeBlock>> blockSelector,
            Func<IMyCubeBlock, AHeatBehavior> behaviorCreator
        )
        {
            MyAPIGateway.Utilities.SendModMessage(HeatProviderMesageId, new Dictionary<string, object>
            {
                { "factory", new Func<long, IDictionary<long, IDictionary<string, object>>>((gridId) =>
                    {
                        MyCubeGrid grid = MyAPIGateway.Entities.GetEntityById(gridId) as MyCubeGrid;
                        var selectedBlocks = blockSelector.Invoke(grid);
                        Dictionary<IMyCubeBlock, AHeatBehavior> behaviors = new Dictionary<IMyCubeBlock, AHeatBehavior>();
                        foreach (var block in selectedBlocks) {
                            behaviors.Add(block, behaviorCreator(block));
                        }
                        return MapBehaviors(behaviors);
                    })
                },
                { "creator", new Func<long, IDictionary<string, object>>((blockId) =>
                    {
                        MyCubeBlock block = MyAPIGateway.Entities.GetEntityById(blockId) as MyCubeBlock;
                        var behavior = behaviorCreator(block);
                        if (behavior == null) {
                            return null;
                        }
                        return MapBehavior(behavior);
                    })
                }
            });
        }

        public interface IHeatUtils
        {
            /// <summary>
            /// Calculates the ambient temperature for a given block, considering its environment.
            /// </summary>
            float CalculateAmbientTemperature(IMyCubeBlock block);

            /// <summary>
            /// Estimates the specific heat capacity based on the provided density.
            /// </summary>
            float EstimateSpecificHeat(float density);

            /// <summary>
            /// Calculates the heat loss from an active thruster over a given time interval.
            /// </summary>
            float GetActiveThrusterHeatLoss(IMyThrust thruster, float thrustRatio, float deltaTime);

            /// <summary>
            /// Calculates the heat loss from an active air vent over a given time interval.
            /// </summary>
            float GetActiveVentHealLoss(IMyAirVent vent, float deltaTime);

            /// <summary>
            /// Calculates the heat loss from an active heat vent over a given time interval.
            /// </summary>
            float GetActiveHeatVentLoss(IMyHeatVent vent, float deltaTime);

            /// <summary>
            /// Calculates the ambient heat loss for a block over a given time interval.
            /// </summary>
            float GetAmbientHeatLoss(IMyCubeBlock block, float deltaTime);

            /// <summary>
            /// Returns the density of the specified block.
            /// </summary>
            float GetDensity(IMyCubeBlock block);

            /// <summary>
            /// Gets the current heat value of the specified block.
            /// </summary>
            float GetHeat(IMyCubeBlock block);

            /// <summary>
            /// Returns the area of the largest face of the specified slim block.
            /// </summary>
            float GetLargestFaceArea(IMySlimBlock block);

            /// <summary>
            /// Returns the mass of the specified block.
            /// </summary>
            float GetMass(IMyCubeBlock block);

            /// <summary>
            /// Returns the real surface area of the specified block (e.g., battery).
            /// </summary>
            float GetRealSurfaceArea(IMyCubeBlock battery);

            /// <summary>
            /// Gets the sun direction vector for a block on a given planet.
            /// </summary>
            Vector3 GetSunDirection(IMyCubeBlock block, MyPlanet planet);

            /// <summary>
            /// Returns the temperature at a specific position on a planet.
            /// </summary>
            float GetTemperatureOnPlanet(Vector3D position);

            /// <summary>
            /// Returns the thermal capacity of the specified block.
            /// </summary>
            float GetThermalCapacity(IMyCubeBlock block);

            /// <summary>
            /// Determines if the block is in a pressurized room.
            /// </summary>
            bool IsBlockInPressurizedRoom(IMyCubeBlock block);

            /// <summary>
            /// Sets the heat value of a block. Optionally, the operation can be silent.
            /// </summary>
            void SetHeat(IMyCubeBlock block, float heat, bool silent = true);

            /// <summary>
            /// Applies a heat change to a block and returns the new heat value.
            /// </summary>
            float ApplyHeatChange(IMyCubeBlock block, float heatChange);

            /// <summary>
            /// Returns the wind speed affecting the specified block.
            /// </summary>
            float GetBlockWindSpeed(IMyCubeBlock block);

            /// <summary>
            /// Calculates the heat exchange between two neighboring blocks over a given time interval.
            /// </summary>
            float GetExchangeWithNeighbor(IMyCubeBlock block, IMyCubeBlock neighbor, float deltaTime);

            /// <summary>
            /// Returns the air density at the specified block's location.
            /// </summary>
            float GetAirDensity(IMyCubeBlock block);

            /// <summary>
            /// Calculates the heat loss from an active exhaust block over a given time interval.
            /// </summary>
            float GetActiveExhaustHeatLoss(IMyExhaustBlock exhaust, float deltaTime);

            /// <summary>
            /// Receives the heat network data.
            /// Returns null if the block is not part of a heat network.
            /// </summary>
            HeatNetworkData? GetNetworkData(IMyCubeBlock block);

            /// <summary>
            /// Calculates the heat exchange between a block and networkBlock if it's pipe network over a given time interval.
            /// Returns 0 if the networkBlock is not part of a heat network.
            /// </summary>
            float GetExchangeWithNetwork(IMyCubeBlock block, IMyCubeBlock networkBlock, float deltaTime);

            /// <summary>
            /// Calculates the heat exchange between two blocks over a given time interval.
            /// Considers both regular adjacency and pipe network connections.
            /// Returns 0 if the blocks are not connected in any way.
            /// </summary>
            float GetExchangeUniversal(IMyCubeBlock block, IMyCubeBlock neighborBlock, float deltaTime);

            /// <summary>
            /// Retrieves the current heat management system configuration.
            /// Contains all the configurable parameters that control the behavior of the heat system,
            /// such as cooling rates, conductivity values, and various behavioral flags.
            /// </summary>
            /// <returns>The current heat management system configuration.</returns>
            HmsConfig GetHmsConfig();
        }

        public struct HeatNetworkData
        {
            public int hash;
            public int length;
            public float averageTemperature;
        }

        public interface IHeatEffects
        {
            /// <summary>
            /// Instantiates a smoke effect on the specified battery block to indicate overheating or damage.
            /// </summary>
            void InstantiateSmoke(IMyCubeBlock battery);

            /// <summary>
            /// Removes the smoke effect from the specified battery block.
            /// </summary>
            void RemoveSmoke(IMyCubeBlock battery);

            /// <summary>
            /// Updates the heat-related lighting effect on the specified block.
            /// </summary>
            void UpdateBlockHeatLight(IMyCubeBlock block, float heat);

            /// <summary>
            /// Updates the positions of all heat-related lights in the scene.
            /// </summary>
            void UpdateLightsPosition();
        }

        

        public class HmsConfig
        {
            /// <summary>
            /// The version of the heat management system configuration.
            /// Used for config versioning and updates.
            /// </summary>
            public string HEAT_SYSTEM_VERSION { get; set; }

            /// <summary>
            /// Controls whether the configuration should automatically update when a new version is detected.
            /// If true, the config will be reset to defaults when version mismatch is detected.
            /// </summary>
            public bool HEAT_SYSTEM_AUTO_UPDATE { get; set; }

            /// <summary>
            /// Controls how quickly heat dissipates from blocks into the atmosphere.
            /// Increase for faster cooling, decrease for slower cooling.
            /// </summary>
            public float HEAT_COOLDOWN_COEFF { get; set; }

            /// <summary>
            /// Controls how much heat is radiated into space from blocks.
            /// Higher values mean more heat is lost to space.
            /// </summary>
            public float HEAT_RADIATION_COEFF { get; set; }

            /// <summary>
            /// The fraction of energy discharged from batteries that is converted into heat.
            /// Raise to make batteries generate more heat per discharge.
            /// </summary>
            public float DISCHARGE_HEAT_FRACTION { get; set; }

            /// <summary>
            /// Controls whether the discharge heat fraction can be configured by users.
            /// </summary>
            public bool DISCHARGE_HEAT_CONFIGURABLE { get; set; }

            /// <summary>
            /// Governs how efficiently heat spreads between connected blocks.
            /// Higher values mean heat equalizes faster across the grid.
            /// </summary>
            public float THERMAL_CONDUCTIVITY { get; set; }

            /// <summary>
            /// The amount of heat removed per tick by a vent.
            /// Increase to make vents more effective at cooling.
            /// </summary>
            public float VENT_COOLING_RATE { get; set; }

            /// <summary>
            /// The amount of heat removed per tick by thrusters.
            /// Increase to make thrusters more effective at cooling themselves.
            /// </summary>
            public float THRUSTER_COOLING_RATE { get; set; }

            /// <summary>
            /// The temperature at which heat source blocks are considered overheated and may explode.
            /// </summary>
            public float CRITICAL_TEMP { get; set; }

            /// <summary>
            /// Modifies how much wind (planetary atmosphere) helps cool blocks.
            /// Increase for stronger wind cooling effects.
            /// </summary>
            public float WIND_COOLING_MULT { get; set; }

            /// <summary>
            /// If true, only grids owned by players are affected by the heat system.
            /// Set to false to include all grids.
            /// </summary>
            public bool LIMIT_TO_PLAYER_GRIDS { get; set; }

            /// <summary>
            /// Controls how efficiently heat pipes transfer heat between connected blocks.
            /// Higher values mean heat pipes are more effective at heat transfer.
            /// </summary>
            public float HEATPIPE_CONDUCTIVITY { get; set; }

            /// <summary>
            /// Controls the rate at which exhaust blocks reject heat to the environment (joules/second).
            /// </summary>
            public float EXHAUST_HEAT_REJECTION_RATE { get; set; }

            /// <summary>
            /// Controls whether blocks should visually glow when they heat up.
            /// </summary>
            public bool HEAT_GLOW_INDICATION { get; set; }
        }

        public abstract class AHeatBehavior
        {
            protected IMyCubeBlock Block;

            public AHeatBehavior(IMyCubeBlock block)
            {
                Block = block;
            }

            // public static IDictionary<IMyCubeBlock, AHeatBehavior> CollectHeatBehaviors(MyCubeGrid grid)
            // {
            //     Replace 'AHeatBehaviorDerived' with your actual logic, the rest is here for demonstration.
            //     var behaviors = new Dictionary<IMyCubeBlock, AHeatBehavior>();
            //     foreach (var block in grid.GetFatBlocks<MyCubeBlock>())
            //         {
            //             if (block is IMyCubeBlock)
            //             {
            //                 var cubeBlock = block as IMyCubeBlock;
            //                 behaviors[cubeBlock] = new AHeatBehaviorDerived(cubeBlock);
            //             }
            //         }
            //     return behaviors;
            // }

            /// <summary>
            /// Called every simulation tick to calculate the heat change for this block.
            /// Return the amount of heat to add (positive) or remove (negative) for the given deltaTime.
            /// This is the main entry point for your custom heat logic.
            /// </summary>
            /// <param name="deltaTime">The time in seconds since the last update.</param>
            /// <returns>The heat change to apply to the block.</returns>
            abstract public float GetHeatChange(float deltaTime);

            /// <summary>
            /// Called every simulation tick to allow this block to exchange heat with its neighbors.
            /// Use this to implement heat spreading or networked heat transfer.
            /// </summary>
            /// <param name="deltaTime">The time in seconds since the last update.</param>
            abstract public void SpreadHeat(float deltaTime);

            /// <summary>
            /// Called when the block or its heat behavior is being removed from the grid (e.g., block destroyed or grid unloaded).
            /// Use this to clean up any resources, detach events, or perform finalization logic.
            /// </summary>
            abstract public void Cleanup();

            /// <summary>
            /// Called after the block's heat value has been updated (e.g., after ApplyHeatChange or external heat set).
            /// Use this to react to the new heat value, such as triggering effects, damage, or state changes.
            /// </summary>
            /// <param name="heat">The new heat value of the block.</param>
            abstract public void ReactOnNewHeat(float heat);

            internal void AddNeighborAndNetworksInfo(
               IMyCubeBlock block,
               HmsApi api,
               StringBuilder info,
               out float cumulativeNeighborHeatChange,
               out float cumulativeNetworkHeatChange
           )
            {
                var temperatureChange = 0f;
                Dictionary<IMyCubeBlock, float> neighborList;
                Dictionary<IMyCubeBlock, float> networkList;
                Dictionary<IMyCubeBlock, HeatNetworkData> neighborNetworkData;
                CalculateNeighborExchangeStandard(
                    1,
                    block,
                    api,
                    ref temperatureChange,
                    out cumulativeNeighborHeatChange,
                    out cumulativeNetworkHeatChange,
                    out neighborList,
                    out networkList,
                    out neighborNetworkData
                    );

                info.AppendLine($"  Neighbor Block: {cumulativeNeighborHeatChange:+0.00;-0.00;0.00} °C/s");
                info.AppendLine($"  Heat pipes: {cumulativeNetworkHeatChange:+0.00;-0.00;0.00} °C/s");

                if (neighborList.Count > 0)
                {
                    info.AppendLine("");
                    info.AppendLine($"Neighbors:");
                    foreach (var kvp in neighborList)
                    {
                        var neighbor = kvp.Key;
                        var tempChange = kvp.Value;
                        info.AppendLine($"- {neighbor.DisplayNameText} ({api.Utils.GetHeat(neighbor):F2}°C) -> {tempChange:F4} °C/s");
                    }
                    foreach (var kvp in networkList)
                    {
                        var neighbor = kvp.Key;
                        var tempChange = kvp.Value;
                        info.AppendLine($"- {neighbor.DisplayNameText}-NET- ({api.Utils.GetHeat(neighbor):F2}°C) -> {tempChange:F4} °C/s");
                    }
                }
                if (networkList.Count > 0)
                {
                    info.AppendLine("");
                    info.AppendLine($"Pipe networks:");
                    foreach (var kvp in networkList)
                    {
                        var networkBlock = kvp.Key;
                        var networkData = neighborNetworkData[networkBlock];
                        info.AppendLine($"- #{networkData.hash} Length: {networkData.length}, Avg: {networkData.averageTemperature:F1} °C");
                    }
                }
            }

            internal float SpreadHeatStandard(float deltaTime, IMyCubeBlock block, HmsApi api)
            {
                var temperatureChange = 0f;
                float neighborCumulative;
                float networkCumulative;
                Dictionary<IMyCubeBlock, float> neighborList;
                Dictionary<IMyCubeBlock, float> networkList;
                Dictionary<IMyCubeBlock, HeatNetworkData> neighborNetworkData;
                CalculateNeighborExchangeStandard(
                    deltaTime,
                    block,
                    api,
                    ref temperatureChange,
                    out neighborCumulative,
                    out networkCumulative,
                    out neighborList,
                    out networkList,
                    out neighborNetworkData
                    );
                foreach (var kvm in neighborList)
                {
                    var neighbor = kvm.Key;
                    var tempChange = kvm.Value;
                    var neighborTemp = api.Utils.GetHeat(neighbor);
                    var newTemp = neighborTemp + tempChange;
                    api.Utils.SetHeat(neighbor, newTemp);
                }
                foreach (var kvm in networkList)
                {
                    var neighbor = kvm.Key;
                    var tempChange = kvm.Value;
                    var neighborTemp = api.Utils.GetHeat(neighbor);
                    var newTemp = neighborTemp + tempChange;
                    api.Utils.SetHeat(neighbor, newTemp);
                }

                var ownTemperature = api.Utils.GetHeat(block);
                api.Utils.SetHeat(block, ownTemperature + temperatureChange);
                return ownTemperature + temperatureChange;
            }

            public void CalculateNeighborExchangeStandard(
                float deltaTime,
                IMyCubeBlock block,
                HmsApi api,
                ref float ownHeatChange,
                out float neighborCumulative,
                out float networkCumulative,
                out Dictionary<IMyCubeBlock, float> neighborBlocks,
                out Dictionary<IMyCubeBlock, float> neighborNetworks,
                out Dictionary<IMyCubeBlock, HeatNetworkData> neighborNetworkData
            )
            {
                neighborBlocks = new Dictionary<IMyCubeBlock, float>();
                neighborNetworks = new Dictionary<IMyCubeBlock, float>();
                neighborNetworkData = new Dictionary<IMyCubeBlock, HeatNetworkData>();
                neighborCumulative = 0f;
                networkCumulative = 0f;

                var neighborList = new List<IMySlimBlock>();
                block.SlimBlock.GetNeighbours(neighborList);

                float energyTransferred = 0f;
                var ownCapacity = api.Utils.GetThermalCapacity(block);

                foreach (var neighborSlim in neighborList)
                {
                    var neighborFat = neighborSlim.FatBlock;
                    if (neighborFat == null)
                        continue;
                    var netwrorkData = api.Utils.GetNetworkData(neighborFat);
                    var capacity = api.Utils.GetThermalCapacity(neighborFat);
                    var transfer = api.Utils.GetExchangeUniversal(block, neighborFat, deltaTime);
                    if (netwrorkData != null)
                    {
                        neighborNetworks.Add(neighborFat, transfer / capacity);
                        neighborNetworkData.Add(neighborFat, (HeatNetworkData)netwrorkData);
                        networkCumulative = -transfer / ownCapacity;
                    }
                    else
                    {
                        neighborBlocks.Add(neighborFat, transfer / capacity);
                        neighborCumulative = -transfer / ownCapacity;
                    }
                    energyTransferred -= transfer;
                }

                ownHeatChange = energyTransferred / ownCapacity;
            }
        }

        private IDictionary<long, IDictionary<string, object>> MapBehaviors(Dictionary<IMyCubeBlock, AHeatBehavior> behaviors)
        {
            var result = new Dictionary<long, IDictionary<string, object>>();
            foreach (var kvp in behaviors)
            {
                var block = kvp.Key;
                var behavior = kvp.Value;
                if (block == null || behavior == null)
                    continue;

                var blockId = block.EntityId;
                if (!result.ContainsKey(blockId))
                {
                    result[blockId] = new Dictionary<string, object>();
                }
                result[blockId] = MapBehavior(behavior);
            }
            return result;
        }

        private IDictionary<string, object> MapBehavior(AHeatBehavior behavior)
        {
            return new Dictionary<string, object>
                {
                    { "GetHeatChange", new Func<float, float>((deltaTime) => behavior.GetHeatChange(deltaTime)) },
                    { "ReactOnNewHeat", new Action<float>(heat => behavior.ReactOnNewHeat(heat)) },
                    { "SpreadHeat", new Action<float>(deltaTime => behavior.SpreadHeat(deltaTime)) },
                    { "Cleanup", new Action(() => behavior.Cleanup()) }
                };
        }

        internal void Cleanup()
        {
            MyAPIGateway.Utilities.UnregisterMessageHandler(HeatApiMessageId, OnApiReceived);
            Utils = null;
            Effects = null;
            _instance = null;
        }

        internal class HmsUtils : IHeatUtils
        {
            private IDictionary<string, object> client;

            public HmsUtils(object client)
            {
                this.client = client as IDictionary<string, object>;
            }

            public float ApplyHeatChange(IMyCubeBlock block, float heatChange)
            {
                object method;
                if (client.TryGetValue("ApplyHeatChange", out method) && method is Func<long, float, float>)
                {
                    var fn = (Func<long, float, float>)method;
                    return fn(block?.EntityId ?? 0, heatChange);
                }
                return 0f;
            }


            public float CalculateAmbientTemperature(IMyCubeBlock block)
            {
                object method;
                if (client.TryGetValue("CalculateAmbientTemperature", out method) && method is Func<long, float>)
                {
                    var fn = (Func<long, float>)method;
                    return fn(block?.EntityId ?? 0);
                }
                return 0f;
            }


            public float EstimateSpecificHeat(float density)
            {
                object method;
                if (client.TryGetValue("EstimateSpecificHeat", out method) && method is Func<float, float>)
                {
                    var fn = (Func<float, float>)method;
                    return fn(density);
                }
                return 0f;
            }


            public float GetActiveExhaustHeatLoss(IMyExhaustBlock exhaust, float deltaTime)
            {
                object method;
                if (client.TryGetValue("GetActiveExhaustHeatLoss", out method) && method is Func<long, float, float>)
                {
                    var fn = (Func<long, float, float>)method;
                    return fn(exhaust.EntityId, deltaTime);
                }
                return 0f;
            }


            public float GetActiveHeatVentLoss(IMyHeatVent vent, float deltaTime)
            {
                object method;
                if (client.TryGetValue("GetActiveHeatVentLoss", out method) && method is Func<long, float, float>)
                {
                    var fn = (Func<long, float, float>)method;
                    return fn(vent.EntityId, deltaTime);
                }
                return 0f;
            }


            public float GetActiveThrusterHeatLoss(IMyThrust thruster, float thrustRatio, float deltaTime)
            {
                object method;
                if (client.TryGetValue("GetActiveThrusterHeatLoss", out method) && method is Func<long, float, float, float>)
                {
                    var fn = (Func<long, float, float, float>)method;
                    return fn(thruster?.EntityId ?? 0, thrustRatio, deltaTime);
                }
                return 0f;
            }


            public float GetActiveVentHealLoss(IMyAirVent vent, float deltaTime)
            {
                object method;
                if (client.TryGetValue("GetActiveVentHealLoss", out method) && method is Func<long, float, float>)
                {
                    var fn = (Func<long, float, float>)method;
                    return fn(vent?.EntityId ?? 0, deltaTime);
                }
                return 0f;
            }


            public float GetAirDensity(IMyCubeBlock block)
            {
                object method;
                if (client.TryGetValue("GetAirDensity", out method) && method is Func<long, float>)
                {
                    var fn = (Func<long, float>)method;
                    return fn(block.EntityId);
                }
                return 0f;
            }


            public float GetAmbientHeatLoss(IMyCubeBlock block, float deltaTime)
            {
                object method;
                if (client.TryGetValue("GetAmbientHeatLoss", out method) && method is Func<long, float, float>)
                {
                    var fn = (Func<long, float, float>)method;
                    return fn(block?.EntityId ?? 0, deltaTime);
                }
                return 0f;
            }

            public HeatNetworkData? GetNetworkData(IMyCubeBlock block)
            {
                object method;
                if (client.TryGetValue("GetNetworkData", out method) && method is Func<long, object>)
                {
                    var fn = (Func<long, object>)method;
                    return MapToNetworkData(fn(block?.EntityId ?? 0));
                }
                return null;
            }

            private HeatNetworkData? MapToNetworkData(object v)
            {
                if (v is IDictionary<string, object>)
                {
                    var dict = (IDictionary<string, object>)v;
                    var result = new HeatNetworkData();
                    object hashObj;
                    if (dict.TryGetValue("hash", out hashObj) && hashObj is int)
                    {
                        result.hash = (int)hashObj;
                    }
                    object lengthObj;
                    if (dict.TryGetValue("length", out lengthObj) && lengthObj is int)
                    {
                        result.length = (int)lengthObj;
                    }
                    object avgTempObj;
                    if (dict.TryGetValue("averageTemperature", out avgTempObj) && avgTempObj is float)
                    {
                        result.averageTemperature = (float)avgTempObj;
                    }
                    return result;
                }
                return null;
            }

            public float GetBlockWindSpeed(IMyCubeBlock block)
            {
                object method;
                if (client.TryGetValue("GetBlockWindSpeed", out method) && method is Func<long, float>)
                {
                    var fn = (Func<long, float>)method;
                    return fn(block.EntityId);
                }
                return 0f;
            }


            public float GetDensity(IMyCubeBlock block)
            {
                object method;
                if (client.TryGetValue("GetDensity", out method) && method is Func<long, float>)
                {
                    var fn = (Func<long, float>)method;
                    return fn(block?.EntityId ?? 0);
                }
                return 0f;
            }


            public float GetExchangeWithNeighbor(IMyCubeBlock block, IMyCubeBlock neighbor, float deltaTime)
            {
                object method;
                if (client.TryGetValue("GetExchangeWithNeighbor", out method) && method is Func<long, long, float, float>)
                {
                    var fn = (Func<long, long, float, float>)method;
                    return fn(block?.EntityId ?? 0, neighbor?.EntityId ?? 0, deltaTime);
                }
                MyLog.Default.Warning($"[H2Real] No method GetExchangeWithNeighbor found {client.Count}");
                return 0f;
            }


            public float GetHeat(IMyCubeBlock block)
            {
                object method;
                if (client.TryGetValue("GetHeat", out method) && method is Func<long, float>)
                {
                    var fn = (Func<long, float>)method;
                    return fn(block?.EntityId ?? 0);
                }
                return 0f;
            }


            public float GetLargestFaceArea(IMySlimBlock block)
            {
                object method;
                if (client.TryGetValue("GetLargestFaceArea", out method) && method is Func<long, float>)
                {
                    var fn = (Func<long, float>)method;
                    return fn((block as IMyCubeBlock)?.EntityId ?? 0);
                }
                return 0f;
            }


            public float GetMass(IMyCubeBlock block)
            {
                object method;
                if (client.TryGetValue("GetMass", out method) && method is Func<long, float>)
                {
                    var fn = (Func<long, float>)method;
                    return fn(block?.EntityId ?? 0);
                }
                return 0f;
            }


            public float GetRealSurfaceArea(IMyCubeBlock battery)
            {
                object method;
                if (client.TryGetValue("GetRealSurfaceArea", out method) && method is Func<long, float>)
                {
                    var fn = (Func<long, float>)method;
                    return fn(battery?.EntityId ?? 0);
                }
                return 0f;
            }


            public Vector3 GetSunDirection(IMyCubeBlock block, MyPlanet planet)
            {
                object method;
                if (client.TryGetValue("GetSunDirection", out method) && method is Func<long, long, Vector3>)
                {
                    var fn = (Func<long, long, Vector3>)method;
                    return fn(block?.EntityId ?? 0, planet?.EntityId ?? 0);
                }
                return Vector3.Zero;
            }


            public float GetTemperatureOnPlanet(Vector3D position)
            {
                object method;
                if (client.TryGetValue("GetTemperatureOnPlanet", out method) && method is Func<Vector3D, float>)
                {
                    var fn = (Func<Vector3D, float>)method;
                    return fn(position);
                }
                return 0f;
            }


            public float GetThermalCapacity(IMyCubeBlock block)
            {
                object method;
                if (client.TryGetValue("GetThermalCapacity", out method) && method is Func<long, float>)
                {
                    var fn = (Func<long, float>)method;
                    return fn(block?.EntityId ?? 0);
                }
                return 0f;
            }


            public bool IsBlockInPressurizedRoom(IMyCubeBlock block)
            {
                object method;
                if (client.TryGetValue("IsBlockInPressurizedRoom", out method) && method is Func<long, bool>)
                {
                    var fn = (Func<long, bool>)method;
                    return fn(block?.EntityId ?? 0);
                }
                return false;
            }


            public void SetHeat(IMyCubeBlock block, float heat, bool silent = true)
            {
                object method;
                if (client.TryGetValue("SetHeat", out method) && method is Action<long, float, bool>)
                {
                    var fn = (Action<long, float, bool>)method;
                    fn(block?.EntityId ?? 0, heat, silent);
                    return;
                }
            }

            public float GetExchangeWithNetwork(IMyCubeBlock block, IMyCubeBlock networkBlock, float deltaTime)
            {
                object method;
                if (client.TryGetValue("GetExchangeWithNetwork", out method) && method is Func<long, long, float, float>)
                {
                    var fn = (Func<long, long, float, float>)method;
                    return fn(block?.EntityId ?? 0, networkBlock?.EntityId ?? 0, deltaTime);
                }
                return 0f;
            }

            public float GetExchangeUniversal(IMyCubeBlock block, IMyCubeBlock neighborBlock, float deltaTime)
            {
                object method;
                if (client.TryGetValue("GetExchangeUniversal", out method) && method is Func<long, long, float, float>)
                {
                    var fn = (Func<long, long, float, float>)method;
                    return fn(block?.EntityId ?? 0, neighborBlock?.EntityId ?? 0, deltaTime);
                }
                return 0f;
            }

            public HmsConfig GetHmsConfig()
            {
                object method;
                if (client.TryGetValue("GetHmsConfig", out method) && method is Func<object>)
                {
                    var fn = (Func<object>)method;
                    var result = fn();
                    var config = new HmsConfig();
                    if (result is IDictionary<string, object>)
                    {
                        var dict = result as IDictionary<string, object>;
                        object value;
                        if (dict.TryGetValue("HEAT_SYSTEM_VERSION", out value) && value is string) config.HEAT_SYSTEM_VERSION = (string)value;
                        if (dict.TryGetValue("HEAT_SYSTEM_AUTO_UPDATE", out value) && value is bool) config.HEAT_SYSTEM_AUTO_UPDATE = (bool)value;
                        if (dict.TryGetValue("HEAT_COOLDOWN_COEFF", out value) && value is float) config.HEAT_COOLDOWN_COEFF = (float)value;
                        if (dict.TryGetValue("HEAT_RADIATION_COEFF", out value) && value is float) config.HEAT_RADIATION_COEFF = (float)value;
                        if (dict.TryGetValue("DISCHARGE_HEAT_FRACTION", out value) && value is float) config.DISCHARGE_HEAT_FRACTION = (float)value;
                        if (dict.TryGetValue("DISCHARGE_HEAT_CONFIGURABLE", out value) && value is bool) config.DISCHARGE_HEAT_CONFIGURABLE = (bool)value;
                        if (dict.TryGetValue("THERMAL_CONDUCTIVITY", out value) && value is float) config.THERMAL_CONDUCTIVITY = (float)value;
                        if (dict.TryGetValue("VENT_COOLING_RATE", out value) && value is float) config.VENT_COOLING_RATE = (float)value;
                        if (dict.TryGetValue("THRUSTER_COOLING_RATE", out value) && value is float) config.THRUSTER_COOLING_RATE = (float)value;
                        if (dict.TryGetValue("CRITICAL_TEMP", out value) && value is float) config.CRITICAL_TEMP = (float)value;
                        if (dict.TryGetValue("WIND_COOLING_MULT", out value) && value is float) config.WIND_COOLING_MULT = (float)value;
                        if (dict.TryGetValue("LIMIT_TO_PLAYER_GRIDS", out value) && value is bool) config.LIMIT_TO_PLAYER_GRIDS = (bool)value;
                        if (dict.TryGetValue("HEATPIPE_CONDUCTIVITY", out value) && value is float) config.HEATPIPE_CONDUCTIVITY = (float)value;
                        if (dict.TryGetValue("EXHAUST_HEAT_REJECTION_RATE", out value) && value is float) config.EXHAUST_HEAT_REJECTION_RATE = (float)value;
                        if (dict.TryGetValue("HEAT_GLOW_INDICATION", out value) && value is bool) config.HEAT_GLOW_INDICATION = (bool)value;
                        
                    }
                    return config;
                }
                return null;
            }
        }

        internal class HmsEffects : IHeatEffects
        {
            private IDictionary<string, object> client;

            public HmsEffects(object client)
            {
                this.client = client as IDictionary<string, object>;
            }

            public void InstantiateSmoke(IMyCubeBlock battery)
            {
                object method;
                if (client.TryGetValue("InstantiateSmoke", out method) && method is Action<long>)
                {
                    var fn = (Action<long>)method;
                    fn(battery?.EntityId ?? 0);
                    return;
                }
            }

            public void RemoveSmoke(IMyCubeBlock battery)
            {
                object method;
                if (client.TryGetValue("RemoveSmoke", out method) && method is Action<long>)
                {
                    var fn = (Action<long>)method;
                    fn(battery?.EntityId ?? 0);
                    return;
                }
            }

            public void UpdateBlockHeatLight(IMyCubeBlock block, float heat)
            {
                object method;
                if (client.TryGetValue("UpdateBlockHeatLight", out method) && method is Action<long, float>)
                {
                    var fn = (Action<long, float>)method;
                    fn(block?.EntityId ?? 0, heat);
                    return;
                }
            }

            public void UpdateLightsPosition()
            {
                object method;
                if (client.TryGetValue("UpdateLightsPosition", out method) && method is Action)
                {
                    var fn = (Action)method;
                    fn();
                    return;
                }
            }
        }
    }
}