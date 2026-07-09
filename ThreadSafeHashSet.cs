#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;

namespace Shared
{
	// Similar to ThreadSafeDictionary, but for a HashSet.  We don't support all the useful hashset features currently, it's just a replacement for ConcurrentDictionary.
	// Note, ReaderWriterLockSlim owns kernel handles, so this class is IDisposable.  Long-lived/static instances can ignore that
	// (the process teardown reclaims them), but anything that churns instances should dispose them.
	public class ThreadSafeHashSet<TKey> : IDisposable
		where TKey : notnull
	{
		private readonly HashSet<TKey>        _hash = new HashSet<TKey>();
		private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();

		public void Dispose()
		{
			_lock.Dispose();
		}

		// Add only if not present
		public bool Add(TKey key)
		{
			_lock.EnterWriteLock();
			try
			{
				return _hash.Add(key);
			}
			finally
			{
				_lock.ExitWriteLock();
			}
		}

		// Check if an item is in the set
		public bool Contains(TKey key)
		{
			_lock.EnterReadLock();
			try
			{
				return _hash.Contains(key);
			}
			finally
			{
				_lock.ExitReadLock();
			}
		}

		// Remove an item from the set
		public bool Remove(TKey key)
		{
			_lock.EnterWriteLock();
			try
			{
				return _hash.Remove(key);
			}
			finally
			{
				_lock.ExitWriteLock();
			}
		}

		// Get the count of items in the set
		public int Count
		{
			get
			{
				_lock.EnterReadLock();
				try
				{
					return _hash.Count;
				}
				finally
				{
					_lock.ExitReadLock();
				}
			}
		}

		// Clear the set
		public void Clear()
		{
			_lock.EnterWriteLock();
			try
			{
				_hash.Clear();
			}
			finally
			{
				_lock.ExitWriteLock();
			}
		}

		// Be careful, the hash is locked during the callbacks.  Don't mess with the hash during this time, either.
		public void Foreach(Action<TKey> callback)
		{
			_lock.EnterReadLock();
			try
			{
				foreach (TKey k in _hash)
				{
					callback(k);
				}
			}
			finally
			{
				_lock.ExitReadLock();
			}
		}
	}
}