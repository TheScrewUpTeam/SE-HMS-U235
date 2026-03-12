# HMS.U235 - Nuclear Reactor Extension

[![For Space Engineers](https://img.shields.io/badge/Space%20Engineers-v1.200%2B-blue)](https://www.spaceengineersgame.com/)
[![Requires HMS](https://img.shields.io/badge/Requires-HMS%20Heat%20Management%20System-orange)](https://github.com/TheScrewUpTeam/SE-Heat-Management)
[![Status](https://img.shields.io/badge/Status-Heavy%20Development-red)](https://github.com/TheScrewUpTeam/HMS.U235)

An advanced nuclear reactor extension for the Heat Management System (HMS) mod, bringing realistic uranium-235 fission reactor mechanics to Space Engineers.

## Overview

HMS.U235 transforms standard Space Engineers reactors into sophisticated nuclear power systems that simulate realistic thermal mechanics, fuel processing, and power generation cycles. This extension requires the base [HMS - Heat Management System](https://github.com/TheScrewUpTeam/SE-Heat-Management) mod to function.

## Features

### 🌡️ Realistic Thermal Simulation
- **Four-State Reactor Cycle**: Idle → Heating Up → Running → Cooling Down
- **Core Temperature Management**: Manage core temperatures from startup to operating conditions
- **Realistic Heat Exchange**: Heat transfer between reactor core and shell block
- **Thermal Inertia**: Reactors take time to heat up and cool down based on mass and fuel

### ⚡ Advanced Power Generation
- **Temperature-Dependent Output**: Power generation scales with core temperature
- **Fuel Batching System**: Reactors consume uranium in realistic batches based on size
- **Energy Efficiency Modeling**: Accounts for burn efficiency, heat waste, and internal losses
- **Dynamic Power Control**: Automated power output adjustment based on reactor state

### ⛽ Intelligent Fuel Management
- **Automatic Fuel Detection**: Pulls uranium from connected cargo containers via conveyors
- **Batch Processing**: Fuel consumption calculated based on reactor size and volume
- **Fuel Lifecycle Tracking**: Monitor remaining fuel time and consumption rates
- **Fuel Requirements**: Reactors require minimum fuel amounts to start

### 🎛️ Enhanced Terminal Controls
- **Launch/Stop Buttons**: Direct control over reactor startup and shutdown sequences
- **State Monitoring**: Real-time display of reactor status, temperatures, and performance
- **Diagnostic Information**: Detailed thermal analysis including heat exchange rates
- **Safety Features**: Automatic shutdown if temperature limits are exceeded

### 📊 Comprehensive Status Display
- **Core Temperature**: Track internal reactor core temperature
- **Block Temperature**: Monitor external shell temperature
- **Power Output**: Real-time power generation with realistic units (J/s, kJ/s, MJ/s)
- **Fuel Status**: Time-to-live for current fuel batch
- **Thermal Analysis**: Breakdown of heat sources and exchange rates
- **Network Visualization**: Shows heat exchange with neighboring blocks and pipe networks

## How It Works

### Reactor States

1. **Idle**: Reactor is offline, maintaining safe maintenance temperature
2. **Heating Up**: Reactor is warming up to operating temperature
3. **Running**: Reactor is at operating temperature and generating power
4. **Cooling Down**: Reactor is shutting down and cooling to maintenance temperature

### Thermal Mechanics

The reactor simulation models two distinct thermal masses:
- **Core Thermal Capacity**: Based on fuel mass, determines how quickly the reactor core heats up
- **Block Thermal Capacity**: Based on reactor block mass, affects heat exchange with surroundings

Heat flows between the core and block based on temperature differential, enabling realistic warm-up and cool-down cycles.

### Power Generation Model

Power output follows this equation:
```
Power = (CoreTemp / OperatingTemp) × OptimalPower + IgnitionAssist
```

Where:
- **OptimalPower**: Maximum theoretical output based on fuel batch
- **IgnitionAssist**: Small boost when cold to assist startup
- **Temperature Modifier**: Linear scaling based on current core temperature

## Installation

### Prerequisites
- Space Engineers v1.200 or newer
- [HMS - Heat Management System](https://github.com/TheScrewUpTeam/SE-Heat-Management) (required)

### Steps
1. Install HMS base mod via Steam Workshop
2. Install HMS.U235 extension mod
3. Ensure both mods are enabled in your world
4. The extension will automatically enhance all existing reactors

## Configuration

HMS.U235 includes extensive configuration options allowing you to customize reactor behavior:

### Configuration File
```
TSUT_U235_Config.xml (stored in world storage)
```

### Key Settings
- `CORE_THERMAL_CAPACITY`: Heat capacity of reactor core (J/g·°C)
- `REACTOR_WORKING_TEMPERATURE`: Optimal operating temperature (°C)
- `REACTOR_MAINTENANCE_TEMPERATURE`: Safe idle temperature (°C)
- `BURN_ENFFICIENCY`: Fuel burn efficiency (default: 5%)
- `URANIUM_ENERGY`: Energy content of uranium (J/g)
- `ALHPA_MODIFIER`: Fuel batch scaling exponent
- `BETA_MODIFIER`: Burn time scaling exponent

### Auto-Update System
- Configuration automatically updates to latest version
- Settings are preserved during updates
- Option to disable auto-update if needed

## API Integration

HMS.U235 extends the HMS API with specialized reactor functionality:

```csharp
// Access through HMS API
var api = new HmsApi(() => { /* callback */ });

// Reactor-specific behaviors
api.RegisterHeatBehaviorFactory(blockSelector, behaviorCreator);
```

The extension provides:
- Custom `ReactorHandler` implementing `AHeatBehavior`
- Integration with HMS thermal simulation
- Access to HMS utilities and effects systems

## Development Status

⚠️ **Heavy Development** - This mod is actively being developed with frequent updates and improvements.

### Current Focus Areas
- ✅ Reactor state machine and lifecycle
- ✅ Thermal simulation and heat exchange
- ✅ Terminal controls and UI
- ✅ Fuel management system
- ✅ Configuration framework
- ✅ Storage and persistence
- 🔄 Power output optimization
- 🔄 Auto/Manual controls
- 🔄 Network integration improvements

### Known Limitations
- Power output tuning still in progress
- Some debug logging present (will be cleaned up)
- Balance testing ongoing

## License

This project is part of the TSUT (The Screw Up Team) mod collection. See the main HMS repository for license details.

## Contributing

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/AmazingFeature`)
3. Commit your changes (`git commit -m 'Add some AmazingFeature'`)
4. Push to the branch (`git push origin feature/AmazingFeature`)
5. Open a Pull Request

## Support

- 🐛 **Issues**: [GitHub Issues](https://github.com/TheScrewUpTeam/HMS.U235/issues)
- 💬 **Discord**: [The Screw Up Team community](https://discord.com/invite/Zy6GT4nGfC)

## Changelog

### v1.0.1 (Current)
- Reactor controls implementation
- Configuration system with auto-update
- Storage and persistence framework
- Terminal UI enhancements

### v1.0.0 (Baseline)
- Initial reactor state machine
- Basic thermal simulation
- HMS API integration

## Credits

- **The Screw Up Team** - Development
- **TSUT Community** - Testing and feedback
- **Keen Software House** - Space Engineers platform

---

**Note**: This extension mod requires the HMS - Heat Management System base mod to function. Ensure both are installed and enabled.
