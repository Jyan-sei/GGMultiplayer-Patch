using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using LiteNetLib.Utils;
using Together;
using UnityEngine;

namespace GunsawGenetics;

// gg calls these by name once CasualtiesMP is loaded. the host does the
// world change. other clients only get the visual.
public static partial class SpeciesMultiplayerSync
{
	const string CloakMsg = "GG.Cloak";
	const string CloakStateMsg = "GG.Cloak.State";
	const string CrystalMsg = "GG.Crystal";
	const string CrystalStateMsg = "GG.Crystal.State";
	const string WebSpawnMsg = "GG.Web.Spawn";
	const string WebSpawnedMsg = "GG.Web.Spawned";
	const string WebHitMsg = "GG.Web.Hit";
	const string WebBreakMsg = "GG.Web.Break";
	const string TalkMsg = "GG.Talk";
	const string TalkLineMsg = "GG.Talk.Line";
	const string StealMsg = "GG.Steal";
	const string StealResultMsg = "GG.Steal.Result";
	const string StealthMsg = "GG.Stealth";
	const string CannibalMsg = "GG.Cannibal";
	const string CannibalOkMsg = "GG.Cannibal.Ok";
	const string BiteMsg = "GG.Dune.Bite";
	const string LimbMsg = "GG.Limb.Gone";
	const string PrideMsg = "GG.Pride";
	const string RozaMsg = "GG.Roza.Hold";
	const string LeapyMsg = "GG.Leapy.Hold";
	const string LevitateMsg = "GG.Merc.Lift";
	const string AlbinoMsg = "GG.Albino.On";

	const int MaxWebsPerPlayer = 4;
	const float TalkReach = 12f;
	const float WebOriginReach = 30f;
	const float WebLandingReach = 80f;

	struct WebRec
	{
		public bool Point;
		public Vector2 Origin;
		public Vector2 Landing;
	}

	static readonly Dictionary<ushort, GameObject> _webs = new Dictionary<ushort, GameObject>();
	static readonly Dictionary<ushort, WebRec> _webRec = new Dictionary<ushort, WebRec>();
	static readonly Dictionary<ushort, List<ushort>> _websByOwner = new Dictionary<ushort, List<ushort>>();
	static readonly Dictionary<ushort, float> _webHitAt = new Dictionary<ushort, float>();
	static readonly Dictionary<ushort, bool> _cloak = new Dictionary<ushort, bool>();
	static readonly Dictionary<ushort, bool> _rozaHold = new Dictionary<ushort, bool>();
	static readonly Dictionary<ushort, bool> _leapyHold = new Dictionary<ushort, bool>();
	static readonly Dictionary<ushort, Vector2> _grapple = new Dictionary<ushort, Vector2>();
	static readonly Dictionary<ushort, AlbinoFx> _albino = new Dictionary<ushort, AlbinoFx>();
	static AlbinoPeak[] _albinoPeaks;

	struct AlbinoFx
	{
		public int Level;
		public float Active;
		public float Intensity;
		public float Crash;
		public float BaseSpeed;
		public bool On;
	}

	struct AlbinoPeak
	{
		public float Bp;
		public float Hr;
		public float Stamina;
		public float Consciousness;
		public float Speed;
	}

	static ushort _nextWebId = 1;
	static bool _cloakSent;
	static bool _cloakWas;
	static bool _grappleSent;
	static bool _grappleWas;
	static Vector2 _grapplePoint;

	static void RegisterHostPasses()
	{
		Multiplayer.RegisterCustomServerReceiver(CloakMsg, OnServerCloak);
		Multiplayer.RegisterCustomClientReceiver(CloakStateMsg, OnClientCloak);
		Multiplayer.RegisterCustomServerReceiver(CrystalMsg, OnServerCrystal);
		Multiplayer.RegisterCustomClientReceiver(CrystalStateMsg, OnClientCrystal);
		Multiplayer.RegisterCustomServerReceiver(WebSpawnMsg, OnServerWebSpawn);
		Multiplayer.RegisterCustomClientReceiver(WebSpawnedMsg, OnClientWebSpawn);
		Multiplayer.RegisterCustomServerReceiver(WebHitMsg, OnServerWebHit);
		Multiplayer.RegisterCustomClientReceiver(WebBreakMsg, OnClientWebBreak);
		Multiplayer.RegisterCustomServerReceiver(TalkMsg, OnServerTalk);
		Multiplayer.RegisterCustomClientReceiver(TalkLineMsg, OnClientTalk);
		Multiplayer.RegisterCustomServerReceiver(StealMsg, OnServerSteal);
		Multiplayer.RegisterCustomClientReceiver(StealResultMsg, OnClientSteal);
		Multiplayer.RegisterCustomServerReceiver(StealthMsg, OnServerStealth);
		Multiplayer.RegisterCustomServerReceiver(CannibalMsg, OnServerCannibal);
		Multiplayer.RegisterCustomClientReceiver(CannibalOkMsg, OnClientCannibal);
		Multiplayer.RegisterCustomServerReceiver(BiteMsg, OnServerBite);
		Multiplayer.RegisterCustomServerReceiver(LimbMsg, OnServerLimb);
		Multiplayer.RegisterCustomClientReceiver(PrideMsg, OnClientPride);
		Multiplayer.RegisterCustomServerReceiver(RozaMsg, OnServerRoza);
		Multiplayer.RegisterCustomServerReceiver(LeapyMsg, OnServerLeapy);
		Multiplayer.RegisterCustomServerReceiver(LevitateMsg, OnServerLevitate);
		Multiplayer.RegisterCustomServerReceiver(AlbinoMsg, OnServerAlbino);
		TraderMp.Register();
		MPBridgePlugin.Log?.LogInfo("[GG-MP] host passes registered");
	}

	static void SendAbilityCatchup(ScavPlayer plr)
	{
		if (plr == null)
			return;
		foreach (var kv in _cloak)
		{
			if (!kv.Value)
				continue;
			var writer = Multiplayer.CreateNamedWriter(CloakStateMsg);
			NetIo.Put(writer, kv.Key);
			NetIo.Put(writer, true);
			NetIo.ServerSend(writer, plr.playerId);
		}
		foreach (var kv in _grapple)
		{
			var writer = Multiplayer.CreateNamedWriter(CrystalStateMsg);
			NetIo.Put(writer, kv.Key);
			NetIo.Put(writer, true);
			NetIo.Put(writer, kv.Value.x);
			NetIo.Put(writer, kv.Value.y);
			NetIo.ServerSend(writer, plr.playerId);
		}
		TraderMp.SendCatchup(plr);
		foreach (var kv in _webRec)
		{
			var writer = Multiplayer.CreateNamedWriter(WebSpawnedMsg);
			NetIo.Put(writer, kv.Key);
			NetIo.Put(writer, kv.Value.Point);
			NetIo.Put(writer, kv.Value.Origin.x);
			NetIo.Put(writer, kv.Value.Origin.y);
			NetIo.Put(writer, kv.Value.Landing.x);
			NetIo.Put(writer, kv.Value.Landing.y);
			NetIo.ServerSend(writer, plr.playerId);
		}
	}

	static bool IsSpecies(Body body, string species)
	{
		if (body == null || string.IsNullOrEmpty(species))
			return false;
		string have = GetSpeciesForBody(body);
		if (string.IsNullOrEmpty(have) && PlayerCamera.main != null && body == PlayerCamera.main.body)
			have = SpeciesAbilityManager.ActiveSpecies;
		return string.Equals(have, species, StringComparison.Ordinal);
	}

	static bool Near(Component a, Component b, float units)
	{
		if (a == null || b == null)
			return false;
		Vector2 delta = (Vector2)a.transform.position - (Vector2)b.transform.position;
		return delta.sqrMagnitude <= units * units;
	}

	static bool TryOwner(ScavPlayer plr, out knetid netId, out Body body)
	{
		netId = default;
		body = null;
		if (plr == null || plr.netBody == null || plr.body == null)
			return false;
		netId = plr.netBody.netId;
		body = plr.body;
		return netId.id != 0;
	}

	internal static void BroadcastLocalCloakState(bool isCloaked)
	{
		if (!IsNetworkRunning() || PlayerCamera.main == null || PlayerCamera.main.body == null)
			return;
		if (_cloakSent && _cloakWas == isCloaked)
			return;
		_cloakSent = true;
		_cloakWas = isCloaked;
		knetid netId = LocalNetId();
		if (netId.id == 0)
			return;
		if (IsHost())
		{
			BroadcastCloak(netId, isCloaked);
			return;
		}
		var writer = Multiplayer.CreateNamedWriter(CloakMsg);
		NetIo.Put(writer, isCloaked);
		NetIo.ClientSend(writer);
	}

	static void OnServerCloak(ScavPlayer sender, NetDataReader reader)
	{
		bool cloaked = reader.GetBool();
		if (!TryOwner(sender, out knetid netId, out Body body) || !IsSpecies(body, "Voyager"))
			return;
		BroadcastCloak(netId, cloaked);
	}

	static void BroadcastCloak(knetid netId, bool isCloaked)
	{
		_cloak[netId.id] = isCloaked;
		ApplyCloak(netId, isCloaked);
		if (!Net.IsServer)
			return;
		var writer = Multiplayer.CreateNamedWriter(CloakStateMsg);
		NetIo.Put(writer, netId.id);
		NetIo.Put(writer, isCloaked);
		NetIo.ServerSend(writer, ServerMain.AllClientIdsExceptHost);
	}

	static void OnClientCloak(NetDataReader reader)
	{
		ushort id = reader.GetUShort();
		bool cloaked = reader.GetBool();
		ApplyCloak((knetid)id, cloaked);
	}

	static void ApplyCloak(knetid netId, bool isCloaked)
	{
		if (!NetBody.TryGetNetBodyFromId(netId, out var nb) || nb == null || nb.body == null || nb.IsLocal)
			return;
		var visual = nb.body.GetComponent<RemoteCloakVisual>();
		if (visual == null)
			visual = nb.body.gameObject.AddComponent<RemoteCloakVisual>();
		visual.SetTargetCloaked(isCloaked);
		VoyagerCloak.EnsureFade(nb.body);
	}

	internal static void BroadcastLocalCrystalGrapple(bool isGrappling, Vector2 grapplePoint)
	{
		if (!IsNetworkRunning() || PlayerCamera.main == null || PlayerCamera.main.body == null)
			return;
		if (_grappleSent && _grappleWas == isGrappling && (_grapplePoint - grapplePoint).sqrMagnitude < 0.16f)
			return;
		_grappleSent = true;
		_grappleWas = isGrappling;
		_grapplePoint = grapplePoint;
		knetid netId = LocalNetId();
		if (netId.id == 0)
			return;
		if (IsHost())
		{
			BroadcastCrystal(netId, isGrappling, grapplePoint);
			return;
		}
		var writer = Multiplayer.CreateNamedWriter(CrystalMsg);
		NetIo.Put(writer, isGrappling);
		NetIo.Put(writer, grapplePoint.x);
		NetIo.Put(writer, grapplePoint.y);
		NetIo.ClientSend(writer);
	}

	static void OnServerCrystal(ScavPlayer sender, NetDataReader reader)
	{
		bool on = reader.GetBool();
		var point = new Vector2(reader.GetFloat(), reader.GetFloat());
		if (!TryOwner(sender, out knetid netId, out Body body) || !IsSpecies(body, "Crystal"))
			return;
		if (on && ((Vector2)body.transform.position - point).sqrMagnitude > WebLandingReach * WebLandingReach)
			return;
		BroadcastCrystal(netId, on, point);
	}

	static void BroadcastCrystal(knetid netId, bool isGrappling, Vector2 point)
	{
		if (isGrappling)
			_grapple[netId.id] = point;
		else
			_grapple.Remove(netId.id);
		ApplyCrystal(netId, isGrappling, point);
		if (!Net.IsServer)
			return;
		var writer = Multiplayer.CreateNamedWriter(CrystalStateMsg);
		NetIo.Put(writer, netId.id);
		NetIo.Put(writer, isGrappling);
		NetIo.Put(writer, point.x);
		NetIo.Put(writer, point.y);
		NetIo.ServerSend(writer, ServerMain.AllClientIdsExceptHost);
	}

	static void OnClientCrystal(NetDataReader reader)
	{
		ushort id = reader.GetUShort();
		bool on = reader.GetBool();
		var point = new Vector2(reader.GetFloat(), reader.GetFloat());
		ApplyCrystal((knetid)id, on, point);
	}

	static void ApplyCrystal(knetid netId, bool isGrappling, Vector2 point)
	{
		if (!NetBody.TryGetNetBodyFromId(netId, out var nb) || nb == null || nb.body == null || nb.IsLocal)
			return;
		var visual = nb.body.GetComponent<RemoteCrystalTongueVisual>();
		if (visual == null)
			visual = nb.body.gameObject.AddComponent<RemoteCrystalTongueVisual>();
		visual.SetGrapple(isGrappling, point);
	}

	internal static void RequestWebSpawn(bool isPointMode, Vector2 origin, Vector2 landingPoint)
	{
		if (!IsNetworkRunning())
		{
			if (isPointMode)
				SpeciesAbilityManager.SpawnVelvetWebPoint(landingPoint);
			else
				SpeciesAbilityManager.SpawnVelvetWebLine(origin, landingPoint);
			return;
		}
		if (IsHost())
		{
			var me = ScavPlayer.LocalPlayer;
			if (me == null || !IsSpecies(me.body, "Velvet"))
				return;
			ServerSpawnWeb(isPointMode, origin, landingPoint, LocalNetId(), me.body);
			return;
		}
		var writer = Multiplayer.CreateNamedWriter(WebSpawnMsg);
		NetIo.Put(writer, isPointMode);
		NetIo.Put(writer, origin.x);
		NetIo.Put(writer, origin.y);
		NetIo.Put(writer, landingPoint.x);
		NetIo.Put(writer, landingPoint.y);
		NetIo.ClientSend(writer);
	}

	static void OnServerWebSpawn(ScavPlayer sender, NetDataReader reader)
	{
		bool point = reader.GetBool();
		var origin = new Vector2(reader.GetFloat(), reader.GetFloat());
		var landing = new Vector2(reader.GetFloat(), reader.GetFloat());
		if (!TryOwner(sender, out knetid netId, out Body body) || !IsSpecies(body, "Velvet"))
			return;
		ServerSpawnWeb(point, origin, landing, netId, body);
	}

	static void ServerSpawnWeb(bool isPointMode, Vector2 origin, Vector2 landing, knetid ownerId, Body body)
	{
		if (body != null)
		{
			Vector2 from = body.transform.position;
			if ((from - origin).sqrMagnitude > WebOriginReach * WebOriginReach)
				return;
			if ((from - landing).sqrMagnitude > WebLandingReach * WebLandingReach)
				return;
		}
		ushort id = _nextWebId++;
		if (_nextWebId == 0)
			_nextWebId = 1;
		if (!CreateWeb(id, isPointMode, origin, landing))
			return;
		EvictOldestWeb(ownerId);
		if (!_websByOwner.TryGetValue(ownerId.id, out List<ushort> owned))
		{
			owned = new List<ushort>();
			_websByOwner[ownerId.id] = owned;
		}
		owned.Add(id);
		var writer = Multiplayer.CreateNamedWriter(WebSpawnedMsg);
		NetIo.Put(writer, id);
		NetIo.Put(writer, isPointMode);
		NetIo.Put(writer, origin.x);
		NetIo.Put(writer, origin.y);
		NetIo.Put(writer, landing.x);
		NetIo.Put(writer, landing.y);
		NetIo.ServerSend(writer, ServerMain.AllClientIdsExceptHost);
		MPBridgePlugin.Log?.LogInfo("[GG-MP] web " + id + " owner=" + ownerId.id);
	}

	static bool CreateWeb(ushort webId, bool isPointMode, Vector2 origin, Vector2 landing)
	{
		GameObject go = isPointMode
			? SpeciesAbilityManager.SpawnVelvetWebPoint(landing)
			: SpeciesAbilityManager.SpawnVelvetWebLine(origin, landing);
		if (go == null)
			return false;
		var strand = go.GetComponent<WebStrandBehaviour>();
		if (strand != null)
			strand.NetworkWebId = webId;
		_webs[webId] = go;
		_webRec[webId] = new WebRec { Point = isPointMode, Origin = origin, Landing = landing };
		return true;
	}

	static void OnClientWebSpawn(NetDataReader reader)
	{
		ushort id = reader.GetUShort();
		bool point = reader.GetBool();
		var origin = new Vector2(reader.GetFloat(), reader.GetFloat());
		var landing = new Vector2(reader.GetFloat(), reader.GetFloat());
		CreateWeb(id, point, origin, landing);
	}

	internal static void ReportWebHit(ushort webId, BuildingEntity buildingEntity)
	{
		if (buildingEntity == null || !IsNetworkRunning())
			return;
		if (!NetObjectRegistry.TryGetSyncInfo(buildingEntity.gameObject, out var si) || si == null)
			return;
		if (IsHost())
		{
			ServerWebHit(webId, si.syncId);
			return;
		}
		var writer = Multiplayer.CreateNamedWriter(WebHitMsg);
		NetIo.Put(writer, webId);
		NetIo.Put(writer, si.syncId.id);
		NetIo.ClientSend(writer);
	}

	static void OnServerWebHit(ScavPlayer sender, NetDataReader reader)
	{
		ushort webId = reader.GetUShort();
		ushort enemy = reader.GetUShort();
		if (sender == null)
			return;
		ServerWebHit(webId, (knetid)enemy);
	}

	static void ServerWebHit(ushort webId, knetid enemyId)
	{
		if (_webHitAt.TryGetValue(webId, out float at) && Time.time - at < 0.2f)
			return;
		if (!NetObjectRegistry.TryGetSyncInfo(enemyId, out var si) || si == null || si.building == null)
			return;
		if (!_webs.TryGetValue(webId, out GameObject go) || go == null)
			return;
		if (!Near(si.building, go.transform, 6f))
			return;
		var strand = go.GetComponent<WebStrandBehaviour>();
		if (strand == null)
			return;
		_webHitAt[webId] = Time.time;
		bool broke = strand.ApplyNetworkedHit(si.building);
		if (!broke)
			return;
		ForgetWeb(webId);
		var writer = Multiplayer.CreateNamedWriter(WebBreakMsg);
		NetIo.Put(writer, webId);
		NetIo.ServerSend(writer, ServerMain.AllClientIdsExceptHost);
	}

	static void OnClientWebBreak(NetDataReader reader)
	{
		ushort id = reader.GetUShort();
		if (_webs.TryGetValue(id, out GameObject go) && go != null)
		{
			var strand = go.GetComponent<WebStrandBehaviour>();
			if (strand != null)
				strand.ForceBreakVisualOnly();
		}
		ForgetWeb(id);
	}

	static void EvictOldestWeb(knetid ownerId)
	{
		if (!_websByOwner.TryGetValue(ownerId.id, out List<ushort> owned))
		{
			owned = new List<ushort>();
			_websByOwner[ownerId.id] = owned;
		}
		if (owned.Count < MaxWebsPerPlayer)
			return;
		ushort old = owned[0];
		owned.RemoveAt(0);
		if (_webs.TryGetValue(old, out GameObject go) && go != null)
		{
			var strand = go.GetComponent<WebStrandBehaviour>();
			if (strand != null)
				strand.ForceBreakVisualOnly();
		}
		ForgetWeb(old);
		var writer = Multiplayer.CreateNamedWriter(WebBreakMsg);
		NetIo.Put(writer, old);
		NetIo.ServerSend(writer, ServerMain.AllClientIdsExceptHost);
	}

	static void ForgetWeb(ushort webId)
	{
		_webs.Remove(webId);
		_webRec.Remove(webId);
		_webHitAt.Remove(webId);
		foreach (List<ushort> owned in _websByOwner.Values)
			owned.Remove(webId);
	}

	internal static void BroadcastTalkerLine(Talker talker, string line)
	{
		if (!IsNetworkRunning() || talker == null || talker.body == null || string.IsNullOrEmpty(line))
			return;
		if (!talker.body.TryGetNetBody(out var nb) || nb == null)
			return;
		if (IsHost())
		{
			BroadcastTalk(nb.netId, line);
			return;
		}
		var writer = Multiplayer.CreateNamedWriter(TalkMsg);
		NetIo.Put(writer, nb.netId.id);
		NetIo.Put(writer, line);
		NetIo.ClientSend(writer);
	}

	static void OnServerTalk(ScavPlayer sender, NetDataReader reader)
	{
		ushort id = reader.GetUShort();
		string line = reader.GetString();
		if (!TryOwner(sender, out knetid netId, out _) || netId.id != id)
			return;
		BroadcastTalk(netId, line);
	}

	static void BroadcastTalk(knetid netId, string line)
	{
		if (NetBody.TryGetNetBodyFromId(netId, out var nb) && nb != null && nb.body != null && !nb.IsLocal)
		{
			var talker = nb.body.GetComponent<Talker>();
			if (talker != null)
				Talker_Talk_MultiplayerPatch.Force(talker, new List<string> { line }, null, false, false);
		}
		if (!Net.IsServer)
			return;
		var clients = new List<knetid>();
		foreach (knetid client in ServerMain.AllClientIdsExceptHost)
		{
			if (FindPlayer(netId) != null && FindPlayer(netId).playerId.id == client.id)
				continue;
			clients.Add(client);
		}
		if (clients.Count == 0)
			return;
		var writer = Multiplayer.CreateNamedWriter(TalkLineMsg);
		NetIo.Put(writer, netId.id);
		NetIo.Put(writer, line);
		NetIo.ServerSend(writer, clients);
	}

	static void OnClientTalk(NetDataReader reader)
	{
		ushort id = reader.GetUShort();
		string line = reader.GetString();
		if (!NetBody.TryGetNetBodyFromId((knetid)id, out var nb) || nb == null || nb.body == null || nb.IsLocal)
			return;
		var talker = nb.body.GetComponent<Talker>();
		if (talker != null)
			Talker_Talk_MultiplayerPatch.Force(talker, new List<string> { line }, null, false, false);
	}

	public static void ForceTalkLocal(Talker talker, string line, Limb limb, bool force, bool resetTalkTimer)
	{
		if (talker == null || string.IsNullOrEmpty(line))
			return;
		Talker_Talk_MultiplayerPatch.Force(talker, new List<string> { line }, limb, force, resetTalkTimer);
	}

	internal static bool IsTalkerInConversation(Talker talker)
	{
		return false;
	}

	internal static void LoadConversationHistory()
	{
	}

	internal static void ResetConversationHistory()
	{
	}

	internal static void NotifyConversationSeverityBreak(Talker talker)
	{
	}

	internal static bool TryGetTraderSyncId(TraderScript trader, out ushort syncId)
	{
		syncId = 0;
		if (trader == null)
			return false;
		if (!NetObjectRegistry.TryGetSyncInfo(trader.gameObject, out var si) || si == null)
		{
			if (IsHost())
			{
				try
				{
					NetObjectRegistry.NewGO(trader.gameObject);
				}
				catch
				{
				}
			}
			if (!NetObjectRegistry.TryGetSyncInfo(trader.gameObject, out si) || si == null)
				return false;
		}
		syncId = si.syncId.id;
		return syncId != 0;
	}

	internal static Body GetTraderFocusedBody(TraderScript trader)
	{
		if (trader == null)
			return null;
		var tracker = trader.GetComponent<TrackerTrader>();
		return tracker != null ? tracker.focused_body : null;
	}

	internal static void RequestVoyagerSteal(ushort traderSyncId, byte itemIndex, ushort claimedValue)
	{
		if (!IsNetworkRunning())
			return;
		if (IsHost())
		{
			ServerSteal(ScavPlayer.LocalPlayer, traderSyncId, itemIndex, claimedValue, true);
			return;
		}
		var writer = Multiplayer.CreateNamedWriter(StealMsg);
		NetIo.Put(writer, traderSyncId);
		NetIo.Put(writer, itemIndex);
		NetIo.Put(writer, claimedValue);
		NetIo.ClientSend(writer);
	}

	static void OnServerSteal(ScavPlayer sender, NetDataReader reader)
	{
		ushort traderId = reader.GetUShort();
		byte index = reader.GetByte();
		ushort value = reader.GetUShort();
		ServerSteal(sender, traderId, index, value, false);
	}

	static void ServerSteal(ScavPlayer plr, ushort traderSyncId, byte itemIndex, ushort claimedValue, bool hostSelf)
	{
		if (plr == null || plr.body == null || !plr.body.conscious || !IsSpecies(plr.body, "Voyager"))
			return;
		if (!NetObjectRegistry.TryGetSyncInfo((knetid)traderSyncId, out var si) || si == null || !si.IsTrader() || si.trader == null)
			return;
		TraderScript trader = si.trader;
		if (!Near(plr.body, trader, TalkReach) || trader.items == null || itemIndex >= trader.items.Count)
			return;
		TraderItem row = trader.items[itemIndex];
		if (row == null || (ushort)row.value != claimedValue)
			return;
		float chance = StealChance(row.value) + Mathf.RoundToInt(plr.body.skills.INTFrom10) * 0.02f;
		chance = Mathf.Clamp(chance, 0.1f, 0.95f);
		bool ok = UnityEngine.Random.value < chance;
		var tracker = trader.GetComponent<TrackerTrader>();
		if (tracker == null)
			tracker = trader.gameObject.AddComponent<TrackerTrader>();
		tracker.focused_body = plr.body;
		if (ok)
		{
			trader.items.Remove(row);
			GameObject go = Utils.Create(row.id, trader.transform.position, 0f);
			if (go != null && go.TryGetComponent<Item>(out Item item))
				plr.body.AutoPickUpItem(item);
			var clients = ServerMain.AllClientIdsExceptHost.ToList();
			if (clients.Count > 0)
				tracker.Server_SendTraderInventory(clients);
			if (hostSelf && PlayerCamera.main != null)
			{
				PlayerCamera.main.RefreshTraderInventories();
				PlayerCamera.main.UpdateTradeTexts();
			}
		}
		else
		{
			trader.hostility = 100f;
			IReadOnlyList<knetid> who = ServerMain.AllClientIdsExceptHost.ToList();
			tracker.Server_AnnounceTraderReputationState(who);
		}
		if (hostSelf)
			SaySteal(ok);
		else if (plr.playerId.id != 0)
		{
			var writer = Multiplayer.CreateNamedWriter(StealResultMsg);
			NetIo.Put(writer, ok);
			NetIo.ServerSend(writer, plr.playerId);
		}
		MPBridgePlugin.Log?.LogInfo("[GG-MP] voyager steal " + (ok ? "ok" : "fail") + " id=" + plr.GetPersistentId());
	}

	static float StealChance(int value)
	{
		if (value <= 10)
			return Mathf.Lerp(0.95f, 0.85f, value / 10f);
		if (value <= 20)
			return Mathf.Lerp(0.85f, 0.65f, (value - 10) / 10f);
		if (value <= 30)
			return Mathf.Lerp(0.65f, 0.5f, (value - 20) / 10f);
		if (value <= 40)
			return Mathf.Lerp(0.5f, 0.35f, (value - 30) / 10f);
		return Mathf.Lerp(0.35f, 0.15f, Mathf.Clamp01((value - 40) / 30f));
	}

	static void OnClientSteal(NetDataReader reader)
	{
		SaySteal(reader.GetBool());
	}

	static void SaySteal(bool success)
	{
		if (PlayerCamera.main == null || PlayerCamera.main.body == null)
			return;
		var talker = PlayerCamera.main.body.GetComponent<Talker>();
		if (talker == null)
			return;
		string key = success ? "dialogue_voyager_steal_success_" : "dialogue_voyager_steal_fail_";
		talker.GGTalk(Localization.GetRandomDialogue(key), null, false, false);
	}

	internal static void RequestVoyagerStealthKill(ushort traderSyncId, bool armed, float unarmedDamage)
	{
		if (!IsNetworkRunning())
			return;
		if (IsHost())
		{
			ServerStealth(ScavPlayer.LocalPlayer, traderSyncId, armed);
			return;
		}
		var writer = Multiplayer.CreateNamedWriter(StealthMsg);
		NetIo.Put(writer, traderSyncId);
		NetIo.Put(writer, armed);
		NetIo.Put(writer, unarmedDamage);
		NetIo.ClientSend(writer);
	}

	static void OnServerStealth(ScavPlayer sender, NetDataReader reader)
	{
		ushort traderId = reader.GetUShort();
		bool armed = reader.GetBool();
		reader.GetFloat();
		ServerStealth(sender, traderId, armed);
	}

	static void ServerStealth(ScavPlayer plr, ushort traderSyncId, bool armed)
	{
		if (plr == null || plr.body == null)
			return;
		bool velvet = IsSpecies(plr.body, "Velvet");
		if (!velvet && !IsSpecies(plr.body, "Voyager"))
			return;
		if (!NetObjectRegistry.TryGetSyncInfo((knetid)traderSyncId, out var si) || si == null || !si.IsTrader() || si.trader == null)
			return;
		TraderScript trader = si.trader;
		if (!Near(plr.body, trader, TalkReach))
			return;
		var building = trader.GetComponent<BuildingEntity>();
		if (building == null)
			return;
		var tracker = trader.GetComponent<TrackerTrader>();
		if (tracker == null)
			tracker = trader.gameObject.AddComponent<TrackerTrader>();
		tracker.focused_body = plr.body;
		if (velvet)
		{
			building.health = 0f;
			trader.DropInventory();
			FeedVelvet(plr.body);
			MPBridgePlugin.Log?.LogInfo("[GG-MP] velvet consume id=" + plr.GetPersistentId());
			return;
		}
		if (armed)
		{
			building.health = 0f;
			trader.DropInventory();
		}
		else
		{
			building.health -= 250f;
			trader.hostility = 100f;
			IReadOnlyList<knetid> who = ServerMain.AllClientIdsExceptHost.ToList();
			tracker.Server_AnnounceTraderReputationState(who);
		}
		MPBridgePlugin.Log?.LogInfo("[GG-MP] voyager stealth " + (armed ? "armed" : "unarmed") + " id=" + plr.GetPersistentId());
	}

	internal static void RequestVelvetCannibalize(ushort traderSyncId)
	{
		if (!IsNetworkRunning())
			return;
		if (IsHost())
		{
			ServerCannibal(ScavPlayer.LocalPlayer, traderSyncId, true);
			return;
		}
		var writer = Multiplayer.CreateNamedWriter(CannibalMsg);
		NetIo.Put(writer, traderSyncId);
		NetIo.ClientSend(writer);
	}

	static void OnServerCannibal(ScavPlayer sender, NetDataReader reader)
	{
		ServerCannibal(sender, reader.GetUShort(), false);
	}

	static void ServerCannibal(ScavPlayer plr, ushort traderSyncId, bool hostSelf)
	{
		if (plr == null || plr.body == null || !IsSpecies(plr.body, "Velvet"))
			return;
		if (!NetObjectRegistry.TryGetSyncInfo((knetid)traderSyncId, out var si) || si == null || !si.IsTrader() || si.trader == null)
			return;
		TraderScript trader = si.trader;
		if (trader.reputation < 200f || !Near(plr.body, trader, TalkReach))
			return;
		if (hostSelf)
		{
			plr.body.StartCoroutine(SpeciesAbilityManager.VelvetCannibalizeRoutine(trader, plr.body));
			return;
		}
		var writer = Multiplayer.CreateNamedWriter(CannibalOkMsg);
		NetIo.Put(writer, traderSyncId);
		NetIo.ServerSend(writer, plr.playerId);
	}

	static void OnClientCannibal(NetDataReader reader)
	{
		ushort id = reader.GetUShort();
		if (PlayerCamera.main == null || PlayerCamera.main.body == null)
			return;
		if (!NetObjectRegistry.TryGetSyncInfo((knetid)id, out var si) || si == null || !si.IsTrader() || si.trader == null)
			return;
		PlayerCamera.main.body.StartCoroutine(SpeciesAbilityManager.VelvetCannibalizeRoutine(si.trader, PlayerCamera.main.body));
	}

	static void FeedVelvet(Body body)
	{
		body.hunger = Mathf.Min(125f, body.hunger + 25f);
		body.thirst = Mathf.Min(125f, body.thirst + 15f);
		body.happiness = 0f;
		body.sicknessAmount = Mathf.Max(0f, body.sicknessAmount - 30f);
		if (body.limbs == null)
			return;
		foreach (Limb limb in body.limbs)
		{
			if (limb == null || limb.dismembered)
				continue;
			limb.skinHealth = Mathf.Min(100f, limb.skinHealth + 15f);
			limb.muscleHealth = Mathf.Min(100f, limb.muscleHealth + 10f);
			limb.bleedAmount = Mathf.Max(0f, limb.bleedAmount - 5f);
		}
	}

	internal static void SendDuneBite(TraderScript trader)
	{
		if (!IsNetworkRunning() || IsHost() || trader == null)
			return;
		if (PlayerCamera.main == null || !IsSpecies(PlayerCamera.main.body, "Dune"))
			return;
		if (!NetObjectRegistry.TryGetSyncInfo(trader.gameObject, out var si) || si == null)
			return;
		var writer = Multiplayer.CreateNamedWriter(BiteMsg);
		NetIo.Put(writer, si.syncId.id);
		NetIo.ClientSend(writer);
	}

	static readonly Func<TraderScript, bool> DuneBite = Calls.Bind<Func<TraderScript, bool>>(AccessTools.Method(typeof(DuneBitePatch), "Prefix"));

	static void OnServerBite(ScavPlayer sender, NetDataReader reader)
	{
		if (sender == null || sender.body == null || !IsSpecies(sender.body, "Dune"))
			return;
		ushort id = reader.GetUShort();
		if (!NetObjectRegistry.TryGetSyncInfo((knetid)id, out var si) || si == null || !si.IsTrader() || si.trader == null)
			return;
		if (!Near(sender.body, si.trader, TalkReach))
			return;
		if (DuneBite == null)
			return;
		SpeciesContext.Push(sender.body);
		try
		{
			DuneBite(si.trader);
		}
		finally
		{
			SpeciesContext.Pop();
		}
	}

	internal static void SendLimbGone(Limb limb)
	{
		if (!IsNetworkRunning() || IsHost() || limb == null || limb.body == null || limb.body.limbs == null)
			return;
		int index = Array.IndexOf(limb.body.limbs, limb);
		if (index < 0)
			return;
		var writer = Multiplayer.CreateNamedWriter(LimbMsg);
		NetIo.Put(writer, (ushort)index);
		NetIo.ClientSend(writer);
	}

	static void OnServerLimb(ScavPlayer sender, NetDataReader reader)
	{
		int index = reader.GetUShort();
		if (sender == null || sender.body == null || sender.body.limbs == null)
			return;
		if (index < 0 || index >= sender.body.limbs.Length)
			return;
		Limb limb = sender.body.limbs[index];
		if (limb == null || limb.dismembered)
			return;
		limb.Dismember();
	}

	internal static void SendPride(ScavPlayer plr, float duration)
	{
		if (plr == null || !Net.IsServer)
			return;
		var writer = Multiplayer.CreateNamedWriter(PrideMsg);
		NetIo.Put(writer, duration);
		NetIo.ServerSend(writer, plr.playerId);
	}

	static void OnClientPride(NetDataReader reader)
	{
		float duration = reader.GetFloat();
		SpeciesAbilityManager.BaronPrideTimer = Mathf.Max(SpeciesAbilityManager.BaronPrideTimer, duration);
		SpeciesAbilityManager.BaronPrideStrength = Mathf.Max(SpeciesAbilityManager.BaronPrideStrength, duration);
	}

	internal static void SendRozaHold(bool holding)
	{
		if (!IsNetworkRunning() || IsHost())
			return;
		var writer = Multiplayer.CreateNamedWriter(RozaMsg);
		NetIo.Put(writer, holding);
		NetIo.ClientSend(writer);
	}

	internal static void SendLeapyHold(bool holding)
	{
		if (!IsNetworkRunning() || IsHost())
			return;
		var writer = Multiplayer.CreateNamedWriter(LeapyMsg);
		NetIo.Put(writer, holding);
		NetIo.ClientSend(writer);
	}

	internal static void SendLevitate(bool lifting, Vector2 mouse, Vector2 at)
	{
		if (!IsNetworkRunning() || IsHost())
			return;
		var writer = Multiplayer.CreateNamedWriter(LevitateMsg);
		NetIo.Put(writer, lifting);
		NetIo.Put(writer, mouse.x);
		NetIo.Put(writer, mouse.y);
		NetIo.Put(writer, at.x);
		NetIo.Put(writer, at.y);
		NetIo.ClientSend(writer);
	}

	static ushort OwnerId(ScavPlayer plr)
	{
		if (plr == null || plr.netBody == null)
			return 0;
		return plr.netBody.netId.id;
	}

	static void OnServerRoza(ScavPlayer sender, NetDataReader reader)
	{
		bool holding = reader.GetBool();
		ushort id = OwnerId(sender);
		if (id == 0)
			return;
		_rozaHold[id] = holding;
	}

	static void OnServerLeapy(ScavPlayer sender, NetDataReader reader)
	{
		bool holding = reader.GetBool();
		ushort id = OwnerId(sender);
		if (id == 0)
			return;
		_leapyHold[id] = holding;
	}

	internal static bool RozaHolding(ScavPlayer plr)
	{
		ushort id = OwnerId(plr);
		return id != 0 && _rozaHold.TryGetValue(id, out bool on) && on;
	}

	internal static bool LeapyHolding(ScavPlayer plr)
	{
		ushort id = OwnerId(plr);
		return id != 0 && _leapyHold.TryGetValue(id, out bool on) && on;
	}

	static void OnServerLevitate(ScavPlayer sender, NetDataReader reader)
	{
		bool lifting = reader.GetBool();
		var mouse = new Vector2(reader.GetFloat(), reader.GetFloat());
		var at = new Vector2(reader.GetFloat(), reader.GetFloat());
		if (!lifting || sender == null || sender.body == null || !IsSpecies(sender.body, "Mercenary"))
			return;
		if (((Vector2)sender.body.transform.position - at).sqrMagnitude > 20f * 20f)
			return;
		Rigidbody2D best = null;
		float bestD = 1f;
		foreach (Collider2D col in Physics2D.OverlapCircleAll(at, 0.75f))
		{
			if (col == null)
				continue;
			Rigidbody2D rb = col.attachedRigidbody;
			if (rb == null || rb.bodyType != RigidbodyType2D.Dynamic)
				continue;
			if (rb.GetComponentInParent<Body>() != null)
				continue;
			float d = ((Vector2)rb.position - at).sqrMagnitude;
			if (d >= bestD)
				continue;
			bestD = d;
			best = rb;
		}
		if (best == null)
			return;
		Vector2 pull = mouse - best.position;
		if (pull.sqrMagnitude < 0.01f)
			return;
		best.AddForce(pull.normalized * 35f, ForceMode2D.Force);
	}

	internal static void SendAlbinoOn(int level)
	{
		if (!IsNetworkRunning() || IsHost())
			return;
		var writer = Multiplayer.CreateNamedWriter(AlbinoMsg);
		NetIo.Put(writer, (byte)Mathf.Clamp(level, 1, 3));
		NetIo.ClientSend(writer);
	}

	static void OnServerAlbino(ScavPlayer sender, NetDataReader reader)
	{
		int level = reader.GetByte();
		if (sender == null || sender.body == null || !IsSpecies(sender.body, "Albino"))
			return;
		if (sender.netBody == null || sender.netBody.syncBody == null)
			return;
		if (level < 1)
			level = 1;
		if (level > 3)
			level = 3;
		ushort id = sender.netBody.netId.id;
		_albino.TryGetValue(id, out AlbinoFx fx);
		fx.Level = level;
		fx.On = true;
		fx.Active = 15f;
		if (fx.Intensity < 0.15f)
			fx.Intensity = 0.15f;
		if (fx.BaseSpeed <= 0f)
			fx.BaseSpeed = sender.body.maxSpeed;
		_albino[id] = fx;
		MPBridgePlugin.Log?.LogInfo("[GG-MP] albino desync netId=" + id + " level=" + level);
	}

	// music stays on the albino's machine. heart, pain, bleed, and the flatline run here.
	internal static void TickAlbino(ScavPlayer plr)
	{
		if (plr == null || plr.body == null || plr.netBody == null || plr.netBody.syncBody == null)
			return;
		ushort id = plr.netBody.netId.id;
		if (!_albino.TryGetValue(id, out AlbinoFx fx))
			return;
		Body body = plr.body;
		float dt = Time.deltaTime;
		if (fx.On)
		{
			fx.Active -= dt;
			body.averagePain = 0f;
			if (body.limbs != null)
			{
				float bleed = fx.Level >= 2 ? 0.5f : 0.25f;
				foreach (Limb limb in body.limbs)
				{
					if (limb == null)
						continue;
					limb.pain = 0f;
					if (fx.Level >= 1 && limb.bleedAmount > 0f)
						limb.bleedAmount = Mathf.Max(0f, limb.bleedAmount - dt * bleed);
				}
			}
			if (fx.Level >= 3)
			{
				float t = 1f - Mathf.Clamp01(fx.Active / 15f);
				body.fibrillationProgress = Mathf.Max(body.fibrillationProgress, t * t * 100f);
			}
			if (fx.Active <= 0f)
			{
				fx.On = false;
				fx.Active = 0f;
				if (fx.Level >= 3)
					fx.Crash = 4f;
			}
		}
		float goal = fx.On ? 1f : 0f;
		float rate = goal > fx.Intensity ? 0.5f : (1f / 6f);
		fx.Intensity = Mathf.MoveTowards(fx.Intensity, goal, dt * rate);
		if (fx.Intensity > 0f && fx.Level >= 1 && fx.Crash <= 0f)
			ApplyAlbinoPeaks(body, fx);
		if (fx.Crash > 0f)
		{
			fx.Crash -= dt;
			body.heartRate = 0f;
			body.consciousness = 0f;
		}
		if (!fx.On && fx.Intensity <= 0f && fx.Crash <= 0f)
			_albino.Remove(id);
		else
			_albino[id] = fx;
	}

	static void ApplyAlbinoPeaks(Body body, AlbinoFx fx)
	{
		AlbinoPeak[] peaks = AlbinoPeaks();
		if (peaks == null || fx.Level < 0 || fx.Level >= peaks.Length)
			return;
		AlbinoPeak peak = peaks[fx.Level];
		float k = fx.Intensity;
		body.bloodPressure = Mathf.Max(body.bloodPressure, Mathf.Lerp(0f, peak.Bp, k));
		if (peak.Hr > 0f && !body.inCardiacArrest)
			body.heartRate = Mathf.Max(body.heartRate, Mathf.Lerp(0f, peak.Hr, k));
		body.stamina = Mathf.Max(body.stamina, Mathf.Lerp(0f, peak.Stamina, k));
		if (!body.inCardiacArrest)
			body.consciousness = Mathf.Max(body.consciousness, Mathf.Lerp(0f, peak.Consciousness, k));
		if (fx.BaseSpeed > 0f)
			body.maxSpeed = Mathf.Lerp(fx.BaseSpeed, fx.BaseSpeed * peak.Speed, k);
	}

	static AlbinoPeak[] AlbinoPeaks()
	{
		if (_albinoPeaks != null)
			return _albinoPeaks;
		var field = AccessTools.Field(typeof(AlbinoDesync), "Peaks");
		var raw = field != null ? field.GetValue(null) as Array : null;
		if (raw == null)
			return null;
		var next = new AlbinoPeak[raw.Length];
		for (int i = 0; i < raw.Length; i++)
		{
			object box = raw.GetValue(i);
			if (box == null)
				continue;
			Type t = box.GetType();
			next[i].Bp = PeakNum(t, box, "bp");
			next[i].Hr = PeakNum(t, box, "hr");
			next[i].Stamina = PeakNum(t, box, "staminaFloor");
			next[i].Consciousness = PeakNum(t, box, "consciousnessFloor");
			next[i].Speed = PeakNum(t, box, "speedMult");
		}
		_albinoPeaks = next;
		return _albinoPeaks;
	}

	static float PeakNum(Type t, object box, string name)
	{
		var field = AccessTools.Field(t, name);
		if (field == null)
			return 0f;
		object value = field.GetValue(box);
		return value is float n ? n : 0f;
	}
}
