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

			using (var parallelFileCopier = new ParallelFileCopier(_optionsCli))
			{
				parallelFileCopier.VerboseOutput += HandleVerboseOutput;

				try
				{
					await parallelFileCopier.CopyFilesAsync(_optionsCli.SourcePath, _optionsCli.DestinationPath);
				}
				catch (ApplicationException ex)
				{
					Console.Error.WriteLine(ex.Message);
					return 1;
				}
				catch (ArgumentException ex)
				{
					Console.Error.WriteLine(ex.Message);
					return 1;
				}
				catch (Exception ex)
				{
					Console.Error.WriteLine(ex.ToString());
					return 1;
				}
				finally
				{
					parallelFileCopier.VerboseOutput -= HandleVerboseOutput;
				}
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
