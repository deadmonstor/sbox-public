using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Sandbox;
using System;
using System.Linq;

namespace NetworkSnapshotRepro;

public interface IReproStatus
{
	string Title { get; }
	string Instructions { get; }
	string Status { get; }
}

public sealed class ReproHud : PanelComponent
{
	private string Text()
	{
		var repro = Scene.GetAllComponents<IReproStatus>().FirstOrDefault();
		if ( repro is null ) return "No repro controller in this scene.";
		var role = Networking.IsHost ? "HOST" : "CLIENT";
		return $"{repro.Title}\n{role} / local connection {Connection.Local.Id}\n\n{repro.Instructions}\n\n{repro.Status}\n\nStop Play and reopen the scene to reset. See README.md for setup.";
	}

	protected override int BuildHash() => HashCode.Combine( Text() );

	protected override void BuildRenderTree( RenderTreeBuilder builder )
	{
		builder.OpenElement( 0, "div" );
		builder.AddAttribute( 1, "class", "status" );
		builder.AddContent( 2, Text() );
		builder.CloseElement();
	}
}
