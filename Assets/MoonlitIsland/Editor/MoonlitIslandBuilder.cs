using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;
using Random = UnityEngine.Random;

namespace MoonlitIsland.Editor
{
    /// <summary>Reproducible, asset-free scene construction, invoked through Unity CLI eval.</summary>
    public static class MoonlitIslandBuilder
    {
        public const string ScenePath = "Assets/MoonlitIsland/MoonlitIsland.unity";
        const string Root = "Assets/MoonlitIsland";
        /// <summary>World size of the generated landmass.</summary>
        public const float Extent = 460f;
        static Material bark, needleA, needleB, coarsePine, silhouette;

        [MenuItem("Moonlit Island/Build or rebuild island")]
        public static void Build()
        {
            if (EditorApplication.isPlaying) throw new InvalidOperationException("Stop Play mode before rebuilding.");
            // Never discard another scene's unsaved work when called from automation.
            for (int i = 0; i < SceneManager.sceneCount; ++i)
                if (SceneManager.GetSceneAt(i).isDirty)
                    throw new InvalidOperationException("Save the current scene before building the island.");
            Directory.CreateDirectory(Root + "/Materials");
            Directory.CreateDirectory(Root + "/Meshes");
            Directory.CreateDirectory(Root + "/Settings");
            AssetDatabase.Refresh();
            ScanTextureImporter.ConfigureAll();
            Random.InitState(5047);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var root = new GameObject("MOONLIT ISLAND").transform;
            ConfigurePipeline();
            var land = NewGroup("01 • Island and shoreline", root);
            var forest = NewGroup("02 • Old growth forest", root);
            var details = NewGroup("03 • Coastal boulders", root);
            var atmosphere = NewGroup("04 • Moon and atmosphere", root);
            var wildlife = NewGroup("05 • Bat colony", root);
            CreateIsland(land);
            PlantForest(forest);
            ScatterRocks(details);
            IslandOceanBuilder.Create(root);
            CreateAtmosphere(atmosphere);
            var body = Lit("Bat fur charcoal", new Color(.055f,.042f,.065f), .25f);
            var wings = Lit("Bat wing membrane", new Color(.105f,.065f,.11f), .38f);
            wings.SetFloat("_DoubleSidedEnable", 1f);
            wings.SetFloat("_CullMode", 0f);
            HDMaterial.ValidateMaterial(wings);
            var flock = IslandBatFlock.CreateFlock(wildlife, body, wings);
            flock.transform.localPosition = new Vector3(0, 0, 25f); // keep the colony south of the high range
            var camera = CreateCamera(root);
            CreateMoonAndStars(atmosphere, camera);
            PersistMeshes(root);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            Selection.activeGameObject = root.gameObject;
            if (SceneView.lastActiveSceneView != null)
                SceneView.lastActiveSceneView.LookAt(new Vector3(0,18f,-20f), camera.transform.rotation, 260f);
            Debug.Log("MOONLIT_ISLAND_BUILT " + ScenePath + " | Forest children: " + forest.childCount);
        }

        /// <summary>
        /// CLI hook: attach the bat pilot to the built scene's camera without
        /// regenerating the island. Idempotent, so it can be run repeatedly.
        /// </summary>
        public static void EnsurePilotRig()
        {
            if (EditorApplication.isPlaying) throw new InvalidOperationException("Stop Play mode first.");
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var rig = UnityEngine.Object.FindAnyObjectByType<IslandCameraRig>();
            if (rig == null) throw new InvalidOperationException("IslandCameraRig missing; rebuild the island first.");
            if (rig.GetComponent<IslandBatPilot>() != null)
            {
                Debug.Log("MOONLIT_PILOT_PRESENT");
                return;
            }
            rig.gameObject.AddComponent<IslandBatPilot>();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log("MOONLIT_PILOT_ATTACHED");
        }

        static void ConfigurePipeline()
        {
            const string path = Root + "/Settings/MoonlitIslandHDRP.asset";
            var pipeline = AssetDatabase.LoadAssetAtPath<HDRenderPipelineAsset>(path);
            if (pipeline == null)
            {
                var source = QualitySettings.renderPipeline != null ? QualitySettings.renderPipeline : GraphicsSettings.defaultRenderPipeline;
                if (source == null) throw new InvalidOperationException("This scene requires the project's HDRP asset.");
                AssetDatabase.CopyAsset(AssetDatabase.GetAssetPath(source), path);
                pipeline = AssetDatabase.LoadAssetAtPath<HDRenderPipelineAsset>(path);
            }
            var settings = pipeline.currentPlatformRenderPipelineSettings;
            settings.supportWater = true;
            settings.waterSimulationResolution = WaterSimulationResolution.Medium128;
            pipeline.currentPlatformRenderPipelineSettings = settings;
            EditorUtility.SetDirty(pipeline);
            GraphicsSettings.defaultRenderPipeline = pipeline;
            QualitySettings.renderPipeline = pipeline;
        }

        static Transform NewGroup(string name, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        static Material Lit(string name, Color color, float smoothness)
        {
            string path = Root + "/Materials/" + name + ".mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null) { mat = new Material(Shader.Find("HDRP/Lit")); AssetDatabase.CreateAsset(mat,path); }
            mat.name = name;
            mat.SetColor("_BaseColor",color);
            mat.SetFloat("_Smoothness",smoothness);
            mat.SetFloat("_Metallic",0);
            mat.enableInstancing = true;
            HDMaterial.ValidateMaterial(mat);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static Material Scanned(string name, string folder, Color tint, float smoothness, Vector2 tiling)
        {
            string path = Root + "/Materials/" + name + ".mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null) { mat = new Material(Shader.Find("HDRP/Lit")); AssetDatabase.CreateAsset(mat,path); }
            mat.name = name;
            string prefix = Root + "/ScanTextures/" + folder + "/" + folder;
            var color = AssetDatabase.LoadAssetAtPath<Texture2D>(prefix + "_Color.jpg");
            var normal = AssetDatabase.LoadAssetAtPath<Texture2D>(prefix + "_Normal.jpg");
            if (color == null || normal == null) throw new InvalidOperationException("Missing scan textures: " + folder);
            mat.SetColor("_BaseColor", tint);
            mat.SetTexture("_BaseColorMap", color);
            mat.SetTextureScale("_BaseColorMap", tiling);
            mat.SetTextureOffset("_BaseColorMap", Vector2.zero);
            mat.SetTexture("_NormalMap", normal);
            mat.SetFloat("_NormalScale", 1f);
            mat.SetFloat("_Smoothness", smoothness);
            mat.SetFloat("_Metallic", 0f);
            mat.enableInstancing = true;
            HDMaterial.ValidateMaterial(mat);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static Material Unlit(string name, Color color)
        {
            string path = Root + "/Materials/" + name + ".mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null) { mat = new Material(Shader.Find("HDRP/Unlit")); AssetDatabase.CreateAsset(mat,path); }
            mat.name = name;
            mat.SetColor("_UnlitColor",color);
            HDMaterial.ValidateMaterial(mat);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        // ---- Landform ----

        public static float Radius(float x, float z)
        {
            const float a = 150f, b = 115f;
            float angle = Mathf.Atan2(z/b,x/a);
            float edge = 1f + .06f*Mathf.Sin(angle*3f+.3f) + .047f*Mathf.Cos(angle*5f-.6f) + .023f*Mathf.Sin(angle*9f);
            return Mathf.Sqrt(x*x/(a*a)+z*z/(b*b))/edge;
        }

        /// <summary>Shared forest-density field: high where woods stand, low in clearings.</summary>
        public static float ForestDensity(float x, float z)
        {
            return Mathf.PerlinNoise(x*.013f+5.2f, z*.013f+9.1f)*.7f
                 + Mathf.PerlinNoise(x*.045f+13.7f, z*.045f+2.8f)*.3f;
        }

        public static float Height(float x, float z)
        {
            float r = Radius(x,z);
            float baseHeight = 15f*Mathf.Pow(Mathf.Clamp01(1-r), .8f);
            float hills = 8.5f*Mathf.PerlinNoise(x*.021f+7.3f,z*.021f+3.1f);
            float detail = 1.1f*Mathf.PerlinNoise(x*.11f+8,z*.11f+1);
            float land = baseHeight + (hills+detail)*Mathf.Clamp01((1.08f-r)*5f) - .8f - Mathf.Max(0,r-1f)*26f;
            return land + Mountains(x,z);
        }

        /// <summary>Ridged multifractal peaks: a main range across the north plus two outliers.</summary>
        static float Mountains(float x, float z)
        {
            float main = RangeMask(x, z, 10f, -130f, 170f, 95f);
            float west = RangeMask(x, z, -150f, 55f, 62f, 48f);
            float east = RangeMask(x, z, 160f, 70f, 55f, 44f);
            float ridge = Ridged(x*.011f, z*.011f)*.6f + Ridged(x*.027f+9f, z*.027f+4f)*.3f + Ridged(x*.06f+2f, z*.06f+7f)*.1f;
            return (main*105f + west*50f + east*42f) * ridge + (main+west+east)*10f;
        }

        static float RangeMask(float x, float z, float cx, float cz, float rx, float rz)
        {
            float d = Mathf.Sqrt((x-cx)*(x-cx)/(rx*rx) + (z-cz)*(z-cz)/(rz*rz));
            return Mathf.Pow(Mathf.Clamp01(1f-d), 1.6f);
        }

        static float Ridged(float x, float z)
        {
            float n = Mathf.PerlinNoise(x,z)*2f-1f;
            return (1f-Mathf.Abs(n))*(1f-Mathf.Abs(n));
        }

        static void CreateIsland(Transform parent)
        {
            const int n=280;
            var verts=new Vector3[(n+1)*(n+1)];
            var uv=new Vector2[verts.Length];
            var triangles=new List<int>();
            for(int z=0;z<=n;z++) for(int x=0;x<=n;x++)
            {
                float px=(x/(float)n-.5f)*Extent, pz=(z/(float)n-.5f)*Extent;
                int i=z*(n+1)+x; verts[i]=new Vector3(px,Height(px,pz),pz);
                uv[i]=new Vector2((float)x/n,(float)z/n);
            }
            for(int z=0;z<n;z++) for(int x=0;x<n;x++)
            {
                int a=z*(n+1)+x,b=a+1,c=a+n+1,d=c+1;
                triangles.AddRange(new[]{a,c,b,b,c,d});
            }
            var mesh=new Mesh{name="Sculpted island"}; mesh.indexFormat=UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices=verts; mesh.uv=uv; mesh.SetTriangles(triangles,0);
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            var go=MeshObject("Island • ridged highland coast",parent,mesh,new Material[1]);
            go.isStatic=true;
            go.AddComponent<MeshCollider>().sharedMesh=mesh;
            IslandGroundSurface.Apply(go, Extent);
        }

        static GameObject MeshObject(string name,Transform parent,Mesh mesh,Material[] materials)
        {
            var go=new GameObject(name,typeof(MeshFilter),typeof(MeshRenderer)); go.transform.SetParent(parent,false);
            go.GetComponent<MeshFilter>().sharedMesh=mesh; go.GetComponent<MeshRenderer>().sharedMaterials=materials;
            return go;
        }

        // ---- Forest ----

        static void PlantForest(Transform parent)
        {
            bark = Scanned("Cedar bark scanned", "Bark", new Color(.62f,.55f,.5f), .4f, new Vector2(2f,2.4f));
            needleA = Lit("Pine deep green", new Color(.048f,.115f,.095f), .13f);
            needleB = Lit("Pine blue green", new Color(.085f,.17f,.155f), .17f);
            coarsePine = Lit("Distant coarse pine", new Color(.055f,.125f,.10f), .12f);
            silhouette = Lit("Far treeline silhouette", new Color(.028f,.052f,.048f), .1f);

            var templates=new Mesh[9]; for(int i=0;i<templates.Length;i++) templates[i]=PineMesh(i);
            var near=new List<Vector3>();
            var mid=new Dictionary<long,List<Matrix4x4>>();
            var far=new Dictionary<long,List<Matrix4x4>>();
            int midCount=0, farCount=0;

            var planted=new List<Vector2>();
            var grid=new Dictionary<long,List<Vector2>>();
            var cameraStart=new Vector2(165,-185);
            for(int attempt=0;attempt<260000 && planted.Count<12000;attempt++)
            {
                // Sample directly inside the island ellipse; square sampling wastes ~76% of attempts.
                float radial=Mathf.Sqrt(Random.value)*.94f, angle=Random.Range(0,Mathf.PI*2f);
                float x=Mathf.Cos(angle)*150f*radial*1.28f, z=Mathf.Sin(angle)*115f*radial*1.28f;
                float r=Radius(x,z); if(r>.97f) continue;
                float h=Height(x,z); if(h<1.8f) continue;
                float slope=Slope(x,z); if(slope>.62f) continue;
                float density=ForestDensity(x,z);
                if(density<.28f) continue; // natural clearings
                float treeline=68f+10f*Mathf.PerlinNoise(x*.02f+3f,z*.02f+8f);
                if(h>treeline) continue;
                // A winding break in the trees reveals the depth of the forest.
                if(Mathf.Abs(x-8*Mathf.Sin(z*.05f))<3.6f && z<10f && z>-110f) continue;

                float cameraDistance=Vector2.Distance(new Vector2(x,z),cameraStart);
                bool nearTree=cameraDistance<230f && near.Count<1800;
                float spacing=Mathf.Lerp(7.5f,3.4f,Mathf.InverseLerp(.28f,.78f,density));
                // Merged mid/far tiers overlap invisibly at range, so they pack tighter than the hero trees.
                if(TooClose(grid,x,z,(nearTree?spacing:spacing*.45f)*.72f)) continue;
                float yaw=Random.Range(0,360f);
                float scale=Random.Range(.8f,1.5f)*(h>40f?Mathf.Lerp(1f,.62f,Mathf.InverseLerp(40f,treeline,h)):1f);
                var matrix=Matrix4x4.TRS(new Vector3(x,h-.15f,z),Quaternion.Euler(0,yaw,0),new Vector3(scale,Random.Range(.85f,1.45f)*scale,scale));

                if(nearTree)
                {
                    near.Add(new Vector3(x,h-.15f,z));
                    var go=MeshObject("Cedar "+near.Count.ToString("000"),parent,templates[Random.Range(0,templates.Length)],new[]{bark,needleA,needleB});
                    go.transform.position=new Vector3(x,h-.15f,z);
                    go.transform.rotation=Quaternion.Euler(Random.Range(-2.5f,2.5f),yaw,Random.Range(-2.5f,2.5f));
                    go.transform.localScale=new Vector3(scale,Random.Range(.85f,1.45f)*scale,scale);
                    go.isStatic=true;
                    go.GetComponent<MeshRenderer>().shadowCastingMode=cameraDistance<120f?ShadowCastingMode.On:ShadowCastingMode.Off;
                }
                else if(cameraDistance>330f && farCount<4000) { AddMatrix(far,CellKey(x,z),matrix); farCount++; }
                else if(midCount<6500) { AddMatrix(mid,CellKey(x,z),matrix); midCount++; }

                planted.Add(new Vector2(x,z));
                InsertGrid(grid,x,z);
            }

            BuildChunkMeshes(parent, mid, CoarsePineMesh(), coarsePine, "Mid forest block");
            BuildChunkMeshes(parent, far, SilhouettePineMesh(), silhouette, "Far treeline block");
            Debug.Log("MOONLIT_FOREST near=" + near.Count + " mid=" + midCount + " far=" + farCount);
        }

        static float Slope(float x, float z)
        {
            const float e=2f;
            float hx=Height(x+e,z)-Height(x-e,z), hz=Height(x,z+e)-Height(x,z-e);
            return Mathf.Atan2(Mathf.Sqrt(hx*hx+hz*hz),2f*e);
        }

        static long CellKey(float x, float z) { return ((long)Mathf.FloorToInt(x/64f)<<32) ^ (uint)Mathf.FloorToInt(z/64f); }

        static void AddMatrix(Dictionary<long,List<Matrix4x4>> map, long key, Matrix4x4 m)
        {
            if(!map.TryGetValue(key, out var list)) { list=new List<Matrix4x4>(); map[key]=list; }
            list.Add(m);
        }

        static bool TooClose(Dictionary<long,List<Vector2>> grid, float x, float z, float minDistance)
        {
            int cx=Mathf.FloorToInt(x/12f), cz=Mathf.FloorToInt(z/12f);
            float sqr=minDistance*minDistance;
            for(int i=-1;i<=1;i++) for(int j=-1;j<=1;j++)
            {
                long key=((long)(cx+i)<<32)^(uint)(cz+j);
                if(!grid.TryGetValue(key, out var cell)) continue;
                foreach(var p in cell)
                    if((p.x-x)*(p.x-x)+(p.y-z)*(p.y-z)<sqr) return true;
            }
            return false;
        }

        static void InsertGrid(Dictionary<long,List<Vector2>> grid, float x, float z)
        {
            long key=((long)Mathf.FloorToInt(x/12f)<<32)^(uint)Mathf.FloorToInt(z/12f);
            if(!grid.TryGetValue(key, out var cell)) { cell=new List<Vector2>(); grid[key]=cell; }
            cell.Add(new Vector2(x,z));
        }

        /// <summary>Bakes one merged mesh per 64-unit cell so mid- and far-tier forests cost one draw call each.</summary>
        static void BuildChunkMeshes(Transform parent, Dictionary<long,List<Matrix4x4>> chunks, Mesh template, Material material, string name)
        {
            var srcVerts = template.vertices;
            var srcTris = template.triangles;
            int index = 0;
            foreach(var chunk in chunks)
            {
                int trees = chunk.Value.Count;
                var verts = new Vector3[srcVerts.Length * trees];
                var tris = new int[srcTris.Length * trees];
                for(int t = 0; t < trees; t++)
                {
                    var m = chunk.Value[t];
                    int vo = t * srcVerts.Length, to = t * srcTris.Length;
                    for(int v = 0; v < srcVerts.Length; v++)
                        verts[vo + v] = m.MultiplyPoint3x4(srcVerts[v]);
                    for(int k = 0; k < srcTris.Length; k++)
                        tris[to + k] = srcTris[k] + vo;
                }
                var mesh = new Mesh { name = name + " " + (index++) };
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                mesh.vertices = verts;
                mesh.SetTriangles(tris, 0);
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                var go = MeshObject(mesh.name, parent, mesh, new[] { material });
                go.isStatic = true;
                go.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
            }
        }

        static Mesh PineMesh(int variant)
        {
            var v=new List<Vector3>();var uv=new List<Vector2>();var tris=new[]{new List<int>(),new List<int>(),new List<int>()};
            float height=Random.Range(9f,14f);
            AddCone(v,uv,tris[0],Vector3.zero,.34f,.06f,height*.94f,9);
            for(int tier=0;tier<7;tier++)
            {
                float t=tier/7f;float y=height*(.21f+t*.72f);
                float radius=Mathf.Lerp(3.1f,.5f,t)*Random.Range(.85f,1.1f);
                AddNeedleCrown(v,tris[1+tier%2],new Vector3(0,y,0),radius,height*.33f*(1-t*.55f),12);
            }
            while(uv.Count<v.Count) uv.Add(Vector2.zero);
            var mesh=new Mesh{name="Cedar silhouette "+variant};mesh.SetVertices(v);mesh.SetUVs(0,uv);mesh.subMeshCount=3;
            for(int i=0;i<3;i++)mesh.SetTriangles(tris[i],i);mesh.RecalculateNormals();mesh.RecalculateBounds();return mesh;
        }

        static void AddCone(List<Vector3> verts,List<Vector2> uv,List<int> tri,Vector3 origin,float r0,float r1,float height,int segments)
        {
            for(int k=0;k<segments;k++)
            {
                float a=k*Mathf.PI*2/segments,b=(k+1)*Mathf.PI*2/segments;
                int i=verts.Count;
                verts.Add(origin+new Vector3(Mathf.Cos(a)*r0,0,Mathf.Sin(a)*r0));
                verts.Add(origin+new Vector3(Mathf.Cos(b)*r0,0,Mathf.Sin(b)*r0));
                verts.Add(origin+new Vector3(Mathf.Cos(a)*r1,height,Mathf.Sin(a)*r1));
                verts.Add(origin+new Vector3(Mathf.Cos(b)*r1,height,Mathf.Sin(b)*r1));
                uv.Add(new Vector2((float)k/segments,0));uv.Add(new Vector2((float)(k+1)/segments,0));
                uv.Add(new Vector2((float)k/segments,1));uv.Add(new Vector2((float)(k+1)/segments,1));
                tri.AddRange(new[]{i,i+2,i+1,i+1,i+2,i+3});
            }
        }

        static void AddNeedleCrown(List<Vector3> v,List<int> tri,Vector3 o,float radius,float height,int segments)
        {
            int start=v.Count;
            // Alternating long branch tips and inset notches make a broken, organic canopy.
            for(int ring=0;ring<3;ring++) for(int j=0;j<segments*2;j++)
            {
                float angle=j*Mathf.PI/segments;
                float radiusScale=ring==0?1: ring==1?.6f:.035f;
                float irregular=(j%2==0?1:.64f)*Random.Range(.89f,1.11f);
                v.Add(o+new Vector3(Mathf.Cos(angle)*radius*radiusScale*irregular,ring==0?Random.Range(-.24f,.13f):ring==1?height*.45f:height,Mathf.Sin(angle)*radius*radiusScale*irregular));
            }
            int count=segments*2;
            for(int ring=0;ring<2;ring++)for(int j=0;j<count;j++)
            {
                int a=start+ring*count+j,b=start+ring*count+(j+1)%count,c=a+count,d=b+count;
                tri.AddRange(new[]{a,c,b,b,c,d});
            }
            int bottom=v.Count;v.Add(o+Vector3.up*.15f);
            for(int j=0;j<count;j++)tri.AddRange(new[]{bottom,start+j,start+(j+1)%count});
        }

        /// <summary>Three stacked tapering cones: reads as a pine at mid distance for ~50 triangles.</summary>
        static Mesh CoarsePineMesh()
        {
            var v=new List<Vector3>();var uv=new List<Vector2>();var tris=new List<int>();
            float height=Random.Range(11f,15f);
            AddCone(v,uv,tris,Vector3.zero,.42f,.10f,height,7);
            AddCone(v,uv,tris,new Vector3(0,height*.42f,0),2.6f,.14f,height*.58f,7);
            AddCone(v,uv,tris,new Vector3(0,height*.74f,0),1.55f,.08f,height*.30f,7);
            var mesh=new Mesh{name="Coarse pine"};
            mesh.SetVertices(v);mesh.SetUVs(0,uv);mesh.SetTriangles(tris,0);
            mesh.RecalculateNormals();mesh.RecalculateBounds();return mesh;
        }

        /// <summary>Two crossed tapered pine profiles: a distant tree for ~10 triangles.</summary>
        static Mesh SilhouettePineMesh()
        {
            // Profile: trunk base, three widening shoulder points, and a peak.
            var profile=new[]{new Vector2(0,0),new Vector2(.5f,.14f),new Vector2(1.9f,.34f),new Vector2(2.9f,.52f),new Vector2(2.2f,.68f),new Vector2(1.2f,.84f),new Vector2(0,1f)};
            var v=new List<Vector3>();var tris=new List<int>();
            for(int cross=0;cross<2;cross++)
            {
                Quaternion yaw=Quaternion.Euler(0,cross*90f+22f,0);
                // One flat polygon per cross; wound so its +X face is front-facing.
                int start=v.Count;
                foreach(var p in profile) v.Add(yaw*new Vector3(p.x,11f*p.y,0));
                for(int i=1;i<profile.Length-1;i++)
                    tris.AddRange(new[]{start,start+i+1,start+i});
                int mirror=v.Count;
                for(int i=profile.Length-1;i>=0;i--) v.Add(yaw*new Vector3(-profile[i].x,11f*profile[i].y,0));
                for(int i=1;i<profile.Length-1;i++)
                    tris.AddRange(new[]{mirror,mirror+i,mirror+i+1});
            }
            var mesh=new Mesh{name="Silhouette pine"};
            mesh.SetVertices(v);mesh.SetTriangles(tris,0);
            mesh.RecalculateNormals();mesh.RecalculateBounds();return mesh;
        }

        static void ScatterRocks(Transform parent)
        {
            var stone = Scanned("Coast boulder scanned", "MossyRock", new Color(.72f,.75f,.72f), .34f, new Vector2(1.3f,.9f));
            var source=GameObject.CreatePrimitive(PrimitiveType.Sphere);
            var mesh=UnityEngine.Object.Instantiate(source.GetComponent<MeshFilter>().sharedMesh);
            mesh.name="Weathered basalt";UnityEngine.Object.DestroyImmediate(source);
            var verts=mesh.vertices;for(int i=0;i<verts.Length;i++)verts[i]*=.8f+.4f*Mathf.PerlinNoise(verts[i].x*8+3,verts[i].z*8+verts[i].y*5+7);
            mesh.vertices=verts;mesh.RecalculateNormals();
            for(int i=0;i<160;i++)
            {
                float angle=Random.Range(0,Mathf.PI*2),r=Random.Range(.86f,1.05f);
                float x=Mathf.Cos(angle)*150*r,z=Mathf.Sin(angle)*115*r;
                var go=MeshObject("Basalt "+i.ToString("000"),parent,mesh,new[]{stone});
                float s=Random.Range(1.3f,5.4f);go.transform.position=new Vector3(x,Height(x,z)+s*.12f,z);
                go.transform.localScale=new Vector3(s,Random.Range(.8f,2.1f)*s*.6f,s*Random.Range(.7f,1.3f));
                go.transform.rotation=Random.rotation;
                go.isStatic=true;
            }
        }

        static void CreateAtmosphere(Transform parent)
        {
            string path=Root+"/Settings/NightAtmosphere.asset";
            var profile=AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
            if(profile!=null) AssetDatabase.DeleteAsset(path);
            profile=ScriptableObject.CreateInstance<VolumeProfile>();AssetDatabase.CreateAsset(profile,path);
            var visual=profile.Add<VisualEnvironment>(true);visual.skyType.Override((int)SkyType.Gradient);
            visual.skyAmbientMode.Override(SkyAmbientMode.Dynamic);
            var sky=profile.Add<GradientSky>(true);
            sky.top.Override(new Color(.018f,.035f,.085f));sky.middle.Override(new Color(.13f,.22f,.30f));sky.bottom.Override(new Color(.045f,.075f,.1f));
            sky.gradientDiffusion.Override(1.7f);sky.skyIntensityMode.Override(SkyIntensityMode.Multiplier);sky.multiplier.Override(650f);
            var exposure=profile.Add<Exposure>(true);exposure.mode.Override(ExposureMode.Fixed);exposure.fixedExposure.Override(9f);
            var fog=profile.Add<Fog>(true);fog.enabled.Override(true);fog.enableVolumetricFog.Override(true);fog.meanFreePath.Override(900f);
            fog.baseHeight.Override(-4f);fog.maximumHeight.Override(80f);fog.albedo.Override(new Color(.33f,.46f,.58f));fog.depthExtent.Override(420f);
            var tone=profile.Add<Tonemapping>(true);tone.mode.Override(TonemappingMode.ACES);
            var bloom=profile.Add<Bloom>(true);bloom.intensity.Override(.16f);bloom.threshold.Override(1.1f);
            var vignette=profile.Add<Vignette>(true);vignette.intensity.Override(.16f);vignette.smoothness.Override(.55f);
            foreach(var c in profile.components)AssetDatabase.AddObjectToAsset(c,profile);
            var volume=NewGroup("Blue hour atmosphere",parent).gameObject.AddComponent<Volume>();volume.isGlobal=true;volume.priority=10;volume.sharedProfile=profile;
            Directional("Moonlight • silver rim",parent,new Vector3(34,-35,0),new Color(.64f,.77f,1f),3600f,true);
            Directional("Sky bounce • soft teal",parent,new Vector3(48,145,0),new Color(.3f,.55f,.65f),850f,false);
        }

        static void Directional(string name,Transform parent,Vector3 angles,Color color,float intensity,bool shadows)
        {
            var go=NewGroup(name,parent).gameObject;go.transform.rotation=Quaternion.Euler(angles);
            var light=go.AddComponent<Light>();light.type=LightType.Directional;go.AddComponent<HDAdditionalLightData>();
            light.color=color;light.lightUnit=LightUnit.Lux;light.intensity=intensity;light.shadows=shadows?LightShadows.Soft:LightShadows.None;
        }

        static Camera CreateCamera(Transform parent)
        {
            var go=NewGroup("Main Camera",parent).gameObject;go.tag="MainCamera";
            var cam=go.AddComponent<Camera>();cam.nearClipPlane=.3f;cam.farClipPlane=2500f;cam.fieldOfView=46f;
            go.transform.position=new Vector3(165,95,-185);go.transform.LookAt(new Vector3(0,20,0));
            var hd=go.AddComponent<HDAdditionalCameraData>();hd.antialiasing=HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing;
            IslandOceanBuilder.EnableCameraWater(hd);go.AddComponent<AudioListener>();
            var rig=go.AddComponent<IslandCameraRig>();rig.focus=new Vector3(0,20,0);
            rig.maximumDistance=720f;rig.minimumDistance=60f;rig.dragSensitivity=.14f;
            go.AddComponent<IslandBatPilot>();
            return cam;
        }

        static void CreateMoonAndStars(Transform parent,Camera cam)
        {
            var moon=GameObject.CreatePrimitive(PrimitiveType.Sphere);moon.name="Moon";moon.transform.SetParent(parent,false);
            moon.transform.position=cam.transform.position+cam.transform.forward*850+cam.transform.right*245+cam.transform.up*265;
            moon.transform.localScale=Vector3.one*26;
            UnityEngine.Object.DestroyImmediate(moon.GetComponent<Collider>());
            moon.GetComponent<Renderer>().sharedMaterial=Unlit("Silver moon",new Color(1.4f,1.7f,2.1f));
            var starMat=Unlit("Distant starlight",new Color(.8f,1.0f,1.4f));
            var v=new List<Vector3>();var tris=new List<int>();
            for(int i=0;i<180;i++)
            {
                Vector3 dir=Random.onUnitSphere;if(dir.y<.08f)dir.y=Mathf.Abs(dir.y)+.08f;dir.Normalize();
                Vector3 p=dir*1150;Vector3 side=Vector3.Cross(dir,Vector3.up).normalized*Random.Range(.22f,.65f);Vector3 up=Vector3.Cross(side.normalized,dir)*side.magnitude;
                int a=v.Count;v.Add(p-side-up);v.Add(p+side-up);v.Add(p+side+up);v.Add(p-side+up);
                tris.AddRange(new[]{a,a+2,a+1,a,a+3,a+2,a,a+1,a+2,a,a+2,a+3});
            }
            var m=new Mesh{name="Star field"};m.SetVertices(v);m.SetTriangles(tris,0);m.RecalculateNormals();
            MeshObject("Sparse night stars",parent,m,new[]{starMat});
        }

        static void PersistMeshes(Transform root)
        {
            int index=0;var saved=new HashSet<Mesh>();
            foreach(var mf in root.GetComponentsInChildren<MeshFilter>())
            {
                var mesh=mf.sharedMesh;
                if(mesh==null || AssetDatabase.Contains(mesh) || !saved.Add(mesh))continue;
                string path=Root+"/Meshes/"+(index++).ToString("000")+"_"+mesh.name.Replace("/","_")+".asset";
                var existing=AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if(existing!=null)AssetDatabase.DeleteAsset(path);
                AssetDatabase.CreateAsset(mesh,path);
            }
        }
    }
}
