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
    public class ReactorGameLogic : HmsApi.AHmsBlockComponent
    {
        IMyReactor _reactor;
        IMyCubeGrid _subscribedGrid;
        IMyInventory _inventory;
        private IMyInventory Inventory => _inventory ?? (_inventory = _reactor?.GetInventory(0));

        private bool _apiInitialized = false;
        private bool _autoRestartOn = false;
        private float _batchFuelAmouont = 1f;
        private float _batchBurningTime;
        private float _coreTemp;
        private string _lastLaunchFailReason = "";
        private float _blockTermalCapacity;
        private float _coreTermalCapacity;
        private float _defMaxMW;
        private float _theoreticalMaxMW;
        private float _burningCycleCountDown;
        private float _lastTempChange;
        private float _pullCooldown = 0f;
        private float _controlRodThreshold;
        private bool _meltdownTriggered = false;
        private float _blinkTimer = 0f;
        private bool _smokeActive = false;
        private bool _blinkFrameUpdateRegistered = false;
        private bool _warnedPrimary = false;
        private bool _warnedCritical = false;
        private float _lastOutputMW = 0f;
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
                _warnedPrimary = false;
                _warnedCritical = false;
                if (MyAPIGateway.Session.IsServer)
                    BroadcastState();
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
                if (!MyAPIGateway.Session.IsServer) { ReactorSession.Networking.SendToServer(new ReactorSetAutoRestart { EntityId = _reactor.EntityId, Value = value }); return; }
                _autoRestartOn = value;
                Storage.SetBool(_reactor, Config.BlockStateKey, value);
            }
        }

        public float ControlRodThreshold
        {
            get { return _controlRodThreshold; }
            set
            {
                if (!MyAPIGateway.Session.IsServer) { ReactorSession.Networking.SendToServer(new ReactorSetControlRod { EntityId = _reactor.EntityId, Value = value }); return; }
                _controlRodThreshold = value;
                Storage.SetFloat(_reactor, Config.ControlRodThresholdKey, value);
            }
        }

        public void ManualLaunch()
        {
            if (!MyAPIGateway.Session.IsServer) { ReactorSession.Networking.SendToServer(new ReactorLaunchRequest { EntityId = _reactor.EntityId }); return; }
            TryStartSequence();
        }

        public void ManualStop()
        {
            if (!MyAPIGateway.Session.IsServer) { ReactorSession.Networking.SendToServer(new ReactorStopRequest { EntityId = _reactor.EntityId }); return; }
            _autoRestartOn = false;
            Storage.SetBool(_reactor, Config.BlockStateKey, false);
            if (State == ReactorState.HeatingUp)
                Inventory.AddItems((MyFixedPoint)_batchFuelAmouont, new MyObjectBuilder_Ingot { SubtypeName = "Uranium" });
            State = ReactorState.CoolingDown;
        }

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            base.Init(objectBuilder);

            _reactor = Entity as IMyReactor;
            if (_reactor == null) return;

            _inventory = _reactor.GetInventory(0);
            int stateVal = (int)Math.Round(Storage.GetFloat(_reactor, Config.ReactorState));
            _state = (stateVal >= 0 && stateVal <= 3) ? (ReactorState)stateVal : ReactorState.Idle;
            _burningCycleCountDown = Storage.GetFloat(_reactor, Config.FuelCooldown);
            _coreTemp = Storage.GetFloat(_reactor, Config.CoreTempKey, 0f);
            _autoRestartOn = Storage.GetBool(_reactor, Config.BlockStateKey, false);
            _controlRodThreshold = Storage.GetFloat(_reactor, Config.ControlRodThresholdKey, Config.Instance.CONTROL_ROD_THRESHOLD_DEFAULT);

            _reactor.Enabled = false;
            _reactor.EnabledChanged += OnEnabledChanged;
            _reactor.AppendingCustomInfo += OnAppendCustomInfo;

            ComputeFuelPlan(_reactor, out _batchFuelAmouont, out _batchBurningTime);
            _coreTermalCapacity = GetCoreThermalCapacity();

            _theoreticalMaxMW = (GetCleanEnergy() / _batchBurningTime) / 1000000f;
            _defMaxMW = (_reactor as MyReactor)?.BlockDefinition?.MaxPowerOutput ?? 1f;
            if (_defMaxMW > 0f)
                _reactor.PowerOutputMultiplier = _theoreticalMaxMW / _defMaxMW;

            InitiateSource();
        }

        protected override void OnHmsInit()
        {
            ReactorTerminalControls.Register();
        }

        public override void OnDetachedFromHeatSystem() { }

        public override void Close()
        {
            if (_reactor != null)
            {
                _reactor.EnabledChanged -= OnEnabledChanged;
                _reactor.AppendingCustomInfo -= OnAppendCustomInfo;
            }
            base.Close();
        }

        public override void OnAddedToScene()
        {
            base.OnAddedToScene();
            if (_reactor == null) return;
            _reactor.AppendingCustomInfo -= OnAppendCustomInfo;
            _reactor.AppendingCustomInfo += OnAppendCustomInfo;
            if (_subscribedGrid != null)
            {
                _subscribedGrid.OnBlockIntegrityChanged -= OnBlockIntegrityChanged;
                _subscribedGrid.OnGridBlockDamaged -= OnGridBlockDamaged;
            }
            _subscribedGrid = _reactor.CubeGrid;
            if (_subscribedGrid != null)
            {
                _subscribedGrid.OnBlockIntegrityChanged += OnBlockIntegrityChanged;
                _subscribedGrid.OnGridBlockDamaged += OnGridBlockDamaged;
            }
        }

        public override void OnRemovedFromScene()
        {
            if (_subscribedGrid != null)
            {
                _subscribedGrid.OnBlockIntegrityChanged -= OnBlockIntegrityChanged;
                _subscribedGrid.OnGridBlockDamaged -= OnGridBlockDamaged;
                _subscribedGrid = null;
            }
            base.OnRemovedFromScene();
        }

        private void EnsureApiInitialized()
        {
            if (_apiInitialized || Api?.Utils == null || _reactor == null) return;
            _apiInitialized = true;
            _blockTermalCapacity = Api.Utils.GetThermalCapacity(_reactor);
            _coreTermalCapacity = GetCoreThermalCapacity();
            float blockHeat = Api.Utils.GetHeat(_reactor);
            if (_coreTemp == 0f)
                _coreTemp = blockHeat;
            else if (blockHeat == 0f)
                Api.Utils.SetHeat(_reactor, _coreTemp, silent: true);
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
            if (Api?.Utils == null) return;

            float currentHeat = Api.Utils.GetHeat(_reactor);
            float internalUse = _lastTempChange;
            float neighborExchange;
            float networkExchange;

            var neighborInfo = new StringBuilder();
            AddNeighborAndNetworksInfo(neighborInfo, out neighborExchange, out networkExchange);

            float ambientExchange = Api.Utils.GetAmbientHeatLoss(block, 1);
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

        public override float GetHeatChange(float deltaTime)
        {
            EnsureApiInitialized();
            if (Api?.Utils == null) return 0f;
            if (!MyAPIGateway.Session.IsServer)
            {
                if (_state == ReactorState.Running)
                    _burningCycleCountDown = Math.Max(0f, _burningCycleCountDown - deltaTime);
                return 0f;
            }
            EstimateErrors();
            var @internal = GetTempChange(deltaTime);
            var ambientExchange = Api.Utils.GetAmbientHeatLoss(_reactor, deltaTime);
            return @internal - ambientExchange;
        }

        public override void SpreadHeat(float deltaTime) => SpreadHeatStandard(deltaTime);

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

        public override void ReactOnNewHeat(float heat)
        {
            Api?.Effects.UpdateBlockHeatLight(_reactor, heat);
            bool shouldBlink = State == ReactorState.Running && CoreTemp > Config.Instance.REACTOR_WORKING_TEMPERATURE && !_meltdownTriggered;
            SetBlinkFrameUpdate(shouldBlink);
            if (!shouldBlink)
                UpdateEmissiveState();
            UpdateSmokeEffect();
            _reactor?.SetDetailedInfoDirty();
            _reactor?.RefreshCustomInfo();
            if (MyAPIGateway.Session.IsServer)
                BroadcastState();
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
                float meltdownTemp = Config.Instance.REACTOR_MELTDOWN_TEMPERATURE;
                if (!_warnedPrimary && CoreTemp >= meltdownTemp - 200f)
                {
                    _warnedPrimary = true;
                    BroadcastState();
                }
                if (!_warnedCritical && CoreTemp >= meltdownTemp - 100f)
                {
                    _warnedCritical = true;
                    BroadcastState();
                }
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
            _lastOutputMW = outputMW;
            // vanilla OnCapacityChanged/OnEnabledChanged reset these; re-assert or distributor ignores the source
            if (outputMW > 0f)
            {
                _source.Enabled = true;
                _source.SetRemainingCapacityByType(MyResourceDistributorComponent.ElectricityId, float.PositiveInfinity);
            }
            if (_defMaxMW > 0f)
                _reactor.PowerOutputMultiplier = (outputMW > 0f ? outputMW : _theoreticalMaxMW) / _defMaxMW;
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
            var extTemp = Api.Utils.GetHeat(_reactor);
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
            float ceiling = Config.Instance.REACTOR_MELTDOWN_TEMPERATURE - 100f;
            float range = ceiling - _controlRodThreshold;
            if (range <= 0f)
                return Config.Instance.MAX_ROD_FRACTION;
            float t = Math.Max(0f, Math.Min(1f, (CoreTemp - _controlRodThreshold) / range));
            return (float)Math.Pow(t, 0.5f) * Config.Instance.MAX_ROD_FRACTION;
        }

        private float getInternalExchangeEnergy(float deltaTime)
        {
            var extTemp = Api.Utils.GetHeat(_reactor);
            float sizeScale = (float)Math.Pow(_batchFuelAmouont / FUEL_REFERENCE, CONDUCTANCE_SIZE_EXPONENT);
            float conductivity = Api.Utils.GetHmsConfig().HEATPIPE_CONDUCTIVITY * Config.Instance.CORE_TO_BLOCK_CONDUCTANCE_MODIFIER * sizeScale;
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
            if (Api?.Utils == null) return;
            bool shouldSmoke = !_meltdownTriggered && CoreTemp >= Config.Instance.REACTOR_MELTDOWN_TEMPERATURE - 200;
            if (shouldSmoke && !_smokeActive)
            {
                Api.Effects.InstantiateSmoke(_reactor);
                _smokeActive = true;
            }
            else if (!shouldSmoke && _smokeActive)
            {
                Api.Effects.RemoveSmoke(_reactor);
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

            var grid = _reactor.CubeGrid;
            var slim = _reactor.SlimBlock;
            float gs = grid.GridSize;
            float hx = (slim.Max.X - slim.Min.X + 1) * gs / 2f;
            float hy = (slim.Max.Y - slim.Min.Y + 1) * gs / 2f;
            float hz = (slim.Max.Z - slim.Min.Z + 1) * gs / 2f;
            float circumRadius = (float)Math.Sqrt(hx * hx + hy * hy + hz * hz);
            float primaryRadius = Math.Max(30f, circumRadius * 6f);
            float faceRadius = Math.Max(16f, circumRadius * 3f);

            var center = _reactor.GetPosition();
            var m = grid.WorldMatrix;
            MyVisualScriptLogicProvider.CreateExplosion(center, primaryRadius, 5000000);
            MyVisualScriptLogicProvider.CreateExplosion(center + m.Right   * hx, faceRadius, 2000000);
            MyVisualScriptLogicProvider.CreateExplosion(center - m.Right   * hx, faceRadius, 2000000);
            MyVisualScriptLogicProvider.CreateExplosion(center + m.Up      * hy, faceRadius, 2000000);
            MyVisualScriptLogicProvider.CreateExplosion(center - m.Up      * hy, faceRadius, 2000000);
            MyVisualScriptLogicProvider.CreateExplosion(center + m.Forward * hz, faceRadius, 2000000);
            MyVisualScriptLogicProvider.CreateExplosion(center - m.Forward * hz, faceRadius, 2000000);
        }

        private string FormatEnergyPerSecond(double value)
        {
            if (value >= 1000000) return $"{value / 1000000:F2} MJ/s";
            if (value >= 1000) return $"{value / 1000:F2} kJ/s";
            return $"{value:F0} J/s";
        }

        private void BroadcastState()
        {
            if (_reactor == null || !MyAPIGateway.Session.IsServer) return;
            ReactorSession.Networking.RelayToClients(new ReactorStateSync
            {
                EntityId = _reactor.EntityId,
                CoreTemp = CoreTemp,
                FuelCountdown = FuelCountdown,
                State = (int)State,
                AutoRestart = _autoRestartOn,
                ControlRodThreshold = _controlRodThreshold,
                OutputMW = _lastOutputMW
            });
        }

        public void ReceiveStateSync(float coreTemp, float fuelCountdown, ReactorState state, bool autoRestart, float controlRodThreshold, float outputMW)
        {
            _coreTemp = coreTemp;
            _burningCycleCountDown = fuelCountdown;
            _state = state;
            _autoRestartOn = autoRestart;
            _controlRodThreshold = controlRodThreshold;
            UpdateEmissiveState();
            SetOutputPower(outputMW);
            _reactor?.SetDetailedInfoDirty();
            _reactor?.RefreshCustomInfo();
        }
    }
}
