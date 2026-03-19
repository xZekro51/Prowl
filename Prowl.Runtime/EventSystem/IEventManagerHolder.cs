using System;
using System.Collections.Generic;


namespace Prowl.Runtime.EventSystem;

public interface IEventManagerHolder<T> where T : struct, Enum
{
    EventManager<T> EventManager { get; }
}

public static class IEventManagerHolderExtensions
{
    public static void InvokeEvents<T>(this IEventManagerHolder<T>[] holders, T eventType, params EventParam[] parameters) where T : struct, Enum
    {
        for (int i = 0; i < holders.Length; i++)
        {
            var holder = holders[i];
            holder?.EventManager.InvokeEvent(eventType, parameters);
        }
    }
    public static void InvokeEvents<T>(this List<IEventManagerHolder<T>> holders, T eventType, params EventParam[] parameters) where T : struct, Enum
    {
        for (int i = 0; i < holders.Count; i++)
        {
            IEventManagerHolder<T> holder = holders[i];
            holder?.EventManager.InvokeEvent(eventType, parameters);
        }
    }

    public static bool TryGetParam<T>(this EventParam[] parameters, out T param)
    {
        param = default;

        for (int i = 0; i < parameters.Length; i++)
        {
            if (parameters[i] is T t)
            {
                param = t;
                return true;
            }
        }

        return false;
    }

    public static T[] GetParams<T>(this EventParam[] parameters)
    {
        int count = 0;
        for (int i = 0; i < parameters.Length; i++)
        {
            if (parameters[i] is T)
                count++;
        }

        if (count == 0)
            return System.Array.Empty<T>();

        T[] result = new T[count];
        int index = 0;
        for (int i = 0; i < parameters.Length; i++)
        {
            if (parameters[i] is T t)
                result[index++] = t;
        }

        return result;
    }

    public static T ToEnum<T>(this int value) where T : struct, Enum
        => (T)Enum.ToObject(typeof(T), value);
}
