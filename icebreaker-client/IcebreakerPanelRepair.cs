using System;
using System.Collections.Generic;
using System.IO;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using EFT.Quests;
using HarmonyLib;
using SPT.Reflection.Patching;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Audio;

namespace Manimal.Icebreaker
{
    // Wiring the Vessel's three breaker panels. retail 1.1.5 drives each one through a
    // trigger graph on the panel's Logic object: Switch (Open -> Try_Repair) ->
    // HandlerRequirementsWithFailedTrigger [toolkit 590c2e11..] -> HandlerPlantAction
    // 6.4s (multitool plant, "Repairing objective") -> HandlerAnimator bool 'Repair',
    // the four Flicker_Red_Light_* off, electric_shield_repair_loop/done, and
    // HandlerCompleteQuestCondition. the rip kept Switch, Animator and the lights but
    // dropped every handler, and half of them dont exist in this client anyway, so the
    // same sequence is rebuilt on the three switches at raid start. the quest step is
    // the ported VisitPlace condition, completed through the TriggerVisited counter.
    internal static class IcebreakerPanelRepair
    {
        internal const string ToolkitTpl = "590c2e1186f77425357b6124";
        internal const string QuestId = "6aa83f652bec3212bf930002";
        internal const float PlantSeconds = 6.4f;
        private const string ResourcePrefix = "Manimal.Icebreaker.PanelRepair.";

        internal sealed class Panel
        {
            internal string SwitchId, Target, ConditionId, Room;
        }

        // switch Id -> VisitPlace target / ported condition. pairing read off each room's
        // HandlerCompleteQuestCondition in retail level704, not guessed from geometry
        internal static readonly Panel[] Panels =
        {
            new Panel { SwitchId = "switch_Icebreaker_Design_Stuff_00004", Target = "fix_element_one", ConditionId = "6aa83f652bec3212bf93002c", Room = "Room_01" },
            new Panel { SwitchId = "switch_Icebreaker_Design_Stuff_00003", Target = "fix_element_02", ConditionId = "6aa83f652bec3212bf93002d", Room = "Room_02" },
            new Panel { SwitchId = "switch_Icebreaker_Design_Stuff_00002", Target = "fix_element_03", ConditionId = "6aa83f652bec3212bf93002e", Room = "Room_03" },
        };

        private static AudioClip _loop, _done;
        private static bool _clipsFailed;

        internal static void ResetForRaid()
        {
            // clips are raid-independent; components die with the scene
        }

        internal static void Restore()
        {
            try
            {
                int bound = 0;
                foreach (var sw in UnityEngine.Object.FindObjectsOfType<Switch>(true))
                {
                    if (sw == null || !sw.gameObject.scene.name.StartsWith("Icebreaker", StringComparison.Ordinal)) continue;
                    var panel = Find(sw.Id);
                    if (panel == null || sw.GetComponent<PanelRepair>() != null) continue;
                    sw.gameObject.AddComponent<PanelRepair>().Bind(sw, panel);
                    bound++;
                }
                if (bound == Panels.Length) Plugin.Log.LogInfo($"[PanelRepair] {bound} breaker panels wired (retail trigger graph rebuilt)");
                else Plugin.Log.LogWarning($"[PanelRepair] expected {Panels.Length} breaker switches, bound {bound}");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[PanelRepair] restore failed: {e.Message}"); }
        }

        private static Panel Find(string switchId)
        {
            if (string.IsNullOrEmpty(switchId)) return null;
            foreach (var p in Panels) if (p.SwitchId == switchId) return p;
            return null;
        }

        internal static bool HasToolkit(Player player)
        {
            if (player?.Profile?.Inventory == null) return false;
            foreach (var it in player.Profile.Inventory.AllRealPlayerItems)
                if (it != null && it.TemplateId == ToolkitTpl) return true;
            return false;
        }

        internal static QuestDataClass FindQuest(Player player)
        {
            var quests = player?.Profile?.QuestsData;
            if (quests == null) return null;
            foreach (var q in quests) if (q != null && q.Id == QuestId) return q;
            return null;
        }

        // retail PCM16 embedded like the door blizzard loop (Resources/PanelRepair)
        internal static bool TryGetClips(out AudioClip loop, out AudioClip done)
        {
            loop = _loop; done = _done;
            if (loop != null && done != null) return true;
            if (_clipsFailed) return false;
            try
            {
                var assembly = typeof(IcebreakerPanelRepair).Assembly;
                JObject data;
                using (var stream = assembly.GetManifestResourceStream(ResourcePrefix + "manifest.json"))
                using (var reader = new StreamReader(stream ?? throw new InvalidDataException("Missing panel audio manifest")))
                    data = JObject.Parse(reader.ReadToEnd());
                if (data.Value<int>("formatVersion") != 1) throw new InvalidDataException("Unknown panel audio format");
                int channels = data.Value<int>("channels"), frequency = data.Value<int>("frequency");
                var clips = (JObject)data["clips"];
                _loop = LoadClip(assembly, (JObject)clips["loop"], channels, frequency);
                _done = LoadClip(assembly, (JObject)clips["done"], channels, frequency);
                loop = _loop; done = _done;
                return true;
            }
            catch (Exception e)
            {
                _clipsFailed = true;
                Plugin.Log.LogWarning($"[PanelRepair] repair audio unavailable: {e.Message}");
                return false;
            }
        }

        private static AudioClip LoadClip(System.Reflection.Assembly assembly, JObject entry, int channels, int frequency)
        {
            using (var stream = assembly.GetManifestResourceStream(ResourcePrefix + entry.Value<string>("file")))
            using (var reader = new BinaryReader(stream ?? throw new InvalidDataException("Missing panel audio samples")))
            {
                if (channels < 1 || channels > 2 || frequency < 8000 || frequency > 96000 ||
                    stream.Length == 0 || stream.Length % (2 * channels) != 0)
                    throw new InvalidDataException("Invalid PCM16 audio dimensions");
                var samples = new float[checked((int)(stream.Length / 2))];
                for (int i = 0; i < samples.Length; i++) samples[i] = reader.ReadInt16() / 32768f;
                var clip = AudioClip.Create(entry.Value<string>("clip"), samples.Length / channels, channels, frequency, false);
                if (!clip.SetData(samples, 0)) throw new InvalidDataException("AudioClip rejected panel samples");
                return clip;
            }
        }
    }

    internal sealed class PanelRepair : MonoBehaviour
    {
        internal IcebreakerPanelRepair.Panel Panel;
        internal Switch Switch;
        internal bool Repaired, Repairing;

        private Animator _anim;
        private readonly List<GameObject> _redLights = new List<GameObject>();
        private GamePlayerOwner _owner;
        private BetterSource _loopSource;
        private AudioMixerGroup _mixer;

        internal void Bind(Switch sw, IcebreakerPanelRepair.Panel panel)
        {
            Switch = sw;
            Panel = panel;
            _anim = sw.GetComponentInParent<Animator>();
            var light = sw.transform.Find("Light");
            if (light != null)
                for (int i = 0; i < light.childCount; i++)
                {
                    var child = light.GetChild(i);
                    if (child.name.StartsWith("Flicker_Red_Light", StringComparison.Ordinal)) _redLights.Add(child.gameObject);
                }
            if (_anim == null || _redLights.Count == 0)
                Plugin.Log.LogWarning($"[PanelRepair] {panel.Room}: animator={_anim != null} redLights={_redLights.Count} — visuals incomplete");
            // a step already turned in shows the repaired panel; retail never restored
            // this but the ported condition state is right there in the profile
            var quest = IcebreakerPanelRepair.FindQuest(Singleton<GameWorld>.Instance?.MainPlayer);
            if (quest?.CompletedConditions != null && quest.CompletedConditions.Contains(panel.ConditionId))
            {
                Repaired = true;
                SetVisual(true);
            }
        }

        // retail LocalDisableIfNoQuest: the panel only answers while the quest is Started
        internal bool QuestStarted
        {
            get
            {
                var quest = IcebreakerPanelRepair.FindQuest(Singleton<GameWorld>.Instance?.MainPlayer);
                return quest != null && quest.Status == EQuestStatus.Started;
            }
        }

        internal void BeginRepair(GamePlayerOwner owner)
        {
            if (Repaired || Repairing) return;
            var player = owner?.Player;
            if (player == null) return;
            if (!IcebreakerPanelRepair.HasToolkit(player))
            {
                EFT.Communications.NotificationManager.DisplayMessageNotification("Requires toolkit");
                return;
            }
            if (!(player.CurrentState is IdlePlayerState))
            {
                owner.DisplayPreloaderUiNotification("You can't plant quest item while moving".Localized(null));
                return;
            }
            _owner = owner;
            Repairing = true;
            owner.ShowObjectivesPanel("Repairing objective {0:F1}", IcebreakerPanelRepair.PlantSeconds);
            SetVisual(true);
            PlayLoop();
            // the native multitool plant state: same animation, hold and cancel rules the
            // retail HandlerPlantAction rode on
            player.CurrentManagedState.Plant(true, true, IcebreakerPanelRepair.PlantSeconds, OnPlantDone);
        }

        private void OnPlantDone(bool successful)
        {
            Repairing = false;
            try { _owner?.CloseObjectivesPanel(); } catch { }
            StopLoop();
            if (!successful)
            {
                SetVisual(false); // Drop_Repair: lights back to broken
                return;
            }
            Repaired = true;
            PlayDone();
            try
            {
                var player = _owner?.Player ?? Singleton<GameWorld>.Instance?.MainPlayer;
                player?.SpecialPlaceVisited(Panel.Target, 0);
                _owner?.ClearInteractionState();
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[PanelRepair] {Panel.Room}: condition hand-off threw: {e.Message}"); }
            Plugin.Log.LogInfo($"[PanelRepair] {Panel.Room} repaired -> {Panel.Target}");
        }

        private void SetVisual(bool repaired)
        {
            try
            {
                if (_anim) _anim.SetBool("Repair", repaired);
                foreach (var go in _redLights) if (go) go.SetActive(!repaired);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[PanelRepair] {Panel.Room}: visual state threw: {e.Message}"); }
        }

        private AudioMixerGroup Mixer(BetterAudio audio)
        {
            if (_mixer != null || audio.Master == null) return _mixer;
            foreach (var g in audio.Master.FindMatchingGroups("TechnicalSounds"))
                if (g != null && g.name == "TechnicalSounds") { _mixer = g; break; }
            return _mixer;
        }

        // authored: group InteractiveObjects, rolloff 15, gain 0.9, ContinuousPropagated,
        // TechnicalSounds mixer. the loop clip is 6.5s played once and cut on a drop
        private void PlayLoop()
        {
            if (!FikaBridge.CanRender || !IcebreakerPanelRepair.TryGetClips(out var loop, out _)) return;
            var audio = MonoBehaviourSingleton<BetterAudio>.Instance;
            if (audio == null) return;
            StopLoop();
            if (!audio.TryPlayAtPoint(out _loopSource, transform.position, loop, BetterAudio.AudioSourceGroupType.InteractiveObjects,
                15, 0.9f, EOcclusionTest.ContinuousPropagated, Mixer(audio),
                spatialize: true, oneShot: false, autoReleaseSource: false, enabledHighPassFilter: false)) return;
            _loopSource.Loop = false;
            _loopSource.SetPitch(1f);
            _loopSource.SetBaseVolume(0.9f);
        }

        private void StopLoop()
        {
            var source = _loopSource;
            _loopSource = null;
            if (source == null) return;
            try { source.Stop(0f); source.Release(); }
            catch (Exception e) { Plugin.Log.LogWarning($"[PanelRepair] loop release threw: {e.Message}"); }
        }

        private void PlayDone()
        {
            if (!FikaBridge.CanRender || !IcebreakerPanelRepair.TryGetClips(out _, out var done)) return;
            var audio = MonoBehaviourSingleton<BetterAudio>.Instance;
            if (audio == null) return;
            if (audio.TryPlayAtPoint(out var source, transform.position, done, BetterAudio.AudioSourceGroupType.InteractiveObjects,
                15, 0.9f, EOcclusionTest.ContinuousPropagated, Mixer(audio),
                spatialize: true, oneShot: true, autoReleaseSource: true, enabledHighPassFilter: false))
                source.SetBaseVolume(0.9f);
        }

        private void OnDestroy()
        {
            StopLoop();
            if (Repairing && _owner != null) try { _owner.CloseObjectivesPanel(); } catch { }
        }
    }

    // own the three panel switches' action menus, the chain-door way: vanilla would name
    // the action from the ripped-empty ContextMenuTip and flip the switch state on use.
    [IcebreakerPatchTarget(typeof(InteractionContextHelper), "GetAvailableActions", typeof(GamePlayerOwner), typeof(Switch))]
    internal sealed class Patch_PanelRepairSwitches : IcebreakerModulePatch<Patch_PanelRepairSwitches>
    {
        [PatchPostfix]
        private static void Postfix(ref EFT.UI.AvailableInteractionState __result, GamePlayerOwner owner, Switch interactiveSwitch)
        {
            try
            {
                if (interactiveSwitch == null) return;
                var panel = interactiveSwitch.GetComponent<PanelRepair>();
                if (panel == null) return;
                if (panel.Repaired || panel.Repairing || !panel.QuestStarted)
                {
                    if (__result != null) __result.Actions.Clear();
                    __result = null;
                    return;
                }
                var act = new EFT.UI.InteractionAction { Name = "Use", Action = () => panel.BeginRepair(owner) };
                if (__result == null) __result = new EFT.UI.AvailableInteractionState { Actions = new List<EFT.UI.InteractionAction> { act } };
                else { __result.Actions.Clear(); __result.Actions.Add(act); }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[PanelRepair] actions patch threw: {e.Message}"); }
        }
    }
}
