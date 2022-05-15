using KrahmerSoft.ParallelFileCopierLib;
using System;

namespace KrahmerSoft.ParallelFileCopierCli
{
	public class ParallelFileCopierOptionsCli : ParallelFileCopierOptions
	{
		public string SourcePath { get; set; }
		public string DestinationPath { get; set; }
		public int ShowVerboseLevel { get; set; }

		public bool ValidateOptions()
		{
			bool valid = true;

			if (string.IsNullOrWhiteSpace(SourcePath))
			{
				Console.Error.WriteLine("Source Path is required.");
				valid = false;
			}

			if (string.IsNullOrWhiteSpace(DestinationPath))
			{
				Console.Error.WriteLine("Destination Path is required.");
				valid = false;
			}

			if (!valid)
			{
				Console.Error.WriteLine();
				Console.Error.WriteLine("  --help for usage details.");
			}

			return valid;
		}
	}
}
