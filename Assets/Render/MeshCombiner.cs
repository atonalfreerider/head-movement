using System;
using System.Collections.Generic;
using Shapes;
using UnityEngine;

namespace Render
{
    public class MeshCombiner : MonoBehaviour
    {
        MeshFilter[] individualMeshFilters = Array.Empty<MeshFilter>();
        Transform referenceParent;
        Color polyColor;

        MeshFilter[] CombineMeshFilters = Array.Empty<MeshFilter>();

        public void Init(
            MeshFilter[] individualMeshFilters, 
            Transform referenceParent,
            Color polyColor)
        {
            this.individualMeshFilters = individualMeshFilters;
            this.referenceParent = referenceParent;
            this.polyColor = polyColor;
        }

        public MeshFilter[] RecreateCombines()
        {
            List<CombineInstance[]> combines =
                GroupMeshesForCombining(individualMeshFilters, referenceParent);

            if (CombineMeshFilters.Length == 0)
            {
                CombineMeshFilters =
                    CreateAndAttachCombinesToGameObjects(combines);
            }
            else
            {
                // This happens under the assumption that the number of vertices of the meshes that are being combined
                // does not change from when the mesh filters were originally created. For example, we may want to
                // recombine when the individual mesh positional information has changed, but the meshes are identical
                // in shape and size. This lets us reuse existing game objects.
                for (int i = 0; i < CombineMeshFilters.Length; i++)
                {
                    CombineMeshFilters[i].sharedMesh.CombineMeshes(combines[i]);
                }
            }

            return CombineMeshFilters;
        }

        static List<CombineInstance[]> GroupMeshesForCombining(
            IEnumerable<MeshFilter> meshes,
            Transform parentTransform)
        {
            // In Unity, a single mesh cannot have more than 65,534 vertices. There doesn't seem to be any official
            // documentation on the mesh vertex limit in Unity, but there is some knowledge floating around about it on
            // the internet.
            //
            // This is why even when combining meshes, we still may need more than a single mesh for the result.
            //
            // See: https://answers.unity.com/questions/255405/vertex-limit.html
            
            
            // For mobile, the limit per mesh must be even lower (even though it is technically possible to have the 
            // 655534 limit. This is because mobile units have less room in RAM for storing large meshes.
            
            // See: https://docs.unity3d.com/Manual/ModelingOptimizedCharacters.html

#if UNITY_ANDROID
            const int maxVertexCount = 3000;
#else
            const int maxVertexCount = 65534;
#endif
            
            int vertexCount = 0;

            List<CombineInstance[]> combines = new List<CombineInstance[]>();
            List<CombineInstance> combine = new List<CombineInstance>();

            foreach (MeshFilter meshFilter in meshes)
            {
                if (meshFilter == null || meshFilter.sharedMesh == null) continue;
                
                vertexCount += meshFilter.sharedMesh.vertexCount;

                if (vertexCount > maxVertexCount)
                {
                    vertexCount = meshFilter.sharedMesh.vertexCount;
                    combines.Add(combine.ToArray());
                    combine = new List<CombineInstance>();
                }

                combine.Add(
                    new CombineInstance
                    {
                        mesh = meshFilter.sharedMesh,
                        transform = parentTransform.worldToLocalMatrix *
                                    meshFilter.transform.localToWorldMatrix
                    });
            }

            combines.Add(combine.ToArray());

            return combines;
        }

        MeshFilter[] CreateAndAttachCombinesToGameObjects(
            IReadOnlyList<CombineInstance[]> combinedMeshes)
        {
            MeshFilter[] combineMeshFilters = new MeshFilter[combinedMeshes.Count];

            for (int i = 0; i < combineMeshFilters.Length; i++)
            {
                // Create a game object with a mesh filter for each combine and attach it to the designated parent transform.
                Polygon poly = PolygonFactory.Instance
                    .PolygonPool
                    .PolygonFromPool();
                poly.SetColor(polyColor);
                poly.transform.SetParent(referenceParent, false);
                combineMeshFilters[i] =
                    poly.meshFilter;
                combineMeshFilters[i].sharedMesh.CombineMeshes(combinedMeshes[i]);
            }

            return combineMeshFilters;
        }

        public void SetDisplayStateCombinesAndIndividuals(bool showCombines, bool showIndividuals)
        {
            foreach (MeshFilter combineMeshFilter in CombineMeshFilters)
            {
                combineMeshFilter.gameObject.SetActive(showCombines);
            }
            
            foreach (MeshFilter individualMesh in individualMeshFilters)
            {
                if (individualMesh)
                {
                    individualMesh.gameObject.SetActive(showIndividuals);
                }
            }
        }
    }
}