using System.Text;
using Sandbox.ModAPI;
using TSUT.HeatManagement;

namespace TSUT.U235
{
    public class ReactorAdapter : HmsApi.AHeatBehavior
    {
        readonly IMyReactor _reactor;
        readonly HmsApi _api;
        readonly ReactorGameLogic _logic;

        public ReactorAdapter(IMyReactor block, HmsApi api) : base(block)
        {
            _reactor = block;
            _api = api;
            _logic = block.GameLogic?.GetAs<ReactorGameLogic>();
            _logic?.SetApi(api, this);
        }

        public override float GetHeatChange(float deltaTime) => _logic?.GetHeatChange(deltaTime) ?? 0f;

        public override void ReactOnNewHeat(float heat) => _logic?.ReactOnNewHeat(heat);

        public override void SpreadHeat(float deltaTime) => SpreadHeatStandard(deltaTime, _reactor, _api);

        public override void Cleanup() { }

        public void AppendNeighborInfo(StringBuilder info, out float neighborExchange, out float networkExchange)
        {
            AddNeighborAndNetworksInfo(_reactor, _api, info, out neighborExchange, out networkExchange);
        }
    }
}
