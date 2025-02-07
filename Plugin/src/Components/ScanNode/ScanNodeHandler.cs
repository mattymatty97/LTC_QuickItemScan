using System;
using GameNetcodeStuff;
using QuickItemScan.Components.ScanElement;
using QuickItemScan.Dependency;
using QuickItemScan.Patches;
using UnityEngine;

namespace QuickItemScan.Components.ScanNode;

public class ScanNodeHandler : MonoBehaviour, IComparable<ScanNodeHandler>
{
    private static readonly Vector3[] Corners =
    [
        new(-1,  1,  1),
        new( 1,  1, -1),
        new(-1,  1, -1),
        new( 1, -1,  1),
        new(-1, -1,  1),
        new( 1, -1, -1)
    ];

    //collider at eyePos of localPlayer
    internal static Collider ScannerCollider;

    //Unity Components associated with the ScanNode
    public class ScanNodeComponents
    {
        protected internal ScanNodeComponents() {}

        public Collider Collider { get; internal set; }
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
        public ScanElementHolder Element { get; internal set; }
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
        public ScanElementHolder Element { get; internal set; }
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
    private SphereCollider _scanRadiusTrigger = null!;
    
    //main properties for the ScanNode
    public ScanNodeProperties ScanNode { get; internal set; } = null!;
    //Node has Line Of Sight to the player
    public bool HasLos { get; internal set; } = false;
    //Cached distance to the player
    public float DistanceToPlayer { get; private set; } = float.PositiveInfinity;
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
        Components.Collider                 = ScanNode.GetComponent<Collider>();
        Components.GrabbableObject          = ScanNode.GetComponentInParent<GrabbableObject>();
        Components.EnemyAI                  = ScanNode.GetComponentInParent<EnemyAI>();
        Components.TerminalAccessibleObject = ScanNode.GetComponentInParent<TerminalAccessibleObject>();

        var ogMaxRange = ScanNode.maxRange;
        float maxRange = ogMaxRange;

        if (LGUProxy.Enabled)
        {
            maxRange += LGUProxy.GetScanRangeIncrease(ScanNode);
        }

        //add scanSphere
        _scanRadiusTrigger = gameObject.AddComponent<SphereCollider>();
        _scanRadiusTrigger.isTrigger = true;
        _scanRadiusTrigger.radius = maxRange;
        _scanRadiusTrigger.includeLayers = LayerMask.GetMask("Player");
        _scanRadiusTrigger.excludeLayers = ~LayerMask.GetMask("Player");
        _cachedMaxDistance = ogMaxRange;
    }
    private void OnTriggerEnter(Collider other)
    {
        if (other != ScannerCollider)
            return;

        var gameNetworkManager = GameNetworkManager.Instance;
        //the player must be the local player
        if (gameNetworkManager == null || gameNetworkManager.localPlayerController == null)
            return;

        //the player must be alive
        if (gameNetworkManager.localPlayerController.isPlayerDead)
            return;

        PlayerIsInRange();
    }

    private void OnTriggerExit(Collider other)
    {
        if (other != ScannerCollider)
            return;

        //player could be dead we do not care

        PlayerIsOutOfRange();
    }

    private void PlayerIsInRange()
    {
        if(QuickItemScan.PluginConfig.Debug.VerboseColliders.Value)
            QuickItemScan.Log.LogDebug($"{ScanNode.headerText}({GetInstanceID()}) is now in range");

        //player entered scan range
        InMaxRange = true;
        DistanceToPlayer = _cachedMaxDistance;
        ScannerPatches.ScannableNodes.Add(this);
    }
    private void PlayerIsOutOfRange()
    {
        if(QuickItemScan.PluginConfig.Debug.VerboseColliders.Value)
            QuickItemScan.Log.LogDebug($"{ScanNode.headerText}({GetInstanceID()}) is now out of range");

        //player is out of scan range
        InMaxRange = false;
        InMinRange = true;
        HasLos = false;
        DistanceToPlayer = float.PositiveInfinity;
        ScannerPatches.ScannableNodes.Remove(this);
    }

    private void OnDestroy()
    {
        if (QuickItemScan.PluginConfig.Debug.VerboseColliders.Value)
            QuickItemScan.Log.LogDebug($"{ScanNode.headerText}({GetInstanceID()}) has been deleted");
        
        //node has been deleted
        //Cleanup routine
        InMaxRange = false;
        InMinRange = true;
        IsValid = false;
        HasLos = false;
        IsOnScreen = false;
        DistanceToPlayer = float.PositiveInfinity;
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

        var ogMaxRange = ScanNode.maxRange;
        float maxRange = ogMaxRange;

        //update sphere radius to match maxRange
        if (_cachedMaxDistance == ogMaxRange)
        {
            if (LGUProxy.Enabled)
            {
                maxRange += LGUProxy.GetScanRangeIncrease(ScanNode);
            }

            _cachedMaxDistance = ogMaxRange;
            _scanRadiusTrigger.radius = maxRange;
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
        if (!ShouldUpdate())
        {
            //reset values to start clean on next valid update
            IsOnScreen = false;
            IsValid = false;
            InMinRange = true;
            return;
        }
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
        //viewport z is already the distance to the camera plane in world units
        //( negative means behind camera )
        DistanceToPlayer = Vector3.Distance(camera.transform.position, scanNodePosition);

        //throttle updates to save some computing
        _updateInterval -= Time.deltaTime;
        if (_updateInterval > 0)
            return;

        _updateInterval = 0.1f;

        //check if this node has a reason to be scanned

        IsValid = CheckValid();

        //update other values only if needed
        if (!InMaxRange || !IsValid)
            return;

        InMinRange = DistanceToPlayer < ScanNode.minRange;

        HasLos = true;
        if (!ScanNode.requiresLineOfSight)
            return;

        HasLos = false;
        //only update LOS if we have a reason to
        if (InMinRange)
            return;

        //if the node is not in player FOV then it does not have LOS
        if (!IsOnScreen)
            return;

        if (QuickItemScan.PluginConfig.Performance.LineOfSight.ScanThroughWalls.Value)
            HasLos = true;
        else if (LGUProxy.Enabled && LGUProxy.ShouldSkipLOSCheck(ScanNode))
            HasLos = true;
        else
        {
            HasLos = !Physics.Linecast(localPlayer.playerEye.position, ScanNode.transform.position, 256,
                QueryTriggerInteraction.Ignore);

            if (HasLos || !QuickItemScan.PluginConfig.Performance.LineOfSight.CheckCorners.Value)
                return;

            var b = Components.Collider.bounds;
            var extents = b.extents;

            foreach (var corner in Corners)
            {
                var point = transform.TransformPoint(Vector3.Scale(extents, corner));

                if (Physics.Linecast(localPlayer.playerEye.position, point, 256,
                        QueryTriggerInteraction.Ignore))
                    continue;

                HasLos = true;
                break;
            }
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

        if (QuickItemScan.PluginConfig.Optional.ScanOpenDoors.Value)
            return true;

        //if it is a door and is closed
        if (terminalAccessibleObject && terminalAccessibleObject.isBigDoor &&
            terminalAccessibleObject.isDoorOpen)
            return false;

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
