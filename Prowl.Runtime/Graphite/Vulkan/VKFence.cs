// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Silk.NET.Vulkan;

using VkFence = Silk.NET.Vulkan.Fence;

namespace Prowl.Runtime.Graphite.Vulkan;

/// <summary>
/// Vulkan implementation of a GPU fence.
/// </summary>
internal unsafe class VKFence : Graphite.Fence
{
    private readonly VKGraphiteDevice _device;
    internal VkFence Handle { get; }

    internal VKFence(VKGraphiteDevice device, bool signaled)
    {
        _device = device;

        var fenceInfo = new FenceCreateInfo
        {
            SType = StructureType.FenceCreateInfo,
            Flags = signaled ? FenceCreateFlags.SignaledBit : 0,
        };

        VKGraphiteDevice.Check(device.Vk.CreateFence(device.Device, &fenceInfo, null, out var fence));
        Handle = fence;
    }

    public override bool IsSignaled
    {
        get
        {
            var result = _device.Vk.GetFenceStatus(_device.Device, Handle);
            return result == Result.Success;
        }
    }

    public override void Reset()
    {
        var fence = Handle;
        VKGraphiteDevice.Check(_device.Vk.ResetFences(_device.Device, 1, &fence));
    }

    public override void Wait()
    {
        var fence = Handle;
        VKGraphiteDevice.Check(_device.Vk.WaitForFences(_device.Device, 1, &fence, true, ulong.MaxValue));
    }

    public override bool Wait(uint timeoutMs)
    {
        var fence = Handle;
        ulong timeoutNs = (ulong)timeoutMs * 1_000_000;
        var result = _device.Vk.WaitForFences(_device.Device, 1, &fence, true, timeoutNs);
        return result == Result.Success;
    }

    protected override void DisposeResources()
    {
        _device.Vk.DestroyFence(_device.Device, Handle, null);
    }
}
