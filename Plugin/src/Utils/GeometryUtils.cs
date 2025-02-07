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
    
    public static T GetMedianFromPoint<T>(this IEnumerable<T> self, Vector2 point, Func<T,Vector2, float> distanceFunc)
    {
        var distanceComparer = Comparer<Tuple<T, float>>.Create((t1,t2) => t1.Item2.CompareTo(t2.Item2));
        var values = ListPool<Tuple<T,float>>.Get();

        foreach (var curr in self)
        {
            var distance = distanceFunc.Invoke(curr, point);
            values.AddOrdered(new Tuple<T, float>(curr,distance), distanceComparer);
        }

        var mid = values.Count /  2;
        var ret = values[mid].Item1;
        
        ListPool<Tuple<T,float>>.Release(values);

        return ret;
    }
}