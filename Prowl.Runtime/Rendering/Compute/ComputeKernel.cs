// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using Prowl.Runtime.Graphite;

namespace Prowl.Runtime.Rendering.Compute;

/// <summary>
/// A compiled compute shader kernel ready for dispatch.
/// Manages the compute pipeline state and bind group layout for both
/// OpenGL and Vulkan backends.
/// </summary>
public sealed class ComputeKernel : IDisposable
{
    private ComputePipelineState? _pipeline;
    private ShaderModule? _module;
    private BindGroupLayout? _bindGroupLayout;
    private BindGroupLayoutEntry[]? _layoutEntries;
    private SpirvReflection.ReflectionResult? _reflection;
    private bool _disposed;

    /// <summary>
    /// Whether this kernel was successfully compiled and is ready for dispatch.
    /// </summary>
    public bool IsValid => _pipeline != null && !_disposed;

    /// <summary>
    /// The bind group layout derived from shader reflection (Vulkan) or
    /// from the explicit layout entries (OpenGL).
    /// </summary>
    public BindGroupLayout? BindGroupLayout => _bindGroupLayout;

    /// <summary>
    /// The underlying compute pipeline state.
    /// </summary>
    public ComputePipelineState? Pipeline => _pipeline;

    /// <summary>
    /// The bind group layout entries describing each binding slot.
    /// Used by <see cref="ComputeDispatcher"/> to resolve binding indices.
    /// </summary>
    public BindGroupLayoutEntry[]? LayoutEntries => _layoutEntries;

    /// <summary>
    /// SPIR-V reflection data (Vulkan only). Null on OpenGL.
    /// </summary>
    internal SpirvReflection.ReflectionResult? Reflection => _reflection;

    /// <summary>
    /// Compiles a compute shader from GLSL source.
    /// On OpenGL, the source is compiled directly. On Vulkan, it is
    /// cross-compiled to SPIR-V via shaderc.
    /// </summary>
    /// <param name="glslSource">Complete GLSL compute shader source (without #version).
    /// The appropriate version directive is prepended automatically.</param>
    /// <param name="layoutEntries">Explicit bind group layout entries. If null on Vulkan,
    /// the layout is derived from SPIR-V reflection.</param>
    /// <param name="debugName">Optional name for error messages and GPU debuggers.</param>
    public ComputeKernel(string glslSource, BindGroupLayoutEntry[]? layoutEntries = null, string? debugName = null)
    {
        if (!Graphics.IsGraphiteReady)
        {
            Debug.LogWarning($"ComputeKernel '{debugName}': Graphite device not ready.");
            return;
        }

        GraphiteDevice device = Graphics.Graphite;

        if (!device.Capabilities.SupportsCompute)
        {
            Debug.LogWarning($"ComputeKernel '{debugName}': Device does not support compute shaders.");
            return;
        }

        try
        {
            bool isVulkan = device.BackendType == GraphicsBackendType.Vulkan;

            // Prepend version directive
            string versionDirective = isVulkan ? "#version 450\n" : "#version 430\n";
            string fullSource = versionDirective + glslSource;

            if (isVulkan)
            {
                fullSource = fullSource.Insert(versionDirective.Length, "#define PROWL_VULKAN 1\n");
            }

            if (isVulkan)
            {
                byte[] spirv = ShaderCrossCompiler.CompileGLSLToSPIRV(fullSource, ShaderStage.Compute, debugName);
                _module = device.CreateShaderModule(ShaderModuleDescriptor.ComputeSPIRV(spirv));
                _reflection = SpirvReflection.Reflect(spirv);

                // Create bind group layout from reflection or explicit entries
                if (layoutEntries != null)
                {
                    _layoutEntries = layoutEntries;
                    _bindGroupLayout = device.CreateBindGroupLayout(new BindGroupLayoutDescriptor(layoutEntries)
                    {
                        DebugName = debugName != null ? $"{debugName}_layout" : null,
                    });
                }
                else if (_reflection != null)
                {
                    // Build layout entries from reflection so LayoutEntries is always available
                    List<BindGroupLayoutEntry> reflectedEntries = new();
                    foreach (SpirvReflection.ResourceBinding binding in _reflection.Bindings)
                    {
                        switch (binding.Type)
                        {
                            case SpirvReflection.ResourceType.UniformBuffer:
                                reflectedEntries.Add(BindGroupLayoutEntry.UniformBuffer(
                                    binding.Binding, ShaderStage.Compute, false, binding.Name));
                                break;
                            case SpirvReflection.ResourceType.CombinedImageSampler:
                                reflectedEntries.Add(BindGroupLayoutEntry.CombinedTextureSampler(
                                    binding.Binding, ShaderStage.Compute, binding.Name));
                                break;
                            case SpirvReflection.ResourceType.StorageBuffer:
                                reflectedEntries.Add(BindGroupLayoutEntry.StorageBuffer(
                                    binding.Binding, ShaderStage.Compute, false, false, binding.Name));
                                break;
                        }
                    }
                    _layoutEntries = reflectedEntries.ToArray();
                    _bindGroupLayout = device.CreateBindGroupLayout(
                        new BindGroupLayoutDescriptor(_layoutEntries)
                        {
                            DebugName = debugName != null ? $"{debugName}_layout" : null,
                        });
                }
            }
            else
            {
                _module = device.CreateShaderModule(ShaderModuleDescriptor.ComputeGLSL(fullSource));

                // On OpenGL, create layout from explicit entries if provided
                if (layoutEntries != null)
                {
                    _layoutEntries = layoutEntries;
                    _bindGroupLayout = device.CreateBindGroupLayout(new BindGroupLayoutDescriptor(layoutEntries)
                    {
                        DebugName = debugName != null ? $"{debugName}_layout" : null,
                    });
                }
            }

            // Create compute pipeline state
            ComputePipelineStateDescriptor psoDesc = new()
            {
                ComputeShader = _module,
                BindGroupLayouts = _bindGroupLayout != null ? [_bindGroupLayout] : null,
                DebugName = debugName,
            };

            _pipeline = device.CreateComputePipelineState(in psoDesc);
        }
        catch (Exception ex)
        {
            Debug.LogError($"ComputeKernel '{debugName}' compilation failed: {ex.Message}");
            Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _pipeline?.Dispose();
        _pipeline = null;
        _module?.Dispose();
        _module = null;
        _bindGroupLayout?.Dispose();
        _bindGroupLayout = null;
        _reflection = null;
    }
}
