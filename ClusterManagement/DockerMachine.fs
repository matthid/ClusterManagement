namespace ClusterManagement

open System.IO

module DockerMachine =

    let getMachineName clusterName nodeName =
        sprintf "%s-%s" clusterName nodeName

    let createProcess cluster args =
        let dockerMachineDir = StoragePath.getDockerMachineDir cluster
        DockerMachineWrapper.createProcess dockerMachineDir args
        |> CreateProcess.addSetup (fun () ->
            let c = ClusterConfig.readClusterConfig cluster
            let tokens = ClusterConfig.getTokens c
            let homeDir = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile)
            let awsConfigDir = Path.Combine(homeDir, ".aws")
            let awsCredentials = Path.Combine(awsConfigDir, "credentials")
            let awsConfig = Path.Combine(awsConfigDir, "config")
            let restoreAwsConfig = if File.Exists awsConfig then File.Move (awsConfig, awsConfig + ".backup"); true else false
            let restoreAwsCredentials = if File.Exists awsCredentials then File.Move (awsCredentials, awsCredentials + ".backup"); true else false
            let cleanup () =
                if restoreAwsCredentials then File.Delete awsCredentials; File.Move (awsCredentials + ".backup", awsCredentials)
                if restoreAwsConfig then File.Delete awsConfig; File.Move (awsConfig + ".backup", awsConfig)
            try
                if Directory.Exists awsConfigDir |> not then Directory.CreateDirectory awsConfigDir |> ignore
                File.WriteAllText (awsCredentials, Env.getResourceText "aws_config" |> Config.replaceTokens tokens)
                File.WriteAllText (awsCredentials, Env.getResourceText "aws_credentials" |> Config.replaceTokens tokens)
            with _ -> cleanup(); reraise()
            { new IProcessHook with
                member x.ProcessExited _ = ()
                member x.ParseSuccess _ = ()
                member x.Dispose () = cleanup() }
            )

    let ssh cluster nodeName command =
        let machineName = getMachineName cluster nodeName
        let args = command |> Arguments.OfWindowsCommandLine
        //let cmd = args.Args.[0]
        //let restArgs = { Args = args.Args.[1..args.Args.Length - 1] }
        let usingArgs =
            //if cmd = "sudo" then
            //    ([|"ssh"; machineName; "echo"; restArgs.ToWindowsCommandLine |> CmdLineParsing.escapeCommandLineForShell |] |> Arguments.OfArgs)
            //else
                ([|yield "ssh"; yield machineName; yield! args.Args |] |> Arguments.OfArgs)
        createProcess cluster usingArgs

    let sshExt cluster nodeName (createProcess:CreateProcess<_>) =
        let cmdLine =
            match createProcess.Command with
            | ShellCommand s -> s
            | RawCommand (f, arg) -> sprintf "%s %s" f arg.ToWindowsCommandLine
        ssh cluster nodeName cmdLine
        |> CreateProcess.withResultFunc createProcess.GetResult
        |> CreateProcess.addSetup createProcess.Setup
    
    let getExternalIp cluster nodeName =
        let runIp arguments =
            CreateProcess.fromRawCommand "ip" arguments
        let machineName = getMachineName cluster nodeName
        runIp [|machineName|]
        |> sshExt cluster nodeName
        |> CreateProcess.redirectOutput
        |> CreateProcess.map (fun r -> r.Output.Trim())


    type internal InspectJson = FSharp.Data.JsonProvider<"machine-inspect-example.json">
    let internal parseInspectJson json =
        let json = InspectJson.Load(new System.IO.StringReader(json))
        json

    let internal inspect cluster nodeName =
        let machineName = getMachineName cluster nodeName
        createProcess cluster ([|"inspect"; machineName |] |> Arguments.OfArgs)
        |> CreateProcess.redirectOutput
        |> CreateProcess.map (fun r -> parseInspectJson (r.Output.Trim()))
      

    let runDockerOnNode cluster nodeName dockerCommand =
        dockerCommand
        |> CreateProcess.withCommand 
            (match dockerCommand.Command with
             | ShellCommand _ -> invalidOp "expected RawCommand"
             | RawCommand (_, args) -> RawCommand("sudo docker", args))
        |> sshExt cluster nodeName

    let internal parseIfConfig (ifConfigOut:string) =
        ifConfigOut.Split ([|'\r';'\n'|], System.StringSplitOptions.RemoveEmptyEntries)
        |> Seq.tryFind (fun line -> line.Contains "inet addr")
        |> Option.bind (fun line ->
            try
                line.Split ([| ' '; '\t'; '\r'; '\n' |], System.StringSplitOptions.RemoveEmptyEntries)
                |> Seq.skip 1
                |> Seq.tryHead
            with e ->
                if Env.isVerbose then eprintfn "Error: %O" e
                None)
        |> Option.bind (fun h -> 
            try
                h.Split([|':'|])
                |> Seq.skip 1
                |> Seq.tryHead
            with e ->
                if Env.isVerbose then eprintfn "Error: %O" e
                None)

          
    let getIp networkInterface cluster nodeName =
        let runIfConfig arguments =
            CreateProcess.fromRawCommand "ifconfig" arguments
        let getIpFromInterface networkInterface =
            runIfConfig [|networkInterface|]
            |> CreateProcess.redirectOutput
            |> CreateProcess.map (fun o -> 
                match parseIfConfig o.Output with
                | Some ip -> ip
                | None -> failwithf "Could not detect ip of interace via 'ifconfig %s | grep \"inet addr\"'" networkInterface)
        getIpFromInterface networkInterface
        |> sshExt cluster nodeName

    let getDockerIp cluster nodeName =
        getIp "docker0" cluster nodeName
    let getEth0Ip cluster nodeName =
        getIp "eth0" cluster nodeName

    let runDockerPs cluster nodeName =
        DockerWrapper.ps ()
        |> runDockerOnNode cluster nodeName
        
    let internal runDockerInspect cluster nodeName containerId =
        DockerWrapper.inspect containerId
        |> runDockerOnNode cluster nodeName

    let runDockerKill cluster nodeName containerId =
        DockerWrapper.kill containerId
        |> runDockerOnNode cluster nodeName

    let runDockerRemove cluster nodeName force containerId =
        DockerWrapper.remove force containerId
        |> runDockerOnNode cluster nodeName

    let remove cluster machineName =
        createProcess cluster ([|"rm";"-y"; machineName |] |> Arguments.OfArgs)
            
        