﻿namespace MBrace.Runtime.Store

open System
open System.IO
open System.Security.AccessControl
open System.Runtime.Serialization

open MBrace.Core
open MBrace.Core.Internals
open MBrace.Runtime
open MBrace.Runtime.Utils
open MBrace.Runtime.Utils.Retry

/// Cloud file store implementation targeting local file systems.
[<Sealed; DataContract>]
type FileSystemStore private (rootPath : string, defaultDirectory : string) =

    [<DataMember(Name = "RootPath")>]
    let rootPath = rootPath

    let normalize defaultDirectory (path : string) =
        if Path.IsPathRooted path then
            let nf = Path.GetFullPath path
            if nf.StartsWith rootPath then nf
            else
                let msg = sprintf "invalid path '%O'." path
                raise <| new FormatException(msg)
        else
            Path.Combine(defaultDirectory, path) |> Path.GetFullPath

    [<DataMember(Name = "DefaultDirectory")>]
    let defaultDirectory = normalize rootPath defaultDirectory

    let normalize p = normalize defaultDirectory p

    // IOException will be signifies attempt to perform concurrent writes of file.
    // An exception to this rule is FileNotFoundException, which is a subtype of IOException.
    static let fileAccessRetryPolicy = 
        Policy(fun retries exn ->
            match exn with
            | :? FileNotFoundException 
            | :? DirectoryNotFoundException -> None
            | :? UnauthorizedAccessException when retries < 5 -> TimeSpan.FromMilliseconds 200. |> Some
            | :? IOException -> TimeSpan.FromMilliseconds 200. |> Some
            | _ -> None)

    static let fileDeleteRetryPolicy = 
        Policy(fun retries exn ->
            match exn with
            | :? FileNotFoundException 
            | :? DirectoryNotFoundException -> None
            | :? UnauthorizedAccessException when retries < 10 -> TimeSpan.FromMilliseconds 200. |> Some
            | :? IOException when retries < 10 -> TimeSpan.FromMilliseconds 200. |> Some
            | _ -> None)

    let initDir dir =
        retry (RetryPolicy.Retry(2, 0.5<sec>))
                (fun () ->
                    if not <| Directory.Exists dir then
                        Directory.CreateDirectory dir |> ignore)

    let isCaseSensitive =
        let file = Path.Combine(rootPath, mkUUID().ToLower())
        File.CreateText(file).Close()
        try not <| File.Exists(file.ToUpper())
        finally File.Delete file

    static let getETag (path : string) : ETag = 
        let fI = new FileInfo(path)
        let lwt = fI.LastWriteTimeUtc
        let lwtB = BitConverter.GetBytes lwt.Ticks
        let size = BitConverter.GetBytes fI.Length
        Array.append lwtB size |> Convert.ToBase64String

    /// <summary>
    ///     Creates a new FileSystemStore client instance.
    /// </summary>
    /// <param name="rootPath">Local or UNC path to root directory of store instance.</param>
    /// <param name="defaultDirectory">Default directory used for resolving relative paths in store. Defaults to the root path.</param>
    /// <param name="create">Create root directory if missing. Defaults to false.</param>
    /// <param name="cleanup">Cleanup root directory if it exists. Defaults to false.</param>
    static member Create(rootPath : string, ?defaultDirectory : string, ?create : bool, ?cleanup : bool) =
        let create = defaultArg create false
        if not <| Path.IsPathRooted rootPath then invalidArg "rootPath" "Must be absolute path."
        if create then
            ignore <| WorkingDirectory.CreateWorkingDirectory(rootPath, ?cleanup = cleanup)
        elif not <| Directory.Exists rootPath then
            raise <| new DirectoryNotFoundException(rootPath)

        let defaultDirectory = defaultArg defaultDirectory rootPath
        new FileSystemStore(rootPath, defaultDirectory)

    /// <summary>
    ///     Creates a cloud file system store that can be shared between local processes.
    /// </summary>
    static member CreateSharedLocal(?defaultDirectory : string) =
        let path = Path.Combine(Path.GetTempPath(), "mbrace-shared", "fileStore")
        FileSystemStore.Create(path, ?defaultDirectory = defaultDirectory, create = true, cleanup = false)

    /// <summary>
    ///     Creates a cloud file system store that is unique to the current process.
    /// </summary>
    static member CreateUniqueLocal(?defaultDirectory : string) =
        let path = Path.Combine(WorkingDirectory.GetDefaultWorkingDirectoryForProcess(), "localStore")
        FileSystemStore.Create(path, ?defaultDirectory = defaultDirectory, create = true, cleanup = true)

    /// Creates a file system store instance that is unique.
    static member CreateRandomLocal() =
        FileSystemStore.Create(WorkingDirectory.GetRandomWorkingDirectory(), create = true, cleanup = true)

    /// FileSystemStore root path
    member __.RootPath = rootPath

    interface ICloudFileStore with
        member __.Name = "FileSystemStore"
        member __.Id = rootPath
        member __.RootDirectory = rootPath
        member __.DefaultDirectory = defaultDirectory
        member __.IsCaseSensitiveFileSystem = isCaseSensitive
        member __.WithDefaultDirectory newDirectory = new FileSystemStore(rootPath, newDirectory) :> _
        member __.IsPathRooted(path : string) = Path.IsPathRooted path
        member __.GetDirectoryName(path : string) = Path.GetDirectoryName path
        member __.GetFileName(path : string) = Path.GetFileName path
        member __.Combine(paths : string []) = Path.Combine paths
        member __.GetRandomDirectoryName () = Path.Combine(rootPath, mkUUID())

        member __.GetFileSize(path : string) = async {
            return let fI = new FileInfo(normalize path) in fI.Length
        }

        member __.GetLastModifiedTime(path : string, isDirectory : bool) = async {
            return
                let path = normalize path in
                if isDirectory then
                    let dI = new DirectoryInfo(path)
                    if dI.Exists then new DateTimeOffset(dI.LastWriteTime)
                    else raise <| new DirectoryNotFoundException(path)
                else
                    let fI = new FileInfo(path)
                    if fI.Exists then new DateTimeOffset(fI.LastWriteTime)
                    else raise <| new FileNotFoundException(path)
        }

        member __.FileExists(file : string) = async {
            return File.Exists(normalize file)
        }

        member __.DeleteFile(file : string) = async {
            try return! retryAsync fileDeleteRetryPolicy (async { return File.Delete(normalize file) })
            with :? DirectoryNotFoundException -> return ()
        }

        member __.EnumerateFiles(directory : string) = async {
            return Directory.EnumerateFiles(normalize directory) |> Seq.toArray
        }

        member __.DirectoryExists(directory : string) = async {
            return Directory.Exists(normalize directory)
        }

        member __.CreateDirectory(directory : string) = async {
            return Directory.CreateDirectory(normalize directory) |> ignore
        }

        member __.DeleteDirectory(container : string, recursiveDelete : bool) = async {
            try return! retryAsync fileDeleteRetryPolicy (async { return Directory.Delete(normalize container, recursiveDelete) })
            with :? DirectoryNotFoundException -> ()
        }

        member __.EnumerateDirectories(directory) = async {
            return Directory.EnumerateDirectories(normalize directory) |> Seq.toArray
        }

        member __.BeginWrite(path : string) = async {
            let path = normalize path
            initDir <| Path.GetDirectoryName path
            return! retryAsync fileAccessRetryPolicy <| async { return  new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None) :> Stream }
        }

        member __.BeginRead(path : string) = async {
            try return! retryAsync fileAccessRetryPolicy <| async { return new FileStream(normalize path, FileMode.Open, FileAccess.Read, FileShare.Read) :> Stream }
            with :? DirectoryNotFoundException -> return raise <| new FileNotFoundException(path)
        }

        member self.UploadFromStream(target : string, source : Stream) = async {
            let target = normalize target
            initDir <| Path.GetDirectoryName target
            use! fs = retryAsync fileAccessRetryPolicy <| async { return new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None) }
            do! source.CopyToAsync fs |> Async.AwaitTaskCorrect
        }

        member self.DownloadToStream(source : string, target : Stream) = async {
            use! fs = (self :> ICloudFileStore).BeginRead source
            do! fs.CopyToAsync target |> Async.AwaitTaskCorrect
        }

        member self.DownloadToLocalFile(source : string, target : string) = async {
            let source = normalize source
            do! retryAsync fileAccessRetryPolicy <| async { File.Copy(source, target, overwrite = true) }
        }

        member self.UploadFromLocalFile(source : string, target : string) = async {
            let target = normalize target
            initDir <| Path.GetDirectoryName target
            do! retryAsync fileAccessRetryPolicy <| async { File.Copy(source, target, overwrite = true) }
        }

        member __.TryGetETag (path : string) = async {
            return
                let path = normalize path in
                if File.Exists path then Some(getETag path)
                else None
        }

        member __.WriteETag(path : string, writer : Stream -> Async<'R>) : Async<ETag * 'R> = async {
            let path = normalize path
            initDir <| Path.GetDirectoryName path
            use! fs = retryAsync fileAccessRetryPolicy <| async { return new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None) }
            let! r = writer fs
            // flush to disk before closing stream to ensure etag is correct
            if fs.CanWrite then fs.Flush(flushToDisk = true)
            return getETag path, r
        }

        member __.ReadETag(path : string, etag : ETag) = async {
            let path = normalize path
            let! fs = retryAsync fileAccessRetryPolicy <| async { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read) }
            if etag = getETag path then
                return Some(fs :> Stream)
            else
                return None
        }