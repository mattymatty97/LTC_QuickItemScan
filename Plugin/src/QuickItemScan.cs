using System;
using System.Collections.Generic;
using System.Reflection;
using QuickItemScan.Dependency;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace QuickItemScan;

[BepInPlugin(GUID, NAME, VERSION)]
[BepInDependency("BMX.LobbyCompatibility", Flags:BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("ainavt.lc.lethalconfig", Flags:BepInDependency.DependencyFlags.SoftDependency)]
internal class QuickItemScan : BaseUnityPlugin
{
		
	public static QuickItemScan INSTANCE { get; private set; }
		
	public const string GUID = "mattymatty.QuickItemScan";
	public const string NAME = "QuickItemScan";
	public const string VERSION = "0.0.2";

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
		}

		internal static class Performance
		{
			internal static class Cluster
			{
				internal static ConfigEntry<int> ClusterCount;
				internal static ConfigEntry<int> ClusterMin;
				internal static ConfigEntry<float> ClusterDistance;
			}
			
			internal static class Cheat
			{
				internal static ConfigEntry<bool> ScanThroughWalls;
			}
			
		}

		internal static ConfigEntry<bool> Verbose;
	        
		internal static void Init()
		{
			var config = INSTANCE.Config;
			//Initialize Configs
			Scanner.ScanTimer = config.Bind("Scanner", "Scan duration", 5.0f,
				new ConfigDescription("how long the scanned items will stay on screen ( negative means no expiration )",
					new AcceptableValueRange<float>(-1f, 20f)));
			Scanner.MaxScanItems = config.Bind("Scanner", "Max items", 100,
				new ConfigDescription("how many items can be shown on screen",
					new AcceptableValueRange<int>(1, 999)));
			Scanner.NewNodeDelay = config.Bind("Scanner", "New node delay", 0.03f,
				new ConfigDescription("how long to wait before showing a new node",
					new AcceptableValueRange<float>(0f, 1f)));
			Scanner.NewNodeCount = config.Bind("Scanner", "New node count", 5,
				new ConfigDescription("how many nodes to show each cycle ( -1 means no delay )",
					new AcceptableValueRange<int>(-1, 10)));
			Performance.Cluster.ClusterCount = config.Bind("Performance.Cluster", "Count", 20,
				new ConfigDescription("how many clusters to compute ( 0 means disabled )",
					new AcceptableValueRange<int>(0, 100)));
			Performance.Cluster.ClusterMin = config.Bind("Performance.Cluster", "Min items", 3,
				new ConfigDescription("min number of items to form a cluster",
					new AcceptableValueRange<int>(3, 10)));
			Performance.Cluster.ClusterDistance = config.Bind("Performance.Cluster", "Max distance", 60f,
				new ConfigDescription("max distance in pixels between points in a cluster",
					new AcceptableValueRange<float>(0f, 300f)));
			Performance.Cheat.ScanThroughWalls = config.Bind("Performance.Cheat", "Scan through walls", false,
				new ConfigDescription("skip expensive Line Of Sight check!"));
                
			Verbose = config.Bind("Debug", "verbose", false,
				new ConfigDescription("print more logs"));
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