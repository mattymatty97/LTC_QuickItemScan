using System.Collections.Generic;
using System.Linq;
using GameNetcodeStuff;
using HarmonyLib;
using QuickItemScan.Components;
using TMPro;
using UnityEngine;
using UnityEngine.Pool;

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

    internal static bool CanScan()
    {
        if (!HUDManager.Instance)
            return false;
        
        return HUDManager.Instance.playerPingingScan > 0 && PingIndexes.Any();
    }

    [HarmonyPatch(typeof(HUDManager), nameof(HUDManager.Start)), HarmonyPostfix]
    private static void GetPingIndexes(HUDManager __instance)
    {
        //reset all states
        PingIndexes.Clear();
        DisplayedScanNodes.Clear();
        
        //pre-load the queue with all the possible scans
        for (var i = 0; i < __instance.scanElements.Length; i++)
        {
            PingIndexes.Enqueue(i);
        }
    }


    [HarmonyPatch(typeof(HUDManager), nameof(HUDManager.UpdateScanNodes)), HarmonyPrefix]
    private static bool RewriteUpdateScan(HUDManager __instance, PlayerControllerB playerScript, bool __runOriginal)
    {
        if (!__runOriginal)
            return false;

        TryAddNewNodes(__instance);
        
        var scannedScrap = UpdateNodesOnScreen(__instance, playerScript);

        if (!scannedScrap)
        {
            __instance.totalScrapScanned = 0;
            __instance.totalScrapScannedDisplayNum = 0;
            __instance.addToDisplayTotalInterval = 0.35f;
        }

        __instance.scanInfoAnimator.SetBool(DisplayHash, __instance.scannedScrapNum >= 2 && scannedScrap);

        return false;
    }

    private static bool UpdateNodesOnScreen(HUDManager @this, PlayerControllerB playerScript)
    {
        @this.scannedScrapNum = 0;
        
        using (ListPool<int>.Get(out var returnedIndexes))
        {
            using (ListPool<ScanNodeHandler>.Get(out var toRemove))
            {
                foreach (var handler in DisplayedScanNodes)
                {
                    var element = handler.DisplayData.Element;
                    //update expiration time
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
                    
                    //if hide elements is active consider all slots as not Initialized
                    if (@this.scanElementsHidden)
                    {
                        handler.DisplayData.Shown = false;
                    }
                    else
                    {
                        //initialize the field ( run it only once to save resources )
                        if (!handler.DisplayData.Shown)
                        {
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

                        //update position on screen
                        var screenPoint = playerScript.gameplayCamera.WorldToScreenPoint(scanNode.transform.position);
                        element.anchoredPosition = new Vector2(screenPoint.x - 439.48f, screenPoint.y - 244.8f);
                    }

                    //track scrap
                    if (scanNode.nodeType == 2)
                        @this.scannedScrapNum++;
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
        return @this.scannedScrapNum > 0;
    }

    private static void TryAddNewNodes(HUDManager @this)
    {
        /*
        @this.updateScanInterval -= Time.deltaTime;
        if (@this.updateScanInterval > 0)
            return;
        @this.updateScanInterval = 0.1f;
        */
        
        //only run when the player is scanning ( 0.3f after click )
        if (@this.playerPingingScan <= 0)
            return;

        var addedItems = 0;
        
        //sort nodes by priority ( nodeType > validity > distance )
        //TODO: find if there is a more efficient way of doing this
        ScanNodeHandler.ScannableNodes.Sort();
        
        //loop over the list
        foreach (var nodeHandler in ScanNodeHandler.ScannableNodes)
        {
            if (!nodeHandler)
                continue;

            var visible = nodeHandler.IsValid && !nodeHandler.InMinRange && nodeHandler.HasLos;
            
            if (DisplayedScanNodes.Contains(nodeHandler))
            {
                //if already shown update the expiration time
                if (visible)
                    nodeHandler.DisplayData.TimeLeft = QuickItemScan.PluginConfig.ScanTimer.Value;
                continue;
            }
            
            //if we already scanned enough for this frame skip all the rest
            if (addedItems > QuickItemScan.PluginConfig.ItemsPerFrame.Value)
                break;

            //skip if not visible
            if (!visible)
                continue;
            
            //try grab a new scanSlot ( skip if we filled all slots )
            if (!PingIndexes.TryDequeue(out var index))
                continue;

            addedItems++;

            DisplayedScanNodes.Add(nodeHandler);

            nodeHandler.DisplayData.Active   = true;
            nodeHandler.DisplayData.Shown    = false;
            nodeHandler.DisplayData.Index    = index;
            nodeHandler.DisplayData.Element  = @this.scanElements[index];
            nodeHandler.DisplayData.TimeLeft = QuickItemScan.PluginConfig.ScanTimer.Value;

            //no idea why but adding them to this dictionary is required for them to show up in the right position
            @this.scanNodes[@this.scanElements[index]] = nodeHandler.ScanNode;

            //update scrap values
            if (nodeHandler.ScanNode.nodeType != 2)
                continue;

            @this.totalScrapScanned += nodeHandler.ScanNode.scrapValue;
            @this.addedToScrapCounterThisFrame = true;
        }
    }
    
    
}