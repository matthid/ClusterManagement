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

    let stopFlocker () =
      async {
        match getSupportedHostInfo() with
        | GenericDocker ->
            if Env.isVerbose then printfn "stopping services."
            for service in [ "flocker-control-service"; "flocker-control-agent"; "flocker-dataset-agent"; "flocker-docker-plugin" ] do
                let! res =
                    [|"kill"; service|]
                    |> Arguments.OfArgs
                    |> DockerWrapper.createProcess
                    |> CreateProcess.redirectOutput
                    |> CreateProcess.warnOnExitCode "Failed to kill container"
                    |> Proc.startAndAwait
            
                let! res =
                    [|"rm"; service|]
                    |> Arguments.OfArgs
                    |> DockerWrapper.createProcess
                    |> CreateProcess.redirectOutput
                    |> CreateProcess.warnOnExitCode  "Failed to remove container"
                    |> Proc.startAndAwait
                
                ()
        | Ubuntu16_04 ->
            // see https://flocker-docs.clusterhq.com/en/latest/docker-integration/enabling-control-service.html#ubuntu-16-04
            // systemctl stop flocker-control
            // systemctl stop flocker-dataset-agent
            // systemctl stop flocker-container-agent
            // systemctl stop flocker-docker-plugin
            do! chrootHost "systemctl stop flocker-control" |> Proc.startAndAwait
            do! chrootHost "systemctl stop flocker-dataset-agent" |> Proc.startAndAwait
            do! chrootHost "systemctl stop flocker-container-agent" |> Proc.startAndAwait
            do! chrootHost "systemctl stop flocker-docker-plugin" |> Proc.startAndAwait
            ()
      }

    let installFlocker (nodeName:string) nodeType =
      async {
        if Env.isVerbose then printfn "installing and starting flocker services."
        match getSupportedHostInfo() with
        | GenericDocker ->
            // Start flocker control service
            if nodeName.EndsWith "master-01" then
                let! res = 
                    (sprintf "run --name=flocker-control-volume -v /var/lib/flocker %s:%s true" DockerImages.flockerControlService DockerImages.flockerTag)
                    |> Arguments.OfWindowsCommandLine
                    |> DockerWrapper.createProcess
                    |> CreateProcess.redirectOutput
                    |> CreateProcess.warnOnExitCode "Failed to create volume container, it might already exist"
                    |> Proc.startAndAwait
            
                do! 
                    (sprintf "run --restart=always -d --net=host -v /etc/flocker:/etc/flocker --volumes-from=flocker-control-volume --name=flocker-control-service %s:%s" DockerImages.flockerControlService DockerImages.flockerTag)
                    |> Arguments.OfWindowsCommandLine
                    |> DockerWrapper.createProcess
                    |> CreateProcess.ensureExitCode
                    |> Proc.startAndAwait
                    |> Async.Ignore

            // Flocker container agent
            do!
                (sprintf "run --restart=always -d --net=host --privileged -v /flocker:/flocker -v /:/host -v /etc/flocker:/etc/flocker -v /dev:/dev --name=flocker-dataset-agent %s:%s" DockerImages.flockerDatasetAgent DockerImages.flockerTag)
                |> Arguments.OfWindowsCommandLine
                |> DockerWrapper.createProcess
                |> CreateProcess.ensureExitCode
                |> Proc.startAndAwait
                |> Async.Ignore

            // flocker docker plugin
            do!
                (sprintf "run --restart=always -d --net=host -v /etc/flocker:/etc/flocker -v /run/docker:/run/docker --name=flocker-docker-plugin %s:%s" DockerImages.flockerDockerPlugin DockerImages.flockerTag)
                |> Arguments.OfWindowsCommandLine
                |> DockerWrapper.createProcess
                |> CreateProcess.ensureExitCode
                |> Proc.startAndAwait
                |> Async.Ignore
        | Ubuntu16_04 ->
            // see https://flocker-docs.clusterhq.com/en/latest/docker-integration/manual-install.html
            do! chrootHost "apt-get update" |> CreateProcess.ensureExitCode |> Proc.startAndAwait
            do! chrootHost "apt-get -y install apt-transport-https software-properties-common" |> CreateProcess.ensureExitCode |> Proc.startAndAwait
            do! chrootHost "/bin/bash -c 'add-apt-repository -y \"deb https://clusterhq-archive.s3.amazonaws.com/ubuntu/$(lsb_release --release --short)/\\$(ARCH) /\"'" |> CreateProcess.ensureExitCode |> Proc.startAndAwait
            
            System.IO.File.WriteAllText ("/host/tmp/apt-pref", """Package: *
Pin: origin clusterhq-archive.s3.amazonaws.com
Pin-Priority: 700""")
            System.IO.File.Copy("/host/tmp/apt-pref", "/host/etc/apt/preferences.d/buildbot-700", true)
            System.IO.File.Delete("/host/tmp/apt-pref")
            do! chrootHost "apt-get update" |> CreateProcess.ensureExitCode |> Proc.startAndAwait
            do! chrootHost "apt-get -y install --force-yes clusterhq-flocker-cli" |> CreateProcess.ensureExitCode |> Proc.startAndAwait
            do! chrootHost "apt-get -y install --force-yes clusterhq-flocker-node" |> CreateProcess.ensureExitCode |> Proc.startAndAwait
            do! chrootHost "apt-get -y install --force-yes clusterhq-flocker-docker-plugin" |> CreateProcess.ensureExitCode |> Proc.startAndAwait
            
            // Start services
            if nodeName.EndsWith "master-01" then
                do! chrootHost "systemctl enable flocker-control" |> CreateProcess.ensureExitCode |> Proc.startAndAwait
                do! chrootHost "systemctl start flocker-control" |> CreateProcess.ensureExitCode |> Proc.startAndAwait
            do! chrootHost "systemctl enable flocker-dataset-agent" |> CreateProcess.ensureExitCode |> Proc.startAndAwait
            do! chrootHost "systemctl start flocker-dataset-agent" |> CreateProcess.ensureExitCode |> Proc.startAndAwait
            do! chrootHost "systemctl enable flocker-container-agent" |> CreateProcess.ensureExitCode |> Proc.startAndAwait
            do! chrootHost "systemctl start flocker-container-agent" |> CreateProcess.ensureExitCode |> Proc.startAndAwait
            do! chrootHost "systemctl enable flocker-docker-plugin" |> CreateProcess.ensureExitCode |> Proc.startAndAwait
            do! chrootHost "systemctl start flocker-docker-plugin" |> CreateProcess.ensureExitCode |> Proc.startAndAwait

            ()
      }
