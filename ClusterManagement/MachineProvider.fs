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

    let rexraySettingsMap =
        [ "ebs", ["AWS_ACCESS_KEY_ID", "EBS_ACCESSKEY"
                  "AWS_ACCESS_KEY_SECRET", "EBS_SECRETKEY"
                  "AWS_REGION", "EBS_REGION"]
          "s3fs", ["AWS_ACCESS_KEY_ID", "S3FS_ACCESSKEY"
                   "AWS_ACCESS_KEY_SECRET", "S3FS_SECRETKEY"
                   "AWS_REGION", "S3S_REGION"] ]
        |> dict

    let installRexRayPlugin plugin (c:ClusterConfig.MyClusterConfig) (nodeName:string) nodeType =
      async {
        if Env.isVerbose then printfn "installing and starting rexray services."

        let forceConfig name =
            match ClusterConfig.getConfig name c with
            | Some va -> va
            | None -> failwithf "Expected config %s" name

        let settings =
            match rexraySettingsMap.TryGetValue plugin with
            | true, set -> set
            | _ -> failwithf "Unknown plugin '%s'" plugin
        //let keyId = forceConfig "AWS_ACCESS_KEY_ID"
        //let secret = forceConfig "AWS_ACCESS_KEY_SECRET"
        //let region = forceConfig "AWS_REGION"
        let settingsCmdLine =
            settings
            |> Seq.map fst
            |> Seq.map (fun set -> sprintf "%s=%s" set (forceConfig set))
            |> fun set -> System.String.Join(" ", set)
        let pluginInfo =
            match DockerImages.rexrayPlugins.TryGetValue plugin with
            | true, plug -> plug
            | _ -> failwithf "Unknown plugin (2) '%s'" plugin

        let! (result : ProcessResults<unit>) =
            (sprintf "plugin inspect %s " pluginInfo.ImageName)
            |> Arguments.OfWindowsCommandLine
            |> DockerWrapper.createProcess
            |> Proc.startRaw
        if result.ExitCode = 0 then
            // exists -> disable and set EBS_ACCESSKEY=%s EBS_SECRETKEY=%s EBS_REGION=%s
            do!
                (sprintf "plugin disable --force %s" pluginInfo.ImageName)
                |> Arguments.OfWindowsCommandLine
                |> DockerWrapper.createProcess
                |> CreateProcess.ensureExitCode
                |> Proc.startAndAwait
                |> Async.Ignore
            do!
                (sprintf "plugin set %s %s"
                    pluginInfo.ImageName settingsCmdLine)
                |> Arguments.OfWindowsCommandLine
                |> DockerWrapper.createProcess
                |> CreateProcess.ensureExitCode
                |> Proc.startAndAwait
                |> Async.Ignore
        else
            // Install rexray docker plugin
            do!
                (sprintf "plugin install --disable --grant-all-permissions %s %s"
                    pluginInfo.ImageName settingsCmdLine)
                |> Arguments.OfWindowsCommandLine
                |> DockerWrapper.createProcess
                |> CreateProcess.ensureExitCode
                |> Proc.startAndAwait
                |> Async.Ignore

        // sudo docker plugin upgrade --skip-remote-check --grant-all-permissions rexray/ebs:0.9.0 rexray/ebs:0.8.2
        //
        do!
            (sprintf "plugin upgrade --skip-remote-check --grant-all-permissions %s %s:%s"
                pluginInfo.ImageName pluginInfo.ImageName pluginInfo.Tag)
            |> Arguments.OfWindowsCommandLine
            |> DockerWrapper.createProcess
            |> CreateProcess.ensureExitCode
            |> Proc.startAndAwait
            |> Async.Ignore

        do!
            (sprintf "plugin enable %s" pluginInfo.ImageName)
            |> Arguments.OfWindowsCommandLine
            |> DockerWrapper.createProcess
            |> CreateProcess.ensureExitCode
            |> Proc.startAndAwait
            |> Async.Ignore
      }