﻿module internal Nessos.MBrace.Runtime.Definitions.ProcessManager

open System

open Nessos.Thespian
open Nessos.Thespian.Cluster
open Nessos.Thespian.Cluster.BehaviorExtensions

open Nessos.MBrace
open Nessos.MBrace.Runtime
open Nessos.MBrace.Runtime.Logging
open Nessos.MBrace.Utils


let private triggerSytemFault (ctx: BehaviorContext<_>) reply (e: exn) =
    async {
        reply <| Exception (new SystemCorruptedException(e.Message))

        ctx.LogInfo "SYSTEM CORRUPTION DETECTED"
        ctx.LogError e
        
        //TODO!!!
        //Isolate cancellation in this async workflow
        //Deactivating self will cancel the workflow
        try
            ctx.LogInfo "Deactivating process manager..."
            do! Cluster.ClusterManager <!- fun ch -> DeActivateDefinition(ch, { Definition = empDef/"master"/"processManager"; InstanceId = 0 })
        with e -> ctx.LogWarning e

        ctx.LogInfo "TRIGGERING SYSTEM FAILURE..."
        Cluster.ClusterManager <-- FailCluster e
    }

let processManagerBehavior (processMonitor: ActorRef<Replicated<ProcessMonitor, ProcessMonitorDb>>)
                           (assemblyManager: ActorRef<AssemblyManager>)
                           (ctx: BehaviorContext<_>)
                           (msg: ProcessManager) =
    async {
        match msg with
        | ProcessManager.CreateDynamicProcess(RR ctx reply as replyChannel, requestId, processImage) ->
            //ASSUME ALL EXCEPTIONS PROPERLY HANDLED AND DOCUMENTED
            try
                //This message may be due to a retry from a client; check what is logged
                //FaultPoint
                //SystemCorruptionException => system inconsistency;; SYSTEM FAULT

                // TODO (5/2014): ProcessManager assembly load protocol has changed; this might be redundant
                let! processInfoOpt = processMonitor <!- fun ch -> Singular(Choice1Of2 <| TryGetProcessInfoByRequestId(ch, requestId))

                match processInfoOpt with
                | Some entry -> entry (*|> Process*) |> Value |> reply
                | None ->
                    ctx.LogInfo <| sprintf' "Creating new process for (request = %A, client = %A)..."  requestId processImage.CompilerId

                    let processInitializationRecord: ProcessCreationData = 
                        { 
                            ClientId = processImage.CompilerId
                            RequestId = requestId
                            Name = processImage.Name
                            Type = processImage.ReturnTypeRaw
                            TypeName = processImage.TypeName
                            Dependencies = processImage.Dependencies
                        }

                    //FaultPoint
                    //SystemException => run out of pid slots ;; SYSTEM FAULT
                    //TODO!!! Maybe not treat this as a system fault
                    let! processRecord = processMonitor <!- fun ch -> Singular(Choice1Of2 <| InitializeProcess(ch, processInitializationRecord))


                    // try cleaning up cloud process logs ; not sure if this is the best place to do it
                    try
                        let store = StoreRegistry.DefaultStoreInfo.Store
                        let reader = StoreCloudLogger.GetReader(store, processRecord.ProcessId)
                        do! reader.DeleteLogs()

                    with e -> ctx.LogError e

                    //FaultPoint
                    //BroadcastFailureException => failed to do any kind of replication ;; SYSTEM FAULT
                    do! processMonitor <!- fun ch -> Replicated(ch, Choice1Of2 <| NotifyProcessInitialized processRecord)

                    ctx.LogInfo <| sprintf' "Initialized new cloud process %A" processRecord.ProcessId

                    let processActivationRecord = {
                        Definition = empDef/"process"
                        Instance = Some processRecord.ProcessId
                        Configuration = ActivationConfiguration.FromValues [Conf.Val Configuration.RequiredAssemblies processImage.Dependencies]
                        ActivationStrategy = ActivationStrategy.processStrategy
                    }

                    //FaultPoint
                    //InvalidActivationStrategy => invalid argument;; collocation strategy not supported ;; SYSTEM FAULT
                    //CyclicDefininitionDependency => invalid configuration ;; SYSTEM FAULT
                    //OutOfNodesExceptions => unable to recover due to lack of nodes ;; SYSTEM FAULT
                    //KeyNotFoundException ;; from NodeManager => definition.Path not found in node;; SYSTEM FAULT
                    //ActivationFailureException ;; from NodeManager => failed to activate definition.Path;; reply exception
                    //SystemCorruptionException => system failure occurred during execution;; SYSTEM FAULT
                    //SystemFailureException => Global system failure;; SYSTEM FAULT
                    do! Cluster.ClusterManager <!- fun ch -> ActivateDefinition(ch, processActivationRecord)

                    ctx.LogInfo <| sprintf' "Cloud process %A is ready..." processRecord.ProcessId

                    //FaultPoint
                    //SystemCorruptionException => system inconsistency;; SYSTEM FAULT
                    let! r = Cluster.ClusterManager <!- fun ch -> ResolveActivationRefs(ch, { Definition = empDef/"process"/"scheduler"/"schedulerRaw"; InstanceId = processRecord.ProcessId })
                    //Throws
                    //InvalidCastException => SYSTEM FAULT
                    let scheduler = new RawActorRef<Scheduler>(r.[0] :?> ActorRef<RawProxy>)

                    ctx.LogInfo "Scheduler resolved."

                    //FaultPoint
                    //BroadcastFailureException => no replication at all;; root task not posted;; SYSTEM FAULT
                    do! scheduler <!- fun ch -> NewProcess(ch, processRecord.ProcessId, processImage.ComputationRaw)
                        
                    //FaultPoint
                    //BroadcastFailureException => no replication at all;; SYSTEM FAULT
                    do! processMonitor <!- fun ch -> Replicated(ch, Choice1Of2 <| NotifyProcessStarted(processRecord.ProcessId, System.DateTime.Now))

                    ctx.LogInfo <| sprintf "Cloud process %A is running..." processRecord.ProcessId

                    //FaultPoint
                    //SystemCorruptionException => system inconsistency;; SYSTEM FAULT
                    let! processInfo = processMonitor <!- fun ch -> Singular(Choice1Of2 <| TryGetProcessInfo(ch, processRecord.ProcessId))

                    processInfo |> Option.get (*|> Process*) |> Value |> reply

            with MessageHandlingException2(ActivationFailureException _ as e) ->
                    reply <| Exception (new MBraceException(sprintf "Failed to activate process: %s" e.Message))
                | MessageHandlingException2(SystemFailureException _ as e) ->
                    reply <| Exception (new SystemFailedException(e.Message))
                | MessageHandlingException2 e ->
                    do! triggerSytemFault ctx reply e
                | :? InvalidCastException as e ->
                    do! triggerSytemFault ctx reply e
                | BroadcastFailureException _ as e ->
                    do! triggerSytemFault ctx reply e
                | e ->
                    do! triggerSytemFault ctx reply e

        | ProcessManager.GetAssemblyLoadInfo(RR ctx reply, requestId, assemblyIds) ->
            try
                let! loadInfo = assemblyManager <!- fun ch -> AssemblyManager.GetInfo(ch ,assemblyIds)

                reply <| Value loadInfo

            with e ->
                ctx.LogError e
                reply <| Exception e

        | ProcessManager.LoadAssemblies(RR ctx reply, requestId, assemblies) ->
            try
                let! loadResults = assemblyManager <!- fun ch -> AssemblyManager.CacheAssemblies(ch, assemblies)

                reply <| Value loadResults

            with e ->
                ctx.LogError e
                reply <| Exception e

        | ProcessManager.RequestDependencies(RR ctx reply, ids) ->
            try
                //FaultPoint
                //-

                // always include assemblies
                let request = ids |> List.map (fun id -> true,id)
                let! images = assemblyManager <!- fun ch -> AssemblyManager.GetImages(ch, request)

                images |> Value |> reply
            with e ->
                ctx.LogError e
                reply (Exception e)

        | ProcessManager.GetProcessInfo(RR ctx reply, processId) ->
            try
                let! processInfo = processMonitor <!- fun ch -> Singular(Choice1Of2 <| TryGetProcessInfo(ch, processId))
                match processInfo with
                | None -> SystemException("Could not retrieve process info entry.") |> Reply.exn |> reply
                | Some i -> i |> Value |> reply

            with e ->
                ctx.LogError e
                reply (Exception e)

        | ProcessManager.GetAllProcessInfo replyChan ->
            //TODO!!! If there is a repication failure, the client will see it?
            processMonitor <-- Singular(Choice1Of2 <| ProcessMonitor.GetAllProcessInfo replyChan)

        | ProcessManager.KillProcess(RR ctx reply, processId) ->
            try
                //FaultPoint
                //-
                //do! processMonitor <!- fun ch -> Replicated(ch, Choice1Of2 <| CompleteProcess(processId, ExceptionResult (new ProcessKilledException("Process has been killed.") :> exn, None) |> Serializer.Serialize |> ProcessSuccess))
                do! processMonitor <!- fun ch -> Replicated(ch, Choice1Of2 <| CompleteProcess(processId, Killed))

                //FaultPoint
                //-
                do! Cluster.ClusterManager <!- fun ch -> DeActivateDefinition(ch, { Definition = empDef/"process"; InstanceId = processId })

                reply nothing
            with e ->
                ctx.LogError e
                reply (Exception e)

        | ProcessManager.ClearProcessInfo(replyChannel, processId) ->
            //TODO!!! If there is a repication failure, the client will see it?
            processMonitor <-- Replicated(replyChannel, Choice1Of2 <| FreeProcess processId)

        | ProcessManager.ClearAllProcessInfo replyChannel ->
            //TODO!!! If there is a repication failure, the client will see it?
            processMonitor <-- Replicated(replyChannel, Choice1Of2 FreeAllProcesses)
    }

