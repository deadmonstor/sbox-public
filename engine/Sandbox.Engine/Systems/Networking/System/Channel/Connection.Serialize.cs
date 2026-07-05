namespace Sandbox;

public abstract partial class Connection
{
	static object BytePack.ISerializer.BytePackRead( ref ByteStream bs, Type targetType )
	{
		var id = bs.Read<Guid>();
		return Find( id );
	}

	static void BytePack.ISerializer.BytePackWrite( object value, ref ByteStream bs )
	{
		if ( value is not Connection connection )
		{
			bs.Write( Guid.Empty );
			return;
		}

		bs.Write( connection.Id );
	}
}
