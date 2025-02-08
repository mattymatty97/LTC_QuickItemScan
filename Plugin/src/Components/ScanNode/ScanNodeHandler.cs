using System;
using System.Collections.Generic;
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
        protected internal ScanNodeClusterData(ScanNodeHandler handler)
        {
            _handler = handler;
        }

        private readonly ScanNodeHandler _handler;

        //other elements in the cluster
        internal List<ScanNodeHandler> Cluster { get; set; } = null!;

        //is this node assigned to a Cluster
        public bool HasCluster => Cluster != null;

        //the master of a cluster is the node that holds the Element
        public bool IsMaster => Cluster is { Count: > 0 } && Cluster[0] == _handler;

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
    public ScanNodeClusterData ClusterData { get; }

    //local variables for internal use
    private float _updateInterval = 0f;
    
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

    public ScanNodeHandler()
    {
        ClusterData = new ScanNodeClusterData(this);
    }

    private void Start()
    {
        //cache possible components for the ScanNode
        Components.Collider                 = ScanNode.GetComponent<Collider>();
        Components.GrabbableObject          = ScanNode.GetComponentInParent<GrabbableObject>();
        Components.EnemyAI                  = ScanNode.GetComponentInParent<EnemyAI>();
        Components.TerminalAccessibleObject = ScanNode.GetComponentInParent<TerminalAccessibleObject>();
    }

    private void PlayerIsInRange()
    {
        if(QuickItemScan.PluginConfig.Debug.VerboseColliders.Value)
            QuickItemScan.Log.LogDebug($"{ScanNode.headerText}({GetInstanceID()}) is now in range");

        //player entered scan range
        ScannerPatches.ScannableNodes.Add(this);
    }
    private void PlayerIsOutOfRange()
    {
        if(QuickItemScan.PluginConfig.Debug.VerboseColliders.Value)
            QuickItemScan.Log.LogDebug($"{ScanNode.headerText}({GetInstanceID()}) is now out of range");

        //player is out of scan range
        InMinRange = true;
        HasLos = false;
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
        ScannerPatches.ScannableNodes.Remove(this);
        
        ScannerPatches.RemoveNode(this);
    }

    private void FixedUpdate()
    {
        if (!QuickItemScan.IsEnabled)
            return;

        //if scan-node got deleted
        if (!ScanNode)
        {
            Destroy(gameObject);
            return;
        }

        //sanity checks
        if (!GameNetworkManager.Instance)
            return;

        var localPlayer = GameNetworkManager.Instance.localPlayerController;
        if (!localPlayer)
            return;

        var ogMaxRange = ScanNode.maxRange;
        float maxRange = ogMaxRange;
        if (LGUProxy.Enabled)
        {
            maxRange += LGUProxy.GetScanRangeIncrease(ScanNode);
        }

        var sqrMaxRange = maxRange * maxRange;

        var old = InMaxRange;
        InMaxRange = DistanceToPlayer <= sqrMaxRange;

        if (old == InMaxRange)
            return;

        if (InMaxRange)
            PlayerIsInRange();
        else
            PlayerIsOutOfRange();
    }

    private void LateUpdate()
    {
        if (!QuickItemScan.IsEnabled)
            return;

        //if scan-node got deleted
        if (!ScanNode)
        {
            Destroy(gameObject);
            return;
        }

        //sanity checks
        if (!GameNetworkManager.Instance)
            return;

        var localPlayer = GameNetworkManager.Instance.localPlayerController;
        if (!localPlayer)
            return;

        var playerEye = localPlayer.playerEye;
        var camera = localPlayer.gameplayCamera;

        var scanNodePosition = transform.position;

        //Compute distance to player
        DistanceToPlayer = (playerEye.position - scanNodePosition).sqrMagnitude;
        
        //save some computing if we can't be scanned or no-one is scanning
        if (!ShouldUpdate())
        {
            //reset values to start clean on next valid update
            IsOnScreen = false;
            IsValid = false;
            InMinRange = true;
            return;
        }

        //check if we're inside the camera FOV
        DisplayData.ViewportPos = camera.WorldToViewportPoint(scanNodePosition);
        IsOnScreen = DisplayData.ViewportPos is { z: > 0, x: >= 0 and <= 1, y: >= 0 and <= 1 };

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

        //only update LOS if we have a reason to
        if (!ShouldUpdateLOS())
            return;

        HasLos = false;
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

    private bool ShouldUpdateLOS()
    {
        var hudManager = HUDManager.Instance;
        //Node is shown in HUD, or it is in range and player is scanning
        return InMaxRange && hudManager && hudManager.playerPingingScan > 0;
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
