using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;

[assembly: CompilationRelaxations(8)]
[assembly: RuntimeCompatibility(WrapNonExceptionThrows = true)]
[assembly: Debuggable(DebuggableAttribute.DebuggingModes.Default | DebuggableAttribute.DebuggingModes.DisableOptimizations | DebuggableAttribute.DebuggingModes.IgnoreSymbolStoreSequencePoints | DebuggableAttribute.DebuggingModes.EnableEditAndContinue)]
[assembly: TargetFramework(".NETCoreApp,Version=v10.0", FrameworkDisplayName = ".NET 10.0")]
[assembly: AssemblyMetadata("IsTrimmable", "True")]
[assembly: AssemblyMetadata("IsAotCompatible", "True")]
[assembly: AssemblyCompany("Vortex")]
[assembly: AssemblyConfiguration("Debug")]
[assembly: AssemblyFileVersion("1.0.12.0")]
[assembly: AssemblyInformationalVersion("1.0.12+ff99147ce244d5d50fe3074c12675cef971dfba1")]
[assembly: AssemblyProduct("Vortex")]
[assembly: AssemblyTitle("Vortex")]
[assembly: AssemblyVersion("1.0.12.0")]
[module: RefSafetyRules(11)]
namespace Vortex;

/// <summary>
/// Typed delegate container wrapping a <see cref="T:System.Func`2" /> for async
/// event handlers. Participates in the same priority-sorted snapshot as synchronous
/// containers, enabling mixed sync/async handler chains.
/// <para>
/// When invoked via the synchronous <see cref="M:Vortex.AsyncEventDelegateContainer`2.Invoke(`1)" /> path, the returned
/// <see cref="T:System.Threading.Tasks.Task" /> is observed but not awaited. Use
/// <see cref="M:Vortex.Event`1.InvokeAsync``1(``0)" /> or the generated
/// <c>InvokeXxxAsync</c> methods to properly await async handlers.
/// </para>
/// </summary>
public class AsyncEventDelegateContainer<T, TArgs> : EventDelegateContainer<T, TArgs>, IAsyncInvocable<TArgs> where T : struct, Enum
{
	private readonly Func<TArgs, Task>? _asyncDelegate;

	/// <inheritdoc />
	public override bool MatchesDelegate(Delegate handler)
	{
		return handler?.Equals(_asyncDelegate) ?? false;
	}

	public AsyncEventDelegateContainer(T eventType, Func<TArgs, Task> asyncDelegate, EventPriority priority = default(EventPriority))
		: base(eventType, priority)
	{
		_asyncDelegate = asyncDelegate;
	}

	public AsyncEventDelegateContainer(T eventType, Func<TArgs, Task> asyncDelegate, EventPriority priority, string? sourceFile, int sourceLine, string? sourceMember)
		: base(eventType, priority, sourceFile, sourceLine, sourceMember)
	{
		_asyncDelegate = asyncDelegate;
	}

	/// <summary>
	/// Protected constructor for subclasses that provide their own invocation
	/// logic and do not use the <see cref="F:Vortex.AsyncEventDelegateContainer`2._asyncDelegate" /> field.
	/// </summary>
	protected AsyncEventDelegateContainer(T eventType, EventPriority priority)
		: base(eventType, priority)
	{
	}

	protected AsyncEventDelegateContainer(T eventType, EventPriority priority, string? sourceFile, int sourceLine, string? sourceMember)
		: base(eventType, priority, sourceFile, sourceLine, sourceMember)
	{
	}

	/// <summary>
	/// Synchronous invocation fallback. Fires the async handler without awaiting.
	/// In DEBUG builds a warning is logged - prefer <see cref="M:Vortex.AsyncEventDelegateContainer`2.InvokeAsync(`1)" /> instead.
	/// </summary>
	public override void Invoke(TArgs args)
	{
		if (base.Enabled)
		{
			EventSystemDiagnostics.LogWarning?.Invoke($"[EventSystem] Async handler on {typeof(T).Name} invoked synchronously. Use InvokeAsync/InvokeEventAsync for proper async execution. Handler: {base.SourceDescription}");
			_asyncDelegate?.Invoke(args);
		}
	}

	/// <summary>
	/// Asynchronously invokes the handler and returns the resulting <see cref="T:System.Threading.Tasks.Task" />.
	/// </summary>
	public virtual Task InvokeAsync(TArgs args)
	{
		if (!base.Enabled)
		{
			return Task.CompletedTask;
		}
		return _asyncDelegate?.Invoke(args) ?? Task.CompletedTask;
	}
}
/// <summary>
/// Specialized container for parameterless async events that stores a
/// <see cref="T:System.Func`1" /> directly, avoiding the closure allocation that
/// wrapping in a <see cref="T:System.Func`2" /> would incur.
/// </summary>
public sealed class ParameterlessAsyncEventDelegateContainer<T> : AsyncEventDelegateContainer<T, Unit> where T : struct, Enum
{
	private readonly Func<Task> _asyncAction;

	/// <inheritdoc />
	public override bool MatchesDelegate(Delegate handler)
	{
		return handler?.Equals(_asyncAction) ?? false;
	}

	public ParameterlessAsyncEventDelegateContainer(T eventType, Func<Task> asyncAction, EventPriority priority = default(EventPriority))
		: base(eventType, priority)
	{
		_asyncAction = asyncAction;
	}

	public ParameterlessAsyncEventDelegateContainer(T eventType, Func<Task> asyncAction, EventPriority priority, string? sourceFile, int sourceLine, string? sourceMember)
		: base(eventType, priority, sourceFile, sourceLine, sourceMember)
	{
		_asyncAction = asyncAction;
	}

	public override void Invoke(Unit args)
	{
		if (base.Enabled)
		{
			EventSystemDiagnostics.LogWarning?.Invoke($"[EventSystem] Async handler on {typeof(T).Name} invoked synchronously. Use InvokeAsync/InvokeEventAsync for proper async execution. Handler: {base.SourceDescription}");
			_asyncAction?.Invoke();
		}
	}

	public override Task InvokeAsync(Unit args)
	{
		if (!base.Enabled)
		{
			return Task.CompletedTask;
		}
		return _asyncAction?.Invoke() ?? Task.CompletedTask;
	}
}
public class Event<T> where T : struct, Enum
{
	/// <summary>
	/// JIT-time cache: <c>true</c> when <typeparamref name="TArgs" /> implements
	/// <see cref="T:Vortex.ICancellable" />. Evaluated once per closed generic and stored in a
	/// static field, so the hot-path <see cref="M:Vortex.Event`1.Invoke``1(``0)" /> never boxes value-type
	/// args just to check the interface.
	/// </summary>
	private static class CancellableCheck<TArgs>
	{
		public static readonly bool IsCancellable = typeof(ICancellable).IsAssignableFrom(typeof(TArgs));
	}

	private readonly T _eventType;

	private readonly EventManager<T> _eventManager;

	private readonly Dictionary<EventPriority, List<EventDelegateContainer<T>>> _eventDelegates = new Dictionary<EventPriority, List<EventDelegateContainer<T>>>();

	private readonly List<EventPriority> _sortedKeys = new List<EventPriority>();

	private readonly object _lock = new object();

	/// <summary>
	/// Copy-on-write snapshot: a flat, priority-sorted array of all delegates.
	/// Rebuilt only when the subscriber list changes (Add/Remove), never on Invoke.
	/// Used by DEBUG diagnostics to detect type-mismatch handlers.
	/// </summary>
	private EventDelegateContainer<T>[] _cachedSnapshot = Array.Empty<EventDelegateContainer<T>>();

	/// <summary>
	/// Actual valid length of <see cref="F:Vortex.Event`1._cachedSnapshot" /> when rented from ArrayPool.
	/// The rented array may be larger than needed; use this length for iteration.
	/// </summary>
	private int _cachedSnapshotLength = 0;

	/// <summary>
	/// Per-<c>TArgs</c> typed COW snapshots keyed by <see cref="T:System.Type" />.
	/// Each value is a <c>EventDelegateContainer&lt;T, TArgs&gt;[]</c> stored as
	/// <see cref="T:System.Object" />.
	/// <para>
	/// <see cref="M:Vortex.Event`1.Invoke``1(``0)" /> retrieves the matching typed array with a single
	/// dictionary lookup and one array-reference cast - <b>no per-element type check</b>.
	/// </para>
	/// </summary>
	private readonly ConcurrentDictionary<Type, object> _typedSnapshots = new ConcurrentDictionary<Type, object>();

	/// <summary>
	/// Per-<c>TArgs</c> actual lengths of typed snapshots. Since typed arrays
	/// are rented from ArrayPool, they may be larger than needed.
	/// </summary>
	private readonly ConcurrentDictionary<Type, int> _typedSnapshotLengths = new ConcurrentDictionary<Type, int>();

	private volatile bool _enabled = true;

	/// <summary>
	/// When greater than zero, snapshot rebuilds are deferred until the batch
	/// count returns to zero. Incremented by <see cref="M:Vortex.Event`1.BeginBatch" /> and
	/// decremented by <see cref="M:Vortex.Event`1.EndBatch" />. Must only be accessed under <see cref="F:Vortex.Event`1._lock" />.
	/// </summary>
	private int _batchDepth;

	/// <summary>
	/// Tracks whether any Add/Remove occurred while batching was active,
	/// so that <see cref="M:Vortex.Event`1.EndBatch" /> knows whether a rebuild is needed.
	/// </summary>
	private bool _batchDirty;

	/// <summary>
	/// Per-<c>TArgs</c> cached factory delegates that build strongly typed
	/// <c>EventDelegateContainer&lt;T, TArgs&gt;[]</c> from a list of base containers.
	/// Avoids repeated <see cref="M:System.Array.CreateInstance(System.Type,System.Int32)" /> and per-element
	/// <see cref="M:System.Array.SetValue(System.Object,System.Int32)" /> overhead.
	/// </summary>
	private static readonly ConcurrentDictionary<Type, Func<List<EventDelegateContainer<T>>, object>> s_arrayBuilders = new ConcurrentDictionary<Type, Func<List<EventDelegateContainer<T>>, object>>();

	/// <summary>
	/// Per-<c>TArgs</c> cached methods that return rented arrays to the appropriate
	/// <c>ArrayPool&lt;EventDelegateContainer&lt;T, TArgs&gt;&gt;.Shared</c>.
	/// </summary>
	private static readonly ConcurrentDictionary<Type, Action<object>> s_arrayReturnMethods = new ConcurrentDictionary<Type, Action<object>>();

	/// <summary>
	/// Configurable threshold in milliseconds. Handlers exceeding this duration
	/// will be logged as warnings in DEBUG builds. Set to 0 to disable.
	/// </summary>
	public static double SlowHandlerThresholdMs { get; set; } = 200.0;

	public T EventType => _eventType;

	public EventManager<T> EventManager => _eventManager;

	public bool Enabled
	{
		get
		{
			return _enabled;
		}
		set
		{
			_enabled = value;
		}
	}

	public Event(EventManager<T> eventManager, T eventType)
	{
		_eventType = eventType;
		_eventManager = eventManager;
	}

	/// <summary>
	/// Begins a batch operation. While batched, <see cref="M:Vortex.Event`1.Add(Vortex.EventDelegateContainer{`0},System.Boolean)" /> and <see cref="M:Vortex.Event`1.Remove(Vortex.EventDelegateContainer{`0})" />
	/// will not rebuild snapshots. Call <see cref="M:Vortex.Event`1.EndBatch" /> when finished to rebuild once.
	/// Calls may be nested; only the outermost <see cref="M:Vortex.Event`1.EndBatch" /> triggers the rebuild.
	/// </summary>
	public void BeginBatch()
	{
		lock (_lock)
		{
			_batchDepth++;
		}
	}

	/// <summary>
	/// Ends a batch operation. If this is the outermost batch and any mutations
	/// occurred, the COW snapshot is rebuilt exactly once.
	/// </summary>
	public void EndBatch()
	{
		lock (_lock)
		{
			if (_batchDepth > 0)
			{
				_batchDepth--;
			}
			if (_batchDepth == 0 && _batchDirty)
			{
				_batchDirty = false;
				RebuildSnapshot();
			}
		}
	}

	public void Invoke<TArgs>(TArgs args)
	{
		if (!Enabled)
		{
			return;
		}
		EventDelegateContainer<T, TArgs>[] array;
		int num;
		EventDelegateContainer<T>[] cachedSnapshot;
		int cachedSnapshotLength;
		lock (_lock)
		{
			if (_typedSnapshots.TryGetValue(typeof(TArgs), out object value))
			{
				array = (EventDelegateContainer<T, TArgs>[])value;
				num = _typedSnapshotLengths[typeof(TArgs)];
			}
			else
			{
				array = Array.Empty<EventDelegateContainer<T, TArgs>>();
				num = 0;
			}
			cachedSnapshot = _cachedSnapshot;
			cachedSnapshotLength = _cachedSnapshotLength;
		}
		if (cachedSnapshotLength > num)
		{
			for (int i = 0; i < cachedSnapshotLength; i++)
			{
				if (!(cachedSnapshot[i] is EventDelegateContainer<T, TArgs>))
				{
					WarnTypeMismatch<TArgs>(cachedSnapshot[i]);
				}
			}
		}
		double slowHandlerThresholdMs = SlowHandlerThresholdMs;
		Stopwatch stopwatch = ((slowHandlerThresholdMs > 0.0) ? Stopwatch.StartNew() : null);
		Span<EventDelegateContainer<T, TArgs>> span = array.AsSpan(0, num);
		for (int j = 0; j < span.Length; j++)
		{
			stopwatch?.Restart();
			span[j].Invoke(args);
			if (stopwatch != null)
			{
				stopwatch.Stop();
				double totalMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
				if (totalMilliseconds > slowHandlerThresholdMs)
				{
					EventSystemDiagnostics.LogWarning?.Invoke($"[EventSystem] Slow handler on {typeof(T).Name}.{_eventType}: {totalMilliseconds:F2}ms (threshold {slowHandlerThresholdMs:F1}ms). Handler: {array[j].SourceDescription}");
				}
			}
			if (CancellableCheck<TArgs>.IsCancellable && args is ICancellable { Cancelled: not false })
			{
				break;
			}
		}
	}

	/// <summary>
	/// Asynchronously invokes all handlers for the given <typeparamref name="TArgs" />,
	/// awaiting async handlers (<see cref="T:Vortex.IAsyncInvocable`1" />) sequentially while
	/// calling synchronous handlers inline. Priority ordering and cancellation semantics
	/// are identical to <see cref="M:Vortex.Event`1.Invoke``1(``0)" />.
	/// <para>
	/// Intended for editor/tool events where handlers legitimately need to perform I/O.
	/// Not recommended for the game-loop hot path - use <see cref="M:Vortex.Event`1.Invoke``1(``0)" /> instead.
	/// </para>
	/// </summary>
	public async Task InvokeAsync<TArgs>(TArgs args)
	{
		if (!Enabled)
		{
			return;
		}
		EventDelegateContainer<T, TArgs>[] typedSnapshot;
		int typedLength;
		EventDelegateContainer<T>[] fullSnapshot;
		int fullLength;
		lock (_lock)
		{
			if (_typedSnapshots.TryGetValue(typeof(TArgs), out object obj))
			{
				typedSnapshot = (EventDelegateContainer<T, TArgs>[])obj;
				typedLength = _typedSnapshotLengths[typeof(TArgs)];
			}
			else
			{
				typedSnapshot = Array.Empty<EventDelegateContainer<T, TArgs>>();
				typedLength = 0;
			}
			fullSnapshot = _cachedSnapshot;
			fullLength = _cachedSnapshotLength;
		}
		if (fullLength > typedLength)
		{
			for (int j = 0; j < fullLength; j++)
			{
				if (!(fullSnapshot[j] is EventDelegateContainer<T, TArgs>))
				{
					WarnTypeMismatch<TArgs>(fullSnapshot[j]);
				}
			}
		}
		double threshold = SlowHandlerThresholdMs;
		Stopwatch sw = ((threshold > 0.0) ? Stopwatch.StartNew() : null);
		for (int i = 0; i < typedLength; i++)
		{
			sw?.Restart();
			EventDelegateContainer<T, TArgs> eventDelegateContainer = typedSnapshot[i];
			if (eventDelegateContainer is IAsyncInvocable<TArgs> asyncHandler)
			{
				await asyncHandler.InvokeAsync(args).ConfigureAwait(continueOnCapturedContext: false);
			}
			else
			{
				typedSnapshot[i].Invoke(args);
			}
			if (sw != null)
			{
				sw.Stop();
				double elapsed = sw.Elapsed.TotalMilliseconds;
				if (elapsed > threshold)
				{
					EventSystemDiagnostics.LogWarning?.Invoke($"[EventSystem] Slow handler on {typeof(T).Name}.{_eventType}: {elapsed:F2}ms (threshold {threshold:F1}ms). Handler: {typedSnapshot[i].SourceDescription}");
				}
			}
			if (CancellableCheck<TArgs>.IsCancellable && args is ICancellable cancellable && cancellable.Cancelled)
			{
				break;
			}
		}
	}

	/// <summary>
	/// Returns a read-only view of the currently registered handlers for the
	/// given <typeparamref name="TArgs" /> type, sorted by priority.
	/// The span references a COW snapshot - it is safe to read after the lock
	/// is released but may become stale if handlers are added or removed.
	/// </summary>
	public ReadOnlySpan<EventDelegateContainer<T, TArgs>> GetHandlers<TArgs>()
	{
		lock (_lock)
		{
			if (_typedSnapshots.TryGetValue(typeof(TArgs), out object value))
			{
				EventDelegateContainer<T, TArgs>[] array = (EventDelegateContainer<T, TArgs>[])value;
				int length = _typedSnapshotLengths[typeof(TArgs)];
				return array.AsSpan(0, length);
			}
			return ReadOnlySpan<EventDelegateContainer<T, TArgs>>.Empty;
		}
	}

	/// <summary>
	/// Logs a warning when a registered handler is skipped because its TArgs
	/// does not match the invoked type. Only compiled into DEBUG builds.
	/// </summary>
	private void WarnTypeMismatch<TArgs>(EventDelegateContainer<T> container)
	{
		Type type = container.GetType();
		Type type2 = null;
		if (type.IsGenericType && type.GenericTypeArguments.Length == 2)
		{
			type2 = type.GenericTypeArguments[1];
		}
		EventSystemDiagnostics.LogWarning?.Invoke($"[EventSystem] Type mismatch on {typeof(T).Name}.{_eventType}: handler registered for '{type2?.Name ?? "unknown"}' but invoked with '{typeof(TArgs).Name}'. Handler was skipped. (registered at {container.SourceDescription})");
	}

	public bool Add(EventDelegateContainer<T> eventDelegate, bool allowMultiple = false)
	{
		bool flag = false;
		lock (_lock)
		{
			if (!_eventDelegates.TryGetValue(eventDelegate.Priority, out List<EventDelegateContainer<T>> value))
			{
				value = new List<EventDelegateContainer<T>>();
				_eventDelegates[eventDelegate.Priority] = value;
				SortKeys();
			}
			if (allowMultiple || !value.Any((EventDelegateContainer<T> e) => e.Event == eventDelegate.Event))
			{
				value.Add(eventDelegate);
				eventDelegate.Link(this);
				flag = true;
			}
			if (flag)
			{
				if (_batchDepth > 0)
				{
					_batchDirty = true;
				}
				else
				{
					RebuildSnapshot();
				}
			}
			eventDelegate.Added = flag;
			return flag;
		}
	}

	public bool Remove(EventDelegateContainer<T> eventDelegate)
	{
		lock (_lock)
		{
			bool flag = false;
			if (_eventDelegates.ContainsKey(eventDelegate.Priority))
			{
				flag = _eventDelegates[eventDelegate.Priority].Remove(eventDelegate);
			}
			if (flag)
			{
				eventDelegate.Unlink();
				if (_batchDepth > 0)
				{
					_batchDirty = true;
				}
				else
				{
					RebuildSnapshot();
				}
			}
			return flag;
		}
	}

	/// <summary>
	/// Removes the first delegate container whose wrapped handler equals the
	/// specified <paramref name="handler" />. Used by generated <c>-=</c> event accessors.
	/// </summary>
	public bool RemoveByDelegate(Delegate handler)
	{
		lock (_lock)
		{
			for (int i = 0; i < _sortedKeys.Count; i++)
			{
				List<EventDelegateContainer<T>> list = _eventDelegates[_sortedKeys[i]];
				for (int j = 0; j < list.Count; j++)
				{
					if (list[j].MatchesDelegate(handler))
					{
						EventDelegateContainer<T> eventDelegateContainer = list[j];
						list.RemoveAt(j);
						eventDelegateContainer.Unlink();
						if (_batchDepth > 0)
						{
							_batchDirty = true;
						}
						else
						{
							RebuildSnapshot();
						}
						return true;
					}
				}
			}
			return false;
		}
	}

	private void SortKeys()
	{
		_sortedKeys.Clear();
		_sortedKeys.AddRange(_eventDelegates.Keys);
		EventPriority.Sort(_sortedKeys);
	}

	/// <summary>
	/// Rebuilds the flat, priority-sorted snapshot array and per-<c>TArgs</c> typed
	/// snapshot arrays from the current delegate buckets.
	/// Must be called under <see cref="F:Vortex.Event`1._lock" />.
	/// </summary>
	private void RebuildSnapshot()
	{
		int num = 0;
		for (int i = 0; i < _sortedKeys.Count; i++)
		{
			num += _eventDelegates[_sortedKeys[i]].Count;
		}
		if (num == 0)
		{
			if (_cachedSnapshot.Length != 0)
			{
				ArrayPool<EventDelegateContainer<T>>.Shared.Return(_cachedSnapshot, clearArray: true);
			}
			_cachedSnapshot = Array.Empty<EventDelegateContainer<T>>();
			_cachedSnapshotLength = 0;
			_typedSnapshots.Clear();
			_typedSnapshotLengths.Clear();
			return;
		}
		if (_cachedSnapshot.Length != 0)
		{
			ArrayPool<EventDelegateContainer<T>>.Shared.Return(_cachedSnapshot, clearArray: true);
		}
		EventDelegateContainer<T>[] array = ArrayPool<EventDelegateContainer<T>>.Shared.Rent(num);
		int num2 = 0;
		for (int j = 0; j < _sortedKeys.Count; j++)
		{
			List<EventDelegateContainer<T>> list = _eventDelegates[_sortedKeys[j]];
			for (int k = 0; k < list.Count; k++)
			{
				array[num2++] = list[k];
			}
		}
		_cachedSnapshot = array;
		_cachedSnapshotLength = num;
		RebuildTypedSnapshots();
	}

	/// <summary>
	/// Rebuilds per-<c>TArgs</c> typed snapshot arrays from the priority-sorted
	/// delegate buckets.  Each resulting array is a properly typed
	/// <c>EventDelegateContainer&lt;T, TArgs&gt;[]</c>, enabling
	/// <see cref="M:Vortex.Event`1.Invoke``1(``0)" /> to iterate with direct method calls and
	/// zero per-element type checks.
	/// Must be called under <see cref="F:Vortex.Event`1._lock" />.
	/// </summary>
	private void RebuildTypedSnapshots()
	{
		foreach (KeyValuePair<Type, object> typedSnapshot in _typedSnapshots)
		{
			Type key = typedSnapshot.Key;
			if (!s_arrayReturnMethods.TryGetValue(key, out Action<object> value))
			{
				value = CreateArrayReturnMethod(key);
				s_arrayReturnMethods[key] = value;
			}
			value(typedSnapshot.Value);
		}
		_typedSnapshots.Clear();
		_typedSnapshotLengths.Clear();
		Dictionary<Type, List<EventDelegateContainer<T>>> dictionary = null;
		for (int i = 0; i < _sortedKeys.Count; i++)
		{
			List<EventDelegateContainer<T>> list = _eventDelegates[_sortedKeys[i]];
			for (int j = 0; j < list.Count; j++)
			{
				EventDelegateContainer<T> eventDelegateContainer = list[j];
				Type argsType = eventDelegateContainer.ArgsType;
				if (dictionary == null)
				{
					dictionary = new Dictionary<Type, List<EventDelegateContainer<T>>>();
				}
				if (!dictionary.TryGetValue(argsType, out var value2))
				{
					value2 = (dictionary[argsType] = new List<EventDelegateContainer<T>>());
				}
				value2.Add(eventDelegateContainer);
			}
		}
		if (dictionary == null)
		{
			return;
		}
		foreach (KeyValuePair<Type, List<EventDelegateContainer<T>>> item in dictionary)
		{
			if (!s_arrayBuilders.TryGetValue(item.Key, out Func<List<EventDelegateContainer<T>>, object> value3))
			{
				value3 = CreateArrayBuilder(item.Key);
				s_arrayBuilders[item.Key] = value3;
			}
			_typedSnapshots[item.Key] = value3(item.Value);
			_typedSnapshotLengths[item.Key] = item.Value.Count;
		}
	}

	/// <summary>
	/// Generic helper invoked through a cached delegate.  Creates a strongly typed
	/// array and populates it with simple reference casts - no <see cref="M:System.Array.SetValue(System.Object,System.Int32)" />
	/// overhead.
	/// </summary>
	private static object BuildTypedArray<TArgs>(List<EventDelegateContainer<T>> list)
	{
		EventDelegateContainer<T, TArgs>[] array = ArrayPool<EventDelegateContainer<T, TArgs>>.Shared.Rent(list.Count);
		for (int i = 0; i < list.Count; i++)
		{
			array[i] = (EventDelegateContainer<T, TArgs>)list[i];
		}
		return array;
	}

	/// <summary>
	/// Generic helper invoked through a cached delegate. Returns a rented typed array
	/// to its <c>ArrayPool&lt;EventDelegateContainer&lt;T, TArgs&gt;&gt;.Shared</c>.
	/// </summary>
	private static void ReturnTypedArray<TArgs>(object array)
	{
		ArrayPool<EventDelegateContainer<T, TArgs>>.Shared.Return((EventDelegateContainer<T, TArgs>[])array, clearArray: true);
	}

	/// <summary>
	/// Creates and returns a delegate that calls <see cref="M:Vortex.Event`1.BuildTypedArray``1(System.Collections.Generic.List{Vortex.EventDelegateContainer{`0}})" />
	/// closed over the given <paramref name="argsType" />.  The reflection cost is paid
	/// once; subsequent rebuilds reuse the cached delegate.
	/// </summary>
	private static Func<List<EventDelegateContainer<T>>, object> CreateArrayBuilder(Type argsType)
	{
		MethodInfo method = typeof(Event<T>).GetMethod("BuildTypedArray", BindingFlags.Static | BindingFlags.NonPublic);
		MethodInfo method2 = method.MakeGenericMethod(argsType);
		return (Func<List<EventDelegateContainer<T>>, object>)Delegate.CreateDelegate(typeof(Func<List<EventDelegateContainer<T>>, object>), method2);
	}

	/// <summary>
	/// Creates and returns a delegate that calls <see cref="M:Vortex.Event`1.ReturnTypedArray``1(System.Object)" />
	/// closed over the given <paramref name="argsType" />. Used to return rented arrays
	/// to the appropriate ArrayPool instance.
	/// </summary>
	private static Action<object> CreateArrayReturnMethod(Type argsType)
	{
		MethodInfo method = typeof(Event<T>).GetMethod("ReturnTypedArray", BindingFlags.Static | BindingFlags.NonPublic);
		MethodInfo method2 = method.MakeGenericMethod(argsType);
		return (Action<object>)Delegate.CreateDelegate(typeof(Action<object>), method2);
	}
}
/// <summary>
/// Lightweight accessor for a parameterless event slot.
/// Supports <c>+=</c> / <c>-=</c> for subscribe/unsubscribe and
/// <see cref="M:Vortex.EventAccessor`1.Invoke" /> for firing the event.
/// </summary>
/// <remarks>
/// This is a <see langword="readonly struct" /> returned by generated event
/// properties. The no-op setter on the property allows
/// <c>Domain.OnFoo += handler</c> to compile (expands to
/// <c>Domain.OnFoo = Domain.OnFoo + handler</c>).
/// </remarks>
public readonly struct EventAccessor<TEnum>(EventManager<TEnum> manager, TEnum eventType) where TEnum : struct, Enum
{
	private readonly EventManager<TEnum> _manager = manager;

	private readonly TEnum _eventType = eventType;

	/// <summary>Fire the parameterless event.</summary>
	public void Invoke()
	{
		_manager.InvokeEvent(_eventType);
	}

	/// <summary>Asynchronously fire the parameterless event, awaiting async handlers.</summary>
	public Task InvokeAsync()
	{
		return _manager.InvokeEventAsync(_eventType);
	}

	public static EventAccessor<TEnum> operator +(EventAccessor<TEnum> accessor, Action handler)
	{
		accessor._manager.AddNewDelegate(accessor._eventType, handler, default(EventPriority), "C:\\Users\\huggy\\source\\repos\\Vortex\\Vortex\\EventAccessor.cs", 39, "op_Addition");
		return accessor;
	}

	public static EventAccessor<TEnum> operator -(EventAccessor<TEnum> accessor, Action handler)
	{
		accessor._manager.RemoveDelegate(accessor._eventType, handler);
		return accessor;
	}

	public static EventAccessor<TEnum> operator +(EventAccessor<TEnum> accessor, Func<Task> handler)
	{
		accessor._manager.AddNewAsyncDelegate(accessor._eventType, handler, default(EventPriority), "C:\\Users\\huggy\\source\\repos\\Vortex\\Vortex\\EventAccessor.cs", 51, "op_Addition");
		return accessor;
	}

	public static EventAccessor<TEnum> operator -(EventAccessor<TEnum> accessor, Func<Task> handler)
	{
		accessor._manager.RemoveDelegate(accessor._eventType, handler);
		return accessor;
	}
}
/// <summary>
/// Lightweight accessor for a typed event slot.
/// Supports <c>+=</c> / <c>-=</c> for subscribe/unsubscribe and
/// <see cref="M:Vortex.EventAccessor`2.Invoke(`1)" /> for firing the event with arguments.
/// </summary>
public readonly struct EventAccessor<TEnum, TArgs>(EventManager<TEnum> manager, TEnum eventType) where TEnum : struct, Enum
{
	private readonly EventManager<TEnum> _manager = manager;

	private readonly TEnum _eventType = eventType;

	/// <summary>Fire the event with the given arguments.</summary>
	public void Invoke(TArgs args)
	{
		_manager.InvokeEvent(_eventType, args);
	}

	/// <summary>Asynchronously fire the event with the given arguments, awaiting async handlers.</summary>
	public Task InvokeAsync(TArgs args)
	{
		return _manager.InvokeEventAsync(_eventType, args);
	}

	public static EventAccessor<TEnum, TArgs> operator +(EventAccessor<TEnum, TArgs> accessor, Action<TArgs> handler)
	{
		accessor._manager.AddNewDelegate(accessor._eventType, handler, default(EventPriority), "C:\\Users\\huggy\\source\\repos\\Vortex\\Vortex\\EventAccessor.cs", 86, "op_Addition");
		return accessor;
	}

	public static EventAccessor<TEnum, TArgs> operator -(EventAccessor<TEnum, TArgs> accessor, Action<TArgs> handler)
	{
		accessor._manager.RemoveDelegate(accessor._eventType, handler);
		return accessor;
	}

	public static EventAccessor<TEnum, TArgs> operator +(EventAccessor<TEnum, TArgs> accessor, Func<TArgs, Task> handler)
	{
		accessor._manager.AddNewAsyncDelegate(accessor._eventType, handler, default(EventPriority), "C:\\Users\\huggy\\source\\repos\\Vortex\\Vortex\\EventAccessor.cs", 98, "op_Addition");
		return accessor;
	}

	public static EventAccessor<TEnum, TArgs> operator -(EventAccessor<TEnum, TArgs> accessor, Func<TArgs, Task> handler)
	{
		accessor._manager.RemoveDelegate(accessor._eventType, handler);
		return accessor;
	}
}
/// <summary>
/// Declares the canonical <c>TArgs</c> type for an event enum value.
/// When present, <see cref="M:Vortex.EventManager`1.AddNewDelegate``1(`0,System.Action{``0},Vortex.EventPriority,System.String,System.Int32,System.String)" /> and
/// <see cref="M:Vortex.EventManager`1.InvokeEvent``1(`0,``0)" /> will assert that the
/// supplied <c>TArgs</c> matches the declared type.
/// <para>
/// Omitting the attribute on a value means "any TArgs is accepted" (opt-in safety).
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class EventArgsAttribute : Attribute
{
	public Type ArgsType { get; }

	public EventArgsAttribute(Type argsType)
	{
		ArgsType = argsType ?? throw new ArgumentNullException("argsType");
	}
}
/// <summary>
/// Builds and caches a mapping from each <typeparamref name="T" /> enum value
/// to the <see cref="T:System.Type" /> declared by its <see cref="T:Vortex.EventArgsAttribute" />
/// (if any). Evaluated once per closed generic <c>T</c>.
/// </summary>
internal static class EventArgsContract<T> where T : struct, Enum
{
	/// <summary>
	/// Maps each enum value to its declared <c>TArgs</c> type, or <c>null</c>
	/// if the value has no <see cref="T:Vortex.EventArgsAttribute" />.
	/// </summary>
	private static readonly Dictionary<T, Type?> s_declaredArgs = Build();

	private static Dictionary<T, Type?> Build()
	{
		Dictionary<T, Type> dictionary = new Dictionary<T, Type>();
		Type typeFromHandle = typeof(T);
		T[] values = Enum.GetValues<T>();
		foreach (T val in values)
		{
			string name = Enum.GetName(val);
			dictionary[val] = (typeFromHandle.GetField(name)?.GetCustomAttribute<EventArgsAttribute>())?.ArgsType;
		}
		return dictionary;
	}

	/// <summary>
	/// Returns <c>true</c> when <typeparamref name="TArgs" /> is compatible
	/// with the contract declared on <paramref name="eventType" />.
	/// Returns <c>true</c> when no attribute is present (opt-in model).
	/// </summary>
	public static bool IsValid<TArgs>(T eventType)
	{
		if (!s_declaredArgs.TryGetValue(eventType, out Type value) || (object)value == null)
		{
			return true;
		}
		return value == typeof(TArgs);
	}

	/// <summary>
	/// Returns the declared type name for diagnostics, or <c>"(none)"</c>.
	/// </summary>
	public static string GetDeclaredName(T eventType)
	{
		if (s_declaredArgs.TryGetValue(eventType, out Type value) && (object)value != null)
		{
			return value.Name;
		}
		return "(none)";
	}
}
/// <summary>
/// Non-generic base class for delegate containers, enabling heterogeneous storage
/// within a single <see cref="T:Vortex.Event`1" />. Subscribe via the typed
/// <see cref="T:Vortex.EventDelegateContainer`2" /> derived class.
/// Implements <see cref="T:System.IDisposable" /> for self-unsubscription.
/// </summary>
public abstract class EventDelegateContainer<T> : IEventDelegateContainer, IDisposable where T : struct, Enum
{
	private Event<T> _event;

	private bool _added;

	private bool _disposed;

	private readonly T eventType;

	private bool enabled = true;

	private readonly EventPriority priority;

	public EventManager<T>? EventManager => Event?.EventManager;

	public Event<T> Event
	{
		get
		{
			return _event;
		}
		private set
		{
			_event = value;
		}
	}

	public bool Added
	{
		get
		{
			return _added;
		}
		internal set
		{
			_added = value;
		}
	}

	public T EventType => eventType;

	public bool Enabled
	{
		get
		{
			return enabled;
		}
		private set
		{
			enabled = value;
		}
	}

	public EventPriority Priority => priority;

	/// <summary>
	/// The <c>TArgs</c> type this container was registered with.
	/// Used by <see cref="T:Vortex.Event`1" /> to build per-type snapshots without reflection.
	/// </summary>
	public abstract Type ArgsType { get; }

	/// <summary>
	/// Source file where this handler was registered. Captured automatically
	/// via <see cref="T:System.Runtime.CompilerServices.CallerFilePathAttribute" /> in DEBUG builds.
	/// </summary>
	public string? SourceFile { get; private set; }

	/// <summary>
	/// Source line number where this handler was registered.
	/// </summary>
	public int SourceLine { get; private set; }

	/// <summary>
	/// Name of the member that registered this handler.
	/// </summary>
	public string? SourceMember { get; private set; }

	/// <summary>
	/// Returns a compact "File:Line (Member)" string for diagnostics,
	/// or <c>"unknown"</c> when source info was not captured.
	/// </summary>
	public string SourceDescription => (SourceFile != null) ? $"{SourceFile}:{SourceLine} ({SourceMember})" : "unknown";

	/// <summary>
	/// Returns <c>true</c> when this container wraps the specified handler delegate.
	/// Used by the generated <c>-=</c> event accessor path.
	/// </summary>
	public abstract bool MatchesDelegate(Delegate handler);

	public void Link(Event<T> @event)
	{
		Event = @event;
	}

	public void Unlink()
	{
		Event = null;
	}

	protected EventDelegateContainer(T eventType, EventPriority priority)
	{
		this.priority = priority;
		this.eventType = eventType;
	}

	protected EventDelegateContainer(T eventType, EventPriority priority, string? sourceFile, int sourceLine, string? sourceMember)
		: this(eventType, priority)
	{
		SourceFile = ((sourceFile != null) ? Path.GetFileName(sourceFile) : null);
		SourceLine = sourceLine;
		SourceMember = sourceMember;
	}

	public void Enable()
	{
		Enabled = true;
	}

	public void Disable()
	{
		Enabled = false;
	}

	/// <summary>
	/// Removes this delegate from its parent event, enabling <c>using</c> patterns
	/// and preventing leaks.
	/// </summary>
	public void Dispose()
	{
		if (!_disposed)
		{
			_disposed = true;
			Event?.Remove(this);
		}
	}
}
/// <summary>
/// Typed delegate container wrapping an <see cref="T:System.Action`1" />.
/// </summary>
public class EventDelegateContainer<T, TArgs> : EventDelegateContainer<T>, IInvocable<TArgs> where T : struct, Enum
{
	private readonly Action<TArgs>? eventDelegate;

	public override Type ArgsType => typeof(TArgs);

	/// <inheritdoc />
	public override bool MatchesDelegate(Delegate handler)
	{
		return handler?.Equals(eventDelegate) ?? false;
	}

	public EventDelegateContainer(T eventType, Action<TArgs> eventDelegate, EventPriority priority = default(EventPriority))
		: base(eventType, priority)
	{
		this.eventDelegate = eventDelegate;
	}

	public EventDelegateContainer(T eventType, Action<TArgs> eventDelegate, EventPriority priority, string? sourceFile, int sourceLine, string? sourceMember)
		: base(eventType, priority, sourceFile, sourceLine, sourceMember)
	{
		this.eventDelegate = eventDelegate;
	}

	/// <summary>
	/// Protected constructor for subclasses that provide their own invocation
	/// logic and do not use the <see cref="F:Vortex.EventDelegateContainer`2.eventDelegate" /> field.
	/// </summary>
	protected EventDelegateContainer(T eventType, EventPriority priority)
		: base(eventType, priority)
	{
	}

	protected EventDelegateContainer(T eventType, EventPriority priority, string? sourceFile, int sourceLine, string? sourceMember)
		: base(eventType, priority, sourceFile, sourceLine, sourceMember)
	{
	}

	public virtual void Invoke(TArgs args)
	{
		if (base.Enabled)
		{
			eventDelegate?.Invoke(args);
		}
	}
}
/// <summary>
/// Specialized container for parameterless events that stores an <see cref="T:System.Action" />
/// directly, avoiding the closure allocation that wrapping in an
/// <see cref="T:System.Action`1" /> would incur.
/// </summary>
public sealed class ParameterlessEventDelegateContainer<T> : EventDelegateContainer<T, Unit> where T : struct, Enum
{
	private readonly Action _action;

	/// <inheritdoc />
	public override bool MatchesDelegate(Delegate handler)
	{
		return handler?.Equals(_action) ?? false;
	}

	public ParameterlessEventDelegateContainer(T eventType, Action action, EventPriority priority = default(EventPriority))
		: base(eventType, priority)
	{
		_action = action;
	}

	public ParameterlessEventDelegateContainer(T eventType, Action action, EventPriority priority, string? sourceFile, int sourceLine, string? sourceMember)
		: base(eventType, priority, sourceFile, sourceLine, sourceMember)
	{
		_action = action;
	}

	public override void Invoke(Unit args)
	{
		if (base.Enabled)
		{
			_action?.Invoke();
		}
	}
}
/// <summary>
/// Marks a <c>partial class</c> as an event domain.
/// The source generator will produce a backing enum, an <see cref="T:Vortex.EventManager`1" />,
/// and typed <c>Invoke</c> / <c>Subscribe</c> / <c>GlobalInvoke</c> convenience methods
/// for each <see cref="T:Vortex.EventKey" /> field declared in the class.
/// <para>
/// <b>Static domains</b> (the class is <c>static</c>) produce a single shared
/// <see cref="T:Vortex.EventManager`1" /> and static convenience methods - ideal for
/// global engine events.
/// </para>
/// <para>
/// <b>Instance domains</b> (the class is <em>not</em> <c>static</c>) produce a
/// per-instance <see cref="T:Vortex.EventManager`1" /> and instance convenience methods -
/// ideal for component-level or per-object events. Dispose <see cref="T:Vortex.EventManager`1" />
/// via <c>Manager.Dispose()</c> when the owner is no longer needed.
/// <c>GlobalInvoke</c> methods remain static and broadcast across all global managers.
/// </para>
/// <para>
/// <b>Static example:</b>
/// <code>
/// [EventDomain(Global = true)]
/// public static partial class MyEvents
/// {
///     [EventArgs(typeof(MyArgs))]
///     private static readonly EventKey _OnSomething = new();
///
///     public readonly struct MyArgs { public int Value; }
/// }
/// // Usage: MyEvents.InvokeOnSomething(args);
/// </code>
/// </para>
/// <para>
/// <b>Instance example:</b>
/// <code>
/// [EventDomain]
/// public partial class ActorEvents
/// {
///     [EventArgs(typeof(DamageArgs))]
///     private static readonly EventKey _OnDamaged = new();
///
///     public readonly record struct DamageArgs(float Amount);
/// }
/// // Usage: actor.InvokeOnDamaged(new DamageArgs(10));
/// </code>
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class EventDomainAttribute : Attribute
{
	/// <summary>
	/// When <c>true</c>, the generated <see cref="T:Vortex.EventManager`1" /> is created
	/// with <c>global: true</c>, making it participate in
	/// <see cref="M:Vortex.EventManager`1.GlobalInvokeEvent(`0)" /> calls.
	/// </summary>
	public bool Global { get; set; }
}
/// <summary>
/// Marker type for source-generated event domains.
/// Declare <c>private static readonly EventKey _OnXxx</c> fields inside a class marked with
/// <see cref="T:Vortex.EventDomainAttribute" /> to define events. The leading underscore is stripped
/// by the generator to produce the public event name. Optionally decorate each
/// field with <see cref="T:Vortex.EventArgsAttribute" /> to specify the event's argument type
/// (defaults to <see cref="T:Vortex.Unit" /> when omitted).
/// <para>
/// <b>Example:</b>
/// <code>
/// [EventDomain]
/// public static partial class MyEvents
/// {
///     [EventArgs(typeof(MyPayload))]
///     private static readonly EventKey _OnSomething = new();
/// }
/// </code>
/// The generator will emit <c>public static EventTypes OnSomething =&gt; EventTypes.OnSomething;</c>
/// along with convenience methods like <c>InvokeOnSomething</c> and <c>SubscribeOnSomething</c>.
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 1)]
public readonly struct EventKey
{
}
public class EventManager<T> : IDisposable where T : struct, Enum
{
	private static readonly List<EventManager<T>> s_instances = new List<EventManager<T>>();

	private static readonly object s_instancesLock = new object();

	private bool _disposed;

	/// <summary>
	/// Copy-on-write snapshot of the static instances list, rebuilt only on Add/Remove.
	/// </summary>
	private static EventManager<T>[] s_instancesSnapshot = Array.Empty<EventManager<T>>();

	/// <summary>
	/// Copy-on-write snapshot containing only global managers.
	/// Rebuilt when instances or their <see cref="P:Vortex.EventManager`1.Global" /> flag change.
	/// </summary>
	private static EventManager<T>[] s_globalSnapshot = Array.Empty<EventManager<T>>();

	private readonly ConcurrentDictionary<T, Event<T>> _events = new ConcurrentDictionary<T, Event<T>>();

	private bool global = false;

	private bool enabled = true;

	public static EventManager<T> LastGlobalInstance
	{
		get
		{
			EventManager<T>[] array = s_globalSnapshot;
			for (int num = array.Length - 1; num >= 0; num--)
			{
				if (array[num].Enabled)
				{
					return array[num];
				}
			}
			return null;
		}
	}

	public bool Global
	{
		get
		{
			return global;
		}
		set
		{
			if (global == value)
			{
				return;
			}
			global = value;
			lock (s_instancesLock)
			{
				RebuildGlobalSnapshot();
			}
		}
	}

	public bool Enabled
	{
		get
		{
			return enabled;
		}
		set
		{
			enabled = value;
			foreach (Event<T> value2 in _events.Values)
			{
				value2.Enabled = value;
			}
		}
	}

	/// <summary>
	/// Rebuilds the global-only snapshot from the current instances list.
	/// Must be called under <see cref="F:Vortex.EventManager`1.s_instancesLock" />.
	/// </summary>
	private static void RebuildGlobalSnapshot()
	{
		List<EventManager<T>> list = new List<EventManager<T>>();
		for (int i = 0; i < s_instances.Count; i++)
		{
			if (s_instances[i].Global)
			{
				list.Add(s_instances[i]);
			}
		}
		s_globalSnapshot = ((list.Count > 0) ? list.ToArray() : Array.Empty<EventManager<T>>());
	}

	public EventManager(bool global = false)
	{
		Global = global;
		lock (s_instancesLock)
		{
			s_instances.Add(this);
			s_instancesSnapshot = s_instances.ToArray();
			RebuildGlobalSnapshot();
		}
	}

	/// <summary>
	/// Returns the <see cref="T:Vortex.Event`1" /> for the given enum value,
	/// creating it atomically on first access via <see cref="M:System.Collections.Concurrent.ConcurrentDictionary`2.GetOrAdd(`0,System.Func{`0,`1})" />.
	/// </summary>
	private Event<T> GetOrCreateEvent(T eventType)
	{
		return _events.GetOrAdd(eventType, delegate(T key)
		{
			Event<T> obj = new Event<T>(this, key);
			if (!enabled)
			{
				obj.Enabled = false;
			}
			return obj;
		});
	}

	public bool AddDelegate(EventDelegateContainer<T> eventDelegate, bool allowMultiple = false)
	{
		return GetOrCreateEvent(eventDelegate.EventType).Add(eventDelegate, allowMultiple);
	}

	public void RemoveDelegate(EventDelegateContainer<T> eventDelegate)
	{
		if (_events.TryGetValue(eventDelegate.EventType, out Event<T> value))
		{
			value.Remove(eventDelegate);
		}
	}

	/// <summary>
	/// Removes the first delegate container that wraps the given handler delegate.
	/// Used by the generated event <c>-=</c> accessors.
	/// </summary>
	public bool RemoveDelegate(T eventType, Delegate handler)
	{
		if (_events.TryGetValue(eventType, out Event<T> value))
		{
			return value.RemoveByDelegate(handler);
		}
		return false;
	}

	/// <summary>
	/// Begins a batch operation on all existing events. While batched,
	/// <see cref="M:Vortex.Event`1.Add(Vortex.EventDelegateContainer{`0},System.Boolean)" /> and <see cref="M:Vortex.Event`1.Remove(Vortex.EventDelegateContainer{`0})" /> will not
	/// rebuild COW snapshots. Call <see cref="M:Vortex.EventManager`1.EndBatch" /> when finished.
	/// Calls may be nested.
	/// </summary>
	public void BeginBatch()
	{
		foreach (Event<T> value in _events.Values)
		{
			value.BeginBatch();
		}
	}

	/// <summary>
	/// Ends a batch operation. If this is the outermost batch and mutations
	/// occurred, COW snapshots are rebuilt once per event.
	/// </summary>
	public void EndBatch()
	{
		foreach (Event<T> value in _events.Values)
		{
			value.EndBatch();
		}
	}

	public void RemoveEvent(Event<T> xEvent)
	{
		_events.Remove(xEvent.EventType, out var _);
	}

	public void EnableEvent(T eventType)
	{
		GetOrCreateEvent(eventType).Enabled = true;
	}

	public void DisableEvent(T eventType)
	{
		GetOrCreateEvent(eventType).Enabled = false;
	}

	/// <summary>
	/// Invoke an event with typed arguments. Only delegates registered
	/// with a matching <typeparamref name="TArgs" /> will be called.
	/// </summary>
	public void InvokeEvent<TArgs>(T eventType, TArgs args)
	{
		if (!_disposed && Enabled)
		{
			Event<T> value;
			if (!EventArgsContract<T>.IsValid<TArgs>(eventType))
			{
				EventSystemDiagnostics.LogError?.Invoke($"[EventSystem] Type mismatch on {typeof(T).Name}.{eventType}: invoked with '{typeof(TArgs).Name}' but the event declares '{EventArgsContract<T>.GetDeclaredName(eventType)}' via [EventArgs].");
			}
			else if (_events.TryGetValue(eventType, out value))
			{
				value.Invoke(args);
			}
		}
	}

	/// <summary>
	/// Returns the <see cref="T:Vortex.Event`1" /> for the given enum value if it
	/// has been created, or <c>null</c> if no subscribers have been registered.
	/// </summary>
	public Event<T>? GetEvent(T eventType)
	{
		_events.TryGetValue(eventType, out Event<T> value);
		return value;
	}

	/// <summary>
	/// Invoke a parameterless event.
	/// </summary>
	public void InvokeEvent(T eventType)
	{
		InvokeEvent(eventType, default(Unit));
	}

	/// <summary>
	/// Asynchronously invoke an event with typed arguments. Async handlers are
	/// awaited sequentially; synchronous handlers are called inline.
	/// Intended for editor/tool events that perform I/O.
	/// </summary>
	public async Task InvokeEventAsync<TArgs>(T eventType, TArgs args)
	{
		if (!_disposed && Enabled)
		{
			Event<T> evt;
			if (!EventArgsContract<T>.IsValid<TArgs>(eventType))
			{
				EventSystemDiagnostics.LogError?.Invoke($"[EventSystem] Type mismatch on {typeof(T).Name}.{eventType}: invoked with '{typeof(TArgs).Name}' but the event declares '{EventArgsContract<T>.GetDeclaredName(eventType)}' via [EventArgs].");
			}
			else if (_events.TryGetValue(eventType, out evt))
			{
				await evt.InvokeAsync(args).ConfigureAwait(continueOnCapturedContext: false);
			}
		}
	}

	/// <summary>
	/// Asynchronously invoke a parameterless event.
	/// </summary>
	public Task InvokeEventAsync(T eventType)
	{
		return InvokeEventAsync(eventType, default(Unit));
	}

	/// <summary>
	/// Register a typed delegate for an event.
	/// </summary>
	public EventDelegateContainer<T, TArgs> AddNewDelegate<TArgs>(T eventType, Action<TArgs> eventDelegate, EventPriority priority = default(EventPriority), [CallerFilePath] string? sourceFile = null, [CallerLineNumber] int sourceLine = 0, [CallerMemberName] string? sourceMember = null)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (!EventArgsContract<T>.IsValid<TArgs>(eventType))
		{
			throw new InvalidOperationException($"[EventSystem] Type mismatch on {typeof(T).Name}.{eventType}: handler registered with '{typeof(TArgs).Name}' but the event declares '{EventArgsContract<T>.GetDeclaredName(eventType)}' via [EventArgs]. Fix the subscriber's type parameter.");
		}
		EventDelegateContainer<T, TArgs> eventDelegateContainer = new EventDelegateContainer<T, TArgs>(eventType, eventDelegate, priority, sourceFile, sourceLine, sourceMember);
		GetOrCreateEvent(eventType).Add(eventDelegateContainer);
		return eventDelegateContainer;
	}

	/// <summary>
	/// Register a parameterless delegate for an event.
	/// </summary>
	public EventDelegateContainer<T, Unit> AddNewDelegate(T eventType, Action eventDelegate, EventPriority priority = default(EventPriority), [CallerFilePath] string? sourceFile = null, [CallerLineNumber] int sourceLine = 0, [CallerMemberName] string? sourceMember = null)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (!EventArgsContract<T>.IsValid<Unit>(eventType))
		{
			throw new InvalidOperationException($"[EventSystem] Type mismatch on {typeof(T).Name}.{eventType}: handler registered with 'Unit' (parameterless) but the event declares '{EventArgsContract<T>.GetDeclaredName(eventType)}' via [EventArgs]. Fix the subscriber's type parameter.");
		}
		ParameterlessEventDelegateContainer<T> parameterlessEventDelegateContainer = new ParameterlessEventDelegateContainer<T>(eventType, eventDelegate, priority, sourceFile, sourceLine, sourceMember);
		GetOrCreateEvent(eventType).Add(parameterlessEventDelegateContainer);
		return parameterlessEventDelegateContainer;
	}

	/// <summary>
	/// Register a typed async delegate for an event.
	/// </summary>
	public AsyncEventDelegateContainer<T, TArgs> AddNewAsyncDelegate<TArgs>(T eventType, Func<TArgs, Task> eventDelegate, EventPriority priority = default(EventPriority), [CallerFilePath] string? sourceFile = null, [CallerLineNumber] int sourceLine = 0, [CallerMemberName] string? sourceMember = null, bool allowMultiple = false)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (!EventArgsContract<T>.IsValid<TArgs>(eventType))
		{
			throw new InvalidOperationException($"[EventSystem] Type mismatch on {typeof(T).Name}.{eventType}: async handler registered with '{typeof(TArgs).Name}' but the event declares '{EventArgsContract<T>.GetDeclaredName(eventType)}' via [EventArgs]. Fix the subscriber's type parameter.");
		}
		AsyncEventDelegateContainer<T, TArgs> asyncEventDelegateContainer = new AsyncEventDelegateContainer<T, TArgs>(eventType, eventDelegate, priority, sourceFile, sourceLine, sourceMember);
		GetOrCreateEvent(eventType).Add(asyncEventDelegateContainer, allowMultiple);
		return asyncEventDelegateContainer;
	}

	/// <summary>
	/// Register a parameterless async delegate for an event.
	/// </summary>
	public AsyncEventDelegateContainer<T, Unit> AddNewAsyncDelegate(T eventType, Func<Task> eventDelegate, EventPriority priority = default(EventPriority), [CallerFilePath] string? sourceFile = null, [CallerLineNumber] int sourceLine = 0, [CallerMemberName] string? sourceMember = null, bool allowMultiple = false)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (!EventArgsContract<T>.IsValid<Unit>(eventType))
		{
			throw new InvalidOperationException($"[EventSystem] Type mismatch on {typeof(T).Name}.{eventType}: async handler registered with 'Unit' (parameterless) but the event declares '{EventArgsContract<T>.GetDeclaredName(eventType)}' via [EventArgs]. Fix the subscriber's type parameter.");
		}
		ParameterlessAsyncEventDelegateContainer<T> parameterlessAsyncEventDelegateContainer = new ParameterlessAsyncEventDelegateContainer<T>(eventType, eventDelegate, priority, sourceFile, sourceLine, sourceMember);
		GetOrCreateEvent(eventType).Add(parameterlessAsyncEventDelegateContainer, allowMultiple);
		return parameterlessAsyncEventDelegateContainer;
	}

	/// <summary>
	/// Invoke an event with typed arguments across all global managers.
	/// </summary>
	public static void GlobalInvokeEvent<TArgs>(T eventType, TArgs args)
	{
		EventManager<T>[] array = s_globalSnapshot;
		foreach (EventManager<T> eventManager in array)
		{
			if (eventManager.Enabled)
			{
				eventManager.InvokeEvent(eventType, args);
			}
		}
	}

	/// <summary>
	/// Invoke a parameterless event across all global managers.
	/// </summary>
	public static void GlobalInvokeEvent(T eventType)
	{
		GlobalInvokeEvent(eventType, default(Unit));
	}

	/// <summary>
	/// Asynchronously invoke an event with typed arguments across all global managers.
	/// </summary>
	public static async Task GlobalInvokeEventAsync<TArgs>(T eventType, TArgs args)
	{
		EventManager<T>[] snapshot = s_globalSnapshot;
		foreach (EventManager<T> instance in snapshot)
		{
			if (instance.Enabled)
			{
				await instance.InvokeEventAsync(eventType, args).ConfigureAwait(continueOnCapturedContext: false);
			}
		}
	}

	/// <summary>
	/// Asynchronously invoke a parameterless event across all global managers.
	/// </summary>
	public static Task GlobalInvokeEventAsync(T eventType)
	{
		return GlobalInvokeEventAsync(eventType, default(Unit));
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		enabled = false;
		foreach (Event<T> value in _events.Values)
		{
			value.Enabled = false;
		}
		_events.Clear();
		lock (s_instancesLock)
		{
			s_instances.Remove(this);
			s_instancesSnapshot = s_instances.ToArray();
			RebuildGlobalSnapshot();
		}
		GC.SuppressFinalize(this);
	}

	~EventManager()
	{
		if (!_disposed)
		{
			EventSystemDiagnostics.LogWarning?.Invoke("EventManager<" + typeof(T).Name + "> was not disposed before finalization.");
		}
	}
}
/// <summary>
/// A zero-size struct used as <c>TArgs</c> for parameterless events.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 1)]
public readonly struct Unit
{
}
/// <summary>
/// Represents a hierarchical priority as an ordered array of integers.
/// Comparison is lexicographic: <c>[0,34,5]</c> &lt; <c>[1,0,2]</c> &lt; <c>[1,0,4]</c>.
/// A <c>default</c> instance is equivalent to <c>[0]</c>.
/// </summary>
public readonly struct EventPriority : IComparable<EventPriority>, IEquatable<EventPriority>
{
	private static readonly int[] s_default = new int[1];

	private readonly int[]? _levels;

	/// <summary>The priority levels. Never null; defaults to a single-element <c>[0]</c>.</summary>
	public ReadOnlySpan<int> Levels => _levels ?? s_default;

	public EventPriority(params int[] levels)
	{
		_levels = ((levels != null && levels.Length > 0) ? levels : null);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public int CompareTo(EventPriority other)
	{
		int[] array = _levels ?? s_default;
		int[] array2 = other._levels ?? s_default;
		if (array == array2)
		{
			return 0;
		}
		return CompareLevels(array, array2);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static int CompareLevels(int[] a, int[] b)
	{
		int num = Math.Min(a.Length, b.Length);
		for (int i = 0; i < num; i++)
		{
			int num2 = a[i] - b[i];
			if (num2 != 0)
			{
				return num2;
			}
		}
		return a.Length - b.Length;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool Equals(EventPriority other)
	{
		int[] array = _levels ?? s_default;
		int[] array2 = other._levels ?? s_default;
		if (array == array2)
		{
			return true;
		}
		return ((ReadOnlySpan<int>)array.AsSpan()).SequenceEqual((ReadOnlySpan<int>)array2);
	}

	public override bool Equals(object? obj)
	{
		return obj is EventPriority other && Equals(other);
	}

	public override int GetHashCode()
	{
		int[] array = _levels ?? s_default;
		HashCode hashCode = default(HashCode);
		for (int i = 0; i < array.Length; i++)
		{
			hashCode.Add(array[i]);
		}
		return hashCode.ToHashCode();
	}

	public static bool operator ==(EventPriority left, EventPriority right)
	{
		return left.Equals(right);
	}

	public static bool operator !=(EventPriority left, EventPriority right)
	{
		return !left.Equals(right);
	}

	public static bool operator <(EventPriority left, EventPriority right)
	{
		return left.CompareTo(right) < 0;
	}

	public static bool operator >(EventPriority left, EventPriority right)
	{
		return left.CompareTo(right) > 0;
	}

	public static bool operator <=(EventPriority left, EventPriority right)
	{
		return left.CompareTo(right) <= 0;
	}

	public static bool operator >=(EventPriority left, EventPriority right)
	{
		return left.CompareTo(right) >= 0;
	}

	/// <summary>Allows <c>int</c> to be used where <see cref="T:Vortex.EventPriority" /> is expected.</summary>
	public static implicit operator EventPriority(int single)
	{
		return new EventPriority(single);
	}

	/// <summary>Allows <c>int[]</c> to be used where <see cref="T:Vortex.EventPriority" /> is expected.</summary>
	public static implicit operator EventPriority(int[] levels)
	{
		return new EventPriority(levels);
	}

	public override string ToString()
	{
		int[] array = _levels ?? s_default;
		if (array.Length == 1)
		{
			return array[0].ToString();
		}
		return "[" + string.Join(",", array) + "]";
	}

	/// <summary>
	/// Sorts a <see cref="T:System.Collections.Generic.List`1" /> in-place using an insertion sort
	/// optimized for the small lists typical in event systems. Avoids repeated
	/// <see cref="T:System.ReadOnlySpan`1" /> construction by working directly with the
	/// underlying <c>int[]</c> arrays and uses binary search on the already-sorted
	/// prefix to find the insertion point, reducing the number of comparisons from
	/// O(n²) to O(n log n) while keeping O(n²) moves (which dominate only at
	/// larger sizes where the list would be unusual for an event system).
	/// </summary>
	public static void Sort(List<EventPriority> list)
	{
		Span<EventPriority> span = CollectionsMarshal.AsSpan(list);
		int length = span.Length;
		if (length <= 1)
		{
			return;
		}
		for (int i = 1; i < length; i++)
		{
			EventPriority eventPriority = span[i];
			int[] b = eventPriority._levels ?? s_default;
			int num = 0;
			int num2 = i;
			while (num < num2)
			{
				int num3 = num + num2 >>> 1;
				if (CompareLevels(span[num3]._levels ?? s_default, b) <= 0)
				{
					num = num3 + 1;
				}
				else
				{
					num2 = num3;
				}
			}
			if (num < i)
			{
				span.Slice(num, i - num).CopyTo(span.Slice(num + 1));
				span[num] = eventPriority;
			}
		}
	}
}
/// <summary>
/// Configurable logging hooks for the event system.
/// The hosting application should assign
/// <see cref="P:Vortex.EventSystemDiagnostics.LogWarning" /> and <see cref="P:Vortex.EventSystemDiagnostics.LogError" /> during startup
/// so that diagnostic messages are routed through the engine's logging
/// infrastructure.
/// </summary>
public static class EventSystemDiagnostics
{
	/// <summary>
	/// Called for non-critical diagnostic messages (e.g. slow handlers, sync-over-async).
	/// </summary>
	public static Action<string>? LogWarning { get; set; }

	/// <summary>
	/// Called for error-level diagnostic messages (e.g. type-mismatch on invoke).
	/// </summary>
	public static Action<string>? LogError { get; set; }
}
/// <summary>
/// Interface for typed, async invocation of an event handler.
/// Implemented by <see cref="T:Vortex.AsyncEventDelegateContainer`2" /> so that
/// <see cref="M:Vortex.Event`1.InvokeAsync``1(``0)" /> can await async handlers while still
/// calling synchronous handlers inline.
/// </summary>
public interface IAsyncInvocable<in TArgs>
{
	Task InvokeAsync(TArgs args);
}
/// <summary>
/// Implement on event argument types to allow handlers to stop further propagation.
/// When a handler sets <see cref="P:Vortex.ICancellable.Cancelled" /> to <c>true</c>, subsequent handlers
/// in the priority chain are skipped.
/// </summary>
public interface ICancellable
{
	bool Cancelled { get; set; }
}
public interface IEventDelegateContainer : IDisposable
{
	bool Enabled { get; }

	bool Added { get; }

	void Enable();

	void Disable();
}
public interface IEventManagerHolder<T> where T : struct, Enum
{
	EventManager<T> EventManager { get; }
}
public static class EventManagerExtensions
{
	public static void InvokeEvents<T, TArgs>(this IEventManagerHolder<T>[] holders, T eventType, TArgs args) where T : struct, Enum
	{
		for (int i = 0; i < holders.Length; i++)
		{
			holders[i]?.EventManager.InvokeEvent(eventType, args);
		}
	}

	public static void InvokeEvents<T, TArgs>(this List<IEventManagerHolder<T>> holders, T eventType, TArgs args) where T : struct, Enum
	{
		for (int i = 0; i < holders.Count; i++)
		{
			holders[i]?.EventManager.InvokeEvent(eventType, args);
		}
	}

	public static void InvokeEvents<T>(this IEventManagerHolder<T>[] holders, T eventType) where T : struct, Enum
	{
		for (int i = 0; i < holders.Length; i++)
		{
			holders[i]?.EventManager.InvokeEvent(eventType);
		}
	}

	public static void InvokeEvents<T>(this List<IEventManagerHolder<T>> holders, T eventType) where T : struct, Enum
	{
		for (int i = 0; i < holders.Count; i++)
		{
			holders[i]?.EventManager.InvokeEvent(eventType);
		}
	}
}
/// <summary>
/// Interface for typed, zero-cast invocation of an event handler.
/// Implemented by <see cref="T:Vortex.EventDelegateContainer`2" /> so that
/// callers holding a typed reference can invoke without a runtime type check.
/// </summary>
public interface IInvocable<in TArgs>
{
	void Invoke(TArgs args);
}
