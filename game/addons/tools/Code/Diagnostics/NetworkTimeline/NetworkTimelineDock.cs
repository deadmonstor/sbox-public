using Sandbox;

namespace Editor;

/// <summary>
/// Shows a per-object timeline of SyncVar changes and RPC calls, and lets you scrub backwards
/// to see (and apply to the live GameObject) what its networked state looked like in the past.
/// </summary>
[Dock( "Editor", "Network Timeline", "podcasts" )]
public class NetworkTimelineDock : Widget
{
	const int RowHeight = 20;
	const int ObjectListWidth = 220;
	const int ScrubBarHeight = 28;
	const int BannerHeight = 24;

	Button StartStopButton;
	bool _recording;

	Guid _selectedRootId;
	string _selectedRootName;

	readonly List<(Rect Rect, Guid Id, string Name)> _objectRows = new();
	Rect _scrubRect;
	Rect _returnToLiveRect;
	bool _draggingScrub;

	public NetworkTimelineDock( Widget parent ) : base( parent )
	{
		MinimumSize = new Vector2( 480, 260 );

		Layout = Layout.Column();
		Layout.Margin = 4;

		var header = Layout.AddRow();
		header.Spacing = 4;
		StartStopButton = header.Add( new Button( "Start Recording", this ) { Clicked = ToggleRecording } );
		header.Add( new Button( "Clear", this ) { Clicked = ClearHistory } );
		header.AddStretchCell();

		Layout.AddSpacingCell( 4 );
	}

	public override void OnDestroyed()
	{
		base.OnDestroyed();

		NetworkTimelineRecorder.Current?.StopRecording();
		_recording = false;
	}

	void ToggleRecording()
	{
		_recording = !_recording;

		if ( _recording )
		{
			NetworkTimelineRecorder.Current?.StartRecording();
			StartStopButton.Text = "Stop Recording";
		}
		else
		{
			NetworkTimelineRecorder.Current?.StopRecording();
			StartStopButton.Text = "Start Recording";
		}
	}

	void ClearHistory()
	{
		NetworkTimelineRecorder.Current?.ClearHistory();
	}

	Rect ContentRect => new Rect( 0, 44, Width, Height - 44 );

	protected override void OnPaint()
	{
		base.OnPaint();

		var content = ContentRect;
		var listRect = new Rect( content.Left, content.Top, ObjectListWidth, content.Height );
		var mainRect = new Rect( listRect.Right, content.Top, content.Width - ObjectListWidth, content.Height );

		PaintObjectList( listRect );
		PaintMain( mainRect );

		Update();
	}

	void PaintObjectList( Rect rect )
	{
		Paint.ClearPen();
		Paint.SetBrush( Theme.SurfaceLightBackground );
		Paint.DrawRect( rect );

		_objectRows.Clear();

		var recorder = NetworkTimelineRecorder.Current;
		var y = rect.Top + 4;

		var scene = Game.ActiveScene;
		if ( scene is null )
		{
			Paint.SetPen( Theme.TextDisabled );
			Paint.DrawText( rect.Shrink( 8 ), "No active scene", TextFlag.LeftTop );
			return;
		}

		foreach ( var go in scene.GetAllObjects( false ) )
		{
			if ( go is null || !go.IsValid() || !go.IsNetworkRoot )
				continue;

			var rowRect = new Rect( rect.Left, y, rect.Width, RowHeight );
			var isSelected = go.Id == _selectedRootId;
			var isScrubbing = recorder?.IsScrubbing( go.Id ) ?? false;

			if ( isSelected )
			{
				Paint.ClearPen();
				Paint.SetBrush( Theme.SelectedBackground );
				Paint.DrawRect( rowRect );
			}

			Paint.SetPen( isScrubbing ? Theme.Yellow : Theme.Text );
			Paint.DrawText( rowRect.Shrink( 6, 0 ), go.Name, TextFlag.LeftCenter );

			_objectRows.Add( (rowRect, go.Id, go.Name) );

			y += RowHeight;
		}
	}

	void PaintMain( Rect rect )
	{
		if ( _selectedRootId == Guid.Empty )
		{
			Paint.SetPen( Theme.TextDisabled );
			Paint.DrawText( rect, "Select a networked object", TextFlag.Center );
			return;
		}

		var recorder = NetworkTimelineRecorder.Current;
		var isScrubbing = recorder?.IsScrubbing( _selectedRootId ) ?? false;

		var bannerRect = new Rect( rect.Left, rect.Top, rect.Width, BannerHeight );
		var scrubRect = new Rect( rect.Left, rect.Bottom - ScrubBarHeight, rect.Width, ScrubBarHeight );
		var listRect = new Rect( rect.Left, bannerRect.Bottom, rect.Width, rect.Height - BannerHeight - ScrubBarHeight );

		PaintBanner( bannerRect, isScrubbing );
		PaintEventList( listRect, recorder, isScrubbing );
		PaintScrubBar( scrubRect, recorder, isScrubbing );
	}

	void PaintBanner( Rect rect, bool isScrubbing )
	{
		_returnToLiveRect = default;

		Paint.ClearPen();
		Paint.SetBrush( isScrubbing ? Theme.Yellow.WithAlpha( 0.15f ) : Theme.SurfaceLightBackground );
		Paint.DrawRect( rect );

		if ( !isScrubbing )
		{
			Paint.SetPen( Theme.TextLight );
			Paint.DrawText( rect.Shrink( 8, 0 ), $"Live - {_selectedRootName}", TextFlag.LeftCenter );
			return;
		}

		Paint.SetPen( Theme.Yellow );
		Paint.DrawText( rect.Shrink( 8, 0 ), "VIEWING HISTORY", TextFlag.LeftCenter );

		var buttonRect = new Rect( rect.Right - 116, rect.Top + 2, 108, rect.Height - 4 );
		Paint.ClearPen();
		Paint.SetBrush( Theme.Blue );
		Paint.DrawRect( buttonRect, 3 );
		Paint.SetPen( Color.White );
		Paint.DrawText( buttonRect, "Return to Live", TextFlag.Center );

		_returnToLiveRect = buttonRect;
	}

	void PaintEventList( Rect rect, NetworkTimelineRecorder recorder, bool isScrubbing )
	{
		Paint.ClearPen();
		Paint.SetBrush( Theme.SurfaceBackground );
		Paint.DrawRect( rect );

		var events = recorder?.GetEvents( _selectedRootId ) ?? Array.Empty<NetworkTimelineRecorder.TimelineEvent>();
		if ( events.Count == 0 )
		{
			Paint.SetPen( Theme.TextDisabled );
			Paint.DrawText( rect, "No events recorded yet", TextFlag.Center );
			return;
		}

		var maxRows = Math.Max( 1, rect.Height / RowHeight );
		var y = rect.Top + 2;
		var foundCurrent = false;

		for ( var i = events.Count - 1; i >= 0 && (events.Count - i) <= maxRows; i-- )
		{
			var ev = events[i];
			var rowRect = new Rect( rect.Left, y, rect.Width, RowHeight );

			// Events after the scrub playhead haven't "happened" yet from the viewed point in time.
			var isFuture = isScrubbing && ev.Time > _scrubTime;

			// The most recent non-future event is the one whose value is currently applied to the object.
			var isCurrent = isScrubbing && !isFuture && !foundCurrent;
			if ( isCurrent )
				foundCurrent = true;

			if ( isCurrent )
			{
				Paint.ClearPen();
				Paint.SetBrush( Theme.Yellow.WithAlpha( 0.15f ) );
				Paint.DrawRect( rowRect );
			}

			var (icon, color) = ev.Kind switch
			{
				NetworkTimelineRecorder.EventKind.SyncVar => ("var", Theme.Green),
				NetworkTimelineRecorder.EventKind.Transform => ("pos", Theme.Primary),
				NetworkTimelineRecorder.EventKind.RpcOut => ("out", Theme.Blue),
				NetworkTimelineRecorder.EventKind.RpcIn => ("in", Theme.Pink),
				_ => ("?", Theme.TextDisabled)
			};

			var detail = ev.Kind is NetworkTimelineRecorder.EventKind.SyncVar or NetworkTimelineRecorder.EventKind.Transform
				? $"{ev.OldValue ?? "null"} -> {ev.NewValue ?? "null"}"
				: FormatArgs( ev.NewValue as object[] );

			Paint.SetPen( isFuture ? color.WithAlpha( 0.3f ) : color );
			Paint.DrawText( new Rect( rowRect.Left + 6, rowRect.Top, 40, rowRect.Height ), icon, TextFlag.LeftCenter );

			Paint.SetPen( isFuture ? Theme.TextDisabled : Theme.Text );
			Paint.DrawText( new Rect( rowRect.Left + 50, rowRect.Top, rowRect.Width - 56, rowRect.Height ),
				$"[{ev.Time:0.000}] {ev.Label} {detail}", TextFlag.LeftCenter );

			y += RowHeight;
		}
	}

	static string FormatArgs( object[] args )
	{
		if ( args is null || args.Length == 0 )
			return "()";

		return $"({string.Join( ", ", args )})";
	}

	void PaintScrubBar( Rect rect, NetworkTimelineRecorder recorder, bool isScrubbing )
	{
		_scrubRect = rect;

		Paint.ClearPen();
		Paint.SetBrush( Theme.SurfaceLightBackground );
		Paint.DrawRect( rect );

		var events = recorder?.GetEvents( _selectedRootId ) ?? Array.Empty<NetworkTimelineRecorder.TimelineEvent>();
		if ( events.Count < 2 )
			return;

		var minTime = events[0].Time;
		var maxTime = events[^1].Time;
		if ( maxTime <= minTime )
			return;

		foreach ( var ev in events )
		{
			var t = (ev.Time - minTime) / (maxTime - minTime);
			var x = rect.Left + (float)t * rect.Width;

			var tickColor = ev.Kind switch
			{
				NetworkTimelineRecorder.EventKind.SyncVar => Theme.Green,
				NetworkTimelineRecorder.EventKind.Transform => Theme.Primary,
				_ => Theme.Blue
			};

			Paint.SetPen( tickColor );
			Paint.DrawLine( new Vector2( x, rect.Top + 4 ), new Vector2( x, rect.Bottom - 4 ) );
		}

		if ( isScrubbing )
		{
			var playheadTime = _scrubTime;
			var pt = (playheadTime - minTime) / (maxTime - minTime);
			var px = rect.Left + (float)Math.Clamp( pt, 0, 1 ) * rect.Width;

			Paint.SetPen( Theme.Yellow );
			Paint.DrawLine( new Vector2( px, rect.Top ), new Vector2( px, rect.Bottom ) );
		}
	}

	double _scrubTime;

	protected override void OnMousePress( MouseEvent e )
	{
		base.OnMousePress( e );

		if ( !e.LeftMouseButton )
			return;

		foreach ( var row in _objectRows )
		{
			if ( !row.Rect.IsInside( e.LocalPosition ) )
				continue;

			_selectedRootId = row.Id;
			_selectedRootName = row.Name;
			return;
		}

		if ( _returnToLiveRect.IsInside( e.LocalPosition ) )
		{
			NetworkTimelineRecorder.Current?.ReturnToLive( _selectedRootId );
			return;
		}

		if ( _scrubRect.IsInside( e.LocalPosition ) )
		{
			_draggingScrub = true;
			ScrubToPosition( e.LocalPosition );
		}
	}

	protected override void OnMouseMove( MouseEvent e )
	{
		base.OnMouseMove( e );

		if ( _draggingScrub && e.LeftMouseButton )
		{
			ScrubToPosition( e.LocalPosition );
		}
	}

	protected override void OnMouseReleased( MouseEvent e )
	{
		base.OnMouseReleased( e );

		_draggingScrub = false;
	}

	void ScrubToPosition( Vector2 localPosition )
	{
		var recorder = NetworkTimelineRecorder.Current;
		if ( recorder is null || _selectedRootId == Guid.Empty )
			return;

		var events = recorder.GetEvents( _selectedRootId );
		if ( events.Count < 2 )
			return;

		var minTime = events[0].Time;
		var maxTime = events[^1].Time;
		if ( maxTime <= minTime )
			return;

		var t = Math.Clamp( (localPosition.x - _scrubRect.Left) / _scrubRect.Width, 0, 1 );
		_scrubTime = minTime + t * (maxTime - minTime);

		recorder.ScrubTo( _selectedRootId, _scrubTime );
	}
}
