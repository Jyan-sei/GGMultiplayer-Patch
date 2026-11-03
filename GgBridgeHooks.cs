using System;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace GunsawGenetics;

// gg looks up SpeciesMultiplayerSync on GunsawGeneticsMPBridge, and it only
// calls across when it thinks KrokoshaCasualtiesMP is loaded. both point here.
internal static class GgBridgeHooks
{
	static bool _resolveHooked;
	static Harmony _harmony;

	internal static void Install()
	{
		if (!_resolveHooked)
		{
			AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
			_resolveHooked = true;
		}

		_harmony ??= new Harmony(PluginInfo.GUID + ".ggbridge");
		try
		{
			_harmony.PatchAll(typeof(GgBridgeHooks).Assembly);
		}
		catch (Exception e)
		{
			// a bad patch must not skip species sync
			MPBridgePlugin.Log?.LogError("[GG-MP] PatchAll failed: " + e.Message);
		}

		try
		{
			SpeciesGate.InstallSoloSilence(_harmony);
		}
		catch (Exception e)
		{
			MPBridgePlugin.Log?.LogError("[GG-MP] solo picker silence failed: " + e.Message);
		}

		try
		{
			GGBloodMPSync.Install(_harmony);
		}
		catch (Exception e)
		{
			MPBridgePlugin.Log?.LogError("[GG-MP] blood sync install failed: " + e.Message);
		}

		try
		{
			GGWoundViewSync.Install(_harmony);
		}
		catch (Exception e)
		{
			MPBridgePlugin.Log?.LogError("[GG-MP] wound view install failed: " + e.Message);
		}

		try
		{
			OrangeArms.Install(_harmony);
		}
		catch (Exception e)
		{
			MPBridgePlugin.Log?.LogError("[GG-MP] orange limb sync install failed: " + e.Message);
		}

		try
		{
			TraderMp.Install(_harmony);
		}
		catch (Exception e)
		{
			MPBridgePlugin.Log?.LogError("[GG-MP] trader sell hook failed: " + e.Message);
		}

		try
		{
			SpeciesContext.Install(_harmony);
		}
		catch (Exception e)
		{
			MPBridgePlugin.Log?.LogError("[GG-MP] species context install failed: " + e.Message);
		}

		try
		{
			GgHeartbeatFix.Install(_harmony);
		}
		catch (Exception e)
		{
			MPBridgePlugin.Log?.LogError("[GG-MP] heartbeat gate install failed: " + e.Message);
		}

		try
		{
			GgCombatScaling.Install(_harmony);
		}
		catch (Exception e)
		{
			MPBridgePlugin.Log?.LogError("[GG-MP] combat scaling install failed: " + e.Message);
		}

		try
		{
			GgDietMp.Install(_harmony);
		}
		catch (Exception e)
		{
			MPBridgePlugin.Log?.LogError("[GG-MP] diet/hunger install failed: " + e.Message);
		}

		try
		{
			VoyagerCloak.Install(_harmony);
		}
		catch (Exception e)
		{
			MPBridgePlugin.Log?.LogError("[GG-MP] voyager cloak install failed: " + e.Message);
		}

		MPBridgePlugin.Log?.LogInfo(
			"[GG-MP] Hooked GG MPBridge.ResolveType - ggmpcompat SpeciesMultiplayerSync; " +
			"IsKrokMpPresent also true when CasualtiesMP is loaded.");
	}

	static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
	{
		var requested = new AssemblyName(args.Name).Name;
		if (string.Equals(requested, PluginInfo.StockAssemblyName, StringComparison.OrdinalIgnoreCase))
			return typeof(SpeciesMultiplayerSync).Assembly;
		return null;
	}

	[HarmonyPatch]
	static class ResolveTypePatch
	{
		static MethodBase TargetMethod() =>
			AccessTools.Method(typeof(MPBridge), "ResolveType");

		static bool Prefix(ref Type __result)
		{
			__result = typeof(SpeciesMultiplayerSync);
			return false;
		}
	}

	[HarmonyPatch]
	static class IsKrokMpPresentPatch
	{
		static MethodBase TargetMethod() =>
			AccessTools.Method(typeof(MPBridge), "IsKrokMpPresent");

		static bool Prefix(ref bool __result)
		{
			// gg only calls across when this is true. together stands in for krok.
			if (Chainloader.PluginInfos.ContainsKey("CasualtiesMP"))
			{
				__result = true;
				return false;
			}
			return true;
		}
	}
}
