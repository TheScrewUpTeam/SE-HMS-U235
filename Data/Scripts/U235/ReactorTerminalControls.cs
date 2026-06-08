using System.Collections.Generic;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Utils;

namespace TSUT.U235
{
    public static class ReactorTerminalControls
    {
        static bool _registered = false;

        public static void Register()
        {
            if (_registered) return;
            _registered = true;

            var existingControls = new List<IMyTerminalControl>();
            MyAPIGateway.TerminalControls.GetControls<IMyReactor>(out existingControls);
            foreach (var control in existingControls)
            {
                if (control.Id == "OnOff")
                {
                    var originalVisible = control.Visible;
                    control.Visible = b => !(b is IMyReactor) && (originalVisible == null || originalVisible(b));
                    break;
                }
            }

            var autoSwitch = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlOnOffSwitch, IMyReactor>("ReactorAutoMode");
            autoSwitch.Title = MyStringId.GetOrCompute("Mode");
            autoSwitch.OnText = MyStringId.GetOrCompute("Auto");
            autoSwitch.OffText = MyStringId.GetOrCompute("Manual");
            autoSwitch.SupportsMultipleBlocks = false;
            autoSwitch.Visible = b => b.GameLogic?.GetAs<ReactorGameLogic>() != null;
            autoSwitch.Enabled = b => b.GameLogic?.GetAs<ReactorGameLogic>() != null;
            autoSwitch.Getter = b => b.GameLogic?.GetAs<ReactorGameLogic>()?.AutoRestartOn ?? false;
            autoSwitch.Setter = (b, value) => { var h = b.GameLogic?.GetAs<ReactorGameLogic>(); if (h != null) h.AutoRestartOn = value; };
            MyAPIGateway.TerminalControls.AddControl<IMyReactor>(autoSwitch);

            var launchButton = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyReactor>("HeatReactor_Launch");
            launchButton.Title = MyStringId.GetOrCompute("Launch Reactor");
            launchButton.Tooltip = MyStringId.GetOrCompute("Begin the reactor warm-up and start power generation.");
            launchButton.SupportsMultipleBlocks = false;
            launchButton.Visible = b => b.GameLogic?.GetAs<ReactorGameLogic>() != null;
            launchButton.Enabled = b => b.GameLogic?.GetAs<ReactorGameLogic>()?.IsReadyToLaunch ?? false;
            launchButton.Action = b => b.GameLogic?.GetAs<ReactorGameLogic>()?.ManualLaunch();
            MyAPIGateway.TerminalControls.AddControl<IMyReactor>(launchButton);

            var stopButton = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyReactor>("HeatReactor_Stop");
            stopButton.Title = MyStringId.GetOrCompute("Stop Reactor");
            stopButton.Tooltip = MyStringId.GetOrCompute("Abort and begin cooldown. Fuel returned if still heating up, wasted if already running.");
            stopButton.SupportsMultipleBlocks = false;
            stopButton.Visible = b => b.GameLogic?.GetAs<ReactorGameLogic>() != null;
            stopButton.Enabled = b => b.GameLogic?.GetAs<ReactorGameLogic>()?.IsReadyToStop ?? false;
            stopButton.Action = b => b.GameLogic?.GetAs<ReactorGameLogic>()?.ManualStop();
            MyAPIGateway.TerminalControls.AddControl<IMyReactor>(stopButton);
        }
    }
}
