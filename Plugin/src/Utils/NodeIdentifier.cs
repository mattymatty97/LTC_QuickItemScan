using System;
using System.Collections.Generic;

namespace QuickItemScan.Utils;

public readonly struct NodeIdentifier(int type, string name)
{
    public readonly int Type = type;
    public readonly string Name = name;

    private sealed class TypeNameEqualityComparer : IEqualityComparer<NodeIdentifier>
    {
        public bool Equals(NodeIdentifier x, NodeIdentifier y)
        {
            return x.Type == y.Type && x.Name == y.Name;
        }

        public int GetHashCode(NodeIdentifier obj)
        {
            return HashCode.Combine(obj.Type, obj.Name);
        }
    }

    public bool Equals(NodeIdentifier other)
    {
        return Type == other.Type && Name == other.Name;
    }

    public override bool Equals(object obj)
    {
        return obj is NodeIdentifier other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Type, Name);
    }
}