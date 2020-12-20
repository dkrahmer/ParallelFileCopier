using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace KrahmerSoft.ParallelFileCopierLib
{
	// Class derived from https://stackoverflow.com/questions/18922985/concurrent-hashsett-in-net-framework
	public class ConcurrentHashSet<T> : IDisposable
	{
		private readonly HashSet<T> _hashSet = new HashSet<T>();
		private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);

		#region Implementation of ICollection<T> ...ish
		public bool Add(T item)
		{
			_lock.EnterWriteLock();
			try
			{
				return _hashSet.Add(item);
			}
			finally
			{
				_lock.ExitWriteLock();
			}
		}

		public void Clear()
		{
			_lock.EnterWriteLock();
			try
			{
				_hashSet.Clear();
			}
			finally
			{
				_lock.ExitWriteLock();
			}
		}

		public bool Contains(T item)
		{
			_lock.EnterReadLock();
			try
			{
				return _hashSet.Contains(item);
			}
			finally
			{
				_lock.ExitReadLock();
			}
		}

		public bool Remove(T item)
		{
			_lock.EnterWriteLock();
			try
			{
				return _hashSet.Remove(item);
			}
			finally
			{
				_lock.ExitWriteLock();
			}
		}

		public int Count
		{
			get
			{
				_lock.EnterReadLock();
				try
				{
					return _hashSet.Count;
				}
				finally
				{
					_lock.ExitReadLock();
				}
			}
		}


		internal IEnumerable<T> ToArray()
		{
			_lock.EnterReadLock();
			try
			{
				return _hashSet.ToArray();
			}
			finally
			{
				_lock.ExitReadLock();
			}
		}

		#endregion

		#region Dispose
		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}
		protected virtual void Dispose(bool disposing)
		{
			if (disposing)
				if (_lock != null)
					_lock.Dispose();
		}
		~ConcurrentHashSet()
		{
			Dispose(false);
		}
		#endregion
	}
}