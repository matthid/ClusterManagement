namespace ClusterManagement
open System.IO
open System.Diagnostics

[<AutoOpen>]
module StreamExtensions =

    type System.IO.Stream with
        static member CombineWrite (target1:System.IO.Stream, target2:System.IO.Stream)=
            if not target1.CanWrite || not target2.CanWrite then 
                raise <| System.ArgumentException("Streams need to be writeable to combine them.")
            let notsupported () = raise <| System.InvalidOperationException("operation not suppotrted")
            { new System.IO.Stream() with
                member __.CanRead = false
                member __.CanSeek = false
                member __.CanTimeout = target1.CanTimeout || target2.CanTimeout
                member __.CanWrite = true
                member __.Length = target1.Length
                member __.Position with get () = target1.Position and set _ = notsupported()
                member __.Flush () = target1.Flush(); target2.Flush()
                member __.FlushAsync (tok) = 
                    async {
                        do! target1.FlushAsync(tok)
                        do! target2.FlushAsync(tok)
                    }
                    |> Async.StartImmediateAsTask
                    :> System.Threading.Tasks.Task
                member __.Seek (_, _) = notsupported()
                member __.SetLength (_) = notsupported()
                member __.Read (_, _, _) = notsupported()
                member __.Write (buffer, offset, count)=
                    target1.Write(buffer, offset, count)
                    target2.Write(buffer, offset, count)
                override __.WriteAsync(buffer, offset, count, tok) =
                    async {
                        let! child1 = 
                            target1.WriteAsync(buffer, offset, count, tok)
                            |> Async.AwaitTask
                            |> Async.StartChild
                        let! child2 =
                            target2.WriteAsync(buffer, offset, count, tok)
                            |> Async.AwaitTask
                            |> Async.StartChild
                        do! child1
                        do! child2
                    }
                    |> Async.StartImmediateAsTask
                    :> System.Threading.Tasks.Task
                }

        static member InterceptStream (readStream:System.IO.Stream, track:System.IO.Stream)=
            if not readStream.CanRead || not track.CanWrite then 
                raise <| System.ArgumentException("track Stream need to be writeable and readStream readable to intercept the readStream.")
            { new System.IO.Stream() with
                member __.CanRead = true
                member __.CanSeek = readStream.CanSeek
                member __.CanTimeout = readStream.CanTimeout || track.CanTimeout
                member __.CanWrite = readStream.CanWrite
                member __.Length = readStream.Length
                member __.Position with get () = readStream.Position and set v = readStream.Position <- v
                member __.Flush () = readStream.Flush(); track.Flush()
                member __.FlushAsync (tok) = 
                    async {
                        do! readStream.FlushAsync(tok)
                        do! track.FlushAsync(tok)
                    }
                    |> Async.StartImmediateAsTask
                    :> System.Threading.Tasks.Task
                member __.Seek (offset, origin) = readStream.Seek(offset, origin)
                member __.SetLength (l) = readStream.SetLength(l)
                member __.Read (buffer, offset, count) =
                    let read = readStream.Read(buffer, offset, count)
                    track.Write(buffer, offset, read)
                    read
                override __.ReadAsync (buffer, offset, count, _) =
                  async {
                    let! read = readStream.ReadAsync(buffer, offset, count)
                    do! track.WriteAsync(buffer, offset, read)
                    return read
                  }
                  |> Async.StartImmediateAsTask
                member __.Write (buffer, offset, count)=
                    readStream.Write(buffer, offset, count)
                override __.WriteAsync(buffer, offset, count, tok) =
                    readStream.WriteAsync(buffer, offset, count, tok)
                override __.Dispose(t) = if t then readStream.Dispose()
                }

type Command =
    | ShellCommand of string
    /// Windows: https://msdn.microsoft.com/en-us/library/windows/desktop/bb776391(v=vs.85).aspx
    /// Linux(mono): https://github.com/mono/mono/blob/0bcbe39b148bb498742fc68416f8293ccd350fb6/eglib/src/gshell.c#L32-L104 (because we need to create a commandline string internally which need to go through that code)
    /// Linux(netcore): See https://github.com/fsharp/FAKE/pull/1281/commits/285e585ec459ac7b89ca4897d1323c5a5b7e4558 and https://github.com/dotnet/corefx/blob/master/src/System.Diagnostics.Process/src/System/Diagnostics/Process.Unix.cs#L443-L522
    | RawCommand of executable:FilePath * arguments:Arguments

type DataRef<'T>=
    internal { retrieveRaw : (unit -> 'T) ref }
    static member Empty = { retrieveRaw = ref (fun _ -> invalidOp "Can retrieve only when a process has been started!") }
    static member Map f (inner:DataRef<'T>) =
        { retrieveRaw = ref (fun _ -> f inner.Value) }
    member x.Value = (!x.retrieveRaw)()

type StreamRef = DataRef<System.IO.Stream>
//type DataRef =
//    static member Empty<'T> = DataRef
type StreamSpecification =
    | Inherit
    | UseStream of closeOnExit:bool * stream:System.IO.Stream
    | CreatePipe of StreamRef // The underlying framework creates pipes already

type ProcessOutput = { Output : string; Error : string }
type IProcessHook =
    inherit System.IDisposable
    abstract member ProcessExited : int -> unit
    abstract member ParseSuccess : int -> unit
type ResultGenerator<'TRes> =
    {   GetRawOutput : unit -> ProcessOutput
        GetResult : ProcessOutput -> 'TRes }
type CreateProcess<'TRes> =
    private {   
        Command : Command
        WorkingDirectory : string option
        Environment : (string * string) list option
        StandardInput : StreamSpecification 
        StandardOutput : StreamSpecification 
        StandardError : StreamSpecification
        Setup : unit -> IProcessHook
        GetRawOutput : (unit -> ProcessOutput) option
        GetResult : ProcessOutput -> 'TRes
    }
    member internal x.ToStartInfo =
        let p = new ProcessStartInfo()
        match x.Command with
        | ShellCommand s ->
            p.UseShellExecute <- true
            p.FileName <- s
            p.Arguments <- null
        | RawCommand (filename, args) ->
            p.UseShellExecute <- false
            p.FileName <- filename
            p.Arguments <- args.ToStartInfo
        match x.StandardInput with
        | Inherit ->
            p.RedirectStandardInput <- false
        | UseStream _ | CreatePipe _ ->
            p.RedirectStandardInput <- true
        match x.StandardOutput with
        | Inherit ->
            p.RedirectStandardOutput <- false
        | UseStream _ | CreatePipe _ ->
            p.RedirectStandardOutput <- true
        match x.StandardError with
        | Inherit ->
            p.RedirectStandardError <- false
        | UseStream _ | CreatePipe _ ->
            p.RedirectStandardError <- true
                
        let setEnv key var =
            p.EnvironmentVariables.[key] <- var
        x.Environment
            |> Option.iter (Seq.iter (fun (key, value) -> setEnv key value))
        p.WindowStyle <- ProcessWindowStyle.Hidden
        p
    member x.CommandLine =
        match x.Command with
        | ShellCommand s -> s
        | RawCommand (f, arg) -> sprintf "%s %s" f arg.ToWindowsCommandLine
module CreateProcess  =
    let emptyHook =
        { new IProcessHook with
            member __.Dispose () = ()
            member __.ProcessExited _ = ()
            member __.ParseSuccess _ = () }
    let fromCommand command =
        {   Command = command
            WorkingDirectory = None
            // Problem: Environment not allowed when using ShellCommand
            Environment = None
            // Problem: Redirection not allowed when using ShellCommand
            StandardInput = Inherit
            // Problem: Redirection not allowed when using ShellCommand
            StandardOutput = Inherit
            // Problem: Redirection not allowed when using ShellCommand
            StandardError = Inherit
            GetRawOutput = None
            GetResult = fun _ -> ()
            Setup = fun _ -> emptyHook }
    let fromRawWindowsCommandLine command windowsCommandLine =
        fromCommand <| RawCommand(command, Arguments.OfWindowsCommandLine windowsCommandLine)
    let fromRawCommand command args =
        fromCommand <| RawCommand(command, Arguments.OfArgs args)

    let ofStartInfo (p:System.Diagnostics.ProcessStartInfo) =
        {   Command = if p.UseShellExecute then ShellCommand p.FileName else RawCommand(p.FileName, Arguments.OfStartInfo p.Arguments)
            WorkingDirectory = if System.String.IsNullOrWhiteSpace p.WorkingDirectory then None else Some p.WorkingDirectory
            Environment = 
                p.EnvironmentVariables
                |> Seq.cast<System.Collections.DictionaryEntry>
                |> Seq.map (fun kv -> string kv.Key, string kv.Value)
                |> Seq.toList
                |> Some
            StandardInput = if p.RedirectStandardError then CreatePipe StreamRef.Empty else Inherit
            StandardOutput = if p.RedirectStandardError then CreatePipe StreamRef.Empty else Inherit
            StandardError = if p.RedirectStandardError then CreatePipe StreamRef.Empty else Inherit
            GetRawOutput = None
            GetResult = fun _ -> ()
            Setup = fun _ -> emptyHook
        } 
    
    let interceptStream target (s:StreamSpecification) =
        match s with
        | Inherit -> Inherit
        | UseStream (close, stream) ->
            let combined = Stream.CombineWrite(stream, target)
            UseStream(close, combined)
        | CreatePipe pipe ->
            CreatePipe (StreamRef.Map (fun s -> Stream.InterceptStream(s, target)) pipe)
    
    let copyRedirectedProcessOutputsToStandardOutputs (c:CreateProcess<_>)=
        { c with
            StandardOutput =
                let stdOut = System.Console.OpenStandardOutput()
                interceptStream stdOut c.StandardOutput
            StandardError =
                let stdErr = System.Console.OpenStandardError()
                interceptStream stdErr c.StandardError }
    
    let withWorkingDirectory workDir (c:CreateProcess<_>)=
        { c with
            WorkingDirectory = Some workDir }
    let withCommand command (c:CreateProcess<_>)=
        { c with
            Command = command }

    let replaceFilePath newFilePath (c:CreateProcess<_>)=
        { c with
            Command =
                match c.Command with
                | ShellCommand s -> failwith "Expected RawCommand"
                | RawCommand (_, c) -> RawCommand(newFilePath, c) }
    let mapFilePath f (c:CreateProcess<_>)=
        c
        |> replaceFilePath (f (match c.Command with ShellCommand s -> failwith "Expected RawCommand" | RawCommand (file, _) -> f file))

    let private combine (d1:IProcessHook) (d2:IProcessHook) =
        { new IProcessHook with
            member __.Dispose () = d1.Dispose(); d2.Dispose()
            member __.ProcessExited e = d1.ProcessExited(e); d2.ProcessExited(e)
            member __.ParseSuccess e = d1.ParseSuccess(e); d2.ParseSuccess(e) }
    let addSetup f (c:CreateProcess<_>) =
        { c with
            Setup = fun _ -> combine (c.Setup()) (f()) }
            
    let withEnvironment env (c:CreateProcess<_>)=
        { c with
            Environment = Some env }
    let withStandardOutput stdOut (c:CreateProcess<_>)=
        { c with
            StandardOutput = stdOut }
    let withStandardError stdErr (c:CreateProcess<_>)=
        { c with
            StandardError = stdErr }
    let withStandardInput stdIn (c:CreateProcess<_>)=
        { c with
            StandardInput = stdIn }

    let private withResultFuncRaw f x =
        {   Command = x.Command
            WorkingDirectory = x.WorkingDirectory
            Environment = x.Environment
            StandardInput = x.StandardInput
            StandardOutput = x.StandardOutput
            StandardError = x.StandardError
            GetRawOutput = x.GetRawOutput
            GetResult = f
            Setup = x.Setup }
    let map f x =
        withResultFuncRaw (x.GetResult >> f) x
    let redirectOutput (c:CreateProcess<_>) =
        match c.GetRawOutput with
        | None ->
            let outMem = new MemoryStream()
            let errMem = new MemoryStream()
        
            let getOutput () =
                outMem.Position <- 0L
                errMem.Position <- 0L
                let stdErr = (new StreamReader(errMem)).ReadToEnd()
                let stdOut = (new StreamReader(outMem)).ReadToEnd()
                { Output = stdOut; Error = stdErr }

            { c with
                StandardOutput = UseStream (false, outMem)
                StandardError = UseStream (false, errMem)
                GetRawOutput = Some getOutput }
                |> withResultFuncRaw id
        | Some f ->
            c |> withResultFuncRaw id
    let withResultFunc f (x:CreateProcess<_>) =
        match x.GetRawOutput with
        | Some _ -> x |> withResultFuncRaw f
        | None -> x |> redirectOutput |> withResultFuncRaw f
         
    let addOnExited f (r:CreateProcess<_>) =
        r
        |> addSetup (fun _ ->
           { new IProcessHook with
                member __.Dispose () = ()
                member __.ProcessExited exitCode =
                    if exitCode <> 0 then f exitCode
                member __.ParseSuccess _ = () })
    let ensureExitCodeWithMessage msg (r:CreateProcess<_>) =
        r
        |> addOnExited (fun exitCode ->
            if exitCode <> 0 then failwith msg)
            

    let ensureExitCode (r:CreateProcess<_>) =
        r
        |> addOnExited (fun exitCode ->
            if exitCode <> 0 then
                let msg =
                    match r.GetRawOutput with
                    | Some f ->
                        let output = f()
                        (sprintf "Process exit code '%d' <> 0. Command Line: %s\nStdOut: %s\nStdErr: %s" exitCode r.CommandLine output.Output output.Error)
                    | None ->
                        (sprintf "Process exit code '%d' <> 0. Command Line: %s" exitCode r.CommandLine)
                failwith msg    
                )
    
    let warnOnExitCode msg (r:CreateProcess<_>) =
        r
        |> addOnExited (fun exitCode ->
            if exitCode <> 0 then
                let msg =
                    match r.GetRawOutput with
                    | Some f ->
                        let output = f()
                        (sprintf "%s. exit code '%d' <> 0. Command Line: %s\nStdOut: %s\nStdErr: %s" msg exitCode r.CommandLine output.Output output.Error)
                    | None ->
                        (sprintf "%s. exit code '%d' <> 0. Command Line: %s" msg exitCode r.CommandLine)
                //if Env.isVerbose then
                eprintfn "%s" msg    
                )
type ProcessResults<'a> =
  { ExitCode : int
    CreateProcess : CreateProcess<'a>
    Result : 'a }    
module Proc =
    // mono sets echo off for some reason, therefore interactive mode doesn't work as expected
    // this enables this tty feature which makes the interactive mode work as expected
    let private setEcho (b:bool) =
        // See https://github.com/mono/mono/blob/master/mcs/class/corlib/System/ConsoleDriver.cs#L289
        let t = System.Type.GetType("System.ConsoleDriver")
        if Env.isMono then
            let flags = System.Reflection.BindingFlags.Static ||| System.Reflection.BindingFlags.NonPublic
            if isNull t then
                eprintfn "Expected to find System.ConsoleDriver.SetEcho"
                false
            else
                let setEchoMethod = t.GetMethod("SetEcho", flags)
                if isNull setEchoMethod then
                    eprintfn "Expected to find System.ConsoleDriver.SetEcho"
                    false
                else
                    setEchoMethod.Invoke(null, [| b :> obj |]) :?> bool
        else false
    let startRaw (c:CreateProcess<_>) =
      async {
        use hook = c.Setup()
        let p = c.ToStartInfo
        let commandLine = 
            sprintf "%s> \"%s\" %s" p.WorkingDirectory p.FileName p.Arguments
      
        if Env.isVerbose then
            printfn "%s... RedirectInput: %b, RedirectOutput: %b, RedirectError: %b" commandLine p.RedirectStandardInput p.RedirectStandardOutput p.RedirectStandardError
        
        use toolProcess = new Process(StartInfo = p)
        
        let isStarted = ref false
        let mutable readOutputTask = System.Threading.Tasks.Task.FromResult Stream.Null
        let mutable readErrorTask = System.Threading.Tasks.Task.FromResult Stream.Null
        let mutable redirectStdInTask = System.Threading.Tasks.Task.FromResult Stream.Null
        let tok = new System.Threading.CancellationTokenSource()
        let start() =
            if not <| !isStarted then
                toolProcess.EnableRaisingEvents <- true
                setEcho true |> ignore
                if not <| toolProcess.Start() then
                    failwithf "could not start process: %s" commandLine
                isStarted := true
                
                let handleStream parameter processStream isInputStream =
                    async {
                        match parameter with
                        | Inherit ->
                            return failwithf "Unexpected value"
                        | UseStream (shouldClose, stream) ->
                            if isInputStream then
                                do! stream.CopyToAsync(processStream, 81920, tok.Token)
                                    |> Async.AwaitTask
                            else
                                do! processStream.CopyToAsync(stream, 81920, tok.Token)
                                    |> Async.AwaitTask
                            return
                                if shouldClose then stream else Stream.Null
                        | CreatePipe (r) ->
                            r.retrieveRaw := fun _ -> processStream
                            return Stream.Null
                    }

                if p.RedirectStandardInput then
                    redirectStdInTask <-
                      handleStream c.StandardInput toolProcess.StandardInput.BaseStream true
                      // Immediate makes sure we set the ref cell before we return...
                      |> fun a -> Async.StartImmediateAsTask(a, cancellationToken = tok.Token)
                      
                if p.RedirectStandardOutput then
                    readOutputTask <-
                      handleStream c.StandardOutput toolProcess.StandardOutput.BaseStream false
                      // Immediate makes sure we set the ref cell before we return...
                      |> fun a -> Async.StartImmediateAsTask(a, cancellationToken = tok.Token)

                if p.RedirectStandardError then
                    readErrorTask <-
                      handleStream c.StandardError toolProcess.StandardError.BaseStream false
                      // Immediate makes sure we set the ref cell before we return...
                      |> fun a -> Async.StartImmediateAsTask(a, cancellationToken = tok.Token)
    
        // Wait for the process to finish
        let! exitEvent = 
            toolProcess.Exited
                // This way the handler gets added before actually calling start or "EnableRaisingEvents"
                |> Event.guard start
                |> Async.AwaitEvent
                |> Async.StartImmediateAsTask
        // Waiting for the process to exit (buffers)
        toolProcess.WaitForExit()

        let delay = System.Threading.Tasks.Task.Delay 500
        let all =  System.Threading.Tasks.Task.WhenAll([readErrorTask; readOutputTask; redirectStdInTask])
        let! t = System.Threading.Tasks.Task.WhenAny(all, delay)
                 |> Async.AwaitTask
        if t = delay then
            eprintfn "At least one redirection task did not finish: \nReadErrorTask: %O, ReadOutputTask: %O, RedirectStdInTask: %O" readErrorTask.Status readOutputTask.Status redirectStdInTask.Status
        tok.Cancel()
        
        // wait for finish -> AwaitTask has a bug which makes it unusable for chanceled tasks.
        // workaround with continuewith
        let! streams = all.ContinueWith (new System.Func<System.Threading.Tasks.Task<Stream[]>, Stream[]> (fun t -> t.GetAwaiter().GetResult())) |> Async.AwaitTask
        for s in streams do s.Dispose()
        setEcho false |> ignore
        
        hook.ProcessExited(toolProcess.ExitCode)
        let o, realResult =
            match c.GetRawOutput with
            | Some f -> f(), true
            | None -> { Output = ""; Error = "" }, false

        if Env.isVerbose && realResult then
            printfn "Process Output: %s, Error: %s" o.Output o.Error

        let result =
            try c.GetResult o
            with e ->
                let msg =
                    if realResult then
                        sprintf "Could not parse output from process, StdOutput: %s, StdError %s" o.Output o.Error
                    else
                        "Could not parse output from process, but RawOutput was not retrieved."
                raise <| System.Exception(msg, e)
        
        hook.ParseSuccess toolProcess.ExitCode
        return { ExitCode = toolProcess.ExitCode; CreateProcess = c; Result = result }
      }
      // Immediate makes sure we set the ref cell before we return the task...
      |> Async.StartImmediateAsTask
    
    let start c = 
        async {
            let! result = startRaw c
            return result.Result
        }
        |> Async.StartImmediateAsTask

    /// Convenience method when you immediatly want to await the result of 'start', just note that
    /// when used incorrectly this might lead to race conditions 
    /// (ie if you use StartAsTask and access reference cells in CreateProcess after that returns)
    let startAndAwait c = start c |> Async.AwaitTask

    let ensureExitCodeWithMessageGetResult msg (r:ProcessResults<_>) =
        let { Setup = f } =
            { r.CreateProcess with Setup = fun _ -> CreateProcess.emptyHook }
            |> CreateProcess.ensureExitCodeWithMessage msg
        let hook = f ()
        hook.ProcessExited r.ExitCode
        r.Result

    let getResultIgnoreExitCode (r:ProcessResults<_>) =
        r.Result

    let ensureExitCodeGetResult (r:ProcessResults<_>) =
        let { Setup = f } =
            { r.CreateProcess with Setup = fun _ -> CreateProcess.emptyHook }
            |> CreateProcess.ensureExitCode
        let hook = f ()
        hook.ProcessExited r.ExitCode
        r.Result

    