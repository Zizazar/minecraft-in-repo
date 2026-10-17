using UnityEngine;

namespace MinecraftRepo
{
    /// One "display" entry of a Minecraft item model json (rotation in degrees, translation in 1/16 block).
    internal struct McDisplay
    {
        public Vector3 Rot, Trans;
        public float Scale;

        public McDisplay(float rx, float ry, float rz, float tx, float ty, float tz, float s)
        {
            Rot = new Vector3(rx, ry, rz);
            Trans = new Vector3(tx, ty, tz);
            Scale = s;
        }

        // values from the 1.21 jar: models/item/{handheld,generated,bow,shield,shield_blocking,crossbow,handheld_rod}.json, block/block.json
        // first person
        public static readonly McDisplay HandheldRight = new McDisplay(0, -90, 25, 1.13f, 3.2f, 1.13f, 0.68f);
        public static readonly McDisplay HandheldLeft = new McDisplay(0, 90, -25, 1.13f, 3.2f, 1.13f, 0.68f);
        public static readonly McDisplay BlockRight = new McDisplay(0, 45, 0, 0, 0, 0, 0.4f);
        public static readonly McDisplay BlockLeft = new McDisplay(0, 225, 0, 0, 0, 0, 0.4f);
        public static readonly McDisplay ShieldRight = new McDisplay(0, 180, 5, -10, 2, -10, 1.25f);
        public static readonly McDisplay ShieldLeft = new McDisplay(0, 180, 5, 10, 0, -10, 1.25f);
        public static readonly McDisplay ShieldBlockingRight = new McDisplay(0, 180, -5, -15, 5, -11, 1.25f);
        public static readonly McDisplay ShieldBlockingLeft = new McDisplay(0, 180, -5, 5, 5, -11, 1.25f);
        public static readonly McDisplay CrossbowRight = new McDisplay(-90, 0, -55, 1.13f, 3.2f, 1.13f, 0.68f);
        public static readonly McDisplay CrossbowLeft = new McDisplay(-90, 0, 35, 1.13f, 3.2f, 1.13f, 0.68f);
        public static readonly McDisplay RodRight = new McDisplay(0, 90, 25, 0, 1.6f, 0.8f, 0.68f);
        public static readonly McDisplay RodLeft = new McDisplay(0, -90, -25, 0, 1.6f, 0.8f, 0.68f);
        public static readonly McDisplay BlockGui = new McDisplay(30, 225, 0, 0, 0, 0, 0.625f);
        public static readonly McDisplay ShieldGui = new McDisplay(15, -25, -5, 2, 3, 0, 0.65f);

        // third person (thirdperson_righthand / _lefthand; a missing left entry falls back to the right one)
        public static readonly McDisplay TpHandheldRight = new McDisplay(0, -90, 55, 0, 4, 0.5f, 0.85f);
        public static readonly McDisplay TpHandheldLeft = new McDisplay(0, 90, -55, 0, 4, 0.5f, 0.85f);
        public static readonly McDisplay TpGenerated = new McDisplay(0, 0, 0, 0, 3, 1, 0.55f);
        public static readonly McDisplay TpBlock = new McDisplay(75, 45, 0, 0, 2.5f, 0, 0.375f);
        public static readonly McDisplay TpBowRight = new McDisplay(-80, 260, -40, -1, -2, 2.5f, 0.9f);
        public static readonly McDisplay TpBowLeft = new McDisplay(-80, -280, 40, -1, -2, 2.5f, 0.9f);
        public static readonly McDisplay TpCrossbowRight = new McDisplay(-90, 0, -60, 2, 0.1f, -3, 0.9f);
        public static readonly McDisplay TpCrossbowLeft = new McDisplay(-90, 0, 30, 2, 0.1f, -3, 0.9f);
        public static readonly McDisplay TpRodRight = new McDisplay(0, 90, 55, 0, 4, 2.5f, 0.85f);
        public static readonly McDisplay TpRodLeft = new McDisplay(0, -90, -55, 0, 4, 2.5f, 0.85f);
        public static readonly McDisplay TpShieldRight = new McDisplay(0, 90, 0, 10, 6, -4, 1f);
        public static readonly McDisplay TpShieldLeft = new McDisplay(0, 90, 0, 10, 6, 12, 1f);
        public static readonly McDisplay TpShieldBlockingRight = new McDisplay(45, 155, 0, -3.49f, 11, -2, 1f);
        public static readonly McDisplay TpShieldBlockingLeft = new McDisplay(45, 155, 0, 11.51f, 7, 2.5f, 1f);

        /// Minecraft's ItemTransform.apply: the left hand negates the Y/Z rotation and the X translation.
        public void Apply(McPose p, bool leftHand)
        {
            float ry = leftHand ? -Rot.y : Rot.y, rz = leftHand ? -Rot.z : Rot.z;
            p.T((leftHand ? -Trans.x : Trans.x) / 16f, Trans.y / 16f, Trans.z / 16f);
            p.RX(Rot.x); p.RY(ry); p.RZ(rz);
            p.S(Scale, Scale, Scale);
        }
    }

    /// Minecraft's PoseStack in its own right-handed space (camera looks down -Z). `Unity` converts the result
    /// for meshes built in Unity's left-handed space (z flipped on both sides).
    internal class McPose
    {
        public Matrix4x4 M = Matrix4x4.identity;
        static readonly Matrix4x4 Flip = Matrix4x4.Scale(new Vector3(1, 1, -1));

        public void T(float x, float y, float z) => M *= Matrix4x4.Translate(new Vector3(x, y, z));
        public void RX(float deg) { if (deg != 0f) M *= Matrix4x4.Rotate(Quaternion.AngleAxis(deg, Vector3.right)); }
        public void RY(float deg) { if (deg != 0f) M *= Matrix4x4.Rotate(Quaternion.AngleAxis(deg, Vector3.up)); }
        public void RZ(float deg) { if (deg != 0f) M *= Matrix4x4.Rotate(Quaternion.AngleAxis(deg, Vector3.forward)); }
        public void S(float x, float y, float z) => M *= Matrix4x4.Scale(new Vector3(x, y, z));

        public McPose Copy() => new McPose { M = M };

        /// The pose for a mesh in Unity space (v_unity = flip(v_minecraft)), expressed in Unity space.
        public Matrix4x4 Unity => Flip * M * Flip;

        /// The pose for a mesh already in Minecraft's model space, expressed in Unity space.
        public Matrix4x4 UnityFromMc => Flip * M;

        /// Sets a transform's local TRS from a matrix (shear, from the bow's stretch, is dropped).
        public static void SetLocal(Transform t, Matrix4x4 m)
        {
            Vector3 r = m.GetColumn(0), u = m.GetColumn(1), f = m.GetColumn(2);
            t.localPosition = m.GetColumn(3);
            t.localRotation = Quaternion.LookRotation(f, u);
            t.localScale = new Vector3(r.magnitude, u.magnitude, f.magnitude);
        }
    }
}
