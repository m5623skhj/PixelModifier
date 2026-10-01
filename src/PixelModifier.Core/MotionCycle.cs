using System.Numerics;

namespace PixelModifier.Core;

public sealed record MotionQuality(float Score, float DeformationRetention, float FootSlip,
    float MotionLoss, float AbruptMotion, float LoopJump, float BoneError, float KneeError);

public sealed record MotionCycle(IReadOnlyList<FramePlan> Frames, IReadOnlyList<LayerMap> SourceLayers,
    IReadOnlyList<float> LayerScales, MotionQuality Quality, int WorkingSize)
{
    /// <summary>Plans an entire loop with per-layer limits; one limb cannot reduce the other parts' movement.</summary>
    public static MotionCycle Create(IReadOnlyList<RenderSource> sources, MotionVariant variant,
        GenerationSettings settings, CancellationToken cancellation = default)
    {
        var frames = new FramePlan[settings.FrameCount];
        var layers = new LayerMap[sources.Count];
        for (int i = 0; i < sources.Count; i++) layers[i] = LayerMap.Create(sources[i], cancellation);
        var scales = new float[Renderer.LayerCount];
        Array.Fill(scales, 1);
        for (int i = 0; i < frames.Length; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            float phase = (float)i / frames.Length;
            var plan = Timeline.Plan(sources, phase, variant, settings);
            frames[i] = plan;
            var limits = Renderer.LayerLimits(sources[plan.SourceIndex], layers[plan.SourceIndex], plan, phase, variant, cancellation);
            for (int layer = 0; layer < scales.Length; layer++) scales[layer] = Math.Min(scales[layer], limits[layer]);
        }
        float retention = scales.Average();
        var quality = Evaluate(sources, variant, settings, retention, cancellation);
        return new(Array.AsReadOnly(frames), Array.AsReadOnly(layers), Array.AsReadOnly(scales), quality, settings.WorkingSize);
    }

    /// <summary>Ranks geometric stability, including the loop seam. This is a heuristic, not a visual quality model.</summary>
    private static MotionQuality Evaluate(IReadOnlyList<RenderSource> sources, MotionVariant variant,
        GenerationSettings settings, float retention, CancellationToken cancellation)
    {
        int count = Math.Max(48, settings.FrameCount);
        var samples = new Rig[count];
        var references = new Rig[count];
        float bodyLength = Math.Max(.01f, sources.Average(s => Motion.LegLength(s.Rig)));
        var joints = Enum.GetValues<Joint>();
        for (int i = 0; i < count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var plan = Timeline.Plan(sources, (float)i / count, variant, settings);
            var rest = sources[plan.SourceIndex].Rig;
            references[i] = rest;
            // Bone layers follow the solved pose fully; secondary cloth limits do not shrink the landmarks.
            foreach (var joint in joints)
            {
                var point = plan.Pose[joint];
                if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
                    return new(0, retention, 1, 1, 1, 1, 1, 1);
            }
            samples[i] = plan.Pose;
        }

        float abrupt = 0, boneError = 0, kneeError = 0, slip = 0;
        int contactPairs = 0;
        float stepSum = 0, seamStep = 0;
        float stride = Motion.Stride(sources[0].Rig, variant, settings);
        float direction = settings.Facing == Facing.Left ? -1 : 1;
        float expectedStep = -2 * stride * direction / (Motion.StanceDuration(settings.Motion, variant) * count);
        for (int i = 0; i < count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            int next = (i + 1) % count, previous = (i + count - 1) % count;
            foreach (var joint in joints)
            {
                var velocity = samples[next][joint].Vector - samples[i][joint].Vector;
                var preceding = samples[i][joint].Vector - samples[previous][joint].Vector;
                abrupt += Math.Max(0, (velocity - preceding).Length() * count * count / bodyLength - 35);
                if (i == count - 1) seamStep += velocity.Length(); else stepSum += velocity.Length();
            }
            foreach (var bone in Rigging.Bones)
            {
                float original = Vector2.Distance(references[i][bone.Start].Vector, references[i][bone.End].Vector);
                float actual = Vector2.Distance(samples[i][bone.Start].Vector, samples[i][bone.End].Vector);
                boneError += Math.Abs(actual - original) / Math.Max(.005f, original);
            }
            foreach (var (knee, foot, offset) in new[]
            { (Joint.KneeA, Joint.FootA, 0f), (Joint.KneeB, Joint.FootB, .5f) })
            {
                float maxFlex = Math.Max(Flex(references[i], knee, foot),
                    Motion.KneeFlexLimit(settings.Motion));
                kneeError += Math.Max(0, Flex(samples[i], knee, foot) - maxFlex) / MathF.PI;
                // Authored multi-image poses may use their own contact timing; do not score them as preset contacts.
                if (sources.Count != 1) continue;
                var a = Motion.SampleGait((float)i / count + offset, settings.Motion, variant);
                var b = Motion.SampleGait((float)next / count + offset, settings.Motion, variant);
                if (a.Contact < .7f || b.Contact < .7f) continue;
                var delta = samples[next][foot].Vector - samples[i][foot].Vector;
                // In-place stance moves backward at travel speed. It should be stationary in world space.
                slip += (Math.Abs(delta.X - expectedStep) + Math.Abs(delta.Y)) /
                    Math.Max(.00001f, stride / count);
                contactPairs++;
            }
        }
        abrupt /= count * joints.Length;
        boneError /= count * Rigging.Bones.Length;
        kneeError /= count * 2;
        slip /= Math.Max(1, contactPairs);
        float averageStep = stepSum / (count - 1);
        float loopJump = Math.Max(0, seamStep / Math.Max(.00001f, averageStep) - 1.5f);
        float motionLoss = 0;
        if (sources.Count == 1 && stride > .00001f)
        {
            float excursion = 0;
            foreach (var foot in new[] { Joint.FootA, Joint.FootB })
                excursion += samples.Max(r => r[foot].X) - samples.Min(r => r[foot].X);
            motionLoss = Math.Max(motionLoss, 1 - Math.Clamp(excursion / (4 * stride), 0, 1));
        }
        float penalty = 3 * slip + 5 * motionLoss + .025f * abrupt + 2 * loopJump + 4 * boneError +
            4 * kneeError + (1 - retention);
        return new(100 / (1 + penalty), retention, slip, motionLoss, abrupt, loopJump, boneError, kneeError);
    }

    private static float Flex(Rig rig, Joint knee, Joint foot)
    {
        var upper = rig[knee].Vector - rig[Joint.Hip].Vector;
        var lower = rig[foot].Vector - rig[knee].Vector;
        float lengths = upper.Length() * lower.Length();
        return lengths < 1e-10f ? 0 : MathF.Acos(Math.Clamp(Vector2.Dot(upper, lower) / lengths, -1, 1));
    }
}
