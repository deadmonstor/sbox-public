using Sandbox;
using System;
using System.Linq;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace NetworkObjectLoad;

public sealed class NetworkLoadHud : PanelComponent
{
	private NetworkObjectScenario _scenario;
	protected override void OnStart() => _scenario = Scene.GetAllComponents<NetworkObjectScenario>().FirstOrDefault();
	protected override int BuildHash() => HashCode.Combine( _scenario?.Status, _scenario?.Detail, Networking.IsHost );

	protected override void BuildRenderTree( RenderTreeBuilder builder )
	{
		var role = Networking.IsHost ? "HOST" : "CLIENT";
		builder.OpenElement( 0, "div" );
		builder.AddAttribute( 1, "class", "status" );
		builder.AddContent( 2, $"NETWORK OBJECT LOAD / {role}\n{_scenario?.Status}\n\n{_scenario?.Detail}\n\nSelect a workload scene before Play. Restart Play to reset.\nLate Join: connect an additional client during Running.\nRendering is disabled by default. Metrics export locally after completion." );
		builder.CloseElement();
	}
}
