using System;
using Sandbox.ModAPI;
using VRage.Utils;

namespace TSUT.HeatManagement
{
    public class Config
    {
        public static string Version = "1.0.2";

        public static readonly Guid CoreTempKey = new Guid("decafbad-0000-4c00-babe-c0ffee000011");
        public static readonly Guid BlockStateKey = new Guid("decafbad-0000-4c00-babe-c0ffee000012");
        public static readonly Guid FuelCooldown = new Guid("decafbad-0000-4c00-babe-c0ffee000013");
        public static readonly Guid ReactorState = new Guid("decafbad-0000-4c00-babe-c0ffee000014");
        public static readonly Guid ControlRodThresholdKey = new Guid("decafbad-0000-4c00-babe-c0ffee000015");

        public string SYSTEM_VERSION = "1.0.2";
        public bool SYSTEM_AUTO_UPDATE = true;
        public float CORE_TO_BLOCK_CONDUCTANCE_MODIFIER = 25f;
        public float CORE_THERMAL_CAPACITY = 100; // J/kg*°C
        public float REACTOR_MINIMAL_LAUNCH_TEMPERATURE = 0; // °C
        public float REACTOR_WORKING_TEMPERATURE = 800; // °C
        public float REACTOR_MELTDOWN_TEMPERATURE = 1250; // °C
        public float REACTOR_MAINTENANCE_TEMPERATURE = 100; // °C
        public float MAX_ENERGY_OUTPUT = 1000000; // J/kg fuel
        public float ALHPA_MODIFIER = 1.15f; // mass ~ V^alpha
        public float BETA_MODIFIER = 0.29f; // time ~ mass^beta
        public float URANIUM_ENERGY = 82100000000; // Total U energy (J/kg)
        public float BURN_ENFFICIENCY = 0.25f; // 25% burn efficiency
        public float BURN_TIME = 1800; // Baseline for burning time (s)
        public float INTERNAL_WASTE = 0.05f; // 5% of energy used to clear fuel waste
        public float HEAT_WASTE = 0.67f; // 67% of energy wasted as heat (real reactor thermal efficiency ~33%)
        public float MELTDOWN_GRIND_TEMP_THRESHOLD = 200f; // Min core temp (°C) to trigger meltdown when grinding
        public float HEAT_SCALE_EXPONENT = 0.185f;
        public float CONTROL_ROD_THRESHOLD_DEFAULT = 900f;
        public float MAX_ROD_FRACTION = 0.80f;
        private static Config _instance;
        private const string CONFIG_FILE = "TSUT_U235_Config.xml";

        public static Config Instance
        {
            get
            {
                if (_instance == null)
                    _instance = Load();
                return _instance;
            }
        }

        public static Config Load()
        {
            Config config = new Config();
            if (MyAPIGateway.Utilities.FileExistsInWorldStorage(CONFIG_FILE, typeof(Config)))
            {
                try
                {
                    string contents;
                    using (var reader = MyAPIGateway.Utilities.ReadFileInWorldStorage(CONFIG_FILE, typeof(Config)))
                    {
                        contents = reader.ReadToEnd();
                    }

                    // Check if version exists in the XML before deserializing
                    bool hasVersion = contents.Contains("<SYSTEM_VERSION>");

                    config = MyAPIGateway.Utilities.SerializeFromXML<Config>(contents);

                    var defaultConfig = new Config();

                    var configUpdateNeeded = !hasVersion || config.SYSTEM_AUTO_UPDATE && config.SYSTEM_VERSION != defaultConfig.SYSTEM_VERSION;

                    MyLog.Default.WriteLine($"[HMS.U235] AutoUpdate: {config.SYSTEM_AUTO_UPDATE}, VersionMatches: {hasVersion && config.SYSTEM_VERSION == defaultConfig.SYSTEM_VERSION}, UpdateNeeded: {configUpdateNeeded}");

                    // Check version and auto-update if needed
                    if (configUpdateNeeded)
                    {
                        MyAPIGateway.Utilities.ShowMessage("HMS.U235", $"Config version mismatch. Auto-updating from {(hasVersion ? config.SYSTEM_VERSION : "Unknown")} to {defaultConfig.SYSTEM_VERSION}");
                        // Keep auto-update setting but reset everything else to defaults
                        bool autoUpdate = config.SYSTEM_AUTO_UPDATE;
                        config = new Config();
                        config.SYSTEM_AUTO_UPDATE = autoUpdate;
                        return config;
                    }
                }
                catch (Exception e)
                {
                    MyAPIGateway.Utilities.ShowMessage("HMS.U235", $"Failed to load config, using defaults. {e.Message}");
                }
            }

            return config;
        }

        public void Save()
        {
            try
            {
                using (var writer = MyAPIGateway.Utilities.WriteFileInWorldStorage(CONFIG_FILE, typeof(Config)))
                {
                    writer.Write(MyAPIGateway.Utilities.SerializeToXML(this));
                }
            }
            catch (Exception e)
            {
                MyLog.Default.Warning("HMS.U235", $"Failed to save config: {e.Message}");
            }
        }
    }
}