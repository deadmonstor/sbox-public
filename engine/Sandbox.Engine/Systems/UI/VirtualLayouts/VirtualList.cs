using Microsoft.AspNetCore.Components;
using Sandbox.UI.Layout;

namespace Sandbox.UI;

/// <summary>
/// A virtualized, scrollable list panel that only creates item panels when visible.
/// </summary>
public sealed class VirtualList : BaseVirtualPanel
{
	int? selectionAnchor;
	int selectionEnd;
	Vector2 selectionStartPoint;
	Vector2 selectionEndPoint;
	bool hasSelection;

	internal override void BeginSelection( Panel target )
	{
		ResetSelection();
		foreach ( var (index, cell) in _created )
		{
			if ( target != cell && !target.IsAncestor( cell ) ) continue;
			selectionAnchor = selectionEnd = index;
			break;
		}
	}

	internal override void ResetSelection()
	{
		selectionAnchor = null;
		hasSelection = false;
		UnselectAllInChildren();
		NeedsRebuild = true;
	}

	internal override bool TryGetSelectionRange( out int start, out int end )
	{
		start = end = 0;
		if ( selectionAnchor is not int anchor ) return false;

		start = Math.Min( anchor, selectionEnd );
		end = Math.Max( anchor, selectionEnd );
		return true;
	}

	protected override void OnDragSelect( SelectionEvent e )
	{
		if ( AllowChildSelection && selectionAnchor.HasValue )
		{
			selectionStartPoint = ScreenPositionToPanelPosition( e.StartPoint ) + ScrollOffset;
			selectionEndPoint = ScreenPositionToPanelPosition( e.EndPoint ) + ScrollOffset;
			var step = ItemHeight + Layout.Spacing.y;
			var paddingTop = Box.RectInner.Top - Box.Rect.Top;
			var index = (int)MathF.Floor( (selectionEndPoint.y - paddingTop) * ScaleFromScreen / step );
			selectionEnd = Math.Clamp( index, 0, Math.Max( 0, ItemCount - 1 ) );
			hasSelection = true;
			NeedsRebuild = true;
		}
		base.OnDragSelect( e );
	}

	protected override void FinalLayoutChildren( Vector2 offset )
	{
		base.FinalLayoutChildren( offset );
		if ( !hasSelection ) return;

		var start = PanelPositionToScreenPosition( selectionStartPoint - ScrollOffset );
		var end = PanelPositionToScreenPosition( selectionEndPoint - ScrollOffset );
		base.OnDragSelect( new SelectionEvent( "ondragselect", this )
		{
			StartPoint = start,
			EndPoint = end,
			SelectionRect = new Rect( start ).AddPoint( end )
		} );
	}

	public override string GetClipboardValue( bool cut )
	{
		if ( !AllowChildSelection ) return base.GetClipboardValue( cut );

		return string.Join( "\n", _created.OrderBy( x => x.Key )
			.Select( x => CollectSelectedChildrenText( x.Value ) )
			.Where( x => !string.IsNullOrEmpty( x ) ) );
	}

	/// <summary>
	/// Vertical list layout used to position/measure items. (Swappable later if needed.)
	/// </summary>
	internal VerticalListLayout Layout { get; } = new();

	/// <summary>
	/// Fixed height of each item.
	/// </summary>
	[Parameter]
	public float ItemHeight
	{
		get => Layout.ItemHeight;
		set => Layout.ItemHeight = value;
	}

	protected override void UpdateLayoutSpacing( Vector2 spacing )
	{
		Layout.Spacing = spacing;
	}

	protected override bool UpdateLayout()
	{
		return Layout.Update( Box, ScaleFromScreen, ScrollOffset.y * ScaleFromScreen );
	}

	protected override void GetVisibleRange( out int first, out int pastEnd )
	{
		Layout.GetVisibleRange( out first, out pastEnd );
	}

	protected override void PositionPanel( int index, Panel panel )
	{
		Layout.Position( index, panel );
	}

	protected override float GetTotalHeight( int itemCount )
	{
		return Layout.GetHeight( itemCount );
	}
}
