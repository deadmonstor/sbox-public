using Sandbox;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using System;
using System.Linq;

namespace DormancyLab;

public sealed class DormancyHud : PanelComponent
{
	private string Text()
	{
		var scenario = Scene.GetAllComponents<DormancyScenario>().FirstOrDefault();
		if ( scenario is null ) return "Network Dormancy Lab";
		var role = Networking.IsHost ? "HOST" : "CLIENT";
		var mode = scenario.AutoAdvance ? "Sequence mode: advances after a pass; stops on failure." : "Single-scene mode.";
		return $"NETWORK DORMANCY LAB / {(int)scenario.Scenario + 1}/10 / {scenario.Scenario}\n{role} / {scenario.Status}\n\n{scenario.Detail}\n\n{mode}\nOpen the console for checkpoint reports.\nHost-only checks never count as a replication pass.\nRestart Play to reset. Performance measurement is disabled.";
	}

	protected override int BuildHash() => HashCode.Combine( Text(), Networking.IsHost );

	protected override void BuildRenderTree( RenderTreeBuilder builder )
	{
		builder.OpenElement( 0, "div" );
		builder.AddAttribute( 1, "class", "status" );
		builder.AddContent( 2, Text() );
		builder.CloseElement();
	}
}
