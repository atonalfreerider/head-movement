using Shapes.Lines;
using UnityEngine;

namespace Shapes
{
    public class PolygonFactory : MonoBehaviour
    {
        public static PolygonFactory Instance;
        public PolygonPool PolygonPool;
        public Material mainMat;
        public Material transMat;
        public Material textMat;

        // regular polygons
        [HideInInspector] public Polygon tetra, icosahedron0, tri, hex;
        
        void Awake()
        {
            Instance = this;
            BuildPolygons();
            StaticLink.InitStaticLink(this);
            Circle.NewCylinder.Init(this);
            NewCube.InitCube(this);
        }

        // INIT
        void BuildPolygons()
        {
            tetra = NewPoly(mainMat);
            Polyhedra.VertsAndFaces tetraVertsAndFaces = Polyhedra.NewTetraVertsAndFaces(1);

            tetra.Draw3DPoly(tetraVertsAndFaces.verts, Polyhedra.IndicesFromTris(tetraVertsAndFaces.faces));
            tetra.name = "tetrahedron";
            tetra.SetColor(Color.white);
            tetra.transform.SetParent(transform, false);
            tetra.gameObject.SetActive(false);
            
            hex = NewPoly(mainMat);
            hex.DrawRegPoly(1, 6, Mathf.PI / 6f, 1, 0);
            hex.name = "hexagon";
            hex.SetColor(Color.white);
            hex.transform.SetParent(transform, false);
            hex.gameObject.SetActive(false);
            
            icosahedron0 = NewPoly(mainMat);
            Polyhedra.VertsAndFaces ivaf = Polyhedra.NewIcoVertsAndFaces(1, 0);
            Polyhedra.NewPolyhedron(icosahedron0, ivaf.verts, ivaf.faces, false);
            icosahedron0.name = "icosahedron0";
            icosahedron0.SetColor(Color.white);
            icosahedron0.transform.SetParent(transform, false);
            icosahedron0.gameObject.SetActive(false);
            
            // regular polygons
            tri = NewPoly(mainMat);
            tri.DrawRegPoly(1, 3, 0, 1, 0);
            tri.name = "triangle";
            tri.SetColor(Color.white);
            tri.transform.SetParent(transform, false);
            tri.gameObject.SetActive(false);
        }

        public static Polygon NewPoly(Material passMat)
        {
            Polygon newPoly = new GameObject("Polygon").AddComponent<Polygon>();
            AddMesh(newPoly.gameObject, newPoly, passMat);
            newPoly.rend = newPoly.gameObject.GetComponent<Renderer>();

            return newPoly;
        }
        
        public static Circle NewCirclePoly(Material passMat)
        {
            Circle newPoly = new GameObject("CirclePolygon").AddComponent<Circle>();
            AddMesh(newPoly.gameObject, newPoly, passMat);
            newPoly.rend = newPoly.gameObject.GetComponent<Renderer>();

            return newPoly;
        }

        public static Rectangle NewRectPoly(Material passMat)
        {
            Rectangle newPoly = new GameObject("RectPolygon").AddComponent<Rectangle>();
            AddMesh(newPoly.gameObject, newPoly, passMat);
            newPoly.rend = newPoly.gameObject.GetComponent<Renderer>();

            return newPoly;
        }

        public static Polygon DrawTri(float h, float b, Color passColor)
        {
            Polygon newTri = new GameObject("TrianglePolygon").AddComponent<Polygon>();
            AddMesh(newTri.gameObject, newTri, Instance.mainMat);
            newTri.rend = newTri.GetComponent<Renderer>();
            Vector3[] skinList =
            {
                new(0, 0, h),
                new(b * .5f, 0, 0),
                new(-b * .5f, 0, 0)
            };

            int[] indList = {0, 1, 2};

            newTri.Draw3DPoly(skinList, indList);
            newTri.SetColor(passColor);
            return newTri;
        }

        public static void AddMesh(GameObject polyGO, Polygon basePoly, Material passMat)
        {
            // add mesh;
            MeshFilter filter = polyGO.AddComponent<MeshFilter>();
            filter.sharedMesh = new Mesh();
            MeshRenderer meshRend = polyGO.AddComponent<MeshRenderer>();
            meshRend.sharedMaterial = passMat;
            meshRend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            meshRend.receiveShadows = false;
            meshRend.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            basePoly.meshFilter = filter;
        }
    }
}