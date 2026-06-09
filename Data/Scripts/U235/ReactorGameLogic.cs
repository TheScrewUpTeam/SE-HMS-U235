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
using VRage.Game.Entity;
using VRage.Utils;
using VRageMath;
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
        private IMyInventory Inventory => _inventory ?? (_inventory = _reactor?.GetInventory(0));

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
        private float _controlRodThreshold;
        private bool _meltdownTriggered = false;
        private float _blinkTimer = 0f;
        private bool _smokeActive = false;
        private bool _blinkFrameUpdateRegistered = false;
        private MyResourceSourceComponent _source;
        private ReactorState _state;

        const float FUEL_REFERENCE = 0.08f;
        const float VOLUME_REFERENCE = 0.125f;
        const float LONGATION_REFERENCE = 2240;
        const float CONDUCTANCE_SIZE_EXPONENT = 0.22f;

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
                UpdateEmissiveState();
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

        public float ControlRodThreshold
        {
            get { return _controlRodThreshold; }
            set
            {
                _controlRodThreshold = value;
                Storage.SetFloat(_reactor, Config.ControlRodThresholdKey, value);
            }
        }

        public void ManualLaunch() => TryStartSequence();

        public void ManualStop()
        {
            _autoRestartOn = false;
            Storage.SetBool(_reactor, Config.BlockStateKey, false);
            if (State == ReactorState.HeatingUp)
                Inventory.AddItems((MyFixedPoint)_batchFuelAmouont, new MyObjectBuilder_Ingot { SubtypeName = "Uranium" });
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
            _controlRodThreshold = Storage.GetFloat(_reactor, Config.ControlRodThresholdKey, Config.Instance.CONTROL_ROD_THRESHOLD_DEFAULT);

            _reactor.Enabled = false;
            _reactor.EnabledChanged += OnEnabledChanged;
            _reactor.AppendingCustomInfo += OnAppendCustomInfo;
            _reactor.CubeGrid.OnBlockIntegrityChanged += OnBlockIntegrityChanged;
            _reactor.CubeGrid.OnGridBlockDamaged += OnGridBlockDamaged;

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
                _reactor.CubeGrid.OnBlockIntegrityChanged -= OnBlockIntegrityChanged;
                _reactor.CubeGrid.OnGridBlockDamaged -= OnGridBlockDamaged;
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
            _coreTermalCapacity = GetCoreThermalCapacity();
            float blockHeat = api.Utils.GetHeat(_reactor);
            if (_coreTemp == 0f)
                _coreTemp = blockHeat;
            else if (blockHeat == 0f)
                api.Utils.SetHeat(_reactor, _coreTemp, silent: true);
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
            float meltdownTemp = Config.Instance.REACTOR_MELTDOWN_TEMPERATURE;
            if (CoreTemp >= meltdownTemp - 100)
                builder.AppendLine("!!! CRITICAL: MELTDOWN IMMINENT !!!");
            else if (CoreTemp >= meltdownTemp - 200)
                builder.AppendLine("WARNING: Core temperature approaching critical!");
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
            float reactionRate = (1f - GetControlRodFraction()) * (CoreTemp / Config.Instance.REACTOR_WORKING_TEMPERATURE) * 100f;
            builder.AppendLine($"Reaction Rate: {reactionRate:F0}%");
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
            bool shouldBlink = State == ReactorState.Running && CoreTemp > Config.Instance.REACTOR_WORKING_TEMPERATURE && !_meltdownTriggered;
            SetBlinkFrameUpdate(shouldBlink);
            if (!shouldBlink)
                UpdateEmissiveState();
            UpdateSmokeEffect();
            _reactor?.SetDetailedInfoDirty();
            _reactor?.RefreshCustomInfo();
        }

        public override void UpdateBeforeSimulation()
        {
            _blinkTimer += 1f / 60f;
            UpdateEmissiveState();
        }

        private void SetBlinkFrameUpdate(bool active)
        {
            if (active == _blinkFrameUpdateRegistered) return;
            _blinkFrameUpdateRegistered = active;
            if (active)
                NeedsUpdate |= MyEntityUpdateEnum.EACH_FRAME;
            else
                NeedsUpdate &= ~MyEntityUpdateEnum.EACH_FRAME;
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
            if (process && CoreTemp >= Config.Instance.REACTOR_MELTDOWN_TEMPERATURE)
            {
                TriggerMeltdown();
                return 0f;
            }
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
            if (process && CoreTemp >= Config.Instance.REACTOR_MELTDOWN_TEMPERATURE)
            {
                TriggerMeltdown();
                return 0f;
            }
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

        private float GetSizeHeatMultiplier()
        {
            return (float)Math.Pow(FUEL_REFERENCE / _batchFuelAmouont, Config.Instance.HEAT_SCALE_EXPONENT);
        }

        private float GetControlRodFraction()
        {
            if (_controlRodThreshold >= Config.Instance.REACTOR_MELTDOWN_TEMPERATURE)
                return 0f;
            float range = Config.Instance.REACTOR_MELTDOWN_TEMPERATURE - _controlRodThreshold;
            float fraction = (CoreTemp - _controlRodThreshold) / range;
            return Math.Max(0f, Math.Min(Config.Instance.MAX_ROD_FRACTION, fraction));
        }

        private float getInternalExchangeEnergy(float deltaTime)
        {
            var extTemp = _api.Utils.GetHeat(_reactor);
            float sizeScale = (float)Math.Pow(_batchFuelAmouont / FUEL_REFERENCE, CONDUCTANCE_SIZE_EXPONENT);
            float conductivity = _api.Utils.GetHmsConfig().HEATPIPE_CONDUCTIVITY * Config.Instance.CORE_TO_BLOCK_CONDUCTANCE_MODIFIER * sizeScale;
            float tempDiff = CoreTemp - extTemp;
            float energyTransferred = ApplyExchangeLimit(tempDiff * conductivity * deltaTime, _coreTermalCapacity, _coreTermalCapacity, tempDiff);
            return energyTransferred;
        }

        public float ApplyExchangeLimit(float energyDelta, float capA, float capB, float tempDiff)
        {
            if (energyDelta >= 0)
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
            return energyPerSecond * (1f - GetControlRodFraction()) * deltaTime;
        }

        private float GetCurrentHeatChange(float deltaTime)
        {
            float totalBatchEnergy = _batchFuelAmouont * Config.Instance.URANIUM_ENERGY;
            float extractedEnergy = totalBatchEnergy * Config.Instance.BURN_ENFFICIENCY;
            float totalHeat = extractedEnergy * Config.Instance.HEAT_WASTE;
            float heatPerSec = totalHeat / _batchBurningTime;
            return heatPerSec * GetSizeHeatMultiplier() * (1f - GetControlRodFraction()) * deltaTime;
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
            float temperatureModifier = Math.Max(temp / Config.Instance.REACTOR_WORKING_TEMPERATURE, 0f);
            float basePower = temperatureModifier * optimalPower;
            float ignitionAssist = temperatureModifier < 1f ? (1f - temperatureModifier) * 0.1f * optimalPower : 0f;
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
            var fuel = Inventory.FindItem(uraniumId);
            if (fuel == null) return false;
            Inventory.RemoveItemAmount(fuel, amount);
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
            return Inventory.GetItemAmount(uraniumId) >= amount;
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
                bool transferred = Inventory.TransferItemFrom(containerInv, itemIndex, null, null, amount, checkConnection: false);
                if (transferred)
                    return true;
            }
            return false;
        }

        private void UpdateEmissiveState()
        {
            var block = _reactor as MyCubeBlock;
            if (block?.Render?.RenderObjectIDs == null || block.Render.RenderObjectIDs.Length == 0) return;
            uint renderObjectId = block.Render.RenderObjectIDs[0];
            if (_meltdownTriggered)
            {
                block.UpdateEmissiveParts(renderObjectId, 1.0f, Color.Red, Color.Red);
                return;
            }
            Color color;
            float emissivity;
            switch (_state)
            {
                case ReactorState.Idle:        color = Color.White;            emissivity = 0.5f; break;
                case ReactorState.HeatingUp:   color = new Color(255, 140, 0); emissivity = 0.8f; break;
                case ReactorState.Running:     color = Color.Green;            emissivity = 1.0f; break;
                case ReactorState.CoolingDown: color = Color.Cyan;             emissivity = 0.7f; break;
                default: return;
            }
            if (_blinkFrameUpdateRegistered)
            {
                float workingTemp = Config.Instance.REACTOR_WORKING_TEMPERATURE;
                float meltdownTemp = Config.Instance.REACTOR_MELTDOWN_TEMPERATURE;
                float t = Math.Min((CoreTemp - workingTemp) / (meltdownTemp - 50f - workingTemp), 1f);
                float period = 2.0f - 1.5f * t;
                emissivity = 0.5f + 0.5f * (float)Math.Sin(2 * Math.PI * _blinkTimer / period);
            }
            block.UpdateEmissiveParts(renderObjectId, emissivity, color, color);
        }

        private void OnBlockIntegrityChanged(IMySlimBlock block)
        {
            if (block.FatBlock != _reactor || _meltdownTriggered) return;
            if (!_reactor.IsFunctional && CoreTemp >= Config.Instance.MELTDOWN_GRIND_TEMP_THRESHOLD)
                TriggerMeltdown();
        }

        private void UpdateSmokeEffect()
        {
            if (_api == null) return;
            bool shouldSmoke = !_meltdownTriggered && CoreTemp >= Config.Instance.REACTOR_MELTDOWN_TEMPERATURE - 200;
            if (shouldSmoke && !_smokeActive)
            {
                _api.Effects.InstantiateSmoke(_reactor);
                _smokeActive = true;
            }
            else if (!shouldSmoke && _smokeActive)
            {
                _api.Effects.RemoveSmoke(_reactor);
                _smokeActive = false;
            }
        }

        private void OnGridBlockDamaged(IMySlimBlock block, float damage, MyHitInfo? hitInfo, long attackerId)
        {
            if (block.FatBlock != _reactor || _meltdownTriggered) return;
            if (!_reactor.IsFunctional && CoreTemp >= Config.Instance.MELTDOWN_GRIND_TEMP_THRESHOLD)
                TriggerMeltdown();
        }

        private void TriggerMeltdown()
        {
            if (_meltdownTriggered) return;
            _meltdownTriggered = true;
            if (!MyAPIGateway.Session.IsServer) return;
            MyLog.Default.WriteLine($"[HMS.U235] [{_reactor?.DisplayNameText}] MELTDOWN at {CoreTemp:F0}°C");
            UpdateEmissiveState();
            var secondaryPositions = SpawnNeighborContainers();
            MyVisualScriptLogicProvider.CreateExplosion(_reactor.GetPosition(), 10f, 1000000);
            foreach (var pos in secondaryPositions)
                MyVisualScriptLogicProvider.CreateExplosion(pos, 5f, 100000);
        }

        private List<Vector3D> SpawnNeighborContainers()
        {
            var grid = _reactor.CubeGrid;
            string containerSubtype = grid.GridSizeEnum == MyCubeSize.Large ? "LargeBlockSmallContainer" : "SmallBlockSmallContainer";
            var spawnedPositions = new List<Vector3D>();
            foreach (var pos in GetNeighborPositions(_reactor.SlimBlock.Min, _reactor.SlimBlock.Max))
            {
                var existing = grid.GetCubeBlock(pos);
                if (existing != null)
                    grid.RemoveBlock(existing, false);
                if (!grid.CanAddCube(pos))
                    continue;
                var ob = new MyObjectBuilder_CargoContainer { SubtypeName = containerSubtype, Min = pos };
                var slim = grid.AddBlock(ob, false);
                if (slim?.FatBlock != null)
                {
                    var inventory = slim.FatBlock.GetInventory();
                    if (inventory != null)
                    {
                        var ammoId = new MyDefinitionId(typeof(MyObjectBuilder_AmmoMagazine), "LargeCalibreAmmo");
                        MyPhysicalItemDefinition itemDef;
                        int count = 5;
                        if (MyDefinitionManager.Static.TryGetPhysicalItemDefinition(ammoId, out itemDef) && itemDef.Volume > 0f)
                            count = Math.Max(1, (int)((float)inventory.MaxVolume / itemDef.Volume));
                        inventory.AddItems((MyFixedPoint)count, new MyObjectBuilder_AmmoMagazine { SubtypeName = "LargeCalibreAmmo" });
                    }
                    spawnedPositions.Add(grid.GridIntegerToWorld(pos));
                }
            }
            return spawnedPositions;
        }

        private IEnumerable<Vector3I> GetNeighborPositions(Vector3I min, Vector3I max)
        {
            for (int y = min.Y; y <= max.Y; y++)
                for (int z = min.Z; z <= max.Z; z++)
                {
                    yield return new Vector3I(min.X - 1, y, z);
                    yield return new Vector3I(max.X + 1, y, z);
                }
            for (int x = min.X; x <= max.X; x++)
                for (int z = min.Z; z <= max.Z; z++)
                {
                    yield return new Vector3I(x, min.Y - 1, z);
                    yield return new Vector3I(x, max.Y + 1, z);
                }
            for (int x = min.X; x <= max.X; x++)
                for (int y = min.Y; y <= max.Y; y++)
                {
                    yield return new Vector3I(x, y, min.Z - 1);
                    yield return new Vector3I(x, y, max.Z + 1);
                }
        }

        private string FormatEnergyPerSecond(double value)
        {
            if (value >= 1000000) return $"{value / 1000000:F2} MJ/s";
            if (value >= 1000) return $"{value / 1000:F2} kJ/s";
            return $"{value:F0} J/s";
        }
    }
}
