namespace Sandbox;

public static partial class Rpc
{
    /// <summary>
    /// Maximum number of RPCs per second per connection.
    /// </summary>
    [ConVar]
    public static int rpc_limit_hz { get; set; } = 25;

    /// <summary>
    /// Should we kick connections that exceed the RPC rate limit?
    /// </summary>
    [ConVar]
    public static bool rpc_limit_kick { get; set; } = false;

    internal static bool RateLimitCheck( Connection source, int methodIdentity )
    {
        if ( source.IsHost ) return true;
        if ( rpc_limit_hz <= 0 ) return true;

        if ( !source.RpcMethodRateLimits.TryGetValue( methodIdentity, out var limiter ) )
        {
            limiter = new RpcRateLimiter();
        }

        if ( !limiter.Throttle() )
        {
            if ( rpc_limit_kick )
            {
                source.Kick( "RPC Rate Limit Exceeded" );
            }

            return false;
        }

        source.RpcMethodRateLimits[methodIdentity] = limiter;
        return true;
    }
}
