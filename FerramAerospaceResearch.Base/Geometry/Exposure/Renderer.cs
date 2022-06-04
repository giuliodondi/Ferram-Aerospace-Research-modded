using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using FerramAerospaceResearch.Resources;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FerramAerospaceResearch.Geometry.Exposure;

[StructLayout(LayoutKind.Explicit)]
public struct ColorUintConverter
{
    [FieldOffset(0)] private uint u;
    [FieldOffset(0)] private Color32 c;

    public static Color32 AsColor(uint value)
    {
        return new ColorUintConverter { u = value }.c;
    }

    public static uint AsUint(Color32 color)
    {
        return new ColorUintConverter { c = color }.u;
    }
}

[BurstCompile]
public struct PixelCountJob : IJobParallelFor
{
    [ReadOnly] public NativeSlice<uint> texture;
    public NativeSlice<int> pixels;

    public void Execute(int index)
    {
        uint color = texture[index];
        if (color == 0)
            return;

        int partIndex = Renderer.GetIndex(color);
        unsafe
        {
            // [] returns a copy so cannot use ref with it, instead access from the buffer pointer directly
            int* pixelBegin = (int*)pixels.GetUnsafePtr();
            Interlocked.Increment(ref *(pixelBegin + partIndex));
        }
    }
}

public static class Renderer
{
    private static readonly bool supportsComputeShaders;
    private static bool computeWarningIssued;
    private static readonly float3 cameraScale;

    static Renderer()
    {
        supportsComputeShaders = SystemInfo.supportsComputeShaders;
        cameraScale = new float3(1, 1, SystemInfo.usesReversedZBuffer ? -1 : 1);
    }

    public static int GetIndex(uint value)
    {
        return (int)(value - 1);
    }

    public static uint Encode(int index)
    {
        return (uint)(index + 1);
    }

    public static ProcessingDevice SelectActualDevice(ProcessingDevice device)
    {
        if (device is not ProcessingDevice.GPU || supportsComputeShaders)
            return device;

        if (!computeWarningIssued)
            return ProcessingDevice.CPU;

        FARLogger.Warning("Compute shaders are not supported on your system!");
        computeWarningIssued = true;

        return ProcessingDevice.CPU;
    }

    public static void TransformBoundsCorners(NativeSlice<float3> corners, in Bounds bounds, in float4x4 transform)
    {
        if (corners.Length != 8)
            throw new ArgumentException($"Invalid number of corners allocated: expected 8 but got {corners.Length}");

        Vector3 min = bounds.min;
        Vector3 max = bounds.max;

        // NativeSlice throws errors accessing its items if it wasn't created from a valid native array such as from stackalloc
        corners[0] = math.transform(transform, new float3(min.x, min.y, min.z));
        corners[1] = math.transform(transform, new float3(min.x, min.y, max.z));
        corners[2] = math.transform(transform, new float3(min.x, max.y, min.z));
        corners[3] = math.transform(transform, new float3(min.x, max.y, max.z));
        corners[4] = math.transform(transform, new float3(max.x, min.y, min.z));
        corners[5] = math.transform(transform, new float3(max.x, min.y, max.z));
        corners[6] = math.transform(transform, new float3(max.x, max.y, min.z));
        corners[7] = math.transform(transform, new float3(max.x, max.y, max.z));
    }

    public static CameraInfo ProjectToCameraRender(in float3 center, in float3 lookDir, in NativeSlice<float3> corners)
    {
        var min = new float2(math.INFINITY);
        var max = new float2(-math.INFINITY);
        var nearFar = new float2(math.INFINITY, -math.INFINITY);

        float3 dir = math.normalize(lookDir);
        foreach (float3 corner in corners)
        {
            float depth = math.dot(corner, dir);
            float2 planePos = (corner - depth * dir).xy;

            min = math.min(min, planePos);
            max = math.max(max, planePos);

            nearFar.x = math.min(nearFar.x, depth);
            nearFar.y = math.max(nearFar.y, depth);
        }

        float3 camPos = center;
        float centerDepth = (center - math.project(center, lookDir)).z;
        float extent = 1.1f * math.max(centerDepth - nearFar.x, nearFar.y - centerDepth);
        camPos -= extent * lookDir;

        var viewMatrix = float4x4.TRS(camPos, quaternion.LookRotation(lookDir, Vector3.up), cameraScale);
        viewMatrix = math.inverse(viewMatrix);
        float4x4 projectionMatrix =
            GL.GetGPUProjectionMatrix(Matrix4x4.Ortho(min.x, max.x, min.y, max.y, 0.01f, 2 * extent), true);

        float2 size = max - min;

        return new CameraInfo
        {
            forward = lookDir,
            projectedArea = (double)size.x * size.y,
            position = camPos,
            vpMatrix = math.mul(projectionMatrix, viewMatrix),
        };
    }

    public static JobHandle CountPixels(NativeSlice<uint> texture, NativeSlice<int> pixelCounts)
    {
        // TODO: use multiple sequential counter jobs working on texture slices and separate count arrays since
        // atomic increment on shared cache lines is slow
        var job = new PixelCountJob
        {
            pixels = pixelCounts,
            texture = texture,
        };
        return job.Schedule(texture.Length, 256);
    }
}

public class Renderer<T> : IDisposable, IReadOnlyDictionary<T, int> where T : Object
{
    // ReSharper disable once StaticMemberInGenericType
    private static readonly Pool<HashSet<UnityEngine.Renderer>> renderersPool =
        new(() => new HashSet<UnityEngine.Renderer>(ObjectReferenceEqualityComparer<UnityEngine.Renderer>.Default),
            set => set.Clear());

    // rendered object cache
    private readonly Dictionary<T, int> objects = new(ObjectReferenceEqualityComparer<T>.Default);
    private readonly List<HashSet<UnityEngine.Renderer>> renderers = new();

    // currently executing jobs
    private readonly HashSet<RenderBatch> activeBatches = new(ObjectReferenceEqualityComparer<RenderBatch>.Default);

    // pool of inactive jobs, reusing old ones to reduce the overhead of instantiating Unity resources and GC allocations
    private Pool<RenderBatch> batchPool;

    // material used to render the objects
    private Material material;

    public Material Material
    {
        get { return material; }
        set
        {
            if (ReferenceEquals(material, value))
                return;
            material = value;
            ForEachJob(value, (job, mat) => job.Material = mat);
        }
    }

    // number of requests executed last time
    public int ActiveBatches
    {
        get { return activeBatches.Count; }
    }

    // temporary MPB to avoid GC
    private MaterialPropertyBlock mpb;

    // texture size, greater size -> better resolution
    private int2 renderSize = 512;

    public int2 RenderSize
    {
        get { return renderSize; }
        set
        {
            if (math.all(renderSize == value))
                return;
            renderSize = value;
            ForEachJob(value, (job, v) => job.RenderSize = v);
        }
    }

    // compute shader to count pixels
    private ComputeShader pixelCountShader;

    public ComputeShader PixelCountShader
    {
        get { return pixelCountShader; }
        set
        {
            if (ReferenceEquals(pixelCountShader, value))
                return;
            pixelCountShader = value;
            ForEachJob(value, (job, v) => job.PixelCountShader = v);
        }
    }

    private Kernel pixelCountKernel;

    public Kernel PixelCountKernel
    {
        get { return pixelCountKernel; }
        set
        {
            pixelCountKernel = value;
            ForEachJob(value, (job, v) => job.PixelCountKernel = v);
        }
    }

    // rendering volume
    public Bounds Bounds { get; set; }

    // flag for any changes in objects/their renderers
    private bool isDirty = true;

    // request device to count pixels on
    private ProcessingDevice device = ProcessingDevice.GPU;

    public ProcessingDevice Device
    {
        get { return device; }
        set
        {
            device = value;
            ActualDevice = Renderer.SelectActualDevice(value);
            ForEachJob(ActualDevice, (job, v) => job.Device = v);
        }
    }

    // actual device pixels are counted on
    public ProcessingDevice ActualDevice { get; private set; }

    public float AspectRatio
    {
        get { return (float)renderSize.x / renderSize.y; }
    }

    public Renderer()
    {
        // circular reference in closure, clean up in Dispose
        batchPool = new Pool<RenderBatch>(() => new RenderBatch
                                          {
                                              Device = ActualDevice,
                                              Material = Material,
                                              PixelCountKernel = PixelCountKernel,
                                              PixelCountShader = PixelCountShader,
                                              RenderSize = RenderSize
                                          },
                                          null);
    }

    /// <summary>
    /// Mark CommandBuffer for reconstruction on the next request
    /// </summary>
    public void ReconstructCommandBuffers()
    {
        ForEachJob(true, (job, _) => job.ReconstructCommandBuffer());
    }

    public Dictionary<T, int>.KeyCollection Keys
    {
        get { return objects.Keys; }
    }

    public Dictionary<T, int>.ValueCollection Values
    {
        get { return objects.Values; }
    }

    public Dictionary<T, int>.Enumerator GetEnumerator()
    {
        return objects.GetEnumerator();
    }

    IEnumerator<KeyValuePair<T, int>> IEnumerable<KeyValuePair<T, int>>.GetEnumerator()
    {
        return objects.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return ((IEnumerable)objects).GetEnumerator();
    }

    public int Count
    {
        get { return objects.Count; }
    }

    public bool ContainsKey(T key)
    {
        return objects.ContainsKey(key);
    }

    public bool TryGetValue(T key, out int value)
    {
        return objects.TryGetValue(key, out value);
    }

    public int this[T key]
    {
        get { return objects[key]; }
    }

    IEnumerable<T> IReadOnlyDictionary<T, int>.Keys
    {
        get { return objects.Keys; }
    }

    IEnumerable<int> IReadOnlyDictionary<T, int>.Values
    {
        get { return objects.Values; }
    }

    private void ReleaseUnmanagedResources()
    {
        batchPool.Dispose();

        // cancel all jobs first before waiting
        CancelPendingJobs();

        // need to wait before releasing resources
        foreach (RenderBatch job in activeBatches)
            job.Dispose();
        activeBatches.Clear();
    }

    private void Dispose(bool disposing)
    {
        ReleaseUnmanagedResources();
        if (disposing)
        {
            batchPool = null;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    ~Renderer()
    {
        Dispose(false);
    }

    private void ForEachJob<V>(V value, Action<RenderBatch, V> func)
    {
        foreach (RenderBatch job in activeBatches)
            func(job, value);
        foreach (RenderBatch job in batchPool)
            func(job, value);
    }

    private Color GetObjColor(T obj)
    {
        // unique per object, stability doesn't matter as renderers will have to be rebuilt when anything changes
        if (!objects.TryGetValue(obj, out int index))
        {
            index = objects.Count;
            renderers.Add(renderersPool.Acquire());
            objects.Add(obj, index);
        }

        uint id = Renderer.Encode(index);
        return ColorUintConverter.AsColor(id);
    }

    public void CancelPendingJobs()
    {
        foreach (RenderBatch job in activeBatches)
            job.Apply(h => h.Cancel());
    }

    public Color SetupRenderers<R>(T obj, R objRenderers, MaterialPropertyBlock propertyBlock = null)
        where R : IEnumerable<UnityEngine.Renderer>
    {
        Color color = GetObjColor(obj);
        int index = objects[obj];
        foreach (UnityEngine.Renderer renderer in objRenderers)
        {
            SetupRenderer(renderer, color, propertyBlock);
            renderers[index].Add(renderer);
        }

        return color;
    }

    public Color SetupRenderer(T obj, UnityEngine.Renderer renderer, MaterialPropertyBlock propertyBlock = null)
    {
        Color color = GetObjColor(obj);
        SetupRenderer(renderer, color, propertyBlock);
        renderers[objects[obj]].Add(renderer);
        isDirty = true;
        return color;
    }

    private void SetupRenderer(UnityEngine.Renderer renderer, Color color, MaterialPropertyBlock propertyBlock = null)
    {
        if (propertyBlock == null)
        {
            mpb ??= new MaterialPropertyBlock();
            renderer.GetPropertyBlock(mpb); // always overwrites block in any case
            propertyBlock = mpb;
        }

        propertyBlock.SetColor(ShaderPropertyIds._ExposedColor, color);
        renderer.SetPropertyBlock(propertyBlock);
    }

    public void Reset()
    {
        foreach (HashSet<UnityEngine.Renderer> set in renderers)
            renderersPool.Release(set);
        objects.Clear();
        isDirty = true;
    }

    public void Render<R>(R requests, in float4x4 transform) where R : IReadOnlyList<RenderRequest>
    {
        if (isDirty)
        {
            ReconstructCommandBuffers();
            isDirty = false;
        }

        RenderBatch batch = batchPool.Acquire();
        batch.Execute(renderers, requests, Bounds, transform, onRenderBatchCompletedAction, this);
        activeBatches.Add(batch);
    }

    // convert only once to avoid GC allocations
    // ReSharper disable once StaticMemberInGenericType
    private static readonly Action<RenderBatch, object> onRenderBatchCompletedAction = OnRenderBatchCompleted;

    private static void OnRenderBatchCompleted(RenderBatch batch, object o)
    {
        var self = o as Renderer<T>;
        FARLogger.Assert(self != null, nameof(self) + " != null");

        // ReSharper disable once PossibleNullReferenceException
        self.activeBatches.Remove(batch);
        self.batchPool.Release(batch);
    }
}
