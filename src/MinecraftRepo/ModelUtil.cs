using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MinecraftRepo
{
    internal static class Mats
    {
        static Shader lit, unlit, transparent, sprite;
        static Material litWhite;
        static readonly Dictionary<Texture, Material> spriteMats = new Dictionary<Texture, Material>();

        static Shader First(params string[] names)
        {
            foreach (var n in names)
            {
                var s = Shader.Find(n);
                if (s != null && s.isSupported) { Plugin.Log.LogInfo("shader: " + n); return s; }
            }
            return null;
        }

        public static Shader Lit => lit ? lit : lit = First("Standard", "Legacy Shaders/Diffuse", "Mobile/Diffuse", "Unlit/Texture", "Sprites/Default");
        public static Shader Unlit => unlit ? unlit : unlit = First("Unlit/Texture", "Sprites/Default", "UI/Default");
        public static Shader Transparent => transparent ? transparent : transparent = First("Legacy Shaders/Transparent/Diffuse", "Sprites/Default", "Unlit/Transparent", "UI/Default");

        /// Unlit, alpha-blended, double-sided: fire, flames, explosion and smoke sprites.
        public static Shader Sprite => sprite ? sprite : sprite = First("Sprites/Default", "Unlit/Transparent", "Legacy Shaders/Particles/Alpha Blended", "UI/Default");

        public static Material SpriteTex(Texture tex)
        {
            if (tex && spriteMats.TryGetValue(tex, out var m) && m) return m;
            m = new Material(Sprite) { mainTexture = tex, renderQueue = 3000 };
            // Sprites/Default reads these from the SpriteRenderer; set them so plain meshes draw too
            m.SetColor("_RendererColor", Color.white);
            m.SetVector("_Flip", new Vector4(1, 1, 0, 0));
            if (tex) spriteMats[tex] = m;
            return m;
        }

        /// Full-bright opaque material. R.E.P.O. ships no Unlit/Texture (the fallback, Sprites/Default, has no depth
        /// write or culling, so boxes show their back faces): then Standard with the texture as emission.
        public static Material UnlitTex(Texture tex)
        {
            if (Unlit && Unlit.name == "Unlit/Texture") return new Material(Unlit) { mainTexture = tex };
            var m = LitTex(tex);
            m.EnableKeyword("_EMISSION");
            m.SetTexture("_EmissionMap", tex);
            m.SetColor("_EmissionColor", Color.white);
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            return m;
        }

        public static Material LitTex(Texture tex)
        {
            var m = new Material(Lit) { mainTexture = tex };
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0f);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
            m.SetOverrideTag("RenderType", "Opaque");
            return m;
        }

        public static Material LitWhite => litWhite ? litWhite : litWhite = LitTex(McAssets.White);

        public static Material TransparentTex(Texture tex)
        {
            var m = new Material(Transparent) { mainTexture = tex, renderQueue = 3001 };
            return m;
        }
    }

    /// Collects quads into a Unity mesh.
    internal class MeshBuilder
    {
        readonly List<Vector3> v = new List<Vector3>();
        readonly List<Vector2> uv = new List<Vector2>();
        readonly List<int> t = new List<int>();

        /// Corners as seen from outside: bottom-left, bottom-right, top-right, top-left.
        public void Quad(Vector3 bl, Vector3 br, Vector3 tr, Vector3 tl, Vector2 uvBl, Vector2 uvBr, Vector2 uvTr, Vector2 uvTl)
        {
            int i = v.Count;
            v.Add(bl); v.Add(br); v.Add(tr); v.Add(tl);
            uv.Add(uvBl); uv.Add(uvBr); uv.Add(uvTr); uv.Add(uvTl);
            t.Add(i); t.Add(i + 3); t.Add(i + 2);
            t.Add(i); t.Add(i + 2); t.Add(i + 1);
        }

        public void QuadPoint(Vector3 bl, Vector3 br, Vector3 tr, Vector3 tl, Vector2 uvc) => Quad(bl, br, tr, tl, uvc, uvc, uvc, uvc);

        public Mesh Build(string name)
        {
            var m = new Mesh { name = name };
            if (v.Count > 65000) m.indexFormat = IndexFormat.UInt32;
            m.SetVertices(v);
            m.SetUVs(0, uv);
            m.SetTriangles(t, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        /// A box whose faces sample pixel rects (x, y from the top-left, w, h) of a texture: block models.
        public void UvBox(Vector3 min, Vector3 max, Rect side, Rect top, Rect bottom, Texture tex) =>
            UvBox(min, max, side, side, side, side, top, bottom, tex);

        /// Per-face rects: +Z, -Z, +X, -X, top, bottom.
        public void UvBox(Vector3 min, Vector3 max, Rect pz, Rect nz, Rect px, Rect nx, Rect top, Rect bottom, Texture tex)
        {
            float W = tex ? tex.width : 16, H = tex ? tex.height : 16;
            float x0 = min.x, y0 = min.y, z0 = min.z, x1 = max.x, y1 = max.y, z1 = max.z;
            void F(Vector3 bl, Vector3 br, Vector3 tr, Vector3 tl, Rect r)
            {
                const float e = 0.01f;
                float u0 = (r.x + e) / W, u1 = (r.x + r.width - e) / W;
                float vTop = 1 - (r.y + e) / H, vBot = 1 - (r.y + r.height - e) / H;
                Quad(bl, br, tr, tl, new Vector2(u0, vBot), new Vector2(u1, vBot), new Vector2(u1, vTop), new Vector2(u0, vTop));
            }
            F(new Vector3(x1, y0, z1), new Vector3(x0, y0, z1), new Vector3(x0, y1, z1), new Vector3(x1, y1, z1), pz);
            F(new Vector3(x0, y0, z0), new Vector3(x1, y0, z0), new Vector3(x1, y1, z0), new Vector3(x0, y1, z0), nz);
            F(new Vector3(x1, y0, z0), new Vector3(x1, y0, z1), new Vector3(x1, y1, z1), new Vector3(x1, y1, z0), px);
            F(new Vector3(x0, y0, z1), new Vector3(x0, y0, z0), new Vector3(x0, y1, z0), new Vector3(x0, y1, z1), nx);
            F(new Vector3(x0, y1, z0), new Vector3(x1, y1, z0), new Vector3(x1, y1, z1), new Vector3(x0, y1, z1), top);
            F(new Vector3(x0, y0, z1), new Vector3(x1, y0, z1), new Vector3(x1, y0, z0), new Vector3(x0, y0, z0), bottom);
        }

        /// A vertical quad seen from both sides (fire planes).
        public void DoubleQuad(Vector3 bl, Vector3 br, Vector3 tr, Vector3 tl)
        {
            Quad(bl, br, tr, tl, new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1));
            Quad(br, bl, tl, tr, new Vector2(1, 0), new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1));
        }

        /// A Minecraft model box. Sizes in skin pixels; texture offset (u, v) uses Minecraft's box unwrap.
        /// cutout=true emits one quad per opaque pixel, so no alpha-tested shader is needed (used for overlays).
        public void SkinBox(Vector3 min, Vector3 max, int u, int vv, int w, int h, int d, Texture2D tex, bool cutout)
        {
            float x0 = min.x, y0 = min.y, z0 = min.z, x1 = max.x, y1 = max.y, z1 = max.z;
            // front (+Z)
            Face(new Vector3(x1, y0, z1), new Vector3(x0, y0, z1), new Vector3(x0, y1, z1), new Vector3(x1, y1, z1), u + d, vv + d, w, h, tex, cutout);
            // back (-Z)
            Face(new Vector3(x0, y0, z0), new Vector3(x1, y0, z0), new Vector3(x1, y1, z0), new Vector3(x0, y1, z0), u + d + w + d, vv + d, w, h, tex, cutout);
            // +X (entity's right)
            Face(new Vector3(x1, y0, z0), new Vector3(x1, y0, z1), new Vector3(x1, y1, z1), new Vector3(x1, y1, z0), u, vv + d, d, h, tex, cutout);
            // -X (entity's left)
            Face(new Vector3(x0, y0, z1), new Vector3(x0, y0, z0), new Vector3(x0, y1, z0), new Vector3(x0, y1, z1), u + d + w, vv + d, d, h, tex, cutout);
            // top
            Face(new Vector3(x1, y1, z1), new Vector3(x0, y1, z1), new Vector3(x0, y1, z0), new Vector3(x1, y1, z0), u + d, vv, w, d, tex, cutout);
            // bottom
            Face(new Vector3(x1, y0, z0), new Vector3(x0, y0, z0), new Vector3(x0, y0, z1), new Vector3(x1, y0, z1), u + d + w, vv, w, d, tex, cutout);
        }

        void Face(Vector3 bl, Vector3 br, Vector3 tr, Vector3 tl, int px, int py, int pw, int ph, Texture2D tex, bool cutout)
        {
            float W = tex.width, H = tex.height;
            float sx = W / 64f, sy = H / 64f; // HD skins scale the layout
            if (!cutout)
            {
                const float e = 0.01f;
                var a = new Vector2((px + e) * sx / W, 1 - (py + ph - e) * sy / H);
                var b = new Vector2((px + pw - e) * sx / W, 1 - (py + ph - e) * sy / H);
                var c = new Vector2((px + pw - e) * sx / W, 1 - (py + e) * sy / H);
                var dd = new Vector2((px + e) * sx / W, 1 - (py + e) * sy / H);
                Quad(bl, br, tr, tl, a, b, c, dd);
                return;
            }
            var right = br - bl; var up = tl - bl;
            for (int j = 0; j < ph; j++)
                for (int i = 0; i < pw; i++)
                {
                    int tx = (int)((px + i + 0.5f) * sx), ty = (int)((py + j + 0.5f) * sy);
                    if (tex.GetPixel(tx, tex.height - 1 - ty).a < 0.5f) continue;
                    float s0 = (float)i / pw, s1 = (float)(i + 1) / pw;
                    float t0 = 1f - (float)(j + 1) / ph, t1 = 1f - (float)j / ph;
                    var uvc = new Vector2((tx + 0.5f) / W, 1 - (ty + 0.5f) / H);
                    QuadPoint(bl + right * s0 + up * t0, bl + right * s1 + up * t0, bl + right * s1 + up * t1, bl + right * s0 + up * t1, uvc);
                }
        }

        /// Minecraft's item model: the sprite extruded one pixel thick, one quad per opaque pixel face.
        /// The sprite lies in the XY plane, 1 unit wide, centred on the origin.
        public static Mesh ItemMesh(Texture2D tex)
        {
            var mb = new MeshBuilder();
            int w = tex.width, h = tex.height;
            float p = 1f / w, th = p * 0.5f;
            bool Solid(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && tex.GetPixel(x, y).a >= 0.5f;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (!Solid(x, y)) continue;
                    var uvc = new Vector2((x + 0.5f) / w, (y + 0.5f) / h);
                    float X0 = x * p - 0.5f, X1 = X0 + p, Y0 = y * p - 0.5f, Y1 = Y0 + p;
                    // front faces -Z, back faces +Z
                    mb.QuadPoint(new Vector3(X0, Y0, -th), new Vector3(X1, Y0, -th), new Vector3(X1, Y1, -th), new Vector3(X0, Y1, -th), uvc);
                    mb.QuadPoint(new Vector3(X1, Y0, th), new Vector3(X0, Y0, th), new Vector3(X0, Y1, th), new Vector3(X1, Y1, th), uvc);
                    if (!Solid(x - 1, y)) mb.QuadPoint(new Vector3(X0, Y0, th), new Vector3(X0, Y0, -th), new Vector3(X0, Y1, -th), new Vector3(X0, Y1, th), uvc);
                    if (!Solid(x + 1, y)) mb.QuadPoint(new Vector3(X1, Y0, -th), new Vector3(X1, Y0, th), new Vector3(X1, Y1, th), new Vector3(X1, Y1, -th), uvc);
                    if (!Solid(x, y - 1)) mb.QuadPoint(new Vector3(X0, Y0, th), new Vector3(X1, Y0, th), new Vector3(X1, Y0, -th), new Vector3(X0, Y0, -th), uvc);
                    if (!Solid(x, y + 1)) mb.QuadPoint(new Vector3(X0, Y1, -th), new Vector3(X1, Y1, -th), new Vector3(X1, Y1, th), new Vector3(X0, Y1, th), uvc);
                }
            return mb.Build(tex.name + "_item");
        }
    }

    internal static class ModelUtil
    {
        static readonly Dictionary<Texture2D, Mesh> itemMeshes = new Dictionary<Texture2D, Mesh>();
        static readonly Dictionary<Texture2D, Material> itemMats = new Dictionary<Texture2D, Material>();
        static Mesh cube;

        public static Mesh ItemMesh(Texture2D t)
        {
            if (!itemMeshes.TryGetValue(t, out var m)) itemMeshes[t] = m = MeshBuilder.ItemMesh(t);
            return m;
        }

        public static Material ItemMat(Texture2D t)
        {
            if (!itemMats.TryGetValue(t, out var m)) itemMats[t] = m = Mats.LitTex(t);
            return m;
        }

        public static Mesh Cube
        {
            get
            {
                if (cube) return cube;
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube = go.GetComponent<MeshFilter>().sharedMesh;
                Object.Destroy(go);
                return cube;
            }
        }

        /// A renderer-only copy of a PhysGrabObject's visuals (no colliders, scripts or network views).
        /// `bounds` is in the clone root's local space.
        public static GameObject CloneVisual(GameObject src, out Bounds bounds) => CloneVisual(src, out bounds, null);

        /// `localTo`: when given, transforms are taken from local matrices up to that root instead of world
        /// matrices, so it works on hidden objects whose parents are scaled to zero (R.E.P.O.'s flashlight).
        public static GameObject CloneVisual(GameObject src, out Bounds bounds, System.Func<Renderer, bool> include, System.Func<Transform, bool> identity = null)
        {
            var root = new GameObject("mc_visual_" + src.name);
            bool any = false;
            bounds = new Bounds(Vector3.zero, Vector3.zero);
            var w2l = src.transform.worldToLocalMatrix;
            foreach (var r in src.GetComponentsInChildren<Renderer>(include != null))
            {
                if (include == null ? !r.enabled : !include(r)) continue;
                Mesh mesh = null;
                Matrix4x4 rel;
                if (include != null && r is MeshRenderer)
                {
                    var mf0 = r.GetComponent<MeshFilter>();
                    if (!mf0 || !mf0.sharedMesh) continue;
                    mesh = mf0.sharedMesh;
                    rel = Matrix4x4.identity;
                    for (var t = r.transform; t && t != src.transform; t = t.parent)
                    {
                        if (identity != null && identity(t)) continue;
                        // a hide animation can leave any parent scaled to zero; treat that as unscaled
                        var sc = t.localScale;
                        if (Mathf.Abs(sc.x) < 1e-4f || Mathf.Abs(sc.y) < 1e-4f || Mathf.Abs(sc.z) < 1e-4f) sc = Vector3.one;
                        rel = Matrix4x4.TRS(t.localPosition, t.localRotation, sc) * rel;
                    }
                }
                else if (r is MeshRenderer)
                {
                    var mf = r.GetComponent<MeshFilter>();
                    if (!mf || !mf.sharedMesh) continue;
                    mesh = mf.sharedMesh;
                    rel = w2l * r.transform.localToWorldMatrix;
                }
                else if (r is SkinnedMeshRenderer smr)
                {
                    mesh = new Mesh();
                    smr.BakeMesh(mesh);
                    rel = w2l * Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one);
                }
                else continue;

                var child = new GameObject(r.name);
                child.transform.SetParent(root.transform, false);
                child.transform.localPosition = rel.GetColumn(3);
                child.transform.localRotation = rel.rotation;
                child.transform.localScale = rel.lossyScale;
                child.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = child.AddComponent<MeshRenderer>();
                mr.sharedMaterials = r.sharedMaterials;
                mr.shadowCastingMode = ShadowCastingMode.On;

                var mb = mesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var c = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var p = rel.MultiplyPoint3x4(c);
                    if (!any) { bounds = new Bounds(p, Vector3.zero); any = true; }
                    else bounds.Encapsulate(p);
                }
            }
            if (!any) bounds = new Bounds(Vector3.zero, Vector3.one * 0.3f);
            return root;
        }

        /// Wraps a clone so it's centred on its pivot and its largest side is `size` metres.
        public static GameObject Normalised(GameObject clone, Bounds b, float size)
        {
            var holder = new GameObject("mc_norm");
            clone.transform.SetParent(holder.transform, false);
            float s = size / Mathf.Max(0.001f, Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z)));
            clone.transform.localScale = Vector3.one * s;
            clone.transform.localPosition = -b.center * s;
            return holder;
        }

        /// Wraps a clone centred on its bounds, largest side `size` metres, turned by `rot` (clone space to holder).
        public static GameObject Oriented(GameObject clone, Bounds b, float size, Quaternion rot)
        {
            var norm = Normalised(clone, b, size);
            var holder = new GameObject("mc_oriented");
            norm.transform.SetParent(holder.transform, false);
            norm.transform.localRotation = rot;
            return holder;
        }

        public static void SetLayer(GameObject go, int layer)
        {
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
        }

        public static void SetShadows(GameObject go, ShadowCastingMode mode)
        {
            foreach (var r in go.GetComponentsInChildren<Renderer>(true)) r.shadowCastingMode = mode;
        }

        /// Minecraft's item model: the sprite extruded one pixel thick, `size` metres wide.
        public static GameObject SpriteModel(Texture2D tex, float size)
        {
            var go = new GameObject("mc_tool");
            go.AddComponent<MeshFilter>().sharedMesh = ItemMesh(tex);
            go.AddComponent<MeshRenderer>().sharedMaterial = ItemMat(tex);
            go.transform.localScale = Vector3.one * size;
            var holder = new GameObject("mc_norm");
            go.transform.SetParent(holder.transform, false);
            return holder;
        }

        /// The held-item model for a stack: the extruded sprite for tools, a mini copy for objects.
        public static GameObject HeldModel(ItemStack s, float size)
        {
            if (s.IsTool) return SpriteModel(s.Icon, size);
            if (s.Kind == ItemKind.Mc || s.Id != null) return McItems.Model(s.Id, size);
            var o = s.Objects.Count > 0 ? s.Objects[0] : null;
            if (!o) return new GameObject("mc_empty");
            var clone = CloneVisual(o.gameObject, out var b);
            return Normalised(clone, b, size);
        }
    }

    /// Item icons rendered once per kind at the HUD's pixel size (16 GUI pixels x GUI scale): R.E.P.O. objects in
    /// perspective, Minecraft models (TNT, shield) with their model json's "gui" transform in orthographic view.
    internal static class IconCache
    {
        const int Layer = 31;
        static readonly Dictionary<string, Texture2D> icons = new Dictionary<string, Texture2D>();
        static Camera cam;
        static Light light;

        /// Icon size in screen pixels; McHud sets it from the GUI scale. Icons re-render when it changes.
        public static int PixelSize = 64;

        static bool Fresh(Texture2D t) => t && t.width == Mathf.Clamp(PixelSize, 16, 256);

        public static Texture2D Get(ItemStack s)
        {
            if (icons.TryGetValue(s.Key, out var t) && Fresh(t)) return t;
            foreach (var o in s.Objects) if (o) return ForObject(o);
            return t ? t : McAssets.White;
        }

        public static Texture2D ForObject(PhysGrabObject o)
        {
            var key = ItemStack.KeyOf(o);
            if (icons.TryGetValue(key, out var t) && Fresh(t)) return t;
            if (t) Object.Destroy(t);
            var clone = ModelUtil.CloneVisual(o.gameObject, out var b);
            t = RenderModel(ModelUtil.Normalised(clone, b, 1f), Quaternion.Euler(0, 200, 0), true);
            icons[key] = t;
            return t;
        }

        /// A Minecraft model (centred, 1 unit = 1 block) drawn the way Minecraft draws it in a slot: the model's
        /// "gui" display transform, orthographic, the slot one block tall, under white ambient light only (the model
        /// carries its own shading).
        public static Texture2D ForModel(string key, System.Func<GameObject> make, McDisplay gui)
        {
            if (icons.TryGetValue(key, out var t) && Fresh(t)) return t;
            if (t) Object.Destroy(t);
            var model = make();
            if (!model) return null;
            var holder = new GameObject("mc_icon_holder");
            var pivot = new GameObject("mc_icon_pose").transform;
            pivot.SetParent(holder.transform, false);
            model.transform.SetParent(pivot, false);
            var pose = new McPose();
            gui.Apply(pose, false);
            McPose.SetLocal(pivot, pose.Unity);
            t = RenderModel(holder, Quaternion.identity, false);
            icons[key] = t;
            return t;
        }

        /// A model of any size in perspective, like R.E.P.O. objects (the flashlight). Null when nothing showed.
        public static Texture2D ForModelPerspective(string key, System.Func<GameObject> make, Quaternion rot)
        {
            if (icons.TryGetValue(key, out var t) && Fresh(t)) return t;
            if (t) Object.Destroy(t);
            var go = make();
            if (!go) return null;
            t = RenderModel(go, rot, true);
            if (Opaque(t) < 4)
            {
                Plugin.Log.LogWarning("icon '" + key + "' rendered empty");
                Object.Destroy(t);
                return null;
            }
            icons[key] = t;
            return t;
        }

        static int Opaque(Texture2D t)
        {
            int n = 0;
            foreach (var c in t.GetPixels32()) if (c.a > 0) n++;
            return n;
        }

        static Texture2D RenderModel(GameObject go, Quaternion modelRot, bool perspective)
        {
            go.transform.position = new Vector3(0, -4000, 0);
            go.transform.rotation = modelRot;
            ModelUtil.SetLayer(go, Layer);

            if (!cam)
            {
                var cgo = new GameObject("mc_icon_cam");
                Object.DontDestroyOnLoad(cgo);
                cam = cgo.AddComponent<Camera>();
                cam.enabled = false;
                cam.cullingMask = 1 << Layer;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.allowHDR = false;
                cam.allowMSAA = false;
                cam.renderingPath = RenderingPath.Forward;
                cam.nearClipPlane = 0.05f;
                cam.farClipPlane = 20f;
                var lgo = new GameObject("mc_icon_light");
                lgo.transform.SetParent(cgo.transform, false);
                light = lgo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.cullingMask = 1 << Layer;
                light.intensity = 1.1f;
                light.enabled = false;
            }
            if (perspective)
            {
                var rot = Quaternion.Euler(28f, 0f, 0f);
                cam.orthographic = false;
                cam.fieldOfView = 25f;
                cam.transform.rotation = rot;
                cam.transform.position = go.transform.position - rot * Vector3.forward * 3.3f;
            }
            else
            {
                // Minecraft's GUI looks down its -Z (Unity +Z) and a slot is one block tall
                cam.orthographic = true;
                cam.orthographicSize = 0.5f;
                cam.transform.rotation = Quaternion.identity;
                cam.transform.position = go.transform.position - Vector3.forward * 5f;
            }
            light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                // the level's light probes would tint the icon
                r.lightProbeUsage = LightProbeUsage.Off;
                r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            }

            var fog = RenderSettings.fog;
            var amb = RenderSettings.ambientLight;
            var ambMode = RenderSettings.ambientMode;
            var probe = RenderSettings.ambientProbe;
            // perspective (R.E.P.O. objects): a key light; Minecraft models carry their shading, so plain white
            var ambient = perspective ? new Color(0.55f, 0.55f, 0.55f) : Color.white;
            RenderSettings.fog = false;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = ambient;
            var sh = new SphericalHarmonicsL2();
            sh.AddAmbientLight(ambient);
            RenderSettings.ambientProbe = sh;
            light.enabled = perspective;

            int size = Mathf.Clamp(PixelSize, 16, 256);
            var black = Shot(Color.black, size);
            var white = Shot(Color.white, size);

            light.enabled = false;
            RenderSettings.fog = fog;
            RenderSettings.ambientMode = ambMode;
            RenderSettings.ambientLight = amb;
            RenderSettings.ambientProbe = probe;
            // now, not at the end of the frame: the next icon rendered this frame uses the same spot
            Object.DestroyImmediate(go);

            // alpha from the difference between a black and a white background
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            var a = black.GetPixels(); var bw = white.GetPixels();
            var outp = new Color[a.Length];
            for (int i = 0; i < a.Length; i++)
            {
                float alpha = 1f - Mathf.Clamp01(((bw[i].r - a[i].r) + (bw[i].g - a[i].g) + (bw[i].b - a[i].b)) / 3f);
                outp[i] = alpha < 0.3f ? new Color(0, 0, 0, 0) : new Color(Mathf.Clamp01(a[i].r / alpha), Mathf.Clamp01(a[i].g / alpha), Mathf.Clamp01(a[i].b / alpha), 1f);
            }
            tex.SetPixels(outp);
            tex.Apply();
            Object.Destroy(black); Object.Destroy(white);
            return tex;
        }

        static Texture2D Shot(Color bg, int size)
        {
            var rt = RenderTexture.GetTemporary(size, size, 24, RenderTextureFormat.ARGB32);
            cam.backgroundColor = bg;
            cam.targetTexture = rt;
            cam.Render();
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false);
            t.ReadPixels(new Rect(0, 0, size, size), 0, 0);
            t.Apply();
            RenderTexture.active = prev;
            cam.targetTexture = null;
            RenderTexture.ReleaseTemporary(rt);
            return t;
        }
    }
}
