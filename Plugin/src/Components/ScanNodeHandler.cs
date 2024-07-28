using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using QuickItemScan.Patches;
using UnityEngine;

namespace QuickItemScan.Components;

public class ScanNodeHandler : MonoBehaviour, IComparable<ScanNodeHandler>
{
    public static readonly List<ScanNodeHandler> ScannableNodes = new();
    
    public class ScanNodeComponents
    {
        public GrabbableObject GrabbableObject { get; internal set; }
        public EnemyAI EnemyAI { get; internal set; }
        public TerminalAccessibleObject TerminalAccessibleObject { get; internal set; }
        public Renderer ScanNodeRenderer { get; internal set; }
    }
    
    public class ScanNodeDisplayData
    {
        public int Index { get; internal set; } = -1;
        public float TimeLeft { get; internal set; } = 1;
        public RectTransform Element { get; internal set; }
        public bool Shown { get; internal set; }
        public bool Active { get; internal set; }
    }

    public ScanNodeComponents Components { get; } = new();
    public ScanNodeDisplayData DisplayData { get; } = new();
    
    private int _cachedMaxDistance = 0;
    
    private SphereCollider _scanRadiusTrigger = null;
    public ScanNodeProperties ScanNode { get; internal set; } = null;
    public bool HasLos { get; internal set; } = false;
    public double DistanceToPlayer { get; private set; } = double.PositiveInfinity;
    public bool InMaxRange { get; private set; } = false;
    public bool InMinRange { get; private set; } = true;
    public bool IsValid { get; private set; } = false;
    public bool IsOnScreen { get; internal set; } = false;

    private void Start()
    {
        //cache possible components for the ScanNode
        Components.GrabbableObject          = gameObject.GetComponentInChildren<GrabbableObject>();
        Components.EnemyAI                  = gameObject.GetComponentInChildren<EnemyAI>();
        Components.TerminalAccessibleObject = gameObject.GetComponentInChildren<TerminalAccessibleObject>();
        Components.ScanNodeRenderer         = ScanNode.GetComponent<Renderer>();
        
        //add scanSphere
        _scanRadiusTrigger = gameObject.AddComponent<SphereCollider>();
        _scanRadiusTrigger.isTrigger = true;
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag("Player"))
            return;

        var controller = other.GetComponentInParent<PlayerControllerB>();
        var gameNetworkManager = GameNetworkManager.Instance;
        
        if (gameNetworkManager == null || controller==null || controller != gameNetworkManager.localPlayerController)
            return;
        
        if (gameNetworkManager.localPlayerController.isPlayerDead)
            return;
        
        if(QuickItemScan.PluginConfig.Verbose.Value)
            QuickItemScan.Log.LogDebug($"{ScanNode.headerText}({GetInstanceID()}) is now in range");
        
        InMaxRange = true;
        DistanceToPlayer = _cachedMaxDistance;
        ScannableNodes.Add(this);
    }

    private void OnTriggerExit(Collider other)
    {        
        if (!other.CompareTag("Player"))
            return;

        var controller = other.GetComponentInParent<PlayerControllerB>();
        var gameNetworkManager = GameNetworkManager.Instance;
        
        if (gameNetworkManager == null || controller==null || controller != gameNetworkManager.localPlayerController)
            return;
        
        if(QuickItemScan.PluginConfig.Verbose.Value)
            QuickItemScan.Log.LogDebug($"{ScanNode.headerText}({GetInstanceID()}) is now out of range");
        
        InMaxRange = false;
        InMinRange = true;
        HasLos = false;
        DistanceToPlayer = double.PositiveInfinity;
        ScannableNodes.Remove(this);
    }

    private void OnDestroy()
    {
        if(QuickItemScan.PluginConfig.Verbose.Value)
            QuickItemScan.Log.LogDebug($"{ScanNode.headerText}({GetInstanceID()}) is now out of range");
        
        InMaxRange = false;
        InMinRange = true;
        IsValid = false;
        HasLos = false;
        IsOnScreen = false;
        DistanceToPlayer = double.PositiveInfinity;
        ScannableNodes.Remove(this);
    }

    private void FixedUpdate()
    {
        //if scan-node got deleted
        //TODO: maybe delete ourselves too
        if (!ScanNode)
            return;

        //update sphere radius to match maxRange ( update if range is changed at runtime too )
        if (_cachedMaxDistance != ScanNode.maxRange)
        {
            _cachedMaxDistance = ScanNode.maxRange;
            
            //SphereCollider will shrink based on the largest scale, make sure to account for that in the radius
            var scale = transform.lossyScale;
            var factor = Math.Max(scale.x, Math.Max(scale.y, scale.z));

            _scanRadiusTrigger.radius = _cachedMaxDistance / factor;
        }
        
        //do not compute if not needed
        if ( ShouldUpdate() )
        {
            var localPlayer = GameNetworkManager.Instance.localPlayerController;
            var playerLocation = localPlayer.transform.position;
            var scanNodePosition = ScanNode.transform.position;
            var camera = localPlayer.gameplayCamera;

            IsOnScreen = Components.ScanNodeRenderer.isVisible;
            
            //check if this node is supposed to be scannable

            IsValid = CheckValid();
            
            //update other values only if needed
            if (InMaxRange && IsValid)
            {
                DistanceToPlayer = Vector3.Distance(scanNodePosition, playerLocation);

                InMinRange = DistanceToPlayer < ScanNode.minRange;

                if (!InMinRange && ScanNode.requiresLineOfSight)
                {
                    if (!IsOnScreen)
                        HasLos = false;
                    else
                    {
                        HasLos = !Physics.Linecast(camera.transform.position, ScanNode.transform.position, 256,
                            QueryTriggerInteraction.Ignore);
                    }
                }
            }
        }
        else
        {
            IsOnScreen = false;
            IsValid = false;
            InMinRange = true;
        }
    }

    private bool ShouldUpdate()
    {
        return DisplayData.Active || ( InMaxRange && ScannerPatches.CanScan());
    }
    
    private bool CheckValid()
    {
        var grabbableObject = Components.GrabbableObject;
        var enemyAI = Components.EnemyAI;
        var terminalAccessibleObject = Components.TerminalAccessibleObject;
        
        if (grabbableObject is not null
            && (grabbableObject.isHeld || grabbableObject.isHeldByEnemy || grabbableObject.deactivated)) 
            return false;

        if (enemyAI is not null && enemyAI.isEnemyDead) 
            return false;

        if (terminalAccessibleObject is not null && terminalAccessibleObject.isBigDoor &&
            terminalAccessibleObject.isDoorOpen)
            return false;
        
        return true;
    }

    public int CompareTo(ScanNodeHandler other)
    {
        if (!ScanNode)
            return -1;
        
        var tmp = ScanNode.nodeType.CompareTo(other.ScanNode.nodeType);
        if (tmp != 0)
            return tmp;

        tmp = IsValid.CompareTo(other.IsValid);

        if (tmp != 0)
            return tmp;

        return DistanceToPlayer.CompareTo(other.DistanceToPlayer);
    }
}