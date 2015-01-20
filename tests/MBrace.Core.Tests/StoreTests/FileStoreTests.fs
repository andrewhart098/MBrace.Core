﻿namespace MBrace.Tests

open System
open System.Threading

open MBrace
open MBrace.Continuation
open MBrace.InMemory
open MBrace.Tests

open NUnit.Framework
open FsUnit

/// Cloud file store test suite
[<TestFixture; AbstractClass>]
type ``FileStore Tests`` (nParallel : int) as self =

    let runRemote wf = self.Run wf 
    let runLocal wf = self.RunLocal wf

    let runProtected wf = 
        try self.Run wf |> Choice1Of2
        with e -> Choice2Of2 e

    /// Run workflow in the runtime under test
    abstract Run : Cloud<'T> -> 'T
    /// Evaluate workflow in the local test process
    abstract RunLocal : Cloud<'T> -> 'T

    [<Test>]
    member __.``CloudRef - simple`` () = 
        let ref = runRemote <| CloudRef.New 42
        ref.Value |> runLocal |> should equal 42

    [<Test>]
    member __.``CloudRef - Parallel`` () =
        cloud {
            let! ref = CloudRef.New [1 .. 100]
            let! (x, y) = cloud { let! v = ref.Value in return v.Length } <||> cloud { let! v = ref.Value in return v.Length }
            return x + y
        } |> runRemote |> should equal 200

    [<Test>]
    member __.``CloudRef - Distributed tree`` () =
        let tree = CloudTree.createTree 5 |> runRemote
        CloudTree.getBranchCount tree |> runRemote |> should equal 31


    [<Test>]
    member __.``CloudSequence - simple`` () = 
        let b = runRemote <| CloudSequence.New [1..10000]
        b.Cache() |> runLocal |> should equal true
        b.Count |> runLocal |> should equal 10000
        b.ToEnumerable() |> runLocal |> Seq.sum |> should equal (List.sum [1..10000])

    [<Test>]
    member __.``CloudSequence - parallel`` () =
        let ref = runRemote <| CloudSequence.New [1..10000]
        ref.ToEnumerable() |> runLocal |> Seq.length |> should equal 10000
        cloud {
            let! ref = CloudSequence.New [1 .. 10000]
            let! (x, y) = 
                cloud { let! seq = ref.ToEnumerable() in return Seq.length seq } 
                    <||>
                cloud { let! seq = ref.ToEnumerable() in return Seq.length seq } 

            return x + y
        } |> runRemote |> should equal 20000

    [<Test>]
    member __.``CloudSequence - partitioned`` () =
        cloud {
            let! seqs = CloudSequence.NewPartitioned([|1L .. 1000000L|], 1024L * 1024L)
            seqs.Length |> should be (greaterThanOrEqualTo 8)
            seqs.Length |> should be (lessThan 10)
            let! partialSums = seqs |> Array.map (fun c -> cloud { let! e = c.ToEnumerable() in return Seq.sum e }) |> Cloud.Parallel
            return Array.sum partialSums
        } |> runRemote |> should equal (Array.sum [|1L .. 1000000L|])

    [<Test>]
    member __.``CloudSequence - of deserializer`` () =
        cloud {
            use! file = CloudFile.WriteLines([1..100] |> List.map (fun i -> string i))
            let deserializer (s : System.IO.Stream) =
                seq {
                    use textReader = new System.IO.StreamReader(s)
                    while not textReader.EndOfStream do
                        yield textReader.ReadLine()
                }

            let! seq = CloudSequence.FromFile(file.Path, deserializer)
            let! ch = Cloud.StartChild(cloud { let! e = seq.ToEnumerable() in return Seq.length e })
            return! ch
        } |> runRemote |> should equal 100

    [<Test>]
    member __.``CloudFile - simple`` () =
        let file = CloudFile.WriteAllBytes [|1uy .. 100uy|] |> runRemote
        CloudFile.GetSize file |> runLocal |> should equal 100
        cloud {
            let! bytes = CloudFile.ReadAllBytes file
            return bytes.Length
        } |> runRemote |> should equal 100

    [<Test>]
    member __.``CloudFile - large`` () =
        let file =
            cloud {
                let text = Seq.init 1000 (fun _ -> "lorem ipsum dolor sit amet")
                return! CloudFile.WriteLines(text)
            } |> runRemote

        cloud {
            let! lines = CloudFile.ReadLines file
            return Seq.length lines
        } |> runRemote |> should equal 1000

    [<Test>]
    member __.``CloudFile - read from stream`` () =
        let mk a = Array.init (a * 1024) byte
        let n = 512
        cloud {
            use! f = 
                CloudFile.Create(fun stream -> async {
                    let b = mk n
                    stream.Write(b, 0, b.Length)
                    stream.Flush()
                    stream.Dispose() })

            let! bytes = CloudFile.ReadAllBytes(f)
            return bytes
        } |> runRemote |> should equal (mk n)

    [<Test>]
    member __.``CloudFile - get by name`` () =
        cloud {
            use! f = CloudFile.WriteAllBytes([|1uy..100uy|])
            let! t = Cloud.StartChild(cloud { 
                return! CloudFile.ReadAllBytes f.Path
            })

            return! t
        } |> runRemote |> should equal [|1uy .. 100uy|]

    [<Test>]
    member __.``CloudFile - disposable`` () =
        cloud {
            let! file = CloudFile.WriteAllText "lorem ipsum dolor"
            do! cloud { use file = file in () }
            return! CloudFile.ReadAllText file
        } |> runProtected |> Choice.shouldFailwith<_,exn>

    [<Test>]
    member __.``CloudFile - get files in container`` () =
        cloud {
            let! container = FileStore.GetRandomDirectoryName()
            let! fileNames = FileStore.Combine(container, Seq.map (sprintf "file%d") [1..10])
            let! files =
                fileNames
                |> Seq.map (fun f -> CloudFile.WriteAllBytes([|1uy .. 100uy|], f))
                |> Cloud.Parallel

            let! files' = CloudFile.Enumerate container
            return files.Length = files'.Length
        } |> runRemote |> should equal true

    [<Test>]
    member __.``CloudFile - attempt to write on stream`` () =
        cloud {
            use! cf = CloudFile.Create(fun stream -> async { stream.WriteByte(10uy) })
            return! CloudFile.Read(cf, fun stream -> async { stream.WriteByte(20uy) })
        } |> runProtected |> Choice.shouldFailwith<_,exn>

    [<Test>]
    member __.``CloudFile - attempt to read nonexistent file`` () =
        cloud {
            let cf = new CloudFile(Guid.NewGuid().ToString())
            return! CloudFile.Read(cf, fun s -> async { return s.ReadByte() })
        } |> runProtected |> Choice.shouldFailwith<_,exn>

    [<Test>]
    member __.``CloudDirectory - Create, populate, delete`` () =
        cloud {
            let! dir = CloudDirectory.Create ()
            let! exists = CloudDirectory.Exists dir
            exists |> shouldEqual true
            let write i = cloud {
                let! path = FileStore.GetRandomFileName dir
                let! _ = CloudFile.WriteAllText("lorem ipsum dolor", path = path)
                ()
            }

            do! Seq.init 20 write |> Cloud.Parallel |> Cloud.Ignore

            let! files = CloudFile.Enumerate dir
            files.Length |> should equal 20
            do! CloudDirectory.Delete dir
            let! exists = CloudDirectory.Exists dir
            exists |> shouldEqual false
        } |> runRemote

    [<Test>]
    member __.``CloudDirectory - dispose`` () =
        let dir, file =
            cloud {
                use! dir = CloudDirectory.Create ()
                let! path = FileStore.GetRandomFileName dir
                let! file = CloudFile.WriteAllText("lorem ipsum dolor", path = path)
                return dir, file
            } |> runRemote

        CloudDirectory.Exists dir |> runLocal |> shouldEqual false
        CloudFile.Exists file |> runLocal |> shouldEqual false