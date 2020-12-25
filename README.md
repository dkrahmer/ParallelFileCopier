# ParallelFileCopier

C# .NET library and CLI application to perform file or recursive directory copies in parallel.
Supports multiple files at once, multiple copy streams per files, or both together.

Parallel file copying can greatly improve file transfer speeds over high latency, high bandwidth network connections for large files or many small files.

Parallel file copying on a local drive may perform slower than a standard single-threaded file copy due to the extra overhead of multiple streams, threads and hardware I/O.

Custom behavior is configurable via properties or CLI arguments.

```
Usage: ParallelFileCopier [OPTIONS]... [SOURCE-PATH] [DESTINATION-PATH]

Required arguments:
  [SOURCE-PATH]                  path to the source file or directory
  [DESTINATION-PATH]             path to the destination file or directory

Optional arguments:
  -b, --buffer-size              copy buffer size (default: 131072)
  -e, --copy-empty-directories   copy directories, even if they are empty (default: false)
  -I, --incremental-source-path  use incremented paths for each read stream for a single file (useful for copies over SSHFS mounts)
                                 _{threadNumber} will be appended to the given ABSOLUTE base source path starting with 2 for the 2nd thread.
                                 enough incremental symbolic links should be created to handle max-threads-per-file
  -i, --use-incomplete-filename  use 'incomplete' filename while copying file data before renaming (default: true)
  -f, --max-concurrent-files     maximum concurrent file copies (default: 4)
  -h, --help                     display this help information
  -l, --max-file-queue-length    maximum copy task queue length - source directory is scanned in background (default: 50)
  -q, --quiet                    quite, no output
  -T, --max-total-threads        maximum total concurrent copy stream threads overall (default: 4)
  -t, --max-threads-per-file     maximum concurrent copy stream threads per file (default: 4)
  -v, --verbose                  increase verbose output (Example: -vvv for verbosity level 3)
```

### --incremental-source-path explanation

The --incremental-source-path feature is mainly useful for transferring large files from remote mounted file systems, such as SSHFS (FUSE).
SSHFS always uses a single connection and single thread for each file path rather than each file stream.
Using --incremental-source-path is a hack that tricks the underlying handler into making multiple connections to the remote server for a single file.
This feature requires the creation of directory symlinks on the remote file system before copying any files.

Example:
  - Remote primary source directory: /home/remote-user/download
  - Remote symlinks that point to the primary source: /home/remote-user/download_2, /home/remote-user/download_3, etc...
  - Local mount to remote directory over SSHFS with *max_conns=8: /mnt/remote-sshfs/ -> /home/remote-user/
  - Copy command: ParallelFileCopier -I /mnt/remote-sshfs/download /mnt/remote-sshfs/download/ /home/local-user/download/file.bin
    - ParallelFileCopier will enumerates the "download" directory to access the symlinks and force multiple connections to be made.

*max_conns was added in sshfs-3.7.1 and is required to allow parallel file copying. See release info: https://www.ctolib.com/article/releases/36784
