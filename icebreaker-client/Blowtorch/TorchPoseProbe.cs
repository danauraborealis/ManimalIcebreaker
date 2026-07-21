using System.Reflection;
using System.Text;
using Comfort.Common;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace Manimal.Icebreaker.Blowtorch
{
    // TP-pose debug probe (temporary): F10 dumps the BODY animator's params + every
    // layer's state hash/weight for whatever is currently in hands. capture once with
    // the vanilla rangefinder (correct TP pose) and once with the torch (broken) —
    // the diff names the layer/param that actually differs, ending the guesswork.
    public class TorchPoseProbe : MonoBehaviour
    {
        [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.OnGameStarted))]
        private static class Patch_SpawnProbe
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                new GameObject("Icebreaker_TorchPoseProbe").AddComponent<TorchPoseProbe>();
            }
        }

        private void Update()
        {
            if (!Plugin.DiagHotkeys.Value || !Input.GetKeyDown(KeyCode.F10)) return;
            var player = Singleton<GameWorld>.Instance?.MainPlayer;
            if (player == null) return;
            try
            {
                Dump(player);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError($"[PoseProbe] dump failed: {e}");
            }
        }

        private static void Dump(Player player)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[PoseProbe] hands={player.HandsController?.GetType().Name} item={player.HandsController?.Item?.TemplateId}");

            DumpAnimator(sb, "BODY", Unwrap(player.BodyAnimatorCommon));
            DumpAnimator(sb, "ARMS", Unwrap(player.ArmsAnimatorCommon));
            Plugin.Log.LogWarning(sb.ToString());
        }

        // IAnimator wrappers hide the UnityEngine.Animator — fish it out by type
        private static Animator Unwrap(object ianimator)
        {
            if (ianimator == null) return null;
            if (ianimator is Animator a) return a;
            var t = ianimator.GetType();
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                if (typeof(Animator).IsAssignableFrom(p.PropertyType))
                    return p.GetValue(ianimator) as Animator;
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                if (typeof(Animator).IsAssignableFrom(f.FieldType))
                    return f.GetValue(ianimator) as Animator;
            return null;
        }

        private static void DumpAnimator(StringBuilder sb, string tag, Animator anim)
        {
            if (anim == null)
            {
                sb.AppendLine($"  {tag}: <no raw Animator found>");
                return;
            }
            sb.AppendLine($"  {tag}: go='{anim.gameObject.name}' controller='{anim.runtimeAnimatorController?.name}' enabled={anim.enabled} speed={anim.speed}");
            for (int i = 0; i < anim.layerCount; i++)
            {
                var info = anim.GetCurrentAnimatorStateInfo(i);
                sb.AppendLine($"    L{i} '{anim.GetLayerName(i)}' w={anim.GetLayerWeight(i):0.00} state={info.shortNameHash} nt={info.normalizedTime:0.00}");
            }
            foreach (var p in anim.parameters)
            {
                string v = p.type switch
                {
                    AnimatorControllerParameterType.Float => anim.GetFloat(p.nameHash).ToString("0.###"),
                    AnimatorControllerParameterType.Int => anim.GetInteger(p.nameHash).ToString(),
                    AnimatorControllerParameterType.Bool => anim.GetBool(p.nameHash).ToString(),
                    _ => "trig",
                };
                sb.AppendLine($"    P {p.name}={v}");
            }
        }
    }
}
