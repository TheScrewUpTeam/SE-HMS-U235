using System.Collections.Generic;
using Sandbox.ModAPI;
using TSUT.HeatManagement;
using VRage.Game.Components;
using VRage.Game.ModAPI;

namespace TSUT.U235
{
    [MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
    public class Session : MySessionComponentBase
    {
        HmsApi _api;

        public override void LoadData()
        {
            _api = new HmsApi(OnHmsConnected);
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
                        cubeBlocks.Add(reactor);
                    return cubeBlocks;
                },
                (block) =>
                {
                    if (!(block is IMyReactor)) return null;
                    return new ReactorAdapter(block as IMyReactor, _api);
                }
            );
        }
    }
}
