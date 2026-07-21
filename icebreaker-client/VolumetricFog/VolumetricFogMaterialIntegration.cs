using UnityEngine;
using static VolumetricFogAndMist.VolumetricFog.ShaderParams;

namespace VolumetricFogAndMist {

    [ExecuteAlways, RequireComponent(typeof(VolumetricFog))]
    public class VolumetricFogMaterialIntegration : MonoBehaviour {

        enum PropertyType {
            Float,
            Vector,
            Color,
            Texture2D,
            FloatArray,
            Float4Array,
            ColorArray,
            Matrix4x4
        }

        struct Properties {
            public int id;
            public PropertyType type;
        }

        static readonly Properties[] props =  {
            new Properties { id = NoiseTex, type = PropertyType.Texture2D },
            new Properties { id = BlueNoiseTexture, type = PropertyType.Texture2D },
            new Properties { id = FogAlpha, type = PropertyType.Float },
            new Properties { id = FogColor, type = PropertyType.Color },
            new Properties { id = FogDistance, type = PropertyType.Vector },
            new Properties { id = FogData, type = PropertyType.Vector },
            new Properties { id = DeepObscurance, type = PropertyType.Float },
            new Properties { id = FogWindDir, type = PropertyType.Vector },
            new Properties { id = FogStepping, type = PropertyType.Vector },
            new Properties { id = BlurTex, type = PropertyType.Texture2D },
            new Properties { id = FogVoidPosition, type = PropertyType.Vector },
            new Properties { id = FogVoidData, type = PropertyType.Vector },
            new Properties { id = FogAreaPosition, type = PropertyType.Vector },
            new Properties { id = FogAreaData, type = PropertyType.Vector },
            new Properties { id = FogOfWarTexture, type = PropertyType.Texture2D },
            new Properties { id = FogOfWarCenter, type = PropertyType.Vector },
            new Properties { id = FogOfWarSize, type = PropertyType.Vector },
            new Properties { id = FogOfWarCenterAdjusted, type = PropertyType.Vector },
            new Properties { id = FogPointLightPosition, type = PropertyType.Float4Array },
            new Properties { id = FogPointLightColor, type = PropertyType.ColorArray },
            new Properties { id = SunPosition, type = PropertyType.Vector },
            new Properties { id = SunDir, type = PropertyType.Vector },
            new Properties { id = SunColor, type = PropertyType.Vector },
            new Properties { id = FogScatteringData, type = PropertyType.Vector },
            new Properties { id = FogScatteringData2, type = PropertyType.Vector },
            new Properties { id = FogDiffusionSpread, type = PropertyType.Float },
            new Properties { id = FogScatteringTint, type = PropertyType.Color },
            new Properties { id = GlobalSunDepthTexture, type = PropertyType.Texture2D },
            new Properties { id = GlobalSunProjection, type = PropertyType.Matrix4x4 },
            new Properties { id = GlobalSunWorldPos, type = PropertyType.Vector },
            new Properties { id = GlobalSunShadowsData, type = PropertyType.Vector },
            new Properties { id = Jitter, type = PropertyType.Float },
            new Properties { id = ClipDir, type = PropertyType.Vector }
        };

        static readonly string[] keywords = {
            "FOG_DISTANCE_ON", "FOG_AREA_SPHERE", "FOG_AREA_BOX", "FOG_VOID_SPHERE", "FOG_VOID_BOX", "FOG_OF_WAR_ON", "FOG_SCATTERING_ON", "FOG_BLUR_ON", "FOG_POINT_LIGHTS", "FOG_SUN_SHADOWS_ON"
        };

        [Tooltip("The fog renderer")]
        public VolumetricFog fog;

        [Tooltip("Assign at least one renderer in the scene using a material you wish to add the fog effect")]
        public Renderer[] materials;

        void OnEnable () {
            fog = GetComponent<VolumetricFog>();
        }


        void OnPreRender () {
            if (fog == null) return;
            Material fogMat = fog.fogMat;
            if (fogMat == null || materials == null) return;

            int materialLength = materials.Length;
            if (materialLength == 0) return;

            // sync uniforms
            int propsLength = props.Length;
            for (int k = 0; k < propsLength; k++) {
                if (!fogMat.HasProperty(props[k].id)) continue;
                switch (props[k].type) {
                    case PropertyType.Color:
                        Color color = fogMat.GetColor(props[k].id);
                        for (int m = 0; m < materialLength; m++) {
                            if (materials[m] != null && materials[m].sharedMaterial != null)
                                materials[m].sharedMaterial.SetColor(props[k].id, color);
                        }
                        break;
                    case PropertyType.ColorArray:
                        Color[] colors = fogMat.GetColorArray(props[k].id);
                        if (colors != null) {
                            for (int m = 0; m < materialLength; m++) {
                                if (materials[m] != null && materials[m].sharedMaterial != null)
                                    materials[m].sharedMaterial.SetColorArray(props[k].id, colors);
                            }
                        }
                        break;
                    case PropertyType.FloatArray:
                        float[] floats = fogMat.GetFloatArray(props[k].id);
                        if (floats != null) {
                            for (int m = 0; m < materialLength; m++) {
                                if (materials[m] != null && materials[m].sharedMaterial != null)
                                    materials[m].sharedMaterial.SetFloatArray(props[k].id, floats);
                            }
                        }
                        break;
                    case PropertyType.Float4Array:
                        Vector4[] vectors = fogMat.GetVectorArray(props[k].id);
                        if (vectors != null) {
                            for (int m = 0; m < materialLength; m++) {
                                if (materials[m] != null && materials[m].sharedMaterial != null)
                                    materials[m].sharedMaterial.SetVectorArray(props[k].id, vectors);
                            }
                        }
                        break;
                    case PropertyType.Float:
                        float f = fogMat.GetFloat(props[k].id);
                        for (int m = 0; m < materialLength; m++) {
                            if (materials[m] != null && materials[m].sharedMaterial != null)
                                materials[m].sharedMaterial.SetFloat(props[k].id, f);
                        }
                        break;
                    case PropertyType.Vector:
                        Vector4 v = fogMat.GetVector(props[k].id);
                        for (int m = 0; m < materialLength; m++) {
                            if (materials[m] != null && materials[m].sharedMaterial != null)
                                materials[m].sharedMaterial.SetVector(props[k].id, v);
                        }
                        break;
                    case PropertyType.Matrix4x4:
                        Matrix4x4 matrix = fogMat.GetMatrix(props[k].id);
                        for (int m = 0; m < materialLength; m++) {
                            if (materials[m] != null && materials[m].sharedMaterial != null)
                                materials[m].sharedMaterial.SetMatrix(props[k].id, matrix);
                        }
                        break;
                    case PropertyType.Texture2D:
                        Texture tex = fogMat.GetTexture(props[k].id);
                        for (int m = 0; m < materialLength; m++) {
                            if (materials[m] != null && materials[m].sharedMaterial != null)
                                materials[m].sharedMaterial.SetTexture(props[k].id, tex);
                        }
                        break;
                }
            }

            // sync shader keywords
            int keywordsLength = keywords.Length;
            for (int k = 0; k < keywordsLength; k++) {
                if (fogMat.IsKeywordEnabled(keywords[k])) {
                    for (int m = 0; m < materialLength; m++) {
                        if (materials[m] != null && materials[m].sharedMaterial != null)
                            materials[m].sharedMaterial.EnableKeyword(keywords[k]);
                    }
                } else {
                    for (int m = 0; m < materialLength; m++) {
                        if (materials[m] != null && materials[m].sharedMaterial != null)
                            materials[m].sharedMaterial.DisableKeyword(keywords[k]);
                    }
                }

            }

        }
    }

}