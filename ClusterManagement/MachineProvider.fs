namespace ClusterManagement

type NodeType =
    | Master
    | Worker
    | MasterWorker

module HostInteraction =
    let chrootPath = ref "chroot"

    type SupportedHost =
        | Ubuntu16_04
        | GenericDocker
        static member FromString (s:string) =
            match s.ToUpperInvariant() with
            | "Ubuntu 16.04" -> Ubuntu16_04
            | _ -> failwithf "Unknown host '%s'" s


    let chrootHost command =
        let args = command |> Arguments.OfWindowsCommandLine
        let usingArgs =
            ([| yield "/host"; yield! args.Args |] |> Arguments.OfArgs)
        CreateProcess.fromCommand (RawCommand (!chrootPath, usingArgs))

    let chrootHostExt (createProcess:CreateProcess<_>) =
        let cmdLine =
            match createProcess.Command with
            | ShellCommand s -> s
            | RawCommand (f, arg) -> sprintf "%s %s" f arg.ToWindowsCommandLine

        chrootHost cmdLine
        |> CreateProcess.withResultFunc createProcess.GetResult
        |> CreateProcess.addSetup createProcess.Setup

    let private currentHost = ref None
    let private detectHost () =
        if Env.isVerbose then printfn "detecting host system..."
        eprintfn "Currently only Ubuntu16_04 is supported (and hard coded)"
        Ubuntu16_04
    let getSupportedHostInfo () =
        match !currentHost with
        | None ->
            let h = detectHost()
            currentHost := Some h
            h
        | Some h ->
            h

    let installRexRayPlugin (c:ClusterConfig.MyClusterConfig) (nodeName:string) nodeType =
      async {
        if Env.isVerbose then printfn "installing and starting rexray services."
        
        let forceConfig name =
            match ClusterConfig.getConfig name c with
            | Some va -> va
            | None -> failwithf "Expected config %s" name

        let keyId = forceConfig "AWS_ACCESS_KEY_ID"
        let secret = forceConfig "AWS_ACCESS_KEY_SECRET"
        let region = forceConfig "AWS_REGION"

        // rexray docker plugin
        do!
            (sprintf "plugin install --grant-all-permissions %s:%s EBS_ACCESSKEY=%s EBS_SECRETKEY=%s EBS_REGION=%s"
                DockerImages.rexrayDockerPlugin DockerImages.rexrayTag keyId secret region)
            |> Arguments.OfWindowsCommandLine
            |> DockerWrapper.createProcess
            |> CreateProcess.ensureExitCode
            |> Proc.startAndAwait
            |> Async.Ignore
      }