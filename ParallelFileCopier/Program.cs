using KrahmerSoft.ParallelFileCopierLib;
using System;
using System.Threading.Tasks;

namespace KrahmerSoft.ParallelFileCopierCli
{
	internal class Program
	{
		private static ParallelFileCopierOptionsCli _optionsCli;
		private static int Main(string[] args)
		{
			return MainAsync(args).GetAwaiter().GetResult();
		}

		private static async Task<int> MainAsync(string[] args)
		{
			_optionsCli = new ParallelFileCopierOptionsCli();

			CommandLineParser.ParseArguments(args, _optionsCli);

			if (!_optionsCli.ValidateOptions())
				return 1;

			var parallelFileCopier = new ParallelFileCopier(_optionsCli);
			parallelFileCopier.VerboseOutput += HandleVerboseOutput;

			try
			{
				await parallelFileCopier.CopyFilesAsync(_optionsCli.SourcePath, _optionsCli.DestinationPath);
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine(ex.ToString());
			}

			return 0;
		}

		private static void HandleVerboseOutput(object sender, VerboseInfo e)
		{
			if (e.VerboseLevel > _optionsCli.ShowVerboseLevel)
				return;

			Console.WriteLine($"{e.Message}");
		}
	}
}
