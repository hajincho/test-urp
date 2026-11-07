using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.World
{
    /// <summary>
    /// The rock / stair / wall compositions are single meshes that also contain small low-poly plants (grass
    /// tufts, reeds, spiky blades, bushes), and their whole LOD meshes are used as MeshColliders, so those plants
    /// blocked the player. The editor (CompositionPlantCollision) generates a plant-free collision copy of every
    /// such mesh and lists the pairs here. On every scene load each MeshCollider that uses a listed mesh is
    /// switched to its copy; rendering is untouched and the scene file is not modified.
    /// </summary>
    public sealed class CompositionPlantCollisionMap : ScriptableObject
    {
        public const string ResourcePath = "CompositionPlantCollisionMap";

        [Tooltip("Rule version the copies were generated with (the editor regenerates them when it changes).")]
        public int version;
        public List<Mesh> sources = new List<Mesh>();
        public List<Mesh> plantFree = new List<Mesh>();

        static Dictionary<Mesh, Mesh> lookup;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            lookup = null;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            for (int i = 0; i < SceneManager.sceneCount; i++) Apply(SceneManager.GetSceneAt(i));
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => Apply(scene);

        /// <summary>Forget the cached pairs (the editor calls this after regenerating the map).</summary>
        public static void ResetCache() => lookup = null;

        /// <summary>Switches the scene's composition colliders to their plant-free copies. Returns the count.</summary>
        public static int Apply(Scene scene)
        {
            if (!scene.isLoaded) return 0;
            if (lookup == null)
            {
                lookup = new Dictionary<Mesh, Mesh>();
                var map = Resources.Load<CompositionPlantCollisionMap>(ResourcePath);
                if (map)
                    for (int i = 0; i < Mathf.Min(map.sources.Count, map.plantFree.Count); i++)
                        if (map.sources[i] && map.plantFree[i]) lookup[map.sources[i]] = map.plantFree[i];
            }
            if (lookup.Count == 0) return 0;
            int switched = 0;
            foreach (var root in scene.GetRootGameObjects())
                foreach (var collider in root.GetComponentsInChildren<MeshCollider>(true))
                    if (collider.sharedMesh && lookup.TryGetValue(collider.sharedMesh, out var free))
                    {
                        collider.sharedMesh = free;
                        switched++;
                    }
            if (switched > 0)
                Debug.Log($"[CompositionPlantCollision] {scene.name}: {switched} composition colliders now ignore grass/plants.");
            return switched;
        }
    }
}
