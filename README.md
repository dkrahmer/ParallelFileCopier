# ParallelFileCopier

C# .NET library and CLI application to perform file or recursive directory copies in parallel.
Supports multiple files at once, multiple copy streams per files, or both together.

Custom behavior is configurable via properties or CLI arguments.

```
Usage: ParallelFileCopier [OPTIONS]... [SOURCE-PATH] [DESTINATION-PATH]

Required arguments:
  [SOURCE-PATH]                  path to the source file or directory
  [DESTINATION-PATH]             path to the destination file or directory

Optional arguments:
  -f, --max-concurrent-files     maximum concurrent file copies (default: 8)
  -t, --max-threads-per-file     maximum concurrent copy stream threads per file (default: 8)
  -T, --max-total-threads        maximum total concurrent copy stream threads overall (default: 8)
  -b, --buffer-size              copy buffer size (default: 131072)
  -l, --max-file-queue-length    maximum copy task queue length - source directory is scanned in background (default: 50)
  -i, --use-incomplete-filename  use 'incomplete' filename while copying file data before renaming (default: true)
  -q, --quiet                    quite, no output
  -v, --verbose                  increase verbose output (Example: -vvv for verbosity level 3)
  -h, --help                     display this help information
```