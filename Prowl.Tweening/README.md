# Prowl.Tweening

A tweening library for animating values over time: positions, colours, health bars, UI fades,
anything that changes from A to B on a curve.

- **Zero dependencies.** Nothing but the BCL. Copy the folder into any repo and it compiles.
- **No GC at steady state.** Creating, running and killing tweens allocates **0 bytes** once the
  internal arrays have grown. Verified, not aspirational.
- **Fast.** ~9 ns per tween per frame; 100 000 simultaneous tweens tick in under 1 ms.

> The public API is deliberately shaped like DOTween's, and the internals borrow LitMotion's
> data-oriented storage, so anyone arriving from either will feel at home. That is the only place
> those libraries are mentioned - everything below is described on its own terms.

## Setup

Inside Prowl this is already wired up: `Game` ticks the library every frame, so tweens just work.

Standalone, call the update methods yourself from your game loop:

```csharp
Tween.Update(deltaTime, unscaledDeltaTime);      // drives UpdateType.Normal + reclaims dead tweens
Tween.LateUpdate(deltaTime);                     // UpdateType.Late
Tween.FixedUpdate(fixedDeltaTime);               // UpdateType.Fixed
Tween.ManualUpdate(myDeltaTime);                 // UpdateType.Manual
```

`Tween.Update` is the only one you must call - it also performs the per-frame cleanup pass.
Everything is single-threaded; create, configure and tick from the same thread.

## Transform shortcuts

`Prowl.Runtime` adds extension methods for `Transform`, which is what most gameplay code wants:

```csharp
transform.Move(new Float3(0, 5, 0), 1f).SetEase(Ease.OutBack);
transform.MoveY(5f, 1f);                       // one axis, leaves the others alone
transform.LocalMove(target, 1f);
transform.RotateTo(new Float3(0, 90, 0), 1f);  // euler degrees
transform.RotateTo(someQuaternion, 1f);        // shortest arc
transform.LocalRotateTo(euler, 1f);
transform.Scale(2f, 0.3f).SetLoops(2, LoopType.Yoyo);
transform.ScaleX(1.5f, 0.3f);
transform.LookAt(enemy.Transform.Position, 0.4f);
transform.KillTweens();                        // stop everything animating this transform
```

Each one reads the transform's current value as its start, tags the tween with the transform as its
target, and returns the handle so you can keep chaining. They are allocation-free.

`RotateTo` carries the `To` suffix because `Transform` already has an instance method
`Rotate(Float3 axis, float angle)` - an instance method always beats an extension method, so a
`transform.Rotate(euler, 1f)` shortcut would have silently snap-rotated instead of tweening.

## Creating tweens directly

**Allocation-free** - hand over the state instead of capturing it, and keep the lambda `static`:

```csharp
Tween.To(player.Health, 100f, 1f)
     .Bind(player, static (v, p) => p.Health = v)
     .SetEase(Ease.OutQuad)
     .OnComplete(player, static p => p.OnHealed());
```

`Bind` is what actually schedules the tween - until you call it, nothing exists.

**Getter/setter** - shorter to write, at the cost of the two delegates you pass in:

```csharp
Tween.To(() => player.Health, x => player.Health = x, 100f, 1f)
     .SetEase(Ease.OutQuad);
```

Built-in value types: `float`, `double`, `int`, `long`, and `System.Numerics`' `Vector2`,
`Vector3`, `Vector4`, `Quaternion`. Inside Prowl, `Float2`, `Float3`, `Float4`, `Quaternion` and
`Color` are available too through the adapters in `Prowl.Runtime`:

```csharp
Tween.To<Color, ColorAdapter>(sprite.Color, Color.Red, 0.5f)
     .Bind(sprite, static (c, s) => s.Color = c);
```

## The `Tween` handle

`Tween` is a 12-byte struct, not an object. Store it, copy it, pass it around - never allocates.
Every method is safe on a dead or `default` handle; it just does nothing.

```csharp
Tween t = Tween.To(0f, 1f, 2f).Bind(x, static (v, o) => o.Alpha = v);

t.SetEase(Ease.InOutSine)            // or SetEase(ease, overshoot), SetEase(ease, amplitude, period)
 .SetEase(myEaseFunction)            // custom: float f(time, duration, overshoot, period)
 .SetLoops(-1, LoopType.Yoyo)        // Restart | Yoyo | Incremental, -1 = infinite
 .SetDelay(0.5f)
 .SetAutoKill(false)
 .SetUpdate(UpdateType.Late, isIndependentUpdate: true)
 .SetId("ui")                        // for Tween.KillAll("ui")
 .SetTarget(x)                       // for Tween.KillAll(x)
 .SetTimeScale(2f)
 .SetRelative()                      // end = start + end
 .From();                            // swap start/end and apply the new start immediately

t.Play(); t.Pause(); t.TogglePause();
t.Restart(); t.Rewind(); t.Complete(); t.Goto(1.25f); t.Flip();
t.Kill(complete: false);

bool alive = t.IsActive;             // also: IsPlaying, IsComplete, Elapsed, Duration,
                                     // FullDuration, CompletedLoops, TimeScale
```

Callbacks: `OnStart`, `OnPlay`, `OnPause`, `OnUpdate`, `OnStepComplete`, `OnComplete`, `OnKill`,
`OnRewind`. Each has an `Action` overload and an allocation-free `(state, Action<TState>)` overload.

> One caveat on the state overloads: a tween keeps a *single* state object shared by all of its
> typed callbacks, so passing different states to two `On*` calls on the same tween keeps only the
> last one. Passing the same object (the usual case) is fine.

Global control: `Tween.KillAll()`, `Tween.KillAll(idOrTarget)`, `Tween.PauseAll()`,
`Tween.PlayAll(id)`, `Tween.RestartAll(id)`, `Tween.RewindAll(id)`, `Tween.CompleteAll(id)`, and
`Tween.GlobalTimeScale` to slow everything down at once.

## Sequences

A sequence *is* a tween - ease it, loop it, nest it, give it callbacks:

```csharp
Sequence seq = Tween.Sequence();
seq.Append(transform.MoveY(5f, 1f));
seq.Join(transform.Scale(2f, 1f));            // parallel with the last Append
seq.AppendInterval(0.25f);
seq.AppendCallback(() => Console.WriteLine("halfway"));
seq.Insert(2f, someTween);
seq.Prepend(introTween);
seq.SetLoops(3, LoopType.Yoyo).OnComplete(() => Console.WriteLine("done"));
```

`Sequence` converts implicitly to `Tween`, so it works anywhere a tween does - including inside
another sequence. Killing a sequence kills its children.

## Odds and ends

```csharp
Tween.DelayedCall(2f, () => Spawn());
Tween.DelayedCall(2f, this, static self => self.Spawn());   // allocation-free
float v = Tween.EasedValue(0f, 1f, 0.4f, Ease.OutBack);     // sample a curve, no tween involved
float e = EaseUtility.Evaluate(Ease.InOutQuad, 0.3f);       // raw easing
```

Defaults live on `Tween.DefaultEase`, `Tween.DefaultAutoKill`,
`Tween.DefaultEaseOvershootOrAmplitude` and `Tween.DefaultEasePeriod`.

## Tweening your own types

Implement `ITweenAdapter<T>` on a `readonly struct`. Each `(T, TAdapter)` pair gets its own dense
storage automatically, and the JIT inlines `Evaluate` into the update loop - no virtual dispatch:

```csharp
public readonly struct ColorAdapter : ITweenAdapter<Color>
{
    public Color Evaluate(in Color a, in Color b, float t) => new(
        a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t,
        a.B + (b.B - a.B) * t, a.A + (b.A - a.A) * t);

    public Color Add(in Color a, in Color b) => new(a.R + b.R, a.G + b.G, a.B + b.B, a.A + b.A);
}

Tween.To<Color, ColorAdapter>(from, to, 1f).Bind(mat, static (c, m) => m.Color = c);
```

`Evaluate` must be **unclamped** - `LoopType.Incremental` feeds it progress above 1.

## How it works

- One dense array per `(value type, adapter)` pair, holding timing state and start/end values
  inline. The update loop is a straight linear walk over contiguous memory.
- Public handles point at a **sparse slot table**, so entries can be swapped around during
  compaction without invalidating anything. A version counter on each slot means a stale handle
  resolves to nothing instead of to the wrong tween.
- Killing only flips a flag. Structural removal is batched into one sweep per frame, which makes it
  safe to kill tweens from inside a callback.
- Setters are stored as `Action<T, object>` plus a state object, so a `static` lambda captures
  nothing and the delegate is reinterpreted rather than wrapped.
- Callbacks, ids and targets live in a separate array that is only allocated if something uses them.

## Behaviour worth knowing

- A tween's start value is read when it is **created**, not when it first plays. This only matters
  if the property changes during a `SetDelay`.
- `Flip()` swaps start and end and mirrors the playhead inside the current cycle.
- Path, punch, shake and string tweens are not included. Custom adapters cover most of the gap.
- Inside Prowl, tweens on scaled time are frozen while play mode is paused or while the editor is
  in edit mode. Tweens created with `SetUpdate(isIndependentUpdate: true)` run on unscaled time and
  keep going - that is what editor and UI animations want.
