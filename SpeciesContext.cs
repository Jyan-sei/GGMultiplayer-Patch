using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Together;
using UnityEngine;

namespace GunsawGenetics;

// gg reads one ActiveSpecies for every body the host ticks.
// push that body's species for the call, then put the local one back.
internal static class SpeciesContext
{
	struct Snap
	{
		public string Species;
		public Body Player;
		public float Pos;
		public float Neg;
		public float Ceil;
		public float Floor;
		public float CannibalAt;
		public bool Trauma;
		public bool Cannibal;
		public bool SleepReset;
		public bool LeapyHold;
	}

	static readonly List<Snap> _stack = new List<Snap>();
	static readonly FieldInfo HarmBody = AccessTools.Field(typeof(SelfHarmer), "body");
	static readonly FieldInfo LeapyHolding = AccessTools.Field(typeof(SpeciesAbilityManager), "leapyRegrowthHolding");
	static readonly FieldInfo RozaHolding = AccessTools.Field(typeof(SpeciesAbilityManager), "rozaHolding");
	static readonly FieldInfo Levitating = AccessTools.Field(typeof(SpeciesAbilityManager), "isLevitating");
	static readonly FieldInfo LevitateTarget = AccessTools.Field(typeof(SpeciesAbilityManager), "levitationTarget");
	static readonly Action<SpeciesAbilityManager> LeapyBone = Calls.Bind<Action<SpeciesAbilityManager>>(AccessTools.Method(typeof(SpeciesAbilityManager), "UpdateLeapyBoneHealing"));
	static readonly Action<SpeciesAbilityManager> LeapyBleed = Calls.Bind<Action<SpeciesAbilityManager>>(AccessTools.Method(typeof(SpeciesAbilityManager), "UpdateLeapyBleedReduction"));
	static readonly Action<SpeciesAbilityManager> LeapyCosts = Calls.Bind<Action<SpeciesAbilityManager>>(AccessTools.Method(typeof(SpeciesAbilityManager), "UpdateLeapyRegrowthCosts"));
	static readonly Action<SpeciesAbilityManager, Limb, int> LeapyComplete = Calls.Bind<Action<SpeciesAbilityManager, Limb, int>>(AccessTools.Method(typeof(SpeciesAbilityManager), "CompleteLeapyLimbRegrowth"));
	static readonly Action<SpeciesAbilityManager> RozaFreeze = Calls.Bind<Action<SpeciesAbilityManager>>(AccessTools.Method(typeof(SpeciesAbilityManager), "TryFreezeRozaItems"));
	static readonly Func<float> HornPride = Calls.Bind<Func<float>>(AccessTools.Method(typeof(SpeciesAbilityManager), "GetBaronHornPrideMultiplier"));
	static readonly FieldInfo MaxHealthField = AccessTools.Field(typeof(SpiderHandler), "maxHealth");
	static readonly Dictionary<ushort, float[]> _leapy = new Dictionary<ushort, float[]>();
	static readonly Dictionary<int, float> _happy = new Dictionary<int, float>();
	static bool _windowWas;
	static bool _letMercenaryDie;
	static bool _rozaSent;
	static bool _leapySent;
	static float _leapySentAt;
	static bool _localHold;
	static bool _levSent;

	internal static void Install(Harmony harmony)
	{
		Wrap(harmony, AccessTools.Method(typeof(Body), "Eat"), nameof(Pop));
		Wrap(harmony, AccessTools.Method(typeof(Body), "Drink"), nameof(Pop));
		Wrap(harmony, AccessTools.Method(typeof(Body), "HandleBodyTemperature"), nameof(Pop));
		Wrap(harmony, AccessTools.Method(typeof(Body), "HandleCirculation"), nameof(Pop));
		Wrap(harmony, AccessTools.Method(typeof(Body), "HandleBody"), nameof(PopBody));
		var harm = AccessTools.Method(typeof(SelfHarmer), "AttemptHarm");
		if (harm != null)
			harmony.Patch(harm, First(nameof(PushHarm)), Last(nameof(Pop)));
		var exp = AccessTools.Method(typeof(Skills), "AddExp");
		if (exp != null)
			harmony.Patch(exp, First(nameof(PushExp)), Last(nameof(Pop)));
		Pride(harmony, typeof(SpiderHandler));
		Pride(harmony, typeof(CrystalEnemy));
		Pride(harmony, typeof(TraderScript));
		var gate = AccessTools.Method(typeof(MercenaryReformationTriggerPatch), "Prefix");
		if (gate != null)
			harmony.Patch(gate, prefix: new HarmonyMethod(typeof(SpeciesContext), nameof(AllowLastStand)));
		var freeze = AccessTools.Method(typeof(SpeciesAbilityManager), "IsFreezable");
		if (freeze != null)
			harmony.Patch(freeze, prefix: new HarmonyMethod(typeof(SpeciesContext), nameof(FreezeDry)));
		var albino = AccessTools.Method(typeof(AlbinoDesync), "Update");
		if (albino != null)
			harmony.Patch(albino, postfix: new HarmonyMethod(typeof(SpeciesContext), nameof(AlbinoAfter)));
		var orangeLoad = AccessTools.Method(typeof(OrangeSaveLoadFixPatch), "Prefix");
		if (orangeLoad != null)
			harmony.Patch(orangeLoad, prefix: new HarmonyMethod(typeof(OrangeArms), nameof(OrangeArms.SkipStaleSetup)));
		var bite = AccessTools.Method(typeof(DuneBitePatch), "Prefix");
		if (bite != null)
			harmony.Patch(bite, postfix: new HarmonyMethod(typeof(SpeciesContext), nameof(AfterDuneBite)));
		var limb = AccessTools.Method(typeof(Limb), "Dismember");
		if (limb != null)
			harmony.Patch(limb, postfix: new HarmonyMethod(typeof(SpeciesContext), nameof(AfterDismember)));
		var power = AccessTools.Method(AccessTools.TypeByName("Together.CombatStuff"), "CalculateAttackPower");
		if (power != null)
			harmony.Patch(power, postfix: new HarmonyMethod(typeof(SpeciesContext), nameof(OrangeOffhand)));
		var periodic = AccessTools.Method(typeof(Body), "HandlePeriodicChecks");
		if (periodic != null)
		{
			UnpatchName(periodic, "RozaMoodNormalizationPatch");
			harmony.Patch(periodic,
				prefix: new HarmonyMethod(typeof(SpeciesContext), nameof(RozaNormPrefix)),
				postfix: new HarmonyMethod(typeof(SpeciesContext), nameof(RozaNormPostfix)));
		}
		MPBridgePlugin.Log?.LogInfo("[GG-MP] species context follows the body");
	}

	static void UnpatchName(MethodBase original, string declaringType)
	{
		var info = Harmony.GetPatchInfo(original);
		if (info == null)
			return;
		var harmony = new Harmony(PluginInfo.GUID + ".rozanorm");
		foreach (var patch in info.Prefixes)
		{
			if (patch.PatchMethod?.DeclaringType?.Name == declaringType)
				harmony.Unpatch(original, patch.PatchMethod);
		}
		foreach (var patch in info.Postfixes)
		{
			if (patch.PatchMethod?.DeclaringType?.Name == declaringType)
				harmony.Unpatch(original, patch.PatchMethod);
		}
	}

	// stock halves Roza mood drift only while ActiveSpecies is Roza, so a Voyager
	// host normalizes a remote Roza at the full rate and then syncs that over the owner.
	static void RozaNormPrefix(Body __instance, out float __state)
	{
		__state = 0f;
		if (__instance != null && SpeciesOf(__instance) == "Roza")
			__state = __instance.happiness;
	}

	static void RozaNormPostfix(Body __instance, float __state)
	{
		if (__instance == null || SpeciesOf(__instance) != "Roza")
			return;
		float num = __instance.happiness - __state;
		if (num != 0f)
			__instance.happiness = __state + num * 0.5f;
	}

	static HarmonyMethod First(string name)
	{
		return new HarmonyMethod(typeof(SpeciesContext), name) { priority = Priority.First };
	}

	static HarmonyMethod Last(string name)
	{
		return new HarmonyMethod(typeof(SpeciesContext), name) { priority = Priority.Last };
	}

	static void Wrap(Harmony harmony, MethodInfo method, string postfix)
	{
		if (method == null)
			return;
		harmony.Patch(method, First(nameof(PushBody)), Last(postfix));
	}

	static void Pride(Harmony harmony, Type type)
	{
		var method = AccessTools.Method(type, "AnimalDeath");
		if (method == null)
			return;
		harmony.Patch(method, postfix: new HarmonyMethod(typeof(SpeciesContext), nameof(AfterDeath)));
	}

	internal static void Tick()
	{
		TickMercenaryWindow();
		TickOwnedAbility();
		if (Net.IsServer)
			TickRemotePassives();
	}

	static void TickMercenaryWindow()
	{
		bool active = SpeciesAbilityManager.MercenaryDeathWindowActive;
		if (_windowWas && !active)
			_letMercenaryDie = true;
		if (active)
			_letMercenaryDie = false;
		_windowWas = active;
		Body body = SpeciesAbilityManager.PlayerBody;
		if (body != null && body.brainHealth > 50f)
			_letMercenaryDie = false;
	}

	// the reform window skips the real last stand. once it closes, the next one has to land.
	static bool AllowLastStand(ref bool __result)
	{
		if (!_letMercenaryDie)
			return true;
		__result = true;
		return false;
	}

	static void TickOwnedAbility()
	{
		if (!SpeciesMultiplayerSync.IsNetworkRunning() || SpeciesMultiplayerSync.IsHost())
			return;
		var inst = SpeciesAbilityManager.Instance;
		if (inst == null)
			return;
		bool roza = RozaHolding != null && RozaHolding.GetValue(inst) is bool hold && hold;
		if (roza != _rozaSent)
		{
			_rozaSent = roza;
			SpeciesMultiplayerSync.SendRozaHold(roza);
		}
		bool leapy = LeapyHolding != null && LeapyHolding.GetValue(inst) is bool grow && grow;
		if (leapy != _leapySent || (leapy && Time.time > _leapySentAt + 1f))
		{
			_leapySent = leapy;
			_leapySentAt = Time.time;
			SpeciesMultiplayerSync.SendLeapyHold(leapy);
		}
		bool lift = Levitating != null && Levitating.GetValue(inst) is bool up && up;
		Rigidbody2D target = LevitateTarget != null ? LevitateTarget.GetValue(inst) as Rigidbody2D : null;
		if (!lift || target == null)
		{
			if (_levSent)
			{
				_levSent = false;
				SpeciesMultiplayerSync.SendLevitate(false, Vector2.zero, Vector2.zero);
			}
			return;
		}
		_levSent = true;
		Vector2 mouse = MouseWorld(target.position);
		SpeciesMultiplayerSync.SendLevitate(true, mouse, target.position);
	}

	static Vector2 MouseWorld(Vector2 fallback)
	{
		if (Camera.main == null)
			return fallback;
		return Camera.main.ScreenToWorldPoint(Input.mousePosition);
	}

	static void TickRemotePassives()
	{
		var inst = SpeciesAbilityManager.Instance;
		if (inst == null)
			return;
		foreach (var plr in ScavPlayer.BodyToPlayerDict.Values)
		{
			if (plr == null || plr.body == null || plr == ScavPlayer.LocalPlayer)
				continue;
			string species = SpeciesOf(plr.body);
			if (species == "Leapy")
				TickLeapy(inst, plr);
			else if (species == "Roza" && SpeciesMultiplayerSync.RozaHolding(plr))
				TickRoza(inst, plr.body);
			else if (species == "Albino")
				SpeciesMultiplayerSync.TickAlbino(plr);
		}
	}

	static void TickLeapy(SpeciesAbilityManager inst, ScavPlayer plr)
	{
		Push(plr.body);
		try
		{
			bool holding = ReadHold() && plr.body.conscious && plr.body.hunger >= 20f;
			if (!holding)
				return;
			plr.body.hunger = Mathf.Max(0f, plr.body.hunger - 4.5f * Time.deltaTime);
			if (LeapyBone != null)
				LeapyBone(inst);
			if (LeapyBleed != null)
				LeapyBleed(inst);
			if (LeapyCosts != null)
				LeapyCosts(inst);
			if (plr.body.limbs == null || plr.netBody == null)
				return;
			if (!_leapy.TryGetValue(plr.netBody.netId.id, out float[] progress) || progress.Length != plr.body.limbs.Length)
			{
				progress = new float[plr.body.limbs.Length];
				_leapy[plr.netBody.netId.id] = progress;
			}
			for (int i = 0; i < plr.body.limbs.Length; i++)
			{
				Limb limb = plr.body.limbs[i];
				if (limb == null || !limb.dismembered)
				{
					progress[i] = 0f;
					continue;
				}
				progress[i] += Time.deltaTime / 14f;
				if (progress[i] < 1f || LeapyComplete == null)
					continue;
				progress[i] = 0f;
				LeapyComplete(inst, limb, i);
			}
		}
		catch (Exception e)
		{
			MPBridgePlugin.Log?.LogWarning("[GG-MP] leapy tick failed: " + e.Message);
		}
		finally
		{
			Pop();
		}
	}

	static void TickRoza(SpeciesAbilityManager inst, Body body)
	{
		Push(body);
		try
		{
			if (body.temperature > 29f)
			{
				float rate = body.temperature > 38f ? 0.4f / (1f + (body.temperature - 38f) * 0.4f) : 0.4f;
				body.temperature -= Mathf.Min(rate * Time.deltaTime, body.temperature - 29f);
				body.thirst -= 0.3f * Time.deltaTime;
				body.energy -= 0.15f * Time.deltaTime;
			}
			if (body.temperature <= 30f && RozaFreeze != null)
				RozaFreeze(inst);
		}
		catch (Exception e)
		{
			MPBridgePlugin.Log?.LogWarning("[GG-MP] roza tick failed: " + e.Message);
		}
		finally
		{
			Pop();
		}
	}

	static void PushBody(Body __instance)
	{
		Push(__instance);
	}

	static void PushHarm(SelfHarmer __instance)
	{
		Body body = HarmBody != null ? HarmBody.GetValue(__instance) as Body : null;
		Push(body);
	}

	static void PushExp(Skills __instance)
	{
		Body body = null;
		if (__instance != null)
		{
			foreach (var plr in ScavPlayer.BodyToPlayerDict.Values)
			{
				if (plr != null && plr.body != null && plr.body.skills == __instance)
				{
					body = plr.body;
					break;
				}
			}
			if (body == null && PlayerCamera.main != null && PlayerCamera.main.body != null && PlayerCamera.main.body.skills == __instance)
				body = PlayerCamera.main.body;
		}
		Push(body);
	}

	internal static void Pop()
	{
		if (_stack.Count == 0)
			return;
		Snap snap = _stack[_stack.Count - 1];
		_stack.RemoveAt(_stack.Count - 1);
		SpeciesAbilityManager.ActiveSpecies = snap.Species ?? "";
		SpeciesAbilityManager.PlayerBody = snap.Player;
		SpeciesMoodSystem.PositiveMoodMultiplier = snap.Pos;
		SpeciesMoodSystem.NegativeMoodMultiplier = snap.Neg;
		SpeciesMoodSystem.MoodCeiling = snap.Ceil;
		SpeciesMoodSystem.MoodFloor = snap.Floor;
		SpeciesMoodSystem.SelfCannibalizeThreshold = snap.CannibalAt;
		SpeciesMoodSystem.TraumaImmune = snap.Trauma;
		SpeciesMoodSystem.SelfCannibalizeOnMisery = snap.Cannibal;
		SpeciesMoodSystem.ResetPositiveMoodOnSleep = snap.SleepReset;
		WriteHold(snap.LeapyHold);
	}

	static void PopBody(Body __instance)
	{
		bool local = PlayerCamera.main != null && __instance == PlayerCamera.main.body;
		if (!local && __instance != null && _happy.TryGetValue(__instance.GetInstanceID(), out float before))
			ApplyRemoteMood(__instance, before);
		if (!local && __instance != null && SpeciesOf(__instance) == "Dune")
			SpeciesMoodSystem.UpdateDuneStarvation(__instance);
		Pop();
	}

	// gg's own mood postfix returns unless the body is the camera.
	static void ApplyRemoteMood(Body body, float before)
	{
		string species = SpeciesAbilityManager.ActiveSpecies;
		if (string.IsNullOrEmpty(species))
			return;
		float delta = body.happiness - before;
		if (species == "Voyager" && delta < 0f)
			body.happiness = before + delta * VoyagerPrideSystem.GetNegativeMoodMultiplier(before);
		else if (delta > 0f && SpeciesMoodSystem.PositiveMoodMultiplier != 1f)
			body.happiness = before + delta * SpeciesMoodSystem.PositiveMoodMultiplier;
		else if (delta < 0f && SpeciesMoodSystem.NegativeMoodMultiplier != 1f)
			body.happiness = before + delta * SpeciesMoodSystem.NegativeMoodMultiplier;
		body.happiness = Mathf.Clamp(body.happiness, SpeciesMoodSystem.MoodFloor, SpeciesMoodSystem.MoodCeiling);
		if (SpeciesMoodSystem.TraumaImmune)
			body.traumaAmount = 0f;
	}

	internal static void Push(Body body)
	{
		if (body != null)
			_happy[body.GetInstanceID()] = body.happiness;
		if (_stack.Count == 0)
			_localHold = ReadHold();
		_stack.Add(new Snap
		{
			Species = SpeciesAbilityManager.ActiveSpecies,
			Player = SpeciesAbilityManager.PlayerBody,
			Pos = SpeciesMoodSystem.PositiveMoodMultiplier,
			Neg = SpeciesMoodSystem.NegativeMoodMultiplier,
			Ceil = SpeciesMoodSystem.MoodCeiling,
			Floor = SpeciesMoodSystem.MoodFloor,
			CannibalAt = SpeciesMoodSystem.SelfCannibalizeThreshold,
			Trauma = SpeciesMoodSystem.TraumaImmune,
			Cannibal = SpeciesMoodSystem.SelfCannibalizeOnMisery,
			SleepReset = SpeciesMoodSystem.ResetPositiveMoodOnSleep,
			LeapyHold = ReadHold()
		});
		string species = SpeciesOf(body);
		if (string.IsNullOrEmpty(species))
			return;
		SpeciesAbilityManager.ActiveSpecies = species;
		if (body != null)
			SpeciesAbilityManager.PlayerBody = body;
		ApplyMood(species, body);
		if (species == "Leapy")
			WriteHold(BodyHoldsLeapy(body));
	}

	// stock keeps one hold flag. heart rate, fibrillation, and the regen scale all read it,
	// so the host holding paints that cost onto every Leapy the host ticks.
	static bool BodyHoldsLeapy(Body body)
	{
		if (body == null)
			return false;
		if (PlayerCamera.main != null && PlayerCamera.main.body == body)
			return _localHold;
		foreach (var plr in ScavPlayer.BodyToPlayerDict.Values)
		{
			if (plr != null && plr.body == body)
				return SpeciesMultiplayerSync.LeapyHolding(plr);
		}
		return false;
	}

	static bool ReadHold()
	{
		var inst = SpeciesAbilityManager.Instance;
		return inst != null && LeapyHolding != null && LeapyHolding.GetValue(inst) is bool on && on;
	}

	static void WriteHold(bool on)
	{
		var inst = SpeciesAbilityManager.Instance;
		if (inst != null && LeapyHolding != null)
			LeapyHolding.SetValue(inst, on);
	}

	static void ApplyMood(string species, Body body)
	{
		SpeciesMoodSystem.PositiveMoodMultiplier = 1f;
		SpeciesMoodSystem.NegativeMoodMultiplier = 1f;
		SpeciesMoodSystem.MoodCeiling = 100f;
		SpeciesMoodSystem.MoodFloor = -100f;
		SpeciesMoodSystem.ResetPositiveMoodOnSleep = false;
		SpeciesMoodSystem.TraumaImmune = false;
		SpeciesMoodSystem.SelfCannibalizeOnMisery = false;
		SpeciesMoodSystem.SelfCannibalizeThreshold = -90f;
		if (species == "Dune")
		{
			SpeciesMoodSystem.MoodFloor = -30f;
			SpeciesMoodSystem.MoodCeiling = 60f;
			SpeciesMoodSystem.TraumaImmune = true;
		}
		else if (species == "Velvet")
		{
			SpeciesMoodSystem.PositiveMoodMultiplier = 2.5f;
			SpeciesMoodSystem.ResetPositiveMoodOnSleep = true;
			SpeciesMoodSystem.TraumaImmune = true;
			SpeciesMoodSystem.SelfCannibalizeOnMisery = true;
			SpeciesMoodSystem.SelfCannibalizeThreshold = -85f;
		}
		else if (species == "Orange" && body != null)
		{
			int arms = OrangeFourArms.GetTotalIntactArmCount(body);
			float scale = arms >= 4 ? 1.5f : arms == 3 ? 1.3f : 1.15f;
			SpeciesMoodSystem.PositiveMoodMultiplier = scale;
			SpeciesMoodSystem.NegativeMoodMultiplier = scale;
		}
	}

	static string SpeciesOf(Body body)
	{
		return SpeciesMultiplayerSync.ResolveSpeciesForBody(body);
	}

	static void AfterDeath(Component __instance)
	{
		if (__instance == null)
			return;
		float max = 100f;
		if (MaxHealthField != null && MaxHealthField.DeclaringType.IsInstanceOfType(__instance))
			max = (float)MaxHealthField.GetValue(__instance);
		else
		{
			var field = AccessTools.Field(__instance.GetType(), "maxHealth");
			if (field != null && field.FieldType == typeof(float))
				max = (float)field.GetValue(__instance);
		}
		if (max < 50f)
			return;
		Vector2 at = __instance.transform.position;
		float mult = HornPride != null ? HornPride() : 1f;
		foreach (var plr in ScavPlayer.BodyToPlayerDict.Values)
		{
			Body body = plr != null ? plr.body : null;
			if (body == null || SpeciesOf(body) != "Baron")
				continue;
			if (Vector2.Distance(at, body.transform.position) > 32f)
				continue;
			float duration;
			if (max >= 2000f)
				duration = 780f;
			else
				duration = Mathf.Clamp(Mathf.Sqrt(max) * 3f, 25f, 120f);
			duration *= mult;
			if (PlayerCamera.main != null && body == PlayerCamera.main.body)
			{
				SpeciesAbilityManager.BaronPrideTimer = Mathf.Max(SpeciesAbilityManager.BaronPrideTimer, duration);
				SpeciesAbilityManager.BaronPrideStrength = Mathf.Max(SpeciesAbilityManager.BaronPrideStrength, duration);
			}
			else if (Net.IsServer)
				SpeciesMultiplayerSync.SendPride(plr, duration);
		}
	}

	// orange offhand hits as hard as the main hand. gg only cancels the 0.75 when the
	// local species is orange, so the host scored an orange client's plastic hit at 21.
	static void OrangeOffhand(Body attacker, AttackInfo atk, ref float __result)
	{
		if (attacker == null || atk == null || !atk.physicalSwing || attacker.handSlot != 1)
			return;
		bool localOrange = SpeciesAbilityManager.ActiveSpecies == "Orange";
		bool bodyOrange = string.Equals(SpeciesMultiplayerSync.GetSpeciesForBody(attacker), "Orange", StringComparison.Ordinal);
		if (bodyOrange && !localOrange)
			__result /= 0.75f;
		else if (!bodyOrange && localOrange)
			__result *= 0.75f;
	}

	static void AfterDuneBite(TraderScript __instance)
	{
		SpeciesMultiplayerSync.SendDuneBite(__instance);
	}

	static void AfterDismember(Limb __instance)
	{
		if (__instance == null || __instance.body == null)
			return;
		if (PlayerCamera.main == null || __instance.body != PlayerCamera.main.body)
			return;
		if (!SpeciesMoodSystem.selfCannibalizeInProgress)
			return;
		SpeciesMultiplayerSync.SendLimbGone(__instance);
	}

	// food in the freeze list is perishable even when it is not wet.
	static bool FreezeDry(Item item, ref bool __result)
	{
		if (item == null || string.IsNullOrEmpty(item.id))
			return true;
		var field = AccessTools.Field(typeof(SpeciesAbilityManager), "RozaFreezableFood");
		if (field != null && field.GetValue(null) is ICollection<string> foods && foods.Contains(item.id))
		{
			__result = true;
			return false;
		}
		return true;
	}

	static readonly FieldInfo AlbinoActive = AccessTools.Field(typeof(AlbinoDesync), "isAlbinoAbilityActive");
	static readonly FieldInfo AlbinoLevel = AccessTools.Field(typeof(AlbinoDesync), "albinoDesyncLevel");
	static readonly FieldInfo AlbinoCrash = AccessTools.Field(typeof(AlbinoDesync), "consciousnessCrashTimer");
	static bool _desyncWas;

	static void AlbinoAfter()
	{
		NoteAlbinoPress();
		Body body = SpeciesAbilityManager.PlayerBody;
		if (body == null)
			return;
		// stock desync adds bleed. undo that add and take the same amount off.
		ReverseBleed(body);
		if (AlbinoCrash == null || !(AlbinoCrash.GetValue(null) is float left) || left <= 0f)
			return;
		body.heartRate = 0f;
		body.consciousness = 0f;
	}

	static void ReverseBleed(Body body)
	{
		if (body.limbs == null || AlbinoActive == null || AlbinoLevel == null)
			return;
		if (!(AlbinoActive.GetValue(null) is bool on) || !on)
			return;
		int level = AlbinoLevel.GetValue(null) is int n ? n : 0;
		if (level < 1)
			return;
		float rate = level >= 2 ? 0.5f : 0.25f;
		float undo = Time.deltaTime * rate * 2f;
		foreach (Limb limb in body.limbs)
		{
			if (limb == null || limb.bleedAmount <= 0f)
				continue;
			limb.bleedAmount = Mathf.Max(0f, limb.bleedAmount - undo);
		}
	}

	// the key is local. the host owns the heart, the pain, and the flatline.
	static void NoteAlbinoPress()
	{
		if (!SpeciesMultiplayerSync.IsNetworkRunning() || SpeciesMultiplayerSync.IsHost())
			return;
		if (AlbinoActive == null || AlbinoLevel == null)
			return;
		bool on = AlbinoActive.GetValue(null) is bool active && active;
		if (on && !_desyncWas)
		{
			int level = AlbinoLevel.GetValue(null) is int n ? n : 1;
			SpeciesMultiplayerSync.SendAlbinoOn(level);
		}
		_desyncWas = on;
	}
}
