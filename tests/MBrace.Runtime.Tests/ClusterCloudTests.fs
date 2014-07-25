#nowarn "0044" // 'While loop considered harmful' message.

namespace Nessos.MBrace.Runtime.Tests

    open System
    open System.IO
    open System.Threading

    open FsUnit
    open NUnit.Framework

    open Nessos.MBrace
    open Nessos.MBrace.Utils
    open Nessos.MBrace.Client
    open Nessos.MBrace.Core.Tests
    

    [<ClusterTestsCategory>]
    type ``Cluster Cloud Tests``() =
        inherit ``Core Tests``()

        let currentRuntime : MBraceRuntime option ref = ref None
        
        override __.Name = "Cluster Cloud Tests"
        override __.IsLocalTesting = false
        override __.ExecuteExpression<'T>(expr: Quotations.Expr<Cloud<'T>>): 'T =
            MBrace.RunRemote __.Runtime expr

        member __.Runtime =
            match currentRuntime.Value with
            | None -> invalidOp "No runtime specified in test fixture."
            | Some r -> r

        abstract InitRuntime : unit -> unit
        [<TestFixtureSetUp>]
        default test.InitRuntime() =
            lock currentRuntime (fun () ->
                match currentRuntime.Value with
                | Some runtime -> runtime.Kill()
                | None -> ()
            
                MBraceSettings.MBracedExecutablePath <- Path.Combine(Directory.GetCurrentDirectory(), "mbraced.exe")
                let runtime = MBraceRuntime.InitLocal(3, debug = true)
                currentRuntime := Some runtime)
        
        abstract FiniRuntime : unit -> unit
        [<TestFixtureTearDown>]
        default test.FiniRuntime() =
            lock currentRuntime (fun () -> 
                match currentRuntime.Value with
                | None -> invalidOp "No runtime specified in test fixture."
                | Some r -> r.Shutdown() ; currentRuntime := None)


        [<Test>]
        member test.SerializationTest () =
            shouldFailwith<MBraceException> (fun () -> <@ cloud { return new System.Net.HttpListener() } @> |> test.ExecuteExpression |> ignore)

        [<Test>] 
        member t.``Cloud Log`` () = 
            let msg = "Cloud Log Test Msg"
            let ps = <@ cloud { do! Cloud.Log msg } @> |> t.Runtime.CreateProcess
            ps.AwaitResult()
            let dumps = ps.GetLogs()
            dumps.Length |> should equal 1
            (dumps |> Seq.head).Message |> should equal msg

        [<Test>] 
        member t.``Cloud Logf - multiple`` () =
            let ps =
                <@
                    cloud {
                        let taskF (_ : int) = cloud {
                            for i in [1 .. 20] do
                                do! Cloud.Sleep 10
                                do! Cloud.Logf "msg = %d" i
                        }

                        do!
                            [1..5]
                            |> Seq.map taskF
                            |> Cloud.Parallel
                            |> Cloud.Ignore

                        do! Cloud.Sleep 100
                    }
                @> |> t.Runtime.CreateProcess

            ps.AwaitResult()

            let logs = ps.GetLogs()

            logs.Length |> should equal 100

        [<Test>] 
        member t.``Cloud Log - delete`` () = 
            let msg = "Cloud Log Test Msg"
            let ps = <@ cloud { do! Cloud.Log msg } @> |> t.Runtime.CreateProcess
            ps.AwaitResult()
            Seq.isEmpty (ps.GetLogs()) |> should equal false
            ps.DeleteLogs()
            ps.GetLogs() |> should equal Seq.empty
            
            
        [<Test>] 
        member test.``Cloud Trace`` () = 
            let ps = <@ testTrace 1 @> |> test.Runtime.CreateProcess
            ps.AwaitResult() |> ignore
            let dumps = ps.GetLogs()
            ()
            should equal true (traceHasValue dumps "a" "1")
            should equal true (traceHasValue dumps "x" "2")

        [<Test>] 
        member test.``Serialization Exception`` () = 
            test.SerializationTest()

        [<Test>]
        member test.``Cloud Trace Exception `` () = 
            let ps = <@ cloud { return 1 / 0 } |> Cloud.Trace @> |> test.Runtime.CreateProcess
            shouldFailwith<CloudException> (fun () -> ps.AwaitResult() |> ignore)
            let dumps = ps.GetLogs()
            should equal true (dumps |> Seq.exists(fun e -> e.Message.Contains("DivideByZeroException")))
                
        [<Test>] 
        member test.``Cloud Trace handle Exception`` () = 
            let ps = <@ testTraceExc () @> |> test.Runtime.CreateProcess
            ps.AwaitResult() |> ignore
            let dumps = ps.GetLogs()
            should equal true (traceHasValue dumps "ex" "error")

        [<Test>] 
        member test.``Cloud Trace For Loop`` () = 
            let ps = <@ testTraceForLoop () @> |> test.Runtime.CreateProcess
            ps.AwaitResult() |> ignore
            let dumps = ps.GetLogs()
            should equal true (traceHasValue dumps "i" "1")
            should equal true (traceHasValue dumps "i" "2")
            should equal true (traceHasValue dumps "i" "3")

        [<Test>]
        member test.``Quotation Cloud Trace`` () = 
            let ps = <@ cloud { let! x = cloud { return 1 } in return x } |> Cloud.Trace @> |> test.Runtime.CreateProcess
            ps.AwaitResult() |> ignore
            let dumps = ps.GetLogs()
            should equal false (traceHasValue dumps "x" "1")

        [<Test>]
        member test.``Cloud NoTraceInfo Attribute`` () = 
            let ps = <@ cloud { return! cloud { return 1 } <||> cloud { return 2 } } |> Cloud.Trace @> |> test.Runtime.CreateProcess
            ps.AwaitResult() |> ignore
            let dumps = ps.GetLogs()
            let result = 
                dumps 
                |> Seq.exists 
                    (fun e -> 
                        match e.TraceInfo with
                        | Some i -> i.Function = Some ("op_LessBarBarGreater")
                        | None -> false)

            should equal false result

        [<Test>]
        member test.``Parallel Cloud Trace`` () = 
            let proc = <@ testParallelTrace () @> |> test.Runtime.CreateProcess 
            proc.AwaitResult() |> ignore
            let dumps = proc.GetLogs()
            should equal true (traceHasValue dumps "x" "2")
            should equal true (traceHasValue dumps "y" "3")
            should equal true (traceHasValue dumps "r" "5")

        [<Test; Repeat 10>]
        member test.``Z3 Test Kill Process - Process killed, process stops`` () =
            let mref = MutableCloudRef.New 0 |> MBrace.RunLocal
            let ps = 
                <@ cloud {
                    while true do
                        do! Async.Sleep 100 |> Cloud.OfAsync
                        let! curval = MutableCloudRef.Read mref
                        do! MutableCloudRef.Force(mref, curval + 1)
                } @> |> test.Runtime.CreateProcess

            test.Runtime.KillProcess(ps.ProcessId)
            let val1 = MutableCloudRef.Read mref |> MBrace.RunLocal
            let val2 = MutableCloudRef.Read mref |> MBrace.RunLocal

            ps.Result |> should equal ProcessResult<unit>.Killed
            should equal val1 val2

        [<Test; Repeat 10>]
        member test.``Z4 Test Kill Process - Fork bomb`` () =
            let m = MutableCloudRef.New 0 |> MBrace.RunLocal

            let ps = 
                test.Runtime.CreateProcess 
                    <@ 
                        let rec fork () = cloud { 
                            do! MutableCloudRef.Force(m,1)
                            let! _ = fork() <||> fork() 
                            return () } in 
                        
                        fork () 
                    @>

            Thread.Sleep 4000
            test.Runtime.KillProcess(ps.ProcessId)
            Thread.Sleep 2000
            MutableCloudRef.Force(m, 0) |> MBrace.RunLocal
            Thread.Sleep 1000
            let v = MutableCloudRef.Read(m) |> MBrace.RunLocal
            ps.Result |> should equal ProcessResult<unit>.Killed
            should equal 0 v

        [<Test; Repeat 100>]
        member test.``Parallel with exception cancellation`` () =
            let flag = MutableCloudRef.New false |> MBrace.RunLocal
            test.Runtime.Run <@ testParallelWithExceptionCancellation flag @>
            let v = MutableCloudRef.Read flag |> MBrace.RunLocal
            v |> should equal true

        [<Test;>]
        member test.``Process.StreamLogs`` () =
            let ps =
                <@ cloud {
                        for i in [|1..10|] do
                            do! Cloud.Logf "i = %d" i 
                } @> |> test.Runtime.CreateProcess
            ps.AwaitResult()
            ps.StreamLogs()


        [<Test;RuntimeAdministrationCategory>]
        member __.``Fetch logs`` () =
            __.Runtime.GetSystemLogs() |> Seq.isEmpty |> should equal false
            __.Runtime.Nodes.Head.GetSystemLogs() |> Seq.isEmpty |> should equal false

        [<Test;RuntimeAdministrationCategory>]
        member __.``Process delete container`` () =
            let ps = __.Runtime.CreateProcess <@ cloud { return! CloudRef.New(42) } @>
            ps.AwaitResult() |> ignore
            ps.DeleteContainer()

        [<Test;RuntimeAdministrationCategory>]
        member __.``Ping the Runtime``() =
            should greaterThan TimeSpan.Zero (__.Runtime.Ping())
            
        [<Test;RuntimeAdministrationCategory>]
        member __.``Get Runtime Status`` () =
            __.Runtime.Active |> should equal true

        [<Test;RuntimeAdministrationCategory>]
        member __.``Get Runtime Information`` () =
            __.Runtime.ShowInfo ()

        [<Test;RuntimeAdministrationCategory>]
        member __.``Get Runtime Performance Information`` () =
            __.Runtime.ShowInfo (true)

        [<Test;RuntimeAdministrationCategory>]
        member __.``Get Runtime Deployment Id`` () =
            __.Runtime.Id |> ignore

        [<Test;RuntimeAdministrationCategory>]
        member __.``Get Runtime Nodes`` () =
            __.Runtime.Nodes |> Seq.length |> should greaterThan 0

        [<Test;RuntimeAdministrationCategory>]
        member __.``Get Master Node`` () =
            __.Runtime.Master |> ignore

        [<Test;RuntimeAdministrationCategory>]
        member __.``Get Alt Nodes`` () =
            __.Runtime.Alts |> ignore

        [<Test;RuntimeAdministrationCategory; ExpectedException(typeof<Nessos.MBrace.NonExistentObjectStoreException>)>]
        member __.``Delete container`` () =
            let s = __.Runtime.Run <@ CloudSeq.New([1..10]) @> 
            __.Runtime.GetStoreClient().DeleteContainer(s.Container) 
            Seq.toList s |> ignore

        [<Test; Repeat 4;RuntimeAdministrationCategory>]
        member __.``Reboot runtime`` () =
            __.Runtime.Reboot()
            __.Runtime.Run <@ cloud { return 1 + 1 } @> |> should equal 2

        [<Test;RuntimeAdministrationCategory>]
        member __.``Attach Node`` () =
            let n = __.Runtime.Nodes |> List.length
            __.Runtime.AttachLocal 1 

            wait 500

            let n' = __.Runtime.Nodes |> List.length 
            n' - n |> should equal 1

        [<Test;RuntimeAdministrationCategory>]
        member __.``Detach Node`` () =
            let nodes = __.Runtime.Nodes 
            let n = nodes.Length
            let node2 = nodes.[1] 
            __.Runtime.Detach node2 

            wait 500

            let n' =__.Runtime.Nodes |> List.length
            n - n' |> should equal 1

        [<Test;RuntimeAdministrationCategory>]
        member __.``Node Permissions`` () =
            let nodes = __.Runtime.Nodes 
            let n = nodes.Head

            // Node client caches information for a few millisecods
            // sleep to force that values are up to date

            let p = n.Permissions

            n.Permissions <- Permissions.None
            do Thread.Sleep 500
            n.Permissions |> should equal Permissions.None

            n.Permissions <- Permissions.Slave
            do Thread.Sleep 500
            n.Permissions |> should equal Permissions.Slave

            n.Permissions <- p
            do Thread.Sleep 500
            n.Permissions |> should equal p