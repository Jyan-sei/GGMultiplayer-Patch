using System;
using System.Collections.Generic;
using System.IO;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib.Utils;
using Newtonsoft.Json.Linq;
using Together;
using UnityEngine;
using UnityEngine.UI;

namespace GunsawGenetics;

// orange grows two more hands. slots 6 and 7, limbs 15 through 20.
// the solo loader only does that when the bep config says orange, which mp must not write.
internal static class OrangeArms
{
	internal static void EnsureIfOrange(Body body, string species)
	{
		if (!string.Equals(species, "Orange", StringComparison.Ordinal))
			return;
		Ensure(body);
		if (body != null && Util.IsBodyLocal(body))
			SpeciesAbilityManager.ActiveSpecies = "Orange";
	}

	internal static void Ensure(Body body)
	{
		if (body == null)
			return;
		bool grown = body.slots != null && body.slots.Length > 7
			&& body.limbs != null && body.limbs.Length > 20;
		if (!string.IsNullOrEmpty(SpeciesMultiplayerSync.GetSpeciesForBody(body))
			&& !string.Equals(SpeciesMultiplayerSync.GetSpeciesForBody(body), "Orange", StringComparison.Ordinal))
			return;
		if (IsPrefab(body))
			return;
		if (!grown)
		{
			try
			{
				OrangeFourArms.Setup(body);
				KeepOnBody(body);
				int slots = body.slots != null ? body.slots.Length : 0;
				int limbs = body.limbs != null ? body.limbs.Length : 0;
				MPBridgePlugin.Log?.LogInfo("[GG-MP] orange arms body=" + body.name + " slots=" + slots + " limbs=" + limbs);
				StripPrefab(body);
			}
			catch (Exception e)
			{
				MPBridgePlugin.Log?.LogWarning("[GG-MP] orange arms setup failed: " + e.Message);
			}
		}
		if (body.limbs != null && body.limbs.Length > 15)
		{
			KeepOnBody(body);
			GrowInfection(body, body.limbs.Length);
		}
		// the two extra wedges are the extra hands. a 15 limb body does not get them.
		ScavPlayer me = ScavPlayer.LocalPlayer;
		if (me == null || me.body != body || PlayerCamera.main == null)
			return;
		if (body.limbs == null || body.limbs.Length <= 15)
			return;
		try
		{
			OrangeFourArms.SetupRadialMenu(PlayerCamera.main);
		}
		catch (Exception e)
		{
			MPBridgePlugin.Log?.LogWarning("[GG-MP] orange arms ui failed: " + e.Message);
		}
	}

	// the paperdoll is built for 15 limbs. orange is 21, and updateview indexes
	// limbImages[k] and the infection flags for every limb. the remote flag
	// array is sized when the body wakes, before the extra arms exist, so grow
	// it after together swaps that array in.
	[HarmonyPatch(typeof(WoundView), "UpdateView")]
	[HarmonyPriority(Priority.Last)]
	static class GrowWoundView
	{
		static int _logged;

		static Exception Finalizer(Exception __exception)
		{
			FitCameraInfection();
			return __exception;
		}

		static void Prefix(WoundView __instance)
		{
			if (__instance == null || __instance.body == null || __instance.body.limbs == null)
				return;
			int n = __instance.body.limbs.Length;
			if (__instance.limbLookingAt < 0 || __instance.limbLookingAt >= n)
				__instance.limbLookingAt = 0;
			if (n <= 15)
			{
				ShowExtra(__instance, false);
				return;
			}
			if (!ImagesFit(__instance, n))
				Grow(__instance);
			OrangeArms.GrowInfection(__instance.body, n);
			// together already swapped the camera list in. updateview reads one
			// entry per limb, so this call needs the long list. the finalizer
			// fits it back to the camera body if the swap is left behind.
			if (PlayerCamera.main != null && PlayerCamera.main.showInfection != null
				&& PlayerCamera.main.showInfection.Length < n)
				GrowBool(ref PlayerCamera.main.showInfection, n);
			GrowTips(__instance, n);
			ShowExtra(__instance, true);
		}

		static bool ImagesFit(WoundView view, int n)
		{
			return view.limbImages != null && view.limbImages.Length >= n
				&& view.limbImageFlash != null && view.limbImageFlash.Length >= n
				&& view.limbImageLerp != null && view.limbImageLerp.Length >= n
				&& view.origPositions != null && view.origPositions.Length >= n;
		}

		static void Grow(WoundView view)
		{
			if (WoundView.view != view || view.body == null || view.body.limbs == null)
				return;
			if (view.body.limbs.Length <= 15)
				return;
			try
			{
				OrangeFourArms.SetupWoundViewLimbs(view.body);
				int images = view.limbImages != null ? view.limbImages.Length : 0;
				MPBridgePlugin.Log?.LogInfo("[GG-MP] orange wound view images=" + images);
			}
			catch (Exception e)
			{
				int id = view.GetInstanceID();
				if (_logged == id)
					return;
				_logged = id;
				MPBridgePlugin.Log?.LogWarning("[GG-MP] orange wound view failed: " + e.Message);
			}
		}

		static void GrowTips(WoundView view, int n)
		{
			UITooltip[] tips = view.limbTooltips;
			Image[] images = view.limbImages;
			if (tips == null || images == null || tips.Length >= n)
				return;
			var next = new UITooltip[n];
			Array.Copy(tips, next, tips.Length);
			for (int i = tips.Length; i < n && i < images.Length; i++)
			{
				if (images[i] == null)
					continue;
				UITooltip tip = images[i].GetComponent<UITooltip>();
				if (tip == null)
					tip = images[i].gameObject.AddComponent<UITooltip>();
				tip.skipLocale = true;
				next[i] = tip;
			}
			view.limbTooltips = next;
		}

		static void ShowExtra(WoundView view, bool on)
		{
			Image[] images = view.limbImages;
			if (images == null)
				return;
			int limbs = view.body != null && view.body.limbs != null ? view.body.limbs.Length : 0;
			for (int i = 15; i < images.Length; i++)
			{
				if (images[i] == null)
					continue;
				bool show = on && i < limbs;
				if (images[i].gameObject.activeSelf != show)
					images[i].gameObject.SetActive(show);
			}
		}
	}

	static void GrowInfection(Body body, int n)
	{
		if (body.TryGetNetBody(out NetBody nb))
			GrowBool(ref nb.localoverride_showInfection, n);
		// the camera walks this array against its own limbs every frame.
		// a longer list throws inside PlayerCamera.Update and the follow never runs.
		if (PlayerCamera.main == null || PlayerCamera.main.body != body || PlayerCamera.main.body.limbs == null)
			return;
		int fit = PlayerCamera.main.body.limbs.Length;
		if (n > fit)
			n = fit;
		GrowBool(ref PlayerCamera.main.showInfection, n);
	}

	// wound view indexes one flag per limb. together points the camera at the
	// remote list for that call, then puts the local list back. if that call
	// throws, the long list stays and the camera update dies on the next frame.
	internal static void FitCameraInfection()
	{
		PlayerCamera cam = PlayerCamera.main;
		if (cam == null || cam.body == null || cam.body.limbs == null)
			return;
		int n = cam.body.limbs.Length;
		bool[] flags = cam.showInfection;
		if (flags != null && flags.Length == n)
			return;
		var fit = new bool[n];
		if (flags != null)
			Array.Copy(flags, fit, Math.Min(flags.Length, n));
		cam.showInfection = fit;
	}

	static void GrowBool(ref bool[] arr, int n)
	{
		if (arr != null && arr.Length >= n)
			return;
		var next = new bool[n];
		if (arr != null)
			Array.Copy(arr, next, arr.Length);
		arr = next;
	}

	static bool BodyIsOrange(Body body)
	{
		if (body == null)
			return false;
		string species = SpeciesMultiplayerSync.GetSpeciesForBody(body);
		if (string.Equals(species, "Orange", StringComparison.Ordinal))
			return true;
		return PlayerCamera.main != null
			&& PlayerCamera.main.body == body
			&& string.Equals(SpeciesAbilityManager.ActiveSpecies, "Orange", StringComparison.Ordinal);
	}

	static string SaveFolder()
	{
		return SavesystemPatch.savedatapathreplacement;
	}

	// a file with orange, or with a hand past the vanilla six, has to grow before the loader indexes limbs.
	static bool FileWantsArms(string folder)
	{
		if (string.IsNullOrEmpty(folder))
			return false;
		string path = Path.Combine(folder, "save.sv");
		if (!File.Exists(path))
			return false;
		try
		{
			var root = JObject.Parse(SaveSystem.Unzip(File.ReadAllBytes(path)));
			if (string.Equals(root.Value<string>("ggSpecies"), "Orange", StringComparison.Ordinal))
				return true;
			var limbs = root["limbs"] as JArray;
			if (limbs != null && limbs.Count > 15)
				return true;
			var items = root["items"] as JArray;
			if (items == null)
				return false;
			foreach (var item in items)
			{
				int slot = item.Value<int?>("slot") ?? -1;
				if (slot >= 6)
					return true;
			}
		}
		catch (Exception e)
		{
			MPBridgePlugin.Log?.LogWarning("[GG-MP] orange save read failed: " + e.Message);
		}
		return false;
	}

	[HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.SaveGame))]
	[HarmonyPriority(Priority.Last)]
	static class GrowBeforeSave
	{
		static void Prefix()
		{
			try
			{
				Body body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
				if (body == null)
					return;
				if (KnownOther(body))
					return;
				if (BodyIsOrange(body) || FileWantsArms(SaveFolder()))
					Ensure(body);
			}
			catch (Exception e)
			{
				MPBridgePlugin.Log?.LogWarning("[GG-MP] orange save grow failed: " + e.Message);
			}
		}
	}

	// a mismatched infection list used to throw here and skip the camera follow.
	[HarmonyPatch(typeof(PlayerCamera), "HandleLimbInfectionShows")]
	static class FitInfectionBeforeShow
	{
		static void Prefix()
		{
			FitCameraInfection();
		}
	}

	[HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.TryLoadGame))]
	[HarmonyPriority(Priority.Last)]
	static class GrowBeforeLoad
	{
		static void Prefix()
		{
			try
			{
				Body body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
				if (KnownOther(body) || !FileWantsArms(SaveFolder()))
					return;
				Ensure(body);
			}
			catch (Exception e)
			{
				MPBridgePlugin.Log?.LogWarning("[GG-MP] orange load grow failed: " + e.Message);
			}
		}
	}

	// the health packet stops at limb 14. breaks, bleeds, splints and the rest
	// of the extra arms ride along with that send.
	const string ExtraLimbMsg = "GG.Orange.Limbs";
	static bool _extraHooked;

	internal static bool SkipStaleSetup()
	{
		if (!SpeciesMultiplayerSync.IsNetworkRunning())
			return true;
		Body body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
		return GuardSetup(body);
	}

	// leftover solo config must not grow arms on a multiplayer body that is not orange this run.
	internal static bool GuardSetup(Body body)
	{
		if (!SpeciesMultiplayerSync.IsNetworkRunning())
			return true;
		if (body == null)
			return false;
		string species = SpeciesMultiplayerSync.GetSpeciesForBody(body);
		if (IsPrefab(body))
			return false;
		if (string.Equals(species, "Orange", StringComparison.Ordinal))
			return true;
		if (!string.IsNullOrEmpty(species))
			return false;
		return string.IsNullOrEmpty(species)
			&& PlayerCamera.main != null
			&& body == PlayerCamera.main.body
			&& SpeciesAbilityManager.ActiveSpecies == "Orange";
	}

	static bool KnownOther(Body body)
	{
		string species = SpeciesMultiplayerSync.GetSpeciesForBody(body);
		return !string.IsNullOrEmpty(species)
			&& !string.Equals(species, "Orange", StringComparison.Ordinal);
	}

	static bool IsPrefab(Body body)
	{
		GameObject prefab = ServerMain.CHARACTER_PREFAB;
		if (body == null || prefab == null)
			return false;
		return body.transform.IsChildOf(prefab.transform);
	}

	static void StripPrefab(Body grown)
	{
		GameObject prefab = ServerMain.CHARACTER_PREFAB;
		if (prefab == null)
			return;
		Body body = prefab.GetComponentInChildren<Body>(true);
		if (body == null || body == grown)
			return;
		Strip(body);
	}

	static void KeepOnBody(Body body)
	{
		if (body == null || body.limbs == null)
			return;
		for (int i = 15; i < body.limbs.Length; i++)
		{
			Limb limb = body.limbs[i];
			if (limb == null || limb.transform.IsChildOf(body.transform))
				continue;
			limb.transform.SetParent(body.transform, true);
		}
	}

	static int DestroyNamedExtras(Body body)
	{
		Limb[] found = body.GetComponentsInChildren<Limb>(true);
		int n = 0;
		foreach (Limb limb in found)
		{
			if (limb == null || limb.gameObject == null || !IsExtraName(limb.gameObject.name))
				continue;
			if (KeptLimb(body, limb))
				continue;
			Unhook(body, limb);
			UnityEngine.Object.Destroy(limb.gameObject);
			n++;
		}
		return n;
	}

	static bool IsExtraName(string name)
	{
		return name == "UpArmF2" || name == "DownArmF2" || name == "HandF2"
			|| name == "UpArmB2" || name == "DownArmB2" || name == "HandB2";
	}

	static bool KeptLimb(Body body, Limb limb)
	{
		if (body.limbs == null)
			return false;
		int n = Math.Min(15, body.limbs.Length);
		for (int i = 0; i < n; i++)
		{
			if (body.limbs[i] == limb)
				return true;
		}
		return false;
	}

	internal static void Strip(Body body)
	{
		if (body == null)
			return;
		int named = DestroyNamedExtras(body);
		int wedges = RemoveLocalWedges(body);
		bool extraLimbs = body.limbs != null && body.limbs.Length > 15;
		bool extraSlots = body.slots != null && body.slots.Length > 6;
		if (!extraLimbs && !extraSlots && named == 0)
		{
			if (wedges > 0)
				MPBridgePlugin.Log?.LogInfo("[GG-MP] stripped orange hand wedges");
			FitInfection(body);
			return;
		}
		if (extraLimbs)
		{
			var keep = new List<Limb>();
			for (int i = 0; i < body.limbs.Length; i++)
			{
				Limb limb = body.limbs[i];
				if (i < 15)
				{
					if (limb != null)
						keep.Add(limb);
					continue;
				}
				if (limb == null)
					continue;
				Unhook(body, limb);
				UnityEngine.Object.Destroy(limb.gameObject);
			}
			body.limbs = keep.ToArray();
		}
		if (body.slots != null && body.slots.Length > 6)
		{
			for (int i = 6; i < body.slots.Length; i++)
			{
				if (body.slots[i] != null)
					UnityEngine.Object.Destroy(body.slots[i].gameObject);
			}
			var smaller = new InventorySlot[6];
			Array.Copy(body.slots, smaller, 6);
			body.slots = smaller;
		}
		ForgetInitialized(body);
		FitInfection(body);
		MPBridgePlugin.Log?.LogInfo("[GG-MP] stripped extra arms");
	}

	// orange grows the infection flags to match the extra arms. together walks
	// that list against body.limbs every slower update. a shorter limb list
	// after a strip throws IndexOutOfRange on every tick.
	static void FitInfection(Body body)
	{
		if (body == null || body.limbs == null)
			return;
		if (body.TryGetNetBody(out NetBody nb))
			FitBool(ref nb.localoverride_showInfection, body.limbs.Length);
		if (PlayerCamera.main != null && PlayerCamera.main.body == body)
			FitCameraInfection();
	}

	static void FitBool(ref bool[] arr, int n)
	{
		if (n < 0 || arr == null || arr.Length == n)
			return;
		var fit = new bool[n];
		Array.Copy(arr, fit, Math.Min(arr.Length, n));
		arr = fit;
	}

	static void Unhook(Body body, Limb gone)
	{
		if (body.limbs == null)
			return;
		foreach (Limb limb in body.limbs)
		{
			if (limb == null || limb.connectedLimbs == null)
				continue;
			bool hit = false;
			for (int i = 0; i < limb.connectedLimbs.Length; i++)
			{
				if (limb.connectedLimbs[i] == gone)
				{
					hit = true;
					break;
				}
			}
			if (!hit)
				continue;
			var next = new List<Limb>();
			foreach (Limb other in limb.connectedLimbs)
			{
				if (other != null && other != gone)
					next.Add(other);
			}
			limb.connectedLimbs = next.ToArray();
		}
	}

	static int RemoveLocalWedges(Body body)
	{
		ScavPlayer me = ScavPlayer.LocalPlayer;
		PlayerCamera cam = PlayerCamera.main;
		if (me == null || cam == null || me.body != body || cam.radialMenu == null)
			return 0;
		int n = DestroyChild(cam.radialMenu, "MainMainHand2");
		n += DestroyChild(cam.radialMenu, "MainBackHand2");
		n += DestroyHandLabels(cam.radialMenu);
		if (n > 0)
			ForgetRadial(cam);
		return n;
	}

	static int DestroyChild(Transform root, string name)
	{
		Transform child = root.Find(name);
		if (child == null)
			return 0;
		UnityEngine.Object.Destroy(child.gameObject);
		return 1;
	}

	static int DestroyHandLabels(Transform root)
	{
		int n = 0;
		for (int i = root.childCount - 1; i >= 0; i--)
		{
			Transform child = root.GetChild(i);
			if (child == null)
				continue;
			Component tmp = child.GetComponent("TMPro.TextMeshProUGUI");
			if (tmp == null)
				continue;
			var prop = AccessTools.Property(tmp.GetType(), "text");
			string text = prop != null ? prop.GetValue(tmp, null) as string : null;
			if (text != "Tertiary\nhand" && text != "Quaternary\nhand")
				continue;
			UnityEngine.Object.Destroy(child.gameObject);
			n++;
		}
		return n;
	}

	static void ForgetRadial(PlayerCamera cam)
	{
		var field = AccessTools.Field(typeof(OrangeFourArms), "radialMenuInitialized");
		if (field == null)
			return;
		object set = field.GetValue(null);
		if (set is ICollection<PlayerCamera> cams)
			cams.Remove(cam);
	}

	static void ForgetInitialized(Body body)
	{
		var field = AccessTools.Field(typeof(OrangeFourArms), "initialized");
		if (field == null)
			return;
		object set = field.GetValue(null);
		if (set is ICollection<Body> bodies)
			bodies.Remove(body);
		else if (set is ICollection<int> ids)
			ids.Remove(body.GetInstanceID());
	}

	internal static void Install(Harmony harmony)
	{
		if (harmony != null)
		{
			var setup = AccessTools.Method(typeof(OrangeFourArms), "Setup");
			if (setup != null)
				harmony.Patch(setup, prefix: new HarmonyMethod(typeof(OrangeArms), nameof(GuardSetup)));
		}
		if (_extraHooked)
			return;
		_extraHooked = true;
		HookExtraThrow(harmony);
		Multiplayer.RegisterCustomClientReceiver(ExtraLimbMsg, OnExtraLimbs);
		var medical = AccessTools.TypeByName("Together.MedicalSync");
		var send = medical != null ? AccessTools.Method(medical, "Server_SendCharacterHealth") : null;
		if (send == null || harmony == null)
		{
			MPBridgePlugin.Log?.LogWarning("[GG-MP] orange limb sync hook missing");
			return;
		}
		harmony.Patch(send, postfix: new HarmonyMethod(typeof(OrangeArms), nameof(AfterHealth)));
	}

	static Body _throwBody;
	static int _throwHand;
	static bool _throwAim;

	// together throws the item in handSlot and refuses a selected hand above 1.
	// an orange extra hand holds the item in slot 6 or 7, so the host declined
	// the throw and the item snapped back. aim the host at the slot that has it.
	static void HookExtraThrow(Harmony harmony)
	{
		var sync = AccessTools.TypeByName("Together.ItemSync");
		var method = sync != null ? AccessTools.Method(sync, "ServerReceiver__ThrowItem") : null;
		if (method == null || harmony == null)
		{
			MPBridgePlugin.Log?.LogWarning("[GG-MP] extra-hand throw hook missing");
			return;
		}
		harmony.Patch(method,
			prefix: new HarmonyMethod(typeof(OrangeArms), nameof(AimThrow)),
			postfix: new HarmonyMethod(typeof(OrangeArms), nameof(RestoreThrow)));
	}

	static void AimThrow(knetid clientId, NetDataReader reader)
	{
		_throwAim = false;
		if (reader == null)
			return;
		int at = reader.Position;
		ushort id;
		try
		{
			id = reader.GetUShort();
		}
		finally
		{
			reader.SetPosition(at);
		}
		if (!ScavPlayer.TryGetPlayerFromClientId(clientId, out ScavPlayer plr) || plr == null || plr.body == null)
			return;
		if (!NetObjectRegistry.TryGetSyncInfo((knetid)id, out SyncInfo si) || si == null || si.item == null)
			return;
		Body body = plr.body;
		int slot = SlotHolding(body, si.item);
		if (slot < 0 || slot == body.handSlot)
			return;
		_throwBody = body;
		_throwHand = body.handSlot;
		body.handSlot = slot;
		_throwAim = true;
	}

	static void RestoreThrow()
	{
		if (!_throwAim || _throwBody == null)
			return;
		_throwBody.handSlot = _throwHand;
		_throwBody = null;
		_throwAim = false;
	}

	static int SlotHolding(Body body, Item item)
	{
		if (body.slots == null || item == null)
			return -1;
		for (int i = 0; i < body.slots.Length; i++)
		{
			if (body.HoldingItem(i) && body.GetItem(i) == item)
				return i;
		}
		return -1;
	}

	static void AfterHealth(NetBody nb)
	{
		try
		{
			SendExtra(nb);
		}
		catch (Exception e)
		{
			MPBridgePlugin.Log?.LogWarning("[GG-MP] orange limb sync failed: " + e.Message);
		}
	}

	static void SendExtra(NetBody nb)
	{
		if (nb == null || nb.syncBody == null || nb.body == null || nb.body.limbs == null || nb.body.limbs.Length <= 15)
			return;
		if (!Net.IsServer)
			return;
		var writer = Multiplayer.CreateNamedWriter(ExtraLimbMsg);
		writer.Put(nb.netId.id);
		writer.Put((byte)(nb.body.limbs.Length - 15));
		for (int i = 15; i < nb.body.limbs.Length; i++)
			WriteLimb(writer, nb.body.limbs[i]);
		NetIo.ServerSend(writer, ServerMain.AllClientIdsExceptHost);
	}

	static void WriteLimb(NetDataWriter writer, Limb limb)
	{
		byte flags = 0;
		if (limb.dismembered) flags |= 1;
		if (limb.infected) flags |= 2;
		if (limb.broken) flags |= 4;
		if (limb.dislocated) flags |= 8;
		if (limb.splinted) flags |= 16;
		if (limb.GetComponent<TourniquetScript>() != null) flags |= 32;
		SplintLimb splint = limb.GetComponent<SplintLimb>();
		if (splint != null && splint.item == "carcasssplint") flags |= 64;
		writer.Put(flags);
		writer.Put((byte)Mathf.Clamp(limb.shrapnel, 0, 255));
		writer.Put(limb.skinHealth);
		writer.Put(limb.muscleHealth);
		writer.Put(limb.bleedAmount);
		writer.Put(limb.pain);
		writer.Put(limb.infectionAmount);
		writer.Put(limb.broken ? Mathf.Max(limb.boneHealTimer, 0.01f) : 0f);
		writer.Put(limb.dislocated ? Mathf.Max(limb.dislocationTimer, 0.01f) : 0f);
		writer.Put(limb.furBloodAmount);
		writer.Put((ushort)Mathf.Clamp(limb.disinfectionTime, 0f, ushort.MaxValue));
	}

	static void OnExtraLimbs(NetDataReader reader)
	{
		ushort id = reader.GetUShort();
		int count = reader.GetByte();
		NetBody.TryGetNetBodyFromId((knetid)id, out NetBody nb);
		Body body = nb != null ? nb.body : null;
		for (int i = 0; i < count; i++)
		{
			byte flags = reader.GetByte();
			byte shrapnel = reader.GetByte();
			float skin = reader.GetFloat();
			float muscle = reader.GetFloat();
			float bleed = reader.GetFloat();
			float pain = reader.GetFloat();
			float infection = reader.GetFloat();
			float bone = reader.GetFloat();
			float disloc = reader.GetFloat();
			float fur = reader.GetFloat();
			ushort disinfect = reader.GetUShort();
			int index = 15 + i;
			if (body == null || body.limbs == null || index >= body.limbs.Length || body.limbs[index] == null)
				continue;
			ApplyLimb(body.limbs[index], flags, shrapnel, skin, muscle, bleed, pain, infection, bone, disloc, fur, disinfect);
		}
	}

	static void ApplyLimb(Limb limb, byte flags, byte shrapnel, float skin, float muscle, float bleed, float pain, float infection, float bone, float disloc, float fur, ushort disinfect)
	{
		limb.dismembered = (flags & 1) != 0;
		if (limb.gameObject != null)
			limb.gameObject.SetActive(!limb.dismembered);
		limb.infected = (flags & 2) != 0;
		if (bone > 0f)
		{
			if (!limb.broken)
				limb.BreakBone();
		}
		else if (limb.broken)
			limb.MendBone();
		limb.boneHealTimer = bone;
		if (disloc > 0f)
		{
			if (!limb.dislocated)
				limb.Dislocate();
		}
		else
			limb.dislocated = false;
		limb.dislocationTimer = disloc;
		limb.shrapnel = shrapnel;
		limb.skinHealth = skin;
		limb.muscleHealth = muscle;
		limb.bleedAmount = bleed;
		limb.pain = pain;
		limb.infectionAmount = infection;
		limb.furBloodAmount = fur;
		limb.disinfectionTime = disinfect;
		bool wantSplint = (flags & 16) != 0;
		limb.splinted = wantSplint;
		if (wantSplint)
		{
			SplintLimb splint = limb.GetComponent<SplintLimb>();
			if (splint == null)
			{
				splint = limb.gameObject.AddComponent<SplintLimb>();
				bool carcass = (flags & 64) != 0;
				splint.item = carcass ? "carcasssplint" : "splint";
				splint.conditionLossMinute = carcass ? 0.036f : 0.015f;
				splint.condition = 1f;
			}
		}
		else
		{
			SplintLimb old = limb.GetComponent<SplintLimb>();
			if (old != null)
				UnityEngine.Object.Destroy(old);
		}
		bool wantTie = (flags & 32) != 0;
		TourniquetScript tie = limb.GetComponent<TourniquetScript>();
		if (wantTie)
		{
			if (tie == null)
				limb.gameObject.AddComponent<TourniquetScript>();
		}
		else if (tie != null)
		{
			if (tie.affectedLimbs != null)
			{
				for (int i = 0; i < tie.affectedLimbs.Count; i++)
				{
					if (tie.affectedLimbs[i] != null)
						tie.affectedLimbs[i].blockedBleeding = false;
				}
			}
			UnityEngine.Object.Destroy(tie);
		}
	}
}
