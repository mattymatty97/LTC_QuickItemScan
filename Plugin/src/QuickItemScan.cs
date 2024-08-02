using System;
using System.Collections.Generic;
using System.Reflection;
using QuickItemScan.Dependency;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using MonoMod.RuntimeDetour;
using QuickItemScan.Patches;

namespace QuickItemScan;

[BepInPlugin(GUID, NAME, VERSION)]
[BepInDependency("BMX.LobbyCompatibility", Flags:BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("ainavt.lc.lethalconfig", Flags:BepInDependency.DependencyFlags.SoftDependency)]
internal class QuickItemScan : BaseUnityPlugin
{
	public static readonly List<Hook> Hooks = [];
	public static QuickItemScan INSTANCE { get; private set; }
		
	public const string GUID = "mattymatty.QuickItemScan";
	public const string NAME = "QuickItemScan";
	public const string VERSION = "0.0.8";

	internal static ManualLogSource Log;
            
	private void Awake()
	{
		INSTANCE = this;
		Log = Logger;
		try
		{
			if (LobbyCompatibilityChecker.Enabled)
				LobbyCompatibilityChecker.Init();
			if (AsyncLoggerProxy.Enabled)
				AsyncLoggerProxy.WriteEvent(NAME, "Awake", "Initializing");
			Log.LogInfo("Initializing Configs");

			PluginConfig.Init();
				
			Log.LogInfo("Patching Methods");
			var harmony = new Harmony(GUID);
			harmony.PatchAll(Assembly.GetExecutingAssembly());
			ScannerPatches.InitMonoMod();
				
			Log.LogInfo(NAME + " v" + VERSION + " Loaded!");
			if (AsyncLoggerProxy.Enabled)
				AsyncLoggerProxy.WriteEvent(NAME, "Awake", "Finished Initializing");
		}
		catch (Exception ex)
		{
			Log.LogError("Exception while initializing: \n" + ex);
		}
	}
	internal static class PluginConfig
	{
		internal static class Scanner
		{
			internal static ConfigEntry<float> ScanTimer;
			internal static ConfigEntry<int> MaxScanItems;
			internal static ConfigEntry<float> NewNodeDelay;
			internal static ConfigEntry<int> NewNodeCount;
			
			internal static class Total
			{
				internal static ConfigEntry<int> MaxValue;
				internal static ConfigEntry<float> BaseSpeed;
				internal static ConfigEntry<float> ScalingFactor;
				internal static ConfigEntry<bool> UpdateDown;
			}
		}

		internal static class Performance
		{
			internal static class Cluster
			{
				internal static ConfigEntry<int> NodeCount;
				internal static ConfigEntry<int> MinItems;
				internal static ConfigEntry<float> MaxDistance;
				internal static ConfigEntry<bool> IgnoreDistance;
				internal static ConfigEntry<bool> UseClosest;
			}
			
			internal static class Cheat
			{
				internal static ConfigEntry<bool> ScanThroughWalls;
			}
			
		}
		
		internal static class Optional
		{
			internal static ConfigEntry<bool> ScanOpenDoors;
		}

		internal static class Debug
		{
			internal static ConfigEntry<bool> Verbose;
		}

		internal static void Init()
		{
			var config = INSTANCE.Config;
			//Initialize Configs
			//Scanner
			Scanner.ScanTimer = config.Bind("Scanner", "Expire after", 15.0f,
				new ConfigDescription("how long the scanned items will stay on screen ( negative means no expiration )",
					new AcceptableValueRange<float>(-1f, 20f)));
			Scanner.MaxScanItems = config.Bind("Scanner", "Max nodes", 100,
				new ConfigDescription("how many items can be shown on screen",
					new AcceptableValueRange<int>(1, 999)));
			Scanner.NewNodeDelay = config.Bind("Scanner", "New node delay", 0.03f,
				new ConfigDescription("how long to wait before showing a new node",
					new AcceptableValueRange<float>(0f, 1f)));
			Scanner.NewNodeCount = config.Bind("Scanner", "New node count", 5,
				new ConfigDescription("how many new nodes to show each cycle ( -1 means no delay )",
					new AcceptableValueRange<int>(-1, 10)));
			//Scanner.Total
			Scanner.Total.MaxValue = config.Bind("Scanner.Total", "Maximum Value", 10000, new ConfigDescription(
				"Maximum scrap value that can be visualized\nVanilla: 10000", new AcceptableValueRange<int>(20, 90000)));
			Scanner.Total.BaseSpeed = config.Bind("Scanner.Total", "Base Speed", 300f, new ConfigDescription(
				"Value per second before multipliers are applied\nVanilla: 1500", new AcceptableValueRange<float>(20f, 3000f)));
			Scanner.Total.ScalingFactor = config.Bind("Scanner.Total", "Scaling Factor", 3f, new ConfigDescription(
				"Scaling factor for the speed\nVanilla: 0", new AcceptableValueRange<float>(0f, 99f)));
			Scanner.Total.UpdateDown = config.Bind("Scanner.Total", "Update If Lower", true, new ConfigDescription(
				"Update counter on lower total\nVanilla: false"));
			//Performance.Cluster
			Performance.Cluster.NodeCount = config.Bind("Performance.Cluster", "Count", 20,
				new ConfigDescription("how many clusters to compute ( 0 means disabled )",
					new AcceptableValueRange<int>(0, 100)));
			Performance.Cluster.MinItems = config.Bind("Performance.Cluster", "Min items", 3,
				new ConfigDescription("min number of items to form a cluster",
					new AcceptableValueRange<int>(3, 10)));
			Performance.Cluster.MaxDistance = config.Bind("Performance.Cluster", "Max distance %", 8.5f,
				new ConfigDescription("% distance of screen between points in a cluster",
					new AcceptableValueRange<float>(0f, 15f)));
			Performance.Cluster.IgnoreDistance = config.Bind("Performance.Cluster", "Bypass distance", false,
				new ConfigDescription("always cluster all items on screen ( ignore distance )"));
			Performance.Cluster.UseClosest = config.Bind("Performance.Cluster", "Use Closest", false,
				new ConfigDescription("cluster node will show on the closest node instead of computing the median"));
			//Performance.Cheat
			Performance.Cheat.ScanThroughWalls = config.Bind("Performance.Cheat", "Scan through walls", false,
				new ConfigDescription("skip expensive Line Of Sight check!"));
			//Optional
			Optional.ScanOpenDoors = config.Bind("Optional", "Scan Open Doors", false,
				new ConfigDescription("allow scanning open powered doors"));

			//Debug
			Debug.Verbose = config.Bind("Debug", "verbose", false,
				new ConfigDescription("print more logs"));
			
			if (LethalConfigProxy.Enabled)
			{
				LethalConfigProxy.AddConfig(Scanner.ScanTimer);
				LethalConfigProxy.AddConfig(Scanner.MaxScanItems, true);
				LethalConfigProxy.AddConfig(Scanner.NewNodeCount);
				LethalConfigProxy.AddConfig(Scanner.NewNodeDelay);
				//
				LethalConfigProxy.AddConfig(Performance.Cluster.NodeCount, true);
				LethalConfigProxy.AddConfig(Performance.Cluster.MinItems);
				LethalConfigProxy.AddConfig(Performance.Cluster.MaxDistance);
				LethalConfigProxy.AddConfig(Performance.Cluster.IgnoreDistance);
				//
				LethalConfigProxy.AddConfig(Performance.Cheat.ScanThroughWalls);
				//
				LethalConfigProxy.AddConfig(Optional.ScanOpenDoors);
				//
				LethalConfigProxy.AddConfig(Debug.Verbose);
			}
			
			
            CleanAndSave();
		}

		internal static void CleanAndSave()
		{
			var config = INSTANCE.Config;
			//remove unused options
			PropertyInfo orphanedEntriesProp = config.GetType().GetProperty("OrphanedEntries", BindingFlags.NonPublic | BindingFlags.Instance);

			var orphanedEntries = (Dictionary<ConfigDefinition, string>)orphanedEntriesProp!.GetValue(config, null);

			orphanedEntries.Clear(); // Clear orphaned entries (Unbinded/Abandoned entries)
			config.Save(); // Save the config file
		}
            
	}

}