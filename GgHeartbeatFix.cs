using System.Reflection;
using CasualtiesTogetherUtils;
using HarmonyLib;
using Together;
using UnityEngine;

namespace GunsawGenetics;

// opening a remote wound view left the local body thumping too.
// only the body on the panel gets the heart sound.
internal static class GgHeartbeatFix
{
	static FieldInfo _muteHeart;

	internal static void Install(Harmony harmony)
	{
		MethodInfo circ = AccessTools.Method(typeof(Body), nameof(Body.HandleCirculation));
		if (circ == null)
		{
			MPBridgePlugin.Log?.LogWarning("[GG-MP] heartbeat gate: HandleCirculation missing");
			return;
		}

		var soundPatch = AccessTools.TypeByName("Sound_Play_MultiplayerPatch");
		_muteHeart = soundPatch != null
			? AccessTools.Field(soundPatch, "is_the_fucking_updateheart_shit")
			: null;
		if (_muteHeart == null)
		{
			MPBridgePlugin.Log?.LogWarning("[GG-MP] heartbeat gate: Sound_Play mute flag missing");
			return;
		}

		harmony.Patch(
			circ,
			prefix: new HarmonyMethod(typeof(GgHeartbeatFix), nameof(GatePrefix))
			{
				priority = Priority.Last
			});
		MPBridgePlugin.Log?.LogInfo("[GG-MP] heartbeat audio gated to wound-view subject");
	}

	// after together's prefix, so this write is the one that sticks.
	static void GatePrefix(Body __instance)
	{
		if (_muteHeart == null || __instance == null || !Net.IsRunning)
			return;
		if (!Util.IsInWoundView() || WoundView.view == null || WoundView.view.body == null)
			return;

		Body subject = WoundView.view.body;
		if (subject == __instance)
		{
			_muteHeart.SetValue(null, !__instance.alive);
			return;
		}

		_muteHeart.SetValue(null, true);
	}
}
