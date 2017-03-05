namespace ClusterManagement

open System.IO

module DockerMachine =
    

    let getMachineName clusterName nodeName =
        sprintf "%s-%s" clusterName nodeName

    let runExt (opts) cluster args =
      async {
        let c = ClusterConfig.readClusterConfig cluster
        let tokens = ClusterConfig.getTokens c
        let dockerMachineDir = StoragePath.getDockerMachineDir cluster
        let homeDir = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile)
        let awsConfigDir = Path.Combine(homeDir, ".aws")
        let awsCredentials = Path.Combine(awsConfigDir, "credentials")
        let awsConfig = Path.Combine(awsConfigDir, "config")
        let restoreAwsConfig = if File.Exists awsConfig then File.Move (awsConfig, awsConfig + ".backup"); true else false
        let restoreAwsCredentials = if File.Exists awsCredentials then File.Move (awsCredentials, awsCredentials + ".backup"); true else false
        try
            if Directory.Exists awsConfigDir |> not then Directory.CreateDirectory awsConfigDir |> ignore
            File.WriteAllText (awsCredentials, Env.getResourceText "aws_config" |> Config.replaceTokens tokens)
            File.WriteAllText (awsCredentials, Env.getResourceText "aws_credentials" |> Config.replaceTokens tokens)
            let! res = DockerMachineWrapper.runExt opts dockerMachineDir args
            return res
        finally
            if restoreAwsCredentials then File.Delete awsCredentials; File.Move (awsCredentials + ".backup", awsCredentials)
            if restoreAwsConfig then File.Delete awsConfig; File.Move (awsConfig + ".backup", awsConfig)
      }
      
    let run cluster args = runExt Proc.RedirectOptions.Default cluster args
    let runInteractive cluster args = runExt Proc.RedirectOptions.Interactive cluster args
    

    let runOnNode cluster nodeName command =
      async {
        let machineName = getMachineName cluster nodeName
        return! run cluster (sprintf "ssh %s %s" machineName command)
      }

    let getExternalIp cluster nodeName =
      async {
        let machineName = getMachineName cluster nodeName
        let! res = run cluster (sprintf "ip %s" machineName)
        res |> Proc.failOnExitCode |> ignore
        return res.Output.StdOut.Trim()
      }

    type internal InspectJson = FSharp.Data.JsonProvider<"machine-inspect-example.json">
    let internal parseInspectJson json =
        let json = InspectJson.Load(new System.IO.StringReader(json))
        json

    let internal inspect cluster nodeName =
      async {
        let machineName = getMachineName cluster nodeName
        let! res = run cluster (sprintf "inspect %s" machineName)
        res |> Proc.failOnExitCode |> ignore
        return parseInspectJson(res.Output.StdOut.Trim())
      }
      
    let runDockerOnNode cluster nodeName args =
      async {
        return! runOnNode cluster nodeName (sprintf "sudo docker %s" args)
      }

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
      async {
        let machineName = getMachineName cluster nodeName
        let! res = runOnNode cluster nodeName (sprintf "ifconfig %s" networkInterface)
        let ifConfigOut = res.Output.StdOut //           inet addr:172.17.0.1  Bcast:0.0.0.0  Mask:255.255.0.0
        
        if Env.isVerbose then
            printfn "\"ifconfig %s\" returned: %s" networkInterface ifConfigOut
        
        let ip =
            match parseIfConfig ifConfigOut with
            | Some ip -> ip
            | None -> failwithf "Could not detect ip of machine '%s' via 'ifconfig docker0 | grep \"inet addr\"'" machineName
        return ip
      }

    let getDockerIp cluster nodeName =
        getIp "docker0" cluster nodeName
    let getEth0Ip cluster nodeName =
        getIp "eth0" cluster nodeName

    type DockerPsQuietRow = { ContainerId : string }
    let internal parseDockerPsQuiet (stdOut:string) =
        stdOut.Split ([|'\r';'\n'|], System.StringSplitOptions.RemoveEmptyEntries)
        |> Seq.map (fun line -> { ContainerId = line.Trim() } )
        |> Seq.toList

    let runDockerPs cluster nodeName =
      async {
        let! res = runDockerOnNode cluster nodeName "ps -q"
        res |> Proc.failOnExitCode |> ignore
        return parseDockerPsQuiet res.Output.StdOut
      }
        
    let internal runDockerInspect cluster nodeName containerId =
      async {
        let! res = runDockerOnNode cluster nodeName (sprintf "inspect %s" containerId)
        res |> Proc.failOnExitCode |> ignore
        return DockerWrapper.getFirstInspectJson res.Output.StdOut
      }

    let runDockerKill cluster nodeName containerId =
      async {
        let! res = runDockerOnNode cluster nodeName (sprintf "kill %s" containerId)
        res |> Proc.failOnExitCode |> ignore
      }

    let runDockerRemove cluster nodeName containerId =
      async {
        let! res = runDockerOnNode cluster nodeName (sprintf "rm %s" containerId)
        res |> Proc.failOnExitCode |> ignore
      }

            
        