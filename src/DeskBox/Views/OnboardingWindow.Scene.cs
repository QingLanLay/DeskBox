using System.Numerics;
using DeskBox.Services;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;

namespace DeskBox.Views;

/// <summary>
/// The left stage is one persistent illustration canvas. Each step owns a
/// single hero element (logo mark / drop box / keycap / tray / feature tiles)
/// that swaps via a soft crossfade + settle — no lateral page motion. Per-step
/// loops (mark assembly, file flight, keycap press, tray ping, tile float)
/// are fully stopped when leaving the step.
/// </summary>
public sealed partial class OnboardingWindow
{
    private const int SceneTransitionMs = 380;

    /// <summary>One full coincide → fan → permute → merge → snap cycle.</summary>
    private const int MarkAssemblyLoopMs = 7200;

    /// <summary>One full scatter → inhale → tidy row → reveal → breathe cycle.</summary>
    private const int DropSceneLoopMs = 7200;

    /// <summary>
    /// Bumped by every StopSceneLoops so one-shot callbacks (the scene
    /// entrance's halo hand-off) can tell their loop from a newer one.
    /// </summary>
    private int _sceneLoopGeneration;

    private void ApplySceneState(int step, bool animate)
    {
        StopSceneLoops();

        // Instant-apply callers (first layout, theme rebuild, intro restart)
        // get no crossfade to mask a pose jump, so park the animated scene
        // pieces on their resting poses whenever the loops were just torn
        // down in one shot.
        if (!animate)
        {
            ResetMarkPose();
        }

        // A shared halo is the only element that persists across every step.
        Set(SceneHalo, 0, 0, step == 0 ? 1f : 0.92f, step == 0 ? 0.55f : 0.4f, animate);

        Set(SceneMark, 0, 0, step == 0 ? 1f : 0.9f, step == 0 ? 1f : 0f, animate);

        bool drop = step == 1;
        Set(SceneDropHero, 0, 0, drop ? 1f : 0.94f, drop ? 1f : 0f, animate);
        Set(SceneFileToken, 0, 0, 1f, drop ? 1f : 0f, animate, 260);
        Set(SceneFileToken2, 0, 0, 1f, drop ? 1f : 0f, animate, 260);
        Set(SceneFileToken3, 0, 0, 1f, drop ? 1f : 0f, animate, 260);
        Set(SceneDropBoxHalo, 0, 0, 1f, 0f, animate, 220);
        Set(SceneDropBoxArt, 0, 0, 1f, 0f, animate, 220);
        Set(SceneDropSlot1, 0, 0, 1f, drop ? 1f : 0f, animate, 220);
        Set(SceneDropSlot2, 0, 0, 1f, drop ? 1f : 0f, animate, 220);
        Set(SceneDropSlot3, 0, 0, 1f, drop ? 1f : 0f, animate, 220);
        Set(SceneMoveBadge, 0, 0, 1f, 0f, animate, 220);
        Set(ScenePathChip, 0, 0, 1f, drop ? 1f : 0f, animate, 220);
        Set(ScenePathParticle, 0, 0, 1f, 0f, animate, 220);

        bool keycap = step == 2;
        Set(SceneKeycapHost, 0, keycap ? 0f : 12f, keycap ? 1f : 0.9f, keycap ? 1f : 0f, animate);
        Set(SceneKeycapHalo, 0, 0, 1f, 0f, animate, 220);

        bool tray = step == 3;
        Set(SceneTaskbar, 0, tray ? 0f : 10f, tray ? 1f : 0.95f, tray ? 1f : 0f, animate);
        Set(SceneTrayMenu, 0, tray ? 0f : 10f, tray ? 1f : 0.9f, tray ? 1f : 0f, animate);
        Set(SceneTrayHalo, 0, 0, 1f, 0f, animate, 220);

        bool features = step == 4;
        Set(SceneFeatureTiles, 0, features ? 0f : 14f, features ? 1f : 0.9f, features ? 1f : 0f, animate);
    }

    private void Set(
        UIElement element,
        float x,
        float y,
        float scale,
        float opacity,
        bool animate,
        int durationMs = SceneTransitionMs)
    {
        var visual = GetVisual(element);
        visual.StopAnimation("Translation");
        visual.StopAnimation("Scale");
        visual.StopAnimation("Opacity");
        var targetTranslation = new Vector3(x, y, 0);
        var targetScale = new Vector3(scale, scale, 1f);

        if (!animate || !WindowsCompatibilityService.AreAnimationsEnabled)
        {
            element.Translation = targetTranslation;
            visual.Scale = targetScale;
            visual.Opacity = opacity;
            return;
        }

        var compositor = visual.Compositor;
        var ease = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));
        var duration = TimeSpan.FromMilliseconds(durationMs);

        var translation = compositor.CreateVector3KeyFrameAnimation();
        translation.Duration = duration;
        translation.InsertKeyFrame(0f, element.Translation);
        translation.InsertKeyFrame(1f, targetTranslation, ease);
        visual.StartAnimation("Translation", translation);

        var scaleAnimation = compositor.CreateVector3KeyFrameAnimation();
        scaleAnimation.Duration = duration;
        scaleAnimation.InsertKeyFrame(0f, visual.Scale);
        scaleAnimation.InsertKeyFrame(1f, targetScale, ease);
        visual.StartAnimation("Scale", scaleAnimation);

        var opacityAnimation = compositor.CreateScalarKeyFrameAnimation();
        opacityAnimation.Duration = TimeSpan.FromMilliseconds(
            Math.Min(durationMs, SceneTransitionMs - 80));
        opacityAnimation.InsertKeyFrame(0f, visual.Opacity);
        opacityAnimation.InsertKeyFrame(1f, opacity, ease);
        visual.StartAnimation("Opacity", opacityAnimation);
    }

    private void StartSceneLoops(int step)
    {
        if (!WindowsCompatibilityService.AreAnimationsEnabled)
        {
            return;
        }

        switch (step)
        {
            case 0:
                StartMarkAssemblyLoop();
                break;
            case 1:
                StartFileFlightLoop();
                break;
            case 2:
                StartKeycapPressLoop();
                break;
            case 3:
                StartTrayHaloLoop();
                break;
            case 4:
                StartFeatureFloatLoop();
                break;
        }
    }

    private void StopSceneLoops()
    {
        _sceneLoopGeneration++;
        StopLoopAnimation(SceneHalo);
        StopLoopAnimation(SceneMark);
        StopLoopAnimation(SceneMarkGroup);
        StopLoopAnimation(SceneMarkSlab0);
        StopLoopAnimation(SceneMarkSlab1);
        StopLoopAnimation(SceneMarkSlab2);
        StopLoopAnimation(SceneDropHero);
        StopLoopAnimation(SceneDropBox);
        StopLoopAnimation(SceneDropBoxArt);
        StopLoopAnimation(SceneDropSlot1);
        StopLoopAnimation(SceneDropSlot2);
        StopLoopAnimation(SceneDropSlot3);
        StopLoopAnimation(SceneDropBoxHalo);
        StopLoopAnimation(SceneFileToken);
        StopLoopAnimation(SceneFileToken2);
        StopLoopAnimation(SceneFileToken3);
        StopLoopAnimation(SceneMoveBadge);
        StopLoopAnimation(ScenePathChip);
        StopLoopAnimation(ScenePathParticle);
        StopLoopAnimation(SceneKeycap);
        StopLoopAnimation(SceneKeycapHalo);
        StopLoopAnimation(SceneTrayHalo);
        foreach (var child in SceneFeatureTiles.Children)
        {
            StopLoopAnimation(child);
        }
    }

    private static void StopLoopAnimation(UIElement element)
    {
        var visual = GetVisual(element);
        visual.StopAnimation("Translation");
        visual.StopAnimation("RotationAngle");
        visual.StopAnimation("Scale");
        visual.StopAnimation("Opacity");
    }

    // ── Step 1: the mark assembles — the three slabs coincide face-on as one
    // tile, fan apart along the logo diagonal, permute cyclically (each beat
    // moves every slab one slot; the front slab hops over the top, scaled
    // down and dimmed to read as passing behind), then merge back into the
    // stacked logo, breathe, and snap shut into the next cycle ──

    private void StartMarkAssemblyLoop()
    {
        var duration = TimeSpan.FromMilliseconds(MarkAssemblyLoopMs);
        var compositor = GetVisual(SceneHalo).Compositor;
        var easeOut = EaseOut();
        var easeIn = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.7f, 0f), new Vector2(0.84f, 0f));
        var easeInOut = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.65f, 0f), new Vector2(0.35f, 1f));
        var arcEase = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.45f, 0f), new Vector2(0.25f, 1f));

        // Waypoints are the approved 240px-stage preview scaled to the 128px
        // mark. pitch = one (4,3)-viewBox-unit slab step so all slabs coincide
        // on the middle footprint; back/middle/front are the exploded slots
        // straddling the resting logo along its diagonal; arc is the hop over
        // the top. Fixed z-order by design — the "behind" pass is faked with
        // scale + dim because Composition cannot animate Canvas.ZIndex.
        var pitch = new Vector2(21.3f, 16f);
        var back = new Vector2(-20.3f, -14.9f);
        var middle = new Vector2(2.1f, -2.1f);
        var front = new Vector2(22.4f, 17.1f);
        var arc = new Vector2(1.1f, -34.1f);

        // Back slab: coincides, fans out first, hops over the top on beat 3.
        StartMarkSlabLoop(SceneMarkSlab0, duration,
        [
            new(0.00f, pitch),
            new(0.12f, pitch, Easing: easeOut),
            new(0.26f, back, 1.02f),
            new(0.36f, back),
            new(0.46f, middle, Easing: easeInOut),
            new(0.56f, front, Easing: easeInOut),
            new(0.60f, arc, 0.94f, 0.85f, arcEase),
            new(0.64f, back, Easing: arcEase),
            new(0.68f, back),
            new(0.78f, Vector2.Zero, Easing: easeOut),
            new(0.92f, Vector2.Zero),
            new(1.00f, pitch, 0.99f, Easing: easeIn),
        ]);

        // Middle slab: hops over the top on beat 2.
        StartMarkSlabLoop(SceneMarkSlab1, duration,
        [
            new(0.00f, Vector2.Zero),
            new(0.14f, Vector2.Zero, Easing: easeOut),
            new(0.28f, middle, 1.02f),
            new(0.36f, middle),
            new(0.46f, front, Easing: easeInOut),
            new(0.51f, arc, 0.94f, 0.85f, arcEase),
            new(0.56f, back, Easing: arcEase),
            new(0.64f, middle, Easing: easeInOut),
            new(0.70f, middle),
            new(0.80f, Vector2.Zero, Easing: easeOut),
            new(0.92f, Vector2.Zero),
            new(1.00f, Vector2.Zero),
        ]);

        // Front slab: hops over the top on beat 1.
        StartMarkSlabLoop(SceneMarkSlab2, duration,
        [
            new(0.00f, -pitch),
            new(0.16f, -pitch, Easing: easeOut),
            new(0.30f, front, 1.02f),
            new(0.36f, front),
            new(0.41f, arc, 0.94f, 0.85f, arcEase),
            new(0.46f, back, Easing: arcEase),
            new(0.56f, middle, Easing: easeInOut),
            new(0.64f, front, Easing: easeInOut),
            new(0.72f, front),
            new(0.82f, Vector2.Zero, Easing: easeOut),
            new(0.92f, Vector2.Zero),
            new(1.00f, -pitch, 0.99f, Easing: easeIn),
        ]);

        StartMarkGroupLoop(duration, easeOut, easeInOut);
        StartMarkHaloPulse(duration);
    }

    /// <summary>
    /// Drives one slab's Translation/Scale/Opacity from a single key table so
    /// the three properties stay phase-locked through the assembly loop.
    /// </summary>
    private static void StartMarkSlabLoop(UIElement slab, TimeSpan duration, MarkKey[] keys)
    {
        var visual = GetVisual(slab);
        var compositor = visual.Compositor;
        var translation = compositor.CreateVector3KeyFrameAnimation();
        var scale = compositor.CreateVector3KeyFrameAnimation();
        var opacity = compositor.CreateScalarKeyFrameAnimation();
        foreach (var animation in new KeyFrameAnimation[] { translation, scale, opacity })
        {
            animation.Duration = duration;
            animation.IterationBehavior = AnimationIterationBehavior.Forever;
        }

        foreach (var key in keys)
        {
            var translationValue = new Vector3(key.Translation.X, key.Translation.Y, 0f);
            var scaleValue = new Vector3(key.Scale, key.Scale, 1f);
            if (key.Easing is null)
            {
                translation.InsertKeyFrame(key.Progress, translationValue);
                scale.InsertKeyFrame(key.Progress, scaleValue);
                opacity.InsertKeyFrame(key.Progress, key.Opacity);
            }
            else
            {
                translation.InsertKeyFrame(key.Progress, translationValue, key.Easing);
                scale.InsertKeyFrame(key.Progress, scaleValue, key.Easing);
                opacity.InsertKeyFrame(key.Progress, key.Opacity, key.Easing);
            }
        }

        visual.StartAnimation("Translation", translation);
        visual.StartAnimation("Scale", scale);
        visual.StartAnimation("Opacity", opacity);
    }

    /// <summary>
    /// Group-level motion on SceneMarkGroup only: a breathe at the coincided
    /// tile, a lift at the merge impact, a settle breathe on the finished
    /// logo, and a squash into the snap-shut.
    /// </summary>
    private void StartMarkGroupLoop(
        TimeSpan duration,
        CompositionEasingFunction easeOut,
        CompositionEasingFunction easeInOut)
    {
        var visual = GetVisual(SceneMarkGroup);
        var translation = visual.Compositor.CreateVector3KeyFrameAnimation();
        translation.Duration = duration;
        translation.IterationBehavior = AnimationIterationBehavior.Forever;
        translation.InsertKeyFrame(0.00f, Vector3.Zero);
        translation.InsertKeyFrame(0.05f, new Vector3(0, -1.6f, 0), easeInOut);
        translation.InsertKeyFrame(0.10f, Vector3.Zero, easeInOut);
        translation.InsertKeyFrame(0.64f, Vector3.Zero);
        translation.InsertKeyFrame(0.70f, new Vector3(0, -1.1f, 0), easeOut);
        translation.InsertKeyFrame(0.78f, Vector3.Zero, easeInOut);
        translation.InsertKeyFrame(0.85f, new Vector3(0, -3.2f, 0), easeInOut);
        translation.InsertKeyFrame(0.92f, Vector3.Zero, easeInOut);
        translation.InsertKeyFrame(1.00f, Vector3.Zero);
        visual.StartAnimation("Translation", translation);

        var scale = visual.Compositor.CreateVector3KeyFrameAnimation();
        scale.Duration = duration;
        scale.IterationBehavior = AnimationIterationBehavior.Forever;
        scale.InsertKeyFrame(0.00f, Vector3.One);
        scale.InsertKeyFrame(0.05f, new Vector3(1.012f, 1.012f, 1f), easeInOut);
        scale.InsertKeyFrame(0.10f, Vector3.One, easeInOut);
        scale.InsertKeyFrame(0.64f, Vector3.One);
        scale.InsertKeyFrame(0.70f, new Vector3(1.02f, 1.02f, 1f), easeOut);
        scale.InsertKeyFrame(0.78f, Vector3.One, easeInOut);
        scale.InsertKeyFrame(0.96f, new Vector3(0.99f, 0.99f, 1f));
        scale.InsertKeyFrame(1.00f, Vector3.One);
        visual.StartAnimation("Scale", scale);
    }

    /// <summary>
    /// The shared halo beats with the loop — one pulse per permutation hop,
    /// a bigger flash as the slabs merge into the logo. Also restarted by
    /// the scene entrance once its one-shot fade hands Opacity back.
    /// </summary>
    private void StartMarkHaloPulse(TimeSpan duration)
    {
        var visual = GetVisual(SceneHalo);
        var compositor = visual.Compositor;
        var easeInOut = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.65f, 0f), new Vector2(0.35f, 1f));

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.Duration = duration;
        opacity.IterationBehavior = AnimationIterationBehavior.Forever;
        opacity.InsertKeyFrame(0.00f, 0.55f);
        opacity.InsertKeyFrame(0.41f, 0.78f, easeInOut);
        opacity.InsertKeyFrame(0.46f, 0.55f, easeInOut);
        opacity.InsertKeyFrame(0.51f, 0.78f, easeInOut);
        opacity.InsertKeyFrame(0.56f, 0.55f, easeInOut);
        opacity.InsertKeyFrame(0.61f, 0.78f, easeInOut);
        opacity.InsertKeyFrame(0.66f, 0.55f, easeInOut);
        opacity.InsertKeyFrame(0.74f, 0.95f, easeInOut);
        opacity.InsertKeyFrame(0.82f, 0.55f, easeInOut);
        opacity.InsertKeyFrame(1.00f, 0.55f);
        visual.StartAnimation("Opacity", opacity);

        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.Duration = duration;
        scale.IterationBehavior = AnimationIterationBehavior.Forever;
        scale.InsertKeyFrame(0.00f, Vector3.One);
        scale.InsertKeyFrame(0.41f, new Vector3(1.06f, 1.06f, 1f), easeInOut);
        scale.InsertKeyFrame(0.46f, Vector3.One, easeInOut);
        scale.InsertKeyFrame(0.51f, new Vector3(1.06f, 1.06f, 1f), easeInOut);
        scale.InsertKeyFrame(0.56f, Vector3.One, easeInOut);
        scale.InsertKeyFrame(0.61f, new Vector3(1.06f, 1.06f, 1f), easeInOut);
        scale.InsertKeyFrame(0.66f, Vector3.One, easeInOut);
        scale.InsertKeyFrame(0.74f, new Vector3(1.12f, 1.12f, 1f), easeInOut);
        scale.InsertKeyFrame(0.82f, Vector3.One, easeInOut);
        scale.InsertKeyFrame(1.00f, Vector3.One);
        visual.StartAnimation("Scale", scale);
    }

    /// <summary>
    /// Parks the mark slabs and group on the exact logo pose — the static
    /// fallback for instant scene applies and disabled animations.
    /// </summary>
    private void ResetMarkPose()
    {
        foreach (var element in new UIElement[]
                 { SceneMarkSlab0, SceneMarkSlab1, SceneMarkSlab2, SceneMarkGroup })
        {
            var visual = GetVisual(element);
            visual.StopAnimation("Translation");
            visual.StopAnimation("Scale");
            visual.StopAnimation("Opacity");
            element.Translation = Vector3.Zero;
            visual.Scale = Vector3.One;
            visual.Opacity = 1f;
        }
    }

    private readonly record struct MarkKey(
        float Progress,
        Vector2 Translation,
        float Scale = 1f,
        float Opacity = 1f,
        CompositionEasingFunction? Easing = null);

    // ── Step 2: three scattered desktop files get inhaled one by one — each
    // landing squashes the box and pops that file's icon into a tidy slot
    // (icon-view row of three) while the storage art hands over; the move
    // badge pops on the last landing, then the path chip slides out below the
    // box and a file particle drops into it. 7.2s, mirrored from the approved
    // preview; the drop choreography keeps fixed z-order throughout ──

    private void StartFileFlightLoop()
    {
        var duration = TimeSpan.FromMilliseconds(DropSceneLoopMs);
        var compositor = GetVisual(SceneHalo).Compositor;
        var easeOut = EaseOut();
        var easeIn = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.7f, 0f), new Vector2(0.84f, 0f));
        var easeInOut = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.65f, 0f), new Vector2(0.35f, 1f));

        // Token waypoints end with the icon center at the box center
        // (180,205 canvas). No rotation by design — the scatter reads from
        // positions alone; Fluent motion prefers straight, eased flights.
        var zero = Vector2.Zero;
        StartDropLoop(SceneFileToken, duration,
        [
            new(0.00f, zero),
            new(0.05f, new Vector2(0, -3f), 0f, 1f, 1f, easeInOut),
            new(0.12f, zero, 0f, 1f, 1f, easeInOut),
            new(0.19f, new Vector2(77, -54), 0f, 0.96f, 1f, easeInOut),
            new(0.24f, new Vector2(124, -61), 0f, 0.72f, 0f, easeInOut),
            new(0.90f, new Vector2(124, -61), 0f, 0.72f, 0f),
            new(0.92f, zero, 0f, 1f, 0f),
            new(0.97f, zero, 0f, 1f, 1f, easeIn),
            new(1.00f, zero),
        ]);
        StartDropLoop(SceneFileToken2, duration,
        [
            new(0.00f, zero),
            new(0.05f, new Vector2(0, -2f), 0f, 1f, 1f, easeInOut),
            new(0.20f, zero, 0f, 1f, 1f, easeInOut),
            new(0.27f, new Vector2(38, -74), 0f, 0.96f, 1f, easeInOut),
            new(0.32f, new Vector2(62, -93), 0f, 0.72f, 0f, easeInOut),
            new(0.90f, new Vector2(62, -93), 0f, 0.72f, 0f),
            new(0.92f, zero, 0f, 1f, 0f),
            new(0.97f, zero, 0f, 1f, 1f, easeIn),
            new(1.00f, zero),
        ]);
        StartDropLoop(SceneFileToken3, duration,
        [
            new(0.00f, zero),
            new(0.05f, new Vector2(0, -3.5f), 0f, 1f, 1f, easeInOut),
            new(0.28f, zero, 0f, 1f, 1f, easeInOut),
            new(0.35f, new Vector2(71, -96), 0f, 0.96f, 1f, easeInOut),
            new(0.40f, new Vector2(114, -129), 0f, 0.72f, 0f, easeInOut),
            new(0.90f, new Vector2(114, -129), 0f, 0.72f, 0f),
            new(0.92f, zero, 0f, 1f, 0f),
            new(0.97f, zero, 0f, 1f, 1f, easeIn),
            new(1.00f, zero),
        ]);

        // The box swallows with a quick squash at each landing (hold 1 until
        // the first landing approaches — no pre-event drift).
        StartDropLoop(SceneDropBox, duration,
        [
            new(0.00f, zero, 0f),
            new(0.20f, zero, 0f),
            new(0.24f, zero, 0f, 1.03f, 1f, easeOut),
            new(0.29f, zero, 0f, 1f, 1f, easeInOut),
            new(0.32f, zero, 0f, 1.028f, 1f, easeOut),
            new(0.37f, zero, 0f, 1f, 1f, easeInOut),
            new(0.40f, zero, 0f, 1.03f, 1f, easeOut),
            new(0.47f, zero, 0f, 1f, 1f, easeInOut),
            new(1.00f, zero, 0f),
        ]);

        // The storage art hands its interior over to the icon row.
        StartDropLoop(SceneDropBoxArt, duration,
        [
            new(0.00f, zero, 0f, 1f, 1f),
            new(0.12f, zero, 0f, 1f, 1f),
            new(0.20f, zero, 0f, 0.96f, 0f, easeInOut),
            new(0.88f, zero, 0f, 0.96f, 0f),
            new(0.96f, zero, 0f, 1f, 1f, easeInOut),
            new(1.00f, zero, 0f, 1f, 1f),
        ]);

        StartDropLoop(SceneDropSlot1, duration, BuildSlotKeys(0.24f, easeOut, easeInOut));
        StartDropLoop(SceneDropSlot2, duration, BuildSlotKeys(0.32f, easeOut, easeInOut));
        StartDropLoop(SceneDropSlot3, duration, BuildSlotKeys(0.40f, easeOut, easeInOut));

        // One halo pulse per landing, another as the chip reveals.
        StartDropLoop(SceneDropBoxHalo, duration,
        [
            new(0.00f, zero, 0f, 1f, 0f),
            new(0.16f, zero, 0f, 1f, 0f),
            new(0.24f, zero, 0f, 1f, 0.28f, easeInOut),
            new(0.30f, zero, 0f, 1f, 0.05f, easeInOut),
            new(0.32f, zero, 0f, 1f, 0.26f, easeInOut),
            new(0.38f, zero, 0f, 1f, 0.05f, easeInOut),
            new(0.40f, zero, 0f, 1f, 0.28f, easeInOut),
            new(0.47f, zero, 0f, 1f, 0.05f, easeInOut),
            new(0.56f, zero, 0f, 1.02f, 0.22f, easeInOut),
            new(0.66f, zero, 0f, 1f, 0f, easeInOut),
            new(1.00f, zero, 0f, 1f, 0f),
        ]);

        StartDropLoop(SceneMoveBadge, duration,
        [
            new(0.00f, zero, 0f, 0.85f, 0f),
            new(0.40f, zero, 0f, 0.85f, 0f),
            new(0.45f, zero, 0f, 1.08f, 1f, easeOut),
            new(0.49f, zero, 0f, 1f, 1f, easeInOut),
            new(0.84f, zero, 0f, 1f, 1f),
            new(0.90f, zero, 0f, 0.9f, 0f),
            new(1.00f, zero, 0f, 0.85f, 0f),
        ]);

        // The chip rests hidden just above its slot, slides out with a soft
        // overshoot, and retracts during the reset.
        StartDropLoop(ScenePathChip, duration,
        [
            new(0.00f, new Vector2(0, -22f), 0f, 0.98f, 0f),
            new(0.46f, new Vector2(0, -22f), 0f, 0.98f, 0f, easeOut),
            new(0.56f, zero, 0f, 1.02f, 1f, easeOut),
            new(0.60f, zero, 0f, 1f, 1f, easeInOut),
            new(0.86f, zero, 0f, 1f, 1f),
            new(0.94f, new Vector2(0, -10f), 0f, 0.99f, 0f, easeIn),
            new(1.00f, new Vector2(0, -22f), 0f, 0.98f, 0f),
        ]);

        // A file particle drops from the box floor into the chip's folder
        // (chip widened to 240, so the icon sits further left).
        StartDropLoop(ScenePathParticle, duration,
        [
            new(0.00f, zero, 0f, 1f, 0f),
            new(0.46f, zero, 0f, 1f, 0f),
            new(0.50f, zero, 0f, 1f, 1f, easeOut),
            new(0.58f, new Vector2(-55, 4), 0f, 0.85f, 1f, easeInOut),
            new(0.66f, new Vector2(-102, 43), 0f, 0.2f, 0f, easeIn),
            new(1.00f, new Vector2(-102, 43), 0f, 0.2f, 0f),
        ]);

        // Group breathe on the whole hero while the story holds still.
        StartDropLoop(SceneDropHero, duration,
        [
            new(0.00f, zero, 0f),
            new(0.62f, zero, 0f),
            new(0.74f, new Vector2(0, -3f), 0f, 1f, 1f, easeInOut),
            new(0.86f, zero, 0f, 1f, 1f, easeInOut),
            new(1.00f, zero, 0f),
        ]);
    }

    private static DropKey[] BuildSlotKeys(
        float landing,
        CompositionEasingFunction easeOut,
        CompositionEasingFunction easeInOut)
    {
        return
        [
            new(0.00f, Vector2.Zero, 0f, 0.3f, 0f),
            new(landing - 0.02f, Vector2.Zero, 0f, 0.3f, 0f),
            new(landing + 0.02f, Vector2.Zero, 0f, 1.08f, 1f, easeOut),
            new(landing + 0.06f, Vector2.Zero, 0f, 1f, 1f, easeInOut),
            new(0.88f, Vector2.Zero, 0f, 1f, 1f),
            new(0.94f, Vector2.Zero, 0f, 0.85f, 0f),
            new(1.00f, Vector2.Zero, 0f, 0.3f, 0f),
        ];
    }

    /// <summary>
    /// Drives one drop-scene element's Translation/RotationAngle/Scale/Opacity
    /// from a single key table so all four properties stay phase-locked.
    /// </summary>
    private static void StartDropLoop(UIElement element, TimeSpan duration, DropKey[] keys)
    {
        var visual = GetVisual(element);
        var compositor = visual.Compositor;
        var translation = compositor.CreateVector3KeyFrameAnimation();
        var rotation = compositor.CreateScalarKeyFrameAnimation();
        var scale = compositor.CreateVector3KeyFrameAnimation();
        var opacity = compositor.CreateScalarKeyFrameAnimation();
        foreach (var animation in new KeyFrameAnimation[]
                 { translation, rotation, scale, opacity })
        {
            animation.Duration = duration;
            animation.IterationBehavior = AnimationIterationBehavior.Forever;
        }

        foreach (var key in keys)
        {
            var translationValue = new Vector3(key.Translation.X, key.Translation.Y, 0f);
            var scaleValue = new Vector3(key.Scale, key.Scale, 1f);
            if (key.Easing is null)
            {
                translation.InsertKeyFrame(key.Progress, translationValue);
                rotation.InsertKeyFrame(key.Progress, key.RotationDegrees);
                scale.InsertKeyFrame(key.Progress, scaleValue);
                opacity.InsertKeyFrame(key.Progress, key.Opacity);
            }
            else
            {
                translation.InsertKeyFrame(key.Progress, translationValue, key.Easing);
                rotation.InsertKeyFrame(key.Progress, key.RotationDegrees, key.Easing);
                scale.InsertKeyFrame(key.Progress, scaleValue, key.Easing);
                opacity.InsertKeyFrame(key.Progress, key.Opacity, key.Easing);
            }
        }

        visual.StartAnimation("Translation", translation);
        visual.StartAnimation("RotationAngle", rotation);
        visual.StartAnimation("Scale", scale);
        visual.StartAnimation("Opacity", opacity);
    }

    private readonly record struct DropKey(
        float Progress,
        Vector2 Translation,
        float RotationDegrees = 0f,
        float Scale = 1f,
        float Opacity = 1f,
        CompositionEasingFunction? Easing = null);

    // ── Step 3: the keycap depresses with a soft halo ping ──

    private void StartKeycapPressLoop()
    {
        var duration = TimeSpan.FromMilliseconds(1800);
        var ease = EaseOut();

        var keycapVisual = GetVisual(SceneKeycap);
        var press = keycapVisual.Compositor.CreateVector3KeyFrameAnimation();
        press.Duration = duration;
        press.IterationBehavior = AnimationIterationBehavior.Forever;
        press.InsertKeyFrame(0f, Vector3.One);
        press.InsertKeyFrame(0.12f, new Vector3(0.9f, 0.9f, 1f), ease);
        press.InsertKeyFrame(0.28f, Vector3.One, ease);
        press.InsertKeyFrame(1f, Vector3.One);
        keycapVisual.StartAnimation("Scale", press);

        var haloVisual = GetVisual(SceneKeycapHalo);
        var halo = haloVisual.Compositor.CreateScalarKeyFrameAnimation();
        halo.Duration = duration;
        halo.IterationBehavior = AnimationIterationBehavior.Forever;
        halo.InsertKeyFrame(0f, 0f);
        halo.InsertKeyFrame(0.12f, 0.4f, ease);
        halo.InsertKeyFrame(0.58f, 0f, ease);
        halo.InsertKeyFrame(1f, 0f);
        haloVisual.StartAnimation("Opacity", halo);

        var haloScale = haloVisual.Compositor.CreateVector3KeyFrameAnimation();
        haloScale.Duration = duration;
        haloScale.IterationBehavior = AnimationIterationBehavior.Forever;
        haloScale.InsertKeyFrame(0f, new Vector3(0.9f, 0.9f, 1f));
        haloScale.InsertKeyFrame(0.58f, new Vector3(1.16f, 1.16f, 1f), ease);
        haloScale.InsertKeyFrame(1f, new Vector3(0.9f, 0.9f, 1f));
        haloVisual.StartAnimation("Scale", haloScale);
    }

    // ── Step 4: the tray icon pings under the menu ──

    private void StartTrayHaloLoop()
    {
        var duration = TimeSpan.FromMilliseconds(1400);
        var ease = EaseOut();
        var haloVisual = GetVisual(SceneTrayHalo);

        var halo = haloVisual.Compositor.CreateScalarKeyFrameAnimation();
        halo.Duration = duration;
        halo.IterationBehavior = AnimationIterationBehavior.Forever;
        halo.InsertKeyFrame(0f, 0.08f);
        halo.InsertKeyFrame(0.5f, 0.3f, ease);
        halo.InsertKeyFrame(1f, 0.08f, ease);
        haloVisual.StartAnimation("Opacity", halo);

        var haloScale = haloVisual.Compositor.CreateVector3KeyFrameAnimation();
        haloScale.Duration = duration;
        haloScale.IterationBehavior = AnimationIterationBehavior.Forever;
        haloScale.InsertKeyFrame(0f, new Vector3(0.88f, 0.88f, 1f));
        haloScale.InsertKeyFrame(0.5f, new Vector3(1.14f, 1.14f, 1f), ease);
        haloScale.InsertKeyFrame(1f, new Vector3(0.88f, 0.88f, 1f), ease);
        haloVisual.StartAnimation("Scale", haloScale);
    }

    // ── Step 5: the icon tiles idle-float with a gentle stagger ──

    private void StartFeatureFloatLoop()
    {
        var durations = new[] { 2600, 3000, 3400 };
        var index = 0;
        foreach (var child in SceneFeatureTiles.Children)
        {
            StartFloat(child, durations[index % durations.Length], (index * 130) % 700);
            index++;
        }
    }

    private void StartFloat(UIElement element, int durationMs, int delayMs)
    {
        var visual = GetVisual(element);
        var duration = TimeSpan.FromMilliseconds(durationMs);
        var ease = EaseOut();
        var rise = visual.Compositor.CreateVector3KeyFrameAnimation();
        rise.Duration = duration;
        rise.DelayTime = TimeSpan.FromMilliseconds(delayMs);
        rise.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        rise.IterationBehavior = AnimationIterationBehavior.Forever;
        rise.InsertKeyFrame(0f, Vector3.Zero);
        rise.InsertKeyFrame(0.5f, new Vector3(0, -3.5f, 0), ease);
        rise.InsertKeyFrame(1f, Vector3.Zero, ease);
        visual.StartAnimation("Translation", rise);
    }

    /// <summary>
    /// One-shot entrance on first paint: the halo fades in and the mark settles.
    /// </summary>
    private void PlaySceneEntrance()
    {
        if (!WindowsCompatibilityService.AreAnimationsEnabled || _stepIndex != 0)
        {
            return;
        }

        var haloVisual = GetVisual(SceneHalo);
        haloVisual.StopAnimation("Opacity");
        var haloFade = haloVisual.Compositor.CreateScalarKeyFrameAnimation();
        haloFade.Duration = TimeSpan.FromMilliseconds(600);
        haloFade.InsertKeyFrame(0f, 0f);
        haloFade.InsertKeyFrame(1f, 0.55f, EaseOut());

        // This one-shot fade owns SceneHalo.Opacity while it runs, cancelling
        // the assembly loop's pulse — hand the property back once it lands,
        // unless the scene already moved on (generation bump) or closed.
        int loopGeneration = _sceneLoopGeneration;
        var batch = haloVisual.Compositor.CreateScopedBatch(
            CompositionBatchTypes.Animation);
        haloVisual.StartAnimation("Opacity", haloFade);
        batch.End();
        batch.Completed += (_, _) =>
        {
            if (!_isClosed &&
                _stepIndex == 0 &&
                _sceneLoopGeneration == loopGeneration)
            {
                StartMarkHaloPulse(TimeSpan.FromMilliseconds(MarkAssemblyLoopMs));
            }
        };

        var markVisual = GetVisual(SceneMark);
        markVisual.StopAnimation("Opacity");
        markVisual.StopAnimation("Scale");
        var markFade = markVisual.Compositor.CreateScalarKeyFrameAnimation();
        markFade.Duration = TimeSpan.FromMilliseconds(420);
        markFade.DelayTime = TimeSpan.FromMilliseconds(120);
        markFade.DelayBehavior = Microsoft.UI.Composition.AnimationDelayBehavior.SetInitialValueBeforeDelay;
        markFade.InsertKeyFrame(0f, 0f);
        markFade.InsertKeyFrame(1f, 1f, EaseOut());
        markVisual.StartAnimation("Opacity", markFade);
        var markScale = markVisual.Compositor.CreateVector3KeyFrameAnimation();
        markScale.Duration = TimeSpan.FromMilliseconds(480);
        markScale.DelayTime = TimeSpan.FromMilliseconds(120);
        markScale.DelayBehavior = Microsoft.UI.Composition.AnimationDelayBehavior.SetInitialValueBeforeDelay;
        markScale.InsertKeyFrame(0f, new Vector3(0.92f, 0.92f, 1f));
        markScale.InsertKeyFrame(1f, Vector3.One, EaseOut());
        markVisual.StartAnimation("Scale", markScale);
    }

    private CubicBezierEasingFunction EaseOut()
    {
        return GetVisual(SceneHalo).Compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));
    }
}
