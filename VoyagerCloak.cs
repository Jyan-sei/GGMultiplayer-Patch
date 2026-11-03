using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GunsawGenetics;

// the key toggle is gone. standing still fills cloak power over 3s.
// each tile you travel spends 15%. half power and above is the cloaked side.
// the fade is applied again at the end of the frame so a later color write cannot pop it back.
internal static class VoyagerCloak
{
	internal const float Half = 0.5f;
	const float FillSeconds = 3f;
	const float DrainPerTile = 0.15f;
	const float Tile = 1f;
	// idle physics wobble is not a step. slower than this still counts as standing.
	const float StillSpeed = 0.25f;

	static readonly FieldInfo RemoteAlpha = AccessTools.Field(typeof(RemoteCloakVisual), "currentAlpha");
	static readonly FieldInfo RemoteTarget = AccessTools.Field(typeof(RemoteCloakVisual), "targetCloaked");
	static readonly FieldInfo ElderRange = AccessTools.Field(typeof(ElderThornbackBehaviour), "maxDistance");
	static readonly FieldInfo CloakStateField = AccessTools.Field(typeof(SpeciesAbilityManager), "cloakState");
	static readonly FieldInfo IsCloakedField = AccessTools.Field(typeof(SpeciesAbilityManager), "isCloaked");
	static readonly FieldInfo SoundTime = AccessTools.Field(typeof(SpeciesAbilityManager), "lastVoyagerCloakSoundTime");
	static readonly FieldInfo CloakClip = AccessTools.Field(typeof(SpeciesAbilityManager), "VoyagerCloakSound");
	static readonly FieldInfo DecloakClip = AccessTools.Field(typeof(SpeciesAbilityManager), "VoyagerDecloakSound");
	static readonly Action<SpeciesAbilityManager, AudioClip> PlaySound = Calls.Bind<Action<SpeciesAbilityManager, AudioClip>>(AccessTools.Method(typeof(SpeciesAbilityManager), "PlayVoyagerCloakSound"));
	static readonly Action<SpeciesAbilityManager> Rad = Calls.Bind<Action<SpeciesAbilityManager>>(AccessTools.Method(typeof(SpeciesAbilityManager), "UpdateVoyagerRadiation"));
	static readonly Action<SpeciesAbilityManager> Wet = Calls.Bind<Action<SpeciesAbilityManager>>(AccessTools.Method(typeof(SpeciesAbilityManager), "UpdateVoyagerWetness"));
	static readonly Action<SpeciesAbilityManager> Items = Calls.Bind<Action<SpeciesAbilityManager>>(AccessTools.Method(typeof(SpeciesAbilityManager), "UpdateVoyagerItemReactions"));
	static readonly Func<SpeciesAbilityManager, bool> StealthKill = Calls.Bind<Func<SpeciesAbilityManager, bool>>(AccessTools.Method(typeof(SpeciesAbilityManager), "UpdateVoyagerStealthKill"));
	static readonly Action<SpeciesAbilityManager> StealthMusic = Calls.Bind<Action<SpeciesAbilityManager>>(AccessTools.Method(typeof(SpeciesAbilityManager), "UpdateVoyagerStealthCombatMusic"));
	static readonly Action<SpeciesAbilityManager, bool> Sneak = Calls.Bind<Action<SpeciesAbilityManager, bool>>(AccessTools.Method(typeof(SpeciesAbilityManager), "UpdateVoyagerSneakSpeed"));
	static readonly FieldInfo SmoothedSpeed = AccessTools.Field(typeof(SpeciesAbilityManager), "smoothedSpeed");
	static readonly FieldInfo LastSpeedPos = AccessTools.Field(typeof(SpeciesAbilityManager), "lastPosition");
	static bool _logged;

	static float _power;
	static bool _strong;
	static bool _havePos;
	static Vector2 _lastPos;
	static int _bodyId;
	static int _sight;
	static int _watch;

	internal static void Install(Harmony harmony)
	{
		harmony.Patch(
			AccessTools.Method(typeof(SpeciesAbilityManager), "Update"),
			postfix: new HarmonyMethod(typeof(VoyagerCloak), nameof(AfterAbilityUpdate)));
		harmony.Patch(
			AccessTools.Method(typeof(SpeciesAbilityManager), "UpdateVoyager"),
			prefix: new HarmonyMethod(typeof(VoyagerCloak), nameof(ReplaceToggle)));
		harmony.Patch(
			AccessTools.Method(typeof(SpiderHandler), "Update"),
			prefix: new HarmonyMethod(typeof(VoyagerCloak), nameof(SightEnter)),
			postfix: new HarmonyMethod(typeof(VoyagerCloak), nameof(SightLeave)));
		harmony.Patch(
			AccessTools.Method(typeof(ElderThornbackBehaviour), "Update"),
			prefix: new HarmonyMethod(typeof(VoyagerCloak), nameof(ElderPrefix)),
			postfix: new HarmonyMethod(typeof(VoyagerCloak), nameof(ElderPostfix)));
		MethodInfo overlap = AccessTools.Method(typeof(Physics2D), "OverlapCircle", new[] { typeof(Vector2), typeof(float), typeof(int) });
		if (overlap != null)
			harmony.Patch(overlap, postfix: new HarmonyMethod(typeof(VoyagerCloak), nameof(OverlapPostfix)));
		else
			MPBridgePlugin.Log?.LogWarning("[GG-MP] voyager cloak sight overlap missing");
		var cannon = AccessTools.Method(typeof(SoundCannon), "CanSeePos");
		if (cannon != null)
			harmony.Patch(cannon, postfix: new HarmonyMethod(typeof(VoyagerCloak), nameof(HideFromCannon)));
		PatchTrigger(harmony, AccessTools.Method(typeof(SpikeStabberScript), "OnTriggerEnter2D"));
		PatchTrigger(harmony, AccessTools.Method(typeof(BearTrap), "OnTriggerEnter2D"));
		PatchTrigger(harmony, AccessTools.Method(typeof(MineScript), "OnTriggerEnter2D"));
		var trader = AccessTools.Method(typeof(TraderScript), "Update");
		if (trader != null)
			harmony.Patch(trader,
				prefix: new HarmonyMethod(typeof(VoyagerCloak), nameof(WatchEnter)),
				postfix: new HarmonyMethod(typeof(VoyagerCloak), nameof(WatchLeave)));
		MPBridgePlugin.Log?.LogInfo("[GG-MP] voyager cloak charges while still, spends power per tile");
	}

	// the stock method toggles on the ability key. power replaces that.
	static bool ReplaceToggle(SpeciesAbilityManager __instance)
	{
		if (SpeciesAbilityManager.ActiveSpecies != "Voyager")
			return true;
		Body body = SpeciesAbilityManager.PlayerBody;
		if (body == null)
			return true;
		try
		{
			TickPower(body);
			bool strong = _power >= Half;
			SetStockState(__instance, strong);
			SpeciesAbilityManager.CurrentCloakAlpha = Mathf.Lerp(1f, 0.2f, _power);
			if (strong != _strong)
			{
				_strong = strong;
				PlayEdge(__instance, strong);
				SpeciesMultiplayerSync.BroadcastLocalCloakState(strong);
			}
			KeepSpeed(__instance, body);
			Call(__instance, Rad);
			Call(__instance, Wet);
			Call(__instance, Items);
			if (StealthKill != null)
				StealthKill(__instance);
			Call(__instance, StealthMusic);
			if (Sneak != null)
				Sneak(__instance, strong && body.crouching);
		}
		catch (Exception e)
		{
			if (!_logged)
			{
				_logged = true;
				MPBridgePlugin.Log?.LogError("[GG-MP] voyager cloak power failed: " + e.Message);
			}
		}
		return false;
	}

	// stock sneak speed reads this. the toggle used to refresh it.
	static void KeepSpeed(SpeciesAbilityManager inst, Body body)
	{
		if (SmoothedSpeed == null || LastSpeedPos == null)
			return;
		Vector3 last = (Vector3)LastSpeedPos.GetValue(inst);
		Vector3 position = body.transform.position;
		if (last == Vector3.zero)
			last = position;
		float dt = Time.deltaTime;
		float speed = dt > 0f ? Vector3.Distance(position, last) / dt : 0f;
		float smoothed = (float)SmoothedSpeed.GetValue(inst);
		smoothed = speed > smoothed ? speed : Mathf.Lerp(smoothed, speed, dt * 4f);
		LastSpeedPos.SetValue(inst, position);
		SmoothedSpeed.SetValue(inst, smoothed);
	}

	static void TickPower(Body body)
	{
		int id = body.GetInstanceID();
		Vector2 pos = body.transform.position;
		if (id != _bodyId || !_havePos)
		{
			_bodyId = id;
			_havePos = true;
			_lastPos = pos;
			_power = 0f;
			return;
		}
		float dist = Vector2.Distance(pos, _lastPos);
		_lastPos = pos;
		float dt = Time.deltaTime;
		if (dt <= 0f)
			return;
		// a load or a teleport is not a sprint
		if (dist > 8f)
			return;
		if (!WearingCloak(body))
		{
			_power = Mathf.MoveTowards(_power, 0f, dt / FillSeconds);
			return;
		}
		float speed = dist / dt;
		if (speed < StillSpeed)
			_power += dt / FillSeconds;
		else
			_power -= (dist / Tile) * DrainPerTile;
		_power = Mathf.Clamp01(_power);
	}

	static bool WearingCloak(Body body)
	{
		Item worn = body.GetWearable("voyagercloak");
		if (worn == null)
			return false;
		BatteryItem battery = worn.GetComponent<BatteryItem>();
		return battery == null || battery.hasCharge;
	}

	static void SetStockState(SpeciesAbilityManager inst, bool strong)
	{
		if (CloakStateField != null)
		{
			object value = Enum.Parse(CloakStateField.FieldType, strong ? "Cloaked" : "Off");
			CloakStateField.SetValue(inst, value);
		}
		if (IsCloakedField != null)
			IsCloakedField.SetValue(inst, strong);
	}

	static void PlayEdge(SpeciesAbilityManager inst, bool up)
	{
		if (PlaySound == null)
			return;
		FieldInfo clipField = up ? CloakClip : DecloakClip;
		if (clipField == null)
			return;
		// the stock player ignores a second clip inside half a second. a real cross should speak.
		if (SoundTime != null)
			SoundTime.SetValue(inst, -1000f);
		PlaySound(inst, clipField.GetValue(inst) as AudioClip);
	}

	static void Call(SpeciesAbilityManager inst, Action<SpeciesAbilityManager> method)
	{
		if (method != null)
			method(inst);
	}

	internal static void EnsureFade(Body body)
	{
		if (body == null)
			return;
		if (body.GetComponent<CloakFade>() == null)
			body.gameObject.AddComponent<CloakFade>();
	}

	static void AfterAbilityUpdate()
	{
		if (SpeciesAbilityManager.ActiveSpecies != "Voyager")
			return;
		EnsureFade(SpeciesAbilityManager.PlayerBody);
	}

	internal static bool Hides(Body body)
	{
		if (body == null || !IsVoyager(body))
			return false;
		if (PlayerCamera.main != null && body == PlayerCamera.main.body)
			return _power >= Half;
		var visual = body.GetComponent<RemoteCloakVisual>();
		if (visual != null && RemoteTarget != null && RemoteTarget.GetValue(visual) is bool on && on)
			return true;
		return Alpha(body) <= Half;
	}

	static bool IsVoyager(Body body)
	{
		string mapped = SpeciesMultiplayerSync.GetSpeciesForBody(body);
		if (string.Equals(mapped, "Voyager", System.StringComparison.Ordinal))
			return true;
		return PlayerCamera.main != null && body == PlayerCamera.main.body
			&& SpeciesAbilityManager.ActiveSpecies == "Voyager";
	}

	internal static float Alpha(Body body)
	{
		if (PlayerCamera.main != null && body == PlayerCamera.main.body
			&& SpeciesAbilityManager.ActiveSpecies == "Voyager")
			return SpeciesAbilityManager.CurrentCloakAlpha;
		var visual = body.GetComponent<RemoteCloakVisual>();
		if (visual == null || RemoteAlpha == null)
			return 1f;
		object value = RemoteAlpha.GetValue(visual);
		return value is float alpha ? alpha : 1f;
	}

	static void SightEnter()
	{
		_sight++;
	}

	static void SightLeave(SpiderHandler __instance)
	{
		_sight = Mathf.Max(0, _sight - 1);
		DropChase(__instance);
	}

	// the chase point is a snapshot. if it is still on a hidden body, send the creature off.
	static void DropChase(SpiderHandler spider)
	{
		if (spider == null)
			return;
		for (int i = 0; i < CloakFade.Live.Count; i++)
		{
			CloakFade fade = CloakFade.Live[i];
			if (fade == null)
				continue;
			Body body = fade.GetComponent<Body>();
			if (!Hides(body))
				continue;
			Vector2 at = body.transform.position;
			if (Vector2.Distance(spider.transform.position, at) > spider.seeDistance + 1f)
				continue;
			if (Vector2.Distance(spider.target, at) > 4f)
				continue;
			Vector2 away = (Vector2)spider.transform.position - at;
			if (away.sqrMagnitude < 0.01f)
				away = Vector2.right;
			spider.target = (Vector2)spider.transform.position + away.normalized * 8f;
			spider.moveTime = 0.6f;
			return;
		}
	}

	static void ElderPrefix(ElderThornbackBehaviour __instance, out float __state)
	{
		__state = ElderRange != null ? (float)ElderRange.GetValue(__instance) : 0f;
		Body me = PlayerCamera.main != null ? PlayerCamera.main.body : null;
		if (ElderRange != null && Hides(me))
			ElderRange.SetValue(__instance, 0f);
	}

	static void ElderPostfix(ElderThornbackBehaviour __instance, float __state)
	{
		if (ElderRange != null)
			ElderRange.SetValue(__instance, __state);
	}

	static void PatchTrigger(Harmony harmony, MethodInfo method)
	{
		if (method == null)
			return;
		string name = method.GetParameters().Length > 0 ? method.GetParameters()[0].Name : null;
		string prefix = name == "collision" ? nameof(SkipCollision) : nameof(SkipIfCloaked);
		try
		{
			harmony.Patch(method, prefix: new HarmonyMethod(typeof(VoyagerCloak), prefix) { priority = Priority.First });
		}
		catch (Exception e)
		{
			MPBridgePlugin.Log?.LogWarning("[GG-MP] cloak trigger patch failed: " + e.Message);
		}
	}

	static bool SkipCollision(Collider2D collision)
	{
		return SkipIfCloaked(collision);
	}

	static void WatchEnter()
	{
		_watch++;
	}

	static void WatchLeave()
	{
		if (_watch > 0)
			_watch--;
	}

	static bool SkipIfCloaked(Collider2D other)
	{
		return other == null || !Hides(BodyOf(other));
	}

	static void HideFromCannon(Vector2 pos, ref bool __result)
	{
		if (!__result)
			return;
		foreach (Collider2D hit in Physics2D.OverlapCircleAll(pos, 1.5f))
		{
			if (hit != null && Hides(BodyOf(hit)))
			{
				__result = false;
				return;
			}
		}
	}

	static void OverlapPostfix(ref Collider2D __result)
	{
		if ((_sight <= 0 && _watch <= 0) || __result == null)
			return;
		Body body = BodyOf(__result);
		if (Hides(body))
			__result = null;
	}

	static Body BodyOf(Collider2D col)
	{
		Body body = col.GetComponent<Body>();
		if (body != null)
			return body;
		Limb limb = col.GetComponent<Limb>();
		if (limb != null)
			body = limb.GetComponentInParent<Body>();
		if (body != null)
			return body;
		return col.GetComponentInParent<Body>();
	}

	sealed class CloakFade : MonoBehaviour
	{
		internal static readonly List<CloakFade> Live = new List<CloakFade>();
		readonly Dictionary<int, float> _base = new Dictionary<int, float>();

		void OnEnable()
		{
			Live.Add(this);
		}

		void OnDisable()
		{
			Live.Remove(this);
		}

		void LateUpdate()
		{
			Body body = GetComponent<Body>();
			if (body == null)
				return;
			float alpha = VoyagerCloak.Alpha(body);
			if (alpha >= 0.99f && _base.Count == 0)
				return;
			Paint(body, alpha);
		}

		void Paint(Body body, float alpha)
		{
			PaintList(body.GetComponents<SpriteRenderer>(), alpha);
			if (body.limbs == null)
				return;
			for (int i = 0; i < body.limbs.Length; i++)
			{
				Limb limb = body.limbs[i];
				if (limb == null)
					continue;
				PaintList(limb.GetComponentsInChildren<SpriteRenderer>(true), alpha);
			}
		}

		void PaintList(SpriteRenderer[] list, float alpha)
		{
			if (list == null)
				return;
			for (int i = 0; i < list.Length; i++)
			{
				SpriteRenderer sr = list[i];
				if (sr == null)
					continue;
				int id = sr.GetInstanceID();
				if (!_base.ContainsKey(id))
					_base[id] = sr.color.a > 0.01f ? sr.color.a : 1f;
				Color color = sr.color;
				color.a = _base[id] * alpha;
				sr.color = color;
			}
		}
	}
}
