using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace WorldBuilder.Gpu;

internal sealed unsafe class TerrainComputeShaderWrapper : IDisposable
{
    private const ulong UniformBufferSize = 256;

    private readonly VulkanGpuContext _ctx;
    private readonly Vk _vk;
    private readonly Device _device;
    private readonly Queue _queue;
    private readonly uint _queueFamily;
    private readonly CommandPool _commandPool;

    private ShaderModule _shaderModule;
    private Pipeline _pipeline;
    private PipelineLayout _pipelineLayout;
    private DescriptorSetLayout _descriptorLayout;
    private DescriptorPool _descriptorPool;
    private DescriptorSet _descriptorSet;

    private VkBuffer _heightBuffer;
    private DeviceMemory _heightMemory;
    private nint _heightMapped;

    private VkBuffer _uniformBuffer;
    private DeviceMemory _uniformMemory;
    private nint _uniformMapped;

    private Fence _fence;

    public TerrainComputeShaderWrapper(VulkanGpuContext ctx, ReadOnlySpan<byte> spirvNoise)
    {
        _ctx = ctx;
        _vk = ctx.Vk;
        _device = ctx.Device;
        _queue = ctx.Queue;
        _queueFamily = ctx.QueueFamilyIndex;
        _commandPool = ctx.CommandPool;

        fixed (byte* spirvPtr = spirvNoise)
        {
            ShaderModuleCreateInfo sm = new()
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)spirvNoise.Length,
                PCode = (uint*)spirvPtr,
            };

            if (_vk.CreateShaderModule(_device, in sm, null, out _shaderModule) != Result.Success)
                throw new InvalidOperationException("VkCreateShaderModule (noise) failed.");
        }

        DescriptorSetLayoutBinding b0 = new()
        {
            Binding = 0,
            DescriptorType = DescriptorType.StorageBuffer,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.ComputeBit,
        };

        DescriptorSetLayoutBinding b1 = new()
        {
            Binding = 1,
            DescriptorType = DescriptorType.UniformBuffer,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.ComputeBit,
        };

        DescriptorSetLayoutBinding[] bindings = { b0, b1 };
        fixed (DescriptorSetLayoutBinding* pBindings = bindings)
        {
            DescriptorSetLayoutCreateInfo dsl = new()
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = 2,
                PBindings = pBindings,
            };

            if (_vk.CreateDescriptorSetLayout(_device, in dsl, null, out _descriptorLayout) != Result.Success)
                throw new InvalidOperationException("VkCreateDescriptorSetLayout failed.");
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
            throw new InvalidOperationException("VkCreatePipelineLayout failed.");

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
            throw new InvalidOperationException("VkCreateComputePipelines failed.");

        DescriptorPoolSize[] sizes =
        {
            new DescriptorPoolSize { Type = DescriptorType.StorageBuffer, DescriptorCount = 1 },
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
                throw new InvalidOperationException("VkCreateDescriptorPool failed.");
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
            throw new InvalidOperationException("VkAllocateDescriptorSets failed.");

        FenceCreateInfo fi = new() { SType = StructureType.FenceCreateInfo };
        if (_vk.CreateFence(_device, in fi, null, out _fence) != Result.Success)
            throw new InvalidOperationException("VkCreateFence failed.");
    }

    public void DispatchRidgedNoise(float[] heightsDest, int width, int height, in NoiseParamsGpu parameters)
    {
        int count = checked(width * height);
        ulong byteSize = (ulong)(count * sizeof(float));

        if (_heightBuffer.Handle == 0)
        {
            (_heightBuffer, _heightMemory, _heightMapped) = _ctx.CreateHostVisibleBuffer(byteSize, BufferUsageFlags.StorageBufferBit);
            (_uniformBuffer, _uniformMemory, _uniformMapped) = _ctx.CreateHostVisibleBuffer(UniformBufferSize, BufferUsageFlags.UniformBufferBit);

            DescriptorBufferInfo* bufInfos = stackalloc DescriptorBufferInfo[2];
            bufInfos[0] = new DescriptorBufferInfo { Buffer = _heightBuffer, Offset = 0, Range = byteSize };
            bufInfos[1] = new DescriptorBufferInfo { Buffer = _uniformBuffer, Offset = 0, Range = UniformBufferSize };

            WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[2];
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
                DescriptorType = DescriptorType.UniformBuffer,
                PBufferInfo = bufInfos + 1,
            };

            _vk.UpdateDescriptorSets(_device, 2, writes, 0, null);
        }

        *(NoiseParamsGpu*)(void*)_uniformMapped = parameters;

        _vk.ResetFences(_device, 1, ref _fence);

        CommandBufferAllocateInfo cbai = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };

        if (_vk.AllocateCommandBuffers(_device, in cbai, out CommandBuffer cmdBuf) != Result.Success)
            throw new InvalidOperationException("VkAllocateCommandBuffers failed.");

        CommandBufferBeginInfo bi = new() { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        if (_vk.BeginCommandBuffer(cmdBuf, in bi) != Result.Success)
            throw new InvalidOperationException("VkBeginCommandBuffer failed.");

        _vk.CmdBindPipeline(cmdBuf, PipelineBindPoint.Compute, _pipeline);

        DescriptorSet* boundSets = stackalloc DescriptorSet[1];
        boundSets[0] = _descriptorSet;
        _vk.CmdBindDescriptorSets(cmdBuf, PipelineBindPoint.Compute, _pipelineLayout, 0, 1, boundSets, 0, null);

        uint gx = ((uint)count + 255u) / 256u;
        _vk.CmdDispatch(cmdBuf, gx, 1, 1);

        if (_vk.EndCommandBuffer(cmdBuf) != Result.Success)
            throw new InvalidOperationException("VkEndCommandBuffer failed.");

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
            throw new InvalidOperationException("VkQueueSubmit failed.");

        if (_vk.WaitForFences(_device, 1, ref _fence, true, ulong.MaxValue) != Result.Success)
            throw new InvalidOperationException("VkWaitForFences failed.");

        var span = new Span<float>((void*)_heightMapped, count);
        span.CopyTo(heightsDest.AsSpan());

        Span<CommandBuffer> oneCmd = stackalloc CommandBuffer[1];
        oneCmd[0] = cmdBuf;
        _vk.FreeCommandBuffers(_device, _commandPool, oneCmd);
    }

    public void Dispose()
    {
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

        if (_heightMemory.Handle != 0)
        {
            _vk.UnmapMemory(_device, _heightMemory);
            _vk.FreeMemory(_device, _heightMemory, null);
            _heightMemory = default;
            _heightMapped = default;
        }

        if (_heightBuffer.Handle != 0)
        {
            _vk.DestroyBuffer(_device, _heightBuffer, null);
            _heightBuffer = default;
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
