using System;
using System.Collections.Generic;
using Sandbox.Internal;
using Sandbox.Network;

namespace SceneTests.GameObjects;

using static GlobalGameNamespace;

/// <summary>
/// Tests shared prediction timelines and networked participants.
/// </summary>
[TestClass]
public class ReusablePredictionTest
{
	TypeLibrary previousTypeLibrary;

	/// <summary>
	/// Registers the engine and test types for prediction scenes.
	/// </summary>
	[TestInitialize]
	public void TestInitialize()
	{
		previousTypeLibrary = Game.TypeLibrary;
		Game.TypeLibrary = new TypeLibrary();
		Game.TypeLibrary.AddAssembly( typeof( PrefabFile ).Assembly, false );
		Game.TypeLibrary.AddAssembly( typeof( ModelRenderer ).Assembly, false );
		Game.TypeLibrary.AddAssembly( typeof( ReusablePredictionTest ).Assembly, false );
		JsonUpgrader.UpdateUpgraders( Game.TypeLibrary );
	}

	/// <summary>
	/// Restores the previous type library.
	/// </summary>
	[TestCleanup]
	public void TestCleanup() => Game.TypeLibrary = previousTypeLibrary;

	/// <summary>
	/// Accumulates input and counts first-pass effects.
	/// </summary>
	[Expose]
	public sealed class Mover : PredictionParticipant<int, int>
	{
		/// <summary>
		/// Accumulated simulated input.
		/// </summary>
		public int Value { get; private set; }

		/// <summary>
		/// Number of first-pass authority effects.
		/// </summary>
		public int AuthorityEffects { get; private set; }

		/// <summary>
		/// Number of first-pass predicted effects.
		/// </summary>
		public int PredictedEffects { get; private set; }

		/// <summary>
		/// Whether validation rejects captured input.
		/// </summary>
		public bool RejectInput { get; set; }

		protected override int CaptureInput() => 1;

		protected override int CaptureState() => Value;

		protected override void RestoreState( in int state )
		{
			Value = state;
			WorldPosition = Vector3.Forward * Value;
		}

		protected override bool ValidateInput( ref int input )
		{
			input = 1;

			return !RejectInput;
		}

		protected override void Simulate( in int input, in PredictionContext context )
		{
			Value += input;
			WorldPosition = Vector3.Forward * Value;
		}

		protected override void AfterSimulate( in int input, in PredictionContext context )
		{
			if ( context.IsAuthority )
			{
				AuthorityEffects++;
			}
			else
			{
				PredictedEffects++;
			}
		}
	}

	/// <summary>
	/// Mirrors a mover after its dependency has simulated.
	/// </summary>
	[Expose]
	public sealed class Dependent : PredictionParticipant<int, int>
	{
		/// <summary>
		/// The dependent value derived from the mover.
		/// </summary>
		public int Value { get; private set; }

		/// <summary>
		/// Numeric order overridden by the mover dependency.
		/// </summary>
		public override int PredictionOrder => -1000;

		/// <summary>
		/// The mover that must simulate first.
		/// </summary>
		public override IEnumerable<Component> PredictionDependencies => new[] { GetComponent<Mover>() };

		protected override int CaptureInput() => 0;

		protected override int CaptureState() => Value;

		protected override void RestoreState( in int state ) => Value = state;

		protected override void Simulate( in int input, in PredictionContext context ) => Value = GetComponent<Mover>().Value * 2;

		protected override void OnCorrected( in int previous, in int corrected )
			=> Assert.AreEqual( GetComponent<Mover>().Value * 2, corrected, "Correction callbacks must observe a coherent reconciled root." );
	}

	static DeltaSnapshot Snapshot( GameObject go )
	{
		IDeltaSnapshot source = go._net;
		var result = new DeltaSnapshot();
		result.CopyFrom( source, source.WriteSnapshotState(), 2 );

		return result;
	}

	/// <summary>
	/// Typed participants share transport, replay, and acknowledgement.
	/// </summary>
	[TestMethod]
	public void TwoTypedParticipantsShareTransportReplayAndAcknowledgement()
	{
		var hostScene = new Scene();
		var clientScene = new Scene();
		using var peers = new ClientAndHost( TypeLibrary );

		peers.Host.Messages.Clear();
		peers.Client.Messages.Clear();

		Mover host;
		Dependent hostDependent;
		DeltaSnapshot latest;

		using ( hostScene.Push() )
		{
			peers.BecomeHost();
			var root = new GameObject();
			host = root.Components.Create<Mover>();
			hostDependent = root.Components.Create<Dependent>();
			root.NetworkSpawn( peers.Client );
			using ( Time.PushScope( 0.02, 0.02 ) )
			{
				hostScene.InternalFixedUpdate();
			}

			latest = Snapshot( root );
		}

		Mover client;
		Dependent clientDependent;

		using ( clientScene.Push() )
		{
			peers.BecomeClient();
			peers.ProcessMessages();
			peers.Client.Messages.Clear();
			client = clientScene.GetComponentInChildren<Mover>();
			clientDependent = clientScene.GetComponentInChildren<Dependent>();
			Assert.IsNotNull( client );
			Assert.IsNotNull( clientDependent );
			Assert.AreEqual( host.Prediction.Id, client.Prediction.Id, "Every peer must use the host-created transport component." );
		}

		for ( var tick = 2; tick < 60; tick++ )
		{
			using ( clientScene.Push() )
			{
				peers.BecomeClient();
				if ( tick % 3 == 2 )
				{
					((IDeltaSnapshot)client.GameObject._net).OnSnapshot( peers.Host, latest );
				}

				using ( Time.PushScope( tick * 0.02, 0.02 ) )
				{
					clientScene.InternalFixedUpdate();
				}

				Assert.AreEqual( client.Value * 2, clientDependent.Value );
				Assert.AreEqual( Vector3.Forward * client.Value, client.WorldPosition,
					"The predicting owner must be able to apply its simulated transform." );
			}

			using ( hostScene.Push() )
			{
				peers.BecomeHost();
				if ( tick % 4 == 0 )
				{
					peers.Host.Messages.Clear();
				}

				if ( tick % 3 == 0 )
				{
					peers.ProcessMessages();
					peers.Host.Messages.Clear();
				}

				using ( Time.PushScope( tick * 0.02, 0.02 ) )
				{
					hostScene.InternalFixedUpdate();
				}

				latest = Snapshot( host.GameObject );
				Assert.AreEqual( host.Value * 2, hostDependent.Value );
			}
		}

		Assert.IsTrue( host.Value > 40, "The second consumer must run via engine command transport." );
		Assert.AreEqual( (int)host.Prediction.AcknowledgedCommand, host.Value );
		Assert.AreEqual( host.Value, host.AuthorityEffects, "Retransmission must never duplicate first-pass host effects." );
		Assert.AreEqual( (int)client.Prediction.Command, client.PredictedEffects, "Replay must never repeat local effects." );
		Assert.AreEqual( host.Value + (int)unchecked(client.Prediction.Command - host.Prediction.AcknowledgedCommand), client.Value );
		Assert.AreSame( client.Prediction, clientDependent.Prediction );

		int disabledValue = 0;

		for ( var tick = 60; tick < 92; tick++ )
		{
			using ( hostScene.Push() )
			{
				peers.BecomeHost();

				if ( tick == 60 )
				{
					hostDependent.Enabled = false;
				}

				if ( tick == 75 )
				{
					hostDependent.Enabled = true;
				}

				peers.ProcessMessages();
				peers.Host.Messages.Clear();

				using ( Time.PushScope( tick * 0.02, 0.02 ) )
				{
					hostScene.InternalFixedUpdate();
				}

				latest = Snapshot( host.GameObject );
			}

			using ( clientScene.Push() )
			{
				peers.BecomeClient();
				peers.ProcessMessages(); // Enrollment changes must replicate without a custom consumer RPC.
				peers.Client.Messages.Clear();
				((IDeltaSnapshot)client.GameObject._net).OnSnapshot( peers.Host, latest );

				using ( Time.PushScope( tick * 0.02, 0.02 ) )
				{
					clientScene.InternalFixedUpdate();
				}

				Assert.AreEqual( tick >= 75, clientDependent.Enabled );

				if ( tick == 60 )
				{
					disabledValue = clientDependent.Value;
				}

				if ( tick < 75 )
				{
					Assert.AreEqual( disabledValue, clientDependent.Value, "Disabled participants must not replay or simulate." );
				}
				else
				{
					Assert.AreEqual( client.Value * 2, clientDependent.Value, "Re-enrollment must restore a coherent root." );
				}
			}
		}

		Assert.AreEqual( host.Value, host.AuthorityEffects );
		Assert.AreEqual( host.Value + (int)unchecked(client.Prediction.Command - host.Prediction.AcknowledgedCommand), client.Value );
	}

	/// <summary>
	/// Reset invalidates command times and releases transform authority.
	/// </summary>
	[TestMethod]
	public void ResetInvalidatesCommandTokensAndReleasesOwnedTransformAuthority()
	{
		var scene = new Scene();
		using var scope = scene.Push();
		using var peers = new ClientAndHost( TypeLibrary );
		peers.BecomeHost();
		var root = new GameObject();
		var mover = root.Components.Create<Mover>();
		root.NetworkSpawn( peers.Host );
		Assert.IsFalse( scene.HasPhysicsWorld, "Value-only prediction must not require a native physics world." );

		using ( Time.PushScope( 0.02, 0.02 ) )
		{
			scene.InternalFixedUpdate();
		}

		Assert.IsFalse( scene.HasPhysicsWorld, "Completing prediction must not manufacture a physics world." );
		var runner = mover.Prediction;
		var epoch = runner.Epoch;
		Assert.IsTrue( runner.TryGetCommandTime( epoch, runner.AcknowledgedCommand, out var token ) );
		Assert.IsTrue( token.IsValidFor( scene ) );
		Assert.IsFalse( runner.TryGetCommandTime( epoch, unchecked(runner.AcknowledgedCommand + 1), out _ ) );

		// A GUI/RPC frame delta is not a simulation tick.
		using ( Time.PushScope( 0.04, 0.007 ) )
		{
			runner.ResetTimeline();
		}

		Assert.AreNotEqual( epoch, runner.Epoch );
		Assert.IsFalse( token.IsValidFor( scene ) );
		Assert.IsFalse( runner.TryGetCommandTime( epoch, token.Command, out _ ) );
		mover.Enabled = false;

		using ( Time.PushScope( 0.06, 0.02 ) )
		{
			scene.InternalFixedUpdate();
		}

		Assert.AreEqual( NetworkFlags.None, root.Network.Flags & NetworkFlags.HostTransformAuthority );
	}

	/// <summary>
	/// Resets the shared timeline during its first effect callback.
	/// </summary>
	[Expose]
	public sealed class ResettingAction : PredictionParticipant<int, int>
	{
		/// <summary>
		/// Epoch observed before the timeline reset.
		/// </summary>
		public Guid ExecutedEpoch { get; private set; }

		/// <summary>
		/// Command observed before the timeline reset.
		/// </summary>
		public uint ExecutedCommand { get; private set; }

		bool reset;
		int value;

		protected override int CaptureInput() => 1;

		protected override int CaptureState() => value;

		protected override void RestoreState( in int state ) => value = state;

		protected override void Simulate( in int input, in PredictionContext context ) => value += input;

		protected override void AfterSimulate( in int input, in PredictionContext context )
		{
			if ( reset )
			{
				return;
			}

			reset = true;
			ExecutedEpoch = context.Epoch;
			ExecutedCommand = context.Command;
			Prediction.ResetTimeline();
		}
	}

	/// <summary>
	/// Counts effects after the resetting participant.
	/// </summary>
	[Expose]
	public sealed class LaterAction : PredictionParticipant<int, int>
	{
		/// <summary>
		/// Number of first-pass effects.
		/// </summary>
		public int Effects { get; private set; }

		/// <summary>
		/// The resetting participant that must simulate first.
		/// </summary>
		public override IEnumerable<Component> PredictionDependencies => new[] { GetComponent<ResettingAction>() };

		protected override int CaptureInput() => 0;

		protected override int CaptureState() => 0;

		protected override void RestoreState( in int state )
		{
		}

		protected override void Simulate( in int input, in PredictionContext context )
		{
		}

		protected override void AfterSimulate( in int input, in PredictionContext context ) => Effects++;
	}

	/// <summary>
	/// Reset during effects aborts the old timeline's remaining work.
	/// </summary>
	[TestMethod]
	public void ResetDuringAfterSimulateAbortsOldEffectsAcknowledgementAndTokens()
	{
		var scene = new Scene();
		using var scope = scene.Push();
		using var peers = new ClientAndHost( TypeLibrary );
		peers.BecomeHost();
		var root = new GameObject();
		var action = root.Components.Create<ResettingAction>();
		var later = root.Components.Create<LaterAction>();
		root.NetworkSpawn( peers.Host );

		using ( Time.PushScope( 0.02, 0.02 ) )
		{
			scene.InternalFixedUpdate();
		}

		var runner = action.Prediction;
		Assert.AreNotEqual( action.ExecutedEpoch, runner.Epoch );
		Assert.AreEqual( 0u, runner.AcknowledgedCommand );
		Assert.AreEqual( 0u, runner.Command );
		Assert.AreEqual( 0, later.Effects );
		Assert.IsFalse( runner.TryGetCommandTime( action.ExecutedEpoch, action.ExecutedCommand, out _ ) );
		Assert.IsFalse( runner.TryGetCommandTime( runner.Epoch, 0, out _ ) );

		using ( Time.PushScope( 0.04, 0.02 ) )
		{
			scene.InternalFixedUpdate();
		}

		Assert.AreEqual( 1u, runner.AcknowledgedCommand );
		Assert.AreEqual( 1, later.Effects );
		Assert.IsTrue( runner.TryGetCommandTime( runner.Epoch, 1, out _ ) );
		Assert.AreEqual( 1f / ProjectSettings.Physics.FixedUpdateFrequency, runner.Context.Delta );
	}

	/// <summary>
	/// Host configuration changes invalidate existing command times.
	/// </summary>
	[TestMethod]
	public void HostConfigurationChangesInvalidateTokensBeforeTheNextTick()
	{
		var scene = new Scene();
		using var scope = scene.Push();
		using var peers = new ClientAndHost( TypeLibrary );
		peers.BecomeHost();
		var mover = new GameObject().Components.Create<Mover>();
		mover.GameObject.NetworkSpawn( peers.Host );

		using ( Time.PushScope( 0.02, 0.02 ) )
		{
			scene.InternalFixedUpdate();
		}

		var runner = mover.Prediction;
		var epoch = runner.Epoch;
		Assert.IsTrue( runner.TryGetCommandTime( epoch, runner.AcknowledgedCommand, out var token ) );
		runner.InterpolationDelay = 0.03f;
		Assert.IsFalse( token.IsValidFor( scene ) );

		using ( Time.PushScope( 0.04, 0.02 ) )
		{
			scene.InternalFixedUpdate();
		}

		Assert.AreNotEqual( epoch, runner.Epoch );
		Assert.IsTrue( runner.Context.CommandTime.InterpolationDelay <= 0.03f );
	}

	/// <summary>
	/// Remote commands bound interpolation and advance despite rejected input.
	/// </summary>
	[TestMethod]
	public void RemoteCommandsBoundCaptureInterpolationAndRejectedInputStillAdvances()
	{
		var oldInterpolation = Networking.InterpolationTime;
		var hostScene = new Scene();
		var clientScene = new Scene();
		using var peers = new ClientAndHost( TypeLibrary );
		peers.Host.Messages.Clear();
		peers.Client.Messages.Clear();

		try
		{
			Mover host;
			DeltaSnapshot snapshot;

			using ( hostScene.Push() )
			{
				peers.BecomeHost();
				host = new GameObject().Components.Create<Mover>();
				host.Prediction.InterpolationDelay = 0.03f;
				host.GameObject.NetworkSpawn( peers.Client );

				using ( Time.PushScope( 0.02, 0.02 ) )
				{
					hostScene.InternalFixedUpdate();
				}

				snapshot = Snapshot( host.GameObject );
			}

			Mover client;

			using ( clientScene.Push() )
			{
				peers.BecomeClient();
				peers.ProcessMessages();
				peers.Client.Messages.Clear();
				client = clientScene.GetComponentInChildren<Mover>();
				((IDeltaSnapshot)client.GameObject._net).OnSnapshot( peers.Host, snapshot );
				Networking.InterpolationTime = 0.02f;

				using ( Time.PushScope( 0.04, 0.02 ) )
				{
					clientScene.InternalFixedUpdate();
				}
			}

			using ( hostScene.Push() )
			{
				peers.BecomeHost();
				Networking.InterpolationTime = 0.09f; // Not the owner's displayed-world delay.
				peers.ProcessMessages();
				peers.Host.Messages.Clear();

				using ( Time.PushScope( 0.04, 0.02 ) )
				{
					hostScene.InternalFixedUpdate();
				}

				Assert.IsTrue( host.Prediction.TryGetCommandTime( host.Prediction.Epoch, 1, out var time ) );
				Assert.AreEqual( 0.02f, time.InterpolationDelay );
				Assert.AreEqual( 1, host.Value );
			}

			using ( clientScene.Push() )
			{
				peers.BecomeClient();
				Networking.InterpolationTime = 0.08f;

				using ( Time.PushScope( 0.06, 0.02 ) )
				{
					clientScene.InternalFixedUpdate();
				}
			}

			using ( hostScene.Push() )
			{
				peers.BecomeHost();
				host.RejectInput = true;
				peers.ProcessMessages();
				peers.Host.Messages.Clear();

				using ( Time.PushScope( 0.06, 0.02 ) )
				{
					hostScene.InternalFixedUpdate();
				}

				Assert.IsTrue( host.Prediction.TryGetCommandTime( host.Prediction.Epoch, 2, out var time ) );
				Assert.AreEqual( 0.03f, time.InterpolationDelay );
				Assert.AreEqual( 2u, host.Prediction.AcknowledgedCommand );
				Assert.AreEqual( 1, host.Value, "Rejected participant input must execute as default without stalling the command timeline." );
			}
		}
		finally
		{
			Networking.InterpolationTime = oldInterpolation;
		}
	}

	/// <summary>
	/// Walking prediction replicates its shared runner before commands execute.
	/// </summary>
	[TestMethod]
	[DataRow( false )]
	[DataRow( true )]
	public void WalkingPredictionReplicatesItsRunnerBeforeCommands( bool enableAfterSpawn )
	{
		var hostScene = new Scene();
		var clientScene = new Scene();
		using var peers = new ClientAndHost( TypeLibrary );
		peers.Host.Messages.Clear();
		peers.Client.Messages.Clear();
		var previousMove = Input.AnalogMove;

		try
		{
			PlayerController host;
			Prediction hostRunner;
			DeltaSnapshot snapshot;

			using ( hostScene.Push() )
			{
				peers.BecomeHost();
				var floor = new GameObject();
				floor.WorldPosition = Vector3.Down * 50;
				var collider = floor.Components.Create<BoxCollider>();
				collider.Scale = new Vector3( 2000, 2000, 100 );
				collider.Static = true;
				floor.NetworkSpawn();

				host = new GameObject().Components.Create<PlayerController>();
				host.WorldPosition = Vector3.Up;
				host.EnableFootstepSounds = false;

				if ( !enableAfterSpawn )
				{
					host.UseClientPrediction = true;
				}

				host.GameObject.NetworkSpawn( peers.Client );

				if ( enableAfterSpawn )
				{
					host.UseClientPrediction = true;
				}

				hostRunner = host.GetComponent<Prediction>();
				Assert.IsNotNull( hostRunner, "Opting in must create a transport component before the next fixed update." );

				using ( Time.PushScope( 0.02, 0.02 ) )
				{
					hostScene.InternalFixedUpdate();
				}

				snapshot = Snapshot( host.GameObject );
			}

			PlayerController client;

			using ( clientScene.Push() )
			{
				peers.BecomeClient();
				peers.ProcessMessages();
				peers.Client.Messages.Clear();
				client = clientScene.GetComponentInChildren<PlayerController>();
				Assert.IsNotNull( client );
				var clientRunner = client.GetComponent<Prediction>();
				Assert.IsNotNull( clientRunner );
				Assert.AreEqual( hostRunner.Id, clientRunner.Id, "RPC and snapshot identities must match even when enabled after spawning." );
			}

			for ( var tick = 2; tick <= 25; tick++ )
			{
				using ( clientScene.Push() )
				{
					peers.BecomeClient();
					((IDeltaSnapshot)client.GameObject._net).OnSnapshot( peers.Host, snapshot );
					Input.AnalogMove = Vector3.Forward;

					using ( Time.PushScope( tick * 0.02, 0.02 ) )
					{
						clientScene.InternalFixedUpdate();
					}

					Assert.IsTrue( client.GetComponent<Prediction>().IsPredictingLocally );
				}

				using ( hostScene.Push() )
				{
					peers.BecomeHost();
					peers.ProcessMessages();
					peers.Host.Messages.Clear();

					using ( Time.PushScope( tick * 0.02, 0.02 ) )
					{
						hostScene.InternalFixedUpdate();
					}

					snapshot = Snapshot( host.GameObject );
				}
			}

			Assert.AreEqual( 24u, hostRunner.AcknowledgedCommand );
			Assert.IsTrue( host.WorldPosition.x > 20, $"The replicated runner must simulate owner walking commands on the host: {host.WorldPosition}" );
			Assert.IsTrue( client.WorldPosition.x > 20, $"The owning client must predict real walking: {client.WorldPosition}" );
			Assert.IsTrue( (host.WorldPosition - client.WorldPosition).Length < 0.1f );
		}
		finally
		{
			Input.AnalogMove = previousMove;
			clientScene.Destroy();
			hostScene.Destroy();
		}
	}

	/// <summary>
	/// Provides missing or cyclic dependencies for rejection tests.
	/// </summary>
	[Expose]
	public sealed class InvalidDependency : PredictionParticipant<int, int>
	{
		/// <summary>
		/// Whether the participant depends on itself.
		/// </summary>
		public bool Cycle { get; set; }

		/// <summary>
		/// A self-reference or an absent mover dependency.
		/// </summary>
		public override IEnumerable<Component> PredictionDependencies => new Component[] { Cycle ? this : GetComponent<Mover>() };

		protected override int CaptureInput() => 0;

		protected override int CaptureState() => 0;

		protected override void RestoreState( in int state )
		{
		}

		protected override void Simulate( in int input, in PredictionContext context ) => Assert.Fail( "Invalid dependencies must not simulate." );
	}

	/// <summary>
	/// Missing and cyclic dependencies are rejected before simulation.
	/// </summary>
	[TestMethod]
	[DataRow( false )]
	[DataRow( true )]
	public void MissingOrCyclicDependenciesAreRejectedBeforeSimulation( bool cycle )
	{
		using var scope = new Scene().Push();
		using var peers = new ClientAndHost( TypeLibrary );
		peers.BecomeHost();
		var participant = new GameObject().Components.Create<InvalidDependency>();
		participant.Cycle = cycle;

		using ( Time.PushScope( 0.02, 0.02 ) )
		{
			Assert.ThrowsException<InvalidOperationException>( () => participant.Prediction.Step() );
		}
	}
}
