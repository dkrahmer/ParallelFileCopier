using KrahmerSoft.ParallelFileCopierLib;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace KrahmerSoft.ParallelFileCopierCli
{
	internal class Program
	{
		private static ParallelFileCopierOptionsCli _optionsCli;
		private static CancellationTokenSource _cancellationTokenSource;
		private static bool _sigintReceived;

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

			_cancellationTokenSource = new CancellationTokenSource();
			ListenForCancelation();

			using (var parallelFileCopier = new ParallelFileCopier(_optionsCli))
			{
				parallelFileCopier.VerboseOutput += HandleVerboseOutput;

				try
				{
					await parallelFileCopier.CopyFilesAsync(_optionsCli.SourcePath, _optionsCli.DestinationPath, _cancellationTokenSource.Token);

					if (_cancellationTokenSource.IsCancellationRequested)
						return 1;

					return 0;
				}
				catch (OperationCanceledException ex)
				{
					return 1;
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
		}

		private static void ListenForCancelation()
		{
			Console.CancelKeyPress += (_, ea) =>
			{
				_sigintReceived = true;
				// Tell .NET to not terminate the process
				ea.Cancel = true;

				Console.WriteLine("Received SIGINT (Ctrl+C) - Gracefully canceling...");
				_cancellationTokenSource.Cancel();
			};

			AppDomain.CurrentDomain.ProcessExit += (_, _) =>
			{
				if (!_sigintReceived)
					return; // ignore - normal termination

				Console.WriteLine("Received SIGTERM - Gracefully canceling...");
				_cancellationTokenSource.Cancel();
			};
		}

		private static void HandleVerboseOutput(object sender, VerboseInfo e)
		{
			if (e.VerboseLevel > _optionsCli.ShowVerboseLevel)
				return;

			Console.WriteLine($"{e.Message}");
		}
	}
}
