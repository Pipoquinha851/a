using NUnit.Framework;
using System;
using Unity.Jobs;
using Unity.Collections;
using Unity.Collections.Tests;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
internal class NativeMultiHashMapTests_JobDebugger : NativeMultiHashMapTestsFixture
{
    [Test]
    public void NativeMultiHashMap_Read_And_Write_Without_Fences()
    {
        var hashMap = new NativeMultiHashMap<int, int>(hashMapSize, CommonRwdAllocator.Handle);
        var writeStatus = CollectionHelper.CreateNativeArray<int>(hashMapSize, CommonRwdAllocator.Handle);
        var readValues = CollectionHelper.CreateNativeArray<int>(hashMapSize, CommonRwdAllocator.Handle);

        var writeData = new MultiHashMapWriteParallelForJob()
        {
            hashMap = hashMap.AsParallelWriter(),
            status = writeStatus,
            keyMod = hashMapSize,
        };

        var readData = new MultiHashMapReadParallelForJob()
        {
            hashMap = hashMap,
            values = readValues,
            keyMod = writeData.keyMod,
        };

        var writeJob = writeData.Schedule(hashMapSize, 1);
        Assert.Throws<InvalidOperationException>(() => { readData.Schedule(hashMapSize, 1); });
        writeJob.Complete();

        hashMap.Dispose();
        writeStatus.Dispose();
        readValues.Dispose();
    }

// DOTS-6203 Nested containers aren't detected in DOTS Runtime currently
#if !UNITY_DOTSRUNTIME
    struct NestedMapJob : IJob
    {
        public NativeMultiHashMap<int, NativeMultiHashMap<int, int>> nestedMap;

        public void Execute()
        {
            nestedMap.Clear();
        }
    }

    [Test]
    public void NativeMultiHashMap_NestedJob_Error()
    {
        var map = new NativeMultiHashMap<int, NativeMultiHashMap<int, int>>(hashMapSize, CommonRwdAllocator.Handle);

        var nestedJob = new NestedMapJob
        {
            nestedMap = map
        };

        JobHandle job = default;
        Assert.Throws<InvalidOperationException>(() => { job = nestedJob.Schedule(); });
        job.Complete();

        map.Dispose();
    }
#endif
}
#endif
