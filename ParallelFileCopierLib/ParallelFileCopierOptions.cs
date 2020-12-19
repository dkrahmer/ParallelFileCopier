namespace KrahmerSoft.ParallelFileCopierLib
{
	public class ParallelFileCopierOptions
	{
		public int MaxConcurrentFiles { get; set; } = 4;
		public int MaxThreadsPerFile { get; set; } = 4;
		public int MaxTotalThreads { get; set; } = 4;
		public int BufferSize { get; set; } = 128 * 1024;
		public int MaxFileQueueLength { get; set; } = 50;
		public bool UseIncompleteFilename { get; set; } = true;
		public bool CopyEmptyDirectories { get; set; } = false;
	}
}
