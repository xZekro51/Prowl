// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using Prowl.Runtime.Graphite;
using Prowl.Runtime.Rendering;
using Prowl.Vector;

using Silk.NET.OpenGL;

using Graphite = Prowl.Runtime.Graphite;

namespace Prowl.Runtime;

public class GraphicsProgram : IDisposable
{
    private static int _nextId = 0;

    public int ID { get; }

    // Uniform cache - tracks what values are currently set in this shader program
    internal class UniformCache
    {
        public Dictionary<string, float> floats = [];
        public Dictionary<string, int> ints = [];
        public Dictionary<string, Float2> vectors2 = [];
        public Dictionary<string, Float3> vectors3 = [];
        public Dictionary<string, Float4> vectors4 = [];
        public Dictionary<string, Float4x4> matrices = [];
        public Dictionary<string, GraphicsBuffer> buffers = [];

        public void Clear()
        {
            floats.Clear();
            ints.Clear();
            vectors2.Clear();
            vectors3.Clear();
            vectors4.Clear();
            matrices.Clear();
            buffers.Clear();
        }
    }

    internal UniformCache uniformCache = new();

    public bool IsDisposed { get; protected set; }

    public uint Handle { get; private set; }

    /// <summary>Shadow Graphite vertex shader module (null when Graphite is not ready).</summary>
    public Graphite.ShaderModule? GraphiteVertexModule { get; private set; }

    /// <summary>Shadow Graphite fragment shader module (null when Graphite is not ready).</summary>
    public Graphite.ShaderModule? GraphiteFragmentModule { get; private set; }

    /// <summary>Shadow Graphite geometry shader module (null when Graphite is not ready).</summary>
    public Graphite.ShaderModule? GraphiteGeometryModule { get; private set; }

    /// <summary>Whether this program was created for a non-GL backend (Vulkan).
    /// When true, only Graphite modules are valid; GL <see cref="Handle"/> is 0.</summary>
    private bool _isGraphiteOnly;

    /// <summary>Merged SPIR-V reflection data. Populated on both Vulkan and OpenGL backends.</summary>
    internal SpirvReflection.ReflectionResult? Reflection { get; private set; }

    /// <summary>Cached bind group layout derived from <see cref="Reflection"/>.</summary>
    private BindGroupLayout? _bindGroupLayout;

    /// <summary>
    /// Returns a cached <see cref="BindGroupLayout"/> derived from the SPIR-V reflection.
    /// Creates it on first call.  Returns null if no reflection data is available.
    /// </summary>
    internal BindGroupLayout? GetOrCreateBindGroupLayout()
    {
        if (_bindGroupLayout != null) return _bindGroupLayout;
        if (Reflection == null) return null;
        _bindGroupLayout = GraphiteMaterialBinder.CreateBindGroupLayout(Reflection);
        return _bindGroupLayout;
    }

    public GraphicsProgram(string fragmentSource, string vertexSource, string geometrySource) : base()
    {
        ID = System.Threading.Interlocked.Increment(ref _nextId);

        bool isVulkan = Graphics.IsGraphiteReady &&
                        Graphics.Graphite.BackendType == Graphite.GraphicsBackendType.Vulkan;

        if (isVulkan)
        {
            _isGraphiteOnly = true;
            CreateGraphiteModulesVulkan(vertexSource, fragmentSource, geometrySource);
        }
        else
        {
            _isGraphiteOnly = false;
            CompileOpenGL(fragmentSource, vertexSource, geometrySource);
            CreateGraphiteModulesGL(vertexSource, fragmentSource, geometrySource);
        }
    }

    #region OpenGL compilation

    private void CompileOpenGL(string fragmentSource, string vertexSource, string geometrySource)
    {
        int statusCode = -1;
        string info = string.Empty;

        Handle = Graphics.GL.CreateProgram();

        // Create fragment shader if requested
        if (!string.IsNullOrEmpty(fragmentSource))
        {
            uint fragmentShader = Graphics.GL.CreateShader(ShaderType.FragmentShader);
            Graphics.GL.ShaderSource(fragmentShader, fragmentSource);
            Graphics.GL.CompileShader(fragmentShader);

            Graphics.GL.GetShaderInfoLog(fragmentShader, out info);
            Graphics.GL.GetShader(fragmentShader, ShaderParameterName.CompileStatus, out statusCode);

            if (statusCode != 1)
            {
                IsDisposed = true;
                Graphics.GL.DeleteShader(fragmentShader);
                Graphics.GL.DeleteProgram(Handle);

                throw new InvalidOperationException("Failed to Compile Fragment Shader Source.\n" +
                    info + "\n\n" +
                    "Status Code: " + statusCode.ToString());
            }

            Graphics.GL.AttachShader(Handle, fragmentShader);
            Graphics.GL.DeleteShader(fragmentShader);
        }

        // Create vertex shader if requested
        if (!string.IsNullOrEmpty(vertexSource))
        {
            uint vertexShader = Graphics.GL.CreateShader(ShaderType.VertexShader);
            Graphics.GL.ShaderSource(vertexShader, vertexSource);
            Graphics.GL.CompileShader(vertexShader);

            Graphics.GL.GetShaderInfoLog(vertexShader, out info);
            Graphics.GL.GetShader(vertexShader, ShaderParameterName.CompileStatus, out statusCode);

            if (statusCode != 1)
            {
                IsDisposed = true;
                Graphics.GL.DeleteShader(vertexShader);
                Graphics.GL.DeleteProgram(Handle);

                throw new InvalidOperationException("Failed to Compile Vertex Shader Source.\n" +
                    info + "\n\n" +
                    "Status Code: " + statusCode.ToString());
            }

            Graphics.GL.AttachShader(Handle, vertexShader);
            Graphics.GL.DeleteShader(vertexShader);
        }

        // Create geometry shader if requested
        if (!string.IsNullOrEmpty(geometrySource))
        {
            uint geometryShader = Graphics.GL.CreateShader(ShaderType.GeometryShader);
            Graphics.GL.ShaderSource(geometryShader, geometrySource);
            Graphics.GL.CompileShader(geometryShader);

            Graphics.GL.GetShaderInfoLog(geometryShader, out info);
            Graphics.GL.GetShader(geometryShader, ShaderParameterName.CompileStatus, out statusCode);

            if (statusCode != 1)
            {
                IsDisposed = true;
                Graphics.GL.DeleteShader(geometryShader);
                Graphics.GL.DeleteProgram(Handle);

                throw new InvalidOperationException("Failed to Compile Geometry Shader Source.\n" +
                    info + "\n\n" +
                    "Status Code: " + statusCode.ToString());
            }

            Graphics.GL.AttachShader(Handle, geometryShader);
            Graphics.GL.DeleteShader(geometryShader);
        }

        // Link the compiled program
        Graphics.GL.LinkProgram(Handle);

        Graphics.GL.GetProgramInfoLog(Handle, out info);
        Graphics.GL.GetProgram(Handle, ProgramPropertyARB.LinkStatus, out statusCode);
        if (statusCode != 1)
        {
            IsDisposed = true;
            Graphics.GL.DeleteProgram(Handle);

            throw new InvalidOperationException("Failed to Link Shader Program.\n" +
                    info + "\n\n" +
                    "Status Code: " + statusCode.ToString());
        }

        Graphics.GL.Flush();
    }

    #endregion

    #region Graphite module creation

    private void CreateGraphiteModulesGL(string vertexSource, string fragmentSource, string geometrySource)
    {
        if (!Graphics.IsGraphiteReady)
            return;

        try
        {
            if (!string.IsNullOrEmpty(vertexSource))
                GraphiteVertexModule = Graphics.Graphite.CreateShaderModule(Graphite.ShaderModuleDescriptor.VertexGLSL(vertexSource));
            if (!string.IsNullOrEmpty(fragmentSource))
                GraphiteFragmentModule = Graphics.Graphite.CreateShaderModule(Graphite.ShaderModuleDescriptor.FragmentGLSL(fragmentSource));
            if (!string.IsNullOrEmpty(geometrySource))
                GraphiteGeometryModule = Graphics.Graphite.CreateShaderModule(Graphite.ShaderModuleDescriptor.GeometryGLSL(geometrySource));
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[GraphicsProgram] Failed to create Graphite shader modules (GL): {ex.Message}");
        }

        // Also cross-compile GLSL → SPIR-V for reflection data.
        // This populates Reflection so that GetOrCreateBindGroupLayout() works
        // on OpenGL, enabling the Graphite command list rendering path.
        GenerateSpirvReflectionForGL(vertexSource, fragmentSource, geometrySource);
    }

    private void GenerateSpirvReflectionForGL(string vertexSource, string fragmentSource, string geometrySource)
    {
        SpirvReflection.ReflectionResult? vertRefl = null, fragRefl = null, geomRefl = null;

        try
        {
            if (!string.IsNullOrEmpty(vertexSource))
            {
                string vkSource = PatchGlslForSpirvReflection(vertexSource);
                byte[] spirv = Graphite.ShaderCrossCompiler.CompileGLSLToSPIRV(vkSource, Graphite.ShaderStage.Vertex);
                vertRefl = SpirvReflection.Reflect(spirv);
            }
            if (!string.IsNullOrEmpty(fragmentSource))
            {
                string vkSource = PatchGlslForSpirvReflection(fragmentSource);
                byte[] spirv = Graphite.ShaderCrossCompiler.CompileGLSLToSPIRV(vkSource, Graphite.ShaderStage.Fragment);
                fragRefl = SpirvReflection.Reflect(spirv);
            }
            if (!string.IsNullOrEmpty(geometrySource))
            {
                string vkSource = PatchGlslForSpirvReflection(geometrySource);
                byte[] spirv = Graphite.ShaderCrossCompiler.CompileGLSLToSPIRV(vkSource, Graphite.ShaderStage.Geometry);
                geomRefl = SpirvReflection.Reflect(spirv);
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[GraphicsProgram] SPIR-V cross-compilation for GL reflection failed: {ex.Message}");
            return;
        }

        List<SpirvReflection.ReflectionResult> results = [];
        if (vertRefl != null) results.Add(vertRefl);
        if (fragRefl != null) results.Add(fragRefl);
        if (geomRefl != null) results.Add(geomRefl);
        if (results.Count > 0)
            Reflection = SpirvReflection.Merge(results.ToArray());

#if DEBUG
        ValidateBindingsAgainstGL();
#endif
    }

    /// <summary>
    /// Patches OpenGL-targeted GLSL (#version 410) for SPIR-V cross-compilation
    /// by replacing the version directive with #version 450 and adding the
    /// PROWL_VULKAN define so conditional blocks match the Vulkan code path.
    /// </summary>
    private static string PatchGlslForSpirvReflection(string glslSource)
    {
        const string patchedHeader = "#version 450\n#define PROWL_VULKAN 1\n";

        int versionIdx = glslSource.IndexOf("#version", StringComparison.Ordinal);
        if (versionIdx >= 0)
        {
            int endOfLine = glslSource.IndexOf('\n', versionIdx);
            if (endOfLine >= 0)
                return patchedHeader + glslSource.Substring(endOfLine + 1);
        }

        return patchedHeader + glslSource;
    }

#if DEBUG
    /// <summary>
    /// Validates that SPIR-V reflection binding names match the active resources
    /// in the linked GL program. Logs warnings for any discrepancies that could
    /// cause incorrect rendering when the Graphite command list path is used on OpenGL.
    /// </summary>
    private void ValidateBindingsAgainstGL()
    {
        if (Reflection == null || Handle == 0)
            return;

        GL gl = Graphics.GL;
        int spirvUboCount = 0;

        foreach (SpirvReflection.ResourceBinding binding in Reflection.Bindings)
        {
            if (string.IsNullOrEmpty(binding.Name))
                continue;

            switch (binding.Type)
            {
                case SpirvReflection.ResourceType.UniformBuffer:
                {
                    spirvUboCount++;
                    uint blockIndex = gl.GetUniformBlockIndex(Handle, binding.Name);
                    if (blockIndex == uint.MaxValue) // GL_INVALID_INDEX
                    {
                        Debug.LogWarning(
                            $"[ShaderBindingValidation] SPIR-V reflection found UBO '{binding.Name}' " +
                            $"at set={binding.Set} binding={binding.Binding}, but GL program {Handle} " +
                            $"does not have a matching uniform block (may be optimized away by GL compiler).");
                    }
                    break;
                }

                case SpirvReflection.ResourceType.CombinedImageSampler:
                case SpirvReflection.ResourceType.SampledTexture:
                {
                    int location = gl.GetUniformLocation(Handle, binding.Name);
                    if (location < 0)
                    {
                        Debug.LogWarning(
                            $"[ShaderBindingValidation] SPIR-V reflection found sampler '{binding.Name}' " +
                            $"at set={binding.Set} binding={binding.Binding}, but GL program {Handle} " +
                            $"does not have a matching uniform location (may be optimized away by GL compiler).");
                    }
                    break;
                }
            }
        }

        // Compare total UBO counts between GL and SPIR-V reflection
        gl.GetProgram(Handle, ProgramPropertyARB.ActiveUniformBlocks, out int glBlockCount);
        if (glBlockCount != spirvUboCount)
        {
            Debug.LogWarning(
                $"[ShaderBindingValidation] GL program {Handle} has {glBlockCount} active uniform blocks, " +
                $"but SPIR-V reflection found {spirvUboCount} UBO bindings. " +
                $"Some blocks may be optimized away differently between the GL and SPIR-V compilers.");
        }
    }
#endif

    private void CreateGraphiteModulesVulkan(string vertexSource, string fragmentSource, string geometrySource)
    {
        // Cross-compile GLSL → SPIR-V, then create Vulkan shader modules.
        // Also run SPIR-V reflection to discover auto-assigned descriptor bindings.
        SpirvReflection.ReflectionResult? vertRefl = null, fragRefl = null, geomRefl = null;

        if (!string.IsNullOrEmpty(vertexSource))
        {
            byte[] spirv = Graphite.ShaderCrossCompiler.CompileGLSLToSPIRV(vertexSource, Graphite.ShaderStage.Vertex);
            GraphiteVertexModule = Graphics.Graphite.CreateShaderModule(Graphite.ShaderModuleDescriptor.VertexSPIRV(spirv));
            vertRefl = SpirvReflection.Reflect(spirv);
        }
        if (!string.IsNullOrEmpty(fragmentSource))
        {
            byte[] spirv = Graphite.ShaderCrossCompiler.CompileGLSLToSPIRV(fragmentSource, Graphite.ShaderStage.Fragment);
            GraphiteFragmentModule = Graphics.Graphite.CreateShaderModule(Graphite.ShaderModuleDescriptor.FragmentSPIRV(spirv));
            fragRefl = SpirvReflection.Reflect(spirv);
        }
        if (!string.IsNullOrEmpty(geometrySource))
        {
            byte[] spirv = Graphite.ShaderCrossCompiler.CompileGLSLToSPIRV(geometrySource, Graphite.ShaderStage.Geometry);
            GraphiteGeometryModule = Graphics.Graphite.CreateShaderModule(Graphite.ShaderModuleDescriptor.GeometrySPIRV(spirv));
            geomRefl = SpirvReflection.Reflect(spirv);
        }

        // Merge reflection from all stages
        var results = new List<SpirvReflection.ReflectionResult>();
        if (vertRefl != null) results.Add(vertRefl);
        if (fragRefl != null) results.Add(fragRefl);
        if (geomRefl != null) results.Add(geomRefl);
        if (results.Count > 0)
            Reflection = SpirvReflection.Merge(results.ToArray());
    }

    #endregion

    public static GraphicsProgram? currentProgram = null;
    public void Use()
    {
        if (_isGraphiteOnly)
            return;

        if (currentProgram != null && currentProgram.Handle == Handle)
            return;

        Graphics.GL.UseProgram(Handle);
        currentProgram = this;
    }

    public void Dispose()
    {
        if (IsDisposed)
            return;

        if (currentProgram != null && currentProgram.Handle == Handle)
            currentProgram = null;

        GraphiteVertexModule?.Dispose();
        GraphiteFragmentModule?.Dispose();
        GraphiteGeometryModule?.Dispose();
        GraphiteVertexModule = null;
        GraphiteFragmentModule = null;
        GraphiteGeometryModule = null;

        _bindGroupLayout?.Dispose();
        _bindGroupLayout = null;
        Reflection = null;

        if (!_isGraphiteOnly)
            Graphics.GL.DeleteProgram(Handle);

        IsDisposed = true;
    }

    public override string ToString()
    {
        return Handle.ToString();
    }
}
