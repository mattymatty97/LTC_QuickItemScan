using System;
using System.Buffers;
using System.Collections.Generic;
using UnityEngine.Pool;

namespace QuickItemScan.Utils;

public class DBSCAN
{
    public class DisposableScan : IDisposable
    {
        protected internal DisposableScan(Action disposeAction)
        {
            this._disposeAction = disposeAction;
        }

        private readonly Action _disposeAction;
        public void Dispose()
        {
            _disposeAction();
        }
    }
    
    
    public static DisposableScan ParseClusters<T>(List<T> list, Func<T, T, float> distanceFunction, float maxDistance, int minPoints, out List<List<T>> clusters, out List<T> outliers) where T : class
    {
        var finalClusters = clusters = ListPool<List<T>>.Get();
        var finalOutliers = outliers = ListPool<T>.Get();

        var disposable = new DisposableScan(() =>
        {
            ListPool<T>.Release(finalOutliers);
            foreach (var cluster in finalClusters)
            {
                ListPool<T>.Release(cluster);
            }
            ListPool<List<T>>.Release(finalClusters);
        });
        
        var count = list.Count;

        if (count <= 0)
            return disposable;

        using (DictionaryPool<T, bool>.Get(out var pointDict))
        {
            using (ListPool<T>.Get(out var members))
            {
                for (var i = 0; i < list.Count ; i++)
                {
                    var point = list[i];
                    if(pointDict.ContainsKey(point))
                        continue;

                    GetNeighbours(point, members);
                    if(QuickItemScan.PluginConfig.Verbose.Value)
                        QuickItemScan.Log.LogDebug($"{point} has {members.Count} neighbours");
                    if (members.Count < (minPoints - 1))
                    {
                        pointDict[point] = false;
                        continue;
                    }

                    pointDict[point] = true;

                    for (var j = 0; j < members.Count; j++)
                    {
                        pointDict[members[j]] = true;
                    }
                    
                    for (var j = 0; j < members.Count; j++)
                    {
                        using (ListPool<T>.Get(out var members2))
                        {
                            GetNeighbours(members[j], members2);
                            if(QuickItemScan.PluginConfig.Verbose.Value)
                                QuickItemScan.Log.LogDebug($"{point} -> {members[j]} has {members2.Count} neighbours");
                            if (members2.Count >= (minPoints - 1 ))
                            {
                                for (var k = 0; k < members2.Count; k++)
                                {
                                    pointDict[members2[k]] = true;
                                }
                                members.AddRange(members2);   
                            }
                        }    
                    }
                    members.Add(point);
                    var cluster = ListPool<T>.Get();
                    cluster.AddRange(members);
                    clusters.Add(cluster);
                }
                
            }

            foreach (var (point, hasCluster) in pointDict)
            {
                if (hasCluster)
                    continue;
                outliers.Add(point);
            }
            
            void GetNeighbours(T point, List<T> members)
            {
                members.Clear();
                for (var i = 0; i < list.Count; i++)
                {
                    var curr = list[i];
                    
                    if (curr == point)
                        continue;
            
                    if (pointDict.TryGetValue(curr, out var hasCluster) && hasCluster)
                        continue;
            
                    var distance = distanceFunction(point, curr);
                    
                    if(QuickItemScan.PluginConfig.Verbose.Value)
                        QuickItemScan.Log.LogDebug($"{point} -> {curr} : {distance}");
   
                    if (distance <= maxDistance)
                    {
                        if(QuickItemScan.PluginConfig.Verbose.Value)
                            QuickItemScan.Log.LogDebug($"{curr} is neighbour");
                        members.Add(curr);
                    }
                }
            }
        }
        
        return disposable;
    }
}