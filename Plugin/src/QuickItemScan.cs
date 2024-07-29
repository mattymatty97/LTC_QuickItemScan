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
		internal static ConfigEntry<float> ScanTimer;
		internal static ConfigEntry<int> MaxScanItems;
		internal static ConfigEntry<float> NewNodeDelay;
		internal static ConfigEntry<int> NewNodeCount;
		internal static ConfigEntry<int> ClusterCount;
		internal static ConfigEntry<int> ClusterMin;
		internal static ConfigEntry<float> ClusterDistance;
	        
		internal static ConfigEntry<bool> Verbose;
	        
		internal static void Init()
		{
			var config = INSTANCE.Config;
			//Initialize Configs
			ScanTimer = config.Bind("Scanner", "scan_duration", 5.0f,
				new ConfigDescription("how long the scanned items will stay on screen ( negative means no expiration )",
					new AcceptableValueRange<float>(-1f, 20f)));
			MaxScanItems = config.Bind("Scanner", "max_items", 100,
				new ConfigDescription("how many items can be shown on screen",
					new AcceptableValueRange<int>(1, 999)));
			NewNodeDelay = config.Bind("Scanner", "new_node_delay", 0.03f,
				new ConfigDescription("how long to wait before showing a new node",
					new AcceptableValueRange<float>(0f, 1f)));
			NewNodeCount = config.Bind("Scanner", "new_node_count", 5,
				new ConfigDescription("how many nodes to show each cycle ( -1 means no delay )",
					new AcceptableValueRange<int>(-1, 10)));
			ClusterCount = config.Bind("Scanner", "cluster_count", 20,
				new ConfigDescription("how many clusters to compute ( 0 means disabled )",
					new AcceptableValueRange<int>(0, 100)));
			ClusterMin = config.Bind("Scanner", "cluster_min_items", 3,
				new ConfigDescription("min number of items to form a cluster",
					new AcceptableValueRange<int>(3, 10)));
			ClusterDistance = config.Bind("Scanner", "cluster_distance", 60f,
				new ConfigDescription("max distance in pixels between points in a cluster",
					new AcceptableValueRange<float>(0f, 300f)));
                
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