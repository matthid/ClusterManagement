namespace ClusterManagement

module Which =
    let resolveCygwinPath (fileName:string) =
      async {
        if not Env.isLinux && fileName.StartsWith "/" then
            let cygPath = @"C:\Program Files\Git\usr\bin\cygpath.exe"
            if not (System.IO.File.Exists cygPath) then
                failwithf "Please install git bash on default location for this program to work! ('%s' not found)" cygPath

            let! result = 
                CreateProcess.FromRawCommand cygPath [| "-w"; fileName |]
                |> Proc.redirectOutput
                |> Proc.start
                |> Async.AwaitTask
                |> Async.map Proc.ensureExitCodeGetResult
                |> Async.map (fun r -> r.Output)
            if Env.isVerbose then
                printfn "Resolved Path '%s' to %s" fileName result
            return result
        else return fileName
      }

    let resolveCygwinPathInRawCommand (c:CreateProcess<_>) =
      async {
        match c.Command with
        | RawCommand (fileName, args) ->
            let! result = resolveCygwinPath fileName
            return { c with Command = RawCommand (result, args) }
        | _ -> return c
      }
    let getToolPath toolName =
      async {
        let! toolPath =
            CreateProcess.FromRawCommand "/usr/bin/which" [| toolName |]
            |> Proc.redirectOutput
            |> resolveCygwinPathInRawCommand
            |> Async.bind (Proc.start >> Async.AwaitTask)
            |> Async.map (Proc.ensureExitCodeWithMessageGetResult (sprintf "Tool '%s' was not found with which! Make sure it is installed." toolName))
            |> Async.map (fun r -> r.Output)
        let toolPath =
            if toolPath.EndsWith("\n") then toolPath.Substring(0, toolPath.Length - 1)
            else toolPath
        if System.String.IsNullOrWhiteSpace toolPath then
            failwith "which returned an empty string"
            
        let! resolvedToolPath = resolveCygwinPath toolPath
        return resolvedToolPath
      }