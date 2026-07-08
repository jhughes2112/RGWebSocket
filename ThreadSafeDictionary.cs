#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;

namespace Shared
{
	// Thank you ChatGPT for providing me a way to remove ConcurrentDictionary from a bunch of places.  Concurrent structures are REALLY inefficient and cause lots of memory allocations and copies.
	// Note, this is a slimmed down interface, to prevent the complexities of locking around iteration.  It's good as a key/value store only.
	public class ThreadSafeDictionary<TKey, TValue>
		where TKey : notnull
		where TValue : class
	{
		private readonly Dictionary<TKey, TValue> _dictionary = new Dictionary<TKey, TValue>();
		private readonly ReaderWriterLockSlim     _lock       = new ReaderWriterLockSlim();

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

		// Get or add a value, add is only called if there is no value already
		public TValue GetOrAdd(TKey key, Func<TValue> callback)
		{
			_lock.EnterWriteLock();
			try
			{
				if (_dictionary.TryGetValue(key, out TValue? value)==false)
				{
					value = callback();
					_dictionary.Add(key, value!);
				}
				return value;
			}
			finally
			{
				_lock.ExitWriteLock();
			}
		}


		// Try to get an item from the dictionary
		public bool TryGetValue(TKey key, out TValue value)
		{
			_lock.EnterReadLock();
			try
			{
				return _dictionary.TryGetValue(key, out value!);
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

		// Remove an item from the dictionary
		public bool TryRemove(TKey key, out TValue value)
		{
			_lock.EnterWriteLock();
			try
			{
				return _dictionary.Remove(key, out value!);
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