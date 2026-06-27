namespace Sandbox;

public abstract partial class Connection
{
    internal Dictionary<int, RpcRateLimiter> RpcMethodRateLimits = new();
}

internal struct RpcRateLimiter
{
    public float Current;
    public double LastTime;

    public bool Throttle()
    {
        var now = RealTime.NowDouble;

        if ( LastTime == 0 )
        {
            LastTime = now;
        }

        var delta = now - LastTime;
        LastTime = now;

        Current += (float)delta * Rpc.rpc_limit_hz;

        if ( Current < 1 )
            return false;

        Current -= 1;
        return true;
    }
}
