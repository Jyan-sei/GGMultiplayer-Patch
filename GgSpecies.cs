using System;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace GunsawGenetics;

// species payloads for ApplyStats. the publicizer opens gg internals. stock
// InternalsVisibleTo only names GunsawGeneticsMPBridge.
internal static class GgSpecies
{
	internal struct Resolved
	{
		internal string Key;
		internal int SpriteSlot;
		internal int STR;
		internal int RES;
		internal int INT;
		internal float MoveSpeedMult;
		internal float JumpSpeedMult;
		internal float BloodVolumeMax;
		internal float HungerThirstOffset;
		internal bool IsCustom;
		internal SpeciesData Data;
	}

	internal static bool TryResolve(string speciesKey, int spriteSlotHint, out Resolved resolved)
	{
		resolved = default;
		if (string.IsNullOrEmpty(speciesKey))
			return false;

		var custom = CustomSpeciesManager.Find(speciesKey);
		if (custom != null)
		{
			var data = custom.ToSpeciesData();
			resolved = new Resolved
			{
				Key = speciesKey,
				SpriteSlot = spriteSlotHint > 0 ? spriteSlotHint : custom.SpriteSlot,
				STR = custom.STR,
				RES = custom.RES,
				INT = custom.INT,
				MoveSpeedMult = custom.MoveSpeedMult,
				JumpSpeedMult = custom.JumpSpeedMult,
				BloodVolumeMax = BaronBloodScaling.GetMax(speciesKey),
				HungerThirstOffset = custom.HungerThirstOffset,
				IsCustom = true,
				Data = data,
			};
			return true;
		}

		var builtIn = SpeciesData.GetByName(speciesKey);
		if (builtIn != null)
		{
			// baron 150, custom max, else 100. host health sync has to carry that, not vanilla 100.
			float bloodMax = BaronBloodScaling.GetMax(speciesKey);
			resolved = new Resolved
			{
				Key = speciesKey,
				SpriteSlot = spriteSlotHint > 0 ? spriteSlotHint : builtIn.SpriteSlot,
				STR = builtIn.STR,
				RES = builtIn.RES,
				INT = builtIn.INT,
				MoveSpeedMult = builtIn.MoveSpeedMult,
				JumpSpeedMult = builtIn.JumpSpeedMult,
				BloodVolumeMax = bloodMax,
				HungerThirstOffset = HungerThirstCapConfig.GetOffset(speciesKey),
				IsCustom = false,
				Data = builtIn,
			};
			return true;
		}

		return false;
	}

	internal static float BaselineMaxSpeed()
	{
		return GunsawGeneticsPlugin.BaselineMaxSpeed;
	}

	internal static float BaselineJumpSpeed()
	{
		return GunsawGeneticsPlugin.BaselineJumpSpeed;
	}

	internal static float BaselineMoveForce(Body body)
	{
		// gg has no stored move-force baseline. the claim keeps whatever the body already has.
		return body != null ? body.moveForce : 0f;
	}

	internal static void ApplyLocalSpeciesConfig(SpeciesData data)
	{
		if (data == null)
			return;
		try
		{
			SpeciesBalanceConfig.ApplyStatConfig(data);
		}
		catch (System.Exception e)
		{
			MPBridgePlugin.Log?.LogWarning($"[GG-MP] ApplyStatConfig threw: {e.Message}");
		}
	}

	static bool _replacerLooked;
	static bool _replacerReady;
	static Action<Body, int> _setSlot;
	static Func<int> _getSlot;
	static Action<int> _setCurrent;

	internal static void NoteReplacer()
	{
		EnsureReplacer();
	}

	static void EnsureReplacer()
	{
		if (_replacerLooked)
			return;
		_replacerLooked = true;
		if (!Chainloader.PluginInfos.ContainsKey("spritereplacer"))
		{
			MPBridgePlugin.Log?.LogInfo("[GG-MP] sprite replacer not loaded, skin slots left alone");
			return;
		}
		Type replacer = AccessTools.TypeByName("SpriteReplacerPlugin");
		if (replacer == null)
		{
			MPBridgePlugin.Log?.LogInfo("[GG-MP] sprite replacer listed but its type was not found, skin slots left alone");
			return;
		}
		_setSlot = Calls.Bind<Action<Body, int>>(AccessTools.Method(replacer, "SetBodySlot", new[] { typeof(Body), typeof(int) }));
		if (_setSlot == null)
		{
			MPBridgePlugin.Log?.LogInfo("[GG-MP] sprite replacer has no SetBodySlot, skin slots left alone");
			return;
		}
		var current = replacer.GetProperty("CurrentSlot");
		if (current != null)
		{
			_getSlot = Calls.Bind<Func<int>>(current.GetGetMethod(true));
			_setCurrent = Calls.Bind<Action<int>>(current.GetSetMethod(true));
		}
		_replacerReady = true;
		MPBridgePlugin.Log?.LogInfo("[GG-MP] sprite replacer hooked");
	}

	internal static void SetSpriteSlot(Body body, int slot)
	{
		if (body == null || slot <= 0)
			return;
		EnsureReplacer();
		if (!_replacerReady)
			return;
		// SetBodySlot writes the global CurrentSlot. a remote body must not steal the local one.
		bool local = PlayerCamera.main != null && PlayerCamera.main.body == body;
		int previous = _getSlot != null ? _getSlot() : 0;
		_setSlot(body, slot);
		if (!local && _setCurrent != null)
			_setCurrent(previous);
	}
}
