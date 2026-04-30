using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace WorldBuilder.Gpu;

/// <summary>Vulkan hydraulic droplet compute (SPIR-V from Hydraulic.hlsl): atomics into fixed-point int grids, merged on CPU.</summary>
internal sealed unsafe class HydraulicComputeShaderWrapper : IDisposable
{
    private readonly VulkanGpuContext _ctx;
    private readonly Vk _vk;
    private readonly Device _device;
    private readonly Queue _queue;
    private readonly CommandPool _commandPool;

    private ShaderModule _shaderModule;
    private Pipeline _pipeline;
    private PipelineLayout _pipelineLayout;
    private DescriptorSetLayout _descriptorLayout;
    private DescriptorPool _descriptorPool;
    private DescriptorSet _descriptorSet;

    private int _capacity;

    private VkBuffer _snapshotBuffer;
    private DeviceMemory _snapshotMemory;
    private nint _snapshotMapped;

    private VkBuffer _heightDeltaBuffer;
    private DeviceMemory _heightDeltaMemory;
    private nint _heightDeltaMapped;

    private VkBuffer _depositBuffer;
    private DeviceMemory _depositMemory;
    private nint _depositMapped;

    private VkBuffer _flowBuffer;
    private DeviceMemory _flowMemory;
    private nint _flowMapped;

    private VkBuffer _hydrologyBuffer;
    private DeviceMemory _hydrologyMemory;
    private nint _hydrologyMapped;

    private VkBuffer _uniformBuffer;
    private DeviceMemory _uniformMemory;
    private nint _uniformMapped;

    private Fence _fence;

    public HydraulicComputeShaderWrapper(VulkanGpuContext ctx, ReadOnlySpan<byte> spirvHydraulic)
    {
        _ctx = ctx;
        _vk = ctx.Vk;
        _device = ctx.Device;
        _queue = ctx.Queue;
        _commandPool = ctx.CommandPool;

        fixed (byte* spirvPtr = spirvHydraulic)
        {
            ShaderModuleCreateInfo sm = new()
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)spirvHydraulic.Length,
                PCode = (uint*)spirvPtr,
            };

            if (_vk.CreateShaderModule(_device, in sm, null, out _shaderModule) != Result.Success)
                throw new InvalidOperationException("VkCreateShaderModule (hydraulic) failed.");
        }

        DescriptorSetLayoutBinding[] bindings =
        {
            new DescriptorSetLayoutBinding
            {
                Binding = 0,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.ComputeBit,
            },
            new DescriptorSetLayoutBinding
            {
                Binding = 1,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.ComputeBit,
            },
            new DescriptorSetLayoutBinding
            {
                Binding = 2,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.ComputeBit,
            },
            new DescriptorSetLayoutBinding
            {
                Binding = 3,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.ComputeBit,
            },
            new DescriptorSetLayoutBinding
            {
                Binding = 4,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.ComputeBit,
            },
            new DescriptorSetLayoutBinding
            {
                Binding = 5,
                DescriptorType = DescriptorType.UniformBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.ComputeBit,
            },
        };

        fixed (DescriptorSetLayoutBinding* pBindings = bindings)
        {
            DescriptorSetLayoutCreateInfo dsl = new()
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = (uint)bindings.Length,
                PBindings = pBindings,
            };

            if (_vk.CreateDescriptorSetLayout(_device, in dsl, null, out _descriptorLayout) != Result.Success)
                throw new InvalidOperationException("VkCreateDescriptorSetLayout (hydraulic) failed.");
        }

        DescriptorSetLayout* pSetLayouts = stackalloc DescriptorSetLayout[1];
        pSetLayouts[0] = _descriptorLayout;

        PipelineLayoutCreateInfo pl = new()
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = pSetLayouts,
        };

        if (_vk.CreatePipelineLayout(_device, in pl, null, out _pipelineLayout) != Result.Success)
            throw new InvalidOperationException("VkCreatePipelineLayout (hydraulic) failed.");

        byte* entryName = stackalloc byte[64];
        ReadOnlySpan<byte> mainUtf8 = "main"u8;
        mainUtf8.CopyTo(new Span<byte>(entryName, mainUtf8.Length));
        entryName[mainUtf8.Length] = 0;

        PipelineShaderStageCreateInfo stage = new()
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.ComputeBit,
            Module = _shaderModule,
            PName = entryName,
        };

        ComputePipelineCreateInfo cp = new()
        {
            SType = StructureType.ComputePipelineCreateInfo,
            Stage = stage,
            Layout = _pipelineLayout,
        };

        if (_vk.CreateComputePipelines(_device, default, 1, ref cp, null, out _pipeline) != Result.Success)
            throw new InvalidOperationException("VkCreateComputePipelines (hydraulic) failed.");

        DescriptorPoolSize[] sizes =
        {
            new DescriptorPoolSize { Type = DescriptorType.StorageBuffer, DescriptorCount = 8 },
            new DescriptorPoolSize { Type = DescriptorType.UniformBuffer, DescriptorCount = 1 },
        };

        fixed (DescriptorPoolSize* psizes = sizes)
        {
            DescriptorPoolCreateInfo dpi = new()
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                MaxSets = 1,
                PoolSizeCount = 2,
                PPoolSizes = psizes,
            };

            if (_vk.CreateDescriptorPool(_device, in dpi, null, out _descriptorPool) != Result.Success)
                throw new InvalidOperationException("VkCreateDescriptorPool (hydraulic) failed.");
        }

        DescriptorSetLayout* pLayoutOne = stackalloc DescriptorSetLayout[1];
        pLayoutOne[0] = _descriptorLayout;

        DescriptorSetAllocateInfo dai = new()
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = pLayoutOne,
        };

        if (_vk.AllocateDescriptorSets(_device, in dai, out _descriptorSet) != Result.Success)
            throw new InvalidOperationException("VkAllocateDescriptorSets (hydraulic) failed.");

        (_uniformBuffer, _uniformMemory, _uniformMapped) = _ctx.CreateHostVisibleBuffer(
            HydraulicGpu.UniformByteCount,
            BufferUsageFlags.UniformBufferBit);

        FenceCreateInfo fi = new() { SType = StructureType.FenceCreateInfo };
        if (_vk.CreateFence(_device, in fi, null, out _fence) != Result.Success)
            throw new InvalidOperationException("VkCreateFence (hydraulic) failed.");
    }

    public void RunPass(float[] heights, float[] depositAccum, float[] flowAccum, int width, int height, in HydraulicParamsGpu parameters)
    {
        int count = checked(width * height);
        ulong floatBytes = (ulong)(count * sizeof(float));
        ulong intBytes = (ulong)(count * sizeof(int));

        EnsureGridCapacity(count, floatBytes, intBytes);

        heights.AsSpan().CopyTo(new Span<float>((void*)_snapshotMapped, count));

        new Span<int>((void*)_heightDeltaMapped, count).Clear();
        new Span<int>((void*)_depositMapped, count).Clear();
        new Span<int>((void*)_flowMapped, count).Clear();

        HydraulicGpu.WriteUniform(_uniformMapped, in parameters);

        _vk.ResetFences(_device, 1, ref _fence);

        CommandBufferAllocateInfo cbai = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };

        if (_vk.AllocateCommandBuffers(_device, in cbai, out CommandBuffer cmdBuf) != Result.Success)
            throw new InvalidOperationException("VkAllocateCommandBuffers (hydraulic) failed.");

        CommandBufferBeginInfo bi = new() { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        if (_vk.BeginCommandBuffer(cmdBuf, in bi) != Result.Success)
            throw new InvalidOperationException("VkBeginCommandBuffer (hydraulic) failed.");

        _vk.CmdBindPipeline(cmdBuf, PipelineBindPoint.Compute, _pipeline);

        DescriptorSet* boundSets = stackalloc DescriptorSet[1];
        boundSets[0] = _descriptorSet;
        _vk.CmdBindDescriptorSets(cmdBuf, PipelineBindPoint.Compute, _pipelineLayout, 0, 1, boundSets, 0, null);

        uint drops = parameters.DropsPerPass;
        uint gx = (drops + 255u) / 256u;
        _vk.CmdDispatch(cmdBuf, gx, 1, 1);

        if (_vk.EndCommandBuffer(cmdBuf) != Result.Success)
            throw new InvalidOperationException("VkEndCommandBuffer (hydraulic) failed.");

        CommandBuffer* pCmdSubmit = stackalloc CommandBuffer[1];
        pCmdSubmit[0] = cmdBuf;

        SubmitInfo* pSubmit = stackalloc SubmitInfo[1];
        pSubmit[0] = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = pCmdSubmit,
        };

        if (_vk.QueueSubmit(_queue, 1, pSubmit, _fence) != Result.Success)
            throw new InvalidOperationException("VkQueueSubmit (hydraulic) failed.");

        if (_vk.WaitForFences(_device, 1, ref _fence, true, ulong.MaxValue) != Result.Success)
            throw new InvalidOperationException("VkWaitForFences (hydraulic) failed.");

        float inv = 1f / HydraulicGpu.FixedScale;
        ReadOnlySpan<int> hI = new((void*)_heightDeltaMapped, count);
        ReadOnlySpan<int> dI = new((void*)_depositMapped, count);
        ReadOnlySpan<int> fI = new((void*)_flowMapped, count);

        for (int i = 0; i < count; i++)
        {
            float dh = hI[i] * inv;
            float v = heights[i] + dh;
            if (v < 0f)
                v = 0f;
            heights[i] = v;

            depositAccum[i] += dI[i] * inv;
            flowAccum[i] += fI[i] * inv;
        }

        Span<CommandBuffer> oneCmd = stackalloc CommandBuffer[1];
        oneCmd[0] = cmdBuf;
        _vk.FreeCommandBuffers(_device, _commandPool, oneCmd);
    }

    /// <summary>Zeros GPU hydrology SSBO before the first hydraulic pass (values persist across passes until reset).</summary>
    public void ResetHydrologyAccumulation()
    {
        if (_capacity == 0 || _hydrologyMapped == 0)
            return;
        new Span<int>((void*)_hydrologyMapped, _capacity).Clear();
    }

    internal ReadOnlySpan<int> GetHydrologyFixedRowMajor(int count)
    {
        if (_capacity != count || _hydrologyMapped == 0 || count == 0)
            return ReadOnlySpan<int>.Empty;
        return new ReadOnlySpan<int>((void*)_hydrologyMapped, count);
    }

    private void EnsureGridCapacity(int count, ulong floatBytes, ulong intBytes)
    {
        if (_capacity == count && _snapshotBuffer.Handle != 0 && _hydrologyBuffer.Handle != 0)
            return;

        DestroyGridBuffersOnly();

        (_snapshotBuffer, _snapshotMemory, _snapshotMapped) = _ctx.CreateHostVisibleBuffer(floatBytes, BufferUsageFlags.StorageBufferBit);
        (_heightDeltaBuffer, _heightDeltaMemory, _heightDeltaMapped) = _ctx.CreateHostVisibleBuffer(intBytes, BufferUsageFlags.StorageBufferBit);
        (_depositBuffer, _depositMemory, _depositMapped) = _ctx.CreateHostVisibleBuffer(intBytes, BufferUsageFlags.StorageBufferBit);
        (_flowBuffer, _flowMemory, _flowMapped) = _ctx.CreateHostVisibleBuffer(intBytes, BufferUsageFlags.StorageBufferBit);
        (_hydrologyBuffer, _hydrologyMemory, _hydrologyMapped) = _ctx.CreateHostVisibleBuffer(intBytes, BufferUsageFlags.StorageBufferBit);
        new Span<int>((void*)_hydrologyMapped, count).Clear();

        _capacity = count;

        DescriptorBufferInfo* bufInfos = stackalloc DescriptorBufferInfo[6];
        bufInfos[0] = new DescriptorBufferInfo { Buffer = _snapshotBuffer, Offset = 0, Range = floatBytes };
        bufInfos[1] = new DescriptorBufferInfo { Buffer = _heightDeltaBuffer, Offset = 0, Range = intBytes };
        bufInfos[2] = new DescriptorBufferInfo { Buffer = _depositBuffer, Offset = 0, Range = intBytes };
        bufInfos[3] = new DescriptorBufferInfo { Buffer = _flowBuffer, Offset = 0, Range = intBytes };
        bufInfos[4] = new DescriptorBufferInfo { Buffer = _hydrologyBuffer, Offset = 0, Range = intBytes };
        bufInfos[5] = new DescriptorBufferInfo { Buffer = _uniformBuffer, Offset = 0, Range = HydraulicGpu.UniformByteCount };

        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[6];
        writes[0] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _descriptorSet,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageBuffer,
            PBufferInfo = bufInfos,
        };
        writes[1] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _descriptorSet,
            DstBinding = 1,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageBuffer,
            PBufferInfo = bufInfos + 1,
        };
        writes[2] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _descriptorSet,
            DstBinding = 2,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageBuffer,
            PBufferInfo = bufInfos + 2,
        };
        writes[3] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _descriptorSet,
            DstBinding = 3,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageBuffer,
            PBufferInfo = bufInfos + 3,
        };
        writes[4] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _descriptorSet,
            DstBinding = 4,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageBuffer,
            PBufferInfo = bufInfos + 4,
        };
        writes[5] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _descriptorSet,
            DstBinding = 5,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.UniformBuffer,
            PBufferInfo = bufInfos + 5,
        };

        _vk.UpdateDescriptorSets(_device, 6, writes, 0, null);
    }

    private void DestroyGridBuffersOnly()
    {
        if (_hydrologyMemory.Handle != 0)
        {
            _vk.UnmapMemory(_device, _hydrologyMemory);
            _vk.FreeMemory(_device, _hydrologyMemory, null);
            _hydrologyMemory = default;
            _hydrologyMapped = default;
        }

        if (_hydrologyBuffer.Handle != 0)
        {
            _vk.DestroyBuffer(_device, _hydrologyBuffer, null);
            _hydrologyBuffer = default;
        }

        if (_flowMemory.Handle != 0)
        {
            _vk.UnmapMemory(_device, _flowMemory);
            _vk.FreeMemory(_device, _flowMemory, null);
            _flowMemory = default;
            _flowMapped = default;
        }

        if (_flowBuffer.Handle != 0)
        {
            _vk.DestroyBuffer(_device, _flowBuffer, null);
            _flowBuffer = default;
        }

        if (_depositMemory.Handle != 0)
        {
            _vk.UnmapMemory(_device, _depositMemory);
            _vk.FreeMemory(_device, _depositMemory, null);
            _depositMemory = default;
            _depositMapped = default;
        }

        if (_depositBuffer.Handle != 0)
        {
            _vk.DestroyBuffer(_device, _depositBuffer, null);
            _depositBuffer = default;
        }

        if (_heightDeltaMemory.Handle != 0)
        {
            _vk.UnmapMemory(_device, _heightDeltaMemory);
            _vk.FreeMemory(_device, _heightDeltaMemory, null);
            _heightDeltaMemory = default;
            _heightDeltaMapped = default;
        }

        if (_heightDeltaBuffer.Handle != 0)
        {
            _vk.DestroyBuffer(_device, _heightDeltaBuffer, null);
            _heightDeltaBuffer = default;
        }

        if (_snapshotMemory.Handle != 0)
        {
            _vk.UnmapMemory(_device, _snapshotMemory);
            _vk.FreeMemory(_device, _snapshotMemory, null);
            _snapshotMemory = default;
            _snapshotMapped = default;
        }

        if (_snapshotBuffer.Handle != 0)
        {
            _vk.DestroyBuffer(_device, _snapshotBuffer, null);
            _snapshotBuffer = default;
        }

        _capacity = 0;
    }

    public void Dispose()
    {
        DestroyGridBuffersOnly();

        if (_fence.Handle != 0)
        {
            _vk.DestroyFence(_device, _fence, null);
            _fence = default;
        }

        if (_uniformMemory.Handle != 0)
        {
            _vk.UnmapMemory(_device, _uniformMemory);
            _vk.FreeMemory(_device, _uniformMemory, null);
            _uniformMemory = default;
            _uniformMapped = default;
        }

        if (_uniformBuffer.Handle != 0)
        {
            _vk.DestroyBuffer(_device, _uniformBuffer, null);
            _uniformBuffer = default;
        }

        if (_descriptorPool.Handle != 0)
        {
            _vk.DestroyDescriptorPool(_device, _descriptorPool, null);
            _descriptorPool = default;
        }

        if (_pipeline.Handle != 0)
        {
            _vk.DestroyPipeline(_device, _pipeline, null);
            _pipeline = default;
        }

        if (_pipelineLayout.Handle != 0)
        {
            _vk.DestroyPipelineLayout(_device, _pipelineLayout, null);
            _pipelineLayout = default;
        }

        if (_descriptorLayout.Handle != 0)
        {
            _vk.DestroyDescriptorSetLayout(_device, _descriptorLayout, null);
            _descriptorLayout = default;
        }

        if (_shaderModule.Handle != 0)
        {
            _vk.DestroyShaderModule(_device, _shaderModule, null);
            _shaderModule = default;
        }

        GC.SuppressFinalize(this);
    }
}
