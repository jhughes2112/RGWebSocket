//-------------------
// Reachable Games
// Copyright 2023
//-------------------
// Comment this in for exhaustive callstack tracing for where pooled arrays are being held onto.
//#define LEAK_DEBUGGING

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
 			public bool Remove(T item) { lock (locker) { return data.Remove(item); } }
            public void RemoveAt(int index) { lock (locker) { data.RemoveAt(index); } }
		}

		// Recycling arrays makes for much better memory performance.  There's some cost for the lock(), but I don't expect a lot of contention here since it's at the individual buffer-size level.
		public class PooledArray : IDisposable
		{
			// This has an internal pool, so as not to pollute the StorageLocalBinary class.
			static private ConcurrentDictionary<long, List<PooledArray>> pooledArrays = new ConcurrentDictionary<long, List<PooledArray>>();
			static private ConcurrentDictionary<long, int>               liveArrays = new ConcurrentDictionary<long, int>();  // this counts the number that are allocated but not yet returned (if this grows, there's a leak!)
#if LEAK_DEBUGGING
			static private ConcurrentDictionary<string, int>             callstacksAtInc = new ConcurrentDictionary<string, int>();  // this is REALLY heavy.  Only for leak debugging.
#endif
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
#if LEAK_DEBUGGING
					string callstack = Environment.StackTrace;
					ret.callstacks.Add(callstack);
					for (;;)  // atomically increment the callstack count -- we're inside a lock, so I don't think we have to do this.
					{
						int value = 0;
						if (callstacksAtInc.TryGetValue(callstack, out value)==false)
							callstacksAtInc.TryAdd(callstack, value);  // if this succeeds or fails, all it means is the next loop will work
						if (callstacksAtInc.TryUpdate(callstack, value+1, value))
							break;
					}
#endif
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

#if LEAK_DEBUGGING
						foreach (string callstack in callstacks)
						{
							for (;;)  // atomically decrement the callstack count -- we're inside a lock, so I don't think we have to do this.
							{
								int value = 0;
								if (callstacksAtInc.TryGetValue(callstack, out value)==false)
									throw new Exception("Logic error: more decrements than increments for callstack.");
								if (callstacksAtInc.TryUpdate(callstack, value-1, value))
								{
									if (value==1)
										callstacksAtInc.TryRemove(callstack, out value);  // this is a race condition if it's outside the lock.
									break;
								}
							}
						}
						callstacks.Clear();
#endif
					}
				}
			}
			public void IncRef()  // necessary anytime code needs to hold onto a buffer for a little while, just increment the reference counter, but remember to decref when you're done with it.
			{
				Interlocked.Increment(ref refCount);
#if LEAK_DEBUGGING
				string callstack = Environment.StackTrace;
				callstacks.Add(callstack);
				for (;;)  // atomically increment the callstack count -- we're inside a lock, so I don't think we have to do this.
				{
					int value = 0;
					if (callstacksAtInc.TryGetValue(callstack, out value)==false)
						callstacksAtInc.TryAdd(callstack, value);  // if this succeeds or fails, all it means is the next loop will work
					if (callstacksAtInc.TryUpdate(callstack, value+1, value))
						break;
				}
#endif
			}
			
			// Make this private to enforce using the "using" syntax for exception safety
			void IDisposable.Dispose()  // This just allows the using (...) syntax, it does NOT necessarily delete the object.
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

#if LEAK_DEBUGGING
			// We keep a list of every callstack that incremented this array.  When the decref hits zero, we reduce the count in the central dictionary.  VERY heavy.
			// Doesn't always tell us exactly where the leak is, but it tells us where the refcounts went wrong.
			private List<string> callstacks = new List<string>();
#endif
		}
	}
}