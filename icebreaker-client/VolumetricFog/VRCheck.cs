// gutted for the EFT embed — no VR in tarkov, and the XR module chain pulled 3
// more unity assemblies into the plugin for a check that always lands false
namespace VolumetricFogAndMist
{
    static class VRCheck
    {
        public static bool isActive;
        public static bool isVrRunning;

        public static void Init()
        {
            isActive = false;
            isVrRunning = false;
        }
    }
}
