using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using QuickItemScan.Patches;
using UnityEngine;

namespace QuickItemScan.Components;

public class ScanNodeHandler : MonoBehaviour
{
    public static readonly HashSet<ScanNodeHandler> ScannableNodes = new();
    
    public class ScanNodeCache
    {
        public GrabbableObject GrabbableObject { get; internal set; }
        public EnemyAI EnemyAI { get; internal set; }
        public TerminalAccessibleObject TerminalAccessibleObject { get; internal set; }
    }

    public ScanNodeCache ComponentCache { get; } = new();
    
    private int _cachedMaxDistance = 0;
    
    private SphereCollider _scanRadiusTrigger = null;
    public ScanNodeProperties ScanNode { get; internal set; } = null;
    public bool HasLos { get; internal set; } = false;
    public double CachedDistance { get; private set; } = double.PositiveInfinity;
    public bool InMaxRange { get; private set; } = false;
    public bool InMinRange { get; private set; } = true;
    public bool IsValid { get; private set; } = false;
    public bool IsOnScreen { get; internal set; } = false;
    public bool IsActive { get; internal set; } = false;

    private void Awake()
    {
        
        ComponentCache.GrabbableObject          = gameObject.GetComponentInChildren<GrabbableObject>();
        ComponentCache.EnemyAI                  = gameObject.GetComponentInChildren<EnemyAI>();
        ComponentCache.TerminalAccessibleObject = gameObject.GetComponentInChildren<TerminalAccessibleObject>();
        
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
        InMinRange = false;
        IsValid = CheckValid();
        HasLos = !ScanNode.requiresLineOfSight;
        CachedDistance = _cachedMaxDistance;
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
        CachedDistance = double.PositiveInfinity;
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
        CachedDistance = double.PositiveInfinity;
        ScannableNodes.Remove(this);
    }

    private void FixedUpdate()
    {
        if (!ScanNode)
            return;

        if (_cachedMaxDistance != ScanNode.maxRange)
        {
            _cachedMaxDistance = ScanNode.maxRange;

            var scale = transform.lossyScale;
            var factor = Math.Max(scale.x, Math.Max(scale.y, scale.z));

            _scanRadiusTrigger.radius = _cachedMaxDistance / factor;
        }
        
        //only update if we have slots in the scanner ( or if we're already shown )
        if ( IsActive || (InMaxRange && ScannerPatches.CanScan()) ) //TODO: only update in the 0.3f seconds that the scanner runs
        {
            var localPlayer = GameNetworkManager.Instance.localPlayerController;
            var playerLocation = localPlayer.transform.position;
            var scanNodePosition = ScanNode.transform.position;
            var camera = localPlayer.gameplayCamera;


            if (localPlayer.CameraFOV == 0f)
            {
                var vFoVrad = camera.fieldOfView * Mathf.Deg2Rad;
                var cameraHeightAt1 = Mathf.Tan(vFoVrad * .5f);
                var hFoVrad = Mathf.Atan(cameraHeightAt1 * camera.aspect) * 2;
                localPlayer.CameraFOV = hFoVrad * Mathf.Rad2Deg;
            }

            var direction = scanNodePosition - camera.transform.position;
            direction.Normalize();

            var angle = Vector3.Angle(direction, camera.transform.forward);

            IsOnScreen = angle <= localPlayer.CameraFOV / 2f;

            IsValid = CheckValid();

            if (InMaxRange && IsValid)
            {
                CachedDistance = Vector3.Distance(scanNodePosition, playerLocation);

                InMinRange = CachedDistance < ScanNode.minRange;

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
    
    private bool CheckValid()
    {
        var grabbableObject = ComponentCache.GrabbableObject;
        var enemyAI = ComponentCache.EnemyAI;
        var terminalAccessibleObject = ComponentCache.TerminalAccessibleObject;
        
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
}