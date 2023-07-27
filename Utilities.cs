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

//-------------------
// Verbosity level for logging.
public enum ELogVerboseType : byte
{
	Error = 0,
	Warning = 1,
	Info = 2,
	Debug = 3,
	ExtremelyVerbose = 4
}
// Delegate to pass around Action structure easier and type safe.
public delegate void OnLogDelegate(ELogVerboseType level, string msg);

namespace ReachableGames
{
	namespace RGWebSocket
	{
		//-------------------

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

		//-------------------
		// This is a simple list with an implicit lock around all the actions.  Or you can grab the locker and lock it to do more work on the structure as a whole.
		public class LockingList<T>
		{
			private List<T>  data     = new List<T>();
			private SpinLock spinLock = new SpinLock(false);
 
			// this is the correct pattern for grabbing/releasing the lock.  It's not-re-entrant, so don't call any of these functions while locked.
			public int  Count { get { bool gotLock = false; try  { spinLock.Enter(ref gotLock); return data.Count; } finally { if (gotLock) spinLock.Exit(false); } } }

			// This flexible do-foreach allows you to pass in the delegate and a generic object to hold the data, so you don't have to allocate memory to pass locals through a closure.
			public void DoForEach(Action<T, object> fn, object x)  { bool gotLock = false; try {  spinLock.Enter(ref gotLock); foreach (T t in data) fn(t, x); } finally { if (gotLock) spinLock.Exit(false); } }
 
			public void Add(T item)           { bool gotLock = false; try { spinLock.Enter(ref gotLock); data.Add(item); } finally { if (gotLock) spinLock.Exit(false); } }
			public void Clear()               { bool gotLock = false; try { spinLock.Enter(ref gotLock); data.Clear(); } finally { if (gotLock) spinLock.Exit(false); } }
 			public void MoveTo(List<T> array) { bool gotLock = false; try { spinLock.Enter(ref gotLock); array.AddRange(data); data.Clear(); } finally { if (gotLock) spinLock.Exit(false); } }
 			public bool Remove(T item)        { bool gotLock = false; try { spinLock.Enter(ref gotLock); return data.Remove(item); } finally { if (gotLock) spinLock.Exit(false); } }
            public T    PopFront()            { bool gotLock = false; try { spinLock.Enter(ref gotLock); T ret = default(T); if (data.Count>0) { ret = data[0]; data.RemoveAt(0); } return ret; } finally { if (gotLock) spinLock.Exit(false); } }  // if it's empty, you get null back
            public T    PopBack()             { bool gotLock = false; try { spinLock.Enter(ref gotLock); T ret = default(T); if (data.Count>0) { ret = data[data.Count-1]; data.RemoveAt(data.Count-1); } return ret; } finally { if (gotLock) spinLock.Exit(false); } }  // if it's empty, you get null back

//***** This is an inherent race condition.  Don't use positional operators.
//          public void RemoveAt(int index) { bool gotLock = false; try { spinLock.Enter(ref gotLock); data.RemoveAt(index); } finally { if (gotLock) spinLock.Exit(false); } }
//			public T this[int index]
//			{
//				get { bool gotLock = false; try { spinLock.Enter(ref gotLock); return data[index]; } finally { if (gotLock) spinLock.Exit(false); } }
//				set { bool gotLock = false; try { spinLock.Enter(ref gotLock); data[index] = value; } finally { if (gotLock) spinLock.Exit(false); } }
//			}
		}

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
				if (refCount<=0)
					throw new Exception("Logic error: Reference count went negative in PooledArray.");

				if (Interlocked.Decrement(ref refCount)==0)
				{
#if DEBUG
					// Wipe out the array.  This is more to prevent logic errors where the array is returned and you keep using its contents, so in release builds we don't do this.  Maybe we should, for security reasons too?
					Array.Clear(data, 0, Length);
#endif
					Length = 0;
					Interlocked.Decrement(ref _liveAllocs);
					Interlocked.Add(ref _liveAllocSize, -Length);
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
			static private OnLogDelegate _logger;  // if you assign a logger, you'll get leak alerts at >10,000 buffers.
			static private ConcurrentDictionary<long, LockingList<PooledArray>> _pooledArrays = new ConcurrentDictionary<long, LockingList<PooledArray>>();
#if LEAK_DEBUGGING
			static private LockingList<PooledArray> _borrowedArrays = new LockingList<PooledArray>();   // Keep a list of all the PooledArray objects currently borrowed by something.
			static private long _lastAgeCheckTicks = 0L;
			private const double AGE_CHECK_FREQUENCY_SECONDS = 2.0;                       // How often to check the age of borrowed PooledArray objects.
			private const double AGE_MONITORING_SECONDS = 2.0;                            // How many seconds old an object must be to log its age.
			private const double AGE_FULL_STACK_SECONDS = AGE_MONITORING_SECONDS + 10.0;  // How many seconds old an object must be to log the full call stack from its borrower. It's always larger than AGE_MONITORING_SECONDS, so we add to that.
#endif
			static public long GetLiveAllocs()    { return _liveAllocs; }
			static public long GetLiveAllocSize() { return _liveAllocSize; }

			static public void Initialize(OnLogDelegate logger, long warnAt)
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
				LockingList<PooledArray> poolList;
				if (!_pooledArrays.TryGetValue(roundedLength, out poolList))
				{
					poolList = new LockingList<PooledArray>();
					if (!_pooledArrays.TryAdd(roundedLength, poolList))
						_pooledArrays.TryGetValue(roundedLength, out poolList);  // always succeeds
				}

				PooledArray ret = poolList.PopBack();
				if (ret==null)
				{
					ret = new PooledArray(roundedLength);  // create a new record if we're ever out of them
				}
				ret.Length = length;

#if LEAK_DEBUGGING
				// Leak debugging is handled by tracking the age of each PooledArray object in use.
				// There's two age thresholds:
				//	 1. Logs the id and age of the objects.
				//   2. Logs the id, age, and callstack of when the object was put into service. This lets you see where the call originated, and check to see if that usage has proper disposal (usually it doesn't).
				ret.id = Guid.NewGuid().ToString();

				long nowTicks = DateTime.Now.Ticks;

				// Do this before adding the new object to the list and refCount, so it isn't reflected here.
				if (_logger != null && TimeSpan.FromTicks(nowTicks - _lastAgeCheckTicks).TotalSeconds > AGE_CHECK_FREQUENCY_SECONDS)
				{
					int count = _borrowedArrays.Count;
					if (RGWebSocket.sCloseOutputAsync != null)
					{
						// Subtract one for the RGWebSocket.sCloseOutputAsync that we ignore.
						count--;
					}

					_logger(ELogVerboseType.Info, $"Checking borrowed PooledArray ages: {count}");

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
							_logger(ELogVerboseType.Warning, $"PooledArray found aging object. It's most likely leaked. {pa.id} age: {age}\n{pa._callstack}");
						}
						else if (age > AGE_MONITORING_SECONDS)
						{
							// This one is old enough to monitor, but don't show the full call stack yet.
							_logger(ELogVerboseType.Warning, $"PooledArray {pa.id} age: {age}");
						}
					}, null);

					_lastAgeCheckTicks = nowTicks;
				}

				ret._borrowedAtTicks = nowTicks;
				ret._callstack = Environment.StackTrace;
				_borrowedArrays.Add(ret);
#endif

				ret.IncRef();
				Interlocked.Increment(ref _liveAllocs);
				Interlocked.Add(ref _liveAllocSize, roundedLength);

				if (_liveAllocs>_warnAt)// && _liveAllocs % 100 == 0)
				{
					_logger?.Invoke(ELogVerboseType.Info, $"Leak detected in PooledArray.  Current buffer size: {length}  Live buffers: {_liveAllocs}");
				}

				return ret;
			}
			private void DecRef()
			{
				if (refCount < 0)
					throw new Exception("Logic error: Reference count went negative in PooledArray.");
				
				if (Interlocked.Decrement(ref refCount)==0)
				{
#if DEBUG
					// Wipe out the array.  This is more to prevent logic errors where the array is returned and you keep
					// using its contents, so in release builds we don't do this.  Maybe we should, for security reasons too?
					Array.Clear(data, 0, Length);
#endif
					Length = 0;

					// data.Length is always a power of two already, so we just use that.
					int roundedLength = data.Length;

					_pooledArrays.TryGetValue(roundedLength, out LockingList<PooledArray> poolList);
					poolList.Add(this);

					Interlocked.Decrement(ref _liveAllocs);
					Interlocked.Add(ref _liveAllocSize, -roundedLength);

#if LEAK_DEBUGGING
					_borrowedArrays.Remove(this);
#endif
				}
			}

			public void IncRef()  // necessary anytime code needs to hold onto a buffer for a little while, just increment the reference counter, but remember to decref when you're done with it.
			{
				Interlocked.Increment(ref refCount);
			}

			// Make this private to enforce using the "using" syntax for exception safety
			// this may or may not return this object to the pool... depends on the ref count
			void IDisposable.Dispose() { DecRef(); }
			private PooledArray(int roundedLength) { data = new byte[roundedLength]; }

			public  byte[]      data;
			public  int         Length = 0;    // This is the requested length of the array, which is usually slightly less than data.Length (which is always power of 2)
			private int         refCount = 0;

#if LEAK_DEBUGGING
			// Keep track of when the array was borrowed and where it was borrowed from.
			public string       id;						// Let's us identify specific objects for loggin.
			private long        _borrowedAtTicks;
			private string      _callstack = string.Empty;
#endif
		}
#endif
	}
}