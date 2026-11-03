using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Together;
using UnityEngine;

namespace GunsawGenetics;

// species lock lives in the player's mp save.sv as ggSpecies.
// the bepinex SavedSpecies entry is the solo choice and is left alone.
internal static class SpeciesSave
{
	struct Row
	{
		internal string Species;
		internal int Slot;
	}

	static readonly Dictionary<string, Row> Locked = new Dictionary<string, Row>();
	static readonly Dictionary<string, HashSet<string>> Kits = new Dictionary<string, HashSet<string>>();
	// a start or a tutorial has not loaded a save. the old file stays unread until this run writes its own.
	static bool _fresh;
	static bool _wrote;
	static System.Reflection.FieldInfo _preserveSave;

	internal static void BeginFreshRun()
	{
		_fresh = true;
		_wrote = false;
		Locked.Clear();
		Kits.Clear();
	}

	internal static void ContinueRun()
	{
		_fresh = false;
		_wrote = true;
		Locked.Clear();
		Kits.Clear();
	}

	static bool LeavingFileAlone()
	{
		if (_preserveSave == null)
			_preserveSave = AccessTools.Field(typeof(SavesystemPatch), "PreserveSave");
		return _preserveSave != null && _preserveSave.GetValue(null) is bool on && on;
	}

	internal static bool HasKit(ScavPlayer plr, string key)
	{
		if (plr == null || string.IsNullOrEmpty(key))
			return false;
		string id = plr.GetPersistentId();
		if (Kits.TryGetValue(id, out HashSet<string> have) && have.Contains(key))
			return true;
		if (_fresh && !_wrote)
			return false;
		return ReadKit(SavePath(id), id, key);
	}

	internal static void NoteKit(ScavPlayer plr, string key)
	{
		if (plr == null || string.IsNullOrEmpty(key))
			return;
		string id = plr.GetPersistentId();
		if (!Kits.TryGetValue(id, out HashSet<string> have))
		{
			have = new HashSet<string>();
			Kits[id] = have;
		}
		if (!have.Add(key))
			return;
		if (_fresh && !_wrote)
			return;
		string path = SavePath(id);
		if (!File.Exists(path))
			return;
		try
		{
			var root = JObject.Parse(SaveSystem.Unzip(File.ReadAllBytes(path)));
			root[key] = 1;
			File.WriteAllBytes(path, SaveSystem.Zip(root.ToString(Newtonsoft.Json.Formatting.None)));
		}
		catch (Exception e)
		{
			MPBridgePlugin.Log?.LogWarning("[GG-MP] kit stamp failed: " + e.Message);
		}
	}

	internal static bool HasLimbRecord(Body body)
	{
		if (body == null)
			return false;
		ScavPlayer plr = null;
		foreach (var pair in ScavPlayer.BodyToPlayerDict)
		{
			if (pair.Key == body)
			{
				plr = pair.Value;
				break;
			}
		}
		if (plr == null)
			return false;
		if (_fresh && !_wrote)
			return false;
		string path = SavePath(plr.GetPersistentId());
		if (!File.Exists(path))
			return false;
		try
		{
			var root = JObject.Parse(SaveSystem.Unzip(File.ReadAllBytes(path)));
			var limbs = root["limbs"] as JArray;
			return limbs != null && limbs.Count > 0;
		}
		catch
		{
			return false;
		}
	}

	internal static void Persist(ScavPlayer plr, string species, int slot)
	{
		if (plr == null || string.IsNullOrEmpty(species))
			return;
		string id = plr.GetPersistentId();
		Locked[id] = new Row { Species = species, Slot = slot };
		// the old file is still the previous run. the next real checkpoint replaces it, then the stamp lands.
		bool hold = _fresh && !_wrote && File.Exists(SavePath(id));
		if (!hold)
		{
			string path = SavePath(id);
			if (!File.Exists(path))
				TryWriteCheckpoint(plr);
			if (File.Exists(path))
				Stamp(path, species, slot);
		}
		MPBridgePlugin.Log?.LogInfo($"[GG-MP] species locked id={id} species={species} slot={slot}");
	}

	internal static bool TryRead(ScavPlayer plr, out string species, out int slot)
	{
		species = null;
		slot = 0;
		if (plr == null)
			return false;
		string id = plr.GetPersistentId();
		if (Locked.TryGetValue(id, out Row row) && !string.IsNullOrEmpty(row.Species))
		{
			species = row.Species;
			slot = row.Slot;
			return true;
		}
		if (_fresh)
			return false;
		if (!Read(SavePath(id), out species, out slot))
			return false;
		Locked[id] = new Row { Species = species, Slot = slot };
		return true;
	}

	internal static void Capture(ScavPlayer plr)
	{
		if (plr == null)
			return;
		// a fresh start keeps the previous file on disk but must not treat it as this run's choice.
		if (_fresh)
			return;
		string id = plr.GetPersistentId();
		if (Locked.ContainsKey(id))
			return;
		if (Read(SavePath(id), out string species, out int slot))
			Locked[id] = new Row { Species = species, Slot = slot };
	}

	internal static void Restamp(ScavPlayer plr)
	{
		if (plr == null)
			return;
		string id = plr.GetPersistentId();
		if (!Locked.TryGetValue(id, out Row row) || string.IsNullOrEmpty(row.Species))
			return;
		string path = SavePath(id);
		if (File.Exists(path))
			Stamp(path, row.Species, row.Slot);
	}

	internal static void SaveBody(ScavPlayer plr)
	{
		TryWriteCheckpoint(plr);
	}

	static readonly Action<ScavPlayer, bool> WriteCheckpoint = Calls.Bind<Action<ScavPlayer, bool>>(AccessTools.Method(typeof(PlayerCheckpoint), "WriteOne"));

	static void TryWriteCheckpoint(ScavPlayer plr)
	{
		try
		{
			if (WriteCheckpoint == null)
				return;
			WriteCheckpoint(plr, false);
		}
		catch (Exception e)
		{
			MPBridgePlugin.Log?.LogWarning("[GG-MP] checkpoint before species stamp failed: " + e.Message);
		}
	}

	static string SavePath(string persistentId)
	{
		return Path.Combine(Application.persistentDataPath, "mp_save", persistentId, "save.sv");
	}

	static void Stamp(string path, string species, int slot)
	{
		try
		{
			var root = JObject.Parse(SaveSystem.Unzip(File.ReadAllBytes(path)));
			root["ggSpecies"] = species;
			root["ggSpeciesSlot"] = slot;
			string id = Path.GetFileName(Path.GetDirectoryName(path));
			if (!string.IsNullOrEmpty(id) && Kits.TryGetValue(id, out HashSet<string> keys))
			{
				foreach (string key in keys)
					root[key] = 1;
			}
			File.WriteAllBytes(path, SaveSystem.Zip(root.ToString(Newtonsoft.Json.Formatting.None)));
		}
		catch (Exception e)
		{
			MPBridgePlugin.Log?.LogWarning("[GG-MP] species stamp failed: " + e.Message);
		}
	}

	static bool Read(string path, out string species, out int slot)
	{
		species = null;
		slot = 0;
		if (!File.Exists(path))
			return false;
		try
		{
			var root = JObject.Parse(SaveSystem.Unzip(File.ReadAllBytes(path)));
			species = root.Value<string>("ggSpecies");
			slot = root.Value<int?>("ggSpeciesSlot") ?? 0;
			string id = Path.GetFileName(Path.GetDirectoryName(path));
			if (!string.IsNullOrEmpty(id))
				RememberKits(id, root);
			return !string.IsNullOrEmpty(species);
		}
		catch
		{
			return false;
		}
	}

	static bool ReadKit(string path, string id, string key)
	{
		if (!File.Exists(path))
			return false;
		try
		{
			var root = JObject.Parse(SaveSystem.Unzip(File.ReadAllBytes(path)));
			RememberKits(id, root);
			return Kits.TryGetValue(id, out HashSet<string> have) && have.Contains(key);
		}
		catch
		{
			return false;
		}
	}

	static void RememberKits(string id, JObject root)
	{
		if (root == null || string.IsNullOrEmpty(id))
			return;
		foreach (string key in new[] { "ggKitAlbino", "ggKitMercenary" })
		{
			if (root.Value<int?>(key) != 1)
				continue;
			if (!Kits.TryGetValue(id, out HashSet<string> have))
			{
				have = new HashSet<string>();
				Kits[id] = have;
			}
			have.Add(key);
		}
	}

	// saveandquit writes the body through SaveGame and drops keys it does not know.
	[HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.SaveGame))]
	static class KeepSpeciesOnSave
	{
		static void Postfix()
		{
			if (LeavingFileAlone())
				return;
			try
			{
				if (PlayerCamera.main == null || PlayerCamera.main.body == null)
					return;
				Body body = PlayerCamera.main.body;
				foreach (var pair in ScavPlayer.BodyToPlayerDict)
				{
					if (pair.Key != body)
						continue;
					Restamp(pair.Value);
					return;
				}
			}
			catch (Exception e)
			{
				MPBridgePlugin.Log?.LogWarning("[GG-MP] species restamp after save failed: " + e.Message);
			}
		}
	}

	// WriteForced replaces save.sv. put the lock back after every checkpoint.
	[HarmonyPatch(typeof(PlayerCheckpoint), "WriteOne")]
	static class KeepSpeciesOnCheckpoint
	{
		static void Prefix(ScavPlayer plr)
		{
			Capture(plr);
		}

		static void Postfix(ScavPlayer plr)
		{
			if (LeavingFileAlone())
				return;
			_wrote = true;
			Restamp(plr);
		}
	}

	// together's start prefix returns false, so this has to run first.
	[HarmonyPatch(typeof(PreRunScript), nameof(PreRunScript.StartRun))]
	static class FreshStart
	{
		[HarmonyPriority(Priority.First)]
		static void Prefix()
		{
			SpeciesMultiplayerSync.PrepareFreshStart();
		}
	}

	[HarmonyPatch(typeof(PreRunScript), nameof(PreRunScript.StartTutorial))]
	static class TutorialStart
	{
		[HarmonyPriority(Priority.First)]
		static void Prefix()
		{
			SpeciesMultiplayerSync.PrepareFreshStart();
		}
	}

	[HarmonyPatch(typeof(PreRunScript), nameof(PreRunScript.LoadRun))]
	static class ContinuedStart
	{
		[HarmonyPriority(Priority.First)]
		static void Prefix()
		{
			SpeciesMultiplayerSync.PrepareContinue();
		}
	}
}
