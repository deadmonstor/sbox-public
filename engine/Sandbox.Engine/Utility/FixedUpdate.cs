namespace Sandbox;

internal class FixedUpdate
{
	/// <summary>
	/// How many times a second FixedUpdate runs.
	/// </summary>
	public float Frequency = 16;

	public double Delta => 1d / Frequency;

	/// <summary>
	/// Accumulate frame time up until a maximum amount (maxSteps). While this value
	/// is above the <see cref="Delta"/> time we will invoke a fixed update.
	/// </summary>
	private long _step;

	internal void Run( Action fixedUpdate, double time, int maxSteps )
	{
		var delta = Delta;
		long curStep = (long)Math.Floor( time / delta );

		if ( maxSteps <= 0 )
		{
			_step = curStep;
			return;
		}

		long stepsBehind = curStep - _step;

		if ( stepsBehind <= 0 )
		{
			// Time hasn't moved forward by a full step - or has gone backwards. Stay in sync.
			_step = curStep;
			return;
		}

		if ( stepsBehind <= maxSteps )
		{
			// Normal case - run fixed-size steps to catch up.
			while ( _step < curStep )
			{
				_step++;
				using var timeScope = Time.Scope( (_step * delta), delta );
				fixedUpdate();
			}

			return;
		}

		// We're too far behind to catch up within maxSteps at the fixed step size. Rather than
		// running maxSteps fixed-size steps and dropping the rest of the backlog (which ties
		// velocity-based movement to render framerate once it happens - see #11373), run exactly
		// maxSteps steps sized so their combined duration covers the full elapsed time. This keeps
		// simulated time accurate at the cost of coarser individual steps while under heavy load.
		var elapsed = time - (_step * delta);
		var stepDelta = elapsed / maxSteps;
		var simTime = _step * delta;

		for ( int i = 0; i < maxSteps; i++ )
		{
			simTime += stepDelta;
			using var timeScope = Time.Scope( simTime, stepDelta );
			fixedUpdate();
		}

		_step = curStep;
	}
}
