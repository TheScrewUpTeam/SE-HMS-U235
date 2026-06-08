using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI;
using TSUT.HeatManagement;
using VRage;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;
using IngameInventoryItem = VRage.Game.ModAPI.Ingame.MyInventoryItem;
using IngameItemType = VRage.Game.ModAPI.Ingame.MyItemType;

namespace TSUT.U235
{
    public enum ReactorState
    {
        Idle,
        HeatingUp,
        Running,
        CoolingDown
    }

    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_Reactor), false)]
    public class ReactorGameLogic : MyGameLogicComponent
    {
        IMyReactor _reactor;
        HmsApi _api;
        ReactorAdapter _adapter;
        IMyInventory _inventory;

        private bool _autoRestartOn = false;
        private float _batchFuelAmouont = 1f;
        private float _batchBurningTime;
        private float _coreTemp;
        private string _lastLaunchFailReason = "";
        private float _blockTermalCapacity;
        private float _coreTermalCapacity;
        private float _burningCycleCountDown;
        private float _lastTempChange;
        private float _pullCooldown = 0f;
        private MyResourceSourceComponent _source;
        private ReactorState _state;

        const float FUEL_REFERENCE = 1f;
        const float VOLUME_REFERENCE = 0.125f;
        const float LONGATION_REFERENCE = 600;

        private float FuelCountdown
        {
            get { return _burningCycleCountDown; }
            set
            {
                _burningCycleCountDown = value;
                Storage.SetFloat(_reactor, Config.FuelCooldown, value);
            }
        }

        private ReactorState State
        {
            get { return _state; }
            set
            {
                _state = value;
                Storage.SetFloat(_reactor, Config.ReactorState, (float)value);
                MyLog.Default.WriteLine($"[HMS.U235] [{_reactor?.DisplayNameText}] State → {value}");
            }
        }

        protected float CoreTemp
        {
            get { return _coreTemp; }
            set
            {
                _coreTemp = value;
                Storage.SetFloat(_reactor, Config.CoreTempKey, value);
            }
        }

        public bool IsReadyToLaunch => State == ReactorState.Idle && HasFuelInInventory() && IsTemperatureLaunchReady();

        public bool IsReadyToStop => State == ReactorState.Running || State == ReactorState.HeatingUp;

        public bool AutoRestartOn
        {
            get { return _autoRestartOn; }
            set
            {
                _autoRestartOn = value;
                Storage.SetBool(_reactor, Config.BlockStateKey, value);
            }
        }

        public void ManualLaunch() => TryStartSequence();

        public void ManualStop()
        {
            _autoRestartOn = false;
            Storage.SetBool(_reactor, Config.BlockStateKey, false);
            if (State == ReactorState.HeatingUp)
                _inventory.AddItems((MyFixedPoint)_batchFuelAmouont, new MyObjectBuilder_Ingot { SubtypeName = "Uranium" });
            State = ReactorState.CoolingDown;
        }

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            _reactor = Entity as IMyReactor;
            if (_reactor == null) return;

            _inventory = _reactor.GetInventory(0);
            _state = (ReactorState)Math.Round(Storage.GetFloat(_reactor, Config.ReactorState));
            _burningCycleCountDown = Storage.GetFloat(_reactor, Config.FuelCooldown);
            _coreTemp = Storage.GetFloat(_reactor, Config.CoreTempKey, 0f);
            _autoRestartOn = Storage.GetBool(_reactor, Config.BlockStateKey, false);

            _reactor.Enabled = false;
            _reactor.EnabledChanged += OnEnabledChanged;
            _reactor.AppendingCustomInfo += OnAppendCustomInfo;

            ComputeFuelPlan(_reactor, out _batchFuelAmouont, out _batchBurningTime);
            _coreTermalCapacity = GetCoreThermalCapacity();

            InitiateSource();

            NeedsUpdate |= MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }

        public override void UpdateOnceBeforeFrame()
        {
            ReactorTerminalControls.Register();
        }

        public override void Close()
        {
            if (_reactor != null)
            {
                _reactor.EnabledChanged -= OnEnabledChanged;
                _reactor.AppendingCustomInfo -= OnAppendCustomInfo;
            }
            _reactor = null;
            _api = null;
            _adapter = null;
        }

        public void SetApi(HmsApi api, ReactorAdapter adapter)
        {
            _api = api;
            _adapter = adapter;
            _blockTermalCapacity = api.Utils.GetThermalCapacity(_reactor);
            if (_coreTemp == 0f)
                _coreTemp = api.Utils.GetHeat(_reactor);
        }

        private void InitiateSource()
        {
            _source = _reactor.Components.Get<MyResourceSourceComponent>();
            _source.Enabled = true;
            // Mod manages fuel externally — vanilla capacity tracking would clamp MaxOutput to 0 after inventory is emptied
            _source.SetRemainingCapacityByType(MyResourceDistributorComponent.ElectricityId, float.PositiveInfinity);
            _source.SetMaxOutputByType(MyResourceDistributorComponent.ElectricityId, 0f);
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
            return (size.X * gridSize) * (size.Y * gridSize) * (size.Z * gridSize);
        }

        private void OnAppendCustomInfo(IMyTerminalBlock block, StringBuilder builder)
        {
            if (_api == null) return;

            float currentHeat = _api.Utils.GetHeat(_reactor);
            float internalUse = _lastTempChange;
            float neighborExchange;
            float networkExchange;

            var neighborInfo = new StringBuilder();
            _adapter.AppendNeighborInfo(neighborInfo, out neighborExchange, out networkExchange);

            float ambientExchange = _api.Utils.GetAmbientHeatLoss(block, 1);
            float heatChange = internalUse - ambientExchange + neighborExchange + networkExchange;

            builder.AppendLine("--- HMS.U235 ---");
            builder.AppendLine($"Mode: {(_autoRestartOn ? "AUTO" : "MANUAL")}");
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
                case ReactorState.CoolingDown:
                    AddCoolingDownInfo(builder);
                    break;
            }
            builder.AppendLine($"Core Temperature: {CoreTemp:F2} °C");
            builder.AppendLine("");
            builder.AppendLine($"Temperature: {currentHeat:F2} °C");
            string heatStatus = heatChange > 0 ? "Heating" : heatChange < -0.01 ? "Cooling" : "Stable";
            builder.AppendLine($"Block's Thermal Status: {heatStatus}");
            builder.AppendLine($"Net Heat Change: {heatChange:+0.00;-0.00;0.00} °C/s");
            builder.AppendLine($"Thermal capacity: {_blockTermalCapacity / 1000000:F1} MJ/°C");
            builder.AppendLine($"Core thermal capacity: {_coreTermalCapacity / 1000000:F1} MJ/°C");
            builder.AppendLine("");
            builder.AppendLine("Heat sources:");
            builder.AppendLine($"  Internal use: {internalUse:F2} °C/s");
            builder.AppendLine($"  Air Exchange: {-ambientExchange:+0.00;-0.00;0.00} °C/s");
            builder.Append(neighborInfo);
        }

        private void AddCoolingDownInfo(StringBuilder builder)
        {
            float needToDissipate = CoreTemp - Config.Instance.REACTOR_MAINTENANCE_TEMPERATURE;
            float cooldownPace = getInternalExchangeEnergy(1f) / _coreTermalCapacity;
            builder.AppendLine($"Target Core Temperature: {Config.Instance.REACTOR_MAINTENANCE_TEMPERATURE} °C");
            if (cooldownPace > 0)
            {
                float timeToCooled = needToDissipate / cooldownPace;
                TimeSpan timeSpan = TimeSpan.FromSeconds(timeToCooled);
                builder.AppendLine($"Estimated Time To Cool Down: {timeSpan:mm\\:ss}");
            }
            else
            {
                builder.AppendLine($"WARNING! Reactor shell is too hot, core will never cool down");
            }
        }

        private void AddRunningInfo(StringBuilder builder)
        {
            float curOut = GetCurrentEnergyOutput(1f);
            builder.AppendLine($"Current Power Generation: {FormatEnergyPerSecond(curOut)}");
            builder.AppendLine($"Core Heat Change: {GetCurrentHeatChange(1f) / _coreTermalCapacity:F2} °C/s");
            TimeSpan timeSpan = TimeSpan.FromSeconds(FuelCountdown);
            builder.AppendLine($"Fuel TTL: {timeSpan:hh\\:mm\\:ss}");
        }

        private void AddHeatingUpInfo(StringBuilder builder)
        {
            float Pnow = GetCurrentEnergyOutput(1f);
            float C = _coreTermalCapacity;
            float T = CoreTemp;
            float Ttarget = Config.Instance.REACTOR_WORKING_TEMPERATURE;

            float dT = Math.Max(Ttarget - T, 0f);
            float heatNeeded = dT * C;

            float avgPower = (Pnow + GetOptimalPowerOutput(1f)) / 2;
            float secondsToLaunch = heatNeeded / avgPower;

            builder.AppendLine($"Current Power Generation: {FormatEnergyPerSecond(Pnow)}");
            if (!float.IsNaN(secondsToLaunch) && !float.IsInfinity(secondsToLaunch))
            {
                TimeSpan timeSpan = TimeSpan.FromSeconds(secondsToLaunch);
                builder.AppendLine($"Estimated Time to Heat Up: {timeSpan:mm\\:ss}");
            }
        }

        private void AddIdleInfo(StringBuilder builder)
        {
            if (_lastLaunchFailReason != "")
                builder.AppendLine($"WARNING: {_lastLaunchFailReason}");
        }

        private void OnEnabledChanged(IMyTerminalBlock block)
        {
            if (_reactor.Enabled)
                _reactor.Enabled = false;
        }

        public float GetHeatChange(float deltaTime)
        {
            if (_api == null) return 0f;
            EstimateErrors();
            var @internal = GetTempChange(deltaTime);
            var ambientExchange = _api.Utils.GetAmbientHeatLoss(_reactor, deltaTime);
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
                        if (HasFuelInInventory())
                        {
                            _pullCooldown = 0f;
                            TryStartSequence();
                        }
                        else
                        {
                            _pullCooldown += deltaTime;
                            if (_pullCooldown >= 2f)
                            {
                                _pullCooldown = 0f;
                                TryStartSequence();
                            }
                        }
                    }
                    change += idleInternalExchange / _blockTermalCapacity;
                    if (process)
                        CoreTemp -= idleInternalExchange / _coreTermalCapacity;
                    break;
                case ReactorState.HeatingUp:
                    change += HeatUpCycle(deltaTime, process);
                    break;
                case ReactorState.Running:
                    change += RunningCycle(deltaTime, process);
                    break;
                case ReactorState.CoolingDown:
                    change += CoolingDownCycle(deltaTime, process);
                    break;
            }
            _lastTempChange = change;
            return change;
        }

        public void ReactOnNewHeat(float heat)
        {
            _api?.Effects.UpdateBlockHeatLight(_reactor, heat);
            _reactor?.SetDetailedInfoDirty();
            _reactor?.RefreshCustomInfo();
        }

        private float CoolingDownCycle(float deltaTime, bool process)
        {
            var needToTransfer = (CoreTemp - Config.Instance.REACTOR_MAINTENANCE_TEMPERATURE) * _coreTermalCapacity;
            var canBeTransferred = getInternalExchangeEnergy(deltaTime);
            var realTransfer = Math.Max(Math.Min(needToTransfer, canBeTransferred), 0);
            if (process)
            {
                SetOutputPower(0);
                CoreTemp -= realTransfer / _coreTermalCapacity;
                if (CoreTemp <= Config.Instance.REACTOR_MAINTENANCE_TEMPERATURE)
                    State = ReactorState.Idle;
            }
            return realTransfer / _blockTermalCapacity;
        }

        private float RunningCycle(float deltaTime, bool process)
        {
            var currentPower = GetCurrentEnergyOutput(deltaTime);
            var internalUse = GetCurrentHeatChange(deltaTime);
            if (process)
                CoreTemp += internalUse / _coreTermalCapacity;
            var needToTransfer = (CoreTemp - Config.Instance.REACTOR_WORKING_TEMPERATURE) * _coreTermalCapacity;
            var canBeTransferred = getInternalExchangeEnergy(deltaTime);
            var realTransfer = Math.Max(Math.Min(needToTransfer, canBeTransferred), 0);
            if (process)
            {
                SetOutputPower(GetCurrentEnergyOutput(1) / 1000000);
                CoreTemp -= realTransfer / _coreTermalCapacity;
                FuelCountdown -= deltaTime;
                if (FuelCountdown <= 0)
                {
                    SetOutputPower(0);
                    FuelCountdown = 0;
                    State = ReactorState.CoolingDown;
                }
            }
            return realTransfer / _blockTermalCapacity;
        }

        private void SetOutputPower(float outputMW)
        {
            if (_source == null) return;
            _source.SetMaxOutputByType(MyResourceDistributorComponent.ElectricityId, outputMW);
            var distributor = _reactor.CubeGrid.ResourceDistributor as MyResourceDistributorComponent;
            distributor?.MarkForUpdate();
            _reactor.SetDetailedInfoDirty();
            _reactor.RefreshCustomInfo();
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
            float currentPower = GetCurrentEnergyOutput(deltaTime) + GetCurrentHeatChange(deltaTime);
            if (process)
            {
                CoreTemp += currentPower / _coreTermalCapacity;
                if (CoreTemp >= Config.Instance.REACTOR_WORKING_TEMPERATURE)
                {
                    State = ReactorState.Running;
                    FuelCountdown = _batchBurningTime;
                }
            }
            return change != 0 ? change : 0.00001f;
        }

        private float getInternalExchangeEnergy(float deltaTime)
        {
            var extTemp = _api.Utils.GetHeat(_reactor);
            float conductivity = _api.Utils.GetHmsConfig().HEATPIPE_CONDUCTIVITY * Config.Instance.CORE_TO_BLOCK_CONDUCTANCE_MODIFIER;
            float tempDiff = CoreTemp - extTemp;
            float energyTransferred = tempDiff * conductivity * deltaTime;
            energyTransferred = ApplyExchangeLimit(energyTransferred, _coreTermalCapacity, _blockTermalCapacity, tempDiff);
            return energyTransferred;
        }

        public float ApplyExchangeLimit(float energyDelta, float capA, float capB, float tempDiff)
        {
            if (energyDelta > 0)
                return Math.Min(energyDelta, tempDiff * capB / 2);
            else
                return Math.Max(energyDelta, tempDiff * capA / 2);
        }

        private float GetCoreThermalCapacity()
        {
            float fuelWeight = _batchFuelAmouont * 1000;
            return fuelWeight * Config.Instance.CORE_THERMAL_CAPACITY;
        }

        private float GetCurrentEnergyOutput(float deltaTime) => GetEnergyOutputAtTemp(CoreTemp, deltaTime);

        private float GetOptimalPowerOutput(float deltaTime)
        {
            float totalCleanEnergy = GetCleanEnergy();
            float energyPerSecond = totalCleanEnergy / _batchBurningTime;
            return energyPerSecond * deltaTime;
        }

        private float GetCurrentHeatChange(float deltaTime)
        {
            float totalBatchEnergy = _batchFuelAmouont * Config.Instance.URANIUM_ENERGY;
            float extractedEnergy = totalBatchEnergy * Config.Instance.BURN_ENFFICIENCY;
            float totalHeat = extractedEnergy * Config.Instance.HEAT_WASTE;
            float heatPerSec = totalHeat / _batchBurningTime;
            return heatPerSec * deltaTime;
        }

        private float GetCleanEnergy()
        {
            float totalBatchEnergy = _batchFuelAmouont * Config.Instance.URANIUM_ENERGY;
            float extractedEnergy = totalBatchEnergy * Config.Instance.BURN_ENFFICIENCY;
            float internalWaste = extractedEnergy * Config.Instance.INTERNAL_WASTE;
            float heatWaste = extractedEnergy * Config.Instance.HEAT_WASTE;
            return extractedEnergy - internalWaste - heatWaste;
        }

        private float GetEnergyOutputAtTemp(float temp, float deltaTime)
        {
            float optimalPower = GetOptimalPowerOutput(deltaTime);
            float temperatureModifier = Math.Min(Math.Max(temp / Config.Instance.REACTOR_WORKING_TEMPERATURE, 0f), 1f);
            float basePower = temperatureModifier * optimalPower;
            float ignitionAssist = (1f - temperatureModifier) * 0.1f * optimalPower;
            return basePower + ignitionAssist;
        }

        private void EstimateErrors()
        {
            if (State != ReactorState.Idle) return;
            if (!HasFuelInInventory())
                _lastLaunchFailReason = $"Reactor has not enough fuel, required {_batchFuelAmouont}kg of Uranium to launch";
            else if (!IsTemperatureLaunchReady())
                _lastLaunchFailReason = $"Reactor core is below {Config.Instance.REACTOR_MINIMAL_LAUNCH_TEMPERATURE}°C, coolant frozen";
            else
                _lastLaunchFailReason = "";
        }

        private bool TryStartSequence()
        {
            if (!IsTemperatureLaunchReady()) return false;
            if (!HasFuelInInventory() && (!_autoRestartOn || !TryPullFuel())) return false;

            MyFixedPoint amount = (MyFixedPoint)_batchFuelAmouont;
            var uraniumId = new MyDefinitionId(typeof(MyObjectBuilder_Ingot), "Uranium");
            var fuel = _inventory.FindItem(uraniumId);
            if (fuel == null) return false;
            _inventory.RemoveItemAmount(fuel, amount);
            _source.SetRemainingCapacityByType(MyResourceDistributorComponent.ElectricityId, float.PositiveInfinity);
            State = ReactorState.HeatingUp;
            SetOutputPower(0f);
            _lastLaunchFailReason = "";
            return true;
        }

        private bool IsTemperatureLaunchReady() => CoreTemp >= Config.Instance.REACTOR_MINIMAL_LAUNCH_TEMPERATURE;

        private bool HasFuelInInventory()
        {
            MyFixedPoint amount = (MyFixedPoint)_batchFuelAmouont;
            var uraniumId = new MyDefinitionId(typeof(MyObjectBuilder_Ingot), "Uranium");
            return _inventory.GetItemAmount(uraniumId) >= amount;
        }

        private bool TryPullFuel()
        {
            MyFixedPoint amount = (MyFixedPoint)_batchFuelAmouont;
            var uraniumId = new MyDefinitionId(typeof(MyObjectBuilder_Ingot), "Uranium");
            var allContainers = _reactor.CubeGrid.GetFatBlocks<IMyCargoContainer>();
            foreach (var container in allContainers)
            {
                if (!MyVisualScriptLogicProvider.IsConveyorConnected(_reactor.Name, container.Name))
                    continue;
                var containerInv = container.GetInventory();
                var fuel = containerInv.FindItem(uraniumId);
                if (fuel == null || fuel.Amount < amount) continue;
                var items = new List<IngameInventoryItem>();
                containerInv.GetItems(items);
                var uraniumType = new IngameItemType("MyObjectBuilder_Ingot", "Uranium");
                int itemIndex = -1;
                for (int i = 0; i < items.Count; i++)
                {
                    if (items[i].Type == uraniumType)
                    {
                        itemIndex = i;
                        break;
                    }
                }
                if (itemIndex < 0) continue;
                bool transferred = _inventory.TransferItemFrom(containerInv, itemIndex, null, null, amount, checkConnection: false);
                if (transferred)
                {
                    MyLog.Default.WriteLine($"[HMS.U235] [{_reactor?.DisplayNameText}] Pulled {amount}kg fuel from '{container.DisplayNameText}', auto-restarting");
                    return true;
                }
            }
            return false;
        }

        private string FormatEnergyPerSecond(double value)
        {
            if (value >= 1000000) return $"{value / 1000000:F2} MJ/s";
            if (value >= 1000) return $"{value / 1000:F2} kJ/s";
            return $"{value:F0} J/s";
        }
    }
}
