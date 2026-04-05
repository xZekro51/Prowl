// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Prowl.Runtime.EventSystem;

namespace Prowl.Runtime.Rendering;

/// <summary>
/// Static dispatcher that subscribes to <see cref="RenderingEvents.OnImageEffectsDispatch"/>
/// and executes the active camera's <see cref="ImageEffect"/> components for the requested
/// <see cref="RenderStage"/>.
/// <para>
/// Custom subscribers can hook in at higher or lower priority to run before/after the
/// built-in effects, or to replace the default dispatch entirely.
/// </para>
/// </summary>
internal static class ImageEffectDispatcher
{
    /// <summary>
    /// Registers event subscriptions for image effect dispatch. Called once during
    /// <see cref="Graphics.InitializeEventSubscriptions"/>.
    /// </summary>
    internal static void InitializeEventSubscriptions()
    {
        RenderingEvents.SubscribeOnImageEffectsDispatch(OnDispatch, priority: 0);
    }

    private static void OnDispatch(ImageEffectsDispatchArgs args)
    {
        Camera camera = args.Context.Camera;
        if (camera == null || camera.Effects.Count == 0)
            return;

        RenderStage targetStage = args.Stage;

        foreach (ImageEffect effect in camera.Effects)
        {
            RenderStage effectStage = effect.Stage;

#pragma warning disable CS0618 // Type or member is obsolete
            if (effect.IsOpaqueEffect && effectStage == RenderStage.PostProcess)
                effectStage = RenderStage.AfterLighting;
#pragma warning restore CS0618

            if (effectStage != targetStage)
                continue;

            try
            {
                effect.OnRenderEffect(args.Context);
            }
            catch (Exception ex)
            {
                Debug.LogError($"Image effect {effect.GetType().Name} threw: {ex}");
            }
        }
    }
}
