using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using UnityEngine;
using UnityEngine.Pool;

namespace QuickItemScan.Utils;

public static class GeometryUtils
{
    public static Vector3 GetMedian(this IEnumerable<Vector3> self)
    {
        var xValues = ListPool<float>.Get();
        var yValues = ListPool<float>.Get();
        var zValues = ListPool<float>.Get();

        foreach (var curr in self)
        {
            xValues.AddOrdered(curr.x);
            yValues.AddOrdered(curr.y);
            zValues.AddOrdered(curr.z);
        }

        var mid = xValues.Count /  2;
        var ret = new Vector3(xValues[mid],yValues[mid],zValues[mid]);
        
        ListPool<float>.Release(xValues);
        ListPool<float>.Release(yValues);
        ListPool<float>.Release(zValues);

        return ret;
    }
    
    private static readonly Comparer<Tuple<Vector2, float>> DistanceComparer = Comparer<Tuple<Vector2, float>>.Create((t1,t2) => t1.Item2.CompareTo(t2.Item2));
    
    public static Vector2 GetMedianFromPoint(this IEnumerable<Vector2> self, Vector2 point)
    {
        var values = ListPool<Tuple<Vector2,float>>.Get();

        foreach (var curr in self)
        {
            var distance = Vector3.Distance(curr, point);
            values.AddOrdered(new Tuple<Vector2, float>(curr,distance), DistanceComparer);
        }

        var mid = values.Count /  2;
        var ret = values[mid].Item1;
        
        ListPool<Tuple<Vector2,float>>.Release(values);

        return ret;
    }
}