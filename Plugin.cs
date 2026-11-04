using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;

namespace GunsawGenetics;

// same guid as the stock bridge, newer version, so chainloader loads this file instead.
// gg still asks for GunsawGeneticsMPBridge. GgBridgeHooks points that name here.
[BepInPlugin(PluginInfo.GUID, PluginInfo.Name, PluginInfo.Version)]
[BepInDependency("GunsawGenetics", BepInDependency.DependencyFlags.HardDependency)]
[BepInDependency("CasualtiesMP", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("spritereplacer", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("noah.casualtiesunknown.multiplayerspritereplacer", BepInDependency.DependencyFlags.SoftDependency)]
public sealed class MPBridgePlugin : BaseUnityPlugin
{
	internal static MPBridgePlugin Instance;
	internal static ManualLogSource Log;

	internal static bool usingMSR;


    void Awake()
	{
		Instance = this;
		Log = Logger;

		// still install when together is absent. solo gg looks the type up by name.
		GgBridgeHooks.Install();

		if (!Chainloader.PluginInfos.ContainsKey("CasualtiesMP"))
		{
			Log.LogInfo("[GG-MP] Together not found - bridge idle; GunsawGeneticsPlugin loaded");
			return;
		}

		string togetherVer = "unknown";
		if (Chainloader.PluginInfos.TryGetValue("CasualtiesMP", out var info) && info?.Metadata != null)
			togetherVer = info.Metadata.Version?.ToString() ?? "unknown";

		SpeciesMultiplayerSync.Initialize();
		GgSpecies.NoteReplacer();
		TalkPatch.Initialize();
        Log.LogInfo(
			$"[GG-MP] Bridge attached to CasualtiesMP v{togetherVer} " +
			$"(ggmpcompat v{PluginInfo.Version}; GUID supersedes stock Krok bridge 1.0.0)");
	}

	void Update()
	{
		if (!Chainloader.PluginInfos.ContainsKey("CasualtiesMP"))
			return;
		SpeciesGate.Tick();
	}

	void LateUpdate()
	{
		if (!Chainloader.PluginInfos.ContainsKey("CasualtiesMP"))
			return;
		if (!usingMSR)
		{
			SpeciesMultiplayerSync.RefreshSprites();
		}
		SpeciesContext.Tick();
		TraderMp.TickPending();
	}
}
