using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Editor.Diagnostics;

/// <summary>
/// Offline viewer for the aggregate CSV exported by the snapshot profiler.
/// </summary>
public sealed class SnapshotProfileWindow : Window
{
	SnapshotProfileReport _report;
	string _loadedPath;
	string _metric;
	string _unit;
	Guid? _connection;
	string _search = "";
	Grouping _grouping;
	SortColumn _sortColumn;
	bool _descending = true;
	bool _updatingFilters;
	double? _globalTotal;

	readonly LineEdit _path;
	readonly ComboBox _metricFilter;
	readonly ComboBox _connectionFilter;
	readonly Label _metadata;
	readonly Label _error;
	readonly Label _warning;
	readonly Label _summary;
	readonly Label _empty;
	readonly Label _selection;
	readonly ListView _totals;
	readonly ListView _details;

	enum Grouping { Details, Objects, Fields, Connections }
	enum SortColumn { Value, Count, Identity }
	readonly record struct GroupKey( Guid Connection, Guid ObjectId, ushort Version, int Slot );

	sealed class Row
	{
		public string Identity { get; init; }
		public string Selection { get; init; }
		public double Value { get; init; }
		public decimal Count { get; init; }
		public string ValueText { get; init; }
		public string CountText { get; init; }
		public string AverageText { get; init; }
		public string ShareText { get; init; }
	}

	[Menu( "Editor", "Tools/Snapshot Profiler", "network_check" )]
	public static SnapshotProfileWindow Open()
	{
		var window = new SnapshotProfileWindow();
		window.Show();
		return window;
	}

	public SnapshotProfileWindow()
	{
		Title = "Snapshot Profiler";
		SetWindowIcon( "network_check" );
		Size = new Vector2( 1200, 800 );
		MinimumSize = new Vector2( 960, 700 );
		DeleteOnClose = true;
		Canvas = new Widget( this ) { Layout = Layout.Column() };
		Canvas.Layout.Margin = 12;
		Canvas.Layout.Spacing = 6;

		var fileRow = Canvas.Layout.AddRow();
		_path = fileRow.Add( new LineEdit( Canvas ) { PlaceholderText = "Snapshot profiler CSV path..." }, 1 );
		_path.ReturnPressed += () => LoadCapture( _path.Text );
		fileRow.Add( new Button( "Browse...", Canvas ) { Clicked = Browse } );
		fileRow.Add( new Button( "Load", Canvas ) { Clicked = () => LoadCapture( _path.Text ) } );
		fileRow.Add( new Button( "Reload", Canvas ) { Clicked = () => LoadCapture( _loadedPath ?? _path.Text ), ToolTip = "Reload the last successfully loaded capture" } );

		_error = AddLabel( "", Theme.Red );
		_error.Visible = false;
		_metadata = AddLabel( "No capture loaded. Choose a snapshot profiler CSV to begin.", Theme.Text );
		_warning = AddLabel( "", Theme.Yellow );
		_warning.Visible = false;
		AddLabel( "Offline aggregate only: bytes are outbound snapshot protocol bytes, not transport wire traffic. Timing scopes may overlap; they are not a timeline. GUIDs and numeric slots have no resolved peer, object or property names.", Theme.TextLight );

		Canvas.Layout.Add( new Label.Subtitle( "Authoritative metric totals — unaffected by detail filters", Canvas ) );
		Canvas.Layout.Add( CreateColumns() );
		_totals = Canvas.Layout.Add( new ListView( Canvas ) { ItemSize = new Vector2( 0, 28 ), ItemSpacing = 0, Margin = 0, FixedHeight = 165 } );
		_totals.ItemPaint = PaintRow;
		_totals.ItemSelected = ShowSelection;

		var filters = new ToolBar( Canvas, "SnapshotProfileFilters" );
		Canvas.Layout.Add( filters );
		filters.AddWidget( new Label( "Metric", filters ) );
		_metricFilter = new ComboBox( filters ) { MinimumWidth = 180 };
		filters.AddWidget( _metricFilter );
		filters.AddSeparator();
		filters.AddWidget( new Label( "Connection", filters ) );
		_connectionFilter = new ComboBox( filters ) { MinimumWidth = 270, MaximumWidth = 330 };
		filters.AddWidget( _connectionFilter );
		_connectionFilter.AddItem( "All connections (including shared)" );
		filters.AddSeparator();
		var search = new LineEdit( filters ) { PlaceholderText = "Object / GUID / numeric slot...", MinimumWidth = 220, ClearButtonEnabled = true };
		search.TextChanged += text => { _search = text.Trim(); RefreshDetails(); };
		filters.AddWidget( search );

		var view = new ToolBar( Canvas, "SnapshotProfileView" );
		Canvas.Layout.Add( view );
		view.AddWidget( new Label( "Group", view ) );
		var grouping = new ComboBox( view ) { MinimumWidth = 150 };
		foreach ( var mode in Enum.GetValues<Grouping>() )
		{
			grouping.AddItem( mode == Grouping.Details ? "Detail rows" : mode.ToString(), onSelected: () => { _grouping = mode; RefreshDetails(); } );
		}
		view.AddWidget( grouping );
		view.AddSeparator();
		view.AddWidget( new Label( "Sort", view ) );
		var sorting = new ComboBox( view ) { MinimumWidth = 110 };
		foreach ( var column in Enum.GetValues<SortColumn>() )
		{
			sorting.AddItem( column.ToString(), onSelected: () => { _sortColumn = column; RefreshDetails(); } );
		}
		view.AddWidget( sorting );
		var direction = new ComboBox( view ) { MinimumWidth = 130 };
		direction.AddItem( "Descending", onSelected: () => { _descending = true; RefreshDetails(); } );
		direction.AddItem( "Ascending", onSelected: () => { _descending = false; RefreshDetails(); } );
		view.AddWidget( direction );
		view.AddSeparator();
		view.AddWidget( new Label( "Groups sum only the selected metric and unit, after filtering.", view ) );

		_summary = AddLabel( "Filtered detail: no capture loaded.", Theme.Text );
		Canvas.Layout.Add( CreateColumns() );
		_empty = AddLabel( "No capture loaded.", Theme.TextLight );
		_details = Canvas.Layout.Add( new ListView( Canvas ) { ItemSize = new Vector2( 0, 30 ), ItemSpacing = 0, Margin = 0 }, 1 );
		_details.ItemPaint = PaintRow;
		_details.ItemSelected = ShowSelection;
		_selection = AddLabel( "Select a total or detail row to inspect its full identity. Count is the metric's sample count (MessageBytes samples are messages).", Theme.TextLight );
		_selection.MinimumHeight = 92;
	}

	Label AddLabel( string text, Color color )
	{
		return Canvas.Layout.Add( new Label( text, Canvas ) { Color = color, WordWrap = true, TextSelectable = true, OpenExternalLinks = false } );
	}

	Widget CreateColumns()
	{
		var header = new Widget( Canvas ) { FixedHeight = 22 };
		header.OnPaintOverride = () =>
		{
			Paint.SetPen( Theme.TextLight );
			Paint.SetDefaultFont( 8, 600 );
			DrawColumns( header.LocalRect.Shrink( 8, 0 ), "Metric / identity", "Value", "Count", "Average / sample", "Global share" );
			return true;
		};
		return header;
	}

	void Browse()
	{
		var dialog = new FileDialog( this ) { Title = "Open Snapshot Profiler CSV" };
		try
		{
			dialog.SetFindExistingFile();
			dialog.SetModeOpen();
			dialog.SetNameFilter( "Snapshot profiler CSV (*.csv);;All files (*)" );
			if ( !string.IsNullOrWhiteSpace( _path.Text ) ) dialog.SelectFile( _path.Text );
			if ( dialog.Execute() ) LoadCapture( dialog.SelectedFile );
		}
		finally
		{
			dialog.Destroy();
		}
	}

	/// <summary>
	/// Loads an offline capture. Errors are shown inline and leave the previous report intact.
	/// </summary>
	public void LoadCapture( string path )
	{
		_path.Text = path ?? "";
		SnapshotProfileReport report;
		string fullPath;
		try
		{
			if ( string.IsNullOrWhiteSpace( path ) ) throw new ArgumentException( "Choose a CSV file to load." );
			fullPath = Path.GetFullPath( path );
			using var reader = File.OpenText( fullPath );
			report = SnapshotProfileReport.Parse( reader );
		}
		catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException or ArgumentException or FormatException or OverflowException or System.Security.SecurityException or NotSupportedException )
		{
			_error.Text = $"Could not load capture: {exception.Message}" + (_report is null ? " No capture loaded." : $" Still displaying the previous capture: {_loadedPath}");
			_error.Visible = true;
			return;
		}

		_report = report;
		_loadedPath = fullPath;
		_error.Visible = false;
		_metadata.Text = $"Capture: {fullPath}\nDuration: {report.ElapsedSeconds.ToString( "0.######", CultureInfo.InvariantCulture )} s  |  Active at export: {report.Active}  |  Stopwatch frequency: {report.StopwatchFrequency:N0} Hz  |  Dropped detail samples: {report.DroppedDetailSamples:N0}";
		_warning.Text = report.DroppedDetailSamples > 0
			? "Partial detail: samples were dropped. Detail sums and shares are incomplete; authoritative totals remain the source of truth."
			: "";
		_warning.Visible = report.DroppedDetailSamples > 0;

		_totals.Clear();
		_totals.SetItems( report.Totals.Select( entry => CreateRow( entry.Metric, $"Authoritative total: {entry.Metric}\nUnit: {DisplayUnit( entry.Unit )}. This total is independent of all detail filters.", report.DisplayValue( entry ), entry.Count, entry.Unit, false ) ) );
		PopulateFilters();
		RefreshDetails();
	}

	void PopulateFilters()
	{
		_updatingFilters = true;
		try
		{
			var metrics = _report.Totals.Concat( _report.Details ).Select( entry => (entry.Metric, entry.Unit) ).Distinct().ToList();
			var selectedMetric = metrics.FindIndex( item => item.Metric == _metric && item.Unit == _unit );
			if ( selectedMetric < 0 ) selectedMetric = 0;
			_metricFilter.Clear();
			for ( var index = 0; index < metrics.Count; index++ )
			{
				var metric = metrics[index];
				_metricFilter.AddItem( $"{metric.Metric} ({DisplayUnit( metric.Unit )})", onSelected: () =>
				{
					if ( _updatingFilters ) return;
					_metric = metric.Metric;
					_unit = metric.Unit;
					RefreshDetails();
				} );
			}
			_metric = metrics.Count > 0 ? metrics[selectedMetric].Metric : null;
			_unit = metrics.Count > 0 ? metrics[selectedMetric].Unit : null;
			if ( metrics.Count > 0 ) _metricFilter.CurrentIndex = selectedMetric;

			var connections = _report.Details.Select( entry => entry.Connection ).Where( id => id != Guid.Empty ).Distinct().OrderBy( id => id ).ToList();
			if ( _connection is Guid selected && selected != Guid.Empty && !connections.Contains( selected ) ) _connection = null;
			_connectionFilter.Clear();
			_connectionFilter.AddItem( "All connections (including shared)", onSelected: () => SetConnection( null ) );
			_connectionFilter.AddItem( "Unscoped / shared", onSelected: () => SetConnection( Guid.Empty ) );
			foreach ( var connection in connections )
			{
				_connectionFilter.AddItem( connection.ToString(), onSelected: () => SetConnection( connection ) );
			}
			_connectionFilter.CurrentIndex = _connection is null ? 0 : _connection == Guid.Empty ? 1 : connections.IndexOf( _connection.Value ) + 2;
		}
		finally
		{
			_updatingFilters = false;
		}
	}

	void SetConnection( Guid? connection )
	{
		if ( _updatingFilters ) return;
		_connection = connection;
		RefreshDetails();
	}

	void RefreshDetails()
	{
		if ( _report is null || _updatingFilters ) return;
		_globalTotal = default;
		foreach ( var total in _report.Totals )
		{
			if ( total.Metric == _metric && total.Unit == _unit )
			{
				_globalTotal = _report.DisplayValue( total );
				break;
			}
		}

		var entries = _report.Details.Where( entry => entry.Metric == _metric && entry.Unit == _unit
			&& (_connection is null || entry.Connection == _connection)
			&& MatchesSearch( entry ) ).ToList();
		var rows = new List<Row>();
		if ( _grouping == Grouping.Details )
		{
			foreach ( var entry in entries )
			{
				var identity = $"{ShortId( entry.Connection )} / {ShortId( entry.ObjectId )} / v{entry.Version} / slot {entry.Slot}";
				var selection = $"Metric: {entry.Metric}\nConnection: {FullId( entry.Connection )}\nObject: {FullId( entry.ObjectId )}  |  Version: {entry.Version}  |  Numeric slot: {entry.Slot}";
				rows.Add( CreateRow( identity, selection, _report.DisplayValue( entry ), entry.Count, entry.Unit, true ) );
			}
		}
		else
		{
			foreach ( var group in entries.GroupBy( GroupFor ) )
			{
				var key = group.Key;
				var identity = _grouping switch
				{
					Grouping.Objects => $"Object {ShortId( key.ObjectId )}",
					Grouping.Fields => $"Object {ShortId( key.ObjectId )} / v{key.Version} / slot {key.Slot}",
					_ => $"Connection {ShortId( key.Connection )}"
				};
				var selection = _grouping switch
				{
					Grouping.Objects => $"Object: {FullId( key.ObjectId )}\nConnections, versions and slots aggregated within the current filters.",
					Grouping.Fields => $"Object: {FullId( key.ObjectId )}  |  Version: {key.Version}  |  Numeric slot: {key.Slot}\nConnections aggregated within the current filters.",
					_ => $"Connection: {FullId( key.Connection )}\nObjects, versions and slots aggregated within the current filters."
				};
				rows.Add( CreateRow( identity, $"Metric: {_metric}\n{selection}\nSource detail rows: {group.Count():N0}. Choose Detail rows to inspect individual identities.", group.Sum( entry => _report.DisplayValue( entry ) ), group.Sum( entry => (decimal)entry.Count ), _unit, true ) );
			}
		}

		rows.Sort( CompareRows );
		_details.Clear();
		_details.SetItems( rows );
		var sum = entries.Sum( entry => _report.DisplayValue( entry ) );
		var count = entries.Sum( entry => (decimal)entry.Count );
		_summary.Text = $"{_metric ?? "No metrics"}: {rows.Count:N0} displayed rows from {entries.Count:N0} / {_report.Details.Count:N0} captured detail rows  |  Filtered sum: {FormatValue( sum, _unit )}  |  Count: {count:N0}  |  Average / sample: {Average( sum, count, _unit )}  |  Global share: {Share( sum )}";
		_empty.Text = _report.Totals.Count == 0 && _report.Details.Count == 0 ? "Empty capture: no metric totals or detail rows."
			: _report.Details.Count == 0 ? "This capture has no detail rows. Authoritative totals are shown above."
			: "No detail rows match the selected metric, connection and search.";
		_empty.Visible = rows.Count == 0;
		_selection.Text = "Select a row to inspect full GUIDs, version and numeric slot. Shared/unscoped is not a real peer or object. Counts and averages describe samples of the selected metric.";
	}

	GroupKey GroupFor( SnapshotProfileReport.Entry entry ) => _grouping switch
	{
		Grouping.Objects => new( Guid.Empty, entry.ObjectId, 0, 0 ),
		Grouping.Fields => new( Guid.Empty, entry.ObjectId, entry.Version, entry.Slot ),
		_ => new( entry.Connection, Guid.Empty, 0, 0 )
	};

	bool MatchesSearch( SnapshotProfileReport.Entry entry )
	{
		if ( _search.Length == 0 ) return true;
		return entry.ObjectId.ToString().Contains( _search, StringComparison.OrdinalIgnoreCase )
			|| entry.Connection.ToString().Contains( _search, StringComparison.OrdinalIgnoreCase )
			|| entry.Slot.ToString( CultureInfo.InvariantCulture ).Contains( _search, StringComparison.OrdinalIgnoreCase )
			|| ((entry.ObjectId == Guid.Empty || entry.Connection == Guid.Empty) && "unscoped shared".Contains( _search, StringComparison.OrdinalIgnoreCase ));
	}

	int CompareRows( Row left, Row right )
	{
		var result = _sortColumn switch
		{
			SortColumn.Count => left.Count.CompareTo( right.Count ),
			SortColumn.Identity => StringComparer.OrdinalIgnoreCase.Compare( left.Identity, right.Identity ),
			_ => left.Value.CompareTo( right.Value )
		};
		if ( result == 0 ) result = StringComparer.OrdinalIgnoreCase.Compare( left.Selection, right.Selection );
		return _descending ? -Math.Sign( result ) : result;
	}

	Row CreateRow( string identity, string selection, double value, decimal count, string unit, bool detail ) => new()
	{
		Identity = identity,
		Value = value,
		Count = count,
		ValueText = FormatValue( value, unit ),
		CountText = count.ToString( "N0", CultureInfo.InvariantCulture ),
		AverageText = Average( value, count, unit ),
		ShareText = detail ? Share( value ) : "—",
		Selection = $"{selection}\nValue: {FormatValue( value, unit )}  |  Count: {count:N0}  |  Average / sample: {Average( value, count, unit )}" + (detail ? $"  |  Global share: {Share( value )}" : "")
	};

	string Share( double value ) => _globalTotal is > 0
		? (value / _globalTotal.Value).ToString( "P2", CultureInfo.InvariantCulture )
		: "n/a";

	static string Average( double value, decimal count, string unit ) => count > 0 ? FormatValue( value / (double)count, unit, true ) : "n/a";
	static string DisplayUnit( string unit ) => unit == "stopwatch_ticks" ? "ms" : unit ?? "—";
	static string FormatValue( double value, string unit, bool average = false ) => $"{value.ToString( average || unit == "stopwatch_ticks" ? "N3" : "N0", CultureInfo.InvariantCulture )} {DisplayUnit( unit )}";
	static string ShortId( Guid id ) => id == Guid.Empty ? "shared" : id.ToString()[..8];
	static string FullId( Guid id ) => id == Guid.Empty ? $"{id} (unscoped / shared, not a real identity)" : id.ToString();

	void ShowSelection( object item )
	{
		if ( item is Row row ) _selection.Text = row.Selection;
	}

	static void PaintRow( VirtualWidget widget )
	{
		if ( widget.Object is not Row row ) return;
		Paint.ClearPen();
		Paint.SetBrush( Paint.HasSelected ? Theme.Primary : Paint.HasMouseOver ? Theme.SurfaceLightBackground : widget.Row % 2 == 0 ? Theme.ControlBackground : Theme.WidgetBackground );
		Paint.DrawRect( widget.Rect );
		Paint.SetPen( Paint.HasSelected ? Theme.TextSelected : Theme.Text );
		Paint.SetDefaultFont( 8 );
		DrawColumns( widget.Rect.Shrink( 8, 0 ), row.Identity, row.ValueText, row.CountText, row.AverageText, row.ShareText );
	}

	static void DrawColumns( Rect rect, string identity, string value, string count, string average, string share )
	{
		var width = rect.Width;
		Paint.DrawText( new Rect( rect.Left, rect.Top, width * 0.42f - 8, rect.Height ), identity, TextFlag.LeftCenter | TextFlag.SingleLine );
		Paint.DrawText( new Rect( rect.Left + width * 0.42f, rect.Top, width * 0.19f - 8, rect.Height ), value, TextFlag.RightCenter | TextFlag.SingleLine );
		Paint.DrawText( new Rect( rect.Left + width * 0.61f, rect.Top, width * 0.10f - 8, rect.Height ), count, TextFlag.RightCenter | TextFlag.SingleLine );
		Paint.DrawText( new Rect( rect.Left + width * 0.71f, rect.Top, width * 0.17f - 8, rect.Height ), average, TextFlag.RightCenter | TextFlag.SingleLine );
		Paint.DrawText( new Rect( rect.Left + width * 0.88f, rect.Top, width * 0.12f, rect.Height ), share, TextFlag.RightCenter | TextFlag.SingleLine );
	}
}
