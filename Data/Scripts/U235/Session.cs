
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using TSUT.HeatManagement;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.Utils;

namespace TSUT.U235
{
    [MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
    public class Session : MySessionComponentBase
    {
        HmsApi _api;
        Dictionary<IMyCubeBlock, ReactorHandler> library = new Dictionary<IMyCubeBlock, ReactorHandler>();

        public override void LoadData()
        {
            _api = new HmsApi(OnHmsConnected);
            RegisterAdditionalControls();
        }

        private void OnHmsConnected()
        {
            _api.RegisterHeatBehaviorFactory(
                (grid) =>
                {
                    var reactors = new List<IMyReactor>();
                    MyAPIGateway.TerminalActionsHelper.GetTerminalSystemForGrid(grid).GetBlocksOfType(reactors);
                    var cubeBlocks = new List<IMyCubeBlock>();
                    foreach (var reactor in reactors)
                    {
                        cubeBlocks.Add(reactor);
                    }
                    MyLog.Default.WriteLine($"[HMS.U235] Found {cubeBlocks.Count} reactors on {grid.DisplayNameText}");
                    return cubeBlocks;
                },
                (block) =>
                {
                    if (!(block is IMyReactor))
                        return null;

                    var handler = new ReactorHandler(block as IMyReactor, _api);

                    library.Add(block, handler);

                    return handler;
                }
            );
        }

        private ReactorHandler GetReactorHandler(IMyCubeBlock block)
        {
            ReactorHandler handler;
            if (library.TryGetValue(block, out handler))
                return handler;

            return null;
        }

        private void RegisterAdditionalControls()
        {
            MyAPIGateway.TerminalControls.CustomControlGetter += OnCustomControlsGetter;
        }

        private void OnCustomControlsGetter(IMyTerminalBlock block, List<IMyTerminalControl> controls)
        {
            if (block is IMyReactor)
            {
                RegisterCustomReactorControls(controls);
                MyLog.Default.WriteLine($"[HMS.U235] Buttons added for {block.DisplayNameText}");
            }
        }

        public void RegisterCustomReactorControls(List<IMyTerminalControl> controls)
        {
            if (controls.Any(c => c.Id == "HeatReactor_Launch"))
                return;
            
            var launchButton = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyReactor>("HeatReactor_Launch");
            launchButton.Title = MyStringId.GetOrCompute("Launch Reactor");
            launchButton.Tooltip = MyStringId.GetOrCompute("Begin the reactor warm-up and start power generation.");
            launchButton.SupportsMultipleBlocks = false;
            launchButton.Enabled = b => GetReactorHandler(b).IsReadyToLaunch;
            launchButton.Visible = b => GetReactorHandler(b) != null;
            launchButton.Action = b => GetReactorHandler(b).ManualLaunch();
            controls.Add(launchButton);

            var stopButton = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyReactor>("HeatReactor_Stop");
            stopButton.Title = MyStringId.GetOrCompute("Stop Reactor");
            stopButton.Tooltip = MyStringId.GetOrCompute("Abort and begin stopping process, all the fuel will be wasted");
            stopButton.SupportsMultipleBlocks = false;
            stopButton.Enabled = b => GetReactorHandler(b).IsReadyToStop;
            stopButton.Visible = b => GetReactorHandler(b) != null;
            stopButton.Action = b => GetReactorHandler(b).ManualStop();
            controls.Add(stopButton);
        }
    }
}