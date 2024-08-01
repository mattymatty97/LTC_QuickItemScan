using System;
using System.Collections.Generic;
using System.Diagnostics;
using GameNetcodeStuff;
using QuickItemScan.Patches;
using UnityEngine;

namespace QuickItemScan.Components;

public class ScanNodeHandler : MonoBehaviour, IComparable<ScanNodeHandler>
{
    public static readonly HashSet<ScanNodeHandler> ScannableNodes = new();
    
    public class ScanNodeComponents
    {
        protected internal ScanNodeComponents() {}

        public GrabbableObject GrabbableObject { get; internal set; }
        public EnemyAI EnemyAI { get; internal set; }
        public TerminalAccessibleObject TerminalAccessibleObject { get; internal set; }
    }
    
    public class ScanNodeClusterData
    {
        protected internal ScanNodeClusterData() {}

        public int Index { get; internal set; } = -1;

        public RectTransform Element { get; internal set; }
        
        public bool HasCluster { get; internal set; }
        
        public bool IsMaster { get; internal set; }
        
    }
    
    public class ScanNodeDisplayData
    {
        protected internal ScanNodeDisplayData() {}

        public int Index { get; internal set; } = -1;
        public float TimeLeft { get; internal set; } = 1;
        public RectTransform Element { get; internal set; }
        public bool IsShown { get; internal set; }
        public bool IsActive { get; internal set; }
        public Vector3 ScreenPos { get; internal set; }
        public Vector3 ViewportPos { get; internal set; }
    }

    public ScanNodeComponents Components { get; } = new();
    public ScanNodeDisplayData DisplayData { get; } = new();
    
    public ScanNodeClusterData ClusterData { get; } = new();
    
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
        Components.GrabbableObject          = gameObject.GetComponentInParent<GrabbableObject>();
        Components.EnemyAI                  = gameObject.GetComponentInParent<EnemyAI>();
        Components.TerminalAccessibleObject = gameObject.GetComponentInParent<TerminalAccessibleObject>();

        //add scanSphere
        _scanRadiusTrigger = gameObject.AddComponent<SphereCollider>();
        _scanRadiusTrigger.isTrigger = true;
        _scanRadiusTrigger.radius = ScanNode.maxRange;
        _cachedMaxDistance = ScanNode.maxRange;
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag("Player"))
            return;

        var controller = other.GetComponentInParent<PlayerControllerB>();
        var gameNetworkManager = GameNetworkManager.Instance;
        
        if (gameNetworkManager == null || controller==null || controller != gameNetworkManager.localPlayerController)
            return;
        
        if (controller.isPlayerDead)
            return;
        
        if(QuickItemScan.PluginConfig.Debug.Verbose.Value)
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
        
        if(QuickItemScan.PluginConfig.Debug.Verbose.Value)
            QuickItemScan.Log.LogDebug($"{ScanNode.headerText}({GetInstanceID()}) is now out of range");
        
        InMaxRange = false;
        InMinRange = true;
        HasLos = false;
        DistanceToPlayer = double.PositiveInfinity;
        ScannableNodes.Remove(this);
    }

    private void OnDestroy()
    {
        if (QuickItemScan.PluginConfig.Debug.Verbose.Value)
            QuickItemScan.Log.LogDebug($"{ScanNode.headerText}({GetInstanceID()}) has been deleted");
        
        InMaxRange = false;
        InMinRange = true;
        IsValid = false;
        HasLos = false;
        IsOnScreen = false;
        DistanceToPlayer = double.PositiveInfinity;
        ScannableNodes.Remove(this);
        
        ScannerPatches.RemoveNode(this);
    }

    private float _updateInterval = 0f;

    private void FixedUpdate()
    {
        //if scan-node got deleted
        if (!ScanNode)
        {
            Destroy(gameObject);
            return;
        }

        //update sphere radius to match maxRange
        if (_cachedMaxDistance != ScanNode.maxRange)
        {
            _cachedMaxDistance = ScanNode.maxRange;
            _scanRadiusTrigger.radius = _cachedMaxDistance;
        }
    }

    private void LateUpdate()
    {
        //if scan-node got deleted
        if (!ScanNode)
        {
            Destroy(gameObject);
            return;
        }
        
        //save some computing if no-one is scanning ( or can be scanned )
        if ( ShouldUpdate() )
        {
            //sanity checks
            if (!GameNetworkManager.Instance)
                return;
            
            var localPlayer = GameNetworkManager.Instance.localPlayerController;
            if (!localPlayer)
                return;
            var scanNodePosition = ScanNode.transform.position;
            var camera = localPlayer.gameplayCamera;
            
            //check if we're inside the camera FOV
            DisplayData.ViewportPos = camera.WorldToViewportPoint(scanNodePosition);
            IsOnScreen = DisplayData.ViewportPos is { z: > 0, x: >= 0 and <= 1, y: >= 0 and <= 1 };
            //viewport z is already the distance to the camera in world units
            //( negative means behind camera )
            DistanceToPlayer = Math.Abs(DisplayData.ViewportPos.z);
            
            _updateInterval -= Time.deltaTime;
            if (_updateInterval > 0)
                return;

            _updateInterval = 0.1f;
            
            //check if this node has a reason to be scanned

            IsValid = CheckValid();
            
            //update other values only if needed
            if (InMaxRange && IsValid)
            {
                InMinRange = DistanceToPlayer < ScanNode.minRange;

                if (ScanNode.requiresLineOfSight)
                {
                    if (!InMinRange)
                    {
                        if (!IsOnScreen)
                            HasLos = false;
                        else
                        {
                            if (QuickItemScan.PluginConfig.Performance.Cheat.ScanThroughWalls.Value)
                                HasLos = true;
                            else
                                HasLos = !Physics.Linecast(camera.transform.position, ScanNode.transform.position, 256,
                                    QueryTriggerInteraction.Ignore);
                        }
                    }
                    else
                    {
                        HasLos = false;
                    }
                }
                else
                {
                    HasLos = true;
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
        return DisplayData.IsActive || ( InMaxRange && ScannerPatches.CanScan());
    }
    
    private bool CheckValid()
    {
        if (!ScanNode.gameObject.activeInHierarchy)
            return false;
        
        var grabbableObject = Components.GrabbableObject;
        var enemyAI = Components.EnemyAI;
        var terminalAccessibleObject = Components.TerminalAccessibleObject;
        
        if (grabbableObject && (grabbableObject.isHeld || grabbableObject.isHeldByEnemy || grabbableObject.deactivated)) 
            return false;

        if (enemyAI && enemyAI.isEnemyDead) 
            return false;

        if (!QuickItemScan.PluginConfig.Optional.ScanOpenDoors.Value)
        {
            if (terminalAccessibleObject && terminalAccessibleObject.isBigDoor &&
                terminalAccessibleObject.isDoorOpen)
                return false;
        }

        return true;
    }

    public int CompareTo(ScanNodeHandler other)
    {
        if (!ScanNode)
            return 1;

        if (!other)
            return -1;
        
        var tmp = IsOnScreen.CompareTo(other.IsOnScreen);
        if (tmp != 0)
            return -tmp;
        
        tmp = IsValid.CompareTo(other.IsValid);
        if (tmp != 0)
            return -tmp;
        
        tmp = ScanNode.nodeType.CompareTo(other.ScanNode.nodeType);
        if (tmp != 0)
            return tmp;

        return DistanceToPlayer.CompareTo(other.DistanceToPlayer);
    }

    public override string ToString()
    {
        return $"{ScanNode.headerText}({GetInstanceID()})";
    }
}