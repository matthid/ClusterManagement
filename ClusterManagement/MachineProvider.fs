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

        let! (result : ProcessResults<unit>) =
            (sprintf "plugin inspect %s " DockerImages.rexrayDockerPlugin)
            |> Arguments.OfWindowsCommandLine
            |> DockerWrapper.createProcess
            |> Proc.startRaw
        if result.ExitCode = 0 then
            // exists -> disable and set EBS_ACCESSKEY=%s EBS_SECRETKEY=%s EBS_REGION=%s
            do!
                (sprintf "plugin disable --force %s" DockerImages.rexrayDockerPlugin)
                |> Arguments.OfWindowsCommandLine
                |> DockerWrapper.createProcess
                |> CreateProcess.ensureExitCode
                |> Proc.startAndAwait
                |> Async.Ignore
            do!
                (sprintf "plugin set %s EBS_ACCESSKEY=%s EBS_SECRETKEY=%s EBS_REGION=%s"
                    DockerImages.rexrayDockerPlugin keyId secret region)
                |> Arguments.OfWindowsCommandLine
                |> DockerWrapper.createProcess
                |> CreateProcess.ensureExitCode
                |> Proc.startAndAwait
                |> Async.Ignore
        else 
            // Install rexray docker plugin
            do!
                (sprintf "plugin install --disable --grant-all-permissions %s EBS_ACCESSKEY=%s EBS_SECRETKEY=%s EBS_REGION=%s"
                    DockerImages.rexrayDockerPlugin keyId secret region)
                |> Arguments.OfWindowsCommandLine
                |> DockerWrapper.createProcess
                |> CreateProcess.ensureExitCode
                |> Proc.startAndAwait
                |> Async.Ignore
        
        // sudo docker plugin upgrade --skip-remote-check --grant-all-permissions rexray/ebs:0.9.0 rexray/ebs:0.8.2
        //
        do!
            (sprintf "plugin upgrade --skip-remote-check --grant-all-permissions %s %s:%s"
                DockerImages.rexrayDockerPlugin DockerImages.rexrayDockerPlugin DockerImages.rexrayTag)
            |> Arguments.OfWindowsCommandLine
            |> DockerWrapper.createProcess
            |> CreateProcess.ensureExitCode
            |> Proc.startAndAwait
            |> Async.Ignore
            
        do!
            (sprintf "plugin enable %s" DockerImages.rexrayDockerPlugin)
            |> Arguments.OfWindowsCommandLine
            |> DockerWrapper.createProcess
            |> CreateProcess.ensureExitCode
            |> Proc.startAndAwait
            |> Async.Ignore
      }