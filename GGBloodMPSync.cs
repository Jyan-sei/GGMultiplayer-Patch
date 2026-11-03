using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GunsawGenetics;

// gg blood math reads ActiveSpecies, so every body on screen uses the local player.
// swap those patches for the species recorded on that body. baron 150, custom max, else 100.
internal static class GGBloodMPSync
{
	internal static void Install(Harmony harmony)
	{
		int stripped = 0;
		stripped += Replace(harmony, AccessTools.Method(typeof(Body), "bloodToLitersBody"), null, nameof(LitersPostfix));
		stripped += Replace(harmony, AccessTools.PropertyGetter(typeof(Body), "bloodVolumePercentage"), null, nameof(PercentagePostfix));
		stripped += Replace(harmony, AccessTools.Method(typeof(Body), "bloodToLiters"), null, nameof(BleedPostfix));
		stripped += Replace(harmony, AccessTools.Method(typeof(Body), "HandleCirculation"), nameof(CirculationPrefix), nameof(CirculationPostfix));
		stripped += Replace(harmony, AccessTools.Method(typeof(Body), "HandleCirculation"), null, nameof(SpeciesHeartPostfix));
		stripped += Replace(harmony, AccessTools.Method(typeof(WoundView), "UpdateView"), null, nameof(WeightPostfix));
		var ecg = AccessTools.Method(typeof(Body), "GetECGHeight");
		if (ecg != null)
			harmony.Patch(ecg, postfix: new HarmonyMethod(typeof(GGBloodMPSync), nameof(ClampEcg)));
		var trace = AccessTools.Method(typeof(ECGVisualizer), "Update");
		if (trace != null)
			harmony.Patch(trace, prefix: new HarmonyMethod(typeof(GGBloodMPSync), nameof(GuardEcgTrace)));
		MPBridgePlugin.Log?.LogInfo($"[GG-MP] blood sync per body, removed {stripped} ActiveSpecies patches");
	}

	static readonly FieldInfo EcgWidth = AccessTools.Field(typeof(ECGVisualizer), "width");
	static readonly FieldInfo EcgHeight = AccessTools.Field(typeof(ECGVisualizer), "height");
	static readonly FieldInfo EcgLastY = AccessTools.Field(typeof(ECGVisualizer), "lastY");

	// the trace texture only has room for a sample in [-1, 1]. leapy regen drives the
	// heart curve past that, and the visualizer then indexes off the pixel grid every frame.
	static void ClampEcg(ref float __result)
	{
		if (float.IsNaN(__result) || float.IsInfinity(__result))
			__result = 0f;
		else
			__result = Mathf.Clamp(__result, -1f, 1f);
	}

	static bool GuardEcgTrace(ECGVisualizer __instance)
	{
		if (__instance == null || EcgWidth == null || EcgHeight == null || EcgLastY == null)
			return true;
		int width = (int)EcgWidth.GetValue(__instance);
		int height = (int)EcgHeight.GetValue(__instance);
		if (width <= 0 || height <= 1)
			return false;
		int last = (int)EcgLastY.GetValue(__instance);
		if (last < 0 || last >= height)
			EcgLastY.SetValue(__instance, height / 2);
		return true;
	}

	static int Replace(Harmony harmony, MethodBase original, string prefix, string postfix)
	{
		if (original == null)
		{
			MPBridgePlugin.Log?.LogWarning("[GG-MP] blood method missing, skip");
			return 0;
		}

		int n = StripActiveSpeciesPatches(original);
		harmony.Patch(
			original,
			prefix: prefix == null ? null : new HarmonyMethod(typeof(GGBloodMPSync), prefix),
			postfix: postfix == null ? null : new HarmonyMethod(typeof(GGBloodMPSync), postfix));
		return n;
	}

	static int StripActiveSpeciesPatches(MethodBase original)
	{
		var info = Harmony.GetPatchInfo(original);
		if (info == null)
			return 0;

		var methods = new List<MethodInfo>();
		foreach (var p in info.Prefixes)
			if (IsActiveSpeciesPatch(p.PatchMethod))
				methods.Add(p.PatchMethod);
		foreach (var p in info.Postfixes)
			if (IsActiveSpeciesPatch(p.PatchMethod))
				methods.Add(p.PatchMethod);

		if (methods.Count == 0)
			return 0;

		var harmony = new Harmony(PluginInfo.GUID + ".bloodunstick");
		foreach (var method in methods)
			harmony.Unpatch(original, method);
		return methods.Count;
	}

	static bool IsActiveSpeciesPatch(MethodInfo method)
	{
		var name = method?.DeclaringType?.Name;
		if (string.IsNullOrEmpty(name))
			return false;
		return name.StartsWith("BaronBlood")
			|| name == "SpeciesWeightDisplayPatch"
			|| name == "DuneHeatHeartRatePatch"
			|| name == "RozaColdHeartRatePatch"
			|| name == "LeapyRegrowthHeartRatePatch";
	}

	internal static float MaxFor(Body body)
	{
		if (body == null)
			return 100f;
		string mapped = SpeciesMultiplayerSync.GetSpeciesForBody(body);
		string species = string.IsNullOrEmpty(mapped) ? SpeciesAbilityManager.ActiveSpecies : mapped;
		if (string.IsNullOrEmpty(species))
			return 100f;
		return BaronBloodScaling.GetMax(species);
	}

	static void LitersPostfix(Body __instance, ref float __result)
	{
		float max = MaxFor(__instance);
		if (Mathf.Approximately(max, 100f))
			return;
		__result = __instance.bloodVolumePercentage * 5f * (max / 100f);
	}

	static void PercentagePostfix(Body __instance, ref float __result)
	{
		float max = MaxFor(__instance);
		if (Mathf.Approximately(max, 100f))
			return;
		if (BaronBloodScaling.InCirculationRescale)
			return;
		__result = 0.5f + __instance.bloodVolume / (max * 2f);
	}

	static void BleedPostfix(Body __instance, float amount, ref float __result)
	{
		float max = MaxFor(__instance);
		if (Mathf.Approximately(max, 100f))
			return;
		__result = amount * 0.025f * (max / 100f);
	}

	static void CirculationPrefix(Body __instance, out float __state)
	{
		__state = float.NaN;
		float max = MaxFor(__instance);
		if (Mathf.Approximately(max, 100f))
			return;
		__state = __instance.bloodVolume;
		__instance.bloodVolume = __state / max * 100f;
		BaronBloodScaling.InCirculationRescale = true;
	}

	static void CirculationPostfix(Body __instance, float __state)
	{
		if (float.IsNaN(__state))
			return;
		BaronBloodScaling.InCirculationRescale = false;
		float max = MaxFor(__instance);
		__instance.bloodVolume = __instance.bloodVolume / 100f * max;
	}

	// gg's dune/roza/leapy heart patches key off the local species and then edit every body.
	static void SpeciesHeartPostfix(Body __instance)
	{
		if (__instance == null)
			return;
		string species = SpeciesOf(__instance);
		if (species == "Dune" && __instance.temperature > 38.5f && __instance.fibrillationProgress <= 0f)
		{
			float drop = (__instance.temperature - 37f) * 0.5f * 0.12f;
			__instance.heartRate -= drop * Time.deltaTime;
			return;
		}
		if (species == "Roza" && __instance.temperature < 34f && __instance.fibrillationProgress <= 0f)
		{
			float rise = (37f - __instance.temperature) * 0.5f * 0.35f;
			__instance.heartRate += rise * Time.deltaTime;
			return;
		}
		if (species != "Leapy")
			return;
		var mgr = SpeciesAbilityManager.Instance;
		if (mgr == null || !mgr.leapyRegrowthHolding)
			return;
		float heal = mgr.GetLeapyActiveHealWeight();
		if (heal <= 0f)
			return;
		__instance.heartRate += 3f * heal * Time.deltaTime;
	}

	static readonly FieldInfo WeightTextField = AccessTools.Field(typeof(WoundView), "weightText");
	static readonly PropertyInfo WeightTextProp = WeightTextField?.FieldType.GetProperty("text");

	static void WeightPostfix(WoundView __instance)
	{
		if (__instance == null || __instance.body == null || WeightTextField == null || WeightTextProp == null)
			return;
		object label = WeightTextField.GetValue(__instance);
		if (label == null)
			return;
		float kg = __instance.body.weightOffset * 0.34f + 50f + WeightBonus(SpeciesOf(__instance.body));
		WeightTextProp.SetValue(label, kg.ToString("0.#") + "kg");
	}

	static float WeightBonus(string species)
	{
		switch (species)
		{
			case "Orange": return 15f;
			case "Milky": return -10f;
			case "Leapy": return -5f;
			case "Chompy": return 10f;
			case "Roza": return 5f;
			case "Voyager": return -5f;
			case "Baron": return 30f;
			case "Chik": return -15f;
			case "Crystal": return 10f;
			case "Velvet": return 5f;
			case "Shelly": return 10f;
			case "Dune": return 5f;
			case "Albino": return -5f;
		}
		var custom = CustomSpeciesManager.Find(species);
		return custom != null ? custom.WeightBonusKg : 0f;
	}

	static string SpeciesOf(Body body)
	{
		string mapped = SpeciesMultiplayerSync.GetSpeciesForBody(body);
		if (!string.IsNullOrEmpty(mapped))
			return mapped;
		return SpeciesAbilityManager.ActiveSpecies;
	}
}
