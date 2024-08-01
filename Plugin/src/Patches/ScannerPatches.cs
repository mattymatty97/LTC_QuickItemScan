using System;
using System.Collections.Generic;
using System.Linq;
using GameNetcodeStuff;
using HarmonyLib;
using MonoMod.RuntimeDetour;
using QuickItemScan.Components;
using QuickItemScan.Dependency;
using QuickItemScan.Utils;
using TMPro;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Pool;
using Object = UnityEngine.Object;

namespace QuickItemScan.Patches;

[HarmonyPatch]
internal class ScannerPatches
{
    private static readonly int ColorNumberHash = Animator.StringToHash("colorNumber");
    private static readonly int DisplayHash = Animator.StringToHash("display");
    private static readonly Vector2 ViewportCenter = new Vector2(0.5f,0.5f);
    //compare by viewport z value ( distance to camera )
    private static readonly IComparer<ScanNodeHandler> ZComparer = Comparer<ScanNodeHandler>.Create((n1, n2) =>
        n1.DisplayData.RectPos.z.CompareTo(n2.DisplayData.RectPos.z));
    //calculate cord distance
    private static readonly Func<ScanNodeHandler, ScanNodeHandler, float> DisplayDistance = (n1, n2) =>
        Vector2.Distance(n1.DisplayData.RectPos, n2.DisplayData.RectPos);
    private static readonly Func<ScanNodeHandler, Vector2, float> ViewportDistance = (n1, v2) =>
        Vector2.Distance(n1.DisplayData.ViewportPos, v2);
    
    //internal counters
    private static float _newNodeInterval;
    private static float _clusterInterval;
    private static int _newNodesToAdd;
    //lobby object cache
    private static RectTransform _screenRect;
    private static GameObject _mainHolder;
    
    //scanElements
    private static RectTransform[] ScanDisplays = [];
    //clusterElements
    private static RectTransform[] ClusterDisplays = [];
    //name of each cluster
    private static string[] ClusterDisplayAssignment = [];
    //nodes assigned to clusters
    private static List<ScanNodeHandler>[] ClusterNodes = [];
    //available scanElements
    private static readonly Queue<int> FreeScanDisplays = [];
    //available clusterElements
    private static readonly Queue<int> FreeClusterDisplays = [];
    //scan nodes in player range
    public static readonly HashSet<ScanNodeHandler> ScannableNodes = new();
    //scan nodes currently on HUD
    private static readonly HashSet<ScanNodeHandler> DisplayedScanNodes = new();
    

    [HarmonyPatch(typeof(ScanNodeProperties), nameof(ScanNodeProperties.Awake))]
    [HarmonyPostfix]
    private static void OnAwake(ScanNodeProperties __instance)
    {
        //create our root object if missing
        if (!_mainHolder)
        {
            if (AsyncLoggerProxy.Enabled)
                AsyncLoggerProxy.WriteEvent(QuickItemScan.NAME, "MainHolder", "New");
            _mainHolder = new GameObject(QuickItemScan.NAME);
        }
        
        //create our tracker object in a separate hierarchy
        //( so we can use SphereColliders without having to care about world scale )
        var gameObject = new GameObject($"{__instance.headerText} - Tracker");
        gameObject.transform.SetParent(_mainHolder.transform, false);
        
        //add out HandlerComponent
        var handler = gameObject.AddComponent<ScanNodeHandler>();
        handler.ScanNode = __instance;
        
        //add the constraint, so it will follow as close as possible the actual SanNode
        var constraint = gameObject.AddComponent<PositionConstraint>();
        constraint.AddSource(new ConstraintSource
        {
            sourceTransform = __instance.transform,
            //weight needs to be non-0
            weight = 1
        });
        constraint.locked = true;
        constraint.constraintActive = true;
    }

    [HarmonyPatch(typeof(HUDManager), nameof(HUDManager.Start))]
    [HarmonyPostfix]
    private static void InitValues(HUDManager __instance)
    {
        //reset all states
        _newNodeInterval = 0f;

        FreeScanDisplays.Clear();
        FreeClusterDisplays.Clear();

        DisplayedScanNodes.Clear();

        var original = __instance.scanElements[0];
        
        //empty this so other mods do not complain
        Array.Resize(ref __instance.scanElements, 0);

        Array.Resize(ref ScanDisplays, QuickItemScan.PluginConfig.Scanner.MaxScanItems.Value);

        for (var i = 0; i < QuickItemScan.PluginConfig.Scanner.MaxScanItems.Value; i++)
        {
            var element = Object.Instantiate(original, original.transform.position, original.transform.rotation,
                original.transform.parent);
            element.transform.name = $"new {original.transform.name}-{i}";
            //mark index as available
            FreeScanDisplays.Enqueue(i);
            ScanDisplays[i] = element;
        }

        Array.Resize(ref ClusterDisplays, QuickItemScan.PluginConfig.Performance.Cluster.NodeCount.Value);
        Array.Resize(ref ClusterDisplayAssignment, QuickItemScan.PluginConfig.Performance.Cluster.NodeCount.Value);
        Array.Resize(ref ClusterNodes, QuickItemScan.PluginConfig.Performance.Cluster.NodeCount.Value);

        for (var i = 0; i < QuickItemScan.PluginConfig.Performance.Cluster.NodeCount.Value; i++)
        {
            var element = Object.Instantiate(original, original.transform.position, original.transform.rotation,
                original.transform.parent);
            element.transform.name = $"cluster {original.transform.name}-{i}";
            //mark index as available
            FreeClusterDisplays.Enqueue(i);
            ClusterDisplays[i] = element;
            ClusterDisplayAssignment[i] = null;
            var nodes = ClusterNodes[i];
            //initialize cluster assignment list
            if (nodes == null) ClusterNodes[i] = nodes = new List<ScanNodeHandler>();

            nodes.Clear();
        }
    }

    [HarmonyPatch(typeof(HUDManager), nameof(HUDManager.DisableAllScanElements))]
    [HarmonyPostfix]
    private static void ClearScans(HUDManager __instance)
    {
        //reset all states
        FreeScanDisplays.Clear();
        for (var index = 0; index < ScanDisplays.Length; index++)
        {
            var element = ScanDisplays[index];
            if(element)
                element.gameObject.SetActive(false);
            FreeScanDisplays.Enqueue(index);
        }

        FreeClusterDisplays.Clear();
        for (var index = 0; index < ClusterDisplays.Length; index++)
        {
            var element = ClusterDisplays[index];
            if(element)
                element.gameObject.SetActive(false);
            FreeClusterDisplays.Enqueue(index);
        }
        
        for (var index = 0; index < ClusterDisplayAssignment.Length; index++)
        {
            ClusterDisplayAssignment[index] = null;
        }

        foreach (var list in ClusterNodes)
        {
            foreach (var node in list)
            {
                node.ClusterData.Index = -1;
                node.ClusterData.IsMaster = false;
                node.ClusterData.HasCluster = false;
                node.ClusterData.Element = null;
            }

            list.Clear();
        }

        foreach (var handler in DisplayedScanNodes)
        {
            handler.DisplayData.IsActive = false;
            handler.DisplayData.TimeLeft = 0;
            handler.DisplayData.Element = null;
            handler.DisplayData.Index = -1;
        }

        DisplayedScanNodes.Clear();

        __instance.totalScrapScanned = 0;
        __instance.totalScrapScannedDisplayNum = 0;
    }

    //create MonoMod Hooks
    internal static void InitMonoMod()
    {
        QuickItemScan.Hooks.Add(
            new Hook(
                AccessTools.Method(typeof(HUDManager), nameof(HUDManager.UpdateScanNodes)),
                RewriteUpdateScan,
                new HookConfig()
                {
                    Priority = -99
                }
                ));
    }
    
    private static void RewriteUpdateScan(Action<HUDManager, PlayerControllerB> orig, HUDManager self, PlayerControllerB controllerB)
    {
        //track how many nodes can be added
        if (_newNodesToAdd == 0)
        {
            _newNodeInterval -= Time.deltaTime;
            if (_newNodeInterval <= 0)
            {
                _newNodesToAdd = QuickItemScan.PluginConfig.Scanner.NewNodeCount.Value;
                _newNodeInterval = QuickItemScan.PluginConfig.Scanner.NewNodeDelay.Value;
            }
        }

        TryAddNewNodes(self);

        UpdateNodesOnScreen(self, out var scannedScrap);

        UpdateClustersOnScreen(self);

        if (!scannedScrap)
        {
            self.totalScrapScanned = 0;
            self.totalScrapScannedDisplayNum = 0;
            self.addToDisplayTotalInterval = 0.35f;
        }

        self.scanInfoAnimator.SetBool(DisplayHash, self.scannedScrapNum >= 2 && scannedScrap);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(HUDManager), nameof(HUDManager.AssignNewNodes))]
    [HarmonyPatch(typeof(HUDManager), nameof(HUDManager.AttemptScanNode))]
    [HarmonyPatch(typeof(HUDManager), nameof(HUDManager.MeetsScanNodeRequirements))]
    [HarmonyPatch(typeof(HUDManager), nameof(HUDManager.AssignNodeToUIElement))]
    private static bool SkipUnusedMethods()
    {
        return false;
    }

    internal static bool CanUpdate()
    {
        //if we're scanning and if there are available ScanElements
        return HUDManager.Instance?.playerPingingScan > 0 && FreeScanDisplays.Count > 0;
    }

    private static void TryAddNewNodes(HUDManager hudManager)
    {
        //throttle scan searches
        hudManager.updateScanInterval -= Time.deltaTime;
        if (hudManager.updateScanInterval > 0)
            return;
        hudManager.updateScanInterval = 0.1f;

        //only run when the player is scanning ( 0.3f after click )
        if (hudManager.playerPingingScan <= 0)
            return;
        
        if (AsyncLoggerProxy.Enabled)
            AsyncLoggerProxy.WriteEvent(QuickItemScan.NAME, "TryAddNewNodes", "Start");
        
        hudManager.scannedScrapNum = 0;

        //if there are no nodes skip the loop entirely
        //TODO: check if it is worth or the list pool cost is negligible
        if (ScannableNodes.Count > 0)
            using (ListPool<ScanNodeHandler>.Get(out var orderedNodes))
            {
                //loop over the list
                foreach (var nodeHandler in ScannableNodes)
                {
                    //skip if node does is deleted
                    if (!nodeHandler)
                        continue;

                    //skip if node does is not in player FOV
                    if (!nodeHandler.IsOnScreen)
                        continue;
                    
                    //check visibility
                    var visible = nodeHandler.IsValid && !nodeHandler.InMinRange && nodeHandler.HasLos;
                    
                    if (DisplayedScanNodes.Contains(nodeHandler))
                    {
                        //if already shown update the expiration time
                        if (visible)
                        {
                            if (QuickItemScan.PluginConfig.Scanner.ScanTimer.Value >= 0)
                                nodeHandler.DisplayData.TimeLeft = QuickItemScan.PluginConfig.Scanner.ScanTimer.Value;
                            if (nodeHandler.ScanNode?.nodeType == 2)
                                hudManager.scannedScrapNum++;
                        }
                        continue;
                    }

                    //skip if not visible
                    if (!visible)
                        continue;

                    //skip if we filled all slots
                    //( should never happen at this point but skipping the costly ordered insertion is still worth the check )
                    if (FreeScanDisplays.Count <= 0)
                        continue;

                    //sort by priority ( see ScanNodeHandler.compareTo )
                    orderedNodes.AddOrdered(nodeHandler);
                }

                foreach (var nodeHandler in orderedNodes)
                {
                    //try grab a new scanElement ( skip if we filled all slots )
                    if (!FreeScanDisplays.TryDequeue(out var index))
                        break;

                    var element = ScanDisplays[index];
                    
                    //should never happen but better check it anyways
                    //skip if the chosen element is destroyed
                    if (!element)
                        continue;

                    //add curent node tho the list of nodes on screen
                    DisplayedScanNodes.Add(nodeHandler);
                    
                    //mark all state variables
                    nodeHandler.DisplayData.IsActive = true;
                    nodeHandler.DisplayData.IsShown = false;
                    nodeHandler.DisplayData.Element = element;
                    nodeHandler.DisplayData.Index = index;
                    if (QuickItemScan.PluginConfig.Scanner.ScanTimer.Value >= 0)
                        nodeHandler.DisplayData.TimeLeft = QuickItemScan.PluginConfig.Scanner.ScanTimer.Value;

                    //update scrap values
                    if (nodeHandler.ScanNode?.nodeType != 2)
                    {
                        hudManager.scannedScrapNum++;
                    }
                }
            }
        
        if (AsyncLoggerProxy.Enabled)
            AsyncLoggerProxy.WriteEvent(QuickItemScan.NAME, "TryAddNewNodes", "End");
    }

    private static void UpdateNodesOnScreen(HUDManager hudManager, out bool hasScannedScrap)
    {
        hasScannedScrap = false;
        
        //skip if we have no nodes on screen
        if (DisplayedScanNodes.Count <= 0)
            return;

        //throttle the recalculation of clusters
        var shouldComputeClusters = false;
        _clusterInterval -= Time.deltaTime;
        if (_clusterInterval <= 0)
        {
            _clusterInterval = 1f;
            //only compute if clusters are enabled
            if (QuickItemScan.PluginConfig.Performance.Cluster.NodeCount.Value > 0)
                shouldComputeClusters = true;
        }
        
        //grab the cached player ScreenRect object or cache it
        if (!_screenRect)
        {
            var playerScreen = hudManager.playerScreenShakeAnimator.gameObject;
            _screenRect = playerScreen.GetComponent<RectTransform>();
        }
        var screenRect = _screenRect.rect;

        hudManager.totalScrapScanned = 0;
        //TODO: maybe use manual Get and dispose of the dictionary
        using (DictionaryPool<string, List<ScanNodeHandler>>.Get(out var clusterableNodes))
        {
            using (ListPool<ScanNodeHandler>.Get(out var toRemove))
            {
                foreach (var handler in DisplayedScanNodes)
                {
                    var element = handler.DisplayData.Element;
                    //update expiration time
                    if (QuickItemScan.PluginConfig.Scanner.ScanTimer.Value >= 0 && handler.DisplayData.IsShown)
                        handler.DisplayData.TimeLeft -= Time.deltaTime;
                    //check if node expired or is not visible anymore
                    if (!handler || !handler.ScanNode || !handler.IsOnScreen || !handler.IsValid || handler.DisplayData.TimeLeft <= 0)
                    {
                        toRemove.Add(handler);
                        continue;
                    }

                    var scanNode = handler.ScanNode;

                    //initialize the field ( run it only once to save resources )
                    //throttle init of new nodes
                    if (!handler.DisplayData.IsShown && _newNodesToAdd != 0)
                    {
                        //mark the state
                        handler.DisplayData.IsShown = true;
                        //update the counter
                        if (_newNodesToAdd > 0)
                            _newNodesToAdd--;
                        
                        //activate the ScanElement
                        if (!element.gameObject.activeSelf)
                        {
                            element.gameObject.SetActive(true);
                            element.GetComponent<Animator>().SetInteger(ColorNumberHash, scanNode.nodeType);
                            if (scanNode.creatureScanID != -1)
                                hudManager.AttemptScanNewCreature(scanNode.creatureScanID);
                        }

                        //update the ScanElement text
                        var scanElementText = element.gameObject.GetComponentsInChildren<TextMeshProUGUI>();
                        if (scanElementText.Length > 1)
                        {
                            scanElementText[0].text = scanNode.headerText;
                            scanElementText[1].text = scanNode.subText;
                        }
                    }

                    //update position on screen ( use patch from LCUltrawide for compatibility )
                    //use cached viewport pos
                    var viewportPoint = handler.DisplayData.ViewportPos;
                    var rectPoint = new Vector3(screenRect.xMin + screenRect.width * viewportPoint.x,
                        screenRect.yMin + screenRect.height * viewportPoint.y, viewportPoint.z);
                    //update the ScanElement position
                    element.anchoredPosition = rectPoint;
                    //cache the actual position
                    handler.DisplayData.RectPos = rectPoint;
                    
                    //track scrap
                    if (scanNode.nodeType == 2)
                    {
                        hudManager.totalScrapScanned += scanNode.scrapValue;
                        hasScannedScrap = true;
                    }
                
                    //if we should recalculate the clusters
                    if (!shouldComputeClusters)
                        continue;
                    
                    //if this node has been activated
                    if (!handler.DisplayData.IsShown)
                        continue;

                    //categorize the node by text
                    //TODO: do not assume they node types will not have overlapping names
                    if (!clusterableNodes.TryGetValue(scanNode.headerText, out var list))
                    {
                        list = ListPool<ScanNodeHandler>.Get();
                        clusterableNodes[scanNode.headerText] = list;
                    }
                    
                    //sort them only by distance
                    list.AddOrdered(handler, ZComparer);
                }

                //remove all expired nodes
                foreach (var handler in toRemove)
                {
                    RemoveNode(handler);
                }
            }

            if (shouldComputeClusters)
                ComputeClusterNodes(hudManager, clusterableNodes);
            
            //release native arrays 
            foreach (var (_, list) in clusterableNodes) ListPool<ScanNodeHandler>.Release(list);
        }
    }

    internal static void RemoveNode(ScanNodeHandler handler)
    {
        //if the node was on screen
        if (handler.DisplayData.IsActive)
        {
            //remove it from the list
            DisplayedScanNodes.Remove(handler);

            var element = handler.DisplayData.Element;
            //disable the ScanElement
            element.gameObject.SetActive(false);
            //mark the index as free
            FreeScanDisplays.Enqueue(handler.DisplayData.Index);
            
            //reset states
            handler.DisplayData.IsActive = false;
            handler.DisplayData.TimeLeft = 0;
            handler.DisplayData.Element = null;
            handler.DisplayData.Index = -1;
        }
        
        //if node was in a cluster
        if (handler.ClusterData.HasCluster)
        {
            var cIndex = handler.ClusterData.Index;
            var cluster = ClusterNodes[cIndex];
            //remove it form the nodes in the cluster
            cluster.Remove(handler);

            //if it was the master of the cluster
            if (handler.ClusterData.IsMaster)
            {
                //if there are other nodes in the cluster
                if (cluster.Count > 0)
                {
                    //elect a new master
                    var next = cluster[0];
                    next.ClusterData.IsMaster = true;
                }
                else
                {
                    //disable the cluster
                    DisableCluster(cIndex);
                    //mark the index as free
                    FreeClusterDisplays.Enqueue(cIndex);
                }
            }

            //reset states
            handler.ClusterData.HasCluster = false;
            handler.ClusterData.Index = -1;
            handler.ClusterData.Element = null;
            handler.ClusterData.IsMaster = false;
        }
    }

    private static void ComputeClusterNodes(HUDManager @this, Dictionary<string, List<ScanNodeHandler>> clusterableData)
    {
        
        if (AsyncLoggerProxy.Enabled)
            AsyncLoggerProxy.WriteEvent(QuickItemScan.NAME, "ComputeClusterNodes", "Start");
        
        //reset all clusters to empty
        ResetClusters();
        
        //read cached value or create it
        if (!_screenRect)
        {
            var playerScreen = @this.playerScreenShakeAnimator.gameObject;
            _screenRect = playerScreen.GetComponent<RectTransform>();
        }
        var screenRect = _screenRect.rect;
        
        //calculate max distance to cluster
        var distance = Math.Max(screenRect.width, screenRect.height)
            * (QuickItemScan.PluginConfig.Performance.Cluster.MaxDistance.Value / 100f);
        
        if(QuickItemScan.PluginConfig.Debug.Verbose.Value)
            QuickItemScan.Log.LogDebug($"Distance is: {distance}");
        
        //iterate all the categories
        foreach (var (_, list) in clusterableData)
        {
            //use different logic if we need to process the clusters dynamically or just the whole screen
            if (!QuickItemScan.PluginConfig.Performance.Cluster.IgnoreDistance.Value)
            {
                //use the DBSCAN algorithm to compute clusters
                using (DBSCAN.ParseClusters(list, DisplayDistance, distance,
                           QuickItemScan.PluginConfig.Performance.Cluster.MinItems.Value,
                           out var clusters, out var outliers))
                {
                    //if there are cluster
                    if (clusters.Count > 0)
                        foreach (var cluster in clusters)
                        {
                            ProcessCluster(cluster, outliers);
                        }

                    ProcessOutliers(outliers);
                }
            }
            else
            {
                using (ListPool<ScanNodeHandler>.Get(out var outliers))
                {
                    //if we have enough nodes process them together
                    if (list.Count > QuickItemScan.PluginConfig.Performance.Cluster.MinItems.Value)
                    {
                        ProcessCluster(list, outliers);
                    }
                    else
                    {
                        //otherwise make them all as separate nodes
                        outliers.AddRange(list);
                    } 
                    
                    ProcessOutliers(outliers);
                }
            }
        }
        
        if (AsyncLoggerProxy.Enabled)
            AsyncLoggerProxy.WriteEvent(QuickItemScan.NAME, "ComputeClusterNodes", "End");
        
        return;

        void ProcessCluster(List<ScanNodeHandler> cluster, List<ScanNodeHandler> outliers)
        {
            //try to get a new empty cluster
            //skip if there are none
            if (!FreeClusterDisplays.TryDequeue(out var index))
            {
                outliers.AddRange(cluster);
                return;
            }

            var element = ClusterDisplays[index];
            
            //assign each node to the cluster
            for (var i = 0; i < cluster.Count; i++)
            {
                var node = cluster[i];
                
                //set the states
                node.ClusterData.Element = element;
                node.ClusterData.Index = index;
                node.ClusterData.HasCluster = true;
                
                //add the node to the list
                ClusterNodes[index].AddOrdered(node, ZComparer);
            }
        }

        void ProcessOutliers(List<ScanNodeHandler> outliers)
        {
            foreach (var nodeHandler in outliers)
            {
                //if it was already an outlier skip it
                if (!nodeHandler.ClusterData.HasCluster)
                    continue;
                
                //reset the states
                nodeHandler.ClusterData.HasCluster = false;
                nodeHandler.ClusterData.IsMaster = false;

                //force it to redraw the original ScanNode
                nodeHandler.DisplayData.IsShown = false;
            }
        }
    }

    private static void UpdateClustersOnScreen(HUDManager @this)
    {
        for (var i = 0; i < ClusterNodes.Length; i++)
        {
            var cluster = ClusterNodes[i];
            
            //try to find a valid node
            ScanNodeHandler target = null;
            while (cluster.Count > 0 && !target)
            {
                target = cluster[0];
                if (!target || !target.ScanNode)
                {
                    cluster.Remove(target);
                    target = null;
                }
            }
            
            //if we found the valid node
            if (target)
            {
                var element = ClusterDisplays[i];
                
                var scrapValue = 0;
                
                //if it is a scrap node calculate the total scrap value
                var isScrap = target.ScanNode.nodeType == 2;
                if (isScrap)
                    scrapValue = cluster.Select(n => n.ScanNode.scrapValue).Sum();

                var scanNode = target.ScanNode;
                
                //if the clusterElement was disaled enable it
                if (!element.gameObject.activeSelf)
                {
                    element.gameObject.SetActive(true);
                    element.GetComponent<Animator>().SetInteger(ColorNumberHash, scanNode.nodeType);
                }
            
                //if the clusterElement not is assigned or was used by another cluster
                if (!string.Equals(ClusterDisplayAssignment[i], scanNode.headerText))
                {
                    //update the text in the element
                    ClusterDisplayAssignment[i] = scanNode.headerText;
                    var scanElementText = element.gameObject.GetComponentsInChildren<TextMeshProUGUI>();
                    if (scanElementText.Length > 1)
                    {
                        scanElementText[0].text = $"{scanNode.headerText} x{cluster.Count}";
                        scanElementText[1].text = isScrap ? $"Value: {scrapValue}" : scanNode.subText;
                    }
                }
                
                //if we need to compute the median point
                if (!QuickItemScan.PluginConfig.Performance.Cluster.UseClosest.Value)
                {
                    using (ListPool<Vector2>.Get(out var points))
                    {
                        var medianPoint = cluster.GetMedianFromPoint(ViewportCenter, ViewportDistance);

                        element.anchoredPosition = medianPoint.DisplayData.RectPos;
                    }
                }
                else
                {
                    //or use the closes node position
                    element.anchoredPosition = target.DisplayData.RectPos;
                }

                //mark all other nodes as not master
                foreach (var node in cluster)
                {
                    node.ClusterData.IsMaster = false;
                    var nodeElement = node.DisplayData.Element;
                    //disable the ScanNodes
                    if (nodeElement&& nodeElement.gameObject.activeSelf)
                        nodeElement.gameObject.SetActive(false);
                }
                //mark the target as the master
                target.ClusterData.IsMaster = true;
            }
            else
            {
                //if no nodes disable the cluster
                DisableCluster(i);
            }
        }    
    }
    
    private static void DisableCluster(int index)
    {
        var element = ClusterDisplays[index];
        ClusterDisplayAssignment[index] = null;
        if (element && element.gameObject.activeSelf)
            element.gameObject.SetActive(false);
    }

    private static void ResetClusters()
    {
        FreeClusterDisplays.Clear();
        for (var index = 0; index < ClusterDisplays.Length; index++)
        {
            //DisableCluster(index);
            FreeClusterDisplays.Enqueue(index);
        }

        foreach (var list in ClusterNodes)
        {
            foreach (var node in list)
            {
                node.ClusterData.Index = -1;
                node.ClusterData.IsMaster = false;
                node.ClusterData.Element = null;
            }

            list.Clear();
        }
    }
}