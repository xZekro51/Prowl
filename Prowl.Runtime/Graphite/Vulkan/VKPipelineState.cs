// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace Prowl.Runtime.Graphite.Vulkan;

/// <summary>
/// Vulkan implementation of a graphics pipeline state.
/// </summary>
internal unsafe class VKPipelineState : PipelineState
{
    private readonly VKGraphiteDevice _device;
    internal Pipeline Handle { get; }
    internal PipelineLayout PipelineLayoutHandle { get; }
    internal RenderPass CompatibleRenderPass { get; }

    internal VKPipelineState(VKGraphiteDevice device, in PipelineStateDescriptor descriptor)
    {
        _device = device;
        Topology = descriptor.Topology;
        DebugName = descriptor.DebugName;

        // Create pipeline layout
        var setLayouts = Array.Empty<DescriptorSetLayout>();
        if (descriptor.BindGroupLayouts != null && descriptor.BindGroupLayouts.Length > 0)
        {
            setLayouts = new DescriptorSetLayout[descriptor.BindGroupLayouts.Length];
            for (int i = 0; i < descriptor.BindGroupLayouts.Length; i++)
                setLayouts[i] = ((VKBindGroupLayout)descriptor.BindGroupLayouts[i]).Handle;
        }

        fixed (DescriptorSetLayout* pSetLayouts = setLayouts)
        {
            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = (uint)setLayouts.Length,
                PSetLayouts = pSetLayouts,
            };
            VKGraphiteDevice.Check(device.Vk.CreatePipelineLayout(device.Device, &layoutInfo, null, out var pipelineLayout));
            PipelineLayoutHandle = pipelineLayout;
        }

        // Create a compatible render pass from the layout descriptor
        var rpKey = new RenderPassKey
        {
            ColorFormats = descriptor.RenderPassLayout.ColorFormats ?? [],
            ColorLoadOps = new LoadOp[descriptor.RenderPassLayout.ColorFormats?.Length ?? 0],
            ColorStoreOps = new StoreOp[descriptor.RenderPassLayout.ColorFormats?.Length ?? 0],
            DepthFormat = descriptor.RenderPassLayout.DepthStencilFormat,
            DepthLoadOp = LoadOp.DontCare,
            DepthStoreOp = StoreOp.DontCare,
            StencilLoadOp = LoadOp.DontCare,
            StencilStoreOp = StoreOp.DontCare,
            SampleCount = descriptor.RenderPassLayout.SampleCount,
        };
        for (int i = 0; i < rpKey.ColorLoadOps.Length; i++)
        {
            rpKey.ColorLoadOps[i] = LoadOp.DontCare;
            rpKey.ColorStoreOps[i] = StoreOp.DontCare;
        }
        CompatibleRenderPass = device.GetOrCreateRenderPass(in rpKey);

        // Shader stages
        var stages = new List<PipelineShaderStageCreateInfo>();
        var entryPointPtrs = new List<nint>();

        void AddStage(Graphite.ShaderModule? module, ShaderStageFlags stage)
        {
            if (module is not VKShaderModule vkModule) return;
            var ptr = SilkMarshal.StringToPtr(vkModule.EntryPoint);
            entryPointPtrs.Add(ptr);
            stages.Add(new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = stage,
                Module = vkModule.Handle,
                PName = (byte*)ptr,
            });
        }

        AddStage(descriptor.VertexShader, ShaderStageFlags.VertexBit);
        AddStage(descriptor.FragmentShader, ShaderStageFlags.FragmentBit);
        AddStage(descriptor.GeometryShader, ShaderStageFlags.GeometryBit);

        // Vertex input
        var vertexBindings = Array.Empty<VertexInputBindingDescription>();
        var vertexAttributes = new List<VertexInputAttributeDescription>();
        if (descriptor.VertexLayout.Buffers != null)
        {
            vertexBindings = new VertexInputBindingDescription[descriptor.VertexLayout.Buffers.Length];
            for (int i = 0; i < descriptor.VertexLayout.Buffers.Length; i++)
            {
                ref readonly var buf = ref descriptor.VertexLayout.Buffers[i];
                vertexBindings[i] = new VertexInputBindingDescription
                {
                    Binding = (uint)i,
                    Stride = buf.Stride,
                    InputRate = buf.StepMode == VertexStepMode.Instance ? VertexInputRate.Instance : VertexInputRate.Vertex,
                };
                if (buf.Attributes != null)
                {
                    foreach (var attr in buf.Attributes)
                    {
                        vertexAttributes.Add(new VertexInputAttributeDescription
                        {
                            Location = attr.Location,
                            Binding = (uint)i,
                            Format = VKFormatHelper.ToVkVertexFormat(attr.Format),
                            Offset = attr.Offset,
                        });
                    }
                }
            }
        }

        fixed (PipelineShaderStageCreateInfo* pStages = stages.ToArray())
        fixed (VertexInputBindingDescription* pBindings = vertexBindings)
        fixed (VertexInputAttributeDescription* pAttrs = vertexAttributes.ToArray())
        {
            var vertexInputState = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = (uint)vertexBindings.Length,
                PVertexBindingDescriptions = pBindings,
                VertexAttributeDescriptionCount = (uint)vertexAttributes.Count,
                PVertexAttributeDescriptions = pAttrs,
            };

            var inputAssembly = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = VKFormatHelper.ToVkTopology(descriptor.Topology),
                PrimitiveRestartEnable = false,
            };

            // Use dynamic viewport and scissor
            var dynamicStates = stackalloc DynamicState[]
            {
                DynamicState.Viewport,
                DynamicState.Scissor,
                DynamicState.BlendConstants,
                DynamicState.StencilReference,
            };
            var dynamicState = new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = 4,
                PDynamicStates = dynamicStates,
            };

            var viewportState = new PipelineViewportStateCreateInfo
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1,
                ScissorCount = 1,
            };

            ref readonly var rast = ref descriptor.RasterizerState;

            // The VK backend uses a negative viewport height for Y-flip (see VKCommandList.SetViewportCore).
            // The Y-flip reverses the apparent winding order in framebuffer space, but since Vulkan's
            // native framebuffer Y-axis is already opposite to OpenGL's window Y-axis, these two
            // inversions cancel out. The net result is that the winding order in Vulkan framebuffer
            // space (with Y-flip) matches OpenGL window coordinates, so no front face flip is needed.
            var rasterizer = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                DepthClampEnable = rast.DepthClampEnable,
                RasterizerDiscardEnable = false,
                PolygonMode = VKFormatHelper.ToVkPolygonMode(rast.PolygonMode),
                CullMode = VKFormatHelper.ToVkCullMode(rast.CullMode),
                FrontFace = VKFormatHelper.ToVkFrontFace(rast.FrontFace),
                DepthBiasEnable = rast.DepthBiasEnable,
                DepthBiasConstantFactor = rast.DepthBiasConstant,
                DepthBiasSlopeFactor = rast.DepthBiasSlope,
                LineWidth = 1.0f,
            };

            var multisample = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = VKFormatHelper.ToVkSampleCount(descriptor.RenderPassLayout.SampleCount),
                SampleShadingEnable = false,
                MinSampleShading = 1.0f,
            };

            ref readonly var ds = ref descriptor.DepthStencilState;
            var depthStencil = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = ds.DepthTestEnable,
                DepthWriteEnable = ds.DepthWriteEnable,
                DepthCompareOp = VKFormatHelper.ToVkCompareOp(ds.DepthCompare),
                DepthBoundsTestEnable = false,
                StencilTestEnable = ds.StencilTestEnable,
                Front = ToVkStencilOpState(ds.StencilFront, ds.StencilReadMask, ds.StencilWriteMask),
                Back = ToVkStencilOpState(ds.StencilBack, ds.StencilReadMask, ds.StencilWriteMask),
            };

            // Blend state — attachment count MUST match render pass color attachment count (Vulkan spec)
            int colorAttachmentCount = descriptor.RenderPassLayout.ColorFormats?.Length ?? 0;
            var blendAttachments = Array.Empty<PipelineColorBlendAttachmentState>();
            if (descriptor.BlendState.Attachments != null && descriptor.BlendState.Attachments.Length > 0)
            {
                int srcCount = descriptor.BlendState.Attachments.Length;
                int count = Math.Max(srcCount, colorAttachmentCount);
                blendAttachments = new PipelineColorBlendAttachmentState[count];
                for (int i = 0; i < count; i++)
                {
                    // Replicate last descriptor entry for any extra color attachments
                    ref readonly var ba = ref descriptor.BlendState.Attachments[Math.Min(i, srcCount - 1)];
                    blendAttachments[i] = new PipelineColorBlendAttachmentState
                    {
                        BlendEnable = ba.BlendEnable,
                        SrcColorBlendFactor = VKFormatHelper.ToVkBlendFactor(ba.SrcColorFactor),
                        DstColorBlendFactor = VKFormatHelper.ToVkBlendFactor(ba.DstColorFactor),
                        ColorBlendOp = VKFormatHelper.ToVkBlendOp(ba.ColorOp),
                        SrcAlphaBlendFactor = VKFormatHelper.ToVkBlendFactor(ba.SrcAlphaFactor),
                        DstAlphaBlendFactor = VKFormatHelper.ToVkBlendFactor(ba.DstAlphaFactor),
                        AlphaBlendOp = VKFormatHelper.ToVkBlendOp(ba.AlphaOp),
                        ColorWriteMask = (ColorComponentFlags)VKFormatHelper.ToVkColorWriteMask(ba.WriteMask),
                    };
                }
            }
            else if (colorAttachmentCount > 0)
            {
                // No blend state provided but render pass has color attachments — use opaque defaults
                blendAttachments = new PipelineColorBlendAttachmentState[colorAttachmentCount];
                for (int i = 0; i < colorAttachmentCount; i++)
                {
                    blendAttachments[i] = new PipelineColorBlendAttachmentState
                    {
                        BlendEnable = false,
                        ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                    };
                }
            }

            fixed (PipelineColorBlendAttachmentState* pBlendAttachments = blendAttachments)
            {
                var colorBlend = new PipelineColorBlendStateCreateInfo
                {
                    SType = StructureType.PipelineColorBlendStateCreateInfo,
                    LogicOpEnable = false,
                    AttachmentCount = (uint)blendAttachments.Length,
                    PAttachments = pBlendAttachments,
                };

                var pipelineInfo = new GraphicsPipelineCreateInfo
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    StageCount = (uint)stages.Count,
                    PStages = pStages,
                    PVertexInputState = &vertexInputState,
                    PInputAssemblyState = &inputAssembly,
                    PViewportState = &viewportState,
                    PRasterizationState = &rasterizer,
                    PMultisampleState = &multisample,
                    PDepthStencilState = &depthStencil,
                    PColorBlendState = &colorBlend,
                    PDynamicState = &dynamicState,
                    Layout = PipelineLayoutHandle,
                    RenderPass = CompatibleRenderPass,
                    Subpass = 0,
                };

                VKGraphiteDevice.Check(device.Vk.CreateGraphicsPipelines(device.Device, default, 1, &pipelineInfo, null, out var pipeline));
                Handle = pipeline;
            }
        }

        // Free entry point strings
        foreach (var ptr in entryPointPtrs)
            SilkMarshal.Free(ptr);
    }

    protected override void DisposeResources()
    {
        _device.Vk.DestroyPipeline(_device.Device, Handle, null);
        _device.Vk.DestroyPipelineLayout(_device.Device, PipelineLayoutHandle, null);
    }

    private static StencilOpState ToVkStencilOpState(StencilFaceState face, byte readMask, byte writeMask) => new()
    {
        FailOp = VKFormatHelper.ToVkStencilOp(face.FailOp),
        PassOp = VKFormatHelper.ToVkStencilOp(face.PassOp),
        DepthFailOp = VKFormatHelper.ToVkStencilOp(face.DepthFailOp),
        CompareOp = VKFormatHelper.ToVkCompareOp(face.Compare),
        CompareMask = readMask,
        WriteMask = writeMask,
        Reference = 0,
    };
}

/// <summary>
/// Vulkan implementation of a compute pipeline state.
/// </summary>
internal unsafe class VKComputePipelineState : ComputePipelineState
{
    private readonly VKGraphiteDevice _device;
    internal Pipeline Handle { get; }
    internal PipelineLayout PipelineLayoutHandle { get; }

    internal VKComputePipelineState(VKGraphiteDevice device, in ComputePipelineStateDescriptor descriptor)
    {
        _device = device;
        DebugName = descriptor.DebugName;

        if (descriptor.ComputeShader is not VKShaderModule computeModule)
            throw new ArgumentException("Compute shader must be a Vulkan shader module.");

        // Create pipeline layout
        var setLayouts = Array.Empty<DescriptorSetLayout>();
        if (descriptor.BindGroupLayouts != null && descriptor.BindGroupLayouts.Length > 0)
        {
            setLayouts = new DescriptorSetLayout[descriptor.BindGroupLayouts.Length];
            for (int i = 0; i < descriptor.BindGroupLayouts.Length; i++)
                setLayouts[i] = ((VKBindGroupLayout)descriptor.BindGroupLayouts[i]).Handle;
        }

        fixed (DescriptorSetLayout* pSetLayouts = setLayouts)
        {
            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = (uint)setLayouts.Length,
                PSetLayouts = pSetLayouts,
            };
            VKGraphiteDevice.Check(device.Vk.CreatePipelineLayout(device.Device, &layoutInfo, null, out var pipelineLayout));
            PipelineLayoutHandle = pipelineLayout;
        }

        var entryPt = SilkMarshal.StringToPtr(computeModule.EntryPoint);
        var stageInfo = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.ComputeBit,
            Module = computeModule.Handle,
            PName = (byte*)entryPt,
        };

        var pipelineInfo = new ComputePipelineCreateInfo
        {
            SType = StructureType.ComputePipelineCreateInfo,
            Stage = stageInfo,
            Layout = PipelineLayoutHandle,
        };

        VKGraphiteDevice.Check(device.Vk.CreateComputePipelines(device.Device, default, 1, &pipelineInfo, null, out var pipeline));
        Handle = pipeline;

        SilkMarshal.Free(entryPt);
    }

    protected override void DisposeResources()
    {
        _device.Vk.DestroyPipeline(_device.Device, Handle, null);
        _device.Vk.DestroyPipelineLayout(_device.Device, PipelineLayoutHandle, null);
    }
}
