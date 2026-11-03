using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GunsawGenetics;

// host says pick or locked. the solo picker stays shut until that pick
// lands on a body that is already in the world. one choice, then it stays shut.
// applying a species here does not keep the bepinex SavedSpecies write.
internal static class SpeciesGate
{
	enum Mode
	{
		Wait,
		Pick,
		Locked
	}

	static Mode _mode = Mode.Wait;
	static string _forced;
	static bool _committed;
	static bool _chose;
	static bool _ignoredPick;
	static float _askAt;
	static bool _seenWorld;

	internal static void Reset()
	{
		_mode = Mode.Wait;
		_forced = null;
		_committed = false;
		_chose = false;
		_ignoredPick = false;
	}

	internal static void Tick()
	{
		if (!SpeciesMultiplayerSync.IsNetworkRunning())
		{
			Reset();
			_seenWorld = false;
			return;
		}
		NoteLeftWorld();
		SpeciesMultiplayerSync.NoteTutorialEntry();
		if (_mode == Mode.Wait || (_mode == Mode.Locked && _forced == null))
			AskIfNeeded();
		SpeciesMultiplayerSync.TickDecisions();
		// solo WaitAndInitialize also raises the picker. only the host's pick,
		// after the body exists, may leave it up. a second open after the choice
		// freezes the menu because the first confirm clears PlayerBody.
		if (SoloOpenAllowed())
			OpenPicker();
		else
			ShutPicker();
	}

	static void NoteLeftWorld()
	{
		bool playing = SceneManager.GetActiveScene().name == "SampleScene" && WorldGeneration.world != null;
		if (_seenWorld && !playing)
			Reset();
		_seenWorld = playing;
	}

	internal static bool SoloOpenAllowed()
	{
		return _mode == Mode.Pick && !_committed && BodyReady();
	}

	internal static void AllowPick()
	{
		// a confirm sticks. a lock that arrived before the tutorial picker does not.
		if (_chose)
		{
			if (!_ignoredPick)
			{
				_ignoredPick = true;
				MPBridgePlugin.Log?.LogInfo("[GG-MP] gate pick ignored, already chosen");
			}
			return;
		}
		if (_committed)
		{
			_committed = false;
			_forced = null;
		}
		_mode = Mode.Pick;
		MPBridgePlugin.Log?.LogInfo("[GG-MP] gate pick");
	}

	internal static void CommitLocal(string species, int slot)
	{
		if (string.IsNullOrEmpty(species))
			return;
		_committed = true;
		_chose = true;
		ShutPicker();
		ForceLocal(species, slot);
	}

	internal static void ForceLocal(string species, int slot)
	{
		_mode = Mode.Locked;
		ShutPicker();
		if (string.IsNullOrEmpty(species))
			return;
		_committed = true;
		if (_forced == species)
			return;
		// resume is identity only. ApplySpecies rewrites skills and xp back to the species floor.
		SpeciesData data = Resolve(species);
		Body body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
		if (data == null || body == null)
			return;
		_forced = species;
		GunsawGeneticsPlugin.PlayerBody = body;
		SpeciesAbilityManager.ActiveSpecies = data.Name;
		SpeciesAbilityManager.PlayerBody = body;
		if (slot <= 0)
			slot = data.SpriteSlot;
		SpeciesPickerUI.TrySwitchSprite(slot);
		OrangeArms.EnsureIfOrange(body, data.Name);
		MPBridgePlugin.Log?.LogInfo("[GG-MP] gate locked identity " + species + " slot=" + slot);
	}

	static bool BodyReady()
	{
		if (PlayerCamera.main == null || PlayerCamera.main.body == null)
			return false;
		WorldGeneration world = WorldGeneration.world;
		return world != null && world.worldExists && !world.generatingWorld;
	}

	static void OpenPicker()
	{
		GunsawGeneticsPlugin.PlayerBody = PlayerCamera.main.body;
		if (GunsawGeneticsPlugin.ShowPicker)
			return;
		GunsawGeneticsPlugin.ShowPicker = true;
		Time.timeScale = 0f;
		MPBridgePlugin.Log?.LogInfo("[GG-MP] gate picker open");
	}

	static void ShutPicker()
	{
		if (GunsawGeneticsPlugin.ShowPicker)
			GunsawGeneticsPlugin.ShowPicker = false;
		if (Time.timeScale == 0f)
			Time.timeScale = 1f;
	}

	static void AskIfNeeded()
	{
		if (SpeciesMultiplayerSync.IsHost())
			return;
		if (Time.realtimeSinceStartup < _askAt)
			return;
		Body body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
		WorldGeneration world = WorldGeneration.world;
		if (body == null || world == null || world.generatingWorld)
			return;
		_askAt = Time.realtimeSinceStartup + 2f;
		SpeciesMultiplayerSync.AskGate();
	}

	static SpeciesData Resolve(string species)
	{
		SpeciesData data = SpeciesData.GetByName(species);
		if (data != null)
			return data;
		CustomSpeciesData custom = CustomSpeciesManager.Find(species);
		return custom != null ? custom.ToSpeciesData() : null;
	}

	// ApplySpecies writes SavedSpecies when the run is real. put the solo value back.
	[HarmonyPatch(typeof(SpeciesPickerUI), "ApplySpecies")]
	static class KeepSoloSpeciesConfig
	{
		static string _previous;
		static bool _restore;

		static void Prefix()
		{
			if (!SpeciesMultiplayerSync.IsNetworkRunning() || GunsawGeneticsPlugin.SavedSpecies == null)
				return;
			_previous = GunsawGeneticsPlugin.SavedSpecies.Value;
			_restore = true;
		}

		static void Postfix()
		{
			if (!_restore || GunsawGeneticsPlugin.SavedSpecies == null)
				return;
			_restore = false;
			GunsawGeneticsPlugin.SavedSpecies.Value = _previous;
		}
	}

	// the confirm already rewrote this body. a later solo open must not come back.
	[HarmonyPatch(typeof(SpeciesPickerUI), "ApplySpecies")]
	static class CommitOnApply
	{
		static void Prefix(out Body __state)
		{
			__state = GunsawGeneticsPlugin.PlayerBody;
		}

		static void Postfix(SpeciesData sp, Body __state)
		{
			if (!SpeciesMultiplayerSync.IsNetworkRunning() || sp == null || __state == null)
				return;
			if (GunsawGeneticsPlugin.PlayerBody != null)
				return;
			if (!string.Equals(SpeciesAbilityManager.ActiveSpecies, sp.Name, System.StringComparison.Ordinal))
				return;
			CommitLocal(sp.Name, sp.SpriteSlot);
		}
	}

	// new runs raise the solo picker half a second after the body starts.
	// that write lands after a confirm and reopens a menu that can no longer close.
	internal static void InstallSoloSilence(Harmony harmony)
	{
		try
		{
			MethodInfo iterator = AccessTools.Method(typeof(BodyStartPatch), "WaitAndInitialize");
			MethodInfo moveNext = AccessTools.EnumeratorMoveNext(iterator);
			if (moveNext == null)
			{
				MPBridgePlugin.Log?.LogWarning("[GG-MP] solo picker silence missed");
				return;
			}
			harmony.Patch(moveNext, postfix: new HarmonyMethod(typeof(SpeciesGate), nameof(SilenceMoveNext)));
		}
		catch (Exception e)
		{
			MPBridgePlugin.Log?.LogWarning("[GG-MP] solo picker silence failed: " + e.Message);
		}
	}

	static void SilenceMoveNext()
	{
		if (!SpeciesMultiplayerSync.IsNetworkRunning())
			return;
		if (SoloOpenAllowed())
			return;
		if (GunsawGeneticsPlugin.ShowPicker)
			GunsawGeneticsPlugin.ShowPicker = false;
		if (Time.timeScale == 0f)
			Time.timeScale = 1f;
	}
}
