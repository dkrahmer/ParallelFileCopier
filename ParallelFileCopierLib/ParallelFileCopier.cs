using Mono.Unix;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace KrahmerSoft.ParallelFileCopierLib
{
	public class ParallelFileCopier : IDisposable
	{
		private SemaphoreSlim _copyOperationsSemaphore = new SemaphoreSlim(1);
		private SemaphoreSlim _concurrentFilesSemaphore;
		private SemaphoreSlim _maxFileQueueLengthSemaphore;
		private SemaphoreSlim _maxTotalThreadsSemaphore;
		private SemaphoreSlim _maxTotalThreadsSafetySemaphore = new SemaphoreSlim(1);
		private ParallelFileCopierOptions _options;
		private ConcurrentBag<Exception> _exceptions;
		private ConcurrentHashSet<Task> _copyFileTasks;
		private long _copiedFileCount;
		private long _copiedByteCount;
		private Stopwatch _copyStopwatch;

		public event EventHandler<FileCopyData> StartFileCopy;
		public event EventHandler<FileCopyData> EndFileCopy;
		public event EventHandler<VerboseInfo> VerboseOutput;

		private readonly object _isPosixPlatformLock = new object();
		private bool? _isPosixPlatform;
		protected bool IsPosixPlatform
		{
			get
			{
				if (_isPosixPlatform == null)
				{
					lock (_isPosixPlatformLock)
					{
						if (_isPosixPlatform == null) // double-check inside the lock
						{
							_isPosixPlatform = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
						}
					}
				}

				return _isPosixPlatform.Value;
			}
		}

		public ParallelFileCopier(ParallelFileCopierOptions options = null)
		{
			options = options ?? new ParallelFileCopierOptions(); // use default options if none specified

			if (options.MaxConcurrentFiles <= 0)
				throw new ArgumentException("MaxConcurrentFiles must be > 0");

			if (options.MaxFileQueueLength <= 0)
				throw new ArgumentException("MaxFileQueueLength must be > 0");

			if (options.MaxThreadsPerFile <= 0)
				throw new ArgumentException("MaxThreadsPerFile must be > 0");

			if (options.MaxTotalThreads <= 0)
				throw new ArgumentException("MaxTotalThreads must be > 0");

			if (options.MaxTotalThreads < options.MaxThreadsPerFile)
				options.MaxThreadsPerFile = options.MaxTotalThreads; // Fix the value so we do not lock the process

			_options = options;
			_concurrentFilesSemaphore = new SemaphoreSlim(_options.MaxConcurrentFiles);
			_maxFileQueueLengthSemaphore = new SemaphoreSlim(_options.MaxFileQueueLength);
			_maxTotalThreadsSemaphore = new SemaphoreSlim(_options.MaxTotalThreads);
		}

		public async Task CopyFilesAsync(string sourcePath, string destinationPath)
		{
			CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
			CancellationToken cancellationToken = cancellationTokenSource.Token;

			await CopyFilesAsync(sourcePath, destinationPath, cancellationToken);
		}

		public async Task CopyFilesAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
		{
			if (cancellationToken.IsCancellationRequested)
				return;

			await StartCopyOperationAsync(cancellationToken);
			if (cancellationToken.IsCancellationRequested)
				return;

			try
			{
				await CopyFilesInternalAsync(sourcePath, destinationPath, 0, cancellationToken);
				if (cancellationToken.IsCancellationRequested)
					return;

				if (_exceptions.Count == 1)
					throw _exceptions.FirstOrDefault();

				if (_exceptions.Count > 1)
					throw new AggregateException(_exceptions);
			}
			finally
			{
				EndCopyOperation(cancellationToken);
			}
		}

		protected async Task CopyFilesInternalAsync(string sourcePath, string destinationPath, int level, CancellationToken cancellationToken)
		{
			if (cancellationToken.IsCancellationRequested)
				return;

			PathType sourceType = GetPathType(sourcePath);
			PathType destinationType = GetPathType(destinationPath);
			string sourceFileMask = "*";

			if (destinationType == PathType.Directory && sourceType != PathType.Unknown)
			{
				destinationPath = Path.Combine(destinationPath, Path.GetFileName(sourcePath));
				destinationType = sourceType;
			}
			else
			{
				if (sourceType == PathType.Unknown)
				{
					sourceType = PathType.Directory;
					if (!(sourcePath.EndsWith("/") || sourcePath.EndsWith("\\")))
					{
						sourceFileMask = Path.GetFileName(sourcePath);
						sourcePath = Path.GetDirectoryName(sourcePath);
						sourceType = PathType.Directory; // assume it's a file mask
					}
				}
				else if (destinationType == PathType.Unknown)
				{
					if (destinationPath.EndsWith("/") || destinationPath.EndsWith("\\"))
					{
						destinationPath = Path.Combine(destinationPath, Path.GetFileName(sourcePath));
						destinationType = PathType.Directory;
					}
					else
					{
						destinationType = sourceType;
					}
				}

				if (sourceType == PathType.Directory)
				{
					if (!Directory.Exists(sourcePath))
						throw new ArgumentException("Source directory does not exist.");

					if (destinationType == PathType.File)
						throw new ArgumentException("Destination path cannot be a file if the source path is a directory.");
				}
			}

			if (_exceptions.Any())
				return;

			try
			{
				if (_options.CopyEmptyDirectories)
				{
					if (destinationType == PathType.Directory)
					{
						if (!Directory.Exists(destinationPath))
						{
							VerboseOutput?.Invoke(this, new VerboseInfo(1, () => $"Creating destination directory \"{destinationPath}\"..."));
							Directory.CreateDirectory(destinationPath);
						}
					}
					else if (destinationType == PathType.File)
					{
						string destinationPathDirectory = Path.GetDirectoryName(destinationPath);
						if (!Directory.Exists(destinationPathDirectory))
						{
							VerboseOutput?.Invoke(this, new VerboseInfo(1, () => $"Creating destination directory \"{destinationPathDirectory}\"..."));
							Directory.CreateDirectory(destinationPathDirectory);
						}
					}
				}

				if (sourceType == PathType.File)
				{
					if (destinationType == PathType.Directory)
					{
						destinationType = PathType.File;
						destinationPath = Path.Combine(destinationPath, Path.GetFileName(sourcePath));
					}

					await EnqueueCopyFileTaskAsync(_copyFileTasks, sourcePath, destinationPath, cancellationToken);
					if (cancellationToken.IsCancellationRequested)
						return;
				}
				else
				{
					VerboseOutput?.Invoke(this, new VerboseInfo(1, () => $"Reading source files list from \"{sourcePath}\"..."));
					foreach (string sourceFilePath in Directory.EnumerateFiles(sourcePath, sourceFileMask))
					{
						if (cancellationToken.IsCancellationRequested)
							return;

						string sourceFilename = Path.GetFileName(sourceFilePath);
						await EnqueueCopyFileTaskAsync(_copyFileTasks, sourceFilePath, Path.Combine(destinationPath, sourceFilename), cancellationToken);
						if (cancellationToken.IsCancellationRequested)
							return;

						if (_exceptions.Any())
							return;
					}

					VerboseOutput?.Invoke(this, new VerboseInfo(1, () => $"Reading source subdirectories list from \"{sourcePath}\"..."));
					foreach (string sourceSubDirectory in Directory.EnumerateDirectories(sourcePath))
					{
						if (cancellationToken.IsCancellationRequested)
							return;

						string subDirectoryName = Path.GetFileName(sourceSubDirectory);
						await CopyFilesInternalAsync(Path.Combine(sourceSubDirectory, sourceFileMask), Path.Combine(destinationPath, subDirectoryName), level + 1, cancellationToken);
						if (cancellationToken.IsCancellationRequested)
							return;

						if (_exceptions.Any())
							return;
					}
				}
			}
			catch (ApplicationException)
			{
				throw;
			}
			catch (Exception ex)
			{
				throw new ApplicationException($"Failed to copy \"{sourcePath}\" -> \"{destinationPath}\"", ex);
			}
			finally
			{
				if (level == 0)
					await Task.WhenAll(_copyFileTasks.ToArray()); // wait for all queued file copies to complete
			}
		}

		private async Task EnqueueCopyFileTaskAsync(ConcurrentHashSet<Task> copyTasks, string sourcePath, string destinationPath, CancellationToken cancellationToken)
		{
			if (cancellationToken.IsCancellationRequested)
				return;

			await _maxFileQueueLengthSemaphore.WaitAsync(cancellationToken);
			if (cancellationToken.IsCancellationRequested)
				return;

			VerboseOutput?.Invoke(this, new VerboseInfo(2, () => $"Adding copy task to queue: \"{sourcePath}\" -> \"{destinationPath}\"..."));

			var copyFileTask = CopyFileInternalAsync(sourcePath, destinationPath, cancellationToken);
			if (cancellationToken.IsCancellationRequested)
				return;

			copyTasks.Add(copyFileTask);

			var cleanupTask = copyFileTask.ContinueWith((antecedent) =>
			{
				try
				{
					if (antecedent.Exception != null)
					{
						lock (_exceptions)
						{
							_exceptions.Add(antecedent.Exception);
						}
					}
					copyTasks.Remove(copyFileTask);
				}
				finally
				{
					_maxFileQueueLengthSemaphore.Release();
				}
			});
			copyTasks.Add(cleanupTask);

			var finalizeTask = cleanupTask.ContinueWith((antecedent) =>
			{
				copyTasks.Remove(cleanupTask);
			});
		}

		public async Task CopyFileAsync(string sourceFilePath, string destinationFilePath, CancellationToken cancellationToken)
		{
			if (cancellationToken.IsCancellationRequested)
				return;

			await StartCopyOperationAsync(cancellationToken);
			if (cancellationToken.IsCancellationRequested)
				return;

			try
			{
				await CopyFileInternalAsync(sourceFilePath, destinationFilePath, cancellationToken);
				if (cancellationToken.IsCancellationRequested)
					return;
			}
			finally
			{
				EndCopyOperation(cancellationToken);
			}
		}

		public async Task CopyFileInternalAsync(string sourceFilePath, string destinationFilePath, CancellationToken cancellationToken)
		{
			if (cancellationToken.IsCancellationRequested)
				return;

			await _concurrentFilesSemaphore.WaitAsync(cancellationToken);
			if (cancellationToken.IsCancellationRequested)
				return;
			await _maxTotalThreadsSafetySemaphore.WaitAsync(cancellationToken);
			if (cancellationToken.IsCancellationRequested)
				return;

			bool maxTotalThreadsSafetySemaphoreReleased = false;
			int lockedTotalThreadCount = 0;

			try
			{
				if (_exceptions.Any())
					return;

				string incompleteDestinationFilePath = _options.UseIncompleteFilename ? $"{destinationFilePath.TrimEnd('.')}.{Guid.NewGuid()}.incomplete" : destinationFilePath;

				// Copy values to local vars
				int bufferSize = _options.BufferSize;
				int copyThreadCount = _options.MaxThreadsPerFile;
				int minChunksPerThread = _options.MinChunksPerThread;

				int minChunkSize = bufferSize;
				var sourceFileInfo = new FileInfo(sourceFilePath);

				if (_options.SkipExistingIdenticalFiles)
				{
					if (File.Exists(destinationFilePath))
					{
						var tempDestinationFileInfo = new FileInfo(destinationFilePath);
						if (sourceFileInfo.Length == tempDestinationFileInfo.Length
							&& sourceFileInfo.LastWriteTimeUtc == tempDestinationFileInfo.LastWriteTimeUtc)
						{
							VerboseOutput?.Invoke(this, new VerboseInfo(1, () => $"Skipping existing same-size file in destination: \"{destinationFilePath}\""));
							return;
						}
					}
				}

				// make sure each thread will transfer at least 8 chunks to make it worth our while to create a thread
				int minBytesPerChunk = minChunkSize * minChunksPerThread;
				long absoluteMaxThreadCount = (int)(sourceFileInfo.Length < minBytesPerChunk ? 1 : sourceFileInfo.Length / minBytesPerChunk);

				if (copyThreadCount > absoluteMaxThreadCount)
					copyThreadCount = (int)absoluteMaxThreadCount;

				for (int i = 0; i < copyThreadCount; i++) // Wait until we have enough threads available for this file
				{
					await _maxTotalThreadsSemaphore.WaitAsync(cancellationToken);
					if (cancellationToken.IsCancellationRequested)
						return;

					lockedTotalThreadCount++;
				}

				_maxTotalThreadsSafetySemaphore.Release();
				maxTotalThreadsSafetySemaphoreReleased = true;

				if (_exceptions.Any())
					return;

				string destinationDirectory = Path.GetDirectoryName(destinationFilePath);
				if (!Directory.Exists(destinationDirectory))
				{
					VerboseOutput?.Invoke(this, new VerboseInfo(1, () => $"Creating destination directory \"{destinationDirectory}\"..."));
					Directory.CreateDirectory(destinationDirectory);
				}

				if (File.Exists(destinationFilePath))
				{
					VerboseOutput?.Invoke(this, new VerboseInfo(0, () => $"Destination file exists{(_options.SkipExistingIdenticalFiles ? " with different size/date" : "")}. Deleting \"{destinationFilePath}\" before copy..."));
					File.Delete(destinationFilePath);
				}

				StartFileCopy?.Invoke(this, new FileCopyData() { SourceFilePath = sourceFilePath, DestinationFilePath = destinationFilePath });
				VerboseOutput?.Invoke(this, new VerboseInfo(0, () => $"Copying \"{sourceFilePath}\" -> \"{destinationFilePath}\"..."));

				using (var writer = new FileStream(incompleteDestinationFilePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, FileOptions.Asynchronous | FileOptions.RandomAccess))
				{
					// Init the new file
					writer.SetLength(0);
				}

				var tasks = new List<Task>();

				long currentFileChunkNumber = -1;
				long getNextFileChunkNumber() => Interlocked.Increment(ref currentFileChunkNumber);

				var resizeFileLock = new SemaphoreSlim(1);

				for (int threadNumber = 0; threadNumber < copyThreadCount; threadNumber++)
				{
					string sourceFilePathOverride = sourceFilePath;
					if (threadNumber > 0 && !string.IsNullOrWhiteSpace(_options.IncrementalSourcePath)
						&& sourceFilePathOverride.StartsWith(_options.IncrementalSourcePath, StringComparison.InvariantCultureIgnoreCase))
					{
						sourceFilePathOverride = sourceFilePathOverride.Substring(0, _options.IncrementalSourcePath.Length)
							+ $"_{threadNumber + 1}"
							+ sourceFilePathOverride.Substring(_options.IncrementalSourcePath.Length);
						VerboseOutput?.Invoke(this, new VerboseInfo(2, () => $"Using IncrementalSymLinkSourcePath \"{sourceFilePathOverride}\""));
					}

					var task = CopyChunksAsync(sourceFilePathOverride, incompleteDestinationFilePath, bufferSize, getNextFileChunkNumber, resizeFileLock, cancellationToken);

					tasks.Add(task);
				}

				await Task.WhenAll(tasks);
				if (cancellationToken.IsCancellationRequested)
				{
					if (File.Exists(incompleteDestinationFilePath))
					{
						// Delete the file since it may not be complete
						VerboseOutput?.Invoke(this, new VerboseInfo(0, () => $"Canceling: Deleting incomplete file \"{incompleteDestinationFilePath}\"..."));
						try
						{
							File.Delete(incompleteDestinationFilePath);
						}
						catch (Exception ex)
						{
							VerboseOutput?.Invoke(this, new VerboseInfo(0, () => $"Canceling: Could not delete incomplete file \"{incompleteDestinationFilePath}\"! - {ex.ToString()}"));
						}
					}
					return;
				}

				if (incompleteDestinationFilePath != destinationFilePath)
				{
					VerboseOutput?.Invoke(this, new VerboseInfo(2, () => $"Renaming \"{incompleteDestinationFilePath}\" -> \"{destinationFilePath}\"..."));

					if (File.Exists(destinationFilePath))
						File.Delete(destinationFilePath);

					File.Move(incompleteDestinationFilePath, destinationFilePath);
				}

				var destinationFileInfo = new FileInfo(destinationFilePath);

				try
				{
					destinationFileInfo.LastAccessTime = sourceFileInfo.LastAccessTime;
					destinationFileInfo.LastWriteTime = sourceFileInfo.LastWriteTime;
					destinationFileInfo.CreationTimeUtc = sourceFileInfo.CreationTimeUtc;
				}
				catch (Exception ex)
				{
					throw new ApplicationException("Failed to set destination write and create times.", ex);
				}

				if (!IsPosixPlatform)
				{
					try
					{
						destinationFileInfo.Attributes = sourceFileInfo.Attributes;
					}
					catch (Exception ex)
					{
						throw new ApplicationException("Failed to set destination attributes.", ex);
					}
				}

				destinationFileInfo.Refresh(); // Force write all changes now
				destinationFileInfo = null;

				if (IsPosixPlatform)
				{
					// Additional handling for posix platforms
					try
					{
						var sourceUnixFileInfo = new UnixFileInfo(sourceFilePath);
						var destinationUnixFileInfo = new UnixFileInfo(destinationFilePath)
						{
							FileAccessPermissions = sourceUnixFileInfo.FileAccessPermissions,
							Protection = sourceUnixFileInfo.Protection,
							FileSpecialAttributes = sourceUnixFileInfo.FileSpecialAttributes
						};

						destinationUnixFileInfo.SetOwner(sourceUnixFileInfo.OwnerUserId, sourceUnixFileInfo.OwnerGroupId);

						destinationUnixFileInfo.Refresh(); // Force write all changes now
					}
					catch (Exception ex)
					{
						throw new ApplicationException("Failed to set destination attributes (Posix).", ex);
					}
				}

				Interlocked.Increment(ref _copiedFileCount);
				ShowStatistics(1);
				EndFileCopy?.Invoke(this, new FileCopyData() { SourceFilePath = sourceFilePath, DestinationFilePath = destinationFilePath });
			}
			catch (Exception ex)
			{
				throw new ApplicationException($"Failed to copy \"{sourceFilePath}\" -> \"{destinationFilePath}\"", ex);
			}
			finally
			{
				if (!maxTotalThreadsSafetySemaphoreReleased)
				{
					_maxTotalThreadsSafetySemaphore.Release();
				}

				for (; lockedTotalThreadCount > 0; lockedTotalThreadCount--)
				{
					_maxTotalThreadsSemaphore.Release();
				}

				_concurrentFilesSemaphore.Release();
				EndFileCopy?.Invoke(this, new FileCopyData() { SourceFilePath = sourceFilePath, DestinationFilePath = destinationFilePath });
			}
		}

		private async Task CopyChunksAsync(string sourceFilePath, string destinationFilePath, int bufferSize, Func<long> getNextFileChunkNumber, SemaphoreSlim resizeFileLock, CancellationToken cancellationToken)
		{
			var buffer = new byte[bufferSize];
			long totalBytesRead = 0;

			using (var writer = new FileStream(destinationFilePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, bufferSize, FileOptions.Asynchronous | FileOptions.RandomAccess))
			using (var reader = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize, FileOptions.Asynchronous | FileOptions.RandomAccess))
			{
				do
				{
					long fileChunkNumber = getNextFileChunkNumber();
					long startPosition = fileChunkNumber * bufferSize;
					long bytesToCopy = bufferSize;
					long minFileSize = startPosition + bytesToCopy;
					if (minFileSize > reader.Length)
					{
						minFileSize = reader.Length;
						bytesToCopy = reader.Length - startPosition;
					}

					if (bytesToCopy <= 0)
						break;

					await resizeFileLock.WaitAsync(cancellationToken);
					if (cancellationToken.IsCancellationRequested)
						return;

					try
					{
						if (writer.Length < minFileSize)
							writer.SetLength(minFileSize);
					}
					finally
					{
						resizeFileLock.Release();
					}
					writer.Seek(startPosition, SeekOrigin.Begin);
					reader.Seek(startPosition, SeekOrigin.Begin);

					int bytesToRead = bufferSize;

					if (bytesToRead <= 0)
						break;

					VerboseOutput?.Invoke(this, new VerboseInfo(3, () => $"Reading {bytesToRead:N0} bytes from \"{sourceFilePath}\" ({startPosition:N0}-{startPosition + bytesToRead:N0})..."));
					int bytesRead = await reader.ReadAsync(buffer, 0, bytesToRead, cancellationToken);
					if (cancellationToken.IsCancellationRequested)
						return;
					if (bytesRead <= 0)
						break;

					totalBytesRead += bytesRead;

					VerboseOutput?.Invoke(this, new VerboseInfo(3, () => $"Writing {bytesToRead:N0} bytes to \"{destinationFilePath}\" ({startPosition:N0}-{startPosition + bytesRead:N0})..."));
					await writer.WriteAsync(buffer, 0, bytesRead, cancellationToken);

					Interlocked.Add(ref _copiedByteCount, bytesRead);
					if (cancellationToken.IsCancellationRequested)
						return;
				} while (true);
			}
		}

		private async Task StartCopyOperationAsync(CancellationToken cancellationToken)
		{
			if (cancellationToken.IsCancellationRequested)
				return;

			await _copyOperationsSemaphore.WaitAsync(cancellationToken); // only 1 user operation at a time
			if (cancellationToken.IsCancellationRequested)
				return;

			_copyFileTasks = new ConcurrentHashSet<Task>();
			_exceptions = new ConcurrentBag<Exception>();
			_copyStopwatch = new Stopwatch();
			_copyStopwatch.Start();
		}

		private void EndCopyOperation(CancellationToken cancellationToken)
		{
			try
			{
				_copyStopwatch.Stop();

				if (_exceptions.Count == 1)
					throw _exceptions.FirstOrDefault();

				if (_exceptions.Count > 1)
					throw new AggregateException(_exceptions);

				ShowStatistics(0);

				if (cancellationToken.IsCancellationRequested)
				{
					VerboseOutput?.Invoke(this, new VerboseInfo(0, () => $"Copy INCOMPLETE due to cancellation."));
				}
				else
				{
					VerboseOutput?.Invoke(this, new VerboseInfo(0, () => $"Copy complete."));
				}
			}
			finally
			{
				_copyOperationsSemaphore.Release();
			}
		}

		private void ShowStatistics(int verboseLevel)
		{
			var copyStopwatchElapsed = _copyStopwatch.Elapsed;
			var totalSeconds = _copyStopwatch.Elapsed.TotalSeconds;
			var copiedFileCount = _copiedFileCount;
			var copiedByteCount = _copiedByteCount;

			if (totalSeconds <= 0.0001)
				totalSeconds = 0.0001; // Prevent divide by zero

			double bytesPerSecond = copiedByteCount / totalSeconds;
			VerboseOutput?.Invoke(this, new VerboseInfo(verboseLevel, () => $"Copied {copiedFileCount:N0} files ({copiedByteCount:N0} bytes) in {copyStopwatchElapsed.ToString("g")} ({bytesPerSecond:N0} b/s)"));
		}

		private enum PathType
		{
			Unknown,
			Directory,
			File
		}

		private PathType GetPathType(string sourcePath)
		{
			if (Directory.Exists(sourcePath))
			{
				return PathType.Directory;
			}

			if (File.Exists(sourcePath))
			{
				return PathType.File;
			}

			return PathType.Unknown;
		}

		#region IDisposable Support
		private bool _disposedValue = false; // To detect redundant calls

		protected virtual void Dispose(bool disposing)
		{
			if (!_disposedValue)
			{
				if (disposing)
				{
					// dispose managed state (managed objects).
				}

				_copyOperationsSemaphore?.Dispose();
				_concurrentFilesSemaphore?.Dispose();
				_maxFileQueueLengthSemaphore?.Dispose();
				_maxTotalThreadsSemaphore?.Dispose();
				_maxTotalThreadsSafetySemaphore?.Dispose();

				_copyOperationsSemaphore = null;
				_concurrentFilesSemaphore = null;
				_maxFileQueueLengthSemaphore = null;
				_maxTotalThreadsSemaphore = null;
				_maxTotalThreadsSafetySemaphore = null;
				_copyFileTasks = null;

				_disposedValue = true;
			}
		}

		// This code added to correctly implement the disposable pattern.
		public void Dispose()
		{
			// Do not change this code. Put cleanup code in Dispose(bool disposing) above.
			Dispose(true);
		}
		#endregion
	}
}