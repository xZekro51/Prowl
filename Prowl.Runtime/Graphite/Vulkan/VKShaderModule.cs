// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Silk.NET.Vulkan;

using VkShaderModule = Silk.NET.Vulkan.ShaderModule;

namespace Prowl.Runtime.Graphite.Vulkan;

/// <summary>
/// Vulkan implementation of a shader module.
/// Requires SPIR-V binary source.
/// </summary>
internal unsafe class VKShaderModule : Graphite.ShaderModule
{
    private readonly VKGraphiteDevice _device;
    internal VkShaderModule Handle { get; }

    internal VKShaderModule(VKGraphiteDevice device, in ShaderModuleDescriptor descriptor)
    {
        _device = device;
        Stage = descriptor.Stage;
        EntryPoint = descriptor.EntryPoint;
        DebugName = descriptor.DebugName;

        if (descriptor.Source.Type != ShaderSourceType.SPIRV)
            throw new InvalidOperationException("Vulkan backend requires SPIR-V shader source. GLSL is not supported; pre-compile to SPIR-V.");

        var code = descriptor.Source.Code;
        fixed (byte* pCode = code)
        {
            var createInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)code.Length,
                PCode = (uint*)pCode,
            };

            VKGraphiteDevice.Check(device.Vk.CreateShaderModule(device.Device, &createInfo, null, out var module));
            Handle = module;
        }
    }

    protected override void DisposeResources()
    {
        _device.Vk.DestroyShaderModule(_device.Device, Handle, null);
    }
}
