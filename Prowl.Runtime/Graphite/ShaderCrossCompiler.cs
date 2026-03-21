// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.InteropServices;
using System.Text;

using Silk.NET.Shaderc;

using ShadercApi = Silk.NET.Shaderc.Shaderc;

namespace Prowl.Runtime.Graphite;

/// <summary>
/// Provides GLSL → SPIR-V cross-compilation using the shaderc library (via Silk.NET.Shaderc).
/// Thread-safe: a single compiler instance is shared and guarded by a lock.
/// </summary>
internal static unsafe class ShaderCrossCompiler
{
    private static readonly object s_lock = new();
    private static ShadercApi? s_api;
    private static Compiler* s_compiler;

    /// <summary>
    /// Compiles GLSL source code to SPIR-V binary.
    /// </summary>
    /// <param name="glslSource">The complete GLSL source (including #version directive).</param>
    /// <param name="stage">The shader stage.</param>
    /// <param name="debugName">Optional file name for error messages.</param>
    /// <returns>SPIR-V bytecode as a byte array.</returns>
    /// <exception cref="InvalidOperationException">Thrown when compilation fails.</exception>
    public static byte[] CompileGLSLToSPIRV(string glslSource, ShaderStage stage, string? debugName = null)
    {
        lock (s_lock)
        {
            EnsureInitialized();

            var options = s_api!.CompileOptionsInitialize();
            try
            {
                // Target Vulkan 1.0 with SPIR-V 1.0
                s_api.CompileOptionsSetTargetEnv(options, TargetEnv.Vulkan, 0);
                s_api.CompileOptionsSetTargetSpirv(options, SpirvVersion.Shaderc10);
                s_api.CompileOptionsSetSourceLanguage(options, SourceLanguage.Glsl);
                s_api.CompileOptionsSetOptimizationLevel(options, OptimizationLevel.Zero);

                // Relax Vulkan rules to accept OpenGL-style GLSL (bare non-opaque
                // uniforms outside blocks, etc.) and let shaderc auto-assign any
                // missing binding/location qualifiers.
                s_api.CompileOptionsSetVulkanRulesRelaxed(options, true);
                s_api.CompileOptionsSetAutoBindUniforms(options, true);
                s_api.CompileOptionsSetAutoMapLocations(options, true);

                var shaderKind = MapShaderKind(stage);
                var fileName = debugName ?? "shader";

                var result = s_api.CompileIntoSpv(
                    s_compiler,
                    glslSource,
                    (nuint)Encoding.UTF8.GetByteCount(glslSource),
                    shaderKind,
                    fileName,
                    "main",
                    options);

                try
                {
                    var status = s_api.ResultGetCompilationStatus(result);
                    if (status != CompilationStatus.Success)
                    {
                        var errorMsg = s_api.ResultGetErrorMessageS(result);
                        throw new InvalidOperationException(
                            $"GLSL → SPIR-V compilation failed for {fileName} ({stage}):\n{errorMsg}");
                    }

                    var length = (int)s_api.ResultGetLength(result);
                    var bytesPtr = s_api.ResultGetBytes(result);

                    var spirv = new byte[length];
                    Marshal.Copy((nint)bytesPtr, spirv, 0, length);
                    return spirv;
                }
                finally
                {
                    s_api.ResultRelease(result);
                }
            }
            finally
            {
                s_api.CompileOptionsRelease(options);
            }
        }
    }

    /// <summary>
    /// Compiles GLSL source code to SPIR-V, returning null on failure instead of throwing.
    /// </summary>
    public static byte[]? TryCompileGLSLToSPIRV(string glslSource, ShaderStage stage, out string? errorMessage, string? debugName = null)
    {
        try
        {
            errorMessage = null;
            return CompileGLSLToSPIRV(glslSource, stage, debugName);
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return null;
        }
    }

    private static void EnsureInitialized()
    {
        if (s_api != null)
            return;

        s_api = ShadercApi.GetApi();
        s_compiler = s_api.CompilerInitialize();

        if (s_compiler == null)
            throw new InvalidOperationException("Failed to initialize shaderc compiler.");
    }

    private static ShaderKind MapShaderKind(ShaderStage stage) => stage switch
    {
        ShaderStage.Vertex => ShaderKind.VertexShader,
        ShaderStage.Fragment => ShaderKind.FragmentShader,
        ShaderStage.Geometry => ShaderKind.GeometryShader,
        ShaderStage.Compute => ShaderKind.ComputeShader,
        ShaderStage.TessellationControl => ShaderKind.TessControlShader,
        ShaderStage.TessellationEvaluation => ShaderKind.TessEvaluationShader,
        _ => throw new ArgumentException($"Unsupported shader stage for cross-compilation: {stage}", nameof(stage)),
    };

    /// <summary>
    /// Releases the shaderc compiler. Call during application shutdown.
    /// </summary>
    internal static void Shutdown()
    {
        lock (s_lock)
        {
            if (s_api != null && s_compiler != null)
            {
                s_api.CompilerRelease(s_compiler);
                s_compiler = null;
            }

            s_api?.Dispose();
            s_api = null;
        }
    }
}
