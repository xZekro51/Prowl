// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Prowl.Runtime.Utils;

public struct EnumArray<TEnum, TValue> where TEnum : struct, Enum
{
    public readonly TValue[] Values;
    private readonly int MinValue;
    private readonly int MaxValue;
    public readonly int Length => Values.Length;

    public readonly bool Any(Func<TValue, bool> predicate)
    {
        foreach (var value in Values)
        {
            if (predicate(value)) return true;
        }
        return false;
    }

    public EnumArray()
    {
        var enumValues = Enum.GetValues<TEnum>();
        for (int i = 0; i < enumValues.Length; i++)
        {
            int intValue = Convert.ToInt32(enumValues[i]);
            if (i == 0)
            {
                MinValue = intValue;
                MaxValue = intValue;
            }
            else
            {
                if (intValue < MinValue) MinValue = intValue;
                if (intValue > MaxValue) MaxValue = intValue;
            }
        }
        Values = new TValue[MaxValue - MinValue + 1];
    }

    public readonly void Clear()
    {
        Array.Clear(Values, 0, Values.Length);
    }

    public static void Swap(ref EnumArray<TEnum, TValue> a, ref EnumArray<TEnum, TValue> b)
    {
        var temp = a; a = b; b = temp;
    }

    public void CopyTo(EnumArray<TEnum, TValue> recipient)
    {
        Array.Copy(Values, recipient.Values, Values.Length);
    }

    public TValue this[TEnum key]
    {
        get
        {
            return this[Convert.ToInt32(key)];
        }
        set
        {
            Values[Convert.ToInt32(key)] = value;
        }
    }

    public TValue this[int key]
    {
        get
        {
            int index = key - MinValue;
            if (index < 0 || index >= Values.Length)
                throw new IndexOutOfRangeException($"Key {key} is out of range for EnumArray.");
            return Values[index];
        }
        set
        {
            int index = key - MinValue;
            if (index < 0 || index >= Values.Length)
                throw new IndexOutOfRangeException($"Key {key} is out of range for EnumArray.");
            Values[index] = value;
        }
    }
}
