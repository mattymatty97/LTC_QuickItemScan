using System;
using GameNetcodeStuff;
using QuickItemScan.Patches;
using UnityEngine;

namespace QuickItemScan.Components;

public class ScanNodeHandler : MonoBehaviour, IComparable<ScanNodeHandler>
{
    //Unity Components associated with the ScanNode
    public class ScanNodeComponents
    {
        protected internal ScanNodeComponents() {}

        public GrabbableObject GrabbableObject { get; internal set; }
        public EnemyAI EnemyAI { get; internal set; }
        public TerminalAccessibleObject TerminalAccessibleObject { get; internal set; }
    }
    
    //Holder class with the current cluster status
    public class ScanNodeClusterData
    {
        protected internal ScanNodeClusterData() {}
        //Index to the assigned cluster ScanElement
        public int Index { get; internal set; } = -1;
        //the assigned cluster ScanElement
        public RectTransform Element { get; internal set; }
        //is this node assigned to a Cluster
        public bool HasCluster { get; internal set; }
        //the master of a cluster is the node that holds the Element
        //( if this node is removed from screen it needs to either elect a new master or disable the cluster )
        public bool IsMaster { get; internal set; }
        
    }
    
    //Holder class with the current state
    public class ScanNodeDisplayData
    {
        protected internal ScanNodeDisplayData() {}

        //Index to the assigned ScanElement
        public int Index { get; internal set; } = -1;
        //the assigned ScanElement
        public RectTransform Element { get; internal set; }
        //is this node assigned to a ScanElement
        public bool IsActive { get; internal set; }
        //has this ScanElement been activated
        public bool IsShown { get; internal set; }
        //how long until this ScanNode should disappear form screen
        public float TimeLeft { get; internal set; } = 1;
        
        //cached value of the current position of the object in viewport space
        public Vector3 ViewportPos { get; internal set; }
        
        //cached value of the current position of the object in the cameraRect space
        public Vector3 RectPos { get; internal set; }
    }

    //holder Properties
    public ScanNodeComponents Components { get; } = new();
    public ScanNodeDisplayData DisplayData { get; } = new();
    public ScanNodeClusterData ClusterData { get; } = new();
    
    
    //local variables for internal use
    private int _cachedMaxDistance = 0;
    private float _updateInterval = 0f;
    private SphereCollider _scanRadiusTrigger = null;
    
    //main properties for the ScanNode
    public ScanNodeProperties ScanNode { get; internal set; } = null;
    //Node has Line Of Sight to the player
    public bool HasLos { get; internal set; } = false;
    //Cached distance to the player
    public double DistanceToPlayer { get; private set; } = double.PositiveInfinity;
    //This node is in range to be scanned
    //( inside the SphereCollider range )
    public bool InMaxRange { get; private set; } = false;
    //This node is too close to be scanned
    public bool InMinRange { get; private set; } = true;
    //this node targets a valid entity/item/door
    public bool IsValid { get; private set; } = false;
    //this node is currently in the player camera Field of View
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
        //were seaching for a player
        if (!other.CompareTag("Player"))
            return;

        var controller = other.GetComponentInParent<PlayerControllerB>();
        var gameNetworkManager = GameNetworkManager.Instance;
        //the player must be the local player
        if (gameNetworkManager == null || controller==null || controller != gameNetworkManager.localPlayerController)
            return;
        //the player must be alive
        if (controller.isPlayerDead)
            return;
        
        if(QuickItemScan.PluginConfig.Debug.Verbose.Value)
            QuickItemScan.Log.LogDebug($"{ScanNode.headerText}({GetInstanceID()}) is now in range");
        //player entered scan range
        InMaxRange = true;
        DistanceToPlayer = _cachedMaxDistance;
        ScannerPatches.ScannableNodes.Add(this);
    }

    private void OnTriggerExit(Collider other)
    {        
        //were seaching for a player
        if (!other.CompareTag("Player"))
            return;

        var controller = other.GetComponentInParent<PlayerControllerB>();
        var gameNetworkManager = GameNetworkManager.Instance;
        
        //the player must be the local player
        if (gameNetworkManager == null || controller==null || controller != gameNetworkManager.localPlayerController)
            return;
        //player could be dead we do not care
        
        if(QuickItemScan.PluginConfig.Debug.Verbose.Value)
            QuickItemScan.Log.LogDebug($"{ScanNode.headerText}({GetInstanceID()}) is now out of range");
        
        //player is out of scan range
        InMaxRange = false;
        InMinRange = true;
        HasLos = false;
        DistanceToPlayer = double.PositiveInfinity;
        ScannerPatches.ScannableNodes.Remove(this);
    }

    private void OnDestroy()
    {
        if (QuickItemScan.PluginConfig.Debug.Verbose.Value)
            QuickItemScan.Log.LogDebug($"{ScanNode.headerText}({GetInstanceID()}) has been deleted");
        
        //node has been deleted
        //Cleanup routine
        InMaxRange = false;
        InMinRange = true;
        IsValid = false;
        HasLos = false;
        IsOnScreen = false;
        DistanceToPlayer = double.PositiveInfinity;
        ScannerPatches.ScannableNodes.Remove(this);
        
        ScannerPatches.RemoveNode(this);
    }

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
            
            //throttle updates to save some computing
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
                    //only update LOS if we have a reason to
                    if (!InMinRange)
                    {
                        //if the node is not in player FOV then it does not have LOS
                        if (!IsOnScreen)
                            HasLos = false;
                        else
                        {
                            //do LOS as vanilla ( check for a raycast collisions )
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
            //reset values to start clean on next valid update
            IsOnScreen = false;
            IsValid = false;
            InMinRange = true;
        }
    }

    private bool ShouldUpdate()
    {
        //Node is shown in HUD, or it is in range and player can scan
        return DisplayData.IsActive || ( InMaxRange && ScannerPatches.CanUpdate());
    }
    
    private bool CheckValid()
    {
        //node is not disabled
        if (!ScanNode.gameObject.activeInHierarchy)
            return false;
        
        var grabbableObject = Components.GrabbableObject;
        var enemyAI = Components.EnemyAI;
        var terminalAccessibleObject = Components.TerminalAccessibleObject;
        
        //object is not held
        if (grabbableObject && (grabbableObject.isHeld || grabbableObject.isHeldByEnemy || grabbableObject.deactivated)) 
            return false;

        //enemy is alive
        if (enemyAI && enemyAI.isEnemyDead) 
            return false;

        
        if (!QuickItemScan.PluginConfig.Optional.ScanOpenDoors.Value)
        {
            //door is not open
            if (terminalAccessibleObject && terminalAccessibleObject.isBigDoor &&
                terminalAccessibleObject.isDoorOpen)
                return false;
        }

        return true;
    }

    public int CompareTo(ScanNodeHandler other)
    {
        //if we're deleted sort last
        if (!ScanNode)
            return 1;
            
        //if they're deleted sort first
        if (!other)
            return -1;
        
        //nodes in FOV first
        var tmp = IsOnScreen.CompareTo(other.IsOnScreen);
        if (tmp != 0)
            return -tmp;
        
        //valid nodes first
        tmp = IsValid.CompareTo(other.IsValid);
        if (tmp != 0)
            return -tmp;
        
        //sort by node type
        tmp = ScanNode.nodeType.CompareTo(other.ScanNode.nodeType);
        if (tmp != 0)
            return tmp;

        //closer nodes first
        return DistanceToPlayer.CompareTo(other.DistanceToPlayer);
    }

    public override string ToString()
    {
        return $"{ScanNode.headerText}({GetInstanceID()})";
    }
}