using System;
using System.Collections.Generic;
using CasualtiesTogetherUtils;
using LiteNetLib;
using LiteNetLib.Utils;
using Together;
using UnityEngine;

namespace GunsawGenetics;

// the type gg looks up on GunsawGeneticsMPBridge. the hooks send that lookup here.
public static partial class SpeciesMultiplayerSync
{
	const string PickRequestMsg = "GG.Species.PickRequest";
	const string PickBroadcastMsg = "GG.Species.PickBroadcast";
	const string CatchupMsg = "GG.Species.Catchup";
	const string GateMsg = "GG.Species.Gate";
	const string GateAskMsg = "GG.Species.GateAsk";

	internal static readonly Dictionary<ushort, string> knownSpeciesNames = new Dictionary<ushort, string>();
    internal static readonly Dictionary<ushort, int> knownSlots = new Dictionary<ushort, int>();
    internal static readonly HashSet<string> _offeredPick = new HashSet<string>();
    internal static readonly HashSet<int> _tutorialOffered = new HashSet<int>();
	static int _runWorldId;
	static int _tutorialWorldId;

	static bool _initialized;
	static bool _joinedHooked;

	public static bool IsNetworkRunning()
	{
		return Net.IsRunning;
	}

	public static bool IsHost()
	{
		return Net.IsRunning && Net.IsServer;
	}

	internal static void Initialize()
	{
		if (_initialized)
			return;
		_initialized = true;

		Multiplayer.RegisterCustomServerReceiver(PickRequestMsg, OnServerReceivePickRequest);
		Multiplayer.RegisterCustomClientReceiver(PickBroadcastMsg, OnClientReceiveSpeciesPicked);
		Multiplayer.RegisterCustomClientReceiver(CatchupMsg, OnClientReceiveCatchup);
		Multiplayer.RegisterCustomClientReceiver(GateMsg, OnClientReceiveGate);
		Multiplayer.RegisterCustomServerReceiver(GateAskMsg, OnServerReceiveGateAsk);
		RegisterHostPasses();

		if (!_joinedHooked)
		{
			ScavPlayer.OnPlayerJoined += OnPlayerJoinedCatchup;
			_joinedHooked = true;
		}

		MPBridgePlugin.Log?.LogInfo("[GG-MP] Initialize() completed successfully. Pick/catchup handlers registered.");
	}

	// gg calls this after a local pick.
	internal static void BroadcastLocalPick(string speciesName, int spriteSlot)
	{
		if (string.IsNullOrEmpty(speciesName))
			return;
		if (!IsNetworkRunning())
		{
			MPBridgePlugin.Log?.LogInfo($"[GG-MP] BroadcastLocalPick species={speciesName} - Net not running, skipped");
			return;
		}

		knetid netId = LocalNetId();
		if (netId.id == 0)
		{
			MPBridgePlugin.Log?.LogWarning($"[GG-MP] BroadcastLocalPick species={speciesName} - local netId=0, skipped");
			return;
		}

		if (IsHost())
		{
			ScavPlayer me = ScavPlayer.LocalPlayer;
			if (!Util.IsTutorialWorld()
				&& me != null && SpeciesSave.TryRead(me, out string locked, out int lockedSlot)
				&& !string.Equals(locked, speciesName, StringComparison.Ordinal))
			{
				MPBridgePlugin.Log?.LogInfo(
					$"[GG-MP] pick refused, locked {locked} netId={netId.id}");
				ApplyPickAuthoritative(netId, locked, lockedSlot);
				SpeciesGate.ForceLocal(locked, lockedSlot);
				return;
			}
			MPBridgePlugin.Log?.LogInfo(
				$"[GG-MP] BroadcastLocalPick host apply netId={netId.id} species={speciesName} slot={spriteSlot}");
			ApplyPickAuthoritative(netId, speciesName, spriteSlot);
			return;
		}

		if (!Multiplayer.IsNetworkActiveAndIsClient())
		{
			MPBridgePlugin.Log?.LogWarning(
				$"[GG-MP] BroadcastLocalPick species={speciesName} - client path inactive, skipped");
			return;
		}

		MPBridgePlugin.Log?.LogInfo(
			$"[GG-MP] BroadcastLocalPick - PickRequest netId={netId.id} species={speciesName} slot={spriteSlot}");
		RecordKnownSpecies(netId, speciesName, spriteSlot);
		var writer = Multiplayer.CreateNamedWriter(PickRequestMsg);
		writer.Put(speciesName);
		writer.Put(spriteSlot);
		NetIo.ClientSend(writer);
	}

	static void OnServerReceivePickRequest(ScavPlayer sender, NetDataReader reader)
	{
		if (sender == null || sender.netBody == null)
			return;

		string species = reader.GetString();
		int slot = reader.GetInt();
		knetid netId = sender.netBody.netId;

		if (!Util.IsTutorialWorld()
			&& SpeciesSave.TryRead(sender, out string locked, out int lockedSlot)
			&& !string.Equals(locked, species, StringComparison.Ordinal))
		{
			MPBridgePlugin.Log?.LogInfo(
				$"[GG-MP] pick refused, locked {locked} netId={netId.id}");
			ApplyPickAuthoritative(netId, locked, lockedSlot);
			SendGate(sender, true, locked, lockedSlot);
			return;
		}

		MPBridgePlugin.Log?.LogInfo(
			$"[GG-MP] PickRequest netId={netId.id} species={species} slot={slot} from client {sender.playerId.id}");

		ApplyPickAuthoritative(netId, species, slot);
	}

	// the sprite replacer swaps a body once. limb awake and a rejoin both land after that,
	// and a remote body's vanilla sprites come back. keep painting the known slot.
	internal static void RefreshSprites()
	{
		foreach (var kv in knownSlots)
		{
			if (kv.Value <= 0)
				continue;
			var netId = (knetid)kv.Key;
			if (!TryResolveExact(netId, out Body body))
				continue;
			try
			{
				GgSpecies.SetSpriteSlot(body, kv.Value, kv.Key);
			}
			catch (System.Exception e)
			{
				MPBridgePlugin.Log?.LogWarning("[GG-MP] sprite refresh failed netId=" + netId.id + " " + e.Message);
			}
		}
		foreach (var plr in ScavPlayer.BodyToPlayerDict.Values)
		{
			if (plr == null || plr.body == null)
				continue;
			string species = GetSpeciesForBody(plr.body);
			if (string.Equals(species, "Orange", StringComparison.Ordinal))
				OrangeArms.EnsureIfOrange(plr.body, species);
			else
				OrangeArms.Strip(plr.body);
		}
	}

	internal static void PrepareFreshStart()
	{
		_runWorldId = 0;
		_tutorialWorldId = 0;
		SpeciesSave.BeginFreshRun();
		SpeciesGate.Reset();
		_offeredPick.Clear();
		_tutorialOffered.Clear();
		knownSpeciesNames.Clear();
		knownSlots.Clear();
	}

	internal static void PrepareContinue()
	{
		_runWorldId = 0;
		SpeciesSave.ContinueRun();
	}

	// tutorial always asks again. the saved species stays on the real file.
	internal static void NoteTutorialEntry()
	{
		if (!Util.IsTutorialWorld())
			return;
		int id = WorldGeneration.world.GetInstanceID();
		if (_tutorialWorldId == id)
			return;
		_tutorialWorldId = id;
		SpeciesGate.Reset();
		_tutorialOffered.Clear();
		if (!IsHost())
			return;
		SpeciesSave.BeginFreshRun();
		_offeredPick.Clear();
		knownSpeciesNames.Clear();
		knownSlots.Clear();
		MPBridgePlugin.Log?.LogInfo("[GG-MP] tutorial, species pick is scratch");
	}

	// start does not load the player file. drop the previous lock before the first decision.
	internal static void NoteNewWorld()
	{
		WorldGeneration world = WorldGeneration.world;
		if (world == null)
			return;
		int id = world.GetInstanceID();
		if (_runWorldId == id)
			return;
		_runWorldId = id;
		if (Util.IsTutorialWorld())
			return;
		if (SaveSystem.loadedRun)
		{
			SpeciesSave.ContinueRun();
			return;
		}
		SpeciesSave.BeginFreshRun();
		SpeciesGate.Reset();
		_offeredPick.Clear();
		_tutorialOffered.Clear();
		knownSpeciesNames.Clear();
		knownSlots.Clear();
		MPBridgePlugin.Log?.LogInfo("[GG-MP] run is not a loaded save, old species file left unread");
	}

	internal static void TickDecisions()
	{
		if (!IsHost())
			return;
		NoteNewWorld();
		WorldGeneration world = WorldGeneration.world;
		if (world == null || !world.worldExists || world.generatingWorld)
			return;
		foreach (ScavPlayer plr in ScavPlayer.BodyToPlayerDict.Values)
			SendDecision(plr, false);
		foreach (var kv in knownSpeciesNames)
		{
			ScavPlayer owner = FindPlayer((knetid)kv.Key);
			SpeciesKit.Grant(owner, kv.Value);
		}
	}

	internal static void AskGate()
	{
		if (!IsNetworkRunning() || IsHost())
			return;
		var writer = Multiplayer.CreateNamedWriter(GateAskMsg);
		NetIo.ClientSend(writer);
	}

	static void OnServerReceiveGateAsk(ScavPlayer sender, NetDataReader reader)
	{
		SendDecision(sender, true);
	}

	static void SendDecision(ScavPlayer plr, bool resend)
	{
		if (!IsHost() || plr == null || !TryNetId(plr, out knetid netId))
			return;
		string id = plr.GetPersistentId();
		// tutorial is a scratch world. host and joiners both pick, and the real save stays as it was.
		if (Util.IsTutorialWorld())
		{
			if (!(resend || _tutorialOffered.Add(netId.id)))
				return;
			if (plr.IsLocal)
				SpeciesGate.AllowPick();
			else
				SendGate(plr, false, "", 0);
			return;
		}
		bool locked = SpeciesSave.TryRead(plr, out string species, out int slot);
		if (locked)
		{
			// a rejoin is a new net id. the persistent id was already locked, so key off the id.
			bool mapped = knownSpeciesNames.TryGetValue(netId.id, out string have)
				&& string.Equals(have, species, StringComparison.Ordinal);
			if (!mapped)
				ApplyPickAuthoritative(netId, species, slot);
			if (plr.IsLocal)
				SpeciesGate.ForceLocal(species, slot);
			if (!plr.IsLocal && (!mapped || resend))
				SendGate(plr, true, species, slot);
			return;
		}
		if (plr.IsLocal)
		{
			if (_offeredPick.Add(id))
				SpeciesGate.AllowPick();
			return;
		}
		if (resend || _offeredPick.Add(id))
			SendGate(plr, false, "", 0);
	}

	static void SendGate(ScavPlayer plr, bool locked, string species, int slot)
	{
		var writer = Multiplayer.CreateNamedWriter(GateMsg);
		writer.Put(locked ? 1 : 0);
		writer.Put(species ?? "");
		writer.Put(slot);
		NetIo.ServerSend(writer, plr.playerId);
		MPBridgePlugin.Log?.LogInfo(
			$"[GG-MP] gate {(locked ? "locked " + species : "pick")} -> {plr.playerName}");
	}

	static void OnClientReceiveGate(NetDataReader reader)
	{
		int locked = reader.GetInt();
		string species = reader.GetString();
		int slot = reader.GetInt();
		if (locked != 0)
			SpeciesGate.ForceLocal(species, slot);
		else
			SpeciesGate.AllowPick();
	}

	static ScavPlayer FindPlayer(knetid netId)
	{
		foreach (ScavPlayer plr in ScavPlayer.BodyToPlayerDict.Values)
		{
			if (plr != null && TryNetId(plr, out knetid id) && id.id == netId.id)
				return plr;
		}
		return null;
	}

	static bool TryNetId(ScavPlayer plr, out knetid netId)
	{
		netId = default;
		try
		{
			if (plr == null || plr.netBody == null)
				return false;
			netId = plr.netBody.netId;
			return netId.id != 0;
		}
		catch
		{
			return false;
		}
	}

	static void ApplyPickAuthoritative(knetid netId, string species, int slot)
	{
		if (!TryValidateSpecies(species, slot, out var resolved))
			return;

		ScavPlayer owner = FindPlayer(netId);
		bool scratch = Util.IsTutorialWorld();
		// same species already in the mp save. do not rewrite blood or skills.
		bool resume = !scratch && owner != null
			&& SpeciesSave.TryRead(owner, out string had, out _)
			&& string.Equals(had, resolved.Key, StringComparison.Ordinal);
		// a failed first apply leaves the experiment 9/8/7. that is not a real build.
		bool repair = resume && owner.body != null && resolved.Key != "Experiment" && StillExperimentStats(owner.body);
		RecordKnownSpecies(netId, resolved.Key, resolved.SpriteSlot);
		ApplySlotToNetId(netId, resolved.SpriteSlot);
		if (!resume || repair)
			ApplyStatsToNetId(netId, resolved.Key);
		// items land before the checkpoint so the stamp and the gear share one file.
		SpeciesKit.Grant(owner, resolved.Key);
		// gg rolls the local body. a remote albino is rolled here, tutorial included.
		// the client copy is wiped by the next host snapshot.
		if (string.Equals(resolved.Key, "Albino", StringComparison.Ordinal) && !resume
			&& owner != null && owner != ScavPlayer.LocalPlayer)
			SpeciesKit.RollAlbino(owner);
		if (owner != null && !resume && !scratch)
			SpeciesSave.Persist(owner, resolved.Key, resolved.SpriteSlot);
		else if (repair)
			SpeciesSave.SaveBody(owner);
		try
		{
			BroadcastPickFromServer(netId, resolved.Key, resolved.SpriteSlot, resume ? 0 : 1);
		}
		catch (System.Exception e)
		{
			MPBridgePlugin.Log?.LogError($"[GG-MP] PickBroadcast failed netId={netId.id} species={resolved.Key}: {e.Message}");
		}
	}

	static void BroadcastPickFromServer(knetid netId, string species, int slot, int writeStats)
	{
		if (!Net.IsServer)
			return;
		var writer = Multiplayer.CreateNamedWriter(PickBroadcastMsg);
		writer.Put(netId.id);
		writer.Put(species);
		writer.Put(slot);
		writer.Put(writeStats);
		NetIo.ServerSend(writer, ServerMain.AllClientIdsExceptHost);
	}

	static void OnClientReceiveSpeciesPicked(NetDataReader reader)
	{
		ushort id = reader.GetUShort();
		string species = reader.GetString();
		int slot = reader.GetInt();
		int writeStats = reader.GetInt();
		var netId = (knetid)id;

		RecordKnownSpecies(netId, species, slot);
		ApplySlotToNetId(netId, slot);
		if (writeStats != 0)
			ApplyStatsToNetId(netId, species);
		if (string.Equals(species, "Orange", StringComparison.Ordinal) && TryResolveExact(netId, out Body orange))
			OrangeArms.EnsureIfOrange(orange, species);
	}

	static void OnPlayerJoinedCatchup(ScavPlayer plr)
	{
		if (!Net.IsServer || plr == null)
			return;
		// the host already has the map. catchup is for the joiner.
		if (plr == ScavPlayer.LocalPlayer)
			return;
		SendAbilityCatchup(plr);
		if (knownSpeciesNames.Count == 0)
			return;

		var writer = Multiplayer.CreateNamedWriter(CatchupMsg);
		writer.Put(knownSpeciesNames.Count);
		foreach (var kv in knownSpeciesNames)
		{
			writer.Put(kv.Key);
			writer.Put(kv.Value);
			writer.Put(knownSlots.TryGetValue(kv.Key, out int slot) ? slot : 0);
		}
		NetIo.ServerSend(writer, plr.playerId);
		MPBridgePlugin.Log?.LogInfo(
			$"[GG-MP] Catchup species map count={knownSpeciesNames.Count} - client {plr.playerId.id}");
	}

	static void OnClientReceiveCatchup(NetDataReader reader)
	{
		int count = reader.GetInt();
		int applied = 0;
		for (int i = 0; i < count; i++)
		{
			ushort id = reader.GetUShort();
			string species = reader.GetString();
			int slot = reader.GetInt();
			var netId = (knetid)id;
			RecordKnownSpecies(netId, species, slot);
			// catchup is a late join. the saved body already has its skills.
			if (ApplySlotToNetId(netId, slot))
				applied++;
		}
		MPBridgePlugin.Log?.LogInfo($"[GG-MP] Catchup species map count={count} applied={applied}");
	}

	internal static void RecordKnownSpecies(knetid netId, string species, int slot)
	{
		if (netId.id == 0 || string.IsNullOrEmpty(species))
			return;
		bool changed = !knownSpeciesNames.TryGetValue(netId.id, out string had)
			|| !string.Equals(had, species, StringComparison.Ordinal)
			|| !knownSlots.TryGetValue(netId.id, out int hadSlot)
			|| hadSlot != slot;
		knownSpeciesNames[netId.id] = species;
		knownSlots[netId.id] = slot;
		if (changed)
			GGWoundViewSync.InvalidateSpeciesCache();
	}

	internal static int GetSlotForNetId(knetid netId)
	{
		return knownSlots.TryGetValue(netId.id, out int slot) ? slot : 0;
	}

	public static string GetSpeciesForNetId(knetid netId)
	{
		return knownSpeciesNames.TryGetValue(netId.id, out string name) ? name : null;
	}

	// host map for this body. ActiveSpecies is one global and paints the wrong person.
	public static string GetSpeciesForBody(Body body)
	{
		if (body == null)
			return null;

		if (body.TryGetNetBody(out var nb) && nb != null && nb.syncBody != null)
		{
			string mapped = GetSpeciesForNetId(nb.netId);
			if (!string.IsNullOrEmpty(mapped))
				return mapped;
		}

		// NetBody can lag a frame behind BodyToPlayerDict after spawn / catchup.
		// netId throws until SyncBody exists, and Gunsaw logs every failed call.
		if (ScavPlayer.BodyToPlayerDict != null)
		{
			foreach (var plr in ScavPlayer.BodyToPlayerDict.Values)
			{
				if (plr == null || plr.body != body || plr.netBody == null || plr.netBody.syncBody == null)
					continue;
				string mapped = GetSpeciesForNetId(plr.netBody.netId);
				if (!string.IsNullOrEmpty(mapped))
					return mapped;
			}
		}

		if (PlayerCamera.main != null && body == PlayerCamera.main.body
			&& !string.IsNullOrEmpty(SpeciesAbilityManager.ActiveSpecies))
			return SpeciesAbilityManager.ActiveSpecies;

		return null;
	}

	public static string ResolveSpeciesForBody(Body body)
	{
		string mapped = GetSpeciesForBody(body);
		if (!string.IsNullOrEmpty(mapped))
			return mapped;
		if (body != null && PlayerCamera.main != null && body == PlayerCamera.main.body)
			return SpeciesAbilityManager.ActiveSpecies;
		return null;
	}

	internal static bool ApplySlotToNetId(knetid netId, int slot)
	{
		if (!TryResolveExact(netId, out var body))
			return false;
		try
		{
			GgSpecies.SetSpriteSlot(body, slot, netId);
		}
		catch (System.Exception e)
		{
			MPBridgePlugin.Log?.LogWarning("[GG-MP] sprite slot failed netId=" + netId.id + " " + e.Message);
		}
		string species = GetSpeciesForNetId(netId) ?? "?";
		MPBridgePlugin.Log?.LogInfo($"[GG-MP] ApplySlot netId={netId.id} species={species} slot={slot}");
		return true;
	}

	static bool StillExperimentStats(Body body)
	{
		// experiment baseline from SpeciesData. a species pick that never landed stays here.
		return body.skills != null
			&& body.skills.STR == 9
			&& body.skills.RES == 8
			&& body.skills.INT == 7;
	}

	static void SetBloodMax(knetid netId, string speciesKey)
	{
		if (!TryResolveBody(netId, out var body))
			return;
		float max = BaronBloodScaling.GetMax(speciesKey);
		if (max <= 0f)
			return;
		body.bloodVolume = max;
		MPBridgePlugin.Log?.LogInfo($"[GG-MP] blood set netId={netId.id} species={speciesKey} vol={max}");
	}

	internal static void ApplyStatsToNetId(knetid netId, string speciesKey)
	{
		if (!TryResolveBody(netId, out var body) || body.skills == null)
			return;
		int slot = GetSlotForNetId(netId);
		if (!TryValidateSpecies(speciesKey, slot, out var resolved) || resolved.Data == null)
			return;

		// ActiveSpecies is global. only the owner should take the local config.
		if (Util.IsBodyLocal(body))
		{
			GgSpecies.ApplyLocalSpeciesConfig(resolved.Data);
			SpeciesAbilityManager.ActiveSpecies = resolved.Key;
		}

		if (Net.IsServer || Util.IsBodyLocal(body) || !Net.IsRunning)
		{
			body.skills.STR = resolved.STR;
			body.skills.RES = resolved.RES;
			body.skills.INT = resolved.INT;
			body.skills.UpdateExpBoundaries();
			body.skills.expSTR = body.skills.minSTR;
			body.skills.expRES = body.skills.minRES;
			body.skills.expINT = body.skills.minINT;
			if (GunsawGeneticsPlugin.BaselineMaxSpeed < 0f)
			{
				GunsawGeneticsPlugin.BaselineMaxSpeed = body.maxSpeed;
				GunsawGeneticsPlugin.BaselineJumpSpeed = body.jumpSpeed;
			}
			float baseSpeed = GgSpecies.BaselineMaxSpeed();
			float baseJump = GgSpecies.BaselineJumpSpeed();
			if (baseSpeed > 0f && resolved.MoveSpeedMult > 0f)
				body.maxSpeed = baseSpeed * resolved.MoveSpeedMult;
			else if (GunsawGeneticsPlugin.BaselineMaxSpeed > 0f && resolved.MoveSpeedMult > 0f)
				body.maxSpeed = GunsawGeneticsPlugin.BaselineMaxSpeed * resolved.MoveSpeedMult;
			if (baseJump > 0f && resolved.JumpSpeedMult > 0f)
				body.jumpSpeed = baseJump * resolved.JumpSpeedMult;
			else if (GunsawGeneticsPlugin.BaselineJumpSpeed > 0f && resolved.JumpSpeedMult > 0f)
				body.jumpSpeed = GunsawGeneticsPlugin.BaselineJumpSpeed * resolved.JumpSpeedMult;
			float moveForce = GgSpecies.BaselineMoveForce(body);
			if (moveForce > 0f && resolved.MoveSpeedMult > 0f)
				body.moveForce = moveForce * resolved.MoveSpeedMult;
			if (resolved.BloodVolumeMax > 0f)
				body.bloodVolume = resolved.BloodVolumeMax;
			if (Mathf.Abs(resolved.HungerThirstOffset) > 0.01f)
			{
				body.hunger = 100f + resolved.HungerThirstOffset;
				body.thirst = 100f + resolved.HungerThirstOffset;
			}
			OrangeArms.EnsureIfOrange(body, resolved.Key);
			MPBridgePlugin.Log?.LogInfo(
				"[GG-MP] ApplyStats netId=" + netId.id + " " + resolved.Key
				+ " str=" + body.skills.STR + " res=" + body.skills.RES + " int=" + body.skills.INT);
		}
		else
		{
			MPBridgePlugin.Log?.LogInfo(
				"[GG-MP] ApplyStats netId=" + netId.id + " " + resolved.Key
				+ " (slot/map only; stats on host/owner)");
		}

		if (!Util.IsBodyLocal(body))
			GrantStartingItemsIfRemote(netId, resolved.Key);
	}

	static bool TryValidateSpecies(string speciesKey, int slot, out GgSpecies.Resolved resolved)
	{
		if (GgSpecies.TryResolve(speciesKey, slot, out resolved))
			return true;

		MPBridgePlugin.Log?.LogWarning(
			$"[GG-MP] Unknown custom species key={speciesKey} - skipped (pack parity)");
		resolved = default;
		return false;
	}

	static bool TryResolveExact(knetid netId, out Body body)
	{
		body = null;
		if (netId.id == 0)
			return false;
		if (NetBody.TryGetNetBodyFromId(netId, out var nb) && nb != null && nb.body != null)
		{
			body = nb.body;
			return true;
		}
		return false;
	}

	static bool TryResolveBody(knetid netId, out Body body)
	{
		body = null;
		if (netId.id == 0)
			return false;

		if (NetBody.TryGetNetBodyFromId(netId, out var nb) && nb != null && nb.body != null)
		{
			body = nb.body;
			return true;
		}

		// players resolve through NetBody above. this dict is the leftover krok path for other objects.
		if (NetObjectRegistry.NetIdToSyncInfoDict != null &&
		    NetObjectRegistry.NetIdToSyncInfoDict.TryGetValue(netId, out var si) &&
		    si != null && si.go != null)
		{
			body = si.go.GetComponent<Body>();
			if (body == null)
				body = si.go.GetComponentInParent<Body>();
			return body != null;
		}

		return false;
	}

	static knetid LocalNetId()
	{
		var me = ScavPlayer.LocalPlayer;
		if (me?.netBody == null)
			return 0;
		try
		{
			return me.netBody.netId;
		}
		catch
		{
			return 0;
		}
	}

	static void GrantStartingItemsIfRemote(knetid netId, string species)
	{
		if (!IsHost() || !TryResolveBody(netId, out Body body))
			return;
		ScavPlayer owner = FindPlayer(netId);
		if (owner == null || owner.body != body)
			return;
		SpeciesKit.Grant(owner, species);
	}
}
