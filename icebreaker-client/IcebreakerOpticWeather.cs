using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace Manimal.Icebreaker
{
    // Native OpticComponentUpdater only copies EFT's effects. Our replacement fog
    // needs a renderer on this camera too. Snow is drawn explicitly so the optic's
    // culling mask/occlusion cannot discard the map's procedural flake meshes.
    internal sealed class IcebreakerOpticWeather : MonoBehaviour
    {
        private Camera _camera;
        private Behaviour _globalFog;
        private FieldInfo[] _fogFields;
        private SnowFlakes _source;
        private MeshRenderer[] _flakes;
        private bool[] _restore;
        private CommandBuffer _snowPass;
        private bool _loggedSnow, _failed;

        internal void Sync()
        {
            if (!FikaBridge.CanRender) return;
            if (_failed) return;
            try
            {
                if (_camera == null) _camera = GetComponent<Camera>();
                IcebreakerVolFog.SyncOptic(_camera);
                var source = IcebreakerWeather.GlobalFogSource;
                if (source == null) return;
                if (_globalFog == null)
                {
                    var type = source.GetType();
                    _globalFog = (GetComponent(type) ?? gameObject.AddComponent(type)) as Behaviour;
                    var names = new[] { "fogShader", "distanceFog", "useRadialDistance", "heightFog", "startDistance" };
                    _fogFields = Array.ConvertAll(names, n => AccessTools.Field(type, n));
                }
                foreach (var field in _fogFields)
                    if (field != null) field.SetValue(_globalFog, field.GetValue(source));
                _globalFog.enabled = source.enabled;
            }
            catch (Exception e)
            {
                _failed = true;
                Plugin.Log.LogError("[OpticWeather] camera setup failed: " + e);
            }
        }

        private void OnPreCull()
        {
            // Defensive restore also covers a previous render that was interrupted.
            RestoreSnow();
            _snowPass?.Clear();
            if (!IceGate.On)
            {
                if (_globalFog != null) _globalFog.enabled = false;
                var fog = GetComponent<VolumetricFogAndMist.VolumetricFog>();
                if (fog != null) fog.enabled = false;
                return;
            }
            if (_failed) return;
            var source = IcebreakerWeather.SnowSource;
            if (source == null || !source.isActiveAndEnabled) return;
            if (_source != source || _flakes == null || _flakes.Length == 0)
            {
                _source = source;
                _flakes = source.GetComponentsInChildren<MeshRenderer>();
                _restore = new bool[_flakes.Length];
            }
            if (_snowPass == null)
            {
                _camera = GetComponent<Camera>();
                _snowPass = new CommandBuffer { name = "Icebreaker optic snow" };
                _camera.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, _snowPass);
            }
            int drawn = 0;
            for (int i = 0; i < _flakes.Length; i++)
            {
                var flake = _flakes[i];
                if (flake == null || !flake.enabled || !flake.gameObject.activeInHierarchy) continue;
                var material = flake.sharedMaterial;
                if (material == null) continue;
                // Suppress its automatic draw for THIS render only, avoiding double
                // density on scopes whose native mask already includes snow.
                _restore[i] = true;
                flake.enabled = false;
                _snowPass.DrawRenderer(flake, material, 0, 0);
                drawn++;
            }
            if (!_loggedSnow && drawn > 0)
            {
                _loggedSnow = true;
                Plugin.Log.LogInfo($"[OpticWeather] {drawn} snow meshes bound to '{_camera.name}', FOV={_camera.fieldOfView:0.##}");
            }
        }

        private void OnPostRender() => RestoreSnow();

        private void RestoreSnow()
        {
            if (_restore == null) return;
            for (int i = 0; i < _restore.Length; i++)
            {
                if (!_restore[i]) continue;
                if (_flakes[i] != null) _flakes[i].enabled = true;
                _restore[i] = false;
            }
        }

        private void OnDisable()
        {
            RestoreSnow();
            if (_snowPass == null) return;
            if (_camera != null) _camera.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, _snowPass);
            _snowPass.Release();
            _snowPass = null;
        }
    }
}
