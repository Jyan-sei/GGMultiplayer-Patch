using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace GunsawGenetics;

// the paperdoll, name, and scars follow ActiveSpecies, which is the local player.
// an inspected body should show that body's species instead.
internal static class GGWoundViewSync
{
	// one window, remembered so updateview does not rebuild it every frame
	static readonly Dictionary<int, string> shown = new Dictionary<int, string>();
	static int _bodyToken;
	static string _bodySpecies;

	internal static void InvalidateSpeciesCache()
	{
		shown.Clear();
		_bodyToken = 0;
		_bodySpecies = null;
	}
	static readonly FieldInfo RecolorSpeciesField = AccessTools.Field(typeof(WoundViewRecolor), "currentSpeciesForRecolor");
	static readonly FieldInfo NameTextField = AccessTools.Field(typeof(WoundView), "nameText");
	static readonly FieldInfo GameFontField = AccessTools.Field(typeof(SpeciesMoodSystem), "GameFont");
	static readonly Func<string> BaronHead = Calls.Bind<Func<string>>(AccessTools.Method(typeof(SpeciesAbilityManager), "GetBaronHeadSuffix"));
	// stock calls the recolor every frame. it only runs while we are painting one look.
	static int _paint;
	static Harmony _harmony;
	static MethodInfo _update;
	static bool _stockCleared;
	static readonly Stopwatch _clock = new Stopwatch();
	static int _samples;
	static double _sampleMs;

	internal static void Install(Harmony harmony)
	{
		MethodInfo update = AccessTools.Method(typeof(WoundView), "UpdateView");
		if (update == null)
		{
			MPBridgePlugin.Log?.LogWarning("[GG-MP] wound view update missing");
			return;
		}
		int stripped = Unpatch(harmony, update, "WoundViewLabelWatchdogPatch");
		stripped += Unpatch(harmony, update, "WoundViewScarUpdatePatch");
		stripped += Unpatch(harmony, update, "WoundViewSpritePatch");
		// genetics often patches after us, so unpatch can miss it. skip the body either way.
		// that postfix reloads every paperdoll sprite and recolors every image, every frame.
		MethodInfo stockPaint = AccessTools.Method(typeof(WoundViewSpritePatch), "Postfix");
		if (stockPaint != null)
			harmony.Patch(stockPaint, prefix: new HarmonyMethod(typeof(GGWoundViewSync), nameof(SkipStockPaint)));
		MethodInfo recolor = AccessTools.Method(typeof(WoundViewRecolor), "Apply");
		MethodInfo paper = AccessTools.Method(typeof(WoundViewSpritePatch), "ApplyPaperdollSprites");
		if (recolor != null)
			harmony.Patch(recolor, prefix: new HarmonyMethod(typeof(GGWoundViewSync), nameof(PaintAllowed)));
		if (paper != null)
			harmony.Patch(paper, prefix: new HarmonyMethod(typeof(GGWoundViewSync), nameof(PaintAllowed)));
		_harmony = harmony;
		_update = update;
		harmony.Patch(
			update,
			prefix: new HarmonyMethod(typeof(GGWoundViewSync), nameof(BeginFrame)),
			postfix: new HarmonyMethod(typeof(GGWoundViewSync), nameof(Postfix)));
		MPBridgePlugin.Log?.LogInfo(
			"[GG-MP] wound view paint is cached, removed " + stripped
			+ " per-frame patches, recolor gate=" + (recolor != null)
			+ " paper gate=" + (paper != null));
	}

	static int Unpatch(Harmony harmony, MethodInfo original, string declaringType)
	{
		var info = Harmony.GetPatchInfo(original);
		if (info == null)
			return 0;
		int n = 0;
		foreach (var patch in info.Postfixes)
		{
			if (patch.PatchMethod?.DeclaringType?.Name != declaringType)
				continue;
			harmony.Unpatch(original, patch.PatchMethod);
			n++;
		}
		return n;
	}

	// the stock paint walks every image and builds new sprites. one run per look is enough.
	static bool SkipStockPaint()
	{
		return false;
	}

	static bool PaintAllowed()
	{
		return _paint > 0;
	}

	static void NoteFrame()
	{
		if (_samples >= 30)
			return;
		_sampleMs += _clock.Elapsed.TotalMilliseconds;
		_samples++;
		if (_samples == 30)
			MPBridgePlugin.Log?.LogInfo("[GG-MP] wound view update ms=" + (_sampleMs / 30.0).ToString("0.0"));
	}

	static void ClearStockPaint()
	{
		if (_stockCleared || _harmony == null || _update == null)
			return;
		Unpatch(_harmony, _update, "WoundViewSpritePatch");
		var info = Harmony.GetPatchInfo(_update);
		if (info != null)
		{
			foreach (var patch in info.Postfixes)
			{
				if (patch.PatchMethod?.DeclaringType?.Name == "WoundViewSpritePatch")
					return;
			}
		}
		_stockCleared = true;
		MPBridgePlugin.Log?.LogInfo("[GG-MP] wound view stock paint unhooked");
	}

	[HarmonyPriority(Priority.First)]
	static void BeginFrame()
	{
		if (_samples < 30)
			_clock.Restart();
	}

	// last so this runs after the genetics paperdoll postfix, which writes the local species name
	[HarmonyPriority(Priority.Last)]
	static void Postfix(WoundView __instance)
	{
		NoteFrame();
		ClearStockPaint();
		if (__instance == null)
			return;
		Body body = __instance.body;
		if (body == null)
			return;
		string species = SpeciesOf(body);
		if (string.IsNullOrEmpty(species))
			return;
		// a new body or a new species drops the painted look. a leftover leapy doll was sticking on scarlet.
		int bodyToken = body.GetInstanceID();
		if (_bodyToken != bodyToken || !string.Equals(_bodySpecies, species, System.StringComparison.Ordinal))
		{
			shown.Clear();
			_bodyToken = bodyToken;
			_bodySpecies = species;
		}
		// the corner title is the prefab word until this writes it. own view used to
		// skip that write, so it stayed "experiment" after the first open.
		SetLabel(__instance, species);
		HoldStill(__instance);
		string visual = VisualSpecies(body, species);
		string head = "";
		if (visual == "Baron" && BaronHead != null)
			head = BaronHead() ?? "";
		int images = __instance.limbImages != null ? __instance.limbImages.Length : 0;
		string look = species + "\n" + visual + "\n" + head + "\n" + images;
		int id = __instance.GetInstanceID();
		if (shown.TryGetValue(id, out string had) && had == look)
			return;
		shown[id] = look;
		NoteFont(__instance);
		// recolor samples whatever sprite is on the image. start already shifted those to the
		// local species, so put the vanilla green back first or every window stays that tint.
		_paint++;
		try
		{
			string tinted = RecolorSpeciesField != null ? RecolorSpeciesField.GetValue(null) as string : null;
			if (!string.Equals(tinted, species, System.StringComparison.Ordinal))
			{
				WoundViewRecolor.ResetToOriginal();
				WoundViewRecolor.Apply(__instance, species);
			}
			// paint once. the stock patch was doing this, plus a full recolor, every frame.
			WoundViewSpritePatch.ApplyPaperdollSprites(__instance, visual);
			// the second pair has its own smaller art. paperdoll has no limb 15-20 png,
			// and copying the main arms onto them stacks those pieces on the lower slots.
			if (species == "Orange")
				RestoreLowerArms(__instance);
			ApplyScars(__instance, body, visual);
		}
		finally
		{
			_paint--;
		}
		MPBridgePlugin.Log?.LogInfo("[GG-MP] wound view species=" + species);
	}

	static void NoteFont(WoundView view)
	{
		if (GameFontField == null || GameFontField.GetValue(null) != null)
			return;
		Type tmp = AccessTools.TypeByName("TMPro.TextMeshProUGUI");
		if (tmp == null)
			return;
		Component text = view.GetComponentInChildren(tmp);
		if (text == null)
			return;
		PropertyInfo font = text.GetType().GetProperty("font");
		object value = font != null ? font.GetValue(text, null) : null;
		if (value != null)
			GameFontField.SetValue(null, value);
	}

	static readonly Action<Image[]> LowerArmSprites = Calls.Bind<Action<Image[]>>(AccessTools.Method(typeof(OrangeFourArms), "ApplyLowerArmWoundViewSprites"));

	// orange places the extra arms, then swaps in Orange_Lower_Limb art at that size.
	// run it again after the paperdoll pass so those six keep their own sprites.
	static void RestoreLowerArms(WoundView view)
	{
		if (LowerArmSprites == null || view.limbImages == null || view.limbImages.Length <= 15)
			return;
		try
		{
			LowerArmSprites(view.limbImages);
		}
		catch (Exception e)
		{
			MPBridgePlugin.Log?.LogWarning("[GG-MP] orange lower arms failed: " + e.Message);
		}
	}

	static string SpeciesOf(Body body)
	{
		// host map, including the local body. ActiveSpecies can still be the previous run.
		string mapped = SpeciesMultiplayerSync.GetSpeciesForBody(body);
		if (!string.IsNullOrEmpty(mapped))
			return mapped;
		bool local = PlayerCamera.main != null && body == PlayerCamera.main.body;
		if (local && !string.IsNullOrEmpty(SpeciesAbilityManager.ActiveSpecies))
			return SpeciesAbilityManager.ActiveSpecies;
		return "Experiment";
	}

	// updateview adds a random offset from limb pain every frame. with no pain
	// that offset is noise, and on the long orange doll it reads as the whole body shaking.
	static void HoldStill(WoundView view)
	{
		Body body = view.body;
		if (body == null || body.limbs == null || view.limbImages == null || view.origPositions == null)
			return;
		int n = body.limbs.Length;
		if (n <= 0 || view.limbImages.Length < n || view.origPositions.Length < n)
			return;
		for (int i = 0; i < n; i++)
		{
			Limb limb = body.limbs[i];
			if (limb != null && limb.pain > 4f)
				return;
		}
		for (int i = 0; i < n; i++)
		{
			Image image = view.limbImages[i];
			if (image == null)
				continue;
			Vector2 placed = view.origPositions[i];
			if (image.rectTransform.anchoredPosition != placed)
				image.rectTransform.anchoredPosition = placed;
		}
	}

	static string VisualSpecies(Body body, string species)
	{
		if (species != "Mercenary")
			return species;
		if (PlayerCamera.main == null || body != PlayerCamera.main.body)
			return species;
		string host = SpeciesAbilityManager.MercenaryHostSpecies;
		return !string.IsNullOrEmpty(host) && host != "Mercenary" ? host : species;
	}

	static Type _labelType;
	static string _labelSpecies;
	static string _labelText;
	static int _labelView;
	static object _labelName;
	static object _labelSlot;

	static void SetLabel(WoundView view, string species)
	{
		if (_labelSpecies != species)
		{
			_labelSpecies = species;
			_labelText = Localization.Get("species_" + species.ToLower() + "_name", species).ToUpper();
		}
		int id = view.GetInstanceID();
		if (_labelView != id)
		{
			_labelView = id;
			_labelName = NameTextField != null ? NameTextField.GetValue(view) : null;
			_labelSlot = null;
			Transform title = view.transform.Find("ExperimentTitle");
			Transform slot = title != null ? title.Find("Species") : null;
			if (slot != null)
			{
				if (_labelType == null)
					_labelType = AccessTools.TypeByName("TMPro.TextMeshProUGUI");
				if (_labelType != null)
					_labelSlot = slot.GetComponent(_labelType);
			}
		}
		WriteText(_labelName, _labelText);
		WriteText(_labelSlot, _labelText);
	}

	static readonly HashSet<object> _styled = new HashSet<object>();

	static void WriteText(object label, string text)
	{
		if (label == null)
			return;
		PropertyInfo textProp = label.GetType().GetProperty("text");
		if (textProp == null)
			return;
		if ((textProp.GetValue(label, null) as string) != text)
			textProp.SetValue(label, text, null);
		if (_styled.Contains(label))
			return;
		PropertyInfo style = label.GetType().GetProperty("fontStyle");
		if (style != null && style.CanWrite)
			style.SetValue(label, 1, null);
		_styled.Add(label);
	}

	static void ApplyScars(WoundView view, Body body, string visual)
	{
		Sprite left = WoundViewInjuryCache.GetLeftEyeMissing(visual);
		if (visual == "Albino" && IsLocal(body) && !AlbinoWounds.EyeScar)
			left = null;
		Paint(view, "eyeGoneImage", left, WoundViewInjuryCache.VanillaEyeGone, WoundViewInjuryCache.VanillaEyeGoneRotation);
		Paint(view, "bothEyesGoneImage", WoundViewInjuryCache.GetRightEyeMissing(visual), WoundViewInjuryCache.VanillaBothEyesGone, WoundViewInjuryCache.VanillaBothEyesRotation);
		Paint(view, "disfiguredImage", WoundViewInjuryCache.GetJawDisfigured(visual), WoundViewInjuryCache.VanillaDisfigured, WoundViewInjuryCache.VanillaDisfiguredRotation);
	}

	static bool IsLocal(Body body)
	{
		return PlayerCamera.main != null && body == PlayerCamera.main.body;
	}

	static void Paint(WoundView view, string fieldName, Sprite custom, Sprite vanilla, Quaternion vanillaRot)
	{
		FieldInfo field = AccessTools.Field(typeof(WoundView), fieldName);
		object image = field != null ? field.GetValue(view) : null;
		if (image == null)
			return;
		System.Type type = image.GetType();
		PropertyInfo sprite = type.GetProperty("sprite");
		if (sprite != null)
			sprite.SetValue(image, custom != null ? custom : vanilla, null);
		PropertyInfo rect = type.GetProperty("rectTransform");
		object rt = rect != null ? rect.GetValue(image, null) : null;
		if (rt is Component component)
		{
			Transform transform = component.transform;
			transform.localRotation = custom != null ? Quaternion.identity : vanillaRot;
			transform.localScale = Vector3.one;
		}
	}
}
