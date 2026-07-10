#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace Shared
{
	// Thank you ChatGPT for providing me a way to remove ConcurrentDictionary from a bunch of places.  Concurrent structures are REALLY inefficient and cause lots of memory allocations and copies.
	// Note, this is a slimmed down interface, to prevent the complexities of locking around iteration.  It's good as a key/value store only.
	// Note, ReaderWriterLockSlim owns kernel handles, so this class is IDisposable.  Long-lived/static instances can ignore that
	// (the process teardown reclaims them), but anything that churns instances should dispose them.
	public class ThreadSafeDictionary<TKey, TValue> : IDisposable
		where TKey : notnull
		where TValue : class
	{
		private readonly Dictionary<TKey, TValue> _dictionary = new Dictionary<TKey, TValue>();
		private readonly ReaderWriterLockSlim     _lock       = new ReaderWriterLockSlim();

		public void Dispose()
		{
			_lock.Dispose();
		}

		// Add only if not present
		public bool Add(TKey key, TValue value)
		{
			_lock.EnterWriteLock();
			try
			{
				if (_dictionary.ContainsKey(key)==false)
				{
					_dictionary.Add(key, value);
					return true;
				}
				return false;
			}
			finally
			{
				_lock.ExitWriteLock();
			}
		}

		// Add or update an item in the dictionary
		public void AddOrUpdate(TKey key, TValue value)
		{
			_lock.EnterWriteLock();
			try
			{
				_dictionary[key] = value;  // Add or update the item
			}
			finally
			{
				_lock.ExitWriteLock();
			}
		}

		// Get or add a value, add is only called if there is no value already.
		// DELIBERATE DESIGN: the callback runs while the write lock is held.  That is the only way to guarantee a heavy object is
		// constructed exactly once -- checking first and constructing outside the lock is a race (ConcurrentDictionary "solves" it
		// by sometimes constructing twice and throwing one away, wasting memory and cycles).  The price of that guarantee: the
		// callback must be quick, must NOT touch this dictionary (LockRecursionException), and must NOT take other locks that
		// could be held by someone waiting on this one.
		public TValue GetOrAdd(TKey key, Func<TValue> callback)
		{
			_lock.EnterWriteLock();
			try
			{
				if (_dictionary.TryGetValue(key, out TValue? value)==false)
				{
					value = callback();
					if (value==null)
						throw new InvalidOperationException("ThreadSafeDictionary.GetOrAdd callback returned null; refusing to poison the dictionary.");  // fail fast here, not mysteriously at some later TryGetValue
					_dictionary.Add(key, value);
				}
				return value;
			}
			finally
			{
				_lock.ExitWriteLock();
			}
		}


		// Try to get an item from the dictionary.  MaybeNullWhen keeps the compiler honest: callers are forced to check the
		// bool before dereferencing value, instead of being handed a fake non-null guarantee.
		public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value)
		{
			_lock.EnterReadLock();
			try
			{
				return _dictionary.TryGetValue(key, out value);
			}
			finally
			{
				_lock.ExitReadLock();
			}
		}

		// Remove an item from the dictionary
		public bool Remove(TKey key)
		{
			_lock.EnterWriteLock();
			try
			{
				return _dictionary.Remove(key);
			}
			finally
			{
				_lock.ExitWriteLock();
			}
		}

		// Remove an item only if the predicate still holds, checked under the write lock.  Avoids deleting an entry
		// another thread updated between your read and the removal.
		public bool RemoveIf(TKey key, Func<TValue, bool> predicate)
		{
			_lock.EnterWriteLock();
			try
			{
				if (_dictionary.TryGetValue(key, out TValue? value) && predicate(value))
				{
					return _dictionary.Remove(key);
				}
				return false;
			}
			finally
			{
				_lock.ExitWriteLock();
			}
		}

		// Remove an item from the dictionary, returning the value that was removed
		public bool TryRemove(TKey key, [MaybeNullWhen(false)] out TValue value)
		{
			_lock.EnterWriteLock();
			try
			{
				return _dictionary.Remove(key, out value);
			}
			finally
			{
				_lock.ExitWriteLock();
			}
		}

		// Get the count of items in the dictionary
		public int Count
		{
			get
			{
				_lock.EnterReadLock();
				try
				{
					return _dictionary.Count;
				}
				finally
				{
					_lock.ExitReadLock();
				}
			}
		}

		// Clear the dictionary
		public void Clear()
		{
			_lock.EnterWriteLock();
			try
			{
				_dictionary.Clear();
			}
			finally
			{
				_lock.ExitWriteLock();
			}
		}

		// Check if the dictionary contains a key
		public bool ContainsKey(TKey key)
		{
			_lock.EnterReadLock();
			try
			{
				return _dictionary.ContainsKey(key);
			}
			finally
			{
				_lock.ExitReadLock();
			}
		}

		// Be careful, the dictionary is locked during the callbacks.  Don't mess with the Dictionary during this time, either.
		public void Foreach(Action<TKey, TValue> callback)
		{
			_lock.EnterReadLock();
			try
			{
				foreach ((TKey k, TValue v) in _dictionary)
				{
					callback(k, v);
				}
			}
			finally
			{
				_lock.ExitReadLock();
			}
		}
	}
}