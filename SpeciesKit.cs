using System;
using System.Collections.Generic;
using CasualtiesTogetherUtils;
using HarmonyLib;
using Together;
using UnityEngine;

namespace GunsawGenetics;

// host adds the species kit after the save is already on the body.
// bound gear is worn if missing. one-shots are offered once. nothing already
// in a slot is dropped to make room.
internal static class SpeciesKit
{
	static readonly HashSet<int> _done = new HashSet<int>();
	static readonly HashSet<int> _waiting = new HashSet<int>();
	static bool _fromKit;
	static bool _hostRoll;
	static readonly Action<NetBody, bool> SendHealth = Calls.Bind<Action<NetBody, bool>>(
		AccessTools.Method(AccessTools.TypeByName("Together.MedicalSync"), "Server_SendCharacterHealth", new[] { typeof(NetBody), typeof(bool) }));

	internal static void RollAlbino(ScavPlayer plr)
	{
		if (plr == null || plr.body == null)
			return;
		bool rolled = false;
		_hostRoll = true;
		try
		{
			AlbinoWounds.RollAndApply(plr.body);
			rolled = true;
		}
		catch (Exception e)
		{
			MPBridgePlugin.Log?.LogWarning("[GG-MP] albino wounds failed: " + e.Message);
		}
		finally
		{
			_hostRoll = false;
		}
		if (!rolled)
			return;
		if (plr.body.TryGetNetBody(out NetBody nb) && nb != null && SendHealth != null)
		{
			try
			{
				SendHealth(nb, true);
			}
			catch (Exception e)
			{
				MPBridgePlugin.Log?.LogWarning("[GG-MP] albino wounds send failed: " + e.Message);
			}
		}
		int id = 0;
		if (nb != null && nb.syncBody != null)
			id = nb.netId.id;
		MPBridgePlugin.Log?.LogInfo("[GG-MP] albino wounds applied netId=" + id);
	}

	internal static void Grant(ScavPlayer plr, string species)
	{
		if (plr == null || plr.body == null || string.IsNullOrEmpty(species))
			return;
		if (SpeciesMultiplayerSync.IsNetworkRunning() && !SpeciesMultiplayerSync.IsHost())
			return;
		int token = plr.body.GetInstanceID();
		if (_done.Contains(token))
			return;
		// the save load instantiates every saved item. granting before that
		// wears a second copy, and the loader then nests the saved one into it.
		if (!InventoryReady(plr))
		{
			if (_waiting.Add(token))
				MPBridgePlugin.Log?.LogInfo("[GG-MP] kit waiting for save load id=" + plr.GetPersistentId());
			return;
		}
		bool scratch = Util.IsTutorialWorld();
		_fromKit = true;
		try
		{
			switch (species)
			{
				case "Voyager":
					EnsureBound(plr.body, "voyagercloak");
					break;
				case "Albino":
					EnsureBound(plr.body, "nsc");
					OfferOnce(plr, "ggKitAlbino", scratch, "scarf", "painkillers");
					break;
				case "Mercenary":
					if (MercenaryChips.AllIds != null)
					{
						foreach (string id in MercenaryChips.AllIds)
							EnsureBound(plr.body, id);
					}
					OfferOnce(plr, "ggKitMercenary", scratch, "scarf");
					break;
				case "Orange":
					OrangeArms.Ensure(plr.body);
					break;
			}
			_done.Add(token);
		}
		catch (Exception e)
		{
			MPBridgePlugin.Log?.LogWarning("[GG-MP] kit grant failed " + species + ": " + e.Message);
		}
		finally
		{
			_fromKit = false;
		}
	}

	static bool InventoryReady(ScavPlayer plr)
	{
		if (Util.IsTutorialWorld())
			return true;
		if (plr.ServerPlayerState != null && plr.ServerPlayerState.loaded_save)
			return true;
		return !PlayerCheckpoint.HasPlayerFile(plr);
	}

	static void EnsureBound(Body body, string id)
	{
		if (HasId(body, id))
		{
			try
			{
				if (body.GetWearable(id) == null)
				{
					Item have = Find(body, id);
					if (have != null)
						body.WearWearable(have);
				}
			}
			catch
			{
			}
			MPBridgePlugin.Log?.LogInfo("[GG-MP] kit " + id + " already on body");
			return;
		}
		BoundWearables.EnsureWorn(body, id);
		MPBridgePlugin.Log?.LogInfo("[GG-MP] kit " + id + (HasId(body, id) ? " added" : " not worn"));
	}

	static void OfferOnce(ScavPlayer plr, string stamp, bool scratch, params string[] ids)
	{
		if (!scratch && SpeciesSave.HasKit(plr, stamp))
			return;
		foreach (string id in ids)
		{
			if (id == "painkillers")
			{
				if (HasId(plr.body, id))
				{
					MPBridgePlugin.Log?.LogInfo("[GG-MP] kit " + id + " already on body");
					continue;
				}
				TryPocket(plr.body, id);
				MPBridgePlugin.Log?.LogInfo("[GG-MP] kit " + id + (HasId(plr.body, id) ? " added" : " not pocketed"));
				continue;
			}
			EnsureBound(plr.body, id);
		}
		if (scratch)
			return;
		foreach (string id in ids)
		{
			if (!HasId(plr.body, id))
				return;
		}
		SpeciesSave.NoteKit(plr, stamp);
		MPBridgePlugin.Log?.LogInfo("[GG-MP] kit " + stamp + " id=" + plr.GetPersistentId());
	}

	static void TryPocket(Body body, string id)
	{
		GameObject go = Utils.Create(id, body.transform.position, 0f);
		if (go == null || !go.TryGetComponent<Item>(out Item item))
			return;
		body.AutoPickUpItem(item);
		if (item == null || item.transform.parent == null)
			UnityEngine.Object.Destroy(go);
	}

	static bool HasId(Body body, string id)
	{
		if (body == null)
			return false;
		if (body.HoldingItem(id))
			return true;
		try
		{
			if (body.GetWearable(id) != null)
				return true;
		}
		catch
		{
		}
		var all = body.GetAllItemsThorough();
		if (all == null)
			return false;
		for (int i = 0; i < all.Count; i++)
		{
			Item item = all[i];
			if (item != null && item.id == id)
				return true;
		}
		return false;
	}

	static Item Find(Body body, string id)
	{
		var all = body.GetAllItemsThorough();
		if (all == null)
			return null;
		for (int i = 0; i < all.Count; i++)
		{
			Item item = all[i];
			if (item != null && item.id == id)
				return item;
		}
		return null;
	}

	// ApplySpecies also spawns these. in mp the host grant is the only copy,
	// and it runs after the save items are already on the body.
	[HarmonyPatch(typeof(SpeciesPickerUI), "SpawnAndWear")]
	static class SkipPickerWear
	{
		static bool Prefix()
		{
			if (_fromKit)
				return true;
			return !SpeciesMultiplayerSync.IsNetworkRunning();
		}
	}

	[HarmonyPatch(typeof(SpeciesPickerUI), "SpawnAndPickUp")]
	static class SkipPickerPocket
	{
		static bool Prefix()
		{
			if (_fromKit)
				return true;
			return !SpeciesMultiplayerSync.IsNetworkRunning();
		}
	}

	[HarmonyPatch(typeof(BoundWearables), "EnsureWorn")]
	static class SkipPickerBound
	{
		static bool Prefix()
		{
			if (_fromKit)
				return true;
			return !SpeciesMultiplayerSync.IsNetworkRunning();
		}
	}

	// a save that already has limbs is an old body. a new file can still roll.
	[HarmonyPatch(typeof(AlbinoWounds), nameof(AlbinoWounds.RollAndApply))]
	static class SkipLoadedAlbinoWounds
	{
		static bool Prefix(Body body)
		{
			if (_hostRoll)
				return true;
			if (!SpeciesMultiplayerSync.IsNetworkRunning())
				return true;
			// a client roll is overwritten by the next host snapshot, tutorial included
			if (!SpeciesMultiplayerSync.IsHost())
			{
				MPBridgePlugin.Log?.LogInfo("[GG-MP] albino wounds skipped, host rolls them");
				return false;
			}
			if (Util.IsTutorialWorld() || !SpeciesSave.HasLimbRecord(body))
				return true;
			MPBridgePlugin.Log?.LogInfo("[GG-MP] albino wounds skipped, save already has limbs");
			return false;
		}
	}
}
