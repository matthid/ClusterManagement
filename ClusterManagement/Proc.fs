namespace ClusterManagement
open System.IO
open System.Diagnostics

type VolatileBarrier() =
    [<VolatileField>]
    let mutable isStopped = false
    member __.Proceed = not isStopped
    member __.Stop() = isStopped <- true

[<AutoOpen>]
module AsyncExtensions =

    open System.Threading
    open System.Threading.Tasks
    let internal startImmediateAsTask (token:CancellationToken, computation : Async<_>, taskCreationOptions) : Task<_> =
        if obj.ReferenceEquals(computation, null) then raise <| System.NullReferenceException("computation is null!")
        let taskCreationOptions = defaultArg taskCreationOptions TaskCreationOptions.None
        let tcs = new TaskCompletionSource<_>(taskCreationOptions)

        // The contract: 
        //      a) cancellation signal should always propagate to task
        //      b) CancellationTokenSource that produced a token must not be disposed until the the task.IsComplete
        // We are:
        //      1) registering for cancellation signal here so that not to miss the signal
        //      2) disposing the registration just before setting result/exception on TaskCompletionSource -
        //              otherwise we run a chance of disposing registration on already disposed  CancellationTokenSource
        //              (See (b) above)
        //      3) ensuring if reg is disposed, we do SetResult
        let barrier = VolatileBarrier()
        let reg = token.Register(fun _ -> if barrier.Proceed then tcs.SetCanceled())
        let task = tcs.Task
        let disposeReg() =
            barrier.Stop()
            if not (task.IsCanceled) then reg.Dispose()

        let a = 
            async { 
                try
                    let! result = computation
                    do 
                        disposeReg()
                        tcs.TrySetResult(result) |> ignore
                with
                |   e -> 
                        disposeReg()
                        tcs.TrySetException(e) |> ignore
            }
        Async.StartImmediate(a, token)
        task
    type Async with
        static member StartImmediateAsTask (computation,?taskCreationOptions,?cancellationToken)=
            let token = defaultArg cancellationToken Async.DefaultCancellationToken       
            startImmediateAsTask(token,computation,taskCreationOptions)

[<AutoOpen>]
module StreamExtensions =

    type System.IO.Stream with
        static member CombineWrite (target1:System.IO.Stream, target2:System.IO.Stream)=
            if not target1.CanWrite || not target2.CanWrite then 
                raise <| System.ArgumentException("Streams need to be writeable to combine them.")
            let notsupported () = raise <| System.InvalidOperationException("operation not suppotrted")
            { new System.IO.Stream() with
                member x.CanRead = false
                member x.CanSeek = false
                member x.CanTimeout = target1.CanTimeout || target2.CanTimeout
                member x.CanWrite = true
                member x.Length = target1.Length
                member x.Position with get () = target1.Position and set v = notsupported()
                member x.Flush () = target1.Flush(); target2.Flush()
                member x.FlushAsync (tok) = 
                    async {
                        do! target1.FlushAsync(tok)
                        do! target2.FlushAsync(tok)
                    }
                    |> Async.StartImmediateAsTask
                    :> System.Threading.Tasks.Task
                member x.Seek (offset, origin) = notsupported()
                member x.SetLength (l) = notsupported()
                member x.Read (buffer, offset, count) = notsupported()
                member x.Write (buffer, offset, count)=
                    target1.Write(buffer, offset, count)
                    target2.Write(buffer, offset, count)
                override x.WriteAsync(buffer, offset, count, tok) =
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
            let notsupported () = raise <| System.InvalidOperationException("operation not suppotrted")
            { new System.IO.Stream() with
                member x.CanRead = true
                member x.CanSeek = readStream.CanSeek
                member x.CanTimeout = readStream.CanTimeout || track.CanTimeout
                member x.CanWrite = readStream.CanWrite
                member x.Length = readStream.Length
                member x.Position with get () = readStream.Position and set v = readStream.Position <- v
                member x.Flush () = readStream.Flush(); track.Flush()
                member x.FlushAsync (tok) = 
                    async {
                        do! readStream.FlushAsync(tok)
                        do! track.FlushAsync(tok)
                    }
                    |> Async.StartImmediateAsTask
                    :> System.Threading.Tasks.Task
                member x.Seek (offset, origin) = readStream.Seek(offset, origin)
                member x.SetLength (l) = readStream.SetLength(l)
                member x.Read (buffer, offset, count) =
                    let read = readStream.Read(buffer, offset, count)
                    track.Write(buffer, offset, read)
                    read
                override x.ReadAsync (buffer, offset, count, tok) =
                  async {
                    let! read = readStream.ReadAsync(buffer, offset, count)
                    do! track.WriteAsync(buffer, offset, read)
                    return read
                  }
                  |> Async.StartImmediateAsTask
                member x.Write (buffer, offset, count)=
                    readStream.Write(buffer, offset, count)
                override x.WriteAsync(buffer, offset, count, tok) =
                    readStream.WriteAsync(buffer, offset, count, tok)
                override x.Dispose(t) = if t then readStream.Dispose()
                }
module internal CmdLineParsing =
    let windowsArgvToCommandLine args =
        let escapeBackslashes (sb:System.Text.StringBuilder) (s:string) (lastSearchIndex:int) =
            // Backslashes must be escaped if and only if they precede a double quote.
            [ lastSearchIndex .. -1 .. 0]
            |> Seq.takeWhile (fun i -> s.[i] = '\\')
            //|> Seq.map (fun c -> )
            //|> fun c -> Seq.replicate c '\\'
            |> Seq.iter (fun c -> sb.Append '\\' |> ignore)
        
        let sb = new System.Text.StringBuilder()
        for (s:string) in args do
            sb.Append('"') |> ignore
            // Escape double quotes (") and backslashes (\).
            let mutable searchIndex = 0
            
            // Put this test first to support zero length strings.
            let mutable quoteIndex = 0
            while searchIndex < s.Length && quoteIndex >= 0 do

                quoteIndex <- s.IndexOf('"', searchIndex)
                if quoteIndex >= 0 then
                    sb.Append(s, searchIndex, quoteIndex - searchIndex) |> ignore
                    escapeBackslashes sb s (quoteIndex - 1)
                    sb.Append('\\') |> ignore
                    sb.Append('"') |> ignore
                    searchIndex <- quoteIndex + 1
            
            sb.Append(s, searchIndex, s.Length - searchIndex) |> ignore
            escapeBackslashes sb s (s.Length - 1)
            sb.Append(@""" ") |> ignore
        
        sb.ToString(0, System.Math.Max(0, sb.Length - 1))

    let windowsCommandLineToArgv (arguments:string) =
        // https://github.com/dotnet/corefx/blob/master/src/System.Diagnostics.Process/src/System/Diagnostics/Process.Unix.cs#L443-L522
        let currentArgument = new System.Text.StringBuilder()
        let mutable inQuotes = false
        let results = System.Collections.Generic.List<_>()

        // Iterate through all of the characters in the argument string.
        let mutable i = 0
        while i < arguments.Length do
            // From the current position, iterate through contiguous backslashes.
            let mutable backslashCount = 0
            while i < arguments.Length && arguments.[i] = '\\' do
                i <- i + 1
                backslashCount <- backslashCount + 1
            if backslashCount > 0 then
                if i > arguments.Length || arguments.[i] <> '"' then
                    // Backslashes not followed by a double quote:
                    // they should all be treated as literal backslashes.
                    currentArgument.Append('\\', backslashCount) |> ignore
                    i <- i - 1
                else
                    // Backslashes followed by a double quote:
                    // - Output a literal slash for each complete pair of slashes
                    // - If one remains, use it to make the subsequent quote a literal.
                    currentArgument.Append('\\', backslashCount / 2) |> ignore
                    if backslashCount % 2 = 0 then
                        i <- i - 1
                    else
                        currentArgument.Append('"') |> ignore
            else
                let c = arguments.[i]
                
                match c with
                // If this is a double quote, track whether we're inside of quotes or not.
                // Anything within quotes will be treated as a single argument, even if
                // it contains spaces.
                | '"' ->
                    inQuotes <-  not inQuotes
                // If this is a space/tab and we're not in quotes, we're done with the current
                // argument, and if we've built up any characters in the current argument,
                // it should be added to the results and then reset for the next one.
                | ' ' | '\t' when not inQuotes ->
                    if currentArgument.Length > 0 then
                        results.Add(currentArgument.ToString())
                        currentArgument.Clear() |> ignore
                // Nothing special; add the character to the current argument.
                | _ ->
                    currentArgument.Append(c) |> ignore
            i <- i + 1

        // If we reach the end of the string and we still have anything in our current
        // argument buffer, treat it as an argument to be added to the results.
        if currentArgument.Length > 0 then
            results.Add(currentArgument.ToString())

        results.ToArray()


type FilePath = string
type Arguments = 
    { Args : string array }
    static member Empty = { Args = [||] }
    /// See https://msdn.microsoft.com/en-us/library/17w5ykft.aspx
    static member OfWindowsCommandLine cmd =
        { Args = CmdLineParsing.windowsCommandLineToArgv cmd }
    /// This is the reverse of https://msdn.microsoft.com/en-us/library/17w5ykft.aspx
    member x.ToWindowsCommandLine = CmdLineParsing.windowsArgvToCommandLine x.Args
    static member OfArgs args = { Args = args }
    static member OfStartInfo cmd =
        Arguments.OfWindowsCommandLine cmd
    member internal x.ToStartInfo =
        x.ToWindowsCommandLine

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
type CreateProcess<'TRes> =
    private {   
        Command : Command
        WorkingDirectory : string option
        Environment : (string * string) list option
        StandardInput : StreamSpecification 
        StandardOutput : StreamSpecification 
        StandardError : StreamSpecification
        GetRawOutput : (unit -> ProcessOutput) option
        GetResult : unit -> 'TRes
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
module CreateProcess  =
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
            GetResult = fun _ -> () }
    let fromRawWindowsCommandLine command windowsCommandLine =
        fromCommand <| RawCommand(command, Arguments.OfWindowsCommandLine windowsCommandLine)
    let fromRawCommand command args =
        fromCommand <| RawCommand(command, Arguments.OfArgs args)
    let map f x =
        {   Command = x.Command
            WorkingDirectory = x.WorkingDirectory
            Environment = x.Environment
            StandardInput = x.StandardInput
            StandardOutput = x.StandardOutput
            StandardError = x.StandardError
            GetRawOutput = x.GetRawOutput
            GetResult = (fun () -> f (x.GetResult()) ) }
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
                |> map (fun _ -> getOutput())
        | Some f ->
            c |> map (fun _ -> f ())

type ProcessResults<'a> =
  { ExitCode : int
    CommandLine : string 
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
    let start (c:CreateProcess<_>) =
      async {
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
        let! streams = all.ContinueWith (new System.Func<System.Threading.Tasks.Task<Stream[]>, Stream[]> (fun t -> t.Result)) |> Async.AwaitTask
        for s in streams do s.Dispose()

        setEcho false |> ignore
        try
            let result = c.GetResult ()
            return { ExitCode = toolProcess.ExitCode; CommandLine = commandLine; Result = result }
        with e ->
            let msg =
                match c.GetRawOutput with
                | Some f ->
                    let o = f()
                    sprintf "Could not parse output from process, StdOutput: %s, StdError %s" o.Output o.Error
                | None ->
                    "Could not parse output from process, but RawOutput was not retrieved."
            return raise <| System.Exception(msg, e)
      }
      // Immediate makes sure we set the ref cell before we return the task...
      |> Async.StartImmediateAsTask
    
    /// Convenience method when you immediatly want to await the result of 'start', just note that
    /// when used incorrectly this might lead to race conditions 
    /// (ie if you use StartAsTask and access reference cells in CreateProcess after that returns)
    let startAndAwait c = start c |> Async.AwaitTask

    let ensureExitCodeWithMessageGetResult msg (r:ProcessResults<_>) =
        if r.ExitCode <> 0 then failwith msg
        r.Result

    let getResultIgnoreExitCode (r:ProcessResults<_>) =
        r.Result

            

    let ensureExitCodeGetResult (r:ProcessResults<_>) =
        match r :> obj with
        | :? ProcessResults<ProcessOutput> as o ->
            ensureExitCodeWithMessageGetResult (sprintf "Process exit code '%d' <> 0. Command Line: %s\nStdOut: %s\nStdErr: %s" r.ExitCode r.CommandLine o.Result.Output o.Result.Error) r
        | _ ->
            ensureExitCodeWithMessageGetResult (sprintf "Process exit code '%d' <> 0. Command Line: %s, Result: %A" r.ExitCode r.CommandLine r.Result) r
    
    
    [<System.Obsolete("Use createProcess instead")>]
    type NoOutput = | NotOutput
    [<System.Obsolete("Use createProcess instead")>]
    type RedirectedOutput = { StdOut : string; StdErr : string }
    [<System.Obsolete("Use createProcess instead")>]
    type StreamOutput = { OutStream : System.IO.Stream; ErrStream : System.IO.Stream }
    
    [<System.Obsolete("Use createProcess instead")>]
    type ProcResult<'TRes> =
          { ExitCode : int
            Output : 'TRes
            CommandLine : string }
    [<System.Obsolete("Use createProcess instead")>]
    type AsyncProcResult<'TRes> = Async<ProcResult<'TRes>>
    
    [<System.Obsolete("Use createProcess instead")>]
    type StdInMode =
        | FixedString of string
        | Stream of System.IO.Stream
        | NonInteractive
        | RedirectStdIn
    [<System.Obsolete("Use createProcess instead")>]
    type RedirectionMode =
        { PrintProcessStdOut:bool; PrintProcessStdErr:bool; StdInMode:StdInMode }
        static member Default =
            { PrintProcessStdOut = false; PrintProcessStdErr = false; StdInMode = StdInMode.NonInteractive }
        /// this almost works like "real" interactive mode, but is no real TTY -> therefore processes will behave differently
        /// bash needs to be run with -i to work for example.
        static member RedirectInteractive =
            { PrintProcessStdOut = true; PrintProcessStdErr = true; StdInMode = StdInMode.RedirectStdIn }
            
    [<System.Obsolete("Use createProcess instead")>]
    type RedirectOptionsP<'TRes> =
        /// Interactive means all output and errors are already printed to stdout, but are not available here.
        private | Interactive
        /// Redirect some streams -> no interation possible for the user
        | Redirection of RedirectionMode
        //| Forward of RedirectionMode
    
    [<System.Obsolete("Use createProcess instead")>]
    type RedirectOptions = 
        static member Interactive = RedirectOptionsP<NoOutput>.Interactive
        static member Redirection mode =
            RedirectOptionsP<RedirectedOutput>.Redirection mode
        static member Default =
            RedirectOptions.Redirection RedirectionMode.Default
        static member RedirectInteractive =
            RedirectOptions.Redirection RedirectionMode.RedirectInteractive
        //static member Forward mode =
        //    RedirectOptionsP<StreamOutput>.Forward mode
        //static member SimpleForward  =
        //    RedirectOptions.Forward RedirectionMode.Default
        
    [<System.Obsolete("Use createProcess instead")>]
    let private createOut (opts:RedirectOptionsP<'TRes>) stdOut stdErr =
        let tres = typeof<'TRes>
        match opts with
        | Interactive ->
            if tres = typeof<NoOutput> then
                NoOutput.NotOutput :> obj :?> 'TRes
            else
                failwithf "Cannot return %s as 'TRes when %A" tres.FullName opts
        | Redirection _ ->
            if tres = typeof<RedirectedOutput> then
                { StdOut = stdOut; StdErr = stdErr } :> obj :?> 'TRes
            else
                failwithf "Cannot return %s as 'TRes when %A" tres.FullName opts
        //| Forward _ ->
        //    if tres = typeof<StreamOutput> then
        //        { OutStream = outStream; ErrStream = errStream } :> obj :?> 'TRes
        //    else
        //        failwithf "Cannot return %s as 'TRes when %A" tres.FullName opts

        
    [<System.Obsolete("Use createProcess instead")>]
    let private startProcessRaw (opts:RedirectOptionsP<'TRes>) (p:ProcessStartInfo) : AsyncProcResult<'TRes> =
      async {

        let stdInString, printStdout, printStderr =
            match opts with
            | Interactive ->
                p.RedirectStandardInput <- false
                p.RedirectStandardOutput <- false
                p.RedirectStandardError <- false
                null, true, true
            | Redirection {PrintProcessStdOut = printStdout; PrintProcessStdErr = printStderr; StdInMode = stdInMode} ->
            //| Forward {PrintProcessStdOut = printStdout; PrintProcessStdErr = printStderr; StdInMode = stdInMode} ->
                let stdInString =
                    match stdInMode with
                    | FixedString s -> s
                    | _ -> null
                p.RedirectStandardInput <- not (isNull stdInString)
                p.RedirectStandardOutput <- true
                p.RedirectStandardError <- true
                stdInString, printStdout, printStderr
        if p.UseShellExecute then
            p.UseShellExecute <- not (p.RedirectStandardInput ||  p.RedirectStandardOutput || p.RedirectStandardError)
        let outMem = new MemoryStream()
        let errMem = new MemoryStream()
        let c =
            { CreateProcess.ofStartInfo p with
                StandardOutput =
                    if p.RedirectStandardOutput then
                        if printStdout then 
                            let stdOut = System.Console.OpenStandardOutput()
                            UseStream (false, Stream.CombineWrite(outMem, stdOut))
                        else UseStream (false, outMem)
                    else Inherit
                StandardError =
                    if p.RedirectStandardError then
                        if printStderr then 
                            let stdErr = System.Console.OpenStandardError()
                            UseStream (false, Stream.CombineWrite(errMem, stdErr))
                        else UseStream (false, errMem)
                    else Inherit
                StandardInput =
                    if p.RedirectStandardInput && not (isNull stdInString) then
                        let byteArray = System.Text.Encoding.UTF8.GetBytes( stdInString )
                        let stream = new MemoryStream( byteArray )
                        UseStream (true, stream)
                    else Inherit }
        let! proc = start (c) |> Async.AwaitTask

        outMem.Position <- 0L
        errMem.Position <- 0L
        let! stdErr = (new StreamReader(errMem)).ReadToEndAsync() |> Async.AwaitTask
        let! stdOut = (new StreamReader(outMem)).ReadToEndAsync() |> Async.AwaitTask
        return
            { CommandLine = proc.CommandLine
              ExitCode = proc.ExitCode
              Output = createOut opts stdOut stdErr }
      }
    
        

    [<System.Obsolete("Use createProcess instead")>]
    let startProcessCustomizedWithOpts (opts:RedirectOptionsP<'TRes>) configP =
      async {
        
        let p = new ProcessStartInfo(CreateNoWindow = true)
        p.UseShellExecute <- false
        configP p
        
        if not Env.isLinux then
            let cygPath = @"C:\Program Files\Git\usr\bin\cygpath.exe"
            if not (File.Exists cygPath) then
                failwithf "Please install git bash on default location for this program to work! ('%s' not found)" cygPath
            if p.FileName.StartsWith "/" then
                let p2 = 
                    new ProcessStartInfo(
                        FileName = cygPath,
                        Arguments = sprintf "-w \"%s\"" p.FileName,
                        CreateNoWindow = true)
                let! r = startProcessRaw RedirectOptions.Default p2
                p.FileName <- r.Output.StdOut
        
        return! startProcessRaw opts p
      }
    
    [<System.Obsolete("Use createProcess instead")>]
    let startProcessCustomized configP =
        startProcessCustomizedWithOpts RedirectOptions.Default configP
    [<System.Obsolete("Use createProcess instead")>]
    let defaultCustomize workingDir processFile arguments (p:ProcessStartInfo) =
        p.FileName <- processFile
        p.Arguments <- arguments
        p.WorkingDirectory <- System.IO.Path.GetFullPath(workingDir)
    [<System.Obsolete("Use createProcess instead")>]
    let startProcessWithOpts opts workingDir processFile arguments =
        startProcessCustomizedWithOpts opts (defaultCustomize workingDir processFile arguments)
    
    
    [<System.Obsolete("Use createProcess instead")>]
    let startProcessIn workingDir processFile arguments = startProcessWithOpts RedirectOptions.Default workingDir processFile arguments
    
    [<System.Obsolete("Use createProcess instead")>]
    let startProcess processFile arguments = startProcessWithOpts RedirectOptions.Default (Directory.GetCurrentDirectory()) processFile arguments
    
    [<System.Obsolete("Use createProcess instead")>]
    let failWithMessage (msg:string) (r:ProcResult<'TRes>) =
        if r.ExitCode <> 0 then failwith msg
        r
        
    [<System.Obsolete("Use createProcess instead")>]
    let failOnExitCode (r:ProcResult<'TRes>) =
        match r :> obj with
        | :? ProcResult<RedirectedOutput> as o ->
            failWithMessage (sprintf "Process exit code '%d' <> 0. Command Line: %s\nStdOut: %s\nStdErr: %s" r.ExitCode r.CommandLine o.Output.StdOut o.Output.StdErr) r
        | _ ->
            failWithMessage (sprintf "Process exit code '%d' <> 0. Command Line: %s" r.ExitCode r.CommandLine) r

    [<System.Obsolete("Use createProcess instead")>]
    let getStdOut (r:ProcResult<RedirectedOutput>) = r.Output.StdOut
    
    [<System.Obsolete("Use createProcess instead")>]
    let escapeCommandLineForShell (cmdLine:string) =
        sprintf "'%s'" (cmdLine.Replace("'", "'\\''"))

