using System;
using System.Collections.Generic;
using HarmonyLib;
using LiteNetLib.Utils;
using Together;
using UnityEngine;

namespace GunsawGenetics;

// the albino trader roll and the sell lookup both happen per machine.
// the host decides the skin. a dead item id must not abort the sale.
internal static class TraderMp
{
	const string SkinMsg = "GG.Trader.Skin";

	static readonly HashSet<ushort> _albino = new HashSet<ushort>();
	static readonly HashSet<ushort> _pending = new HashSet<ushort>();
	static readonly HashSet<int> _stocked = new HashSet<int>();
	static readonly Action<TraderScript> Convert = Calls.Bind<Action<TraderScript>>(AccessTools.Method(typeof(AlbinoTrader), "Convert"));
	static readonly Action<TraderScript> AddStock = Calls.Bind<Action<TraderScript>>(AccessTools.Method(typeof(AlbinoInventoryPatch), "Postfix"));

	internal static void Register()
	{
		Multiplayer.RegisterCustomClientReceiver(SkinMsg, OnClientSkin);
	}

	internal static void SendCatchup(ScavPlayer plr)
	{
		if (plr == null)
			return;
		foreach (ushort id in _albino)
			Send(id, plr.playerId);
	}

	static void Send(ushort id, knetid client)
	{
		var writer = Multiplayer.CreateNamedWriter(SkinMsg);
		writer.Put(id);
		NetIo.ServerSend(writer, client);
	}

	static void Broadcast(ushort id)
	{
		var writer = Multiplayer.CreateNamedWriter(SkinMsg);
		writer.Put(id);
		NetIo.ServerSend(writer, ServerMain.AllClientIdsExceptHost);
	}

	static void OnClientSkin(NetDataReader reader)
	{
		ushort id = reader.GetUShort();
		if (!TryTrader(id, out TraderScript trader))
		{
			_pending.Add(id);
			return;
		}
		Apply(trader);
	}

	static bool TryTrader(ushort id, out TraderScript trader)
	{
		trader = null;
		if (!NetObjectRegistry.TryGetSyncInfo((knetid)id, out var si) || si == null || si.go == null)
			return false;
		trader = si.go.GetComponent<TraderScript>();
		return trader != null;
	}

	static void Apply(TraderScript trader)
	{
		if (trader == null)
			return;
		bool already = trader.character == 4;
		if (!_stocked.Add(trader.GetInstanceID()) && already)
			return;
		try
		{
			if (!already && Convert != null)
				Convert(trader);
			if (AddStock != null)
				AddStock(trader);
			MPBridgePlugin.Log?.LogInfo("[GG-MP] albino trader applied character=" + trader.character);
		}
		catch (Exception e)
		{
			MPBridgePlugin.Log?.LogWarning("[GG-MP] albino trader skin failed: " + e.Message);
		}
	}

	internal static void TickPending()
	{
		if (_pending.Count == 0)
			return;
		ushort[] ids = new ushort[_pending.Count];
		_pending.CopyTo(ids);
		foreach (ushort id in ids)
		{
			if (!TryTrader(id, out TraderScript trader))
				continue;
			_pending.Remove(id);
			Apply(trader);
		}
	}

	// gg rolls this on every machine. clients wait for the host.
	[HarmonyPatch(typeof(AlbinoTraderConvertPatch), "Prefix")]
	static class OneRoll
	{
		static bool Prefix()
		{
			if (!SpeciesMultiplayerSync.IsNetworkRunning())
				return true;
			return SpeciesMultiplayerSync.IsHost();
		}
	}

	[HarmonyPatch(typeof(TraderScript), "Start")]
	static class ShareSkin
	{
		static void Postfix(TraderScript __instance)
		{
			if (__instance == null || !SpeciesMultiplayerSync.IsNetworkRunning())
				return;
			if (!SpeciesMultiplayerSync.TryGetTraderSyncId(__instance, out ushort id))
				return;
			if (SpeciesMultiplayerSync.IsHost())
			{
				if (__instance.character != 4)
					return;
				_albino.Add(id);
				Broadcast(id);
				return;
			}
			if (_pending.Contains(id))
			{
				_pending.Remove(id);
				Apply(__instance);
			}
		}
	}

	// haggle rebuilds the whole trade list. holding the button was doing that several times a second.
	[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.RefreshTraderInventories))]
	static class SlowTradeList
	{
		static float _next;

		static bool Prefix()
		{
			if (Time.unscaledTime < _next)
				return false;
			_next = Time.unscaledTime + 0.25f;
			return true;
		}
	}

	internal static void Install(Harmony harmony)
	{
		var sell = AccessTools.Method(typeof(TraderScript_GiveItem_MultiplayerPatch), "Server_TraderSell");
		if (sell == null || harmony == null)
		{
			MPBridgePlugin.Log?.LogWarning("[GG-MP] trader sell hook missing");
			return;
		}
		harmony.Patch(sell, prefix: new HarmonyMethod(typeof(SafeSell), "Prefix"));
	}

	// a sync entry with no object used to throw and leave the sold item in the bag.
	static class SafeSell
	{
		static bool Prefix(knetid clientId, NetDataReader reader)
		{
			try
			{
				Sell(clientId, reader);
			}
			catch (Exception e)
			{
				MPBridgePlugin.Log?.LogWarning("[GG-MP] trader sell failed: " + e.Message);
			}
			return false;
		}

		static void Sell(knetid clientId, NetDataReader reader)
		{
			reader.Get(out knetid targettrader);
			reader.Get(out knetid solditem);
			if (!TraderSync.Server_TraderInteractionCheck(clientId, targettrader, "SellItem", out ScavPlayer plr, out SyncInfo traderSi, out TrackerTrader tt))
				return;
			if (tt != null)
				tt.focused_body = plr.body;
			Item item = Resolve(plr != null ? plr.body : null, solditem);
			if (item == null || traderSi == null || traderSi.trader == null || traderSi.trader.reputation < 30f || !InReach(item, plr.body))
			{
				MPBridgePlugin.Log?.LogInfo("[GG-MP] trader sell denied item=" + solditem.id);
				return;
			}
			traderSi.trader.GiveItem(item);
		}

		static Item Resolve(Body body, knetid id)
		{
			if (NetObjectRegistry.NetIdToSyncInfoDict != null
				&& NetObjectRegistry.NetIdToSyncInfoDict.TryGetValue(id, out SyncInfo si)
				&& si != null && si.go != null)
			{
				Item direct = si.go.GetComponent<Item>();
				if (direct != null)
					return direct;
			}
			if (body == null)
				return null;
			Item[] found = body.GetComponentsInChildren<Item>(true);
			foreach (Item item in found)
			{
				if (item != null && Same(item, id))
					return item;
			}
			return null;
		}

		static bool Same(Item item, knetid id)
		{
			var tracker = item.GetComponent<SyncInfoGameObjectTracker>();
			if (tracker == null)
				return false;
			return tracker.syncId.id == id.id;
		}

		static bool InReach(Item item, Body body)
		{
			if (item == null || body == null)
				return false;
			if (item.transform.IsChildOf(body.transform))
				return true;
			float reach = ((Vector2)item.transform.position - (Vector2)body.transform.position).sqrMagnitude;
			return reach <= 20f * 20f;
		}
	}
}
