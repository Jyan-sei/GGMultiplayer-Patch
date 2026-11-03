using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Together;
using UnityEngine;

namespace GunsawGenetics;

// milky, orange, and voyager only scaled a hit when ActiveSpecies matched
// and the attacker was the camera. a remote claw never got the multiplier.
// scale from the attacker's species. tile hp still does not replicate.
internal static class GgCombatScaling
{
	internal static void Install(Harmony harmony)
	{
		MethodInfo attack = AccessTools.Method(typeof(Body), nameof(Body.Attack));
		MethodInfo impact = AccessTools.Method(typeof(Limb), nameof(Limb.ImpactDamage));
		int stripped = 0;
		if (attack != null)
		{
			stripped += UnpatchDeclaring(attack,
				nameof(MilkyCombatScalingPatch),
				nameof(OrangeCombatScalingPatch),
				nameof(VoyagerCombatScalingPatch));
			harmony.Patch(attack, prefix: new HarmonyMethod(typeof(GgCombatScaling), nameof(AttackPrefix)));
		}
		if (impact != null)
		{
			stripped += UnpatchDeclaring(impact, nameof(BaronTerrainDamagePatch));
			harmony.Patch(impact, postfix: new HarmonyMethod(typeof(GgCombatScaling), nameof(BaronTerrainPostfix)));
		}
		MPBridgePlugin.Log?.LogInfo(
			"[GG-MP] combat scaling per attacker species, removed " + stripped + " local-only patches");
	}

	static int UnpatchDeclaring(MethodBase original, params string[] typeNames)
	{
		var info = Harmony.GetPatchInfo(original);
		if (info == null)
			return 0;
		var set = new HashSet<string>(typeNames);
		int n = 0;
		var harmony = new Harmony(PluginInfo.GUID + ".combatunstick");
		foreach (var p in info.Prefixes)
		{
			string name = p.PatchMethod?.DeclaringType?.Name;
			if (name == null || !set.Contains(name))
				continue;
			harmony.Unpatch(original, p.PatchMethod);
			n++;
		}
		foreach (var p in info.Postfixes)
		{
			string name = p.PatchMethod?.DeclaringType?.Name;
			if (name == null || !set.Contains(name))
				continue;
			harmony.Unpatch(original, p.PatchMethod);
			n++;
		}
		return n;
	}

	static void AttackPrefix(Body __instance, AttackInfo atk)
	{
		if (__instance == null || atk == null)
			return;
		string species = SpeciesMultiplayerSync.ResolveSpeciesForBody(__instance);
		if (string.IsNullOrEmpty(species))
			return;

		switch (species)
		{
			case "Milky":
				if (atk.unarmed)
				{
					atk.damage *= 0.75f;
					atk.structuralDamage *= 0.75f;
				}
				else
				{
					atk.damage *= 1.3f;
					atk.structuralDamage *= 1.3f;
				}
				break;
			case "Orange":
				if (atk.unarmed)
				{
					atk.damage *= 1.35f;
					atk.structuralDamage *= 1.35f;
				}
				else
				{
					atk.damage *= 0.85f;
					atk.structuralDamage *= 0.85f;
				}
				break;
			case "Voyager":
				if (atk.unarmed)
					atk.damage *= 1.4f;
				break;
		}
	}

	// baron crater on the host. clients will not see the tile hp until the next world snapshot.
	const float BaronTerrainMult = 2.5f;
	const float BaronCraterMinForce = 12f;
	const float BaronCraterScale = 0.15f;
	const int BaronCraterMaxRadius = 4;

	static void BaronTerrainPostfix(Limb __instance, float force, Vector2 dir)
	{
		if (__instance == null || __instance.body == null || WorldGeneration.world == null)
			return;
		if (SpeciesMultiplayerSync.IsNetworkRunning() && !SpeciesMultiplayerSync.IsHost())
			return;
		if (!string.Equals(SpeciesMultiplayerSync.ResolveSpeciesForBody(__instance.body), "Baron", System.StringComparison.Ordinal))
			return;
		if (force <= __instance.body.jumpSpeed)
			return;

		Vector2 at = (Vector2)__instance.transform.position + dir;
		float dmg = force * BaronTerrainMult;
		if (force < BaronCraterMinForce)
		{
			WorldGeneration.world.DamageBlock(at, dmg, false, false);
			return;
		}

		int radius = Mathf.Clamp(Mathf.RoundToInt((force - BaronCraterMinForce) * BaronCraterScale), 1, BaronCraterMaxRadius);
		Vector2Int center = WorldGeneration.world.WorldToBlockPos(at);
		for (int i = -radius; i <= radius; i++)
		{
			for (int j = -radius; j <= radius; j++)
			{
				float dist = Mathf.Sqrt(i * i + j * j);
				if (dist > radius)
					continue;
				Vector2Int cell = center + new Vector2Int(i, j);
				if (WorldGeneration.world.GetBlock(cell) == 0)
					continue;
				float falloff = 1f - dist / radius;
				WorldGeneration.world.DamageBlock(cell, dmg * falloff, false, false, false);
			}
		}
	}
}
