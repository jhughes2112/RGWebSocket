//-------------------
// Reachable Games
// Copyright 2019
//-------------------

using System;
using System.Collections.Generic;

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
	}
}