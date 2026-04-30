using System.Diagnostics.CodeAnalysis;
using System.Numerics;

using Silk.NET.Vulkan;
using SixLabors.ImageSharp.PixelFormats;

using VkBuffer = Silk.NET.Vulkan.Buffer;

using WorldBuilder.Domain.Terrain;

namespace WorldBuilder.Gpu;

/// <summary>SPIR-V <c>CalculateBiomes</c> (<c>CalculateBiomes.hlsl</c>): RGBA biome/splat-style grid — A channel is GPU tree-density (Worley × white-noise jitter).</summary>
internal sealed unsafe class BiomeComputeShaderWrapper : IDisposable
{
    private const uint Tg = 8;

    private readonly VulkanGpuContext _ctx;
    private readonly Vk _vk;
    private readonly Device _device;
    private readonly Queue _queue;
    private readonly CommandPool _commandPool;

    private ShaderModule _shaderModule = default;
    private Pipeline _pipeline = default;
    private PipelineLayout _pipelineLayout = default;
    private DescriptorSetLayout _descriptorLayout = default;
    private DescriptorPool _descriptorPool = default;
    private DescriptorSet _descriptorSet = default;

    private VkBuffer _heightBuf = default;
    private VkBuffer _waterBuf = default;
    private VkBuffer _slopeBuf = default;
    private VkBuffer _biomeBuf = default;
    private VkBuffer _flowAccumBuf = default;
    private VkBuffer _uniformBuf = default;

    private DeviceMemory _heightMem = default;
    private DeviceMemory _waterMem = default;
    private DeviceMemory _slopeMem = default;
    private DeviceMemory _biomeMem = default;
    private DeviceMemory _flowAccumMem = default;
    private DeviceMemory _uniformMem = default;

    private nint _heightMapped;
    private nint _waterMapped;
    private nint _slopeMapped;
    private nint _biomeMapped;
    private nint _flowAccumMapped;
    private nint _uniformMapped;

    private Fence _fence = default;

    private int _gridCount;
    private ulong _floatBytes;
    private ulong _biomeBytes;

    private bool _initialized;

    public BiomeComputeShaderWrapper(VulkanGpuContext ctx, ReadOnlySpan<byte> spirvBiome)
    {
        _ctx = ctx;
        _vk = ctx.Vk;
        _device = ctx.Device;
        _queue = ctx.Queue;
        _commandPool = ctx.CommandPool;

        fixed (byte* spirvPtr = spirvBiome)
        {
            ShaderModuleCreateInfo sm = new()
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)spirvBiome.Length,
                PCode = (uint*)spirvPtr,
            };

            if (_vk.CreateShaderModule(_device, in sm, null, out _shaderModule) != Result.Success)
                throw new InvalidOperationException("VkCreateShaderModule (CalculateBiomes) failed.");
        }

        _descriptorLayout = CreateDescriptorSetLayout();
        _pipelineLayout = CreatePipelineLayout(_descriptorLayout);
        _pipeline = CreateComputePipeline(_shaderModule, _pipelineLayout);

        DescriptorPoolSize[] poolSizes =
        {
            new DescriptorPoolSize { Type = DescriptorType.StorageBuffer, DescriptorCount = 8 },
            new DescriptorPoolSize { Type = DescriptorType.UniformBuffer, DescriptorCount = 2 },
        };

        fixed (DescriptorPoolSize* ps = poolSizes)
        {
            DescriptorPoolCreateInfo dpi = new()
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                MaxSets = 2,
                PoolSizeCount = 2,
                PPoolSizes = ps,
            };

            if (_vk.CreateDescriptorPool(_device, in dpi, null, out _descriptorPool) != Result.Success)
                throw new InvalidOperationException("VkCreateDescriptorPool (biome) failed.");
        }

        _descriptorSet = AllocateDescriptorSet(_descriptorPool, _descriptorLayout);

        ulong uSz = BiomeParamsGpuMarshal.UniformByteCount;
        (_uniformBuf, _uniformMem, _uniformMapped) =
            _ctx.CreateHostVisibleBuffer(uSz, BufferUsageFlags.UniformBufferBit);

        FenceCreateInfo fi = new() { SType = StructureType.FenceCreateInfo };
        if (_vk.CreateFence(_device, in fi, null, out _fence) != Result.Success)
            throw new InvalidOperationException("VkCreateFence (biome) failed.");

        _initialized = true;
    }

    /// <returns>RGBA8 PNG, row-major WxH texels aligned with height/water grids (<c>W</c> = tree-density splat).</returns>
    public bool TryDispatchAndEncodePng(
        ReadOnlySpan<float> heightNormRowMajor,
        ReadOnlySpan<float> waterDepthRowMajor,
        ReadOnlySpan<float> slopeDegRowMajor,
        ReadOnlySpan<float> hydraulicFlowIntegratedRowMajor,
        int width,
        int height,
        in BiomeParamsGpu parameters,
        [NotNullWhen(true)] out byte[]? rgbaPng)
    {
        rgbaPng = null;
        int count = checked(width * height);
        if (width < 4 || height < 4 ||
            heightNormRowMajor.Length != count ||
            waterDepthRowMajor.Length != count ||
            slopeDegRowMajor.Length != count ||
            hydraulicFlowIntegratedRowMajor.Length != count)
            return false;

        EnsureBuffers(count);

        heightNormRowMajor.CopyTo(new Span<float>((void*)_heightMapped, count));
        waterDepthRowMajor.CopyTo(new Span<float>((void*)_waterMapped, count));
        slopeDegRowMajor.CopyTo(new Span<float>((void*)_slopeMapped, count));
        hydraulicFlowIntegratedRowMajor.CopyTo(new Span<float>((void*)_flowAccumMapped, count));

        BiomeParamsGpuMarshal.WriteUniform(_uniformMapped, parameters);

        _vk.ResetFences(_device, 1, ref _fence);

        CommandBufferAllocateInfo cbai = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };

        if (_vk.AllocateCommandBuffers(_device, in cbai, out CommandBuffer cmd) != Result.Success)
            throw new InvalidOperationException("VkAllocateCommandBuffers (biome) failed.");

        CommandBufferBeginInfo bi =
            new() { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        if (_vk.BeginCommandBuffer(cmd, in bi) != Result.Success)
            throw new InvalidOperationException("VkBeginCommandBuffer (biome) failed.");

        _vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipeline);

        DescriptorSet* setPtr = stackalloc DescriptorSet[1];
        setPtr[0] = _descriptorSet;
        _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _pipelineLayout, 0, 1, setPtr, 0, null);

        uint gx = (uint)Math.Max(1, (width + (int)Tg - 1) / (int)Tg);
        uint gy = (uint)Math.Max(1, (height + (int)Tg - 1) / (int)Tg);

        _vk.CmdDispatch(cmd, gx, gy, 1);

        if (_vk.EndCommandBuffer(cmd) != Result.Success)
            throw new InvalidOperationException("VkEndCommandBuffer (biome) failed.");

        CommandBuffer* pCmdSubmit = stackalloc CommandBuffer[1];
        pCmdSubmit[0] = cmd;

        SubmitInfo si = new()
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = pCmdSubmit,
        };

        if (_vk.QueueSubmit(_queue, 1, ref si, _fence) != Result.Success)
            throw new InvalidOperationException("VkQueueSubmit (biome) failed.");

        if (_vk.WaitForFences(_device, 1, ref _fence, true, ulong.MaxValue) != Result.Success)
            throw new InvalidOperationException("VkWaitForFences (biome) failed.");

        Span<CommandBuffer> oneCb = stackalloc CommandBuffer[] { cmd };
        _vk.FreeCommandBuffers(_device, _commandPool, oneCb);

        var bio = new Span<Vector4>((void*)_biomeMapped, count);
        var rgba = new Rgba32[count];
        for (int i = 0; i < count; i++)
        {
            Vector4 v = bio[i];
            rgba[i] = new Rgba32(
                QuantizeChan(v.X),
                QuantizeChan(v.Y),
                QuantizeChan(v.Z),
                QuantizeChan(v.W));
        }

        rgbaPng = TerrainRgba8Png.Encode(rgba, width, height);
        return true;
    }

    private static byte QuantizeChan(float c)
    {
        float t = Math.Clamp(c, 0f, 1f);
        return (byte)Math.Clamp(Math.Round((double)t * 255.0), 0d, 255d);
    }

    private void EnsureBuffers(int count)
    {
        if (_gridCount == count && _heightBuf.Handle != 0)
            return;

        DestroyDataBuffersOnly();

        _gridCount = count;
        _floatBytes = (ulong)(count * sizeof(float));
        _biomeBytes = (ulong)(count * sizeof(Vector4));

        BufferUsageFlags bufUse = BufferUsageFlags.StorageBufferBit;

        (_heightBuf, _heightMem, _heightMapped) = _ctx.CreateHostVisibleBuffer(_floatBytes, bufUse);
        (_waterBuf, _waterMem, _waterMapped) = _ctx.CreateHostVisibleBuffer(_floatBytes, bufUse);
        (_slopeBuf, _slopeMem, _slopeMapped) = _ctx.CreateHostVisibleBuffer(_floatBytes, bufUse);
        (_biomeBuf, _biomeMem, _biomeMapped) = _ctx.CreateHostVisibleBuffer(_biomeBytes, bufUse);
        (_flowAccumBuf, _flowAccumMem, _flowAccumMapped) = _ctx.CreateHostVisibleBuffer(_floatBytes, bufUse);

        ulong uni = BiomeParamsGpuMarshal.UniformByteCount;
        DescriptorBufferInfo* infos = stackalloc DescriptorBufferInfo[6];
        infos[0] = new DescriptorBufferInfo { Buffer = _heightBuf, Offset = 0, Range = _floatBytes };
        infos[1] = new DescriptorBufferInfo { Buffer = _waterBuf, Offset = 0, Range = _floatBytes };
        infos[2] = new DescriptorBufferInfo { Buffer = _slopeBuf, Offset = 0, Range = _floatBytes };
        infos[3] = new DescriptorBufferInfo { Buffer = _biomeBuf, Offset = 0, Range = _biomeBytes };
        infos[4] = new DescriptorBufferInfo { Buffer = _flowAccumBuf, Offset = 0, Range = _floatBytes };
        infos[5] = new DescriptorBufferInfo { Buffer = _uniformBuf, Offset = 0, Range = uni };

        WriteDescriptorSet* wr = stackalloc WriteDescriptorSet[6];
        for (uint b = 0; b < 6; b++)
        {
            wr[b] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _descriptorSet,
                DstBinding = b,
                DescriptorCount = 1,
                DescriptorType =
                    b == 5 ? DescriptorType.UniformBuffer : DescriptorType.StorageBuffer,
                PBufferInfo = infos + b,
            };
        }

        _vk.UpdateDescriptorSets(_device, 6, wr, 0, null);
    }

    private void DestroyDataBuffersOnly()
    {
        SafeDestroyBuf(ref _heightBuf, ref _heightMem, ref _heightMapped);
        SafeDestroyBuf(ref _waterBuf, ref _waterMem, ref _waterMapped);
        SafeDestroyBuf(ref _slopeBuf, ref _slopeMem, ref _slopeMapped);
        SafeDestroyBuf(ref _biomeBuf, ref _biomeMem, ref _biomeMapped);
        SafeDestroyBuf(ref _flowAccumBuf, ref _flowAccumMem, ref _flowAccumMapped);
        _gridCount = 0;
        _floatBytes = 0;
        _biomeBytes = 0;
    }

    private void SafeDestroyBuf(ref VkBuffer b, ref DeviceMemory mem, ref nint mapped)
    {
        if (mem.Handle != 0 && mapped != 0)
        {
            _vk.UnmapMemory(_device, mem);
            mapped = 0;
        }

        if (mem.Handle != 0)
        {
            _vk.FreeMemory(_device, mem, null);
            mem = default;
        }

        if (b.Handle != 0)
        {
            _vk.DestroyBuffer(_device, b, null);
            b = default;
        }
    }

    private DescriptorSet AllocateDescriptorSet(DescriptorPool pool, DescriptorSetLayout dsl)
    {
        DescriptorSetLayout* pLay = stackalloc DescriptorSetLayout[1];
        pLay[0] = dsl;

        DescriptorSetAllocateInfo dai = new()
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = pool,
            DescriptorSetCount = 1,
            PSetLayouts = pLay,
        };

        if (_vk.AllocateDescriptorSets(_device, in dai, out var ds) != Result.Success)
            throw new InvalidOperationException("VkAllocateDescriptorSets (biome)");

        return ds;
    }

    private DescriptorSetLayout CreateDescriptorSetLayout()
    {
        DescriptorSetLayoutBinding[] binds =
        {
            new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },
            new DescriptorSetLayoutBinding { Binding = 1, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },
            new DescriptorSetLayoutBinding { Binding = 2, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },
            new DescriptorSetLayoutBinding { Binding = 3, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },
            new DescriptorSetLayoutBinding { Binding = 4, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },
            new DescriptorSetLayoutBinding { Binding = 5, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },
        };

        fixed (DescriptorSetLayoutBinding* p = binds)
        {
            DescriptorSetLayoutCreateInfo info = new()
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = (uint)binds.Length,
                PBindings = p,
            };

            if (_vk.CreateDescriptorSetLayout(_device, in info, null, out var layout) != Result.Success)
                throw new InvalidOperationException("Biome descriptor layout failed.");

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

        if (_vk.CreatePipelineLayout(_device, in pl, null, out var pipeLayout) != Result.Success)
            throw new InvalidOperationException("VkCreatePipelineLayout (biome)");

        return pipeLayout;
    }

    private Pipeline CreateComputePipeline(ShaderModule module, PipelineLayout pipeLayout)
    {
        byte* entryBytes = stackalloc byte[96];
        ReadOnlySpan<byte> nameBytes = "CalculateBiomes\0"u8;
        nameBytes.CopyTo(new Span<byte>(entryBytes, nameBytes.Length));

        PipelineShaderStageCreateInfo stage = new()
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.ComputeBit,
            Module = module,
            PName = entryBytes,
        };

        ComputePipelineCreateInfo cp = new()
        {
            SType = StructureType.ComputePipelineCreateInfo,
            Stage = stage,
            Layout = pipeLayout,
        };

        if (_vk.CreateComputePipelines(_device, default, 1, ref cp, null, out var pipe) != Result.Success)
            throw new InvalidOperationException("VkCreateComputePipelines (biome)");

        return pipe;
    }

    public void Dispose()
    {
        if (!_initialized)
            return;

        DestroyDataBuffersOnly();

        if (_fence.Handle != 0)
        {
            _vk.DestroyFence(_device, _fence, null);
            _fence = default;
        }

        if (_uniformBuf.Handle != 0)
            SafeDestroyBuf(ref _uniformBuf, ref _uniformMem, ref _uniformMapped);

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

        _initialized = false;
        GC.SuppressFinalize(this);
    }
}
