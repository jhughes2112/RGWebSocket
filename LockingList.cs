#nullable enable
#nullable disable warnings
//-------------------
// Reachable Games
// Copyright 2023
//-------------------

using System;
using System.Threading;
using System.Collections.Generic;

//-------------------

namespace ReachableGames
{
	namespace RGWebSocket
	{
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
			public void Clear()                { bool gotLock = false; try { spinLock.Enter(ref gotLock); data.Clear(); } finally { if (gotLock) spinLock.Exit(false); } }
 			public void MoveTo(List<T> array) { bool gotLock = false; try { spinLock.Enter(ref gotLock); array.AddRange(data); data.Clear(); } finally { if (gotLock) spinLock.Exit(false); } }
 			public bool Remove(T item)        { bool gotLock = false; try { spinLock.Enter(ref gotLock); return data.Remove(item); } finally { if (gotLock) spinLock.Exit(false); } }
            public T   PopFront()             { bool gotLock = false; try { spinLock.Enter(ref gotLock); T ret = default(T); if (data.Count>0) { ret = data[0]; data.RemoveAt(0); } return ret; } finally { if (gotLock) spinLock.Exit(false); } }  // if it's empty, you get null back
            public T   PopBack()              { bool gotLock = false; try { spinLock.Enter(ref gotLock); T ret = default(T); if (data.Count>0) { ret = data[data.Count-1]; data.RemoveAt(data.Count-1); } return ret; } finally { if (gotLock) spinLock.Exit(false); } }  // if it's empty, you get null back

//***** This is an inherent race condition.  Don't use positional operators.
//          public void RemoveAt(int index) { bool gotLock = false; try { spinLock.Enter(ref gotLock); data.RemoveAt(index); } finally { if (gotLock) spinLock.Exit(false); } }
//			public T this[int index]
//			{
//				get { bool gotLock = false; try { spinLock.Enter(ref gotLock); return data[index]; } finally { if (gotLock) spinLock.Exit(false); } }
//				set { bool gotLock = false; try { spinLock.Enter(ref gotLock); data[index] = value; } finally { if (gotLock) spinLock.Exit(false); } }
//			}
		}
	}
}