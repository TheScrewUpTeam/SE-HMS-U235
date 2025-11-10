using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using TSUT.HeatManagement;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Utils;
using static TSUT.HeatManagement.HmsApi;

namespace TSUT.U235
{
    public enum ReactorState
    {
        Idle,
        HeatingUp,
        Running,
        CoolingDown
    }

    public class ReactorHandler : AHeatBehavior
    {
        IMyReactor _reactor;
        HmsApi _api;
        IMyInventory _inventory;

        private bool _autoRestartOn = false;
        private bool _switchSubscribed = false;
        private ReactorState State;
        private float _batchFuelAmouont = 1f; // kg
        private float _batchBurningTime;
        private float _coreTemp;
        private string _lastLaunchFailReason = "";
        private float _blockTermalCapacity;
        private float _coreTermalCapacity;

        const float FUEL_REFERENCE = 1f;
        const float VOLUME_REFERENCE = 0.125f;
        const float LONGATION_REFERENCE = 600;

        public bool IsReadyToLaunch
        {
            get
            {
                return State == ReactorState.Idle && HasFuel() && IsTemperatureLaunchReady();
            }
        }

        public bool IsReadyToStop
        {
            get
            {
                return State == ReactorState.Running;
            }
        }

        protected float CoreTemp
        {
            get { return _coreTemp; }
            set
            {
                _coreTemp = value;
                Storage.SetFloat(_reactor, Config.BlockStateKey, value);
            }
        }

        public void ManualLaunch()
        {
            TryStartSequence();
        }

        public void ManualStop()
        {
            State = ReactorState.CoolingDown;
        }

        public ReactorHandler(IMyReactor block, HmsApi api) : base(block)
        {
            _api = api;
            _reactor = block;
            _inventory = block.GetInventory(0);
            _coreTemp = Storage.GetFloat(block, Config.CoreTempKey, _api.Utils.GetHeat(block));
            _autoRestartOn = Storage.GetBool(block, Config.BlockStateKey, false);
            MyAPIGateway.TerminalControls.CustomControlGetter += OnCustomControlGetter;
            block.Enabled = false;
            block.EnabledChanged += OnEnabledChanged;
            block.AppendingCustomInfo += OnAppendCustomInfo;
            _blockTermalCapacity = _api.Utils.GetThermalCapacity(block);
            _coreTermalCapacity = GetCoreThermalCapacity();
            ComputeFuelPlan(block, out _batchFuelAmouont, out _batchBurningTime);
        }

        private void ComputeFuelPlan(IMyReactor block, out float batchFuelAmouont, out float batchBurningTime)
        {
            float volumeM3 = GetBlockVolume(block);
            float ratio = volumeM3 / VOLUME_REFERENCE;
            batchFuelAmouont = (float)Math.Ceiling(FUEL_REFERENCE * (float)Math.Pow(ratio, Config.Instance.ALHPA_MODIFIER));

            batchBurningTime = (float)Math.Ceiling(LONGATION_REFERENCE * (float)Math.Pow(batchFuelAmouont / FUEL_REFERENCE, Config.Instance.BETA_MODIFIER));
        }

        private float GetBlockVolume(IMyReactor block)
        {
            MyCubeBlockDefinition definition;

            if (!MyDefinitionManager.Static.TryGetCubeBlockDefinition(block.BlockDefinition, out definition))
                return 0f;

            var size = definition.Size;
            var gridSize = block.CubeGrid.GridSize;
            float volume = (size.X * gridSize) * (size.Y * gridSize) * (size.Z * gridSize);
            return volume;
        }

        private void OnAppendCustomInfo(IMyTerminalBlock block, StringBuilder builder)
        {
            float currentHeat = _api.Utils.GetHeat(_reactor);
            float internalUse = GetTempChange(1, false) / _blockTermalCapacity; // °C/s
            float neighborExchange;
            float networkExchange;

            var neighborInfo = new StringBuilder();

            AddNeighborAndNetworksInfo(
                block,
                _api,
                neighborInfo,
                out neighborExchange,
                out networkExchange
            );

            float ambientExchange = _api.Utils.GetAmbientHeatLoss(block, 1);
            float heatChange = internalUse - ambientExchange + neighborExchange + networkExchange;

            builder.AppendLine("--- HMS.U235 ---");
            builder.AppendLine($"Reactor state: {State}");
            switch (State)
            {
                case ReactorState.Idle:
                    AddIdleInfo(builder);
                    break;
                case ReactorState.HeatingUp:
                    AddHeatingUpInfo(builder);
                    break;
                case ReactorState.Running:
                    AddRunningInfo(builder);
                    break;
            }
            builder.AppendLine("");
            builder.AppendLine($"Temperature: {currentHeat:F2} °C");
            builder.AppendLine($"Core temperature: {CoreTemp:F2} °C");
            string heatStatus = heatChange > 0 ? "Heating" : heatChange < -0.01 ? "Cooling" : "Stable";
            builder.AppendLine($"Thermal Status: {heatStatus}");
            builder.AppendLine($"Net Heat Change: {heatChange:+0.00;-0.00;0.00} °C/s");
            builder.AppendLine($"Thermal capacity: {_blockTermalCapacity / 1000000:F1} MJ/°C");
            builder.AppendLine($"Core thermal capacity: {_coreTermalCapacity / 1000000:F1} MJ/°C");
            builder.AppendLine("");
            builder.AppendLine("Heat sources:");
            builder.AppendLine($"  Internal use: {internalUse:F2} °C/s");
            builder.AppendLine($"  Air Exchange: {-ambientExchange:+0.00;-0.00;0.00} °C/s");
            builder.Append(neighborInfo);
        }

        private void AddRunningInfo(StringBuilder builder)
        {
            float curOut = GetCurrentEnergyOutput();
            builder.AppendLine($"Current generation: {FormatEnergyPerSecond(curOut)}");
        }

        private void AddHeatingUpInfo(StringBuilder builder)
        {
            float Pnow = GetCurrentEnergyOutput();      // J/s right now
            float C = _coreTermalCapacity;              // J/°C
            float T = CoreTemp;
            float Ttarget = Config.Instance.REACTOR_WORKING_TEMPERATURE;

            // Estimate remaining heat needed
            float dT = Math.Max(Ttarget - T, 0f);
            float heatNeeded = dT * C;                  // total joules needed

            // Estimate using average power (current + final)/2 to simulate rising curve
            float avgPower = (Pnow + getOptimalPowerOutput()) / 2;
            float secondsToLaunch = heatNeeded / avgPower;

            builder.AppendLine($"Current generation: {FormatEnergyPerSecond(Pnow)}");
            if (!float.IsNaN(secondsToLaunch) && !float.IsInfinity(secondsToLaunch))
            {
                TimeSpan timeSpan = TimeSpan.FromSeconds(secondsToLaunch);
                string formattedTime = timeSpan.ToString(@"mm\:ss");
                builder.AppendLine($"Estimated time to heat up: {formattedTime}");
            }
        }

        private void AddIdleInfo(StringBuilder builder)
        {
            if (State == ReactorState.Idle && _lastLaunchFailReason != "")
            {
                builder.AppendLine($"WARNING: {_lastLaunchFailReason}");
            }
        }

        private void OnEnabledChanged(IMyTerminalBlock block)
        {
            _autoRestartOn = _reactor.Enabled;
            Storage.SetBool(_reactor, Config.BlockStateKey, _reactor.Enabled);
            _reactor.Enabled = false;
        }

        private void OnCustomControlGetter(IMyTerminalBlock topBlock, List<IMyTerminalControl> controls)
        {
            if (topBlock != _reactor || _switchSubscribed)
                return;

            foreach (var control in controls)
            {
                if (control.Id == "OnOff")
                {
                    var onOffControl = control as IMyTerminalControlOnOffSwitch;
                    if (onOffControl == null)
                        continue;

                    onOffControl.Getter += (block) =>
                    {
                        if (block == _reactor)
                            return _autoRestartOn;
                        return (block as IMyFunctionalBlock).Enabled;
                    };
                    onOffControl.Setter += (block, value) =>
                    {
                        if (block != _reactor)
                            return;

                        _autoRestartOn = value;
                    };
                    _switchSubscribed = true;
                }
            }
        }

        public override void Cleanup()
        {
            MyAPIGateway.TerminalControls.CustomControlGetter -= OnCustomControlGetter;
            _reactor.EnabledChanged -= OnEnabledChanged;
            _reactor.AppendingCustomInfo -= OnAppendCustomInfo;
        }

        public override float GetHeatChange(float deltaTime)
        {
            EstimateErrors();
            var @internal = GetTempChange(deltaTime);
            var ambientExchange = _api.Utils.GetAmbientHeatLoss(_reactor, deltaTime);

            var blockTemp = _api.Utils.GetHeat(_reactor);

            MyLog.Default.WriteLine($"[HMS.U235] GetHeatChange[{_reactor.DisplayNameText}]: B{blockTemp:F6}, I: {@internal:F4}, A{ambientExchange:F4} C{@internal - ambientExchange:F4}");

            return @internal - ambientExchange;
        }

        public float GetTempChange(float deltaTime, bool process = true)
        {
            float change = 0;
            switch (State)
            {
                case ReactorState.Idle:
                    var idleInternalExchange = getInternalExchangeEnergy(deltaTime);
                    if (_autoRestartOn && process)
                    {
                        TryStartSequence();
                    }
                    change += idleInternalExchange / _blockTermalCapacity;
                    if (process)
                    {
                        CoreTemp -= idleInternalExchange / _coreTermalCapacity;
                    }
                    break;
                case ReactorState.HeatingUp:
                    change += HeatUpCycle(deltaTime, process);
                    break;
                case ReactorState.Running:
                    change += RunningCycle(deltaTime, process);
                    break;
                default:
                    break;
            }


            return change;
        }

        private float RunningCycle(float deltaTime, bool process)
        {
            // TODO: Estimate fraction transferrable to the heat, fuel burning time
            var currentPower = GetCurrentEnergyOutput();
            SetOutputPower(currentPower / 1000000);
            var needToTransfer = (CoreTemp - Config.Instance.REACTOR_WORKING_TEMPERATURE) * _coreTermalCapacity;
            var canBeTransferred = getInternalExchangeEnergy(deltaTime);
            var realTransfer = Math.Max(Math.Min(needToTransfer, canBeTransferred), 0);
            if (process)
            {
                CoreTemp -= realTransfer / _coreTermalCapacity;
            }
            return realTransfer / _blockTermalCapacity;
        }

        private void SetOutputPower(float outputMW)
        {
            // TODO: Figure out and fix
            // var distributor = _reactor.CubeGrid.ResourceDistributor as MyResourceDistributorComponent;
            // distributor?.AddSource(_reactor.EntityId, outputMW);
        }

        private float HeatUpCycle(float deltaTime, bool process)
        {
            float change = 0;
            var extTemp = _api.Utils.GetHeat(_reactor);
            if (extTemp > CoreTemp)
            {
                float energyTransferred = getInternalExchangeEnergy(deltaTime);
                float coreChange = -energyTransferred / _coreTermalCapacity;
                float blockChange = energyTransferred / _blockTermalCapacity;
                if (process)
                    CoreTemp += coreChange;
                change += blockChange;
            }
            float currentPower = GetCurrentEnergyOutput();
            if (process)
            {
                CoreTemp += currentPower / _coreTermalCapacity;
                if (CoreTemp >= Config.Instance.REACTOR_WORKING_TEMPERATURE)
                {
                    State = ReactorState.Running;
                }
            }

            return change != 0 ? change : 0.00001f;
        }

        /**
        Return result in J
        */
        private float getInternalExchangeEnergy(float deltaTime)
        {
            var extTemp = _api.Utils.GetHeat(_reactor);
            float conductivity = _api.Utils.GetHmsConfig().HEATPIPE_CONDUCTIVITY * Config.Instance.CORE_TO_BLOCK_CONDUCTANCE_MODIFIER;
            float tempDiff = CoreTemp - extTemp;
            float energyTransferred = tempDiff * conductivity * deltaTime;
            energyTransferred = ApplyExchangeLimit(energyTransferred, _coreTermalCapacity, _blockTermalCapacity, tempDiff);
            MyLog.Default.WriteLine($"[HMS.U235] InternalExchange: ET{extTemp:F2}, C:{conductivity:F2}, TD{tempDiff:F2} TR{energyTransferred:F2}");
            return energyTransferred;
        }

        public float ApplyExchangeLimit(float energyDelta, float capA, float capB, float tempDiff)
        {
            float limit;
            if (energyDelta > 0)
            {
                limit = tempDiff * capB / 2;
                return Math.Min(energyDelta, limit);
            }
            else
            {
                limit = tempDiff * capA / 2;
                return Math.Max(energyDelta, limit);
            }
        }

        /**
        Returns result in J/°C
        */
        private float GetCoreThermalCapacity()
        {
            float fuelWeight = _batchFuelAmouont * 1000; // g
            return fuelWeight * Config.Instance.CORE_THERMAL_CAPACITY;
        }

        /**
        Returns result in J/s
        */
        private float GetCurrentEnergyOutput()
        {
            return GetEnergyOutputAtTemp(CoreTemp);
        }

        private float getOptimalPowerOutput()
        {
            return _batchFuelAmouont * Config.Instance.MAX_ENERGY_OUTPUT;
        }

        private float GetEnergyOutputAtTemp(float temp)
        {
            float optimalPower = getOptimalPowerOutput();

            // Normalize temperature ratio
            float t = Math.Min(Math.Max(temp / Config.Instance.REACTOR_WORKING_TEMPERATURE, 0f), 1f);

            // Linear growth with a mild ignition assist when cold
            float basePower = t * optimalPower;

            // Small ignition boost that fades out as temperature rises
            float ignitionAssist = (1f - t) * 0.05f * optimalPower;

            return basePower + ignitionAssist; // in J/s
        }

        private void EstimateErrors()
        {
            switch (State)
            {
                case ReactorState.Idle:
                    if (!HasFuel())
                    {
                        _lastLaunchFailReason = $"Reactor has not enough fuel, required {_batchFuelAmouont}kg of Uranium to launch";
                    }
                    else if (!IsTemperatureLaunchReady())
                    {
                        _lastLaunchFailReason = $"Reactor core is below {Config.Instance.REACTOR_MINIMAL_LAUNCH_TEMPERATURE}°C, coolant frozen";
                    }
                    else
                    {
                        _lastLaunchFailReason = "";
                    }
                    break;
            }
        }

        private bool TryStartSequence()
        {
            if (!HasFuel())
            {
                return false;
            }
            if (!IsTemperatureLaunchReady())
            {
                return false;
            }
            MyFixedPoint amount = (MyFixedPoint)_batchFuelAmouont;
            var uraniumId = new MyDefinitionId(typeof(MyObjectBuilder_Ingot), "Uranium");
            var fuel = _inventory.FindItem(uraniumId);
            _inventory.RemoveItemAmount(fuel, amount);
            State = ReactorState.HeatingUp;
            _lastLaunchFailReason = "";
            return true;
        }

        private bool IsTemperatureLaunchReady()
        {
            return CoreTemp >= Config.Instance.REACTOR_MINIMAL_LAUNCH_TEMPERATURE;
        }

        private bool HasFuel()
        {
            MyFixedPoint amount = (MyFixedPoint)_batchFuelAmouont;
            var uraniumId = new MyDefinitionId(typeof(MyObjectBuilder_Ingot), "Uranium");
            var fuel = _inventory.GetItemAmount(uraniumId);
            if (fuel >= amount)
            {
                return true;
            }
            return TryPullFuel();
        }

        private bool TryPullFuel()
        {
            MyFixedPoint amount = (MyFixedPoint)_batchFuelAmouont;
            var uraniumId = new MyDefinitionId(typeof(MyObjectBuilder_Ingot), "Uranium");
            var containers = GetConnectedContainers(_reactor);
            foreach (var container in containers)
            {
                var fuel = container.GetInventory().FindItem(uraniumId);
                if (fuel == null || fuel.Amount < amount)
                    continue;
                _inventory.TransferItemFrom(container.GetInventory(), fuel, amount);
                return true;
            }
            return false;
        }

        private List<IMyCargoContainer> GetConnectedContainers(IMyCubeBlock target)
        {
            var allContainers = target.CubeGrid.GetFatBlocks<IMyCargoContainer>();
            var result = new List<IMyCargoContainer>();
            foreach (var container in allContainers)
            {
                if (MyVisualScriptLogicProvider.IsConveyorConnected(target.Name, container.Name))
                {
                    result.Add(container);
                }
            }
            return result;
        }

        public override void ReactOnNewHeat(float heat)
        {
            _api.Effects.UpdateBlockHeatLight(_reactor, heat);
            _reactor.SetDetailedInfoDirty();
            _reactor.RefreshCustomInfo();
        }

        public override void SpreadHeat(float deltaTime)
        {
            SpreadHeatStandard(deltaTime, _reactor, _api);
        }

        private string FormatEnergyPerSecond(double value)
        {
            if (value >= 1000000)
                return $"{value / 1000000:F2} MJ/s";
            else if (value >= 1000)
                return $"{value / 1000:F2} kJ/s";
            else
                return $"{value:F0} J/s";
        }
    }
}