using Sandbox;
using Sandbox.UI;
using System;
using System.Collections.Generic;
using System.Linq;

public sealed class HiddenSubtreePanel : PanelComponent
{
	static readonly Sandbox.Diagnostics.Logger Log = new( "HiddenSubtree" );
	static readonly int[] Counts = { 0, 2000, 10000, 25000 };
	const int CellsPerGroup = 100;
	const int WarmupFrames = 30;
	const int SampleFrames = 120;
	const int StepFrames = 3;

	/// <summary>
	/// Resize a visible bar every frame so the root layout runs every frame, like any animated HUD.
	/// </summary>
	[Property] public bool ForceLayout { get; set; } = true;

	Panel stash;
	Panel ticker;
	Panel checkList;
	Panel fixtures;
	Label stats;
	Label summary;
	Button forceButton;
	readonly List<Button> countButtons = new();
	int hiddenNodes;
	ulong lastFrame;
	float nextStats;
	readonly Queue<double> samples = new();

	int benchIndex = -1;
	int benchFrames;
	int benchRestoreCount;
	readonly List<double> benchSamples = new();
	readonly List<string> benchResults = new();

	readonly Queue<Action> steps = new();
	int stepWait;
	int passed;
	int failed;
	bool forceBeforeChecks;
	Panel reference, wideReference, toggle, moved, added, host, inner;

	protected override void OnTreeFirstBuilt()
	{
		Panel.AddClass( "hiddensubtree" );
		Panel.AddChild<Label>( "title" ).Text = "Layout: display: none subtrees";
		Panel.AddChild<Label>().Text = "Run on master and on the fix branch, then compare the Benchmark numbers. The Checks should pass on both.";

		var countRow = Panel.AddChild<Panel>( "row" );
		countRow.AddChild<Label>().Text = "Hidden nodes:";
		foreach ( var count in Counts )
		{
			var button = countRow.AddChild<Button>();
			button.Text = count.ToString( "N0" );
			button.AddEventListener( "onclick", () => SetHiddenNodes( count ) );
			countButtons.Add( button );
		}

		var actionRow = Panel.AddChild<Panel>( "row" );
		forceButton = actionRow.AddChild<Button>();
		forceButton.AddEventListener( "onclick", () => ForceLayout = !ForceLayout );
		var bench = actionRow.AddChild<Button>();
		bench.Text = "Benchmark";
		bench.AddEventListener( "onclick", StartBenchmark );
		var check = actionRow.AddChild<Button>();
		check.Text = "Run checks";
		check.AddEventListener( "onclick", StartChecks );

		stats = Panel.AddChild<Label>( "stats" );
		ticker = Panel.AddChild<Panel>( "ticker" );
		summary = Panel.AddChild<Label>( "summary" );
		summary.Text = "Benchmark: not run";
		checkList = Panel.AddChild<Panel>( "checks" );
		fixtures = Panel.AddChild<Panel>( "fixtures" );

		// The large display: none subtree, a sibling of the ticker so every forced layout pass reaches it
		stash = Panel.AddChild<Panel>( "stash" );

		SetHiddenNodes( 10000 );
		StartChecks();
	}

	void SetHiddenNodes( int count )
	{
		stash.DeleteChildren( true );
		for ( int g = 0; g < count / CellsPerGroup; g++ )
		{
			var group = stash.AddChild<Panel>( "group" );
			for ( int i = 0; i < CellsPerGroup; i++ ) group.AddChild<Panel>( "cell" );
		}

		hiddenNodes = count;
		samples.Clear();
		for ( int i = 0; i < Counts.Length; i++ ) countButtons[i].SetClass( "active", Counts[i] == count );
	}

	void StartBenchmark()
	{
		if ( benchIndex >= 0 || steps.Count > 0 ) return;
		ForceLayout = true;
		benchRestoreCount = hiddenNodes;
		benchResults.Clear();
		benchIndex = 0;
		BeginBenchmarkCount();
	}

	void BeginBenchmarkCount()
	{
		SetHiddenNodes( Counts[benchIndex] );
		benchFrames = 0;
		benchSamples.Clear();
		summary.Text = $"Benchmark: measuring {Counts[benchIndex]:N0} hidden nodes...";
	}

	void SampleBenchmark( double layoutMs )
	{
		benchFrames++;
		if ( benchFrames <= WarmupFrames ) return;
		benchSamples.Add( layoutMs );
		if ( benchSamples.Count < SampleFrames ) return;

		benchResults.Add( $"{Counts[benchIndex]:N0} = {benchSamples.Average():0.000} ms" );
		benchIndex++;
		if ( benchIndex < Counts.Length )
		{
			BeginBenchmarkCount();
			return;
		}

		benchIndex = -1;
		summary.Text = $"Benchmark (avg root layout time over {SampleFrames} frames): {string.Join( ", ", benchResults )}";
		Log.Info( summary.Text );
		SetHiddenNodes( benchRestoreCount );
	}

	void StartChecks()
	{
		if ( benchIndex >= 0 || steps.Count > 0 ) return;
		passed = failed = 0;
		checkList.DeleteChildren( true );
		fixtures.DeleteChildren( true );

		// Nothing else dirties the root while the checks run, so only the changes under test trigger layout
		forceBeforeChecks = ForceLayout;
		ForceLayout = false;

		reference = fixtures.AddChild<Panel>( "probe" );
		wideReference = fixtures.AddChild<Panel>( "probe wide" );
		toggle = fixtures.AddChild<Panel>( "probe" );
		var home = fixtures.AddChild<Panel>( "slot" );
		moved = home.AddChild<Panel>( "probe" );
		host = fixtures.AddChild<Panel>( "host hidden" );
		inner = host.AddChild<Panel>( "inner" );

		steps.Enqueue( () => toggle.SetClass( "hidden", true ) );
		steps.Enqueue( () =>
		{
			Expect( "Hidden panel is zero sized", toggle.Box.Rect.Width == 0 && toggle.Box.Rect.Height == 0, toggle.Box.Rect.Size, Vector2.Zero );
			toggle.SetClass( "hidden", false );
		} );
		steps.Enqueue( () =>
		{
			ExpectSameSize( "Shown panel matches reference", toggle, reference );
			toggle.SetClass( "hidden", true );
		} );
		steps.Enqueue( () =>
		{
			Expect( "Hidden again after being shown is zero sized", toggle.Box.Rect.Width == 0 && toggle.Box.Rect.Height == 0, toggle.Box.Rect.Size, Vector2.Zero );
			moved.Parent = inner;
		} );
		steps.Enqueue( () => added = inner.AddChild<Panel>( "probe" ) );
		steps.Enqueue( () => added.SetClass( "wide", true ) );
		steps.Enqueue( () => host.SetClass( "hidden", false ) );
		steps.Enqueue( () =>
		{
			ExpectSameSize( "Panel moved into hidden subtree is laid out when shown", moved, reference );
			ExpectSameSize( "Panel added and resized while hidden is laid out when shown", added, wideReference );
			host.SetClass( "hidden", true );
		} );
		steps.Enqueue( () =>
		{
			Expect( "Subtree hidden again is zero sized", host.Box.Rect.Width == 0 && host.Box.Rect.Height == 0, host.Box.Rect.Size, Vector2.Zero );
			ForceLayout = forceBeforeChecks;
			Log.Info( $"Checks: {passed} passed, {failed} failed" );
		} );
		stepWait = StepFrames;
	}

	void ExpectSameSize( string name, Panel actual, Panel expected )
	{
		var a = actual.Box.Rect.Size;
		var e = expected.Box.Rect.Size;
		Expect( name, MathF.Abs( a.x - e.x ) <= 1 && MathF.Abs( a.y - e.y ) <= 1 && e.x > 0, a, e );
	}

	void Expect( string name, bool ok, Vector2 actual, Vector2 expected )
	{
		if ( ok ) passed++;
		else failed++;
		var line = $"{(ok ? "PASS" : "FAIL")}  {name}  (actual {actual.x:0}x{actual.y:0}, expected {expected.x:0}x{expected.y:0})";
		checkList.AddChild<Label>( ok ? "pass" : "fail" ).Text = line;
		if ( ok ) Log.Info( line );
		else Log.Warning( line );
	}

	protected override void OnUpdate()
	{
		if ( stash is null ) return;

		if ( steps.Count > 0 && --stepWait <= 0 )
		{
			steps.Dequeue()();
			stepWait = StepFrames;
		}

		if ( ForceLayout ) ticker.Style.Width = Length.Percent( Time.Now * 40f % 100f );
		forceButton.Text = $"Force layout every frame: {(ForceLayout ? "on" : "off")}";

		var root = Panel.FindRootPanel();
		if ( root is null ) return;
		var frame = root.Stats;
		if ( frame.RenderCount == 0 || frame.FrameNumber == lastFrame ) return;
		lastFrame = frame.FrameNumber;

		var layoutMs = frame.LayoutTime.TotalMilliseconds;
		samples.Enqueue( layoutMs );
		while ( samples.Count > SampleFrames ) samples.Dequeue();
		if ( benchIndex >= 0 ) SampleBenchmark( layoutMs );

		// Label changes dirty the layout too, so leave the stats alone while the checks run
		if ( steps.Count > 0 || Time.Now < nextStats ) return;
		nextStats = Time.Now + 0.25f;
		stats.Text = $"Root layout: {samples.Average():0.000} ms avg, {samples.Max():0.000} ms max (last {samples.Count} frames) | {hiddenNodes:N0} hidden nodes";
	}
}
