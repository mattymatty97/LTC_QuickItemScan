using System.Collections.Generic;

namespace QuickItemScan.Utils;

public static class ListExtensions
{
    public static void AddOrdered<T>(this List<T> self, T item)
    {
        var index = self.BinarySearch(item);
        if (index < 0) index = ~index;
        self.Insert(index, item);
    }
}