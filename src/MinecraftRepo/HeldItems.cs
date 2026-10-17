using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MinecraftRepo
{
    /// What one player shows in their hands: the stacks (0 = main hand, the right arm; 1 = off hand, the left arm), the item
    /// in use and the arm swings. The local player's comes from McPlayer, other players' from McNet.
    internal class HandsState
    {
        public const byte UseNone = 0, UseBow = 1, UseCrossbow = 2, UseShield = 3, UseThrow = 4;

        /// Nothing in either hand (never written to).
        public static readonly HandsState None = new HandsState();

        public readonly ItemStack[] Hands = new ItemStack[2];
        public readonly bool[] Cast = new bool[2];      // a fishing rod whose bobber is out
        public int UsingHand = -1;
        public byte UseKind;
        public float UseSeconds = -1f;
        public readonly float[] Swing = { -1f, -1f };   // seconds into the arm swing, -1 = none
        public bool FlashlightOn = true;

        public float UseOf(int hand) => UsingHand == hand ? UseSeconds : -1f;
        public bool Blocking(int hand) => UsingHand == hand && UseKind == UseShield;

        /// The local player's hands; empty while the mod isn't driving the player.
        public void FillLocal()
        {
            var p = McPlayer.Instance;
            bool on = p && p.Active;
            for (int i = 0; i < 2; i++)
            {
                Hands[i] = on ? p.Inventory.Hand(i) : null;
                Cast[i] = BobberEntity.IsCastWith(Hands[i]);
            }
            UsingHand = on ? p.UsingHand : -1;
            UseKind = on ? p.UseKind : UseNone;
            UseSeconds = on ? p.UseSeconds : -1f;
            Swing[0] = on ? p.SwingTime : -1f;
            Swing[1] = on ? p.OffSwingTime : -1f;
            FlashlightOn = !p || p.FlashlightOn;
        }
    }

    /// Minecraft's HumanoidModel arm poses (PlayerRenderer.getArmPose). Gun and Flashlight are R.E.P.O. items, aimed where
    /// the player looks like R.E.P.O.'s own flashlight and grabber arm.
    internal enum ArmPose { Empty, Item, Block, Bow, CrossbowCharge, CrossbowHold, Throw, Gun, Flashlight }

    /// Minecraft's HumanoidModel.setupAnim for the arms (1.21: walk swing, poseRightArm/poseLeftArm, setupAttackAnimation,
    /// sneaking, bobArms) as model-part rotations in radians, applied Z * Y * X. Minecraft's model space (y down, facing -z)
    /// differs from ours (y up, facing +z) by -I, which commutes with every rotation, so the same angles turn our arms.
    internal struct ArmPoses
    {
        public Vector3 Right, Left;
        public ArmPose RightPose, LeftPose;
        public float BodyY;
        public int SwingArm; // -1 none, 0 right, 1 left

        public Vector3 Arm(int i) => i == 0 ? Right : Left;
        public ArmPose Pose(int i) => i == 0 ? RightPose : LeftPose;

        /// The arm is posed by an item (its own, or the other hand's two-handed one) or swinging, rather than hanging.
        public bool Moves(int i) => Pose(i) != ArmPose.Empty || TwoHanded(RightPose) || TwoHanded(LeftPose) || SwingArm == i;

        public static bool TwoHanded(ArmPose p) => p == ArmPose.Bow || p == ArmPose.CrossbowCharge || p == ArmPose.CrossbowHold;

        public static Quaternion Rot(Vector3 r) =>
            Quaternion.AngleAxis(r.z * Mathf.Rad2Deg, Vector3.forward) * Quaternion.AngleAxis(r.y * Mathf.Rad2Deg, Vector3.up)
            * Quaternion.AngleAxis(r.x * Mathf.Rad2Deg, Vector3.right);

        static ArmPose PoseOf(HandsState h, int hand)
        {
            var s = h.Hands[hand];
            if (s == null) return ArmPose.Empty;
            if (h.UsingHand == hand)
            {
                switch (h.UseKind)
                {
                    case HandsState.UseBow: return ArmPose.Bow;
                    case HandsState.UseCrossbow: return ArmPose.CrossbowCharge;
                    case HandsState.UseShield: return ArmPose.Block;
                    case HandsState.UseThrow: return ArmPose.Throw;
                }
            }
            else if (s.IsMc("crossbow") && s.Loaded != null && h.Swing[0] < 0f && h.Swing[1] < 0f) return ArmPose.CrossbowHold;
            if (s.IsMc("flashlight")) return ArmPose.Flashlight;
            if (RepoWeapons.Gun(s)) return ArmPose.Gun;
            return ArmPose.Item;
        }

        /// headX: head pitch (+ = looking down), headY: head yaw against the body, radians. walkPhase/walkAmount: the walk
        /// animation (Minecraft's limbSwing * 0.6662 and limbSwingAmount). age: ticks, for the idle sway.
        public static ArmPoses Compute(HandsState h, float headX, float headY, float walkPhase, float walkAmount, float age, bool crouching)
        {
            var a = new ArmPoses { SwingArm = -1 };
            a.RightPose = PoseOf(h, 0);
            a.LeftPose = PoseOf(h, 1);
            if (TwoHanded(a.RightPose)) a.LeftPose = h.Hands[1] != null ? ArmPose.Item : ArmPose.Empty;
            float c = Mathf.Cos(walkPhase) * walkAmount; // cos(x + PI) * 2 * amount * 0.5 for the right arm
            a.Right = new Vector3(-c, 0f, 0f);
            a.Left = new Vector3(c, 0f, 0f);

            // the arm in use is posed last so its two-handed pose wins; otherwise a two-handed left pose wins
            bool rightLast = h.UsingHand >= 0 ? h.UsingHand == 0 : !TwoHanded(a.LeftPose);
            float full = h.UseKind == HandsState.UseThrow ? 1f : McPlayer.CrossbowChargeSeconds; // a grenade throw charges in 1 s
            float charge = h.UseSeconds >= 0f ? Mathf.Clamp01(h.UseSeconds / full) : 0f;
            if (rightLast) { PoseArm(ref a, false, headX, headY, charge); PoseArm(ref a, true, headX, headY, charge); }
            else { PoseArm(ref a, true, headX, headY, charge); PoseArm(ref a, false, headX, headY, charge); }

            // setupAttackAnimation: the swing twists the body and swings the arm down across it
            a.SwingArm = h.Swing[0] >= 0f ? 0 : h.Swing[1] >= 0f ? 1 : -1;
            if (a.SwingArm >= 0)
            {
                float t = Mathf.Clamp01(h.Swing[a.SwingArm] / McPlayer.SwingDuration);
                float bodyY = Mathf.Sin(Mathf.Sqrt(t) * Mathf.PI * 2f) * 0.2f;
                if (a.SwingArm == 1) bodyY = -bodyY;
                a.BodyY = bodyY;
                a.Right.y += bodyY;
                a.Left.y += bodyY;
                a.Left.x += bodyY;
                float f = 1f - t;
                f *= f;
                f *= f;
                f = 1f - f;
                float f1 = Mathf.Sin(f * Mathf.PI);
                float f2 = Mathf.Sin(t * Mathf.PI) * -(headX - 0.7f) * 0.75f;
                ref var arm = ref (a.SwingArm == 0 ? ref a.Right : ref a.Left);
                arm.x -= f1 * 1.2f + f2;
                arm.y += bodyY * 2f;
                arm.z += Mathf.Sin(t * Mathf.PI) * -0.4f;
            }
            if (crouching) { a.Right.x += 0.4f; a.Left.x += 0.4f; }

            // bobArms
            float sway = Mathf.Cos(age * 0.09f) * 0.05f + 0.05f, nod = Mathf.Sin(age * 0.067f) * 0.05f;
            a.Right.z += sway; a.Right.x += nod;
            a.Left.z -= sway; a.Left.x -= nod;
            return a;
        }

        static void PoseArm(ref ArmPoses a, bool right, float headX, float headY, float charge)
        {
            var pose = right ? a.RightPose : a.LeftPose;
            ref var arm = ref (right ? ref a.Right : ref a.Left);
            float k = right ? 1f : -1f;
            switch (pose)
            {
                case ArmPose.Empty:
                    arm.y = 0f;
                    break;
                case ArmPose.Item:
                    arm.x = arm.x * 0.5f - Mathf.PI / 10f;
                    arm.y = 0f;
                    break;
                case ArmPose.Block:
                    arm.x = arm.x * 0.5f - 0.9424779f + Mathf.Clamp(headX, -Mathf.PI * 4f / 9f, 0.43633232f);
                    arm.y = -k * 30f * Mathf.Deg2Rad + Mathf.Clamp(headY, -Mathf.PI / 6f, Mathf.PI / 6f);
                    break;
                case ArmPose.Bow:
                    a.Right.y = -0.1f + headY - (right ? 0f : 0.4f);
                    a.Left.y = 0.1f + headY + (right ? 0.4f : 0f);
                    a.Right.x = -Mathf.PI / 2f + headX;
                    a.Left.x = -Mathf.PI / 2f + headX;
                    break;
                case ArmPose.CrossbowCharge:
                {
                    // AnimationUtils.animateCrossbowCharge
                    ref var hold = ref (right ? ref a.Right : ref a.Left);
                    ref var other = ref (right ? ref a.Left : ref a.Right);
                    hold.y = -k * 0.8f;
                    hold.x = -0.97079635f;
                    other.x = hold.x;
                    other.y = Mathf.Lerp(0.4f, 0.85f, charge) * k;
                    other.x = Mathf.Lerp(other.x, -Mathf.PI / 2f, charge);
                    break;
                }
                case ArmPose.CrossbowHold:
                {
                    // AnimationUtils.animateCrossbowHold
                    ref var hold = ref (right ? ref a.Right : ref a.Left);
                    ref var other = ref (right ? ref a.Left : ref a.Right);
                    hold.y = -k * 0.3f + headY;
                    other.y = k * 0.6f + headY;
                    hold.x = -Mathf.PI / 2f + headX + 0.1f;
                    other.x = -1.5f + headX;
                    break;
                }
                case ArmPose.Throw:
                {
                    // a grenade wound up for an overhand throw: from the item pose, the arm rises up and a little back as
                    // the charge builds, out to the side so the grenade clears the head (Minecraft's spear pose, straight
                    // up, put it on top of the robot's head)
                    float e = charge * (2f - charge);
                    arm.x = arm.x * 0.5f * (1f - e) + Mathf.Lerp(-Mathf.PI / 10f, -3.3f, e);
                    arm.y = 0f;
                    arm.z = k * 0.5f * e;
                    break;
                }
                case ArmPose.Gun:
                    // one arm of the crossbow hold: pointing where the player looks
                    arm.x = -Mathf.PI / 2f + headX + 0.1f;
                    arm.y = -k * 0.3f + headY;
                    break;
                case ArmPose.Flashlight:
                    // held out in front and following half the look, like R.E.P.O.'s flashlight arm
                    arm.x = -1.1f + headX * 0.5f;
                    arm.y = -k * 0.15f + headY;
                    break;
            }
        }
    }

    /// The items in one player model's hands (the R.E.P.O. robot or Steve) and its arms posed to hold them, following
    /// Minecraft's ItemInHandLayer: the item sits at the bottom of the arm, 2 pixels in front of it, turned (-90 x, 180 y) and
    /// then by the model json's third-person display transform. The models are children of the arms, so they follow
    /// everything that moves the body after this.
    internal class HeldRig
    {
        class Held
        {
            public Transform Root;
            public string Sig;
            public Texture2D Tex;
            public bool RealSize;  // R.E.P.O. guns and the flashlight: their own size in metres, pointing along the arm
            public float Length;   // real-size items: metres from back to front
            public ShadowCastingMode Shadows = (ShadowCastingMode)(-1);
            public Light Light;    // another player's flashlight beam or torch light
        }

        readonly Held[] held = { new Held(), new Held() };

        // Minecraft's model space (y down, facing -z) to ours, and Minecraft's item space to our item meshes
        static readonly Matrix4x4 C = Matrix4x4.Scale(new Vector3(-1f, -1f, -1f));
        static readonly Matrix4x4 Flip = Matrix4x4.Scale(new Vector3(1f, 1f, -1f));

        // ---------- Steve ----------

        /// Steve's arms pivot where Minecraft's do, so the whole of Minecraft's chain applies (arm units: skin pixels).
        public void UpdateSteve(SteveModel m, HandsState h, bool[] show, ShadowCastingMode shadows, int layer, PlayerAvatar remote)
        {
            float blockMetres = 16f * Plugin.SteveScale.Value;
            for (int i = 0; i < 2; i++)
            {
                var it = held[i];
                var s = show[i] ? h.Hands[i] : null;
                var arm = i == 0 ? m.ArmR : m.ArmL;
                Sync(it, s, arm, layer);
                if (!it.Root) continue;
                // the bottom centre of the arm: 10 pixels down from the pivot, the arm's middle a pixel outward
                var local = Matrix4x4.Translate(new Vector3(i == 0 ? 1f : -1f, -10f, 0f)) * Matrix4x4.Scale(Vector3.one * 16f)
                    * FromHand(it, s, i == 1, h.Blocking(i), blockMetres);
                McPose.SetLocal(it.Root, local);
                Finish(it, s, h, i, shadows, remote);
            }
        }

        // ---------- R.E.P.O. robot ----------

        // [0] right arm (main hand), [1] left arm (off hand)
        readonly Transform[] arms = new Transform[2], anchors = new Transform[2], bodies = new Transform[2];
        readonly Vector3[] handPoint = new Vector3[2];   // the hand in the arm's space when there is no anchor
        readonly Quaternion[] bodyRest = { Quaternion.identity, Quaternion.identity };
        readonly float[] weight = new float[2];
        float robotBlock = 0.8f;
        int robotState; // 0 not set up, 1 ready, -1 no arms
        static bool logged;

        /// Turns each arm (blended in and out) so its hand points where Minecraft's arm would, then puts the item at the
        /// hand in the frame Minecraft's arm would have. `free`: R.E.P.O. isn't using the arm (grab beam, map, tumbling).
        public void UpdateRobot(PlayerAvatarVisuals v, HandsState h, ArmPoses a, bool[] show, bool[] free, ShadowCastingMode shadows, int layer, PlayerAvatar remote, float dt)
        {
            if (robotState == 0) robotState = SetupRobot(v) ? 1 : -1;
            if (robotState < 0) return;
            for (int i = 0; i < 2; i++)
            {
                var arm = arms[i];
                if (!arm) continue;
                bool posed = free[i] && a.Moves(i);
                weight[i] = free[i] ? Mathf.MoveTowards(weight[i], posed ? 1f : 0f, dt / 0.15f) : 0f;
                var body = bodies[i] ? bodies[i].rotation * Quaternion.Inverse(bodyRest[i]) : v.transform.rotation;
                var mc = body * ArmPoses.Rot(a.Arm(i));
                var handLocal = anchors[i] ? arm.InverseTransformPoint(anchors[i].position) : handPoint[i];
                if (weight[i] > 0f)
                {
                    var dir = arm.TransformPoint(handLocal) - arm.position;
                    if (dir.sqrMagnitude > 1e-8f)
                    {
                        var target = Quaternion.FromToRotation(dir, mc * Vector3.down) * arm.rotation;
                        arm.rotation = Quaternion.Slerp(arm.rotation, target, Mathf.SmoothStep(0f, 1f, weight[i]));
                    }
                }

                var it = held[i];
                var s = show[i] && free[i] ? h.Hands[i] : null;
                Sync(it, s, arm, layer);
                if (!it.Root) continue;
                var handW = arm.TransformPoint(handLocal);
                var actual = handW - arm.position;
                // Minecraft's arm frame, turned onto the real arm while it blends in
                var frame = actual.sqrMagnitude > 1e-8f ? Quaternion.FromToRotation(mc * Vector3.down, actual) * mc : mc;
                var w = Matrix4x4.TRS(handW, frame, Vector3.one * robotBlock) * FromHand(it, s, i == 1, h.Blocking(i), robotBlock);
                McPose.SetLocal(it.Root, arm.worldToLocalMatrix * w);
                Finish(it, s, h, i, shadows, remote);
            }
        }

        bool SetupRobot(PlayerAvatarVisuals v)
        {
            var ra = v.GetComponent<PlayerAvatarRightArm>();
            var la = v.GetComponent<PlayerAvatarLeftArm>();
            arms[0] = ra ? ra.rightArmTransform : null;
            arms[1] = la ? la.leftArmTransform : null;
            if (!arms[0] && !arms[1]) return false;
            // the hands: the grabber's spot on the right arm, the flashlight's on the left (where other players see it held)
            anchors[0] = ra && arms[0] && ra.grabberTransformTarget && ra.grabberTransformTarget.IsChildOf(arms[0]) ? ra.grabberTransformTarget : null;
            var fc = la ? la.flashlightController : null;
            anchors[1] = fc && arms[1] && fc.FollowTransformClient && fc.FollowTransformClient.IsChildOf(arms[1]) ? fc.FollowTransformClient : null;
            for (int i = 0; i < 2; i++)
            {
                if (!arms[i]) continue;
                // the body the arm hangs from (above the right arm's grab-aim pivot), upright the way the avatar faces
                var b = arms[i].parent;
                if (i == 0 && ra.rightArmParentTransform && b == ra.rightArmParentTransform && b.parent) b = b.parent;
                bodies[i] = b;
                if (b) bodyRest[i] = SnapAxes(Quaternion.Inverse(v.transform.rotation) * b.rotation);
                if (!anchors[i]) handPoint[i] = FarEnd(arms[i]);
            }
            float height = RobotHeight(v);
            // Steve is 1.875 blocks tall
            robotBlock = height / 1.875f * Mathf.Max(0.05f, Plugin.HeldItemScale.Value);
            if (!logged)
            {
                logged = true;
                string Arm(int i) => !arms[i] ? "none" : arms[i].name + (anchors[i] ? " (hand '" + anchors[i].name + "' at " + arms[i].InverseTransformPoint(anchors[i].position).ToString("F3") + ")"
                    : " (hand from its mesh at " + handPoint[i].ToString("F3") + ")") + ", body '" + (bodies[i] ? bodies[i].name : "-") + "' rest " + bodyRest[i].eulerAngles.ToString("F0");
                Plugin.Log.LogInfo("held items on the robot: right arm " + Arm(0) + "; left arm " + Arm(1) + "; height " + height.ToString("F2")
                    + " m, a block is " + robotBlock.ToString("F2") + " m");
            }
            return true;
        }

        static float RobotHeight(PlayerAvatarVisuals v)
        {
            bool any = false;
            var b = new Bounds();
            if (v.meshParent)
                foreach (var r in v.meshParent.GetComponentsInChildren<Renderer>())
                {
                    if (!r.enabled || r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
                    if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
                }
            return any ? Mathf.Clamp(b.size.y, 0.8f, 2.2f) : 1.5f;
        }

        /// The far end of the arm's meshes, in the arm's space.
        static Vector3 FarEnd(Transform arm)
        {
            bool any = false;
            var b = new Bounds();
            foreach (var r in arm.GetComponentsInChildren<MeshRenderer>())
            {
                var mf = r.GetComponent<MeshFilter>();
                if (!mf || !mf.sharedMesh) continue;
                var m = arm.worldToLocalMatrix * r.transform.localToWorldMatrix;
                var mb = mf.sharedMesh.bounds;
                for (int k = 0; k < 8; k++)
                {
                    var p = m.MultiplyPoint3x4(mb.center + Vector3.Scale(mb.extents, new Vector3((k & 1) == 0 ? -1 : 1, (k & 2) == 0 ? -1 : 1, (k & 4) == 0 ? -1 : 1)));
                    if (!any) { b = new Bounds(p, Vector3.zero); any = true; } else b.Encapsulate(p);
                }
            }
            if (!any) return arm.InverseTransformDirection(Vector3.down) * 0.25f;
            var c = b.center;
            var d = c.sqrMagnitude > 1e-8f ? c.normalized : arm.InverseTransformDirection(Vector3.down);
            return c + d * (Mathf.Abs(b.extents.x * d.x) + Mathf.Abs(b.extents.y * d.y) + Mathf.Abs(b.extents.z * d.z));
        }

        static Vector3 SnapAxis(Vector3 v)
        {
            float x = Mathf.Abs(v.x), y = Mathf.Abs(v.y), z = Mathf.Abs(v.z);
            if (x >= y && x >= z) return new Vector3(Mathf.Sign(v.x), 0f, 0f);
            return y >= z ? new Vector3(0f, Mathf.Sign(v.y), 0f) : new Vector3(0f, 0f, Mathf.Sign(v.z));
        }

        /// The nearest rotation by right angles: rigs rest axis-aligned, and this ignores the lean the body has right now.
        static Quaternion SnapAxes(Quaternion q)
        {
            var f = SnapAxis(q * Vector3.forward);
            var u = SnapAxis(q * Vector3.up);
            return Mathf.Abs(Vector3.Dot(f, u)) > 0.5f ? Quaternion.identity : Quaternion.LookRotation(f, u);
        }

        // ---------- shared ----------

        /// The item's transform from the bottom centre of the arm, in blocks along the arm's own axes (y up the arm, z out of
        /// its front).
        static Matrix4x4 FromHand(Held it, ItemStack s, bool left, bool blocking, float blockMetres)
        {
            if (it.RealSize)
                // muzzle (or lens) along the arm, its top toward the arm's front, sticking out past the hand
                return Matrix4x4.TRS(new Vector3(0f, -it.Length * 0.2f / blockMetres, 1.5f / 16f), Quaternion.LookRotation(Vector3.down, Vector3.forward),
                    Vector3.one / blockMetres);
            var p = new McPose();
            p.T(0f, 0f, -2f / 16f);
            p.RX(-90f);
            p.RY(180f);
            TpDisplay(s, left, blocking).Apply(p, left);
            return C * p.M * Flip;
        }

        /// The thirdperson_righthand / _lefthand entry of the item's model json.
        static McDisplay TpDisplay(ItemStack s, bool left, bool blocking)
        {
            if (s.IsMc("shield"))
                return blocking ? (left ? McDisplay.TpShieldBlockingLeft : McDisplay.TpShieldBlockingRight) : (left ? McDisplay.TpShieldLeft : McDisplay.TpShieldRight);
            if (s.IsMc("crossbow")) return left ? McDisplay.TpCrossbowLeft : McDisplay.TpCrossbowRight;
            if (s.IsMc("bow")) return left ? McDisplay.TpBowLeft : McDisplay.TpBowRight;
            if (s.IsMc("fishing_rod")) return left ? McDisplay.TpRodLeft : McDisplay.TpRodRight;
            if (s.IsTool || RepoWeapons.Melee(s)) return left ? McDisplay.TpHandheldLeft : McDisplay.TpHandheldRight;
            if (s.Kind == ItemKind.Mc && McItems.IsSprite(s.Id)) return McDisplay.TpGenerated; // generated.json: no left entry
            return McDisplay.TpBlock; // TNT, and R.E.P.O. objects shown like blocks
        }

        static string Sig(ItemStack s)
        {
            if (s == null) return null;
            var top = s.IsVirtual || s.Objects.Count == 0 ? null : s.Objects[s.Objects.Count - 1];
            return (int)s.Kind + "|" + s.Key + "|" + s.Id + "|" + (top ? top.GetInstanceID() : 0);
        }

        void Sync(Held it, ItemStack s, Transform parent, int layer)
        {
            var sig = Sig(s);
            if (sig == it.Sig && (!it.Root || it.Root.parent == parent)) return;
            if (it.Root) Object.Destroy(it.Root.gameObject);
            it.Root = null;
            it.Tex = null;
            it.Light = null;
            it.Sig = sig;
            if (s == null) return;
            var model = Model(s, out it.RealSize, out it.Length);
            if (!model) return;
            var root = new GameObject("mc_held_" + (s.Id ?? s.Key)).transform;
            root.SetParent(parent, false);
            model.transform.SetParent(root, false);
            ModelUtil.SetLayer(root.gameObject, layer);
            it.Root = root;
            it.Shadows = (ShadowCastingMode)(-1);
        }

        /// Minecraft items one block wide (as in first person); R.E.P.O. guns and the flashlight at their own size.
        static GameObject Model(ItemStack s, out bool realSize, out float length)
        {
            realSize = false;
            length = 0f;
            if (!s.IsVirtual)
            {
                var top = s.Objects.Count > 0 ? s.Objects[s.Objects.Count - 1] : null;
                if (!top) return null;
                var gun = top.GetComponent<ItemGun>();
                if (gun) { realSize = true; return RealGun(gun, out length); }
            }
            if (s.IsMc("flashlight"))
            {
                realSize = true;
                length = 0.3f;
                return McItems.Model("flashlight", length);
            }
            return SteveManager.HandModel(s);
        }

        /// A copy of a R.E.P.O. gun at its own size, muzzle along +Z, centred on its bounds.
        static GameObject RealGun(ItemGun g, out float length)
        {
            var clone = ModelUtil.CloneVisual(g.gameObject, out var b);
            var m = RepoWeapons.Muzzle(g);
            var rot = Quaternion.identity;
            if (m)
            {
                var inv = Quaternion.Inverse(g.transform.rotation);
                rot = Quaternion.Inverse(Quaternion.LookRotation(inv * m.forward, inv * m.up));
            }
            float sc = g.transform.lossyScale.x;
            if (sc < 1e-3f) sc = 1f;
            var holder = new GameObject("mc_gun_tp");
            clone.transform.SetParent(holder.transform, false);
            clone.transform.localRotation = rot;
            clone.transform.localScale = Vector3.one * sc;
            clone.transform.localPosition = -(rot * b.center) * sc;
            length = Mathf.Abs((rot * b.size).z) * sc;
            return holder;
        }

        /// Sprite states (bow pull, crossbow load, cast rod), shadows, and another player's flashlight beam or torch light
        /// (the local player's are McWorld's, at the camera).
        void Finish(Held it, ItemStack s, HandsState h, int hand, ShadowCastingMode shadows, PlayerAvatar remote)
        {
            var tex = McItems.StateSprite(s, h.UseOf(hand), h.Cast[hand]);
            if (tex != null && tex != it.Tex)
            {
                it.Tex = tex;
                var mf = it.Root.GetComponentInChildren<MeshFilter>();
                var mr = it.Root.GetComponentInChildren<MeshRenderer>();
                if (mf) mf.sharedMesh = ModelUtil.ItemMesh(tex);
                if (mr) mr.sharedMaterial = ModelUtil.ItemMat(tex);
            }
            if (it.Shadows != shadows)
            {
                it.Shadows = shadows;
                ModelUtil.SetShadows(it.Root.gameObject, shadows);
            }
            if (!remote) return;
            bool flash = s.IsMc("flashlight"), torch = s.IsMc("torch");
            if (!flash && !torch) return;
            if (!it.Light) it.Light = MakeLight(it.Root, flash, remote);
            it.Light.enabled = !flash || h.FlashlightOn;
            if (flash)
            {
                var look = remote.localCamera ? remote.localCamera.GetOverrideTransform() : null;
                it.Light.transform.rotation = look ? look.rotation : it.Root.rotation;
            }
        }

        static Light MakeLight(Transform parent, bool flashlight, PlayerAvatar owner)
        {
            var go = new GameObject(flashlight ? "mc_held_flashlight_light" : "mc_held_torch_light");
            go.transform.SetParent(parent, false);
            var l = go.AddComponent<Light>();
            l.shadows = LightShadows.None;
            l.renderMode = LightRenderMode.ForcePixel;
            if (!flashlight)
            {
                l.type = LightType.Point;
                l.color = new Color(1f, 0.78f, 0.5f);
                l.range = 10f * McWorld.B;
                l.intensity = 1.1f;
                return l;
            }
            // the owner's own (hidden) R.E.P.O. flashlight beam
            l.type = LightType.Spot;
            var src = owner.flashlightController ? owner.flashlightController.spotlight : FlashlightController.Instance ? FlashlightController.Instance.spotlight : null;
            if (src)
            {
                l.color = src.color;
                l.range = src.range;
                l.spotAngle = src.spotAngle;
                l.innerSpotAngle = src.innerSpotAngle;
                l.intensity = Mathf.Max(1f, src.intensity);
                l.cookie = src.cookie;
                l.shadows = src.shadows;
                l.shadowStrength = src.shadowStrength;
                l.cullingMask = src.cullingMask;
            }
            else
            {
                l.color = new Color(1f, 0.95f, 0.85f);
                l.range = 20f;
                l.spotAngle = 60f;
                l.intensity = 1.5f;
            }
            return l;
        }

        /// The renderers of the items shown now (the inventory preview draws them with the robot).
        public void AddRenderers(List<Renderer> list)
        {
            foreach (var it in held)
                if (it.Root && it.Root.gameObject.activeInHierarchy)
                    foreach (var r in it.Root.GetComponentsInChildren<Renderer>())
                        if (r.enabled && !list.Contains(r)) list.Add(r);
        }
    }
}
