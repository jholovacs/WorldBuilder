using System.Reflection;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace WorldBuilder.Gpu;

/// <summary>
/// Minimal Vulkan device + compute queue + command pool for terrain kernels (SPIR-V from DXC).
/// </summary>
internal sealed unsafe class VulkanGpuContext : IDisposable
{
    public readonly Vk Vk;
    public readonly Instance Instance;
    public readonly PhysicalDevice PhysicalDevice;
    public readonly Device Device;
    public readonly Queue Queue;
    public readonly uint QueueFamilyIndex;
    public readonly CommandPool CommandPool;

    public VulkanGpuContext()
    {
        Vk = Vk.GetApi();

        ApplicationInfo* pApp = stackalloc ApplicationInfo[1];
        pApp[0] = new ApplicationInfo
        {
            SType = StructureType.ApplicationInfo,
            ApiVersion = Vk.Version12,
        };

        InstanceCreateInfo createInfo = new()
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = pApp,
        };

        if (Vk.CreateInstance(in createInfo, null, out Instance) != Result.Success)
            throw new InvalidOperationException("VkCreateInstance failed.");

        uint deviceCount = 0;
        Vk.EnumeratePhysicalDevices(Instance, &deviceCount, null);
        if (deviceCount == 0)
            throw new InvalidOperationException("No Vulkan physical devices.");

        var physicalDevices = stackalloc PhysicalDevice[(int)deviceCount];
        Vk.EnumeratePhysicalDevices(Instance, &deviceCount, physicalDevices);
        PhysicalDevice = physicalDevices[0];

        uint queueFamilyCount = 0;
        Vk.GetPhysicalDeviceQueueFamilyProperties(PhysicalDevice, &queueFamilyCount, null);
        var queueFamilies = new QueueFamilyProperties[queueFamilyCount];
        fixed (QueueFamilyProperties* qfp = queueFamilies)
            Vk.GetPhysicalDeviceQueueFamilyProperties(PhysicalDevice, &queueFamilyCount, qfp);

        QueueFamilyIndex = uint.MaxValue;
        for (uint i = 0; i < queueFamilyCount; i++)
        {
            if ((queueFamilies[i].QueueFlags & QueueFlags.ComputeBit) != 0)
            {
                QueueFamilyIndex = i;
                break;
            }
        }

        if (QueueFamilyIndex == uint.MaxValue)
            throw new InvalidOperationException("No Vulkan compute queue family.");

        float* priorities = stackalloc float[1];
        priorities[0] = 1f;

        DeviceQueueCreateInfo* pQueueCreate = stackalloc DeviceQueueCreateInfo[1];
        pQueueCreate[0] = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = QueueFamilyIndex,
            QueueCount = 1,
            PQueuePriorities = priorities,
        };

        DeviceCreateInfo deviceCreateInfo = new()
        {
            SType = StructureType.DeviceCreateInfo,
            QueueCreateInfoCount = 1,
            PQueueCreateInfos = pQueueCreate,
        };

        if (Vk.CreateDevice(PhysicalDevice, in deviceCreateInfo, null, out Device) != Result.Success)
            throw new InvalidOperationException("VkCreateDevice failed.");

        Vk.GetDeviceQueue(Device, QueueFamilyIndex, 0, out Queue);

        CommandPoolCreateInfo poolInfo = new()
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            QueueFamilyIndex = QueueFamilyIndex,
        };

        if (Vk.CreateCommandPool(Device, in poolInfo, null, out CommandPool) != Result.Success)
            throw new InvalidOperationException("VkCreateCommandPool failed.");
    }

    public uint FindMemoryType(uint typeFilter, MemoryPropertyFlags properties)
    {
        Vk.GetPhysicalDeviceMemoryProperties(PhysicalDevice, out var memProps);
        for (uint i = 0; i < memProps.MemoryTypeCount; i++)
        {
            if ((typeFilter & (1u << (int)i)) != 0
                && (memProps.MemoryTypes[(int)i].PropertyFlags & properties) == properties)
                return i;
        }

        throw new InvalidOperationException("Failed to find suitable memory type.");
    }

    public (VkBuffer Buffer, DeviceMemory Memory, nint Mapped) CreateHostVisibleBuffer(ulong size, BufferUsageFlags usage)
    {
        BufferCreateInfo bufferInfo = new()
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
        };

        if (Vk.CreateBuffer(Device, in bufferInfo, null, out VkBuffer buffer) != Result.Success)
            throw new InvalidOperationException("VkCreateBuffer failed.");

        Vk.GetBufferMemoryRequirements(Device, buffer, out var req);

        MemoryAllocateInfo allocInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = req.Size,
            MemoryTypeIndex = FindMemoryType(req.MemoryTypeBits, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit),
        };

        if (Vk.AllocateMemory(Device, in allocInfo, null, out DeviceMemory memory) != Result.Success)
            throw new InvalidOperationException("VkAllocateMemory failed.");

        if (Vk.BindBufferMemory(Device, buffer, memory, 0) != Result.Success)
            throw new InvalidOperationException("VkBindBufferMemory failed.");

        void* mapped = null;
        if (Vk.MapMemory(Device, memory, 0, size, 0, &mapped) != Result.Success)
            throw new InvalidOperationException("VkMapMemory failed.");

        return (buffer, memory, (nint)mapped);
    }

    public static byte[] LoadEmbeddedSpirv(string fileNameWithExtension)
    {
        var asm = Assembly.GetExecutingAssembly();
        var full = asm.GetManifestResourceNames().FirstOrDefault(n =>
            n.EndsWith(fileNameWithExtension, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Embedded shader '{fileNameWithExtension}' not found. Available: {string.Join(", ", asm.GetManifestResourceNames())}");

        using var s = asm.GetManifestResourceStream(full)!;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public void Dispose()
    {
        Vk.DestroyCommandPool(Device, CommandPool, null);
        Vk.DestroyDevice(Device, null);
        Vk.DestroyInstance(Instance, null);
        GC.SuppressFinalize(this);
    }
}
