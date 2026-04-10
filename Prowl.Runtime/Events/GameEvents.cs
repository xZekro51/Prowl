// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Vortex;

namespace Prowl.Runtime.Events;

[EventDomain]
public partial class GameEvents
{
    private static readonly EventKey _OnBeforeBeginUpdate = new();
    private static readonly EventKey _OnAfterBeginUpdate = new();
}
