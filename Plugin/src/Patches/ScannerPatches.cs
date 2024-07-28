using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using QuickItemScan.Components;
using TMPro;
using UnityEngine;
using UnityEngine.Pool;
using Object = UnityEngine.Object;

namespace QuickItemScan.Patches;

[HarmonyPatch]
internal class ScannerPatches
{
    [HarmonyPatch(typeof(ScanNodeProperties), nameof(ScanNodeProperties.Awake)), HarmonyPostfix]
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

    //TODO: use a Queue of RectTransfomrs instead of just their indexes
    private static readonly Queue<int> PingIndexes = [];

    private static readonly HashSet<ScanNodeHandler> DisplayedScanNodes = new();
    private static readonly int ColorNumberHash = Animator.StringToHash("colorNumber");
    private static readonly int DisplayHash = Animator.StringToHash("display");
    private static float NewNodeInterval = 0f;
    private static int NewNodesToAdd = 0;

    internal static bool CanScan()
    {
        if (!HUDManager.Instance)
            return false;

        return HUDManager.Instance.playerPingingScan > 0 && PingIndexes.Any();
    }

    [HarmonyPatch(typeof(HUDManager), nameof(HUDManager.Start)), HarmonyPostfix]
    private static void InitValues(HUDManager __instance)
    {
        //reset all states
        NewNodeInterval = 0f;
        
        PingIndexes.Clear();

        DisplayedScanNodes.Clear();

        Array.Resize(ref __instance.scanNodesHit, QuickItemScan.PluginConfig.MaxScanItems.Value);

        var original = __instance.scanElements[0];
        __instance.DisableAllScanElements();
        Array.Resize(ref __instance.scanElements, QuickItemScan.PluginConfig.MaxScanItems.Value);
        for (var i = 0; i < QuickItemScan.PluginConfig.MaxScanItems.Value; i++)
        {
            var element = Object.Instantiate(original, original.transform.position, original.transform.rotation,
                original.transform.parent);
            element.transform.name = $"new {original.transform.name}-{i}";
            PingIndexes.Enqueue(i);
            __instance.scanElements[i] = element;
        }
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

    [HarmonyPatch(typeof(HUDManager), nameof(HUDManager.DisableAllScanElements)), HarmonyPostfix]
    private static void ClearScans(HUDManager __instance)
    {
        foreach (var handler in DisplayedScanNodes)
        {
            PingIndexes.Enqueue(handler.DisplayData.Index);
            handler.DisplayData.Active = false;
            handler.DisplayData.TimeLeft = 0;
            handler.DisplayData.Element = null;
            handler.DisplayData.Index = -1;
        }

        DisplayedScanNodes.Clear();
        __instance.scanNodes.Clear();
    }

    [HarmonyPatch(typeof(HUDManager), nameof(HUDManager.UpdateScanNodes)), HarmonyPrefix]
    private static bool RewriteUpdateScan(HUDManager __instance, bool __runOriginal)
    {
        if (!__runOriginal)
            return false;

        //track how many nodes can be added
        if (NewNodesToAdd == 0)
        {
            NewNodeInterval -= Time.deltaTime;
            if (NewNodeInterval <= 0)
            {
                NewNodesToAdd = QuickItemScan.PluginConfig.NewNodeCount.Value;
                NewNodeInterval = QuickItemScan.PluginConfig.NewNodeDelay.Value;
            }
        }

        TryAddNewNodes(__instance);
        
        var scannedScrap = UpdateNodesOnScreen(__instance);
       

        if (!scannedScrap)
        {
            __instance.totalScrapScanned = 0;
            __instance.totalScrapScannedDisplayNum = 0;
            __instance.addToDisplayTotalInterval = 0.35f;
        }

        __instance.scanInfoAnimator.SetBool(DisplayHash, __instance.scannedScrapNum >= 2 && scannedScrap);

        return false;
    }

    private static bool UpdateNodesOnScreen(HUDManager @this)
    {
        var scrapOnScreen = false;
        
        using (ListPool<int>.Get(out var returnedIndexes))
        {
            using (ListPool<ScanNodeHandler>.Get(out var toRemove))
            {
                foreach (var handler in DisplayedScanNodes)
                {
                    var element = handler.DisplayData.Element;
                    //update expiration time
                    if (QuickItemScan.PluginConfig.ScanTimer.Value >=0)
                        handler.DisplayData.TimeLeft -= Time.deltaTime;
                    //check if node expired or is not visible anymore
                    if (!handler || !handler.IsOnScreen || !handler.IsValid || handler.DisplayData.TimeLeft <= 0)
                    {
                        returnedIndexes.Add(handler.DisplayData.Index);
                        toRemove.Add(handler);
                        handler.DisplayData.Active = false;
                        handler.DisplayData.TimeLeft = 0;
                        handler.DisplayData.Element = null;
                        handler.DisplayData.Index = -1;

                        if (handler.ScanNode.nodeType == 2)
                            @this.totalScrapScanned -= handler.ScanNode.scrapValue;
                        continue;
                    }

                    var scanNode = handler.ScanNode;

                    //initialize the field ( run it only once to save resources )
                    //throttle init of new nodes
                    if (!handler.DisplayData.Shown && NewNodesToAdd != 0)
                    {
                        if(NewNodesToAdd > 0)
                            NewNodesToAdd--;
                        handler.DisplayData.Shown = true;
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

                    //apparently this is not needed!
                    /*//update position on screen
                    var screenPoint = playerScript.gameplayCamera.WorldToScreenPoint(scanNode.transform.position);
                    element.anchoredPosition = new Vector2(screenPoint.x - 439.48f, screenPoint.y - 244.8f);*/

                    //track scrap
                    if (scanNode.nodeType == 2)
                        scrapOnScreen = true;
                }

                //remove all expired nodes
                foreach (var handler in toRemove)
                {
                    DisplayedScanNodes.Remove(handler);
                }

                //return all expired indexes to the Queue
                foreach (var elementIndex in returnedIndexes)
                {
                    var element = @this.scanElements[elementIndex];
                    element.gameObject.SetActive(false);
                    @this.scanNodes.Remove(element);
                    PingIndexes.Enqueue(elementIndex);
                }
            }
        }

        //report if we found scrap
        return scrapOnScreen;
    }

    private static void TryAddNewNodes(HUDManager @this)
    {
        @this.updateScanInterval -= Time.deltaTime;
        if (@this.updateScanInterval > 0)
            return;
        @this.updateScanInterval = 0.1f;

        //sort nodes by priority ( nodeType > validity > distance )
        //TODO: find if there is a more efficient way of doing this
        ScanNodeHandler.ScannableNodes.Sort();

        //only run when the player is scanning ( 0.3f after click )
        if (@this.playerPingingScan <= 0)
            return;

        @this.scannedScrapNum = 0;

        //loop over the list
        //foreach (var nodeHandler in scanNodeList)
        foreach (var nodeHandler in ScanNodeHandler.ScannableNodes)
        {
            if (!nodeHandler)
                continue;

            var visible = nodeHandler.IsValid && !nodeHandler.InMinRange && nodeHandler.HasLos;

            if (DisplayedScanNodes.Contains(nodeHandler))
            {
                //if already shown update the expiration time
                if (visible)
                {
                    if (QuickItemScan.PluginConfig.ScanTimer.Value >=0)
                        nodeHandler.DisplayData.TimeLeft = QuickItemScan.PluginConfig.ScanTimer.Value;
                    if (nodeHandler.ScanNode.nodeType == 2)
                        @this.scannedScrapNum++;
                }

                continue;
            }

            //skip if not visible
            if (!visible)
                continue;

            //try grab a new scanSlot ( skip if we filled all slots )
            if (!PingIndexes.TryDequeue(out var index))
                continue;

            DisplayedScanNodes.Add(nodeHandler);

            nodeHandler.DisplayData.Active = true;
            nodeHandler.DisplayData.Shown = false;
            nodeHandler.DisplayData.Index = index;
            nodeHandler.DisplayData.Element = @this.scanElements[index];
            if (QuickItemScan.PluginConfig.ScanTimer.Value >=0)
                nodeHandler.DisplayData.TimeLeft = QuickItemScan.PluginConfig.ScanTimer.Value;

            //no idea why but adding them to this dictionary is required for them to show up in the right position
            //apparently used by the animators on the scan node displays
            @this.scanNodes[@this.scanElements[index]] = nodeHandler.ScanNode;

            //update scrap values
            if (nodeHandler.ScanNode.nodeType != 2)
                continue;

            @this.totalScrapScanned += nodeHandler.ScanNode.scrapValue;
            @this.addedToScrapCounterThisFrame = true;

            @this.scannedScrapNum++;
        }
    }
}