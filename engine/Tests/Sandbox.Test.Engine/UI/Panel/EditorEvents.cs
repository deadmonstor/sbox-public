using Editor;
using Sandbox.Engine;
using Sandbox.UI;
using System.Collections.Generic;
using System.Reflection;

namespace UITests;

[TestClass]
[DoNotParallelize]
public class EditorPanelEvents
{
	sealed class HotloadPanel : Panel, ResourceLibrary.IEventListener
	{
		public int Hotloads;
		public int ResourceSaves;
		public int GameEvents;

		public void OnSave( GameResource resource ) => ResourceSaves++;

		[EditorEvent.Hotload]
		void OnEditorHotload() => Hotloads++;

		[Event( "test.panel.registration" )]
		void OnGameEvent() => GameEvents++;
	}

	[TestMethod]
	public void OnlyEditorAssemblyPanelsReceiveEventsUntilDeleted()
	{
		var previousTools = IToolsDll.Current;
		var managerField = typeof( EditorEvent ).GetField( "eventManager", BindingFlags.Static | BindingFlags.NonPublic );
		var assembliesField = typeof( EditorEvent ).GetField( "registeredAssemblies", BindingFlags.Static | BindingFlags.NonPublic );
		var previousManager = managerField.GetValue( null );
		var registeredAssemblies = (HashSet<Assembly>)assembliesField.GetValue( null );
		var previousAssemblies = registeredAssemblies.ToArray();
		using var testManager = new Sandbox.Internal.EventSystem();
		var previousGameManager = GlobalContext.Current.EventSystem;
		using var testGameManager = new Sandbox.Internal.EventSystem();
		// Sandbox.Menu also defines Sandbox.Global, so resolve the tools copy explicitly.
		var assemblyProperty = typeof( ToolsDll ).Assembly.GetType( "Sandbox.Global" )
			.GetProperty( "Assembly", BindingFlags.Static | BindingFlags.NonPublic );
		var previousAssembly = assemblyProperty.GetValue( null );

		try
		{
			managerField.SetValue( null, testManager );
			registeredAssemblies.Clear();
			IToolsDll.Current = new ToolsDll();
			GlobalContext.Current.EventSystem = testGameManager;
			testGameManager.RegisterAssembly( typeof( EditorPanelEvents ).Assembly );
			using var surface = new UISurface();

			// Game/menu assemblies are not enrolled with EditorEvent.
			var gamePanel = new HotloadPanel { Parent = surface.Root };
			EditorEvent.RunInterface<ResourceLibrary.IEventListener>( x => x.OnSave( null ) );
			Assert.AreEqual( 0, gamePanel.ResourceSaves );

			EditorEvent.RegisterAssembly( typeof( EditorPanelEvents ).Assembly );
			var panel = new HotloadPanel { Parent = surface.Root };
			EditorEvent.RunInterface<ResourceLibrary.IEventListener>( x => x.OnSave( null ) );
			Assert.AreEqual( 1, panel.ResourceSaves );
			Assert.AreEqual( 0, gamePanel.ResourceSaves );

			EditorEvent.Run( "hotloaded" );
			Assert.AreEqual( 1, panel.Hotloads );
			Sandbox.Event.Run( "test.panel.registration" );
			Assert.AreEqual( 1, panel.GameEvents );

			panel.Parent = null;
			panel.Parent = surface.Root;
			EditorEvent.Run( "hotloaded" );
			Assert.AreEqual( 2, panel.Hotloads );
			EditorEvent.RunInterface<ResourceLibrary.IEventListener>( x => x.OnSave( null ) );
			Assert.AreEqual( 2, panel.ResourceSaves );
			Sandbox.Event.Run( "test.panel.registration" );
			Assert.AreEqual( 2, panel.GameEvents );

			// Rebuilding assembly event hooks must retain the live panel instance.
			EditorEvent.UnregisterAssembly( typeof( EditorPanelEvents ).Assembly );
			var unloadedPanel = new HotloadPanel { Parent = surface.Root };
			EditorEvent.RunInterface<ResourceLibrary.IEventListener>( x => x.OnSave( null ) );
			Assert.AreEqual( 0, unloadedPanel.ResourceSaves );
			EditorEvent.RegisterAssembly( typeof( EditorPanelEvents ).Assembly );
			EditorEvent.Run( "hotloaded" );
			Assert.AreEqual( 3, panel.Hotloads );

			panel.Delete( true );
			EditorEvent.Run( "hotloaded" );
			Assert.AreEqual( 3, panel.Hotloads );
			var savesAfterDelete = panel.ResourceSaves;
			EditorEvent.RunInterface<ResourceLibrary.IEventListener>( x => x.OnSave( null ) );
			Assert.AreEqual( savesAfterDelete, panel.ResourceSaves );
			Sandbox.Event.Run( "test.panel.registration" );
			Assert.AreEqual( 2, panel.GameEvents );
		}
		finally
		{
			managerField.SetValue( null, previousManager );
			registeredAssemblies.Clear();
			registeredAssemblies.UnionWith( previousAssemblies );
			IToolsDll.Current = previousTools;
			GlobalContext.Current.EventSystem = previousGameManager;
			assemblyProperty.SetValue( null, previousAssembly );
		}
	}
}
