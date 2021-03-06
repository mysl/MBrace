﻿namespace Nessos.MBrace.Runtime.Daemon

    module internal Configuration =

        open System
        open System.Collections.Generic
        open System.Diagnostics
        open System.IO
        open System.Net
        open System.Reflection
        open System.Threading

        open Nessos.FsPickler

        open Nessos.Vagrant
        
        open Nessos.Thespian
        open Nessos.Thespian.ConcurrencyTools
        open Nessos.Thespian.Remote.TcpProtocol
        open Nessos.Thespian.Remote.PipeProtocol
        open Nessos.Thespian.Serialization

        open Nessos.MBrace.Utils
        open Nessos.MBrace.Utils.Retry
        open Nessos.MBrace.Utils.String
        open Nessos.MBrace.Store
        open Nessos.MBrace.Runtime
        open Nessos.MBrace.Runtime.Logging
        open Nessos.MBrace.Runtime.Daemon.Configuration

        let isWindowed = Environment.UserInteractive && selfProc.MainWindowHandle <> 0n

        let thisAssembly = System.Reflection.Assembly.GetExecutingAssembly()
        let selfExe = thisAssembly.Location
        let selfInfo = thisAssembly.GetName()

        let exiter = new ConsoleProcessExiter(true) :> IExiter

        let redirectAsync (source : StreamReader) (target : TextWriter) =
            async {
                while not source.EndOfStream do
                    let! line = Async.AwaitTask <| source.ReadLineAsync()
                    do! target.WriteLineAsync(line) |> Async.AwaitIAsyncResult |> Async.Ignore
            }

        let flushConsole () = Console.Error.Flush() ; Console.Out.Flush()

        let initLogger workingDirectory logFiles logLevel =
            // gather constituent loggers
            let loggers =

                let createFileLogger (file: string) =
                    let file =
                        if Path.IsPathRooted file then file
                        else
                            Path.Combine(workingDirectory, file)

                    let logger = new FileLogger(file, showDate = true, append = true)
                    logger.LogInfo "M-BRACE DAEMON STARTUP"
                    logger :> ISystemLogger

                let fileLoggers =
                    logFiles
                    |> Seq.distinct
                    |> Seq.choose 
                        (fun file ->
                            try Some <| createFileLogger file
                            with e -> exiter.Exit(sprintf "ERROR: cannot initialize logger: %s" e.Message, id = 5)) 
                    |> Seq.toList

                let jsonLogPath = Path.Combine(workingDirectory, "jsonLog.txt")
                let jsonLogger = new JsonFileLogger(Path.Combine(workingDirectory, "jsonLog.txt"), append = false) :> ISystemLogger

                // Dependency injection : TODO remove
                IoC.RegisterValue<string>(jsonLogPath, parameter = "jsonLogFile")

                Logger.createConsoleLogger () :: jsonLogger :: fileLoggers

            let logger = 
                loggers 
                |> Logger.broadcast 
                |> Logger.maxLogLevel logLevel
                |> Logger.wrapAsync

            logger



        let mkFancyConsole (address : Address) (state : NodeState) =
            if isConsoleWindow then
                let consoleBgColor, role =
                    match state with
                    | NodeState.Slave 
                    | NodeState.AltMaster -> ConsoleColor.Blue, "Slave"
                    | NodeState.Master -> ConsoleColor.DarkMagenta, "Master"
                    | NodeState.Idle -> ConsoleColor.Black, "Idle"

                let title =
                    stringB {
                        yield "mbraced"
                        yield sprintf' "[PID %d]" selfProc.Id
                        yield sprintf' "(%s Node)" role
                        yield sprintf' " - %s:%d" address.HostnameOrAddress address.Port
                    
                    } |> String.build

                Console.Title <- title
                if isWindowed then
                    Console.BackgroundColor <- consoleBgColor

        let registerFancyConsoleEvent (address : Address) =
            mkFancyConsole address NodeState.Idle
            Definitions.MBraceNode.stateChangeObservable.Subscribe(mkFancyConsole address) |> ignore


        let registerPerfMonitorAsync () = async {
            let monitor = new Nessos.MBrace.Runtime.PerformanceMonitor()
            monitor.Start()
            return monitor
        }

        // parser implementations        

        type WorkerPorts =
            | Singular of int
            | Range of int * int

        let parsePort (p : int) =
            if p < -1 || p > int UInt16.MaxValue then failwithf "invalid port %d." p else p

        let parseWorkerPortRange (lower, upper) =
            if lower < 0 || upper < lower || upper > int UInt16.MaxValue then
                failwithf "invalid port range %d..%d." lower upper
            else Range(lower, upper)

        let parseWorkerExe (path : string) =
            let path = 
                if Path.IsPathRooted path then path
                else Path.Combine(Path.GetDirectoryName selfExe, path)

            if File.Exists path then path
            else
                let msg = sprintf "invalied mbrace worker executable '%s'." path
                raise <| new FileNotFoundException(path)

        let parseSingularWorkerPort = parsePort >> Singular

        let evalPorts (ports : WorkerPorts list) =
            ports |> List.collect(function Singular p -> [p] | Range (l,u) -> [l..u])

        let printWorkerPorts (ports : WorkerPorts list) =
            stringB {
                yield "Worker port pool: "

                let printPort (p : WorkerPorts) =
                    stringB {
                        match p with
                        | Singular p -> yield sprintf' "%d" p
                        | Range (l,u) -> yield sprintf' "[%d..%d]" l u
                    }

                match ports with
                | [] -> ()
                | h :: rest ->
                    yield! printPort h

                    for p in rest do
                        yield ", "
                        yield! printPort p
            } |> String.build

        let isLocalIpAddress =
            let localIPs =
                hset [
                        yield IPAddress.IPv6Loopback
                        yield IPAddress.Loopback
                        yield! Dns.GetHostAddresses(Dns.GetHostName())
                ]

            localIPs.Contains


        let parseHostname (hostname : string) =
            if hostname = null then Dns.GetHostName()
            else
                match hostname.Trim() with
                | "" -> Dns.GetHostName()
                | h -> h

        let parseListenIps (ips: string list) =
            match ips with
            | [] -> [IPAddress.Any]
            | _ -> 
                let parsed = ips |> List.map (fun ipstr -> 
                    let success, ip = IPAddress.TryParse ipstr
                    if success then Choice1Of2 ip else Choice2Of2 ipstr)

                let choose1Of2 = function Choice1Of2 fst -> Some fst | _ -> None
                let choose2Of2 = function Choice2Of2 snd -> Some snd | _ -> None
                let is1Of2 = function Choice1Of2 _ -> true | _ -> false
                let is2Of2 = function Choice2Of2 _ -> true | _ -> false

                if parsed |> List.forall is1Of2 then
                    let candidates = 
                        parsed |> List.choose choose1Of2
                               |> List.map (fun ip -> if isLocalIpAddress ip then Choice1Of2 ip else Choice2Of2 ip)

                    if candidates |> List.forall is1Of2 then 
                        candidates |> List.choose choose1Of2
                    else 
                        let nonLocal = candidates |> List.choose choose2Of2
                        exiter.Exit(sprintf "unable to listen to non-local ips: %A" nonLocal)
                else
                    let parseFailed = parsed |> List.choose choose2Of2
                    exiter.Exit(sprintf "unable to parse ips: %A" parseFailed)


        let resolveAddress (hostname : string) (port : int) =
            // check if valid local hostname
            try
                match hostname with
                | IPSubnet.CIDR _ ->
                    let hostname' = Dns.GetHostAddresses(Dns.GetHostName())
                                    |> Seq.filter (fun ip -> ip.AddressFamily = Sockets.AddressFamily.InterNetwork)
                                    |> Seq.map string
                                    |> Seq.tryFind (fun ip -> IPSubnet.contains hostname ip)
                    match hostname' with
                    | Some h -> Address(h, port)
                    | None   -> exiter.Exit(sprintf "invalid hostname '%s'. No matching IP found." hostname, id = 1)
                | hostname when Dns.GetHostAddresses hostname |> Array.forall isLocalIpAddress ->
                    Address(hostname, port)
                | _ ->
                    exiter.Exit(sprintf "invalid hostname '%s'." hostname, id = 1)
            with _ -> exiter.Exit(sprintf "invalid hostname '%s'." hostname, id = 1)


        let mainLoop () : 'T =
            let rec loop () = async {
                do! Async.Sleep 2000
                return! loop ()
            }

            Async.RunSynchronously(loop())

        let initWindowProc (args : MBracedConfig list) =
            try
                let selfExe = System.Reflection.Assembly.GetExecutingAssembly().Location
                let args = args |> List.filter (function Detach | Spawn_Window -> false | _ -> true)

                let psi = new ProcessStartInfo(selfExe, mbracedParser.PrintCommandLineFlat args)
                psi.UseShellExecute <- true
                psi.CreateNoWindow <- false
                psi.WorkingDirectory <- Path.GetDirectoryName selfExe

                let _ = Process.Start psi in ()
            with e -> exiter.Exit("error spawning child process.", id = 5)

        let initBackgroundProc (args : MBracedConfig list) =
            async {
                try
                    // set up an ipc receiver actor
                    let receiverId = Guid.NewGuid().ToString()
                    use receiver =
                        Receiver.create<MBracedSpawningServer> ()
                        |> Receiver.rename receiverId
                        |> Receiver.publish [| PipeProtocol () |]
                        |> Receiver.start

                    let selfExe = System.Reflection.Assembly.GetExecutingAssembly().Location
                    let args = Parent_Receiver_Id (selfProc.Id, receiverId) :: args |> List.filter (function Detach | Spawn_Window -> false | _ -> true)

                    let proc = new System.Diagnostics.Process()

                    proc.StartInfo.FileName <- selfExe
                    proc.StartInfo.Arguments <- mbracedParser.PrintCommandLineFlat args
                    proc.StartInfo.UseShellExecute <- false
                    proc.StartInfo.CreateNoWindow <- true
                    proc.StartInfo.RedirectStandardOutput <- true
                    proc.StartInfo.RedirectStandardError <- true

                    proc.EnableRaisingEvents <- true

                    let _ = proc.Start ()

                    // start IO redirection
                    use cts = new CancellationTokenSource()
                    use _ = exiter.ExitEvent.Subscribe(fun _ -> cts.Cancel())
                    do Async.Start(redirectAsync proc.StandardError Console.Error, cts.Token)
                    do Async.Start(redirectAsync proc.StandardOutput Console.Out, cts.Token)

                    let! result = 
                        receiver 
                        |> Receiver.toObservable 
                        |> Observable.merge (proc.Exited |> Observable.map (fun _ -> StartupError (proc.ExitCode, None)))
                        |> Async.AwaitObservable

                    match result with
                    | StartupError (id,_) -> 
                        do! Async.Sleep 10
                        exiter.Exit(id = id)
                    | StartupSuccessful (_,r) ->
                        r.Reply <| Value (Some "Detaching from console.")
                        do! Async.Sleep 50
                        exiter.Exit(id = 0)

                with e -> exiter.Exit("error spawning child process.", id = 5)
            } |> Async.RunSynchronously