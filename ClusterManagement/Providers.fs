namespace ClusterManagement

module Providers = 
    
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

    let createMachine clusterName machineName clusterConfig =
      async {
        match getProvider clusterConfig with
        | AWS ->
            let awsRegion = (ClusterConfig.getConfig "AWS_REGION" clusterConfig).Value
        
            let! res = DockerMachine.runInteractive clusterName (sprintf "create --driver amazonec2 --amazonec2-region %s \"%s\"" awsRegion machineName)
            res |> Proc.failWithMessage "failed create new machine! If the error reads 'There is already a keypair' you should try to remove the corresponding key from the aws console." |> ignore
        | Generic ->
            let privKey = (ClusterConfig.getConfig "SSH_KEY" clusterConfig).Value
            let host = (ClusterConfig.getConfig "SSH_HOST" clusterConfig).Value
            let tmp = System.IO.Path.GetTempFileName()
            System.IO.File.WriteAllText(tmp, privKey)
            let userArg =
                match ClusterConfig.getConfig "SSH_USER" clusterConfig with
                | Some s -> sprintf "--generic-ssh-user=%s " s
                | _ -> ""
            let portArg =
                match ClusterConfig.getConfig "SSH_PORT" clusterConfig with
                | Some s -> sprintf "--generic-ssh-port=%s " s
                | _ -> ""
            let enginePortArg =
                match ClusterConfig.getConfig "ENGINE_PORT" clusterConfig with
                | Some s -> sprintf "--generic-engine-port=%s " s
                | _ -> ""
            let! res = 
                DockerMachine.runInteractive clusterName 
                    (sprintf "create --driver generic %s%s%s--generic-ssh-key=%s --generic-ip-address=%s \"%s\"" portArg enginePortArg userArg tmp host machineName)
            res |> Proc.failWithMessage "failed create new machine!" |> ignore
            ()
      }

    let getAgentConfig clusterName clusterConfig =
        let tokens = Config.getTokens clusterName clusterConfig
        let getReplacedResourceText name =
            let resourceText = Env.getResourceText name
            Config.replaceTokens tokens resourceText
        getReplacedResourceText "agent.yml"

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
        DockerWrapper.run 
            (sprintf "run --env AWS_ACCESS_KEY_ID=%s --env AWS_SECRET_ACCESS_KEY=%s --env AWS_DEFAULT_REGION=%s garland/aws-cli-docker aws %s"
            keyId secret region command)

    let allowInternalNetworking clusterName clusterConfig =
      async {
        match getProvider clusterConfig with
        | AWS ->
            let! res = DockerMachine.inspect clusterName "master-01"
            let securityGroupId = res.Driver.SecurityGroupIds.[0]
            let! res  = runAws clusterConfig (sprintf "ec2 authorize-security-group-ingress --group-id %s --protocol all --port 0-65535 --source-group %s" securityGroupId securityGroupId)
            if res.Output.StdErr.Contains "InvalidPermission.Duplicate" || res.Output.StdOut.Contains "InvalidPermission.Duplicate" then
                if Env.isVerbose then eprintfn "Ignoring InvalidPermission.Duplicate error while editing security group."
            else
                res |> Proc.failOnExitCode |> ignore
        | Generic ->
            printfn "Make sure all machines can access each other via hostnames!"
      }