// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

using Prowl.Runtime.Resources;

using Silk.NET.Maths;
using Silk.NET.Windowing;

using Vortex;

namespace Prowl.Runtime.Events;

[EventDomain]
public partial class InputEvents
{
    [EventArgs(typeof(OnKeyArgs))]
    private static readonly EventKey _OnKeyEvent = new();
    [EventArgs(typeof(OnMouseArgs))]
    private static readonly EventKey _OnMouseEvent = new();

    public readonly record struct OnKeyArgs(KeyCode KeyCode, bool IsKeyPressed);
    public readonly record struct OnMouseArgs(MouseButton Button, float X, float Y, bool bool1, bool bool2);
}
//      OnMouseEvent?.Invoke(MouseButton.Unknown, MousePosition.X, MousePosition.Y, false, true);
