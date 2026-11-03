extern alias mpreplacer;
using System;
using BepInEx.Bootstrap;
using CasualtiesTogetherUtils;
using HarmonyLib;
using Together;

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

    internal static void NoteReplacer()
	{
		// JIT will throw FIleNotFound when attempting to run a method that references an assembly that is not loaded. can be ignored
		try
		{
			loadMSR();
		}
		catch (System.IO.FileNotFoundException) { }
		catch (Exception e)
		{
            MPBridgePlugin.Log?.LogError($"[GG-MP] loadMSR threw: {e.Message}");
        }

		try
		{
            loadBSR();
		}
		catch (System.IO.FileNotFoundException) { }
		catch (Exception e)
		{
            MPBridgePlugin.Log?.LogError($"[GG-MP] loadBSR threw: {e.Message}");
        }
	}

	static void loadBSR()
	{
		if (_replacerLooked) return;
        _replacerLooked = true;
        SetSpriteSlot = (Action<Body, int, knetid>)bsr_SetSpriteSolt;
        MPBridgePlugin.Log?.LogDebug($"[GG-MP] Loaded body sprite replacer compat");
    }

	static void loadMSR()
	{
        if (_replacerLooked) return;
        _replacerLooked = true;
        SetSpriteSlot = (Action<Body, int, knetid>)msr_SetSpriteSlot;
		MPBridgePlugin.usingMSR = true;
        MPBridgePlugin.Log?.LogDebug($"[GG-MP] Loaded multiplayer sprite replacer compat");
    }

	public static Action<Body, int, knetid> SetSpriteSlot = delegate { };

	static void bsr_SetSpriteSolt(Body body, int slot, knetid _)
	{
        if (!body || slot <= 0)
            return;
        bool local = PlayerCamera.main != null && PlayerCamera.main.body == body;
		int previous = SpriteReplacerPlugin.CurrentSlot;
        SpriteReplacerPlugin.SetBodySlot(body, slot);
        if (!local)
            SpriteReplacerPlugin.CurrentSlot = previous;
    }

	static void msr_SetSpriteSlot(Body body, int slot, knetid id)
	{
        if (!body || slot <= 0 || body != PlayerCamera.main?.body)
            return;

		if(SpeciesMultiplayerSync.knownSpeciesNames.TryGetValue(id, out var name))
		{
            if (!mpreplacer.SpriteCache.SwitchToSlot($"GG{name}", false) && !mpreplacer.SpriteCache.SwitchToSlot($"st{slot}", false))
            {
                MPBridgePlugin.Log?.LogDebug($"[GG-MP] Skin not found for {name}");
            }
		}
		else if(!mpreplacer.SpriteCache.SwitchToSlot($"st{slot}", false))
		{
            MPBridgePlugin.Log?.LogDebug($"[GG-MP] Skin not found for {slot}");
        }
    }
}
