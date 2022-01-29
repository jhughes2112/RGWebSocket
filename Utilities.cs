//-------------------
// Reachable Games
// Copyright 2019
//-------------------

using System;
using System.Threading;
using System.Collections.Generic;
using System.Collections.Concurrent;

namespace ReachableGames
{
	namespace RGWebSocket
	{
		static public class Utilities
		{
			// Human-readable formatted string, from StackOverflow
			static public String BytesToHumanReadable(long byteCount)
			{
				string[] suf = { "B", "KB", "MB", "GB", "TB", "PB", "EB" }; //Longs run out around EB
				if (byteCount == 0)
					return "0" + suf[0];
				long bytes = Math.Abs(byteCount);
				int place = Convert.ToInt32(Math.Floor(Math.Log(bytes, 1024)));
				double num = Math.Round(bytes / Math.Pow(1024, place), 1);
				return (Math.Sign(byteCount) * num).ToString("F1") + suf[place];
			}
		}

		// This is a simple list with an implicit lock around all the actions.  Or you can grab the locker and lock it to do more work on the structure as a whole.
		public class LockingList<T>
		{
			private List<T> data   = new List<T>();
			private object  locker = new object();
 
			public int    Count { get { lock (locker) { return data.Count; } } }
 			public object Locker { get { return locker; } }
 
			public T this[int index]
			{
				get { lock (locker) { return data[index]; } }
				set { lock (locker) { if (index>=0 && index<data.Count) data[index] = value; else throw new ArgumentOutOfRangeException("index", index, $"Index was {index} but needed to be >=0 and <={data.Count-1}"); } }
			}
 
			public void Add(T item) { lock (locker) { data.Add(item); } }
			public void Clear()     { lock (locker) { data.Clear(); } }
 			public void MoveTo(List<T> array) { lock (locker) { array.AddRange(data); data.Clear(); } }
		}

		// Recycling arrays makes for much better memory performance.  There's some cost for the lock(), but I don't expect a lot of contention here since it's at the individual buffer-size level.
		public class PooledArray : IDisposable
		{
			// This has an internal pool, so as not to pollute the StorageLocalBinary class.
			static private ConcurrentDictionary<long, List<PooledArray>> pooledArrays = new ConcurrentDictionary<long, List<PooledArray>>();
			static private ConcurrentDictionary<long, int>               liveArrays = new ConcurrentDictionary<long, int>();  // this counts the number that are allocated but not yet returned (if this grows, there's a leak!)
			static public PooledArray BorrowFromPool(int length)
			{
				// Round up to powers of two, so we don't have too many different categories, but none smaller than 128 bytes.
				int roundedLength = 128;
				while (roundedLength < length)
					roundedLength *= 2;

				// Create the list the first time we need it
				List<PooledArray> poolList;
				if (!pooledArrays.TryGetValue(roundedLength, out poolList))
				{
					poolList = new List<PooledArray>();
					if (!pooledArrays.TryAdd(roundedLength, poolList))
						pooledArrays.TryGetValue(roundedLength, out poolList);  // always succeeds
					liveArrays.TryAdd(roundedLength, 0);
				}

				lock (poolList)  // ConcurrentQueue has memory leaks--do not change List to that just to avoid the lock.
				{
					if (poolList.Count==0)
						poolList.Add(new PooledArray(roundedLength));  // create a new record if we're ever out of them
					PooledArray ret = poolList[poolList.Count-1];
					ret.Length = length;
					ret.IncRef();
					poolList.RemoveAt(poolList.Count-1);
					for (;;)  // atomic increment is complicated due to the concurrent dictionary
					{
						int value = liveArrays[roundedLength];  // only fetch once, otherwise race conditions
						if (liveArrays.TryUpdate(roundedLength, value+1, value))
						{
							//***** I don't think 10,000 of anything is really a very conservative value, but if this trips and you DON'T have a leak, bump it up.
							if (value>10000)
								throw new Exception($"Leak detected in PooledArray.  {value} buffers live of size {length}");
							break;
						}
					}
					return ret;
				}
			}
			private void DecRef()
			{
				if (refCount<=0)
					throw new Exception("Logic error: Reference count went negative in PooledArray.");

				if (Interlocked.Decrement(ref refCount)==0)
				{
#if DEBUG
					// Wipe out the array.  This is more to prevent logic errors where the array is returned and you keep using its contents, so in release builds we don't do this.  Maybe we should, for security reasons too?
					Array.Clear(data, 0, Length);
#endif
					Length = 0;

					// data.Length is always a power of two already, so we just use that.
					int roundedLength = data.Length;
					pooledArrays.TryGetValue(roundedLength, out List<PooledArray> poolList);

					lock (poolList)  // ConcurrentQueue has memory leaks--do not change List to that just to avoid the lock.
					{
						poolList.Add(this);
						for (;;)  // atomic decrement is complicated due to the concurrent dictionary
						{
							int value = liveArrays[roundedLength];  // only fetch once, otherwise race conditions
							if (liveArrays.TryUpdate(roundedLength, value-1, value))
								break;
						}
					}
				}
			}
			public void IncRef()  // necessary anytime code needs to hold onto a buffer for a little while, just increment the reference counter, but remember to decref when you're done with it.
			{
				Interlocked.Increment(ref refCount);
			}
			public void Dispose()  // This just allows the using (...) syntax, it does NOT necessarily delete the object.
			{
				DecRef();  // this may or may not return this object to the pool... depends on the ref count
			}
			private PooledArray(int roundedLength)
			{
				data = new byte[roundedLength];
			}

			// Most arrays also require stream interfaces, so we just allocate them here and keep them around.  Saves more allocations later.
			public  byte[]      data;
			public  int         Length = 0;    // This is the requested length of the array, which is usually slightly less than data.Length (which is always power of 2)
			private int         refCount = 0;
		}
	}
}