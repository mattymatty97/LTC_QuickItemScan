﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HarmonyLib;
using QuickItemScan.Components;
using QuickItemScan.Utils;
using TMPro;
using UnityEngine;
using UnityEngine.Pool;
using Object = UnityEngine.Object;

namespace QuickItemScan.Patches;

[HarmonyPatch]
internal class ScannerPatches
{
    private static RectTransform[] ScanDisplays = [];
    private static RectTransform[] ClusterDisplays = [];
    private static List<ScanNodeHandler>[] ClusterNodes = [];
    private static readonly Queue<int> FreeScanDisplays = [];
    private static readonly Queue<int> FreeClusterDisplays = [];

    private static readonly HashSet<ScanNodeHandler> DisplayedScanNodes = new();
    private static readonly int ColorNumberHash = Animator.StringToHash("colorNumber");
    private static readonly int DisplayHash = Animator.StringToHash("display");
    private static float _newNodeInterval;
    private static float _clusterInterval;
    private static int _newNodesToAdd;

    private static readonly IComparer<ScanNodeHandler> ZComparer = Comparer<ScanNodeHandler>.Create((n1, n2) =>
        n1.DisplayData.ScreenPos.z.CompareTo(n2.DisplayData.ScreenPos.z));

    private static readonly Func<ScanNodeHandler, ScanNodeHandler, float> DisplayDistance = (n1, n2) =>
        Vector2.Distance(n1.DisplayData.ScreenPos, n2.DisplayData.ScreenPos);

    [HarmonyPatch(typeof(ScanNodeProperties), nameof(ScanNodeProperties.Awake))]
    [HarmonyPostfix]
    private static void OnAwake(ScanNodeProperties __instance)
    {
        //add our components in a nested gameobject, so they won't interfere
        var gameObject = new GameObject
        {
            name = nameof(QuickItemScan)
        };
        gameObject.transform.SetParent(__instance.transform, false);
        var handler = gameObject.AddComponent<ScanNodeHandler>();
        handler.ScanNode = __instance;
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
        __instance.DisableAllScanElements();
        //empty this so other mods do not complain
        Array.Resize(ref __instance.scanElements, 0);

        Array.Resize(ref ScanDisplays, QuickItemScan.PluginConfig.MaxScanItems.Value);

        for (var i = 0; i < QuickItemScan.PluginConfig.MaxScanItems.Value; i++)
        {
            var element = Object.Instantiate(original, original.transform.position, original.transform.rotation,
                original.transform.parent);
            element.transform.name = $"new {original.transform.name}-{i}";
            FreeScanDisplays.Enqueue(i);
            ScanDisplays[i] = element;
        }

        Array.Resize(ref ClusterDisplays, QuickItemScan.PluginConfig.ClusterCount.Value);
        Array.Resize(ref ClusterNodes, QuickItemScan.PluginConfig.ClusterCount.Value);

        for (var i = 0; i < QuickItemScan.PluginConfig.ClusterCount.Value; i++)
        {
            var element = Object.Instantiate(original, original.transform.position, original.transform.rotation,
                original.transform.parent);
            element.transform.name = $"cluster {original.transform.name}-{i}";
            FreeClusterDisplays.Enqueue(i);
            ClusterDisplays[i] = element;
            var nodes = ClusterNodes[i];
            if (nodes == null) ClusterNodes[i] = nodes = new List<ScanNodeHandler>();

            nodes.Clear();
        }
    }

    [HarmonyPatch(typeof(HUDManager), nameof(HUDManager.DisableAllScanElements))]
    [HarmonyPostfix]
    private static void ClearScans(HUDManager __instance)
    {
        FreeScanDisplays.Clear();
        for (var index = 0; index < ScanDisplays.Length; index++)
        {
            var element = ScanDisplays[index];
            element.gameObject.SetActive(false);
            FreeScanDisplays.Enqueue(index);
        }

        FreeClusterDisplays.Clear();
        for (var index = 0; index < ClusterDisplays.Length; index++)
        {
            var element = ClusterDisplays[index];
            element.gameObject.SetActive(false);
            FreeClusterDisplays.Enqueue(index);
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

    [HarmonyPatch(typeof(HUDManager), nameof(HUDManager.UpdateScanNodes))]
    [HarmonyPrefix]
    private static bool RewriteUpdateScan(HUDManager __instance, bool __runOriginal)
    {
        if (!__runOriginal)
            return false;

        //track how many nodes can be added
        if (_newNodesToAdd == 0)
        {
            _newNodeInterval -= Time.deltaTime;
            if (_newNodeInterval <= 0)
            {
                _newNodesToAdd = QuickItemScan.PluginConfig.NewNodeCount.Value;
                _newNodeInterval = QuickItemScan.PluginConfig.NewNodeDelay.Value;
            }
        }

        TryAddNewNodes(__instance);

        var scannedScrap = UpdateNodesOnScreen(__instance);

        UpdateClusterOnScreen(__instance);

        if (!scannedScrap)
        {
            __instance.totalScrapScanned = 0;
            __instance.totalScrapScannedDisplayNum = 0;
            __instance.addToDisplayTotalInterval = 0.35f;
        }

        __instance.scanInfoAnimator.SetBool(DisplayHash, __instance.scannedScrapNum >= 2 && scannedScrap);

        return false;
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

    internal static bool CanScan()
    {
        return HUDManager.Instance?.playerPingingScan > 0 && FreeScanDisplays.Count > 0;
    }

    private static void TryAddNewNodes(HUDManager @this)
    {
        @this.updateScanInterval -= Time.deltaTime;
        if (@this.updateScanInterval > 0)
            return;
        @this.updateScanInterval = 0.1f;

        //only run when the player is scanning ( 0.3f after click )
        if (@this.playerPingingScan <= 0)
            return;

        @this.scannedScrapNum = 0;

        if (ScanNodeHandler.ScannableNodes.Count > 0)
            using (ListPool<ScanNodeHandler>.Get(out var orderedNodes))
            {
                //loop over the list
                foreach (var nodeHandler in ScanNodeHandler.ScannableNodes)
                {
                    if (!nodeHandler)
                        continue;

                    if (!nodeHandler.IsOnScreen)
                        continue;

                    var visible = nodeHandler.IsValid && !nodeHandler.InMinRange && nodeHandler.HasLos;

                    if (DisplayedScanNodes.Contains(nodeHandler))
                    {
                        //if already shown update the expiration time
                        if (visible)
                        {
                            if (QuickItemScan.PluginConfig.ScanTimer.Value >= 0)
                                nodeHandler.DisplayData.TimeLeft = QuickItemScan.PluginConfig.ScanTimer.Value;
                            if (nodeHandler.ScanNode.nodeType == 2)
                                @this.scannedScrapNum++;
                        }

                        continue;
                    }

                    //skip if not visible
                    if (!visible)
                        continue;

                    //skip if we filled all slots
                    if (FreeScanDisplays.Count <= 0)
                        continue;

                    //sort nodes by priority ( nodeType > distance )
                    orderedNodes.AddOrdered(nodeHandler);
                }

                foreach (var nodeHandler in orderedNodes)
                {
                    //try grab a new scanSlot ( skip if we filled all slots )
                    if (!FreeScanDisplays.TryDequeue(out var index))
                        break;

                    var element = ScanDisplays[index];

                    if (!element)
                        continue;

                    DisplayedScanNodes.Add(nodeHandler);

                    nodeHandler.DisplayData.IsActive = true;
                    nodeHandler.DisplayData.IsShown = false;
                    nodeHandler.DisplayData.Element = element;
                    nodeHandler.DisplayData.Index = index;
                    if (QuickItemScan.PluginConfig.ScanTimer.Value >= 0)
                        nodeHandler.DisplayData.TimeLeft = QuickItemScan.PluginConfig.ScanTimer.Value;

                    //no idea why but adding them to this dictionary is required for them to show up in the right position
                    //EDIT: apparently LCUltrawide uses this to patch ( we skip this to bypass their patch )
                    //@this.scanNodes[element] = nodeHandler.ScanNode;
                    //update scrap values
                    if (nodeHandler.ScanNode.nodeType != 2)
                        continue;

                    //@this.totalScrapScanned += nodeHandler.ScanNode.scrapValue;
                    //@this.addedToScrapCounterThisFrame = true;

                    @this.scannedScrapNum++;
                }
            }
    }

    private static bool UpdateNodesOnScreen(HUDManager @this)
    {
        if (DisplayedScanNodes.Count <= 0)
            return false;

        var shouldComputeClusters = false;
        _clusterInterval -= Time.deltaTime;
        if (_clusterInterval <= 0)
        {
            _clusterInterval = 1f;
            //only compute if clusters are enabled
            if (QuickItemScan.PluginConfig.ClusterCount.Value > 0)
                shouldComputeClusters = true;
        }

        var scrapOnScreen = false;
        @this.totalScrapScanned = 0;

        var playerScreen = @this.playerScreenShakeAnimator.gameObject;
        var screenRect = playerScreen.GetComponent<RectTransform>().rect;

        using (DictionaryPool<string, List<ScanNodeHandler>>.Get(out var clusterableNodes))
        {
            using (ListPool<ScanNodeHandler>.Get(out var toRemove))
            {
                foreach (var handler in DisplayedScanNodes)
                {
                    var element = handler.DisplayData.Element;
                    //update expiration time
                    if (QuickItemScan.PluginConfig.ScanTimer.Value >= 0 && handler.DisplayData.IsShown)
                        handler.DisplayData.TimeLeft -= Time.deltaTime;
                    //check if node expired or is not visible anymore
                    if (!handler || !handler.IsOnScreen || !handler.IsValid || handler.DisplayData.TimeLeft <= 0)
                    {
                        toRemove.Add(handler);
                        continue;
                    }

                    var scanNode = handler.ScanNode;

                    //initialize the field ( run it only once to save resources )
                    //throttle init of new nodes
                    if (!handler.DisplayData.IsShown && _newNodesToAdd != 0)
                    {
                        handler.DisplayData.IsShown = true;
                        if (_newNodesToAdd > 0)
                            _newNodesToAdd--;
                        if (!element.gameObject.activeSelf)
                        {
                            element.gameObject.SetActive(true);
                            element.GetComponent<Animator>().SetInteger(ColorNumberHash, scanNode.nodeType);
                            if (scanNode.creatureScanID != -1)
                                @this.AttemptScanNewCreature(scanNode.creatureScanID);
                        }

                        var scanElementText = element.gameObject.GetComponentsInChildren<TextMeshProUGUI>();
                        if (scanElementText.Length > 1)
                        {
                            scanElementText[0].text = scanNode.headerText;
                            scanElementText[1].text = scanNode.subText;
                        }
                    }

                    //update position on screen ( use patch from LCUltrawide for compatibility )

                    var viewportPoint =
                        GameNetworkManager.Instance.localPlayerController.gameplayCamera.WorldToViewportPoint(
                            scanNode.transform.position);
                    var screenPoint = new Vector3(screenRect.xMin + screenRect.width * viewportPoint.x,
                        screenRect.yMin + screenRect.height * viewportPoint.y, viewportPoint.z);
                    element.anchoredPosition = screenPoint;

                    /*
                    var screenPoint =
                        GameNetworkManager.Instance.localPlayerController.gameplayCamera.WorldToScreenPoint(
                            scanNode.transform.position);
                    element.anchoredPosition = new Vector2(screenPoint.x - 439.48f, screenPoint.y - 244.8f);
                    */

                    handler.DisplayData.ScreenPos = screenPoint;
                    //track scrap
                    if (scanNode.nodeType == 2)
                    {
                        @this.totalScrapScanned += handler.ScanNode.scrapValue;
                        scrapOnScreen = true;
                    }

                    if (!shouldComputeClusters)
                        continue;

                    if (!handler.DisplayData.IsShown)
                        continue;

                    if (!clusterableNodes.TryGetValue(scanNode.headerText, out var list))
                    {
                        list = ListPool<ScanNodeHandler>.Get();
                        clusterableNodes[scanNode.headerText] = list;
                    }

                    list.Add(handler);
                }

                //remove all expired nodes
                foreach (var handler in toRemove)
                {
                    DisplayedScanNodes.Remove(handler);

                    var element = handler.DisplayData.Element;

                    element.gameObject.SetActive(false);
                    FreeScanDisplays.Enqueue(handler.DisplayData.Index);

                    handler.DisplayData.IsActive = false;
                    handler.DisplayData.TimeLeft = 0;
                    handler.DisplayData.Element = null;
                    handler.DisplayData.Index = -1;


                    if (handler.ClusterData.HasCluster)
                    {
                        var cIndex = handler.ClusterData.Index;
                        var cluster = ClusterNodes[cIndex];
                        cluster.Remove(handler);

                        if (handler.ClusterData.IsMaster)
                        {
                            if (cluster.Count > 0)
                            {
                                var next = cluster[0];
                                next.ClusterData.IsMaster = true;
                            }
                            else
                            {
                                DisableCluster(cIndex);
                                FreeClusterDisplays.Enqueue(cIndex);
                            }
                        }

                        handler.ClusterData.HasCluster = false;
                        handler.ClusterData.Index = -1;
                        handler.ClusterData.Element = null;
                        handler.ClusterData.IsMaster = false;
                    }
                }
            }

            if (shouldComputeClusters)
                ComputeClusterNodes(@this, clusterableNodes);

            foreach (var (_, list) in clusterableNodes) ListPool<ScanNodeHandler>.Release(list);
        }

        
        //report if we found scrap
        return scrapOnScreen;
    }

    private static void ComputeClusterNodes(HUDManager @this, Dictionary<string, List<ScanNodeHandler>> clusterableData)
    {
        ResetClusters();

        foreach (var (_, list) in clusterableData)
        {
            using (DBSCAN.ParseClusters(list, DisplayDistance, QuickItemScan.PluginConfig.ClusterDistance.Value,
                       QuickItemScan.PluginConfig.ClusterMin.Value,
                       out var clusters, out var outliers))
            {
                if (clusters.Count > 0)
                    foreach (var cluster in clusters)
                    {
                        if (!FreeClusterDisplays.TryDequeue(out var index))
                        {
                            outliers.AddRange(cluster);
                            continue;
                        }

                        var element = ClusterDisplays[index];

                        cluster.Sort(ZComparer);

                        for (var i = 0; i < cluster.Count; i++)
                        {
                            var node = cluster[i];

                            node.ClusterData.Element = element;
                            node.ClusterData.Index = index;
                            node.ClusterData.HasCluster = true;

                            ClusterNodes[index].AddOrdered(node, ZComparer);
                        }
                    }

                if (outliers.Count > 0)
                    foreach (var nodeHandler in outliers)
                    {
                        if (!nodeHandler.ClusterData.HasCluster)
                            continue;
                        nodeHandler.ClusterData.HasCluster = false;
                        nodeHandler.ClusterData.IsMaster = false;

                        //force it to redraw the original
                        nodeHandler.DisplayData.IsShown = false;
                    }
            }
        }
    }

    private static void UpdateClusterOnScreen(HUDManager @this)
    {
        for (var i = 0; i < ClusterNodes.Length; i++)
        {
            var cluster = ClusterNodes[i];

            if (cluster.Count > 0)
            {
                var element = ClusterDisplays[i];

                var target = cluster[0];


                var scrapValue = 0;

                var isScrap = target.ScanNode.nodeType == 2;
                if (isScrap)
                    scrapValue = cluster.Select(n => n.ScanNode.scrapValue).Sum();

                if (!element.gameObject.activeSelf)
                {
                    var scanNode = target.ScanNode;
                    element.gameObject.SetActive(true);
                    element.GetComponent<Animator>().SetInteger(ColorNumberHash, scanNode.nodeType);
                    
                    var scanElementText = element.gameObject.GetComponentsInChildren<TextMeshProUGUI>();
                    if (scanElementText.Length > 1)
                    {
                        scanElementText[0].text = $"{scanNode.headerText} x{cluster.Count}";
                        scanElementText[1].text = isScrap ? $"Sum: {scrapValue}" : scanNode.subText;
                    }
                }

                element.anchoredPosition = target.DisplayData.ScreenPos;

                foreach (var node in cluster)
                {
                    node.ClusterData.IsMaster = false;
                    var nodeElement = node.DisplayData.Element;
                    if (nodeElement&& nodeElement.gameObject.activeSelf)
                        nodeElement.gameObject.SetActive(false);
                }
                
                target.ClusterData.IsMaster = true;
            }
            else
            {
                DisableCluster(i);
            }
        }    
    }
    
    private static void DisableCluster(int index)
    {
        var element = ClusterDisplays[index];
        if (element.gameObject.activeSelf)
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