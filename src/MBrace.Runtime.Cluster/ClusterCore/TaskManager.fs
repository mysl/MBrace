﻿module internal Nessos.MBrace.Runtime.Definitions.TaskManager

open System

open Nessos.Thespian
open Nessos.Thespian.ConcurrencyTools
open Nessos.Thespian.Cluster
open Nessos.Thespian.Cluster.BehaviorExtensions

open Nessos.MBrace.Runtime
open Nessos.MBrace.Utils

type State = {
    TasksRetryRequested: Set<TaskId>
    TasksProcessing: Set<TaskId>
    Scheduler: ActorRef<Scheduler>
} with
    static member Empty = {
        TasksRetryRequested = Set.empty
        TasksProcessing = Set.empty
        Scheduler = ActorRef.empty()
    }

let newTaskId () : TaskId = Guid.NewGuid().ToString()

let taskManagerBehavior (processId: ProcessId)
                        (workerPool: ActorRef<WorkerPool>)
                        (taskLog: ActorRef<AsyncReplicated<TaskLog, TaskLogEntry[]>>)
                        (ctx: BehaviorContext<_>)
                        (state: State) 
                        (msg: TaskManager) =

    let postTask parentTaskId worker taskPayload (tasksToRetry: Set<TaskId>) = async {
        let (processId, taskId), _ = taskPayload

        try
            ctx.LogInfo <| sprintf' "Posting task (%A, %A) to %A" processId taskId (ActorRef.toUniTcpAddress worker |> Option.get)

            do! worker <-!- ExecuteTask taskPayload

            ctx.LogInfo <| sprintf' "(%A, %A) task posted." processId taskId

            return tasksToRetry
        with :? CommunicationException
            | FailureException _ ->
                ctx.Self <-- RetryTask(parentTaskId, taskPayload)

                ctx.LogInfo <| sprintf' "TaskManager: (%A, %A) post failed. Set to retry." processId taskId

                let (_, taskId), _ = taskPayload

                return tasksToRetry |> Set.add taskId
    }

    let recoverTask parentTaskId ((processId, taskId) as taskHeader) processBody state = async {
        ctx.LogInfo <| sprintf' "(%A, %A) retrying post." processId taskId

        //select new worker
        let! worker = workerPool <!- fun ch -> Select(ch)
        match worker with
        | Some worker ->

            let payload = taskHeader, processBody

            //replace log entry
            do! taskLog <-!- AsyncReplicated(Choice1Of2 <| TaskLog.Log([| taskId, parentTaskId, worker, payload |]))
            //do! taskLog <!- fun ch -> SyncReplicated(ch, TaskLog.Log([| taskId, parentTaskId, worker, payload |]))

            //repost
            let tasksRetryRequested' = state.TasksRetryRequested |> Set.remove taskId
            let! tasksRetryRequested'' = postTask parentTaskId worker payload tasksRetryRequested'

            return { state with TasksRetryRequested = tasksRetryRequested'' }
        | _ ->
            ctx.Self <-- msg
            return state
    }

    async {
        match msg with
        | SetScheduler scheduler ->
            return { state with Scheduler = scheduler }

        | RecoverTasks tasks ->
            return! tasks |> Array.foldAsync (fun state' (parentTaskId, (taskHeader, processBody)) -> recoverTask parentTaskId taskHeader processBody state') state
        | RetryTask(parentTaskId, (((processId, taskId) as taskHeader), processBody)) ->
            ctx.LogInfo <| sprintf' "Attempting retry for (%A, %A)..." processId taskId
            //check if retry was requested. If not then this is a duplicate retry attempt
            if state.TasksRetryRequested |> Set.contains taskId then
                return! recoverTask parentTaskId taskHeader processBody state
            else return state

        | CreateRootTask(confirmationReplyChannel, processId, processBody) ->
            //ASSUME ALL EXCEPTIONS PROPERLY HANDLED AND DOCUMENTED

            ctx.LogInfo <| sprintf' "Creating root task for process %A." processId

            //generate task id
            let taskId = newTaskId()

            ctx.LogInfo <| sprintf' "(%A) assuming task id %A." processId taskId

            //select worker
            //FaultPoint ;; nothing
            let! worker = workerPool <!- fun ch -> Select(ch)
            match worker with
            | Some worker ->
                let taskPayload = (processId, taskId), processBody

                //log task
                //FaultPoint INDIRECT
                //BroadcastFailureException => no replication at all
                taskLog <-- SyncReplicated(confirmationReplyChannel, Choice1Of2 <| TaskLog.Log([| taskId, None, worker, taskPayload |]))

                ctx.LogInfo <| sprintf' "(%A, %A) task created." processId taskId

                //execute root task
                let! tasksRetryRequested' = postTask None worker taskPayload state.TasksRetryRequested

                return { state with TasksRetryRequested = tasksRetryRequested' }
            | None ->
                ctx.LogInfo "No available worker. Retrying..."
                ctx.Self <-- msg
                return state

        | CreateTasks((RR ctx reply) as confirmationReplyChannel, (processId, parentTaskId), processBodies) ->
            
            match processBodies with
            | [processBody] -> //single child task generated by scheduler
                //generate task id
                let taskId = newTaskId ()

                ctx.LogInfo <| sprintf' "Created child task (%A, %A) of %A." processId taskId parentTaskId

                //select worker
                let! worker = workerPool <!- fun ch -> Select(ch)
                match worker with
                | Some worker ->

                    let taskPayload = (processId, taskId), processBody

                    //log task
                    taskLog <-- AsyncReplicated(Choice1Of2 <| TaskLog.Log([| taskId, Some parentTaskId, worker, taskPayload |]))
                    //do! taskLog <!- fun ch -> SyncReplicated(ch, TaskLog.Log([| taskId, Some parentTaskId, worker, taskPayload |]))

                    ctx.LogInfo <| sprintf' "Logged (%A, %A) of %A." processId taskId parentTaskId

                    //unlog parent task and confirm task creation
                    taskLog <-- AsyncReplicated(Choice1Of2 <| Unlog [| parentTaskId |])
                    reply nothing
                    ctx.LogInfo <| sprintf' "Unlogged parent %A of (%A, %A)" parentTaskId processId taskId
                    //taskLog <-- SyncReplicated(confirmationReplyChannel, Unlog [| parentTaskId |])

                    //execute task
                    let! tasksRetryRequested' = postTask (Some parentTaskId) worker taskPayload state.TasksRetryRequested

                    let tasksProcessing' = state.TasksProcessing |> Set.remove parentTaskId

                    return { state with TasksRetryRequested = tasksRetryRequested'; TasksProcessing = tasksProcessing' }
                | _ ->
                    ctx.LogInfo "No available worker. Retrying..."
                    ctx.Self <-- msg
                    return state
            | processBodies -> //parallel tasks generated by scheduler
                let taskCount = processBodies |> List.length
                
                //select workers
                let! workers = workerPool <!- fun ch -> SelectMany(ch, taskCount)
                match workers with
                | Some workers ->
                    //generate taskIds
                    let taskIds = [ for _ in 1 .. taskCount -> newTaskId() ]

                    ctx.LogInfo <| sprintf' "Created %d parallel child tasks of %A in %A." taskIds.Length parentTaskId processId 

                    //log tasks
                    let logEntries = 
                        workers |> Array.toList
                                |> List.zip3 taskIds processBodies
                                |> List.map (fun (taskId', processBody, worker) -> taskId', Some parentTaskId, worker, ((processId, taskId'), processBody))
                                |> List.toArray

                    taskLog <-- AsyncReplicated(Choice1Of2 <| TaskLog.Log logEntries)
                    //do! taskLog <!- fun ch -> SyncReplicated(ch, TaskLog.Log logEntries)
                    ctx.LogInfo <| sprintf' "Logged children of %A in %A" parentTaskId processId

                    //All child tasks have been logged
                    //unlog parent task and confirm parallel task creation
                    taskLog <-- AsyncReplicated(Choice1Of2 <| Unlog [| parentTaskId |])
                    reply nothing
                    ctx.LogInfo <| sprintf' "Unlogged parent %A in %A" parentTaskId processId
                    //taskLog <-- SyncReplicated(confirmationReplyChannel, Unlog [| parentTaskId |])

                    //execute tasks
                    let! tasksRetryRequested' = 
                        logEntries |> Seq.toList |> List.foldAsync (fun tasksRetry (_, parentTaskId, worker, taskPayload) -> 
//                            let w = match worker with
//                                    | :? ReliableActorRef<Worker> as rw -> rw.UnreliableRef
//                                    | _ -> worker
                            postTask parentTaskId worker taskPayload tasksRetry) state.TasksRetryRequested
                
                    let tasksProcessing' = state.TasksProcessing |> Set.remove parentTaskId

                    return { state with TasksRetryRequested = tasksRetryRequested'; TasksProcessing = tasksProcessing' }
                | _ ->
                    ctx.LogInfo "No available worker. Retrying..."
                    ctx.Self <-- msg
                    return state

        | LeafTaskComplete taskId ->
            ctx.LogInfo <| sprintf' "Leaf task %A complete." taskId

            //the scheduler did not generate any child tasks for the task with id=taskId
            //simply unlog that task
            taskLog <-- AsyncReplicated(Choice1Of2 <| Unlog [| taskId |])
            //do! taskLog <!- fun ch -> SyncReplicated(ch, Unlog [| taskId |])

            let tasksProcessing' = state.TasksProcessing |> Set.remove taskId

            return { state with TasksProcessing = tasksProcessing' }

        | FinalTaskComplete(replyChannel, taskId) ->
            ctx.LogInfo <| sprintf' "Final task %A complete." taskId

            taskLog <-- SyncReplicated(replyChannel, Choice1Of2 <| Unlog [| taskId |])

            let tasksProcessing' = state.TasksProcessing |> Set.remove taskId

            return { state with TasksProcessing = tasksProcessing' }

        | TaskResult((processId, taskId) as taskHeader, taskResult) ->
            ctx.LogInfo <| sprintf' "Received result of (%A, %A)." processId taskId

            //check if task is logged (if not then this is a duplicate result)
            let! isLogged = taskLog <!- fun ch -> AsyncSingular(Choice1Of2 <| IsLogged(ch, taskId))

            let tasksProcessing' =
                if isLogged then
                    //forward task result to scheduler
                    ctx.LogInfo <| sprintf' "Giving result of (%A, %A) to sheduler." processId taskId
                    state.Scheduler <-- Scheduler.TaskResult(taskHeader, taskResult)
                    state.TasksProcessing |> Set.add taskId
                else
                    ctx.LogInfo "WARNING! Result is not for a logged task!." 
                    state.TasksProcessing

            return { state with TasksProcessing = tasksProcessing' }

        | Recover actorId ->
            ctx.LogInfo <| sprintf' "Recovering from failure of worker %A." actorId

            let! logEntries = taskLog <!- fun ch -> AsyncSingular(Choice1Of2 <| RetrieveByWorker(ch, actorId))

            let logEntries' = logEntries |> Seq.filter (fun (taskId, _, _, _) -> not << state.TasksProcessing.Contains <| taskId)

            ctx.LogInfo <| sprintf' "Will recover %d tasks..." (logEntries' |> Seq.length)

            for _, parentTaskId, _, taskPayload in logEntries' do
                ctx.Self <-- RetryTask(parentTaskId, taskPayload)

            let tasksRetryRequested' =
                logEntries' |> Seq.fold (fun tasksRetry (taskId, _, _, _) -> tasksRetry |> Set.add taskId) state.TasksRetryRequested

            return { state with TasksRetryRequested = tasksRetryRequested' }

        | CancelSiblingTasks(RR ctx reply as confirmationReplyChannel, taskId) ->
            ctx.LogInfo <| sprintf' "Cancelling task %A" taskId

            let! tasksToCancel = taskLog <!- fun ch -> AsyncSingular(Choice1Of2 <| GetSiblingTasks(ch, taskId))
            ctx.LogInfo <| sprintf' "Will cancel %d tasks" tasksToCancel.Length

            let cancelledTaskIds = tasksToCancel |> Array.map fst
            taskLog <-- AsyncReplicated(Choice1Of2 <| Unlog cancelledTaskIds)
            reply nothing
            //reliableTaskLog <-- SyncRely(confirmationReplyChannel, Unlog cancelledTaskIds)

            do! tasksToCancel |> Seq.groupBy snd
                              |> Seq.map (fun (worker, ts) -> worker, ts |> Seq.map fst |> Seq.toArray)
                              |> Seq.map (fun (worker, tids) -> async {
                                    try
                                        do! worker <-!- CancelTasks tids
                                    with e -> ctx.LogError e
                                        //ctx.LogError e <| sprintf' "TaskManager: Failed to cancel tasks of process: %A, in worker: %A" processId worker
                                })
                              |> Async.Parallel
                              |> Async.Ignore
                            
            ctx.LogInfo "Triggered task cancellation in workers."

            return state

        | GetActiveTaskCount replyChannel ->
            //ASSUME ALL EXCEPTIONS PROPERLY HANDLED AND DOCUMENTED
             
            //FaultPoint INDIRECT ;; nothing
            taskLog <-- AsyncSingular(Choice1Of2 <| GetCount(replyChannel))

            return state

        | IsValidTask(RR ctx reply, taskId) ->
            let! isLogged = taskLog <!- fun ch -> AsyncSingular(Choice1Of2 <| IsLogged(ch, taskId))

            reply <| Value isLogged

            return state

        | GetWorkerCount ch ->
            workerPool <-- GetAvailableWorkerCount ch

            return state
    }

