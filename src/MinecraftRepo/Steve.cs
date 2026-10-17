using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace MinecraftRepo
{
    /// Builds Minecraft's player model (64x64 skin layout) out of boxes. Units: skin pixels, origin at the feet,
    /// facing +Z. Scale the root by Plugin.SteveScale to get metres.
    internal class SteveModel
    {
        public Transform Root, Body, Head, ArmR, ArmL, LegR, LegL;
        static Material skinMat;
        static readonly Dictionary<string, Mesh> meshes = new Dictionary<string, Mesh>();

        static Material SkinMat => skinMat ? skinMat : skinMat = Mats.LitTex(McAssets.Skin);

        static Mesh Part(string name, Vector3 min, Vector3 max, int u, int v, int w, int h, int d, int ou, int ov, float inflate)
        {
            if (meshes.TryGetValue(name, out var m)) return m;
            var mb = new MeshBuilder();
            mb.SkinBox(min, max, u, v, w, h, d, McAssets.Skin, false);
            if (ou >= 0)
            {
                var inf = Vector3.one * inflate;
                mb.SkinBox(min - inf, max + inf, ou, ov, w, h, d, McAssets.Skin, true);
            }
            return meshes[name] = mb.Build("steve_" + name);
        }

        static Transform Make(string name, Transform parent, Vector3 pivot, Mesh mesh)
        {
            var go = new GameObject("steve_" + name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pivot;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = SkinMat;
            return go.transform;
        }

        public static SteveModel Build(Transform parent)
        {
            var s = new SteveModel();
            var root = new GameObject("mc_steve").transform;
            root.SetParent(parent, false);
            s.Root = root;
            var holder = new GameObject("pixels").transform;
            holder.SetParent(root, false);
            holder.localScale = Vector3.one * Plugin.SteveScale.Value;

            // body pivots at the hips so crouching leans the upper body
            s.Body = Make("body", holder, new Vector3(0, 12, 0), Part("body", new Vector3(-4, 0, -2), new Vector3(4, 12, 2), 16, 16, 8, 12, 4, 16, 32, 0.25f));
            s.Head = Make("head", s.Body, new Vector3(0, 12, 0), Part("head", new Vector3(-4, 0, -4), new Vector3(4, 8, 4), 0, 0, 8, 8, 8, 32, 0, 0.5f));
            // arms pivot where PlayerModel's do ((-5, 2, 0) in Minecraft's model space): 2 pixels below the shoulder top and a
            // pixel in from the arm's middle, so Minecraft's arm poses and held-item offsets apply unchanged
            s.ArmR = Make("armR", s.Body, new Vector3(5, 10, 0), Part("armR_body", new Vector3(-1, -10, -2), new Vector3(3, 2, 2), 40, 16, 4, 12, 4, 40, 32, 0.25f));
            s.ArmL = Make("armL", s.Body, new Vector3(-5, 10, 0), Part("armL_body", new Vector3(-3, -10, -2), new Vector3(1, 2, 2), 32, 48, 4, 12, 4, 48, 48, 0.25f));
            s.LegR = Make("legR", holder, new Vector3(2, 12, 0), Part("legR", new Vector3(-2, -12, -2), new Vector3(2, 0, 2), 0, 16, 4, 12, 4, 0, 32, 0.25f));
            s.LegL = Make("legL", holder, new Vector3(-2, 12, 0), Part("legL", new Vector3(-2, -12, -2), new Vector3(2, 0, 2), 16, 48, 4, 12, 4, 0, 48, 0.25f));
            return s;
        }

        /// Just the right arm, for the first-person hand. Pivot at the shoulder, arm hangs along -Y.
        public static GameObject Arm(Transform parent)
        {
            var t = Make("fp_arm", parent, Vector3.zero, Part("armR", new Vector3(-2, -12, -2), new Vector3(2, 0, 2), 40, 16, 4, 12, 4, 40, 32, 0.25f));
            return t.gameObject;
        }

        public void SetShadowMode(ShadowCastingMode mode)
        {
            foreach (var r in Root.GetComponentsInChildren<MeshRenderer>(true))
                if (r.shadowCastingMode != mode) r.shadowCastingMode = mode;
        }

        /// Minecraft's HumanoidModel: legs from the walk animation (walkPhase = limbSwing * 0.6662), arms from `arms`, head
        /// pitch, sneaking, and the body twist of a swing. The arms hang from the body, so their Minecraft rotations (relative
        /// to the model) are taken back out of the body's lean and twist.
        public void Animate(float walkPhase, float walkAmount, float headPitch, bool crouching, bool crawling, ArmPoses arms)
        {
            float leg = Mathf.Cos(walkPhase) * 1.4f * walkAmount * Mathf.Rad2Deg;
            LegR.localRotation = Quaternion.Euler(leg, 0, 0);
            LegL.localRotation = Quaternion.Euler(-leg, 0, 0);

            if (crawling)
            {
                Body.parent.localRotation = Quaternion.Euler(80, 0, 0);
                Body.parent.localPosition = new Vector3(0, 0.25f, -0.6f);
                Head.localRotation = Quaternion.Euler(Mathf.Clamp(headPitch, -60, 30) - 70, 0, 0);
                Body.localRotation = Quaternion.identity;
                ArmR.localRotation = ArmPoses.Rot(arms.Right);
                ArmL.localRotation = ArmPoses.Rot(arms.Left);
                return;
            }
            Body.parent.localRotation = Quaternion.identity;
            Body.parent.localPosition = new Vector3(0, crouching ? -0.1f : 0f, 0);
            float lean = crouching ? 28f : 0f;
            var twist = Quaternion.AngleAxis(arms.BodyY * Mathf.Rad2Deg, Vector3.up);
            var body = twist * Quaternion.Euler(lean, 0, 0);
            Body.localRotation = body;
            Head.localRotation = Quaternion.Euler(-lean, 0, 0) * Quaternion.Inverse(twist) * Quaternion.Euler(Mathf.Clamp(headPitch, -85, 85), 0, 0);
            var unBody = Quaternion.Inverse(body);
            ArmR.localRotation = unBody * ArmPoses.Rot(arms.Right);
            ArmL.localRotation = unBody * ArmPoses.Rot(arms.Left);
        }
    }

    /// Replaces each R.E.P.O. robot with Steve on this PC, puts every player's Minecraft items in their model's hands,
    /// draws the first-person hand, and adds F5 views.
    internal class SteveManager : MonoBehaviour
    {
        class Entry
        {
            public PlayerAvatarVisuals Visuals;
            public SteveModel Model; // only with ReplaceWithSteve
            public readonly HeldRig Held = new HeldRig();
            public int Layer;
            public Vector3 LastPos;
            public float WalkPhase, WalkAmount;
            public bool HidFlashlight;
        }
        readonly Dictionary<PlayerAvatarVisuals, Entry> entries = new Dictionary<PlayerAvatarVisuals, Entry>();
        readonly HashSet<PlayerAvatarVisuals> seen = new HashSet<PlayerAvatarVisuals>();
        readonly bool[] show = new bool[2], free = new bool[2];
        static SteveManager instance;

        /// The local player's hands this frame (the inventory preview draws them too).
        public static readonly HandsState Local = new HandsState();

        public static int ViewMode; // 0 first person, 1 third person back, 2 third person front

        // first-person hands: [0] main (right), [1] off hand (left). Drawn by their own camera at full screen
        // resolution (R.E.P.O. renders the world into a small texture); McHud puts HandTexture under the HUD.
        class HandRig
        {
            public Transform Root, Pose, Item;
            public ItemStack Stack;
            public Texture2D Tex; // the sprite shown for items with states (bow, crossbow, fishing rod)
        }
        HandRig[] rigs = { new HandRig(), new HandRig() };
        Transform handRoot, armT;
        Camera handCam;
        RenderTexture handRT;
        static int handLayer = -1;
        float xBob, yBob, walkDist, walkBob;
        bool bobInit;
        Vector3 lastCamPos;

        /// The hands' frame this frame, null while they are hidden.
        public static RenderTexture HandTexture;

        void Start()
        {
            instance = this;
            Camera.onPreCull += PreCull;
            Camera.onPostRender += PostRender;
        }

        void OnDestroy()
        {
            Camera.onPreCull -= PreCull;
            Camera.onPostRender -= PostRender;
            HandTexture = null;
            if (handRT) { handRT.Release(); Destroy(handRT); }
        }

        void Update()
        {
            var kb = Keyboard.current;
            var p = McPlayer.Instance;
            if (kb != null && p && p.CanAct && kb.f5Key.wasPressedThisFrame) ViewMode = (ViewMode + 1) % 3;
            if (p == null || !p.Active) ViewMode = 0;
            if (ViewMode != 0 && PlayerAvatar.instance && PlayerAvatar.instance.playerAvatarVisuals)
                PlayerAvatar.instance.playerAvatarVisuals.showSelfOverrideTimer = 0.2f;
        }

        void LateUpdate()
        {
            Local.FillLocal();
            UpdateAvatars();
            UpdateHand();
        }

        // ---------- bodies and the items in their hands ----------

        void UpdateAvatars()
        {
            seen.Clear();
            // Steve also replaces the menu and lobby robots; held items are only for players in the game
            if (Plugin.ReplaceWithSteve.Value)
                foreach (var v in FindObjectsOfType<PlayerAvatarVisuals>()) seen.Add(v);
            else if (GameDirector.instance)
                foreach (var pa in GameDirector.instance.PlayerList)
                    if (pa && pa.playerAvatarVisuals) seen.Add(pa.playerAvatarVisuals);
            foreach (var v in seen)
                if (!entries.ContainsKey(v)) entries[v] = Create(v);
            var dead = new List<PlayerAvatarVisuals>();
            foreach (var kv in entries)
            {
                if (!kv.Key) { dead.Add(kv.Key); continue; }
                Tick(kv.Value);
            }
            foreach (var d in dead) entries.Remove(d);
        }

        Entry Create(PlayerAvatarVisuals v)
        {
            var e = new Entry { Visuals = v, LastPos = v.transform.position };
            // render on the robot's layer so the cameras that draw players (menu and lobby ones too) draw these
            var robot = v.meshParent ? v.meshParent.GetComponentInChildren<Renderer>(true) : null;
            e.Layer = robot ? robot.gameObject.layer : v.gameObject.layer;
            if (Plugin.ReplaceWithSteve.Value)
            {
                e.Model = SteveModel.Build(v.transform);
                ModelUtil.SetLayer(e.Model.Root.gameObject, e.Layer);
            }
            return e;
        }

        void Tick(Entry e)
        {
            var v = e.Visuals;
            var pa = v.playerAvatar;
            bool inGame = pa && !v.isMenuAvatar;
            bool isLocal = inGame && pa.isLocal;
            float dt = Mathf.Max(Time.deltaTime, 0.0001f);

            var pos = v.transform.position;
            var delta = pos - e.LastPos; delta.y = 0;
            e.LastPos = pos;
            float speed = delta.magnitude / dt;
            if (speed > 20f) speed = 0f; // teleport
            e.WalkAmount = Mathf.Lerp(e.WalkAmount, Mathf.Clamp01(speed / 4f), dt * 10f);
            e.WalkPhase += speed * dt * 2.6f;

            // what this player holds: ours, another modded player's as last sent, nothing for players without the mod
            bool remoteActive = false;
            var hands = !inGame ? null : isLocal ? Local : McNet.HandsOf(pa, out remoteActive);
            if (inGame && !isLocal && pa.flashlightController && (remoteActive || e.HidFlashlight))
            {
                // their R.E.P.O. flashlight is an item for them too: it's drawn as one, not in the left hand by R.E.P.O.
                pa.flashlightController.hideFlashlight = remoteActive;
                e.HidFlashlight = remoteActive;
            }

            // R.E.P.O. keeps the arms while tumbling or crawling, and the right one for the grab beam and the map
            bool body = inGame && !pa.isDisabled && !pa.deadSet && !pa.isTumbling && !pa.isCrawling && v.meshParent && v.meshParent.activeInHierarchy;
            var rightArm = v.playerAvatarRightArm;
            bool rightBusy = inGame && ((pa.physGrabber && pa.physGrabber.physGrabBeamActive)
                || (rightArm && rightArm.mapToolController && rightArm.mapToolController.Active));
            free[0] = body && !rightBusy;
            free[1] = body;
            show[0] = free[0] && hands != null;
            show[1] = free[1] && hands != null;

            float pitch = 0f;
            var look = isLocal && Camera.main ? Camera.main.transform : inGame && pa.localCamera ? pa.localCamera.GetOverrideTransform() : null;
            if (look) pitch = Mathf.Clamp(Mathf.DeltaAngle(0f, look.eulerAngles.x), -90f, 90f);
            bool crouch = inGame && pa.isCrouching;
            bool crawl = inGame && pa.isCrawling;
            var shadows = isLocal && ViewMode == 0 ? ShadowCastingMode.ShadowsOnly : ShadowCastingMode.On;
            var remote = inGame && !isLocal ? pa : null;
            float age = Time.time * 20f;

            if (e.Model != null)
            {
                // hide the robot (and anything R.E.P.O. re-enables later) but leave non-body effects alone
                if (v.meshParent)
                    foreach (var r in v.meshParent.GetComponentsInChildren<Renderer>(true))
                        if (!r.forceRenderingOff && !(r is ParticleSystemRenderer)) r.forceRenderingOff = true;
                bool visible = v.meshParent && v.meshParent.activeInHierarchy;
                if (pa && (pa.isDisabled || pa.deadSet || pa.isTumbling)) visible = false;
                e.Model.Root.gameObject.SetActive(visible);
                if (!visible) return;
                if (isLocal) e.Model.SetShadowMode(shadows);
                var poses = ArmPoses.Compute(hands ?? HandsState.None, pitch * Mathf.Deg2Rad, 0f, e.WalkPhase, e.WalkAmount, age, crouch && !crawl);
                e.Model.Animate(e.WalkPhase, e.WalkAmount, pitch, crouch, crawl, poses);
                e.Held.UpdateSteve(e.Model, hands ?? HandsState.None, show, shadows, e.Layer, remote);
                return;
            }
            if (!inGame) return;
            // the robot's legs keep R.E.P.O.'s own walk, so its arms only swing half as much with it
            var robotPoses = ArmPoses.Compute(hands ?? HandsState.None, pitch * Mathf.Deg2Rad, 0f, e.WalkPhase, e.WalkAmount * 0.5f, age, false);
            e.Held.UpdateRobot(v, hands ?? HandsState.None, robotPoses, show, free, shadows, e.Layer, remote, dt);
        }

        /// Adds the renderers of the items in the local player's hands (for the inventory preview of the robot).
        public static void AddLocalHeld(List<Renderer> list)
        {
            var vis = PlayerAvatar.instance ? PlayerAvatar.instance.playerAvatarVisuals : null;
            if (instance && vis && instance.entries.TryGetValue(vis, out var e)) e.Held.AddRenderers(list);
        }

        // ---------- first-person hands ----------

        void UpdateHand()
        {
            var p = McPlayer.Instance;
            var cam = Camera.main;
            bool show = p && p.Active && cam && ViewMode == 0 && !(PhysGrabber.instance && PhysGrabber.instance.grabbed);
            if (!show)
            {
                HandTexture = null;
                if (handRoot) handRoot.gameObject.SetActive(false);
                bobInit = false;
                return;
            }
            if (!handRoot) CreateHands();
            handRoot.gameObject.SetActive(true);
            handRoot.SetPositionAndRotation(cam.transform.position, cam.transform.rotation);
            if (!handRT || handRT.width != Screen.width || handRT.height != Screen.height)
            {
                if (handRT) { handRT.Release(); Destroy(handRT); }
                handRT = new RenderTexture(Screen.width, Screen.height, 24, RenderTextureFormat.ARGB32) { name = "mc_hands" };
            }
            // the camera is rebuilt after each level change, the texture is not
            if (handCam.targetTexture != handRT) handCam.targetTexture = handRT;
            if (Time.frameCount % 30 == 0) HideHandLayer();
            HandTexture = handRT;
            if (handCheckAt < 0f) handCheckAt = Time.time + 2f;
            else if (handCheckAt > 0f && Time.time > handCheckAt) { handCheckAt = 0f; LogHandPixels(); }

            var view = ViewPose(cam, Time.deltaTime);
            // Minecraft shows only the bow's or crossbow's hand while it is drawn, and only the main hand while it
            // holds a charged crossbow
            int using_ = p.UsingHand;
            var usedStack = using_ >= 0 ? p.Inventory.Hand(using_) : null;
            int only = usedStack != null && (usedStack.IsMc("bow") || usedStack.IsMc("crossbow")) ? using_ : -1;
            var main = p.Inventory.Held;
            if (using_ < 0 && main != null && main.IsMc("crossbow") && main.Loaded != null) only = 0;
            UpdateRig(rigs[0], 0, p, view, only != 1);
            UpdateRig(rigs[1], 1, p, view, only != 0);
            UpdateArm(p, view, only != 1 && p.Inventory.Held == null);
        }

        float handCheckAt = -1f;

        /// One check per level that the hand camera draws something (the log line is the oracle).
        void LogHandPixels()
        {
            var prev = RenderTexture.active;
            RenderTexture.active = handRT;
            var t = new Texture2D(handRT.width, handRT.height, TextureFormat.RGBA32, false);
            t.ReadPixels(new Rect(0, 0, handRT.width, handRT.height), 0, 0);
            RenderTexture.active = prev;
            int n = 0;
            foreach (var c in t.GetPixels32()) if (c.a > 0) n++;
            Destroy(t);
            Plugin.Log.LogInfo("first-person hands: " + n + " visible pixels of " + handRT.width + "x" + handRT.height);
        }

        void CreateHands()
        {
            handCheckAt = -1f;
            if (handLayer < 0)
            {
                // an unnamed layer that no R.E.P.O. camera needs; 31 is the icon/avatar preview layer
                handLayer = 31;
                for (int i = 30; i >= 8; i--)
                    if (string.IsNullOrEmpty(LayerMask.LayerToName(i))) { handLayer = i; break; }
                Plugin.Log.LogInfo("first-person hands on layer " + handLayer);
            }
            handRoot = new GameObject("mc_hands").transform;
            handCam = handRoot.gameObject.AddComponent<Camera>();
            handCam.cullingMask = 1 << handLayer;
            handCam.clearFlags = CameraClearFlags.SolidColor;
            handCam.backgroundColor = new Color(0, 0, 0, 0);
            handCam.fieldOfView = 70f; // Minecraft draws the hand with a fixed 70 degree FOV
            handCam.nearClipPlane = 0.05f;
            handCam.farClipPlane = 8f;
            handCam.renderingPath = RenderingPath.Forward;
            handCam.allowHDR = false;
            handCam.allowMSAA = false;
            handCam.useOcclusionCulling = false;
            handCam.depth = -100f;

            // keeps held items readable in dark rooms; level lights add on top
            var lgo = new GameObject("mc_hand_light");
            lgo.transform.SetParent(handRoot, false);
            lgo.transform.localRotation = Quaternion.Euler(35f, -25f, 0f);
            var l = lgo.AddComponent<Light>();
            l.type = LightType.Directional;
            l.cullingMask = 1 << handLayer;
            l.intensity = 0.55f;
            l.shadows = LightShadows.None;

            rigs = new[] { new HandRig(), new HandRig() };
            armT = null;
            HideHandLayer();
        }

        void HideHandLayer()
        {
            foreach (var c in Camera.allCameras)
                if (c != handCam && (c.cullingMask & (1 << handLayer)) != 0) c.cullingMask &= ~(1 << handLayer);
        }

        /// Shared by both hands: Minecraft's walking bob (bobView) and the hands lagging behind the look
        /// (xBob/yBob, half the gap per tick, 0.1 of the difference applied).
        McPose ViewPose(Camera cam, float dt)
        {
            float pitch = Mathf.DeltaAngle(0f, cam.transform.eulerAngles.x), yaw = cam.transform.eulerAngles.y;
            var pos = cam.transform.position;
            if (!bobInit) { xBob = pitch; yBob = yaw; lastCamPos = pos; walkBob = 0f; bobInit = true; }
            float lag = 1f - Mathf.Pow(0.5f, dt * 20f);
            xBob += (pitch - xBob) * lag;
            yBob += Mathf.DeltaAngle(yBob, yaw) * lag;
            var d = pos - lastCamPos; d.y = 0f;
            lastCamPos = pos;
            float speed = dt > 0f ? d.magnitude / dt : 0f;
            if (speed > 20f) speed = 0f;
            walkDist += speed * dt * 0.6f;
            walkBob += (Mathf.Min(0.1f, speed / 20f) - walkBob) * (1f - Mathf.Pow(0.6f, dt * 20f));

            var v = new McPose();
            float w = -walkDist * Mathf.PI;
            v.T(Mathf.Sin(w) * walkBob * 0.5f, -Mathf.Abs(Mathf.Cos(w) * walkBob), 0f);
            v.RZ(Mathf.Sin(w) * walkBob * 3f);
            v.RX(Mathf.Abs(Mathf.Cos(w - 0.2f) * walkBob) * 5f);
            v.RX((pitch - xBob) * 0.1f);
            v.RY(Mathf.DeltaAngle(yBob, yaw) * 0.1f);
            return v;
        }

        // Minecraft's applyItemArmTransform
        static void ArmTransform(McPose p, int k, float equipped) => p.T(k * 0.56f, -0.52f + equipped * -0.6f, -0.72f);

        /// Minecraft's ItemInHandRenderer.renderArmWithItem: use animations (bow, block, a grenade wound up like
        /// a trident), otherwise the swing, then the item model's first-person display transform.
        void UpdateRig(HandRig r, int hand, McPlayer p, McPose view, bool visible)
        {
            if (!r.Root)
            {
                r.Root = new GameObject(hand == 0 ? "mc_hand_main" : "mc_hand_off").transform;
                r.Root.SetParent(handRoot, false);
                r.Pose = new GameObject("pose").transform;
                r.Pose.SetParent(r.Root, false);
            }
            var held = p.Inventory.Hand(hand);
            if (held != r.Stack)
            {
                if (r.Item) Destroy(r.Item.gameObject);
                r.Item = null;
                r.Stack = held;
                r.Tex = null;
                if (held != null)
                {
                    var m = HandModel(held);
                    m.transform.SetParent(r.Pose, false);
                    r.Item = m.transform;
                    PrepareFirstPerson(m);
                }
            }
            r.Root.gameObject.SetActive(visible && held != null);
            if (!visible || held == null) return;

            int k = hand == 0 ? 1 : -1; // the main arm is the right one
            float equipped = 1f - (hand == 0 ? p.EquipProgress : p.OffEquipProgress); // Minecraft: 0 = fully raised
            float swingT = hand == 0 ? p.SwingTime : p.OffSwingTime;
            float sw = swingT >= 0f ? Mathf.Clamp01(swingT / McPlayer.SwingDuration) : 0f;
            bool using_ = p.UsingHand == hand;
            float useTicks = using_ ? p.UseSeconds * 20f : 0f;

            var pose = view.Copy();
            if (using_ && held.IsMc("bow"))
            {
                ArmTransform(pose, k, equipped);
                pose.T(k * -0.2785682f, 0.18344387f, 0.15731531f);
                pose.RX(-13.935f);
                pose.RY(k * 35.3f);
                pose.RZ(k * -9.785f);
                float f12 = useTicks / 20f;
                f12 = Mathf.Min(1f, (f12 * f12 + f12 * 2f) / 3f);
                if (f12 > 0.1f) pose.T(0f, Mathf.Sin((useTicks - 0.1f) * 1.3f) * (f12 - 0.1f) * 0.004f, 0f);
                pose.T(0f, 0f, f12 * 0.04f);
                pose.S(1f, 1f, 1f + f12 * 0.2f);
                pose.RY(-k * 45f);
            }
            else if (using_ && held.IsMc("crossbow"))
            {
                ArmTransform(pose, k, equipped);
                pose.T(k * -0.4785682f, -0.094387f, 0.05731531f);
                pose.RX(-11.935f);
                pose.RY(k * 65.3f);
                pose.RZ(k * -9.785f);
                float f13 = Mathf.Min(1f, useTicks / (McPlayer.CrossbowChargeSeconds * 20f));
                if (f13 > 0.1f) pose.T(0f, Mathf.Sin((useTicks - 0.1f) * 1.3f) * (f13 - 0.1f) * 0.004f, 0f);
                pose.T(0f, 0f, f13 * 0.04f);
                pose.S(1f, 1f, 1f + f13 * 0.2f);
                pose.RY(-k * 45f);
            }
            else if (using_ && p.ThrowCharge >= 0f)
            {
                // a grenade wound up for the throw: drawn back and up toward the shoulder as the charge (1 s) builds,
                // tilted back, trembling once fully charged like a drawn bow. (Minecraft's trident pose lifts a long
                // model 0.7 up from the hand, which put a round grenade in the middle of the view.)
                float f = Mathf.Min(1f, useTicks / 20f);
                float e = f * (2f - f); // eased
                ArmTransform(pose, k, equipped);
                pose.T(k * 0.12f * e, 0.16f * e, 0.12f * e);
                if (f > 0.1f) pose.T(0f, Mathf.Sin((useTicks - 0.1f) * 1.3f) * (f - 0.1f) * 0.004f, 0f);
                pose.RX(25f * e);
                pose.RZ(k * -15f * e);
            }
            else if (using_)
            {
                ArmTransform(pose, k, equipped); // shield: only the blocking display changes
            }
            else
            {
                float sq = Mathf.Sqrt(sw);
                pose.T(k * -0.4f * Mathf.Sin(sq * Mathf.PI), 0.2f * Mathf.Sin(sq * Mathf.PI * 2f), -0.2f * Mathf.Sin(sw * Mathf.PI));
                ArmTransform(pose, k, equipped);
                // applyItemArmAttackTransform
                pose.RY(k * (45f + Mathf.Sin(sw * sw * Mathf.PI) * -20f));
                float f1 = Mathf.Sin(sq * Mathf.PI);
                pose.RZ(k * f1 * -20f);
                pose.RX(f1 * -80f);
                pose.RY(k * -45f);
                // a charged crossbow in the main hand is held up, aimed
                if (hand == 0 && held.IsMc("crossbow") && held.Loaded != null && sw < 0.001f)
                {
                    pose.T(k * -0.641864f, 0f, 0f);
                    pose.RY(k * 10f);
                }
            }

            if (RepoWeapons.Gun(held))
            {
                // not a Minecraft item: held at the hand pointing where you look, kicked up by each shot
                float kick = Mathf.Exp(-(Time.time - p.GunFiredAt[hand]) * 12f);
                r.Pose.localPosition = (Vector3)pose.Unity.GetColumn(3) + new Vector3(0f, 0.02f * kick, -0.08f * kick);
                r.Pose.localRotation = Quaternion.Euler(-14f * kick, 0f, 0f);
                r.Pose.localScale = Vector3.one;
                return;
            }
            if (held.IsMc("flashlight"))
            {
                // not a Minecraft item: held at the hand, pointing where you look like R.E.P.O.'s flashlight
                r.Pose.localPosition = pose.Unity.GetColumn(3);
                r.Pose.localRotation = Quaternion.identity;
                r.Pose.localScale = Vector3.one;
                return;
            }
            Display(held, k < 0, using_).Apply(pose, k < 0);
            McPose.SetLocal(r.Pose, pose.Unity);

            var tex = McItems.StateSprite(held);
            if (tex != null && tex != r.Tex && r.Item)
            {
                r.Tex = tex;
                var mf = r.Item.GetComponentInChildren<MeshFilter>();
                var mr = r.Item.GetComponentInChildren<MeshRenderer>();
                if (mf) mf.sharedMesh = ModelUtil.ItemMesh(tex);
                if (mr) mr.sharedMaterial = ModelUtil.ItemMat(tex);
            }
        }

        /// The first-person display entry of the item's model json.
        static McDisplay Display(ItemStack s, bool left, bool using_)
        {
            if (s.IsMc("shield"))
                return using_ ? (left ? McDisplay.ShieldBlockingLeft : McDisplay.ShieldBlockingRight) : (left ? McDisplay.ShieldLeft : McDisplay.ShieldRight);
            if (s.IsMc("crossbow")) return left ? McDisplay.CrossbowLeft : McDisplay.CrossbowRight;
            if (s.IsMc("fishing_rod")) return left ? McDisplay.RodLeft : McDisplay.RodRight;
            if (s.IsTool || s.IsMc("bow") || RepoWeapons.Melee(s)) return left ? McDisplay.HandheldLeft : McDisplay.HandheldRight;
            if (s.Kind == ItemKind.Mc && McItems.IsSprite(s.Id)) return McDisplay.HandheldRight; // generated.json: no left entry
            return left ? McDisplay.BlockLeft : McDisplay.BlockRight; // TNT, and R.E.P.O. objects shown like blocks
        }

        /// Item models in Minecraft's units: one block wide, centred like the model after its -0.5 offset.
        internal static GameObject HandModel(ItemStack s)
        {
            if (s.IsTool) return ModelUtil.SpriteModel(s.Icon, 1f);
            if (s.IsMc("shield")) return McItems.ShieldItemModel();
            if (s.IsMc("flashlight")) return McItems.Model("flashlight", 0.45f);
            if (s.Kind == ItemKind.Mc) return McItems.Model(s.Id, 1f);
            var gun = RepoWeapons.Gun(s);
            if (gun) return GunModel(gun);
            var melee = RepoWeapons.Melee(s);
            if (melee) return MeleeModel(melee);
            return ModelUtil.HeldModel(s, 1f);
        }

        /// A R.E.P.O. gun turned so its muzzle points along +Z (where the hand camera looks).
        static GameObject GunModel(ItemGun g)
        {
            var clone = ModelUtil.CloneVisual(g.gameObject, out var b);
            var m = RepoWeapons.Muzzle(g);
            var rot = Quaternion.identity;
            if (m)
            {
                var inv = Quaternion.Inverse(g.transform.rotation);
                rot = Quaternion.Inverse(Quaternion.LookRotation(inv * m.forward, inv * m.up));
            }
            return ModelUtil.Oriented(clone, b, 0.55f, rot);
        }

        /// A R.E.P.O. melee weapon laid along a Minecraft tool sprite: its long axis on the sprite diagonal with
        /// the head (where its hurt collider sits) at the tip, so the handheld display holds it like a sword.
        static GameObject MeleeModel(ItemMelee w)
        {
            var clone = ModelUtil.CloneVisual(w.gameObject, out var b);
            var size = b.size;
            int a = size.x >= size.y && size.x >= size.z ? 0 : size.y >= size.z ? 1 : 2;
            int c = a == 0 ? (size.y >= size.z ? 1 : 2) : a == 1 ? (size.x >= size.z ? 0 : 2) : (size.x >= size.y ? 0 : 1);
            var axis = Vector3.zero; axis[a] = 1f;
            var side = Vector3.zero; side[c] = 1f;
            var hc = w.GetComponentInChildren<HurtCollider>(true);
            var head = hc ? w.transform.InverseTransformPoint(hc.transform.position) : b.center + axis;
            if (Vector3.Dot(head - b.center, axis) < 0f) axis = -axis;
            var tip = new Vector3(1f, 1f, 0f).normalized;
            var across = new Vector3(-1f, 1f, 0f).normalized;
            var rot = Quaternion.LookRotation(tip, across) * Quaternion.Inverse(Quaternion.LookRotation(axis, side));
            return ModelUtil.Oriented(clone, b, 1.3f, rot);
        }

        /// The empty main hand: Minecraft's renderPlayerArm and the player model's right arm.
        void UpdateArm(McPlayer p, McPose view, bool visible)
        {
            if (!armT)
            {
                armT = SteveModel.Arm(handRoot).transform;
                PrepareFirstPerson(armT.gameObject);
            }
            armT.gameObject.SetActive(visible);
            if (!visible) return;

            float equipped = 1f - p.EquipProgress;
            float sw = p.SwingTime >= 0f ? Mathf.Clamp01(p.SwingTime / McPlayer.SwingDuration) : 0f;
            float sq = Mathf.Sqrt(sw);
            var pose = view.Copy();
            pose.T(-0.3f * Mathf.Sin(sq * Mathf.PI) + 0.64f, 0.4f * Mathf.Sin(sq * Mathf.PI * 2f) - 0.6f + equipped * -0.6f, -0.4f * Mathf.Sin(sw * Mathf.PI) - 0.72f);
            pose.RY(45f);
            pose.RY(Mathf.Sin(sq * Mathf.PI) * 70f);
            pose.RZ(Mathf.Sin(sw * sw * Mathf.PI) * -20f);
            pose.T(-1f, 3.6f, 3.5f);
            pose.RZ(120f);
            pose.RX(200f);
            pose.RY(-135f);
            pose.T(5.6f, 0f, 0f);
            // PlayerModel's right arm: pivot (-5, 2, 0) pixels, idle sway zRot 0.1 rad
            pose.T(-5f / 16f, 2f / 16f, 0f);
            pose.RZ(0.1f * Mathf.Rad2Deg);
            // our arm mesh (pixels, y up, front +Z, pivot at the shoulder top) into Minecraft's part space
            pose.S(1f / 16f, 1f / 16f, 1f / 16f);
            pose.T(-1f, -2f, 0f);
            pose.S(-1f, -1f, -1f);
            McPose.SetLocal(armT, pose.UnityFromMc);
        }

        void PrepareFirstPerson(GameObject go)
        {
            ModelUtil.SetShadows(go, ShadowCastingMode.Off);
            foreach (var r in go.GetComponentsInChildren<Renderer>(true)) r.receiveShadows = false;
            ModelUtil.SetLayer(go, handLayer);
        }

        // ---------- F5 third-person camera ----------

        Vector3 savedPos;
        Quaternion savedRot;
        Camera movedCam;

        ShadowQuality savedShadowQuality;

        void PreCull(Camera cam)
        {
            if (cam == handCam && handCam)
            {
                // the hands receive no shadows; don't let their camera render the level's shadow maps again
                savedShadowQuality = QualitySettings.shadows;
                QualitySettings.shadows = ShadowQuality.Disable;
                return;
            }
            if (ViewMode == 0 || cam != Camera.main || movedCam) return;
            var p = McPlayer.Instance;
            if (p == null || !p.Active) return;
            movedCam = cam;
            savedPos = cam.transform.position;
            savedRot = cam.transform.rotation;
            var fwd = savedRot * Vector3.forward;
            var dir = ViewMode == 1 ? -fwd : fwd;
            float dist = 4f;
            var mask = ~0;
            int l = LayerMask.NameToLayer("Player"); if (l >= 0) mask &= ~(1 << l);
            l = LayerMask.NameToLayer("PlayerVisuals"); if (l >= 0) mask &= ~(1 << l);
            if (Physics.SphereCast(savedPos, 0.15f, dir, out var hit, dist, mask, QueryTriggerInteraction.Ignore))
                dist = Mathf.Max(0.3f, hit.distance - 0.1f);
            cam.transform.position = savedPos + dir * dist;
            cam.transform.rotation = ViewMode == 1 ? savedRot : Quaternion.LookRotation(-fwd, savedRot * Vector3.up);
        }

        void PostRender(Camera cam)
        {
            if (cam == handCam && handCam) { QualitySettings.shadows = savedShadowQuality; return; }
            if (cam != movedCam) return;
            cam.transform.position = savedPos;
            cam.transform.rotation = savedRot;
            movedCam = null;
        }
    }
}
