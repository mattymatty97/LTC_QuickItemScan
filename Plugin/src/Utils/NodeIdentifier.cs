using System;
using System.Collections.Generic;

namespace QuickItemScan.Utils;

public readonly struct NodeIdentifier(int type, string name)
{
    public readonly int Type = type;
    public readonly string Name = name;

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
