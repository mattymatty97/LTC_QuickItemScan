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
        var gameObject = new GameObject
        {
            name = nameof(QuickItemScan)
        };
        gameObject.transform.SetParent(__instance.transform, false);
        var handler = gameObject.AddComponent<ScanNodeHandler>();
        handler.ScanNode = __instance;
    }

    private class ScanNodeDisplayData
    {
        public int Index;
        public float TimeLeft;
        public bool Initialized;
    }

    private static readonly Queue<int> PingIndexes = [];

    private static readonly Dictionary<ScanNodeHandler, ScanNodeDisplayData> DisplayedScanNodes = new();
    private static readonly int ColorNumber = Animator.StringToHash("colorNumber");
    private static readonly int Display1 = Animator.StringToHash("display");

    internal static bool CanScan()
    {
        return PingIndexes.Any();
    }

    [HarmonyPatch(typeof(HUDManager), nameof(HUDManager.Start)), HarmonyPrefix]
    private static void GetPingIndexes(HUDManager __instance)
    {
        PingIndexes.Clear();
        DisplayedScanNodes.Clear();

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

        var foundScrap = false;


        using (ListPool<int>.Get(out var returnedIndexes))
        {
            using (ListPool<ScanNodeHandler>.Get(out var toRemove))
            {
                foreach (var (handler, scanNodeDisplayData) in DisplayedScanNodes)
                {
                    var element = __instance.scanElements[scanNodeDisplayData.Index];
                    scanNodeDisplayData.TimeLeft -= Time.deltaTime;
                    if (!handler || !handler.IsOnScreen || !handler.IsValid || scanNodeDisplayData.TimeLeft <= 0)
                    {
                        returnedIndexes.Add(scanNodeDisplayData.Index);
                        toRemove.Add(handler);
                        handler.IsActive = false;
                        
                        if (handler.ScanNode.nodeType == 2)
                            __instance.totalScrapScanned -= handler.ScanNode.scrapValue;
                        continue;
                    }
                    
                    var scanNode = handler.ScanNode;

                    if (__instance.scanElementsHidden)
                    {
                        scanNodeDisplayData.Initialized = false;
                    }
                    else
                    {
                        if (!scanNodeDisplayData.Initialized)
                        {
                            scanNodeDisplayData.Initialized = true;
                            if (!element.gameObject.activeSelf)
                            {
                                element.gameObject.SetActive(true);
                                element.GetComponent<Animator>().SetInteger(ColorNumber, scanNode.nodeType);
                                if (scanNode.creatureScanID != -1)
                                    __instance.AttemptScanNewCreature(scanNode.creatureScanID);
                            }

                            var scanElementText = element.gameObject.GetComponentsInChildren<TextMeshProUGUI>();
                            if (scanElementText.Length > 1)
                            {
                                scanElementText[0].text = scanNode.headerText;
                                scanElementText[1].text = scanNode.subText;
                            }
                        }

                        var screenPoint = playerScript.gameplayCamera.WorldToScreenPoint(scanNode.transform.position);
                        element.anchoredPosition = new Vector2(screenPoint.x - 439.48f, screenPoint.y - 244.8f);
                    }

                    if (scanNode.nodeType == 2)
                        foundScrap = true;
                }

                foreach (var handler in toRemove)
                {
                    DisplayedScanNodes.Remove(handler);
                }
            }

            foreach (var elementIndex in returnedIndexes)
            {
                __instance.scanElements[elementIndex].gameObject.SetActive(false);
                PingIndexes.Enqueue(elementIndex);
            }
        }

        if (!foundScrap)
        {
            __instance.totalScrapScanned = 0;
            __instance.totalScrapScannedDisplayNum = 0;
            __instance.addToDisplayTotalInterval = 0.35f;
        }

        __instance.scanInfoAnimator.SetBool(Display1, __instance.scannedScrapNum >= 2 && foundScrap);

        return false;
    }


    [HarmonyPatch(typeof(HUDManager), "PingScan_performed"), HarmonyPostfix]
    private static void PerformScan(HUDManager __instance, bool __runOriginal)
    {
        if (!__runOriginal)
            return;

        //TODO: move this to the Update cycle similar to vanilla
        //TODO: make ScanNodeHandler only update during the vanilla delay
        foreach (var nodeHandler in ScanNodeHandler.ScannableNodes)
        {
            if (!nodeHandler)
                continue;

            var visible = nodeHandler.IsValid && !nodeHandler.InMinRange && nodeHandler.HasLos;

            if (DisplayedScanNodes.TryGetValue(nodeHandler, out var data))
            {
                if (visible)
                    data.TimeLeft = QuickItemScan.PluginConfig.ScanTimer.Value;
                continue;
            }

            if (!visible)
                continue;
            
            if (!PingIndexes.TryDequeue(out var index))
                return;

            DisplayedScanNodes[nodeHandler] =
                new ScanNodeDisplayData
                {
                    Index = index,
                    TimeLeft = QuickItemScan.PluginConfig.ScanTimer.Value
                };

            nodeHandler.IsActive = true;

            __instance.scanNodes[__instance.scanElements[index]] = nodeHandler.ScanNode;

            if (nodeHandler.ScanNode.nodeType != 2)
                continue;

            __instance.totalScrapScanned += nodeHandler.ScanNode.scrapValue;
            __instance.addedToScrapCounterThisFrame = true;
        }
    }
}