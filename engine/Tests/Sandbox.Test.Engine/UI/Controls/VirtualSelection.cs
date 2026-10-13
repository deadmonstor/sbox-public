using System.Collections.Generic;
using Sandbox.UI;

namespace UITests.Controls;

[TestClass]
[DoNotParallelize]
public class VirtualSelectionTest
{
	[TestMethod]
	public void SelectionSurvivesScrollingAndCopiesInItemOrder()
	{
		ThreadSafe.MarkMainThread();
		var root = new RootPanel { PanelBounds = new Rect( 0, 0, 1000, 1000 ) };
		var list = new VirtualList { Parent = root, AllowChildSelection = true, ItemHeight = 20 };
		list.Style.Set( "width: 200px; height: 100px; flex-direction: column;" );
		var labels = new Dictionary<int, Label>();
		list.OnCreateCell = ( cell, data ) =>
		{
			var label = new Label { Parent = cell, Text = $"Row {data}" };
			label.Style.Set( "width: 200px; height: 20px; font-size: 12px;" );
			labels[(int)data] = label;
		};
		list.Items = Enumerable.Range( 0, 100 ).Select( x => (object)x ).ToList();
		root.Layout();
		root.Layout();
		list.ScrollOffset = new Vector2( 0, 200 );
		root.Layout();
		root.Layout();

		var target = labels[12];
		var selection = new Selection();
		selection.UpdateSelection( root, target, true, true, false, new Vector2( 199, target.Box.Rect.Center.y ) );
		list.ScrollOffset = new Vector2( 0, 100 );
		root.Layout();
		root.Layout();
		Assert.IsTrue( target.IsValid, "The starting cell must survive scrolling out of view." );

		var end = new Vector2( 0, labels[5].Box.Rect.Center.y );
		selection.UpdateSelection( root, labels[5], true, false, false, end );
		root.TickInternal();
		root.Layout();
		root.Layout();
		var copied = list.GetClipboardValue( false );
		Assert.AreEqual( string.Join( "\n", Enumerable.Range( 5, 8 ).Select( x => $"Row {x}" ) ), copied );

		selection.UpdateSelection( root, labels[5], false, false, true, end );
		root.TickInternal();
		list.ScrollOffset = new Vector2( 0, 0 );
		root.Layout();
		root.Layout();
		Assert.AreEqual( copied, list.GetClipboardValue( false ), "Scrolling after release must preserve the copied range." );

		selection.UpdateSelection( root, list, true, true, false, new Vector2( 0, 0 ) );
		root.Layout();
		root.Layout();
		Assert.IsFalse( target.IsValid, "Clearing selection must release retained cells." );
		root.Delete( true );
	}
}
