using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace KrahmerSoft.ParallelFileCopierLib
{
	public class ParallelFileCopier
	{
		private SemaphoreSlim _concurrentFilesSemaphore;
		private SemaphoreSlim _maxFileQueueLengthSemaphore;
		private SemaphoreSlim _maxTotalThreadsSemaphore;
		private SemaphoreSlim _maxTotalThreadsSafetySemaphore = new SemaphoreSlim(1);
		private ParallelFileCopierOptions _options;
		private List<Exception> _exceptions;

		public event EventHandler<FileCopyData> StartFileCopy;
		public event EventHandler<FileCopyData> EndFileCopy;
		public event EventHandler<VerboseInfo> VerboseOutput;

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
			_exceptions = new List<Exception>();

			await CopyFilesInternalAsync(sourcePath, destinationPath, 0);

			if (_exceptions.Count == 1)
				throw _exceptions[0];

			if (_exceptions.Count > 1)
				throw new AggregateException(_exceptions);

			return;
		}

		protected async Task CopyFilesInternalAsync(string sourcePath, string destinationPath, int level)
		{
			var copyTasks = new HashSet<Task>();
			try
			{
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

				if (sourceType == PathType.File)
				{
					if (destinationType == PathType.Directory)
					{
						destinationType = PathType.File;
						destinationPath = Path.Combine(destinationPath, Path.GetFileName(sourcePath));
					}

					await EnqueueCopyFileTaskAsync(copyTasks, sourcePath, destinationPath);
				}
				else
				{
					VerboseOutput?.Invoke(this, new VerboseInfo(1, () => $"Reading source subdirectories list from \"{sourcePath}\"..."));
					foreach (string sourceSubDirectory in Directory.EnumerateDirectories(sourcePath))
					{
						string subDirectoryName = Path.GetFileName(sourceSubDirectory);
						await CopyFilesInternalAsync(sourceSubDirectory, Path.Combine(destinationPath, subDirectoryName), level + 1);

						if (_exceptions.Any())
							return;
					}

					VerboseOutput?.Invoke(this, new VerboseInfo(1, () => $"Reading source files list from \"{sourcePath}\"..."));
					foreach (string sourceFilePath in Directory.EnumerateFiles(sourcePath, sourceFileMask))
					{
						string sourceFilename = Path.GetFileName(sourceFilePath);
						await EnqueueCopyFileTaskAsync(copyTasks, sourceFilePath, Path.Combine(destinationPath, sourceFilename));

						if (_exceptions.Any())
							return;
					}
				}
			}
			catch (Exception ex)
			{
				throw new ApplicationException($"Failed to copy \"{sourcePath}\" -> \"{destinationPath}\"", ex);
			}
			finally
			{
				if (level == 0)
					await Task.WhenAll(copyTasks); // wait for all queued file copies to complete
			}
		}

		private async Task EnqueueCopyFileTaskAsync(HashSet<Task> copyTasks, string sourcePath, string destinationPath)
		{
			await _maxFileQueueLengthSemaphore.WaitAsync();
			VerboseOutput?.Invoke(this, new VerboseInfo(1, () => $"Adding copy task to queue: \"{sourcePath}\" -> \"{destinationPath}\"..."));
			var task = CopyFileInternalAsync(sourcePath, destinationPath);
			copyTasks.Add(task);
			var continueTask = task.ContinueWith((antecedent) =>
			{
				_maxFileQueueLengthSemaphore.Release();
				copyTasks.Remove(task);
				if (antecedent.Exception != null)
				{
					//VerboseOutput?.Invoke(this, new VerboseInfo(-1, () => $"Copy exception: {antecedent.Exception.ToString()}"));
					lock (_exceptions)
					{
						_exceptions.Add(antecedent.Exception);
					}
				}
			});
		}

		public async Task CopyFileAsync(string sourceFilePath, string destinationFilePath)
		{
			_exceptions = new List<Exception>();
			await CopyFileInternalAsync(sourceFilePath, destinationFilePath);
		}

		public async Task CopyFileInternalAsync(string sourceFilePath, string destinationFilePath)
		{
			await _concurrentFilesSemaphore.WaitAsync();
			await _maxTotalThreadsSafetySemaphore.WaitAsync();
			bool maxTotalThreadsSafetySemaphoreReleased = false;
			int lockedTotalThreadCount = 0;

			try
			{
				if (_exceptions.Any())
					return;

				string incompleteDestinationFilePath = _options.UseIncompleteFilename ? $"{destinationFilePath}--incomplete--{Guid.NewGuid().ToString()}" : destinationFilePath;

				// Copy values to local vars
				int bufferSize = _options.BufferSize;
				int copyThreadCount = _options.MaxThreadsPerFile;

				int minChunkSize = bufferSize;
				var sourceFileInfo = new FileInfo(sourceFilePath);
				long absoluteMaxThreadCount = (int)(sourceFileInfo.Length < minChunkSize ? 1 : sourceFileInfo.Length / minChunkSize);

				if (copyThreadCount > absoluteMaxThreadCount)
					copyThreadCount = (int)absoluteMaxThreadCount;

				for (int i = 0; i < copyThreadCount; i++) // Wait until we have enough threads available for this file
				{
					await _maxTotalThreadsSemaphore.WaitAsync();
					lockedTotalThreadCount++;
				}

				_maxTotalThreadsSafetySemaphore.Release();
				maxTotalThreadsSafetySemaphoreReleased = true;

				if (_exceptions.Any())
					return;

				StartFileCopy?.Invoke(this, new FileCopyData() { SourceFilePath = sourceFilePath, DestinationFilePath = destinationFilePath });
				VerboseOutput?.Invoke(this, new VerboseInfo(0, () => $"Copying \"{sourceFilePath}\" -> \"{destinationFilePath}\"..."));

				if (File.Exists(destinationFilePath))
					File.Delete(destinationFilePath);

				long chunkSize = sourceFileInfo.Length / copyThreadCount;
				using (var writer = new FileStream(incompleteDestinationFilePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, FileOptions.Asynchronous | FileOptions.RandomAccess))
				{
					// Allocate space in the new file
					writer.SetLength(sourceFileInfo.Length);
				}

				var tasks = new List<Task>();

				for (int threadNumber = 0; threadNumber < copyThreadCount; threadNumber++)
				{
					long startPosition = threadNumber * chunkSize;
					long bytesToCopy = chunkSize;
					if (threadNumber >= copyThreadCount - 1) // last chunk might be a bit larger to handle the slack
						bytesToCopy = sourceFileInfo.Length - startPosition;

					var task = CopyChunk(sourceFilePath, incompleteDestinationFilePath, startPosition, bytesToCopy);

					tasks.Add(task);
				}

				await Task.WhenAll(tasks);

				if (incompleteDestinationFilePath != destinationFilePath)
				{
					VerboseOutput?.Invoke(this, new VerboseInfo(2, () => $"Renaming \"{incompleteDestinationFilePath}\" -> \"{destinationFilePath}\"..."));

					if (File.Exists(destinationFilePath))
						File.Delete(destinationFilePath);

					File.Move(incompleteDestinationFilePath, destinationFilePath);
				}

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

		private async Task CopyChunk(string sourceFilePath, string destinationFilePath, long startPosition, long bytesToCopy)
		{
			int bufferSize = _options.BufferSize;
			var buffer = new byte[bufferSize];

			long bytesToCopyRemaining = bytesToCopy;
			long totalBytesRead = 0;

			using (var writer = new FileStream(destinationFilePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, bufferSize, FileOptions.Asynchronous | FileOptions.RandomAccess))
			using (var reader = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize, FileOptions.Asynchronous | FileOptions.RandomAccess))
			{
				writer.Seek(startPosition, SeekOrigin.Begin);
				reader.Seek(startPosition, SeekOrigin.Begin);

				do
				{
					int bytesToRead = bufferSize;
					if (bytesToRead > bytesToCopyRemaining)
						bytesToRead = (int)bytesToCopyRemaining;

					if (bytesToRead <= 0)
						break;

					VerboseOutput?.Invoke(this, new VerboseInfo(3, () => $"Reading {bytesToRead:#,##0} bytes from \"{sourceFilePath}\" ({startPosition:#,##0}-{startPosition + bytesToRead:#,##0})..."));
					int bytesRead = await reader.ReadAsync(buffer, 0, bytesToRead);
					if (bytesRead <= 0)
						break;

					totalBytesRead += bytesRead;

					VerboseOutput?.Invoke(this, new VerboseInfo(3, () => $"Writing {bytesToRead:#,##0} bytes to \"{destinationFilePath}\" ({startPosition:#,##0}-{startPosition + bytesRead:#,##0})..."));
					await writer.WriteAsync(buffer, 0, bytesRead);

					bytesToCopyRemaining -= bytesRead;
				} while (true);
			}
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
	}
}