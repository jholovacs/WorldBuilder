using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using WorldBuilder.Domain.Terrain;

namespace WorldBuilder.Gpu;

/// <summary>Two-pass shallow water (flux + mass/momentum update) on Vulkan.</summary>
internal sealed unsafe class ShallowWaterComputeShaderWrapper : IDisposable
{
    private const uint Tg = 8;

    private readonly VulkanGpuContext _ctx;
    private readonly Vk _vk;
    private readonly Device _device;
    private readonly Queue _queue;
    private readonly CommandPool _commandPool;

    private ShaderModule _fluxModule;
    private ShaderModule _updateModule;

    private Pipeline _fluxPipeline;
    private Pipeline _updatePipeline;

    private PipelineLayout _fluxPipelineLayout;
    private PipelineLayout _updatePipelineLayout;

    private DescriptorSetLayout _fluxDescriptorLayout;
    private DescriptorSetLayout _updateDescriptorLayout;

    private DescriptorPool _descriptorPool;

    private DescriptorSet _fluxSetA;
    private DescriptorSet _fluxSetB;
    private DescriptorSet _updateSetAB;
    private DescriptorSet _updateSetBA;

    private int _gridCount;
    private ulong _floatBytes;
    private ulong _waterBytes;
    private ulong _fluxBytes;

    private VkBuffer _terrainBuffer;
    private DeviceMemory _terrainMemory;
    private nint _terrainMapped;

    private VkBuffer _waterA;
    private DeviceMemory _waterAMemory;
    private nint _waterAMapped;

    private VkBuffer _waterB;
    private DeviceMemory _waterBMemory;
    private nint _waterBMapped;

    private VkBuffer _fluxBuffer;
    private DeviceMemory _fluxMemory;
    private nint _fluxMapped;

    private VkBuffer _uniformBuffer;
    private DeviceMemory _uniformMemory;
    private nint _uniformMapped;

    private Fence _fence;

    private bool _initialized;

    public ShallowWaterComputeShaderWrapper(VulkanGpuContext ctx, ReadOnlySpan<byte> spirvFlux, ReadOnlySpan<byte> spirvUpdate)
    {
        _ctx = ctx;
        _vk = ctx.Vk;
        _device = ctx.Device;
        _queue = ctx.Queue;
        _commandPool = ctx.CommandPool;

        fixed (byte* p = spirvFlux)
        {
            ShaderModuleCreateInfo sm = new()
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)spirvFlux.Length,
                PCode = (uint*)p,
            };
            if (_vk.CreateShaderModule(_device, in sm, null, out _fluxModule) != Result.Success)
                throw new InvalidOperationException("VkCreateShaderModule (shallow water flux) failed.");
        }

        fixed (byte* p2 = spirvUpdate)
        {
            ShaderModuleCreateInfo sm = new()
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)spirvUpdate.Length,
                PCode = (uint*)p2,
            };
            if (_vk.CreateShaderModule(_device, in sm, null, out _updateModule) != Result.Success)
                throw new InvalidOperationException("VkCreateShaderModule (shallow water update) failed.");
        }

        _fluxDescriptorLayout = CreateFluxDescriptorLayout();
        _updateDescriptorLayout = CreateUpdateDescriptorLayout();

        _fluxPipelineLayout = CreatePipelineLayout(_fluxDescriptorLayout);
        _updatePipelineLayout = CreatePipelineLayout(_updateDescriptorLayout);

        _fluxPipeline = CreateComputePipeline(_fluxModule, _fluxPipelineLayout);
        _updatePipeline = CreateComputePipeline(_updateModule, _updatePipelineLayout);

        DescriptorPoolSize[] poolSizes =
        {
            new DescriptorPoolSize { Type = DescriptorType.StorageBuffer, DescriptorCount = 32 },
            new DescriptorPoolSize { Type = DescriptorType.UniformBuffer, DescriptorCount = 8 },
        };

        fixed (DescriptorPoolSize* ps = poolSizes)
        {
            DescriptorPoolCreateInfo dpi = new()
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                MaxSets = 8,
                PoolSizeCount = 2,
                PPoolSizes = ps,
            };

            if (_vk.CreateDescriptorPool(_device, in dpi, null, out _descriptorPool) != Result.Success)
                throw new InvalidOperationException("VkCreateDescriptorPool (shallow water) failed.");
        }

        _fluxSetA = AllocateDescriptorSet(_fluxDescriptorLayout);
        _fluxSetB = AllocateDescriptorSet(_fluxDescriptorLayout);
        _updateSetAB = AllocateDescriptorSet(_updateDescriptorLayout);
        _updateSetBA = AllocateDescriptorSet(_updateDescriptorLayout);

        (_uniformBuffer, _uniformMemory, _uniformMapped) =
            _ctx.CreateHostVisibleBuffer(ShallowWaterGpu.UniformByteCount, BufferUsageFlags.UniformBufferBit);

        FenceCreateInfo fi = new() { SType = StructureType.FenceCreateInfo };
        if (_vk.CreateFence(_device, in fi, null, out _fence) != Result.Success)
            throw new InvalidOperationException("VkCreateFence (shallow water) failed.");

        _initialized = true;
    }

    /// <param name="waterDepthOutScratch">Filled with simulated water thickness per cell (<c>x</c> channel) — length must equal width×height.</param>
    /// <param name="integratedFlowOutScratch">When non-null, filled with hydraulic-style integrated flow accumulator (<c>W</c> channel) for biome/tree moisture.</param>
    public bool TrySimulateFlowMapPng(
        ReadOnlySpan<float> terrainNormalized,
        int width,
        int height,
        in ShallowWaterParamsGpu parameters,
        float initialWaterDepth,
        int iterations,
        float[]? waterDepthOutScratch,
        float[]? integratedFlowOutScratch,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out byte[]? pngBytes)
    {
        pngBytes = null;
        int count = checked(width * height);
        if (terrainNormalized.Length != count || width < 8 || height < 8)
            return false;

        iterations = Math.Clamp(iterations, 1, 2000);
        float h0 = Math.Clamp(initialWaterDepth, 1e-5f, 0.4f);

        EnsureGridCapacity(count);
        terrainNormalized.CopyTo(new Span<float>((void*)_terrainMapped, count));

        var wa = new Span<System.Numerics.Vector4>((void*)_waterAMapped, count);
        for (int i = 0; i < count; i++)
            wa[i] = new System.Numerics.Vector4(h0, 0f, 0f, 0f);
        _ = _waterBMapped;
        System.Runtime.CompilerServices.Unsafe.InitBlock((void*)_waterBMapped, 0, (uint)_waterBytes);
        System.Runtime.CompilerServices.Unsafe.InitBlock((void*)_fluxMapped, 0, (uint)_fluxBytes);

        ShallowWaterGpu.WriteUniform(_uniformMapped, in parameters);

        _vk.ResetFences(_device, 1, ref _fence);

        CommandBufferAllocateInfo cbai = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };

        if (_vk.AllocateCommandBuffers(_device, in cbai, out CommandBuffer cmd) != Result.Success)
            throw new InvalidOperationException("VkAllocateCommandBuffers (shallow water) failed.");

        CommandBufferBeginInfo bi = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };

        if (_vk.BeginCommandBuffer(cmd, in bi) != Result.Success)
            throw new InvalidOperationException("VkBeginCommandBuffer (shallow water) failed.");

        uint gx = (uint)Math.Max(1, (width + (int)Tg - 1) / (int)Tg);
        uint gy = (uint)Math.Max(1, (height + (int)Tg - 1) / (int)Tg);

        DescriptorSet* fluxSetPtr = stackalloc DescriptorSet[1];
        DescriptorSet* updateSetPtr = stackalloc DescriptorSet[1];

        BufferMemoryBarrier* fluxBar = stackalloc BufferMemoryBarrier[1];
        BufferMemoryBarrier* waterFluxPair = stackalloc BufferMemoryBarrier[2];

        for (int it = 0; it < iterations; it++)
        {
            bool readA = (it & 1) == 0;
            DescriptorSet fluxSet = readA ? _fluxSetA : _fluxSetB;
            DescriptorSet updateSet = readA ? _updateSetAB : _updateSetBA;

            _vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _fluxPipeline);
            fluxSetPtr[0] = fluxSet;
            _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _fluxPipelineLayout, 0, 1, fluxSetPtr, 0, null);
            _vk.CmdDispatch(cmd, gx, gy, 1);

            fluxBar[0] = new BufferMemoryBarrier
            {
                SType = StructureType.BufferMemoryBarrier,
                SrcAccessMask = AccessFlags.ShaderWriteBit,
                DstAccessMask = AccessFlags.ShaderReadBit,
                Buffer = _fluxBuffer,
                Offset = 0,
                Size = Vk.WholeSize,
            };

            _vk.CmdPipelineBarrier(
                cmd,
                PipelineStageFlags.ComputeShaderBit,
                PipelineStageFlags.ComputeShaderBit,
                0,
                0,
                null,
                1,
                fluxBar,
                0,
                null);

            _vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _updatePipeline);
            updateSetPtr[0] = updateSet;
            _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _updatePipelineLayout, 0, 1, updateSetPtr, 0, null);
            _vk.CmdDispatch(cmd, gx, gy, 1);

            VkBuffer waterWriteVk = readA ? _waterB : _waterA;

            waterFluxPair[0] = new BufferMemoryBarrier
            {
                SType = StructureType.BufferMemoryBarrier,
                SrcAccessMask = AccessFlags.ShaderWriteBit,
                DstAccessMask = AccessFlags.ShaderReadBit,
                Buffer = waterWriteVk,
                Offset = 0,
                Size = Vk.WholeSize,
            };

            waterFluxPair[1] = new BufferMemoryBarrier
            {
                SType = StructureType.BufferMemoryBarrier,
                SrcAccessMask = AccessFlags.ShaderReadBit,
                DstAccessMask = AccessFlags.ShaderWriteBit,
                Buffer = _fluxBuffer,
                Offset = 0,
                Size = Vk.WholeSize,
            };

            _vk.CmdPipelineBarrier(
                cmd,
                PipelineStageFlags.ComputeShaderBit,
                PipelineStageFlags.ComputeShaderBit,
                0,
                0,
                null,
                2,
                waterFluxPair,
                0,
                null);
        }

        if (_vk.EndCommandBuffer(cmd) != Result.Success)
            throw new InvalidOperationException("VkEndCommandBuffer (shallow water) failed.");

        CommandBuffer* pCmd = stackalloc CommandBuffer[1];
        pCmd[0] = cmd;
        SubmitInfo si = new()
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = pCmd,
        };

        if (_vk.QueueSubmit(_queue, 1, ref si, _fence) != Result.Success)
            throw new InvalidOperationException("VkQueueSubmit (shallow water) failed.");

        if (_vk.WaitForFences(_device, 1, ref _fence, true, ulong.MaxValue) != Result.Success)
            throw new InvalidOperationException("VkWaitForFences (shallow water) failed.");

        Span<CommandBuffer> oneCmd = stackalloc CommandBuffer[] { cmd };
        _vk.FreeCommandBuffers(_device, _commandPool, oneCmd);

        bool finalOnA = (iterations % 2) == 0;
        nint flowMapMapped = finalOnA ? _waterAMapped : _waterBMapped;
        var wf = new Span<System.Numerics.Vector4>((void*)flowMapMapped, count);
        if (waterDepthOutScratch is not null)
        {
            if (waterDepthOutScratch.Length != count)
                throw new ArgumentException("waterDepthOutScratch.Length must equal width × height.", nameof(waterDepthOutScratch));

            for (int i = 0; i < count; i++)
                waterDepthOutScratch[i] = wf[i].X;
        }

        if (integratedFlowOutScratch is not null)
        {
            if (integratedFlowOutScratch.Length != count)
                throw new ArgumentException("integratedFlowOutScratch.Length must equal width × height.", nameof(integratedFlowOutScratch));

            for (int i = 0; i < count; i++)
                integratedFlowOutScratch[i] = wf[i].W;
        }

        var accumulation = new float[count];
        for (int i = 0; i < count; i++)
            accumulation[i] = wf[i].W;

        pngBytes = TerrainHeightmapPngEncoder.EncodeGrayscale16Png(accumulation.AsSpan(), width, height);
        return true;
    }

    private DescriptorSetLayout CreateFluxDescriptorLayout()
    {
        DescriptorSetLayoutBinding[] bindings =
        {
            new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },
            new DescriptorSetLayoutBinding { Binding = 1, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },
            new DescriptorSetLayoutBinding { Binding = 2, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },
            new DescriptorSetLayoutBinding { Binding = 3, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },
        };

        fixed (DescriptorSetLayoutBinding* p = bindings)
        {
            DescriptorSetLayoutCreateInfo info = new()
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = (uint)bindings.Length,
                PBindings = p,
            };

            if (_vk.CreateDescriptorSetLayout(_device, in info, null, out var layout) != Result.Success)
                throw new InvalidOperationException("Flux descriptor layout failed.");
            return layout;
        }
    }

    private DescriptorSetLayout CreateUpdateDescriptorLayout()
    {
        DescriptorSetLayoutBinding[] bindings =
        {
            new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },
            new DescriptorSetLayoutBinding { Binding = 1, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },
            new DescriptorSetLayoutBinding { Binding = 2, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },
            new DescriptorSetLayoutBinding { Binding = 3, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },
            new DescriptorSetLayoutBinding { Binding = 4, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },
        };

        fixed (DescriptorSetLayoutBinding* p = bindings)
        {
            DescriptorSetLayoutCreateInfo info = new()
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = (uint)bindings.Length,
                PBindings = p,
            };

            if (_vk.CreateDescriptorSetLayout(_device, in info, null, out var layout) != Result.Success)
                throw new InvalidOperationException("Update descriptor layout failed.");
            return layout;
        }
    }

    private PipelineLayout CreatePipelineLayout(DescriptorSetLayout dsl)
    {
        DescriptorSetLayout* p = stackalloc DescriptorSetLayout[1];
        p[0] = dsl;
        PipelineLayoutCreateInfo pl = new()
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = p,
        };

        if (_vk.CreatePipelineLayout(_device, in pl, null, out var layout) != Result.Success)
            throw new InvalidOperationException("Pipeline layout (shallow water) failed.");

        return layout;
    }

    private Pipeline CreateComputePipeline(ShaderModule module, PipelineLayout pipeLayout)
    {
        byte* entryName = stackalloc byte[64];
        ReadOnlySpan<byte> mainUtf8 = "main"u8;
        mainUtf8.CopyTo(new Span<byte>(entryName, mainUtf8.Length));
        entryName[mainUtf8.Length] = 0;

        PipelineShaderStageCreateInfo stage = new()
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.ComputeBit,
            Module = module,
            PName = entryName,
        };

        ComputePipelineCreateInfo cp = new()
        {
            SType = StructureType.ComputePipelineCreateInfo,
            Stage = stage,
            Layout = pipeLayout,
        };

        if (_vk.CreateComputePipelines(_device, default, 1, ref cp, null, out var pipe) != Result.Success)
            throw new InvalidOperationException("VkCreateComputePipelines (shallow water) failed.");

        return pipe;
    }

    private DescriptorSet AllocateDescriptorSet(DescriptorSetLayout dsl)
    {
        DescriptorSetLayout* pLayout = stackalloc DescriptorSetLayout[1];
        pLayout[0] = dsl;

        DescriptorSetAllocateInfo dai = new()
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = pLayout,
        };

        if (_vk.AllocateDescriptorSets(_device, in dai, out var ds) != Result.Success)
            throw new InvalidOperationException("VkAllocateDescriptorSets shallow water.");

        return ds;
    }

    private void EnsureGridCapacity(int count)
    {
        if (_gridCount == count && _terrainBuffer.Handle != 0)
            return;

        DestroyGridBuffers();

        _gridCount = count;
        _floatBytes = (ulong)(count * sizeof(float));
        _waterBytes = (ulong)(count * sizeof(System.Numerics.Vector4));
        _fluxBytes = _waterBytes;

        BufferUsageFlags st = BufferUsageFlags.StorageBufferBit;

        (_terrainBuffer, _terrainMemory, _terrainMapped) = _ctx.CreateHostVisibleBuffer(_floatBytes, st);
        (_waterA, _waterAMemory, _waterAMapped) = _ctx.CreateHostVisibleBuffer(_waterBytes, st);
        (_waterB, _waterBMemory, _waterBMapped) = _ctx.CreateHostVisibleBuffer(_waterBytes, st);
        (_fluxBuffer, _fluxMemory, _fluxMapped) = _ctx.CreateHostVisibleBuffer(_fluxBytes, st);

        ulong uByte = ShallowWaterGpu.UniformByteCount;

        DescriptorBufferInfo* infos = stackalloc DescriptorBufferInfo[6];
        infos[0] = new DescriptorBufferInfo { Buffer = _terrainBuffer, Offset = 0, Range = _floatBytes };
        infos[1] = new DescriptorBufferInfo { Buffer = _waterA, Offset = 0, Range = _waterBytes };
        infos[2] = new DescriptorBufferInfo { Buffer = _waterB, Offset = 0, Range = _waterBytes };
        infos[3] = new DescriptorBufferInfo { Buffer = _fluxBuffer, Offset = 0, Range = _fluxBytes };
        infos[4] = new DescriptorBufferInfo { Buffer = _uniformBuffer, Offset = 0, Range = uByte };

        void WriteFlux(DescriptorSet set, VkBuffer waterRead)
        {
            infos[1] = new DescriptorBufferInfo { Buffer = waterRead, Offset = 0, Range = _waterBytes };
            WriteDescriptorSet* w = stackalloc WriteDescriptorSet[4];
            w[0] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 0, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = infos };
            w[1] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 1, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = infos + 1 };
            w[2] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 2, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = infos + 3 };
            w[3] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 3, DescriptorCount = 1, DescriptorType = DescriptorType.UniformBuffer, PBufferInfo = infos + 4 };
            _vk.UpdateDescriptorSets(_device, 4, w, 0, null);
        }

        WriteFlux(_fluxSetA, _waterA);
        WriteFlux(_fluxSetB, _waterB);

        void WriteUpdate(DescriptorSet set, VkBuffer read, VkBuffer write)
        {
            infos[1] = new DescriptorBufferInfo { Buffer = read, Offset = 0, Range = _waterBytes };
            infos[5] = new DescriptorBufferInfo { Buffer = write, Offset = 0, Range = _waterBytes };

            WriteDescriptorSet* w = stackalloc WriteDescriptorSet[5];
            w[0] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 0, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = infos };
            w[1] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 1, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = infos + 1 };
            w[2] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 2, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = infos + 3 };
            w[3] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 3, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = infos + 5 };
            w[4] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 4, DescriptorCount = 1, DescriptorType = DescriptorType.UniformBuffer, PBufferInfo = infos + 4 };
            _vk.UpdateDescriptorSets(_device, 5, w, 0, null);
        }

        WriteUpdate(_updateSetAB, _waterA, _waterB);
        WriteUpdate(_updateSetBA, _waterB, _waterA);
    }

    private void DestroyGridBuffers()
    {
        SafeFree(ref _terrainBuffer, ref _terrainMemory, ref _terrainMapped);
        SafeFree(ref _waterA, ref _waterAMemory, ref _waterAMapped);
        SafeFree(ref _waterB, ref _waterBMemory, ref _waterBMapped);
        SafeFree(ref _fluxBuffer, ref _fluxMemory, ref _fluxMapped);
        _gridCount = 0;
        _floatBytes = 0;
        _waterBytes = 0;
        _fluxBytes = 0;
    }

    private void SafeFree(ref VkBuffer buf, ref DeviceMemory mem, ref nint mapped)
    {
        if (mem.Handle != 0)
        {
            if (mapped != 0)
            {
                _vk.UnmapMemory(_device, mem);
                mapped = 0;
            }

            _vk.FreeMemory(_device, mem, null);
            mem = default;
        }

        if (buf.Handle != 0)
        {
            _vk.DestroyBuffer(_device, buf, null);
            buf = default;
        }
    }

    public void Dispose()
    {
        if (!_initialized)
            return;

        DestroyGridBuffers();

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
            _uniformMapped = 0;
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

        if (_updatePipeline.Handle != 0)
        {
            _vk.DestroyPipeline(_device, _updatePipeline, null);
            _updatePipeline = default;
        }

        if (_fluxPipeline.Handle != 0)
        {
            _vk.DestroyPipeline(_device, _fluxPipeline, null);
            _fluxPipeline = default;
        }

        if (_updatePipelineLayout.Handle != 0)
        {
            _vk.DestroyPipelineLayout(_device, _updatePipelineLayout, null);
            _updatePipelineLayout = default;
        }

        if (_fluxPipelineLayout.Handle != 0)
        {
            _vk.DestroyPipelineLayout(_device, _fluxPipelineLayout, null);
            _fluxPipelineLayout = default;
        }

        if (_updateDescriptorLayout.Handle != 0)
        {
            _vk.DestroyDescriptorSetLayout(_device, _updateDescriptorLayout, null);
            _updateDescriptorLayout = default;
        }

        if (_fluxDescriptorLayout.Handle != 0)
        {
            _vk.DestroyDescriptorSetLayout(_device, _fluxDescriptorLayout, null);
            _fluxDescriptorLayout = default;
        }

        if (_updateModule.Handle != 0)
        {
            _vk.DestroyShaderModule(_device, _updateModule, null);
            _updateModule = default;
        }

        if (_fluxModule.Handle != 0)
        {
            _vk.DestroyShaderModule(_device, _fluxModule, null);
            _fluxModule = default;
        }

        GC.SuppressFinalize(this);
    }
}
