// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Prowl.Tweening;

/// <summary>
/// Owns every typed storage and drives them. Tweening is single-threaded by design: create,
/// configure and tick tweens from the same thread (normally the main/game thread).
/// </summary>
public static class TweenManager
{
    private static ITweenStorage[] s_storages = new ITweenStorage[8];
    private static int s_storageCount;
    private static readonly object s_registerLock = new();

    /// <summary>
    /// Set the first time a tween asks for a given update type, and never cleared. A stale
    /// <c>true</c> only costs one wasted pass, so the common "nobody uses FixedUpdate" case
    /// skips the whole traversal.
    /// </summary>
    private static readonly bool[] s_usesUpdateType = [true, false, false, false];

    /// <summary>Global speed multiplier applied to the scaled delta of every tween.</summary>
    public static float TimeScale = 1f;

    /// <summary>Total number of live tweens across every value type.</summary>
    public static int ActiveTweenCount
    {
        get
        {
            int total = 0;
            for (int i = 0; i < s_storageCount; i++)
                total += s_storages[i].ActiveCount;
            return total;
        }
    }

    /// <summary>
    /// Per-(value type, adapter) singleton storage. The static field is instantiated once per
    /// closed generic type, so resolving a storage is a plain static field read.
    /// </summary>
    internal static class Of<T, TAdapter>
        where T : unmanaged
        where TAdapter : unmanaged, ITweenAdapter<T>
    {
        internal static readonly TweenStorage<T, TAdapter> Storage = Register(new TweenStorage<T, TAdapter>());
    }

    private static TweenStorage<T, TAdapter> Register<T, TAdapter>(TweenStorage<T, TAdapter> storage)
        where T : unmanaged
        where TAdapter : unmanaged, ITweenAdapter<T>
    {
        lock (s_registerLock)
        {
            if (s_storageCount == s_storages.Length)
                Array.Resize(ref s_storages, s_storageCount * 2);

            storage.Id = s_storageCount;
            s_storages[s_storageCount++] = storage;
            return storage;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ITweenStorage? Resolve(int storageId)
        => (uint)storageId < (uint)s_storageCount ? s_storages[storageId] : null;

    internal static void NotifyUpdateType(UpdateType type) => s_usesUpdateType[(int)type] = true;

    #region Ticking

    /// <summary>
    /// Advances <see cref="UpdateType.Normal"/> tweens and reclaims killed ones.
    /// Call once per frame from your engine's update.
    /// </summary>
    /// <param name="deltaTime">Scaled frame delta.</param>
    /// <param name="unscaledDeltaTime">
    /// Unscaled frame delta, used by tweens marked with <c>SetUpdate(type, true)</c>.
    /// Defaults to <paramref name="deltaTime"/> when negative.
    /// </param>
    public static void Update(float deltaTime, float unscaledDeltaTime = -1f)
    {
        Tick(UpdateType.Normal, deltaTime, unscaledDeltaTime);

        for (int i = 0; i < s_storageCount; i++)
            s_storages[i].Sweep();

        // After the sweep, so a continuation sees fully reclaimed storage.
        TweenAwaiterRegistry.Pump();
    }

    /// <summary>Advances <see cref="UpdateType.Late"/> tweens. Call after your engine's late update.</summary>
    public static void LateUpdate(float deltaTime, float unscaledDeltaTime = -1f)
    {
        Tick(UpdateType.Late, deltaTime, unscaledDeltaTime);
        TweenAwaiterRegistry.Pump();
    }

    /// <summary>Advances <see cref="UpdateType.Fixed"/> tweens. Call from your fixed-step loop.</summary>
    public static void FixedUpdate(float fixedDeltaTime, float unscaledFixedDeltaTime = -1f)
    {
        Tick(UpdateType.Fixed, fixedDeltaTime, unscaledFixedDeltaTime);
        TweenAwaiterRegistry.Pump();
    }

    /// <summary>Advances <see cref="UpdateType.Manual"/> tweens. Call it yourself, whenever.</summary>
    public static void ManualUpdate(float deltaTime, float unscaledDeltaTime = -1f)
    {
        Tick(UpdateType.Manual, deltaTime, unscaledDeltaTime);
        TweenAwaiterRegistry.Pump();
    }

    private static void Tick(UpdateType type, float deltaTime, float unscaledDeltaTime)
    {
        if (!s_usesUpdateType[(int)type])
            return;

        if (unscaledDeltaTime < 0f)
            unscaledDeltaTime = deltaTime;

        float scaled = deltaTime * TimeScale;
        for (int i = 0; i < s_storageCount; i++)
            s_storages[i].Update(type, scaled, unscaledDeltaTime);
    }

    #endregion

    #region Bulk control

    /// <summary>
    /// Kills every tween whose id or target equals <paramref name="idOrTarget"/>, or every live
    /// tween when it is null. Returns how many were killed.
    /// </summary>
    public static int Kill(object? idOrTarget, bool complete = false)
    {
        int killed = 0;
        for (int i = 0; i < s_storageCount; i++)
            killed += s_storages[i].KillWhere(idOrTarget, complete);
        return killed;
    }

    internal static int Control(object? idOrTarget, TweenAction action)
    {
        int affected = 0;
        for (int i = 0; i < s_storageCount; i++)
            affected += s_storages[i].ControlWhere(idOrTarget, action);
        return affected;
    }

    #endregion
}
