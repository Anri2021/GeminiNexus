using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace GeminiNexus.Shared;

public static partial class ComputeKernels
{
    private static long invocations;
    public static long Invocations => Interlocked.Read(ref invocations);

    public static float Dot(ReadOnlySpan<float> left,ReadOnlySpan<float> right,string backend="auto")
    {
        if(left.Length!=right.Length||left.IsEmpty)throw new ArgumentException("Vector dimensions must match and be non-empty.");
        Interlocked.Increment(ref invocations);
        if(backend=="gpu"&&TryOpenCl(left,right,out var gpu))return gpu;
        if(backend=="gpu")throw new PlatformNotSupportedException("A usable OpenCL GPU is not available.");
        if((backend is "auto" or "native")&&TryNative(left,right,out var native))return native;
        if(backend=="native")throw new PlatformNotSupportedException("nexus_native is not available.");
        return DotSimd(left,right);
    }

    private static float DotSimd(ReadOnlySpan<float> left,ReadOnlySpan<float> right)
    {
        var width=Vector<float>.Count;var index=0;var sum=Vector<float>.Zero;
        ref var l=ref MemoryMarshal.GetReference(left);ref var r=ref MemoryMarshal.GetReference(right);
        for(;index<=left.Length-width;index+=width)sum+=Vector.LoadUnsafe(ref l,(nuint)index)*Vector.LoadUnsafe(ref r,(nuint)index);
        var value=Vector.Sum(sum);for(;index<left.Length;index++)value+=Unsafe.Add(ref l,index)*Unsafe.Add(ref r,index);return value;
    }

    private static unsafe bool TryNative(ReadOnlySpan<float> left,ReadOnlySpan<float> right,out float result)
    {
        try{fixed(float* l=left)fixed(float* r=right){result=NativeDot(l,r,(nuint)left.Length);return true;}}
        catch(DllNotFoundException){result=0;return false;}catch(EntryPointNotFoundException){result=0;return false;}
    }

    public static unsafe bool OpenClAvailable()
    {
        try
        {
            uint platformCount=0;if(ClGetPlatformIds(0,null,&platformCount)!=0||platformCount==0)return false;
            var platforms=stackalloc nint[checked((int)platformCount)];if(ClGetPlatformIds(platformCount,platforms,null)!=0)return false;
            for(var index=0;index<platformCount;index++){uint devices=0;if(ClGetDeviceIds(platforms[index],ClDeviceTypeGpu,0,null,&devices)==0&&devices>0)return true;}
            return false;
        }
        catch(DllNotFoundException){return false;}catch(EntryPointNotFoundException){return false;}
    }

    private const ulong ClDeviceTypeGpu=1UL<<2,ClMemWriteOnly=1UL<<1,ClMemReadOnly=1UL<<2,ClMemCopyHostPtr=1UL<<5;
    private const uint ClTrue=1;
    private const string MultiplyKernel="kernel void multiply(global const float* a,global const float* b,global float* output){size_t i=get_global_id(0);output[i]=a[i]*b[i];}";

    private static unsafe bool TryOpenCl(ReadOnlySpan<float> left,ReadOnlySpan<float> right,out float result)
    {
        result=0;nint context=0,queue=0,leftBuffer=0,rightBuffer=0,outputBuffer=0,program=0,kernel=0;
        try
        {
            uint platformCount=0;if(ClGetPlatformIds(0,null,&platformCount)!=0||platformCount==0)return false;
            var platforms=stackalloc nint[checked((int)platformCount)];if(ClGetPlatformIds(platformCount,platforms,null)!=0)return false;
            nint device=0;
            for(var index=0;index<platformCount&&device==0;index++)
            {
                nint candidate=0;uint deviceCount=0;if(ClGetDeviceIds(platforms[index],ClDeviceTypeGpu,1,&candidate,&deviceCount)==0&&deviceCount>0)device=candidate;
            }
            if(device==0)return false;int error=0;context=ClCreateContext(null,1,&device,0,0,&error);if(error!=0||context==0)return false;
            queue=ClCreateCommandQueueWithProperties(context,device,null,&error);if(error!=0||queue==0)return false;
            var bytes=checked((nuint)left.Length*(nuint)sizeof(float));
            fixed(float* leftPointer=left)fixed(float* rightPointer=right)
            {
                leftBuffer=ClCreateBuffer(context,ClMemReadOnly|ClMemCopyHostPtr,bytes,leftPointer,&error);if(error!=0||leftBuffer==0)return false;
                rightBuffer=ClCreateBuffer(context,ClMemReadOnly|ClMemCopyHostPtr,bytes,rightPointer,&error);if(error!=0||rightBuffer==0)return false;
            }
            outputBuffer=ClCreateBuffer(context,ClMemWriteOnly,bytes,null,&error);if(error!=0||outputBuffer==0)return false;
            var source=Encoding.UTF8.GetBytes(MultiplyKernel);fixed(byte* sourcePointer=source)
            {
                var sources=stackalloc byte*[1];sources[0]=sourcePointer;var lengths=stackalloc nuint[1];lengths[0]=(nuint)source.Length;
                program=ClCreateProgramWithSource(context,1,sources,lengths,&error);
            }
            if(error!=0||program==0||ClBuildProgram(program,1,&device,null,0,0)!=0)return false;
            var name=stackalloc byte[]{(byte)'m',(byte)'u',(byte)'l',(byte)'t',(byte)'i',(byte)'p',(byte)'l',(byte)'y',0};
            kernel=ClCreateKernel(program,name,&error);if(error!=0||kernel==0)return false;
            if(ClSetKernelArg(kernel,0,(nuint)sizeof(nint),&leftBuffer)!=0||ClSetKernelArg(kernel,1,(nuint)sizeof(nint),&rightBuffer)!=0||ClSetKernelArg(kernel,2,(nuint)sizeof(nint),&outputBuffer)!=0)return false;
            var work=(nuint)left.Length;if(ClEnqueueNdRangeKernel(queue,kernel,1,null,&work,null,0,null,null)!=0)return false;
            using var output=new NativeFloatBuffer(left.Length);
            if(ClEnqueueReadBuffer(queue,outputBuffer,ClTrue,0,bytes,Unsafe.AsPointer(ref MemoryMarshal.GetReference(output.Span)),0,null,null)!=0)return false;
            result=DotSum(output.Span);return true;
        }
        catch(DllNotFoundException){return false;}catch(EntryPointNotFoundException){return false;}
        finally
        {
            if(kernel!=0)ClReleaseKernel(kernel);if(program!=0)ClReleaseProgram(program);if(outputBuffer!=0)ClReleaseMemObject(outputBuffer);
            if(rightBuffer!=0)ClReleaseMemObject(rightBuffer);if(leftBuffer!=0)ClReleaseMemObject(leftBuffer);if(queue!=0)ClReleaseCommandQueue(queue);if(context!=0)ClReleaseContext(context);
        }
    }

    private static float DotSum(ReadOnlySpan<float> values)
    {
        var width=Vector<float>.Count;var index=0;var sum=Vector<float>.Zero;ref var start=ref MemoryMarshal.GetReference(values);
        for(;index<=values.Length-width;index+=width)sum+=Vector.LoadUnsafe(ref start,(nuint)index);
        var value=Vector.Sum(sum);for(;index<values.Length;index++)value+=Unsafe.Add(ref start,index);return value;
    }

    [LibraryImport("nexus_native",EntryPoint="nexus_dot_f32")]
    private static unsafe partial float NativeDot(float* left,float* right,nuint length);
    [LibraryImport("OpenCL",EntryPoint="clGetPlatformIDs")]
    private static unsafe partial int ClGetPlatformIds(uint count,nint* platforms,uint* platformCount);
    [LibraryImport("OpenCL",EntryPoint="clGetDeviceIDs")]
    private static unsafe partial int ClGetDeviceIds(nint platform,ulong deviceType,uint count,nint* devices,uint* deviceCount);
    [LibraryImport("OpenCL",EntryPoint="clCreateContext")]
    private static unsafe partial nint ClCreateContext(nint* properties,uint deviceCount,nint* devices,nint callback,nint userData,int* error);
    [LibraryImport("OpenCL",EntryPoint="clCreateCommandQueueWithProperties")]
    private static unsafe partial nint ClCreateCommandQueueWithProperties(nint context,nint device,nint* properties,int* error);
    [LibraryImport("OpenCL",EntryPoint="clCreateBuffer")]
    private static unsafe partial nint ClCreateBuffer(nint context,ulong flags,nuint size,void* host,int* error);
    [LibraryImport("OpenCL",EntryPoint="clCreateProgramWithSource")]
    private static unsafe partial nint ClCreateProgramWithSource(nint context,uint count,byte** strings,nuint* lengths,int* error);
    [LibraryImport("OpenCL",EntryPoint="clBuildProgram")]
    private static unsafe partial int ClBuildProgram(nint program,uint deviceCount,nint* devices,byte* options,nint callback,nint userData);
    [LibraryImport("OpenCL",EntryPoint="clCreateKernel")]
    private static unsafe partial nint ClCreateKernel(nint program,byte* name,int* error);
    [LibraryImport("OpenCL",EntryPoint="clSetKernelArg")]
    private static unsafe partial int ClSetKernelArg(nint kernel,uint index,nuint size,void* value);
    [LibraryImport("OpenCL",EntryPoint="clEnqueueNDRangeKernel")]
    private static unsafe partial int ClEnqueueNdRangeKernel(nint queue,nint kernel,uint dimensions,nuint* offset,nuint* global,nuint* local,uint waitCount,nint* waitList,nint* @event);
    [LibraryImport("OpenCL",EntryPoint="clEnqueueReadBuffer")]
    private static unsafe partial int ClEnqueueReadBuffer(nint queue,nint buffer,uint blocking,nuint offset,nuint size,void* target,uint waitCount,nint* waitList,nint* @event);
    [LibraryImport("OpenCL",EntryPoint="clReleaseMemObject")]private static partial int ClReleaseMemObject(nint value);
    [LibraryImport("OpenCL",EntryPoint="clReleaseKernel")]private static partial int ClReleaseKernel(nint value);
    [LibraryImport("OpenCL",EntryPoint="clReleaseProgram")]private static partial int ClReleaseProgram(nint value);
    [LibraryImport("OpenCL",EntryPoint="clReleaseCommandQueue")]private static partial int ClReleaseCommandQueue(nint value);
    [LibraryImport("OpenCL",EntryPoint="clReleaseContext")]private static partial int ClReleaseContext(nint value);
}

public readonly ref struct NativeFloatBuffer
{
    private readonly unsafe float* pointer;
    public int Length{get;}
    public unsafe NativeFloatBuffer(int length){if(length<=0)throw new ArgumentOutOfRangeException(nameof(length));Length=length;pointer=(float*)NativeMemory.AlignedAlloc(checked((nuint)length*(nuint)sizeof(float)),64);if(pointer is null)throw new OutOfMemoryException();}
    public unsafe Span<float> Span=>new(pointer,Length);
    public unsafe void Dispose()=>NativeMemory.AlignedFree(pointer);
}

[StructLayout(LayoutKind.Explicit)]
public struct FloatBits
{
    [FieldOffset(0)]public float Value;
    [FieldOffset(0)]public uint Bits;
}
