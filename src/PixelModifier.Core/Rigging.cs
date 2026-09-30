using System.Numerics;

namespace PixelModifier.Core;

public static class Rigging
{
    public static readonly (Joint Start, Joint End, BodyPart Part)[] Bones =
    [
        (Joint.Neck, Joint.Head, BodyPart.Head), (Joint.Hip, Joint.Neck, BodyPart.Torso),
        (Joint.ShoulderA, Joint.ElbowA, BodyPart.ArmA), (Joint.ElbowA, Joint.HandA, BodyPart.ArmA),
        (Joint.ShoulderB, Joint.ElbowB, BodyPart.ArmB), (Joint.ElbowB, Joint.HandB, BodyPart.ArmB),
        (Joint.Hip, Joint.KneeA, BodyPart.LegA), (Joint.KneeA, Joint.FootA, BodyPart.LegA),
        (Joint.Hip, Joint.KneeB, BodyPart.LegB), (Joint.KneeB, Joint.FootB, BodyPart.LegB)
    ];

    public static Rig Guess(PixelImage normalized)
    {
        var b = normalized.Bounds();
        float left = (float)b.X / normalized.Width, top = (float)b.Y / normalized.Height;
        float width = (float)b.Width / normalized.Width, height = (float)b.Height / normalized.Height;
        Point2 P(float x, float y) => new(left + width * x, top + height * y);
        var rig = new Rig();
        rig[Joint.Head] = P(.5f, .13f); rig[Joint.Neck] = P(.5f, .27f);
        rig[Joint.Hip] = P(.5f, .54f);
        rig[Joint.ShoulderA] = P(.70f, .30f); rig[Joint.ElbowA] = P(.77f, .43f);
        rig[Joint.HandA] = P(.79f, .58f);
        rig[Joint.ShoulderB] = P(.30f, .30f); rig[Joint.ElbowB] = P(.23f, .43f);
        rig[Joint.HandB] = P(.21f, .58f);
        rig[Joint.KneeA] = P(.64f, .76f); rig[Joint.FootA] = P(.65f, .99f);
        rig[Joint.KneeB] = P(.36f, .76f); rig[Joint.FootB] = P(.35f, .99f);
        // Find visible feet in the lower alpha band without using character-specific colors.
        float[] sums = new float[2]; int[] counts = new int[2];
        for (int y = b.Y + (int)(b.Height * .9f); y < b.Y + b.Height; y++)
            for (int x = b.X; x < b.X + b.Width; x++)
                if (normalized.Pixels[(y * normalized.Width + x) * 4 + 3] > 32)
                { int side = x < b.X + b.Width / 2 ? 0 : 1; sums[side] += x + .5f; counts[side]++; }
        if (counts[0] > 0) rig[Joint.FootB] = new(sums[0] / counts[0] / normalized.Width, (b.Y + b.Height - 1f) / normalized.Height);
        if (counts[1] > 0) rig[Joint.FootA] = new(sums[1] / counts[1] / normalized.Width, (b.Y + b.Height - 1f) / normalized.Height);
        return rig;
    }

    public static RegionMap GuessRegions(Rig rig)
    {
        var map = new RegionMap();
        float bodyWidth = Math.Max(.05f, Math.Abs(rig[Joint.ShoulderA].X - rig[Joint.ShoulderB].X) * .52f);
        for (int y = 0; y < map.Height; y++)
            for (int x = 0; x < map.Width; x++)
            {
                var p = new Vector2((x + .5f) / map.Width, (y + .5f) / map.Height);
                BodyPart part;
                if (p.Y < rig[Joint.Neck].Y - .018f) part = BodyPart.Head;
                else
                {
                    float best = float.MaxValue; part = BodyPart.Torso;
                    foreach (var bone in Bones.Where(b => b.Part != BodyPart.Head))
                    {
                        float distance = SegmentDistance(p, rig[bone.Start].Vector, rig[bone.End].Vector);
                        if (bone.Part == BodyPart.Torso) distance *= .65f;
                        if (distance < best) { best = distance; part = bone.Part; }
                    }
                    if (p.Y < rig[Joint.Hip].Y && Math.Abs(p.X - rig[Joint.Hip].X) < bodyWidth)
                        part = BodyPart.Torso;
                    if (p.Y > rig[Joint.Hip].Y && p.Y < Math.Min(rig[Joint.FootA].Y, rig[Joint.FootB].Y) - .10f &&
                        best > .04f) part = BodyPart.Coat;
                }
                map.Values[y * map.Width + x] = (byte)part;
            }
        return map;
    }
    public static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        var delta = b - a;
        float t = delta.LengthSquared() < 1e-8f ? 0 : Math.Clamp(Vector2.Dot(p - a, delta) / delta.LengthSquared(), 0, 1);
        return Vector2.Distance(p, a + t * delta);
    }
}

public readonly record struct GaitSample(float Horizontal, float Lift, float Contact, float SupportProgress);

public static class Motion
{
    private readonly record struct LegGeometry(float Upper, float Lower, float MinReach, float BendDirection);

    public static float StanceDuration(MotionKind motion, MotionVariant variant) =>
        (motion == MotionKind.Run ? .34f : .62f) + variant.Timing * .5f;

    public static float KneeFlexLimit(MotionKind motion) => motion == MotionKind.Run ? 2 * MathF.PI / 3 : MathF.PI / 3;

    /// <summary>Matches foot velocity at lift-off and landing; shorter running support leaves a flight interval.</summary>
    public static GaitSample SampleGait(float phase, MotionKind motion, MotionVariant variant)
    {
        phase -= MathF.Floor(phase);
        float stance = StanceDuration(motion, variant);
        if (phase < stance)
        {
            float progress = phase / stance;
            float contact = Smooth(Math.Clamp(progress / .12f, 0, 1)) *
                Smooth(Math.Clamp((1 - progress) / .16f, 0, 1));
            return new(1 - 2 * progress, 0, contact, progress);
        }
        float u = (phase - stance) / (1 - stance);
        float tangent = -2 * (1 - stance) / stance;
        if (motion == MotionKind.Run)
        {
            // Recovery folds the trailing leg; knee drive lifts it before extending for landing.
            Vector2 position;
            if (u < .24f)
                position = Curve(u / .24f, new(-1, 0), new(-.90f, 2.05f),
                    new(tangent, 0), new(2, 2), .24f);
            else if (u < .58f)
                position = Curve((u - .24f) / .34f, new(-.90f, 2.05f), new(.35f, 2.45f),
                    new(2, 2), new(2.4f, -.3f), .34f);
            else
                position = Curve((u - .58f) / .42f, new(.35f, 2.45f), new(1, 0),
                    new(2.4f, -.3f), new(tangent, 0), .42f);
            return new(position.X, Math.Max(0, position.Y), 0, 0);
        }
        float horizontal = -1 + 2 * Smooth(u) + tangent * (2 * u * u * u - 3 * u * u + u);
        // Clearance peaks early in swing and approaches the ground with zero vertical velocity.
        float lift = u * u * (1 - u) * (1 - u) * (1 - u) / .03456f;
        return new(horizontal, lift, 0, 0);
    }

    private static float Smooth(float t) => t * t * (3 - 2 * t);

    private static Vector2 Curve(float t, Vector2 a, Vector2 b, Vector2 aVelocity, Vector2 bVelocity, float duration)
    {
        float t2 = t * t, t3 = t2 * t;
        return a * (2 * t3 - 3 * t2 + 1) + b * (-2 * t3 + 3 * t2) +
            aVelocity * duration * (t3 - 2 * t2 + t) + bVelocity * duration * (t3 - t2);
    }

    public static float LegLength(Rig rig) =>
        (Vector2.Distance(rig[Joint.Hip].Vector, rig[Joint.KneeA].Vector) +
         Vector2.Distance(rig[Joint.KneeA].Vector, rig[Joint.FootA].Vector) +
         Vector2.Distance(rig[Joint.Hip].Vector, rig[Joint.KneeB].Vector) +
         Vector2.Distance(rig[Joint.KneeB].Vector, rig[Joint.FootB].Vector)) * .5f;

    public static float Stride(Rig rig, MotionVariant variant, GenerationSettings settings) =>
        Math.Min(variant.Stride / .35f, settings.Motion == MotionKind.Run ? .42f : .24f) *
        LegLength(rig) * (settings.Facing == Facing.Front ? .18f : 1);

    /// <summary>Fits pelvis and both feet together within authored bone lengths and knee limits.</summary>
    public static Rig Pose(Rig rest, float phase, MotionVariant variant, GenerationSettings settings, float motionWeight = 1)
    {
        var pose = rest.Clone();
        if (motionWeight <= 0) return pose;
        phase -= MathF.Floor(phase);
        float direction = settings.Facing == Facing.Left ? -1 : 1;
        var restHip = rest[Joint.Hip].Vector;
        var legA = Geometry(Joint.KneeA, Joint.FootA);
        var legB = Geometry(Joint.KneeB, Joint.FootB);
        float bodyScale = LegLength(rest) / .35f;
        float stride = Stride(rest, variant, settings) * direction * motionWeight;
        float lift = variant.Lift * bodyScale * motionWeight * (settings.Facing == Facing.Front ? 1.3f : 1);
        var gaitA = SampleGait(phase, settings.Motion, variant);
        var gaitB = SampleGait(phase + .5f, settings.Motion, variant);
        var displacementA = new Vector2(stride * gaitA.Horizontal, -lift * gaitA.Lift);
        var displacementB = new Vector2(stride * gaitB.Horizontal, -lift * gaitB.Lift);
        float support = gaitA.Contact + gaitB.Contact;
        float compression = (gaitA.Contact * MathF.Sin(MathF.PI * gaitA.SupportProgress) +
            gaitB.Contact * MathF.Sin(MathF.PI * gaitB.SupportProgress)) / Math.Max(1, support);
        float flight = settings.Motion == MotionKind.Run ? 1 - Math.Clamp(support, 0, 1) : 0;
        float bob = variant.Bob * bodyScale * motionWeight * (compression - 1.5f * flight);
        float sway = LegLength(rest) * (settings.Facing == Facing.Front ? .025f : .008f) *
            MathF.Sin(2 * MathF.PI * phase) * motionWeight;

        float amount = 1;
        if (!TryFit(amount, out var hip, out var footA, out var footB))
        {
            TryFit(0, out hip, out footA, out footB);
            float low = 0, high = 1;
            for (int i = 0; i < 10; i++)
            {
                float middle = (low + high) * .5f;
                if (TryFit(middle, out _, out _, out _)) low = middle; else high = middle;
            }
            amount = low;
            TryFit(amount, out hip, out footA, out footB);
        }
        float lean = variant.Lean * direction * motionWeight *
            (.8f + .2f * Math.Clamp(support, 0, 1));
        foreach (Joint joint in Enum.GetValues<Joint>())
        {
            if (joint is Joint.FootA or Joint.FootB or Joint.KneeA or Joint.KneeB) continue;
            pose[joint] = Point2.From(hip + Rotate(rest[joint].Vector - restHip, lean));
        }
        pose[Joint.Hip] = Point2.From(hip);
        ApplyLeg(Joint.KneeA, Joint.FootA, footA, legA);
        ApplyLeg(Joint.KneeB, Joint.FootB, footB, legB);
        ApplyArm(Joint.ShoulderA, Joint.ElbowA, Joint.HandA, phase);
        ApplyArm(Joint.ShoulderB, Joint.ElbowB, Joint.HandB, phase + .5f);
        // Counter-rotation keeps the head steadier than the torso.
        pose[Joint.Head] = Point2.From(pose[Joint.Neck].Vector +
            Rotate(rest[Joint.Head].Vector - rest[Joint.Neck].Vector, lean * .35f));
        return pose;

        LegGeometry Geometry(Joint kneeId, Joint footId)
        {
            var knee = rest[kneeId].Vector;
            var axis = rest[footId].Vector - restHip;
            float upper = Vector2.Distance(restHip, knee);
            float lower = Vector2.Distance(knee, rest[footId].Vector);
            float bend = Vector2.Dot(knee - restHip, new Vector2(axis.Y, -axis.X));
            float restFlex = MathF.Acos(Math.Clamp((axis.LengthSquared() - upper * upper - lower * lower) /
                Math.Max(1e-12f, 2 * upper * lower), -1, 1));
            float maxFlex = Math.Max(restFlex, KneeFlexLimit(settings.Motion));
            float minReach = MathF.Sqrt(Math.Max(0, upper * upper + lower * lower +
                2 * upper * lower * MathF.Cos(maxFlex)));
            float bendDirection = Math.Abs(bend) > .000001f ? MathF.Sign(bend) : direction;
            // A full side-facing run drives both knees forward. Authored key-pose blends keep their own bend side.
            if (settings.Motion == MotionKind.Run && settings.Facing != Facing.Front && motionWeight >= 1)
                bendDirection = direction;
            return new(upper, lower, Math.Min(minReach, axis.Length()), bendDirection);
        }

        bool TryFit(float factor, out Vector2 fittedHip, out Vector2 a, out Vector2 b)
        {
            a = rest[Joint.FootA].Vector + displacementA * factor;
            b = rest[Joint.FootB].Vector + displacementB * factor;
            float hipX = restHip.X + sway * factor;
            float minY = float.NegativeInfinity, maxY = float.PositiveInfinity;
            bool fits = HeightRange(a, legA, rest[Joint.FootA].Y) &&
                HeightRange(b, legB, rest[Joint.FootB].Y) && minY <= maxY;
            fittedHip = fits ? new(hipX, Math.Clamp(restHip.Y + bob * factor, minY, maxY)) : restHip;
            return fits;

            bool HeightRange(Vector2 foot, LegGeometry leg, float originalFootY)
            {
                float dx = foot.X - hipX, reach = leg.Upper + leg.Lower;
                if (Math.Abs(dx) > reach) return false;
                float height = MathF.Sqrt(Math.Max(0, reach * reach - dx * dx));
                minY = Math.Max(minY, foot.Y - height - .000001f);
                maxY = Math.Min(maxY, foot.Y + height + .000001f);
                if (originalFootY > restHip.Y)
                {
                    float minHeight = MathF.Sqrt(Math.Max(0, leg.MinReach * leg.MinReach - dx * dx));
                    maxY = Math.Min(maxY, foot.Y - minHeight + .000001f);
                }
                return true;
            }
        }

        void ApplyLeg(Joint kneeId, Joint footId, Vector2 foot, LegGeometry leg)
        {
            if (leg.Upper < .00001f || leg.Lower < .00001f) return;
            pose[footId] = Point2.From(foot);
            pose[kneeId] = Point2.From(SolveKnee(hip, foot, leg.Upper, leg.Lower, leg.BendDirection));
        }

        void ApplyArm(Joint shoulderId, Joint elbowId, Joint handId, float t)
        {
            var gait = SampleGait(t - .025f, settings.Motion, variant);
            bool run = settings.Motion == MotionKind.Run;
            // Arm swing remains independent of stride clipping and opposes the corresponding leg.
            float swing = run ? MathF.Cos(2 * MathF.PI * (t - .025f)) : gait.Horizontal;
            float angle = (run ? Math.Min(1.05f, variant.ArmSwing) : variant.ArmSwing) *
                direction * swing * motionWeight;
            var upper = rest[elbowId].Vector - rest[shoulderId].Vector;
            var lower = rest[handId].Vector - rest[elbowId].Vector;
            var elbow = pose[shoulderId].Vector + Rotate(upper, angle + lean);
            float bend;
            if (run)
            {
                float authored = MathF.Atan2(upper.X * lower.Y - upper.Y * lower.X, Vector2.Dot(upper, lower));
                float target = -direction * (MathF.PI / 2 + .12f * MathF.Sin(2 * MathF.PI * t));
                float difference = target - authored;
                bend = MathF.Atan2(MathF.Sin(difference), MathF.Cos(difference)) * motionWeight;
            }
            else bend = -.10f * direction * motionWeight;
            pose[elbowId] = Point2.From(elbow);
            pose[handId] = Point2.From(elbow + Rotate(lower, angle + lean + bend));
        }
    }

    public static Vector2 Rotate(Vector2 v, float angle) =>
        new(v.X * MathF.Cos(angle) - v.Y * MathF.Sin(angle), v.X * MathF.Sin(angle) + v.Y * MathF.Cos(angle));

    public static Vector2 SolveKnee(Vector2 root, Vector2 foot, float upper, float lower, float direction)
    {
        var delta = foot - root;
        float distance = Math.Max(.000001f, delta.Length());
        var axis = delta.LengthSquared() > 1e-12f ? Vector2.Normalize(delta) : Vector2.UnitY;
        float along = Math.Clamp((upper * upper - lower * lower + distance * distance) / (2 * distance), -upper, upper);
        float height = MathF.Sqrt(Math.Max(0, upper * upper - along * along));
        return root + axis * along + new Vector2(axis.Y, -axis.X) * height * direction;
    }
}
