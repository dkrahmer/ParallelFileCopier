using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace KrahmerSoft.ParallelFileCopierCli
{
	internal class CommandLineParser
	{
		public static int ParseArguments(string[] args, ParallelFileCopierOptionsCli optionsCli)
		{
			return ParseArgumentsInternal(args, optionsCli, isInternal: false);
		}

		private static int ParseArgumentsInternal(string[] args, ParallelFileCopierOptionsCli optionsCli, bool isInternal)
		{
			if (args == null)
				return 0;

			var argsNotUsedCount = 0;
			var argIndex = 0;
			var requiredArgumentCandidates = new List<string>();

			try
			{
				while (argIndex < args.Length)
				{
					string arg = args[argIndex];
					switch (arg)
					{
						case "--max-concurrent-files":
						case "-f":
							optionsCli.MaxConcurrentFiles = GetNextArgAsInt(args, ref argIndex);
							break;

						case "--max-threads-per-file":
						case "-t":
							optionsCli.MaxThreadsPerFile = GetNextArgAsInt(args, ref argIndex);
							break;

						case "--max-total-threads":
						case "-T":
							optionsCli.MaxTotalThreads = GetNextArgAsInt(args, ref argIndex);
							break;

						case "--buffer-size":
						case "-b":
							optionsCli.BufferSize = GetNextArgAsInt(args, ref argIndex);
							break;

						case "--max-file-queue-length":
						case "-l":
							optionsCli.MaxFileQueueLength = GetNextArgAsInt(args, ref argIndex);
							break;

						case "--min-chunks-per-thread":
						case "-c":
							optionsCli.MinChunksPerThread = GetNextArgAsInt(args, ref argIndex);
							break;

						case "--use-incomplete-filename":
						case "-i":
							optionsCli.UseIncompleteFilename = GetNextArgAsBool(args, ref argIndex);
							break;

						case "--incremental-source-path":
						case "-I":
							optionsCli.IncrementalSourcePath = GetNextArg(args, ref argIndex).TrimEnd(new char[] { '/', '\\' });
							break;

						case "--copy-empty-directories":
						case "-e":
							optionsCli.CopyEmptyDirectories = true;
							break;

						case "--verbose":
						case "-v":
							optionsCli.ShowVerboseLevel++;
							break;

						case "--quiet":
						case "-q":
							optionsCli.ShowVerboseLevel = -1;
							break;

						case "--skip-identical":
						case "-s":
							optionsCli.SkipExistingIdenticalFiles = true;
							break;

						case "--version":
							ShowVersion();
							Environment.Exit(0);
							break;

						case "--help":
						case "-h":
						case "/h":
						case "/?":
							ShowHelp();
							Environment.Exit(0);
							break;

						default:
							int argsNotUsedCountTemp = ParseMergedArgs(arg, optionsCli);

							if (argsNotUsedCountTemp == 1)
								requiredArgumentCandidates.Add(arg);

							argsNotUsedCount += argsNotUsedCountTemp;
							break;
					}

					argIndex++;
				}
			}
			catch (ArgumentException ex)
			{
				Console.Error.WriteLine(ex.Message);
				Environment.Exit(1);
			}

			if (isInternal)
				return argsNotUsedCount;

			if (requiredArgumentCandidates.Count != 2)
			{
				Console.Error.WriteLine($"Invalid arguments: {string.Join(", ", requiredArgumentCandidates)}");
				Environment.Exit(1);
			}

			optionsCli.SourcePath = requiredArgumentCandidates[0];
			argsNotUsedCount--;

			optionsCli.DestinationPath = requiredArgumentCandidates[1];
			argsNotUsedCount--;

			return argsNotUsedCount;
		}

		private static void ShowVersion()
		{
			Console.WriteLine(Assembly.GetExecutingAssembly().GetName().Version);
		}

		private static void ShowHelp()
		{
			var defaultValues = new ParallelFileCopierOptionsCli();

			Console.Write($"ParallelFileCopier v");
			ShowVersion();
			Console.WriteLine();
			Console.WriteLine($"Usage: ParallelFileCopier [OPTIONS]... [SOURCE-PATH] [DESTINATION-PATH]");
			Console.WriteLine();
			Console.WriteLine($"Required arguments:");
			Console.WriteLine($"  [SOURCE-PATH]                  path to the source file or directory");
			Console.WriteLine($"  [DESTINATION-PATH]             path to the destination file or directory");
			Console.WriteLine();
			Console.WriteLine($"Optional arguments:");
			Console.WriteLine($"  -b, --buffer-size              copy buffer size (default: {defaultValues.BufferSize})");
			Console.WriteLine($"  -c, --min-chunks-per-thread    minimum chunks per copy thread - determines");
			Console.WriteLine($"                                 max threads per file based on total file size");
			Console.WriteLine($"                                 (default: {defaultValues.MinChunksPerThread})");
			Console.WriteLine($"  -e, --copy-empty-directories   copy directories, even if they are empty");
			Console.WriteLine($"                                 (default: {(defaultValues.CopyEmptyDirectories ? "true" : "false")})");
			Console.WriteLine($"  -f, --max-concurrent-files     maximum concurrent file copies (default: {defaultValues.MaxConcurrentFiles})");
			Console.WriteLine($"  -h, --help                     display this help information");
			Console.WriteLine($"  -I, --incremental-source-path  use incremented paths for each read stream");
			Console.WriteLine($"                                 for a single file (useful for copies over");
			Console.WriteLine($"                                 SSHFS mounts)");
			Console.WriteLine($"                                 _{{threadNumber}} will be appended to the");
			Console.WriteLine($"                                 given ABSOLUTE base source path starting");
			Console.WriteLine($"                                 with 2 for the 2nd thread.");
			Console.WriteLine($"                                 enough incremental symbolic links should be");
			Console.WriteLine($"                                 created to handle max-threads-per-file");
			Console.WriteLine($"  -i, --use-incomplete-filename  use 'incomplete' filename while copying file");
			Console.WriteLine($"                                 data before renaming (default: {(defaultValues.UseIncompleteFilename ? "true" : "false")})");
			Console.WriteLine($"  -l, --max-file-queue-length    maximum copy task queue length - source");
			Console.WriteLine($"                                 directory is scanned in background");
			Console.WriteLine($"                                 (default: {defaultValues.MaxFileQueueLength})");
			Console.WriteLine($"  -q, --quiet                    quite, no output (except for errors)");
			Console.WriteLine($"  -s, --skip-identical           skip existing files with same size and last");
			Console.WriteLine($"                                 write date in destination (default: {defaultValues.SkipExistingIdenticalFiles})");
			Console.WriteLine($"  -T, --max-total-threads        maximum total concurrent copy stream threads");
			Console.WriteLine($"                                 overall (default: {defaultValues.MaxTotalThreads})");
			Console.WriteLine($"  -t, --max-threads-per-file     maximum concurrent copy stream threads per");
			Console.WriteLine($"                                 file (default: {defaultValues.MaxThreadsPerFile})");
			Console.WriteLine($"  -v, --verbose                  increase verbose output");
			Console.WriteLine($"                                 (Example: -vvv for verbosity level 3)");
			Console.WriteLine($"  --version                      output the version");
		}

		private static string GetNextArg(string[] args, ref int currentArgIndex)
		{
			int nextArgIndex = currentArgIndex + 1;
			if (nextArgIndex >= args.Length)
				return null;

			currentArgIndex = nextArgIndex;
			return args[currentArgIndex];
		}

		private static int GetNextArgAsInt(string[] args, ref int currentArgIndex)
		{
			string strVal = GetNextArg(args, ref currentArgIndex);
			if (!int.TryParse(strVal, out int intVal))
				throw new ArgumentException($"Invalid data argument data. Expected an integer value: '{args[currentArgIndex - 1]} {strVal}'");

			return intVal;
		}

		private static bool GetNextArgAsBool(string[] args, ref int currentArgIndex)
		{
			string strVal = GetNextArg(args, ref currentArgIndex);
			if (!bool.TryParse(strVal, out bool boolVal))
				throw new ArgumentException($"Invalid data argument data. Expected a boolean value (1/0, true/false): '{args[currentArgIndex - 1]} {strVal}'");

			return boolVal;
		}

		private static int ParseMergedArgs(string mergeArgs, ParallelFileCopierOptionsCli optionsCli)
		{
			if (mergeArgs == null || mergeArgs.Length <= 2 || mergeArgs[0] != '-' || mergeArgs[1] == '-')
				return 1;

			var args = mergeArgs.Substring(1).Select(ch => $"-{ch}").ToArray();

			return ParseArgumentsInternal(args, optionsCli, isInternal: true);
		}
	}
}
