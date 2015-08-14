﻿namespace MBrace.ThreadPool.Internals

open System
open System.Threading
open System.Threading.Tasks

open MBrace.Core
open MBrace.Core.Internals
open MBrace.Runtime.Utils

#nowarn "444"

/// ThreadPool parallelism provider
[<Sealed; AutoSerializable(false)>]
type ThreadPoolParallelismProvider private (memoryEmulation : MemoryEmulation, logger : ICloudLogger, faultPolicy : FaultPolicy) =

    static let nullLogger = { new ICloudLogger with member __.Log _ = () }

    static let mkNestedCts (ct : ICloudCancellationToken) = 
        ThreadPoolCancellationTokenSource.CreateLinkedCancellationTokenSource [| ct |] :> ICloudCancellationTokenSource

    /// Gets the current memory emulation mode of the provider instance.
    member __.MemoryEmulation = memoryEmulation

    /// Gets the current logger instance used by the provider instance.
    member __.Logger = logger

    /// <summary>
    ///     Creates a new threadpool runtime instance.
    /// </summary>
    /// <param name="logger">Logger for runtime. Defaults to no logging.</param>
    /// <param name="memoryEmulation">Memory semantics used for parallelism. Defaults to shared memory.</param>
    static member Create (?logger : ICloudLogger, ?memoryEmulation : MemoryEmulation) = 
        let logger = defaultArg logger nullLogger
        let memoryEmulation = defaultArg memoryEmulation MemoryEmulation.Shared
        new ThreadPoolParallelismProvider(memoryEmulation, logger, FaultPolicy.NoRetry)
        
    interface IParallelismProvider with
        member __.CreateLinkedCancellationTokenSource (parents : ICloudCancellationToken[]) = async {
            return ThreadPoolCancellationTokenSource.CreateLinkedCancellationTokenSource parents :> _
        }

        member __.ProcessId = sprintf "In-Memory cloud process (pid:%d)" <| System.Diagnostics.Process.GetCurrentProcess().Id
        member __.JobId = sprintf "TheadId %d" <| System.Threading.Thread.CurrentThread.ManagedThreadId
        member __.Logger = logger
        member __.IsTargetedWorkerSupported = false
        member __.GetAvailableWorkers () = async {
            return [| ThreadPoolWorker.LocalInstance :> IWorkerRef |]
        }

        member __.CurrentWorker = ThreadPoolWorker.LocalInstance :> IWorkerRef

        member __.FaultPolicy = faultPolicy
        member __.WithFaultPolicy newFp = new ThreadPoolParallelismProvider(memoryEmulation, logger, newFp) :> IParallelismProvider

        member __.IsForcedLocalParallelismEnabled = MemoryEmulation.isShared memoryEmulation
        member __.WithForcedLocalParallelismSetting (setting : bool) =
            if setting && memoryEmulation <> MemoryEmulation.Shared then 
                new ThreadPoolParallelismProvider(MemoryEmulation.Shared, logger, faultPolicy) :> IParallelismProvider
            else
                __ :> IParallelismProvider

        member __.ScheduleParallel computations = cloud {
            return! Combinators.Parallel(mkNestedCts, memoryEmulation, Seq.map fst computations)
        }

        member __.ScheduleChoice computations = cloud {
            return! Combinators.Choice(mkNestedCts, memoryEmulation, Seq.map fst computations)
        }

        member __.ScheduleLocalParallel computations = Combinators.Parallel(mkNestedCts, MemoryEmulation.Shared, computations)
        member __.ScheduleLocalChoice computations = Combinators.Choice(mkNestedCts, MemoryEmulation.Shared, computations)

        member __.ScheduleStartAsTask (workflow:Cloud<'T>, _ :FaultPolicy, ?cancellationToken:ICloudCancellationToken, ?target:IWorkerRef, ?taskName:string) = cloud {
            ignore taskName
            target |> Option.iter (fun _ -> raise <| new System.NotSupportedException("Targeted workers not supported in In-Memory runtime."))
            let! resources = Cloud.GetResourceRegistry()
            return Combinators.StartAsTask(workflow, memoryEmulation, resources, ?cancellationToken = cancellationToken) :> ICloudTask<'T>
        }