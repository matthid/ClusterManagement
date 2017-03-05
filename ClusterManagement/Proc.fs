namespace ClusterManagement
open System.IO
open System.Diagnostics

module Proc =
    type NoOutput = | NotOutput
    type RedirectedOutput = { StdOut : string; StdErr : string }

    type ProcResult<'TRes> =
          { ExitCode : int
            Output : 'TRes
            CommandLine : string }
    type AsyncProcResult<'TRes> = Async<ProcResult<'TRes>>
    
    type System.IO.Stream with
        member source.CopyTo2Async (destination1:System.IO.Stream, destination2:System.IO.Stream, bufferSize:int, tok: System.Threading.CancellationToken) =
          async {  
            let buffer = Array.zeroCreate bufferSize
            let bytesRead = ref 0
            let! bytesReadT = source.ReadAsync(buffer, 0, buffer.Length, tok) |> Async.AwaitTask
            bytesRead := bytesReadT
            while !bytesRead > 0 do
                do! destination1.WriteAsync(buffer, 0, !bytesRead, tok) |> Async.AwaitTask
                do! destination2.WriteAsync(buffer, 0, !bytesRead, tok) |> Async.AwaitTask
                let! bytesReadT = source.ReadAsync(buffer, 0, buffer.Length, tok) |> Async.AwaitTask
                bytesRead := bytesReadT
            do! destination1.FlushAsync() |> Async.AwaitTask
            do! destination2.FlushAsync() |> Async.AwaitTask
          }

    type StdInMode =
        | FixedString of string
        | NonInteractive
        | RedirectStdIn
    type RedirectionMode =
        { PrintProcessStdOut:bool; PrintProcessStdErr:bool; StdInMode:StdInMode }
        static member Default =
            { PrintProcessStdOut = false; PrintProcessStdErr = false; StdInMode = StdInMode.NonInteractive }
        /// this almost works like "real" interactive mode, but is no real TTY -> therefore processes will behave differently
        /// bash needs to be run with -i to work for example.
        static member RedirectInteractive =
            { PrintProcessStdOut = true; PrintProcessStdErr = true; StdInMode = StdInMode.RedirectStdIn }
            
    type RedirectOptionsP<'TRes> =
        /// Interactive means all output and errors are already printed to stdout, but are not available here.
        private | Interactive
        /// Redirect some streams -> no interation possible for the user
        | Redirection of RedirectionMode
    
    type RedirectOptions = 
        static member Interactive = RedirectOptionsP<NoOutput>.Interactive
        static member Redirection mode =
            RedirectOptionsP<RedirectedOutput>.Redirection mode
        static member Default =
            RedirectOptions.Redirection RedirectionMode.Default
        static member RedirectInteractive =
            RedirectOptions.Redirection RedirectionMode.RedirectInteractive
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

    let private startProcessRaw (opts:RedirectOptionsP<'TRes>) (p:ProcessStartInfo) : AsyncProcResult<'TRes> =
      async {
        let interactive, stdInString, printStdout, printStderr =
            match opts with
            | Interactive ->
                p.RedirectStandardInput <- false
                p.RedirectStandardOutput <- false
                p.RedirectStandardError <- false
                true, null, true, true
            | Redirection {PrintProcessStdOut = printStdout; PrintProcessStdErr = printStderr; StdInMode = stdInMode} ->
                let stdInString =
                    match stdInMode with
                    | FixedString s -> s
                    | _ -> null
                p.RedirectStandardInput <- not (isNull stdInString)
                p.RedirectStandardOutput <- true
                p.RedirectStandardError <- true
                false, stdInString, printStdout, printStderr

        p.UseShellExecute <- false
        let commandLine = 
            sprintf "%s> \"%s\" %s" p.WorkingDirectory p.FileName p.Arguments
      
        if Env.isVerbose then
            printfn "%s... RedirectInput: %b, RedirectOutput: %b, RedirectError: %b" commandLine p.RedirectStandardInput p.RedirectStandardOutput p.RedirectStandardError
        
        use toolProcess = new Process(StartInfo = p)
            
        let isStarted = ref false
        let outMem = new MemoryStream()
        let errMem = new MemoryStream()
        let mutable readOutputTask = Unchecked.defaultof<_>
        let mutable readErrorTask = Unchecked.defaultof<_>
        let mutable redirectStdInTask = Unchecked.defaultof<_>
        let tok = new System.Threading.CancellationTokenSource()
        let start() =
            if not <| !isStarted then
                toolProcess.EnableRaisingEvents <- true
                setEcho true |> ignore
                if not <| toolProcess.Start() then
                    failwithf "could not start process: %s" commandLine
                isStarted := true
                    
                redirectStdInTask <-
                  async {
                    //if redirectStdIn then
                    //    let stdIn = System.Console.OpenStandardInput()
                    //    if p.RedirectStandardInput then
                    //        eprintfn "WARN: p.RedirectStandardInput = true when redirectStdIn is true"
                    //        do! stdIn.CopyToAsync(toolProcess.StandardInput.BaseStream, 81920, tok.Token) |> Async.AwaitTask
                    //    else
                    //        // We need to output our input
                    //        let stdOut = System.Console.OpenStandardOutput()
                    //        do! stdIn.CopyToAsync(stdOut, 81920, tok.Token) |> Async.AwaitTask
                    if p.RedirectStandardInput then
                        if not (isNull stdInString) then
                            toolProcess.StandardInput.WriteLine(stdInString)
                            toolProcess.StandardInput.Flush()
                            toolProcess.StandardInput.Close()
                        else
                            eprintfn "WARN: p.RedirectStandardInput = true unexpectedly"
                            toolProcess.StandardInput.Close()
                    return 1
                  } |> fun a -> Async.StartAsTask(a, cancellationToken = tok.Token)

                readOutputTask <- 
                  async {
                    if p.RedirectStandardOutput then
                        if printStdout then
                            let stdOut = System.Console.OpenStandardOutput()
                            do! toolProcess.StandardOutput.BaseStream.CopyTo2Async(stdOut, outMem, 81920, tok.Token)
                        else
                            do! toolProcess.StandardOutput.BaseStream.CopyToAsync(outMem, 81920, tok.Token) |> Async.AwaitTask
                    return 1
                  } |> fun a -> Async.StartAsTask(a, cancellationToken = tok.Token)

                readErrorTask <- 
                  async {
                    if p.RedirectStandardError then
                        if printStderr then
                            let stdErr = System.Console.OpenStandardError()
                            do! toolProcess.StandardError.BaseStream.CopyTo2Async(stdErr, errMem, 81920, tok.Token)
                        else
                            do! toolProcess.StandardError.BaseStream.CopyToAsync(errMem, 81920, tok.Token) |> Async.AwaitTask
                    return 1
                  } |> fun a -> Async.StartAsTask(a, cancellationToken = tok.Token)
                    
        // Wait for the process to finish
        let! exitEvent = 
            toolProcess.Exited
                |> Event.guard start
                |> Async.AwaitEvent
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
        do! all.ContinueWith (new System.Func<System.Threading.Tasks.Task, int> (fun t -> 1)) |> Async.AwaitTask |> Async.Ignore
           
        setEcho false |> ignore
        outMem.Position <- 0L
        errMem.Position <- 0L
        let! stdErr = (new StreamReader(errMem)).ReadToEndAsync() |> Async.AwaitTask
        let! stdOut = (new StreamReader(outMem)).ReadToEndAsync() |> Async.AwaitTask
        return
            { CommandLine = commandLine
              ExitCode = toolProcess.ExitCode
              Output = createOut opts stdOut stdErr }
      }

    let startProcessCustomizedWithOpts (opts:RedirectOptionsP<'TRes>) configP =
      async {
        
        let p = new ProcessStartInfo(CreateNoWindow = true)
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
    
    let startProcessCustomized configP =
        startProcessCustomizedWithOpts RedirectOptions.Default configP
    let defaultCustomize workingDir processFile arguments (p:ProcessStartInfo) =
        p.FileName <- processFile
        p.Arguments <- arguments
        p.WorkingDirectory <- System.IO.Path.GetFullPath(workingDir)
    let startProcessWithOpts opts workingDir processFile arguments =
        startProcessCustomizedWithOpts opts (defaultCustomize workingDir processFile arguments)
    
    
    let startProcessIn workingDir processFile arguments = startProcessWithOpts RedirectOptions.Default workingDir processFile arguments
    
    let startProcess processFile arguments = startProcessWithOpts RedirectOptions.Default (Directory.GetCurrentDirectory()) processFile arguments

    let failWithMessage (msg:string) (r:ProcResult<'TRes>) =
        if r.ExitCode <> 0 then failwith msg
        r
        
    let failOnExitCode (r:ProcResult<'TRes>) =
        match r :> obj with
        | :? ProcResult<RedirectedOutput> as o ->
            failWithMessage (sprintf "Process exit code '%d' <> 0. Command Line: %s\nStdOut: %s\nStdErr: %s" r.ExitCode r.CommandLine o.Output.StdOut o.Output.StdErr) r
        | _ ->
            failWithMessage (sprintf "Process exit code '%d' <> 0. Command Line: %s" r.ExitCode r.CommandLine) r

    let getStdOut (r:ProcResult<RedirectedOutput>) = r.Output.StdOut

    
    let escapeBackslashes (sb:System.Text.StringBuilder) (s:string) (lastSearchIndex:int) =
        // Backslashes must be escaped if and only if they precede a double quote.
        [ lastSearchIndex .. -1 .. 0]
        |> Seq.takeWhile (fun i -> s.[i] = '\\')
        //|> Seq.map (fun c -> )
        //|> fun c -> Seq.replicate c '\\'
        |> Seq.iter (fun c -> sb.Append '\\' |> ignore)
        

    let argvToCommandLine args =
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

    let escapeCommandLineForShell (cmdLine:string) =
        sprintf "'%s'" (cmdLine.Replace("'", "'\\''"))