// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime;

namespace Prowl.Launcher;

internal class Program
{
    static void Main(string[] args)
    {
        DpiManager.EnsureProcessDpiAware();

        var app = new LauncherApplication();
        app.Run("Prowl Launcher", 960, 640);
    }
}
