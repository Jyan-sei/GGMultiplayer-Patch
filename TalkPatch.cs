using BepInEx.Bootstrap;
using CasualtiesTogetherUtils;
using LiteEntitySystem;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour.HookGen;
using System;
using System.Collections.Generic;
using System.Text;
using Together;

namespace GunsawGenetics
{
    public static class TalkPatch
    {
        public delegate void orig_Talk(Talker self, List<string> lines, Limb limb = null, bool force = false, bool resetTalkTimer = false);
        public delegate List<string> orig_GetCharacter(string str, int chr);
        public static void Initialize()
        {
            HookEndpointManager.Add(((orig_GetCharacter)Locale.GetCharacter).Method, On_GetCharacter);
            HookEndpointManager.Modify(((Delegate)SpeciesDialogueSubstitutionPatch.Prefix).Method, IL_Clear);
            if (Chainloader.PluginInfos.ContainsKey("CasualtiesMP"))
            {
                try
                {
                    initializeMP();
                }
                catch (System.IO.FileNotFoundException) { }
                catch (Exception e)
                {
                    MPBridgePlugin.Log.LogError("Error hooking Talker:\n" + e);
                }
            }

            MPBridgePlugin.Log.LogInfo("[GG-MP] Talk Patch initialized");
        }

        public static void initializeMP()
        {
            HookEndpointManager.Add(typeof(Talker).GetMethod("Talk", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                    null, new Type[] { typeof(List<string>), typeof(Limb), typeof(bool), typeof(bool) }, null), On_Talk);
        }

        public static List<string> On_GetCharacter(orig_GetCharacter orig, string str, int chr)
        {
            return new LocaleList<string>(str, orig(str, chr));
        }

        public static void On_Talk(orig_Talk orig, Talker self, List<string> lines, Limb limb = null, bool force = false, bool resetTalkTimer = false)
        {
            if (lines is not LocaleList<string> localeList)
            {
                orig(self, lines, limb, force, resetTalkTimer);
                return;
            }

            int traderChar = 0;
            Body player = null;
            if (self.trader)
            {
                traderChar = self.trader.character;
                TrackerTrader tracker = self.trader.GetComponent<TrackerTrader>();
                if (tracker)
                {
                    player = tracker.focused_body;
                }
            }
            player ??= self.body ?? SpeciesAbilityManager.PlayerBody;

            if (!player)
            {
                orig(self, lines, limb, force, resetTalkTimer);
                return;
            }

            if (player.IsBodyLocal() || !(player.TryGetNetBody(out var nb) && SpeciesMultiplayerSync.knownSpeciesNames.TryGetValue(nb.netId, out string speciesName)))
            {
                speciesName = SpeciesAbilityManager.ActiveSpecies;
            }

            List<string> dialogue = GGLocalization($"dialogue_{speciesName.ToLowerInvariant()}{(traderChar > 0 ? $"_chr{self.trader.character}_" : "_")}{localeList.key}");
            if (dialogue.Count > 0)
            {
                orig(self, dialogue, limb, force, resetTalkTimer);
                return;
            }
            else
            {
                orig(self, lines, limb, force, resetTalkTimer);
                return;
            }
        }

        public static List<string> GGLocalization(string key)
        {
            List<string> res = new();
            int index = 1;
            while (true)
            {
                if (Localization.Strings.TryGetValue(key + index, out var value))
                {
                    res.Add(value);
                    index++;
                }
                else
                {
                    break;
                }
            }
            return res;
        }

        // performance issues
        public static void IL_Clear(ILContext il)
        {
            try
            {
                ILCursor c = new(il);
                c.Emit(OpCodes.Nop);
                c.Emit(OpCodes.Ret);
            }
            catch (Exception e)
            {
                MPBridgePlugin.Log?.LogError(e);
            }
        }

        public class LocaleList<T> : List<T>
        {
            public T key;

            public LocaleList(T key)
            {
                this.key = key;
            }

            public LocaleList(T key, int capacity) : base(capacity)
            {
                this.key = key;
            }

            public LocaleList(T key, IEnumerable<T> collection) : base(collection)
            {
                this.key = key;
            }
        }
    }
}
