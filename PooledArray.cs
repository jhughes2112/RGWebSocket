#nullable enable
//-------------------
// Reachable Games
// Copyright 2023
//-------------------
// Comment this in for a logs about aging buffers, including callstacks for really old ones.  Sometimes helpful when tracking down the errant buffers.
//#define LEAK_DEBUGGING
// Comment this in to not recycle buffers, but instead just allocate them and deallocate them according to refcount.
//#define DISABLE_RECYCLING

using System;
using System.Threading;
using System.Collections.Generic;
using System.Collections.Concurrent;
using Logging;
using Shared;

//-------------------

namespace ReachableGames
{
	namespace RGWebSocket
	{
#if DISABLE_RECYCLING
		//-------------------
		// This is a fake pooled array, for debugging and performance comparison.  There is only a refcount, no recycling.  All requests for memory blocks are allocated at the time of request.
		public class PooledArray : IDisposable
		{
			static private long _liveAllocs = 0;
			static private long _liveAllocSize = 0;
			static public long GetLiveAllocs()    { return _liveAllocs; }
			static public long GetLiveAllocSize() { return _liveAllocSize; }
			static public PooledArray BorrowFromPool(int length)
			{
				PooledArray ret = new PooledArray(length);
				ret.Length = length;
				ret.IncRef();
				Interlocked.Increment(ref _liveAllocs);
				Interlocked.Add(ref _liveAllocSize, length);
				return ret;
			}
			private void DecRef()
			{
				// Check the RESULT of the decrement -- pre-checking refCount is a race, and a double-Dispose could slip from 1 -> 0 -> -1 silently.
				int newCount = Interlocked.Decrement(ref refCount);
				if (newCount<0)
					throw new InvalidOperationException("Logic error: Reference count went negative in PooledArray.  Probably a double-Dispose.");
				if (newCount==0)
				{
#if DEBUG
					// Wipe out the array.  This is more to prevent logic errors where the array is returned and you keep using its contents, so in release builds we don't do this.  Maybe we should, for security reasons too?
					Array.Clear(data, 0, Length);
#endif
					Interlocked.Decrement(ref _liveAllocs);
					Interlocked.Add(ref _liveAllocSize, -Length);  // must happen BEFORE Length is reset, or the stat never shrinks
					Length = 0;
				}
			}
			public void IncRef() { Interlocked.Increment(ref refCount); }
			void IDisposable.Dispose() { DecRef(); }
			private PooledArray(int length) { data = new byte[length]; }

			public  byte[]      data;
			public  int         Length = 0;
			private int         refCount = 0;
		}
#else
		//-------------------
		// Recycling arrays makes for much better memory performance.  There's some cost for the lock(), but I don't expect a lot of contention here since it's at the individual buffer-size level.
		public class PooledArray : IDisposable
		{
			// This has an internal pool, so as not to pollute the StorageLocalBinary class.
			static private long _liveAllocs = 0;
			static private long _liveAllocSize = 0;
			static private long _warnAt     = 10000;
			static private ILogging? _logger;  // if you assign a logger, you'll get leak alerts at >10,000 buffers.
			static private ThreadSafeDictionary<long, LockingList<PooledArray>> _pooledArrays = new ThreadSafeDictionary<long, LockingList<PooledArray>>();
#if LEAK_DEBUGGING
			static private LockingList<PooledArray> _borrowedArrays = new LockingList<PooledArray>();   // Keep a list of all the PooledArray objects currently borrowed by something.
			static private long _lastAgeCheckTicks = 0L;
			private const double AGE_CHECK_FREQUENCY_SECONDS = 2.0;                       // How often to check the age of borrowed PooledArray objects.
			private const double AGE_MONITORING_SECONDS = 2.0;                            // How many seconds old an object must be to log its age.
			private const double AGE_FULL_STACK_SECONDS = AGE_MONITORING_SECONDS + 10.0;  // How many seconds old an object must be to log the full call stack from its borrower. It's always larger than AGE_MONITORING_SECONDS, so we add to that.
#endif
			static public long GetLiveAllocs()    { return _liveAllocs; }
			static public long GetLiveAllocSize() { return _liveAllocSize; }

			static public void Initialize(ILogging logger, long warnAt)
			{
				_logger = logger;
				_warnAt = warnAt;
			}

			static public PooledArray BorrowFromPool(int length)
			{
				// Round up to powers of two, so we don't have too many different categories, but none smaller than 128 bytes.
				int roundedLength = 128;
				while (roundedLength < length)
					roundedLength *= 2;

				// Create the list the first time we need it
				LockingList<PooledArray>? poolList;
				if (!_pooledArrays.TryGetValue(roundedLength, out poolList))
				{
					poolList = _pooledArrays.GetOrAdd(roundedLength, () => new LockingList<PooledArray>());  // always succeeds
				}

				PooledArray? ret = poolList!.PopBack();
				if (ret==null)
				{
					ret = new PooledArray(roundedLength);  // create a new record if we're ever out of them
				}
				ret.Length = length;

#if LEAK_DEBUGGING
				long nowTicks = DateTime.Now.Ticks;

				// Do this before adding the new object to the list and refCount, so it isn't reflected here.
				if (_logger != null && TimeSpan.FromTicks(nowTicks - _lastAgeCheckTicks).TotalSeconds > AGE_CHECK_FREQUENCY_SECONDS)
				{
					LogOldAllocations();
				}

				ret._borrowedAtTicks = nowTicks;
				ret._interactions.Add($"Alloc length={length} at {Environment.StackTrace}");
				_borrowedArrays.Add(ret);
#endif

				ret.IncRef();
				Interlocked.Increment(ref _liveAllocs);
				Interlocked.Add(ref _liveAllocSize, roundedLength);

				if (_liveAllocs>_warnAt)// && _liveAllocs % 100 == 0)
				{
					_logger?.Log(EVerbosity.Info, $"Leak detected in PooledArray.  Current buffer size: {length}  Live buffers: {_liveAllocs}");
				}

				return ret;
			}
			private void DecRef()
			{
#if LEAK_DEBUGGING
				_interactions.Add($"DecRef {refCount} -> {refCount-1} at {Environment.StackTrace}");
#endif

				// Check the RESULT of the decrement -- pre-checking refCount is a race, and a double-Dispose could slip from 1 -> 0 -> -1 silently,
				// which would put this buffer back in the pool twice and hand the same array to two different owners.
				int newCount = Interlocked.Decrement(ref refCount);
				if (newCount<0 || _data==null)
					throw new InvalidOperationException("Logic error: Reference count went negative in PooledArray.  Probably a double-Dispose.");

				if (newCount==0)
				{
#if DEBUG
					// Wipe out the array.  This is more to prevent logic errors where the array is returned and you keep
					// using its contents, so in release builds we don't do this.  Maybe we should, for security reasons too?
					Array.Fill<byte>(_data, 0xBE);
#endif
					Length = -1;

					// data.Length is always a power of two already, so we just use that.
					int roundedLength = _data.Length;

					_pooledArrays.TryGetValue(roundedLength, out LockingList<PooledArray>? poolList);
					poolList!.Add(this);

					Interlocked.Decrement(ref _liveAllocs);
					Interlocked.Add(ref _liveAllocSize, -roundedLength);

#if LEAK_DEBUGGING
					_borrowedArrays.Remove(this);
					_interactions.Add($"Freed at {Environment.StackTrace}");
#endif
				}
			}

			public void IncRef()  // necessary anytime code needs to hold onto a buffer for a little while, just increment the reference counter, but remember to decref when you're done with it.
			{
				Interlocked.Increment(ref refCount);
#if LEAK_DEBUGGING
				_interactions.Add($"IncRef {refCount-1} -> {refCount} at {Environment.StackTrace}");
#endif
			}

			static private void LogOldAllocations()
			{
#if LEAK_DEBUGGING
				long nowTicks = DateTime.Now.Ticks;
				int count = _borrowedArrays.Count;
				if (RGWebSocket.sCloseOutputAsync != null)
				{
					// Subtract one for the RGWebSocket.sCloseOutputAsync that we ignore.
					count--;
				}

				_logger.Log(EVerbosity.Info, $"Checking borrowed PooledArray ages: {count}");

				// Count mismatch, something must have leaked.
				// Periodically see what objects are "old", because they probably are leaked.
				// Objects typically have a lifespan shorter than 1-2 seconds.
				_borrowedArrays.DoForEach((pa, obj) =>
				{
					if (pa.Equals(RGWebSocket.sCloseOutputAsync))
					{
						// This is a special array object that is used to indicate closing a web socket, which is created at startup and persists forever.
						// So, we have to ignore the age of this one.
						return;
					}

					double age = TimeSpan.FromTicks(nowTicks - pa._borrowedAtTicks).TotalSeconds;

					if (age > AGE_FULL_STACK_SECONDS)
					{
						_logger.Log(EVerbosity.Warning, $"PooledArray found aging object. It's most likely leaked. {pa.id} age: {age}");
						LogAlloc(pa);
					}
					else if (age > AGE_MONITORING_SECONDS)
					{
						// This one is old enough to monitor, but don't show the full call stack yet.
						_logger.Log(EVerbosity.Warning, $"PooledArray {pa.id} age: {age}");
					}
				}, null);

				_lastAgeCheckTicks = nowTicks;
#endif
			}

			// Exhaustive logging
			static private void LogAlloc(PooledArray pa)
			{
#if LEAK_DEBUGGING
				foreach (string l in pa._interactions)
				{
					_logger.Log(EVerbosity.Warning, l);
				}
#endif
			}

			// Make this private to enforce using the "using" syntax for exception safety
			// this may or may not return this object to the pool... depends on the ref count
			void IDisposable.Dispose() { DecRef(); }
			private PooledArray(int roundedLength) { data = new byte[roundedLength]; }

			public  byte[]      data { 
				get { 
#if LEAK_DEBUGGING
						_interactions.Add($"Accessed at {Environment.StackTrace}");
#endif
						return _data!;
					}
				private set { _data = value; }
			}
			public  int         Length = 0;    // This is the requested length of the array, which is usually slightly less than data.Length (which is always power of 2)
			private int         refCount = 0;
			private byte[]?     _data;

#if LEAK_DEBUGGING
			// Keep track of when the array was borrowed and where it was borrowed from.
			public string        id;						// Let's us identify specific objects for loggin.
			private long         _borrowedAtTicks;
			private List<string> _interactions = new List<string>();  // this allows us to track what happened and where
#endif
		}
#endif
	}
}