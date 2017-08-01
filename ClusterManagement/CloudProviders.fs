namespace ClusterManagement

module CloudProviders =

    type SupportedProviders =
        | AWS
        | Generic
        static member FromString (s:string) =
            match s.ToUpperInvariant() with
            | "AWS" -> AWS
            | "GENERIC" -> Generic
            | _ -> failwithf "Unknown provider '%s'" s

    let getProvider clusterConfig =
        ClusterConfig.getConfig "PROVIDER" clusterConfig
        |> Option.map SupportedProviders.FromString
        |> fun f -> defaultArg f AWS

    let ensureConfig clusterName clusterConfig =
        match getProvider clusterConfig with
        | AWS ->
            for n in [ "AWS_ACCESS_KEY_ID"; "AWS_ACCESS_KEY_SECRET"; "AWS_REGION"; "AWS_ZONE" ] do
                Config.ensureConfig clusterName n clusterConfig
        | Generic ->
            for n in [ "SSH_KEY"; "SSH_HOST" ] do // TODO: add support for multiple keys, and check later while creating machines?...
                Config.ensureConfig clusterName n clusterConfig
            ()

    let defaultPlugin clusterConfig =
        match getProvider clusterConfig with
        | AWS -> Plugin.Ebs
        | _ -> Plugin.S3fs
      
    let createMachine clusterName machineName clusterConfig =
      async {
        match getProvider clusterConfig with
        | AWS ->
            let awsRegion = (ClusterConfig.getConfig "AWS_REGION" clusterConfig).Value
            let sizeArgs =
                match ClusterConfig.getConfig "ROOT_SIZE" clusterConfig with
                | Some size -> [|"--amazonec2-root-size";size|]
                | None -> [||]
            let args = 
                [| yield "create"; yield "--driver"; yield "amazonec2"
                   yield! sizeArgs
                   yield "--amazonec2-region"; yield awsRegion; yield machineName |]
                |> Arguments.OfArgs
            do!
                DockerMachine.createProcess clusterName args
                |> CreateProcess.ensureExitCodeWithMessage
                        "failed create new machine! If the error reads 'There is already a keypair' you should try to remove the corresponding key from the aws console."
                |> Proc.startAndAwait

        | Generic ->
            let privKey = (ClusterConfig.getConfig "SSH_KEY" clusterConfig).Value
            let host = (ClusterConfig.getConfig "SSH_HOST" clusterConfig).Value
            let tmp = System.IO.Path.GetTempFileName()
            System.IO.File.WriteAllText(tmp, privKey)
            do!
                DockerMachine.createProcess clusterName
                    ([|
                        yield! ["create"; "--driver"; "generic"]
                        match ClusterConfig.getConfig "SSH_PORT" clusterConfig with
                        | Some s -> yield sprintf "--generic-ssh-port=%s" s
                        | _ -> ()
                        match ClusterConfig.getConfig "ENGINE_PORT" clusterConfig with
                        | Some s -> yield sprintf "--generic-engine-port=%s" s
                        | _ -> ()
                        match ClusterConfig.getConfig "SSH_USER" clusterConfig with
                        | Some s -> yield sprintf "--generic-ssh-user=%s" s
                        | _ -> ()
                        yield! [sprintf "--generic-ssh-key=%s" tmp; sprintf "--generic-ip-address=%s" host; machineName]
                        |] |> Arguments.OfArgs)
                |> CreateProcess.ensureExitCodeWithMessage "failed create new machine!"
                |> Proc.startAndAwait
            ()
      }

    (*docker run \
--env AWS_ACCESS_KEY_ID=<<YOUR_ACCESS_KEY>> \
--env AWS_SECRET_ACCESS_KEY=<<YOUR_SECRET_ACCESS>> \
--env AWS_DEFAULT_REGION=us-east-1 \
garland/aws-cli-docker \
aws <command> *)
    let private runAws clusterConfig command =
        let forceConfig name =
            match ClusterConfig.getConfig name clusterConfig with
            | Some va -> va
            | None -> failwithf "Expected config %s" name

        let keyId = forceConfig "AWS_ACCESS_KEY_ID"
        let secret = forceConfig "AWS_ACCESS_KEY_SECRET"
        let region = forceConfig "AWS_REGION"

        (sprintf "run --rm --env AWS_ACCESS_KEY_ID=%s --env AWS_SECRET_ACCESS_KEY=%s --env AWS_DEFAULT_REGION=%s garland/aws-cli-docker aws %s"
            keyId secret region command)
            |> Arguments.OfWindowsCommandLine
            |> DockerWrapper.createProcess

    let allowInternalNetworking clusterName clusterConfig =
      async {
        match getProvider clusterConfig with
        | AWS ->
            let! res =
                DockerMachine.inspect clusterName "master-01"
                |> Proc.startAndAwait
            let securityGroupId = res.Driver.SecurityGroupIds.[0]
            let! res =
                runAws clusterConfig (sprintf "ec2 authorize-security-group-ingress --group-id %s --protocol all --port 0-65535 --source-group %s" securityGroupId securityGroupId)
                |> CreateProcess.redirectOutput
                |> Proc.startRaw
                |> Async.AwaitTask
            let output = Proc.getResultIgnoreExitCode res
            if output.Error.Contains "InvalidPermission.Duplicate" || output.Output.Contains "InvalidPermission.Duplicate" then
                if Env.isVerbose then eprintfn "Ignoring InvalidPermission.Duplicate error while editing security group."
            else
                res |> Proc.ensureExitCodeGetResult |> ignore
        | Generic ->
            printfn "Make sure all machines can access each other via hostnames!"
      }


    let provision config (nodeName:string) nodeType  =
        async {
            match getProvider config with
            | AWS ->
                do! HostInteraction.installPlugin Plugin.Ebs config nodeName nodeType
            | Generic ->
                ()
                //do! HostInteraction.installRexRayPlugin "s3fs" config nodeName nodeType
        }