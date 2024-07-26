using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Mono.Cecil;
using FieldAttributes = Mono.Cecil.FieldAttributes;

namespace QuickItemScan.Preloader;

internal class QuickItemScan
{
    internal static ManualLogSource Log { get; } = Logger.CreateLogSource(nameof(QuickItemScan));
        
    public static IEnumerable<string> TargetDLLs { get; } = new string[] { "Assembly-CSharp.dll" };

    private static readonly string MainDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        
    public static void Patch(AssemblyDefinition assembly)
    {
        var logHandler = (bool fail, string message) =>
        {
            if (fail)
                Log.LogWarning(message);
            Log.LogInfo(message);
        };
            
        Log.LogWarning($"Patching {assembly.Name.Name}");
        if (assembly.Name.Name == "Assembly-CSharp")
        {
            foreach (TypeDefinition type in assembly.MainModule.Types)
            {
                if (type.FullName == "ScanNodeProperties")
                {
                    type.AddMethod("Awake", logCallback:logHandler);
                }else if (type.FullName == "GameNetcodeStuff.PlayerControllerB")
                {
                    type.AddField(FieldAttributes.Private,"CameraFOV", type.Module.TypeSystem.Single, logCallback:logHandler);
                }
            }
        }
			
        if (!PluginConfig.Enabled.Value) 
            return;
            
        var outputAssembly = $"{PluginConfig.OutputPath.Value}/{assembly.Name.Name}{PluginConfig.OutputExtension.Value}";
        Log.LogWarning($"Saving modified Assembly to {outputAssembly}");
        assembly.Write(outputAssembly);
    }
        
    // Cannot be renamed, method name is important
    public static void Initialize()
    {
        Log.LogInfo($"Prepatcher Started");
        PluginConfig.Init();
    }

    // Cannot be renamed, method name is important
    public static void Finish()
    {
        Log.LogInfo($"Prepatcher Finished");
    }
	
    public static class PluginConfig
    {
        public static void Init()
        {
            var config = new ConfigFile(Utility.CombinePaths(MainDir, "Development.cfg"), true);
            //Initialize Configs
            Enabled = config.Bind("DevelOptions", "Enabled", false, "Enable development dll output");
            OutputPath = config.Bind("DevelOptions", "OutputPath", MainDir, "Folder where to write the modified dlls");
            OutputExtension = config.Bind("DevelOptions", "OutputExtension", ".pdll", "Extension to use for the modified dlls\n( Do not use .dll if outputting inside the BepInEx folders )");

            //remove unused options
            PropertyInfo orphanedEntriesProp = config.GetType()
                .GetProperty("OrphanedEntries", BindingFlags.NonPublic | BindingFlags.Instance);

            var orphanedEntries = (Dictionary<ConfigDefinition, string>)orphanedEntriesProp!.GetValue(config, null);

            orphanedEntries.Clear(); // Clear orphaned entries (Unbinded/Abandoned entries)
            config.Save(); // Save the config file
        }

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<string> OutputPath;
        internal static ConfigEntry<string> OutputExtension;
    }

}