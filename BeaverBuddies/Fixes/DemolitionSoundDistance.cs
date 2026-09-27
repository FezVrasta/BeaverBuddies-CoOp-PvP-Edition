using HarmonyLib;
using Timberborn.Coordinates;
using Timberborn.DeconstructionSystem;
using Timberborn.DeconstructionSystemUI;
using Timberborn.SoundSystem;
using UnityEngine;

namespace BeaverBuddies.Fixes
{
    /**
     * The game plays the demolition sound as a UI sound, at full volume
     * wherever the building was, so you hear the other player demolishing
     * things across the map. Near the middle of the screen it still plays
     * as before, a bit further out it plays from the building so it fades
     * with distance, and far away it doesn't play at all.
     */
    [ManualMethodOverwrite]
    /*
     * 09/27/2026 (Timberborn 1.1)
        public void UpdateSingleton()
        {
            if (_shouldPlaySound)
            {
                _shouldPlaySound = false;
                _uiSoundController.PlaySound(DeconstructionSoundName);
            }
        }
     */
    [HarmonyPatch(typeof(DeconstructionSoundPlayer), nameof(DeconstructionSoundPlayer.UpdateSingleton))]
    class DeconstructionSoundPlayerUpdatePatcher
    {
        // In tiles, from the point in the middle of the screen
        private const float FullVolumeDistance = 15f;
        private const float MaxDistance = 40f;

        // The nearest building demolished since the last frame
        public static float? NearestDistance;
        public static Vector3 NearestPosition;
        private static GameObject _emitter;

        static bool Prefix(DeconstructionSoundPlayer __instance)
        {
            if (!__instance._shouldPlaySound) return false;
            __instance._shouldPlaySound = false;
            float? distance = NearestDistance;
            NearestDistance = null;

            if (distance == null || distance <= FullVolumeDistance)
            {
                __instance._uiSoundController.PlaySound(DeconstructionSoundPlayer.DeconstructionSoundName);
            }
            else if (distance <= MaxDistance)
            {
                if (!_emitter) _emitter = new GameObject("BeaverBuddies.DemolitionSound");
                _emitter.transform.position = NearestPosition;
                __instance._uiSoundController._soundSystem.PlaySound3D(_emitter, DeconstructionSoundPlayer.DeconstructionSoundName, 10);
            }
            return false;
        }

        public static void Record(Vector3 position, ISoundSystem soundSystem)
        {
            Vector3 listener = soundSystem.ListenerPosition;
            float distance = new Vector2(position.x - listener.x, position.z - listener.z).magnitude;
            if (NearestDistance == null || distance < NearestDistance)
            {
                NearestDistance = distance;
                NearestPosition = position;
            }
        }
    }

    [HarmonyPatch(typeof(DeconstructionSoundPlayer), nameof(DeconstructionSoundPlayer.OnBuildingDeconstructed))]
    class DeconstructionSoundPlayerRecordPatcher
    {
        static void Postfix(DeconstructionSoundPlayer __instance, BuildingDeconstructedEvent buildingDeconstructedEvent)
        {
            var coordinates = buildingDeconstructedEvent.Coordinates;
            if (coordinates.Count == 0) return;
            Vector3 sum = Vector3.zero;
            foreach (Vector3Int c in coordinates) sum += c;
            Vector3 center = CoordinateSystem.GridToWorld(sum / coordinates.Count + new Vector3(0.5f, 0.5f, 0));
            DeconstructionSoundPlayerUpdatePatcher.Record(center, __instance._uiSoundController._soundSystem);
        }
    }
}
