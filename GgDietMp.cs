using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GunsawGenetics;

// diet, eat cap, and the hunger moodles read ActiveSpecies. eat and drink already
// push the eater for the call. use-item tracking and the host tick still see the viewer.
internal static class GgDietMp
{
	internal static void Install(Harmony harmony)
	{
		MethodInfo eat = AccessTools.Method(typeof(Body), nameof(Body.Eat));
		MethodInfo addMoodle = AccessTools.Method(typeof(MoodleManager), nameof(MoodleManager.AddMoodle));
		MethodInfo updateView = AccessTools.Method(typeof(WoundView), nameof(WoundView.UpdateView));
		MethodInfo eatGood = AccessTools.Method(typeof(Talker), nameof(Talker.EatGood));
		MethodInfo eatMediocre = AccessTools.Method(typeof(Talker), nameof(Talker.EatMediocre));

		int stripped = 0;
		if (eat != null)
		{
			stripped += UnpatchDeclaring(eat, nameof(DietPenaltyPatch), nameof(SpeciesEatCapPatch));
			harmony.Patch(
				eat,
				prefix: new HarmonyMethod(typeof(GgDietMp), nameof(EatPrefix)),
				postfix: new HarmonyMethod(typeof(GgDietMp), nameof(EatPostfix)));
		}
		if (addMoodle != null)
		{
			stripped += UnpatchDeclaring(addMoodle, nameof(SpeciesHungerThirstMoodlePatch));
			harmony.Patch(addMoodle, prefix: new HarmonyMethod(typeof(GgDietMp), nameof(MoodlePrefix)));
		}
		if (updateView != null)
		{
			stripped += UnpatchDeclaring(updateView, nameof(SpeciesHungerThirstUIPatch));
			harmony.Patch(updateView, postfix: new HarmonyMethod(typeof(GgDietMp), nameof(HungerUiPostfix)));
		}
		if (eatGood != null)
			harmony.Patch(eatGood, prefix: new HarmonyMethod(typeof(GgDietMp), nameof(EatGoodPrefix)));
		if (eatMediocre != null)
			harmony.Patch(eatMediocre, prefix: new HarmonyMethod(typeof(GgDietMp), nameof(EatMediocrePrefix)));

		// the host confirms a remote use by calling the action itself. that skips UseItem,
		// so the diet tracker never stored the item and the eat looked legal.
		MethodInfo relay = AccessTools.Method(AccessTools.TypeByName("Together.ItemSync"), "BetterUseItem");
		if (relay != null)
			harmony.Patch(relay,
				prefix: new HarmonyMethod(typeof(GgDietMp), nameof(RelayUsePrefix)),
				postfix: new HarmonyMethod(typeof(GgDietMp), nameof(RelayUsePostfix)));

		// yellow flesh checks ActiveSpecies after Eat returns, which has already
		// popped back to the viewer. a milky host then makes a dune sick.
		MethodInfo setup = AccessTools.Method(typeof(Item), "SetupItems");
		if (setup != null)
			harmony.Patch(setup, postfix: new HarmonyMethod(typeof(GgDietMp), nameof(FleshActions)) { priority = Priority.Last });
		FleshActions();

		MPBridgePlugin.Log?.LogInfo(
			"[GG-MP] diet/hunger per-body species, removed " + stripped + " ActiveSpecies patches"
			+ (relay != null ? ", relay use tracks the item" : ""));
	}

	static int UnpatchDeclaring(MethodBase original, params string[] typeNames)
	{
		var info = Harmony.GetPatchInfo(original);
		if (info == null)
			return 0;
		var set = new HashSet<string>(typeNames);
		int n = 0;
		var harmony = new Harmony(PluginInfo.GUID + ".dietunstick");
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

	static string SpeciesOf(Body body)
	{
		return SpeciesMultiplayerSync.ResolveSpeciesForBody(body) ?? "";
	}

	static void RelayUsePrefix(Item item)
	{
		if (item == null || item.Stats == null || !item.Stats.usable || item.Stats.wearable)
			return;
		DietSystem.PendingItemId = item.id;
		DietSystem.PendingEatItem = item;
	}

	static void RelayUsePostfix()
	{
		DietSystem.PendingItemId = null;
		DietSystem.PendingEatItem = null;
		DietSystem.PendingLiquidMeatOverride = null;
	}

	static void FleshActions()
	{
		SetUse("experimentflesh", EatYellowFlesh);
		SetUse("internalorgans", EatOrgans);
	}

	static void SetUse(string id, ItemInfo.Use action)
	{
		try
		{
			if (Item.GlobalItems != null && Item.GlobalItems.TryGetValue(id, out ItemInfo info) && info != null)
				info.useAction = action;
		}
		catch
		{
		}
	}

	static void EatYellowFlesh(Body body, Item item)
	{
		if (body == null || item == null)
			return;
		body.Eat(12.5f, 1.5f);
		item.condition -= 1f;
		string species = SpeciesOf(body);
		if (species != "Velvet" && species != "Dune")
		{
			if (body.talker != null)
				body.talker.EatBad();
			body.happiness -= 6f;
			body.sicknessAmount += 16f;
		}
		else
		{
			if (body.talker != null)
				body.talker.EatGood();
			if (species == "Dune")
				body.happiness += 2f;
		}
		Sound.Play("eatFlesh", body.transform.position);
	}

	static void EatOrgans(Body body, Item item)
	{
		if (body == null || item == null)
			return;
		body.Eat(15f, 3f);
		item.condition -= 0.34f;
		string species = SpeciesOf(body);
		if (species != "Velvet" && species != "Dune")
		{
			if (body.talker != null)
				body.talker.EatBad();
			body.happiness -= 18f;
			body.sicknessAmount += 32f;
		}
		else
		{
			if (body.talker != null)
				body.talker.EatMediocre();
			body.sicknessAmount += 16f;
			if (species == "Dune")
				body.happiness += 5f;
		}
		Sound.Play("eatFlesh", body.transform.position);
	}

	// replaces the diet penalty and the eat cap. a species with a hunger offset
	// eats here, with that cap, and the original Eat does not run.
	static bool EatPrefix(Body __instance, ref float hungerAmount, ref float weightGain, out float __state)
	{
		__state = 0f;
		if (__instance == null)
			return true;

		string species = SpeciesOf(__instance);
		string pendingItemId = DietSystem.PendingItemId;
		DietSystem.PendingItemId = null;
		DietSystem.PendingEatItem = null;
		bool? pendingLiquidMeatOverride = DietSystem.PendingLiquidMeatOverride;
		DietSystem.PendingLiquidMeatOverride = null;

		if (species == "Dune")
			weightGain *= 0.6f;

		DietSystem.PendingCannibalism = species == "Roza"
			&& (pendingItemId == "experimentflesh" || pendingItemId == "internalorgans");
		if (DietSystem.PendingCannibalism)
			__instance.happiness -= 7.5f;

		if (__instance.mindWipe != null)
			DietSystem.PendingDietViolation = false;
		else
		{
			DietSystem.PendingDietViolation = pendingLiquidMeatOverride.HasValue
				? DietSystem.ViolatesDietLiquid(species, pendingLiquidMeatOverride.Value)
				: DietSystem.ViolatesDiet(species, pendingItemId);
			if (DietSystem.PendingDietViolation)
			{
				DietType diet = DietSystem.GetDiet(species);
				if (diet == DietType.SemiHerbivore || diet == DietType.SemiCarnivore)
				{
					hungerAmount *= 0.8f;
					__instance.happiness -= 1f;
					__instance.sicknessAmount += 3f;
				}
				else
				{
					hungerAmount *= 0.65f;
					__instance.happiness -= 3f;
					__instance.sicknessAmount += 6f;
				}
			}
		}

		float offset = HungerThirstCapConfig.GetOffset(species);
		if (offset == 0f)
		{
			// no eat-cap on this species. dune still gets the overflow sickness.
			if (species == "Dune")
			{
				float hunger = __instance.hunger;
				float gained = Mathf.Min(hunger + hungerAmount, 125f + HungerThirstCapConfig.GetOffset(species)) - hunger;
				float overflow = hungerAmount - gained;
				if (overflow > 0f)
					__state = overflow * 1.2f;
			}
			return true;
		}

		// hunger offset: apply the eat here and stop the original.
		float cap = 125f + offset;
		__instance.DropItem(2);
		__instance.eatTime = 0.5f;
		if (__instance.limbs != null && __instance.limbs.Length > 0
			&& (__instance.limbs[0].dislocated || __instance.disfigured))
		{
			__instance.limbs[0].pain += hungerAmount * 0.5f;
			hungerAmount *= 0.75f;
			weightGain *= 0.75f;
		}
		float before = __instance.hunger;
		__instance.hunger += hungerAmount;
		if (__instance.hunger > cap)
			__instance.hunger = cap;
		float applied = __instance.hunger - before;
		__instance.sicknessAmount += (hungerAmount - applied) * 1.2f;
		if (hungerAmount - applied > 3f && Random.Range(0f, 1f) < 0.2f && __instance.vomiter != null)
			__instance.vomiter.Vomit();
		if (__instance.hunger > 90f + offset && Random.value < 0.1f)
		{
			var burp = AccessTools.Field(typeof(Body), "burpTimer");
			if (burp != null)
				burp.SetValue(__instance, Random.Range(5f, 10f));
		}
		if (__instance.dirtyness >= 75f && Random.value < 0.1f)
			__instance.sicknessAmount += 8f;
		__instance.weightOffset += weightGain;
		if (PlayerCamera.main != null)
			PlayerCamera.main.caloriesConsumed += Mathf.RoundToInt(weightGain * 210f);
		return false;
	}

	static void EatPostfix(Body __instance, float __state)
	{
		if (__state > 0f)
			__instance.sicknessAmount -= __state * 0.8f;
	}

	// stock remaps the satiated moodles from ActiveSpecies. use this body's species.
	static bool MoodlePrefix(ref int intensity, string icon, ref string name, ref string desc, ref bool critical)
	{
		Body body = SpeciesAbilityManager.PlayerBody;
		if (body == null && PlayerCamera.main != null)
			body = PlayerCamera.main.body;
		string species = SpeciesOf(body);
		float offset = HungerThirstCapConfig.GetOffset(species);
		if (offset == 0f || body == null)
			return true;

		if (icon == "hunger" && intensity >= 4)
		{
			if (body.hunger > 120f + offset)
			{
				intensity = 5;
				name = Locale.GetMoodle("hunger6");
				desc = Locale.GetMoodle("hunger6dsc");
				critical = false;
				return true;
			}
			if (body.hunger > 100f + offset)
			{
				intensity = 4;
				name = Locale.GetMoodle("hunger5");
				desc = Locale.GetMoodle("hunger5dsc");
				critical = false;
				return true;
			}
			return false;
		}
		if (icon == "overhydrated")
		{
			if (body.thirst > 175f + offset)
			{
				intensity = 3;
				name = Locale.GetMoodle("overhydrated3");
				desc = Locale.GetMoodle("overhydrated3dsc");
				critical = true;
				return true;
			}
			if (body.thirst > 125f + offset)
			{
				intensity = 1;
				name = Locale.GetMoodle("overhydrated2");
				desc = Locale.GetMoodle("overhydrated2dsc");
				critical = false;
				return true;
			}
			if (body.thirst > 100f + offset)
			{
				intensity = 0;
				name = Locale.GetMoodle("overhydrated1");
				desc = Locale.GetMoodle("overhydrated1dsc");
				critical = false;
				return true;
			}
			return false;
		}
		return true;
	}

	static void HungerUiPostfix(WoundView __instance)
	{
		if (__instance == null || __instance.body == null)
			return;
		float offset = HungerThirstCapConfig.GetOffset(SpeciesOf(__instance.body));
		if (offset == 0f)
			return;
		try
		{
			Body body = __instance.body;
			float scale = 100f + offset;
			SetFill(__instance.foodFill, body.hunger / scale);
			SetFill(__instance.overfeedFill, (body.hunger - scale) * 0.01f);
			SetFill(__instance.overhydrateFill, (body.thirst - scale) * 0.01f);
			SetFill(__instance.thirstFill, body.thirst / scale);
		}
		catch
		{
		}
	}

	static void SetFill(Object target, float value)
	{
		if (target == null)
			return;
		GameObject go = target as GameObject;
		if (go == null && target is Component c)
			go = c.gameObject;
		if (go == null)
			return;
		if (go.TryGetComponent(out FilledImagePP fill))
			fill.fillAmount = value;
		else if (go.TryGetComponent(out UnityEngine.UI.Image img))
			img.fillAmount = Mathf.Clamp01(value);
	}

	// vanilla says "full" at hunger 125. the moodles wait for 100 or 120 plus the offset.
	// orange (+30) talked full between 125 and 130 with no moodle.
	static bool EatGoodPrefix(Talker __instance)
	{
		if (__instance == null || __instance.body == null)
			return true;
		// a diet violation already turned this into the bad line. leave it.
		if (DietSystem.PendingDietViolation)
			return true;
		float offset = HungerThirstCapConfig.GetOffset(SpeciesOf(__instance.body));
		if (offset == 0f)
			return true;
		float fullAt = 125f + offset;
		if (__instance.body.hunger >= fullAt)
			__instance.Talk(Locale.GetCharacter("full"));
		else if (__instance.body.hunger < 100f)
			__instance.Talk(Locale.GetCharacter("eatGood"));
		return false;
	}

	static bool EatMediocrePrefix(Talker __instance)
	{
		if (__instance == null || __instance.body == null)
			return true;
		if (DietSystem.PendingDietViolation)
			return true;
		float offset = HungerThirstCapConfig.GetOffset(SpeciesOf(__instance.body));
		if (offset == 0f)
			return true;
		float fullAt = 125f + offset;
		if (__instance.body.hunger >= fullAt)
			__instance.Talk(Locale.GetCharacter("full"));
		else
			__instance.Talk(Locale.GetCharacter("eatMediocre"));
		return false;
	}
}
