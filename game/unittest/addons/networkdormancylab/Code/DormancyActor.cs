using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DormancyLab;

public class DormancyActor : Component, Component.INetworkVisible
{
	[Sync] public int Value { get; set; }
	public bool Visible { get; set; } = true;
	public Guid HiddenFrom { get; set; }

	public bool IsVisibleToConnection( Connection connection, in BBox worldBounds )
		=> Visible && connection.Id != HiddenFrom;

	public virtual string DescribeValues() => $"value={Value}";
}

public sealed class QueryActor : DormancyActor
{
	private int _source;
	private GameObject _destroyOnQuery;

	[Sync( SyncFlags.Query )]
	public int QueryValue
	{
		get
		{
			if ( Networking.IsHost && _destroyOnQuery.IsValid() )
			{
				var target = _destroyOnQuery;
				_destroyOnQuery = null;
				target.DestroyImmediate();
			}
			return _source;
		}
		set => _source = value;
	}

	[Sync] public List<int> PlainList { get; set; } = new();
	[Sync] public Dictionary<int, int> PlainDictionary { get; set; } = new();

	public void SetQuerySource( int value ) => _source = value;
	public void DestroyDuringNextQuery( GameObject target ) => _destroyOnQuery = target;

	public override string DescribeValues()
		=> $"{base.DescribeValues()};query={_source};list={string.Join( ",", PlainList )};dict={string.Join( ",", PlainDictionary.OrderBy( x => x.Key ).Select( x => $"{x.Key}:{x.Value}" ) )}";
}

public sealed class ReliableActor : DormancyActor
{
	[Sync] public NetList<int> Items { get; set; } = new();
	[Sync] public NetDictionary<int, int> Lookup { get; set; } = new();

	public override string DescribeValues()
		=> $"{base.DescribeValues()};items={string.Join( ",", Items )};lookup={string.Join( ",", Lookup.OrderBy( x => x.Key ).Select( x => $"{x.Key}:{x.Value}" ) )}";
}
