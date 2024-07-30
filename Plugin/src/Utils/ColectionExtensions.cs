using System;
using System.Collections.Generic;

namespace QuickItemScan.Utils;

public static class ColectionExtensions
{
    public static void AddOrdered<T>(this List<T> self, T item, IComparer<T> comparer = default)
    {
        var index = self.BinarySearch(item, comparer);
        if (index < 0) index = ~index;
        self.Insert(index, item);
    }
}